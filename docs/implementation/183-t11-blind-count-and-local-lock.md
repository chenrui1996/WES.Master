# T11 盲盘、局部锁与授权差异调整

- 状态：本盘点软件子片已实现验证，结果见[184](184-t11-blind-count-tests.md)；沿发起人已批准T11/RULE-06基线及基本功能优先授权。TransferOrder另续，不整体关闭T11。
- Owner：WMS；FUNC-WMS-005、PAGE-WMS-006、DTC-WMS-007、WMS-TC-012/013、RULE-02/06/08/25、VAL-01/02。
- 权威：[模块规格](../modules/wms-and-ledger.md)、[详细设计](../detailed-design/wms-and-ledger.md)、[规则](../detailed-design/05-approved-rules-and-defaults.md)、[就绪台账](readiness-register.md)。

## 1. 依赖顺序与范围

T11先实现盘点保护（所有库存写的共同依赖），随后独立续片实现多行TransferOrder，不并行修改同一账本primitive。不把后者当可靠性优化或外部验收关闭。比较只做报表/内部primitive、完整自动盘点调度和具名基本闭环，选择第三种：合法位置级计划、启动盲盘/锁、逐行计数修订、非盘点人差异审批、同事务账本调整/解锁、人工解锁失效与重新启动重盘。无自动阈值调账、自动解锁、设备命令或非盲盘选项。

一单固定一个仓与一个库位，锁覆盖该库位所有容器/合法余额，以及跨库位属于这些容器的活动冲突；启动时收集全部合法维度（含零余额，1..100行，超过100明确拒绝不截断），不得客户端挑行漏盘。未知新容器/新维度不能在有效锁期间通过登记/入库穿入。位置范围内没有合法余额明确Blocked，不创建假空盘。多库位大计划可拆独立计划，跨单统计/调度后置。

## 2. 生命周期与锁

状态固定`Draft=0,Counting=1,Ready=2,Completed=3,Invalidated=4`。创建Draft只固定范围。`start`在父仓→排序位置→排序容器资源锁内核当前合法资源/Enabled/schema、无任何正预留/在途/Unknown及pending入库源或目标保护；存在则阻断，不取消设备抢锁。

启动同事务固定私有账面数量/版本、快照UTC、明细ID与generation并激活持久位置锁，旧明细不能复用。计数人GET永远不返回账面量/差异/原余额版本或可推断私有量的原审批输入；即使有库存read也不在此计数契约附加它们。`review`独立权限可读取账面/差异；无计数不伪造差异为0。

局部锁不使用TTL释放：server now>=snapshotAt+1800秒只在GET提供reminderDue=true和负责人/时间，不释放锁或使实盘自动失效；消息推送/升级渠道另按Notifications后续集成。本P初值固定`WES-RULES-1.0/RULE-06/1800s`在详情显示。盘点业务参考快照不是普通分页/导出query-token，不将10分钟读tokenTTL用于自动解锁。

`record`需扫描位置/物料/容器与行匹配，数量非负且基准单位精度合法；append-only每次修订、原操作者/时间/原因/version，最后一次有效计数为当前。全部应盘行已计数才Ready。Ready允许修订但仍需重新审批当前版本。`adjust`明确受权、所有行已计数；任何非零差异的最终计数操作者不能批准整单（即使审批人同时有计数/库存权限），零差异无需第二人但仍必须显式授权确认。

差异>0用现有AdjustmentIn，<0用AdjustmentOut，0不造零流水。所有差异、逐行执行唯一键、计数Approved事实、父单Completed和解锁在同一WMS事务；任一行失败/撤权/取消全回滚，未解锁。复用现有唯一ledger写primitive，可提取同事务batch stage以支持多差异，不能循环各自提交的PostStock或在新owner重复造ledger。实际库存/版本与私有启动快照必须仍匹配，否则Blocked，不默默重基准。

`invalidate`须独立权限/原因/版本，在同一资源锁下终结当前有效锁并将全部计数标失效（历史保留），不能删除。旧版本record/adjust拒绝。Invalidated只能通过新的`start`重新获锁/重新取快照/生成新明细，必须重新盘；原key重放仍是原操作，不重启旧锁。Completed不允许再次启动/调整。

## 3. 全surface保护与并发

不仅在盘点接口检查锁。PostStock所有源/目标（含legacy内部primitive）、Reserve以及相关登记、入库受理/运输最终同步、出库审核/拣选/发运、仓/位启停都必须遵循同一位置/容器保护。锁创建与这些动作共享数据库fence/锁序，不能在先查后写之间绕过。原已提交操作精确重放不因后来盘点锁重新执行或错误Blocked；新写锁冲突显式业务拒绝。盘点自己只能以精确匹配当前计划/generation/全部调整事实的组合事务消费该锁，不能用公共bypass开关绕过。

同位置两个start最多一有效锁；start与现有预留/收货/发运竞争至多合法一侧成功；同单record/invalidate/adjust均CAS版本；header/事实/明细一致快照读取，正常并发不当损坏。tenant/永久键/actor/原tuple规则沿T10，创建1..100输入也需足够保留空间，不靠缓存防重复。

## 4. 当前权限与固定HTTP

沿当前IMasterDataAuthorization、真实creator逐资源字段与保存后复验；不默认给调用者All。每次新操作和永久回执恢复均需完整单据read与相关warehouse/location/item/unit/container read。写后撤权必须回滚，重放只返回原id/version、不返回私有计数。原key换body/action/order冲突。

权限固定：`wms.counts.read`七字段`Id,Code,WarehouseId,LocationId,State,Version,Lines`；`wms.counts.create`三字段`Code,WarehouseId,LocationId`；`wms.counts.start`字段`State`；`wms.counts.record`字段`LineId,Quantity,LocationId,ItemId,ContainerId,Reason`；`wms.counts.adjust`字段`State,Reason`；`wms.counts.invalidate`字段`State,Reason`；独立`wms.counts.review`字段`BookQuantity,Difference`。权限名遵循既有dot/alnum，不加hyphen。

默认关闭`Wes:WmsMasterData:CountsEnabled`，true依赖已有master-data/Identity；没有有效schema明确拒绝，不默认合法。沿既有`/api/wms/counts` POST创建、GET `/{id}`盲盘详情、GET `/{id}/review`受权差异详情；POST `/{id}/start,records,adjustment,invalidation`。

- 创建body `{code,warehouseId,locationId}`。
- start body `{expectedVersion}`（Draft或Invalidated）。
- records body `{expectedVersion,lineId,quantity,locationId,itemId,containerId,reason}`；quantity严格string，0允许；不采信客户端差异/账面。
- quantity是库存绝对数，允许22位整数/6位小数的InventoryQuantity范围；差异单次调整才受9位整数/6位小数的StockMovementQuantity限制，不能误用移动上限阻断大余额零差盘点。
- adjustment/invalidation body `{expectedVersion,reason}`。
- 所有写单一UUID Idempotency-Key/X-WES-Client/HTTPS，actor/correlation服务端；严格拒绝unknown/重复/缺字段/数字数量。
- 写201/精确重放200 `{decision:0|1,id,version}`，id均planId，Location指GET。400非法/403越权/404不存在/409状态版本保护冲突/422schema拒绝/503依赖/500完整性错误，no-store。
- GET200精确`{decision:0,record}`。record字段`id,code,warehouseId,locationId,state,version,createdAt,snapshotAt,lockOwnerId,reminderDue,policy,lines`；snapshotAt/lockOwnerId在未开始为null，Completed/Invalidated保留snapshotAt但lockOwnerId=null；policy固定`WES-RULES-1.0/RULE-06/1800s`；lines固定`id,itemId,unitId,batch,precision,containerId,quantity,countedBy,countedAt`。quantity/countedBy/countedAt在未计数为null，已计数quantity六位string，不含balanceId/bookQuantity/difference。
- review GET独立完整权限；同一record头/lines且每行额外`bookQuantity,difference`（六位string，未计数difference=null，负差允许）；仅review页面显示私有数量，不能把review结果缓存用于盲盘。

## 5. 页面与验证

`/wms/counts/:id?`具名页面：合法UUID范围建单→当前GET→start→扫描位置/物料/容器/精确实盘/修订→独立review GET→受权确认调整；另有明确“人工解锁使计数失效”。每种动作完整输入预览/二次确认，冻结原kind/id/key/body；10秒/离线/503/畸形回执为Unknown，禁止换key或自动队列。成功另读当前；Unknown面板位于查询stale生命周期外。主体变化清全部私有详情/审查数据/预览并取消迟到；盲盘与review明确区分。完整卡片，列偏好不适用；不跨单位求和，未盘不等于0。

TC-T11-COUNT-01..07：实盘12/8/10分别生成+2/-2/零流水；非零自批拒绝；所有行已数才调账且全行回滚；局部锁对源/目标、legacy及登记/预留/入库/出库均阻断；1800秒只提醒；解锁旧计数无效/重盘新generation；tenant/creator/撤权/原tuple/并发；严格HTTP/盲盘不泄露/复核单独字段；真实双账号浏览器Unknown恢复和最终ledger。

调整账沿已有ledger GET展示，`kind=3 AdjustmentIn`及`kind=4 AdjustmentOut`分别正/负差；前端类型、响应符号守卫、筛选与标签同步支持，不将合法调整后的历史整页判为非法。已Applied运输完成的可信原事件重放先恢复原posting，不因后续盘点锁误报Blocked；新执行及未Applied同步仍必须锁内检查。MySQL Guid集合谓词复用显式等式表达式，不用Provider已知失效的primitive Guid Contains。

仅wes_test迁移，Down仅审不执行；相关旧ledger/reservation/inboundtransport/outbound/库位回归、架构、Release0/0、前端全套/严格构建、EF无漂移和独立审查后方可关闭本子片并正常commit/push。尚未实现TransferOrder、T15真实设备运输、多库位盘点调度/通知升级/PDA专页，不宣称整个T11或项目完成。
