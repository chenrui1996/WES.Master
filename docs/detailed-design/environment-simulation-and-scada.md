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
| FUNC-SITE-001 | 001；REQ-SITE-005；SITE-TC-004 | 1保存工程草稿；2校验节点/边/资源/设备/映射；3生成固定版本/hash/问题集；4有error拒绝部署，warning按权限确认。Validate不直接修改运行主数据，编辑后旧校验不可冒充新版本通过 |
| FUNC-SITE-002 | 002；REQ-SITE-006..007；SITE-TC-005..006/029..033 | 1Simulator创建Preparing manifest；2各owner暂存不可变artifact并回Prepared(version,hash)；3核必需参与者/确认有效性；4Simulator本地事务切active并写Outbox；5幂等通知/查询manifest对账；6激活后回滚新建补偿deployment。活动任务固定旧MapVersion由Pack安全决定，不改旧路径 |
| FUNC-SITE-003 | 003/004；REQ-SITE-008..009；SITE-TC-007..008 | 1固定map/设备模型/seed/clock/任务；2按同语义契约运行与注入；3保存结果/日志/参数；4Reset预览并隔离清理。仿真与生产身份边界独立核验，不能删除真实账本/任务以恢复仿真 |
| FUNC-SITE-004 | 005；REQ-SITE-010..011；SITE-TC-009..011 | 1Preview验证环境/endpoint/身份/能力/版本/证据；2授权/复核/幂等操作；3只读联调→受控单设备→批准扩大；4失败恢复已知配置且在途物理任务由Pack核对。Promote不是清任务/库存或绕过部署manifest的捷径 |
| FUNC-SITE-005 | 006；REQ-SITE-001..004；SITE-TC-001..003 | 1受权view/binding保存；2通过owner投影装配；3版本/时间验证；4断线重查、未知明确。SCADA view坐标变更不写WMS坐标或Route，运行态只读 |

参与者Prepared必须绑定DeploymentId/owner/version/hash和确认状态；人工点“准备完成”不能替代owner验证。激活事件漏投用持久manifest修复，不重写owner私表。激活后旧任务命令携原MapVersion和可验证的任务版本策略，不因当前active变化无条件拒绝其合法继续；新任务必须使用有效活动部署（具体在Pack契约落实）。

## 3. 详细测试

夹具工程E1/mapMV1，部署D1 active，候选MV2；ownerWMS/ST/FW/SCADA；ST活动T1绑定MV1；所有物理测试使用隔离虚拟Adapter。

| DTC定义/功能/原TC | 数据/编号步骤 | 断言 | 级别/环境/清理 |
|---|---|---|---|
| DTC-SITE-001 / 001 / SITE-TC-004 | 候选变体缺节点/重复ID/孤边/错Pack/未知冲突资源；1Validate；2直调Deploy；3修改草稿重用旧摘要 | 精确定位对象/引用链；非法运行artifact/激活0；修改后校验失效；不产生部分可用部署 | P0/U+B+E/C0 |
| DTC-SITE-002 / 002 / SITE-TC-005/029..030 | 1创建MV2部署K1两次；2FW准备失败；3分别注入缺/陈旧/错hash确认；4请求激活 | 同K同manifest；D1 active不变；新控制不误认MV2；阻断owner可见；不接受其他Deployment确认 | P0/B+C+E/C1 |
| DTC-SITE-003 / 002 / SITE-TC-032 | 全owner精确Prepared；1激活提交后publisher崩溃；2重启重复事件；3一个owner漏收再查询 | active指针和Outbox同事务；一次逻辑激活；漏收可对账准确版本；不因重复事件重复导入/改库存 | P0/B+C+E/C1 |
| DTC-SITE-004 / 002 / SITE-TC-006/031/033 | T1正执行MV1；1激活MV2；2新任务用MV2；3回滚创建D3引用MV1；4T1继续/安全暂停按策略 | T1路径/预约仍MV1；新任务版本匹配；补偿新记录保留D1/D2；无清任务/覆盖历史；有物理迁移决策证据 | P0/B+C+E/C1 |
| DTC-SITE-005 / 003 / SITE-TC-007 | 同map/seed7/clock任务集；Trigger/Gateway变体；1正常运行；2反馈延迟/丢失/重复/错序；3重放 | 对应真实状态机，不跳段/二次完成；相同输入可复现；结果含版本/hash/注入/日志，仿真标签明确 | P0/S+C+E/C0 |
| DTC-SITE-006 / 003 / SITE-TC-008 | Reset预览含仿真runR1、生产ledgerL1和非仿真活动T1；1申请Reset；2合法仅R1范围再确认 | 危险范围阻断；生产L1/T1及保护资源不删；合法清理只影响R1运行态且保留批准结果证据/审计 | P0/B+E/C1 |
| DTC-SITE-007 / 004 / SITE-TC-009..010 | localhost/回环/测试secret/能力不支持/无复核变体；1Preview；2直调Promote；3合法批准后同K重试 | 所有blocker可见/拒绝；生产切换0；合法一次逻辑Promote；秘密不回显、审批/actor/差异可查 | P0/B+C+E/C1 |
| DTC-SITE-008 / 004 / SITE-TC-011 | 批准单设备试运行，步骤中断且命令Unknown；1故障；2按回滚计划恢复配置；3查设备/任务 | 配置恢复可证明、Unknown任务保留待核；库存/活跃运输未清空；不显示“已安全恢复”直到权威证据 | P0/B+C；H需审批/C1 |
| DTC-SITE-009 / 005 / SITE-TC-001..003 | viewV1绑定R1，R2不存在；1改view坐标；2注入stale/错序；3只读用户尝试运行写 | view变而WMS主数据/Route hash不变；R2 Unknown、旧seq不回退、过期Stale；运行写拒绝，数据库运行事实0变化 | P0/B+C+E/C0 |

## 4. 责任与阻断

工程编辑不涵盖运维处置；issue/指派/解决在Orchestration。准备确认时限、生产身份/网络清单、warning放行等级、Promote双人策略/真实试运行和回滚手册需签核。跨模块prepare/activate可恢复工作流不是全模块ACID；不能为简化页面删除该失败语义。
