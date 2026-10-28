# Outbound Basic Flow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver T10's currently authorized direct shipment flow from draft through reservations, picking, shortage review and shipment.

**Architecture:** WMS owns permanent operation facts and all stock changes. Reuse reservation and PostStock semantics with atomic multi-line approval; Host maps strictly typed default-off endpoints. Vue presents explicit named actions and preserves Unknown operation identity.

**Tech Stack:** .NET10, EF Core10/MySQL, xUnit, Vue3/TypeScript/Vitest/HTTPS Chromium.

**Spec:** [181](../../implementation/181-t10-outbound-basic-flow.md)

## Execution closure

The basic software slice is implemented and verified; [182 evidence](../../implementation/182-t10-outbound-basic-flow-tests.md) supersedes planned counts. All three tasks below delivered their scoped output: owner and strict default-off HTTP; named outbound/picking pages and original Unknown recovery; independent review and integrated gates. Five review findings received regression fixes and follow-up review with no significant remaining issues. Final MySQL392, HTTP/settings/real HTTPS browser120, architecture29, frontend1107 and strict build passed; solution Release0/0, EF no drift and changed-file formatting passed. Initial format failures were corrected with the existing formatter only.

The original step checklists below are historical execution instructions, not outstanding work in this basic slice. Full FEFO/expiry, automatic FIFO source allocation, repeated partial shipments and actual T15 outbound transport remain explicit software dependencies in TodoList; the parent T10 and entire project are not marked complete. Continue with T11's basic scope, without weakening those dependencies or safety boundaries.

## Global Constraints

- MySQL isolated wes_test only; no production/physical controls.
- Exact decimal strings, current complete creator-based permission checks and permanent original operation receipts.
- No nested independently committed reservation/ledger commands; all-row approval atomic.
- Picking is not shipment; shortage does not automatically release stock; picked/inTransit/Unknown cannot be canceled as unexecuted.
- Transport branch never manual shipment; missing real transport is not success.

## Task 1: Durable outbound owner and HTTP

**Files:** create `src\Modules\Wms\Domain\Outbound*.cs`, `Features\Outbound\*.cs`, `WmsOutbound*.cs`, `Infrastructure\Persistence\WmsDbContext.Outbound.cs`, migration/snapshot; `src\Host\Wms\WmsMasterDataEndpoints.Outbound.cs`, settings/map. Tests `tests\Persistence\WmsMasterDataTests.Outbound*.cs`, `tests\Modules\Platform\IdentityHttpTests.Outbound.cs`, settings tests.

**Interfaces:** exactly the seven paths/body/result/read shapes in design181; quantity0..6 strict string, branch0/1/state0..5. Current subject IMasterDataAuthorization with design fields.

- [ ] RED `OutboundApprovalIsAllOrNothing` with Qty10 two plans7 and two-line insufficient variants.
- [ ] Implement immutable plan/facts/receipt guards, real legal resource/Enabled/schema/current authorization and atomic multi-row reservation using existing domain and save validation.
- [ ] Implement pick5/short2→review-release2→ship5 retaining plan7/open2→close ClosedPartial, cross-actor review and no manual transport shipment.
- [ ] Test real concurrent approvals, exact replay/different body conflicts, post-save revocation/cancellation/provider rollback, cross-tenant/creator and protected stock.
- [ ] Add strict default-off HTTP/settings tests and migration. Run targeted + existing ledger/reservation/inbound transport regression; format and inspect SQL. No commits until integrated review.

## Task 2: Named outbound and picking pages

**Files:** create `clients\ops-web\src\features\wms\outbound.ts`, `OutboundPage.vue`, `OutboundPage.spec.ts`; modify routes/navigation/local permission declarations.

**Interfaces:** consume design181 REST contracts verbatim; route both outbound and picking to named workflow with appropriate title/purpose. No private backend entities.

- [ ] RED no action before preflight/preview, exact full multi-row request and no request during preview.
- [ ] Strict typed parsers/BigInt validation and permanent frozen tuple; manual recovery after Unknown with no auto resend/cache.
- [ ] Existing shared confirmation components/layout as appropriate; fresh explicit details GET and proper stale/unauthorized/offline states. Preserve execution panel independent of detail read state.
- [ ] Test Draft→Approve→Pick/Shortage→Ship and Transport disabled state; cross-subject/late response cleanup, keyboard/narrow screen affordances. Run focused, then full frontend tests and strict build.

## Task 3: Integration evidence and delivery

**Files:** `docs\implementation\182-t10-outbound-basic-flow-tests.md`, directly related canonical traceability/readiness/README/TodoList and parent plan.

- [ ] Run real HTTPS browser for new flow including original-key response-loss recovery; exact ledger/quantity/operation assertions.
- [ ] Independent spec/quality review and fix high-confidence issues with focused regression tests.
- [ ] Architecture, Release0/0, EF no drift, changed C# formatting and incremental SQL review. Register actual evidence, unrun FEFO/transport/provider boundaries.
- [ ] Normal commit/push with required trailer and remote HEAD/clean-worktree check; do not mark whole project complete.
