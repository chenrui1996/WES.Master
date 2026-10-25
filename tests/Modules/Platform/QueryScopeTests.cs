using Wes.Platform.Contracts;
using Xunit;

namespace Wes.Modules.Tests.Platform;

public sealed class QueryScopeTests
{
    private static readonly Guid TenantId = Guid.Parse("f0ca7d7b-9cda-4dca-9274-99ac88bf09ab");
    private static readonly Guid ActorId = Guid.Parse("a366fbf8-9226-4561-9ce7-ea3603c41786");
    private static readonly Guid SessionId = Guid.Parse("7b8590bb-1a8e-4312-aa3f-97d378aadfd7");
    private const string AuthorizationFingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string FilterFingerprint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string SortFingerprint = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void Fingerprints_AreCaseNormalizedForStableValueEquality()
    {
        var lower = Create();
        var upper = new QueryScope(TenantId, ActorId, SessionId,
            AuthorizationFingerprint.ToUpperInvariant(), FilterFingerprint.ToUpperInvariant(),
            SortFingerprint.ToUpperInvariant());

        Assert.Equal(lower, upper);
        Assert.Equal(AuthorizationFingerprint, upper.AuthorizationFingerprint);
        Assert.Equal(FilterFingerprint, upper.FilterFingerprint);
        Assert.Equal(SortFingerprint, upper.SortFingerprint);
    }

    [Fact]
    public void EveryIdentityAndQueryDimension_IsPartOfTheScope()
    {
        var scope = Create();

        Assert.NotEqual(scope, Create(actorId: Guid.NewGuid()));
        Assert.NotEqual(scope, Create(sessionId: Guid.NewGuid()));
        Assert.NotEqual(scope, Create(tenantId: Guid.NewGuid()));
        Assert.NotEqual(scope, Create(authorizationFingerprint: new string('d', 64)));
        Assert.NotEqual(scope, Create(filterFingerprint: new string('d', 64)));
        Assert.NotEqual(scope, Create(sortFingerprint: new string('d', 64)));
    }

    [Fact]
    public void EmptyIdentityOrNonSha256Fingerprint_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Create(tenantId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Create(actorId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Create(sessionId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Create(authorizationFingerprint: "not-a-hash"));
        Assert.Throws<ArgumentException>(() => Create(filterFingerprint: new string('g', 64)));
        Assert.Throws<ArgumentException>(() => Create(sortFingerprint: ""));
    }

    private static QueryScope Create(
        Guid? tenantId = null,
        Guid? actorId = null,
        Guid? sessionId = null,
        string? authorizationFingerprint = null,
        string? filterFingerprint = null,
        string? sortFingerprint = null) =>
        new(tenantId ?? TenantId, actorId ?? ActorId, sessionId ?? SessionId,
            authorizationFingerprint ?? AuthorizationFingerprint,
            filterFingerprint ?? FilterFingerprint,
            sortFingerprint ?? SortFingerprint);
}
