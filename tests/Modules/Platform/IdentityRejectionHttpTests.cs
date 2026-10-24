using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Wes.Host.Identity;
using Xunit;

namespace Wes.Modules.Tests.Platform;

/// <summary>Untrusted token rejection must finish before any persistence owner is resolved.</summary>
public sealed class IdentityRejectionHttpTests
{
    /// <summary>Public failures cannot expose vendor validation detail or attempt to use an unverified tenant.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-HTTP-003")]
    public async Task WrongIssuerIsRejectedWithoutOwnerResolutionOrFrameworkErrorDetails()
    {
        await using var factory = new RejectionFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken("untrusted-issuer", "fixture-audience",
            [new Claim("sub", Guid.NewGuid().ToString("D")), new Claim("tid", Guid.NewGuid().ToString("D")),
                new Claim("sid", Guid.NewGuid().ToString("D"))],
            now, now.AddMinutes(15),
            new SigningCredentials(new SymmetricSecurityKey(factory.Key), SecurityAlgorithms.HmacSha512));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        using var response = await client.GetAsync("/api/identity/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("error_description", response.Headers.WwwAuthenticate.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AuthenticationRequired", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private sealed class RejectionFactory : WebApplicationFactory<Program>
    {
        public byte[] Key { get; } = RandomNumberGenerator.GetBytes(64);
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Wes:Identity:Enabled"] = "true",
                ["Wes:Identity:ConnectionString"] = "NotUsedByUntrustedTokenRejection",
                ["Wes:Identity:SigningKey"] = Convert.ToBase64String(Key),
                ["Wes:Identity:ChallengeKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["Wes:Identity:Issuer"] = "fixture-issuer",
                ["Wes:Identity:Audience"] = "fixture-audience",
                ["Wes:Identity:BreachCorpusPath"] = "NotUsedByUntrustedTokenRejection",
            }));
            builder.ConfigureServices(services => services.AddSingleton<IdentityDatabase>(_ =>
                throw new InvalidOperationException("Unverified tokens must not resolve persistence owners.")));
        }
    }
}
