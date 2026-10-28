# T09 耐久运输意图与最终到达移账设计

- 状态：本WMS契约软件子片已实现并验证；[执行计划](../superpowers/plans/2026-10-28-inbound-transport.md)/[独立证据](180-t09-durable-inbound-transport-tests.md)。实际T15、Pack与现场未验收。
- Owner：WMS；FUNC-WMS-002/007、PAGE-WMS-003、REQ-WMS-012..014、DTC-WMS-002/003、WMS-TC-003/004、RULE-01/02/08。
- 前置：[受权审核与收货](177-t09-authorized-inbound-execution.md)及其[验证](178-t09-authorized-inbound-execution-tests.md)已交付。
- 基线：[契约§4](../contracts/01-business-and-execution-contracts.md#4-wms与运输协作)、[WMS设计](../modules/wms-and-ledger.md)、[编排边界](../modules/orchestration.md)、[RULE-08](../detailed-design/05-approved-rules-and-defaults.md)、[依赖计划T09/T15](../superpowers/plans/2026-10-23-wes-development-work-packages.md)。

## 1. 范围选择

按已批准依赖计划优先完成T09的WMS本地耐久链，以显式可信契约夹具验证；不提前宣称T15订单/Leg、真实Pack、Broker、设备反馈或现场握手已经实现。当前仓库没有Orchestration实现项目，不能将其当成可调用能力。

替代方案是提前实现T15并接通运输订单/Leg/交接，会扩大本轮范围且仍依赖尚未接入的Pack。直接开放人工“到达/Completed”接口、用随机运输UUID或默认成功adapter代替编排均不采用。

本片范围：异址收货与唯一intent/Outbox/在途保护同事务；可信运输关联；可信最终完成Inbox；源出/目标入唯一原子移账；入库详情恢复与保护释放。无实际编排装配时只显示待运输，不发任何设备命令。新接口仅为模块间契约，不新增操作者可提交的完成HTTP或前端完成按钮。

## 2. 库存与容器不变量

1. 同址保持原有无运输完成路径，不创建intent、运输Outbox或在途保护。
2. 异址真实收货一旦提交不得因后续运输创建失败而撤销；来源仍是账面位，不表示实物位置。
3. 收货提交时同一执行单元同步建立在途保护，复用现有reservation/availability数量并集。不能只增加页面标签，也不能在异步投递之后才保护。Reserved和InTransit重叠不重复扣减。
4. 原执行单元的在途保护只能由匹配的最终移账消费，不能用普通未执行释放、超时、禁用、重发或Unknown回执解除。另一执行单元不能再次预留或派发该容器。
5. 本基本子集只准整容器精确身份的单维度运输：进入实际派发前，容器全部正库存必须恰为本执行单元在源维度的数量，且无其他未决执行/预留。若还装有其他库存，不自动搬动其他账、拆分容器或让同容器分处两账面位；保持阻断并提供明确原因。整容器多物料/多批次编组是后续独立设计，不作为本片假成功。
6. 最终账务目标须是同仓、同物料/批次/容器/基准单位/精度的合法已登记维度。通过既有受权维度登记流程预先准备；不从完成消息采信balanceId，不自动伪造运营登记。缺失时保留可信完成消息及PendingSync，准备后按原事件重核，不重新运输。目标位被其他容器正库存、相关剩余预留或未完成执行占用时同样不能同步；在派发前和最终移账锁内复验，不能因原容器整量正确而忽略目标位冲突。

第3至6项涉及现有reservation保存守卫、原执行保护及目标维度前置条件，必须与新Inbox一同实施、测试；不能把它们作为非主体可靠性增强跳过。

## 3. 契约与信任边界

- 输出沿用`TransportIntent`：intentId/documentId/executionUnitId/containerId/source/target/quantity/unitId/policyVersion，外包版本化DurableEvent身份。tenant、correlation、causation及UTC来自可信WMS事务；数量使用严格decimal string，policyVersion固定于意图，不读日后可变值重建原消息。
- 运输关联须有可信Orchestration确认的原intent、executionUnit、唯一transport及对应版本。记录的是已持久接收的逻辑运输身份，不表示设备Accepted或到达。
- 输入沿用`TransportCompletion`：completionId/intentId/executionUnitId/transportId/finalLocationId/evidenceRefs/completedAt，并保留可信producer、schemaVersion及事件身份。
- Inbox前必须经显式模块间可信适配边界验证生产者、tenant、完整意图、transport及最终证据；字段合法或owner字符串等于Orchestration本身不是可信证明。拒绝跨租户、未知版本、错执行/运输/位置及无最终证据；依赖不可用明确返回，不能当Verified。
- 可信事实处理使用服务主体与原事实关联，不冒充原收货人发起新控制；原人撤权不删除真实物理事实。人工业务命令仍查当前权限，不借服务入口绕过。
- 测试夹具明确标记为契约验证，不进入默认生产DI。没有真实可信适配器，不注册后台消费者/dispatcher、不装配默认Verified/Ready实现。

## 4. WMS数据与恢复

运输数据独立于现有主数据创建Outbox；后者的source校验、publisher和诊断当前专用于MasterDataChange，不能将新payload直接塞进去。

实现使用两张WMS-owned表：不可变意图/原始消息及一次性版本化运输关联、独立完成Inbox。受信接受回执保留在不可变具名`TransportLinked`历史中，关联与父单事实同事务，不另造第三张接受表。唯一键覆盖tenant+executionUnit、tenant+eventId及每个intent的唯一transport；原语义payload不可修改，更新只能推进有版本的关联/同步状态。FK仅指WMS自己拥有的单据、收货事实及账本，不引用Orchestration私表。

原收货事实继续是execution身份的唯一来源。新增关联/到达事实使用独立operation及payload内的执行引用；不能把相同executionId再次写入现有Received专用唯一索引。扩展Restore时严格验证序列、原版本链、关联与到达tuple，不能只覆盖header.State。

完成Inbox保留PendingSync与Applied的区别：可信到达与WMS同步是两个事实。相同事件不同body/producer/执行tuple明确冲突；同一实物执行的第二完成身份不得产生第二次记账。原事件重送重新核可信来源及精确tuple，Applied只能返回原移账身份/版本，不推进新版本。

177以前的内部primitive/历史异址收货不自动补造运输或把既有事实收编为新请求。迁移不回填成功，旧pending继续受保护；需要历史恢复时另走明确有审计的恢复用例。

## 5. 事务与并发

异址公开收货在现有PostStock组合事务内增加意图、固定消息与同execution的在途保护，保存后权限复验失败时全部回滚。同址与旧内部primitive的既有合同保持明确，不悄悄给内部调用授予运营或运输许可。

关联/最终同步使用独立clean owner、可信tenant及当前事务守卫。锁序保持父仓→排序去重源/目标位→容器，再核父单/余额版本；多个完成、关联、收货与启停争抢同一资源不得形成反向锁序。

最终同步仍复用唯一PostStock移动：同一事务提交源扣减、目标增加、两条带符号entry、原执行保护消费、到达业务事实、单据版本/状态和Inbox Applied回执。任一保存后故障、取消或版本竞争均整笔回滚。不能在移账提交前把Inbox标成Applied，也不能在WMS同步失败时重新派发已完成运输。

共享`InboundPendingProtection`仅在匹配的耐久到达业务事实、唯一Transfer posting及Applied Inbox同时成立后排除原receipt；不以TransportId非空、Orchestration完成状态、API成功或内存对象的Arrived单独释放。Transfer是既有账本类型，本文“移动/Move”为业务描述，不新增第二种移账类型。库位禁用、仓库禁用、新收货/分配与所有容器执行入口使用同一有效保护语义。

## 6. 读取、运行和回退

既有入库详情GET和页面继续展示实际transportId/arrived/状态，需从耐久历史恢复而非客户端本地填补。库存availability读取从相同reservation事实得到在途/不可用数量。原八字段当前授权、403清旧及Unknown原操作恢复不变；不会给前端增加提交运输完成的权限。

意图表同时保存固定原event/完整消息：未关联为待投递，非空TransportId为已经耐久关联；显式worker重调交付同一原消息，接收方须按契约Inbox保证唯一逻辑运输。T09仅验证契约夹具下的接受/丢回执/恢复，不实现或宣称Broker租约、退避、死信或自动投递runner。后续自动投递的租约、预算与重试必须沿既定规则，不能因还无T15而新增无界重试。没有实际适配器时能力关闭，不注册默认成功实现。

仅在批准隔离wes_test上验证MySQL迁移/事务。先审Up/Down/增量SQL，不改旧账、不宣称其他Provider等价。回退先停新接收/投递，保留真实收货、未决意图/完成Inbox及保护；存在已使用新事实时不得删表或回旧二进制误读历史，须采用兼容版本前滚恢复。

## 7. 实施顺序与验收用例

| Case | 先红测试及预期 |
|---|---|
| TC-T09-TRANSPORT-01 | 同址运输0；异址唯一intent/固定Outbox/在途保护与收货同事务；保存后撤权/故障/取消全部回滚；原tuple恢复无第二意图 |
| TC-T09-TRANSPORT-02 | 发送接受后回执丢、同事件重复/竞争/重启：唯一逻辑运输，关联不假到达；无适配器明确待运输；无设备调用 |
| TC-T09-TRANSPORT-03 | 验schema/producer/tenant、intent/execution/transport/finalLocation及最终证据；错tuple/无证据不移账；新键不得为同execution二次记账 |
| TC-T09-TRANSPORT-04 | 源5/合法目标0最终仅一次Move及两entry→源0/目标5；匹配保护消费、Inbox Applied及单据Completed同时提交；重复/不同事件并发及保存后故障无二效 |
| TC-T09-TRANSPORT-05 | 缺目标维度、其他容器占位、额外库存/预留、错精度或未知到达保持PendingSync和保护；修复前置条件后原可信事件恢复，不重复运输 |
| TC-T09-TRANSPORT-06 | 在途与预留并集、同容器二次预留/派发拒绝；普通释放/超时/禁用不解除；最终提交后共享保护释放且仍核真实库存阻断 |
| TC-T09-TRANSPORT-07 | clean-owner/跨tenant/原消息不可改删，真实GET历史恢复/当前授权；旧receipt与同址回归、旧Outbox诊断不受影响；架构、Release、EF无漂移和SQL审查 |

实施依赖顺序为：可信契约与领域事实→同事务意图/保护→耐久关联→完成Inbox/唯一移账→所有共享保护及读取投影→真实MySQL并发/故障与HTTP读取回归。实际执行结果、审查修复与未运行边界见[独立测试记录](180-t09-durable-inbound-transport-tests.md)，未取得结果不记通过。
