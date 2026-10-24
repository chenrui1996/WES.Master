# T03 authenticated identity HTTP test record

- Status: verified software slice; T03 overall remains incomplete.
- Scope: identity HTTP contracts, rejection boundaries, local bootstrap, persistent key ring, and the controlled real-browser acceptance.
- Test database: the authorized isolated MySQL database `wes_test`; generated identities, keys, and synthetic breach corpus only.

## Results

### Identity HTTP and local provisioning

```powershell
dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~IdentityHttpTests|FullyQualifiedName~IdentityRejectionHttpTests|FullyQualifiedName~LocalProvisioningTests" --logger "console;verbosity=quiet"
```

Result: **66 passed, 0 failed, 0 skipped**.

### Real-browser acceptance

```powershell
dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages"
```

Result: **1 passed**. Chromium exercised HTTPS Kestrel, identity, named pages, user/credential/role writes, and logout against `wes_test`.

### Full solution regression

```powershell
dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1
```

Result: **790 passed, 0 failed, 0 skipped**.

The browser test is also included in the 66-test identity filter. The isolated browser rerun is reported separately to make the end-to-end result explicit.

## Boundary

The test corpus is synthetic and does not certify real breach coverage. Passing tests do not approve production secrets, production key-ring ACL/backup, DBA migration, trusted proxy topology, production retention/capacity, or the separate operational and on-site H gates. No test changes those gates or permits business/device control.
