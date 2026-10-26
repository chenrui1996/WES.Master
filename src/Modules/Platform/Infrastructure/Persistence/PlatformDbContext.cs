using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Wes.Platform.Domain;
using Wes.Platform.Features.Identity;

namespace Wes.Platform.Infrastructure.Persistence;

/// <summary>Platform-owned persistence only. Every operational instance requires an explicit trusted tenant.</summary>
public sealed partial class PlatformDbContext : DbContext
{
    private readonly Dictionary<PlatformInboxReceipt, Guid> _inboxRetentionDeletes = [];

    /// <summary>Rejects absent tenant identity before any model, query, or database connection is used.</summary>
    public PlatformDbContext(DbContextOptions<PlatformDbContext> options, Guid tenantId) : base(options)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Platform persistence requires a trusted tenant.");
        }

        TenantId = tenantId;
    }

    /// <summary>Trusted tenant scope, not a client-controlled filter or implicit shared owner.</summary>
    public Guid TenantId { get; }

    /// <summary>Refresh families belonging to the current tenant.</summary>
    public DbSet<Session> Sessions => Set<Session>();
    /// <summary>Tenant-owned named roles; menus are never treated as grants.</summary>
    public DbSet<AccessRole> Roles => Set<AccessRole>();
    /// <summary>Explicit tenant-composite assignments.</summary>
    public DbSet<RoleMembership> Memberships => Set<RoleMembership>();
    /// <summary>Only reviewed, typed nonsecret settings.</summary>
    public DbSet<VersionedSetting> Settings => Set<VersionedSetting>();
    /// <summary>Append-only owner audit facts, never raw request data.</summary>
    public DbSet<PlatformAudit> Audit => Set<PlatformAudit>();
    /// <summary>Short-lived owner-local ordered membership for audit verification queries.</summary>
    public DbSet<PlatformAuditQuerySnapshot> AuditQuerySnapshots => Set<PlatformAuditQuerySnapshot>();
    /// <summary>Platform-owned durable events staged in the same transaction as their source facts.</summary>
    public DbSet<PlatformOutboxMessage> OutboxMessages => Set<PlatformOutboxMessage>();
    /// <summary>Platform-owned idempotency receipts committed atomically with consumer effects.</summary>
    public DbSet<PlatformInboxReceipt> InboxReceipts => Set<PlatformInboxReceipt>();
    /// <summary>Immutable operator evidence committed atomically with each authorized Outbox replay.</summary>
    public DbSet<PlatformOutboxReplayReceipt> OutboxReplayReceipts => Set<PlatformOutboxReplayReceipt>();
    /// <summary>Tenant-account risk independent of user existence, never global source budgets.</summary>
    public DbSet<LoginAccountBudget> LoginAccounts => Set<LoginAccountBudget>();
    /// <summary>One-shot tenant challenges; never raw answers or global risk state.</summary>
    public DbSet<LoginChallenge> LoginChallenges => Set<LoginChallenge>();
    /// <summary>Owner-local password history; never a user-list or public response source.</summary>
    public DbSet<PasswordCredential> PasswordCredentials => Set<PasswordCredential>();
    /// <summary>Append-only finite login security evidence, including unknown-account attempts.</summary>
    public DbSet<LoginSecurityEvent> LoginEvents => Set<LoginSecurityEvent>();

    internal void StageInboxReceiptRetentionRemoval(
        IReadOnlyCollection<PlatformInboxReceipt> receipts,
        DateTimeOffset cutoff)
    {
        var transaction = Database.CurrentTransaction
            ?? throw new InvalidOperationException("Inbox retention requires an explicit owner transaction.");
        if (receipts.Count > 200)
            throw new ArgumentOutOfRangeException(nameof(receipts), "Inbox retention batches cannot exceed 200 receipts.");
        foreach (var receipt in receipts)
        {
            if (receipt.TenantId != TenantId || receipt.ProcessedAt > cutoff
                || Entry(receipt).State != EntityState.Unchanged)
            {
                throw new InvalidOperationException("Only tracked, expired Inbox receipts from this owner can be retained out.");
            }
        }

        foreach (var receipt in receipts)
            _inboxRetentionDeletes[receipt] = transaction.TransactionId;
        InboxReceipts.RemoveRange(receipts);
    }

    /// <summary>Maps only Platform-owned tables with UTC instants, tenant keys and explicit concurrency.</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureDirectory(modelBuilder);
        ConfigureFiles(modelBuilder);
        ConfigureAuditExports(modelBuilder);
        var utc = new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        var password = modelBuilder.Entity<PasswordCredential>();
        password.ToTable("platform_password_credentials");
        password.HasKey(item => new { item.TenantId, item.UserId });
        password.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        password.Property(item => item.UserId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        password.Property(item => item.NormalizedAccount).HasColumnType("longtext").IsRequired();
        password.Property(item => item.AccountDigest).HasColumnType("varchar(64)").HasMaxLength(64);
        password.HasIndex(item => new { item.TenantId, item.AccountDigest }).IsUnique();
        password.Property(item => item.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        password.Property(item => item.ChangedAt).HasConversion(utc).HasColumnType("datetime(6)");
        password.Property(item => item.Enabled).HasDefaultValue(false);
        password.Property<string>("HistoryData").HasColumnType("varchar(2048)").HasMaxLength(2048).IsRequired();
        password.Ignore(item => item.Recent);
        password.HasQueryFilter(item => item.TenantId == TenantId);

        var loginEvent = modelBuilder.Entity<LoginSecurityEvent>();
        loginEvent.ToTable("platform_login_events");
        loginEvent.HasKey(item => new { item.TenantId, item.Id });
        loginEvent.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        loginEvent.Property(item => item.Id).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        loginEvent.Property(item => item.CorrelationId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        loginEvent.Property(item => item.SessionId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        loginEvent.Property(item => item.AccountDigest).HasColumnType("varchar(64)").HasMaxLength(64);
        loginEvent.Property(item => item.SourceDigest).HasColumnType("varchar(64)").HasMaxLength(64);
        loginEvent.Property(item => item.Kind).HasConversion<string>().HasColumnType("varchar(32)").HasMaxLength(32);
        loginEvent.Property(item => item.OccurredAt).HasConversion(utc).HasColumnType("datetime(6)");
        loginEvent.HasIndex(item => new { item.TenantId, item.OccurredAt, item.Id });
        loginEvent.HasQueryFilter(item => item.TenantId == TenantId);

        var challenge = modelBuilder.Entity<LoginChallenge>();
        challenge.ToTable("platform_login_challenges");
        challenge.HasKey(item => new { item.TenantId, item.Id });
        challenge.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        challenge.Property(item => item.Id).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        challenge.Property(item => item.AccountDigest).HasColumnType("varchar(64)").HasMaxLength(64);
        challenge.Property(item => item.SourceDigest).HasColumnType("varchar(64)").HasMaxLength(64);
        challenge.Property(item => item.AnswerMac).HasColumnType("varchar(64)").HasMaxLength(64);
        challenge.Property(item => item.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        challenge.Property(item => item.CreatedAt).HasConversion(utc).HasColumnType("datetime(6)");
        challenge.Property(item => item.ConsumedAt).HasConversion(utc).HasColumnType("datetime(6)");
        challenge.Ignore(item => item.ExpiresAt);
        challenge.HasQueryFilter(item => item.TenantId == TenantId);

        var account = modelBuilder.Entity<LoginAccountBudget>();
        account.ToTable("platform_login_accounts");
        account.HasKey(item => new { item.TenantId, item.AccountDigest });
        account.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        account.Property(item => item.AccountDigest).HasColumnType("varchar(64)").HasMaxLength(64);
        account.Property(item => item.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        account.Property(item => item.LastFailureAt).HasConversion(utc).HasColumnType("datetime(6)");
        account.Property(item => item.LockedUntil).HasConversion(utc).HasColumnType("datetime(6)");
        account.Property<string>("FailuresData").HasColumnType("varchar(1024)").HasMaxLength(1024).IsRequired();
        account.Ignore(item => item.Failures);
        account.HasQueryFilter(item => item.TenantId == TenantId);

        var session = modelBuilder.Entity<Session>();
        session.ToTable("platform_sessions");
        session.HasKey(item => new { item.TenantId, item.Id });
        session.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        session.Property(item => item.Id).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        session.Property(item => item.UserId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        session.Property(item => item.Client).HasConversion<string>().HasColumnType("varchar(8)").HasMaxLength(8);
        session.Property(item => item.RequiresPasswordChange).HasDefaultValue(true);
        session.Property(item => item.CreatedAt).HasConversion(utc).HasColumnType("datetime(6)");
        session.Property(item => item.LastActivityAt).HasConversion(utc).HasColumnType("datetime(6)");
        session.Property(item => item.RevokedAt).HasConversion(utc).HasColumnType("datetime(6)");
        session.Property(item => item.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        session.Ignore(item => item.ExpiresAt);
        session.HasQueryFilter(item => item.TenantId == TenantId);
        session.HasMany(item => item.Credentials).WithOne()
            .HasForeignKey(item => new { item.TenantId, item.SessionId }).OnDelete(DeleteBehavior.Cascade);
        session.Navigation(item => item.Credentials).HasField("_credentials").UsePropertyAccessMode(PropertyAccessMode.Field);

        var credential = modelBuilder.Entity<RefreshCredential>();
        credential.ToTable("platform_refresh_tokens");
        credential.HasKey(item => new { item.TenantId, item.Digest });
        credential.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        credential.Property(item => item.SessionId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        credential.Property(item => item.Digest).HasColumnType("varchar(64)").HasMaxLength(64);
        credential.Property(item => item.ConsumedAt).HasConversion(utc).HasColumnType("datetime(6)");
        credential.HasQueryFilter(item => item.TenantId == TenantId);

        var role = modelBuilder.Entity<AccessRole>();
        role.ToTable("platform_roles");
        role.HasKey(item => new { item.TenantId, item.Id });
        role.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        role.Property(item => item.Id).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        role.Property(item => item.Code).HasColumnType("varchar(64)").HasMaxLength(64);
        role.HasIndex(item => new { item.TenantId, item.Code }).IsUnique();
        role.Property(item => item.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        role.Property<string>("RulesData").HasColumnType("longtext").IsRequired();
        role.Ignore(item => item.Rules);
        role.HasQueryFilter(item => item.TenantId == TenantId);

        var member = modelBuilder.Entity<RoleMembership>();
        member.ToTable("platform_role_memberships");
        member.HasKey(item => new { item.TenantId, item.UserId, item.RoleId });
        member.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        member.Property(item => item.UserId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        member.Property(item => item.RoleId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        member.HasOne<AccessRole>().WithMany().HasForeignKey(item => new { item.TenantId, Id = item.RoleId })
            .OnDelete(DeleteBehavior.Restrict);
        member.HasQueryFilter(item => item.TenantId == TenantId);

        var setting = modelBuilder.Entity<VersionedSetting>();
        setting.ToTable("platform_settings");
        setting.HasKey(item => new { item.TenantId, item.Id });
        setting.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        setting.Property(item => item.Id).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        setting.Property(item => item.Key).HasColumnType("varchar(128)").HasMaxLength(128);
        setting.Property(item => item.Value).HasColumnType("varchar(64)").HasMaxLength(64);
        setting.Property(item => item.Source).HasColumnType("varchar(32)").HasMaxLength(32);
        setting.Property(item => item.Version).HasColumnType("varchar(36)").HasMaxLength(36).IsConcurrencyToken();
        setting.HasIndex(item => new { item.TenantId, item.Key }).IsUnique();
        setting.HasQueryFilter(item => item.TenantId == TenantId);

        var audit = modelBuilder.Entity<PlatformAudit>();
        audit.ToTable("platform_audit");
        audit.HasKey(item => new { item.TenantId, item.Id });
        audit.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        audit.Property(item => item.Id).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        audit.Property(item => item.ActorId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        audit.Property(item => item.TargetId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        audit.Property(item => item.CorrelationId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        audit.Property(item => item.Action).HasConversion<string>().HasColumnType("varchar(32)").HasMaxLength(32);
        audit.Property(item => item.BeforeVersion).HasColumnType("varchar(36)").HasMaxLength(36);
        audit.Property(item => item.AfterVersion).HasColumnType("varchar(36)").HasMaxLength(36);
        audit.Property(item => item.OccurredAt).HasConversion(utc).HasColumnType("datetime(6)");
        audit.HasIndex(item => new { item.TenantId, item.OccurredAt, item.Id });
        audit.HasQueryFilter(item => item.TenantId == TenantId);

        var auditSnapshot = modelBuilder.Entity<PlatformAuditQuerySnapshot>();
        auditSnapshot.ToTable("platform_audit_query_snapshots");
        auditSnapshot.HasKey(item => new { item.TenantId, item.Id });
        auditSnapshot.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        auditSnapshot.Property(item => item.Id).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        auditSnapshot.Property(item => item.ActorId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        auditSnapshot.Property(item => item.SessionId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        auditSnapshot.Property(item => item.AuthorizationFingerprint).HasColumnType("char(64)").HasMaxLength(64);
        auditSnapshot.Property(item => item.FilterFingerprint).HasColumnType("char(64)").HasMaxLength(64);
        auditSnapshot.Property(item => item.SortFingerprint).HasColumnType("char(64)").HasMaxLength(64);
        auditSnapshot.Property(item => item.From).HasConversion(utc).HasColumnType("datetime(6)");
        auditSnapshot.Property(item => item.To).HasConversion(utc).HasColumnType("datetime(6)");
        auditSnapshot.Property(item => item.FilterAction).HasConversion<string>().HasColumnType("varchar(32)").HasMaxLength(32);
        auditSnapshot.Property(item => item.CorrelationId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        auditSnapshot.Property(item => item.OrderedAuditIds).HasColumnType("longtext").IsRequired();
        auditSnapshot.Property(item => item.CreatedAt).HasConversion(utc).HasColumnType("datetime(6)");
        auditSnapshot.Property(item => item.ExpiresAt).HasConversion(utc).HasColumnType("datetime(6)");
        auditSnapshot.HasIndex(item => new { item.TenantId, item.ActorId, item.SessionId, item.ExpiresAt });
        auditSnapshot.HasQueryFilter(item => item.TenantId == TenantId);

        var outbox = modelBuilder.Entity<PlatformOutboxMessage>();
        outbox.ToTable("platform_outbox_messages");
        outbox.HasKey(item => new { item.TenantId, item.EventId });
        outbox.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        outbox.Property(item => item.EventId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        outbox.Property(item => item.ContractName).HasColumnType("varchar(128)").HasMaxLength(128);
        outbox.Property(item => item.SchemaVersion).IsRequired();
        outbox.Property(item => item.Owner).HasColumnType("varchar(32)").HasMaxLength(32);
        outbox.Property(item => item.AggregateId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        outbox.Property(item => item.AggregateVersion).HasColumnType("varchar(36)").HasMaxLength(36);
        outbox.Property(item => item.CorrelationId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        outbox.Property(item => item.CausationId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        outbox.Property(item => item.OccurredAt).HasConversion(utc).HasColumnType("datetime(6)");
        outbox.Property(item => item.Payload).HasColumnType("longtext").IsRequired();
        outbox.Property(item => item.Status).HasConversion<string>().HasColumnType("varchar(16)").HasMaxLength(16)
            .IsConcurrencyToken();
        outbox.Property(item => item.FirstFailureAt).HasConversion(utc).HasColumnType("datetime(6)");
        outbox.Property(item => item.LeaseId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36)
            .IsConcurrencyToken();
        outbox.Property(item => item.LeaseUntil).HasConversion(utc).HasColumnType("datetime(6)");
        outbox.Property(item => item.NextAttemptAt).HasConversion(utc).HasColumnType("datetime(6)");
        outbox.Property(item => item.PublishedAt).HasConversion(utc).HasColumnType("datetime(6)");
        outbox.Property(item => item.LastErrorCategory).HasColumnType("varchar(32)").HasMaxLength(32);
        outbox.Property(item => item.CreatedAt).HasConversion(utc).HasColumnType("datetime(6)");
        outbox.HasIndex(item => new { item.TenantId, item.Status, item.NextAttemptAt, item.EventId });
        outbox.HasIndex(item => new { item.TenantId, item.Status, item.LeaseUntil, item.EventId });
        outbox.HasIndex(item => new { item.TenantId, item.CreatedAt, item.EventId });
        outbox.HasQueryFilter(item => item.TenantId == TenantId);

        var inbox = modelBuilder.Entity<PlatformInboxReceipt>();
        inbox.ToTable("platform_inbox_receipts");
        inbox.HasKey(item => new { item.TenantId, item.Consumer, item.EventId });
        inbox.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        inbox.Property(item => item.Consumer).HasColumnType("varchar(128)").HasMaxLength(128);
        inbox.Property(item => item.EventId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        inbox.Property(item => item.ContractName).HasColumnType("varchar(128)").HasMaxLength(128);
        inbox.Property(item => item.SchemaVersion).IsRequired();
        inbox.Property(item => item.EnvelopeDigest).HasColumnType("char(64)").HasMaxLength(64);
        inbox.Property(item => item.ProcessedAt).HasConversion(utc).HasColumnType("datetime(6)");
        inbox.HasIndex(item => new { item.TenantId, item.ProcessedAt, item.Consumer });
        inbox.HasQueryFilter(item => item.TenantId == TenantId);

        var replay = modelBuilder.Entity<PlatformOutboxReplayReceipt>();
        replay.ToTable("platform_outbox_replay_receipts");
        replay.HasKey(item => new { item.TenantId, item.OperationId });
        replay.Property(item => item.TenantId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        replay.Property(item => item.OperationId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        replay.Property(item => item.EventId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        replay.Property(item => item.ActorId).HasConversion<string>().HasColumnType("varchar(36)").HasMaxLength(36);
        replay.Property(item => item.Reason).HasColumnType("varchar(256)").HasMaxLength(256).IsRequired();
        replay.Property(item => item.PreviousAttempts).IsRequired();
        replay.Property(item => item.Fingerprint).HasColumnType("char(64)").HasMaxLength(64).IsRequired();
        replay.Property(item => item.RequestedAt).HasConversion(utc).HasColumnType("datetime(6)");
        replay.HasIndex(item => new { item.TenantId, item.EventId, item.RequestedAt });
        replay.HasQueryFilter(item => item.TenantId == TenantId);
    }

    /// <summary>Protects every synchronous write path from cross-tenant attachment or owner changes.</summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StageOutboxMessages();
        ValidateWriteScope();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        _inboxRetentionDeletes.Clear();
        return result;
    }

    /// <summary>Protects asynchronous writes and propagates cancellation into the owner transaction.</summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StageOutboxMessages();
        ValidateWriteScope();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        _inboxRetentionDeletes.Clear();
        return result;
    }

    private void StageOutboxMessages()
    {
        var stagedIds = ChangeTracker.Entries<PlatformOutboxMessage>()
            .Select(entry => entry.Entity.EventId)
            .ToHashSet();
        foreach (var entry in ChangeTracker.Entries<PlatformAudit>()
            .Where(entry => entry.State == EntityState.Added)
            .ToArray())
        {
            var message = PlatformOutboxMessage.FromAudit(entry.Entity);
            if (message is not null && stagedIds.Add(message.EventId))
                OutboxMessages.Add(message);
        }
    }

    private void ValidateWriteScope()
    {
        ValidateAuditExportWrites();
        foreach (var entry in ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.Entity is QuarantinedFile && entry.State == EntityState.Deleted)
                throw new InvalidOperationException("File identities must be retained as deletion tombstones.");
            if (entry.Entity is QuarantinedFile && entry.State == EntityState.Modified && entry.Properties.Any(property =>
                property.IsModified && property.Metadata.Name is nameof(QuarantinedFile.UploadOperationId)
                    or nameof(QuarantinedFile.CreatorId) or nameof(QuarantinedFile.CorrelationId)
                    or nameof(QuarantinedFile.FileName) or nameof(QuarantinedFile.ContentType)
                    or nameof(QuarantinedFile.ByteCount) or nameof(QuarantinedFile.Sha256) or nameof(QuarantinedFile.CreatedAt)))
                throw new InvalidOperationException("Original file content metadata and upload identities are immutable.");
            if (entry.Entity is PlatformInboxReceipt inboxReceipt
                && entry.State != EntityState.Added
                && (entry.State != EntityState.Deleted
                    || !_inboxRetentionDeletes.TryGetValue(inboxReceipt, out var transactionId)
                    || Database.CurrentTransaction?.TransactionId != transactionId))
            {
                throw new InvalidOperationException("Platform message receipts are append-only outside bounded Inbox retention.");
            }

            if (entry.Entity is PlatformOutboxReplayReceipt or PlatformAuditExportReceipt
                && entry.State != EntityState.Added)
            {
                throw new InvalidOperationException("Platform message receipts are append-only.");
            }

            if (entry.Entity is PlatformAudit or LoginSecurityEvent && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException("Platform audit facts are append-only.");
            }
            var tenant = entry.Property("TenantId");
            if (tenant.CurrentValue is not Guid current || current != TenantId
                || (entry.State != EntityState.Added && (tenant.OriginalValue is not Guid original || original != TenantId)))
            {
                throw new InvalidOperationException("Platform write exceeds the trusted tenant scope.");
            }
        }
    }
}
