using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using Wes.Platform.Domain;
using Wes.Platform.Infrastructure.Persistence;

namespace Wes.Platform.Features.Identity;

/// <summary>Internal committed session delivery, not an HTTP response model and never serialized with its secret.</summary>
public sealed class LoginSessionDelivery(Guid userId, Guid sessionId, bool requiresPasswordChange, RefreshSecret secret)
{
    /// <summary>Committed subject reference.</summary>
    public Guid UserId { get; } = userId;
    /// <summary>Committed family reference.</summary>
    public Guid SessionId { get; } = sessionId;
    /// <summary>Persistent legacy restriction, also enforced by current role authorization.</summary>
    public bool RequiresPasswordChange { get; } = requiresPasswordChange;
    /// <summary>Server-to-protected-transport only; no default response serialization.</summary>
    [JsonIgnore]
    public RefreshSecret Secret { get; } = secret;
    /// <summary>Prevents incidental delivery formatting.</summary>
    public override string ToString() => "LoginSessionDelivery[REDACTED]";
}

/// <summary>One generic denial shape; only a committed success contains a server-side delivery.</summary>
public sealed record LoginAttemptResult
{
    /// <summary>Non-null only after a durable account/credential/session commit.</summary>
    [JsonIgnore]
    public LoginSessionDelivery? Delivery { get; init; }
    /// <summary>Indicates internal committed success, never a credential policy proxy.</summary>
    public bool Completed => Delivery is not null;
}

/// <summary>Coordinates independent source/challenge consumption with the tenant's atomic credential/risk/session commit.</summary>
public sealed class LoginAttemptHandler(PlatformDbContext database, LoginSourceDbContext sources,
    LoginChallengeProtection protection, TimeProvider clock, ILoggerFactory logs)
{
    private static readonly StoredPasswordHash Dummy = StoredPasswordHash.Import(PasswordHashScheme.BcryptSha384V1,
        "$2a$12$N9qo8uLOickgx2ZMRZoMyebq.QrfAcQ2j7P1iiG.cM44tPZp1iuQS");

    /// <summary>Accepts owner-normalized account and trusted connection source, never preverified booleans or a target user ID.</summary>
    public async Task<LoginAttemptResult> HandleAsync(string normalizedAccount, string password, LoginSourceKey source,
        SessionClient client, Guid correlation, Guid? challengeId = null, string? answer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(client) || correlation == Guid.Empty)
            throw new ArgumentException("Login requires a supported client and valid correlation.");
        var account = LoginAccountKey.FromNormalizedAccount(normalizedAccount);
        if (database.ChangeTracker.Entries().Any() || sources.ChangeTracker.Entries().Any()
            || database.Database.CurrentTransaction is not null || sources.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null
            || database.Database.AutoTransactionBehavior == AutoTransactionBehavior.Never)
            throw new InvalidOperationException("Login requires clean independent owner units of work.");
        var began = Now();
        try
        {
            var admission = await new AdmitLoginSourceHandler(sources, clock, logs.CreateLogger<AdmitLoginSourceHandler>())
                .HandleAsync(source, cancellationToken);
            if (admission != SourceAdmission.Admitted)
                return await RejectAsync(LoginEventKind.SourceLimited, account, source, correlation, began, cancellationToken);
            await EnsureRiskAsync(account, cancellationToken);
            var challenged = false;
            if (challengeId is not null)
            {
                challenged = await new LoginChallengeHandler(database, clock, protection, logs.CreateLogger<LoginChallengeHandler>())
                    .ConsumeAsync(challengeId.Value, account, source, answer, cancellationToken) == ChallengeDecision.Accepted;
                if (!challenged)
                    return await RejectAsync(LoginEventKind.ChallengeRejected, account, source, correlation, began, cancellationToken);
            }
            var budget = await database.LoginAccounts.SingleAsync(item => item.AccountDigest == account.Digest, cancellationToken);
            var checkedAt = Now(began);
            var gate = budget.Evaluate(checkedAt, challenged);
            if (gate != LoginGate.VerifyPassword)
                return await RejectAsync(gate == LoginGate.Locked ? LoginEventKind.Locked : LoginEventKind.ChallengeRequired,
                    account, source, correlation, checkedAt, cancellationToken);
            var credential = await database.PasswordCredentials.SingleOrDefaultAsync(
                item => item.AccountDigest == account.Digest, cancellationToken);
            var verification = credential is null
                ? PasswordCredentialCodec.Verify(password, Dummy) : credential.Verify(password);
            if (credential is null || !credential.Enabled || verification == CredentialVerification.Invalid
                || !await new Wes.Platform.Features.Directory.DirectoryEligibility(database)
                    .IsEligibleAsync(credential.UserId, cancellationToken))
            {
                database.ChangeTracker.Clear();
                return await RecordFailureAsync(account, source, correlation, checkedAt, cancellationToken);
            }
            var now = Now(checkedAt);
            gate = budget.FenceSuccess(now, challenged);
            if (gate != LoginGate.VerifyPassword)
                return await RejectAsync(gate == LoginGate.Locked ? LoginEventKind.Locked : LoginEventKind.ChallengeRequired,
                    account, source, correlation, now, cancellationToken);
            credential.FenceSession(now);
            var restricted = verification == CredentialVerification.LegacyVerifiedRestricted;
            var secret = RefreshSecret.Generate();
            var session = new Session(database.TenantId, credential.UserId, client, now, secret.Digest, restricted);
            database.Sessions.Add(session);
            database.LoginEvents.Add(new LoginSecurityEvent(database.TenantId, account.Digest, source.Digest, correlation,
                restricted ? LoginEventKind.LegacyRestricted : LoginEventKind.Completed, now, session.Id));
            try
            {
                await CommitAsync(cancellationToken);
                return new LoginAttemptResult { Delivery = new LoginSessionDelivery(credential.UserId, session.Id, restricted, secret) };
            }
            catch (DbUpdateConcurrencyException)
            {
                return await RejectAsync(LoginEventKind.VersionConflict, account, source, correlation, now, cancellationToken);
            }
        }
        finally
        {
            database.ChangeTracker.Clear();
        }
    }

    private async Task EnsureRiskAsync(LoginAccountKey account, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (await database.LoginAccounts.AsNoTracking().AnyAsync(item => item.AccountDigest == account.Digest, cancellationToken)) return;
            var budget = new LoginAccountBudget(database.TenantId, account);
            database.LoginAccounts.Add(budget);
            try
            {
                await CommitAsync(cancellationToken);
                database.ChangeTracker.Clear();
                return;
            }
            catch (DbUpdateException error) when (error.InnerException is MySqlException { Number: 1062 }
                && error.Entries.Count == 1 && ReferenceEquals(error.Entries[0].Entity, budget))
            {
                logs.CreateLogger<LoginAttemptHandler>().LogWarning("Login contention {Code}", "LoginRiskCreationConflict");
                database.ChangeTracker.Clear();
            }
        }
        throw new InvalidOperationException("Login could not initialize its current account budget.");
    }

    private async Task<LoginAttemptResult> RecordFailureAsync(LoginAccountKey account, LoginSourceKey source,
        Guid correlation, DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var budget = await database.LoginAccounts.SingleAsync(item => item.AccountDigest == account.Digest, cancellationToken);
            var now = Now(checkedAt);
            budget.RecordAdmittedFailure(now);
            database.LoginEvents.Add(new LoginSecurityEvent(database.TenantId, account.Digest, source.Digest,
                correlation, LoginEventKind.InvalidCredential, now));
            try
            {
                await CommitAsync(cancellationToken);
                logs.CreateLogger<LoginAttemptHandler>().LogWarning("Login rejected {Code}", "LoginInvalidCredential");
                return new LoginAttemptResult();
            }
            catch (DbUpdateConcurrencyException)
            {
                logs.CreateLogger<LoginAttemptHandler>().LogWarning("Login contention {Code}", "LoginFailureVersionConflict");
                database.ChangeTracker.Clear();
            }
        }
        logs.CreateLogger<LoginAttemptHandler>().LogError("Login failed {Code}", "LoginFailureContentionExhausted");
        throw new InvalidOperationException("Login could not commit its verified failure observation.");
    }

    private async Task<LoginAttemptResult> RejectAsync(LoginEventKind kind, LoginAccountKey account,
        LoginSourceKey source, Guid correlation, DateTimeOffset previous, CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        database.LoginEvents.Add(new LoginSecurityEvent(database.TenantId, account.Digest, source.Digest,
            correlation, kind, Now(previous)));
        await database.SaveChangesAsync(cancellationToken);
        logs.CreateLogger<LoginAttemptHandler>().LogWarning("Login rejected {Code}", kind);
        return new LoginAttemptResult();
    }

    private DateTimeOffset Now(DateTimeOffset? previous = null)
    {
        var instant = clock.GetUtcNow();
        var now = new DateTimeOffset(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero);
        if (previous > now) throw new InvalidOperationException("Login time cannot move backwards.");
        return now;
    }

    private async Task CommitAsync(CancellationToken cancellationToken)
    {
        // The Oracle provider's implicit transaction inference is not an atomicity contract for mixed risk/event batches.
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
