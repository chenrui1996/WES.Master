# T08 Task 1 — WMS quantity domain foundation

Date: 2026-10-26

## Status

Task 1 is complete for its isolated pure-domain scope. The new WMS project contains only the quantity contracts and has no runtime composition, persistence, authorization, messaging, endpoint, external-service, or UI integration. T08 overall remains incomplete, as do its T04/T05/T06 hard dependencies. This work does not close DTC-WMS-004, DTC-WMS-010, or DTC-WMS-011.

## Implemented

- Added `src/Modules/Wms/Wes.Wms.csproj` and included it in `WES.slnx`.
- Added a WMS project reference to `tests/Modules/Wes.Modules.Tests.csproj`; did not add WMS to the Host project or runtime assembly.
- Added the immutable pure-domain types in `src/Modules/Wms/Domain/InventoryQuantity.cs`:
  - `InventoryQuantity`: non-negative and exactly representable by `decimal(28,6)`; maximum `9999999999999999999999.999999`. Equality is numeric, so insignificant trailing zeroes are accepted. Exact addition/subtraction reject a negative or out-of-range result with `OverflowException`.
  - `QuantityPrecision`: explicit values 0..6, countable-unit default 0, divisible-unit default 3. Representability checks do not round and accept numerically insignificant trailing zeroes.
  - `StockMovementQuantity`: positive, at most `999999999.999999`, and exactly representable at the supplied precision. Negative movement values are not used to express direction.
- Added focused tests for legal minimum/maximum, negatives, zero, range/scale rejection, trailing zeroes, arithmetic and overflow, precision defaults/range/representability, and movement limits/precision.
- Updated the T08 implementation and test records, readiness register, work-package evidence, and requirement traceability. Parent DTC and T08 statuses remain incomplete.

## Test-first evidence

The tests and minimal project/test/solution wiring were added before the production domain types.

### Initial RED

Command:

```powershell
dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --filter FullyQualifiedName~InventoryQuantityTests
```

Result: expected failure, exit code 1. The empty WMS project built, then the test compilation failed because the domain namespace did not yet exist:

```text
InventoryQuantityTests.cs(1,11): error CS0234: 命名空间“Wes”中不存在类型或命名空间名“Wms”(是否缺少程序集引用?)
```

No production quantity types existed at this point.

While refining test data to avoid binary floating-point literals, the xUnit analyzer caught one mismatched inline argument type (`xUnit1010`); it was corrected to a string literal parsed as an invariant-culture decimal. No production code or expected contract changed, and the final focused rerun passed.

### GREEN and required verification

| Command | Result |
|---|---|
| `dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --filter FullyQualifiedName~InventoryQuantityTests` | Passed 16; failed 0; skipped 0. |
| `dotnet build src\Modules\Wms\Wes.Wms.csproj --configuration Release --no-restore` | Succeeded; 0 warnings; 0 errors. |
| `dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1` | Passed 900; failed 0; skipped 0. Architecture 29, Modules 287, Persistence 356, Route 228. |
| `dotnet format WES.slnx --no-restore --verify-no-changes --include src\Modules\Wms\Domain\InventoryQuantity.cs tests\Modules\Wms\InventoryQuantityTests.cs` | Passed; no formatting differences. |
| `git diff --check` | Passed; no whitespace errors. |

The focused tests and full solution tests ran in Release. The full solution test run was serial (`-m:1`).

## Scope exclusions and remaining concerns

- No database schema, EF Core mapping, migration, `DbContext`, or provider-specific code was introduced. MySQL `decimal(28,6)` storage/rounding/overflow and transaction/concurrency behavior remain unverified and must be tested in a later persistence slice.
- No Host runtime registration, tenant authorization, Outbox/Inbox, external services, API/endpoints, or UI were changed.
- Passing pure-domain tests do not represent complete DTC-WMS-004/010/011 acceptance, WMS ledger behavior, T08 completion, or completion of any hard dependency.
- No unresolved implementation issue was found within this task's isolated numerical contract.

## Documentation and traceability

- `docs/implementation/85-t08-quantity-domain-foundation.md`
- `docs/implementation/86-t08-quantity-domain-foundation-tests.md`
- `docs/implementation/readiness-register.md`
- `docs/superpowers/plans/2026-10-23-wes-development-work-packages.md`
- `docs/04-requirement-design-test-traceability.md`
