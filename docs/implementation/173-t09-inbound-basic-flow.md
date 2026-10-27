# T09 入库基本流程

- 状态：实施中；领域、内部持久收货事务及当前授权只读 HTTP/详情页面已验证，运营主数据依赖、公开写与最终运输持久接线未完成。
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

异址收货事实耐久保留唯一原 tuple，库存仍在收货位，状态 Executing、运输未关联；它不是已投递 Outbox 或已创建运输的证明。持久事务片未扩展发布器、未实现最终到达的持久移账；当前授权只读 HTTP/具名详情由下述续片补齐，公开写与运输仍属 T09 主体待办，不能归为外部验收关闭。

## 当前受权详情续片设计

按 PAGE-WMS-003 接入一个只读具名 `GET /api/wms/inbound/{id}`，复用现有默认关闭 WMS/Identity 部署门禁，不创建默认角色或写入口。公开 WMS owner 私有持有 DbContext；Host 仅通过 Platform 公开 owned-resource authority 核验 `wms.inbound.read`，真实 creator 来自 WMS header，客户端不能提供 creator/部门/权限。

完整详情字段固定为 Id/Code/WarehouseId/State/Version/CreatedAt/Lines/Receipts；需完整当前字段集合，大小写精确，不借用仓库、库存或流水权限。Lines 为最多100计划行及已收/待收数量；Receipts 为原单元、行/容器/收货位/目标位、精确数量和 RequiresTransport/Arrived/TransportId。公共状态为独立枚举，数量统一固定六位 decimal string；不返回租户/creator/actor/operation、JSON存储、账本内部实现或假运输状态。

先读取header定位真实creator并核权，再以独立 RepeatableRead 事务读取header与完整事实、恢复领域不变量并投影；释放读事务后再次核验当前完整字段权限，取消或撤权不返回先前投影。该详情返回单据完整事实而非跨页固定快照；后续大单历史分页独立扩展，不静默截断。缺失/跨租户返回404、无权403、空ID400；数据库错误按既有安全WMS异常记录无敏感细节，坏历史显式失败而非空成功。无库存、事实、审计或Outbox写效果。

验证门槛：真实 MySQL 精确2+3/剩余、异址待运输、真实creator/逐字段/租户隔离、两时点撤权/取消、快照一致、异常清洗；真实HTTPS/当前Platform角色和会话、严格响应shape/数量string、默认不装配及无写路由。此片不替代运营启停/容器及收货写授权。

具名只读客户端同时接通 `/wms/inbound` 与 `/:id`：Wms模块/独立read入口门禁、完整八字段客户端提示、已知UUID显式读取（无目录/无自动提交）。计划与收货卡片精确显示string数量，使用BigInt验证计划=已收+待收及收货逐行合计/状态一致，不用Number计算数量。严格拒绝额外字段/重复身份/错误目标/非法精度或假运输状态。换ID/会话/字段权限清旧并取消请求，迟到丢弃；离线/503只保留明确陈旧的同单详情，401/403/404/合同错误清空；重连不自动查询。只读无预览确认/写动作，不把查不到当空单或提供假创建入口。该UUID详情不是完整目录或写表单。

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

### 当前授权只读 HTTP/详情客户端续片结果

- 缺失 InboundSubject/IInboundAuthorization 的编译 RED 已实际执行，随后接通公开只读 owner、Host 当前 Platform authority 与具名页面，不开放内部 writer。
- 新增23项真实 MySQL 读取用例；最终账本/入库读联合143通过、0失败/跳过。逐字段及真实 creator、跨租户/无效上下文、读后撤权/取消、脏owner/已有事务拒绝、数据库异常清洗与损坏JSON显式失败均已验证。
- 确定性拦截在读事务header已读取后提交新收货：首个返回同一旧快照，下一显式读取返回新事实；不会把旧header拼接新history。读请求无新库存/事实/Outbox效果。
- 新增14项HTTP，覆盖精确公开shape/六位string/0.000001、默认不装配、无写路由、HTTPS/登录/当前会话、独立八字段、Own真实creator、实际creator部门迁移后拒绝、跨租户与伪造header不越权。
- 新增54前端用例（含入口/路由），全前端37文件/824通过，vue-tsc与Vite生产构建通过。包含超出JS整数精度的22位计划数、BigInt逐行核对、同址完成/异址待运输、非法响应清旧、迟到/离线/撤权及陈旧重试。
- 真实HTTPS Kestrel/MySQL/Chromium新增入库变体：实际导航、键盘焦点/Enter、精确DOM数量、375/768/1440宽度与375叠加200%文字无横溢、离线不自动刷新、独立Platform owner移除Lines字段后403清旧、无业务存储缓存。首轮17通过/1失败定位为测试整页goto丢失刻意驻内存的登录态，改为真实SPA导航后全部通过，未改认证政策。
- 最终Modules相关137通过，包含14 HTTP、118 WMS领域、5个真实WMS浏览器变体；与143 owner/824前端不混算独立需求覆盖。既有四个浏览器变体全部重跑。
- 最终架构29项通过；完整Release构建0警告/0错误，相关C# whitespace与git diff检查通过，EF模型无漂移。
- 当前页是已知UUID只读详情，不是完整目录或草稿/审核/收货表单；未重跑无关Route/附件专用浏览器/全部Platform套件，未验证其他Provider或真实Pack/Broker/设备。无新迁移，不宣称T09整包完成。
