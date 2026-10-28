# Blind Count and Local Lock Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver T11's blind count lifecycle and enforce its durable local lock across existing inventory writes.

**Architecture:** WMS owns permanent count facts, private snapshots and local locks. Reuse the unique stock ledger for atomic multi-line adjustments; all existing stock writers share the lock/fence. Host is a strict default-off adapter and Vue separates blind recording from authorized review.

**Tech Stack:** .NET10/EF Core10/MySQL/xUnit, Vue3/TypeScript/Vitest/HTTPS Chromium.

**Spec:** [183](../../implementation/183-t11-blind-count-and-local-lock.md)

**Execution closure:** The three scoped tasks are implemented and verified; see [184](../../implementation/184-t11-blind-count-tests.md) for the final MySQL923, HTTP/browser165, architecture29 and frontend1164 results, Release0/0, EF/SQL/format gates and review repairs. Initial backend-agent RED records are unavailable and are not claimed; later recorded RED/GREEN repairs and freshly executed integrated evidence are retained. TransferOrder and the parent T11 task remain open. Publication follows the existing explicit authorization to commit/push to main without removing this worktree.

## Global Constraints

- No production/physical controls; isolated wes_test only; Down SQL reviewed, not executed.
- Blind read never returns book quantity/difference/balance version. Review is separately authorized.
- Count locks never expire automatically; 1800 seconds only marks a reminder.
- Invalidating a count invalidates all measurements; restarting creates a fresh snapshot and new line identities.
- All quantity effects use the existing ledger in one transaction. Nonzero differences require a different actor.
- Scope is single-location plans, 1..100 lawful dimensions, no truncation; TransferOrder follows separately.

## Task 1: Durable owner, lock integration and HTTP

**Files:** create `src\Modules\Wms\Domain\StockCount*.cs`, `Features\Counts\*.cs`, `WmsCountWriter.cs`, `Infrastructure\Persistence\WmsDbContext.Counts.cs`, migration/snapshot and Host count endpoints; modify stock ledger/reservation/resource/operational guards to share count protection. Tests `tests\Persistence\WmsMasterDataTests.Counts*.cs`, `tests\Modules\Platform\IdentityHttpTests.Counts.cs` and settings/cleanup.

**Interfaces:** exactly the paths, shapes, permissions and enums in183; canonical command body excludes trusted actor/correlation, permanent receipts are id/version only.

- [x] Real MySQL count12/8/10 against10, reject self approval/non-counted lines and verify blind projections have no book values (initial backend RED record unavailable; no fabricated RED claim).
- [x] Implement immutable plan/facts/private snapshots, consistent history, concurrent scope lock/start and original tuple receipt.
- [x] Add atomic ledger adjustment composition without a second ledger writer, and specific count-owned exemption tied to current plan/generation.
- [x] Wire all relevant stock writers under resource fences; source/target movement/reservation/new-dimension tests pass with no unsafe bypass. Verify original committed replays still work (initial backend RED record unavailable).
- [x] Cover1800-second boundary/no unlock, invalidation/recount, stale versions, second actor, rollback/revocation/concurrency and cross-tenant/resource creator permission.
- [x] Implement strict default-off HTTP/settings, both blind and review shapes, all operation gates/keys/body errors and current permission rechecks.
- [x] Migrate onlywes_test, review incremental SQL, run focused owner/HTTP and coupled regression. No commits until integrated review.

## Task 2: Named blind count page

**Files:** create `clients\ops-web\src\features\wms\counts.ts`, `CountsPage.vue`, `CountConfirmation.vue`, tests; modify routes/navigation.

**Interfaces:** consumes183 shapes verbatim. Existing identity-api/resource field gating and QueryState conventions; no schema/permission fallback.

- [x] RED strict parsers reject book values on blind response and malformed counts, zero valid, positive/negative review string exactness.
- [x] Implement create/start/record/review/adjust/invalidate, private review cleanup, complete preview/frozen original tuple and explicit current GET.
- [x] Test stale/offline/late/subject/route changes and Unknown same-key recovery; do not auto retry or persist write inputs.
- [x] Focused and complete frontend tests/strict build; no dist rebuild while browser runs.

## Task 3: Integration and release

**Files:** `tests\Modules\Platform\IdentityHttpTests.CountsBrowser.cs`, client Playwright, `docs\implementation\184-t11-blind-count-tests.md`, directly related canonical traceability/readiness/README/TodoList and parent plan.

- [x] Real HTTPS two-actor count/review/adjust original-key response-loss recovery, actual Qty/ledger/lock assertions; keyboard/narrow/200% checks.
- [x] Independent bounded spec/quality review; fix confirmed blockers and focused follow-up, with recorded RED/GREEN evidence where available.
- [x] Final related MySQL/HTTP/browser, architecture, solutionRelease0/0, EF no drift, changed-file formatting/SQL/relative links (seven unchanged external baseline targets unavailable in this worktree; no newly broken links).
- [x] Record real results/remainingTransferOrder and advanced scope; release through normal authorized commit/push to existing main, with remote==HEAD/worktree-clean verification before reporting completion.
