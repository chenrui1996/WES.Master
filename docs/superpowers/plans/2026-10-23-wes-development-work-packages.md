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

- [x] [新建用户当前目录门禁](../../implementation/155-t04-user-create-loading-gate.md)/[证据](../../implementation/156-t04-user-create-loading-gate-tests.md)：按钮disabled对齐原busy/stale guard，新增2组件测试先红，前端691/严格构建、真实HTTPS创建/凭据/角色流程及四套浏览器回归通过、Release0/0。不关闭父DTC/T04整包。

- [x] [数字字典业务码与真实语言回退](../../implementation/151-t04-numeric-dictionary-code.md)/[证据](../../implementation/152-t04-numeric-dictionary-code-tests.md)：已批准code0现在以string原值保存，分类/语言键/角色/部门校验不变；新增18领域/9HTTP/1真实owner，相关81.NET含真实HTTPS键盘创建/两次改译/语言回退/有引用删除409通过、Release0/0。不关闭完整DTC-PLAT-006/012/014或T04整包。

**Files:** `src\Modules\Platform\Features\{Authorization\RolePolicy.cs,Tenancy\TenantContext.cs,Configuration\VersionedSettings.cs,Audit\QueryAudit.cs}`、对应owner EF配置/迁移；`clients\ops-web\src\features\platform\{UsersPage.vue,RolesPage.vue,TenantsPage.vue,SettingsPage.vue,AuditPage.vue}`；`tests\Modules\Platform\{AuthorizationTests.cs,SettingsAuditTests.cs}`。

**Interfaces:** 消费当前身份；产出FUNC-PLAT-002..004/006、PAGE-PLAT-003..006/008，当前权限/数据范围/字段投影、B/P/H版本化策略查询与不可改审计。普通只读授权缓存30秒，高后果/快照每次复验；无TenantId=0共享。

- [ ] DTC-PLAT-004..006/008..009/011..014先红：空范围放大、跨租户同码缓存、无租户后台主体、旧版本覆盖、secret配置、热更新半状态、引用字典被删、审计越权修改。
- [ ] 做具名RBAC/组织/租户切换、字典/语言回退、强类型业务配置及来源/版本展示；部署Features与租户/数据库业务配置分离，非法P配置拒绝不回退，H缺项保持Unverified；业务审计与所属事务可靠关联。
- [ ] 三Provider的复合唯一键/幂等清理90天后领域键仍防重；页面关联筛选/撤权/敏感字段列不可恢复，验证PAR-01/02/09及VAL-04/08。审计界面不可修改历史。
- [x] 目录命令回执90天清理已接入默认禁用、显式tenant白名单的owner事务维护入口与托管周期；与Inbox原子提交，领域身份/审计/Outbox不删，真实MySQL两owner竞争安全退至下周期。见[维护设计](../../implementation/87-platform-retention-maintenance.md)和[测试](../../implementation/88-platform-retention-maintenance-tests.md)。生产启用/容量及T04其余验收仍未关闭。
- [x] 固定审计快照受权导出：独立当前export/read字段交集、显式列/原600秒成员、永久operation与审计/Outbox原子提交后CSV交付；8项HTTP、14项真实MySQL owner、前端228项/严格构建、真实HTTPS Chromium及最终当前编译源1243项.NET通过。见[设计](../../implementation/111-t04-authorized-audit-export.md)/[证据](../../implementation/112-t04-authorized-audit-export-tests.md)。不授予既有管理员默认export，不关闭T04/T06其余验收；既有健康探测波动已记录。

### T05：owner事务Outbox/Inbox与可靠投递

- [x] [默认关闭WMS托管调度桥](../../implementation/159-t05-hosted-wms-publisher.md)/[证据](../../implementation/160-t05-hosted-wms-publisher-tests.md)：严格部署快照/tenant/batch/周期、新owner runner、60秒合作取消、未确认警告；新增27 Host/真实进程及1真实MySQL runner，最终Release相关147项含四套HTTPS浏览器通过、构建0/0。真实Program仍在更早门禁拒绝未登记的Outbox/MessageQueue runtime owner；实际adapter/运行能力登记/Inbox与完整恢复接线仍缺，不代表真实Broker已接通。

- [x] [WMS owner发布器](../../implementation/157-t05-wms-owner-outbox-publisher.md)/[证据](../../implementation/158-t05-wms-owner-outbox-publisher-tests.md)：90秒耐久租约/CAS、claim计attempt、最多6次/15分钟预算、有限抖动退避/隔离、发送前再验及ACK未确认恢复，原九字段四态/三字段PendingCount与客户端同步。新增33owner/3HTTP/4前端，最终238+32含四套HTTPS浏览器+29=299、前端695/严格构建与Release0/0通过。默认无Host worker/实际transport，Inbox/真实Broker/跨owner统一快照和父DTC仍未关闭。

- [x] [WMS创建事件范围统计](../../implementation/149-t05-wms-creation-event-counts.md)/[证据](../../implementation/150-t05-wms-creation-event-counts-tests.md)：独立当前统计字段creator交集、SQL全量LongCount、共享组件两owner联合类型；WMS仅Pending三字段，不伪造Inbox/发布状态、不跨owner求和。新增26owner/12HTTP/23前端，最终205+36+29=270、前端604/严格构建及Release0/0通过。统一快照/发布消费/真实Broker仍未完成，整包不关闭。

- [x] [消息owner选择与WMS事件客户端](../../implementation/143-t05-owner-selected-message-client.md)/[证据](../../implementation/144-t05-owner-selected-message-client-tests.md)：同路由成对模块+permission门禁、切换取消/清旧、重放锁owner、WMS九字段Pending元数据；新增59前端/导航/路由，最终521/严格构建、真实HTTP/三套浏览器16、架构29通过，Release零警告错误。第二owner客户端已接入，但跨owner统计/统一快照/WMS发布消费与真实Broker仍缺，整包不关闭。

- [x] WMS创建事件第二owner诊断API：独立当前消息全字段/真实creator交集、SQL前授权分页、九字段Pending元数据，无payload/虚构Inbox/attempt；新增38真实owner/13HTTP，相关179+159+29=367通过、Release零警告错误/无模型漂移。[设计](../../implementation/141-t05-wms-creation-event-diagnostics.md)/[证据](../../implementation/142-t05-wms-creation-event-diagnostics-tests.md)。第二owner客户端/跨owner聚合/发布消费/真实Broker仍未完成，整包不关闭。

- [x] WMS主数据新增默认禁用仓/位由owner持久化门禁派生精确不可变Outbox，与永久创建事实同事务；重放不追加、晚期失败三表均回滚、不回填旧operation。新增17真实owner，主数据141+领域/HTTP/真实浏览器100+架构29=270通过，Release零警告错误，实际DDL/模型验证通过。[设计](../../implementation/135-t05-t08-wms-creation-outbox.md)/[证据](../../implementation/136-t05-t08-wms-creation-outbox-tests.md)。不包含WMS publisher/Inbox/跨owner统计或真实Broker，整包不关闭。

**Files:** `src\Modules\Notifications\Features\Messaging\{PublishOutbox.cs,ReplayMessage.cs,QueryDelivery.cs}`；每写入owner的`Infrastructure\Messaging\{OutboxStore.cs,InboxStore.cs}`和本模块迁移；`clients\ops-web\src\features\notifications\MessagesPage.vue`；`tests\Modules\Notifications\MessagingTests.cs`、`tests\Contracts\MessagingContractTests.cs`。

**Interfaces:** 消费稳定事件envelope和授权；产出FUNC-NTF-001/PAGE-NTF-003、模块本地事务适配器及message查询/重放。Notifications只管投递诊断，不持有各owner业务事务或通用跨库Outbox。

- [ ] DTC-NTF-001/002：业务提交前故障→业务/Outbox均0；提交后Broker断开记录保留；消费提交后ACK丢失重投效果1；物理Unknown0自动重发。
- [x] Platform首片实现发布竞争/Inbox幂等、错误分类、1/5/30/120/600秒相邻等待最多5次、总15分钟deadline、永久隔离及受权同payload重放与稳定事件身份；见[Outbox/Inbox](../../implementation/35-t05-platform-outbox.md)、[Inbox](../../implementation/37-t05-platform-inbox.md)、[消息运维](../../implementation/39-t05-platform-message-operations.md)。这不代表Notifications聚合、其他owner适配或真实Broker闭环完成。
- [x] Platform Inbox 90天owner-local有界保留清理及保存失败恢复，许可绑定原owner事务、成功后失效；见[实施](../../implementation/49-t05-inbox-retention-save-recovery.md)及[测试](../../implementation/50-t05-inbox-retention-save-recovery-tests.md)。默认关闭的显式tenant托管保留维护已接线，见[设计](../../implementation/87-platform-retention-maintenance.md)及[测试](../../implementation/88-platform-retention-maintenance-tests.md)；它不等于Quartz作业、托管publisher或真实Broker。
- [x] Platform托管publisher调度：默认禁用、显式tenant/batch/周期、60秒合作取消预算与公共owner边界；缺少唯一singleton transport或Outbox/MessageQueue能力时拒绝启用。新增25项Host与5项真实MySQL测试通过，原34项托管保留配置及12项持久保留回归仍通过。见[设计](../../implementation/95-t05-hosted-platform-publisher.md)/[测试](../../implementation/96-t05-hosted-platform-publisher-tests.md)。没有真实Broker适配/确认丢失验收，不关闭T05。
- [ ] 三Provider/隔离Broker执行崩溃窗口，断线不得丢Outbox；过deadline控制拒发、迟到可信完成核验且不覆盖新位置、认证过期隔离/query；采用RVT-05、VAL-07/08，不声称消息exactly-once。
- [x] PAGE-NTF-003 Platform owner只读客户端：当前All完整必要字段、可选字段投影、实时分页、撤权/迟到/离线清旧；前端346项、真实HTTPS Chromium与2项消息HTTP、1项MySQL查询回归通过。见[设计](../../implementation/127-t05-platform-message-diagnostics-client.md)/[证据](../../implementation/128-t05-platform-message-diagnostics-client-tests.md)。不代表跨owner聚合、真实Broker或重放表单完成。
- [x] PAGE-NTF-003 Platform受权重放客户端：准备前重读、当前All字段、理由/expected/原operation预览、二次确认、未知结果原键显式重试；前端379及严格构建、真实HTTPS Chromium/HTTP3、MySQL重放2通过。见[设计](../../implementation/129-t05-authorized-message-replay-client.md)/[证据](../../implementation/130-t05-authorized-message-replay-client-tests.md)。跨owner/过滤/Inbox计数/真实Broker及T05整包仍未完成。
- [x] Platform消息状态/创建时间筛选在owner SQL中先执行再分页，原GET可选参数兼容、非法值400、取消后不交付；前端396/严格构建、真实HTTPS浏览器/HTTP13、MySQL查询/重放18、架构29通过。见[设计](../../implementation/131-t05-filtered-message-diagnostics.md)/[证据](../../implementation/132-t05-filtered-message-diagnostics-tests.md)。跨owner与Inbox统计/真实Broker及T05整包仍未完成。
- [x] Platform显式字段授权Outbox四状态/现存Inbox收据计数API与手工客户端；前端427/严格构建、MySQL38、HTTP/真实浏览器21、架构29通过，Release零警告错误。见[设计](../../implementation/133-t05-owner-message-counts.md)/[证据](../../implementation/134-t05-owner-message-counts-tests.md)。独立观察不是快照/业务效果数/Broker健康；跨owner统计与真实Broker及整包仍未完成。

### T06：共享交互与授权查询底座

**Files:** `clients\ops-web\src\components\{QueryState.vue,ColumnSettings.vue,RelatedEntitySelect.vue}`、`src\composables\{useScopedView.ts,useLatestRelatedQuery.ts}`；Simulator相同纯UI可提取到`clients\shared-ui\`但须先证明真实复用；`src\Modules\Platform\Contracts\QueryScope.cs`及各owner的`Features\Queries\QuerySnapshot.cs`；`tests\EndToEnd\specs\shared-ui.spec.ts`、组件同目录`*.spec.ts`。

**Interfaces:** 消费当前scope/字段权限；产出loading/空态/error/Stale状态、列偏好/关联候选/父子指纹、实时与固定水位快照协议。快照数据属于查询owner，不集中备份所有域数据到Platform。

- [ ] DTC-UI-001..028/032先用真实具名最小测试页加受控API验证：列必显/顺序/宽度/键盘替代、损坏storage显式提示、本机主体租户环境隔离、父切换旧回包/主从行ID竞态、无导出页不增导出。
- [x] Users/Roles及共享候选撤权/旧请求首片：401/403/明确拒绝清候选与真实编辑草稿，旧成功/失败/已取消signal不能污染新范围；保留25条分页、稳定ID及详情回填，见[实施](../../implementation/89-t06-candidate-authorization-boundary.md)和[测试](../../implementation/90-t06-candidate-authorization-boundary-tests.md)。整套Ops Web 201项及严格构建通过；本片浏览器E2E、其他页面及完整DTC仍待验收。
- [x] 按[功能开关设计](../../detailed-design/feature-switches.md)接入匿名白名单模块UI catalog；加载导航/路由前获取，关闭模块隐藏入口、直接导航有明确不可用态；catalog失败显式报错，不默认全开/空配置。API仍独立授权。已实施，见[客户端记录](../../implementation/41-t06-query-state-and-module-catalog.md)及[测试证据](../../implementation/42-t06-query-state-and-module-catalog-tests.md)。
- [x] 现有审计、用户、角色、租户、配置、字典与会话接入共享实时分页，25条默认与25/50/100选项、查询指纹/失败/空后续页边界及实时offset限制已验证，见[实施](../../implementation/47-t06-shared-realtime-pager.md)和[测试](../../implementation/48-t06-shared-realtime-pager-tests.md)。这不关闭以下固定快照与完整DTC门禁。
- [x] Platform审计 owner 首个固定成员快照切片已落地：RepeatableRead冻结有序审计ID、10分钟TTL、当前授权复验及真实MySQL/HTTPS/Chromium验证；见[实施](../../implementation/51-t06-platform-audit-snapshot.md)和[测试](../../implementation/52-t06-platform-audit-snapshot-tests.md)。该切片不涵盖其他owner、可变数据值快照或T06整体。
- [x] DTC-UI-001 在Platform Audit页的慢请求首片已验证：200ms不显示占位，300ms后显示授权列形状的骨架；同scope刷新保留原证据并显示上次成功UTC读取时间。见[实施](../../implementation/53-t06-audit-slow-query-state.md)及[测试](../../implementation/54-t06-audit-slow-query-state-tests.md)。其余PAGE适用性与DTC-UI-001整体仍待逐项验收。
- [x] DTC-UI-002 在Platform Users页首片已验证：区分首次无用户、筛选未提交、已提交条件无匹配和查询失败；见[实施](../../implementation/55-t06-users-empty-states.md)及[测试](../../implementation/56-t06-users-empty-states-tests.md)。用户目录不适用队列zero-work；其他PAGE/队列适用性仍待验收。
- [x] DTC-UI-014 共享列设置首片已验证方向键排序草稿/显式应用与identity宽度160/600上下边界；见[实施](../../implementation/57-t06-column-settings-keyboard-widths.md)及[测试](../../implementation/58-t06-column-settings-keyboard-widths-tests.md)。恢复默认列仅重置列布局、不清筛选/业务行的首片见[实施](../../implementation/67-t06-column-reset-isolation.md)及[测试](../../implementation/68-t06-column-reset-isolation-tests.md)。其他页面长业务码、固定列/宽屏及全合同仍未验收。
- [x] DTC-UI-016 跨标签首选项变化不覆盖当前未提交列设置草稿，受控StorageEvent和显式应用流程已验证；见[实施](../../implementation/59-t06-column-preference-cross-tab-draft.md)及[测试](../../implementation/60-t06-column-preference-cross-tab-draft-tests.md)。存储禁用与配额失败提示/当前布局保留见[实施](../../implementation/63-t06-column-preference-storage-failures.md)及[测试](../../implementation/64-t06-column-preference-storage-failures-tests.md)；授权schema变化后规范化移除撤权列并保留稳定ID布局见[实施](../../implementation/65-t06-column-preference-schema-migration.md)及[测试](../../implementation/66-t06-column-preference-schema-migration-tests.md)。真实浏览器故障与其它页面场景仍待，整项不标完成。
- [x] DTC-UI-015 的本机偏好作用域切换首片已验证：同一存储下租户、主体与环境独立读写且恢复各自列布局；见[实施](../../implementation/61-t06-column-preference-scope-isolation.md)及[测试](../../implementation/62-t06-column-preference-scope-isolation-tests.md)。此受控组件测试不替代完整DTC-UI-015实体/登出和无业务写E2E。
- [x] DTC-UI-015 登出时清空当前列布局易失状态、保留匹配主体的本机布局并在同主体重新认证后恢复，受控身份/组件测试已通过；见[实施](../../implementation/69-t06-column-preference-logout-boundary.md)及[测试](../../implementation/70-t06-column-preference-logout-boundary-tests.md)。不覆盖页面行数据或关联缓存的完整登出E2E。
- [x] DTC-UI-022 Platform Roles/Users授权部门/角色候选远程首片改为25条/页并验证offset连续、旧页候选保留；见[实施](../../implementation/71-t06-department-candidate-page-size.md)及[测试](../../implementation/72-t06-department-candidate-page-size-tests.md)。搜索竞态、伪造ID提交及其他选择器仍待验收。
- [x] DTC-UI-022 为共享候选读取器补充reset前延迟响应不得覆盖新候选状态的受控回归测试；见[实施](../../implementation/73-t06-stale-directory-candidate-response.md)及[测试](../../implementation/74-t06-stale-directory-candidate-response-tests.md)。未增加搜索API或将此等同A/AB搜索竞态。
- [x] DTC-UI-022 以真实MySQL/HTTP验证不存在角色ID的用户角色替换命令被拒绝且用户版本、成员关联、审计无变化；worktree环境下授权配置源解析和完整Identity HTTP类回归也通过；见[实施](../../implementation/75-t06-reject-unresolved-role-candidate.md)及[测试](../../implementation/76-t06-reject-unresolved-role-candidate-tests.md)。
- [x] DTC-UI-022 Platform Roles指定部门候选精确Code查询首片已通过真实MySQL/HTTP授权交集验证及受控页面A/AB竞态、250ms去抖、25条分页和已选草稿保留；见[实施](../../implementation/77-t06-role-department-code-candidates.md)及[测试](../../implementation/78-t06-role-department-code-candidates-tests.md)。隔离HTTPS Chromium与完整DTC/T06验收仍待。
- [x] DTC-UI-022 Platform Roles编辑既有规则时按已保存部门ID独立批量回填授权可见摘要，不依赖候选页；每批最多100个，按当前owner的Id资源范围和字段权限投影，未解析项不泄漏名称/存在性。Identity HTTP/MySQL连接门禁70项、owner测试2项、Roles页面16项、Ops Web全套176项及生产构建通过；见[实施](../../implementation/79-t06-role-department-reference-resolution.md)及[测试](../../implementation/80-t06-role-department-reference-resolution-tests.md)。隔离HTTPS Chromium与完整DTC/T06仍待验收。
- [x] DTC-UI-022 Platform Users编辑用户角色关联时按已保存角色ID独立批量解析当前授权摘要；每批最多100个，缺失/无权项不回显原始ID，停用角色不可成为新增候选，现有owner仍重验写入。角色引用owner3项、Identity HTTP类73项、Users页面21项、Ops Web全套180项及生产构建通过；见[实施](../../implementation/81-t06-user-role-reference-resolution.md)及[测试](../../implementation/82-t06-user-role-reference-resolution-tests.md)。隔离HTTPS Chromium与完整DTC/T06仍待验收。
- [ ] 实现语义HTML/Composition API/strict TS与Element Plus已有组件复用；白名单偏好不存业务数据/secret/待写。核对/已有导出固定有序ID及读值、水位TTL600秒，权限撤销直接失效；实时offset变化不承诺无丢重。
- [ ] 60条动态更新/增删、599/600秒快照、单位混合不求和、200%字体/+40%翻译/768/1024/1440与局部滚动验收；RVT-06/07。组件绿不代表64页绿，T32逐PAGE适用性再验，PDA/地图不机械套Grid。

### T07：隔离文件与受权引用

- [x] [当前实体附件客户端基本软件](../../implementation/171-t07-current-entity-attachment-client.md)/[证据](../../implementation/172-t07-current-entity-attachment-client-tests.md)：固定仓/位上下文、显式目录/重读、原 tuple 二次确认/未知恢复、父页目标互锁及共有下载。前端770/严格构建、相关MySQL owner482、HTTP/既有浏览器/WMS领域178、架构29通过；新组合浏览器全矩阵及外部项按 TodoList 后置，不代签完整父DTC。

**Files:** `src\Modules\Platform\Features\Files\{UploadFile.cs,ScanFile.cs,DownloadFile.cs,CleanOrphans.cs}`、`Domain\FileRecord.cs`；`clients\ops-web\src\features\platform\FilesPage.vue`；`tests\Modules\Platform\FileLifecycleTests.cs`。

**Interfaces:** 消费实体授权/存储扫描能力；产出FUNC-PLAT-005/PAGE-PLAT-007的隔离→扫描→Available引用，Workflow只存该受权引用，不接真实secret或任意URL。

- [x] [当前实体附件目录owner/API](../../implementation/169-t07-current-entity-attachment-catalog.md)/[证据](../../implementation/170-t07-current-entity-attachment-catalog-tests.md)：固定仓/位集合、当前实体两时点、精确实际引用与逐字段scope SQL过滤后分页，复用暂存查询且无物理IO。新增30真实owner/28 HTTP，相关333含既有HTTPS文件浏览器、精确最终新API28/架构29及Release0/0通过，不重复累计；客户端/真实病毒库/ACL及下列父DTC/T07仍未关闭。
- [x] [永久具名附件绑定/解绑](../../implementation/167-t07-durable-business-file-bindings.md)/[证据](../../implementation/168-t07-durable-business-file-bindings-tests.md)：明确当前双owner Attach/Detach及全部持久来源实体read、引用/计数/版本/审计/永久回执单事务、精确父文件锁后复验和严格PUT/DELETE；同键只恢复原事实。真实MySQL1213锁顺序和来源权限绕过均以实际RED驱动修复。无业务附件目录/客户端或真实ClamAV/病毒库/ACL，不关闭以下父DTC/T07。
- [x] [具名仓/位附件读取](../../implementation/165-t07-entity-authorized-file-reads.md)/[证据](../../implementation/166-t07-entity-authorized-file-reads-tests.md)：固定类型/实际精确引用、当前WMS实体及Platform文件权限交集，复用IO后复验/摘要/审计/清零；新增26真实MySQL/21 HTTP，相关Release221含五套既有HTTPS浏览器、架构29、构建0/0通过。无绑定/解绑应用入口、业务附件目录/客户端及真实病毒库/ACL，不关闭以下父DTC或T07。
- [x] PAR-04独立内容校验基础：20 MiB边界、允许类型的扩展名/MIME/基础签名或UTF-8一致性及常见禁用签名检查；Release定向38项通过，详见[实施记录](../../implementation/83-t07-file-content-validation.md)和[测试记录](../../implementation/84-t07-file-content-validation-tests.md)。不包含上传、存储、扫描、Available或T07整包验收。
- [x] 隔离生命周期/存储/协议及持久化基础：扫描attempt/60秒截止、600秒处理问题投影、24小时孤儿删除fence、不可覆盖私有存储、精确字节摘要、loopback ClamAV协议、tenant文件/引用表及Version并发。新增40项本地测试及8项真实MySQL测试通过，包含实际列排序规则及owner大小写隔离，见[设计](../../implementation/93-t07-quarantined-file-lifecycle.md)/[测试](../../implementation/94-t07-quarantined-file-lifecycle-tests.md)。无Host注册、应用协调、实体授权或页面，不关闭以下整体验收；协议监听器不是真实病毒清除证据。
- [x] 内部扫描/孤儿删除协调：持久attempt/删除fence先于外部IO，原60秒截止预算、迟到结果隔离、存储中断与删除后数据库确认失败恢复；新增20项真实MySQL测试含真实私有存储和并发单claim，原文件8项共同回归28项通过，见[设计](../../implementation/101-t07-file-processing-coordination.md)/[测试](../../implementation/102-t07-file-processing-coordination-tests.md)。仍无实体授权/上传/绑定/下载/HTTP/页面，不将受控scanner当真实病毒清除证据，不关闭T07。
- [x] 当前授权暂存文件入口：真实creator/当前目录部门、会话/角色/字段授权，受权扫描/删除及审计提交后下载、读取中撤权/绑定拒绝和未交付缓冲清零。新增21项真实MySQL测试，见[设计](../../implementation/103-t07-authorized-staging-file-access.md)/[测试](../../implementation/104-t07-authorized-staging-file-access-tests.md)。业务绑定文件明确拒绝，不以暂存权限替代实体授权；上传/HTTP/页面及真实扫描仍未验收，不关闭T07。
- [x] 持久受权上传：当前Upload权限/Own范围、永久tenant/operation身份、审计后CreateNew与重新授权确认，保存失败/取消后只核验精确既存字节恢复，不覆盖部分对象；新增23项真实MySQL与4项名字反例，完整1174项回归通过。见[设计](../../implementation/105-t07-durable-authorized-uploads.md)/[测试](../../implementation/106-t07-durable-authorized-uploads-tests.md)。成功仍隔离，不代签业务实体绑定、HTTP流限额/API/页面或真实扫描，T07仍partial。
- [x] 暂存HTTP传输：显式默认关闭的私有根/loopback装配、当前身份、raw流M/M+1、中文名规范UTF-8、受权元数据/审计下载/扫描/到龄孤儿删除；新增46项，真实HTTPS Kestrel及完整1220项回归通过。见[设计](../../implementation/107-t07-staging-file-http.md)/[测试](../../implementation/108-t07-staging-file-http-tests.md)。仅未绑定暂存范围，页面/业务实体授权/真实ClamAV和部署ACL仍待验收，不关闭T07。
- [x] 暂存单文件工作区：当前授权导航、真实字节上传进度/显式原operation重试、隔离读取/扫描、版本确认删除及审计二进制下载；撤权/目标变更与迟到结果门禁。前端220项/严格构建、真实HTTPS Chromium与完整1221项.NET通过，见[设计](../../implementation/109-t07-staging-file-client.md)/[测试](../../implementation/110-t07-staging-file-client-tests.md)。完整文件目录/业务实体授权及真实病毒库/ACL仍未验收，T07不整体关闭。
- [x] 当前授权暂存目录：SQL内逐字段scope交集、creator当前部门/tenant/引用隔离后分页，客户端显式筛选/共享实时分页与选择后重读；新增14 HTTP/8真实MySQL/12客户端、前端240项/严格构建、真实HTTPS Chromium及完整1265项.NET通过。见[设计](../../implementation/113-t07-authorized-staging-catalog.md)/[证据](../../implementation/114-t07-authorized-staging-catalog-tests.md)。业务绑定/附件授权与完整业务文件目录、真实ClamAV/ACL仍未验收，不关闭T07。
- [ ] DTC-PLAT-007/VAL-05先红：20MiB/+1字节、伪MIME/宏文档/禁类型、存储失败/扫描60秒超时、600秒未恢复、在用引用24小时后清理、跨租户下载。
- [ ] 实现类型/签名/UTF-8与禁NUL验证、隔离存储、扫描结果/失败回收与明确处理问题；扫描服务无值不Available，清孤儿需确认无引用。
- [ ] 跑存储中断/外部DB非ACID恢复/API鉴权/页面回填；只存合法文件hash/摘要不存secret，确认既有受权干净文件不被新上传故障误禁。

## B. 仓储与审批业务

### T08：WMS主数据与唯一账本

- [x] [仓库逻辑启停owner/HTTP/具名页面](../../implementation/174-t08-warehouse-operational-state.md)：当前真实creator/完整read与独立Enabled写、父仓版本与永久原回执、活动阻断/提交前撤权回滚；默认关闭PUT、原因/全量预览/确认、原key/body未知恢复和显式GET。新增38owner/28HTTP/8配置/34前端；相关owner527、最终Release本片38及HTTP配置/五真实浏览器41、前端956/严格构建、架构29/Release0/0及模型无漂移通过（重叠不累计）。新表保留完整状态事实、design-time迁移历史与runtime一致；无库存/子位/运输效果，不关闭库位启停/容器/合法账维度、公开审核收货/最终运输或T08/T09整包。

- [x] [禁用库位创建客户端](../../implementation/163-t08-disabled-location-creation-client.md)/[证据](../../implementation/164-t08-disabled-location-creation-client-tests.md)：当前完整write/父仓read、精确tuple预览/二次确认、原key/body未知恢复与显式GET回执，复用原仓库状态机。新增35前端，全套730/严格构建、相关.NET158含五套真实HTTPS浏览器、Release0/0通过，真实201及owner单效果已核验。无真实Pack/完整层级候选链、启停/容器或父DTC/整包验收。

- [x] [当前授权库位创建HTTP](../../implementation/161-t08-authorized-location-creation-http.md)/[证据](../../implementation/162-t08-authorized-location-creation-http-tests.md)：独立默认关闭写开关及唯一具名singleton schema启动门禁、当前五字段/父仓read、严格输入/永久key、原子禁用位置/事实/Outbox。新增65 HTTP/启动及8配置，6项实际RED后最终Release128含四套HTTPS浏览器+238主数据owner+29架构=395通过、构建0/0。无默认schema或真实Pack验收，库位创建表单/启停/层级/容器和父DTC/整包仍缺。

- [x] [当前授权不可变流水](../../implementation/153-t08-authorized-stock-ledger-query.md)/[证据](../../implementation/154-t08-authorized-stock-ledger-query-tests.md)：独立All完整19字段owner/API/客户端，精确维度/来源/微秒UTC半开SQL过滤后稳定时间分页，正负六位字符串与历史前后余额，当前撤权清旧；新增32真实owner/28HTTP/85前端。最终相关.NET185、前端691/严格构建、Release0/0；含按真实375px+200%修共享外壳。不关闭固定核对快照/导出/来源业务跳转、完整生命周期或T08整包。

- [x] [实时库存客户端](../../implementation/147-t08-inventory-query-client.md)/[证据](../../implementation/148-t08-inventory-query-client-tests.md)：独立Wms路由/导航、完整All字段、精确筛选、六位字符串/BigInt校验、分页/撤权/取消/离线；前端581/严格构建、真实HTTP及三套HTTPS浏览器21、架构29通过，Release0/0。固定核对快照/授权流水/导出与完整生命周期仍缺，不关闭父DTC/T08整包。

- [x] [当前授权库存owner/API](../../implementation/145-t08-authorized-inventory-query.md)/[证据](../../implementation/146-t08-authorized-inventory-query-tests.md)：All完整字段、精确tenant维度过滤、同一RepeatableRead余额/保护份额、复用并集规则与六位decimal字符串，无库存写入。新增26真实owner/18HTTP；账本/预留/读取74、HTTP18、架构29通过、Release0/0、无模型漂移。客户端/固定核对快照/导出及T08整包仍缺。

- [x] 仓库创建客户端：当前write字段、预览与二次确认、原键冻结/10秒未知手工恢复、取消清旧和显式GET回执。新增35客户端，全套462/严格构建通过；相关WMS领域/HTTP/真HTTPS只读+写浏览器146、架构29通过，Release零警告错误。[设计](../../implementation/139-t08-disabled-warehouse-creation-client.md)/[证据](../../implementation/140-t08-disabled-warehouse-creation-client-tests.md)。不开放库位创建/Pack/启停/库存，不关闭T08。

- [x] 仓库创建HTTP接线独立默认关闭、当前write字段、精确key/body/201/200/409，复用writer与原子Outbox；新增38真实HTTP/7配置，相关145+架构29通过、Release零警告错误。[设计](../../implementation/137-t08-authorized-warehouse-creation-http.md)/[证据](../../implementation/138-t08-authorized-warehouse-creation-http-tests.md)。位置创建/真实Pack/仓库表单/其他生命周期仍未完成，整包不关闭。

- [x] 仓库/库位创建原语增加同事务owner-local唯一Outbox，相关270通过；见[T05/T08设计](../../implementation/135-t05-t08-wms-creation-outbox.md)/[证据](../../implementation/136-t05-t08-wms-creation-outbox-tests.md)。不改变默认禁用/当前授权/schema边界，Host写入/表单/其他业务Outbox/运输完成仍缺，不关闭T08。

**Files:** `src\Modules\Wms\Domain\{StockBalance.cs,StockEntry.cs,StockProtection.cs,Location.cs,Container.cs}`、`Features\{MasterData\SaveLocation.cs,Inventory\QueryInventory.cs,Ledger\QueryLedger.cs}`、owner EF配置/迁移；`clients\ops-web\src\features\wms\{LocationsPage.vue,InventoryPage.vue,LedgerPage.vue}`；`tests\Modules\Wms\LedgerInvariantTests.cs`。

**Interfaces:** 消费租户/版本/事件/快照协议；产出FUNC-WMS-001/007、PAGE-WMS-001/002/008以及最小授权位置/库存查询与owner写账合同；不存在公开“任意改余额”接口。

- [x] RULE-01纯数量领域foundation：非负`decimal(28,6)`库存数量、精度0..6/单位默认值及正向单笔上限已完成本地单元测试；见[实施记录](../../implementation/85-t08-quantity-domain-foundation.md)与[测试记录](../../implementation/86-t08-quantity-domain-foundation-tests.md)。不关闭本工作包所列账本/Provider/权限/并发验收或T08整包。
- [x] RULE-08保护量并集纯领域投影：同一执行单元Reserved/InTransit/Unknown只扣一次，互不重叠单元相加、重复身份/超量保护拒绝；23项新增测试验证10/7含在途5时Available3及Unknown保护，见[设计](../../implementation/91-t08-stock-protection-union.md)和[测试](../../implementation/92-t08-stock-protection-union-tests.md)。调用owner须证明单元划分；无Host、数据库或预留命令接线，不关闭以下整体门禁。
- [x] WMS owner账本持久化核心：固定维度余额、永久operation/execution身份、不可变entry、精确收发/同维度成对移账、显式事务/双版本CAS、批次精确比较及不完整事实拒绝。新增11项领域和25项真实MySQL测试通过，见[设计](../../implementation/97-t08-owner-ledger-core.md)/[测试](../../implementation/98-t08-owner-ledger-core-tests.md)。仍无主数据/预留生命周期/授权/Outbox/API/页面，不关闭T08。
- [x] WMS内部持久预留与账本保护：执行份额永久身份、部分释放不可变operation、Reserved/InTransit/Unknown并集、共享balance Version竞争/混合水位拒绝，以及Shipment/Transfer原子消费。新增23项真实MySQL测试与原25项账本共48项通过，见[设计](../../implementation/99-t08-durable-stock-reservations.md)/[测试](../../implementation/100-t08-durable-stock-reservations-tests.md)。仍无具名业务授权/运输完成证据、完整主数据、Outbox/API/页面，不关闭以下整体门禁或T08。
- [x] WMS禁用仓库/库位创建基础：默认禁用、不可变仓/Pack/schema、tenant复合FK/code唯一、永久operation/精确创建事实同事务及schema显式拒绝/不可用；14领域+34真实MySQL测试、原账本联合82项和完整1313项通过，见[设计](../../implementation/115-t08-location-master-data.md)/[证据](../../implementation/116-t08-location-master-data-tests.md)。实际列属性反例驱动binary修正；无业务授权/Host/API/页面、编辑启停、容器绑定或Outbox，不关闭以下整体门禁。
- [x] WMS当前授权主数据详情：公开owner边界与独立当前授权端口，WMS解析真实creator，Platform解析当前部门/会话/角色，完整固定字段及resource permission逐次核验；新增22项真实MySQL，联合159项/领域32项/架构29项通过。见[设计](../../implementation/117-t08-current-master-data-reads.md)/[证据](../../implementation/118-t08-current-master-data-reads-tests.md)。Host生产注册/HTTP/列表/页面、创建授权与完整生命周期未完成，不关闭以下整体门禁。
- [x] WMS主数据详情HTTP：默认关闭的显式部署接线，真实Platform authority与既有JWT subject，两个GET及HTTPS/no-store、当前撤权、实际跨tenant、无写入口；6配置+11 HTTP、最终完整1352项通过，见[设计](../../implementation/119-t08-master-data-detail-http.md)/[证据](../../implementation/120-t08-master-data-detail-http-tests.md)。不自动迁移/授予权限，不含列表/树/页面、创建授权、启停/绑定/Outbox，不关闭以下整体门禁。
- [x] WMS只读详情客户端：`/wms/locations`显式UUID读仓/位，模块/当前完整字段门禁、严格投影/迟到丢弃/离线陈旧，38项新增客户端及全套278通过；真实HTTPS/MySQL/Chromium双资源与独立owner撤权后403清旧、三宽度/键盘/无业务缓存通过。完整Modules525+Architecture29共554项、Release零警告/错误；见[设计](../../implementation/121-t08-master-data-detail-client.md)/[证据](../../implementation/122-t08-master-data-detail-client-tests.md)。未重跑Persistence/Route；仅PAGE-WMS-001详情子集，不关闭列表/树、创建/编辑启停/容器、库存查询/Outbox或T08整包。
- [x] WMS当前受权实时目录：两个GET、creator逐字段范围交集、SQL授权后稳定分页/精确筛选、页面选择Id后重读与双侧403清旧；30真实owner/18 HTTP/28客户端，前端306通过及真实HTTPS浏览器通过。完整1399项后补SQL物化迟到取消两反例，修正后145项相关复验及零警告/错误构建；见[设计](../../implementation/123-t08-authorized-master-data-catalog.md)/[证据](../../implementation/124-t08-authorized-master-data-catalog-tests.md)。无迁移/依赖变化；不关闭树、写授权/生命周期、容器、库存/Outbox或完整父DTC。
- [x] WMS当前授权创建应用边界：独立显式writer、可信主体与当前write完整字段、库位父仓read+schema证据、撤权阻断永久operation重放及默认禁用/单效果原子事实；新增38真实owner，联合124+领域/只读HTTP/真实浏览器50+架构29共203通过、构建零警告/错误。见[设计](../../implementation/125-t08-current-authorized-master-data-creation.md)/[证据](../../implementation/126-t08-current-authorized-master-data-creation-tests.md)。无Host写接线/创建表单/真实Pack默认适配器，不关闭完整生命周期或T08整包。
- [ ] DTC-WMS-001/010/011及DTC-WMS-004的并发原语先红：同容器并发绑定、跨仓/Pack、流水重算、十进制超精度/溢出、Qty10/Reserved7内inTransit5时Available3，重复业务键无二效。
- [ ] 实现decimal(28,6)/API decimal string、计件0小数/可分割默认3（显式0..6）、换算默认关、负库存拒绝；普通FIFO/启用效期FEFO完整必需日期、单序列量1；保护按并集不重扣。
- [ ] 三Provider量化/约束/事务和固定快照导出测试，元数据/关联列/盲盘字段投影不泄露；账本append-only补偿，WCS只能消费契约；VAL-01/02/08。

### T09：入库收货与上架

2026-10-27 执行优先级调整：按[基本功能与后置清单](../../implementation/TodoList.md)先验证前置主体程序，再交付本包最小完整软件链路；外部验收不阻断实施，但仍不伪造 Pack/运输到达或绕过库存不变量。三 Provider 要求继续按 ADR-0002 以 MySQL 为当前必需、其余延后。

- [x] [入库领域首片](../../implementation/173-t09-inbound-basic-flow.md)：多行计划/审核/部分收货、原操作/执行身份去重、同址无运输、异址精确最终到达；新增6项，领域118/架构29/Release0/0通过。该片为内部状态机，持久事务、当前授权 HTTP 与具名页面仍待实施，不关闭本包。
- [x] [内部持久收货事务](../../implementation/173-t09-inbound-basic-flow.md)：header/append-only事实/ledger同次提交，永久回执、父单锁+版本防不同balance并发超收，保存后故障/取消全回滚。最终真实账本120、领域118/架构29、Release0/0及模型无漂移；较早联合主数据353通过，重叠不累计。仅内部primitive；仓/位启停/容器/账维度、当前授权HTTP/页面、Outbox运输及精确最终到达持久移账仍待实施，不关闭本包。
- [x] [当前授权只读HTTP/入库详情页](../../implementation/173-t09-inbound-basic-flow.md)：真实creator/部门及完整八字段、读后核验、一致快照/精确数量和待运输；新增23owner/14HTTP/54前端及1真实HTTPS浏览器。最终owner143、Modules137含领域118与五WMS浏览器、前端824/严格构建通过。已知UUID只读不等于目录/多行草稿/审核/收货表单；运营主数据、公开写及最终运输仍待实施，不关闭本包。
- [x] [当前受权实时入库目录](../../implementation/173-t09-inbound-basic-flow.md)：完整八字段creator交集在SQL分页前过滤、读后复验、精确筛选与稳定分页；真实父页选择后重新GET及双侧403清旧。新增24owner/10HTTP/32前端；最终owner167、Modules147（24HTTP/118领域/5真实HTTPS浏览器）、前端856/严格构建、架构29/Release0/0通过，替代上一片相关回归而不重复累计。无新迁移/写入口，运营主数据、草稿/审核/收货写与最终运输仍待实施，不关闭本包。
- [x] [当前受权草稿创建owner/HTTP/多行表单](../../implementation/173-t09-inbound-basic-flow.md)：完整写字段/真实父仓读取与保存后复验回滚、永久原Id-only回执，独立默认关闭POST、严格decimal string、预览确认/同tuple同键Unknown恢复和手工读回。新增46owner/33HTTP/8配置/41前端；最终owner213、相关Modules188含5真实HTTPS浏览器、前端897/严格构建、架构29/Release0/0通过，替代167/147/856而不重复累计。无新迁移/库存/运输效果；修订及来源深化、受权审核/收货、运营主数据与最终运输仍待实施，不关闭本包。

- [x] [当前受权草稿修订owner/HTTP/多行表单](../../implementation/173-t09-inbound-basic-flow.md)：保留原创建Plan、append-only修订历史及原版本回执，真实creator完整写/read及保存后复验回滚；Draft/父单版本守卫与审核串行化、独立默认关闭PUT、严格ExpectedVersion/完整计划，真实创建→修订→另读及Unknown原tuple恢复。新增39owner/34HTTP/8配置/25前端；最终owner252、相关Modules230含5真实HTTPS浏览器、前端922/严格构建、架构29、Release0/0/模型无漂移通过，替代213/188/897、不重复累计。来源深化、运营主数据、受权审核/收货和最终运输仍待实施，不关闭本包。

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

### T08 Task 1数量领域基础局部证据（2026-10-26）

Task 1已完成其隔离的纯领域范围：新增独立WMS项目及`InventoryQuantity`、`QuantityPrecision`、`StockMovementQuantity`，16项定向Release单元测试通过。项目加入solution和Modules测试引用，但不加入Host装配；未创建持久化/迁移、授权、Outbox/Inbox、API或UI。实现与红绿、构建、回归、格式证据见[数量领域实施记录](../../implementation/85-t08-quantity-domain-foundation.md)及[数量领域测试记录](../../implementation/86-t08-quantity-domain-foundation-tests.md)。T08中完整DTC-WMS-004/010/011和数据库/并发范围的复选步骤保持未勾；T08整体以及T04/T05/T06硬依赖均未关闭。
