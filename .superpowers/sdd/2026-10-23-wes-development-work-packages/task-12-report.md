# Task 12 Report

**Status: DONE_WITH_CONCERNS** — T12a owner-local persistence slice is implemented and verified. T12 and its parent DTCs remain open.

- **Commit hashes:** `c5c01a7` (`feat(workflow): persist immutable definition snapshots`); no push.
- **Tests summary:** Real MySQL T12a persistence **9/9 passed**; validator regression **113/113**, architecture **29/29**, full Release build **0 warnings/0 errors**, migration model check and formatting passed.
- **Concerns:** Workflow management permission/field mapping and current file-reference authorization adapter remain unresolved, so this slice exposes no authorized management API, HTTP endpoints, Host registration, or Vue UI. Do not treat parent DTC-WF-001/008 or T12 as complete.
- **Report path:** `.superpowers\sdd\2026-10-23-wes-development-work-packages\task-12-report.md`

## Scope and implementation

Implemented only the bounded T12a owner-local persistence slice in response to the Controller ruling. The ruling correctly identifies the generic [MutationContext/operation identity contract](../../../docs/contracts/01-business-and-execution-contracts.md): tenant+owner+operation scope, same-payload replay, payload mismatch 409, expected-version conflict 412; `Audit.Append` means the business owner persists the fact/reference in its own transaction while Platform provides read/index. These are not blockers, and the prior claims to that effect were corrected in records 189/190 and project status documentation.

The WorkflowForm owner now has:

- Explicit tenant-scoped EF/MySQL `WorkflowFormDbContext`, migration history `__WesWorkflowFormMigrations`, and `wf_*` tables for schema/definition drafts, published versions, operation receipts, and audit facts.
- Opaque draft version tokens with expectedVersion checks and EF optimistic concurrency; conflicting/stale requests return a conflict result without audit or mutation.
- Separate immutable published snapshots with monotonic per-resource version numbers. A definition snapshot pins an exact published schema ID/version; publishing later schema/definition versions does not rewrite prior rows.
- Tenant+WorkflowForm owner+operationId receipts, body/target/expectedVersion fingerprinting, exact-payload replay and payload mismatch behavior. Correlation is excluded from fingerprint as transport context.
- Workflow-owned append-only audit facts and operation receipts committed in the same local transaction as each successful draft/publish mutation. Failed/conflicting mutations add neither.
- Internal persistence methods only. No management permission grants, client trust, Platform private-table access, file-reference authorization, endpoint, Host/DI registration, deployment switch, or UI was added. The internal definition publish method takes a compiled snapshot from the existing pure validator; there is no externally reachable candidate or permission assertion path.

## Remaining gated surfaces

The unresolved gates are the approved Workflow management permission/field mapping and the current file-reference authorization adapter for Workflow form data. Consequently there is no management read/write API, strict HTTP input mapping, default-disabled endpoint deployment registration, or `DefinitionsPage.vue`/`FormsPage.vue`. Cross-tenant file authorization and current authorized management behavior remain unverified. These gaps keep all DTC parent cases and the overall T12 open. T13 instances, approvals, inbox, and hooks were not started.

Concrete inspected evidence is recorded in 189:

- `docs/contracts/01-business-and-execution-contracts.md` §2 specifies MutationContext, operation scope, expectedVersion, payload mismatch and replay; §3 specifies owner-local Audit.Append.
- `src/Modules/Platform/PlatformScope.cs` exposes current authorization projections, but no approved Workflow management permission/field map was found.
- `src/Modules/Platform/PlatformScope.Files.cs` exposes business-file operations requiring a concrete entity-owner adapter; no current Workflow form file-reference authorization adapter is present.
- T12 detailed design and approved rules do not supply these Workflow-specific authorization bindings.

## Commands and actual results

Initial RED before persistence implementation:

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --filter "FullyQualifiedName~WorkflowFormPersistenceTests"
```

Result: compile failure because new test references to the WorkflowForm persistence namespace/store did not exist yet. This was the intended initial RED.

Final focused persistence verification against the real configured MySQL fixture (`wes_test`; fixture ran `Database.MigrateAsync()`):

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-build --filter "FullyQualifiedName~WorkflowFormPersistenceTests" --logger "console;verbosity=quiet"
```

Result: **9 passed, 0 failed, 0 skipped**. Covers immutable schema S1/S2 and definition/schema history, expectedVersion conflict, operation replay/mismatch, tenant isolation, concurrent publication, concurrent same-operation save, audit/version mutation rejection, and transaction rollback after a post-save interruption.

Pure validator regression:

```powershell
dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-build --filter "FullyQualifiedName~DefinitionValidationTests" --logger "console;verbosity=quiet"
```

Result: **113 passed, 0 failed, 0 skipped**.

Architecture:

```powershell
dotnet test tests\Architecture\Wes.Architecture.Tests.csproj --configuration Release --no-build --logger "console;verbosity=quiet"
```

Result: **29 passed, 0 failed, 0 skipped**.

Migration/model consistency:

```powershell
dotnet ef migrations has-pending-model-changes --project src\Modules\WorkflowForm\Wes.WorkflowForm.csproj --startup-project src\Modules\WorkflowForm\Wes.WorkflowForm.csproj --configuration Release
```

Result: `No changes have been made to the model since the last migration.`

Formatting and full build:

```powershell
dotnet format WES.slnx whitespace --verify-no-changes --no-restore --include src\Modules\WorkflowForm\Features\Definitions\WorkflowDefinitionStore.cs src\Modules\WorkflowForm\Infrastructure\Persistence\WorkflowFormDbContext.cs src\Modules\WorkflowForm\Infrastructure\Persistence\WorkflowFormEntities.cs src\Modules\WorkflowForm\Infrastructure\Persistence\WorkflowFormDesignTimeFactory.cs src\Modules\WorkflowForm\Infrastructure\Persistence\Migrations\20261101082426_WorkflowFormDefinitions.cs src\Modules\WorkflowForm\Infrastructure\Persistence\Migrations\20261101082426_WorkflowFormDefinitions.Designer.cs src\Modules\WorkflowForm\Infrastructure\Persistence\Migrations\WorkflowFormDbContextModelSnapshot.cs tests\Persistence\WorkflowFormPersistenceTests.cs
dotnet build WES.slnx --configuration Release --no-restore
git diff --check
```

Results: format verification passed after applying `dotnet format WES.slnx whitespace --no-restore --include ...`; Release build **0 warnings, 0 errors**; `git diff --check` passed.

Development RED/GREEN notes and final command outputs: `docs/implementation/190-t12-management-surface-blocker-tests.md`. One intermediate attempt to run build and tests simultaneously produced transient MSBuild file-lock warnings; the subsequent standalone final solution build completed with 0 warnings and 0 errors.

No HTTP/client/browser tests were run because those surfaces were intentionally not implemented. No production database was accessed, no migrations were applied outside the authorized test fixture, and no credentials were read or reported.

## Files changed

- Persistence implementation: `src/Modules/WorkflowForm/Features/Definitions/WorkflowDefinitionStore.cs`; `src/Modules/WorkflowForm/Infrastructure/Persistence/{WorkflowFormDbContext.cs,WorkflowFormEntities.cs,WorkflowFormDesignTimeFactory.cs,Migrations/*}`; WorkflowForm project package references/lock; persistence test project reference/lock.
- Tests: `tests/Persistence/WorkflowFormPersistenceTests.cs`; assembly internals visibility for the real-MySQL persistence test assembly.
- Evidence/design/readiness/trace: `docs/implementation/189-t12-management-surface-blocker.md`, `190-t12-management-surface-blocker-tests.md`, `docs/detailed-design/workflow-and-forms.md`, `docs/implementation/readiness-register.md`, `docs/04-requirement-design-test-traceability.md`, and the T12 section in `docs/superpowers/plans/2026-10-23-wes-development-work-packages.md`.
- This report.

## Self-review and unverified acceptance

- Published schema/definition rows, audit facts, and operation receipts reject tracked modification/deletion through the owner DbContext. Draft replacement/deletion paths require matching same-transaction operation and audit identities.
- Real MySQL tests verify durable history, concurrency and operation recovery behavior; no InMemory substitute was used.
- Still unverified/out of scope for this bounded slice: the full task’s candidate/authorization adapter composition, cross-tenant authorized file checks, strict HTTP validation, authenticated endpoints, disabled-by-default Host registration, the two Vue pages and their loading/empty/validation/unauthorized/conflict/error states, and full DTC-WF-001/008. No status claim closes these parent items.
- T13 remains untouched.

## Fix round 1 — Guard every SaveChanges overload

**Status: FIXED AND VERIFIED.** This fix remains within T12a persistence; it does not change the T12/T13 scope or gated management surfaces.

`WorkflowFormDbContext` now routes the parameterless synchronous/asynchronous methods through the `acceptAllChangesOnSuccess` overloads. Both bool overloads invoke `ValidateOwnerWrites()` before delegating to EF Core, so the published-snapshot immutability and draft/audit-consistency guards run for every supported save entry point.

The focused MySQL regression theories exercise both protected write invariants through each entry point: `SaveChanges()`, `SaveChanges(bool)` with `false` and `true`, `SaveChangesAsync(CancellationToken)`, and `SaveChangesAsync(bool, CancellationToken)` with `false` and `true`. The fixture remains the configured real-MySQL fixture; no InMemory provider was substituted.

### Commands and results

- RED reproduction before the production fix:
  `dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --filter "FullyQualifiedName~WorkflowFormPersistenceTests.SaveChangesOverloadsEnforcePublishedAndDraftAuditGuards|FullyQualifiedName~WorkflowFormPersistenceTests.SaveChangesAsyncOverloadsEnforcePublishedAndDraftAuditGuards" --logger "console;verbosity=quiet"`
  **Failed as expected:** 2 failed, 2 passed, 4 total. The explicit `true` bool-overload cases bypassed the guards. The final regression data also explicitly covers `false` and each default overload.
- Formatting:
  `dotnet format whitespace src\Modules\WorkflowForm\Wes.WorkflowForm.csproj --no-restore --verbosity quiet`
  `dotnet format whitespace tests\Persistence\Wes.Persistence.Tests.csproj --no-restore --verbosity quiet`
  **Passed** (exit code 0).
- Final required real-MySQL test command:
  `dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --filter "FullyQualifiedName~WorkflowFormPersistenceTests"`
  **Passed:** 15 passed, 0 failed, 0 skipped; 15 total (Release, `net10.0`; 7 seconds).
- `git diff --check`: **passed** (exit code 0).

### Fix-round files and review

- `src/Modules/WorkflowForm/Infrastructure/Persistence/WorkflowFormDbContext.cs`: all four EF save overloads now converge on a single guarded implementation for their respective sync/async path; validation runs once per save call.
- `tests/Persistence/WorkflowFormPersistenceTests.cs`: targeted MySQL tests assert both snapshot immutability and draft/audit consistency fail closed via every overload, including both bool values.

No HTTP/UI/authorization work was added. The management permission mapping and current file-access adapter remain gates; T12 and parent DTCs remain open. No push was made.
