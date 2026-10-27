using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [Trait("CaseId", "WMS-CLIENT-06")]
    public async Task RealBrowserReadsWarehouseAndLocationAndClearsDetailsAfterCurrentRevocation(bool warehouseCreation, bool inventoryRead)
    {
        var ids = await WmsHostAsync(warehouseCreation);
        if (warehouseCreation) await GrantWarehouseCreationAsync();
        await GrantCreationEventReadAsync();
        await GrantCreationEventCountsAsync();
        var balance = inventoryRead ? await SeedInventoryAsync(ids) : null;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WES.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var web = Path.Combine(root.FullName, "clients", "ops-web");
        Assert.True(File.Exists(Path.Combine(web, "dist", "index.html")), "Build the reviewed client before browser validation.");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), password: null);
        await using var server = new Factory(_connection, _corpus, _clock, Path.Combine(web, "dist"), wmsMasterData: true,
            wmsWarehouseCreation: warehouseCreation);
        server.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        server.StartServer();
        var address = Assert.Single(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var handler = new HttpClientHandler
        { ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual?.Thumbprint == certificate.Thumbprint };
        using var probe = new HttpClient(handler);
        using var live = await probe.GetAsync(address + "/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        var coordination = Path.Combine(Path.GetTempPath(), $"wes-wms-browser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(coordination);
        try
        {
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = web,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            start.ArgumentList.Add(Path.Combine(web, "node_modules", "@playwright", "test", "cli.js"));
            start.ArgumentList.Add("test"); start.ArgumentList.Add("wms-master-data.spec.ts"); start.ArgumentList.Add("--config=playwright.config.ts");
            start.Environment["WES_E2E_URL"] = address;
            start.Environment["WES_E2E_TENANT"] = _tenant.ToString("D");
            start.Environment["WES_E2E_WAREHOUSE_ID"] = ids.Warehouse.ToString("D");
            start.Environment["WES_E2E_LOCATION_ID"] = ids.Location.ToString("D");
            start.Environment["WES_E2E_WMS_COORDINATION"] = coordination;
            start.Environment["WES_E2E_WMS_CREATE"] = warehouseCreation ? "true" : "false";
            start.Environment["WES_E2E_INVENTORY_ID"] = balance?.Id.ToString("D") ?? "";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var revocation = RevokeWhenRequestedAsync();
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                await revocation;
            }
            finally
            {
                deadline.Cancel();
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                try { await revocation; }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { /* Stop only this fixture's pending coordination wait. */ }
            }
            Assert.True(process.ExitCode == 0, await output + Environment.NewLine + await errors);
            Assert.True(File.Exists(Path.Combine(coordination, "revoke.confirmed")), "The real current role must have been revoked.");
            Assert.True(File.Exists(Path.Combine(coordination, "events-revoke.confirmed")), "The real message-field role must have been revoked.");
            if (inventoryRead)
                Assert.True(File.Exists(Path.Combine(coordination, "inventory-revoke.confirmed")), "The real inventory-field role must have been revoked.");

            async Task RevokeWhenRequestedAsync()
            {
                while (!File.Exists(Path.Combine(coordination, "revoke.request")))
                {
                    if (process.HasExited) return;
                    await Task.Delay(50, deadline.Token);
                }
                await using var writer = Open();
                var role = await writer.Roles.SingleAsync(item => item.Code == "WMS", deadline.Token);
                role.Change(role.Version, true, role.Rules.Select(rule => rule with { Fields = rule.Fields.Remove("Name") }));
                await writer.SaveChangesAsync(deadline.Token);
                await File.WriteAllTextAsync(Path.Combine(coordination, "revoke.confirmed"), "revoked", deadline.Token);
                while (!File.Exists(Path.Combine(coordination, "events-revoke.request")))
                {
                    if (process.HasExited) return;
                    await Task.Delay(50, deadline.Token);
                }
                var eventRole = await writer.Roles.SingleAsync(item => item.Code == "WMS-EVENT-READ", deadline.Token);
                eventRole.Change(eventRole.Version, true, eventRole.Rules.Select(rule => rule with { Fields = rule.Fields.Remove("EventId") }));
                var countRole = await writer.Roles.SingleAsync(item => item.Code == "WMS-EVENT-COUNTS", deadline.Token);
                countRole.Change(countRole.Version, true, countRole.Rules.Select(rule => rule with { Fields = rule.Fields.Remove("PendingCount") }));
                await writer.SaveChangesAsync(deadline.Token);
                await File.WriteAllTextAsync(Path.Combine(coordination, "events-revoke.confirmed"), "revoked", deadline.Token);
                if (!inventoryRead) return;
                while (!File.Exists(Path.Combine(coordination, "inventory-revoke.request")))
                {
                    if (process.HasExited) return;
                    await Task.Delay(50, deadline.Token);
                }
                var inventoryRole = await writer.Roles.SingleAsync(item => item.Code == "INVENTORY-READ", deadline.Token);
                inventoryRole.Change(inventoryRole.Version, true,
                    inventoryRole.Rules.Select(rule => rule with { Fields = rule.Fields.Remove("Available") }));
                await writer.SaveChangesAsync(deadline.Token);
                await File.WriteAllTextAsync(Path.Combine(coordination, "inventory-revoke.confirmed"), "revoked", deadline.Token);
            }
        }
        finally
        {
            File.Delete(Path.Combine(coordination, "revoke.request"));
            File.Delete(Path.Combine(coordination, "revoke.confirmed"));
            File.Delete(Path.Combine(coordination, "events-revoke.request"));
            File.Delete(Path.Combine(coordination, "events-revoke.confirmed"));
            File.Delete(Path.Combine(coordination, "inventory-revoke.request"));
            File.Delete(Path.Combine(coordination, "inventory-revoke.confirmed"));
            Directory.Delete(coordination);
        }
        await using var verify = OpenWmsMasterData();
        Assert.Equal(warehouseCreation ? 3 : 2, await verify.MasterDataChanges.CountAsync());
        Assert.Equal(warehouseCreation ? 3 : 2, await verify.OutboxMessages.CountAsync());
        Assert.Equal(inventoryRead ? 1 : 0, await verify.Entries.CountAsync());
        if (inventoryRead)
        {
            Assert.Equal(Wes.Wms.Domain.StockMovementQuantity.MaximumValue, (await verify.Balances.SingleAsync()).Quantity);
            Assert.Empty(await verify.Reservations.ToListAsync());
        }
        Assert.All(await verify.Warehouses.ToListAsync(), item => Assert.False(item.Enabled));
        Assert.False((await verify.Locations.SingleAsync()).Enabled);
        if (warehouseCreation)
        {
            var created = await verify.Warehouses.SingleAsync(item => item.Code == "BROWSER-WH");
            Assert.Equal("Browser warehouse", created.Name);
            var fact = await verify.MasterDataChanges.SingleAsync(item => item.WarehouseId == created.Id);
            Assert.Equal(_user, fact.ActorId);
            var message = await verify.OutboxMessages.SingleAsync(item => item.EventId == fact.OperationId);
            Assert.Equal(created.Id, message.AggregateId);
            Assert.Equal(created.Version, message.AggregateVersion);
        }
    }
}
