# WES.Master Startup Documentation Plan

> **For agentic workers:** This plan covers documentation authoring only. Do not create application source, solution files, migrations, or scaffolding. Follow the approved design and request user review before any later coding phase.

**Goal:** Produce a reviewed, traceable business, architecture, Route, engineering-governance, and test documentation set in WES.Master.

**Architecture:** Keep the approved .NET 10 modular-monolith design, with WMS/WCS domain boundaries, explicit upstream business contracts, downstream protocol adapters, and separate self-developed Route models. This plan defines document deliverables and review checks only; it does not implement runtime behavior.

**Tech Stack:** Markdown documentation; approved design baseline uses .NET 10, Vue 3 + Vite + TypeScript for operations and simulator, UniApp + Vue 3 + TypeScript for PDA, SQL Server/PostgreSQL/MySQL provider support, and Frontloom Core FE + Vue guidance.

**Spec:** `WES.Master/docs/00-project-startup-design.md`

## Global Constraints

- Preserve Seven.Master business capabilities after evidence-based status classification; remove Builder/source-code generation.
- Include PDA, preserve and verify the existing picking page/API, and design ship and stacker-exception gaps; offline PDA remains read-only with no queued writes.
- Use a .NET 10 modular monolith; apply DDD to WMS/WCS invariants and organize workflows by use case.
- Retain SQL Server, PostgreSQL, and MySQL provider-switch capability.
- Use Vue 3 + Vite + TypeScript for operations and simulator; preserve Seven's UniApp + Vue 3 + TypeScript PDA; apply Frontloom Core FE and Vue principles within each platform's constraints.
- Keep each self-developed WCS pack's topology, traffic, and equipment model independent.
- Route prioritizes safety and liveness before throughput and route-cost optimization; include live vehicle state, conflict zones, occupancy timing, waiting, and recovery inputs.
- Expose stable upward business/document-extension contracts and isolate downward device protocols behind adapters.
- This plan authorizes documentation only; no source code, project scaffolding, or generated code.
- Every functional document includes traceable requirements and detailed test cases; every uncertain behavior is explicitly marked for design resolution.

---

## Documentation File Map

| File | Responsibility |
|---|---|
| `WES.Master/README.md` | Entry point, document index, and evidence sources |
| `WES.Master/AGENTS.md` | Short project rules for future agents; links to canonical docs |
| `WES.Master/docs/00-project-startup-design.md` | Approved scope, architecture, Route direction, and unresolved design inputs |
| `WES.Master/docs/01-business-scope-and-status.md` | Feature-by-feature preserve/complete/reserved/remove inventory |
| `WES.Master/docs/02-system-architecture.md` | Module responsibilities, dependencies, contracts, data ownership, runtime boundaries |
| `WES.Master/docs/03-engineering-governance.md` | Backend/frontend, comments, API, security, docs, ADR, PR, and CI rules |
| `WES.Master/docs/04-requirement-design-test-traceability.md` | Requirement IDs and traceability template |
| `WES.Master/docs/modules/*.md` | Per-domain behavior, detailed design, contracts, error/recovery cases, and test cases |
| `WES.Master/docs/route/*.md` | Current-state/evidence review, target algorithm architecture, and Route test catalog |
| `WES.Master/docs/acceptance/01-cross-module-and-provider-matrix.md` | Cross-module E2E, provider, adapter, deployment, and acceptance matrix |
| `WES.Master/docs/adr/README.md` | ADR rules and decision index |

## Task 1: Publish Business Scope and Status Matrix

**Files:**
- Create: `WES.Master/docs/01-business-scope-and-status.md`
- Modify: `WES.Master/README.md`

**Inputs:** Seven.Master docs index, feature matrix, WMS/WCS implementation guide, module docs, tests, and existing application directories.

- [x] Inventory every visible Seven.Master business and platform feature.
- [x] Classify each item as `preserve`, `complete`, `reserved`, `remove`, or `verify`; cite evidence and avoid claiming unverified completion.
- [x] Include authentication/authorization, workflow/forms, jobs, messages/outbox, alarms/news, WMS, PDA, Bus/WCS, external WCS, device integration, SCADA, simulator, operations, observability, deployment, and database providers.
- [x] Mark Builder, source templates, generated APIs, and generated business artifacts for removal.
- [x] Mark BoxSort and incomplete real-equipment/multi-writer capabilities as reserved or incomplete unless source evidence proves otherwise.
- [x] List every business decision still required, including compatibility/migration and first-release scope.
- [x] Review the inventory against the source index and domain entities; require a status and evidence link for every row.

**Acceptance:** Every Seven capability has an explicit disposition, an evidence reference, and a corresponding detailed-design owner or reason for exclusion.

## Task 2: Define System Architecture and Module Contracts

**Files:**
- Create: `WES.Master/docs/02-system-architecture.md`
- Modify: `WES.Master/README.md`

- [x] Define Host and module responsibilities for Platform, Workflow/Form, WMS, Orchestration, Stacker, FourWay, External WCS, Device Integration, SCADA, Simulator, Notifications, and Operations.
- [x] Define module-owned entities, tables, migrations, application contracts, integration events, and allowed dependency direction.
- [x] Document the WMS inventory ledger as the single inventory authority and the prohibition on WCS-owned decrementable inventory.
- [x] Specify upward business/document-extension contracts and downstream protocol-adapter contracts, including simulation using the same semantic command/feedback boundary.
- [x] Document single-deploy modular-monolith trade-offs and the conditions that would justify later extraction.
- [x] Record the selected .NET 10 / Vue 3 / Vite / TypeScript / UniApp / three-provider baseline without scaffolding.
- [x] Review all dependency arrows and ensure no module directly depends on another module's Infrastructure, DbContext, or private entities.

**Acceptance:** Every module has one owner, explicit inward/outward contracts, and a documented forbidden-dependency rule.

## Task 3: Write Platform and Workflow Functional Specifications

**Files:**
- Create: `WES.Master/docs/modules/platform-and-security.md`
- Create: `WES.Master/docs/modules/workflow-and-forms.md`
- Create: `WES.Master/docs/modules/notifications-and-jobs.md`

- [x] Specify authentication, refresh-token lifecycle, password policy, CAPTCHA, RBAC, API/menu/button/data permissions, tenancy, dictionaries/configuration, files, audit, logs, exception codes, i18n, health, metrics, rate limiting, and idempotency.
- [x] Specify workflow state transitions, approver types, conditional branches, countersign behavior, dynamic forms, business hooks, and failure/authorization cases.
- [x] Specify Quartz lifecycle and failure visibility; message/outbox publication, idempotency, retries, dead-letter/replay; alarm lifecycle, SignalR delivery, mail, and news/announcement behavior.
- [x] For each feature, provide actors, preconditions, state/data invariants, API or event contract, permission rules, errors/compensation, observability, and test cases.
- [x] Include unauthorized, duplicate, concurrent, invalid-state, provider-failure, and retry cases wherever applicable.
- [x] Review every listed capability against the Seven docs and implementation evidence; mark missing or partial capability explicitly.

**Acceptance:** Each feature has normal, validation, permission, failure, retry/idempotency, and audit/observability coverage appropriate to its behavior.

## Task 4: Write WMS, Business Documents, and PDA Specifications

**Files:**
- Create: `WES.Master/docs/modules/wms-and-ledger.md`
- Create: `WES.Master/docs/modules/pda.md`

- [x] Specify warehouse/zone/layer/aisle/location/container master data, pack prefixes, locks, handover points, and allocation ownership.
- [x] Define stock ledger invariants for receive, reserve, release, ship, move, cycle-count adjustment, cancellation, and transport completion.
- [x] Specify inbound order/detail/build-pallet and outbound order/line/pick/ship lifecycles, including commit points and compensation.
- [x] Specify cycle count, transfer orders, internal relocation, cross-pack handover, and extension hooks.
- [x] Verify the existing PDA picking page/API behavior and test coverage; do not duplicate it as a new feature unless the audit finds a documented business gap.
- [x] Specify PDA login, task visibility, scan validation, receive, put-away, pick, ship, cycle count, stacker exception handling, read-only offline cache freshness/protection, and server authority; no offline write queue or replay.
- [x] Add per-function API/permission, state, persistence, and test tables, including duplicate scan, wrong location/container, insufficient stock, concurrent reservation, partial completion, lost connectivity, online retry/idempotency, and cancellation.
- [x] Review every stock mutation to ensure exactly one authoritative ledger transition and no WCS-side duplicate inventory.

**Acceptance:** Every WMS/PDA path maps to a documented state transition and a test that asserts resulting ledger, document, and transport states.

## Task 5: Write Orchestration and WCS Package Specifications

**Files:**
- Create: `WES.Master/docs/modules/orchestration.md`
- Create: `WES.Master/docs/modules/wcs-stacker.md`
- Create: `WES.Master/docs/modules/wcs-fourway.md`
- Create: `WES.Master/docs/modules/external-wcs-and-device-adapters.md`

- [x] Specify transport order/leg planning, pack selection, handover, idempotent delivery, cancel/fail/retry, completion, and ledger callback.
- [x] Specify Stacker allocation, double-deep geometry, blocking transfer, task segmentation, ExeStack grouping, capacity, route-flow ownership, and feedback.
- [x] Specify FourWay layer/aisle/location allocation, shuttle identity, parking state transitions, layer traffic, hoist stages, feedback, retry, and reservation release.
- [x] Specify external HTTP/MQ transport selection, codec boundary, callback authentication, duplicate/out-of-order messages, and interface logs.
- [x] Specify normalized device commands/feedback, adapter lifecycle, correlation, sequence, timeout/reconnect, unsupported command, and simulator conformance.
- [x] Add unit, concurrency, persistence, API/contract, cancellation, recovery, and end-to-end cases for each lifecycle.
- [x] Review pack boundaries to ensure no shared WCS topology, cross-pack hot-state writes, or device-protocol parsing in business modules.

**Acceptance:** All task transitions are observable, idempotent where retried, and have explicit safe failure/compensation outcomes.

## Task 6: Write Device, SCADA, Simulator, and Map-First WCS Operations Specifications

**Files:**
- Create: `WES.Master/docs/modules/device-communication.md`
- Create: `WES.Master/docs/modules/environment-simulation-and-scada.md`
- Modify: `WES.Master/docs/modules/orchestration.md`, `WES.Master/docs/modules/wcs-stacker.md`, `WES.Master/docs/modules/wcs-fourway.md`

- [x] Specify connection and point management, read/write permissions, rule execution, alarm mapping, reconnect behavior, and protocol adapter conformance.
- [x] Specify SCADA bindings and read-only state projection; explicitly prohibit authoritative writes from SCADA.
- [x] Specify simulator map edit/validation/deploy, versioning, task execution, virtual device behavior, telemetry, result capture, and production Promote safeguards.
- [x] Specify a 2D map-first execution/operations console with common device/task actions and closed-loop issue handling under Orchestration/WCS ownership; keep environment setup, simulation, real-device Promote, and read-only SCADA projection in the site-visualization specification.
- [x] Add tests for invalid maps, deploy rollback, simulator/real adapter parity, stale/duplicate feedback, read-only enforcement, role denial, unsafe promotion, and audited override.
- [x] Review each command's ownership and distinguish software dispatch from physical safety-PLC responsibility.

**Acceptance:** A complete no-hardware simulation lifecycle and an explicitly bounded production handoff are described and testable.

## Task 7: Document Route Current State and Research Applicability

**Files:**
- Create: `WES.Master/docs/route/01-as-is-and-research-review.md`

- [x] Inventory all Seven self-developed WCS Route, path-dispatch, traffic, reservation, map-version, state-feedback, and Route test files.
- [x] Describe Stacker and FourWay algorithms and list observable limitations with citations to exact source and test locations.
- [x] Summarize the two local Route papers: online goal/precedence coordination and constraint-level cycle detection.
- [x] Separate paper assumptions from WES facts; identify required pose freshness, kinematics, conflict geometry, clock, communication, stop/wait, and recovery assumptions.
- [x] Mark which theory can be adapted, which assumptions need validation, and what cannot be claimed before validation.
- [x] Review the analysis for unsupported performance/safety claims or incorrect attribution.

**Acceptance:** Each current-state claim links to evidence, and each research-derived claim is labeled as a hypothesis or validated requirement.

## Task 8: Define Target Route Architecture and Detailed Test Catalog

**Files:**
- Create: `WES.Master/docs/route/02-target-routing-and-scheduling.md`
- Create: `WES.Master/docs/route/03-route-test-catalog.md`

- [x] Define graph/map validation, immutable map snapshots, edge/node/conflict-zone resources, vehicle state, telemetry freshness, and version transitions.
- [x] Define single-vehicle candidate route costs and deterministic tie-breaks separately from fleet-level safety constraints.
- [x] Define time-window reservations, atomic grant/release, ownership/lease, priority aging/fairness, online goals, and safe replanning.
- [x] Define constraint graph/wait-for relation, cycle detection candidate, precedence revision, recovery actions, manual escalation, and bounded assumptions.
- [x] Keep Stacker, FourWay, and External execution differences explicit while sharing only stable contracts and test utilities.
- [x] Write test cases for valid/invalid maps, reachability, capacity, equal costs, intersections, head-on conflict, overlapping windows, online tasks, waiting cycles, fairness, stale/out-of-order feedback, cancellation cleanup, communication failure, safe recovery, property tests, and multi-vehicle simulation.
- [x] Define a reproducible benchmark protocol; defer numeric thresholds until representative maps, vehicle counts, task rates, and latency targets are confirmed.
- [x] Review safety invariants and liveness assumptions independently; do not equate cycle detection with a universal liveness proof.

**Acceptance:** Every Route requirement maps to an invariant and at least one deterministic or simulation test; no unsupported guarantee or unmeasured performance threshold is asserted.

## Task 9: Publish Engineering Governance and Traceability

**Files:**
- Create: `WES.Master/AGENTS.md`
- Create: `WES.Master/docs/03-engineering-governance.md`
- Create: `WES.Master/docs/04-requirement-design-test-traceability.md`
- Create: `WES.Master/docs/adr/README.md`
- Modify: `WES.Master/README.md`

- [x] Define backend project/module conventions, frontend Vue conventions, naming, API/versioning, migration, logging, error handling, and security review rules.
- [x] Define meaningful comment coverage, XML documentation rules, algorithm/transaction/protocol rationale requirements, test intent comments, and stale-comment review.
- [x] Define feature-document template with problem, actors, flow, state, data invariants, contracts, permissions, failure/compensation, operations, tests, and references.
- [x] Define requirement/test ID grammar and a traceability table with status and evidence.
- [x] Define ADR format, decision owners, PR checklist, module dependency checks, docs freshness, and CI gates.
- [x] Add a concise AGENTS.md that points to canonical docs and forbids source generation or cross-module Infrastructure dependencies.
- [x] Review that root instructions summarize rather than duplicate the canonical standard.

**Acceptance:** A future contributor can find the authoritative rule for each architecture, code, comment, documentation, test, and change-management question.

## Task 10: Publish Cross-Module Acceptance Matrix and Final Audit

**Files:**
- Create: `WES.Master/docs/acceptance/01-cross-module-and-provider-matrix.md`
- Modify: `WES.Master/README.md`
- Modify: `WES.Master/docs/00-project-startup-design.md` only when approved decisions change

- [x] Map Platform → WMS → Bus → WCS → Adapter → feedback → WMS ledger end-to-end cases.
- [x] Map PDA online operation and read-only offline behavior, external system contracts, Simulator-to-device adapter parity, and operator recovery cases.
- [x] Define the SQL Server/PostgreSQL/MySQL test matrix, provider-specific behavior recording, migration tests, and deployment checks.
- [x] Include accessibility, security, reliability, observability, backup/restore, and performance acceptance areas with owner/evidence fields.
- [x] Verify every feature disposition links to a detailed design and test case; every test case links back to a requirement.
- [x] Verify all relative Markdown links, document names, section references, and status labels.
- [x] Search all WES docs for unresolved placeholders, conflicting ownership, unbounded safety claims, and generated-code assumptions.
- [x] Confirm no solution, source file, migration, or scaffold was created.

**Acceptance:** The documentation set is internally consistent, navigable, traceable, and contains no unmarked placeholders or missing feature dispositions.
