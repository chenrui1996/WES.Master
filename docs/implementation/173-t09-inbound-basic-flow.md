# T09 入库基本流程

- 状态：实施中；领域与内部持久收货事务已验证，当前授权 HTTP/页面及最终运输持久接线未完成。
- 需求：FUNC-WMS-002 / PAGE-WMS-003；DTC-WMS-002/003，VAL-01。
- 基线：[WMS 规格](../modules/wms-and-ledger.md)、[规则](../detailed-design/05-approved-rules-and-defaults.md)、[交付顺序](TodoList.md)。

## 最小完整目标与顺序

先构建 WMS 入库单与收货单元的状态不变量，再接 owner 事务、当前授权 HTTP 和具名页面。基本链路为多行草稿→审核→分次收货→收货位唯一入账→同址完成或异址待运输；异址仅在核验最终到达后移账。T15 未接入时明确待运输，不能显示假运输创建成功。

本片先实现内部领域状态机及纯领域测试，不开放未经持久幂等/当前权限保护的写 API。后续事务必须将收货事实、不可变账本、原操作回执与 Outbox 同一次提交，不得先调用会自行提交的 PostStock 再另写单据。原 ledger 校验/保护逻辑应复用，不复制第二个记账器。

## 领域合同

- 显式 tenant/warehouse、单号及有界多行；行固定 item/unit/precision/batch，计划为正且精确、不舍入。收货必须匹配完整维度，零超收。
- 收货单元固定原 operation、actor、execution、行、容器、收货/目标位及数量。相同原键/执行及语义重放返回原单元，换 payload/actor 拒绝，不以 correlation 改写事实。
- 收货按 2+3 满足计划 5；2+4 拒绝且无残余。部分收货不自动关闭剩余。同址无需 transport intent，异址保持原收货事实和唯一意图。
- 最终到达要求原单元、已关联运输、源/目标及容器一致；错反馈不推进，重复正确反馈无第二状态效果。只有全部计划已收到且全部单元到达才 Completed。
- 领域对象不访问 Pack、HTTP 或数据库，不声称进程内去重等于持久幂等。存储需 tenant 复合键、版本 fence 和永久唯一 operation/execution；未实现前不开放应用入口。

## 测试与后续接线

先以缺失类型建立 RED，再验证 5 分收 2+3、2+4/错物料/精度拒绝、同址无运输、异址等待、原键重放/冲突、错运输/容器/位置、重复到达和多行不提前完成。owner 接线还需 MySQL 原子回滚、并发超收、永久重放和当前权限测试；页面/HTTP 未实现保持待办。

## 持久事务续片

采用 WMS 自有入库单行（有界计划 JSON）与 append-only 操作事实表；不是跨模块共享表。每次恢复按持久计划及按序事实重建内部状态机，数据库版本 fence 防止同一计划并发超收。事实保留原 actor/operation/execution/完整 tuple；收货 execution 同时作为稳定收货单元 ID。源事实为待运输意图，未接 T15 前不标记已发送或运输创建成功。

账本 PostStock 增加仅模块内部使用的同事务 staging 入口：既有独立调用行为不变；通过原量化/版本/保护验证后，在同次 SaveChanges/Commit 内加入入库事实及单据 fence。没有先记账后写单据的窗口；重放不再次执行 staging。入库 writer 必须核对账维度与计划行、容器和收货位置一致，不能从任意库存 UUID 推断合法收货。当前续片仍不开放 HTTP；公开授权和页面随后接线。

验证门槛：真实 MySQL 2+3、2+4、永久原键/执行重放、并发竞争、保存后故障全回滚、跨租户及错误维度拒绝、迁移无丢表/旧账回归。纯领域恢复不是替代数据库唯一键或事务。

### 并发与回执

- 收货事务内先以 tenant/order 参数锁定父单并核对版本，再 stage 单据/事实；不能依赖随后 SaveChanges 的 CAS 才拦截事实序号唯一键竞争。不同 balance/容器争同一剩余计划也只有一笔成功。
- header/history 两次读取的数量及末版本必须一致；时点交错返回 VersionConflict，不把新历史套进旧 header。
- 若读完来源后另一请求已提交，ledger 返回 Replayed 时只读恢复原收货事实。不得返回假冲突或自动再写；缺失同事务来源事实显式抛不变量错误。
- 原收货/审批重放返回事实中的原 AfterVersion，即使后续单据已推进。创建重放当前仅为内部 primitive 的现存单据身份/当前版本，不作为已批准的公共永久创建回执合同。
- 原 operation 与 execution 均约束 tuple/actor/order；execution 重放可换 operation，不换执行语义或目标账维度。审批/收货事实共用操作键命名域，创建键在 header 独立约束；公开合同接线前明确其操作命名域。
- 保存守卫禁止改删历史、无事实推进 header、审批携带收货 payload，以及收货缺少同次精确 ledger posting。恢复核验事实身份与末版本。

### 当前边界与后续依赖

本片是内部事务 primitive，不注册公开写入口。尚未核验仓/位启用、真实容器及现场位置 schema；测试专用禁用仓/已有 balance 只验证原子事务，不代表允许运营收货。公开业务前必须补齐这些主体校验和当前字段/资源/会话权限，不能默认启用或拿任意 UUID 代替主数据。当前只接受已存在目标 balance；创建合法账维度仍待接线。

异址收货事实耐久保留唯一原 tuple，库存仍在收货位，状态 Executing、运输未关联；它不是已投递 Outbox 或已创建运输的证明。本片未扩展发布器、未实现最终到达的持久移账；这些与授权 HTTP、具名页面仍属 T09 主体待办，不能归为外部验收关闭。

## 已执行证据

### 领域首片结果

- 缺失 InboundOrder / InboundReceiptCommand / InboundPlanLine 的编译 RED 已实际执行。
- 实现后首轮 117 通过/1 失败：数量值对象按既有合同抛 ArgumentOutOfRangeException，测试误要求精确 ArgumentException；改为允许 ArgumentException 派生类型，未放宽业务数量规则。
- 最终 `FullyQualifiedName~Wes.Modules.Tests.Wms`：118 通过、0 失败/跳过，其中本片新增 6 项。覆盖既有数量、保护、余额和主数据领域测试。
- 架构 29 项通过；完整 Release 构建 0 警告/0 错误，新增 C# whitespace 检查通过。
- 无迁移/HTTP/公开写入口，未将纯领域测试当作持久并发、库存事务或完整 T09 的验收。

### 持久事务续片结果

- 缺失 ExecuteComposedAsync 的编译 RED 已实际执行；首轮 MySQL 7 通过/1 失败定位到父单 fence 前的事实唯一键冲突，改为事务内先锁父单后通过。
- 最终真实 MySQL `FullyQualifiedName~WmsLedgerTests`：120 通过、0 失败/跳过。覆盖 2+3/2+4、草稿不可收货、创建/审批/收货永久身份、后续状态推进后原回执、换 actor/执行 payload、跨租户/错容器、同账和不同账并发、异址真实收货/未到达、历史不可改、非法审批与保存后异常/取消全回滚。
- 确定性查询拦截在来源读取后、ledger 首查前提交另一原命令，验证 Replayed 的原 ID/AfterVersion、唯一 posting 和唯一收货效果，不依赖线程调度碰巧覆盖窗口。
- 较早 ledger/主数据联合回归 353 项通过；最终 ledger 120 项替代其中旧 ledger 结果，不重复合计覆盖。WMS 领域 118、架构 29 项重新通过。
- 完整 Release 构建 0 警告/0 错误；相关 C# whitespace 格式化通过；EF `has-pending-model-changes` 无漂移。
- EF 生成迁移 WmsInboundReceipts 已在批准的 wes_test 真库验证。Up 仅新增入库单/事实两表、tenant 复合外键/唯一索引，Down 仅删除本片两表；不自动迁移生产。
- 本片没有 HTTP/前端变更，未重跑 HTTP、浏览器、前端或 Route 套件；未验证 SQL Server/PostgreSQL、真实 Pack/Broker/设备。保留明确待办，不宣称 T09 整包完成。
