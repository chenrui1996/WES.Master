using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wes.Wms.Domain;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Fact]
    [Trait("CaseId", "TC-T09-EXEC-07")]
    public async Task InboundExecutionRealBrowserApprovesAndRecoversReceivedStockWithoutClaimingFinalArrival()
    {
        var fixture = await ExecutionHttpHostAsync();
        Guid target;
        await using (var seed = OpenWmsMasterData())
        {
            var warehouse = await seed.InboundDocuments.Select(row => row.WarehouseId).SingleAsync();
            var created = await new CreateMasterData(seed, _clock, new LocationHttpSchema()).ExecuteAsync(
                new(Guid.NewGuid(), _user, Guid.NewGuid(), "TARGET", "Target", warehouse, LocationHttpSchema.Pack, "v1"));
            Assert.Equal(MasterDataDecision.Created, created.Decision); target = created.Id!.Value;
        }
        using (var enabled = await LocationStateHttpAsync(target, Guid.NewGuid(), await LocationStateInputAsync(target)))
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WES.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var web = Path.Combine(root.FullName, "clients", "ops-web");
        Assert.True(File.Exists(Path.Combine(web, "dist", "index.html")), "Build the reviewed client before browser validation.");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), password: null);
        var schema = new LocationHttpSchema();
        await using var server = new Factory(_connection, _corpus, _clock, Path.Combine(web, "dist"), wmsMasterData: true,
            wmsInboundApproval: true, wmsInboundReceipt: true,
            locationSchemas: services => services.AddSingleton<IWarehouseLocationSchema>(schema));
        server.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        server.StartServer();
        var address = Assert.Single(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var handler = new HttpClientHandler
        { ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual?.Thumbprint == certificate.Thumbprint };
        using var probe = new HttpClient(handler);
        using var live = await probe.GetAsync(address + "/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        var coordination = Path.Combine(Path.GetTempPath(), $"wes-inbound-execution-{Guid.NewGuid():N}");
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
            start.ArgumentList.Add("test"); start.ArgumentList.Add("inbound-execution.spec.ts"); start.ArgumentList.Add("--config=playwright.config.ts");
            start.Environment["WES_E2E_URL"] = address; start.Environment["WES_E2E_TENANT"] = _tenant.ToString("D");
            start.Environment["WES_E2E_EXECUTION_COORDINATION"] = coordination;
            start.Environment["WES_E2E_EXECUTION_ORDER"] = fixture.Order.ToString("D");
            start.Environment["WES_E2E_EXECUTION_CONTAINER"] = fixture.Container.ToString("D");
            start.Environment["WES_E2E_EXECUTION_SOURCE"] = fixture.Location.ToString("D");
            start.Environment["WES_E2E_EXECUTION_TARGET"] = target.ToString("D");
            start.Environment["WES_E2E_EXECUTION_BALANCE"] = fixture.Balance.ToString("D");
            start.Environment["WES_E2E_EXECUTION_BALANCE_VERSION"] = fixture.BalanceVersion;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var revocation = RevokeAsync();
            try { await process.WaitForExitAsync(deadline.Token); await revocation; }
            finally
            {
                deadline.Cancel();
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                try { await revocation; }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { /* Stop only this fixture's coordination wait. */ }
            }
            Assert.True(process.ExitCode == 0, await output + Environment.NewLine + await errors);
            Assert.True(File.Exists(Path.Combine(coordination, "revoke.confirmed")));
            async Task RevokeAsync()
            {
                while (!File.Exists(Path.Combine(coordination, "revoke.request")))
                { if (process.HasExited) return; await Task.Delay(50, deadline.Token); }
                await using var platform = Open();
                var role = await platform.Roles.SingleAsync(row => row.Code == "INBOUND-EXECUTION", deadline.Token);
                role.Change(role.Version, true, role.Rules.Select(rule => rule with { Fields = rule.Fields.Remove("Lines") }));
                await platform.SaveChangesAsync(deadline.Token);
                await File.WriteAllTextAsync(Path.Combine(coordination, "revoke.confirmed"), "revoked", deadline.Token);
            }
        }
        finally
        {
            File.Delete(Path.Combine(coordination, "revoke.request")); File.Delete(Path.Combine(coordination, "revoke.confirmed"));
            Directory.Delete(coordination);
        }
        await using var verify = OpenWmsMasterData();
        Assert.Equal(InboundOrderState.Executing, (await verify.InboundDocuments.SingleAsync()).State);
        Assert.Equal(5m, (await verify.Balances.SingleAsync()).Quantity);
        Assert.Single(await verify.Postings.ToListAsync()); Assert.Single(await verify.Entries.ToListAsync());
        var facts = await verify.InboundFacts.OrderBy(row => row.Sequence).ToArrayAsync();
        Assert.Equal(new[] { InboundFactKind.Approved, InboundFactKind.Received }, facts.Select(row => row.Kind));
        Assert.Equal(target, facts[1].Receipt().TargetLocationId); Assert.Equal(fixture.Location, facts[1].Receipt().ReceiptLocationId);
        var operations = await verify.InboundExecutionOperations.ToArrayAsync();
        Assert.Equal(2, operations.Length); Assert.All(operations, row => Assert.Equal(_user, row.ActorId));
        Assert.Equal(facts[1].AfterVersion, operations.Single(row => row.Kind == InboundFactKind.Received).AfterVersion);
        Assert.Equal(2, schema.Calls.Count);
    }
}
