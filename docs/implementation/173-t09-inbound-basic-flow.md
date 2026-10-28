# T09 入库基本流程

- 状态：实施中；领域、内部持久收货事务、当前受权目录/详情和草稿创建/修订 owner/HTTP/多行表单已验证。运营主数据依赖、来源深化、公开审核/收货与最终运输持久接线未完成；草稿保存不等于完整单据生命周期。
- 需求：FUNC-WMS-002 / PAGE-WMS-003；DTC-WMS-002/003，VAL-01。
- 基线：[WMS 规格](../modules/wms-and-ledger.md)、[规则](../detailed-design/05-approved-rules-and-defaults.md)、[交付顺序](TodoList.md)。

依赖进展：[仓库逻辑启停](174-t08-warehouse-operational-state.md)owner/默认关闭PUT与页面已验证，保留原创建事实/永久状态回执，并核禁用活动及当前权限。[库位逻辑启停续片](175-t08-location-operational-state.md)增加父仓→库位锁、真实双creator/完整字段、启用时持久Pack/schema校验及禁用活动保护，具体执行证据以该文档为准。合法容器/账维度与物料单位、当前受权审核收货及最终运输仍是主体待办，内部收货原语仍不得以Enabled绕过这些校验；没有耐久到达事实前异址receipt继续保护源/目标，不凭领域状态释放。

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

## 当前受权目录续片设计

复用 PAGE-WMS-003 单号/仓库/状态筛选，增加 `GET /api/wms/inbound` 与详情页内实时目录。offset 0..10000、limit 25/50/100，code 精确规范大写、warehouse 非空UUID、state 仅公开四态；按 Code/Id 稳定升序，无total或跨页快照声明。列表只返回 Id/Code/WarehouseId/State/Version/CreatedAt，不暴露计划、收货/创建人或隐藏总量。

需当前 `wms.inbound.read` 完整八字段才具备选择后读详情能力。Platform 公开 owned-resource creator 查询先逐字段求可见creator交集，WMS以真实header.CreatorId在SQL分页前过滤；MySQL Guid集合采用既有typed表达式模式，不在内存分页或从request接受creator。查询后再核当前creator集合，任何已取行越出当前集合则整页403不返回旧行。空授权集合为成功空目录，不等于缺权403。无新数据库表/写入口。

前端独立目录组件复用 QueryState/RealtimePager，显式查询、过滤/会话变化清旧、迟到取消、陈旧/离线不可选择、403双侧清旧。选择目录行只发出ID，父页重新GET受权详情；目录不是库存/运营写许可。验证分页前范围交集、精确筛选、撤权/取消/数据库错误、严格页面shape与排序、实际浏览器目录选择→详情重读。

目录续片用例追踪（PAGE-WMS-003 / DTC-WMS-002/003 读取适用子集，不关闭父用例）：

| 用例 | 输入与步骤 | 必须结果 / 自动化定位 |
|---|---|---|
| TC-T09-CAT-01 | 26个隐藏creator的单号排在可见单据前，查询默认25条 | 授权在SQL分页前，可见单据仍出现；`InboundCatalogFiltersRealCreatorBeforePagingAndCanSelectTheCurrentDetail` |
| TC-T09-CAT-02 | 31条可见行，读取offset0/25并精确筛选单号/仓库/状态 | 25+6、不重复、稳定排序；`InboundCatalogPagesInStableCodeOrderAndUsesExactFilters` |
| TC-T09-CAT-03 | 任一完整字段缺失，或物化后撤权/creator集合收缩 | 403且无旧行；`InboundCatalogRequiresCurrentCompleteProjection` / `InboundCatalogCurrentCreatorScopeShrinkDiscardsPreviouslyVisibleRows`，HTTP逐字段及真实部门迁移 |
| TC-T09-CAT-04 | 取消、数据库故障、非法分页/响应、离线或迟到 | 不泄漏敏感错误/旧范围，不将失败当成功空目录；真实owner与`InboundCatalog.spec.ts` |
| TC-T09-CAT-05 | 真实父页目录选择、详情403、目录403；HTTPS浏览器键盘选择 | 选择后重新GET、双侧清旧；`InboundCatalog.spec.ts`实际父子组合和既有五变体WMS浏览器 |

本片复用现有默认关闭开关和部署入口，无依赖、模型、迁移或写合同变化；回退到前版应用仅移除目录读取能力，不需回滚库存/事实数据。真实Pack/Broker、SQL Server/PostgreSQL及现场验收仍按TodoList后置。

## 当前受权草稿创建续片设计

按已批准FUNC-WMS-002/PAGE-WMS-003先接通草稿创建，不与审核/收货合并。永久创建回执只返回原Id及Created/Replayed，不返回可变Version；需当前详情权限另读最新版本。原operation绑定可信actor、规范单号、仓库和有序完整计划（稳定行Id/item/unit/batch/precision/精确数量），不同tuple冲突，correlation不改变业务语义。当前内部primitive返回版本的行为保持兼容，但不透传为公共永久创建版本。

写owner需当前 `wms.inbound.write` 完整 Code/WarehouseId/Lines 字段，按新creator=可信actor求资源范围；父仓从WMS取真实creator，需当前 `wms.warehouses.read` 完整 Id/Code/Name/Enabled/Version/CreatedAt 字段。不存在/跨租户不创建；草稿允许禁用仓（无库存/容器/位置占用/设备/运输效果），审核/收货仍必须另外建立运营校验。输入1..100有序唯一行、合法非空身份、ASCII单号、批次与单位0..6精度、正decimal(28,6) string，不接受舍入、科学计数或隐式默认单位。

新公共写owner独立于只读scope；在原创建primitive的同次事务保存后、提交前再次复验写和父仓权限。撤权或取消回滚，故障显式传播；原operation恢复同样核当前权，不允许凭旧键绕权。同键真实并发由唯一约束恢复胜者；不存在原键的唯一冲突为Conflict，不冒成功。原创建header保留不可变计划和创建身份，无新表/迁移，库存/事实/Outbox不得增加。数据库异常复用安全清洗及日志，拒绝结果不泄漏Id。

先运行缺失创建合同的编译RED；真实MySQL验证创建→内部审核推进→原键稳定Id、改actor/计划冲突、逐字段及真实父仓、两时点撤权/取消/保存后故障、同键并发、租户隔离与无库存副作用。HTTP与具名多行表单随后接线；未接通前不宣称完整创建交付。

HTTP续接：`POST /api/wms/inbound`受独立默认false的 `Wes:WmsMasterData:InboundDraftCreationEnabled` 控制，要求WMS/Identity读装配。与仓库/库位创建开关互不隐式开启，不装配审核/收货。沿用HTTPS、当前认证、no-store、唯一D格式非空Idempotency-Key及X-WES-Client=ops-web/pda。严格body只含Code/WarehouseId/Lines，行只含Id/ItemId/UnitId/Batch/Precision/Planned（数量string）；未知成员、null行/超100/非法身份拒绝。201+Location或200原回执仅decision/id；拒绝400/403/404/409，安全数据库503/500。无UI入口前不改只读页，后续多行表单单独接线。

客户端续接：在既有入库页增加具名多行草稿面板，不注册另一套CRUD路由。入口需当前完整写字段及父仓完整读取字段；选择已知仓UUID不是启用许可或完整仓候选链。显式1..100行，稳定行UUID、物料/单位UUID、批次、显式精度、精确数量string；编辑→全量预览冻结tuple/operation→二次确认，绝不预览即POST。取消/超时/离线/503/坏成功响应均Unknown，10秒计时、仅在线手工同键同body恢复；401/403/404/405/409是本次明确拒绝，先前Unknown事实仍保留提示。不写本地存储、不排队/自动重试。换主体/当前字段清旧并取消，迟到丢弃；返回Id只是创建确认，手动重读详情才取得当前版本，不自动审核或收货。面板独立于详情QueryState，不因离线/详情刷新卸载丢失原tuple。尝试创建使父详情/目录失效，原Id重读复用现有详情入口。

草稿续片用例追踪（DTC-WMS-002/003创建适用子集）：

| 用例 | 操作/故障 | 必须结果 / 自动化定位 |
|---|---|---|
| TC-T09-DRAFT-01 | 创建后内部审核推进，再以原operation/body恢复 | 永久只返回原Id，无原/当前Version混淆；`InboundDraftPublicReceiptRetainsOnlyOriginalIdAfterApproval`、`WmsInboundDraftCreatesAndRecoversOnlyStableIdAfterInternalApproval` |
| TC-T09-DRAFT-02 | 逐写/父仓字段缺失、真实父仓creator非当前actor、撤权后原键恢复 | 当前403且无Id；同键不能绕权；`InboundDraftRequiresEveryExactWriteAndParentReadField`、`WmsInboundDraftRequiresRealWarehouseCreatorRatherThanRequestActor`及当前撤权HTTP变体 |
| TC-T09-DRAFT-03 | 保存后撤权/取消/抛异常、同operation双并发、同码不同键 | 拒绝/取消全回滚；同键仅一次创建，异tuple409；`InboundDraftReauthorizationAfterSaveRollsBackOnRevocationOrCancellation` / `InboundDraftSameOperationConcurrentRequestsHaveOneCreationAndStableId` |
| TC-T09-DRAFT-04 | 非法分页外的创建body、未知成员、缺显式precision、数量数字/指数/超精度/溢出及实际外租户父仓 | 400/404、无新草稿/库存；owner严格计划测试及HTTP严格body/伪造租户测试 |
| TC-T09-DRAFT-05 | 多行预览、二次确认、503/超时/离线/401、换主体及迟到响应 | 原tuple冻结、只手工同键恢复、当前主体清旧；`InboundDraftCreation.spec.ts`含实际父子组合 |
| TC-T09-DRAFT-06 | HTTPS Chromium双行5与0.000001、键盘确认、显式GET读回 | 201和Id-only回执、草稿精确两行且无新增posting/fact，375/768/1440及200%文字无横溢、无业务存储缓存；既有五WMS浏览器变体 |

数量输入按语义规范成固定六位再存有序计划，`5`/`5.000`或规范单号大小写恢复同一请求，不因JSON decimal scale不同制造假冲突；不改变行顺序、单位、批次或数量。新增草稿不需要库位/容器/Pack，因为不运营执行；当前物料/单位仍为明确身份与精度计划，不宣称已完成完整主数据生命周期，审核/收货接线必须另核合法当前主数据。

## 当前受权草稿修订续片设计

按FUNC-WMS-002/PAGE-WMS-003的草稿保存补齐行计划修订。已创建单号/仓库/creator及原创建Plan永不覆盖；需要换单号/仓库时另建业务单，不能以修订绕过原创建operation。仅Draft允许全量替换1..100有序计划行；稳定行Id可保留或显式新增/删除，原历史保留。revision事实包含原期望版本与完整规范计划、可信actor/operation/correlation、原AfterVersion；header仅推进Sequence/Version且状态仍Draft。恢复先读原计划，再按序应用revision与审核/收货事实。审核不再假定sequence=1，但仍必须来自Draft；修订不能跨审核、库存或运输边界。

复用创建的严格计划数量校验/规范化，创建原operation的body比较仍使用不可变原Plan，不随修订变化。修订原operation须匹配order/actor/ExpectedVersion/完整规范计划，重放即使后续已审核仍返回原AfterVersion（但复验当前完整权限）；不同tuple冲突。新修订用父单FOR UPDATE/CAS串行化，与审核/收货共用header版本，过期版本不写入；fact/header同次保存、提交前再次复验，撤权/取消/故障回滚。当前WMS从header真实CreatorId取独立`wms.inbound.write`完整Code/WarehouseId/Lines，当前read需完整八字段；不以actor替代creator、不让只读用户写。

公开owner独立于创建/只读scope；HTTP为独立默认关闭`Wes:WmsMasterData:InboundDraftRevisionEnabled`控制的 `PUT /api/wms/inbound/{id}/draft`。开关依赖WMS/Identity读装配，与创建互不隐式开启；原key/client/HTTPS认证沿用创建协议，body严格为ExpectedVersion/Lines，必填/未知成员拒绝。成功200回原decision/id/version；非法400、无权403、缺单404、过期或非草稿/换tuple409。无模型/迁移变化，新增kind仍存varchar(16)，旧读应用不支持新kind，因此开始修订后回退应用须停用revision并保留支持恢复新kind的版本，不能宣称任意旧版可安全读取。

客户端从新鲜Draft详情显式准备修订，只改计划行，冻结目标Id/原版本与全部原tuple；预览→二次确认，10秒Unknown及在线手工同key/body恢复，导航/换主体清旧取消，不自动写/排队或缓存。修订后显式重读当前详情，不将原修订AfterVersion当最新版本；面板不受详情刷新/离线卸载影响。真实MySQL验证多次修订后审核/收货使用新计划、旧计划与回执不变、并发修订/审核、保存后故障与当前撤权；HTTP/实际父子组合与真实浏览器验证具名路径。

| Case ID | 输入/步骤 | 预期及自动化映射 |
|---|---|---|
| TC-T09-REV-01 | 原计划5→7→9，随后审核/实收9；恢复第一修订/创建原键 | 原Plan与原修订AfterVersion不变，当前恢复9并仅一次收货；`InboundRevisionHistoryDrivesReceiptAndDoesNotChangeOriginalCreationIdentity` |
| TC-T09-REV-02 | 逐写三字段/read八字段缺失、真实creator非actor、后续角色/成员撤销 | 当前403/无回执；不以actor替代creator或借用仓库权；owner逐字段和`WmsInboundRevisionRechecksEveryCurrentFieldAndMembershipOnReplay` |
| TC-T09-REV-03 | 保存后撤权/取消/抛异常；同版本双修订/审核竞争；同原键相同/不同body竞争 | header/fact原子回滚；仅一笔新事实；同tuple恢复原回执、换tuple冲突；`InboundRevisionSavedThenFailureOrCancellationRollsBack`及三个并发测试 |
| TC-T09-REV-04 | 过期/非草稿、全量增删/顺序、最大/最小精确量、持久事实错误原版本/状态/覆盖Plan | 409或保存守卫拒绝，历史与库存不变；严格输入/跨租户/故障清洗与`InboundRevisionPersistenceGuardRejectsWrongFenceStateOrOverwrittenCreationPlan` |
| TC-T09-REV-05 | 新鲜详情准备修订、冻结原id/version、503手工原键恢复、实际父页清详情、换所选单据 | 不自动PUT/排队/缓存；Unknown面板保持挂载、迟到不落入新单；`InboundDraftRevision.spec.ts`和既有共享表单超时/离线/身份失效测试 |
| TC-T09-REV-06 | HTTPS Chromium双行创建后显式修订第一行5→7、键盘确认和GET读回 | PUT200三字段原回执；两行精确7/0.000001、原Plan仍5、仅一新revision且无新posting；375/768/1440及200%文字无横溢、无业务存储缓存 |

## 已执行证据

### 当前受权草稿修订续片结果

- 缺失`InboundWriter.ReviseAsync`编译RED已实际执行。原创建Plan保持不可变，新增Revised事实恢复完整修订计划；审核与修订同用父单锁/版本，审核仍只能由Draft开始，不再假定是第一条事实。
- 新增39项真实MySQL owner；最终完整`WmsLedgerTests`252通过、0失败/跳过。多次修订后审核实收9而非原计划5、永久原修订版本、创建原tuple恢复、真实creator/全部当前字段、两时点核权、保存后故障/取消回滚、并发修订/审核、同原键相同/不同tuple、最大/最小精确量/全量增删/顺序、跨租户/错误上下文、安全数据库故障及持久守卫均有证据。
- 新增34项HTTP与8项配置；定向71通过（34修订HTTP与37完整配置），最终相关Modules230通过（91入库HTTP、134WMS领域与配置、5真实HTTPS浏览器）。独立默认关闭PUT与创建互不隐式开启；严格ExpectedVersion/Lines和逐行必填字段、真实creator的完整写/read、原操作当前撤权、HTTPS/会话/client/key、过期/非草稿及安全拒绝均已核验。
- 新增25项前端；完整40文件/922通过，vue-tsc与Vite生产构建通过。复用具名草稿多行组件和既有Unknown/超时/离线状态机，实际父页attempted清旧详情不会卸载修订；新单选择取消旧修订，原id/version/key/body冻结，额外字段/错误单据/无效或未推进版本的成功响应不被确认，成功后另读当前详情。
- 真实HTTPS Kestrel/MySQL/Chromium在原入库变体中新增创建→显式修订第一行5到7→另读完整两行7/0.000001、键盘确认、375/768/1440及200%文字无横溢、无业务存储缓存。持久验证原Plan仍为5，仅一条Revised且没有新增posting；另外原入库执行单的审核/收货事实不变。最终客户端追加更严版本校验后重建，并再次以Release执行全部五浏览器变体，5通过；与上述230重叠，不重复累计。
- 完整Release构建0警告/0错误，架构29通过、相关C# whitespace verify通过。模型无漂移、无新迁移。EF检查首选Host启动项目因未引用Design包拒绝；改用仓库已有WMS design-time factory/启动项目后通过，未添加依赖或更改Host配置。
- 以上252/230/922替代上一片213/188/897的相关回归结果而不重复累计。未运行完整全仓库测试、Route全套、SQL Server/PostgreSQL或真实Pack/Broker/设备验证；本片无运输或物理控制变更。来源深化、运营主数据生命周期、公开审核/收货及最终运输持久移账仍为主体待办，T09未关闭。

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

### 当前授权只读目录续片结果

- 缺失 InboundCreatorAccess 的编译 RED 已实际执行。实现完整八字段creator交集、WMS真实creator的SQL分页前过滤、读取后二次核权、稳定顺序及精确筛选；无隐藏total/计划/收货或私有创建人投影。
- 新增24项真实MySQL owner、10项HTTP、32项前端；最终账本/入库读联合167、相关Modules147（24 HTTP、118 WMS领域、5个真实HTTPS Chromium变体）、全前端38文件/856项通过，均0失败/跳过。历史143/137/824被此轮相关回归替代，不重复累计。
- 覆盖隐藏行排在可见行前的分页反例、25+6页、逐字段缺权、允许空creator集合、物化后撤权/仍有权限但creator范围收缩、取消及数据库异常清洗。HTTP验证当前Own/真实部门范围和完整字段，列表选择不跳过详情授权。
- 首轮测试定位测试方法误嵌入fake及父页选择函数误嵌入catch作用域，分别修正作用域并保持原断言。实际父子组合已验证选择重新GET及两侧403清旧；不是只验证子组件emit。
- 五个WMS真实浏览器变体全部重跑通过；入库变体实际查询目录并以键盘选择重读详情，再完成精确数量、响应式/200%文字、离线与当前字段撤权验证。无整页重载或认证持久策略变更。
- vue-tsc/Vite严格生产构建、架构29项、完整Release构建0警告/0错误、相关C# whitespace和git diff检查通过。无持久模型变更，因此此片不新增迁移或再次应用数据库迁移。
- 本片替代上一片“无目录”边界；仍无公开草稿/审核/收货写表单、运营主数据启停/容器、运输投递和最终到达持久移账。不关闭T09；未重跑无关Route、附件专用浏览器或全部Platform套件，未把外部验证记为通过。

### 当前受权草稿创建 owner/HTTP/客户端续片结果

- 缺失 InboundDraftCreation/IInboundDraftAuthorization/WmsInboundDraftWriter 的编译RED已实际执行。接通独立写owner、真实当前Platform权限adapter、默认关闭POST与具名多行表单；永久回执只有decision/id，无易变Version。内部原创建调用仍可取当前版本，但不作为公开永久版本。
- 新增46项真实MySQL owner、33项HTTP、8项部署配置、41项前端用例。最终账本/入库owner213、相关Modules188（57入库HTTP、126 WMS领域/配置、5真实HTTPS浏览器）、全前端39文件/897通过，均0失败/跳过。历史167/147/856由本轮相关结果替代，不重复累计；46个新增owner不包含原有草稿不可收货用例。
- MySQL覆盖审核推进后原Id、改actor/完整tuple冲突、逐字段及真实父仓provenance、保存后二次核权/撤权/取消/抛异常回滚、同operation双并发、22位整数/六位精度与规范数量重放。草稿不新增库存、posting、receipt fact或Outbox，不启用父仓；真实单据创建身份保留在不可变header。
- HTTP验证严格必填/未知root及行成员、缺显式precision拒绝而非默认0、JSON数字/指数/超精度拒绝、精确Idempotency-Key/X-WES-Client、HTTPS/当前会话/逐字段、真实父仓creator及伪造租户无法使用外租户父仓；201+Location、200原Id和409冲突均验证。
- 首轮HTTP59通过/1失败是fixture默认已附X-WES-Client，补为真正移除默认header后60通过；随后加入缺precision/实际外租户两例，相关HTTP/配置62通过。前端首轮120通过/2失败分别为401会立即清身份而失效、GET省略method的测试预期不符；保留既有认证/请求策略，改为核验实际失效和新GET结果，最终897通过。
- 五WMS真实HTTPS Chromium变体重跑通过。入库变体先在独立新页登录、双行5/0.000001、键盘预览确认201、原Id显式GET读回；再执行原目录/精确收货/离线/当前撤权链路。预览0写、确认仅1POST+1手工GET；持久核两行Draft且无新增posting/fact。375/768/1440及375叠加200%文字无横溢、无业务存储缓存。
- 首轮扩大Modules291项为290通过/1浏览器失败：创建步骤误放在场景已撤销父仓read之后，服务端正确403。改为撤权前的独立页真实创建，不恢复权限/放宽断言；修正多单后用原Id定位既有执行单。最终相关188及独立五浏览器通过；291中其余仓/位创建103项已通过，重叠不重复累计。另修正新增浏览器局部变量遮蔽的编译错误。
- 最终架构29、完整Release构建0警告/0错误、相关C# whitespace及git diff检查通过，vue-tsc/Vite严格生产构建通过。无模型/迁移/依赖变化；不重复执行迁移。关闭新开关保留草稿和operation，仅移除POST，已有GET不受影响。
- 仅交付创建，不包括既有草稿修订/来源类型深化、受权审核/收货、运营仓/位启停与合法容器/物料单位主数据生命周期，亦不包括运输投递/最终到达持久移账。无关Route/附件专用浏览器/全部Platform未重跑；SQL Server/PostgreSQL、真实Pack/Broker/PDA/设备仍无本片验收，不关闭T09或项目。
