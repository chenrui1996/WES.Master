# T14 alarm console implementation plan

> **For agentic workers:** Use executing-plans to implement the tasks in dependency order. Preserve owner boundaries and test every behavior before claiming delivery.

**Goal:** Implement durable alarm notification aggregation, current authorization, independent delivery and two operational pages.

**Architecture:** Notifications owns facts, aggregates, memberships, operations, audits, deliveries and attempts. Host is a thin authenticated/default-disabled adapter to Platform authority. Vue uses public projections and explicit recovery, never EF entities or source-auth assertions.

**Tech Stack:** .NET 10, EF/MySQL existing locked versions, Vue 3/TypeScript/Vitest; no new external messaging dependency required for the durable in-app channel.

**Spec:** [193](../../implementation/193-t14-alarm-console-design.md).

## Global constraints

Read README, readiness register, canonical Notifications rules, engineering governance and INF-005/009/GEN-002/004/006 before changes. Restore only changed manifests or missing packages. No physical success fallbacks or production connections. Default-false HTTP. Every occurrence immutable; no test transport in production composition. Manual ingress is actor-owned, device ingress remains internal/trusted.

### Task 1: Durable aggregation, lifecycle, independent delivery and authority

**Files:** Notifications `Features\Alarms`, `Features\Delivery`, owner persistence/migrations; `tests\Persistence\AlarmConsoleTests.cs`.

**Interfaces:** Consume raw immutable occurrences and trusted tenant/creator; produce public alarm/delivery projections and an application authorization interface without referencing Platform persistence. Exact HTTP fields are specified in 193.

- [x] Write tests for ordinary window boundary, high due time, duplicate/concurrent memberships and fault rollback.
- [x] Run selected persistence tests and record RED.
- [x] Implement membership/aggregate/delivery persistence and atomic operation/audit receipts.
- [x] Add current creator/field authorization and versioned acknowledgement/manual clear/silence/retry with negative and revoke-after-save tests.
- [x] Verify real MySQL GREEN, immutable raw-ingress regression and SQL indexes/collations.

### Task 2: Thin Host adapter and client pages

**Files:** `src\Host\Notifications`, Program/csproj; `clients\ops-web\src\features\notifications\{alarms.ts,AlarmsPage.vue,DeliveriesPage.vue}`; navigation/router/localization; corresponding HTTP and Vitest tests.

**Interfaces:** Use `/api/notifications` and DTO shapes in 193; no client-supplied tenant/creator metadata.

- [x] Test disabled setting, strict input JSON, no-store/auth, safe provider errors and real Platform current authority.
- [x] Implement independent default-false Host setting/DI/endpoints; no automatic migration/worker/real external provider.
- [x] Test strict frontend response states/shape, query failure, paging/details, offline/no-permission and exact original Unknown recovery.
- [x] Implement Vue pages, permission/navigation/localization and named client methods.
- [x] Run focused tests, frontend strict build and available actual HTTPS browser integration.

### Task 3: Persistent verified closure

**Files:** Implementation evidence, README/readiness/TodoList, traceability, canonical design and pitfalls.

- [x] Run combined owner/HTTP tests, architecture, complete Release and frontend tests/build. Full-suite baseline failures are explicit in194/HARD-10/11, not labelled green.
- [x] Inspect EF model drift and generated Up/Down SQL; apply Up only through authorized test fixtures, never Down.
- [x] Format changed C# only, validate Markdown links against HEAD and `git diff --check`.
- [x] Record exact pass/fail/unrun evidence and any remaining software boundaries without closing the whole T14.
- [x] Commit with required Copilot trailer, normal push `HEAD:main`, verify remote hash and clean worktree.
