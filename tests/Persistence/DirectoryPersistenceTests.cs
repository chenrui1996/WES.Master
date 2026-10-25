using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wes.Platform.Domain;
using Wes.Platform.Features.Authorization;
using Wes.Platform.Features.Directory;
using Wes.Platform.Features.Identity;
using Wes.Platform.Features.Tenancy;
using Wes.Platform.Infrastructure.Persistence;
using Xunit;

namespace Wes.Persistence.Tests;

/// <summary>DTC-PLAT-004..006/008/011/014 real MySQL owner behavior using generated isolated tenant fixtures only.</summary>
[Collection("PlatformPersistence")]
[Trait("Provider", "MySQL")]
public sealed class DirectoryPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 4, 0, 0, TimeSpan.Zero);
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private string _connection = "";
    private bool _migrated;

    /// <summary>Uses only the previously authorized wes_test helper, never application/old-system data.</summary>
    public async Task InitializeAsync()
    {
        _connection = MySqlTestConnection.LoadAuthorizedTestConnection();
        await using var database = Open();
        await database.Database.MigrateAsync();
        _migrated = true;
    }

    /// <summary>User creation is durable, separately credentialed, tenant-unique and atomically audited.</summary>
    [Fact]
    public async Task CreateUser_PersistsDirectoryOnlyAndAudit_RejectsCanonicalDuplicate()
    {
        var (context, department, _) = await SeedAsync();
        DirectoryWriteResult result;
        await using (var database = Open())
            result = await Service(database).CreateUserAsync(context, "  ｏｐｅｒａｔｏｒ  ", "Operator", department, Guid.NewGuid());
        Assert.Equal(DirectoryDecision.Changed, result.Decision);
        Assert.NotNull(result.Id);
        await using (var database = Open())
        {
            var row = await database.DirectoryUsers.SingleAsync(x => x.Id == result.Id);
            Assert.Equal("OPERATOR", row.Account);
            Assert.Empty(await database.PasswordCredentials.ToListAsync());
            var audit = Assert.Single(await database.Audit.ToListAsync());
            Assert.Equal(PlatformAuditAction.UserChanged, audit.Action);
            Assert.Equal(row.Version, audit.AfterVersion);
        }
        await using (var database = Open())
        {
            var duplicate = await Service(database).CreateUserAsync(context, "operator", "Other", department, Guid.NewGuid());
            Assert.Equal(DirectoryDecision.Duplicate, duplicate.Decision);
            Assert.Null(duplicate.Id);
            Assert.Single(await database.Audit.ToListAsync());
        }
    }

    private async Task<(TenantContext Context, Guid Department, Guid OtherDepartment)> SeedAsync(
        IEnumerable<PermissionGrant>? grants = null)
    {
        await using var database = Open();
        var department = new DirectoryDepartment(_tenant, "operations", "Operations");
        var other = new DirectoryDepartment(_tenant, "other", "Other");
        var session = new Session(_tenant, _actor, SessionClient.Web, Now, new string('A', 64));
        var role = new AccessRole(_tenant, "directory-owner", grants ?? FullGrants());
        database.DirectoryTenants.Add(new DirectoryTenant(_tenant, "Test tenant"));
        database.Departments.AddRange(department, other);
        database.DirectoryUsers.Add(new DirectoryUser(_tenant, _actor, "OWNER", "Owner", department.Id));
        database.Sessions.Add(session);
        database.Roles.Add(role);
        database.Memberships.Add(new RoleMembership(_tenant, _actor, role.Id));
        await database.SaveChangesAsync();
        return (new TenantContext(_tenant, _actor, session.Id), department.Id, other.Id);
    }

    /// <summary>All-row metadata cannot promote a separate Own grant's sensitive fields onto other users.</summary>
    [Fact]
    public async Task ReadUsers_ProjectsFieldsPerResource_AndUsesAuthoritativeDepartment()
    {
        var (context, department, other) = await SeedAsync();
        var target = Guid.NewGuid();
        await using (var seed = Open())
        {
            seed.DirectoryUsers.Add(new DirectoryUser(_tenant, target, "TARGET", "Target", other));
            var role = await seed.Roles.SingleAsync();
            role.Change(role.Version, true,
            [
                Grant("platform.users.read", "Id", "Account"),
                new PermissionGrant("platform.users.read", DataScope.Own, ImmutableHashSet<Guid>.Empty,
                    ImmutableHashSet.Create("Name")),
                new PermissionGrant("platform.users.write", DataScope.Departments, ImmutableHashSet.Create(department),
                    ImmutableHashSet.Create("Enabled"))
            ]);
            await seed.SaveChangesAsync();
        }
        await using var read = Open();
        var page = await Service(read).ReadUsersAsync(context, new());
        Assert.True(page.Authorized);
        Assert.Equal(2, page.Rows.Length);
        Assert.Equal("Owner", Assert.Single(page.Rows, x => x.Id == _actor).Name);
        Assert.Null(Assert.Single(page.Rows, x => x.Id == target).Name);
        Assert.All(page.Rows, x => Assert.Null(x.DepartmentId));
        var fields = await new CurrentAuthorization(read, new FixedClock()).VisibleFieldsAsync(context,
            "platform.users.write", new ResourceScope(_tenant, target, department));
        Assert.Empty(fields);
    }

    /// <summary>Credential concurrency versions are visible only with the explicit read-side Credential field grant.</summary>
    [Fact]
    public async Task ReadUsers_ProjectsCredentialVersionOnlyWhenGranted()
    {
        var (context, department, _) = await SeedAsync(
            [Grant("platform.users.read", "Id", "Enabled", "Version")]);
        var target = Guid.NewGuid();
        await using (var seed = Open())
        {
            seed.DirectoryUsers.Add(new DirectoryUser(_tenant, target, "CREDENTIAL", "Credential subject", department));
            seed.PasswordCredentials.Add(new PasswordCredential(_tenant, target, "CREDENTIAL",
                StoredPasswordHash.Import(PasswordHashScheme.BcryptSha384V1,
                    "$2a$12$N9qo8uLOickgx2ZMRZoMyebq.QrfAcQ2j7P1iiG.cM44tPZp1iuQS"), Now));
            await seed.SaveChangesAsync();
        }

        await using (var read = Open())
        {
            var row = Assert.Single((await Service(read).ReadUsersAsync(context, new())).Rows, item => item.Id == target);
            var json = JsonSerializer.SerializeToElement(row, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Assert.True(json.TryGetProperty("credentialVersion", out var version));
            Assert.Equal(JsonValueKind.Null, version.ValueKind);
        }

        await using (var seed = Open())
        {
            var role = await seed.Roles.SingleAsync();
            role.Change(role.Version, true, [Grant("platform.users.read", "Id", "Enabled", "Version", "Credential")]);
            await seed.SaveChangesAsync();
        }

        await using var authorizedRead = Open();
        var authorizedRow = Assert.Single((await Service(authorizedRead).ReadUsersAsync(context, new())).Rows,
            item => item.Id == target);
        var authorizedJson = JsonSerializer.SerializeToElement(authorizedRow,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Equal((await authorizedRead.PasswordCredentials.SingleAsync(item => item.UserId == target)).Version,
            authorizedJson.GetProperty("credentialVersion").GetString());
    }

    /// <summary>A dictionary code is persisted independently of labels, with no generated CRUD or culture-based business mutation.</summary>
    [Fact]
    public async Task Dictionary_CreateStableIdentity_IsAtomicWithAudit()
    {
        var (context, _, _) = await SeedAsync();
        await using (var database = Open())
        {
            var result = await Service(database).CreateDictionaryAsync(context, "task", "Ready", "task.ready", Guid.NewGuid());
            Assert.Equal(DirectoryDecision.Changed, result.Decision);
        }
        await using var verify = Open();
        var entry = Assert.Single(await verify.DictionaryEntries.ToListAsync());
        Assert.Equal("Ready", entry.Code);
        Assert.Equal(entry.Version, (await verify.Audit.SingleAsync()).AfterVersion);
    }

    /// <summary>Local bootstrap is explicit, refuses repeats, and never silently installs a default administrator.</summary>
    [Fact]
    public async Task Bootstrap_RequiresExplicitGateAndEvidence_ProvisionsOnceWithoutDefaults()
    {
        await using (var gated = Open())
            Assert.Equal(DirectoryDecision.Forbidden, (await BootstrapAsync(gated, false, PasswordEvidence.Clear)).Decision);
        await using (var unavailable = Open())
            Assert.Equal(DirectoryDecision.PasswordRejected, (await BootstrapAsync(unavailable, true, PasswordEvidence.Unknown)).Decision);
        await using (var empty = Open()) Assert.Empty(await empty.DirectoryUsers.ToListAsync());
        await using (var first = Open())
            Assert.Equal(DirectoryDecision.Changed, (await BootstrapAsync(first, true, PasswordEvidence.Clear)).Decision);
        await using (var repeat = Open())
            Assert.Equal(DirectoryDecision.Forbidden, (await BootstrapAsync(repeat, true, PasswordEvidence.Clear)).Decision);
        await using var verify = Open();
        Assert.Single(await verify.DirectoryTenants.ToListAsync());
        Assert.Single(await verify.DirectoryUsers.ToListAsync());
        Assert.Single(await verify.PasswordCredentials.ToListAsync());
        Assert.Single(await verify.Memberships.ToListAsync());
        Assert.Empty(await verify.Sessions.ToListAsync());
        Assert.True(await new DirectoryEligibility(verify).IsEligibleAsync(_actor));
        Assert.Equal(9, await verify.Audit.CountAsync());
    }

    private Task<DirectoryWriteResult> BootstrapAsync(PlatformDbContext database, bool enabled, PasswordEvidence evidence) =>
        new DirectoryBootstrap(database, new FixedClock()).ProvisionAsync(enabled, _actor, "Explicit local tenant",
            "en", "operations", "Operations", "EXPLICIT.OWNER", "Explicit owner", "Explicit test credential 938!", evidence,
            "reviewed-owner", FullGrants(), Guid.NewGuid());

    /// <summary>Explicit provisioning installs only reviewed nonsecret baselines; physical values retain missing-evidence state.</summary>
    [Fact]
    public async Task Bootstrap_InitializesReviewedCatalogAndAuditsEveryInitialVersion()
    {
        await using (var provision = Open())
            Assert.Equal(DirectoryDecision.Changed, (await BootstrapAsync(provision, true, PasswordEvidence.Clear)).Decision);
        await using var verify = Open();
        Assert.Equal(3, await verify.Settings.CountAsync());
        Assert.Equal("600", (await verify.Settings.SingleAsync(row => row.Key == "query.snapshotTtlSeconds")).Value);
        var hardware = await verify.Settings.SingleAsync(row => row.Key == "device.feedbackFreshnessMilliseconds");
        Assert.Null(hardware.Value);
        Assert.Equal("Unverified", hardware.Source);
        Assert.Equal(3, await verify.Audit.CountAsync(row => row.Action == PlatformAuditAction.SettingChanged));
    }

    /// <summary>Credential provisioning is a distinct versioned owner operation; unavailable/prohibited evidence cannot mutate identity.</summary>
    [Theory]
    [InlineData(PasswordEvidence.Unknown)]
    [InlineData(PasswordEvidence.Prohibited)]
    [InlineData(PasswordEvidence.Clear)]
    public async Task CredentialCreation_UsesTrustedEvidenceAndIndependentVersionedCommand(PasswordEvidence evidence)
    {
        var (context, department, _) = await SeedAsync();
        DirectoryWriteResult user;
        await using (var create = Open())
            user = await Service(create).CreateUserAsync(context, "NEW.USER", "New user", department, Guid.NewGuid());
        await using (var provision = Open())
        {
            var result = await Service(provision).CreateCredentialAsync(context, user.Id!.Value, user.Version,
                "Separate test credential 938!", evidence, Guid.NewGuid());
            Assert.Equal(evidence == PasswordEvidence.Clear ? DirectoryDecision.Changed : DirectoryDecision.PasswordRejected, result.Decision);
        }
        await using var verify = Open();
        var directory = await verify.DirectoryUsers.SingleAsync(x => x.Id == user.Id);
        if (evidence == PasswordEvidence.Clear)
        {
            var credential = await verify.PasswordCredentials.SingleAsync();
            Assert.Equal(CredentialVerification.Verified, credential.Verify("Separate test credential 938!"));
            Assert.NotEqual(user.Version, directory.Version);
            Assert.True(await new DirectoryEligibility(verify).IsEligibleAsync(directory.Id));
            Assert.Equal(2, await verify.Audit.CountAsync());
        }
        else
        {
            Assert.Empty(await verify.PasswordCredentials.ToListAsync());
            Assert.Equal(user.Version, directory.Version);
            Assert.Single(await verify.Audit.ToListAsync());
        }
    }

    /// <summary>A department-limited manager cannot move another subject into or out of unauthorized scope.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangeUser_ChecksBothDepartmentScopes_AndDoesNotTrustRequestedDepartment(bool moveIntoScope)
    {
        var (context, department, other) = await SeedAsync();
        var userId = Guid.NewGuid();
        string version;
        await using (var seed = Open())
        {
            var user = new DirectoryUser(_tenant, userId, "LIMITED", "Limited", moveIntoScope ? other : department);
            version = user.Version;
            seed.DirectoryUsers.Add(user);
            var role = await seed.Roles.SingleAsync();
            role.Change(role.Version, true, [new PermissionGrant("platform.users.write", DataScope.Departments,
                ImmutableHashSet.Create(department), ImmutableHashSet.Create("Name", "DepartmentId", "Enabled"))]);
            await seed.SaveChangesAsync();
        }
        await using (var write = Open())
        {
            var result = await Service(write).ChangeUserAsync(context, userId, version, "Moved",
                moveIntoScope ? department : other, true, Guid.NewGuid());
            Assert.Equal(DirectoryDecision.Forbidden, result.Decision);
            Assert.Null(result.Version);
        }
        await using var verify = Open();
        Assert.Equal(version, (await verify.DirectoryUsers.SingleAsync(x => x.Id == userId)).Version);
        Assert.Empty(await verify.Audit.ToListAsync());
    }

    /// <summary>Directory profile CAS rejects stale writes, and disabling the actor immediately revokes current grants/families.</summary>
    [Fact]
    public async Task ChangeUser_RejectsStaleVersion_AndDisableRevokesSessionEligibility()
    {
        var (context, department, _) = await SeedAsync();
        string version;
        await using (var read = Open()) version = (await read.DirectoryUsers.SingleAsync()).Version;
        await using (var write = Open())
            Assert.Equal(DirectoryDecision.Changed, (await Service(write).ChangeUserAsync(context, _actor, version,
                "Updated", department, true, Guid.NewGuid())).Decision);
        await using (var stale = Open())
            Assert.Equal(DirectoryDecision.VersionConflict, (await Service(stale).ChangeUserAsync(context, _actor, version,
                "Stale", department, true, Guid.NewGuid())).Decision);
        await using (var read = Open()) version = (await read.DirectoryUsers.SingleAsync()).Version;
        await using (var disable = Open())
            Assert.Equal(DirectoryDecision.Changed, (await Service(disable).ChangeUserAsync(context, _actor, version,
                "Disabled", department, false, Guid.NewGuid())).Decision);
        await using var verify = Open();
        Assert.Empty(await new CurrentAuthorization(verify, new FixedClock()).ReadGrantsAsync(context));
        Assert.NotNull((await verify.Sessions.SingleAsync()).RevokedAt);
        Assert.Equal(3, await verify.Audit.CountAsync());
    }

    /// <summary>Membership edits cannot grant a permission absent from the caller, even with user management authority.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Memberships_RequireDelegationAndUserVersion(bool stronger)
    {
        var (context, department, _) = await SeedAsync();
        var target = Guid.NewGuid();
        Guid roleId;
        string version;
        await using (var seed = Open())
        {
            var user = new DirectoryUser(_tenant, target, "MEMBER", "Member", department);
            var role = new AccessRole(_tenant, "assigned", stronger
                ? [Grant("inventory.privileged.write", "Id")] : [Grant("platform.users.read", "Id")]);
            roleId = role.Id;
            version = user.Version;
            seed.DirectoryUsers.Add(user);
            seed.Roles.Add(role);
            await seed.SaveChangesAsync();
        }
        DirectoryWriteResult result;
        await using (var write = Open())
            result = await Service(write).ChangeMembershipsAsync(context, target, version, [roleId], Guid.NewGuid());
        Assert.Equal(stronger ? DirectoryDecision.Forbidden : DirectoryDecision.Changed, result.Decision);
        if (!stronger)
        {
            await using var stale = Open();
            Assert.Equal(DirectoryDecision.VersionConflict, (await Service(stale).ChangeMembershipsAsync(context, target,
                version, [], Guid.NewGuid())).Decision);
        }
        await using var verify = Open();
        Assert.Equal(stronger ? 0 : 1, await verify.Memberships.CountAsync(x => x.UserId == target));
        Assert.Equal(stronger ? 0 : 1, await verify.Audit.CountAsync());
    }

    /// <summary>Translation fallback is diagnostic only; referenced business codes cannot be deleted, including with fresh versions.</summary>
    [Fact]
    public async Task Dictionary_FallsBackToDefaultThenKey_AndProtectsDurableReferences()
    {
        var (context, _, _) = await SeedAsync();
        DirectoryWriteResult created;
        await using (var write = Open())
            created = await Service(write).CreateDictionaryAsync(context, "state", "Ready", "state.ready", Guid.NewGuid());
        await using (var read = Open())
        {
            var row = Assert.Single((await Service(read).ReadDictionaryAsync(context, "state", "fr")).Rows);
            Assert.Equal("[state.ready]", row.Label);
            Assert.True(row.MissingTranslation);
            Assert.Equal("Ready", row.Code);
        }
        DirectoryWriteResult translated;
        await using (var write = Open())
            translated = await Service(write).ChangeTranslationAsync(context, created.Id!.Value, created.Version,
                "en", "Ready label", Guid.NewGuid());
        await using (var read = Open())
        {
            var row = Assert.Single((await Service(read).ReadDictionaryAsync(context, "state", "fr")).Rows);
            Assert.Equal("Ready label", row.Label);
            Assert.Equal("en", row.ResolvedLanguage);
            Assert.False(row.MissingTranslation);
            Assert.Equal("Ready", row.Code);
        }
        var reference = Guid.NewGuid();
        DirectoryWriteResult referenced;
        await using (var write = Open())
            referenced = await Service(write).ChangeDictionaryReferenceAsync(context, created.Id!.Value, translated.Version,
                reference, true, Guid.NewGuid());
        await using (var write = Open())
            Assert.Equal(DirectoryDecision.Referenced, (await Service(write).DeleteDictionaryAsync(context,
                created.Id!.Value, referenced.Version, Guid.NewGuid())).Decision);
        await using (var verify = Open())
        {
            Assert.Single(await verify.DictionaryEntries.ToListAsync());
            Assert.Single(await verify.DictionaryReferences.ToListAsync());
            Assert.Equal(referenced.Version, (await verify.DictionaryEntries.SingleAsync()).Version);
            Assert.Equal(3, await verify.Audit.CountAsync());
        }
        DirectoryWriteResult released;
        await using (var write = Open())
            released = await Service(write).ChangeDictionaryReferenceAsync(context, created.Id!.Value, referenced.Version,
                reference, false, Guid.NewGuid());
        await using (var write = Open())
            Assert.Equal(DirectoryDecision.Changed, (await Service(write).DeleteDictionaryAsync(context,
                created.Id!.Value, released.Version, Guid.NewGuid())).Decision);
        await using var deleted = Open();
        Assert.Empty(await deleted.DictionaryEntries.ToListAsync());
        Assert.Empty(await deleted.DictionaryTranslations.ToListAsync());
        Assert.Equal(5, await deleted.Audit.CountAsync());
    }

    /// <summary>Target IDs alone cannot switch tenancy; only explicitly provisioned same-subject current eligibility is returned.</summary>
    [Fact]
    public async Task TenantSwitch_RequiresSameSubjectEligibleTargetAndCurrentSourceAuthorization()
    {
        var (context, _, _) = await SeedAsync();
        await using (var absent = Open())
            Assert.False((await Service(absent).CheckTenantSwitchAsync(context, _other)).Authorized);
        await using (var target = Open(_other))
            Assert.Equal(DirectoryDecision.Changed, (await BootstrapAsync(target, true, PasswordEvidence.Clear)).Decision);
        await using (var read = Open())
        {
            Assert.True((await Service(read).CheckTenantSwitchAsync(context, _other)).Authorized);
            Assert.Equal(2, (await Service(read).ReadTenantsAsync(context)).Rows.Length);
        }
        await using (var target = Open(_other))
        {
            var department = await target.Departments.SingleAsync();
            department.Change(department.Version, department.Name, false);
            await target.SaveChangesAsync();
        }
        await using var revoked = Open();
        Assert.False((await Service(revoked).CheckTenantSwitchAsync(context, _other)).Authorized);
        Assert.Single((await Service(revoked).ReadTenantsAsync(context)).Rows);
    }

    /// <summary>Hidden fields cannot be inferred through filters; field authorization remains resource-scoped.</summary>
    [Fact]
    public async Task UserFilters_DoNotBecomeHiddenFieldOracles()
    {
        var (context, department, _) = await SeedAsync();
        await using (var seed = Open())
        {
            seed.DirectoryUsers.Add(new DirectoryUser(_tenant, Guid.NewGuid(), "FOREIGN", "Other private name", department));
            var role = await seed.Roles.SingleAsync();
            role.Change(role.Version, true,
            [
                Grant("platform.users.read", "Id"),
                new PermissionGrant("platform.users.read", DataScope.Own, ImmutableHashSet<Guid>.Empty, ImmutableHashSet.Create("Name"))
            ]);
            await seed.SaveChangesAsync();
        }
        await using var read = Open();
        Assert.False((await Service(read).ReadUsersAsync(context, new(Account: "FOREIGN"))).Authorized);
        var own = await Service(read).ReadUsersAsync(context, new(Name: "Owner"));
        Assert.True(own.Authorized);
        Assert.Equal(_actor, Assert.Single(own.Rows).Id);
        Assert.Empty((await Service(read).ReadUsersAsync(context, new(Name: "private"))).Rows);
    }

    /// <summary>A failure after actual SQL writes but before owner commit rolls back both mutation and append-only audit.</summary>
    [Fact]
    public async Task PostSaveFailure_RollsBackDirectoryAndAudit_AndCancellationCannotWrite()
    {
        var (context, department, _) = await SeedAsync();
        await using (var failed = Open(interceptor: new FailAfterSave()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(failed)
                .CreateUserAsync(context, "ROLLBACK", "Rollback", department, Guid.NewGuid()));
            Assert.Empty(failed.ChangeTracker.Entries());
        }
        await using (var cancelled = Open())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(cancelled)
                .CreateUserAsync(context, "CANCELLED", "Cancelled", department, Guid.NewGuid(), new CancellationToken(true)));
        await using var verify = Open();
        Assert.Single(await verify.DirectoryUsers.ToListAsync());
        Assert.Empty(await verify.Audit.ToListAsync());
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
    }

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Injected after actual SQL and before transaction commit.");
    }

    /// <summary>Two genuinely staged editors share one user version and cannot both commit membership successors.</summary>
    [Fact]
    public async Task ConcurrentMembershipEditors_CommitOneSuccessorAndOneConflict()
    {
        var (context, department, _) = await SeedAsync();
        var target = Guid.NewGuid();
        string version;
        Guid roleId;
        await using (var seed = Open())
        {
            var user = new DirectoryUser(_tenant, target, "CONCURRENT", "Concurrent", department);
            var role = new AccessRole(_tenant, "concurrent-role", [Grant("platform.users.read", "Id")]);
            version = user.Version;
            roleId = role.Id;
            seed.DirectoryUsers.Add(user);
            seed.Roles.Add(role);
            await seed.SaveChangesAsync();
        }
        var barrier = new SaveBarrier();
        await using var first = Open(interceptor: barrier);
        await using var second = Open(interceptor: barrier);
        var results = await Task.WhenAll(
            Service(first).ChangeMembershipsAsync(context, target, version, [roleId], Guid.NewGuid()),
            Service(second).ChangeMembershipsAsync(context, target, version, [roleId], Guid.NewGuid()));
        Assert.Single(results, x => x.Decision == DirectoryDecision.Changed);
        Assert.Single(results, x => x.Decision == DirectoryDecision.VersionConflict);
        await using var verify = Open();
        Assert.Single(await verify.Memberships.Where(x => x.UserId == target).ToListAsync());
        Assert.Single(await verify.Audit.ToListAsync());
    }

    /// <summary>Named metadata APIs persist audited versions and only the explicitly granted role envelope.</summary>
    [Fact]
    public async Task DepartmentRoleAndTenantCommands_AreVersionedAndFieldAuthorized()
    {
        var (context, _, _) = await SeedAsync();
        DirectoryWriteResult department;
        await using (var write = Open())
            department = await Service(write).CreateDepartmentAsync(context, "quality", "Quality", Guid.NewGuid());
        await using (var write = Open())
            Assert.Equal(DirectoryDecision.Changed, (await Service(write).ChangeDepartmentAsync(context,
                department.Id!.Value, department.Version, "Quality assurance", true, Guid.NewGuid())).Decision);
        await using (var stale = Open())
            Assert.Equal(DirectoryDecision.VersionConflict, (await Service(stale).ChangeDepartmentAsync(context,
                department.Id!.Value, department.Version, "Stale", true, Guid.NewGuid())).Decision);
        await using (var write = Open())
        {
            Assert.Equal(DirectoryDecision.Changed, (await Service(write).CreateRoleAsync(context, "reader",
                [Grant("platform.users.read", "Id")], Guid.NewGuid())).Decision);
            Assert.Equal(DirectoryDecision.Forbidden, (await Service(write).CreateRoleAsync(context, "too-strong",
                [Grant("inventory.privileged.write", "Id")], Guid.NewGuid())).Decision);
        }
        string tenantVersion;
        await using (var read = Open())
        {
            Assert.Equal(3, (await Service(read).ReadDepartmentsAsync(context)).Rows.Length);
            Assert.Equal(2, (await Service(read).ReadRolesAsync(context)).Rows.Length);
            tenantVersion = Assert.Single((await Service(read).ReadTenantsAsync(context)).Rows).Version!;
        }
        await using (var write = Open())
            Assert.Equal(DirectoryDecision.Changed, (await Service(write).ChangeTenantAsync(context, tenantVersion,
                "Updated tenant", true, "de", Guid.NewGuid())).Decision);
        await using var verify = Open();
        Assert.Equal("de", (await verify.DirectoryTenants.SingleAsync()).DefaultLanguage);
        Assert.Equal(4, await verify.Audit.CountAsync());
    }

    /// <summary>Disabled department/tenant owner facts revoke current grants even on a reused read context.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentEligibility_ImmediatelyReflectsDepartmentAndTenantDisable(bool tenantDisabled)
    {
        var (context, department, _) = await SeedAsync();
        await using var read = Open();
        var authorization = new CurrentAuthorization(read, new FixedClock());
        Assert.NotEmpty(await authorization.ReadGrantsAsync(context));
        await using (var writer = Open())
        {
            if (tenantDisabled)
            {
                var tenant = await writer.DirectoryTenants.SingleAsync();
                tenant.Change(tenant.Version, tenant.Name, false, tenant.DefaultLanguage);
            }
            else
            {
                var row = await writer.Departments.SingleAsync(x => x.Id == department);
                row.Change(row.Version, row.Name, false);
            }
            await writer.SaveChangesAsync();
        }
        Assert.Empty(await authorization.ReadGrantsAsync(context));
        Assert.False((await Service(read).ReadUsersAsync(context, new())).Authorized);
    }

    private sealed class SaveBarrier : SaveChangesInterceptor
    {
        private int _arrived;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrived) == 2) _ready.TrySetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }

    private static IEnumerable<PermissionGrant> FullGrants()
    {
        yield return Grant("platform.users.write", "Account", "Name", "DepartmentId", "Enabled", "Roles", "Credential");
        yield return Grant("platform.users.read", "Id", "Account", "Name", "DepartmentId", "Enabled", "Version", "Roles");
        yield return Grant("platform.departments.write", "Code", "Name", "Enabled");
        yield return Grant("platform.departments.read", "Id", "Code", "Name", "Enabled", "Version");
        yield return Grant("platform.roles.write", "Code", "Rules", "Enabled");
        yield return Grant("platform.roles.read", "Id", "Code", "Rules", "Enabled", "Version");
        yield return Grant("platform.tenants.read", "Id", "Name", "Enabled", "DefaultLanguage", "Version");
        yield return Grant("platform.tenants.write", "Name", "Enabled", "DefaultLanguage");
        yield return Grant("platform.tenants.switch", "Id");
        yield return Grant("platform.dictionary.write", "Category", "Code", "LanguageKey", "Label", "References", "Delete");
        yield return Grant("platform.dictionary.read", "Id", "Category", "Code", "LanguageKey", "Label", "Version");
    }

    private static PermissionGrant Grant(string permission, params string[] fields) =>
        new(permission, DataScope.All, ImmutableHashSet<Guid>.Empty, fields.ToImmutableHashSet());
    private static DirectoryService Service(PlatformDbContext database) => new(database, new FixedClock());
    private PlatformDbContext Open(Guid? tenant = null, IInterceptor? interceptor = null)
    {
        MySqlTestConnection.ValidateTarget(_connection);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseMySQL(_connection, mysql => mysql.MigrationsHistoryTable("__WesPlatformMigrations"));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new PlatformDbContext(options.Options, tenant ?? _tenant);
    }

    /// <summary>Removes only generated tenant fixtures, including immutable audit via explicit test-only SQL.</summary>
    public async Task DisposeAsync()
    {
        if (!_migrated) return;
        foreach (var tenant in new[] { _tenant, _other })
        {
            await using var database = Open(tenant);
            await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_audit WHERE TenantId = {tenant.ToString("D")}");
            database.DictionaryReferences.RemoveRange(await database.DictionaryReferences.ToListAsync());
            database.DictionaryTranslations.RemoveRange(await database.DictionaryTranslations.ToListAsync());
            database.DictionaryEntries.RemoveRange(await database.DictionaryEntries.ToListAsync());
            database.Memberships.RemoveRange(await database.Memberships.ToListAsync());
            database.PasswordCredentials.RemoveRange(await database.PasswordCredentials.ToListAsync());
            database.Sessions.RemoveRange(await database.Sessions.ToListAsync());
            database.Settings.RemoveRange(await database.Settings.ToListAsync());
            database.DirectoryUsers.RemoveRange(await database.DirectoryUsers.ToListAsync());
            database.Roles.RemoveRange(await database.Roles.ToListAsync());
            database.Departments.RemoveRange(await database.Departments.ToListAsync());
            database.DirectoryTenants.RemoveRange(await database.DirectoryTenants.ToListAsync());
            await database.SaveChangesAsync();
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
