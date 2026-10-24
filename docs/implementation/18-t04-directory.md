# T04 durable directory owner slice

- Status: 实施中; owner Platform; REQ-PLAT-004..009, FUNC-PLAT-002..004, DTC-PLAT-004..006/008/011/014.
- Extends existing RBAC, credentials, typed settings and append-only audit. No generated CRUD, default credentials, or production database access.
- Directory users have immutable normalized accounts and explicit department ownership. Creating a user never creates credentials. Credential creation is a separately authorized operation using the existing approved codec and trusted breach evidence.
- Current authorization requires an enabled tenant and directory subject; department resource authorization resolves current directory ownership, never a request department. Each result projects only fields permitted for that individual resource.
- Department moves require authority over both old and new scopes. Role assignment/removal requires authority over the target user and delegation of both previous and requested grants; user versions fence concurrent membership edits.
- Tenant switching returns an eligibility decision, not a trusted context or token. The same immutable subject must be enabled in both tenants. Transport must obtain fresh target authentication, authorization and clear prior UI state; a supplied target ID is not authentication.
- Dictionary stable codes are immutable. Explicit durable references prevent removal; translations fall back requested language → configured default → diagnostic key, never substitute business state codes.
- Multi-write commands use explicit transactions, optimistic versions, cancellation and one append-only audit per owner transition. Rejected/conflicting writes disclose no version. There is no generic audit edit/delete.
- Migration is additive; existing credential/session rows do not silently become directory identities. Explicit reviewed local provisioning must create tenant, department, subject, role and membership before first authentication. No seeded administrator.

## Verification plan

`DirectoryPersistenceTests` uses only `MySqlTestConnection.LoadAuthorizedTestConnection()` and generated tenant fixtures in `wes_test`. Tests cover separation of user/credential creation, current directory eligibility, field and department isolation, delegation, optimistic writes, tenant switching, fallback/reference protection, and audit rollback. Existing authorization regression tests must explicitly provision eligible directory subjects. Provider evidence is MySQL only; other providers, HTTP contracts and UI acceptance remain outside this owner slice. T04 as a whole remains incomplete.

## Public owner contracts for Host integration

Namespace `Wes.Platform.Features.Directory`; instantiate `DirectoryService(PlatformDbContext, TimeProvider)` for the trusted tenant. All methods below accept a final optional `CancellationToken`; all writes take a nonempty correlation UUID, return `DirectoryWriteResult`, clear their owned tracking state and require a clean context. `DirectoryDecision` distinguishes Changed, Forbidden, VersionRequired, VersionConflict, Duplicate, Referenced, PasswordRejected. Denied/conflicting results disclose neither ID nor version.

```csharp
CreateUserAsync(TenantContext context, string account, string name, Guid departmentId, Guid correlation, CancellationToken ct)
ChangeUserAsync(TenantContext context, Guid userId, string? expectedVersion, string name, Guid departmentId, bool enabled, Guid correlation, CancellationToken ct)
CreateCredentialAsync(TenantContext context, Guid userId, string? expectedVersion, string password, PasswordEvidence breachEvidence, Guid correlation, CancellationToken ct)
ChangeMembershipsAsync(TenantContext context, Guid userId, string? expectedVersion, IEnumerable<Guid> roleIds, Guid correlation, CancellationToken ct)
CreateRoleAsync(TenantContext context, string code, IEnumerable<PermissionGrant> rules, Guid correlation, CancellationToken ct)
CreateDepartmentAsync(TenantContext context, string code, string name, Guid correlation, CancellationToken ct)
ChangeDepartmentAsync(TenantContext context, Guid departmentId, string? expectedVersion, string name, bool enabled, Guid correlation, CancellationToken ct)
ChangeTenantAsync(TenantContext context, string? expectedVersion, string name, bool enabled, string defaultLanguage, Guid correlation, CancellationToken ct)
CreateDictionaryAsync(TenantContext context, string category, string code, string languageKey, Guid correlation, CancellationToken ct)
ChangeTranslationAsync(TenantContext context, Guid entryId, string? expectedVersion, string language, string label, Guid correlation, CancellationToken ct)
ChangeDictionaryReferenceAsync(TenantContext context, Guid entryId, string? expectedVersion, Guid referenceId, bool referenced, Guid correlation, CancellationToken ct)
DeleteDictionaryAsync(TenantContext context, Guid entryId, string? expectedVersion, Guid correlation, CancellationToken ct)
ReadUsersAsync(TenantContext context, UserDirectoryFilter filter, CancellationToken ct)
ReadDepartmentsAsync(TenantContext context, int offset, int limit, CancellationToken ct)
ReadRolesAsync(TenantContext context, int offset, int limit, CancellationToken ct)
ReadTenantsAsync(TenantContext context, int offset, int limit, CancellationToken ct)
CheckTenantSwitchAsync(TenantContext context, Guid targetTenantId, CancellationToken ct)
ReadDictionaryAsync(TenantContext context, string category, string language, int offset, int limit, CancellationToken ct)
```

Queries return `DirectoryPage<...Row>` with `Authorized` and immutable field-projected `Rows`; row/filter schemas are defined with XML docs in `DirectoryContracts.cs`. Permission names are `platform.users`, `departments`, `roles`, `tenants`, `dictionary` suffixed `.read`/`.write`; switching is `platform.tenants.switch` with `Id`. Projection field names match row names except user `Roles` → `RoleIds`, dictionary `Label` also governs resolved-language/missing metadata. Required write fields are explicit in each named command; creating users requires Account/Name/DepartmentId/Enabled, credentials require Credential, memberships require Roles. Existing `ChangeRoleHandler`, settings/audit and identity handlers remain unchanged.

`Wes.Platform.Features.Identity.AccountName.Normalize(string)` returns `Trim().Normalize(FormKC).ToUpperInvariant()` with 1..128 Unicode scalar/no-control validation; never normalize passwords. `DirectoryEligibility(PlatformDbContext).IsEligibleAsync(Guid userId, ct)` requires current enabled tenant/user/department/credential. `IsDirectoryEligibleAsync` requires current enabled directory facts and vetoes any disabled credential; suitable for current session checks. No caller-supplied department can authorize a user write.

Breach evidence is obtained only from the trusted local owner service, not a request DTO. Password requests/responses must not be logged or echoed. Target-tenant eligibility is not a token grant: target authentication/session issuance belongs to T03.

### Explicit local bootstrap

`DirectoryBootstrap(PlatformDbContext, TimeProvider).ProvisionAsync(bool localProvisioningEnabled, Guid userId, string tenantName, string defaultLanguage, string departmentCode, string departmentName, string account, string displayName, string password, PasswordEvidence breachEvidence, string roleCode, IEnumerable<PermissionGrant> reviewedGrants, Guid correlation, CancellationToken ct)` returns `DirectoryWriteResult`. This is a trusted **local process** service, not HTTP; no registration/map extension exposes it. The invoking CLI must obtain the explicit gate from controlled local configuration and password from non-echoing secret input, and obtain authoritative local breach evidence before calling. No supplied `true` flag from an HTTP request is authorization.

The scoped tenant must have no directory/security subject state (tenant, user, credential, role, membership, session). Provisioning cannot adopt legacy/imported security rows or replace an existing administrator. It atomically creates explicit tenant/department/user/credential/role/membership and six audit facts, no session. A repeat is denied. The password, subject, grants, names and codes have no built-in defaults; default locale must be supplied explicitly. Restore/recovery/re-enabling a security-disabled credential is not bootstrap.

## Verified evidence (2026-10-24)

- Migration `20261024043705_DirectoryOwner` is additive: six owner tables, tenant-composite restrictive relations, unique account/code indexes and optimistic versions. Existing security rows are unchanged; directory absence fails closed. Review test rejects SQL/database-alter/destructive operations. Applied only using authorized `wes_test` test helper.
- Actual behavior reds before implementation: `CurrentAuthorization_MissingDirectoryIdentity_FailsClosed` failed (1); `CreateUser_PersistsDirectoryOnlyAndAudit_RejectsCanonicalDuplicate` failed (1); `ReadUsers_ProjectsFieldsPerResource_AndUsesAuthoritativeDepartment` failed (1); `Dictionary_CreateStableIdentity_IsAtomicWithAudit` failed (1); explicit bootstrap test failed (1). They subsequently passed. Compile errors during intermediate edits were not counted as behavior red evidence.
- Final scoped command:

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~DirectoryPersistenceTests|FullyQualifiedName~DirectoryMigrationTests|FullyQualifiedName~PlatformAccessPersistenceTests|FullyQualifiedName~LoginEventQueryPersistenceTests|FullyQualifiedName~DisableAccountPersistenceTests" --logger "console;verbosity=quiet"
```

Result: **106 passed, 0 failed, 0 skipped**. This includes directory behavior variants plus the additive migration review and affected current-authorization/disable/audit query regressions. Tests verify SQL-authorized field filters (not just response hiding), old/new department scope, authoritative user department metadata, Unicode canonical uniqueness, independent credential evidence, current disabled tenant/department, one-winner actual concurrent membership saves, safe tenant-switch eligibility, stable dictionary fallback/reference/delete, cancellation, and rollback after actual SQL writes before commit. Existing fixtures explicitly provision directory identities; they do not create default credentials. See the standalone [T04 directory test record](29-t04-directory-tests.md).
- An Oracle MySQL EF 10 primitive Guid-collection binding failure was resolved by typed OR/equality expression trees: scope filtering remains in SQL before paging, not post-fetch filtering.

## Bounded gaps and integration obligations

The directory owner and its additive migrations are implemented. Host endpoints, named client pages, and controlled browser acceptance are now integrated; see the [client implementation](20-t03-t04-operations-client.md) and [test record](29-t04-directory-tests.md). **T04 as a whole is not complete.** Directory departments remain a flat durable code/name/state directory; hierarchical organization/reparenting is not silently invented. This slice does not expose cross-tenant administration, public/shared tenant data, role/user hard-delete, account rename, or security-disabled credential re-enable. SQL Server/PostgreSQL remain deferred. Dictionary usage protection requires business owners to register/release durable references through the explicit command; cross-module private-table discovery is forbidden. Command receipt primitives are not wired to every eligible HTTP command; see [receipt implementation](23-t04-command-receipts.md) and [receipt tests](30-t04-command-receipts-tests.md). Production migrations remain DBA-controlled.
