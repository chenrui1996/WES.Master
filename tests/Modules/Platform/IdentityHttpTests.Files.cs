using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wes.Host.Identity;
using Wes.Host.Platform;
using Wes.Platform;
using Wes.Platform.Domain;
using Wes.Platform.Features.Files;
using Wes.Platform.Features.Tenancy;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed partial class IdentityHttpTests
{
    private string? _filesRoot;

    [Fact]
    [Trait("CaseId", "HTTP-FILE-01")]
    public async Task StagingFilesAreNotExposedWithoutExplicitDeploymentOptIn()
    {
        await SignInAsync();
        using var response = await _client.GetAsync($"/api/platform/files/staging/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("CaseId", "HTTP-FILE-04")]
    public async Task HttpUploadPersistsPrivateQuarantineAndReplaysExactPermanentIdentity()
    {
        await FileHostAsync();
        var operation = Guid.NewGuid();
        using var first = await _client.SendAsync(FileRequest("safe"u8.ToArray(), operation));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        var original = await first.Content.ReadFromJsonAsync<JsonElement>();
        var id = original.GetProperty("id").GetGuid();
        Assert.Equal((int)QuarantinedFileState.Quarantined, original.GetProperty("state").GetInt32());
        using var replay = await _client.SendAsync(FileRequest("safe"u8.ToArray(), operation));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(id, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        using var conflict = await _client.SendAsync(FileRequest("changed"u8.ToArray(), operation));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var read = await _client.GetAsync($"/api/platform/files/staging/{id:D}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var json = await read.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sha256", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_filesRoot!, json, StringComparison.OrdinalIgnoreCase);
        using var isolated = await _client.GetAsync($"/api/platform/files/staging/{id:D}/content");
        Assert.Equal(HttpStatusCode.Conflict, isolated.StatusCode);
        await using var verify = Open();
        var file = await verify.Files.SingleAsync();
        Assert.True(file.Stored);
        Assert.Equal(operation, file.UploadOperationId);
        Assert.Equal(2, await verify.Audit.CountAsync(row =>
            row.Action == PlatformAuditAction.FileUploadCreated || row.Action == PlatformAuditAction.FileUploadStored));
        Assert.Equal("safe"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(_filesRoot!, _tenant.ToString("N"), id.ToString("N"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "HTTP-FILE-03")]
    public async Task HttpUploadEnforcesRealTwentyMiBPlusOneWithAndWithoutContentLength(bool knownLength)
    {
        await FileHostAsync();
        using var request = FileRequest(new byte[FileContentValidator.MaximumBytes + 1], Guid.NewGuid(), knownLength);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PayloadTooLarge", json.GetProperty("code").GetString());
        await using var verify = Open();
        Assert.Empty(await verify.Files.ToListAsync());
        Assert.Empty(Directory.GetFiles(_filesRoot!, "*", SearchOption.AllDirectories));
    }

    [Fact]
    [Trait("CaseId", "HTTP-FILE-03")]
    public async Task HttpUnknownLengthExactTwentyMiBStoresEveryByteWithoutClearingQuarantine()
    {
        await FileHostAsync();
        var bytes = Enumerable.Repeat((byte)'a', FileContentValidator.MaximumBytes).ToArray();
        using var request = FileRequest(bytes, Guid.NewGuid(), knownLength: false);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var verify = Open();
        var file = await verify.Files.SingleAsync();
        Assert.Equal(bytes.Length, file.ByteCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), file.Sha256);
        Assert.Equal(QuarantinedFileState.Quarantined, file.State);
    }

    [Fact]
    [Trait("CaseId", "HTTP-FILE-02")]
    public async Task UploadRevokedCurrentFieldCannotPersistBytesOrFileIdentity()
    {
        await FileHostAsync();
        await using (var seed = Open())
        {
            var role = await seed.Roles.SingleAsync();
            role.Change(role.Version, false, []);
            await seed.SaveChangesAsync();
        }
        using var response = await _client.SendAsync(FileRequest("safe"u8.ToArray(), Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var verify = Open();
        Assert.Empty(await verify.Files.ToListAsync());
        Assert.Empty(Directory.GetFiles(_filesRoot!, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("missing-name")]
    [InlineData("empty-operation")]
    [InlineData("gzip")]
    [InlineData("invalid-name")]
    [InlineData("invalid-utf8")]
    [InlineData("invalid-escape")]
    [Trait("CaseId", "HTTP-FILE-02")]
    public async Task InvalidUploadHeadersNeverCreateDurableOrStorageSideEffects(string input)
    {
        await FileHostAsync();
        using var request = FileRequest("safe"u8.ToArray(), Guid.NewGuid());
        if (input == "missing-name") request.Headers.Remove("X-WES-File-Name");
        if (input == "empty-operation")
        {
            request.Headers.Remove("Idempotency-Key");
            request.Headers.Add("Idempotency-Key", Guid.Empty.ToString("D"));
        }
        if (input == "gzip") request.Content!.Headers.ContentEncoding.Add("gzip");
        if (input == "invalid-name")
        {
            request.Headers.Remove("X-WES-File-Name");
            request.Headers.Add("X-WES-File-Name", "unsafe:path.txt");
        }
        if (input is "invalid-utf8" or "invalid-escape")
        {
            request.Headers.Remove("X-WES-File-Name");
            request.Headers.Add("X-WES-File-Name", input == "invalid-utf8" ? "bad%FF.txt" : "bad%2G.txt");
        }
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verify = Open();
        Assert.Empty(await verify.Files.ToListAsync());
        Assert.Empty(Directory.GetFiles(_filesRoot!, "*", SearchOption.AllDirectories));
    }

    [Fact]
    [Trait("CaseId", "HTTP-FILE-05")]
    public async Task AvailableHttpDownloadDeliversExactAttachmentWithAuditAndCurrentRevocation()
    {
        await FileHostAsync();
        using var upload = await _client.SendAsync(FileRequest("safe"u8.ToArray(), Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        Guid id;
        await using (var seed = Open())
        {
            var file = await seed.Files.SingleAsync();
            var attempt = file.BeginScan(_clock.GetUtcNow());
            file.CompleteScan(attempt, FileScanVerdict.Clean, file.Sha256, file.ByteCount, _clock.GetUtcNow());
            id = file.Id;
            await seed.SaveChangesAsync();
        }
        using var response = await _client.GetAsync($"/api/platform/files/staging/{id:D}/content");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("safe"u8.ToArray(), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("note.txt", response.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Accept-Ranges"));
        await using (var verify = Open())
        {
            Assert.Single(await verify.Audit.Where(row => row.Action == PlatformAuditAction.FileDownloaded).ToListAsync());
            var role = await verify.Roles.SingleAsync();
            role.Change(role.Version, false, []);
            await verify.SaveChangesAsync();
        }
        using var denied = await _client.GetAsync($"/api/platform/files/staging/{id:D}/content");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("insecure", HttpStatusCode.BadRequest)]
    [InlineData("client", HttpStatusCode.BadRequest)]
    [Trait("CaseId", "HTTP-FILE-02")]
    public async Task StagingTransportRequiresBearerHttpsAndExplicitWriteClientBeforeCreatingFiles(string input, HttpStatusCode expected)
    {
        await FileHostAsync();
        using var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        { BaseAddress = new Uri(input == "insecure" ? "http://localhost" : "https://localhost"), HandleCookies = false });
        if (input != "anonymous") client.DefaultRequestHeaders.Authorization = _client.DefaultRequestHeaders.Authorization;
        if (input != "client") client.DefaultRequestHeaders.Add("X-WES-Client", "ops-web");
        using var response = await client.SendAsync(FileRequest("safe"u8.ToArray(), Guid.NewGuid()));
        Assert.Equal(expected, response.StatusCode);
        await using var verify = Open();
        Assert.Empty(await verify.Files.ToListAsync());
        Assert.Empty(Directory.GetFiles(_filesRoot!, "*", SearchOption.AllDirectories));
    }

    [Fact]
    [Trait("CaseId", "HTTP-FILE-05")]
    public async Task HttpCannotReadAnotherTenantFileEvenWithSameCreatorIdentityAndOwnGrant()
    {
        await FileHostAsync();
        var otherTenant = Guid.NewGuid();
        await using var other = new Wes.Platform.Infrastructure.Persistence.PlatformDbContext(
            new DbContextOptionsBuilder<Wes.Platform.Infrastructure.Persistence.PlatformDbContext>()
                .UseMySQL(_connection, mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations")).Options, otherTenant);
        var file = new QuarantinedFile(otherTenant, _user, Guid.NewGuid(), "other.txt", "text/plain", 4,
            Convert.ToHexString(SHA256.HashData("safe"u8)), _clock.GetUtcNow());
        try
        {
            other.DirectoryTenants.Add(new DirectoryTenant(otherTenant, "Other file fixture", "zh-CN"));
            other.Files.Add(file);
            await other.SaveChangesAsync();
            using var metadata = await _client.GetAsync($"/api/platform/files/staging/{file.Id:D}");
            using var content = await _client.GetAsync($"/api/platform/files/staging/{file.Id:D}/content");
            Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, content.StatusCode);
        }
        finally
        {
            await other.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_files WHERE TenantId = {otherTenant.ToString("D")}");
            await other.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_directory_tenants WHERE TenantId = {otherTenant.ToString("D")}");
        }
    }

    [Fact]
    [Trait("CaseId", "HTTP-FILE-05")]
    public async Task CorruptedPrivateObjectReturnsSafe500WithoutDownloadAuditOrContent()
    {
        await FileHostAsync();
        using var upload = await _client.SendAsync(FileRequest("safe"u8.ToArray(), Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        Guid id;
        await using (var seed = Open())
        {
            var file = await seed.Files.SingleAsync();
            var attempt = file.BeginScan(_clock.GetUtcNow());
            file.CompleteScan(attempt, FileScanVerdict.Clean, file.Sha256, file.ByteCount, _clock.GetUtcNow());
            id = file.Id;
            await seed.SaveChangesAsync();
        }
        await File.WriteAllBytesAsync(Path.Combine(_filesRoot!, _tenant.ToString("N"), id.ToString("N")), "evil"u8.ToArray());
        using var response = await _client.GetAsync($"/api/platform/files/staging/{id:D}/content");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("evil", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(_filesRoot!, problem, StringComparison.OrdinalIgnoreCase);
        await using var verify = Open();
        Assert.Empty(await verify.Audit.Where(row => row.Action == PlatformAuditAction.FileDownloaded).ToListAsync());
    }

    [Fact]
    [Trait("CaseId", "HTTP-FILE-03")]
    public async Task RealHttpsKestrelChunkedBoundaryAcceptsExactMAndRejectsMPlusOneWithoutAnotherIdentity()
    {
        await FileHostAsync();
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        certificateRequest.CertificateExtensions.Add(names.Build());
        using var generated = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), password: null);
        await using var server = new Factory(_connection, _corpus, _clock, filesRoot: _filesRoot);
        server.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        server.StartServer();
        var address = Assert.Single(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual?.Thumbprint == certificate.Thumbprint
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Add("X-WES-Client", "ops-web");
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/identity/login", LoginRequest());
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        using (var named = FileRequest("safe"u8.ToArray(), Guid.NewGuid(), knownLength: false))
        {
            named.Headers.Remove("X-WES-File-Name");
            named.Headers.Add("X-WES-File-Name", Uri.EscapeDataString("检验 note.txt"));
            using var uploaded = await client.SendAsync(named);
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
            var id = (await uploaded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            using var read = await client.GetAsync($"/api/platform/files/staging/{id:D}");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var metadata = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("file");
            Assert.Equal("检验 note.txt", metadata.GetProperty("fileName").GetString());
        }
        foreach (var count in new[] { 20_971_520, 20_971_521 })
        {
            using var request = FileRequest(Enumerable.Repeat((byte)'a', count).ToArray(), Guid.NewGuid(), knownLength: false);
            using var response = await client.SendAsync(request);
            Assert.Equal(count == 20_971_520 ? HttpStatusCode.OK : HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        await using var verify = Open();
        Assert.Equal(2, await verify.Files.CountAsync());
    }

    [Fact]
    [Trait("CaseId", "HTTP-FILE-04")]
    public async Task StorageUnavailableReturnsSafe503AndSameOperationCanRecoverWithoutAnotherIdentity()
    {
        await FileHostAsync();
        Directory.Delete(_filesRoot!);
        var operation = Guid.NewGuid();
        using (var failed = await _client.SendAsync(FileRequest("safe"u8.ToArray(), operation)))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            var problem = await failed.Content.ReadAsStringAsync();
            Assert.DoesNotContain(_filesRoot!, problem, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("note.txt", problem, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("exception", problem, StringComparison.OrdinalIgnoreCase);
        }
        await using (var verify = Open())
        {
            Assert.False((await verify.Files.SingleAsync()).Stored);
        }
        Directory.CreateDirectory(_filesRoot!);
        using var recovered = await _client.SendAsync(FileRequest("safe"u8.ToArray(), operation));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        await using var final = Open();
        Assert.True((await final.Files.SingleAsync()).Stored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "HTTP-FILE-05")]
    public async Task ExecutedDownloadAlwaysClearsOwnedContentEvenWhenResponseWriteFails(bool fail)
    {
        await FileHostAsync();
        using var upload = await _client.SendAsync(FileRequest("safe"u8.ToArray(), Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        Guid id, session;
        await using (var seed = Open())
        {
            var file = await seed.Files.SingleAsync();
            var attempt = file.BeginScan(_clock.GetUtcNow());
            file.CompleteScan(attempt, FileScanVerdict.Clean, file.Sha256, file.ByteCount, _clock.GetUtcNow());
            id = file.Id;
            session = (await seed.Sessions.SingleAsync()).Id;
            await seed.SaveChangesAsync();
        }
        await using var owner = _factory.Services.GetRequiredService<IdentityDatabase>().Open(_tenant);
        var adapters = _factory.Services.GetRequiredService<PlatformFileAdapters>();
        var result = await owner.StagingFiles(adapters.Storage, adapters.Scanner)
            .DownloadAsync(new TenantContext(_tenant, _user, session), id, Guid.NewGuid());
        Assert.Equal(FileAccessDecision.Success, result.Decision);
        var download = Assert.IsType<FileDownload>(result.Download);
        var retained = download.Content;
        var http = new DefaultHttpContext();
        using var responseStream = new FailingResponseStream(fail);
        http.Response.Body = responseStream;
        var response = new StagingFileDownload(download);
        if (fail) await Assert.ThrowsAsync<IOException>(() => response.ExecuteAsync(http));
        else await response.ExecuteAsync(http);
        Assert.All(retained.ToArray(), value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => download.Content);
    }

    [Fact]
    [Trait("CaseId", "HTTP-FILE-06")]
    public async Task ScanAndOrphanDeleteRevalidateVersionsAndDoNotTreatUnavailableScannerAsClean()
    {
        // A bound non-listening loopback socket gives deterministic refusal, never a fake clean service.
        using var unavailable = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        unavailable.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        await FileHostAsync(((IPEndPoint)unavailable.LocalEndPoint!).Port);
        using var upload = await _client.SendAsync(FileRequest("safe"u8.ToArray(), Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var json = await upload.Content.ReadFromJsonAsync<JsonElement>();
        var id = json.GetProperty("id").GetGuid();
        var version = json.GetProperty("version").GetString();
        using var missingVersion = await _client.PostAsJsonAsync($"/api/platform/files/staging/{id:D}/scan", new { });
        Assert.Equal((HttpStatusCode)428, missingVersion.StatusCode);
        using var wrongVersion = await _client.PostAsJsonAsync($"/api/platform/files/staging/{id:D}/scan",
            new { expectedVersion = Guid.NewGuid().ToString("D") });
        Assert.Equal(HttpStatusCode.PreconditionFailed, wrongVersion.StatusCode);
        using var youngRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/platform/files/staging/{id:D}")
        { Content = JsonContent.Create(new { expectedVersion = version }) };
        using var young = await _client.SendAsync(youngRequest);
        Assert.Equal(HttpStatusCode.Conflict, young.StatusCode);

        using var scan = await _client.PostAsJsonAsync($"/api/platform/files/staging/{id:D}/scan", new { expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, scan.StatusCode);
        var scanned = (await scan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("file");
        Assert.Equal((int)QuarantinedFileState.Quarantined, scanned.GetProperty("state").GetInt32());
        using var download = await _client.GetAsync($"/api/platform/files/staging/{id:D}/content");
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);
        _clock.Advance(TimeSpan.FromSeconds(600));
        using var problem = await _client.GetAsync($"/api/platform/files/staging/{id:D}");
        Assert.True((await problem.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("file").GetProperty("processingProblem").GetBoolean());
        _clock.Advance(TimeSpan.FromHours(24));
        await SignInAsync();
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/platform/files/staging/{id:D}")
        { Content = JsonContent.Create(new { expectedVersion = scanned.GetProperty("version").GetString() }) };
        using var deleted = await _client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal((int)QuarantinedFileState.Deleted,
            (await deleted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("file").GetProperty("state").GetInt32());
        Assert.False(File.Exists(Path.Combine(_filesRoot!, _tenant.ToString("N"), id.ToString("N"))));
    }

    private sealed class FailingResponseStream(bool fail) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            fail ? ValueTask.FromException(new IOException("synthetic response disconnect")) : base.WriteAsync(buffer, cancellationToken);
    }

    private async Task FileHostAsync(int scannerPort = 3310)
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        _filesRoot = Path.Combine(Path.GetTempPath(), $"wes-http-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_filesRoot);
        _factory = new Factory(_connection, _corpus, _clock, filesRoot: _filesRoot, scannerPort: scannerPort);
        _client = Client();
        await SignInAsync();
        await using var seed = Open();
        var grants = new[]
        {
            new PermissionGrant("platform.files.write", DataScope.Own, ImmutableHashSet<Guid>.Empty,
                ImmutableHashSet.Create("Upload", "Scan", "Delete")),
            new PermissionGrant("platform.files.read", DataScope.Own, ImmutableHashSet<Guid>.Empty,
                ImmutableHashSet.Create("Id", "FileName", "ContentType", "ByteCount", "State", "Stored", "Version", "CreatedAt", "ProcessingProblem")),
            new PermissionGrant("platform.files.download", DataScope.Own, ImmutableHashSet<Guid>.Empty,
                ImmutableHashSet.Create("Content"))
        };
        var role = new AccessRole(_tenant, "files", grants);
        seed.Roles.Add(role);
        seed.Memberships.Add(new RoleMembership(_tenant, _user, role.Id));
        await seed.SaveChangesAsync();
    }

    private static HttpRequestMessage FileRequest(byte[] bytes, Guid operation, bool knownLength = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/platform/files/staging");
        request.Headers.Add("Idempotency-Key", operation.ToString("D"));
        request.Headers.Add("X-WES-File-Name", "note.txt");
        request.Content = knownLength ? new ByteArrayContent(bytes) : new StreamContent(new MemoryStream(bytes));
        if (!knownLength) request.Headers.TransferEncodingChunked = true;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        return request;
    }
}
