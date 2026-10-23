# WES Operations, Map Deployment, and Route Reservation Design

- Status: proposed for review
- Date: 2026-10-23
- Scope: close the two high-severity review findings and define the approved Route reservation and map-issue lifecycle decisions
- Related design: `docs/04-requirement-design-test-traceability.md`, `docs/02-system-architecture.md`, `docs/modules/environment-simulation-and-scada.md`, `docs/modules/orchestration.md`, `docs/route/02-target-routing-and-scheduling.md`, `docs/route/03-route-test-catalog.md`, `docs/acceptance/01-cross-module-and-provider-matrix.md`

## 1. Purpose

Make the WES design package implementable and traceable in four related areas:

1. Define every site and operations/system requirement currently referenced by the traceability matrix and map it to design and acceptance evidence.
2. Specify a recoverable, cross-module map deployment protocol without assuming a distributed transaction.
3. Record the Route reservation authority decision in an ADR.
4. Define the persistent issue lifecycle behind the map-first operations workbench.

This specification changes documentation only. It does not claim implementation or verification.

## 2. Requirements and traceability

### 2.1 Existing site requirements

The traceability matrix references `REQ-SITE-001..011` but those IDs currently have no normative definitions. Define them in the environment/simulation/SCADA design using the already documented behavior and tests, without expanding product behavior:

| Requirement | Required behavior | Test |
|---|---|---|
| REQ-SITE-001 | SCADA must provide a read-only spatial projection with stable view/node bindings and show operational state with its source/version. | SITE-TC-001 |
| REQ-SITE-002 | Missing locations, unknown state, and stale telemetry must be explicitly shown as Unknown/Stale and never represented as empty/healthy. | SITE-TC-002 |
| REQ-SITE-003 | SCADA UI/API must not mutate WMS inventory or WCS execution state. | SITE-TC-003 |
| REQ-SITE-004 | SCADA view/coordinate configuration must remain separate from WMS location master data and WCS route topology. | SITE-TC-001, SITE-TC-003 |
| REQ-SITE-005 | Map validation must reject malformed or inconsistent maps and prevent invalid deployment. | SITE-TC-004 |
| REQ-SITE-006 | Deployment of validated maps must be idempotent and recoverable; incomplete preparation must not be reported as active success. | SITE-TC-005, SITE-TC-029..032 |
| REQ-SITE-007 | Active tasks must remain pinned to their map version or follow an explicit safe migration decision. | SITE-TC-006 |
| REQ-SITE-008 | Simulation must use the same semantic execution contract and preserve reproducible inputs/fault-injection results. | SITE-TC-007 |
| REQ-SITE-009 | Reset must not delete production data, inventory, or active physical work. | SITE-TC-008 |
| REQ-SITE-010 | Promote must reject test/loopback endpoints, unsupported devices, missing authorization, or required review; repeated Promote must be idempotent and auditable. | SITE-TC-009, SITE-TC-010 |
| REQ-SITE-011 | Failed production promotion/rollback must restore a known configuration without erasing or misrepresenting active physical work. | SITE-TC-011 |

### 2.2 Existing field-operations requirements

Define `REQ-SITE-012..016` in the Orchestration operations design and map them one-to-one to `SITE-TC-012..016`:

| Requirement | Required behavior | Test |
|---|---|---|
| REQ-SITE-012 | Mode changes or disabling an application point with active work must be validated by the owning WCS Pack and either rejected or brought to a defined safe state. | SITE-TC-012 |
| REQ-SITE-013 | Force-complete, cancel, and command-retry operations must be rejected when authorization, reason, state, or required confirmation is missing; they must not fabricate completion. | SITE-TC-013 |
| REQ-SITE-014 | A software emergency-stop indication or release must not be represented as hardware-safe without authoritative safety-state confirmation. | SITE-TC-014 |
| REQ-SITE-015 | Concurrent operational state changes must use a version check; a stale command cannot overwrite a newer state. | SITE-TC-015 |
| REQ-SITE-016 | Restart recovery must reconcile durable work with physical state before releasing occupancy or redispatching commands. | SITE-TC-016 |

Keep `REQ-OPS-001..005` for map visibility, common controls, issue closure, and high-risk controls.

### 2.3 System operations requirements

Replace the presently undefined `REQ-SITE-017..021` range in the traceability matrix with:

| Requirement | Required behavior | Test |
|---|---|---|
| REQ-OPS-006 | Restoring into an isolated environment must preserve a consistent WMS ledger, active WCS work, and deployed map versions, while preventing accidental connections to production devices. | SITE-TC-017 |
| REQ-OPS-007 | When a valid per-Pack single-writer lease/fencing token is unavailable, the system must reject control writes and expose only the explicitly allowed read-only capabilities. | SITE-TC-018 |

The architecture and acceptance matrix must describe the requirement, owner, test evidence, and status consistently. Requirement/test status remains designed or pending until implementation evidence exists.

## 3. Map deployment protocol

### 3.1 Ownership and versioning

- Map content and map versions remain immutable.
- Simulator owns the deployment manifest and coordinates preparation and activation.
- WMS, each affected WCS Pack, and SCADA remain owners of their respective location/topology/view data and must prepare and validate their own versioned artifacts.
- A deployment manifest binds one `DeploymentId` to the exact version identifiers and checksums from all participating owners.
- Existing tasks remain pinned to the map version with which they were planned. Deployment never rewrites active task route/version references.

### 3.2 Prepare and activate

1. Simulator creates a deployment manifest in `Preparing` state with an immutable target version set.
2. Each participant validates and stages its artifact and reports `Prepared` or a blocking failure against the same `DeploymentId`.
3. Activation is prohibited until every required participant has prepared the exact version/checksum in the manifest.
4. Simulator atomically changes the manifest's active deployment pointer and writes the corresponding Outbox event in one local transaction.
5. Runtime commands identify the active `DeploymentId` and the relevant owner version. A command is rejected if its WCS Pack has not prepared that deployment or if the supplied deployment/version is stale.
6. Consumers process activation events idempotently. A missed event is recoverable by querying the authoritative manifest; event delivery is not itself the activation authority.

The protocol does not promise an atomic transaction spanning modules. The active manifest is the coordinated activation authority; module-owned staged artifacts are immutable and must be present before its pointer changes.

### 3.3 Failure, rollback, and active work

- A failure before activation leaves the active deployment unchanged. The manifest remains diagnosable as failed or requiring recovery; no participant may report a successful deployment.
- A deployment that cannot establish all preparation acknowledgements is not activated.
- Rollback after activation creates a new compensating deployment that references previously validated immutable artifacts. It does not mutate or delete the failed deployment history.
- Each WCS Pack decides whether active work can continue on its pinned map, must stop at a safe point, or requires controlled replanning. The decision and physical reconciliation are auditable.
- Reset and rollback must not clear inventory, active task state, or physical occupancy.

### 3.4 Acceptance coverage

Strengthen `SITE-TC-005..006` for participant preparation failure, missing/stale acknowledgements, failure before activation, and active-task version pinning. Add `SITE-TC-029..033` for duplicate activation recovery, stale deployment command rejection, post-activation compensating rollback, retry/recovery after participant outage, and proving active tasks retain the old version. Map these assertions to existing `REQ-SITE-006..007`; do not create additional behavior requirements.

## 4. ADR-0001: Route reservation authority

### 4.1 Decision

Use a durable database reservation ledger as the authoritative source for Route grants and ownership, with one active writer per WCS Pack. A monotonically increasing fencing token identifies the current writer epoch. HotStore/Redis may accelerate reads or rebuildable projections but is not the grant authority.

### 4.2 Requirements and invariants

Define two focused requirements in the Route design:

| Requirement | Required behavior | Tests |
|---|---|---|
| REQ-ROUTE-028 | Each WCS Pack must durably record and validate reservation grants before dispatch; HotStore is never grant authority. | ROUTE-TC-040, ROUTE-TC-043..045 |
| REQ-ROUTE-029 | Reservation writes and command dispatch must validate the current monotonic writer epoch; stale writers are fenced, failover reconciles physical state, and unknown occupancy is not released by time alone. | ROUTE-TC-041..042, ROUTE-TC-046 |

- A grant is durable before a device command referencing it can be dispatched.
- Every grant is scoped to Pack, resource, route/task, vehicle, map version, writer epoch, and lifecycle state.
- The database stores the per-Pack lease and monotonically increasing epoch; acquisition/takeover advances the epoch atomically. All grant, release, and dispatch-authorization writes validate the current fencing token. A stale writer cannot grant, extend, or release reservations.
- Grant and release operations are idempotent for the same owner/request identity.
- Conflicting grants cannot both become authoritative. Database uniqueness/transaction behavior must be explicitly tested for each supported provider.
- Lease expiry alone never frees occupancy when physical state is unknown. The resource moves to reconciliation/recovery until authoritative device/position evidence permits release.
- Every command envelope carries its writer epoch. The dispatch gate checks that epoch against the durable lease immediately before sending; after failover, the new writer reconciles commands already in flight and physical state before resuming dispatch.
- HotStore loss can affect performance, not correctness or safety.

### 4.3 Operational and provider constraints

The single-writer lease and fencing authority must itself be durable and fail closed. Readiness for control writes requires a valid writer epoch; read-only operation may remain enabled by explicit policy. Failover must not allow both old and new writer epochs to issue effective grants.

SQL Server, PostgreSQL, and MySQL require provider-specific concurrency tests for unique claims, transaction isolation, lease takeover, fencing validation, crash recovery, and competing grants. Support for a provider is not established until these tests pass.

### 4.4 Alternatives rejected

- **Database multi-writer locking as the primary design:** viable in principle, but lock and isolation behavior varies by provider and would make equivalent semantics harder to establish. It remains a possible later ADR if measured requirements justify it and provider conformance is demonstrated.
- **Redis/HotStore as the grant authority:** rejected because loss, failover, or stale cache state must not determine physical occupancy or authorize a device command.

### 4.5 Consequences and verification

The design adds per-Pack writer-lease/fencing operations, durable reservation lifecycle and recovery work. It provides a clear authority and stale-writer defense but introduces failover/readiness behavior that must be operationally managed.

Add Route tests `ROUTE-TC-040..046` for competing grants, stale fencing tokens, lease takeover, writer crash between grant and dispatch, HotStore loss, restart reconciliation, and unknown physical occupancy. Map each to REQ-ROUTE-028 or REQ-ROUTE-029. Record provider, isolation/configuration, fault injection, and observed durable state. Keep all tests as designed/pending until run.

## 5. Map issue lifecycle

### 5.1 Ownership and source relationship

- Orchestration owns persistent operational issues that require assignment, cross-module coordination, escalation, or verified resolution.
- Notifications continues to own alarm/event delivery and alarm acknowledgement/clear state.
- An issue records a stable source type and source occurrence ID and links to affected asset, location, Pack, task/Leg, alarm, and deployment where known.
- Idempotency is based on source system plus source occurrence ID. Repeated delivery of the same occurrence updates observation metadata, not a duplicate issue. A new occurrence is a new source unless explicitly reopened by a user with an auditable reason.
- Clearing or acknowledging a Notification alarm does not automatically resolve the operational issue. The two lifecycles remain linked but distinct.

### 5.2 States and transitions

The initial state model is:

`Open → Acknowledged → Assigned → Investigating → AwaitingVerification → Resolved`

Any unresolved state may transition to `Escalated`; after authorized reassignment/recovery it may return to `Assigned` or `Investigating`. A `Resolved` issue may return to `Open` when new authoritative evidence shows recurrence or the resolution was invalid; the reopen reason and evidence are retained.

Every transition validates actor authorization, current issue version, allowed prior state, and required reason/evidence. Assignment, escalation target, due/SLA policy, action history, timestamps, and correlation IDs are retained as issue history.

### 5.3 Resolution evidence

- Sending or accepting a command does not resolve an issue.
- Resolution requires fresh authoritative readback from the owning WCS Pack/device and confirmation of the intended task/device state.
- If inventory, location occupancy, or a WMS document may have been affected, the owning WMS contract must reconcile the relevant facts before resolution.
- Stale, missing, contradictory, or unknown physical state blocks resolution and leaves the issue in `AwaitingVerification` or `Escalated`.
- The map is a view and action entry point; unresolved, unlocated, unmapped, stale, and offline issues remain discoverable in an associated list/queue.

### 5.4 New acceptance cases

Add:

| ID | Scenario | Expected result |
|---|---|---|
| SITE-TC-024 | Duplicate delivery of the same alarm/source occurrence | One operational issue exists; last-observed metadata and event history update idempotently. |
| SITE-TC-025 | Alarm clears or is acknowledged while issue remains unresolved | Alarm state changes independently; issue remains open/assigned until resolution evidence is provided. |
| SITE-TC-026 | Unlocated or stale issue is viewed, assigned, and escalated from map workbench | Issue remains visible in the associated queue with explicit location/data-quality state; authorization and assignment/escalation history are retained. |
| SITE-TC-027 | Command accepted but feedback is missing, stale, failed, or contradictory | Issue cannot resolve; it remains awaiting verification or escalates with diagnostic context. |
| SITE-TC-028 | Valid WCS/device readback and required WMS reconciliation, followed by recurrence | Resolution is recorded only after evidence; recurrence reopens or creates an occurrence according to source identity without deleting history. |

## 6. Documentation changes

After approval of this specification:

1. Define `REQ-SITE-001..011` in `docs/modules/environment-simulation-and-scada.md`; update `docs/modules/orchestration.md` for `REQ-SITE-012..016`; update `docs/04-requirement-design-test-traceability.md`, the system architecture, scope matrix, and acceptance matrix.
2. Update `docs/modules/environment-simulation-and-scada.md` with the deployment protocol and deployment tests.
3. Add `docs/adr/ADR-0001-route-reservation-authority.md` and index it in `docs/adr/README.md`.
4. Define REQ-ROUTE-028..029 in `docs/route/02-target-routing-and-scheduling.md`; update its trace row, `docs/route/03-route-test-catalog.md`, FourWay/Stacker references, and the provider acceptance matrix to align with ADR-0001.
5. Add issue ownership, lifecycle, evidence rules, requirements, and `SITE-TC-024..028` to Orchestration and map them in the traceability and cross-module acceptance documents.
6. Keep `docs/01-business-scope-and-status.md` scope ownership and states consistent; do not claim code/test implementation.

## 7. Validation

- Check requirement-to-design-to-test coverage for `SCOPE-DEV-02`, `SCOPE-DEV-03`, `SCOPE-OPS-01`, and `SCOPE-OPS-02`.
- Check that every referenced `REQ-SITE`, `REQ-OPS`, and `SITE-TC` identifier is defined and has no unreferenced requirement.
- Check map-version, deployment-state, writer-lease, fencing-token, reservation-owner, and issue-state terminology for consistency.
- Check local Markdown links and workspace diagnostics.
- Do not run or claim runtime tests; this change is design documentation only.
