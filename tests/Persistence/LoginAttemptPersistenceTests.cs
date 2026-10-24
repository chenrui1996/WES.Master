using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MySql.Data.MySqlClient;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Identity;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Actual coordinated source/challenge/risk/credential/session transactions on the authorized isolated MySQL target.</summary>
[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed class LoginAttemptPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 6, 0, 0, TimeSpan.Zero);
    private const string Account = "OPERATOR";
    private const string Password = "WES reference fixture password";
    private const string Legacy = "$2a$12$N9qo8uLOickgx2ZMRZoMyeA6a9XHSXAtYQSvRT4Sc0ziVCqOGHZUS";
    private const string Modern = "$2a$12$N9qo8uLOickgx2ZMRZoMyebq.QrfAcQ2j7P1iiG.cM44tPZp1iuQS";
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _user = Guid.NewGuid();
    private readonly LoginSourceKey _source = LoginSourceKey.FromTrustedPeer(new IPAddress(Guid.NewGuid().ToByteArray()));
    private readonly LoginChallengeProtection _protection = new(Enumerable.Range(0, 32).Select(item => (byte)item).ToArray());
    private readonly LoginAccountKey _account = LoginAccountKey.FromNormalizedAccount(Account);
    private string _connection = "";
    private bool _migrated;

    /// <summary>Applies only reviewed source and Platform owner migrations, never any old business schema.</summary>
    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var database = Open();
        await database.Database.MigrateAsync();
        await using var source = Sources();
        await source.Database.MigrateAsync();
        _migrated = true;
    }

    /// <summary>Only a durable commit discloses a secret; raw refresh material never appears in default JSON/formatting.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-LOGIN-OWNER-001")]
    public async Task CorrectCredentialCommitsSourceRiskFenceFamilyAndSecurityEvidence(bool legacy)
    {
        var version = await SeedCredentialAsync(legacy);
        await using var write = Open();
        await using var sources = Sources();
        var correlation = Guid.NewGuid();
        var result = await Handler(write, sources).HandleAsync(Account, Password, _source, SessionClient.Web, correlation);
        Assert.True(result.Completed);
        var delivery = Assert.IsType<LoginSessionDelivery>(result.Delivery);
        Assert.Equal(legacy, delivery.RequiresPasswordChange);
        Assert.Equal(_user, delivery.UserId);
        Assert.Empty(write.ChangeTracker.Entries());
        Assert.Empty(sources.ChangeTracker.Entries());
        var output = JsonSerializer.Serialize(result) + JsonSerializer.Serialize(delivery) + result + delivery;
        Assert.DoesNotContain(Password, output);
        Assert.DoesNotContain(delivery.Secret.Value, output);
        Assert.DoesNotContain(delivery.Secret.Digest, output);
        await using var verify = Open();
        var family = await verify.Sessions.Include(item => item.Credentials).SingleAsync();
        Assert.Equal(delivery.SessionId, family.Id);
        Assert.Equal(legacy, family.RequiresPasswordChange);
        Assert.Equal(delivery.Secret.Digest, Assert.Single(family.Credentials).Digest);
        Assert.Equal(Start, family.CreatedAt);
        Assert.True((await verify.PasswordCredentials.SingleAsync()).Enabled);
        Assert.NotEqual(version, (await verify.PasswordCredentials.SingleAsync()).Version);
        Assert.Empty((await verify.LoginAccounts.SingleAsync()).Failures);
        var evidence = await verify.LoginEvents.SingleAsync();
        Assert.Equal(legacy ? LoginEventKind.LegacyRestricted : LoginEventKind.Completed, evidence.Kind);
        Assert.Equal(delivery.SessionId, evidence.SessionId);
        Assert.Equal(correlation, evidence.CorrelationId);
        Assert.Equal(_account.Digest, evidence.AccountDigest);
        Assert.Single((await sources.Sources.SingleAsync(item => item.SourceDigest == _source.Digest)).Observations);
    }

    /// <summary>Unknown, disabled and incorrect proofs share the transport shape and durable failure behavior.</summary>
    [Theory]
    [InlineData("unknown")]
    [InlineData("wrong")]
    [InlineData("disabled")]
    [Trait("CaseId", "IDENTITY-LOGIN-OWNER-001")]
    public async Task InvalidCredentialHasGenericResultAndDurableFailure(string variant)
    {
        if (variant != "unknown") await SeedCredentialAsync(false, variant != "disabled");
        await using var write = Open();
        await using var sources = Sources();
        var result = await Handler(write, sources).HandleAsync(Account, variant == "wrong" ? "wrong test password" : Password,
            _source, SessionClient.Web, Guid.NewGuid());
        Assert.False(result.Completed);
        Assert.Null(result.Delivery);
        Assert.Equal("{\"Completed\":false}", JsonSerializer.Serialize(result));
        await using var verify = Open();
        Assert.Empty(await verify.Sessions.ToListAsync());
        Assert.Single((await verify.LoginAccounts.SingleAsync()).Failures);
        Assert.Equal(LoginEventKind.InvalidCredential, (await verify.LoginEvents.SingleAsync()).Kind);
        Assert.Single((await sources.Sources.SingleAsync(item => item.SourceDigest == _source.Digest)).Observations);
    }

    /// <summary>Three failures block the fourth unchecked candidate; one-shot challenges permit fourth/fifth failures, then lock.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-RISK-001")]
    public async Task AccountChallengeAndFifthFailureLockAreCoordinatedAndDurable()
    {
        await SeedCredentialAsync(false);
        for (var index = 0; index < 3; index++) Assert.False((await AttemptAsync("wrong test password")).Completed);
        Assert.False((await AttemptAsync(Password)).Completed);
        await using (var read = Open())
        {
            Assert.Equal(3, (await read.LoginAccounts.SingleAsync()).Failures.Count);
            Assert.Equal(1, await read.LoginEvents.CountAsync(item => item.Kind == LoginEventKind.ChallengeRequired));
        }
        for (var index = 0; index < 2; index++)
        {
            var challenge = await IssueAsync();
            Assert.False((await AttemptAsync("wrong test password", challenge: challenge)).Completed);
        }
        var blockedChallenge = await IssueAsync();
        Assert.False((await AttemptAsync(Password, challenge: blockedChallenge)).Completed);
        await using (var read = Open())
        {
            Assert.Equal(5, (await read.LoginAccounts.SingleAsync()).Failures.Count);
            Assert.Equal(Start.AddSeconds(900), (await read.LoginAccounts.SingleAsync()).LockedUntil);
            Assert.Equal(1, await read.LoginEvents.CountAsync(item => item.Kind == LoginEventKind.Locked));
            Assert.Empty(await read.Sessions.ToListAsync());
        }
        Assert.True((await AttemptAsync(Password, Start.AddSeconds(900))).Completed);
    }

    /// <summary>Bad and expired challenge submissions burn one bound entry, consume source budget and never verify a candidate.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-LOGIN-RISK-001")]
    public async Task WrongOrExpiredChallengeCannotBecomeAnExtraPasswordAttempt(bool expired)
    {
        await SeedCredentialAsync(false);
        await SeedRiskAsync(3);
        var challenge = await IssueAsync();
        await using var write = Open();
        await using var sources = Sources();
        var result = await Handler(write, sources, expired ? Start.AddSeconds(120) : null).HandleAsync(Account, Password, _source,
            SessionClient.Web, Guid.NewGuid(), challenge.Id, expired ? challenge.Answer : "!!!!!!");
        Assert.False(result.Completed);
        await using var verify = Open();
        Assert.Equal(3, (await verify.LoginAccounts.SingleAsync()).Failures.Count);
        Assert.Empty(await verify.Sessions.ToListAsync());
        Assert.Equal(LoginEventKind.ChallengeRejected, (await verify.LoginEvents.SingleAsync()).Kind);
        if (!expired) Assert.NotNull((await verify.LoginChallenges.SingleAsync()).ConsumedAt);
        Assert.Single((await sources.Sources.SingleAsync(item => item.SourceDigest == _source.Digest)).Observations);
    }

    /// <summary>Thirty committed source observations deny the thirty-first request even with a correct password, until exact expiry.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-RISK-001")]
    public async Task SourceThirtyFirstCannotReachCredentialsOrCreateRiskOrSession()
    {
        await SeedCredentialAsync(false);
        await using (var seed = Sources())
        {
            var budget = new LoginSourceBudget(_source);
            for (var index = 0; index < 30; index++) Assert.Equal(SourceAdmission.Admitted, budget.Admit(Start));
            seed.Sources.Add(budget);
            await seed.SaveChangesAsync();
        }
        Assert.False((await AttemptAsync(Password)).Completed);
        await using (var verify = Open())
        {
            Assert.Empty(await verify.LoginAccounts.ToListAsync());
            Assert.Empty(await verify.Sessions.ToListAsync());
            Assert.Equal(LoginEventKind.SourceLimited, (await verify.LoginEvents.SingleAsync()).Kind);
        }
        Assert.True((await AttemptAsync(Password, Start.AddSeconds(60))).Completed);
    }

    /// <summary>Other tenant credentials do not exist in this owner scope; same digest risk remains independent.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-OWNER-001")]
    public async Task TenantScopedLookupCannotIssueAnotherTenantsIdentity()
    {
        await SeedCredentialAsync(false, tenant: _otherTenant);
        Assert.False((await AttemptAsync(Password)).Completed);
        await using var own = Open();
        Assert.Single((await own.LoginAccounts.SingleAsync()).Failures);
        await using var other = Open(_otherTenant);
        Assert.Empty(await other.Sessions.ToListAsync());
        Assert.Empty(await other.LoginAccounts.ToListAsync());
        Assert.Empty(await other.LoginEvents.ToListAsync());
    }

    /// <summary>Login evidence is subject to the same append-only and trusted tenant guards as other security audit facts.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-MIGRATION-001")]
    public async Task EvidenceCannotBeChangedDeletedOrAttachedAcrossTenants()
    {
        await AttemptAsync("wrong test password");
        await using var write = Open();
        var evidence = await write.LoginEvents.SingleAsync();
        write.Entry(evidence).Property(item => item.Kind).CurrentValue = LoginEventKind.Locked;
        await Assert.ThrowsAsync<InvalidOperationException>(() => write.SaveChangesAsync());
        write.ChangeTracker.Clear();
        write.LoginEvents.Remove(await write.LoginEvents.SingleAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => write.SaveChangesAsync());
        write.ChangeTracker.Clear();
        write.LoginEvents.Add(new LoginSecurityEvent(_otherTenant, _account.Digest, _source.Digest, Guid.NewGuid(),
            LoginEventKind.InvalidCredential, Start));
        await Assert.ThrowsAsync<InvalidOperationException>(() => write.SaveChangesAsync());
    }

    /// <summary>A real event PK conflict rolls the risk/session/credential commit back but does not refund independently consumed source budget.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-LOGIN-FAULT-001")]
    public async Task ActualEvidenceConstraintFailureRollsBackLocalCommit(bool correct)
    {
        var version = await SeedCredentialAsync(false);
        var existing = new LoginSecurityEvent(_tenant, _account.Digest, _source.Digest, Guid.NewGuid(), LoginEventKind.InvalidCredential, Start);
        await using (var seed = Open())
        {
            seed.LoginEvents.Add(existing);
            await seed.SaveChangesAsync();
        }
        await using var write = Open(interceptor: new DuplicateEvent(existing.Id));
        await using var sources = Sources();
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => Handler(write, sources).HandleAsync(Account,
            correct ? Password : "wrong test password", _source, SessionClient.Web, Guid.NewGuid()));
        Assert.Equal(1062, Assert.IsType<MySqlException>(error.GetBaseException()).Number);
        Assert.Empty(write.ChangeTracker.Entries());
        await using var verify = Open();
        Assert.Empty(await verify.Sessions.ToListAsync());
        Assert.Empty((await verify.LoginAccounts.SingleAsync()).Failures);
        Assert.Equal(version, (await verify.PasswordCredentials.SingleAsync()).Version);
        Assert.Single(await verify.LoginEvents.ToListAsync());
        Assert.Single((await sources.Sources.SingleAsync(item => item.SourceDigest == _source.Digest)).Observations);
    }

    /// <summary>Caller state and outer transactions cannot defer the owner commit; guards run before consuming any source budget.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-FAULT-001")]
    public async Task DirtyContextsExternalTransactionsAndPreCancellationAreExplicit()
    {
        await using var write = Open();
        await using var sources = Sources();
        Task<LoginAttemptResult> Invoke(CancellationToken token = default) => Handler(write, sources).HandleAsync(Account,
            Password, _source, SessionClient.Web, Guid.NewGuid(), cancellationToken: token);
        write.LoginAccounts.Add(new LoginAccountBudget(_tenant, _account));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke());
        Assert.Single(write.ChangeTracker.Entries());
        write.ChangeTracker.Clear();
        sources.Sources.Add(new LoginSourceBudget(_source));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke());
        Assert.Single(sources.ChangeTracker.Entries());
        sources.ChangeTracker.Clear();
        write.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke());
        write.Database.AutoTransactionBehavior = AutoTransactionBehavior.WhenNeeded;
        await using (var transaction = await write.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke());
            await transaction.RollbackAsync();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(new CancellationToken(true)));
        using var scope = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke());
    }

    /// <summary>Every truly admitted wrong verification has a committed event even when contenders cross both gates while in flight.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-CONCURRENT-001")]
    public async Task ConcurrentWrongCandidatesMergeWithoutLosingFactsOrExtendingTheFifthLock()
    {
        await SeedCredentialAsync(false);
        await SeedRiskAsync(0);
        var barrier = new EventBarrier(6);
        async Task<LoginAttemptResult> Contender()
        {
            await using var write = Open(interceptor: barrier);
            await using var sources = Sources();
            return await Handler(write, sources).HandleAsync(Account, "wrong test password", _source, SessionClient.Web, Guid.NewGuid());
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Contender()));
        Assert.All(results, result => Assert.False(result.Completed));
        await using var verify = Open();
        var budget = await verify.LoginAccounts.SingleAsync();
        Assert.Equal(5, budget.Failures.Count);
        Assert.Equal(Start.AddSeconds(900), budget.LockedUntil);
        Assert.Equal(6, await verify.LoginEvents.CountAsync(item => item.Kind == LoginEventKind.InvalidCredential));
        Assert.Empty(await verify.Sessions.ToListAsync());
    }

    /// <summary>The same challenge cannot authorize two password attempts or two session deliveries.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-CONCURRENT-001")]
    public async Task OneChallengeHasOnlyOneSuccessfulLoginConsumer()
    {
        await SeedCredentialAsync(false);
        await SeedRiskAsync(3);
        var challenge = await IssueAsync();
        var results = await Task.WhenAll(AttemptAsync(Password, challenge: challenge), AttemptAsync(Password, challenge: challenge));
        Assert.Single(results, result => result.Completed);
        await using var verify = Open();
        Assert.Single(await verify.Sessions.ToListAsync());
        Assert.Equal(3, (await verify.LoginAccounts.SingleAsync()).Failures.Count);
        Assert.Equal(1, await verify.LoginEvents.CountAsync(item => item.Kind == LoginEventKind.Completed));
        Assert.Equal(1, await verify.LoginEvents.CountAsync(item => item.Kind == LoginEventKind.ChallengeRejected));
    }

    /// <summary>Successful password proof cannot be applied to a newer risk version, nor silently retried with a consumed challenge.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-CONCURRENT-001")]
    public async Task ConcurrentAccountFailureFencesOtherwiseCorrectSessionCommit()
    {
        var version = await SeedCredentialAsync(false);
        await SeedRiskAsync(0);
        await using var write = Open(interceptor: new CompetingRiskWrite(async () =>
        {
            await using var competitor = Open();
            (await competitor.LoginAccounts.SingleAsync()).RecordAdmittedFailure(Start);
            await competitor.SaveChangesAsync();
        }));
        await using var sources = Sources();
        var outcome = await Handler(write, sources).HandleAsync(Account, Password, _source, SessionClient.Web, Guid.NewGuid());
        Assert.False(outcome.Completed);
        await using var verify = Open();
        Assert.Single((await verify.LoginAccounts.SingleAsync()).Failures);
        Assert.Equal(version, (await verify.PasswordCredentials.SingleAsync()).Version);
        Assert.Empty(await verify.Sessions.ToListAsync());
        Assert.Equal(LoginEventKind.VersionConflict, (await verify.LoginEvents.SingleAsync()).Kind);
    }

    /// <summary>The credential CAS closes the previous scan-after-new-family gap in both actual commit orders.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-LOGIN-CONCURRENT-001")]
    public async Task LoginAndPasswordChangeShareOneCommitFence(bool loginFirst)
    {
        var version = await SeedCredentialAsync(false);
        await SeedRiskAsync(0);
        Guid currentId;
        await using (var seed = Open())
        {
            var current = new Session(_tenant, _user, SessionClient.Web, Start, RefreshSecret.Generate().Digest);
            currentId = current.Id;
            seed.Sessions.Add(current);
            await seed.SaveChangesAsync();
        }
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var login = Open(interceptor: new OrderedCommit(loginFirst, staged, committed));
        await using var change = Open(interceptor: new OrderedCommit(!loginFirst, staged, committed));
        await using var sources = Sources();
        var context = new TenantContext(_tenant, _user, currentId);
        var loginTask = Handler(login, sources).HandleAsync(Account, Password, _source, SessionClient.Web, Guid.NewGuid());
        var changeTask = new ChangePasswordHandler(change, new FixedClock(Start.AddSeconds(1)),
            NullLogger<ChangePasswordHandler>.Instance).HandleAsync(context, version, Password, "new reviewed replacement password",
                PasswordEvidence.Clear, Guid.NewGuid());
        await Task.WhenAll(loginTask, changeTask);
        Assert.Equal(loginFirst, (await loginTask).Completed);
        Assert.Equal(loginFirst ? PasswordChangeDecision.VersionConflict : PasswordChangeDecision.Completed, (await changeTask).Decision);
        if (loginFirst)
        {
            await using var retry = Open();
            var currentVersion = (await retry.PasswordCredentials.AsNoTracking().SingleAsync()).Version;
            Assert.Equal(PasswordChangeDecision.Completed,
                (await new ChangePasswordHandler(retry, new FixedClock(Start.AddSeconds(2)),
                    NullLogger<ChangePasswordHandler>.Instance).HandleAsync(context, currentVersion, Password,
                    "new reviewed replacement password", PasswordEvidence.Clear, Guid.NewGuid())).Decision);
        }
        await using var verify = Open();
        Assert.All(await verify.Sessions.ToListAsync(), family => Assert.NotNull(family.RevokedAt));
        Assert.Equal(CredentialVerification.Invalid, (await verify.PasswordCredentials.SingleAsync()).Verify(Password));
        Assert.False((await AttemptAsync(Password, Start.AddSeconds(3))).Completed);
    }

    /// <summary>Real role assignments cannot override the persisted legacy restriction, even after refresh reloads the same family.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-LOGIN-OWNER-001")]
    public async Task LegacyRestrictionSurvivesRoleGrantsAndRefresh(bool legacy)
    {
        await SeedCredentialAsync(legacy);
        await using (var seed = Open())
        {
            var role = new AccessRole(_tenant, "operator", [new PermissionGrant("platform.users.write", DataScope.All,
                ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Enabled"))]);
            seed.Roles.Add(role);
            seed.Memberships.Add(new RoleMembership(_tenant, _user, role.Id));
            await seed.SaveChangesAsync();
        }
        var outcome = await AttemptAsync(Password);
        var delivery = Assert.IsType<LoginSessionDelivery>(outcome.Delivery);
        var context = new TenantContext(_tenant, _user, delivery.SessionId);
        await using (var read = Open())
        {
            var grants = await new CurrentAuthorization(read, new FixedClock(Start)).ReadGrantsAsync(context);
            Assert.Equal(legacy ? 0 : 1, grants.Length);
        }
        await using (var refresh = Open())
            Assert.Equal(RefreshDecision.Rotated, (await new RefreshSessionHandler(refresh, new FixedClock(Start.AddSeconds(1)),
                NullLogger<RefreshSessionHandler>.Instance).HandleAsync(delivery.SessionId, delivery.Secret.Value)).Decision);
        await using var verify = Open();
        var after = await new CurrentAuthorization(verify, new FixedClock(Start.AddSeconds(2))).ReadGrantsAsync(context);
        Assert.Equal(legacy ? 0 : 1, after.Length);
        Assert.Equal(legacy, (await verify.Sessions.SingleAsync()).RequiresPasswordChange);
    }

    /// <summary>Cancellation at the owner write never refunds the consumed challenge or source, nor discloses a successor.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-LOGIN-FAULT-001")]
    public async Task CancellationAtEvidenceSavePreservesIndependentConsumptionOnly(bool correct)
    {
        var version = await SeedCredentialAsync(false);
        await SeedRiskAsync(3);
        var challenge = await IssueAsync();
        using var cancellation = new CancellationTokenSource();
        await using var write = Open(interceptor: new CancelEventSave(cancellation));
        await using var sources = Sources();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(write, sources).HandleAsync(Account,
            correct ? Password : "wrong test password", _source, SessionClient.Web, Guid.NewGuid(), challenge.Id, challenge.Answer,
            cancellation.Token));
        Assert.Empty(write.ChangeTracker.Entries());
        await using var verify = Open();
        Assert.Equal(version, (await verify.PasswordCredentials.SingleAsync()).Version);
        Assert.Equal(3, (await verify.LoginAccounts.SingleAsync()).Failures.Count);
        Assert.NotNull((await verify.LoginChallenges.SingleAsync()).ConsumedAt);
        Assert.Empty(await verify.LoginEvents.ToListAsync());
        Assert.Empty(await verify.Sessions.ToListAsync());
        Assert.Single((await sources.Sources.SingleAsync(item => item.SourceDigest == _source.Digest)).Observations);
    }

    /// <summary>Eight actual competing commits cannot be reported as a durably recorded failure.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-FAULT-001")]
    public async Task FailureContentionExhaustionIsAnExplicitFaultRatherThanARecordedDenial()
    {
        await SeedCredentialAsync(false);
        await SeedRiskAsync(0);
        var commits = 0;
        await using var write = Open(interceptor: new CompetingRiskWrite(async () =>
        {
            await using var competitor = Open();
            Assert.Equal(LoginGate.VerifyPassword, (await competitor.LoginAccounts.SingleAsync()).FenceSuccess(Start, false));
            await competitor.SaveChangesAsync();
            commits++;
        }, failures: true));
        await using var sources = Sources();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(write, sources).HandleAsync(Account, "wrong test password",
            _source, SessionClient.Web, Guid.NewGuid()));
        Assert.Equal(8, commits);
        Assert.Empty(write.ChangeTracker.Entries());
        await using var verify = Open();
        Assert.Empty(await verify.LoginEvents.ToListAsync());
        Assert.Empty(await verify.Sessions.ToListAsync());
        Assert.Empty((await verify.LoginAccounts.SingleAsync()).Failures);
        Assert.Single((await sources.Sources.SingleAsync(item => item.SourceDigest == _source.Digest)).Observations);
    }

    /// <summary>A backward clock after the BCrypt proof cannot timestamp a new family or a failure before its checked instant.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-LOGIN-FAULT-001")]
    public async Task ClockReversalAfterVerificationFailsWithoutLocalCommit(bool correct)
    {
        await SeedCredentialAsync(false);
        await using var write = Open();
        await using var sources = Sources();
        var handler = new LoginAttemptHandler(write, sources, _protection, new ReversingClock(), NullLoggerFactory.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(Account, correct ? Password : "wrong test password",
            _source, SessionClient.Web, Guid.NewGuid()));
        Assert.Empty(write.ChangeTracker.Entries());
        await using var verify = Open();
        Assert.Empty(await verify.Sessions.ToListAsync());
        Assert.Empty(await verify.LoginEvents.ToListAsync());
        Assert.Empty((await verify.LoginAccounts.SingleAsync()).Failures);
    }

    /// <summary>First-use account creation handles the actual tenant/digest primary-key race without swallowing unrelated SQL errors.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-LOGIN-CONCURRENT-001")]
    public async Task ConcurrentFirstUseInitializesOnlyOneAccountBudget()
    {
        await SeedCredentialAsync(false);
        var barrier = new RiskCreationBarrier();
        async Task<LoginAttemptResult> Contender()
        {
            await using var write = Open(interceptor: barrier);
            await using var source = Sources();
            return await Handler(write, source).HandleAsync(Account, Password, _source, SessionClient.Web, Guid.NewGuid());
        }
        var results = await Task.WhenAll(Contender(), Contender());
        Assert.InRange(results.Count(result => result.Completed), 1, 2);
        await using var verify = Open();
        Assert.Single(await verify.LoginAccounts.ToListAsync());
        Assert.Equal(results.Count(result => result.Completed), await verify.Sessions.CountAsync());
        Assert.Equal(2, await verify.LoginEvents.CountAsync());
    }

    /// <summary>Disabled first rejects the in-flight login CAS; login first forces a fresh disable scan that includes its new family.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "IDENTITY-DISABLE-CONCURRENT-001")]
    public async Task LoginAndDisableShareCredentialFenceInBothCommitOrders(bool loginFirst)
    {
        var version = await SeedCredentialAsync(false);
        await SeedRiskAsync(0);
        Guid currentId;
        await using (var seed = Open())
        {
            var current = new Session(_tenant, _user, SessionClient.Web, Start, RefreshSecret.Generate().Digest);
            currentId = current.Id;
            var role = new AccessRole(_tenant, "manager", [new PermissionGrant("platform.users.write", DataScope.Own,
                ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Enabled"))]);
            seed.Roles.Add(role);
            seed.Memberships.Add(new RoleMembership(_tenant, _user, role.Id));
            seed.Sessions.Add(current);
            await seed.SaveChangesAsync();
        }
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var login = Open(interceptor: new OrderedCommit(loginFirst, staged, committed));
        await using var disable = Open(interceptor: new OrderedCommit(!loginFirst, staged, committed));
        await using var sources = Sources();
        var context = new TenantContext(_tenant, _user, currentId);
        var loginTask = Handler(login, sources).HandleAsync(Account, Password, _source, SessionClient.Web, Guid.NewGuid());
        var disableTask = new DisableAccountHandler(disable, new FixedClock(Start.AddSeconds(1)),
            NullLogger<DisableAccountHandler>.Instance).HandleAsync(context, _user, version, Guid.NewGuid());
        await Task.WhenAll(loginTask, disableTask);
        Assert.Equal(loginFirst, (await loginTask).Completed);
        Assert.Equal(loginFirst ? AccountDisableDecision.VersionConflict : AccountDisableDecision.Completed, await disableTask);
        if (loginFirst)
        {
            await using var retry = Open();
            var currentVersion = (await retry.PasswordCredentials.AsNoTracking().SingleAsync()).Version;
            Assert.Equal(AccountDisableDecision.Completed,
                await new DisableAccountHandler(retry, new FixedClock(Start.AddSeconds(2)), NullLogger<DisableAccountHandler>.Instance)
                    .HandleAsync(context, _user, currentVersion, Guid.NewGuid()));
        }
        await using var verify = Open();
        Assert.False((await verify.PasswordCredentials.SingleAsync()).Enabled);
        Assert.All(await verify.Sessions.ToListAsync(), family => Assert.NotNull(family.RevokedAt));
        Assert.Empty(await new CurrentAuthorization(verify, new FixedClock(Start.AddSeconds(3))).ReadGrantsAsync(context));
        Assert.False((await AttemptAsync(Password, Start.AddSeconds(3))).Completed);
        await using var passwordChange = Open();
        Assert.Equal(PasswordChangeDecision.Rejected,
            (await new ChangePasswordHandler(passwordChange, new FixedClock(Start.AddSeconds(4)), NullLogger<ChangePasswordHandler>.Instance)
                .HandleAsync(context, Guid.NewGuid().ToString("D"), Password, "new reviewed replacement password",
                    PasswordEvidence.Clear, Guid.NewGuid())).Decision);
    }

    /// <summary>Fail-closed imported disabled credentials cannot use a surviving active family to change their password.</summary>
    [Fact]
    [Trait("CaseId", "IDENTITY-DISABLE-OWNER-001")]
    public async Task DisabledCredentialRejectsPasswordChangeEvenWithAnActiveFamily()
    {
        var version = await SeedCredentialAsync(false, false);
        Guid id;
        await using (var seed = Open())
        {
            var session = new Session(_tenant, _user, SessionClient.Web, Start, RefreshSecret.Generate().Digest);
            id = session.Id;
            seed.Sessions.Add(session);
            await seed.SaveChangesAsync();
        }
        await using var change = Open();
        Assert.Equal(PasswordChangeDecision.Rejected,
            (await new ChangePasswordHandler(change, new FixedClock(Start.AddSeconds(1)), NullLogger<ChangePasswordHandler>.Instance)
                .HandleAsync(new TenantContext(_tenant, _user, id), version, Password,
                    "new reviewed replacement password", PasswordEvidence.Clear, Guid.NewGuid())).Decision);
        Assert.Empty(change.ChangeTracker.Entries());
        await using var verify = Open();
        Assert.Equal(version, (await verify.PasswordCredentials.SingleAsync()).Version);
        Assert.Equal(SessionState.Active, (await verify.Sessions.SingleAsync()).StateAt(Start.AddSeconds(2)));
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    private async Task<string> SeedCredentialAsync(bool legacy, bool enabled = true, Guid? tenant = null)
    {
        await using var seed = Open(tenant);
        var credential = new PasswordCredential(tenant ?? _tenant, _user, Account, StoredPasswordHash.Import(
            legacy ? PasswordHashScheme.SevenBcryptV1 : PasswordHashScheme.BcryptSha384V1, legacy ? Legacy : Modern), Start);
        credential.SetEnabled(enabled, Start);
        seed.PasswordCredentials.Add(credential);
        await seed.SaveChangesAsync();
        return credential.Version;
    }

    private async Task SeedRiskAsync(int count)
    {
        await using var seed = Open();
        var budget = new LoginAccountBudget(_tenant, _account);
        for (var index = 0; index < count; index++) budget.RecordAdmittedFailure(Start);
        seed.LoginAccounts.Add(budget);
        await seed.SaveChangesAsync();
    }

    private async Task<LoginChallengeDelivery> IssueAsync()
    {
        await using var write = Open();
        return await new LoginChallengeHandler(write, new FixedClock(Start), _protection, NullLogger<LoginChallengeHandler>.Instance)
            .IssueAsync(_account, _source);
    }

    private async Task<LoginAttemptResult> AttemptAsync(string password, DateTimeOffset? now = null, LoginChallengeDelivery? challenge = null)
    {
        await using var write = Open();
        await using var sources = Sources();
        return await Handler(write, sources, now).HandleAsync(Account, password, _source, SessionClient.Web,
            Guid.NewGuid(), challenge?.Id, challenge?.Answer);
    }

    private LoginAttemptHandler Handler(PlatformDbContext database, LoginSourceDbContext sources, DateTimeOffset? now = null) =>
        new(database, sources, _protection, new FixedClock(now ?? Start), NullLoggerFactory.Instance);

    private PlatformDbContext Open(Guid? tenant = null, IInterceptor? interceptor = null)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseMySQL(_connection, mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new PlatformDbContext(options.Options, tenant ?? _tenant);
    }

    private LoginSourceDbContext Sources()
    {
        MySqlTestConnection.ValidateTarget(_connection);
        return new LoginSourceDbContext(new DbContextOptionsBuilder<LoginSourceDbContext>()
            .UseMySQL(_connection, mysql => mysql.MigrationsHistoryTable("__WesLoginSourceMigrations")).Options);
    }

    /// <summary>Exact fixture tenants and exact random source digest only; security evidence cleanup is test-only.</summary>
    public async Task DisposeAsync()
    {
        try
        {
            if (!_migrated) return;
            foreach (var tenant in new[] { _tenant, _otherTenant })
            {
                await using var database = Open(tenant);
                await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_login_events WHERE TenantId = {tenant.ToString("D")}");
                await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_audit WHERE TenantId = {tenant.ToString("D")}");
                database.Sessions.RemoveRange(await database.Sessions.ToListAsync());
                database.PasswordCredentials.RemoveRange(await database.PasswordCredentials.ToListAsync());
                database.LoginAccounts.RemoveRange(await database.LoginAccounts.ToListAsync());
                database.LoginChallenges.RemoveRange(await database.LoginChallenges.ToListAsync());
                database.Memberships.RemoveRange(await database.Memberships.ToListAsync());
                database.Roles.RemoveRange(await database.Roles.ToListAsync());
                await database.SaveChangesAsync();
            }
            await using var source = Sources();
            source.Sources.RemoveRange(await source.Sources.Where(item => item.SourceDigest == _source.Digest).ToListAsync());
            await source.SaveChangesAsync();
        }
        finally
        {
            _protection.Dispose();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ReversingClock : TimeProvider
    {
        private int _reads;
        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref _reads) <= 3 ? Start : Start.AddTicks(-10);
    }

    private sealed class DuplicateEvent(Guid id) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var entry = eventData.Context!.ChangeTracker.Entries<LoginSecurityEvent>().SingleOrDefault(item => item.State == EntityState.Added);
            if (entry is not null) entry.Property(item => item.Id).CurrentValue = id;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class EventBarrier(int count) : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!eventData.Context!.ChangeTracker.Entries<LoginSecurityEvent>().Any(item => item.State == EntityState.Added)) return result;
            if (Interlocked.Increment(ref _arrivals) >= count) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return result;
        }
    }

    private sealed class CompetingRiskWrite(Func<Task> commit, bool failures = false) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<LoginSecurityEvent>()
                .Any(item => item.Entity.Kind == (failures ? LoginEventKind.InvalidCredential : LoginEventKind.Completed)))
                await commit();
            return result;
        }
    }

    private sealed class RiskCreationBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!eventData.Context!.ChangeTracker.Entries<LoginAccountBudget>().Any(item => item.State == EntityState.Added))
                return result;
            if (Interlocked.Increment(ref _arrivals) >= 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return result;
        }
    }

    private sealed class OrderedCommit(bool first, TaskCompletionSource staged, TaskCompletionSource committed) : SaveChangesInterceptor
    {
        private bool _participated;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_participated) return result;
            _participated = true;
            if (first) await staged.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            else
            {
                staged.TrySetResult();
                await committed.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (first) committed.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancelEventSave(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<LoginSecurityEvent>().Any(item => item.State == EntityState.Added))
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return ValueTask.FromResult(result);
        }
    }
}
