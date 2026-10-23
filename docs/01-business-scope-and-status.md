# Seven.Master 业务范围与现状矩阵

- 状态：初始业务基线；本轮详细规格与测试目录已建立；现场/产品输入及最终证据签核待完成
- 基线：Seven.Master 当前文档、代码与测试；不能仅凭功能开关或菜单判断功能已完整。
- 状态词：`保留` = 属于产品基线；`补齐/复核` = 保留目标明确但当前实现或验收证据不完整；`预留` = 仅有开关、模型或规划；`移除` = 不进入 WES；`待核` = 证据冲突或实现范围尚未核清。

本轮项目发起人批准现有全部模块详细设计及REQ-ROUTE-001..027逐条语义作为软件实现基线；不将下表中的预留/待核能力升级为已实现。MySQL先行、其他Provider延后按[ADR-0002](adr/ADR-0002-mysql-first-implementation.md)；现场与迁移输入逐项见[实施台账](implementation/readiness-register.md)。

## 1. 总体范围

WES.Master 承接 Seven.Master 的业务能力与通用技术基线，重新划分模块边界并逐项验证。保留的是业务语义和能力，不意味着照搬 Seven 的目录、表结构、UI 或实现方式。所有业务状态最终都必须链接到 WES 详细设计和测试用例；本矩阵是范围索引，不是实现完成声明。

| ID | 业务域/功能 | Seven 基线与证据 | WES 处置 | 当前已知缺口 / 核验重点 | 详细设计负责人文档 |
|---|---|---|---|---|---|
| SCOPE-PLAT-01 | 登录、JWT、刷新令牌、密码、安全策略、验证码、限流、防重 | [开发文档索引](../../Seven.Master/doc/README.md)、[安全与数据权限](../../Seven.Master/doc/13-安全与数据权限.md) | 保留并按 .NET 10 重新设计 | 令牌轮换/撤销、密码策略、异常审计和并发刷新要转成可测试规则 | [Platform 与安全](modules/platform-and-security.md) |
| SCOPE-PLAT-02 | 用户、角色、部门、菜单、按钮/API 授权、动态路由 | [权限与菜单](../../Seven.Master/doc/04-权限与菜单.md)、[功能开关](../../Seven.Master/doc/14-功能开关.md) | 保留 | 统一后端授权和前端可见性；不能把隐藏菜单当作授权 | [Platform 与安全](modules/platform-and-security.md) |
| SCOPE-PLAT-03 | 数据范围与多租户 | [安全与数据权限](../../Seven.Master/doc/13-安全与数据权限.md)、[功能开关](../../Seven.Master/doc/14-功能开关.md) | 保留；按部署场景核验启用策略 | 租户过滤器、跨租户后台任务、租户切换及 provider 一致性 | [Platform 与安全](modules/platform-and-security.md) |
| SCOPE-PLAT-04 | 字典、配置、国际化、文件存储（本地/MinIO） | [文档索引](../../Seven.Master/doc/README.md)、[部署指南](../../Seven.Master/doc/07-部署指南.md) | 保留 | 权限、文件元数据/清理、外部存储故障及语言资源治理需细化 | [Platform 与安全](modules/platform-and-security.md) |
| SCOPE-PLAT-05 | 审计、操作/登录日志、异常码、统一错误、健康检查、指标 | [文档索引](../../Seven.Master/doc/README.md)、[异常码规范](../../Seven.Master/doc/22-异常码规范.md) | 保留 | 确立隐私脱敏、关联 ID、保留期限、错误码兼容及告警阈值 | [Platform 与安全](modules/platform-and-security.md) |
| SCOPE-WF-01 | 工作流/审批、条件分支、审批人、会签、业务钩子 | [工作流文档](../../Seven.Master/doc/05-工作流.md) | 保留并核实现有生命周期 | 失败恢复、并发审批、版本变更和权限边界 | [Workflow 与表单](modules/workflow-and-forms.md) |
| SCOPE-WF-02 | 动态表单 | [工作流文档](../../Seven.Master/doc/05-工作流.md)、[Builder 功能开关](../../Seven.Master/doc/14-功能开关.md) | 保留；与源码生成器明确隔离 | 运行时表单 schema、版本兼容、校验和表单权限；不依赖 Builder 生成代码 | [Workflow 与表单](modules/workflow-and-forms.md) |
| SCOPE-WF-03 | Quartz 定时任务与执行记录 | [文档索引](../../Seven.Master/doc/README.md)、[功能开关](../../Seven.Master/doc/14-功能开关.md) | 保留 | 重入、集群并发、失败可见性、任务取消和 provider 行为 | [通知与定时任务](modules/notifications-and-jobs.md) |
| SCOPE-WF-04 | RabbitMQ/MassTransit、消息队列、Outbox | [消息队列指南](../../Seven.Master/doc/10-消息队列指南.md) | 保留 | Outbox 原子写入、重复投递、死信重放、幂等和补偿须形成验收条件 | [通知与定时任务](modules/notifications-and-jobs.md) |
| SCOPE-WF-05 | 告警、SignalR、邮件、公告/新闻 | [告警模块](../../Seven.Master/doc/09-告警模块.md)、[功能开关](../../Seven.Master/doc/14-功能开关.md)、Seven App 首页广播证据见 [实现说明](../../Seven.Master/doc/20-WMS与WCS实现说明.md) | 保留；公告/新闻管理闭环待核 | 告警确认/清除与去重；公告 API、权限、有效期和 PDA 展示范围 | [通知与定时任务](modules/notifications-and-jobs.md) |
| SCOPE-PLAT-06 | 缓存与 HotStore 热数据 | [HotStore 设计](../../Seven.Master/doc/15-热数据HotStore.md) | 保留，区分通用缓存与 WCS 热状态 | 热状态一致性、预热/持久化/恢复时序、禁用模式不能表现为成功 | [Platform 与安全](modules/platform-and-security.md)、[FourWay WCS](modules/wcs-fourway.md) |
| SCOPE-WMS-01 | 仓库、库区、层、巷道、货位、容器及包绑定 | [WMS/WCS 实现说明](../../Seven.Master/doc/20-WMS与WCS实现说明.md)、[WMS/WCS 包](../../Seven.Master/doc/19-WMS与WCS包.md) | 保留；重新定义模块所有权 | 编码规范、Pack 归属、锁仓、占用/预约不变量、主数据变更审计 | [WMS 与库存账本](modules/wms-and-ledger.md) |
| SCOPE-WMS-02 | 库存账本、可用量、预留、收货、发运、移库和调整 | [WMS/WCS 实现说明](../../Seven.Master/doc/20-WMS与WCS实现说明.md) | 保留为唯一库存权威 | 状态转换、并发预留、流水不可变性、取消/补偿和三种数据库兼容 | [WMS 与库存账本](modules/wms-and-ledger.md) |
| SCOPE-WMS-03 | 入库单、明细、收货、组盘、分配、运输和完成回写 | [WMS/WCS 实现说明](../../Seven.Master/doc/20-WMS与WCS实现说明.md) | 保留 | 同址/异址、无包/多包、目标位预订、运输失败和库存回滚 | [WMS 与库存账本](modules/wms-and-ledger.md) |
| SCOPE-WMS-04 | 出库单、审核、预留、拣选、发运与运输完成 | [WMS/WCS 实现说明](../../Seven.Master/doc/20-WMS与WCS实现说明.md)；测试见 [PickingServiceTests](../../Seven.Master/Seven.Net8/Seven.Tests/Wms/PickingServiceTests.cs) | 保留；核验 PDA/接口/E2E 覆盖 | Seven 文档称 PDA 拣选未实现，但控制器、页面和服务测试存在；将既有拣选作为保留能力，不重复造功能，补足端到端验收 | [WMS 与库存账本](modules/wms-and-ledger.md)、[PDA](modules/pda.md) |
| SCOPE-WMS-05 | 盘点计划、实盘、差异确认和调整 | [WMS/WCS 实现说明](../../Seven.Master/doc/20-WMS与WCS实现说明.md)、[PDA 服务测试](../../Seven.Master/Seven.Net8/Seven.Tests/Pda/PdaServiceTests.cs) | 保留 | 锁仓与在途任务、重复扫码、差异审批及库存流水 | [WMS 与库存账本](modules/wms-and-ledger.md)、[PDA](modules/pda.md) |
| SCOPE-WMS-06 | 仓内 TransferOrder、项目单据扩展；拆合托/冻结/补货等在库处理示例 | [TransferOrderService](../../Seven.Master/Seven.Net8/Seven.Business/TransferOrderService.cs)、[调拨测试](../../Seven.Master/Seven.Net8/Seven.Tests/Business/TransferOrderServiceTests.cs)、[业务扩展规范](../../Seven.Master/doc/23-业务扩展规范.md) | 保留 TransferOrder 示例与扩展机制；其他在库处理须逐项立项/核验 | TransferOrder 有服务/测试证据，但当前确认库存的事务/多行失败原子性、运输编排和 PDA/API 验收需加强；不把规范中的示例误写为已落地能力 | [WMS 与库存账本](modules/wms-and-ledger.md)、[业务编排](modules/orchestration.md) |
| SCOPE-PDA-01 | PDA 登录、收货、上架、拣选、盘点和扩展菜单 | [业务扩展规范](../../Seven.Master/doc/23-业务扩展规范.md)、[PDA 控制器](../../Seven.Master/Seven.Net8/Seven.WebApi/Controllers/Pda/PdaController.cs)、[拣选页面](../../Seven.Master/Seven.App/src/pages/picking/index.vue)、[PDA 测试](../../Seven.Master/Seven.Net8/Seven.Tests/Pda/PdaServiceTests.cs) | 保留；采用 UniApp + Vue 3 + TypeScript | 控制器/页面/服务测试覆盖不等价于真终端 E2E；按业务角色和扫码错误补齐用例。文档与代码差异以代码/测试核验后修正文档 | [PDA](modules/pda.md) |
| SCOPE-PDA-02 | PDA 发运操作、离线只读缓存、立库异常处置 | Seven 文档/代码现状仍需逐项核验 | 补齐设计；离线只读、不保留待提交写操作（已确认） | 明确发运/异常操作授权；本地只读缓存的数据保护、有效期、陈旧提示和设备丢失处理 | [PDA](modules/pda.md)、[Stacker WCS](modules/wcs-stacker.md) |
| SCOPE-BUS-01 | Orchestration Bus 运输单、Leg、跨包交接和完成回写 | [WMS/WCS 包](../../Seven.Master/doc/19-WMS与WCS包.md)、[WMS/WCS 实现说明](../../Seven.Master/doc/20-WMS与WCS实现说明.md) | 保留并重构边界 | 幂等激活、跨包交接、暂停/取消、重试、超时和库存回写唯一性 | [业务编排](modules/orchestration.md) |
| SCOPE-WCS-01 | Stacker 货位分配、双深位、挡路移库、任务拆段和执行反馈 | [WMS/WCS 实现说明](../../Seven.Master/doc/20-WMS与WCS实现说明.md)、Route 源码及测试详见 Route 专项 | 保留并重构 | 容量、挡路任务生命周期、Route/Dispatcher 与占用状态需要统一不变量和回归用例 | [Stacker WCS](modules/wcs-stacker.md)、[Route 现状](route/01-as-is-and-research-review.md) |
| SCOPE-WCS-02 | FourWay 层/巷/位分配、多车交通、停车预约、提升机和反馈 | [WMS/WCS 实现说明](../../Seven.Master/doc/20-WMS与WCS实现说明.md)、Route 源码及测试详见 Route 专项 | 保留并重构 | 实时状态、资源预约、提升机阶段、等待环与故障恢复的并发安全 | [FourWay WCS](modules/wcs-fourway.md)、[Route 现状](route/01-as-is-and-research-review.md) |
| SCOPE-WCS-03 | BoxSort WCS | [功能开关](../../Seven.Master/doc/14-功能开关.md)、[WMS/WCS 包](../../Seven.Master/doc/19-WMS与WCS包.md) | 预留；不作为首期完整能力 | 仅有包/开关预留证据，需另立需求、设备边界和验收计划 | [系统架构](02-system-architecture.md) |
| SCOPE-WCS-04 | 外部 WCS HTTP/MQ、供应商 Codec、回调和接口日志 | [WMS/WCS 包](../../Seven.Master/doc/19-WMS与WCS包.md)、[WMS/WCS 实现说明](../../Seven.Master/doc/20-WMS与WCS实现说明.md) | 保留集成边界；供应商能力逐项核验 | 身份认证、版本/签名、乱序/重复回调、供应商超时和重放 | [外部 WCS 与设备适配器](modules/external-wcs-and-device-adapters.md) |
| SCOPE-DEV-01 | DeviceComm 连接、驱动/协议、点位、读写、规则和告警 | [DeviceComm 文档](../../Seven.Master/doc/16-设备通讯DeviceComm.md) | 保留抽象和已有 adapter；未覆盖设备协议标记为待接入 | 真实设备闭环、权限、连接恢复、设备故障与仿真一致性 | [设备通讯](modules/device-communication.md) |
| SCOPE-DEV-02 | SCADA 地图/设备/货位/任务状态展示 | [WMS/WCS 实现说明](../../Seven.Master/doc/20-WMS与WCS实现说明.md)、[功能开关](../../Seven.Master/doc/14-功能开关.md) | 保留只读状态投影，供 2D 地图优先的运维 Web 复用 | SCADA 不负责命令；投影须显示陈旧/未知/未映射状态，地图客户端控制操作由 Orchestration/WCS 重新授权、校验和执行 | [环境搭建、仿真与 SCADA](modules/environment-simulation-and-scada.md)、[Orchestration 运维设计](modules/orchestration.md) |
| SCOPE-DEV-03 | DeviceComm 现场接入环境、Simulator 地图编辑/校验/Deploy/仿真/Promote 真机切换 | [仿真器与联调闭环](../../Seven.Master/doc/21-仿真器与联调闭环.md)、[Seven Simulator 设计](../../Seven.Master/design/2026-08-29-seven-simulator-design.md) | 保留闭环并核验生产边界 | 不可变地图版本、跨 owner 准备/激活清单、失败补偿部署、活动任务版本固定、仿真/真机契约一致及 Promote 权限/回滚 | [环境搭建、仿真与 SCADA](modules/environment-simulation-and-scada.md)、[设备通讯](modules/device-communication.md) |
| SCOPE-OPS-01 | WCS 运维看板、跨包协调、包级模式/联锁/取消/强制完成和审计 | [运维 Ops 与联锁](../../Seven.Master/doc/24-运维Ops与联锁.md) | 保留；以 2D 监控地图为主入口，跨包流程归 Orchestration，执行校验归各 WCS Pack | 尽可能在地图定位所有设备/任务/问题并提供常用操作；未定位对象仍须可见；问题生命周期归 Orchestration；保护控制、角色分权、双人复核和安全 PLC 边界 | [Orchestration](modules/orchestration.md)、[Stacker WCS](modules/wcs-stacker.md)、[FourWay WCS](modules/wcs-fourway.md) |
| SCOPE-OPS-02 | 部署、容器、健康检查、指标、日志和通用平台运维 | [部署指南](../../Seven.Master/doc/07-部署指南.md)、[健康检查](../../Seven.Master/doc/17-健康检查看板.md)、[指标监控](../../Seven.Master/doc/18-指标与监控.md) | 保留通用技术方案，适配 .NET 10；不等同于现场 WCS 运维控制 | 运行手册、隔离环境备份/恢复、升级回滚、每 Pack 单写者/fencing、provider 矩阵和指标 SLO | [系统架构](02-system-architecture.md)、[验收矩阵](acceptance/01-cross-module-and-provider-matrix.md) |
| SCOPE-DB-01 | SQL Server / PostgreSQL / MySQL provider 切换 | Seven 的 EF Core 结构和测试方法见 [测试方法与落库回归](../../Seven.Master/doc/keypoint/09-测试方法与落库回归.md) | 保留目标；具体兼容能力需逐 provider 验证 | 类型/索引/并发/事务/迁移差异须进入 provider 测试，不能仅靠可配置连接串推定兼容 | [系统架构](02-system-architecture.md)、[验收矩阵](acceptance/01-cross-module-and-provider-matrix.md) |
| SCOPE-EXT-01 | Seven.Business / `Biz_` / WMS extension hooks 等扩展机制 | [业务扩展规范](../../Seven.Master/doc/23-业务扩展规范.md) | 保留扩展意图，重新设计边界 | 不承诺旧命名或代码兼容；所有扩展经公开应用契约，不直接耦合包内设施层 | [系统架构](02-system-architecture.md)、[业务编排](modules/orchestration.md) |
| SCOPE-REMOVE-01 | Builder、源码模板、生成式 API/业务代码 | [代码生成器文档索引](../../Seven.Master/doc/README.md)、[Builder 开关](../../Seven.Master/doc/14-功能开关.md) | 明确移除 | 清理菜单/API/模板/运行时依赖；动态表单等运行时配置能力独立保留 | [系统架构](02-system-architecture.md)、[工程治理](03-engineering-governance.md) |

## 2. PDA 拣选证据说明

Seven 的 [WMS/WCS 实现说明 §5](../../Seven.Master/doc/20-WMS与WCS实现说明.md)对 PDA 拣选描述与代码现状不一致：当前控制器存在 `picking/pending`、`picking/confirm`，应用存在拣选页面；[PickingServiceTests](../../Seven.Master/Seven.Net8/Seven.Tests/Wms/PickingServiceTests.cs)覆盖从审核、拣选生成到确认并完成账本的服务流程，WCS 出库 E2E 测试也覆盖拣选确认。

这足以证明拣选不是空白功能，但不证明 PDA 端到端已经完整验收。WES 处理原则：

1. 保留拣选业务/API/UI 能力，不重复设计另一套拣选闭环。
2. PDA 规格描述现有流程并识别页面/API/服务之间的差异；补齐角色、扫码、重复提交、断网及业务异常用例。
3. 对 `PdaServiceTests` 当前收货/上架/盘点场景及 PDA picking endpoint 单独建立端到端验收用例。
4. 将 Seven 文档中的不一致列为基线文档债务；WES 设计不以过期说明覆盖实际代码证据。

## 3. 跨领域约束

- WMS 是唯一库存数量账本。WCS 可拥有位置预约、任务、设备和 Route 运行状态，但不得形成第二套可扣减库存。
- Stacker、FourWay、外部 WCS 的路网、交通模型、任务生命周期及设备能力保持隔离；共享稳定业务/设备语义契约，不共享未经证明等价的内部状态。
- Simulator 必须与真实设备 adapter 使用相同语义命令/反馈契约；不能将仿真成功当作生产安全证明。
- 开关只控制菜单或宿主组件不等同于删除业务边界；每个功能的启用/禁用和依赖关系须在系统设计中重新明示。
- 任何 `预留`、`待核`、`补齐/复核` 功能在验收矩阵关闭前不得宣传为生产完整能力。

## 4. 待收敛事项

| 决策 ID | 需在详细设计中确定的问题 | 初始处理原则 |
|---|---|---|
| OPEN-SCOPE-01 | 首期上线功能与可延后模块（例如 BoxSort、完整多租户、外部 WCS 供应商范围） | 先列入完整业务范围，再由项目负责人按依赖与收益排序；不得静默丢弃 |
| OPEN-SCOPE-02 | Seven 历史数据/接口/客户端是否迁移兼容 | 独立迁移决策；“重新开始”不默认要求表结构兼容 |
| OPEN-PDA-01 | PDA 只读缓存最长有效期和敏感数据本地保留期限 | 离线行为已定为只读、不排队/重放写操作；缓存期限由现场安全要求确定 |
| OPEN-PDA-02 | PDA 目标终端、扫码硬件、敏感数据缓存与设备丢失处置 | 由现场终端/安全要求确定，不虚构平台能力 |
| OPEN-ROUTE-01 | 地图规模、车辆数、任务率、定位误差、遥测新鲜度、通信时延及性能 SLO | 需现场输入；阈值未定前定义可重复基准方法，不编造数值 |
| OPEN-ROUTE-02 | 设备制动/避障/定位/安全 PLC 的职责与可证明约束 | 明确系统边界；软件调度不替代物理安全控制 |
| OPEN-DB-01 | 三个数据库 provider 的版本、支持策略及生产主选 provider | 通过逐 provider 测试确定，不因连接串可配置而推定完全等价 |
| OPEN-OPS-01 | 强制完成、取消、联锁解除的角色、双人确认和紧急操作规则 | 默认受控、强审计；实际权限与现场安全流程共同定案 |

## 5. 追踪与状态更新

每个 `SCOPE-*` 都必须映射到详细设计中的 `REQ-*`、测试目录中的 `TC-*` 和最终验收记录。发现基线新证据时，更新本矩阵、详细设计、测试用例和相关 Seven 基线说明引用；不得仅修改状态标签。验收前，未完成功能保持 `待核` 或 `补齐/复核`。
