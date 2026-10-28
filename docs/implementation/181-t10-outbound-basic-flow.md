# T10 出库预留、拣选与直发基本链

- 状态：本基本软件子片已实现并验证，见[182证据](182-t10-outbound-basic-flow-tests.md)；按发起人基本功能优先及既有全部模块设计批准推进，完整T10仍保留明确软件依赖。
- Owner：WMS；FUNC-WMS-003/004、PAGE-WMS-004/005、REQ-WMS-015..019、DTC-WMS-004..006、RULE-01/02/07/08。
- 基线：[规格](../modules/wms-and-ledger.md)、[详细设计](../detailed-design/wms-and-ledger.md)、[规则](../detailed-design/05-approved-rules-and-defaults.md)、[就绪台账](readiness-register.md)。

## 1. 基本交付与范围

在现有合法资源、余额/不可变账本、预留、在途保护和当前授权上实现可操作的无运输出库链；不另造库存owner。覆盖多行草稿、全行审核/预留、在线实拣/短缺、非原拣选人受权确认未执行短缺释放、合法直发、取消与部分发后关闭余量，以及当前受权详情和具名页面。

分支固定为`DirectShipment`或`RequiresTransport`；后者可以记录拣选但不开放手工发运、取消已执行事实或假Completed。实际出库运输意图/可信完成由T15集成工作接入，本片明确显示待运输，不重用入库完成消息冒充出库证据。拣选确认从不扣库存。

本片显式选择来源合法balanceId，禁止隐式更换物料/单位/容器。一次计划1..100行，行ID及来源余额各自唯一；同维度拆行先合并为一行，避免重复预留歧义。单位/精度/批次/物料/来源位置/容器从合法余额与注册资源派生。当前资源模型没有效期/序列政策，不能接受调用者自报expiry或假FEFO；只支持既有基准单位/批次政策，FEFO完整日期与自动FIFO候选分配留为明确软件依赖，不宣称通过相关VAL变体。

## 2. 状态和精确数量

父单`Draft→Approved→Executing→Completed`；全部未执行安全取消为`Cancelled`；部分已发后关闭剩余为`ClosedPartial`。部分拣选/发运不自动减少计划或完成整单。

每行固定ExecutionId用于该行预留；多个发运操作具有独立shipment executionId，ledger消耗仍须唯一对应原预留，不能重新占用别的单据保护份额。若现有PostStock一执行一Shipment约束需要扩展，扩展必须保留原预留匹配和每次实际执行唯一键，不放松为任意消费。也可在基本实现中每行只确认一次实际发运：数量为已确认实拣、剩余通过ClosedPartial关闭；不声称多次部分发运能力。

基准用例：库存10/计划7，审核后Qty10/Reserved7；拣5缺2后仍10/7；另一受权操作人确认2未执行后10/5；合法发5后5/0，计划7/未发2不改且父Executing；关闭2后ClosedPartial。短缺不得自动释放，释放不得超过待核且确认未执行的数量。已拣、在途、Unknown、已经实物到达的数量不能当未执行取消。

API数量严格decimal string、0..6位且符合合法单位精度，不舍入/科学记数/超发；版本为非空不透明UUID。业务拒绝显式返回，永久原请求与回执不能靠改key/换body恢复成另一业务。

审查细化：一次直发量等于已确认实拣且不超过有效预留；已记录短缺必须全部经非原人复核释放。未拣且未报短缺的余量可继续受保护，直发后仅显式关闭才释放并ClosedPartial。零实拣/零发运且所有短缺已受权复核的Executing单可安全取消，不能仅因曾记录短缺事实永久卡住；真实实拣/在途/Unknown仍禁止。

## 3. 持久事务与并发

新增WMS出库header/行和append-only操作事实；每条操作保留tenant+operation唯一、actor、correlation、精确规范输入、原结果/版本及UTC。原键先重新核当前完整权限再返回原回执，不重新执行运营/schema或改写原事实。单据当前状态从持久事实/受约束行进度恢复并核一致性，不以可写header任意置完成。

审核所有行在一个本地事务校验并预留：父仓→全部排序源位→全部排序容器→父单/余额版本；任一行库存不足/停用/未知保护/越权/保存后撤权均整笔回滚。不能循环调用各自提交的ChangeStockReservation造成部分预留；复用现有reservation/domain/保存守卫，必要提取可组合stage逻辑。

使用可用量并集，拒绝入库待运输容器/相关位置及其他未决执行，源账面位不代表可再分配。相同余额两单各预留7（Qty10）仅一成功。取消/短缺释放与发运争抢同一资源和版本；新单不得消费其他单据reservation。

原输入统一保留上限65536字符，覆盖完整100行创建与审核；header与事实加载必须同一一致快照，防止正常并发误判历史损坏。其他执行的正Unknown/InTransit按相关来源容器/库位阻断，不仅靠余额剩余可用量扣减；入库pending目标位同样保持保护。

发运复用PostStock与原reservation同事务，新增出库事实/行进度/父单版本和永久回执，保存后权限复验失败全回滚。Shipment只对DirectShipment、当前有效预留和已拣数量开放；需运输分支不接受此命令。取消只释放确认未执行的普通Reserved保护，不清InTransit/Unknown；不删除流水、事实或预留tombstone。

## 4. 当前权限与HTTP

可信subject沿现有MasterDataSubject/IMasterDataAuthorization；creator取真实header，主数据creator逐一核，不能把调用者当资源创建人。每个新操作及原键恢复均需完整单据read字段，并核全部相关仓/位/单位/物料/容器read；新操作另核Enabled/schema、合法登记及余额版本。保存后二次权限核验失败整笔回滚。

单据read字段：`Id,Code,WarehouseId,State,Version,CreatedAt,Lines`；`wms.outbound.read`。动作权限：

| 权限 | 字段 |
|---|---|
| wms.outbound.create | Code,WarehouseId,Lines |
| wms.outbound.approve | State |
| wms.outbound.pick | LineId,PickedQuantity,ShortageQuantity,Reason |
| wms.outbound.release | LineId,Quantity,Reason |
| wms.outbound.ship | LineId,Quantity |
| wms.outbound.cancel | State,Reason |
| wms.outbound.close | State,Reason |

公开路径沿既有`/api/wms/outbound`：POST创建、GET `/{id}`详情；具名POST `/{id}/approval`、`/picking`、`/shortage-release`、`/shipment`、`/cancellation`、`/close-remainder`。默认关闭`Wes:WmsMasterData:OutboundEnabled`，依赖master-data/Identity及唯一合法schema。Host只绑定/鉴权/映射；严格JSON未知字段拒绝，Guid必填。HTTPS/JWT/X-WES-Client/单Idempotency-Key；actor/correlation只服务端生成。

创建body `{code,warehouseId,lines:[{id,balanceId,quantity,branch}]}`，branch固定数值0=DirectShipment/1=RequiresTransport；审核body `{expectedVersion,balances:[{balanceId,expectedVersion}]}`，必须覆盖原计划全部余额且无重复/额外；其他操作body包含expectedVersion及上表动作字段，字段camelCase，数量string。新操作201、精确重放200 `{decision,id,version}`，decision数值0=Committed/1=Replayed；Id为orderId；Location指GET。400非法/403越权/404不存在/409状态版本或库存冲突/422不支持或schema拒绝/503依赖/500安全错误，no-store。

详情GET响应`{decision,record}`，decision0=Success。record为上述七个read字段camelCase，state数值0Draft/1Approved/2Executing/3Completed/4Cancelled/5ClosedPartial。lines字段：`id,executionId,balanceId,itemId,unitId,batch,precision,containerId,locationId,branch,plannedQuantity,reservedQuantity,pickedQuantity,shortageQuantity,releasedQuantity,shippedQuantity`；数量固定六位字符串，不泄露私人payload/内部SQL。客户端严格验证形状和守恒，不从原写回执假造当前详情。

## 5. 页面与操作安全

新增`/wms/outbound`和`/wms/picking`具名入口，共享同一当前出库详情/操作面板，导航permission分别outbound.read与pick；不做生成式CRUD。提供已知UUID另读、完整多行草稿与动作预览/二次确认，冻结order/version/kind/key/body；提交后成功仅明确原操作结果，用户显式读当前详情。

10秒、断网、503、非法成功回执为Unknown，不自动重试/换key，不写业务本地缓存；原kind/id/key/body仅手工原样恢复。导航/主体变动取消旧请求并隔离迟到，详情失新鲜不得卸载Unknown面板。无权/403清旧，当前错误显式展示。需运输行显示“等待运输，不能手工发运”，不提供看似可用的完成按钮。移动/200%/键盘与零工作/未找到/入口未配置明确区分。

## 6. 验证、部署与边界

先红后绿覆盖TC-T10-BASIC-01..07：全行原子预留/竞争；10/7短拣序列；精度/身份/永久原tuple；不同creator逐字段/保存后撤权；Direct与Transport互斥及取消；多行失败/异常/取消回滚；HTTP严格输入/真实授权与页面Unknown/另读。

真实MySQL仅wes_test，先审Up/Down/增量SQL，保持现有账本/预留/入库运输兼容。相关Provider/模块/前端测试、架构/Release/EF无漂移必须取得证据；SQL Server/PostgreSQL延后。回退先停新入口，保留真实出库事实与reservation，存在新数据不能删表/回旧不识别状态的二进制。

本片不等于整个T10/T09或项目完成；后续基本工作仍按T11..T33依赖推进，实际FEFO、出库运输集成明确记入软件待办而非伪装外部验收。
