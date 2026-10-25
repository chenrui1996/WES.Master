# T05 Platform Message Operations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Provide Platform-owner authorized, bounded Outbox diagnostics and same-envelope operator replay with durable actor/reason/idempotency evidence.

**Architecture:** Platform remains the only reader/writer of its Outbox. Current tenant/session authorization gates a safe metadata projection and replay command; replay never accepts a replacement payload or event ID and records its reason in the same owner transaction as resetting the quarantined row. Notifications may aggregate owner APIs later but does not query Platform tables directly.

**Tech Stack:** .NET 10, EF Core 10.0.7, Oracle MySql.EntityFrameworkCore 10.0.7, ASP.NET Core minimal APIs, xUnit, isolated MySQL `wes_test`.

**Spec:** `docs/detailed-design/notifications-and-jobs.md` (FUNC-NTF-001, PAGE-NTF-003, RULE-14..17) and `docs/superpowers/plans/2026-10-23-wes-development-work-packages.md` (T05).

## Global Constraints

- Each event owner persists its own Outbox/Inbox; Notifications never owns another module's transaction.
- Tenant identity comes only from authenticated `IdentityRuntime.Subject`; message payload and broker ACK never establish authorization or business completion.
- Query and replay are deny-by-default and require current active-session grants for the exact `platform.messages.read` and `platform.messages.replay` permissions.
- Replay preserves EventId, contract, schema, owner, aggregate identity/version, correlation, occurrence time, and payload byte-for-byte.
- No physical/unknown message is automatically or manually replayed; no real Broker, production database, or alternate Provider is claimed as tested.
- Platform schema changes are additive owner migrations; all MySQL tests target the authorized isolated `wes_test` database.
- Every meaningful slice has a failing test, a passing verification, implementation/test documentation, and a pushed commit with the required co-author trailer.

---

### Task 1: Adding authorized bounded Outbox diagnostics

**Files:**
- Create: `src/Modules/Platform/Features/Messaging/QueryOutbox.cs`
- Modify: `src/Modules/Platform/PlatformScope.cs`
- Modify: `src/Host/Platform/PlatformEndpoints.cs`
- Test: `tests/Persistence/DirectoryReceiptTests.cs`
- Test: `tests/Modules/Platform/IdentityHttpTests.cs`

**Interfaces:**
- Consumes: `TenantContext`, `CurrentAuthorization.VisibleFieldsAsync`, `PlatformDbContext.OutboxMessages`.
- Produces: `OutboxQueryPage(bool Authorized, ImmutableArray<OutboxQueryRow> Rows)`; each row exposes only EventId, contract/schema, owner, status, attempt count, created/next/published instants, and finite error category. `QueryOutbox.ReadAsync(TenantContext subject, int offset, int limit, CancellationToken cancellationToken)` requires `platform.messages.read` with `DataScope.All`.

- [x] **Step 1: Write failing authorization and projection tests.**

Add persistence tests proving a fresh live session without `platform.messages.read` receives `Authorized == false`, while a session with an All-scope grant receives only bounded metadata and never the payload. Add HTTP tests for a denied response and an authorized page.

- [x] **Step 2: Run the focused tests and confirm they fail.**

Run: `dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~OutboxQuery --verbosity quiet`

Expected: compilation or assertions fail because the query API and permission projection do not exist.

- [x] **Step 3: Implement the safe query projection.**

Use `CurrentAuthorization.VisibleFieldsAsync(subject, "platform.messages.read", new ResourceScope(database.TenantId, Guid.Empty, Guid.Empty), cancellationToken)` and require `EventId`, `Status`, `CreatedAt`, and `Owner`. Reject offset below zero and limit outside 1..100. Query `AsNoTracking()` under the existing tenant filter, sort by `CreatedAt` then `EventId`, paginate, and project only fields actually granted; do not select or serialize `Payload`.

- [x] **Step 4: Wire the owner scope and authenticated GET route.**

Expose `PlatformScope.OutboxAsync(...)` and map `GET /api/platform/messages` using `IdentityRuntime.Subject(http.User)`, `IdentityDatabase.Open(subject.TenantId)`, and the existing HTTPS/client endpoint filter. Return `Forbid` when the owner projection is unauthorized.

- [x] **Step 5: Run persistence and HTTP tests.**

Run: `dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~OutboxQuery --verbosity quiet`

Run: `dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~MessageOperations --verbosity quiet`

Expected: all query authorization, pagination, and projection tests pass.

---

### Task 2: Recording authorized same-envelope replay

**Files:**
- Create: `src/Modules/Platform/Domain/PlatformOutboxReplayReceipt.cs`
- Create: `src/Modules/Platform/Features/Messaging/ReplayOutboxMessage.cs`
- Create: additive migration under `src/Modules/Platform/Infrastructure/Persistence/Migrations/`
- Modify: `src/Modules/Platform/Domain/PlatformOutboxMessage.cs`
- Modify: `src/Modules/Platform/Infrastructure/Persistence/PlatformDbContext.cs`
- Modify: `src/Modules/Platform/PlatformScope.cs`
- Test: `tests/Persistence/DirectoryReceiptTests.cs`

**Interfaces:**
- Consumes: `TenantContext`, `DirectoryOperation`, an event ID, expected attempt count, and a bounded reason.
- Produces: `PlatformOutboxReplayResult(PlatformDecision Decision, int? Attempts)` and an immutable replay receipt keyed by tenant/operation ID.

- [ ] **Step 1: Write failing replay lifecycle tests.**

Cover forbidden actors, non-quarantined and stale-attempt requests, replay of a quarantined safe `platform.audit` event, same-operation retry, conflicting operation reuse, and concurrent replay. Assert the original event envelope is unchanged and the reason/actor are retained with the receipt.

- [ ] **Step 2: Run the focused test and confirm it fails.**

Run: `dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~OutboxReplay --verbosity quiet`

Expected: compilation or assertions fail because replay records and authorized transitions do not exist.

- [ ] **Step 3: Add the immutable replay receipt and additive migration.**

Persist tenant, operation ID, event ID, actor ID, reason (3..256 printable characters), request instant, prior attempts, and a SHA-256 fingerprint of the nonsecret command identity. Use a tenant/operation composite key and owner query filter. Generate the migration with `--configuration Release --context PlatformDbContext`; verify it creates only the replay receipt table and indexes.

- [ ] **Step 4: Implement the transactionally authorized replay command.**

Require a current All-scope `platform.messages.replay` grant for `EventId`, `Status`, `Attempts`, and `Reason`; authorize before receipt lookup. In one explicit owner transaction, return the matching prior receipt for an identical operation, reject fingerprint conflicts, require the expected attempt count and `Quarantined` state, reject any non-`platform.audit` or non-Platform event, stage the immutable receipt, set status to Pending/due-now, reset the automatic retry window and attempt counter to zero (the receipt retains the previous attempt count), save, and commit. Preserve all envelope identity and payload fields. Convert concurrency conflicts to an explicit conflict result; surface unrelated persistence failures.

- [ ] **Step 5: Run focused MySQL lifecycle and migration tests.**

Run: `dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~OutboxReplay --verbosity quiet`

Expected: replay is tenant-scoped, permission-checked, idempotent by operation ID, reasoned, conflict-safe, and preserves the original event envelope.

---

### Task 3: Exposing the replay command and recording evidence

**Files:**
- Modify: `src/Host/Platform/PlatformEndpoints.cs`
- Test: `tests/Modules/Platform/IdentityHttpTests.cs`
- Create: `docs/implementation/39-t05-platform-message-operations.md`
- Create: `docs/implementation/40-t05-platform-message-operations-tests.md`
- Modify: `docs/implementation/readiness-register.md`
- Modify: `docs/superpowers/plans/2026-10-23-wes-development-work-packages.md`

**Interfaces:**
- Consumes: the Task 1 metadata query and Task 2 owner replay command.
- Produces: authenticated `POST /api/platform/messages/{eventId}/replay` accepting only `OperationId`, `ExpectedAttempts`, and `Reason`; payload fields are not accepted.

- [ ] **Step 1: Write failing HTTPS endpoint tests.**

Test unauthenticated rejection, authenticated-but-forbidden rejection, malformed operation/reason rejection, stale expected attempts conflict, successful replay, and operation retry returning the same result without a second replay receipt.

- [ ] **Step 2: Wire minimal API contracts without exposing persistence entities.**

Map the GET route from Task 1 and replay POST route. Bind actor/tenant from the authenticated subject; derive correlation from the trusted trace; never accept actor, tenant, payload, contract, or status from the request body. Map decisions to existing HTTP results consistently with Platform write endpoints.

- [ ] **Step 3: Run the endpoint-focused test and full Release regression.**

Run: `dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~MessageOperations --verbosity quiet`

Run: `dotnet test WES.slnx --no-restore -c Release --verbosity quiet -m:1`

Expected: all endpoint tests and all solution suites pass.

- [ ] **Step 4: Write evidence and limitations.**

Record file paths, migration, focused and full counts, and explicitly state that no real Broker, hosted scheduler, Notifications aggregator, other-owner adapter, alternate Provider, or production recovery drill was verified.

- [ ] **Step 5: Commit and push the completed operations slice.**

Stage only the files listed in these tasks. Commit with a Conventional Commit subject and append:

```text
Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
```

Push the verified commit to `origin/main`.
