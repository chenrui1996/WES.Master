using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Platform.Domain;
using Wes.Platform.Features.Identity;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Exercises real Platform-owned optimistic refresh transactions and tenant isolation.</summary>
[Collection("PlatformPersistence")]
public sealed class PlatformSessionPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private string _connection = "";
    private bool _migrated;

    /// <summary>Uses only the already authorized test database and the Platform-specific migration history.</summary>
    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var context = Open(_tenant);
        await context.Database.MigrateAsync();
        _migrated = true;
    }

    /// <summary>The losing refresh transaction must not leave an extra successor or a live replayed family.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "IDENTITY-REFRESH-002")]
    public async Task ConcurrentRefresh_OnlyOneSuccessor_ThenReplayRevokesFamily()
    {
        var initial = new string('A', 64);
        Guid sessionId;
        Guid otherId;
        await using (var seed = Open(_tenant))
        {
            var session = new Session(_tenant, Guid.NewGuid(), SessionClient.Web, Start, initial);
            var other = new Session(_tenant, Guid.NewGuid(), SessionClient.Web, Start, new string('E', 64));
            seed.Sessions.AddRange(session, other);
            await seed.SaveChangesAsync();
            sessionId = session.Id;
            otherId = other.Id;
        }

        await using var first = Open(_tenant);
        await using var second = Open(_tenant);
        var a = await first.Sessions.Include(item => item.Credentials).SingleAsync(item => item.Id == sessionId);
        var b = await second.Sessions.Include(item => item.Credentials).SingleAsync(item => item.Id == sessionId);
        Assert.Equal(RefreshDecision.Rotated, a.Rotate(initial, new string('B', 64), Start.AddSeconds(1)));
        Assert.Equal(RefreshDecision.Rotated, b.Rotate(initial, new string('C', 64), Start.AddSeconds(1)));

        var results = await Task.WhenAll(SaveAsync(first), SaveAsync(second));

        Assert.Equal(1, results.Count(success => success));
        await using var replay = Open(_tenant);
        var current = await replay.Sessions.Include(item => item.Credentials).SingleAsync(item => item.Id == sessionId);
        Assert.Equal(2, current.Credentials.Count);
        Assert.Equal(1, current.Credentials.Count(item => item.ConsumedAt is null));
        Assert.Equal(RefreshDecision.Replayed, current.Rotate(initial, new string('D', 64), Start.AddSeconds(2)));
        await replay.SaveChangesAsync();
        await using var verification = Open(_tenant);
        Assert.Equal(SessionState.Revoked,
            (await verification.Sessions.SingleAsync(item => item.Id == sessionId)).StateAt(Start.AddSeconds(3)));
        Assert.Equal(SessionState.Active,
            (await verification.Sessions.SingleAsync(item => item.Id == otherId)).StateAt(Start.AddSeconds(3)));
    }

    /// <summary>Fresh context model reuse must not widen another tenant's session view.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "IDENTITY-TENANT-001")]
    public async Task TenantScope_NeverReturnsAnotherTenantsSessions()
    {
        var sharedId = Guid.NewGuid();
        await using (var first = Open(_tenant))
        {
            var session = new Session(_tenant, Guid.NewGuid(), SessionClient.Web, Start, new string('1', 64));
            first.Entry(session).Property(item => item.Id).CurrentValue = sharedId;
            first.Sessions.Add(session);
            await first.SaveChangesAsync();
        }

        await using (var other = Open(_otherTenant))
        {
            var session = new Session(_otherTenant, Guid.NewGuid(), SessionClient.Pda, Start, new string('2', 64));
            other.Entry(session).Property(item => item.Id).CurrentValue = sharedId;
            other.Sessions.Add(session);
            await other.SaveChangesAsync();
        }

        await using var readA = Open(_tenant);
        await using var readB = Open(_otherTenant);
        Assert.All(await readA.Sessions.ToListAsync(), item => Assert.Equal(_tenant, item.TenantId));
        Assert.All(await readB.Sessions.ToListAsync(), item => Assert.Equal(_otherTenant, item.TenantId));
        Assert.Equal(1, await readA.Sessions.CountAsync());
        Assert.Equal(1, await readB.Sessions.CountAsync());
        Assert.Equal(SessionClient.Web, (await readA.Sessions.SingleAsync(item => item.Id == sharedId)).Client);
        Assert.Equal(SessionClient.Pda, (await readB.Sessions.SingleAsync(item => item.Id == sharedId)).Client);
    }

    /// <summary>A missing tenant fails before any query or implicit shared owner can be created.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-TENANT-001")]
    public void MissingTenant_IsRejected() => Assert.Throws<ArgumentException>(() => Open(Guid.Empty));

    /// <summary>A caller cannot attach a valid foreign-tenant aggregate through a scoped context.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "IDENTITY-TENANT-001")]
    public async Task WrongTenantWrite_IsRejectedBeforeSql()
    {
        await using var context = Open(_tenant);
        context.Sessions.Add(new Session(_otherTenant, Guid.NewGuid(), SessionClient.Web, Start, new string('F', 64)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    /// <summary>The actual application handler must persist rotation before disclosure, then durably revoke on replay.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "IDENTITY-REFRESH-002")]
    public async Task Handler_CommitsRotation_ThenDurablyRevokesReplay()
    {
        var secret = RefreshSecret.Generate();
        Guid id;
        await using (var seed = Open(_tenant))
        {
            var family = new Session(_tenant, Guid.NewGuid(), SessionClient.Web, Start, secret.Digest);
            id = family.Id;
            seed.Sessions.Add(family);
            await seed.SaveChangesAsync();
        }

        await using var first = Open(_tenant);
        var rotation = await new RefreshSessionHandler(first, new FixedClock(Start.AddSeconds(1)), NullLogger<RefreshSessionHandler>.Instance)
            .HandleAsync(id, secret.Value);

        Assert.Equal(RefreshDecision.Rotated, rotation.Decision);
        Assert.NotNull(rotation.Successor);
        await using var replay = Open(_tenant);
        var rejection = await new RefreshSessionHandler(replay, new FixedClock(Start.AddSeconds(2)), NullLogger<RefreshSessionHandler>.Instance)
            .HandleAsync(id, secret.Value);
        Assert.Equal(RefreshDecision.Replayed, rejection.Decision);
        Assert.Null(rejection.Successor);
        await using var verification = Open(_tenant);
        var stored = await verification.Sessions.Include(item => item.Credentials).SingleAsync(item => item.Id == id);
        Assert.Equal(SessionState.Revoked, stored.StateAt(Start.AddSeconds(3)));
        Assert.DoesNotContain(stored.Credentials, item => item.Digest == secret.Value);
        Assert.Equal(2, stored.Credentials.Count);
    }

    /// <summary>A real overlapping handler race must execute the re-read path and commit replay revocation.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "IDENTITY-REFRESH-002")]
    public async Task ConcurrentHandlers_OneDisclosesSuccessor_ConflictLoserRevokesFamily()
    {
        var secret = RefreshSecret.Generate();
        Guid id;
        await using (var seed = Open(_tenant))
        {
            var family = new Session(_tenant, Guid.NewGuid(), SessionClient.Web, Start, secret.Digest);
            id = family.Id;
            seed.Sessions.Add(family);
            await seed.SaveChangesAsync();
        }

        var barrier = new SaveBarrier();
        await using var a = Open(_tenant, barrier);
        await using var b = Open(_tenant, barrier);
        var clock = new FixedClock(Start.AddSeconds(1));
        var outcomes = await Task.WhenAll(
            new RefreshSessionHandler(a, clock, NullLogger<RefreshSessionHandler>.Instance).HandleAsync(id, secret.Value),
            new RefreshSessionHandler(b, clock, NullLogger<RefreshSessionHandler>.Instance).HandleAsync(id, secret.Value));

        Assert.Single(outcomes, item => item.Decision == RefreshDecision.Rotated && item.Successor is not null);
        Assert.Single(outcomes, item => item.Decision == RefreshDecision.Replayed && item.Successor is null);
        await using var verification = Open(_tenant);
        var stored = await verification.Sessions.Include(item => item.Credentials).SingleAsync(item => item.Id == id);
        Assert.Equal(SessionState.Revoked, stored.StateAt(Start.AddSeconds(2)));
        Assert.Equal(2, stored.Credentials.Count);
    }

    /// <summary>The database composite foreign key rejects a token attached to a different tenant's session.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "IDENTITY-TENANT-001")]
    public async Task DatabaseForeignKey_CannotAttachCrossTenantCredential()
    {
        Guid id;
        await using (var seed = Open(_otherTenant))
        {
            var family = new Session(_otherTenant, Guid.NewGuid(), SessionClient.Web, Start, new string('3', 64));
            id = family.Id;
            seed.Sessions.Add(family);
            await seed.SaveChangesAsync();
        }

        await using var context = Open(_tenant);
        var digest = new string('4', 64);
        var error = await Assert.ThrowsAsync<MySql.Data.MySqlClient.MySqlException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO platform_refresh_tokens (TenantId,Digest,SessionId,ConsumedAt) VALUES ({_tenant.ToString("D")},{digest},{id.ToString("D")},NULL)"));

        Assert.Equal(1452, error.Number);
        Assert.Equal(0, await context.Set<RefreshCredential>().CountAsync());
    }

    /// <summary>A failed unit of work discloses no secret and leaves no durable credential transition.</summary>
    [Fact]
    [Trait("Provider", "MySQL")]
    [Trait("CaseId", "IDENTITY-REFRESH-002")]
    public async Task Handler_PersistenceFault_DoesNotReturnSuccessOrChangeFamily()
    {
        var secret = RefreshSecret.Generate();
        Guid id;
        await using (var seed = Open(_tenant))
        {
            var session = new Session(_tenant, Guid.NewGuid(), SessionClient.Web, Start, secret.Digest);
            id = session.Id;
            seed.Sessions.Add(session);
            await seed.SaveChangesAsync();
        }

        await using var faulty = Open(_tenant, new FailSave());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RefreshSessionHandler(faulty, new FixedClock(Start.AddSeconds(1)), NullLogger<RefreshSessionHandler>.Instance)
                .HandleAsync(id, secret.Value));

        await using var verification = Open(_tenant);
        var stored = await verification.Sessions.Include(item => item.Credentials).SingleAsync(item => item.Id == id);
        Assert.Null(Assert.Single(stored.Credentials).ConsumedAt);
        Assert.Null(stored.RevokedAt);
    }

    /// <summary>Only generated test tenants are removed, with child-first tracked key deletion.</summary>
    public async Task DisposeAsync()
    {
        if (!_migrated)
        {
            return;
        }

        foreach (var tenant in new[] { _tenant, _otherTenant })
        {
            await using var context = Open(tenant);
            var rows = await context.Sessions.Include(item => item.Credentials).ToListAsync();
            context.Sessions.RemoveRange(rows);
            await context.SaveChangesAsync();
            Assert.Equal(0, await context.Sessions.CountAsync());
            Assert.Equal(0, await context.Set<RefreshCredential>().CountAsync());
        }
    }

    private PlatformDbContext Open(Guid tenant, IInterceptor? interceptor = null)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseMySQL(_connection, mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations"));
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new PlatformDbContext(options.Options, tenant);
    }

    private static async Task<bool> SaveAsync(PlatformDbContext context)
    {
        try
        {
            await context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private sealed class FixedClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private sealed class SaveBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _bothReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
            {
                _bothReady.TrySetResult();
            }

            await _bothReady.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Injected persistence fault.");
    }
}
