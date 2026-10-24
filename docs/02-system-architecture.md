# WES.Master 系统架构

页面/功能/详细测试入口：[全模块设计索引](detailed-design/README.md)、[系统运维与恢复手册设计](detailed-design/system-operations.md)。原架构与ADR继续约束深化文档。

- 状态：目标架构基线；供后续模块详细设计遵循
- 依据：[已批准启动设计](00-project-startup-design.md)、[业务范围与现状矩阵](01-business-scope-and-status.md)
- 范围：模块边界、依赖方向、数据所有权、接口/事件、运行和部署形态。已批准按计划创建必要工程；仍禁止生成式业务代码与空层堆叠。

## 1. 架构目标与决策

WES 以**模块化单体**启动：一个产品部署、一个受控运行时，按业务域界定代码、数据、迁移和团队所有权。WMS/WCS 核心采用领域模型保护库存、运输和执行不变量；跨模块用例通过明确应用契约组织，不把整个系统拆成共享数据库的无边界分层大单体。

| 决策 | 采用方式 | 约束/代价 |
|---|---|---|
| 部署单元 | 单一 WES Host；按模块可观测、可配置 | 模块不可因同进程而访问其他模块私有数据或绕过接口 |
| 业务边界 | Platform、Workflow/Form、WMS、Orchestration、各 WCS 包、Device Integration、SCADA、Simulator、Notifications | 模块各自拥有业务规则、表及迁移；WCS 运维不是独立域模块，跨包流程归 Orchestration、包内命令归对应 WCS Pack |
| 用例组织 | Vertical Slice 在模块内部组织输入、校验、处理、结果和测试 | 不能以每个 CRUD 强制建立空层；Domain/Application/Infrastructure 只在承担真实职责时保留 |
| 领域复杂度 | WMS 库存账本、运输编排、Stacker/FourWay 调度采用 DDD 不变量保护 | 不把实体贫血化为任意 setter，也不把所有平台 CRUD 过度建模 |
| 数据库 | EF Core；MySQL先行，SQL Server/PostgreSQL延后兼容 | [ADR-0002](adr/ADR-0002-mysql-first-implementation.md)；Provider差异归owner，逐Provider验收前不宣称等价 |
| 交互 | 运维和 Simulator 为 Vue 3 + Vite + TypeScript；PDA 保留 UniApp + Vue 3 + TypeScript | UniApp 平台 API 不能泄漏到领域/API 契约；Frontloom 规则结合平台生命周期使用 |
| 设备接入 | 统一设备语义契约 + 可替换协议 Adapter | 业务模块不解析 PLC/供应商报文；仿真与真机 adapter 一致遵守语义契约 |
| 代码生成 | 不使用 Builder、业务源码模板或依赖生成器才能维护的功能 | 运行时动态表单属于独立配置能力，不能等同源码生成器 |

微服务不作为初始形态：库存与运输的跨域一致性、设备反馈时序、Route 热状态和现场诊断在当前阶段更依赖清晰的领域边界与事务/补偿设计。将来只有在独立扩缩容、故障隔离、组织自治等可度量需求成立，且数据所有权和接口契约已稳定后，才可通过 ADR 评估拆分。

## 2. 逻辑结构

下图表示逻辑模块和依赖约束，不是需要机械创建的项目清单：

```text
Clients
  ├── ops-web       Vue 3 + Vite + TypeScript; 2D map-first WCS execution/operations console
  ├── pda           UniApp + Vue 3 + TypeScript
  └── simulator     Vue 3 + Vite + TypeScript
           │ versioned business APIs / SignalR
           ▼
WES Host (composition root, HTTP adapters, auth, middleware)
  ├── Platform             Identity / RBAC / security / audit / config
  ├── WorkflowForm         Workflow / runtime form schema
  ├── Wms                  master data / inventory ledger / documents
  ├── Orchestration        transport orders / legs / handover / compensation
  ├── Wcs.Stacker          Stacker allocation / tasks / Route / traffic
  ├── Wcs.FourWay          FourWay allocation / tasks / Route / hoist
  ├── Wcs.External         supplier-facing WCS clients / codecs
  ├── DeviceIntegration    semantic command / feedback / protocol adapters
  ├── Scada                read-only operational projection
  ├── Simulator            map validation / virtual execution / promotion
  └── Notifications        alarm / SignalR / mail / MQ consumers
           │
           ├── SQL Server / PostgreSQL / MySQL (module-owned schemas/tables)
           ├── RabbitMQ / Outbox (when enabled)
           ├── Redis/cache and HotStore (where required)
           ├── file/object storage
           └── real or simulated device adapters
```

Host is a composition root and transport adapter. It configures modules, authentication middleware, exception mapping, observability and endpoint exposure; it does not implement inventory, workflow, Route or device business rules.

### 2.1 Recommended logical layout

```text
src/
  Host/
  Modules/
    Platform/
    WorkflowForm/
    Wms/
    Orchestration/
    Wcs/
      Stacker/
      FourWay/
      External/
    DeviceIntegration/
    Scada/
    Simulator/
    Notifications/
  Shared/
    BuildingBlocks/     # only proven cross-cutting primitives; never shared domain state
    TestKit/             # reusable test fixtures, not production domain logic
clients/
  ops-web/
  pda/
  simulator/
tests/
  Architecture/
  Modules/
  Contracts/
  EndToEnd/
```

This is a **logical boundary map only**. A physical project is added only when it enforces a real dependency boundary or reduces ownership conflicts. Do not create empty projects, generate CRUD scaffolding, or split a module into projects solely to match a diagram.

## 3. Module responsibility and ownership

| Module | Owns | Exposes | Must not do |
|---|---|---|---|
| Platform | User/role/organization, authentication policy, authorization, data scope/tenant, dictionaries/config, audit/security events, file metadata, readiness/health/metrics foundations | Identity and authorization abstractions, configuration lookup, audit, file and health/metrics contracts | Own WMS/WCS lifecycle rules; treat client menu visibility as authorization |
| WorkflowForm | Workflow definition/instance/task state, runtime form schema/version and validation | Start/approve/reject/query contracts and business-hook contracts | Generate source code; mutate domain tables without an owning module contract |
| Wms | Warehouse/location/container master data, stock balance/ledger, inbound/outbound/cycle-count and WMS document state | Stock and document application contracts; transport intent; WMS read projections | Call WCS implementation details or directly control a device; keep a second inventory ledger |
| Orchestration | Transport order, execution leg, pack selection, handover, retries, cancellation/completion coordination, cross-pack operator workflows, persistent operational issues and read projection | Transport and WCS-operations coordination contracts; map-oriented operational read model; durable integration events | Change stock quantities directly; import Stacker/FourWay private models; override pack-owned safety checks |
| Wcs.Stacker | Stacker topology and location rules, allocation, WCS tasks, path dispatcher, traffic reservations, feedback and pack-level operational command validation | Pack capability/command, operational query/command contract and normalized execution status | Write inventory quantity; share internal Route state with FourWay; accept unvalidated operator overrides |
| Wcs.FourWay | FourWay topology, layer/aisle allocation, vehicle/hoist state, Route, traffic, feedback and pack-level operational command validation | Pack capability/command, operational query/command contract and normalized execution status | Write inventory quantity; trust stale vehicle telemetry as current; accept unvalidated operator overrides |
| Wcs.External | Supplier endpoint configuration, authentication/codec, request correlation, callback normalization and interface diagnostics | External pack adapter implementing orchestration-facing contracts | Leak supplier DTOs/protocols into WMS; treat untrusted callbacks as authoritative without validation |
| DeviceIntegration | Device semantic command/feedback, connection/session abstraction, adapter lifecycle and protocol mapping | Versioned device adapter contract and health/diagnostic status | Own WMS documents, choose inventory moves or weaken WCS safety checks |
| Scada | Read-only state projection and bindings for map/device/location/task display, including freshness and unmapped-state indicators | Query-only operational views consumed by the map-first Ops web client | Own operator workflows or mutate stock, WCS state or device output |
| Simulator | Map edit/validation/deploy version, virtual device behavior, simulation run/results and guarded real-device promotion workflow | Map, simulation and guarded promotion contracts; semantic virtual adapter | Own live WCS operations; bypass production validation/authorization or treat simulated behavior as proof of physical safety |
| Notifications | Alarm lifecycle, delivery state, SignalR and mail dispatch, news/announcement delivery | Notification and alarm contracts | Be the source of truth for the domain condition that raised an alarm |

`Seven.Business`, `Biz_` prefixes and old extension hooks are baseline patterns, not mandatory WES project names. WES preserves the extensibility intent through module-owned extension contracts and explicit business handlers; extension behavior must compose standard WMS services and must not duplicate inventory or WCS state.

The 2D map-first operating experience is a client composition, not a new domain owner: it combines SCADA/read projections with Notifications, Orchestration, DeviceIntegration and pack-owned WCS contracts. Orchestration owns persistent cross-pack operational issues and their assignment/escalation lifecycle; Notifications owns alarm delivery/acknowledgement/clear state; each WCS Pack owns package execution state and final control validation. The issue record links to, but does not replace, its source alarm or domain event.

## 4. Dependency rules and integration patterns

### 4.1 Compile-time dependency direction

1. Each module's inbound HTTP, message and device adapters call that module's application/use-case contract.
2. Application/use-case code depends on its own domain and on explicitly declared inbound/outbound abstractions.
3. Infrastructure implements abstractions owned by the module that consumes the capability; EF Core, Redis, MQ and vendor SDK details remain there.
4. Cross-module calls reference a published application contract or versioned integration contract, never a private entity, `DbContext`, repository, Infrastructure assembly or internal table.
5. Shared BuildingBlocks contain technical primitives only (for example correlation metadata); never place shared WMS/WCS entities, global mutable state, universal repositories or generic business managers there.
6. Host may reference module composition contracts to wire them. A module must not reference Host.
7. UI clients use public versioned API contracts and read projections; they do not query another module's database/schema directly.

### 4.2 Allowed domain collaboration

| Caller | Allowed collaboration | Forbidden shortcut |
|---|---|---|
| WMS → orchestration | Publish a transport intent with idempotency/correlation; accept completion through an explicit WMS application handler | WMS creates Stacker/FourWay task rows |
| Orchestration → WCS | Select pack and dispatch a leg through a pack contract; persist leg state and correlate outcome | Import a pack's Route planner or use its DbContext |
| WCS → WMS | Request/read the minimum authorized location/material context via contract; report normalized completion | Directly deduct stock or complete an outbound document |
| WCS → device | Send supported semantic commands and consume normalized feedback | Use PLC register addresses in route/domain logic |
| Simulator → execution | Run the same semantic contracts behind a virtual adapter and versioned map | Add simulator-only business success paths unavailable to real execution |
| SCADA → operational state | Consume read-only projections or query contracts | Write state by calling internal service or database |
| Workflow → business domain | Call an approved module-owned workflow hook/command | Change arbitrary domain tables based on dynamically typed form fields |

### 4.3 Sync, events, transactions and consistency

- Use synchronous module contracts when the caller needs an immediate decision and the contract owner controls the invariant.
- Use durable integration events for cross-process/long-running work, notifications or retryable side effects. Events have a stable ID, schema version, correlation/causation IDs, timestamp and idempotency semantics.
- When a business state change and event publication must be atomic, persist them via an Outbox owned by the writing module and publish asynchronously. A publisher retry must be safe.
- Local ACID transactions are confined to one module's owned data. Do not imply a single transaction spans WMS, Orchestration and WCS; define a saga/compensation state machine, timeouts and operator recovery instead.
- Consumers persist deduplication state or use naturally idempotent transitions. Out-of-order events are rejected, deferred or reconciled according to explicit aggregate version/state rules.
- SignalR, e-mail, logs and metrics are delivery/observation surfaces, not authoritative business stores.

## 5. Data ownership and provider strategy

| Data set | Owner | Read access for other modules | Write policy |
|---|---|---|---|
| Identity, roles, tenant/security configuration | Platform | Authorization/config contracts | Platform use cases only |
| Workflow definitions and workflow instances | WorkflowForm | Workflow query/decision contracts | Workflow use cases only |
| Warehouse/location/container master data; stock balance and immutable stock ledger | Wms | WMS queries and explicitly scoped read contracts | WMS ledger handlers only |
| WMS documents and reservations | Wms | Document/status query and completion contracts | WMS use cases only |
| Transport orders, legs, pack selection and handover | Orchestration | Orchestration contract/read model | Orchestration use cases only |
| Pack topology, route reservations, WCS task/vehicle state | Corresponding WCS pack | Pack capability and normalized status contract | Owning pack only |
| Device connection/session/diagnostics | DeviceIntegration | Health/telemetry contract with freshness metadata | Adapter owner only |
| Deployment manifests and simulation results | Simulator coordinates versioned deployment preparation/activation and owns simulation results; WMS, each WCS Pack, and SCADA own their respective immutable map/location/topology/view artifacts | Deployment manifest, validated owner-version references, simulation result contract | Simulator coordinates deployment but cannot mutate another module's private map artifacts; production activation is guarded |
| HotStore/cache entries | Platform owns storage adapter; the corresponding domain module owns each key/schema and recovery policy | Explicit cache/query contract | Never authoritative for inventory or physical occupancy; a cache miss/expiry cannot silently grant a resource |
| Alarm delivery | Notifications | Query/delivery contract | Notifications only |
| WCS operational projections and command state | Orchestration for cross-pack coordination; corresponding WCS Pack for package state | Read/command contract with the owning module | Owning Orchestration/WCS module only; audit records remain Platform-owned |

Map deployment is coordinated by Simulator through an immutable deployment manifest. The manifest records exact immutable version references and hashes prepared by each required owner; its active pointer is the coordinated activation authority. WMS, each WCS Pack, and SCADA retain ownership of their own map/location/topology/view artifacts. Cross-module preparation is a recoverable workflow, not a distributed transaction; consumers must validate the active deployment and their prepared artifact before acting.

- One physical database is acceptable initially, but module ownership remains mandatory. Each module owns its table/schema definition, EF configuration and migration history (or an explicit deployment migration group).
- No cross-module foreign key is assumed. Cross-module references use stable IDs and contracts; referential lifecycle and deletion behavior are documented.
- Stock ledger entries are append-only business facts. Corrections use compensating entries, not silent historical edits.
- Provider-specific SQL/types/indexes are isolated behind module persistence implementations. Prefer provider-neutral EF/LINQ and portable constraints; when behavior differs, record a provider capability or an explicit alternative.
- Test concurrency/locking and transaction assumptions on every supported provider. SQLite/in-memory tests are not evidence of SQL Server/PostgreSQL/MySQL production semantics.
- Redis/HotStore is a derived/runtime state store, not the authoritative inventory ledger or sole durable source for work that must survive restart.

## 6. API and device boundary contracts

### 6.1 Business APIs

- API versioning and compatibility follow a documented contract policy; public request/response DTOs are not EF entities.
- Mutating operations specify authentication/authorization, validation, idempotency, concurrency token, correlation, expected prior state and resulting state.
- List/query endpoints declare pagination, stable ordering, data-scope enforcement and stale/read-model semantics.
- API errors use a stable error code and safe message; infrastructure details and device secrets are not returned to clients.
- PDA and web clients call the same business authority and permission rules; presentation differences do not fork ledger semantics.

### 6.2 Device commands and feedback

The semantic boundary expresses supported operations such as target assignment, start/pause/cancel where physically supported, and normalized position/status/completion/fault feedback. It does not expose register maps as business contracts.

Every command/feedback envelope needs a command/event ID, pack/vehicle/device ID, correlation and sequence/version, map version where applicable, occurrence/receive times, payload schema version and validated source. Adapter contracts define timeout, retry, reconnect, duplicate/late feedback, unsupported operation and diagnostic behavior. Device protocol/firmware-specific mapping stays in an adapter.

The interface is not a safety certification. Physical stopping, collision avoidance, emergency stops and safety-rated interlocks remain subject to approved hardware and site safety design.

## 7. Runtime and deployment shape

- One Host composes modules and serves APIs. [Feature switches](detailed-design/feature-switches.md) separate client-only module UI visibility from explicitly declared optional runtime capabilities; configuration is deployment-scoped, strongly typed, validated at startup and immutable until restart. Hiding a UI route never changes server-side authorization.
- Hosted services (Outbox, Quartz, device connections, HotStore warm-up) declare startup ordering, readiness dependency, shutdown/cancellation, singleton ownership and disabled-mode behavior.
- External integrations (SQL provider, Redis, RabbitMQ, MinIO, SignalR and device endpoints) are independently configurable and health-checked. Missing required configuration is an explicit startup/readiness failure, not a silent no-op success.
- For single-instance deployments, in-process coordination is allowed only if restart recovery is specified. Multi-instance operation requires durable leases/locks, idempotent workers and tested ownership; no hidden in-memory singleton may be the only scheduler or Route reservation authority.
- Deployment, backup/restore, secret injection, schema migration and rollback are defined in the operations/acceptance documents before production rollout.
- Operational dashboards, metrics and logs carry correlation, module, operation, document/transport/leg, device and map-version dimensions where useful; avoid credentials and unnecessary personal data.

### 7.1 System operations requirements

| Requirement | 必须满足 | 验收 |
|---|---|---|
| REQ-OPS-006 | 恢复到隔离环境必须保持 WMS 账本、活动 WCS 工作和已部署地图版本的一致性，并阻止该环境意外连接生产设备。 | SITE-TC-017 |
| REQ-OPS-007 | 缺少有效的每 Pack 单写者租约/fencing token 时，系统必须拒绝控制写入；只可开放部署策略明确允许的只读能力。 | SITE-TC-018 |
| REQ-ARCH-008 | 部署功能开关必须区分仅影响客户端入口的模块 UI 开关与显式可选运行能力开关；配置须启动校验，且任何开关不得削弱认证授权、租户隔离、审计、幂等或 H 现场安全门禁。 | ARCH-TC-011 |

上述要求属于设计约束，未实现和验收前保持 `designed`；其验收结果见跨模块矩阵。

## 8. Architecture verification cases

| Test ID | Requirement | Verification and expected result |
|---|---|---|
| ARCH-TC-001 | Module dependency direction | Static architecture test rejects Domain/Application referencing Host, another module's Infrastructure/DbContext/private entities, or device SDKs |
| ARCH-TC-002 | Data ownership | Cross-module persistence test confirms only the owner module can mutate its aggregate/table; no shared mutable entity assembly |
| ARCH-TC-003 | Inventory authority | WCS task completion changes execution state and emits a completion contract; stock changes only through WMS handler and exactly once |
| ARCH-TC-004 | Durable publication | Inject failure between WMS state commit and broker availability; Outbox eventually publishes once logically, with duplicate delivery harmless |
| ARCH-TC-005 | Provider compatibility | Run persistence/transaction/concurrency/migration suite for SQL Server, PostgreSQL and MySQL; provider deviations are reported and approved |
| ARCH-TC-006 | Adapter parity | Run the same semantic command/feedback contract suite against virtual and each implemented real adapter; unsupported capabilities fail explicitly |
| ARCH-TC-007 | Module migration ownership | Apply and roll back/forward module migrations in documented order without touching another module's private schema |
| ARCH-TC-008 | Authorization boundary | Direct API call without permission is denied even if a menu is hidden or client state is modified |
| ARCH-TC-009 | Restart recovery | Restart Host during an Outbox publish, active job, active transport and adapter reconnect; durable state is recovered or operator-visible as unresolved |
| ARCH-TC-010 | PDA contract parity | UniApp and web clients receive the same validation, idempotency and authorization outcome for equivalent business requests |
| ARCH-TC-011 | Feature-switch boundary | Verify UI-only switches do not unload backend APIs or bypass authorization; runtime switches have validated Disabled/Degraded behavior; no switch weakens tenant, audit, idempotency or H safety gates |

## 9. Decision/change control

Architecture changes that alter module ownership, transaction boundaries, provider support, UI runtime, device command semantics or safety responsibilities require an ADR and corresponding update to this document, affected module designs, traceability matrix and tests. A code change may not establish a new architecture rule implicitly. See [工程治理](03-engineering-governance.md) and [ADR 规范](adr/README.md) when published.
