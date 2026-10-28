# Inbound Transport Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist the T09 WMS transport boundary and apply trusted final arrival exactly once without inventing a T15 transport executor.

**Architecture:** WMS owns immutable intent/outgoing envelope and completion Inbox. A required trusted transport adapter verifies/reconciles the Orchestration contract; no default adapter or completion HTTP is registered. Final synchronization composes the existing PostStock transaction and restores the existing inbound history.

**Tech Stack:** .NET 10, EF Core 10, MySQL, xUnit, existing Vue detail projection.

**Spec:** [179 design](../../implementation/179-t09-durable-inbound-transport-design.md)

## Global Constraints

- MySQL first; isolated wes_test only, no production connections or physical commands.
- Same-location receipts create no intent; historical primitive receipts are not silently adopted.
- Parent warehouse → sorted locations → container locks; exact execution identity and decimal quantity.
- Whole-container single-dimension dispatch; registered target dimension required for final posting.
- No default Verified/Ready adapter; no manual Completed HTTP endpoint.

## Execution closure

- [x] Tasks1–4 executed as one coupled transactional unit to keep intent, reservation, history, Inbox and migration invariants consistent.
- [x] Task5 evidence/docs/release gates completed; [actual results and independent review fix](../../implementation/180-t09-durable-inbound-transport-tests.md).
- [x] Final representation: existing `Transfer` posting; intent table stores fixed outgoing message/association, immutable linked history retains acceptance evidence; independent completion Inbox.
- [x] Verification: final transport72, correction regression268, final HTTP/browser89, architecture29, frontend1051/strict build; solution Release0/0, EF no drift, format and SQL review. Earlier533/192 overlap, not additive.

The unchecked items below preserve the original step checklist as planning history; execution status is the closure above and linked actual evidence, not unrun claims.

---

### Task 1: Durable intent and atomic protection

**Files:** create `src\Modules\Wms\Domain\InboundTransportIntent.cs`, `Infrastructure\Persistence\WmsDbContext.InboundTransport.cs`, `tests\Persistence\WmsMasterDataTests.InboundTransport.cs`; modify `InboundExecutionAccess.cs`, reservation validation, fixture cleanup.

**Interfaces:** consume newly staged Received fact/posting; produce `InboundTransportIntent` keyed by tenant/execution with fixed event/intent identity and original serialized contract.

- [ ] Write `InboundTransportReceiptStagesOneIntentAndInTransitProtection` asserting one intent, quantity5, protection Remaining5 and InTransit, source balance5, no Move.
- [ ] Run `dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~InboundTransport`; missing type must fail.
- [ ] Implement intent staging inside the public receipt Stage callback; stage the same-execution reservation/fact under the existing balance version fence. Guard writes against detached intent or malformed receipt protection.
- [ ] Test same-location transport0, original-key replay1, rollback0 and protected availability0.

### Task 2: Trusted contract dispatch and association

**Files:** create `src\Modules\Wms\Features\Inbound\InboundTransport.cs`, `InboundTransportAccess.cs`, `WmsInboundTransportWorker.cs`; extend intent model and tests.

**Interfaces:** `IInboundTransportAdapter.AcceptAsync(InboundTransportRequest, CancellationToken)` returns a verified `InboundTransportAcceptance`; `VerifyCompletionAsync(InboundTransportRequest, InboundTransportCompletion, CancellationToken)` returns explicit Verified/Rejected/Unavailable.

- [ ] Write accepted-response-loss test with a fixture adapter retaining one logical transport keyed by intent and accepting duplicate requests.
- [ ] Implement explicit `DispatchAsync(Guid executionId, CancellationToken)` loading durable original envelope; recheck whole-container stock/other execution protection, call adapter outside WMS transaction, persist exact association/history with version fence. Never generate a new intent on retry.
- [ ] Assert wrong intent/execution/transport/schema/tenant fails; replay returns original association without a second history transition. Expose no Host dispatcher by default.

### Task 3: Trusted final Inbox and unique movement

**Files:** create `Domain\InboundTransportInbox.cs`, `Features\Inbound\InboundTransportAccess.Completion.cs`; modify `InboundDocument.cs`, inbound persistence guards and shared pending helper.

**Interfaces:** `ApplyCompletionAsync(InboundTransportCompletion, CancellationToken)` returns Applied/Replayed/PendingSync/Rejected/Conflict; consume exact linked intent and registered source/target balance dimensions.

- [ ] Write source5/target0 final-arrival test expecting one Move/two entries, source0/target5, consumed protection, restored Arrived and Completed.
- [ ] Persist verified immutable completion before synchronization; missing target or extra stock returns PendingSync and retains protection.
- [ ] Compose existing PostStock with original execution, exact source/target versions, parent/resource guards, newly staged Arrived fact and Inbox Applied identity. Same event different body conflicts; another event cannot replace the original completion.
- [ ] Test duplicate/concurrent delivery, saved-then-failed rollback, cancellation, wrong location/evidence/producer/tenant, and PendingSync retry after registering target. Releasing pending requires durable arrival plus Applied Inbox/posting.

### Task 4: Compatibility and provider migration

**Files:** extend existing provider migration/snapshot using `dotnet ef migrations add WmsInboundTransport`; update affected real fixtures and prior execution assertions.

- [ ] Keep existing ExecutionId unique index receipt-only by putting transport references in typed payloads, not that column.
- [ ] Run targeted InboundTransport tests and old InboundExecution/ledger/reservation/LocationState regressions; preserve old main-data Outbox behavior and read contracts.
- [ ] Generate/review incremental SQL and Down; execute only fixture migration in wes_test.

### Task 5: Evidence, documentation and delivery

**Files:** add `docs\implementation\180-t09-durable-inbound-transport-tests.md`; update 179, README, readiness, TodoList, traceability and T09 plan.

- [ ] Run relevant HTTP/browser/read regression and architecture, Release build, EF no-drift, C# whitespace verification.
- [ ] Record actual evidence and unrun real Orchestration/Pack/provider boundaries, not assumed success.
- [ ] Verify `git diff --check`, new relative links and no temporary evidence staged; commit with Copilot trailer, push normal main and verify remote HEAD and clean worktree.
