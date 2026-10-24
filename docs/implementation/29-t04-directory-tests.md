# T04 directory owner test record

- Status: verified directory persistence and current integration slice; T04 overall remains incomplete.
- Scope: directory persistence/migrations and affected authorization, disable, and login-event regressions.
- Test database: the authorized isolated MySQL database `wes_test`; generated tenant fixtures only.

## Results

### Directory and related persistence matrix

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~DirectoryPersistenceTests|FullyQualifiedName~DirectoryMigrationTests|FullyQualifiedName~PlatformAccessPersistenceTests|FullyQualifiedName~LoginEventQueryPersistenceTests|FullyQualifiedName~DisableAccountPersistenceTests" --logger "console;verbosity=quiet"
```

Result: **106 passed, 0 failed, 0 skipped**.

### Real-browser named-page/write acceptance

```powershell
dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages"
```

Result: **1 passed**; verified user creation, separate credential creation, membership update, and logout through HTTPS.

The persistence cases cover field/scope filtering in SQL, authoritative department ownership, canonical account uniqueness, eligibility, concurrency, dictionary identity/reference behavior, cancellation, and rollback. Browser acceptance is backed by the separately recorded client tests in [the operations-client test record](31-t03-t04-operations-client-tests.md).

## Boundary

Evidence is MySQL-only and does not approve production migrations or other EF providers. T04 still has outstanding integration and release obligations; in particular, receipt support is not wired to every eligible write route, and no result here certifies production authorization policy, operational ownership, or on-site H gates.
