# 环境、地图部署、仿真与 SCADA 页面设计

- 状态：评审中；DTC均designed/未执行
- 权威：[环境/仿真/SCADA](../modules/environment-simulation-and-scada.md)；SCOPE-DEV-02..03；REQ-SITE-001..011
- 共用：[标准](00-shared-page-and-test-standard.md)、[Route包](route.md)、[参考](01-reference-cases.md)
- 产品阅读：工程人员的版本化地图编辑/分步部署和只读状态画面；DENSITY=3、DATA_COMPLEXITY=3、CONSEQUENCE=3。现场运维处置不属于本包。

## 1. 页面

| PAGE定义 | 用户/入口 | 文本线框、字段及验证 | 查询/操作/返回 |
|---|---|---|---|
| PAGE-SITE-001 | 工程编辑员；`/simulator/projects` | L+F：工程列表 → 草稿编辑画布/对象树 → 属性 → 校验问题。稳定node/edge/resource/device/申请点ID、坐标/方向、层/Pack、能力、schema/工程版本；有限非负权重、合法引用 | 编辑草稿/保存新版本/Validate；重复ID/孤边可定位；发布后不可原位编辑；回列表保留工程范围 |
| PAGE-SITE-002 | 部署员/复核者；`/simulator/deployments/:id` | W：环境/差异 → immutable manifest → WMS/各Pack/SCADA Prepared表 → 激活/结果。DeploymentId、artifact version/hash、校验摘要、确认时间/状态/错误、active pointer、Outbox引用 | 按环境/状态/版本筛选；创建部署/受权激活/查询恢复/补偿部署；不能“忽略失败owner继续” |
| PAGE-SITE-003 | 仿真工程师；`/simulator/runs` | W+O：地图/模型/seed/时钟 → Trigger/Gateway模式 → 负载/故障计划 → 执行 → 结果/重放。vehicle/任务数、延迟/丢失/重复/错序、软件/planner版本、hash | 起停仿真/查看结果/同输入重放；模式/能力匹配校验；结果显示软件断言而非“物理安全”徽标 |
| PAGE-SITE-004 | 仿真清理授权员；运行详情Reset面板 | F：运行ID/环境 → 清理对象预览 → 被保护对象/阻断 → 确认/回执。run/tenant/version/对象类型，生产/活跃非仿真任务不得入清理集合 | Reset只清明确仿真运行态；对计划/地图版本保留策略明确；回运行结果保留证据 |
| PAGE-SITE-005 | 生产发布员/设备复核者；`/simulator/promote` | W：源/目标环境 → endpoint/协议/身份/能力差异 → 联调/签核/回滚 → 试运行阶段 → 结果。secret仅引用、MapVersion/manifest、enabled Packs、Adapter/安全证据 | Preview/提交审批/批准后Promote；localhost/回环/测试身份/能力缺失阻断；失败恢复已知配置并核对在途任务 |
| PAGE-SITE-006 | SCADA只读查看员/绑定配置员分权；`/scada` | M只读：视图/区域 → 地图设备/库位 → 状态/来源/观测时间；绑定配置独立编辑模式。view坐标/节点绑定属SCADA，库存/任务/设备数据各owner | 图层/层/Pack/Unknown筛选；保存受权view绑定不改WMS位置/Route；任何运行控制跳转Orchestration地图，不在此执行 |

状态适用：001适用UI-001..011（草稿无设备C1）；002..005适用UI-001/003..010/012，002/003历史列表另UI-002/011；006适用UI-001..008/011/012，view配置另UI-009。006控制写不适用，但必须直调验证拒绝。无位置不画空闲，部分owner查失败仍保留Unknown队列。002/005执行阶段未知工作量只展示阶段/耗时/最近确认，不能按owner数量假装物理完成百分比。

```text
目标环境（仿真/生产明确标识）  工程/校验版本
Manifest参与者 | artifact version/hash | Prepared/阻断/时间
旧active -> 待激活版本        | 差异/活动任务影响
授权/复核/确认               | 执行阶段/可恢复操作ID
失败owner与恢复建议          | 不覆盖旧记录的补偿入口
```

## 2. 功能

| FUNC定义 | 页面/REQ/原TC | 主流程、事务与失败 |
|---|---|---|
| FUNC-SITE-001 | 001；REQ-SITE-005；SITE-TC-004 | 1保存草稿；2校验节点/边/资源/设备/映射；3固定版本/hash/问题集；4error拒绝部署，仿真warning受权确认，生产warning默认拒绝、仅已分类非安全展示项可逐条理由复核。Validate不改运行主数据，修改后旧校验失效 |
| FUNC-SITE-002 | 002；REQ-SITE-006..007；SITE-TC-005..006/029..033 | 1Simulator创建Preparing manifest；2各owner暂存不可变artifact并回Prepared(version,hash)；3核必需参与者/确认有效性；4Simulator本地事务切active并写Outbox；5幂等通知/查询manifest对账；6激活后回滚新建补偿deployment。活动任务固定旧MapVersion由Pack安全决定，不改旧路径 |
| FUNC-SITE-003 | 003/004；REQ-SITE-008..009；SITE-TC-007..008 | 1固定map/设备模型/seed/clock/任务；2按同语义契约运行与注入；3保存结果/日志/参数；4Reset预览并隔离清理。仿真与生产身份边界独立核验，不能删除真实账本/任务以恢复仿真 |
| FUNC-SITE-004 | 005；REQ-SITE-010..011；SITE-TC-009..011 | 1Preview验证环境/endpoint/身份/能力/版本/证据；2授权/复核/幂等操作；3只读联调→受控单设备→批准扩大；4失败恢复已知配置且在途物理任务由Pack核对。Promote不是清任务/库存或绕过部署manifest的捷径 |
| FUNC-SITE-005 | 006；REQ-SITE-001..004；SITE-TC-001..003 | 1受权view/binding保存；2通过owner投影装配；3版本/时间验证；4断线重查、未知明确。SCADA view坐标变更不写WMS坐标或Route，运行态只读 |

参与者Prepared绑定DeploymentId/owner/version/hash/expectedActive代数和确认时间，有效10分钟。按已签核[规则基线§2.1/2.4](05-approved-rules-and-defaults.md)，activate本地事务CAS旧active/代数后递增generation并写Outbox；冲突须重新preview/prepare/审批。默认DrainAffected按Pack排空且无Unknown才激活，CompatibleContinue须兼容证据及跨版本物理域统一检查。旧任务携固定taskDeploymentId/MapVersion，仍核当前epoch/grant/freshness及允许继续策略；新任务只用当前active。漏事件查manifest，不改owner私表；批准不能替代真实准备/排空证据。

## 3. 详细测试

夹具工程E1/mapMV1，部署D1 active，候选MV2；ownerWMS/ST/FW/SCADA；ST活动T1绑定MV1；所有物理测试使用隔离虚拟Adapter。

| DTC定义/功能/原TC | 数据/编号步骤 | 断言 | 级别/环境/清理 |
|---|---|---|---|
| DTC-SITE-001 / 001 / SITE-TC-004 | 缺节点/重复ID/孤边/错Pack/未知冲突域及安全相关/非安全展示warning变体；1Validate；2直调Deploy；3改草稿重用摘要；4同warning分别仿真受权确认/生产普通确认/生产分类展示项逐条理由复核 | 非法artifact/激活0且定位对象；修改旧摘要失效；仿真许可不带入生产，安全/H类warning不可人工豁免，生产仅已分类展示项且完整复核可放行 | P0/U+B+E/C0 |
| DTC-SITE-002 / 002 / SITE-TC-005/029..030 | 1创建MV2部署K1两次；2FW准备失败；3分别注入缺/陈旧/错hash确认；4请求激活 | 同K同manifest；D1 active不变；新控制不误认MV2；阻断owner可见；不接受其他Deployment确认 | P0/B+C+E/C1 |
| DTC-SITE-003 / 002 / SITE-TC-032 | D2/D3同expectedActive=D1/代数且全Prepared；1并发激活；2胜出提交后publisher崩溃；3重启重复/漏事件；4Prepared599/600秒独立变体 | CAS最多一胜，另一冲突不自动覆盖；active/代数/Outbox同事务，漏收查manifest；Prepared599秒其他前提合法可用/600过期拒绝，不重复导入/记账。RVT-04必测 | P0/B+C+E/C1 |
| DTC-SITE-004 / 002 / SITE-TC-006/031/033 | T1在MV1窄道R1，还需后续grant；MV2改名R9；1默认Drain请求；2独立兼容证据齐全；3缺映射/拆并；4新旧任务；5补偿回滚 | Drain拒新任务/新版本grant，T1仅获合规旧图排空grant，暂停未终结仍拒激活，排空/无Unknown才激活；兼容旧T1原部署+当前epoch/grant继续，新任务只active且T2冲突拒；无映射/旧图新建拒，补偿同CAS/排空留历史。RVT-01/04必测 | P0/B+C+E/C1 |
| DTC-SITE-005 / 003 / SITE-TC-007 | 同map/seed7/clock任务集；Trigger/Gateway变体；1正常运行；2反馈延迟/丢失/重复/错序；3重放 | 对应真实状态机，不跳段/二次完成；相同输入可复现；结果含版本/hash/注入/日志，仿真标签明确 | P0/S+C+E/C0 |
| DTC-SITE-006 / 003 / SITE-TC-008 | Reset预览含仿真runR1、生产ledgerL1和非仿真活动T1；1申请Reset；2合法仅R1范围再确认 | 危险范围阻断；生产L1/T1及保护资源不删；合法清理只影响R1运行态且保留批准结果证据/审计 | P0/B+E/C1 |
| DTC-SITE-007 / 004 / SITE-TC-009..010 | localhost/回环/测试secret/能力不支持/无复核变体；1Preview；2直调Promote；3合法批准后同K重试 | 所有blocker可见/拒绝；生产切换0；合法一次逻辑Promote；秘密不回显、审批/actor/差异可查 | P0/B+C+E/C1 |
| DTC-SITE-008 / 004 / SITE-TC-011 | 批准单设备试运行，步骤中断且命令Unknown；1故障；2按回滚计划恢复配置；3查设备/任务 | 配置恢复可证明、Unknown任务保留待核；库存/活跃运输未清空；不显示“已安全恢复”直到权威证据 | P0/B+C；H需审批/C1 |
| DTC-SITE-009 / 005 / SITE-TC-001..003 | viewV1绑定R1，R2不存在；1改view坐标；2注入stale/错序；3只读用户尝试运行写 | view变而WMS主数据/Route hash不变；R2 Unknown、旧seq不回退、过期Stale；运行写拒绝，数据库运行事实0变化 | P0/B+C+E/C0 |

### 3.1 工程/部署密集交互配置

遵循[交互合同](03-dense-page-interaction-contract.md)，数字为PAGE-SITE；G/F/R/M/W为共用测试配置，`!`必显、`+`可选。参与者列表、地图对象树与仿真结果各有独立tableId；画布不是Grid。

| 页面 | 配置 | 默认顺序/可选列 | 新增/编辑/关联筛选与密集操作 |
|---|---|---|---|
| 001 | G+F；画布M | 工程码!、草稿/版本!、校验状态!、更新时间；+hash；对象表：对象码!、类型、Pack/层、引用/校验状态! | 工程→草稿版本→Pack/层→节点/资源/设备绑定。对象类型限制字段，边起终点只选同合法图节点；改Pack/层预览悬空引用，拒绝静默删除；明确树节点新增可继承层，不复制稳定ID |
| 002 | W；参与者/历史G | DeploymentId!、目标环境!、激活状态!、旧/新active；参与者：owner!、artifact版本/hash!、Prepared/失败!、确认时间!；+安全错误 | 工程不可变版本→所需owner artifacts，与环境/Pack能力交叉验证。改任一版本/hash使旧prepare/preview失效，不沿用确认；列隐藏不减少必需参与者，不能勾选“跳过失败” |
| 003 | G+W | runID!、仿真环境!、运行状态!、map/model版本、seed、开始/结束；+软件/planner/hash | 模式→模型能力/注入类型，map→车/节点/资源，故障目标限本次manifest；换map先清不再合法负载/目标。重放固定输入，不把列表条件当新run参数；按状态/模式分组 |
| 004 | W+R；预览集合G | 环境/runID!、清理类别!、对象数/保护数!、阻断/审批状态!；+对象来源/版本 | run固定→可清理仿真对象，默认不选择受保护项；改变run使preview/审批失效；无跨run/生产候选，不因收起组而清受保护对象 |
| 005 | W；差异表G | 源/目标环境!、阶段/审批状态!、manifest版本/hash!；差异：设备码!、协议/身份/能力差异!、验证结果!；+联调证据引用 | 源manifest→批准目标环境→对应真实Adapter/endpoint引用；切目标清旧验证/审批，不继承列表当前环境为生产目标；不自动切真机，不把秘密endpoint放入个人视图 |
| 006 | M；只读资产队列G，绑定编辑F | view/部署!、状态质量/观测时间!；队列：资源码!、类型、Pack/层、状态/Unknown!；+owner/源版本 | 只读view/区域筛选；绑定编辑：view→对象→受权owner资源引用。仅改变SCADA绑定，跨owner同名资源必须type+ID区分；无运维动作或库存/Route坐标编辑 |

| DTC定义/功能/原TC | 固定前置与编号步骤 | UI/持久化/副作用断言 | 级别/环境/清理 |
|---|---|---|---|
| DTC-SITE-010 / 001..005 / SITE-TC-004..011/029..033 | MV1/MV2、D1已prepare MV1、runR1仿真、真实目标E2未验证、SCADA同名资源跨Pack；1草稿换层处理悬空边；2D1改artifact且旧prepare回；3调owner列；4Reset换run；5Promote换目标；6view绑定旧resolve回 | 悬空引用不静默成功；旧prepare不授权新版本、必需owner/hash/环境不可隐藏；Reset/Promote旧preview失效且执行0；SCADA关联不串Pack/不改WMS或Route；视图/列变化无激活/生产命令 | P0/E+B+C/C1 |

## 4. 责任与阻断

工程编辑不含现场运维。Prepared/高后果审批均10分钟、双人绑定、默认Pack排空和CAS按[规则基线](05-approved-rules-and-defaults.md)已定；warning默认阻断生产激活，只有明确非安全展示类warning可由复核者逐条理由放行。Promote默认只读联调，不自动升单设备/扩大阶段。生产身份/网络、兼容/物理输入、真实试运行及正式runbook缺证据为H阻断，不因文档已签核跳过。跨模块仍非ACID。
