using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Wes.Platform.Domain;
using Wes.Platform.Features.Directory;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>RCPT-01..07: isolated real-MySQL directory receipt behavior, never production data.</summary>
[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed class DirectoryReceiptTests : IAsyncLifetime
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otherTenant = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _operation = Guid.NewGuid();
    private readonly Clock _clock = new();
    private string _connection = "";
    private bool _migrated;
    private Guid _department;
    private Guid _otherDepartment;
    private TenantContext _context = null!;

    /// <summary>Applies reviewed owner migrations only to the authorized wes_test database.</summary>
    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var db = Open();
        await db.Database.MigrateAsync();
        _migrated = true;
        var department = new DirectoryDepartment(_tenant, "owner", "Owner");
        var other = new DirectoryDepartment(_tenant, "other", "Other");
        _department = department.Id;
        _otherDepartment = other.Id;
        var session = new Session(_tenant, _actor, SessionClient.Web, _clock.Now, new string('A', 64));
        _context = new(_tenant, _actor, session.Id);
        var role = new AccessRole(_tenant, "owner",
        [
            Grant("platform.users.write", "Account", "Name", "DepartmentId", "Enabled", "Roles", "Credential"),
            Grant("platform.departments.write", "Code", "Name", "Enabled"),
            Grant("platform.dictionary.write", "Category", "Code", "LanguageKey", "Label", "References", "Delete"),
            Grant("platform.roles.write", "Code", "Rules", "Enabled"),
            Grant("platform.tenants.write", "Name", "Enabled", "DefaultLanguage")
        ]);
        db.DirectoryTenants.Add(new DirectoryTenant(_tenant, "Receipt tenant"));
        db.Departments.AddRange(department, other);
        db.DirectoryUsers.Add(new DirectoryUser(_tenant, _actor, "OWNER", "Owner", department.Id));
        db.Sessions.Add(session);
        db.Roles.Add(role);
        db.Memberships.Add(new RoleMembership(_tenant, _actor, role.Id));
        await db.SaveChangesAsync();
    }

    /// <summary>RCPT-01: restart replay retains the first result, transition and audit exactly once.</summary>
    [Fact]
    public async Task CreateUser_RestartReplay_ReturnsOriginalWithOneEffect()
    {
        var first = await CreateAsync();
        var replay = await CreateAsync();
        Assert.Equal(DirectoryDecision.Changed, first.Decision);
        Assert.Equal(first, replay);
        await using var db = Open();
        Assert.Equal(2, await db.DirectoryUsers.CountAsync());
        Assert.Single(await db.Audit.ToListAsync());
        var receipt = Assert.Single(await db.CommandReceipts.ToListAsync());
        Assert.Equal(first.Id, receipt.ResultId);
        Assert.Equal(first.Version, receipt.ResultVersion);
        Assert.Equal(_actor, receipt.ActorId);
        Assert.Equal(_operation, receipt.OperationId);
        Assert.Equal(64, receipt.Fingerprint.Length);
    }

    /// <summary>RCPT-02: an operation identity is not a last-writer-wins slot.</summary>
    [Fact]
    public async Task SameKey_DifferentPayloadOrCommand_ExplicitConflict()
    {
        await CreateAsync();
        Assert.Equal(DirectoryDecision.IdempotencyConflict, (await CreateAsync("different")).Decision);
        await using var db = Open();
        var mismatch = await Service(db).CreateDepartmentAsync(_context, "new", "New", Guid.NewGuid());
        Assert.Equal(DirectoryDecision.IdempotencyConflict, mismatch.Decision);
        Assert.Null(mismatch.Id);
        Assert.Single(await db.Audit.ToListAsync());
    }

    /// <summary>RCPT-03: original department permission must not disclose a target that has since moved.</summary>
    [Fact]
    public async Task CreateUser_ReplayAfterTargetMove_DeniesCachedIdentity()
    {
        var first = await CreateAsync();
        await using (var db = Open())
        {
            var user = await db.DirectoryUsers.SingleAsync(x => x.Id == first.Id);
            user.Change(user.Version, user.Name, _otherDepartment, true);
            var role = await db.Roles.SingleAsync();
            role.Change(role.Version, true,
            [
                new PermissionGrant("platform.users.write", DataScope.Departments,
                    ImmutableHashSet.Create(_department),
                    ImmutableHashSet.Create("Account", "Name", "DepartmentId", "Enabled"))
            ]);
            await db.SaveChangesAsync();
        }
        var replay = await CreateAsync();
        Assert.Equal(DirectoryDecision.Forbidden, replay.Decision);
        Assert.Null(replay.Id);
        Assert.Null(replay.Version);
    }

    /// <summary>RCPT-03: a durable receipt never replaces live session authorization.</summary>
    [Fact]
    public async Task Replay_AfterSessionRevocation_Denies()
    {
        await CreateAsync();
        await using (var db = Open())
        {
            (await db.Sessions.SingleAsync()).Revoke(_clock.Now);
            await db.SaveChangesAsync();
        }
        Assert.Equal(DirectoryDecision.Forbidden, (await CreateAsync()).Decision);
    }

    /// <summary>RCPT-04: independent owner contexts compete for the same durable key.</summary>
    [Fact]
    public async Task SameKey_ConcurrentRequests_OneEffectAndOriginalResult()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => CreateAsync()));
        Assert.All(results, x => Assert.Equal(DirectoryDecision.Changed, x.Decision));
        Assert.All(results, x => Assert.Equal(results[0], x));
        await using var db = Open();
        Assert.Equal(2, await db.DirectoryUsers.CountAsync());
        Assert.Single(await db.CommandReceipts.ToListAsync());
        Assert.Single(await db.Audit.ToListAsync());
    }

    /// <summary>RCPT-05: fault after SQL writes must roll back receipt, entity and audit together.</summary>
    [Fact]
    public async Task ProviderFaultAfterSave_RollsBackAllAndAllowsRetry()
    {
        await using (var db = Open(new FailAfterSave()))
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Service(db).CreateUserAsync(_context, "TARGET", "Target", _department, Guid.NewGuid()));
        await using (var db = Open())
        {
            Assert.Single(await db.DirectoryUsers.ToListAsync());
            Assert.Empty(await db.Audit.ToListAsync());
            Assert.Empty(await db.CommandReceipts.ToListAsync());
        }
        Assert.Equal(DirectoryDecision.Changed, (await CreateAsync()).Decision);
    }

    /// <summary>RCPT-05: cancellation before commit cannot consume a key or commit a transition.</summary>
    [Fact]
    public async Task CancellationAfterSave_RollsBackAll()
    {
        using var cancellation = new CancellationTokenSource();
        await using (var db = Open(new CancelAfterSave(cancellation)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Service(db).CreateUserAsync(_context, "TARGET", "Target", _department, Guid.NewGuid(), cancellation.Token));
        await using var verify = Open();
        Assert.Single(await verify.DirectoryUsers.ToListAsync());
        Assert.Empty(await verify.Audit.ToListAsync());
        Assert.Empty(await verify.CommandReceipts.ToListAsync());
    }

    /// <summary>RCPT-06: exact 90-day expiry stops replay but never deletes the stable account key.</summary>
    [Fact]
    public async Task ExpiredReceipt_DoesNotReplayOrDeleteDomainIdentity()
    {
        await CreateAsync();
        _clock.Now += TimeSpan.FromDays(90);
        // Restore current eligibility: receipt expiry is not session lifetime.
        await using (var db = Open())
        {
            var session = new Session(_tenant, _actor, SessionClient.Web, _clock.Now, new string('B', 64));
            db.Sessions.Add(session);
            await db.SaveChangesAsync();
            _context = new(_tenant, _actor, session.Id);
        }
        Assert.Equal(DirectoryDecision.Duplicate, (await CreateAsync()).Decision);
        await using var verify = Open();
        Assert.Equal(2, await verify.DirectoryUsers.CountAsync());
        Assert.Single(await verify.Audit.ToListAsync());
    }

    /// <summary>RCPT-07: credentials cannot use request fingerprints or receipt replay.</summary>
    [Fact]
    public async Task CreateCredential_WithOperationContext_IsRejectedBeforeMutation()
    {
        await using var db = Open();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).CreateCredentialAsync(
            _context, _actor, null, "", PasswordEvidence.Unknown, Guid.NewGuid()));
        Assert.Empty(await db.PasswordCredentials.ToListAsync());
        Assert.Empty(await db.CommandReceipts.ToListAsync());
    }

    /// <summary>RCPT-01: stale expected-version retries return the committed result, not another version transition.</summary>
    [Fact]
    public async Task ChangeUser_ReplayWithOriginalExpectedVersion_OneAuditAndVersion()
    {
        string version;
        await using (var db = Open()) version = (await db.DirectoryUsers.SingleAsync()).Version;
        DirectoryWriteResult first;
        await using (var db = Open())
            first = await Service(db).ChangeUserAsync(_context, _actor, version, "Updated", _department, true, Guid.NewGuid());
        await using (var db = Open())
        {
            var second = await Service(db).ChangeUserAsync(_context, _actor, version, "Updated", _department, true, Guid.NewGuid());
            Assert.Equal(DirectoryDecision.Changed, first.Decision);
            Assert.Equal(first, second);
            Assert.Equal(first.Version, (await db.DirectoryUsers.SingleAsync()).Version);
            Assert.Single(await db.Audit.ToListAsync());
        }
    }

    /// <summary>RCPT-03: the original operation must not bypass newly hidden command fields.</summary>
    [Fact]
    public async Task Replay_AfterFieldRevocation_Denies()
    {
        await CreateAsync();
        await using (var db = Open())
        {
            var role = await db.Roles.SingleAsync();
            role.Change(role.Version, true, [Grant("platform.users.write", "Account", "DepartmentId", "Enabled")]);
            await db.SaveChangesAsync();
        }
        var replay = await CreateAsync();
        Assert.Equal(DirectoryDecision.Forbidden, replay.Decision);
        Assert.Null(replay.Id);
        Assert.Null(replay.Version);
    }

    /// <summary>RCPT-04: operation UUIDs are scoped by trusted actor, not globally tenant-wide.</summary>
    [Fact]
    public async Task SameOperation_DifferentActor_HasIndependentReceipt()
    {
        await CreateAsync();
        var actor = Guid.NewGuid();
        await using (var db = Open())
        {
            db.DirectoryUsers.Add(new DirectoryUser(_tenant, actor, "OTHERACTOR", "Other actor", _department));
            var role = await db.Roles.SingleAsync();
            db.Memberships.Add(new RoleMembership(_tenant, actor, role.Id));
            var session = new Session(_tenant, actor, SessionClient.Web, _clock.Now, new string('C', 64));
            db.Sessions.Add(session);
            await db.SaveChangesAsync();
            _context = new(_tenant, actor, session.Id);
        }
        await using (var db = Open())
            Assert.Equal(DirectoryDecision.Changed, (await Service(db).CreateUserAsync(
                _context, "OTHER-TARGET", "Target", _department, Guid.NewGuid())).Decision);
        await using var verify = Open();
        Assert.Equal(2, await verify.CommandReceipts.CountAsync());
        Assert.Equal(2, await verify.Audit.CountAsync());
    }

    /// <summary>RCPT-04: the same actor and operation UUID in a different trusted tenant are independent.</summary>
    [Fact]
    public async Task SameActorAndOperation_DifferentTenant_IsIsolated()
    {
        var first = await CreateAsync();
        TenantContext context;
        Guid departmentId;
        await using (var db = Open(tenant: _otherTenant))
        {
            var department = new DirectoryDepartment(_otherTenant, "owner", "Owner");
            departmentId = department.Id;
            var session = new Session(_otherTenant, _actor, SessionClient.Web, _clock.Now, new string('F', 64));
            context = new(_otherTenant, _actor, session.Id);
            var role = new AccessRole(_otherTenant, "owner",
                [Grant("platform.users.write", "Account", "Name", "DepartmentId", "Enabled")]);
            db.DirectoryTenants.Add(new DirectoryTenant(_otherTenant, "Other tenant"));
            db.Departments.Add(department);
            db.DirectoryUsers.Add(new DirectoryUser(_otherTenant, _actor, "OWNER", "Owner", department.Id));
            db.Sessions.Add(session);
            db.Roles.Add(role);
            db.Memberships.Add(new RoleMembership(_otherTenant, _actor, role.Id));
            await db.SaveChangesAsync();
        }
        await using (var db = Open(tenant: _otherTenant))
        {
            var result = await Service(db).CreateUserAsync(context, "TARGET", "Target", departmentId, Guid.NewGuid());
            Assert.Equal(DirectoryDecision.Changed, result.Decision);
            Assert.NotEqual(first.Id, result.Id);
            var receipt = Assert.Single(await db.CommandReceipts.ToListAsync());
            Assert.Equal(_otherTenant, receipt.TenantId);
        }
        await using var original = Open();
        Assert.Equal(first.Id, (await original.CommandReceipts.SingleAsync()).ResultId);
    }

    /// <summary>RCPT-03: removing a membership cannot make its old delegated rules disappear from replay authorization.</summary>
    [Fact]
    public async Task MembershipReplay_LostRemovedRoleDelegation_Denies()
    {
        Guid target;
        string version;
        await using (var db = Open())
        {
            var user = new DirectoryUser(_tenant, Guid.NewGuid(), "MEMBER", "Member", _department);
            var removed = new AccessRole(_tenant, "removed", [Grant("platform.users.write", "Account")]);
            target = user.Id;
            version = user.Version;
            db.DirectoryUsers.Add(user);
            db.Roles.Add(removed);
            db.Memberships.Add(new RoleMembership(_tenant, user.Id, removed.Id));
            await db.SaveChangesAsync();
        }
        await using (var db = Open())
            Assert.Equal(DirectoryDecision.Changed, (await Service(db).ChangeMembershipsAsync(
                _context, target, version, [], Guid.NewGuid())).Decision);
        await using (var db = Open())
        {
            var owner = await db.Roles.SingleAsync(x => x.Code == "owner");
            owner.Change(owner.Version, true, [Grant("platform.users.write", "Roles")]);
            await db.SaveChangesAsync();
        }
        await using (var db = Open())
        {
            var replay = await Service(db).ChangeMembershipsAsync(_context, target, version, [], Guid.NewGuid());
            Assert.Equal(DirectoryDecision.Forbidden, replay.Decision);
            Assert.Null(replay.Id);
            Assert.Null(replay.Version);
        }
    }

    /// <summary>RCPT-03: unrelated tenant roles must not deny replay to an unchanged partially delegable administrator.</summary>
    [Fact]
    public async Task MembershipReplay_DoesNotRequireDelegationOverUnrelatedRoles()
    {
        Guid target;
        string version;
        await using (var db = Open())
        {
            var user = new DirectoryUser(_tenant, Guid.NewGuid(), "PARTIAL", "Partial target", _department);
            var removed = new AccessRole(_tenant, "removed", [Grant("platform.users.write", "Account")]);
            var unrelated = new AccessRole(_tenant, "unrelated", [Grant("inventory.restricted.read", "Id")]);
            target = user.Id; version = user.Version;
            db.DirectoryUsers.Add(user); db.Roles.AddRange(removed, unrelated);
            db.Memberships.Add(new RoleMembership(_tenant, user.Id, removed.Id));
            await db.SaveChangesAsync();
        }
        DirectoryWriteResult first;
        await using (var db = Open())
            first = await Service(db).ChangeMembershipsAsync(_context, target, version, [], Guid.NewGuid());
        Assert.Equal(DirectoryDecision.Changed, first.Decision);
        await using (var db = Open())
        {
            var replay = await Service(db).ChangeMembershipsAsync(_context, target, version, [], Guid.NewGuid());
            Assert.Equal(first, replay);
            Assert.Single(await db.Audit.ToListAsync());
        }
    }

    /// <summary>RCPT-06: replacing an expired receipt also replaces, rather than reuses, its minimal delegation evidence.</summary>
    [Fact]
    public async Task MembershipReceiptExpiry_ReplacesItsDelegationEvidenceAndRetentionRemovesBoth()
    {
        Guid target;
        string version;
        await using (var db = Open())
        {
            var user = new DirectoryUser(_tenant, Guid.NewGuid(), "EXPIRY", "Expiry target", _department);
            target = user.Id; version = user.Version;
            db.DirectoryUsers.Add(user); await db.SaveChangesAsync();
        }
        DirectoryWriteResult first;
        await using (var db = Open())
            first = await Service(db).ChangeMembershipsAsync(_context, target, version, [], Guid.NewGuid());
        Assert.Equal(DirectoryDecision.Changed, first.Decision);
        _clock.Now += DirectoryCommandReceipts.Retention;
        await using (var db = Open())
        {
            var session = new Session(_tenant, _actor, SessionClient.Web, _clock.Now, new string('B', 64));
            db.Sessions.Add(session); await db.SaveChangesAsync();
            _context = _context with { SessionId = session.Id };
        }
        await using (var db = Open())
        {
            var next = await Service(db).ChangeMembershipsAsync(_context, target, first.Version, [], Guid.NewGuid());
            Assert.Equal(DirectoryDecision.Changed, next.Decision);
            Assert.NotEqual(first.Version, next.Version);
        }
        await using (var db = Open())
        {
            Assert.Empty((await db.CommandDelegations.SingleAsync()).RoleIds);
            Assert.Equal(_clock.Now, (await db.CommandReceipts.SingleAsync()).CreatedAt);
            Assert.Equal(2, await db.Audit.CountAsync());
            await using var transaction = await db.Database.BeginTransactionAsync();
            Assert.Equal(1, await DirectoryCommandReceipts.RemoveExpiredAsync(db, _clock.Now + DirectoryCommandReceipts.Retention));
            await db.SaveChangesAsync(); await transaction.CommitAsync();
        }
        await using (var db = Open())
        {
            Assert.Empty(await db.CommandDelegations.ToListAsync());
            Assert.Empty(await db.CommandReceipts.ToListAsync());
            Assert.True(await db.DirectoryUsers.AnyAsync(item => item.Id == target));
        }
    }

    /// <summary>RCPT-01: unordered role rules and their fields produce stable canonical fingerprints.</summary>
    [Fact]
    public async Task CreateRole_ReorderedRules_ReplaysOriginal()
    {
        var a = Grant("platform.users.write", "Name", "Account");
        var b = Grant("platform.dictionary.write", "Label");
        DirectoryWriteResult first;
        await using (var db = Open())
            first = await Service(db).CreateRoleAsync(_context, "delegated", [a, b], Guid.NewGuid());
        Assert.Equal(DirectoryDecision.Changed, first.Decision);
        await using (var db = Open())
            Assert.Equal(first, await Service(db).CreateRoleAsync(_context, "delegated",
                [b, Grant("platform.users.write", "Account", "Name")], Guid.NewGuid()));
        await using var verify = Open();
        Assert.Single(await verify.Audit.ToListAsync());
    }

    /// <summary>RCPT-03: replay checks both requested initial rules and the created role's current rules.</summary>
    [Fact]
    public async Task CreateRole_CurrentTargetRulesNoLongerDelegable_Denies()
    {
        var rule = Grant("platform.users.write", "Name");
        DirectoryWriteResult first;
        await using (var db = Open())
            first = await Service(db).CreateRoleAsync(_context, "delegated", [rule], Guid.NewGuid());
        Assert.Equal(DirectoryDecision.Changed, first.Decision);
        await using (var db = Open())
        {
            var target = await db.Roles.SingleAsync(x => x.Id == first.Id);
            target.Change(target.Version, true, [Grant("platform.settings.write", "Value")]);
            await db.SaveChangesAsync();
        }
        await using (var db = Open())
        {
            var replay = await Service(db).CreateRoleAsync(_context, "delegated", [rule], Guid.NewGuid());
            Assert.Equal(DirectoryDecision.Forbidden, replay.Decision);
            Assert.Null(replay.Id);
        }
    }

    /// <summary>RCPT-01: every other named nonsecret directory command uses the same owner receipt boundary.</summary>
    [Theory]
    [InlineData("CreateDepartment")]
    [InlineData("ChangeDepartment")]
    [InlineData("ChangeTenant")]
    [InlineData("CreateDictionary")]
    [InlineData("ChangeTranslation")]
    [InlineData("ChangeDictionaryReference")]
    [InlineData("ChangeMemberships")]
    public async Task NamedNonsecretCommands_ReplayOneCommittedTransition(string command)
    {
        Guid entryId;
        string entryVersion;
        Guid userId;
        string userVersion;
        string departmentVersion;
        string tenantVersion;
        Guid roleId;
        await using (var db = Open())
        {
            var entry = new DictionaryEntry(_tenant, "task", "ready", "task.ready");
            var user = new DirectoryUser(_tenant, Guid.NewGuid(), "MEMBER", "Member", _department);
            var role = new AccessRole(_tenant, "member", [Grant("platform.users.write", "Name")]);
            entryId = entry.Id;
            entryVersion = entry.Version;
            userId = user.Id;
            userVersion = user.Version;
            roleId = role.Id;
            db.DictionaryEntries.Add(entry);
            db.DirectoryUsers.Add(user);
            db.Roles.Add(role);
            departmentVersion = (await db.Departments.SingleAsync(x => x.Id == _otherDepartment)).Version;
            tenantVersion = (await db.DirectoryTenants.SingleAsync()).Version;
            await db.SaveChangesAsync();
        }
        var reference = Guid.NewGuid();
        async Task<DirectoryWriteResult> ExecuteAsync()
        {
            await using var db = Open();
            var service = Service(db);
            return command switch
            {
                "CreateDepartment" => await service.CreateDepartmentAsync(_context, "new", "New", Guid.NewGuid()),
                "ChangeDepartment" => await service.ChangeDepartmentAsync(_context, _otherDepartment, departmentVersion,
                    "Renamed", true, Guid.NewGuid()),
                "ChangeTenant" => await service.ChangeTenantAsync(_context, tenantVersion, "Renamed", true, "en", Guid.NewGuid()),
                "CreateDictionary" => await service.CreateDictionaryAsync(_context, "task", "new", "task.new", Guid.NewGuid()),
                "ChangeTranslation" => await service.ChangeTranslationAsync(_context, entryId, entryVersion, "en", "Ready", Guid.NewGuid()),
                "ChangeDictionaryReference" => await service.ChangeDictionaryReferenceAsync(_context, entryId, entryVersion,
                    reference, true, Guid.NewGuid()),
                "ChangeMemberships" => await service.ChangeMembershipsAsync(_context, userId, userVersion, [roleId], Guid.NewGuid()),
                _ => throw new InvalidOperationException("Unknown test command.")
            };
        }
        var first = await ExecuteAsync();
        var replay = await ExecuteAsync();
        Assert.Equal(DirectoryDecision.Changed, first.Decision);
        Assert.Equal(first, replay);
        await using var verify = Open();
        Assert.Single(await verify.CommandReceipts.ToListAsync());
        Assert.Single(await verify.Audit.ToListAsync());
    }

    /// <summary>RCPT-02/05: unsuccessful business outcomes never reserve an operation identity.</summary>
    [Fact]
    public async Task DuplicateAndVersionConflict_DoNotCreateReceipts()
    {
        await using (var db = Open())
        {
            Assert.Equal(DirectoryDecision.Duplicate, (await Service(db).CreateUserAsync(
                _context, "OWNER", "Owner", _department, Guid.NewGuid())).Decision);
            Assert.Empty(await db.CommandReceipts.ToListAsync());
        }
        await using (var db = Open())
        {
            var result = await Service(db).ChangeUserAsync(_context, _actor, Guid.NewGuid().ToString(),
                "Unchanged", _department, true, Guid.NewGuid());
            Assert.Equal(DirectoryDecision.VersionConflict, result.Decision);
            Assert.Empty(await db.CommandReceipts.ToListAsync());
            Assert.Empty(await db.Audit.ToListAsync());
        }
        Assert.Equal(DirectoryDecision.Changed, (await CreateAsync()).Decision);
    }

    /// <summary>RCPT-01: opt-in receipts do not change old constructor behavior or retrofit keys onto legacy calls.</summary>
    [Fact]
    public async Task NoOperationContext_PreservesExistingDuplicateSemantics()
    {
        await using (var db = Open())
        {
            var service = new DirectoryService(db, _clock);
            Assert.Equal(DirectoryDecision.Changed, (await service.CreateUserAsync(
                _context, "TARGET", "Target", _department, Guid.NewGuid())).Decision);
        }
        await using (var db = Open())
        {
            var service = new DirectoryService(db, _clock);
            Assert.Equal(DirectoryDecision.Duplicate, (await service.CreateUserAsync(
                _context, "TARGET", "Target", _department, Guid.NewGuid())).Decision);
            Assert.Empty(await db.CommandReceipts.ToListAsync());
            Assert.Single(await db.Audit.ToListAsync());
        }
    }

    /// <summary>RCPT-06: an expired key can serve a new command without removing any stable directory identities.</summary>
    [Fact]
    public async Task ExpiredReceipt_NewBusinessIdentity_ReplacesOnlyReceipt()
    {
        await CreateAsync();
        await AdvanceAndRenewSessionAsync();
        await using (var db = Open())
            Assert.Equal(DirectoryDecision.Changed, (await Service(db).CreateUserAsync(
                _context, "NEW-TARGET", "New target", _department, Guid.NewGuid())).Decision);
        await using var verify = Open();
        Assert.Equal(3, await verify.DirectoryUsers.CountAsync());
        Assert.Single(await verify.CommandReceipts.ToListAsync());
        Assert.Equal(2, await verify.Audit.CountAsync());
    }

    /// <summary>RCPT-06: retention is bounded tracked deletion of receipts only.</summary>
    [Fact]
    public async Task RetentionCleanup_DeletesOnlyExpiredReceipt_NotDomainOrAudit()
    {
        await CreateAsync();
        await AdvanceAndRenewSessionAsync();
        await using (var db = Open())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            Assert.Equal(1, await DirectoryCommandReceipts.RemoveExpiredAsync(db, _clock.Now, 1));
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await using var verify = Open();
        Assert.Empty(await verify.CommandReceipts.ToListAsync());
        Assert.Equal(2, await verify.DirectoryUsers.CountAsync());
        Assert.Single(await verify.Audit.ToListAsync());
        Assert.Equal(DirectoryDecision.Duplicate, (await CreateAsync()).Decision);
    }

    /// <summary>RCPT-01: dictionary deletion replay returns its original safe result despite physical entry deletion.</summary>
    [Fact]
    public async Task DeleteDictionary_Replay_OneTransitionAndAudit()
    {
        Guid id;
        string version;
        await using (var db = Open())
        {
            var entry = new DictionaryEntry(_tenant, "category", "code", "key");
            id = entry.Id;
            version = entry.Version;
            db.DictionaryEntries.Add(entry);
            await db.SaveChangesAsync();
        }
        DirectoryWriteResult first;
        await using (var db = Open())
            first = await Service(db).DeleteDictionaryAsync(_context, id, version, Guid.NewGuid());
        await using (var db = Open())
            Assert.Equal(first, await Service(db).DeleteDictionaryAsync(_context, id, version, Guid.NewGuid()));
        Assert.Equal(DirectoryDecision.Changed, first.Decision);
        await using var verify = Open();
        Assert.Empty(await verify.DictionaryEntries.ToListAsync());
        Assert.Single(await verify.Audit.ToListAsync());
    }

    /// <summary>RCPT-07: additive migration and model persist only the eight approved columns.</summary>
    [Fact]
    public void MigrationAndModel_ContainOnlyApprovedReceiptColumns()
    {
        using var db = Open();
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.Migrations.Single(x => x.Key.EndsWith("_DirectoryCommandReceipts", StringComparison.Ordinal));
        var operations = assembly.CreateMigration(migration.Value, db.Database.ProviderName!).UpOperations;
        Assert.All(operations, x => Assert.True(x is CreateTableOperation or CreateIndexOperation));
        var table = Assert.Single(operations.OfType<CreateTableOperation>());
        Assert.Equal("platform_directory_command_receipts", table.Name);
        Assert.Equal(new[] { "ActorId", "CreatedAt", "Fingerprint", "Kind", "OperationId", "ResultId", "ResultVersion", "TenantId" },
            table.Columns.Select(x => x.Name).Order(StringComparer.Ordinal));
        Assert.Equal(8, db.Model.FindEntityType(typeof(DirectoryCommandReceipt))!.GetProperties().Count());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private async Task AdvanceAndRenewSessionAsync()
    {
        _clock.Now += TimeSpan.FromDays(90);
        await using var db = Open();
        var session = new Session(_tenant, _actor, SessionClient.Web, _clock.Now, new string('D', 64));
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        _context = new(_tenant, _actor, session.Id);
    }

    private async Task<DirectoryWriteResult> CreateAsync(string name = "Target")
    {
        await using var db = Open();
        return await Service(db).CreateUserAsync(_context, "TARGET", name, _department, Guid.NewGuid());
    }

    private DirectoryService Service(PlatformDbContext db) => new(db, _clock, new DirectoryOperation(_operation));

    private PlatformDbContext Open(IInterceptor? interceptor = null, Guid? tenant = null)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseMySQL(_connection, mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options, tenant ?? _tenant);
    }

    private static PermissionGrant Grant(string permission, params string[] fields) =>
        new(permission, DataScope.All, ImmutableHashSet<Guid>.Empty, fields.ToImmutableHashSet());

    /// <summary>Deletes only generated tenant fixtures, never schema or unrelated tenants.</summary>
    public async Task DisposeAsync()
    {
        if (!_migrated) return;
        foreach (var tenant in new[] { _tenant, _otherTenant })
        {
            await using var db = Open(tenant: tenant);
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_audit WHERE TenantId = {tenant.ToString("D")}");
            db.CommandReceipts.RemoveRange(await db.CommandReceipts.ToListAsync());
            db.DictionaryReferences.RemoveRange(await db.DictionaryReferences.ToListAsync());
            db.DictionaryTranslations.RemoveRange(await db.DictionaryTranslations.ToListAsync());
            db.DictionaryEntries.RemoveRange(await db.DictionaryEntries.ToListAsync());
            db.Memberships.RemoveRange(await db.Memberships.ToListAsync());
            db.Sessions.RemoveRange(await db.Sessions.ToListAsync());
            db.DirectoryUsers.RemoveRange(await db.DirectoryUsers.ToListAsync());
            db.Roles.RemoveRange(await db.Roles.ToListAsync());
            db.Departments.RemoveRange(await db.Departments.ToListAsync());
            db.DirectoryTenants.RemoveRange(await db.DirectoryTenants.ToListAsync());
            await db.SaveChangesAsync();
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 24, 4, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Injected post-SQL fault.");
    }

    private sealed class CancelAfterSave(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }
}
