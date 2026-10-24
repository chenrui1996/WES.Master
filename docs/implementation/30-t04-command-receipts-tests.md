# T04 directory command receipt test record

- Status: owner persistence primitives verified; HTTP integration and T04 overall remain incomplete.
- Scope: durable receipt idempotency, replay authorization, concurrency, expiry, and atomic persistence behavior.
- Test database: the authorized isolated MySQL database `wes_test`; generated tenant fixtures only.

## Result

```powershell
dotnet test tests\DirectoryReceipts\Wes.DirectoryReceipts.Tests.csproj --no-restore --filter "FullyQualifiedName~DirectoryReceiptTests" --verbosity quiet
```

Result: **31 passed, 0 failed, 0 skipped**.

Coverage includes stable same-key replay, kind/fingerprint conflicts, current authorization and target checks, tenant/actor isolation, concurrent duplicate submissions, rollback/cancellation, expiry, credential exclusion, and migration/model boundaries.

The dedicated project links the same receipt tests and authorized database fixture. It is intentionally outside the solution test count because the parent persistence test project is independently retained and validated.

## Boundary

This verifies the owner persistence primitives only. It does not claim that every directory, role, or settings HTTP command is connected to receipts, nor that the bounded cleanup operation is scheduled in production. The conservative membership replay behavior and remaining integration contract are documented in [the implementation record](23-t04-command-receipts.md).
