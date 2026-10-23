using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog.Core;
using Serilog.Events;
using Wes.Host.Runtime;
using Xunit;

namespace Wes.Modules.Tests.SystemOperations;

/// <summary>Exercises the real HTTP boundary with synthetic faults and actual structured log output.</summary>
public sealed class RuntimeBoundaryTests
{
    /// <summary>Unexpected failures must be observable but never expose transport or exception secrets.</summary>
    [Theory]
    [InlineData("failure", 500, "InternalError")]
    [InlineData("timeout", 503, "DependencyUnavailable")]
    [InlineData("binding", 400, "ValidationFailed")]
    [Trait("CaseId", "FOUNDATION-ERROR-001")]
    [Trait("CaseId", "FOUNDATION-LOG-001")]
    public async Task Fault_ProducesSafeProblemAndCorrelatedLog(string fault, int status, string code)
    {
        var sink = new CapturingSink();
        using var server = CreateServer(sink, context =>
        {
            throw fault switch
            {
                "timeout" => new TimeoutException("secret-marker"),
                "binding" => new BadHttpRequestException("secret-marker"),
                _ => new InvalidOperationException("secret-marker", new Exception("secret-marker"))
            };
        });
        using var client = server.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/secret-marker?token=secret-marker");
        request.Headers.Add("Authorization", "Bearer secret-marker");
        request.Headers.Add("X-Correlation-Id", "cc11ed89-9fc4-4a87-b337-e645c1f2da42");
        request.Content = new StringContent("secret-marker");

        using var response = await client.SendAsync(request);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(status, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("cc11ed89-9fc4-4a87-b337-e645c1f2da42",
            problem.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal("cc11ed89-9fc4-4a87-b337-e645c1f2da42",
            response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.DoesNotContain("secret-marker", body);
        Assert.NotEmpty(sink.Events);
        Assert.Contains(sink.Events, entry => entry.Level >= LogEventLevel.Warning
            && entry.Properties.ContainsKey("CorrelationId"));
        if (status >= 500)
        {
            Assert.Contains(sink.Events, entry => entry.Level == LogEventLevel.Error);
        }
        Assert.All(sink.Events, entry =>
        {
            Assert.Null(entry.Exception);
            Assert.DoesNotContain("secret-marker", entry.RenderMessage() + string.Join(",", entry.Properties));
        });
    }

    /// <summary>Invalid or ambiguous caller IDs are replaced rather than becoming log injection input.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("secret-marker")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("cc11ed89-9fc4-4a87-b337-e645c1f2da42,38dc5a4a-5937-4002-a832-612f78098afc")]
    [Trait("CaseId", "FOUNDATION-CORRELATION-001")]
    public async Task InvalidCorrelation_IsReplacedWithNonemptyUuid(string? value)
    {
        using var server = CreateServer(new CapturingSink(), context => context.Response.WriteAsync("ok"));
        using var client = server.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        if (value is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", value);
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var actual = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.True(Guid.TryParseExact(actual, "D", out var id));
        Assert.NotEqual(Guid.Empty, id);
        Assert.NotEqual(value, actual);
    }

    /// <summary>Concurrent async requests retain their own response and logging correlation scope.</summary>
    [Fact]
    [Trait("CaseId", "FOUNDATION-CORRELATION-001")]
    public async Task ConcurrentRequests_KeepIndependentCorrelationScopes()
    {
        var sink = new CapturingSink();
        using var server = CreateServer(sink, async context =>
        {
            await Task.Delay(10, context.RequestAborted);
            await context.Response.WriteAsync("ok");
        });
        using var client = server.GetTestClient();
        var ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid().ToString("D")).ToArray();

        await Task.WhenAll(ids.Select(async id =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/");
            request.Headers.Add("X-Correlation-Id", id);
            using var response = await client.SendAsync(request);
            Assert.Equal(id, response.Headers.GetValues("X-Correlation-Id").Single());
        }));

        Assert.All(ids, id => Assert.Contains(sink.Events,
            entry => entry.Properties.TryGetValue("CorrelationId", out var value)
                && value is ScalarValue { Value: string actual } && actual == id));
    }

    /// <summary>Status-only API errors use the common contract without reflecting the request path.</summary>
    [Theory]
    [InlineData(400, "ValidationFailed")]
    [InlineData(401, "AuthenticationRequired")]
    [InlineData(403, "PermissionDenied")]
    [InlineData(404, "ResourceNotFound")]
    [InlineData(405, "MethodNotAllowed")]
    [InlineData(429, "RateLimited")]
    [Trait("CaseId", "FOUNDATION-ERROR-001")]
    public async Task EmptyErrorResponse_UsesSafeProblemContract(int status, string code)
    {
        using var server = CreateServer(new CapturingSink(), context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        });
        using var client = server.GetTestClient();

        using var response = await client.GetAsync("/secret-marker");

        Assert.Equal(status, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("secret-marker", body);
    }

    /// <summary>Owner-produced safe business problems retain their domain code and version semantics.</summary>
    [Fact]
    [Trait("CaseId", "FOUNDATION-ERROR-001")]
    public async Task OwnerProblem_IsNotReplacedByGenericStatusMapping()
    {
        using var server = CreateServer(new CapturingSink(), context =>
            Results.Problem(statusCode: 409, title: "Operation payload changed.",
                extensions: new Dictionary<string, object?> { ["code"] = "IdempotencyPayloadMismatch" }).ExecuteAsync(context));
        using var client = server.GetTestClient();

        using var response = await client.PostAsync("/", null);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("IdempotencyPayloadMismatch", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(response.Headers.GetValues("X-Correlation-Id").Single(),
            body.RootElement.GetProperty("correlationId").GetString());
    }

    /// <summary>A canceled request is neither a completed operation nor an internal server fault.</summary>
    [Fact]
    [Trait("CaseId", "FOUNDATION-ERROR-001")]
    public async Task CallerCancellation_DoesNotProduceInternalError()
    {
        var sink = new CapturingSink();
        using var server = CreateServer(sink, context =>
        {
            context.RequestAborted = new CancellationToken(canceled: true);
            throw new OperationCanceledException(context.RequestAborted);
        });
        using var client = server.GetTestClient();

        using var response = await client.GetAsync("/");

        Assert.Equal(499, (int)response.StatusCode);
        Assert.DoesNotContain(sink.Events, entry => entry.Level >= LogEventLevel.Error);
    }

    private static IHost CreateServer(CapturingSink sink, RequestDelegate endpoint) =>
        new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ILogEventSink>(sink);
                services.AddWesRuntime(new ConfigurationBuilder().Build());
            })
            .Configure(app =>
            {
                app.UseWesRuntime();
                app.Run(endpoint);
            })).Start();

    private sealed class CapturingSink : ILogEventSink
    {
        internal ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}
