# TransferOrder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver WMS multi-line atomic transfer approval and independently recoverable execution-unit posting.

**Architecture:** Reuse the unique WMS ledger and resource fences. Separate durable arrival evidence from its atomic ledger application; parent progress never rewinds committed units. HTTP and Vue are strict adapters, not transport authorities.

**Tech Stack:** .NET10/EF Core10/MySQL/xUnit, Vue3/TypeScript/Vitest/controlled HTTPS Chromium.

**Spec:** [185](../../implementation/185-t11-transfer-order.md)

## Global Constraints

- Only authorized wes_test and controlled local HTTPS; no production, physical controls or executing Down.
- Default-off TransfersEnabled; current multi-resource authority and immutable original receipts.
- Single-warehouse 1..100 lines, fixed same-container ledger tuple, whole single-positive-dimension container units; no implicit split or multi-location container.
- Real T15/Pack remains unavailable; transport lines cannot use human completion as substitute.
- No Generator/Builder, new dependencies or silent fallback. MySQL Guid sets use existing equality helper.

## Task 1: Durable owner, shared protection and strict HTTP

**Files:** create `src\Modules\Wms\Domain\Transfer*.cs`, `Features\Transfers\*.cs`, `WmsTransferWriter.cs`, `Infrastructure\Persistence\WmsDbContext.Transfers.cs`, migration/snapshot and Host transfer endpoints. Modify shared lock/protection callers and fixture cleanup/settings. Tests `tests\Persistence\WmsMasterDataTests.Transfers*.cs`, `tests\Modules\Platform\IdentityHttpTests.Transfers.cs`.

**Interfaces:** exactly the JSON/permissions/enums in185. Public writer uses MasterDataSubject and permanent operation/correlation; private EF types never escape. Add trusted internal completion contract only with explicit verification and no HTTP fake device success.

- [x] Write and run failing real-MySQL whole-order insufficient/rollback test; e.g. A3 available3 and B3 available2 => Conflict/Blocked, zero effective reservations, Draft retained.
- [x] Implement immutable plan/facts, strict dimensions/current permissions and atomic approval; verify same/different-key concurrency and no partial resource holds.
- [x] RED/GREEN A3/B1: dispatch A, durable arrival, ledger exactly once; B arrival persists before injected posting failure, A remains, B protection persists, sync B once then Completed.
- [x] Implement safe closure/Unknown with reservation and destination guards across existing writes; test raw entity/history mutation rejection.
- [x] Wire strict default-off HTTP, settings and exact original receipts; test all missing/duplicate/unknown fields, tenant/current authority and rollback after revocation.
- [x] Apply migration through isolated fixture only; run coupled owner/HTTP paths, record actual results in186. Keep .NET commands serial and coordinate browser startup with client build.

## Task 2: Named current-authorized transfer UI

**Files:** create `clients\ops-web\src\features\wms\transfers.ts`, `TransfersPage.vue`, `TransferConfirmation.vue`, contract/page tests; modify router/navigation tests.

**Interfaces:** create/detail/actions from185 verbatim. Full line cards and precise strings, no cross-unit total. Explicit source/target UUIDs and current version set.

- [x] RED contract import/parser tests with exact shape, quantities/states, malformed and unknown fields.
- [x] Implement create/read/approval/dispatch/arrival/sync/unknown/closure with frozen key/body/current context and current authority gates.
- [x] RED then GREEN parent clears detail after submit but Unknown survives; include 10-second/503/offline/late/identity/route transitions and no auto retry.
- [x] Test multi-line preview, scan mismatch, branch1 unsupported, no cancel for arrived/Unknown, explicit current GET; run focused tests, complete frontend and strict build.

## Task 3: Integrated release

**Files:** create controlled HTTPS browser fixture/client script; update186/README/readiness/traceability/contracts/TodoList/parent plan and applicable pitfalls entries.

- [x] Exercise real multi-line A3/B1 UI, durable arrival/sync and safe ClosedPartial variant, original201-response-loss200 recovery, actual ledger/reservation/protection assertions.
- [x] Review bounded diff and fix confirmed issues with focused regressions; no review report replaces execution.
- [x] Run related MySQL/HTTP/browser, architecture, solutionRelease0/0, EF model check and incremental SQL review, changed C# formatting and document links.
- [x] Record all unrun provider/physical/transport/multi-dimension scope explicitly; commit/push completed verified slice normally, remote==HEAD and clean worktree before reporting completion. Keep parent T11 incomplete if any primary software remains.
