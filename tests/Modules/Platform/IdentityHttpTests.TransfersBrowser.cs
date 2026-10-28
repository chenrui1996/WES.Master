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
using Wes.Wms.Domain;
using Wes.Wms.Features.MasterData;
using Wes.Wms.Features.Transfers;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    [Fact]
    [Trait("CaseId", "TC-T11-TRANSFER-07")]
    public async Task ControlledTransferBrowserPreservesReceiptsAndExactLedger()
    {
        var fixture = await TransfersHttpHostAsync();
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
            wmsTransfers: true, locationSchemas: services => services.AddSingleton<IWarehouseLocationSchema>(new LocationHttpSchema()));
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
        start.ArgumentList.Add("test"); start.ArgumentList.Add("transfers.spec.ts"); start.ArgumentList.Add("--config=playwright.config.ts");
        start.Environment["WES_E2E_URL"] = address; start.Environment["WES_E2E_TENANT"] = _tenant.ToString("D");
        start.Environment["WES_E2E_TRANSFER_FIXTURE"] = "1";
        start.Environment["WES_E2E_TRANSFER_WAREHOUSE"] = fixture.Warehouse.ToString("D");
        start.Environment["WES_E2E_TRANSFER_SOURCE_A"] = fixture.SourceA.ToString("D");
        start.Environment["WES_E2E_TRANSFER_TARGET_A"] = fixture.TargetA.ToString("D");
        start.Environment["WES_E2E_TRANSFER_SOURCE_B"] = fixture.SourceB.ToString("D");
        start.Environment["WES_E2E_TRANSFER_TARGET_B"] = fixture.TargetB.ToString("D");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.True(process.ExitCode == 0, await output + Environment.NewLine + await errors);
        await using var verify = OpenWmsMasterData();
        var partial = await verify.TransferDocuments.SingleAsync(row => row.Code == "TR-BROWSER-PARTIAL");
        var complete = await verify.TransferDocuments.SingleAsync(row => row.Code == "TR-BROWSER-COMPLETE");
        Assert.Equal(TransferState.ClosedPartial, partial.State); Assert.Equal(TransferState.Completed, complete.State);
        Assert.Equal(3m, (await verify.Balances.SingleAsync(row => row.Id == fixture.SourceA)).Quantity);
        Assert.Equal(0m, (await verify.Balances.SingleAsync(row => row.Id == fixture.TargetA)).Quantity);
        Assert.Equal(0m, (await verify.Balances.SingleAsync(row => row.Id == fixture.SourceB)).Quantity);
        Assert.Equal(1m, (await verify.Balances.SingleAsync(row => row.Id == fixture.TargetB)).Quantity);
        Assert.Equal(3, await verify.Postings.CountAsync(row => row.Kind == StockPostingKind.Transfer));
        Assert.Equal(3, await verify.TransferOutbox.CountAsync());
        Assert.Equal(6, await (from entry in verify.Entries
                               join posting in verify.Postings on entry.PostingId equals posting.Id
                               where posting.Kind == StockPostingKind.Transfer
                               select entry).CountAsync());
        Assert.Equal(0m, await verify.Reservations.SumAsync(row => row.RemainingQuantity));
        Assert.False(await TransferProtection.ConflictsAsync(verify, fixture.Warehouse,
            partial.Lines.Select(row => row.TargetLocationId), partial.Lines.Select(row => row.ContainerId), CancellationToken.None));
        var completedFacts = await verify.TransferFacts.Where(row => row.OrderId == complete.Id).ToArrayAsync();
        Assert.All(complete.Lines, line => Assert.Equal(TransferPostingStatus.Synced, TransferProgress.For(completedFacts, line.Id).Posting));
        var partialFacts = await verify.TransferFacts.Where(row => row.OrderId == partial.Id).ToArrayAsync();
        Assert.Single(partial.Lines, line => TransferProgress.For(partialFacts, line.Id).Posting == TransferPostingStatus.Synced);
        Assert.Single(partial.Lines, line => TransferProgress.For(partialFacts, line.Id).Execution == TransferExecutionStatus.Closed);
    }
}
