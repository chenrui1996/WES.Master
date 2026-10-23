# WES.Master 项目启动设计规格

- 状态：已批准；可依据新证据进行事实性修订
- 项目阶段：启动设计与文档整理；不包含源代码和脚手架
- 目标框架：.NET 10
- 前端基线：运维端/Simulator 使用 Vue 3 + Vite + TypeScript；PDA 保留 UniApp（Vue 3 + TypeScript）

## 1. 项目目标

从 Seven.Master 的既有业务重新开始建设 WES.Master。目标是完整盘点并保留 Seven.Master 已有业务能力，移除源码生成器，改善模块可读性与功能完整性，并重构所有自研 WCS Route 寻路/调度算法。

本阶段交付可供团队实施和审查的项目文档：完整业务大纲、系统和模块架构、工程/注释/文档规范、Route 现状与目标设计、逐功能测试用例及跨模块验收追踪。未完成文档审阅前不进入编码阶段。

## 2. 输入资料与证据规则

主要基线：

- [Seven.Master 文档索引](../../Seven.Master/doc/README.md)
- [WMS/WCS 实现说明](../../Seven.Master/doc/20-WMS与WCS实现说明.md)
- [WMS/WCS 包架构](../../Seven.Master/design/2026-08-29-wms-wcs-pack-architecture.md)
- [WMS/WCS 实现指南](../../Seven.Master/design/2026-08-29-wms-wcs-implementation-guide.md)
- [功能开关矩阵](../../Seven.Master/doc/14-功能开关.md)
- [测试方法与落库回归](../../Seven.Master/doc/keypoint/09-测试方法与落库回归.md)
- [Route 研究资料目录](../../Route)
- [系统架构设计参考](../../系统架构设计.md)

梳理时按证据分级记录：`既有且有代码/测试证明`、`既有但部分完成`、`文档规划/预留`、`WES 新增或补齐`、`明确移除`。文档或名称存在但缺少实现/测试证据时，不得写成“已完整实现”。

## 3. 已确认的决策

| 决策项 | 已确认方向 |
|---|---|
| 业务基线 | Seven.Master 业务完整保留；以代码、文档、测试核实状态 |
| 代码生成 | 移除 Builder/源码生成模块、模板、API 和生成产物依赖 |
| PDA | 纳入 WES；保留并复核现有拣选；补齐发运、立库异常处理；离线只读 |
| 后端 | .NET 10；迁移 Seven.Master 已验证的通用技术设计 |
| 前端 | 运维端/Simulator 用 Vue 3 + Vite + TypeScript；PDA 保留 UniApp（Vue 3 + TypeScript）；按 Frontloom Core FE 与 Vue 规范 |
| 数据库 | 保留 SQL Server、PostgreSQL、MySQL provider 切换能力 |
| 团队 | 预计 6–15 人，按业务域分工 |
| 架构 | 模块化单体；WMS/WCS 核心采用 DDD 保护领域不变量，用例纵切组织流程 |
| Route 优先级 | 安全和活性优先，其后优化吞吐与路径代价 |
| Route 输入 | 设计纳入车辆实时状态、路径冲突区、占用时间/等待状态及恢复输入 |
| 集成边界 | 向上开放业务接口和单据扩展；向下以设备适配器接入不同协议 |
| 当前交付 | 文档先行；本阶段不写业务代码、不生成项目脚手架 |

## 4. 业务范围大纲

下列清单是后续完整业务矩阵的目录框架。各功能要进一步拆成业务规则、用例、状态、数据不变量、权限、异常补偿和测试；“保留”最终须由现有实现证据确认。

### 4.1 平台基础能力

- 身份认证：登录/登出、JWT、Refresh Token 轮换、密码哈希、验证码及安全策略。
- 用户、角色、部门/组织、菜单、按钮、API 权限及 RBAC。
- 数据范围、租户隔离、动态菜单、路由/页面访问控制。
- 字典、配置、国际化、文件上传/下载、本地及对象存储。
- 操作/登录/审计/接口日志、异常码、统一错误响应。
- 公告/新闻维护及在运维端、PDA 等客户端的展示（以现有实现证据确认范围）。
- 健康检查、指标、请求链路、限流、防重复提交、部署运维。

### 4.2 流程和平台扩展

- 工作流/审批、步骤和审批人、条件分支/会签、动态表单及业务接入。
- Quartz 任务与执行日志。
- RabbitMQ/MassTransit 消息、Outbox、幂等、失败重试与补偿。
- 告警码、告警确认/清除、SignalR 实时通知、邮件。
- 缓存、HotStore 预热/热状态/持久化及运行监测。

动态表单是运行时业务配置能力，不等同源码生成器；它在移除 Builder 后是否需调整数据模型与 UI，须在 Workflow/Form 规格中核验。

### 4.3 WMS 与单据

- 仓库、库区、层、巷道、货位、容器、库位前缀和 WCS 包绑定。
- 库存权威、可用/预留数量、库存流水、收货/发运/移库记账。
- 入库订单、明细、收货、组盘、目标位分配、运输和完成回写。
- 出库订单、审核与库存预留、组号/优先级、拣选任务、拣选确认、发运。
- 盘点计划、实盘录入、差异确认和库存调整。
- 内部移库、跨包交接、异常单据及取消/重试/补偿。
- PDA 登录、任务列表、扫码和在线操作；离线仅只读缓存，不保留待提交写操作。

规则：WMS 账本是库存权威；WCS 包和设备适配器不得私自扣减或维护第二套可扣减库存。

**PDA 证据差异**：Seven 的 [WMS/WCS 实现说明](../../Seven.Master/doc/20-WMS与WCS实现说明.md)称拣选尚未实现，但当前 [PDA 控制器](../../Seven.Master/Seven.Net8/Seven.WebApi/Controllers/Pda/PdaController.cs)含 `picking/pending` 与 `picking/confirm`，且存在拣选页面。因此 WES 将拣选列为“现有代码待完整测试与业务验收”，不作为重复新增功能；发运和立库异常处理仍列为待补齐/待验收能力。离线策略已确认：只读缓存，不保留待提交写操作。

### 4.4 编排与 WCS

- Orchestration Bus：运输单、Leg 拆分/激活、幂等、交接点、失败暂停和完成回写。
- Stacker：巷道/货位分配、双深规则、挡路移库、执行栈拆段、容量与路径流量。
- FourWay：层/巷/位分配、多车交通、停车位预约、跨层提升机三阶段、路径反馈。
- 外部 WCS：HTTP/MQ 传输、供应商 Codec、回调归一、接口日志及错误重试。
- WCS 运维：运维 Web 以 2D 监控地图为主工作台，尽可能呈现所有可定位设备、任务与告警，并就地处理常见问题；Orchestration 提供跨包协调，Stacker/FourWay 在各自模块内执行包级模式、联锁、任务取消/强制完成和最终安全校验。高风险操作须授权、说明原因及二次确认/必要的双人审批。
- BoxSort：Seven 当前预留边界；没有现成完整能力证据，不视为首期已实现。

各自研 WCS 包保留独立领域路网、流量和设备模型；共用的是明确的业务/执行契约与可复用验证工具，不是强行合并成一张路网。

### 4.5 环境搭建与现场可视化

- DeviceComm：设备接入环境、连接管理、驱动/协议、点位、读写、规则、告警和实时状态。
- 标准设备语义命令与反馈；真实协议由 adapter/plugin 提供实现。
- SCADA：只读视图、节点绑定、设备/库位/任务状态展示。
- Simulator：地图编辑、校验、部署、虚拟设备反馈、仿真测试、结果审查与 Promote 真机切换。
- WCS 运维控制属于 Orchestration/WCS 范围；SCADA 提供只读地图状态投影，运维 Web 以同一地图为主交互入口并调用跨包编排或包级运维契约。
- 前端：运维 Web、PDA、Simulator 三类应用；各自功能、角色、错误和空状态需独立规格。

## 5. 目标架构

### 5.1 决策与取舍

1. **模块化单体（选定）**：单一产品部署、按业务域定义边界；降低跨域改动和协作冲突，同时保留后续独立部署演进空间。
2. Seven 式共享分层单体：迁移成本低，但易保留当前跨业务域耦合和可读性问题，不作为目标结构。
3. 微服务：隔离度高，但会将库存一致性、运输协调、Route 运行状态和设备时序带入分布式事务/部署复杂性；当前不作为起步架构。

### 5.2 建议模块

```text
WES.Host
  ├─ Platform                 Identity / RBAC / Security / Audit / Config
  ├─ WorkflowForm             Workflow / Dynamic Forms
  ├─ Wms                      Master Data / Inventory Ledger / Documents / PDA contracts
  ├─ Orchestration             TransportOrder / Leg / Handover / Compensation
  ├─ Wcs.Stacker               Stacker domain, allocation, path and traffic
  ├─ Wcs.FourWay               FourWay domain, allocation, path, traffic and hoist
  ├─ Wcs.External              External system transport and codecs
  ├─ DeviceIntegration         Device semantic contract and protocol adapters
  ├─ Scada                     Read-only operational visualization
  ├─ Simulator                 Map validation, deploy and simulation contracts
  └─ Notifications             Alarm / SignalR / Mail / MQ integration

apps/
  ├─ ops-web                   Vue 3 + Vite + TypeScript; WCS operations use Orchestration/WCS contracts
  ├─ pda                       UniApp + Vue 3 + TypeScript; mobile client
  └─ simulator                 Vue 3 + Vite + TypeScript
```

模块内部按 Domain / Application / Infrastructure / API adapter 分层，并可按用例纵切组织 handler/service、validator、DTO 和测试。代码结构是边界约束，不要求为每个简单 CRUD 机械地产生多层空壳。Host 负责装配和暴露 API，不实现领域规则。

### 5.3 模块契约与数据所有权

- 每个模块声明拥有的实体、表、迁移、API/事件和业务不变量。
- 跨模块使用版本化应用契约、业务接口或集成事件；禁止直接依赖别的模块 Infrastructure、DbContext 或内部实体。
- 模块可部署在同一进程/数据库；数据库 schema/table ownership 与迁移仍按模块划分。
- 不默认承诺跨模块 ACID；先记录事务边界、Outbox、幂等和补偿策略，再定具体流程。
- 上游集成只看到业务单据/API/事件；下游设备协议只在适配器内转换。

## 6. 黑盒接口和适配器边界

### 6.1 向上：业务与单据扩展

- 提供稳定、版本化、带幂等键的业务 API/事件契约。
- 业务扩展以模块化单据、校验/生命周期 hook 和明确的扩展点加入；不得直接修改其他模块内部表/状态。
- API 使用业务语言表达运输意图、收发货、库存和状态，不暴露 PLC 地址、报文、设备私有码。
- 外部 ERP/MES、Web、PDA 和模拟器基于相同业务权限、验证及审计边界。

### 6.2 向下：设备语义与协议插件

- WCS 面向设备语义接口提交目标、启动/暂停/取消等受支持命令，并接收位置、状态、完成、故障等归一反馈。
- Adapter 处理协议会话、点位/报文映射、连接恢复、超时、重复/乱序消息、关联标识、告警和诊断。
- 协议类型与供应商可扩展；具体支持列表、实时性和控制权限须经单独的设备接入规格确认。
- 仿真 adapter 实现相同契约，用于无真机的功能、Route 和故障恢复验证。
- Adapter 不绕过 WMS/WCS 状态机写业务账本；业务层也不解析设备原始报文。

## 7. Route 重构大纲

### 7.1 现状证据与问题假设

- [FourWayRouter](../../Seven.Master/Seven.Net8/Seven.Infrastructure/Wcs/Packs/FourWay/FourWayRouter.cs) 当前按边权重执行 Dijkstra；路径点间权重相同的确定性、路网有效性及资源冲突的处理边界需要在专项设计核实。
- [StackerRouter](../../Seven.Master/Seven.Net8/Seven.Infrastructure/Wcs/Packs/Stacker/StackerRouter.cs) 当前过滤满容量边并加入占用惩罚，再执行 Dijkstra；调度容量/拥堵的实际成本语义与代价校准尚需定义。
- Seven 中 Stacker 与 FourWay 分别有 path dispatcher、Route/Map 数据和 WCS 测试；当前实现概览见 [WMS/WCS 说明](../../Seven.Master/doc/20-WMS与WCS实现说明.md)。
- 根目录 [Route 资料](../../Route) 包含多机器人在线目标协调与约束级活性检测论文。论文里的空间包络、连续运动和协调假设需要映射到 WES 的节点/边/设备区模型，不能直接宣称证明适用于 WES。

### 7.2 目标子系统

1. **地图与图快照**：检查节点/边引用、方向、容量、权重、禁行、包/层/地图版本；每次规划绑定一致的地图版本。
2. **拓扑和冲突区**：除有向边，还表示节点、交叉点、狭窄通道、提升机口/设备区等互斥资源；为资源定义安全间距、占用与释放条件。
3. **单车候选路径**：在满足设备能力、地图版本和资源限制的图上生成确定性可行路径；成本可分解为距离/时间、拥堵、切换/等待和业务优先级，权重由基准测量确定。
4. **在线任务/状态协调**：接收新目标、当前车辆位置/方向/模式、已执行片段、状态时间戳和通信健康度；重新规划不可破坏已授权/执行中的安全约束。
5. **时空预约**：对冲突区、边和节点执行带时间窗的占用预约；同资源请求必须具备并发原子性、租约/超时策略、所有者标识和可幂等释放。
6. **优先级与公平性**：区分业务优先级和交通让行顺序；避免低优先级任务永久饥饿，规定老化、让行和重排策略。
7. **活性检测与恢复**：将等待/先后关系建模为约束图；在适用前提满足时评估使用论文的约束转换图 cycle 检测（论文提出精确多项式测试，并给出结构化 `O(M log M)` 表示）；检测环后重排优先级、释放可撤销预约或执行安全恢复动作，无法自动恢复时报警并进入明确定义的安全停机/人工处置状态。
8. **逐段执行**：只授权下一安全片段；状态反馈校验位置、序列和时间；失败/取消/超时/重规划必须回收所有关联预约并保留诊断信息。
9. **包内自治**：Stacker 保留其执行栈、巷道/双深和 RouteFlow 语义；FourWay 保留层内多车、停车位、提升机和交通语义；External WCS 使用外部回调边界，不接入自研拓扑。

### 7.3 目标函数与保证声明

采用分层目标而非用单一加权和掩盖安全：

1. 安全约束必须满足：不允许违反设备/资源冲突规则的授权。
2. 活性目标：在列明的公平性、通信、地图、车辆可停靠/恢复等前提下，持续推进或被检测并转入安全恢复；不满足前提时不得声称“必然完成”。
3. 性能优化：在可行方案之间优化吞吐、完成时间、行驶时间、空驶/等待和拥堵；权重、SLA、规模数据经仿真基准确认。

论文仅支持可映射的模型与证明策略，WES 的安全/活性主张必须由模块化假设、反例搜索、仿真和现场验收共同验证。

## 8. 测试与验收设计

每个功能规格均提供可执行粒度的测试表，至少记录：Case ID、需求 ID、层级、前置数据/状态、输入/操作、断言/预期状态与持久化变化、清理/安全说明、自动化落点。

### 8.1 通用验证层

- Domain 单测：状态转换、不变量、权限决策、边界值和业务异常。
- Application 用例测试：多实体协作、事务边界、幂等、并发及补偿。
- Infrastructure/provider 集成：真实数据库 provider、事务/唯一约束/行更新、消息 broker、缓存/HotStore、文件、协议 adapter。
- API 契约测试：版本、认证/授权、验证、异常码、幂等、审计及兼容。
- 前端单元/组件/E2E：权限菜单、语义化交互、loading/empty/error/success、可访问性、离线只读缓存与陈旧提示。
- Simulator/设备契约测试：命令/反馈一致性、故障注入、断连/重连、重复和乱序反馈。
- 回归策略：隔离可重复自动化测试；活库验收数据有明确前缀/清理策略，不误清用户数据。

### 8.2 Route 专项用例类别

- 地图无效、同起终点、不可达、并行边、权重相等、容量为零/已满、地图版本切换。
- 节点、边、交叉区、狭窄通道和提升机资源冲突；迎面行驶、同时到达及时间窗重叠。
- 在线新任务、车辆位置变化、拥堵成本、路线重规划、在执行片段保护。
- 等待链和依赖环、可解与不可解阻塞、环检测正确性、公平性/饥饿、有限恢复。
- 反馈过期/重复/乱序、车辆停滞、通信断连、取消/故障/重规划后的预约回收。
- 随机图/多车属性测试：安全约束不破坏、所有授权均来自合法资源状态、错误输入可解释失败。
- 多车仿真端到端：交叉口、窄巷、反向会车、等待环、停车点恢复、提升机排队和持续新任务。
- 基准测试：规划延迟、协调周期、吞吐、等待、公平性、资源利用率与规模；地图、车辆数量和目标值由业务确认后确定。

## 9. 编码、注释与文档治理

### 9.1 后端

- .NET 10，启用 nullable；公共边界使用明确类型、验证和 CancellationToken。
- Domain 保持纯业务概念和不变量；Application 编排用例；Infrastructure 封装 EF、协议、缓存与外部服务；Web/API adapter 负责协议边界。
- 业务逻辑不放 Controller、EF entity setter、UI 或设备报文 handler 的隐式分支中。
- 禁止跨模块 Infrastructure/DbContext/内部实体引用；架构测试校验依赖方向。
- 不新增源码生成器或需要生成器才能维护的业务代码。
- 三个数据库 provider 的功能行为一致；数据库特有能力要有隔离、替代策略及 provider 测试。

### 9.2 前端

- 运维端/Simulator 使用 Vue 3 + Vite + TypeScript；PDA 保留 Seven 现有 UniApp（Vue 3 + TypeScript）移动端形态。
- 按 Frontloom Core FE 与 Vue 规范执行语义结构、可访问性、严格类型、完整 UI 状态、组件边界和测试；UniApp 平台能力/生命周期以其实际运行端约束为准，不强行套用仅适用于浏览器 SPA 的方案。
- `frontloom-core-fe`：语义 HTML、可访问性、严格类型、完整 UI 状态、仓库风格、安全、渐进增强、响应式、可维护性。
- `frontloom-vue`：Vue Composition API、props/emits/model、composables、Pinia 状态边界、数据获取/错误状态、组件和 composable 测试；UniApp 专属 API 放在清晰的适配边界内。
- 运维端/PDA/Simulator 共享样式/组件需有明确所有权，不允许 UI 直接依赖 WCS 内部状态表；不得为追求统一工具链而无评估地把 UniApp 改成 Vite SPA。
- PDA 离线仅支持只读缓存；明确缓存有效期/敏感数据保护并显示离线/陈旧状态，不排队或重放写操作。

### 9.3 注释要求

- 所有公开类型、方法、接口、DTO/事件契约添加 XML/类型文档，说明语义、参数约束、副作用、异常或线程/取消行为。
- 领域状态机、库存账本、事务/并发、Route 算法、设备适配、协议映射、安全边界、补偿/重试必须写明原因、前置条件、不变量和失败后果。
- 对容易误用的扩展点写使用示例和禁止事项；测试注释说明复杂场景意图。
- 以“尽量覆盖所有有维护价值的信息”为目标；不在简单赋值/显而易见代码上重复语句本身，避免过期或误导注释。
- 文档与注释修改纳入同一 PR 审查；算法注释需链接对应设计和测试 ID。

### 9.4 文档一致性治理

- `docs/` 中经批准的设计文档为产品和工程真源；根级 Agent/IDE 指令文件只引用真源和关键禁止项，不复制完整规范。
- 每个功能以唯一需求 ID 关联业务条目、设计、API/事件、测试和验收。
- 每个架构/技术边界变更建立 ADR，含背景、选择、备选、代价、迁移和验证。
- PR checklist 要求更新关联设计/测试；架构依赖、格式/静态分析、类型检查、单测、契约与 Route 仿真按影响面进入 CI。
- 这些机制用于发现和阻止偏离，文档本身不构成绝对保证；例外须有理由、批准和到期/偿还计划。

## 10. 文档目录计划

```text
WES.Master/
  README.md
  AGENTS.md                         # 项目约束入口，引用 docs 真源
  docs/
    00-project-startup-design.md    # 本规格
    01-business-scope-and-status.md
    02-system-architecture.md
    03-engineering-governance.md
    04-requirement-design-test-traceability.md
    modules/
      platform-and-security.md
      workflow-and-forms.md
      wms-and-ledger.md
      pda.md
      orchestration.md
      wcs-stacker.md
      wcs-fourway.md
      external-wcs-and-device-adapters.md
      device-communication.md
      environment-simulation-and-scada.md
      notifications-and-jobs.md
    route/
      01-as-is-and-research-review.md
      02-target-routing-and-scheduling.md
      03-route-test-catalog.md
    acceptance/
      01-cross-module-and-provider-matrix.md
    adr/
      README.md
```

上述文档均已在本轮建立；文档存在不表示功能已实现或测试已通过。实施阶段按实际盘点证据持续修订；如果一个文件过大或章节边界不清，应按业务能力继续拆分。

## 11. 必须在详细设计阶段收敛的事项

这些事项不阻止本架构方向，但需在对应功能文档中设为明确的决策/验收前置条件，不能在编码时临时猜测：

1. WMS/WCS 单据和状态与 Seven 现有实现的逐字段兼容范围、数据迁移方式及外部 API 兼容策略。
2. 各数据库 provider 的版本、支持矩阵、迁移/回滚流程，以及 provider 特有 SQL 和并发语义。
3. Route 现场信息模型：定位频率与可信度、车辆运动/占用状态、冲突区几何、安全距离、停靠/等待能力、同步时钟和通信失联阈值。
4. 各设备协议和供应商优先级、可用 command/feedback、真机控制权/安全 PLC 边界及安全停机约束。
5. Route 规模基线（地图/节点/边/车辆/任务率）、可接受的规划周期、吞吐目标和公平性指标。
6. PDA 目标终端、只读缓存有效期、敏感数据本地保存期限和设备丢失处理。
7. 工作流、动态表单、消息、租户和运维中哪些功能属于首发范围、可选能力或后续阶段。
8. 团队角色、代码所有人、文档审核人、生产部署及数据库迁移负责人。

## 12. 本阶段验收标准

- WES.Master 目录包含经审阅的业务全量大纲、状态矩阵、架构规范、Route 专项、逐功能设计/测试和总追踪矩阵。
- 每一项业务能力都有范围结论（保留/补齐/预留/移除）、功能流程、关键不变量、失败策略、权限边界和对应测试用例。
- Route 设计分别说明 Stacker、FourWay、External 的边界，并覆盖在线任务、实时状态、冲突区、时空预约、活性环、恢复和设备反馈。
- 技术和性能主张能回溯到 Seven 的实现/测试、Route 研究资料或明确的 WES 验证计划。
- 文档不存在未标记的 TODO/TBD、相互冲突的边界或“功能已完成”但没有证据的表述。
- 本阶段没有生成源代码、解决方案、迁移文件或脚手架。
