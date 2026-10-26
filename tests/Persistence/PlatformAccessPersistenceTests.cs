using System.Collections.Immutable;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wes.Platform.Domain;
using Wes.Platform.Features.Audit;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Settings;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>Real-engine access, optimistic writes and audit integrity using only generated tenant fixtures.</summary>
[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed partial class PlatformAccessPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 16, 0, 0, TimeSpan.Zero);
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private string _connection = "";
    private bool _migrated;

    /// <summary>Applies the reviewed owner migration only to the authorized isolated schema.</summary>
    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var database = Open(_tenant);
        await database.Database.MigrateAsync();
        _migrated = true;
    }

    /// <summary>DTC-PLAT-004: historical sessions/roles alone cannot invent an enabled directory identity.</summary>
    [Fact]
    public async Task CurrentAuthorization_MissingDirectoryIdentity_FailsClosed()
    {
        var (context, _, _) = await SeedAsync(provisionDirectory: false);
        await using var read = Open(_tenant);
        Assert.Empty(await new CurrentAuthorization(read, new FixedClock()).ReadGrantsAsync(context));
    }

    /// <summary>Current enabled roles round-trip through JSON, then revoke immediately even on a reused context.</summary>
    [Fact]
    [Trait("CaseId", "ACCESS-CURRENT-001")]
    public async Task CurrentRoleChange_IsNotHiddenByTrackedEntitiesOrCache()
    {
        var (context, roleId, _) = await SeedAsync();
        await using var read = Open(_tenant);
        var authorization = new CurrentAuthorization(read, new FixedClock());
        Assert.Single(await authorization.ReadGrantsAsync(context));
        await using (var writer = Open(_tenant))
        {
            var role = await writer.Roles.SingleAsync(item => item.Id == roleId);
            Assert.Equal(SettingDecision.Changed, role.Change(role.Version, false, role.Rules));
            await writer.SaveChangesAsync();
        }
        Assert.Empty(await authorization.ReadGrantsAsync(context));
    }

    /// <summary>Removing membership and revoking a live session take effect at the next decision.</summary>
    [Fact]
    [Trait("CaseId", "ACCESS-CURRENT-001")]
    public async Task MembershipAndSession_AreReadFromCurrentOwnerState()
    {
        var (context, roleId, _) = await SeedAsync();
        await using var read = Open(_tenant);
        var authorization = new CurrentAuthorization(read, new FixedClock());
        Assert.Single(await authorization.ReadGrantsAsync(context));
        await using (var writer = Open(_tenant))
        {
            writer.Memberships.Remove(await writer.Memberships.SingleAsync());
            await writer.SaveChangesAsync();
        }
        Assert.Empty(await authorization.ReadGrantsAsync(context));
        await using (var writer = Open(_tenant))
        {
            writer.Memberships.Add(new RoleMembership(_tenant, _actor, roleId));
            (await writer.Sessions.SingleAsync()).Revoke(Now);
            await writer.SaveChangesAsync();
        }
        Assert.Empty(await authorization.ReadGrantsAsync(context));
    }

    /// <summary>Identical role codes in another tenant cannot grant to this actor; mismatched contexts are explicit faults.</summary>
    [Fact]
    [Trait("CaseId", "ACCESS-CURRENT-001")]
    public async Task TenantRoleCodes_AreIsolated_AndContextMismatchFails()
    {
        var (context, _, _) = await SeedAsync();
        await using (var other = Open(_other))
        {
            other.Roles.Add(new AccessRole(_other, "operator", []));
            await other.SaveChangesAsync();
            Assert.Single(await other.Roles.ToListAsync());
        }
        await using var read = Open(_tenant);
        Assert.Single(await read.Roles.ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CurrentAuthorization(read, new FixedClock()).ReadGrantsAsync(context with { TenantId = _other }));
        Assert.Empty(await new CurrentAuthorization(read, new FixedClock())
            .ReadGrantsAsync(context with { ActorId = Guid.NewGuid() }));
    }

    /// <summary>Real database CAS rejects a second editor rather than silently overwriting the first role version.</summary>
    [Fact]
    [Trait("CaseId", "ACCESS-VERSION-001")]
    public async Task ConcurrentRoleEditors_RejectStaleVersion()
    {
        await SeedAsync();
        await using var first = Open(_tenant);
        await using var second = Open(_tenant);
        var a = await first.Roles.SingleAsync();
        var b = await second.Roles.SingleAsync();
        a.Change(a.Version, false, a.Rules);
        b.Change(b.Version, false, b.Rules);
        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    /// <summary>The application path enforces delegation and persists one role-version audit before disclosing success.</summary>
    [Fact]
    [Trait("CaseId", "ACCESS-ROLE-WRITE-001")]
    public async Task RoleHandler_CommitsAuthorizedChangeAndAudit_ThenRejectsStaleVersion()
    {
        var (context, target, version) = await SeedRoleEditorAsync();
        await using (var write = Open(_tenant))
        {
            var result = await RoleHandler(write).HandleAsync(context, target, version, false,
                [StockGrant(DataScope.Departments, ["Id"])], Guid.NewGuid());
            Assert.Equal(SettingDecision.Changed, result.Decision);
            Assert.NotNull(result.Version);
        }
        await using (var stale = Open(_tenant))
        {
            var result = await RoleHandler(stale).HandleAsync(context, target, version, true,
                [StockGrant(DataScope.Departments, ["Id"])], Guid.NewGuid());
            Assert.Equal(SettingDecision.VersionConflict, result.Decision);
            Assert.Null(result.Version);
        }
        await using var verify = Open(_tenant);
        var role = await verify.Roles.SingleAsync(item => item.Id == target);
        Assert.False(role.Enabled);
        var audit = await verify.Audit.SingleAsync();
        Assert.Equal(PlatformAuditAction.RoleChanged, audit.Action);
        Assert.Equal(version, audit.BeforeVersion);
        Assert.Equal(role.Version, audit.AfterVersion);
    }

    /// <summary>Menu management alone cannot delegate broader permission, departments, fields, or a stronger existing role.</summary>
    [Theory]
    [InlineData("no-management")]
    [InlineData("missing-field")]
    [InlineData("all-scope")]
    [InlineData("extra-field")]
    [InlineData("extra-department")]
    [InlineData("strong-target")]
    [Trait("CaseId", "ACCESS-ROLE-WRITE-001")]
    public async Task RoleHandler_RejectsUndelegableChangeWithoutSideEffects(string variant)
    {
        var (context, target, version) = await SeedRoleEditorAsync(variant);
        var requested = variant switch
        {
            "all-scope" => StockGrant(DataScope.All, ["Id"]),
            "extra-field" => StockGrant(DataScope.Departments, ["Id", "Cost"]),
            "extra-department" => StockGrant(DataScope.Departments, ["Id"]) with { Departments = ImmutableHashSet.Create(Guid.NewGuid()) },
            _ => StockGrant(DataScope.Departments, ["Id"])
        };
        await using (var write = Open(_tenant))
        {
            var result = await RoleHandler(write).HandleAsync(context, target, version, false, [requested], Guid.NewGuid());
            Assert.Equal(SettingDecision.Forbidden, result.Decision);
            Assert.Null(result.Version);
        }
        await using var verify = Open(_tenant);
        Assert.Equal(version, (await verify.Roles.SingleAsync(item => item.Id == target)).Version);
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    /// <summary>Role audit failure propagates and leaves no permission/enabled/version half-update or pending EF graph.</summary>
    [Fact]
    [Trait("CaseId", "ACCESS-ROLE-WRITE-001")]
    public async Task RoleHandler_AuditFailureLeavesRoleUnchanged()
    {
        var (context, target, version) = await SeedRoleEditorAsync();
        await using (var write = Open(_tenant, new FailAudit()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => RoleHandler(write).HandleAsync(
                context, target, version, false, [StockGrant(DataScope.Departments, ["Id"])], Guid.NewGuid()));
            Assert.Empty(write.ChangeTracker.Entries());
        }
        await using var verify = Open(_tenant);
        var role = await verify.Roles.SingleAsync(item => item.Id == target);
        Assert.True(role.Enabled);
        Assert.Equal(version, role.Version);
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    /// <summary>A losing authorized editor cannot leave an extra role audit when the actual SQL version check fails.</summary>
    [Fact]
    [Trait("CaseId", "ACCESS-ROLE-WRITE-001")]
    public async Task ConcurrentRoleHandlers_CommitOneRoleVersionAndAudit()
    {
        var (context, target, version) = await SeedRoleEditorAsync();
        var barrier = new SaveBarrier();
        await using var first = Open(_tenant, barrier);
        await using var second = Open(_tenant, barrier);
        var results = await Task.WhenAll(
            RoleHandler(first).HandleAsync(context, target, version, false, [StockGrant(DataScope.Departments, ["Id"])], Guid.NewGuid()),
            RoleHandler(second).HandleAsync(context, target, version, true, [StockGrant(DataScope.Departments, ["Id"])], Guid.NewGuid()));
        var winner = Assert.Single(results, item => item.Decision == SettingDecision.Changed);
        Assert.Null(Assert.Single(results, item => item.Decision == SettingDecision.VersionConflict).Version);
        await using var verify = Open(_tenant);
        Assert.Equal(winner.Version, (await verify.Roles.SingleAsync(item => item.Id == target)).Version);
        var audit = await verify.Audit.SingleAsync();
        Assert.Equal(winner.Version, audit.AfterVersion);
        Assert.Equal(version, audit.BeforeVersion);
    }

    private async Task<(TenantContext Context, Guid Target, string Version)> SeedRoleEditorAsync(string variant = "")
    {
        var (context, editor, _) = await SeedAsync();
        await using var database = Open(_tenant);
        var role = await database.Roles.SingleAsync(item => item.Id == editor);
        var grants = new List<PermissionGrant> { StockGrant(DataScope.Departments, ["Id"]) };
        if (variant != "no-management")
        {
            grants.Add(new PermissionGrant("platform.roles.write", DataScope.All, ImmutableHashSet<Guid>.Empty,
                variant == "missing-field" ? ImmutableHashSet.Create("Enabled") : ImmutableHashSet.Create("Rules", "Enabled")));
        }
        role.Change(role.Version, true, grants);
        var target = new AccessRole(_tenant, "target",
            [StockGrant(DataScope.Departments, variant == "strong-target" ? ["Id", "Cost"] : ["Id"])]);
        database.Roles.Add(target);
        await database.SaveChangesAsync();
        return (context, target.Id, target.Version);
    }

    private static PermissionGrant StockGrant(DataScope scope, string[] fields) =>
        new("wms.stock.read", scope, scope == DataScope.Departments
            ? ImmutableHashSet.Create(Guid.Parse("33333333-3333-3333-3333-333333333333")) : ImmutableHashSet<Guid>.Empty,
            fields.ToImmutableHashSet(StringComparer.Ordinal));

    private static ChangeRoleHandler RoleHandler(PlatformDbContext database) =>
        new(database, new FixedClock(), NullLogger<ChangeRoleHandler>.Instance);

    /// <summary>Scoped setting writes produce exactly one version-linked audit, with no writes on stale or malformed input.</summary>
    [Fact]
    [Trait("CaseId", "SETTINGS-VERSION-001")]
    public async Task SettingWrite_CommitsOneAudit_AndRejectsStaleOrInvalidInput()
    {
        var (context, _, settingId) = await SeedAsync();
        string version;
        await using (var read = Open(_tenant)) version = (await read.Settings.SingleAsync()).Version;
        await using (var write = Open(_tenant))
        {
            var result = await Handler(write).HandleAsync(context, settingId, version, "300", Guid.NewGuid());
            Assert.Equal(SettingDecision.Changed, result.Decision);
            Assert.NotEqual(version, result.Version);
        }
        await using (var stale = Open(_tenant))
        {
            Assert.Equal(SettingDecision.VersionConflict,
                (await Handler(stale).HandleAsync(context, settingId, version, "200", Guid.NewGuid())).Decision);
        }
        await using var verify = Open(_tenant);
        var setting = await verify.Settings.AsNoTracking().SingleAsync();
        Assert.Equal("300", setting.Value);
        var audit = await verify.Audit.AsNoTracking().SingleAsync();
        Assert.Equal(version, audit.BeforeVersion);
        Assert.Equal(setting.Version, audit.AfterVersion);
        Assert.Equal(_actor, audit.ActorId);
        var message = Assert.Single(await verify.OutboxMessages.AsNoTracking().ToListAsync());
        using var payload = System.Text.Json.JsonDocument.Parse(message.Payload);
        Assert.Equal(audit.Id, message.EventId);
        Assert.Equal("SettingChanged", payload.RootElement.GetProperty("action").GetString());
        Assert.Equal(SettingDecision.InvalidValue,
            (await Handler(verify).HandleAsync(context, settingId, setting.Version, "secret", Guid.NewGuid())).Decision);
        Assert.Equal(1, await verify.Audit.CountAsync());
        Assert.Equal(1, await verify.OutboxMessages.CountAsync());
    }

    /// <summary>Denied scope cannot mutate a global setting or discover its current version.</summary>
    [Fact]
    [Trait("CaseId", "SETTINGS-VERSION-001")]
    public async Task OwnScope_DoesNotAuthorizeTenantWideSetting()
    {
        var (context, _, settingId) = await SeedAsync(DataScope.Own);
        await using var write = Open(_tenant);
        var version = (await write.Settings.AsNoTracking().SingleAsync()).Version;
        var result = await Handler(write).HandleAsync(context, settingId, version, "300", Guid.NewGuid());
        Assert.Equal(SettingDecision.Forbidden, result.Decision);
        Assert.Null(result.Version);
        Assert.Empty(await write.Audit.ToListAsync());
    }

    /// <summary>Sync and async EF paths cannot modify or delete committed audit facts.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("CaseId", "AUDIT-IMMUTABLE-001")]
    public async Task Audit_IsAppendOnly(bool delete, bool synchronous)
    {
        await using (var seed = Open(_tenant))
        {
            seed.Audit.Add(NewAudit());
            await seed.SaveChangesAsync();
        }
        await using var write = Open(_tenant);
        var audit = await write.Audit.SingleAsync();
        if (delete) write.Audit.Remove(audit);
        else write.Entry(audit).Property(item => item.CorrelationId).CurrentValue = Guid.NewGuid();
        if (synchronous) Assert.Throws<InvalidOperationException>(() => write.SaveChanges());
        else await Assert.ThrowsAsync<InvalidOperationException>(() => write.SaveChangesAsync());
    }

    /// <summary>Failure after staging both entities propagates and never reports a committed successor.</summary>
    [Fact]
    [Trait("CaseId", "AUDIT-IMMUTABLE-001")]
    public async Task AuditFailure_DoesNotCommitSettingOrAudit()
    {
        var (context, _, settingId) = await SeedAsync();
        string version;
        await using (var read = Open(_tenant)) version = (await read.Settings.SingleAsync()).Version;
        await using (var write = Open(_tenant, new FailAudit()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Handler(write).HandleAsync(context, settingId, version, "300", Guid.NewGuid()));
        }
        await using var verify = Open(_tenant);
        Assert.Equal("600", (await verify.Settings.SingleAsync()).Value);
        Assert.Equal(version, (await verify.Settings.SingleAsync()).Version);
        Assert.Empty(await verify.Audit.ToListAsync());
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
    }

    /// <summary>A real duplicate-key audit failure rolls back the owner update, not only a pre-SQL test fault.</summary>
    [Fact]
    [Trait("CaseId", "AUDIT-ATOMIC-001")]
    public async Task DatabaseAuditFailure_RollsBackOwnerMutation()
    {
        var (context, _, settingId) = await SeedAsync();
        var duplicate = NewAudit();
        string version;
        await using (var seed = Open(_tenant))
        {
            seed.Audit.Add(duplicate);
            version = (await seed.Settings.SingleAsync()).Version;
            await seed.SaveChangesAsync();
        }
        await using (var write = Open(_tenant, new DuplicateAudit(duplicate.Id)))
        {
            var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
                Handler(write).HandleAsync(context, settingId, version, "300", Guid.NewGuid()));
            Assert.Equal(1062, Assert.IsType<MySql.Data.MySqlClient.MySqlException>(error.InnerException).Number);
            Assert.Empty(write.ChangeTracker.Entries());
        }
        await using var verify = Open(_tenant);
        var setting = await verify.Settings.SingleAsync();
        Assert.Equal(version, setting.Version);
        Assert.Equal("600", setting.Value);
        Assert.Equal(duplicate.Id, (await verify.Audit.SingleAsync()).Id);
        var outbox = Assert.Single(await verify.OutboxMessages.ToListAsync());
        Assert.Equal(duplicate.Id, outbox.EventId);
    }

    /// <summary>A provider fault after an audit SQL command must not leave the audit or its associated mutation committed.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("CaseId", "AUDIT-ATOMIC-002")]
    public async Task FailureAfterAuditSql_RollsBackRoleOrSettingAndItsAudit(bool role)
    {
        TenantContext context;
        Guid target;
        string version;
        if (role) (context, target, version) = await SeedRoleEditorAsync();
        else
        {
            (context, _, target) = await SeedAsync();
            await using var read = Open(_tenant);
            var setting = await read.Settings.SingleAsync();
            version = setting.Version;
        }
        await using (var write = Open(_tenant, new FailAfterAuditSql()))
        {
            Func<Task> command = role
                ? () => RoleHandler(write).HandleAsync(context, target, version, false, [StockGrant(DataScope.Departments, ["Id"])], Guid.NewGuid())
                : () => Handler(write).HandleAsync(context, target, version, "300", Guid.NewGuid());
            var fault = await Assert.ThrowsAnyAsync<Exception>(command);
            Assert.IsType<InjectedAuditSqlFailureException>(fault.GetBaseException());
            Assert.Empty(write.ChangeTracker.Entries());
        }
        await using var verify = Open(_tenant);
        Assert.Empty(await verify.Audit.ToListAsync());
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
        if (role)
        {
            var unchanged = await verify.Roles.SingleAsync(item => item.Id == target);
            Assert.True(unchanged.Enabled);
            Assert.Equal(version, unchanged.Version);
        }
        else
        {
            var unchanged = await verify.Settings.SingleAsync(item => item.Id == target);
            Assert.Equal("600", unchanged.Value);
            Assert.Equal(version, unchanged.Version);
        }
    }

    /// <summary>Two actual handlers overlap before SQL; CAS failure also rolls back that handler's audit insert.</summary>
    [Fact]
    [Trait("CaseId", "SETTINGS-VERSION-001")]
    public async Task ConcurrentSettingsHandlers_CommitExactlyOneVersionAndAudit()
    {
        var (context, _, settingId) = await SeedAsync();
        string version;
        await using (var read = Open(_tenant)) version = (await read.Settings.SingleAsync()).Version;
        var barrier = new SaveBarrier();
        await using var first = Open(_tenant, barrier);
        await using var second = Open(_tenant, barrier);
        var results = await Task.WhenAll(
            Handler(first).HandleAsync(context, settingId, version, "300", Guid.NewGuid()),
            Handler(second).HandleAsync(context, settingId, version, "200", Guid.NewGuid()));
        var winner = Assert.Single(results, item => item.Decision == SettingDecision.Changed);
        Assert.Null(Assert.Single(results, item => item.Decision == SettingDecision.VersionConflict).Version);
        await using var verify = Open(_tenant);
        var setting = await verify.Settings.SingleAsync();
        var audit = await verify.Audit.SingleAsync();
        var outbox = await verify.OutboxMessages.SingleAsync();
        Assert.Equal(audit.Id, outbox.EventId);
        Assert.Equal(winner.Version, setting.Version);
        Assert.Equal(winner.Version, audit.AfterVersion);
        Assert.Equal(version, audit.BeforeVersion);
    }

    /// <summary>The database itself enforces the composite role reference even when EF protections are bypassed.</summary>
    [Fact]
    [Trait("CaseId", "ACCESS-TENANT-001")]
    public async Task RoleMembershipForeignKey_RejectsCrossTenantRole()
    {
        Guid roleId;
        await using (var seed = Open(_other))
        {
            var role = new AccessRole(_other, "operator", []);
            roleId = role.Id;
            seed.Roles.Add(role);
            await seed.SaveChangesAsync();
        }
        await using var write = Open(_tenant);
        var error = await Assert.ThrowsAsync<MySql.Data.MySqlClient.MySqlException>(() =>
            write.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO platform_role_memberships (TenantId,UserId,RoleId) VALUES ({_tenant.ToString("D")},{_actor.ToString("D")},{roleId.ToString("D")})"));
        Assert.Equal(1452, error.Number);
        Assert.Empty(await write.Memberships.ToListAsync());
    }

    /// <summary>Every newly added owner entity participates in the existing synchronous and asynchronous tenant guard.</summary>
    [Theory]
    [InlineData("role", false)]
    [InlineData("role", true)]
    [InlineData("membership", false)]
    [InlineData("membership", true)]
    [InlineData("setting", false)]
    [InlineData("setting", true)]
    [InlineData("audit", false)]
    [InlineData("audit", true)]
    [Trait("CaseId", "ACCESS-TENANT-001")]
    public async Task WrongTenantWrites_AreRejected(string entity, bool synchronous)
    {
        await using var write = Open(_tenant);
        object foreign = entity switch
        {
            "role" => new AccessRole(_other, "operator", []),
            "membership" => new RoleMembership(_other, _actor, Guid.NewGuid()),
            "setting" => new VersionedSetting(_other, "query.snapshotTtlSeconds"),
            "audit" => new PlatformAudit(_other, _actor, Guid.NewGuid(), Guid.NewGuid(),
                PlatformAuditAction.SettingChanged, null, Guid.NewGuid().ToString("D"), Now),
            _ => throw new ArgumentException("Unknown test entity.")
        };
        write.Add(foreign);
        if (synchronous) Assert.Throws<InvalidOperationException>(() => write.SaveChanges());
        else await Assert.ThrowsAsync<InvalidOperationException>(() => write.SaveChangesAsync());
    }

    /// <summary>Own rows must be filtered before Take and field projection cannot reveal another grant's columns.</summary>
    [Fact]
    [Trait("CaseId", "AUDIT-QUERY-001")]
    public async Task AuditQuery_AppliesOwnScopeBeforePaging_AndRedactsFields()
    {
        var (context, _, _) = await SeedAsync();
        Guid expected;
        await using (var seed = Open(_tenant))
        {
            var role = await seed.Roles.SingleAsync();
            role.Change(role.Version, true, [new PermissionGrant("platform.audit.read", DataScope.Own,
                ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Id", "OccurredAt", "Action"))]);
            seed.Audit.Add(new PlatformAudit(_tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                PlatformAuditAction.RoleChanged, null, Guid.NewGuid().ToString("D"), Now.AddSeconds(-1)));
            var own = NewAudit();
            expected = own.Id;
            seed.Audit.Add(own);
            await seed.SaveChangesAsync();
        }
        await using var read = Open(_tenant);
        var page = await AuditQuery(read).ReadAsync(context, Now.AddMinutes(-1), Now.AddMinutes(1), limit: 1);
        Assert.True(page.Authorized);
        var row = Assert.Single(page.Rows);
        Assert.Equal(expected, row.Id);
        Assert.Equal(PlatformAuditAction.SettingChanged, row.Action);
        Assert.Null(row.ActorId);
        Assert.Null(row.TargetId);
        Assert.Null(row.CorrelationId);
        Assert.Null(row.BeforeVersion);
        Assert.Null(row.AfterVersion);
    }

    /// <summary>Tenant-wide metadata access must not turn a separate Own grant into Action visibility on everyone.</summary>
    [Fact]
    [Trait("CaseId", "AUDIT-QUERY-001")]
    public async Task AuditQuery_OwnFieldsDoNotLeakThroughAnotherRolesAllScope()
    {
        var (context, _, _) = await SeedAsync();
        var own = NewAudit();
        var foreign = new PlatformAudit(_tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            PlatformAuditAction.RoleChanged, null, Guid.NewGuid().ToString("D"), Now);
        await using (var seed = Open(_tenant))
        {
            var role = await seed.Roles.SingleAsync();
            role.Change(role.Version, true,
            [
                new PermissionGrant("platform.audit.read", DataScope.All, ImmutableHashSet<Guid>.Empty,
                    ImmutableHashSet.Create("Id", "OccurredAt")),
                new PermissionGrant("platform.audit.read", DataScope.Own, ImmutableHashSet<Guid>.Empty,
                    ImmutableHashSet.Create("Action"))
            ]);
            seed.Audit.AddRange(own, foreign);
            await seed.SaveChangesAsync();
        }
        await using var read = Open(_tenant);
        var page = await AuditQuery(read).ReadAsync(context, Now, Now.AddSeconds(1));
        Assert.True(page.Authorized);
        Assert.Equal(2, page.Rows.Length);
        Assert.Equal(PlatformAuditAction.SettingChanged, Assert.Single(page.Rows, item => item.Id == own.Id).Action);
        Assert.Null(Assert.Single(page.Rows, item => item.Id == foreign.Id).Action);
    }

    /// <summary>Explicit All remains tenant-bounded, the upper instant is exclusive, and equal instants use a stable ID tie-break.</summary>
    [Fact]
    [Trait("CaseId", "AUDIT-QUERY-001")]
    public async Task AuditQuery_BoundsTenantAndTime_ThenUsesStableOrdering()
    {
        var (context, _, _) = await SeedAsync();
        await using (var seed = Open(_tenant))
        {
            var role = await seed.Roles.SingleAsync();
            role.Change(role.Version, true, [new PermissionGrant("platform.audit.read", DataScope.All,
                ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Id", "OccurredAt", "ActorId"))]);
            seed.Audit.AddRange(NewAudit(), NewAudit());
            seed.Audit.Add(new PlatformAudit(_tenant, _actor, Guid.NewGuid(), Guid.NewGuid(),
                PlatformAuditAction.RoleChanged, null, Guid.NewGuid().ToString("D"), Now.AddSeconds(1)));
            await seed.SaveChangesAsync();
        }
        await using (var seed = Open(_other))
        {
            seed.Audit.Add(new PlatformAudit(_other, _actor, Guid.NewGuid(), Guid.NewGuid(),
                PlatformAuditAction.RoleChanged, null, Guid.NewGuid().ToString("D"), Now));
            await seed.SaveChangesAsync();
        }
        await using var read = Open(_tenant);
        var page = await AuditQuery(read).ReadAsync(context, Now, Now.AddSeconds(1));
        Assert.True(page.Authorized);
        Assert.Equal(2, page.Rows.Length);
        Assert.All(page.Rows, item => Assert.Equal(_actor, item.ActorId));
        Assert.Equal(page.Rows.Select(item => item.Id.ToString("D")).Order(StringComparer.Ordinal),
            page.Rows.Select(item => item.Id.ToString("D")));
        var next = await AuditQuery(read).ReadAsync(context, Now, Now.AddSeconds(1), offset: 1, limit: 1);
        Assert.Equal(page.Rows[1].Id, Assert.Single(next.Rows).Id);
    }

    /// <summary>Live offsets read sixty stable rows, observe intervening facts, and immediately recheck role access.</summary>
    [Fact]
    [Trait("CaseId", "AUDIT-REALTIME-001")]
    public async Task AuditQuery_SixtyRows_PageBoundariesAreLiveAndAuthorizationIsCurrent()
    {
        var (context, roleId, _) = await SeedAsync();
        await using (var seed = Open(_tenant))
        {
            var role = await seed.Roles.SingleAsync(item => item.Id == roleId);
            role.Change(role.Version, true, [new PermissionGrant("platform.audit.read", DataScope.All,
                ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Id", "OccurredAt"))]);
            seed.Audit.AddRange(Enumerable.Range(0, 60).Select(index =>
                new PlatformAudit(_tenant, _actor, Guid.NewGuid(), Guid.NewGuid(),
                    PlatformAuditAction.RoleChanged, null, Guid.NewGuid().ToString("D"), Now.AddSeconds(index))));
            await seed.SaveChangesAsync();
        }
        await using var read = Open(_tenant);
        var query = AuditQuery(read);
        var first = await query.ReadAsync(context, Now.AddSeconds(-1), Now.AddMinutes(2), limit: 25);
        var second = await query.ReadAsync(context, Now.AddSeconds(-1), Now.AddMinutes(2), offset: 25, limit: 25);
        var third = await query.ReadAsync(context, Now.AddSeconds(-1), Now.AddMinutes(2), offset: 50, limit: 25);
        Assert.All(new[] { first, second, third }, page => Assert.True(page.Authorized));
        Assert.Equal(25, first.Rows.Length);
        Assert.Equal(25, second.Rows.Length);
        Assert.Equal(10, third.Rows.Length);
        Assert.Equal(60, first.Rows.Concat(second.Rows).Concat(third.Rows).Select(row => row.Id).Distinct().Count());
        var larger = await query.ReadAsync(context, Now.AddSeconds(-1), Now.AddMinutes(2), limit: 50);
        Assert.Equal(first.Rows.Concat(second.Rows).Select(row => row.Id), larger.Rows.Select(row => row.Id));
        var all = await query.ReadAsync(context, Now.AddSeconds(-1), Now.AddMinutes(2), limit: 100);
        Assert.Equal(60, all.Rows.Length);
        await using (var writer = Open(_tenant))
        {
            writer.Audit.Add(new PlatformAudit(_tenant, _actor, Guid.NewGuid(), Guid.NewGuid(),
                PlatformAuditAction.RoleChanged, null, Guid.NewGuid().ToString("D"), Now.AddSeconds(-1)));
            await writer.SaveChangesAsync();
        }
        var shifted = await query.ReadAsync(context, Now.AddSeconds(-1), Now.AddMinutes(2), offset: 25, limit: 25);
        Assert.Equal(first.Rows[^1].Id, shifted.Rows[0].Id);
        await using (var writer = Open(_tenant))
        {
            var role = await writer.Roles.SingleAsync(item => item.Id == roleId);
            role.Change(role.Version, false, role.Rules);
            await writer.SaveChangesAsync();
        }
        var denied = await query.ReadAsync(context, Now.AddSeconds(-1), Now.AddMinutes(2), offset: 25, limit: 25);
        Assert.False(denied.Authorized);
        Assert.Empty(denied.Rows);
    }

    /// <summary>A fixed audit snapshot continues the original ordered membership after new earlier facts commit.</summary>
    [Fact]
    [Trait("CaseId", "AUDIT-SNAPSHOT-001")]
    public async Task AuditSnapshot_ReadsCapturedIdsAfterAConcurrentEarlierFact()
    {
        var (context, roleId, _) = await SeedAsync();
        await using (var seed = Open(_tenant))
        {
            var role = await seed.Roles.SingleAsync(item => item.Id == roleId);
            role.Change(role.Version, true, [new PermissionGrant("platform.audit.read", DataScope.All,
                ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Id", "OccurredAt"))]);
            seed.Audit.AddRange(Enumerable.Range(0, 60).Select(index =>
                new PlatformAudit(_tenant, _actor, Guid.NewGuid(), Guid.NewGuid(),
                    PlatformAuditAction.RoleChanged, null, Guid.NewGuid().ToString("D"), Now.AddSeconds(index))));
            await seed.SaveChangesAsync();
        }

        Guid snapshotId;
        await using (var create = Open(_tenant))
        {
            var result = await AuditQuery(create).CreateSnapshotAsync(context, Now.AddSeconds(-1), Now.AddMinutes(2), 25);
            Assert.Equal(AuditSnapshotDecision.Success, result.Decision);
            Assert.NotNull(result.Page);
            Assert.Equal(60, result.Page.Count);
            Assert.Equal(25, result.Page.Rows.Length);
            snapshotId = result.Page.SnapshotId;
        }

        await using (var writer = Open(_tenant))
        {
            writer.Audit.Add(new PlatformAudit(_tenant, _actor, Guid.NewGuid(), Guid.NewGuid(),
                PlatformAuditAction.RoleChanged, null, Guid.NewGuid().ToString("D"), Now.AddSeconds(-1)));
            await writer.SaveChangesAsync();
        }

        await using var read = Open(_tenant);
        var second = await AuditQuery(read).ReadSnapshotAsync(context, snapshotId, 25, 25);
        Assert.Equal(AuditSnapshotDecision.Success, second.Decision);
        Assert.NotNull(second.Page);
        Assert.Equal(60, second.Page.Count);
        Assert.Equal(25, second.Page.Rows.Length);
    }

    /// <summary>Revoking audit-read access invalidates a still-live snapshot before its TTL expires.</summary>
    [Fact]
    [Trait("CaseId", "AUDIT-SNAPSHOT-002")]
    public async Task AuditSnapshot_RejectsContinuationAfterCurrentRoleIsRevoked()
    {
        var (context, roleId, _) = await SeedAsync();
        await using (var seed = Open(_tenant))
        {
            var role = await seed.Roles.SingleAsync(item => item.Id == roleId);
            role.Change(role.Version, true, [new PermissionGrant("platform.audit.read", DataScope.All,
                ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Id", "OccurredAt"))]);
            seed.Audit.Add(NewAudit());
            await seed.SaveChangesAsync();
        }

        Guid snapshotId;
        await using (var create = Open(_tenant))
        {
            var result = await AuditQuery(create).CreateSnapshotAsync(context, Now.AddMinutes(-1), Now.AddMinutes(1));
            Assert.Equal(AuditSnapshotDecision.Success, result.Decision);
            snapshotId = result.Page!.SnapshotId;
        }

        await using (var revoke = Open(_tenant))
        {
            var role = await revoke.Roles.SingleAsync(item => item.Id == roleId);
            role.Change(role.Version, false, role.Rules);
            await revoke.SaveChangesAsync();
        }

        await using var read = Open(_tenant);
        var denied = await AuditQuery(read).ReadSnapshotAsync(context, snapshotId, 0, 25);
        Assert.Equal(AuditSnapshotDecision.Forbidden, denied.Decision);
        Assert.Null(denied.Page);
    }

    /// <summary>Permission denial is explicit; invalid pagination/time input cannot silently become a broad query.</summary>
    [Fact]
    [Trait("CaseId", "AUDIT-QUERY-001")]
    public async Task AuditQuery_DeniesMissingPermission_AndRejectsInvalidBounds()
    {
        var (context, _, _) = await SeedAsync();
        await using var read = Open(_tenant);
        var query = AuditQuery(read);
        var page = await query.ReadAsync(context, Now, Now.AddMinutes(1));
        Assert.False(page.Authorized);
        Assert.Empty(page.Rows);
        await Assert.ThrowsAsync<ArgumentException>(() => query.ReadAsync(context, Now, Now));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => query.ReadAsync(context, Now, Now.AddMinutes(1), limit: 101));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => query.ReadAsync(context, Now, Now.AddMinutes(1), offset: -1));
    }

    private static QueryAudit AuditQuery(PlatformDbContext database) =>
        new(database, new FixedClock(), NullLogger<QueryAudit>.Instance);

    private PlatformAudit NewAudit() => new(_tenant, _actor, Guid.NewGuid(), Guid.NewGuid(),
        PlatformAuditAction.SettingChanged, null, Guid.NewGuid().ToString("D"), Now);

    private async Task<(TenantContext Context, Guid Role, Guid Setting)> SeedAsync(DataScope scope = DataScope.All, bool provisionDirectory = true)
    {
        await using var database = Open(_tenant);
        if (provisionDirectory) DirectoryTestFixture.Add(database, _actor);
        var session = new Session(_tenant, _actor, SessionClient.Web, Now, new string('A', 64));
        var role = new AccessRole(_tenant, "operator", [new PermissionGrant("platform.settings.write", scope,
            ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Value"))]);
        var setting = new VersionedSetting(_tenant, "query.snapshotTtlSeconds");
        database.Sessions.Add(session);
        database.Roles.Add(role);
        database.Memberships.Add(new RoleMembership(_tenant, _actor, role.Id));
        database.Settings.Add(setting);
        await database.SaveChangesAsync();
        return (new TenantContext(_tenant, _actor, session.Id), role.Id, setting.Id);
    }

    private static ChangeSettingHandler Handler(PlatformDbContext database) =>
        new(database, new FixedClock(), NullLogger<ChangeSettingHandler>.Instance);

    private PlatformDbContext Open(Guid tenant, IInterceptor? interceptor = null)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseMySQL(_connection, mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new PlatformDbContext(options.Options, tenant);
    }

    /// <summary>Deletes only this class's generated tenants, with no ordinary audit-delete capability in production.</summary>
    public async Task DisposeAsync()
    {
        if (!_migrated) return;
        foreach (var tenant in new[] { _tenant, _other })
        {
            await using var database = Open(tenant);
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_audit_export_receipts WHERE TenantId = {tenant.ToString("D")}");
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_audit WHERE TenantId = {tenant.ToString("D")}");
            database.Memberships.RemoveRange(await database.Memberships.ToListAsync());
            await database.SaveChangesAsync();
            database.Roles.RemoveRange(await database.Roles.ToListAsync());
            database.Settings.RemoveRange(await database.Settings.ToListAsync());
            database.Sessions.RemoveRange(await database.Sessions.ToListAsync());
            await DirectoryTestFixture.RemoveAsync(database);
            await database.SaveChangesAsync();
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FailAudit : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Assert.Single(eventData.Context!.ChangeTracker.Entries<PlatformAudit>(), item => item.State == EntityState.Added);
            throw new InvalidOperationException("Injected audit persistence failure.");
        }
    }

    private sealed class DuplicateAudit(Guid existingId) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var audit = Assert.Single(eventData.Context!.ChangeTracker.Entries<PlatformAudit>(), item => item.State == EntityState.Added);
            audit.Property(item => item.Id).CurrentValue = existingId;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailAfterAuditSql : DbCommandInterceptor
    {
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO `platform_audit`", StringComparison.Ordinal))
            {
                await result.DisposeAsync();
                throw new InjectedAuditSqlFailureException();
            }
            return result;
        }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default) =>
            command.CommandText.Contains("INSERT INTO `platform_audit`", StringComparison.Ordinal)
                ? throw new InjectedAuditSqlFailureException() : ValueTask.FromResult(result);
    }

    private sealed class InjectedAuditSqlFailureException : Exception;

    private sealed class SaveBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrivals) == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }
}
