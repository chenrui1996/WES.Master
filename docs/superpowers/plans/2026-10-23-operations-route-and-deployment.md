# Operations, Route, and Map Deployment Documentation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close WES design traceability and deployment-protocol gaps and document the approved Route reservation and map-issue lifecycle decisions.

**Architecture:** Keep operational issue records in Orchestration, alarm facts and delivery in Notifications, and final control validation in the owning WCS Pack. Use an immutable map deployment manifest for coordinated activation, and use the durable per-Pack reservation ledger plus single-writer fencing as the Route grant authority.

**Tech Stack:** Markdown design documents, ADR records, existing SCOPE/REQ/TC traceability conventions, VS Code Problems, and workspace search/link checks.

**Spec:** `docs/superpowers/specs/2026-10-23-operations-route-and-deployment-design.md`

## Global Constraints

- This change is documentation only; it does not claim implementation or verification.
- Map content and map versions remain immutable.
- Simulator owns the deployment manifest and coordinates preparation and activation.
- Orchestration owns persistent operational issues; Notifications continues to own alarm/event delivery and alarm acknowledgement/clear state.
- Use a durable database reservation ledger as the authoritative source for Route grants and ownership, with one active writer per WCS Pack.
- A monotonically increasing fencing token identifies the current writer epoch.
- HotStore/Redis may accelerate reads or rebuildable projections but is not the grant authority.
- The active manifest is the coordinated activation authority; module-owned staged artifacts are immutable and must be present before its pointer changes.
- Do not claim cross-module atomic transactions, physical safety certification, code implementation, or tests passed.
- Keep all design and test statuses pending until implementation evidence exists.

---

### Task 1: Complete Site and Operations Requirement Traceability

**Files:**
- Modify: `docs/modules/orchestration.md`
- Modify: `docs/modules/environment-simulation-and-scada.md`
- Modify: `docs/04-requirement-design-test-traceability.md`
- Modify: `docs/01-business-scope-and-status.md`
- Modify: `docs/acceptance/01-cross-module-and-provider-matrix.md`

**Interfaces:**
- Consumes: Approved requirement and test mapping in the spec.
- Produces: Defined `REQ-SITE-001..016`, defined `REQ-OPS-006..007`, and one-to-one design/test mappings for `SITE-TC-001..018`.

- [x] **Step 1: Define the existing SCADA and Simulator requirements**

In `docs/modules/environment-simulation-and-scada.md`, add a normative requirements table based only on existing design behavior:

| ID | Required behavior | Test |
|---|---|---|
| REQ-SITE-001 | SCADA provides a read-only spatial projection with stable bindings and source/version metadata. | SITE-TC-001 |
| REQ-SITE-002 | Missing, unknown, or stale location/telemetry appears as Unknown/Stale, never as empty/healthy. | SITE-TC-002 |
| REQ-SITE-003 | SCADA UI/API cannot mutate WMS inventory or WCS execution state. | SITE-TC-003 |
| REQ-SITE-004 | SCADA view/coordinate data remains separate from WMS location master data and WCS topology. | SITE-TC-001, SITE-TC-003 |
| REQ-SITE-005 | Invalid maps fail validation and cannot be deployed. | SITE-TC-004 |
| REQ-SITE-006 | Map deployment is idempotent and recoverable and never reports incomplete preparation as active success. | SITE-TC-005, SITE-TC-029..032 |
| REQ-SITE-007 | Active tasks remain pinned to their map version or follow an explicit safe migration decision. | SITE-TC-006, SITE-TC-033 |
| REQ-SITE-008 | Simulation uses the shared semantic execution contract and records reproducible inputs/fault-injection results. | SITE-TC-007 |
| REQ-SITE-009 | Simulator Reset cannot delete production data, inventory, or active physical work. | SITE-TC-008 |
| REQ-SITE-010 | Promote rejects test/loopback endpoints, unsupported devices, missing authorization/review, and is idempotent/auditable. | SITE-TC-009, SITE-TC-010 |
| REQ-SITE-011 | Failed Promote/rollback restores known configuration without erasing or misrepresenting active work. | SITE-TC-011 |

- [x] **Step 2: Define the field-operations requirements**

In `docs/modules/orchestration.md`, add a normative requirement table next to the existing operations requirements. Define:

| ID | Required behavior | Test |
|---|---|---|
| REQ-SITE-012 | Validate mode changes or application-point disablement against active work; reject or enter a defined safe state through the owning WCS Pack. | SITE-TC-012 |
| REQ-SITE-013 | Reject force-complete, cancel, and command retry if authorization, reason, state, or required confirmation is missing; never fabricate completion. | SITE-TC-013 |
| REQ-SITE-014 | Never represent software emergency-stop state or release as hardware-safe without authoritative safety-state confirmation. | SITE-TC-014 |
| REQ-SITE-015 | Detect concurrent operations through version checks and reject stale updates. | SITE-TC-015 |
| REQ-SITE-016 | Reconcile durable work against physical state after restart before releasing occupancy or redispatching commands. | SITE-TC-016 |

- [x] **Step 3: Define system-operations requirements**

In `docs/02-system-architecture.md` or its architecture verification section, define `REQ-OPS-006` for isolated restore consistency and prevention of production device connections, and `REQ-OPS-007` for rejecting control writes without a valid per-Pack single-writer lease/fencing token while allowing only explicitly permitted read-only capabilities. Link them to `SITE-TC-017` and `SITE-TC-018`.

- [x] **Step 4: Replace the undefined requirement range**

In `docs/04-requirement-design-test-traceability.md`, replace the `SCOPE-OPS-02` reference to undefined `REQ-SITE-017..021` with `REQ-OPS-006..007`. Add explicit rows for the five field-operation requirements and the two system-operation requirements, including the owning design section, test ID, and `designed` status.

- [x] **Step 5: Align scope and acceptance ownership**

Confirm `SCOPE-OPS-01` and `SCOPE-OPS-02` names and ownership in `docs/01-business-scope-and-status.md`. In the acceptance matrix, state that `SITE-TC-017..018` are system/WCS deployment acceptance cases and cite the exact `REQ-OPS` IDs. Do not mark either test as passed.

- [x] **Step 6: Check requirement coverage**

Search the five files for `REQ-SITE-001..021`, `REQ-OPS-006..007`, and `SITE-TC-001..018`. Confirm every referenced requirement is defined, every defined requirement has a test, and no scope/trace mapping still assigns the undefined `REQ-SITE-017..021` range.

---

### Task 2: Specify Coordinated Map Deployment

**Files:**
- Modify: `docs/modules/environment-simulation-and-scada.md`
- Modify: `docs/02-system-architecture.md`
- Modify: `docs/acceptance/01-cross-module-and-provider-matrix.md`
- Modify: `docs/04-requirement-design-test-traceability.md`

**Interfaces:**
- Consumes: Immutable map-version and module ownership conventions; Task 1's traceability updates.
- Produces: A concrete deployment manifest protocol with explicit prepare, activate, failure, rollback, and active-task behavior.

- [x] **Step 1: Document coordinator and participant ownership**

In the environment/simulation design, state that Simulator owns deployment manifests and coordinates activation; WMS, each affected WCS Pack, and SCADA own and validate their own immutable versioned artifacts. A manifest binds a `DeploymentId` to exact participant versions and checksums.

- [x] **Step 2: Document the prepare/activate protocol**

Add the sequence: create `Preparing` manifest; participants stage and validate the exact artifacts; each reports `Prepared` or a blocking error; refuse activation until every required participant has prepared; atomically update the active manifest pointer and write the Outbox event in the Simulator's local transaction; require runtime commands to carry the active deployment and owner version; process activation events idempotently and recover missed events by querying the manifest.

- [x] **Step 3: Document failure, rollback, and task pinning**

State that pre-activation failure leaves the old deployment active; rollback after activation is a new compensating deployment referencing immutable prior artifacts; active tasks remain pinned to their original MapVersion; the owning WCS Pack chooses continue, safe-stop, or controlled replan and reconciles physical state. Explicitly state that no distributed ACID transaction or silent partial success is promised.

- [x] **Step 4: Add deploy protocol acceptance coverage**

Strengthen `SITE-TC-005..006` for participant preparation failure, missing/stale acknowledgements, pre-activation failure, and active-task version pinning. Add `SITE-TC-029..033` for duplicate activation recovery, stale deployment command rejection, post-activation compensating rollback, retry/recovery after participant outage, and active tasks retaining the old version. Map these assertions to `REQ-SITE-006..007` without creating additional behavior requirements or claiming pass status.

- [x] **Step 5: Align architecture data ownership**

Clarify in `docs/02-system-architecture.md` that Simulator owns deployment coordination/manifest and activation pointer, while domain owners retain their map/topology/view artifact data. Ensure no section implies cross-module atomic commit.

---

### Task 3: Record Route Reservation ADR

**Files:**
- Create: `docs/adr/ADR-0001-route-reservation-authority.md`
- Modify: `docs/adr/README.md`
- Modify: `docs/route/02-target-routing-and-scheduling.md`
- Modify: `docs/route/03-route-test-catalog.md`
- Modify: `docs/acceptance/01-cross-module-and-provider-matrix.md`
- Modify: `docs/modules/wcs-fourway.md`
- Modify: `docs/modules/wcs-stacker.md`

**Interfaces:**
- Consumes: Approved durable reservation ledger + per-Pack single-writer/fencing decision.
- Produces: Indexed ADR-0001, defined `REQ-ROUTE-028..029`, and consistent Route/provider/test mappings.

- [x] **Step 1: Create ADR-0001**

Use the existing ADR template. Set status to `accepted`, date to `2026-10-23`, and record the decision: durable database reservation ledger is authoritative; one active writer per Pack; monotonically increasing fencing token; HotStore/Redis is derived/rebuildable only. Include invariants, rejected multi-writer-locking and Redis-authority alternatives, consequences, provider verification, and review conditions.

- [x] **Step 2: Index the ADR**

Add ADR-0001 to `docs/adr/README.md` and remove the outdated no-ADR claim. Keep the existing baseline list intact.

- [x] **Step 3: Align the Route design**

Replace the undecided alternatives in Route section 5 with the accepted mechanism. Define durable-before-dispatch, stale-writer rejection, idempotent owner-scoped grant/release, no time-only release for unknown occupancy, restart reconciliation, and HotStore-loss behavior. Clarify that provider implementations must satisfy the same logical grant invariant.

- [x] **Step 4: Add Route reservation verification cases**

Define `REQ-ROUTE-028` for durable grant authority and `REQ-ROUTE-029` for fencing/failover in `docs/route/02-target-routing-and-scheduling.md`. Extend `docs/route/03-route-test-catalog.md` with `ROUTE-TC-040..046` for competing grants, stale fencing token, lease takeover, writer crash between grant and dispatch, HotStore loss, restart reconciliation, and unknown physical occupancy. Map each case to the reservation requirement; retain the existing `ROUTE-TC-018..022` scenarios and strengthen them to assert the selected fencing/ledger behavior.

- [x] **Step 5: Align pack and provider documents**

Update FourWay and Stacker reservation descriptions to reference ADR-0001. Update the provider acceptance matrix to require database-specific tests for unique claims, transaction isolation, lease takeover, fencing, crash recovery, and competing grants. Do not imply provider support before results exist.

---

### Task 4: Define Map Issue Lifecycle

**Files:**
- Modify: `docs/modules/orchestration.md`
- Modify: `docs/04-requirement-design-test-traceability.md`
- Modify: `docs/acceptance/01-cross-module-and-provider-matrix.md`
- Modify: `docs/02-system-architecture.md`

**Interfaces:**
- Consumes: Existing map-first operating workbench and Notifications/WCS ownership boundaries.
- Produces: Orchestration-owned issue lifecycle, alarm relationship, resolution evidence rules, `REQ-OPS-008..010`, and `SITE-TC-024..028`.

- [x] **Step 1: Define issue ownership and source identity**

In Orchestration, define persistent operational issues as Orchestration-owned. Notifications retains alarm/event delivery and alarm acknowledgement/clear state. Link issues to stable source system and source occurrence ID, with optional asset/location/Pack/task/Leg/alarm/deployment references. Deduplicate repeated delivery of the same occurrence; do not auto-resolve when its alarm clears.

- [x] **Step 2: Define the lifecycle and authorization**

Document `Open -> Acknowledged -> Assigned -> Investigating -> AwaitingVerification -> Resolved`, allow any unresolved state to enter `Escalated`, and permit an auditable reopen from `Resolved` when authoritative evidence shows recurrence. Validate actor authorization, issue version, allowed prior state, and transition reason/evidence.

- [x] **Step 3: Define closure evidence**

Require fresh WCS/device readback and task/device state verification. Require WMS reconciliation when stock, location occupancy, or a WMS document may have changed. Keep stale, missing, contradictory, or unknown results in `AwaitingVerification` or `Escalated`. State explicitly that command acceptance and alarm clear do not close an issue.

- [x] **Step 4: Add issue requirements and tests**

Define `REQ-OPS-008` for issue provenance and idempotent source-occurrence handling, `REQ-OPS-009` for assignment/escalation and visible unresolved issues, and `REQ-OPS-010` for evidence-gated resolution and auditable reopening. Add `SITE-TC-024..028` for duplicate events, independent alarm/issue states, unlocated/stale visibility and assignment, failed/unknown readback, and valid resolution followed by recurrence.

- [x] **Step 5: Connect the traceability and E2E matrix**

Map `REQ-OPS-008..010` to the exact Orchestration sections and `SITE-TC-024..028`; update the cross-module E2E map workflow to cite the issue lifecycle. Keep statuses designed/pending.

---

### Task 5: Cross-Document Validation

**Files:**
- Review: all files changed in Tasks 1-4 and the approved spec

**Interfaces:**
- Consumes: All documentation changes.
- Produces: A consistent, link-valid design package with no claims of execution or test passage.

- [x] **Step 1: Validate identifiers**

Search the WES documentation for `REQ-SITE-001..021`, `REQ-OPS-001..010`, and `SITE-TC-001..033`. Verify every active requirement ID is defined once, every defined requirement maps to at least one test, and no scope/trace mapping still assigns the undefined `REQ-SITE-017..021` range.

- [x] **Step 2: Validate architectural invariants**

Review the changed sections together and confirm:
  - Simulator coordinates but does not own WMS/WCS/SCADA private map data.
  - No text claims cross-module ACID atomicity.
  - Tasks remain pinned to their map versions.
  - The database reservation ledger, not HotStore, authorizes Route grants.
  - Stale fencing epochs cannot mutate grants.
  - Operational issues belong to Orchestration; alarms belong to Notifications.
  - No issue closes on command send/acceptance or alarm clear alone.

- [x] **Step 3: Validate links and diagnostics**

Check relative Markdown links in changed files using the workspace's available documentation tooling or VS Code Problems. If Unicode paths make a shell link check unreliable, report that limitation rather than treating the check as passed. Run Problems on the changed documentation workspace.

- [x] **Step 4: Report evidence accurately**

Report changed files, requirement/test mapping counts, link/diagnostic results, and remaining verification blockers. State that this is design-only and that runtime/provider/route tests have not been run.

## Self-Review

- Spec coverage: Tasks 1-4 cover requirements/traceability, map deployment, ADR, issue lifecycle, and validation.
- Coverage expansion: `REQ-SITE-001..011` are defined from existing SCADA/Simulator behavior and mapped to existing `SITE-TC-001..011`; this closes additional undefined IDs discovered during plan review without adding new behavior.
- Placeholder scan: No TBD/TODO or unspecified owner is required by the plan.
- Type/interface consistency: Documentation interfaces and IDs match the approved spec; the existing Route catalog ends at `ROUTE-TC-039`, so new reservation cases use `ROUTE-TC-040..046`.
- Repository limitation: The workspace is not a Git repository, so commit steps are omitted.
