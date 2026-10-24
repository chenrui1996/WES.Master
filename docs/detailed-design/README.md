# 页面、功能与详细测试设计索引

- 状态：现有全部页面/功能设计、REQ-ROUTE-001..027逐条语义及[WES-RULES-1.0](05-approved-rules-and-defaults.md)本轮已获项目发起人软件实施基线批准；不代替现场验收
- 进入受控实施；详细设计与用例仍不是实现/测试通过证明，实际结果见[就绪台账](../implementation/readiness-register.md)
- 权威上游：[业务范围](../01-business-scope-and-status.md)、[架构](../02-system-architecture.md)、[治理](../03-engineering-governance.md)、[总追踪](../04-requirement-design-test-traceability.md)、[ADR](../adr/README.md)

## 1. 阅读顺序与交接

1. 先读[共用标准](00-shared-page-and-test-standard.md)、[Ops Web 工业风格视觉基准](06-ops-web-visual-style-baseline.md)与[密集页面交互补充](03-dense-page-interaction-contract.md)：布局、字段/权限/状态、视觉tokens，以及列显隐/顺序/宽度/固定、个人视图、关联显示、级联筛选、主从编辑；共32条共用UI测试。
2. 再读[公共参考](01-reference-cases.md)：原9项公开资料及本轮Legrand源码/GitHub Projects证据，区分采用、不采用与不可访问来源。
3. 按下表进入模块：页面表是信息/字段/布局合同；新增逐页配置矩阵定义默认/必显/可选列、关联选择链与适用测试；功能表是owner/事务/状态规则；详细DTC表是步骤和持久化/副作用断言。
4. 最后读[13条跨模块详细场景](02-cross-module-scenarios.md)，与原验收编号建立关联。
5. 实现前关闭模块末尾的阻断输入；本轮页面与用例不自动批准现场参数、全新能力或物理控制策略。
6. 读[行业复审](04-industry-review-and-open-rule-recommendations.md)理解7项风险，再按[已签核规则与默认值](05-approved-rules-and-defaults.md)实施：前四项高风险设计歧义已定案，29项规则区分B业务/P初始值/H现场门禁；RVT-01..09已并入原DTC必测变体，未执行。
7. 开发/测试排程读[全模块依赖与里程碑](../superpowers/plans/2026-10-23-wes-development-and-testing.md)、[34个开发工作包](../superpowers/plans/2026-10-23-wes-development-work-packages.md)及[测试执行/覆盖/证据计划](../superpowers/plans/2026-10-23-wes-test-execution.md)。实施已获批准，逐片保留证据；未指定具名团队/交期，不虚构日历承诺。

## 2. 设计包与数量

统计口径为表格首列定义的唯一ID，不把引用、参数化变体和纯文本线框重复计算。

| 设计包 | PAGE | FUNC | 业务DTC | 覆盖范围 |
|---|---:|---:|---:|---|
| [Platform](platform-and-security.md) | 8 | 6 | 14 | 身份/会话、用户角色组织、数据范围/租户、配置字典语言文件、审计 |
| [Workflow/Form](workflow-and-forms.md) | 4 | 4 | 9 | 定义发布、待办/审批/同步、运行时表单与历史版本 |
| [Notifications/Jobs](notifications-and-jobs.md) | 6 | 5 | 9 | 告警/渠道、Outbox/死信、Quartz、公告、缓存诊断 |
| [WMS](wms-and-ledger.md) | 8 | 7 | 11 | 主数据/库存/流水、收货上架、出库拣选发运、盘点、TransferOrder |
| [PDA](pda.md) | 6 | 6 | 10 | 登录/任务、扫码收货上架拣选盘点发运、只读离线与异常报告 |
| [Orchestration/运维](orchestration.md) | 5 | 5 | 10 | 地图工作台、订单/Leg、交接、授权动作、问题生命周期 |
| [Stacker](wcs-stacker.md) | 3 | 4 | 7 | 巷道/双深/挡路/拆段、包级检查与恢复 |
| [FourWay](wcs-fourway.md) | 3 | 5 | 7 | 分配、层内交通、停车、提升机和恢复 |
| [External WCS](external-wcs-and-device-adapters.md) | 3 | 4 | 7 | 能力/配置、出站/回调、Unsupported与Unknown |
| [DeviceIntegration](device-communication.md) | 4 | 4 | 7 | 连接/点位、规则、执行/反馈、单写宿主 |
| [Simulator/SCADA](environment-simulation-and-scada.md) | 6 | 5 | 10 | 编辑校验、manifest、仿真/Reset、Promote、只读SCADA |
| [Route](route.md) | 5 | 6 | 14 | 图/候选、ledger/epoch、反馈/恢复、等待公平、基准 |
| [系统运维](system-operations.md) | 3 | 4 | 7 | 健康/日志/部署证据、隔离恢复、Provider/架构门禁 |
| 合计 | 64 | 65 | 122 | 另有共用UI DTC 32条、跨模块DTC 13条；全部DTC共167条 |

一个PAGE可以是地图中的具名面板/详情，而不是必须创建独立全屏页面；同一FUNC可供Web/PDA复用业务owner契约。不能据统计数量机械生成64个路由/65个Handler。

本轮Legrand/GitHub对照补充覆盖64个PAGE的逐页配置，没有凭空增加业务页面；新增20条共用交互测试及13条模块关联测试。列、个人视图、关联链和测试配置是既有PAGE/FUNC的细化，不形成新的权限/状态权威。PDA/登录明确不适用桌面列设置，地图仅其队列表格适用。

## 3. SCOPE覆盖与排除

| 既有范围 | 深化入口 | 边界/状态 |
|---|---|---|
| SCOPE-PLAT-01..05 | [Platform](platform-and-security.md)、[系统](system-operations.md) | 保留；安全初值/禁隐式公共租户已定，真实身份/Provider/恢复仍待验收 |
| SCOPE-PLAT-06 | [通知缓存](notifications-and-jobs.md)、[Route](route.md) | 缓存/HotStore非grant或库存权威 |
| SCOPE-WF-01..02 | [Workflow/Form](workflow-and-forms.md) | 保留有限可验证语义，不从编辑器推断任意BPMN能力 |
| SCOPE-WF-03..05 | [通知/任务](notifications-and-jobs.md) | 公告默认不强制已读/显式PDA渠道、Job单节点与集群策略已定；真实渠道/拓扑待验收 |
| SCOPE-WMS-01..06 | [WMS](wms-and-ledger.md)、[运输](orchestration.md) | 保留TransferOrder；冻结/拆合托/补货等仅示例的能力不扩为首期 |
| SCOPE-PDA-01..02 | [PDA](pda.md) | 保留与补齐，离线只读，不存/重放待提交写 |
| SCOPE-BUS-01 | [Orchestration](orchestration.md) | 跨包运输，不共享包内Route |
| SCOPE-WCS-01..02 | [Stacker](wcs-stacker.md)、[FourWay](wcs-fourway.md)、[Route](route.md) | Pack最终检查，物理安全另验收 |
| SCOPE-WCS-03 | [原范围](../01-business-scope-and-status.md) | BoxSort预留；不编造首期完整页面/用例 |
| SCOPE-WCS-04 | [External](external-wcs-and-device-adapters.md) | 按真实供应商能力逐项验收，FakeCodec不证明兼容 |
| SCOPE-DEV-01 | [Device](device-communication.md) | 驱动/规则抽象保留，现场设备逐项签核 |
| SCOPE-DEV-02..03 | [环境/仿真/SCADA](environment-simulation-and-scada.md) | 只读投影/环境/部署；不是现场运维owner |
| SCOPE-OPS-01 | [地图/问题](orchestration.md)、各Pack | 以2D地图为主，未定位对象队列可发现 |
| SCOPE-OPS-02、SCOPE-DB-01 | [系统运维](system-operations.md) | 手册/证据而非网页生产还原捷径；三Provider待真实验证 |
| SCOPE-EXT-01 | [WMS扩展](wms-and-ledger.md)、[系统架构测试](system-operations.md) | 稳定契约，禁止私表/第二账本 |
| SCOPE-REMOVE-01 | [系统架构测试](system-operations.md)、[原治理](../03-engineering-governance.md) | Builder/源码生成移除，无生成式页面 |

## 4. ID与覆盖口径

`PAGE-{DOMAIN}-{NNN}`、`FUNC-{DOMAIN}-{NNN}`、`DTC-{DOMAIN}-{NNN}`为本轮深化ID；不替代原REQ或TC。模块内数字简写例如“页面001/002”“UI-001”分别指同模块PAGE与共用DTC-UI-001；表格范围是逐项展开的有限集合，不含未列ID。

追踪路径：SCOPE → 原模块/REQ范围 → FUNC → PAGE及DTC → 原TC。DTC-XMOD关联多个模块FUNC，属于集成场景，不新增业务权威。参数化DTC各变体须独立建立前置和断言；实现时可拆自动化case但保留父DTC及原TC关联。

原目录265条具名TC（模块、Route、跨模块验收255条及架构10条的唯一编号）均有详细DTC的原TC关联入口。该数字仅证明设计映射存在；人工断言审查与后续实现验收仍必需，不能等同265条已自动化或通过。

## 5. 待决策与完成定义

- [规则签核基线](05-approved-rules-and-defaults.md)已统一跨版物理冲突/旧任务继续、TransferOrder逐实物执行单元记账及在途交接；同步上游规格/详细测试，RVT作为既有DTC必测变体、不增加167条DTC计数。签核只关闭设计歧义，不表示运行风险已消除。
- REQ-ROUTE-001..027的逐条语义在[Route候选表](route.md)形式化，待Route负责人批准；旧范围键缺口未冒充已关闭。
- 其余历史范围REQ也不能仅靠编号存在视作每条规范性需求已批准；以原模块行为为准，实施前由业务owner把范围键逐项签核。
- 产品：批次/数量精度、部分收发/短拣、局部锁盘、All/Any、公告受众已定；尚须实际物料/单据迁移政策、业务字段映射、支持终端及角色名单。
- 现场/安全：双人绑定、跨版排空/兼容合同已定；设备freshness/定位误差/缓冲/停车提升口信号和query/去重/停止等能力缺证据默认阻断，不借通用秒数放行。
- 运维：会话/缓存/重试/保留、容量/延迟及issue SLA、RPO15分钟/RTO4小时有初始目标；准确Provider版本、目标硬件、实测报告、正式runbook及具名责任人仍须投用前核验。
- 设计包完成不代表生产可上线；必须有批准策略、实现、真实Provider/Adapter/E2E及适用现场证据。
