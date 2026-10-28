# T11 多行TransferOrder主体

- 状态：基本软件子片已验收；2026-10-29，证据见[186](186-t11-transfer-order-tests.md)。依据已批准RULE-07/08、FUNC-WMS-006、PAGE-WMS-007、DTC-WMS-008/009及RVT-02，不改模块架构；不整体关闭父T11。
- Owner：WMS；唯一数量写入仍为PostStock。实际T15/Pack、设备安全和生产部署不在本片许可内。
- 依赖：[盘点保护](183-t11-blind-count-and-local-lock.md)、[规则基线](../detailed-design/05-approved-rules-and-defaults.md)、[模块规格](../modules/wms-and-ledger.md)、[页面与测试](../detailed-design/wms-and-ledger.md)。

## 1. 实现选择与范围

直接复用原子单条Transfer而无父单会缺整单审核和逐单元恢复；只做人工整单一次确认会漏已到待同步核心规则；因此实现耐久多行父单、全行预留和每行固定执行单元。先具备WMS可信契约，不提前建另一套Orchestration或设备完成入口。

一单同仓1..100行，显式合法来源/目标balance，源≠目标，同物料/批次/单位/精度和容器，数量为正且符合基准单位。行、来源、目标、执行容器不得重复；不隐式换容器、不拆合托、不跨仓。当前ledger固定同容器移动，公开基本路径仅支持单合法正库存维度的整容器数量搬运；容器有其他正库存维度、计划只移动其一部分时明确Blocked，不能产生同容器两个正账面位置。多维度整容器编组/拆容器是后续主体能力，不据本片关闭相关父用例。DTC中的3/1执行量用各自合法整容器库存3/1建立，不谎称已支持来源10的部分容器拆分。

状态：`Draft=0,Approved=1,Executing=2,Completed=3,Cancelled=4,ClosedPartial=5`。行执行状态`Unstarted=0,InTransit=1,Arrived=2,Unknown=3,Closed=4`；行记账状态`None=0,Pending=1,Synced=2,Failed=3`。业务数量精确六位string，不跨单位合计。父执行/同步状态由行明确聚合，不能覆盖已完成行事实。

创建不占位、不发运输；审核同WMS事务校验全部合法资源/schema/Enabled/完整权限、版本、数量、源及目标保护，建立全部源预留和目标/容器保护。任一失败Draft及无有效部分预留。父仓→排序位置→排序容器资源fence后重查原operation；当前已成功tuple重放仍需当前权限。不能调用每行独立提交的Reserve构造伪整单原子。

目标零余额不是目标空位的充分条件：审核及最终sync还须复验目标整位置没有其他正库存容器或外部预留，且不同执行容器不得同时承诺同一目标位置。来源可共享位置但不能共享容器/账维度；sync再次验证源仍是精确全量单正维度，拒绝旁路改变后的隐式拆容器。此项按既有收货的容器占位边界落实，不改变JSON/枚举/权限合同。

人工行显式dispatch后InTransit；扫描确认最终到达时先在独立事务保存不可变完成证据，来源预留/目标保护不释放，再尝试唯一PostStock Transfer。成功时余额/预留消费/行进度/父状态/Outbox同事务提交；任一失败只保留已到证据、Pending/Failed及保护，后续sync只补该单元账，不再次搬运。A成功不因B失败回滚。Unknown必须继续保护，不允许普通关闭或自动重运；当前无可信核对证据时明确拒绝sync/释放。

运输行不允许人工dispatch/arrival伪装设备成功，必须经过显式可信adapter的固定意图/transport关联与最终完成合同（内部public service boundary，非任意HTTP completed）。若本片尚未实现该契约/真实T15，明确保留软件依赖及Unsupported，不伪造能力通过。

关闭仅对确认尚未执行行释放预留和目标保护；任何InTransit/Unknown/Arrived未同步行阻断。Draft/全部未执行安全取消为Cancelled；已有Synced行且安全关闭余量为ClosedPartial。全部完成且Synced才Completed，不修改计划/删事实。

## 2. 当前授权与持久化

复用现有IMasterDataAuthorization：`wms.transfers.read/create/approve/dispatch/arrive/sync/unknown/close`。read字段`Id,Code,WarehouseId,State,Version,CreatedAt,Lines`；create字段`Code,WarehouseId,Lines`；approve/dispatch/sync字段`State`；arrive字段`LineId,SourceLocationId,TargetLocationId,ContainerId,Quantity,Reason`；unknown/close字段`State,Reason`。如API动作包含LineId，动作权限仍由固定合同声明而非客户端自报。

对真实单据creator及父仓、源/目标位置、单位、物料、容器完整read权限交集两时点核验，当前权限失效保存后回滚。原回执只保留id/version，原tuple永久，GET另读当前；read在一致快照构造全部投影。源/目标资源creator不可用创建人假代。

按已有document+append-only fact与单元arrival receipt/inbox模式，tenant复合键/FK、code/operation、单元完成唯一约束；新增状态必须配精确事实。DbContext守卫验证Approved与全行reservation对应、Synced与精确唯一Transfer对应，不允许直接修改私有实体/余额或覆盖历史。到达未记账仍通过共享资源保护接入PostStock、预留、维度登记、收发、库位/仓启停及盘点，部署关闭入口不能解除保护。

## 3. 严格HTTP与前端接口

独立默认关闭`Wes:WmsMasterData:TransfersEnabled`，依赖master-data/Identity；非法组合启动失败。所有写需HTTPS/当前Identity/X-WES-Client/永久Idempotency-Key；拒绝未知/重复/缺失字段，不接受actor/tenant/correlation自报。operation及correlation来自既有Host可信边界。

- POST `/api/wms/transfers`：`{code,warehouseId,lines:[{id,sourceBalanceId,targetBalanceId,quantity,branch}]}`，branch0人工/1需运输。
- GET `/api/wms/transfers/{id}`：`{decision:0,record}`。
- POST `/{id}/approval`：`{expectedVersion,balances:[{balanceId,expectedVersion}]}`，精确全部源/目标余额版本一次预览固定。
- POST `/{id}/dispatch`：`{expectedVersion,lineId}`；仅人工行。
- POST `/{id}/arrivals`：`{expectedVersion,lineId,sourceLocationId,targetLocationId,containerId,quantity,reason}`；仅人工行当前InTransit且扫描完全匹配；quantity等于该固定单元全量。
- POST `/{id}/synchronizations`：`{expectedVersion,lineId}`；仅已有耐久到达证据，重试不重运。
- POST `/{id}/unknown`：`{expectedVersion,lineId,reason}`；仅当前在途，保护不释放。
- POST `/{id}/closure`：`{expectedVersion,reason}`；安全关闭剩余未执行。

所有写201/重放200精确`{decision:0|1,id,version}`，不由回执推断当前记账成功。到达事实已耐久但暂未记账时，回执说明原确认已保存，页面显式GET展示Pending/Failed，不把写201标为“移库已完成”。数据库结果未知使用原tuple恢复。

三个含reason的动作均要求1..256字符，不接受边界空白或控制字符；客户端长度/错误提示/textarea与owner一致，超长输入在预览前明确拒绝。

record精确：`id,code,warehouseId,state,version,createdAt,lines`。
line精确：`id,executionId,sourceBalanceId,targetBalanceId,itemId,unitId,batch,precision,containerId,sourceLocationId,targetLocationId,branch,plannedQuantity,reservedQuantity,movedQuantity,closedQuantity,executionStatus,postingStatus,sourceVersion,targetVersion`。数量六位非负string；sourceVersion/targetVersion为当前余额版本，供审核冻结预览，不替代单据expectedVersion。branch1未接入时明确待运输，不开放人工伪完成。

具名`/wms/transfers/:id?`：完整行卡片，多行创建、显式当前详情读取、预览冻结版本集、所有动作二次确认，Unknown原key/body手工恢复。查询stale不卸载原操作面板；主体/路由变化清私有数据并隔离迟到。10秒/离线/503/畸形回执Unknown，不保存本地写队列；详情显示“已到待同步/Unknown/已移/已关闭”区别。列偏好不适用；不跨单位汇总。

显式准备另一新草稿时清空上次单号/仓库/行及行身份，初始化一条空行；不会把已确认单据的旧行复用到下一单。原操作确认仍存在时不允许该动作，故不以清新草稿抹除待核验Unknown。

## 4. 测试与收口

| ID | 输入/故障 | 必须结果 |
|---|---|---|
| TC-T11-TRANSFER-01 | A3库存3/B3库存2、审核中故障、同源并发 | 全行审核失败无部分有效预留/目标保护；失败不Approved |
| TC-T11-TRANSFER-02 | 合法A3/B1审核，A dispatch/扫描到达，重放 | A唯一Transfer及消耗/进度/Outbox，父Executing，B不变 |
| TC-T11-TRANSFER-03 | B到达已耐久后PostStock故障、重启同key/sync | A不回滚，B待同步/失败且不可再分配；补账一次，全Synced才Completed |
| TC-T11-TRANSFER-04 | A完成、B确认未执行关闭；另B在途/Unknown/到达待同步 | 前者ClosedPartial保留计划/已移；后者不能释放/假取消 |
| TC-T11-TRANSFER-05 | 错位/容器/数量、源目标锁/占用/在途、整容器规则、超精度/版本 | 明确拒绝，无部分效果/运输成功兜底 |
| TC-T11-TRANSFER-06 | tenant/多creator/缺字段/保存后撤权、原key换tuple、并发与历史篡改 | 可信当前权限/精确原回执、CAS、无越租户和直接写绕过 |
| TC-T11-TRANSFER-07 | 默认关闭/严格HTTP；真实HTTPS及201截503、离线/迟到、窄屏/键盘 | 原确认恢复只一次、当前GET、无私有数据/本地写缓存泄漏 |

先记录实际RED/GREEN再实现；相关真实MySQL/HTTP/浏览器、前端完整/strict build、架构、Release、EF无漂移、增量SQL/格式及相对链接门禁。证据见[186](186-t11-transfer-order-tests.md)，未取得不得宣称关闭。只允许wes_test，无Down执行/生产/现场。

踩坑检查：INF-001/002/003/006/014/015/016、DEF-004/005/006、GEN-001/002/003/004/005/006适用；本片遇到可复用新问题及时更新[知识库](../engineering-pitfalls/README.md)，不把已查外部机制当已执行回归。
