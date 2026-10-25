# T04 directory command receipt test record

- Status: owner persistence primitives and named HTTP write integration verified; T04 overall remains incomplete.
- Scope: durable receipt idempotency, replay authorization, concurrency, expiry, atomic persistence, and HTTP key/replay behavior.
- Test database: the authorized isolated MySQL database `wes_test`; generated tenant fixtures only.

## Result

```powershell
dotnet test tests\DirectoryReceipts\Wes.DirectoryReceipts.Tests.csproj --no-restore --filter "FullyQualifiedName~DirectoryReceiptTests" --verbosity quiet
```

Result: **31 passed, 0 failed, 0 skipped**.

Coverage includes stable same-key replay, kind/fingerprint conflicts, current authorization and target checks, tenant/actor isolation, concurrent duplicate submissions, rollback/cancellation, expiry, credential exclusion, migration/model boundaries, denial when delegation over a removed membership is revoked, and successful replay for a partially delegable administrator despite unrelated tenant roles.

The named HTTP routes are also verified with:

```powershell
dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~NamedPlatformWritesReplayOriginalSafeResultAndDetectChangedRequests|FullyQualifiedName~MissingOrInvalidOperationKeyCannotMutateDirectory" --logger "console;verbosity=quiet"
```

Result: **6 passed, 0 failed, 0 skipped** across user, role, and settings replay, conflict, current-authorization, and missing/invalid-key cases.

The dedicated project links the same receipt tests and authorized database fixture. It is intentionally outside the solution test count because the parent persistence test project is independently retained and validated.

## Boundary

Named directory writes use the operation key; role and settings handlers use the same receipt transaction contract, while credential writes explicitly reject receipt keys. The bounded cleanup operation is not scheduled in production. Membership replay stores only the IDs of affected roles and rechecks their current rules; it does not store historical grants or consult unrelated roles. Remaining deployment boundaries are documented in [the implementation record](23-t04-command-receipts.md).
