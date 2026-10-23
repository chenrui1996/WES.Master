# 环境搭建、仿真与 SCADA 详细设计

工程/部署/仿真/SCADA页面、功能流程与逐步骤用例：[环境深化设计](../detailed-design/environment-simulation-and-scada.md)。原文仍为业务边界依据，深化文档为评审中、测试未执行。

- 状态：目标设计；范围仅含现场环境/设备接入准备、Simulator 仿真与 Promote 真机切换、SCADA 只读展示
- 范围追踪：`SCOPE-DEV-02`、`SCOPE-DEV-03`
- 基线参考：[仿真联调闭环](../../../Seven.Master/doc/21-仿真器与联调闭环.md)、[Simulator 设计](../../../Seven.Master/design/2026-08-29-seven-simulator-design.md)、[SCADA 测试](../../../Seven.Master/Seven.Net8/Seven.Tests/Scada/Scada2dTests.cs)
- 测试前缀：`SITE-TC-*`

## 1. 责任划分

### 1.1 SCADA

SCADA 为只读现场地图/状态投影，展示 WMS 位置占用、WCS 任务/车辆、设备状态和告警，并标示数据更新时间/陈旧性。它拥有视图配置与节点绑定，不拥有显示对象的运行事实。未知 location 不可默认渲染成确定空位；应显示未知/无数据。

### 1.2 Simulator

Simulator 拥有工程草稿、地图编辑/校验、仿真参数、仿真运行/结果以及部署版本记录。只有显式 Deploy/Promote 工作流可将已验证配置变成运行主数据/生产配置。工程 JSON 本身不等同运行库或生产地图。

地图是版本化不可变快照，部署记录包含工程版本、校验摘要、操作者、目标环境、包集合、回滚点和结果。Reset 仅清理允许清理的仿真运行态，不能误删 WMS 账本或生产数据。

## 2. Simulator 工作流程和门禁

### 2.1 Edit → Validate → Deploy

1. 工程编辑节点、边、设备、申请点、包配置和 SCADA 映射；所有对象具备稳定 ID、坐标/方向、能力和版本。
2. Validate检查字段/稳定ID/类型/引用/连通/方向/冲突域/层包/点码/能力。error拒绝Deploy；仿真warning受权逐条确认，生产warning默认阻断，仅预先分类非安全展示项由复核者逐条附理由才可放行，不以warning代替H证据。
3. Deploy 只接受已验证的不可变地图版本，创建唯一 `DeploymentId` 和不可变 manifest。Manifest 固定目标环境、参与 owner 集合、各 owner 的 artifact version/hash、工程版本、校验摘要、操作者和目标 Pack；重复提交同一幂等请求返回同一部署记录，不能生成重复激活。
4. Simulator 是 deployment coordinator。WMS、每个目标 WCS Pack 和 SCADA 分别校验并暂存自己拥有的数据 artifact，使用同一 `DeploymentId` 回报 `Prepared(version, hash)` 或可诊断 blocker。参与者缺失、hash/version 不匹配或准备失败时，manifest 不能激活，当前活动部署保持不变。
5. 必需参与者全部Prepared且未超过10分钟后，Simulator在本地事务以expectedActiveDeploymentId/expectedGeneration作CAS切换唯一active、递增generation并写Outbox。Prepared绑定同一deployment/owner/artifact版本/hash/期望代数。并发部署只有一个可匹配旧指针，失败者须重新preview/prepare/审批，不自动覆盖。跨模块非ACID，漏事件查询持久manifest恢复。
6. 新任务只接受当前active DeploymentId/MapVersion；旧任务命令携其固定taskDeploymentId/MapVersion，仅在已批准CompatibleContinue或DrainAffected排空阶段继续。Pack核验固定部署artifact及任务继续策略、当前epoch/grant、权限/联锁/freshness，不能任意旧图新建任务，也不能只因active变化拒绝合法旧任务。缺证据拒绝，不以激活事件缓存作为唯一依据。
7. 部署失败在激活前发生时保留旧 active pointer，并将部署标记为可诊断的失败/恢复状态。激活后需要回滚时创建新的 compensating deployment，引用先前验证过的不可变 owner artifacts；不得覆盖或删除原部署历史。
8. 地图变更后活动任务继续固定其原 MapVersion；对应 WCS Pack 决定继续执行、在安全点暂停或受控重规划，并记录决策和设备/位置对账结果。切换活动部署不得隐式改写活动任务路线。

上述规则已按[WES-RULES-1.0 §2.1/2.4](../detailed-design/05-approved-rules-and-defaults.md)定案：默认DrainAffected，按整个Pack确定受影响范围；停止接新任务及新版本grant，既有任务可用旧artifact在当前epoch/联锁/物理域核验后取得仅为排空的后续grant，未开始且无物理效果可安全取消，暂停未终结不等于排空。任务均完成/安全终结、无Unknown后才激活。CompatibleContinue须有映射/容量/几何/执行兼容证据，跨版共物理域；新图缺映射不得激活。补偿亦CAS/排空，Prepared和高后果批准各10分钟，不作物理安全时限。

### 2.2 Simulation → Review

- Trigger 模式通过设备语义 API 注入申请/命令/反馈；Gateway 模式用于验证报文/时序，两者都可控制虚拟设备。
- 仿真支持确定性随机种子、可控时钟或明确时间基准，记录地图版本、车辆数、任务负载、故障注入和结果。
- 结果包括无路、冲突、等待/恢复、完成率、延迟、异常与日志；仿真结论不代表物理安全认证。
- Reset 只能清理已识别的仿真运行态，预览影响对象后执行；不得删除生产数据、库存账本或非仿真任务。

### 2.3 Promote → Production

- Promote Preview 检查环境、设备 Host/Port/协议、身份凭据引用、MapVersion、启用 Pack、WCS/adapter 能力、联锁/安全审查、版本差异和回滚点。
- 仿真身份/loopback/localhost/测试凭据不得冒充生产设备。
- Promote 必须经角色授权、审批/双人复核策略（按环境级别），写不可变审计记录。
- 真机 adapter 先做只读/受控联调，再按单设备试运行和现场验收扩大范围；失败时按回滚计划恢复已知配置，并核验物理任务状态。
- Promote 只切换配置/版本，不得清空或重写库存/活跃运输的权威事实。

## 3. SCADA 状态同步与数据质量

- Read model 提供地图版本、节点绑定、资源 ID、占用/任务/设备状态、最后观测时间和来源版本。
- 实时推送丢失时客户端可重新查询；SignalR 只负责通知，完整快照从 query API 恢复。
- 迟到/乱序状态根据版本/时间规则拒绝回退；超过 freshness 阈值显示 `Stale`，不能继续表现为当前状态。
- 更新 SCADA 坐标/视图不改变 WMS 库位坐标或 WCS 路网；两者通过稳定 ID 关联且可分别审计。
- SCADA 数据契约与页面保持只读；它是运维地图的状态投影来源，不是运维控制模块。运维 Web 复用地图/状态投影作为主工作台，控制命令由 Orchestration/WCS 后端重新授权与校验。

## 4. 观测与恢复

- 记录工程/地图版本、部署 ID、manifest 状态、必需参与者及其 version/hash/确认、目标环境、仿真运行、操作者、设备配置引用和审核决定。
- 现场可视化展示连接状态、SCADA 数据新鲜度和地图/设备投影；WCS 活动/阻塞任务、资源预约与问题处置由 Orchestration/WCS 提供地图工作台所需的只读/命令契约。
- 真机切换或地图部署失败时按 manifest 与参与者记录恢复已知配置；激活前不切换旧部署，激活后通过新的补偿部署恢复版本，并将活动物理任务交由 WCS 运维核对。不得先清缓存或删除任务再诊断。
- Backup/restore 的跨模块验收归入系统验收矩阵；本规格只负责工程/地图版本及 Simulator 运行记录的恢复要求。

## 5. 规范性需求

| ID | 必须满足 | 验收用例 |
|---|---|---|
| REQ-SITE-001 | SCADA 必须提供只读空间投影，使用稳定绑定标识呈现运行状态及其来源/版本。 | SITE-TC-001 |
| REQ-SITE-002 | 位置缺失、状态未知或遥测陈旧时必须明确显示 Unknown/Stale，不得呈现为确定空位或健康。 | SITE-TC-002 |
| REQ-SITE-003 | SCADA UI/API 不得修改 WMS 库存或 WCS 执行状态。 | SITE-TC-003 |
| REQ-SITE-004 | SCADA 视图/坐标配置必须与 WMS 货位主数据和 WCS 路网拓扑分离；修改视图不得改变业务位置或路由事实。 | SITE-TC-001, SITE-TC-003 |
| REQ-SITE-005 | 地图校验必须拒绝结构或引用无效的地图版本；无效版本不得部署。 | SITE-TC-004 |
| REQ-SITE-006 | 部署必须针对明确版本幂等且可恢复；参与模块尚未准备完整时不得报告部署已激活。 | SITE-TC-005, SITE-TC-029, SITE-TC-030, SITE-TC-031, SITE-TC-032 |
| REQ-SITE-007 | 活动任务必须保持绑定其原地图版本，或按明确的安全迁移决策处理；不得静默改写活动路径。 | SITE-TC-006, SITE-TC-033 |
| REQ-SITE-008 | 仿真必须使用与执行相同的语义命令/反馈契约，并记录可复现输入、地图版本和故障注入结果。 | SITE-TC-007 |
| REQ-SITE-009 | Reset 必须限制在已识别的仿真运行态，不得删除生产数据、库存账本或活动物理任务。 | SITE-TC-008 |
| REQ-SITE-010 | Promote 必须拒绝测试/回环设备身份、不支持设备、缺少权限或所需复核的请求；重复 Promote 必须幂等并可审计。 | SITE-TC-009, SITE-TC-010 |
| REQ-SITE-011 | Promote 失败或回滚必须恢复已知配置并核实活动物理任务，不得清除或伪造其状态。 | SITE-TC-011 |

## 6. 详细验收用例

| ID | 场景 | 期望 |
|---|---|---|
| SITE-TC-001 | SCADA 创建视图/绑定节点并查询 | 返回稳定坐标/绑定；权限正确 |
| SITE-TC-002 | location 不存在、状态未知、遥测过期 | 显示 Unknown/Stale；不将缺数据当作确定空闲 |
| SITE-TC-003 | SCADA 页面尝试写库存/WCS 状态 | UI/API 均拒绝；数据库无写入 |
| SITE-TC-004 | 地图缺节点/重复 ID/孤立边/错误 Pack/无效冲突区 | Validate 拒绝并提供可定位问题，Deploy 不落部分数据 |
| SITE-TC-005 | 合法地图重复 Deploy/参与者准备失败 | 同一幂等请求不重复激活；准备失败不切换 active deployment，失败 owner/原因可查询 |
| SITE-TC-006 | 新地图 Deploy 时有活动任务 | 活动任务仍绑定旧 MapVersion；由 owning Pack 按迁移策略继续、暂停或安全重规划，不隐式重写路径 |
| SITE-TC-007 | 仿真 Trigger 与异常/延迟/丢包/重复反馈 | 执行真实状态机语义；故障可复现且结果记录版本/种子 |
| SITE-TC-008 | Reset 目标有活动任务/包含生产数据 | 预览阻止危险清理；生产账本/任务不被删除 |
| SITE-TC-009 | Promote preview 配置含 localhost/测试凭据/不支持设备 | 拒绝 Promote，显示明确 blocker |
| SITE-TC-010 | Promote 无权限/无复核/同一版本重复 | 权限和复核生效；重复 Promote 幂等并可审计 |
| SITE-TC-011 | 生产 Promote 中途失败和回滚 | 恢复到已知配置；在途物理任务状态进入核查 |
| SITE-TC-029 | 一个必需地图 owner 准备失败 | 部署不激活；原活动部署保持有效，失败 owner 和原因可查询 |
| SITE-TC-030 | Prepared 确认缺失、过期或版本/hash 与 manifest 不匹配 | 激活被拒绝；不得用旧确认或不匹配工件继续部署 |
| SITE-TC-031 | 激活后发生故障并执行回滚 | 创建新补偿部署引用先前验证版本；保留原部署历史并核对活动任务 |
| SITE-TC-032 | 参与者恢复后重复处理激活事件/查询 manifest | 激活处理幂等；漏收事件可通过 manifest 对账恢复一致版本 |
| SITE-TC-033 | 地图部署期间存在旧版本活动任务 | 任务保持原 MapVersion，或按 owning Pack 已审计的安全迁移策略处理 |

## 7. 现场/生产门禁

生产切换前需设备安全责任人及 WCS 负责人签核：设备身份/协议、网络区隔、地图版本、仿真结果、Single Writer、Promote 回滚、Route 场景仿真和现场空载/载荷试运行均有证据。运行模式、联锁处置和手工覆盖权限由 Orchestration/WCS 运维设计规定。性能阈值按现场量测，不从 Simulator 样例推断。
