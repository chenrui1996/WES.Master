using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wes.Host.Wms;
using Wes.Platform.Domain;
using Wes.Platform.Features.Identity;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Fact]
    [Trait("CaseId", "TC-T10-BASIC-07")]
    public async Task OutboundRealBrowserReservesPicksReviewsShipsAndRecoversOriginalOperation()
    {
        var fixture = await ExecutionHttpHostAsync();
        Guid warehouse; string balanceVersion;
        await using (var seed = OpenWmsMasterData())
        {
            var balance = await seed.Balances.SingleAsync();
            warehouse = balance.WarehouseId;
            seed.ChangeTracker.Clear();
            var line = Guid.NewGuid();
            var created = await new InboundWriter(seed, _clock).CreateAsync(Guid.NewGuid(), _user, warehouse,
                "OUTBOUND-SEED", [new(line, balance.ItemId, balance.UnitId, balance.Batch, balance.Precision, 10m)]);
            var seedOrder = created.Id!.Value;
            using var approval = await ExecutionPostAsync(seedOrder, "approval", Guid.NewGuid(), new InboundApprovalInput(created.Version!));
            Assert.Equal(HttpStatusCode.Created, approval.StatusCode);
            var version = (await approval.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString()!;
            using var receipt = await ExecutionPostAsync(seedOrder, "receipts", Guid.NewGuid(),
                new InboundReceiptInput(version, fixture.BalanceVersion, line, Guid.NewGuid(), "10",
                    fixture.Container, fixture.Location, fixture.Location, fixture.Balance));
            Assert.Equal(HttpStatusCode.Created, receipt.StatusCode);
        }
        await using (var current = OpenWmsMasterData())
        {
            var balance = await current.Balances.SingleAsync();
            Assert.Equal(10m, balance.Quantity); balanceVersion = balance.Version;
        }
        await GrantOutboundBrowserAsync();
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
        await using var server = new Factory(_connection, _corpus, _clock, Path.Combine(web, "dist"), wmsMasterData: true,
            wmsOutbound: true, locationSchemas: services => services.AddSingleton<IWarehouseLocationSchema>(new LocationHttpSchema()));
        server.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        server.StartServer();
        var address = Assert.Single(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var handler = new HttpClientHandler
        { ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual?.Thumbprint == certificate.Thumbprint };
        using var probe = new HttpClient(handler);
        using var live = await probe.GetAsync(address + "/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = web,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(Path.Combine(web, "node_modules", "@playwright", "test", "cli.js"));
        start.ArgumentList.Add("test"); start.ArgumentList.Add("outbound.spec.ts"); start.ArgumentList.Add("--config=playwright.config.ts");
        start.Environment["WES_E2E_URL"] = address; start.Environment["WES_E2E_TENANT"] = _tenant.ToString("D");
        start.Environment["WES_E2E_OUTBOUND_FIXTURE"] = "1";
        start.Environment["WES_E2E_OUTBOUND_WAREHOUSE"] = warehouse.ToString("D");
        start.Environment["WES_E2E_OUTBOUND_BALANCE"] = fixture.Balance.ToString("D");
        start.Environment["WES_E2E_OUTBOUND_BALANCE_VERSION"] = balanceVersion;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.True(process.ExitCode == 0, await output + Environment.NewLine + await errors);
        await using var verify = OpenWmsMasterData();
        Assert.Equal(5m, (await verify.Balances.SingleAsync()).Quantity);
        Assert.Equal(2, await verify.Postings.CountAsync()); Assert.Equal(2, await verify.Entries.CountAsync());
        var reservation = await verify.Reservations.SingleAsync();
        Assert.Equal(0m, reservation.RemainingQuantity);
        Assert.NotEqual(Guid.Empty, reservation.SourceDocumentId);
    }

    private async Task GrantOutboundBrowserAsync()
    {
        await using var platform = Open();
        var role = new AccessRole(_tenant, "OUTBOUND-BROWSER",
        [
            new("wms.outbound.read", DataScope.All, [], ImmutableHashSet.Create("Id", "Code", "WarehouseId", "State", "Version", "CreatedAt", "Lines")),
            new("wms.outbound.create", DataScope.All, [], ImmutableHashSet.Create("Code", "WarehouseId", "Lines")),
            new("wms.outbound.approve", DataScope.All, [], ImmutableHashSet.Create("State")),
            new("wms.outbound.pick", DataScope.All, [], ImmutableHashSet.Create("LineId", "PickedQuantity", "ShortageQuantity", "Reason")),
            new("wms.outbound.release", DataScope.All, [], ImmutableHashSet.Create("LineId", "Quantity", "Reason")),
            new("wms.outbound.ship", DataScope.All, [], ImmutableHashSet.Create("LineId", "Quantity")),
            new("wms.outbound.cancel", DataScope.All, [], ImmutableHashSet.Create("State", "Reason")),
            new("wms.outbound.close", DataScope.All, [], ImmutableHashSet.Create("State", "Reason"))
        ]);
        platform.Roles.Add(role); platform.Memberships.Add(new(_tenant, _user, role.Id));
        await platform.SaveChangesAsync();
        var reviewer = Guid.NewGuid(); var department = await platform.Departments.SingleAsync();
        platform.DirectoryUsers.Add(new DirectoryUser(_tenant, reviewer, "OUTBOUND-REVIEWER", "Outbound fixture reviewer", department.Id));
        platform.PasswordCredentials.Add(new PasswordCredential(_tenant, reviewer, "OUTBOUND-REVIEWER",
            StoredPasswordHash.Import(PasswordHashScheme.BcryptSha384V1, Hash), _clock.GetUtcNow()));
        var roles = await platform.Roles.ToListAsync();
        var reviewerRole = new AccessRole(_tenant, "OUTBOUND-REVIEWER", roles.SelectMany(row => row.Rules)
            .Select(rule => rule with { Scope = DataScope.All, Departments = ImmutableHashSet<Guid>.Empty }));
        platform.Roles.Add(reviewerRole); platform.Memberships.Add(new(_tenant, reviewer, reviewerRole.Id));
        await platform.SaveChangesAsync();
    }
}
