using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wes.Platform.Domain;
using Wes.Platform.Features.Identity;
using Wes.Wms.Domain;
using Wes.Wms.Features.Inbound;
using Wes.Wms.Features.MasterData;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Fact]
    [Trait("CaseId", "TC-T11-COUNT-07")]
    public async Task CountsRealBrowserSeparatesReviewAndRecoversOriginalAtomicAdjustment()
    {
        var fixture = await CountsHttpHostAsync();
        for (var index = 2; index <= 3; index++)
        {
            await using var seed = OpenWmsMasterData();
            var original = await seed.Balances.AsNoTracking().FirstAsync();
            var container = new StockContainer(_tenant, Guid.NewGuid(), _user, Guid.NewGuid(), $"COUNT-PALLET-{index}",
                "Count fixture pallet", fixture.Warehouse, _clock.GetUtcNow());
            var balance = new StockBalance(_tenant, fixture.Warehouse, fixture.Location, original.ItemId, "", container.Id, original.UnitId, 0);
            await using (var transaction = await seed.Database.BeginTransactionAsync())
            {
                seed.AddRange(container, balance, new StockDimensionRegistration(Guid.NewGuid(), _user, Guid.NewGuid(), balance, _clock.GetUtcNow()));
                await seed.SaveChangesAsync(); await transaction.CommitAsync();
            }
            seed.ChangeTracker.Clear();
            var line = Guid.NewGuid();
            var draft = await new InboundWriter(seed, _clock).CreateAsync(Guid.NewGuid(), _user, fixture.Warehouse,
                $"COUNT-SEED-{index}", [new(line, balance.ItemId, balance.UnitId, "", 0, 10m)]);
            seed.ChangeTracker.Clear();
            var approved = await new InboundWriter(seed, _clock).ApproveAsync(draft.Id!.Value, Guid.NewGuid(), _user,
                Guid.NewGuid(), draft.Version!);
            Assert.Equal(InboundWriteDecision.Committed, approved.Decision);
            seed.ChangeTracker.Clear();
            var received = await new InboundWriter(seed, _clock).ReceiveAsync(draft.Id.Value,
                new(Guid.NewGuid(), _user, Guid.NewGuid(), line, balance.ItemId, balance.UnitId, "", container.Id,
                    fixture.Location, fixture.Location, 10m), Guid.NewGuid(), approved.Version!, balance.Id, balance.Version);
            Assert.Equal(InboundWriteDecision.Committed, received.Decision);
        }
        await using (var platform = Open())
        {
            var reviewer = Guid.NewGuid(); var department = await platform.Departments.SingleAsync();
            platform.DirectoryUsers.Add(new(_tenant, reviewer, "COUNT-REVIEWER", "Count fixture reviewer", department.Id));
            platform.PasswordCredentials.Add(new(_tenant, reviewer, "COUNT-REVIEWER",
                StoredPasswordHash.Import(PasswordHashScheme.BcryptSha384V1, Hash), _clock.GetUtcNow()));
            var roles = await platform.Roles.ToListAsync();
            var reviewerRole = new AccessRole(_tenant, "COUNT-REVIEWER", roles.SelectMany(row => row.Rules)
                .Select(rule => rule with { Scope = DataScope.All, Departments = ImmutableHashSet<Guid>.Empty }));
            platform.Roles.Add(reviewerRole); platform.Memberships.Add(new(_tenant, reviewer, reviewerRole.Id));
            await platform.SaveChangesAsync();
        }
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
            wmsCounts: true, locationSchemas: services => services.AddSingleton<IWarehouseLocationSchema>(new LocationHttpSchema()));
        server.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        server.StartServer();
        var address = Assert.Single(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var handler = new HttpClientHandler
        { ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual?.Thumbprint == certificate.Thumbprint };
        using var probe = new HttpClient(handler);
        Assert.Equal(HttpStatusCode.OK, (await probe.GetAsync(address + "/health/live")).StatusCode);
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
        start.ArgumentList.Add("test"); start.ArgumentList.Add("counts.spec.ts"); start.ArgumentList.Add("--config=playwright.config.ts");
        start.Environment["WES_E2E_URL"] = address; start.Environment["WES_E2E_TENANT"] = _tenant.ToString("D");
        start.Environment["WES_E2E_COUNT_FIXTURE"] = "1";
        start.Environment["WES_E2E_COUNT_WAREHOUSE"] = fixture.Warehouse.ToString("D");
        start.Environment["WES_E2E_COUNT_LOCATION"] = fixture.Location.ToString("D");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.True(process.ExitCode == 0, await output + Environment.NewLine + await errors);
        await using var verify = OpenWmsMasterData();
        var plan = await verify.Counts.SingleAsync();
        Assert.Equal(StockCountState.Completed, plan.State); Assert.False(plan.Active); Assert.Null(plan.LockOwnerId);
        foreach (var line in plan.Lines)
            Assert.Equal(line.Quantity, (await verify.Balances.SingleAsync(row => row.Id == line.BalanceId)).Quantity);
        Assert.Equal(new[] { 8m, 10m, 12m }, plan.Lines.Select(row => row.Quantity!.Value).Order().ToArray());
        Assert.Equal(5, await verify.Postings.CountAsync()); Assert.Equal(5, await verify.Entries.CountAsync());
        Assert.Equal(1, await verify.Postings.CountAsync(row => row.Kind == StockPostingKind.AdjustmentIn));
        Assert.Equal(1, await verify.Postings.CountAsync(row => row.Kind == StockPostingKind.AdjustmentOut));
        Assert.Equal(6, await verify.CountFacts.CountAsync());
    }
}
