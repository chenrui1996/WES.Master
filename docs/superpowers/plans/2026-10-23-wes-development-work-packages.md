# WES 逐工作包开发 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans after implementation approval. Steps use checkbox (`- [ ]`) syntax for tracking. Follow the dependency table; do not implement all tasks at once.

**Goal:** 将66个已设计FUNC拆成可审核、可测试的纵向交付，并为64个PAGE接入真实owner结果。

**Architecture:** 模块化单体/owner契约协作；纯算法、业务事务、真实设备、客户端投影分别验证。契约测试替身只在测试装配使用。

**Tech Stack:** .NET 10/EF Core、Vue 3/TypeScript/Element Plus、UniApp；测试工具遵循[总计划](2026-10-23-wes-development-and-testing.md)与[测试执行计划](2026-10-23-wes-test-execution.md)。

**Spec:** [模块/功能开关设计](../../detailed-design/README.md)、[规则基线](../../detailed-design/05-approved-rules-and-defaults.md)、[架构](../../02-system-architecture.md)、[治理](../../03-engineering-governance.md)。

## Global Constraints

- 本文状态实施中；按批准基线逐片创建必要源文件/依赖/测试。2026-10-23用户追加授权每个工作包完成且验证后提交推送到既有GitHub；不得将内部切片冒称整包完成，不force push。复选框只在整条步骤证据齐全后勾选，部分结果见[实施记录](../../implementation/readiness-register.md)。
- 按[ADR-0002](../../adr/ADR-0002-mysql-first-implementation.md)，首期所有三Provider步骤先落实真实MySQL，SQL Server/PostgreSQL延后。未确认隔离环境仍blocked，不能InMemory替代。
- 逐任务硬依赖/角色/里程碑以[总计划§3](2026-10-23-wes-development-and-testing.md)为准；任务内的接口是语义合同，不是新增领域规则。
- 路径均以仓库根为起点；花括号清单表示分别创建的具名文件，不是自动生成模板。文件只有承担实际职责才创建，允许按代码审查合并简单文件，不允许合并owner。
- T01的软件契约/逐REQ批准已落文档后，可开始无外部环境依赖的T02切片；具名现场责任、真实DB及终端输入仍是相应切片硬门禁，M0/T01整体未完成。合同规划路径不冒称现有可调用API。
- 每个复选步骤按其中列出的具体场景拆成2..5分钟的红灯/实现/复测动作；工作包本身不是2..5分钟任务。可独立拒绝的切片单独PR，每片都跑测试，不做“大包写完才测”。
- 每片运行[测试计划§2](2026-10-23-wes-test-execution.md)的指定CaseId/Provider/Adapter选择器，留红/绿报告；完成时更新本任务关联的现有设计、[总追踪](../../04-requirement-design-test-traceability.md)及验收记录。
- 每个工作包结束必须提供相互独立的实施文档和测试文档：`docs/implementation/<task>-implementation.md`记录变更/决策/部署回退及未完边界，`docs/implementation/<task>-tests.md`记录CaseId、命令/环境、实际结果、失败与未覆盖项；测试未运行时明确标记not-run/blocked，不以实施说明代替测试证据。

---

## A. 工程与平台

### T01：实施就绪与契约冻结

**Files:** 实施获批后修改现有[阶段约束](../../../AGENTS.md)、[业务范围](../../01-business-scope-and-status.md)、[追踪](../../04-requirement-design-test-traceability.md)、[验收](../../acceptance/01-cross-module-and-provider-matrix.md)；新增`docs\contracts\01-business-and-execution-contracts.md`、`docs\implementation\readiness-register.md`，不创建业务工程。

**Interfaces:** 消费所有设计包及RULE-01..29；产出逐REQ签核、主/备owner、精确契约版本/DTO字段/错误code/API兼容策略、跨owner事务说明及H证据责任表。字段至少包括tenant/warehouse/Pack、operation/idempotency、expectedVersion、correlation/causation、业务ID、策略/部署/map、command/epoch/grant和反馈generation/sequence；不强迫普通平台请求携带无关设备字段。

- [ ] 核对原265 TC与当前168父DTC，逐条REQ-ROUTE-001..027签核；BoxSort保持预留，潜在冻结/拆合托/补货不误列已实现；明确Seven迁移物料/单据/跟踪/角色的保留范围。
- [ ] 写入身份/审计、WMS读写/transport intent/completion、Leg与handover、Pack capability/command/query、设备语义反馈、owner prepare/manifest、只读投影与issue/alarm合同；每条列成功/拒绝/Unknown、幂等/版本/取消/超时/数据owner。
- [ ] 做合同反例评审：跨租户、缺map物理映射、换摘要复用key、取消与完成并发、跨Pack双owner；存在歧义即退回对应设计，不用实现猜测；绑定Provider精确版本/驱动、支持终端、H主备与证据取得阶段。

### T02：可信最小工程与测试入口

**Files:** 拟创建`WES.slnx`、`global.json`、`Directory.Build.props`；`src\Host\{Wes.Host.csproj,Program.cs}`；`tests\Modules\{Wes.Modules.Tests.csproj,Fixtures\ProviderFixture.cs,Fixtures\WesApiFixture.cs}`、`tests\Architecture\{Wes.Architecture.Tests.csproj,ModuleBoundaryTests.cs}`；后续Contracts/EndToEnd/Performance工程在首次相关任务中加入。具体生产模块项目只在形成真实边界时添加。

**Interfaces:** 消费T01契约/版本清单；产出可启动但默认禁设备写的Host、可注入TimeProvider、真实三Provider独立fixture、CaseId/Provider测试trait及架构约束。不将所有module DbContext集中进通用repository。

- [ ] 写故意跨owner Infrastructure/DbContext引用、Host夹业务规则、Builder注册应被检测的负夹具；正常最小Host健康与必需DB断开readiness反例；建立测试数据库租户隔离和清理自检。
- [ ] 按[功能开关设计](../../detailed-design/feature-switches.md)建立部署级强类型Features配置、启动时未知键/缺项/错型/依赖校验与不可变快照；运行能力按owner显式注册，Disabled不使用成功No-op。
- [ ] 建.NET10/nullable/格式和分析器、最小装配/错误映射/日志关联/secret脱敏、禁写默认配置；锁定测试SDK/EF/三Provider包版本，沿用xUnit可兼容版本而非盲用skill样例；为首次需要的依赖改manifest后再restore。
- [ ] 运行架构及三Provider连接/迁移/事务小测试，故障真实可注入且测试凭据不进仓库；当前无包可用则报告blocked不切InMemory。覆盖DTC-SYS-001/006/008与FUNC-SYS-001/004/005基础；最终健康/架构仍随每包重跑。

### T03：身份、会话与登录预算

**Files:** `src\Modules\Platform\Features\Identity\{Login.cs,RefreshSession.cs,RevokeSession.cs,ChangePassword.cs}`、`Domain\Session.cs`、`Infrastructure\SessionPersistence.cs`；`clients\ops-web\src\features\identity\{LoginPage.vue,SessionPage.vue,identity-api.ts}`；`tests\Modules\Platform\IdentityTests.cs`、`tests\EndToEnd\specs\platform.spec.ts`。

**Interfaces:** 消费T01身份/安全契约和T02受控时钟；产出FUNC-PLAT-001、PAGE-PLAT-001/002的当前身份/会话/撤销查询，不把菜单当授权。具体token/hash沿用Seven并验证.NET10兼容。

- [ ] 先写DTC-PLAT-001..003/010：两并发refresh最多一后继、重放撤关联会话、Access899/900秒、Refresh绝对8小时、Web/PDA空闲900/300秒；滚动账号第3失败后第4次需验证码、第5失败锁900秒，IP30/31边界。
- [ ] 实现凭据/验证码一次消费/限流预算、当前撤销校验、通用错误与安全站内跳转；新设15..128 Unicode标量/最近5个及泄漏值拒绝，旧兼容验证器账户改密前禁生产写。
- [ ] 真实DB并发+页面测试全部边界/无秘密输出；改密/禁用/单会话登出分别核撤销范围，长任务锁屏不抹已发命令事实。采用VAL-04，禁止只做happy path登录。

### T04：权限、租户、配置和审计

**Files:** `src\Modules\Platform\Features\{Authorization\RolePolicy.cs,Tenancy\TenantContext.cs,Configuration\VersionedSettings.cs,Audit\QueryAudit.cs}`、对应owner EF配置/迁移；`clients\ops-web\src\features\platform\{UsersPage.vue,RolesPage.vue,TenantsPage.vue,SettingsPage.vue,AuditPage.vue}`；`tests\Modules\Platform\{AuthorizationTests.cs,SettingsAuditTests.cs}`。

**Interfaces:** 消费当前身份；产出FUNC-PLAT-002..004/006、PAGE-PLAT-003..006/008，当前权限/数据范围/字段投影、B/P/H版本化策略查询与不可改审计。普通只读授权缓存30秒，高后果/快照每次复验；无TenantId=0共享。

- [ ] DTC-PLAT-004..006/008..009/011..014先红：空范围放大、跨租户同码缓存、无租户后台主体、旧版本覆盖、secret配置、热更新半状态、引用字典被删、审计越权修改。
- [ ] 做具名RBAC/组织/租户切换、字典/语言回退、强类型业务配置及来源/版本展示；部署Features与租户/数据库业务配置分离，非法P配置拒绝不回退，H缺项保持Unverified；业务审计与所属事务可靠关联。
- [ ] 三Provider的复合唯一键/幂等清理90天后领域键仍防重；页面关联筛选/撤权/敏感字段列不可恢复，验证PAR-01/02/09及VAL-04/08。审计界面不可修改历史。

### T05：owner事务Outbox/Inbox与可靠投递

**Files:** `src\Modules\Notifications\Features\Messaging\{PublishOutbox.cs,ReplayMessage.cs,QueryDelivery.cs}`；每写入owner的`Infrastructure\Messaging\{OutboxStore.cs,InboxStore.cs}`和本模块迁移；`clients\ops-web\src\features\notifications\MessagesPage.vue`；`tests\Modules\Notifications\MessagingTests.cs`、`tests\Contracts\MessagingContractTests.cs`。

**Interfaces:** 消费稳定事件envelope和授权；产出FUNC-NTF-001/PAGE-NTF-003、模块本地事务适配器及message查询/重放。Notifications只管投递诊断，不持有各owner业务事务或通用跨库Outbox。

- [ ] DTC-NTF-001/002：业务提交前故障→业务/Outbox均0；提交后Broker断开记录保留；消费提交后ACK丢失重投效果1；物理Unknown0自动重发。
- [x] Platform首片实现发布竞争/Inbox幂等、错误分类、1/5/30/120/600秒相邻等待最多5次、总15分钟deadline、永久隔离及受权同payload重放与稳定事件身份；见[Outbox/Inbox](../../implementation/35-t05-platform-outbox.md)、[Inbox](../../implementation/37-t05-platform-inbox.md)、[消息运维](../../implementation/39-t05-platform-message-operations.md)。这不代表Notifications聚合、其他owner适配或真实Broker闭环完成。
- [x] Platform Inbox 90天owner-local有界保留清理及保存失败恢复，许可绑定原owner事务、成功后失效；见[实施](../../implementation/49-t05-inbox-retention-save-recovery.md)及[测试](../../implementation/50-t05-inbox-retention-save-recovery-tests.md)。托管清理Job与真实Broker仍未实现。
- [ ] 三Provider/隔离Broker执行崩溃窗口，断线不得丢Outbox；过deadline控制拒发、迟到可信完成核验且不覆盖新位置、认证过期隔离/query；采用RVT-05、VAL-07/08，不声称消息exactly-once。

### T06：共享交互与授权查询底座

**Files:** `clients\ops-web\src\components\{QueryState.vue,ColumnSettings.vue,RelatedEntitySelect.vue}`、`src\composables\{useScopedView.ts,useLatestRelatedQuery.ts}`；Simulator相同纯UI可提取到`clients\shared-ui\`但须先证明真实复用；`src\Modules\Platform\Contracts\QueryScope.cs`及各owner的`Features\Queries\QuerySnapshot.cs`；`tests\EndToEnd\specs\shared-ui.spec.ts`、组件同目录`*.spec.ts`。

**Interfaces:** 消费当前scope/字段权限；产出loading/空态/error/Stale状态、列偏好/关联候选/父子指纹、实时与固定水位快照协议。快照数据属于查询owner，不集中备份所有域数据到Platform。

- [ ] DTC-UI-001..028/032先用真实具名最小测试页加受控API验证：列必显/顺序/宽度/键盘替代、损坏storage显式提示、本机主体租户环境隔离、父切换旧回包/主从行ID竞态、无导出页不增导出。
- [x] 按[功能开关设计](../../detailed-design/feature-switches.md)接入匿名白名单模块UI catalog；加载导航/路由前获取，关闭模块隐藏入口、直接导航有明确不可用态；catalog失败显式报错，不默认全开/空配置。API仍独立授权。已实施，见[客户端记录](../../implementation/41-t06-query-state-and-module-catalog.md)及[测试证据](../../implementation/42-t06-query-state-and-module-catalog-tests.md)。
- [x] 现有审计、用户、角色、租户、配置、字典与会话接入共享实时分页，25条默认与25/50/100选项、查询指纹/失败/空后续页边界及实时offset限制已验证，见[实施](../../implementation/47-t06-shared-realtime-pager.md)和[测试](../../implementation/48-t06-shared-realtime-pager-tests.md)。这不关闭以下固定快照与完整DTC门禁。
- [x] Platform审计 owner 首个固定成员快照切片已落地：RepeatableRead冻结有序审计ID、10分钟TTL、当前授权复验及真实MySQL/HTTPS/Chromium验证；见[实施](../../implementation/51-t06-platform-audit-snapshot.md)和[测试](../../implementation/52-t06-platform-audit-snapshot-tests.md)。该切片不涵盖其他owner、可变数据值快照或T06整体。
- [x] DTC-UI-001 在Platform Audit页的慢请求首片已验证：200ms不显示占位，300ms后显示授权列形状的骨架；同scope刷新保留原证据并显示上次成功UTC读取时间。见[实施](../../implementation/53-t06-audit-slow-query-state.md)及[测试](../../implementation/54-t06-audit-slow-query-state-tests.md)。其余PAGE适用性与DTC-UI-001整体仍待逐项验收。
- [x] DTC-UI-002 在Platform Users页首片已验证：区分首次无用户、筛选未提交、已提交条件无匹配和查询失败；见[实施](../../implementation/55-t06-users-empty-states.md)及[测试](../../implementation/56-t06-users-empty-states-tests.md)。用户目录不适用队列zero-work；其他PAGE/队列适用性仍待验收。
- [ ] 实现语义HTML/Composition API/strict TS与Element Plus已有组件复用；白名单偏好不存业务数据/secret/待写。核对/已有导出固定有序ID及读值、水位TTL600秒，权限撤销直接失效；实时offset变化不承诺无丢重。
- [ ] 60条动态更新/增删、599/600秒快照、单位混合不求和、200%字体/+40%翻译/768/1024/1440与局部滚动验收；RVT-06/07。组件绿不代表64页绿，T32逐PAGE适用性再验，PDA/地图不机械套Grid。

### T07：隔离文件与受权引用

**Files:** `src\Modules\Platform\Features\Files\{UploadFile.cs,ScanFile.cs,DownloadFile.cs,CleanOrphans.cs}`、`Domain\FileRecord.cs`；`clients\ops-web\src\features\platform\FilesPage.vue`；`tests\Modules\Platform\FileLifecycleTests.cs`。

**Interfaces:** 消费实体授权/存储扫描能力；产出FUNC-PLAT-005/PAGE-PLAT-007的隔离→扫描→Available引用，Workflow只存该受权引用，不接真实secret或任意URL。

- [ ] DTC-PLAT-007/VAL-05先红：20MiB/+1字节、伪MIME/宏文档/禁类型、存储失败/扫描60秒超时、600秒未恢复、在用引用24小时后清理、跨租户下载。
- [ ] 实现类型/签名/UTF-8与禁NUL验证、隔离存储、扫描结果/失败回收与明确处理问题；扫描服务无值不Available，清孤儿需确认无引用。
- [ ] 跑存储中断/外部DB非ACID恢复/API鉴权/页面回填；只存合法文件hash/摘要不存secret，确认既有受权干净文件不被新上传故障误禁。

## B. 仓储与审批业务

### T08：WMS主数据与唯一账本

**Files:** `src\Modules\Wms\Domain\{StockBalance.cs,StockEntry.cs,StockProtection.cs,Location.cs,Container.cs}`、`Features\{MasterData\SaveLocation.cs,Inventory\QueryInventory.cs,Ledger\QueryLedger.cs}`、owner EF配置/迁移；`clients\ops-web\src\features\wms\{LocationsPage.vue,InventoryPage.vue,LedgerPage.vue}`；`tests\Modules\Wms\LedgerInvariantTests.cs`。

**Interfaces:** 消费租户/版本/事件/快照协议；产出FUNC-WMS-001/007、PAGE-WMS-001/002/008以及最小授权位置/库存查询与owner写账合同；不存在公开“任意改余额”接口。

- [ ] DTC-WMS-001/010/011及DTC-WMS-004的并发原语先红：同容器并发绑定、跨仓/Pack、流水重算、十进制超精度/溢出、Qty10/Reserved7内inTransit5时Available3，重复业务键无二效。
- [ ] 实现decimal(28,6)/API decimal string、计件0小数/可分割默认3（显式0..6）、换算默认关、负库存拒绝；普通FIFO/启用效期FEFO完整必需日期、单序列量1；保护按并集不重扣。
- [ ] 三Provider量化/约束/事务和固定快照导出测试，元数据/关联列/盲盘字段投影不泄露；账本append-only补偿，WCS只能消费契约；VAL-01/02/08。

### T09：入库收货与上架

**Files:** `src\Modules\Wms\Features\Inbound\{SaveInbound.cs,ApproveInbound.cs,ReceiveGoods.cs,RequestPutaway.cs,ApplyArrival.cs}`；`clients\ops-web\src\features\wms\InboundPage.vue`；`tests\Modules\Wms\InboundTests.cs`。

**Interfaces:** 消费WMS事务、transport intent与最终到达合同；产出FUNC-WMS-002/PAGE-WMS-003、一次收货账与唯一上架意图。T15未接入前仅测试合同，生产不回假运输成功。

- [ ] DTC-WMS-002/003：计划5分收2+3、2+4超收拒绝；同址不建运输；异址先R1+5/目标0；运输创建响应丢失/重复到达/错容器位置。
- [ ] 实现草稿/审核/收货流水、分配/组盘与唯一意图；分配失败保留真实收货，最终有效回调才R1移出/D1移入，未结余量默认开放。
- [ ] 三Provider/APIs/具名主从页面断言账本/Outbox/意图次数，运行VAL-01/02；T32以实际ST/FW补完整上架证据，不在本任务伪造Pack。

### T10：出库预留、拣选和发运

**Files:** `src\Modules\Wms\Features\Outbound\{ApproveOutbound.cs,ConfirmPicking.cs,ReportShortage.cs,ConfirmShortageRelease.cs,ConfirmShipment.cs,CancelOutbound.cs}`；`clients\ops-web\src\features\wms\{OutboundPage.vue,PickingPage.vue}`；`tests\Modules\Wms\OutboundTests.cs`。

**Interfaces:** 消费账本/库存保护和运输合同；产出FUNC-WMS-003/004、PAGE-WMS-004/005；确认拣选、合法直发、等待运输三种结果明确区分。

- [ ] DTC-WMS-004..006：Qty10两单各预留7仅一成功；拣5短2仍10/7，复核未执行2→10/5，发5→5/0；取消与设备完成/Unknown竞态。
- [ ] 实现全行审核预留本地事务、短缺待核、受权释放、计划7余2保持开放、部分后关余量ClosedPartial；有活动运输禁止手工二扣；FEFO到期now=expiry拒绝。
- [ ] 三Provider真并发/事务故障/API/PAGE及VAL-01/02；页面换仓/物料清依赖、后台改版本不误提交，取消只影响确认未执行部分。

### T11：盘点与多行TransferOrder

**Files:** `src\Modules\Wms\Features\Counts\{CreateCount.cs,RecordCount.cs,ApproveAdjustment.cs,InvalidateCount.cs}`、`Features\Transfers\{ApproveTransfer.cs,CompleteTransferUnit.cs,CloseTransferRemainder.cs}`；`clients\ops-web\src\features\wms\{CountsPage.vue,TransfersPage.vue}`；`tests\Modules\Wms\{CountTests.cs,TransferOrderTests.cs}`。

**Interfaces:** 消费流水/预留/最终实物事实；产出FUNC-WMS-005/006、PAGE-WMS-006/007；父单进度、executionStatus与postingStatus独立。

- [ ] DTC-WMS-007..009：实盘12/8/10、局部锁期间移动拒绝/1800秒只提醒、人工解锁旧计数失效/重盘、盘点人自批拒绝；两行审核不足整事务无部分预留。
- [ ] 实现默认盲盘服务端不返回账面/差异，非零差异非本人复核；逐实物单元移账/预留消费/进度/Outbox同事务，A完成B到但DB失败保留A和B保护，仅补账不重运。
- [ ] RVT-02/VAL-01/02：B已到/Unknown不得当余量取消，独立A完B确认未执行才ClosedPartial；纯人工无独立实物事实的一次同DB确认才整单原子，三Provider故障窗口均验。

### T12：有限流程与运行时表单定义

**Files:** `src\Modules\WorkflowForm\Features\Definitions\{ValidateDefinition.cs,PublishDefinition.cs}`、`Features\Forms\{PublishSchema.cs,ValidateForm.cs,QueryHistoricalForm.cs}`；`clients\ops-web\src\features\workflow\{DefinitionsPage.vue,FormsPage.vue}`；`tests\Modules\WorkflowForm\DefinitionFormTests.cs`。

**Interfaces:** 消费Platform授权/文件引用/白名单业务字段；产出FUNC-WF-001/004、PAGE-WF-001/004的不可变定义/schema版本。运行时表单不生成源码、不把字段当数据库列。

- [ ] DTC-WF-001/008先红：环/任意并行/未知条件/空候选/自批、平级定义歧义、额外字段/只读字段/跨租户文件、发布S2后旧实例S1回放。
- [ ] 实现线性/白名单排他分支/All/Any有限图、字段类型/权限/验证器与版本引用；编辑已发布版产生新草稿，类型改动清不兼容默认，不静默丢字段。
- [ ] 服务端与页面同样拒绝非法schema/条件，不执行脚本、不任意写业务表；真DB不可变版本/并发发布及关联候选测试。

### T13：审批实例、竞争与业务hook

**Files:** `src\Modules\WorkflowForm\Domain\{WorkflowInstance.cs,ApprovalTask.cs}`、`Features\Instances\{StartWorkflow.cs,DecideTask.cs,WithdrawInstance.cs,ReassignTask.cs}`；`src\Modules\Wms\Features\Workflow\ApplyApproval.cs`；`clients\ops-web\src\features\workflow\{InboxPage.vue,ApprovalPage.vue}`；`tests\Modules\WorkflowForm\WorkflowDecisionTests.cs`。

**Interfaces:** 消费版本固定定义/业务快照/Outbox；产出FUNC-WF-002/003、PAGE-WF-002/003和PendingSync业务hook，不在一个跨模块事务里批准WMS。

- [ ] DTC-WF-002..007/009、VAL-03：All任拒即终拒、Any通过/拒绝分别先commit终态一次，另一待办Cancelled；自批/失资格/关键输入变化、hook执行ACK丢失。
- [ ] 实现实例/首任务/Outbox原子，合法决定/版本竞争、尚无决定且未执行才撤回、失资格受权转派留快照；关键输入变更终止旧版重提。
- [ ] 三Provider屏障控制提交先后、重复决定/重启消费与页面历史/关联字段，核Workflow决定1、WMS批准1、通知逻辑1；PendingSync期间业务不被放行。

## C. 消息、编排和执行内核

### T14：Jobs、告警、通知、公告与缓存诊断

**Files:** `src\Modules\Notifications\Features\{Jobs\ScheduleJob.cs,Alarms\RecordOccurrence.cs,Delivery\DeliverNotification.cs,Announcements\PublishAnnouncement.cs,Cache\QueryCacheStatus.cs}`；`clients\ops-web\src\features\notifications\{AlarmsPage.vue,DeliveriesPage.vue,JobsPage.vue,AnnouncementsPage.vue,CachePage.vue}`；`tests\Modules\Notifications\{JobTests.cs,AlarmAnnouncementTests.cs}`。

**Interfaces:** 消费Outbox/主体授权/设备或owner事件；产出FUNC-NTF-002..005、PAGE-NTF-001/002/004..006。alarm/通知delivery/issue不合一，issue归T28。

- [ ] DTC-NTF-003..009/VAL-06/07：双节点同fireTime、600+30秒Unknown不并跑、DST缺失跳过/重复首次UTC；occurrence幂等/高危禁静默/普通900秒默认与3600上限、公告7天/空受众/PDA显式渠道。
- [ ] 实现AdoJobStore默认单调度节点/多节点cluster唯一实例、诊断skip/重算合并/物理不补跑；通知聚合30秒保留事实、高危即时，受众tenant∩org∩(users∪roles)。
- [ ] Outbox999/1000和59/60秒每30秒监测、同episode不重复通知；缓存Disabled/Unavailable区分、HotStore无权grant；渠道断线重连仅补授权数据，不把Sent当已读。

### T15：运输订单、Leg和唯一交接

**Files:** `src\Modules\Orchestration\Domain\{TransportOrder.cs,ExecutionLeg.cs,Handover.cs}`、`Features\Transport\{CreateTransport.cs,DispatchLeg.cs,ApplyLegFact.cs,CancelTransport.cs}`；`clients\ops-web\src\features\operations\{TransportPage.vue,HandoverSettingsPage.vue}`；`tests\Modules\Orchestration\TransportTests.cs`。

**Interfaces:** 消费WMS意图/最小位置、Pack capability/dispatch/query及最终事实；产出FUNC-BUS-001..003、PAGE-BUS-002/005与order/Leg/保管权/同步状态投影。T21/23/24未接入时仅contract fixture，无生产假Ready。

- [ ] DTC-BUS-001..004：同址无Leg、单/双包/反向无handover拒绝、稳定选择；接受响应丢query、可信迟到事实不覆盖新位置、取消与完成。
- [ ] 实现订单/Leg/Outbox与接受竞争持久化、唯一handover；ReadyToReceive不激活下一Leg，源完成且目的接受同版本才原子转唯一保管权；Unknown保护容器/口。
- [ ] RVT-03/05：ST卸货FW接受回执丢，第二单同容器拒；query双方后仅激活一次，中间WMS流水0/最终一次移动；同时保留订单完成与WMS同步状态。

### T16：虚拟设备、语义执行与故障注入

**Files:** `src\Modules\DeviceIntegration\Contracts\{EquipmentCommand.cs,EquipmentFeedback.cs,EquipmentCapabilities.cs}`、`Features\Rules\{PublishRule.cs,ExecuteRule.cs,QueryExecution.cs}`、`Infrastructure\Virtual\VirtualEquipmentAdapter.cs`；`tests\Contracts\DeviceSemanticContractTests.cs`、`tests\Modules\DeviceIntegration\RuleExecutionTests.cs`。

**Interfaces:** 消费T01语义合同/授权/TimeProvider；产出FUNC-DEV-001..004的虚拟闭环、generation/sequence、可编程延迟/丢回执/重复/乱序/已执行未持久化注入点。实现主责设备PAGE在T25接真驱动诊断。

- [ ] DTC-DEV-001..007先用隔离虚拟能力逐项覆盖：旧generation响应、只读点写、双宿主、32/33步骤/嵌套1/无界循环、缺await/总时限、写生效ACK丢Unknown。
- [ ] 实现写前耐久execution/step/command、能力白名单、不可变规则/固定版本、当前代次await及Unknown query；虚拟Adapter也不跳过与真机共有的状态机/授权。
- [ ] 虚拟contract与故障场景可重复重放；模拟能力/物理参数只带具名测试夹具，不可被Promote带入生产；物理写不因消息/规则错误自动重试，VAL-08缺H真实出口0。

### T17：不可变图和纯Route候选算法

**Files:** 各Pack的`Domain\Routing\{Topology.cs,PhysicalConflictMap.cs}`、`Application\Routing\DijkstraPlanner.cs`；`tests\Modules\Routing\{TopologyTests.cs,DijkstraTests.cs,SmallGraphOracle.cs}`。提取共享纯算法须另经真实复用审查，拓扑/运行状态仍各Pack拥有。

**Interfaces:** 消费批准图/schema与能力快照；产出FUNC-ROUTE-001/002的候选及成本/拒因，不产grant。PAGE-ROUTE-001/002由Pack诊断投影接入，不建“中央Route服务”。

**软件验收：** T17纯内核完成；[实现、红绿与审查证据](../../implementation/07-route-kernel.md)。Stacker/FourWay各自私有，无中央Route服务；SPEC/CODE审查PASS，228项Route自动化通过，父集成430项通过。下列勾选只覆盖本任务纯图/候选算法，不代表完整父DTC、诊断页面、T18预约或现场H验收。

- [x] DTC-ROUTE-001/002：G1始终ABD成本2，G2改BD4后ACD成本2；负/NaN/Inf权重、0容量/未知冲突域/多active/无路/同起终点及能力禁行。
- [x] 实现有限非负权重Dijkstra/稳定tie-break、不可变版本/hash、跨版物理冲突映射/拆并覆盖验证与可复现输入；不把无图当直接路线。
- [x] 用独立小图穷举oracle/seed1..20差分与缩小反例验证；算法候选不证明时间窗/物理安全，A*首期不启。DTC-ROUTE-013的纯性质部分随本任务先跑，性能留T33。

### T18：每Pack耐久预约与epoch

**Files:** Stacker和FourWay分别的`Infrastructure\Routing\{ReservationLedger.cs,WriterLeaseStore.cs,GrantTransaction.cs}`及各自EF配置/迁移；`tests\Modules\Routing\ReservationConcurrencyTests.cs`；`clients\ops-web\src\features\route\ReservationsPanel.vue`。

**Interfaces:** 消费候选全资源集合/物理冲突映射；产出FUNC-ROUTE-003、PAGE-ROUTE-003的token/epoch/窗口及owner-scoped释放，始终单个全量grant事务。

- [ ] DTC-ROUTE-005/006：AB/BA不同edge同R1cap1、X1共享冲突，窗[10,20)/[20,30)与buffer0/1夹具；第二资源失败整grant无有效部分记录；双实例抢lease。
- [ ] 实现DB原子lease接管epoch递增、全活跃图physicalConflictId容量/方向冲突核验、稳定资源处理顺序/并发唯一语义、幂等grant/release和拒绝非owner释放。
- [ ] 三Provider真实隔离/锁死锁/失败重试与崩溃窗口报告；失DB/旧epoch新授权0、HotStore伪空闲不改结果。基准计时接口就绪但不先报性能达标。

### T19：派发、反馈、恢复与公平

**Files:** 各Pack的`Features\Execution\{DispatchCommand.cs,ApplyFeedback.cs,ReconcileExecution.cs,ChangeGoal.cs}`、`Domain\Routing\{WaitGraph.cs,PriorityAging.cs}`；`tests\Modules\Routing\{RecoveryTests.cs,WaitGraphTests.cs}`；`clients\ops-web\src\features\route\{CandidatesPanel.vue,RecoveryPanel.vue}`。

**Interfaces:** 消费耐久grant/epoch、虚拟设备query/反馈、固定deployment artifact；产出FUNC-ROUTE-004/005、PAGE-ROUTE-004及恢复/等待投影。生产未有T26 active gate前出口保持禁用。

- [ ] DTC-ROUTE-003/004/007..012/014、RVT-01/04：跨版同域不双授权、旧任务原图；grant已提交后dispatch前crash/已发ACK丢、旧epoch、缓存丢失、旧反馈/错设备/版本、窗过期占用Unknown。
- [ ] 实现写前派发校验/当前epoch及全量grant、可信反馈/释放、接管先query/物理对账；后续段重验已有全量grant、失效则安全重规划再全量授权，不拆成逐段grant事务。
- [ ] 独立oracle测等待环/假环，aging59/60/540秒为0/1/9，同级FIFO+ID，900秒仅告警；没有安全退让路径则升级不自动反向；只在具备可用性/服务界的夹具中验有界完成。

### T20：Stacker分配与双深规则

**Files:** `src\Modules\Wcs\Stacker\Features\Allocation\{SelectAisle.cs,AllocateDoubleDeep.cs,AcceptRequestPoint.cs}`、`Domain\AllocationClaim.cs`；`clients\ops-web\src\features\stacker\AllocationPage.vue`；`tests\Modules\Stacker\AllocationTests.cs`。

**Interfaces:** 消费WMS最小位置/锁/容器及Pack地图；产出FUNC-STK-001/PAGE-STK-002、持久claim和SUDR受理状态，不跨owner事务写WMS占位。

- [ ] DTC-STK-001..003：禁巷/锁/空位n-1,n,n+1、同权重轮转、深孤占/浅占/Unknown、并发抢最后组、错Pack请求点不误受理。
- [ ] 实现合法候选稳定排序后持久轮转、额外保留初值0但物理/挡路空位仍必需，版本竞争保留可恢复申请；无图兼容默认关闭。
- [ ] 三Provider验证claim无双占、失败无有效部分占位；WMS协作失败可查/补偿、无遗留假成功；PAGE快照保留当时候选不混当前MV2。

### T21：Stacker挡路、拆段与控制恢复

**Files:** `src\Modules\Wcs\Stacker\Features\{BlockerMove\PlanBlockerMove.cs,Execution\AdvanceExeStack.cs,Operations\HandleOperation.cs}`；`clients\ops-web\src\features\stacker\{MonitorPanel.vue,DependenciesPage.vue}`；`tests\Modules\Stacker\ExecutionTests.cs`。

**Interfaces:** 消费claim/Route授权/Leg/设备语义；产出FUNC-STK-002..004、PAGE-STK-001/003和归一完成/包级最终门禁。

- [ ] DTC-STK-004..007：C1挡C2、L3并发占/移位失败；两ExeStack段先回S2/错设备、S1确认离开、MV2发布后T1仍MV1、ACK丢重启及越权强完。
- [ ] 实现独立移位任务与WMS移动确认后恢复原检索；段派发核已有全量grant/当前epoch/窗口；合法控制权限/模式/联锁/理由/复核/物理证据缺一拒绝。
- [ ] 接实际Orchestration与虚拟设备，核T1/B1链/WMS只一次、未核实不释放/不重发；同名Pack切换/旧回包不改变动作对象，真实H缺失出口0。

### T22：FourWay分配与停车

**Files:** `src\Modules\Wcs\FourWay\Features\{Allocation\AllocateLayer.cs,Parking\ReserveParking.cs,Parking\ApplyParkingObservation.cs}`；`clients\ops-web\src\features\fourway\ResourcesPage.vue`；`tests\Modules\FourWay\AllocationParkingTests.cs`。

**Interfaces:** 消费WMS上下文/本Pack层图；产出FUNC-FW-001/003、PAGE-FW-002、持久Free/Reserved/Occupied与owner查询，不用lease到期认定离开。

- [ ] DTC-FW-001/003：层额外空位0/1、车上限m-1/m/m+1和缺上限、最后位并发、仅启动反馈、真实进入/离开、停车满持续高优先流。
- [ ] 实现按层而非全仓过滤、稳定选择/轮转、原子停车状态转换、释放后公平唤醒；容量/信号H未配置禁真实新任务，不默认cap1/无限。
- [ ] 三Provider与虚拟反馈同时验不双预约/不提前Free，aging按59/60/540/900边界；只读资源页不能靠隐藏owner/lease过期清占用。

### T23：FourWay交通、提升三阶段与恢复

**Files:** `src\Modules\Wcs\FourWay\Features\{Execution\DispatchVehicle.cs,Hoist\AdvanceHoistLeg.cs,Operations\ReconcileVehicle.cs}`；`clients\ops-web\src\features\fourway\{MonitorPanel.vue,HoistPage.vue}`；`tests\Modules\FourWay\VehicleHoistTests.cs`。

**Interfaces:** 消费分配/停车、Route授权、Leg与语义设备；产出FUNC-FW-002/004/005、PAGE-FW-001/003和同一Leg内ToSrcAp→HoistLift→FromDesEp完成。

- [ ] DTC-FW-002/004..007：同口T1/T2、错容器/乱序/重复阶段、map歧义/旧坐标/身份generation变化、H1Unknown与HotStore失效。
- [ ] 实现完整资源授权后分段dispatch、AP/提升/EP阶段版本+command+Outbox事务、有效离开/占用反馈；Unknown口不得被后任务抢占。
- [ ] 实际Orchestration/虚拟设备全链确认最终Leg事件1、三Provider恢复无二派发；按层切换不改变车实际层，拖表行不能改阶段；DTC-FW-006先收集功能指标，定量在T33执行。

### T24：External WCS集成

**Files:** `src\Modules\Wcs\External\Features\{Configuration\ConfigurePack.cs,Commands\SubmitExternal.cs,Callbacks\ApplyCallback.cs,Recovery\QueryExternal.cs}`、`Infrastructure\Codecs\`内每个获批供应商具名codec文件；`clients\ops-web\src\features\external\{PacksPage.vue,CommandsPage.vue,CallbacksPage.vue}`；`tests\Contracts\ExternalWcsContractTests.cs`。

**Interfaces:** 消费Leg/语义能力/HTTP或MQ、真实协议版本；产出FUNC-ADP-001..004/PAGE-ADP-001..003与归一事件。测试FakeCodec不注册为生产供应商能力。

- [ ] DTC-ADP-001..007/RVT-05：缺codec/错transport、沙箱执行ACK丢、query/cancel不支持、队列N+1/deadline、错签/nonce/Pack、Cancel与Completed竞争、两个Pack同X1。
- [ ] 每供应商独立配置secret引用/能力证据/有界队列；发送前耐久command/attempt/关联，回调验证认证/schema/nonce及当前业务版本、Inbox幂等；迟到事实不覆盖新位置。
- [ ] 在虚拟沙箱跑全部拒绝/恢复，再在获批vendor sandbox逐协议同套测试；缺H则该供应商blocked，不增“已支持”标记；真实payload/签名/凭据不进页面列或视图。

### T25：真实驱动与单写宿主

**Files:** `src\Modules\DeviceIntegration\Infrastructure\{Step7\Step7Adapter.cs,Modbus\ModbusAdapter.cs,Hosting\SingleWriterHost.cs}`；`clients\ops-web\src\features\devices\{ConnectionsPage.vue,PointsPage.vue,RulesPage.vue,ExecutionsPage.vue}`；`tests\Contracts\{Step7ContractTests.cs,ModbusContractTests.cs}`。

**Interfaces:** 消费T16语义contract；产出FUNC-DEV-001..004/PAGE-DEV-001..004在批准隔离台架的证据。控制点/缩放/endianness/扫描率/超时/停止与fence能力均来自H，不抄通用PLC地址。

- [ ] 对DTC-DEV-001..007逐驱动/固件重跑：地址位偏移边界/错身份、100次交错IO/串行限制、generation5迟到不改6、只读点非法写、双宿主与断线恢复。
- [ ] 实现具名驱动/连接代次/队列/单写宿主策略，非幂等写超时保持Unknown；Route epoch不自动等于协议fence，无协议能力须已验证宿主/网络保护。
- [ ] 记录台架隔离/实际point白名单/物理写计数/设备回读；UI规则发布与执行版本区分，显示列不改步骤。无实物台架可完成虚拟部分但本任务真实证据仍blocked。

## D. 部署、可视化与全链交付

### T26：工程编辑、owner准备和部署切换

**Files:** `src\Modules\Simulator\Features\{Engineering\ValidateEngineering.cs,Deployment\PrepareDeployment.cs,Deployment\ActivateDeployment.cs,Deployment\PreviewPromotion.cs}`、`Domain\DeploymentManifest.cs`；WMS/各Pack/SCADA各自`Features\Deployment\PrepareArtifact.cs`与持久artifact映射；`clients\simulator\src\features\{engineering\EditorPage.vue,deployment\DeployPage.vue,promotion\PromotePage.vue}`；`tests\Modules\Simulator\DeploymentTests.cs`。

**Interfaces:** 消费各owner公开prepare/hash/版本及当前task/grant状态；产出FUNC-SITE-001/002/004、PAGE-SITE-001/002/005的不可变manifest/Prepared/active代数和Promote软件门禁。SCADA artifact准备属于其owner，可先于T27视觉页面完成，不制造循环依赖。

- [ ] DTC-SITE-001..004与007的软件反例、DTC-UI-031、RVT-01/04/07：owner失败/缺确认/错hash、Prepared599/600秒、D2/D3同expectedActive并发CAS、改目标摘要/自批/批准到期、warning分类。
- [ ] 实现Simulator本地事务active CAS+generation+Outbox；默认整PackDrain，拒新任务/新版本grant，旧任务仅按原artifact当前epoch获得排空必要授权，暂停未终结非排空；CompatibleContinue必须证据且跨版同物理域。
- [ ] 回滚建立补偿部署、不改旧记录；漏激活事件查manifest；生产warning默认拒，仅分类非安全展示项逐条理由复核；Promote默认只读/localhost等拒绝。真实分阶段切换和DTC-SITE-008留T34验收，不因软件门禁绿就开生产。

### T27：确定性仿真、Reset和只读SCADA

**Files:** `src\Modules\Simulator\Features\Runs\{StartRun.cs,InjectFault.cs,ResetRun.cs,QueryRun.cs}`、`src\Modules\Scada\Features\{Views\SaveView.cs,Projection\QueryProjection.cs}`；`clients\simulator\src\features\runs\{RunPage.vue,ResultsPage.vue}`、`clients\ops-web\src\features\scada\ScadaPage.vue`；`tests\Modules\Simulator\SimulationScadaTests.cs`。

**Interfaces:** 消费manifest/虚拟设备/owner只读投影；产出FUNC-SITE-003/005、PAGE-SITE-003/004/006及固定seed/clock/hash仿真报告，不在SCADA放现场运维控制。

- [ ] DTC-SITE-005/006/009/010：Trigger/Gateway同输入、延迟/丢回执/重复/乱序、Reset混入生产ledger/非仿真T1、view改坐标、未知绑定与旧seq。
- [ ] 实现可复现注入与阶段/参数/结果留存；Reset严格run范围预览，保护未知任务；SCADA只读数据源与视图配置分开，改view不改WMS坐标/Route hash。
- [ ] 断线查快照恢复、Stale/Unknown/未定位对象不消失；直调运行写拒绝且业务事实0变化；仿真与生产身份/网络边界实测，不把动画当状态确认。

### T28：单2D地图运维与问题闭环

**Files:** `src\Modules\Orchestration\Features\{Operations\PreviewOperation.cs,Operations\ExecuteOperation.cs,Issues\AssignIssue.cs,Issues\VerifyIssue.cs,Projection\QueryOperationsMap.cs}`、`Domain\OperationalIssue.cs`；各Pack已有operations handler最终复验；`clients\ops-web\src\features\operations\{MapWorkbench.vue,IssuesPage.vue,OperationPage.vue}`；`tests\Modules\Orchestration\OperationsIssueTests.cs`。

**Interfaces:** 消费Scada/owner投影、alarm occurrence、Pack能力/动作/回读及WMS对账；产出FUNC-BUS-004/005、PAGE-BUS-001/003/004，唯一issue与operation审批记录，不把alarm清除等同解决。

- [ ] DTC-BUS-005..010、DTC-UI-029、RVT-07/VAL-08：越权/缺理由/自批/换参数/批准599/600、未定位/图层关闭、occ1重复3次/occ2独立、Accepted但无回读、错对象/处置前旧证据。
- [ ] 实现地图→对象/队列→就地动作→owner门禁→回读→对账闭环、Open至Resolved/复开生命周期；P1响应300秒/未解决900秒升级一次，主备缺失禁控制投用，普通状态不绕审批。
- [ ] 所有资产含Unknown能由地图或队列发现，键盘替代/焦点稳定、账面/保管权/观测分栏；处置后新鲜证据不足保持AwaitingVerification，告警清除无库存/资源释放，复开留累计/新响应周期。性能入口提前埋点，实测T33。

### T29：PDA全业务与终端约束

**Files:** `clients\pda\src\pages\{login\index.vue,tasks\index.vue,receiving\index.vue,putaway\index.vue,picking\index.vue,counts\index.vue,shipping\index.vue}`、`src\services\{pda-api.ts,scanner-adapter.ts,operation-recovery.ts}`；`src\Modules\Wms\Features\Pda\QueryPdaTasks.cs`仅业务适配；`tests\EndToEnd\specs\pda.spec.ts`。

**Interfaces:** 消费Platform身份/当前权限、WMS同一用例、issue报告与显式PDA公告；产出FUNC-PDA-001..006/PAGE-PDA-001..006。异常报告只进issue，不给强完/解联锁设备命令。

- [ ] DTC-PDA-001..010、DTC-UI-030、VAL-01/02/04/05：任务换父/错码/第3页回填、连扫/重复提交、收货/拣选/盘点/发运断网、已提交响应丢查operation。
- [ ] 默认键盘楔入+Enter/手输禁、持久业务缓存禁，仅受保护operationID用于查询；获批端可加密最小只读TTL300秒，无活动300秒锁定。所有写在线调用WMS，绝不本地排队/自动重放。
- [ ] H5与逐个批准OS/UniApp/终端/扫码枪/手套网络实测，299/300秒边界及盲盘账面不回包；不将Web E2E代替实机，未证终端blocked不宣称支持。

### T30：系统诊断、隔离恢复与发布手册

**Files:** `src\Host\Health\ReadinessChecks.cs`、`clients\ops-web\src\features\system\{HealthPage.vue,DiagnosticsPage.vue,DeploymentEvidencePage.vue}`；`tests\EndToEnd\RecoveryScenarioTests.cs`、`tests\Modules\System\ProviderMigrationTests.cs`；`docs\runbooks\{deployment.md,isolated-restore.md,writer-reconciliation.md}`。

**Interfaces:** 消费各模块readiness/事件水位/活动command/grant/manifest与准确Provider恢复工具；完成FUNC-SYS-001..005/PAGE-SYS-001..003，不添加网页一键还原/lease接管。

- [ ] DTC-SYS-001..008、RVT-08/VAL-07/08：旧备份在C1发送前但外部已执行、隔离网络误配endpoint、Outbox/Job/Host/Device分窗口重启、DB失联/旧epoch、新writer未核在途，以及Disabled/Misconfigured/Degraded运维区分。
- [ ] 补齐关联诊断/证据引用与具名准确runbook，恢复隔离/生产出口deny/禁写启动，重建WMS流水/任务/grant/owner/hash/水位；不自动发旧Outbox物理命令，Unknown保护不删。
- [ ] 三Provider空库/旧schema升级和备份恢复演练；RPO≤900秒/RTO≤14400秒含外部对账至控制解禁，不达标仍禁控制。清理90天结果不删长期领域键/Unknown，日志/审计保留按PAR-09。

### T31：Seven迁移映射、试迁和切换

**Files:** `docs\migration\{seven-to-wes-mapping.md,cutover-checklist.md,reconciliation-report.md}`；批准范围需要时创建`tools\Migration\`下具名只读抽取/受控导入程序、`tests\EndToEnd\MigrationReconciliationTests.cs`，不访问Seven生产写端。

**Interfaces:** 消费范围/owner/迁移政策与实际脱敏源快照；产出旧→新稳定ID/单据/库存/跟踪/权限/设备/地图映射、重复导入幂等和可审计差异报告。迁移工具调用模块受控导入契约，不跨私表补库存。

- [ ] 写保留能力清单与字段/状态映射，用重复同码不同租户、批次/效期缺失、序列重复、未完成单据/在途Unknown、已有审计/流水和配置secret引用作拒绝夹具；不能以覆盖CRUD菜单证明业务保留。
- [ ] 先只读分析/人工业务确认，再在隔离库导入；余额从流水重算、总量按料/单位/批次/仓维度对账、旧策略不静默清除；未核设备/历史任务不自动重发或变空闲。
- [ ] 重复导入不新增账/命令，差异必须到业务键；切换冻结入口/取最终水位/处理在途/新系统禁写复核/批准解禁与失败回退演练。以DTC-SYS-002/004/006及WMS相关DTC添加迁移变体，不另冒充原已有测试通过。

### T32：全模块集成与页面回归

**Files:** `tests\EndToEnd\{CrossModuleScenarioTests.cs,specs\all-pages.spec.ts,case-matrix.json}`；`tests\Contracts\CompatibilitySuiteTests.cs`；更新[验收矩阵](../../acceptance/01-cross-module-and-provider-matrix.md)与168父DTC执行结果引用。

**Interfaces:** 消费实际模块/锁定build/Provider/Adapter/终端/规则版本；产出全部DTC-XMOD-001..013及64 PAGE逐页适用性结果。集成装配禁止Fake业务完成/替代InMemory。

- [ ] 先把13条[跨模块步骤](../../detailed-design/02-cross-module-scenarios.md)逐条编码为三Provider实际owner路径，含ST/FW上架、提升机、短拣、双包交接、External、故障地图、部署、SCADA、Workflow、强完、重启、issue。
- [ ] 依据每PAGE的G/F/R/M/W/P/S配置展开DTC-UI-001..032，所有适用状态/关联/列配置都执行；逐模块核实UI开关只影响入口、运行开关有owner合同且安全/授权边界未变；不适用记录原因/设计引用，禁止把“同组件测过”自动填64页通过。
- [ ] 检查每个FUNC（含FUNC-SYS-005）至少一合法与主要拒绝/故障、原265 TC均落到实际自动化case/人工H记录；RVT01..09/VAL01..08全覆盖，缺环境blocked/失败failed，不用总通过率掩盖一个高风险失败。

### T33：性能、性质与恢复目标验证

**Files:** `tests\Performance\{Wes.Performance.Tests.csproj,RouteBenchmarkTests.cs,MapProjectionBenchmarkTests.cs,ApiBenchmarkTests.cs,LoadProfiles.json}`、`docs\acceptance\reports\`下本次具名脱敏报告（仅执行时创建）。

**Interfaces:** 消费M5固定功能正确build/地图/seed/硬件/Provider/网络；产出FUNC-ROUTE-006/PAGE-ROUTE-005、DTC-ROUTE-013、DTC-FW-006、DTC-BUS-006、DTC-NTF-008及RVT-09证据。

- [ ] 固定1000资产/50车/100并发用户/100事件每秒/单Pack10000节点30000边，预热5分钟/稳态30分钟/独立3次，保存任务混合、样本量、seed、拒绝/超时/错误率，不把失败样本剔出。
- [ ] 各次独立验API500/1500ms、规划100/500ms、DB grant50/200ms、地图选择至骨架100/250ms、非安全投影1/3秒的p95/p99；门限等值可过/超1ms失败，不平均三次分位。
- [ ] 单独报告故障恢复与公平前提/冲突oracle，任何安全不变量失败即整体相关项失败；RPO/RTO取真实恢复证据复核，缺硬件/报告not-run/blocked而非填0。性能达标不证明物理安全。

### T34：现场验收与受控发布

**Files:** `docs\acceptance\site\`下具名Provider/Adapter/终端/安全/业务签署及复核报告，更新[总验收](../../acceptance/01-cross-module-and-provider-matrix.md)、Promote和正式runbook；无新的通用网页控制器。

**Interfaces:** 消费全部软件/集成/迁移/性能证据及具名现场H；产出DTC-SITE-007/008真实阶段、所有适用C/H结果与生产许可，不能扩出未批准设备/区域/能力。

- [ ] 核H定位/制动/速度/buffer/freshness/时钟/容量信号/载荷/query去重停止/认证时窗/Device fence、Provider版本/事务、网络/身份/主备与正式停止条件；缺一对应范围blocked，不能给经验秒数临时过。
- [ ] 分别批准只读联调→单设备低风险闭环→明确区域/设备/负载扩大，每阶段重验权限/双人审批绑定及当前epoch/grant/联锁；现场急停/安全PLC由既定责任人操作，Web不能解安全保护。
- [ ] 故障/Unknown时执行停止扩大/保持保护/query/补账/补偿部署，不清任务/降DB掩盖事实；业务/设备/安全/运维/QA签署与残余风险明确后才许可真实投用，未支持Provider/供应商/终端不跟随发布“全支持”。

## E. 完成口径

制订计划时全部任务初始为未开始；当前实施切片及阻断以[就绪台账](../../implementation/readiness-register.md)为准。软件、纯仿真、真实Provider、真实Adapter、真机PDA、性能和现场签署分别计状态。计划范围完整不等于软件已实现；模块已实现不等于它的每个环境/变体已通过。执行时只勾完成且有证据的步骤，不批量勾整个模块。
