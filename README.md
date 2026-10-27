# WES.Master 项目启动文档

WES.Master 是面向仓储执行与设备调度的新项目。已从文档启动阶段进入**受控实施**：项目发起人批准现有设计作为软件基线，并明确允许在 main 编码。每个切片按文档→失败测试→实现→复测推进。2026-10-23后续授权允许验证后的工作包提交推送到既有GitHub；该授权不自动放行生产连接、设备控制或现场H签字。

## 权威设计

- [项目启动设计规格](docs/00-project-startup-design.md)：已确认的项目目标、业务范围、架构边界、Route 重构方向、工程规范和文档计划。
- [Agent 开发约束](AGENTS.md)：本阶段文档范围及后续实现不可偏离的架构边界。

## 文档工作区

页面/功能深化入口：[全模块页面、功能与详细测试设计](docs/detailed-design/README.md)。包含文本线框、字段/权限/状态、功能流程及逐步骤测试；已批准为软件实施基线，不代表实现或测试通过。

实施排程：[开发与测试总计划](docs/superpowers/plans/2026-10-23-wes-development-and-testing.md)、[34个开发工作包](docs/superpowers/plans/2026-10-23-wes-development-work-packages.md)、[测试执行与证据计划](docs/superpowers/plans/2026-10-23-wes-test-execution.md)。当前进展与阻断见[实施就绪台账](docs/implementation/readiness-register.md)；跨owner接口见[契约基线](docs/contracts/01-business-and-execution-contracts.md)。数据库按[MySQL先行决策](docs/adr/ADR-0002-mysql-first-implementation.md)，其他两种延后验收。

已实现入口：[无数据库Host首片](docs/implementation/01-foundation-host.md)、[MySQL Provider基础切片与复跑命令](docs/implementation/03-mysql-provider-foundation.md)。真实测试仅使用已获授权的wes_test库；原工程凭据只在本机测试装配内存中读取，不进入WES配置或日志。Host尚未装配业务持久化，就绪检查仍返回503；T02整包尚未完成。
当前连续批次已补齐[HTTP运行时与依赖门禁](docs/implementation/04-host-runtime-foundation.md)，并进入[T03会话核心](docs/implementation/05-platform-session-core.md)；精确交付边界、复跑方式与尚未实现项目见[review交接](docs/implementation/06-review-handoff.md)。T02软件基础已可供内部后续使用，不等于完整系统/生产验收；T03尚无可用登录页面或签发API。

新增内部实现：[T04当前权限、租户、配置及审计基础](docs/implementation/08-platform-access-settings-audit.md)、[T17 Pack私有Route内核记录](docs/implementation/07-route-kernel.md)。完整状态仍以[就绪台账](docs/implementation/readiness-register.md)为准；内部算法/持久化通过不等于已交付64个页面、全部T04–T34或真实设备调度。

身份链路续实施：[T03可信来源耐久登录准入](docs/implementation/09-durable-login-source-admission.md)，已用真实MySQL验证跨账号/租户的30次/60秒预算与并发，不放宽业务租户上下文，也不代表账号/验证码/JWT/登录页面已经装配。新增专用来源DbContext后EF CLI须显式指定`--context`，具体命令见实现记录。

账号风险续实施：[T03耐久账号失败窗口与暂锁](docs/implementation/10-durable-account-login-risk.md)，已验证租户内3后验证码门槛、5后锁900秒及版本冲突保护；只是内部风险状态，不将其当作完整原子登录或会话签发许可。

验证码续实施：[T03验证码耐久一次消费](docs/implementation/11-durable-login-challenges.md)，已验证120秒有效期、绑定账号/来源的正确与错误一次消费、并发CAS及答案MAC保护；不含可用图片渲染、生产秘密装配或完整登录页面。

会话续实施：[T03当前会话登出与原子审计](docs/implementation/12-audited-current-session-logout.md)，已验证只撤当前自有family、审计与撤销原子提交、幂等及refresh两方向竞争；不替代HTTP适配或改密/禁用撤全部会话。

凭据续实施：[T03 Seven兼容密码编码器](docs/implementation/13-compatible-password-codec.md)，新设SHA384增强BCrypt不截断128个Unicode标量，旧Seven模式只返回受限验证；全量545项通过，不替代账号目录、历史/泄漏证据服务或完整登录。

改密续实施：[T03耐久凭据历史与受控改密](docs/implementation/14-durable-password-change.md)，最近5个已设置值含当前值、真实历史验证、当前主体旧密/版本复验，凭据与既有自有family撤销/审计原子提交；全量582项通过。外部泄漏检查、账号目录/禁用、并发新family签发栅栏及HTTP/页面仍待接线。

登录续实施：[T03耐久登录协调与签发栅栏](docs/implementation/15-coordinated-password-login.md)，统一来源/账号/一次性挑战/真实凭据核验，risk与凭据CAS后才提交family和安全事件；旧格式会话持久受限，已关闭login/改密的新family竞态。全量613项通过，尚未公开HTTP/JWT或可用登录页面。

管理续实施：[T03/T04当前授权禁用用户](docs/implementation/16-audited-account-disable.md)，实时复验Enabled字段/资源范围，禁用凭据、全部目标family撤销与版本审计显式原子提交；login/refresh两方向竞争及真实审计故障回滚已验证，最新全量638项通过。用户目录、公开API/页面及整包验收仍未完成。

查询续实施：[T03/T04受权登录安全事件查询](docs/implementation/17-authorized-login-event-query.md)，当前All范围/字段权限、时间/结果/关联筛选后有界分页；Own/部门无不可变主体归属时拒绝，隐藏字段不能作为查询oracle。24项真实MySQL查询测试，最新全量662项通过；保留/归档、导出、HTTP和页面仍待完成。

T04/T05/T06/T08续实施：[批次计划](docs/superpowers/plans/2026-10-26-platform-retention-candidates-stock-protection.md)分别跟踪Platform目录回执/Inbox的可选租户级清理、关联候选撤权/旧响应边界，以及WMS执行单元保护量并集。清理默认关闭，不包含审计删除或真实Broker；WMS仍为未装配Host的纯领域基础。验证结果和未关闭项以[就绪台账](docs/implementation/readiness-register.md)为准，不将局部切片等同四个工作包整体完成。

T07续实施：[隔离文件生命周期与存储/扫描基础](docs/implementation/93-t07-quarantined-file-lifecycle.md)及[测试证据](docs/implementation/94-t07-quarantined-file-lifecycle-tests.md)新增精确字节证据、扫描attempt/截止、删除fence、本地私有存储、loopback ClamAV协议和MySQL文件/引用表。协议测试不是病毒清除，持久化约束不替代实体授权；应用协调、API、页面和真实扫描仍待完成，不能据此关闭T07。

T07协调续实施：[持久扫描与孤儿删除协调](docs/implementation/101-t07-file-processing-coordination.md)及[测试证据](docs/implementation/102-t07-file-processing-coordination-tests.md)接通内部attempt/删除fence、外部IO及确认CAS；慢claim不延长60秒截止，迟到结果不覆盖新attempt，存储/墓碑保存故障保留可恢复状态。真实私有存储与MySQL联动已测，仍无受权上传/绑定/下载或HTTP/页面，真实病毒清除验收继续阻断。

T07权限续实施：[当前授权的暂存文件入口](docs/implementation/103-t07-authorized-staging-file-access.md)及[测试证据](docs/implementation/104-t07-authorized-staging-file-access-tests.md)接通元数据、扫描/删除授权与审计后下载；真实creator/当前部门/会话/字段范围复验，读取中撤权或绑定不交付内容，未交付缓冲清零。仅限未绑定暂存文件，业务附件明确拒绝；上传、业务实体授权、HTTP/页面与真实扫描仍待完成。

T07上传续实施：[持久受权上传](docs/implementation/105-t07-durable-authorized-uploads.md)及[测试证据](docs/implementation/106-t07-durable-authorized-uploads-tests.md)新增永久tenant/operation身份、创建审计后不可覆盖写、精确字节确认与恢复；同operation换actor/名字/内容拒绝，真实20MiB边界成功仍隔离。完整串行回归1174项通过；业务实体绑定、HTTP流限额/API/页面和真实病毒清除仍待验收，不关闭整包。

T07传输续实施：[暂存文件HTTP](docs/implementation/107-t07-staging-file-http.md)及[测试证据](docs/implementation/108-t07-staging-file-http-tests.md)接通默认关闭的受权上传/元数据/下载/扫描/孤儿删除；私有根和loopback端点须显式配置，未知长度最多读M+1判界，中文名规范UTF-8转义，下载审计后attachment交付且清零。真实HTTPS Kestrel验证20MiB/+1与中文往返，46项新增测试、完整串行1220项通过；页面、业务实体绑定与真实扫描/ACL仍待验收，整包未关闭。

T07客户端续实施：[暂存文件工作区](docs/implementation/109-t07-staging-file-client.md)及[证据](docs/implementation/110-t07-staging-file-client-tests.md)接通真实上传进度、当前授权读取/扫描/审计下载/版本删除、显式原operation重试和撤权/目标变更清理。前端220项及严格构建、真实HTTPS Chromium文件交互和完整.NET 1221项通过；不是全局文件目录，业务实体绑定、真实病毒库与部署ACL仍未验收，T07保持partial。

T04导出续实施：[当前授权固定审计快照导出](docs/implementation/111-t04-authorized-audit-export.md)及[证据](docs/implementation/112-t04-authorized-audit-export-tests.md)补齐独立export字段授权、显式列CSV、固定成员/600秒TTL、永久操作防重和审计/Outbox提交后交付。8项HTTP、14项真实MySQL owner、前端228项及真实HTTPS Chromium下载通过；最终当前编译源的完整.NET 1243项通过。既有1秒依赖健康探测曾波动，未隐藏失败；T04/T06整体及其他工作包仍未关闭。

T07目录续实施：[当前授权暂存目录](docs/implementation/113-t07-authorized-staging-catalog.md)及[证据](docs/implementation/114-t07-authorized-staging-catalog-tests.md)补齐SQL内逐字段Own/部门/All交集、引用隔离后实时分页、状态筛选和目录选择后重读详情。新增14项HTTP/8项真实MySQL/12项客户端，前端240项/严格构建及真实HTTPS Chromium通过；全solution构建0警告/0错误，完整.NET 1265项通过。业务文件绑定/附件授权、真实ClamAV与部署ACL仍未验收，五个工作包不整体关闭。

T05续实施：[托管Platform发布调度](docs/implementation/95-t05-hosted-platform-publisher.md)及[测试证据](docs/implementation/96-t05-hosted-platform-publisher-tests.md)接通默认禁用的显式tenant/batch周期和公共owner发布边界；缺少显式transport时拒绝启用，不提供None/成功fallback。调度器、lease和真实MySQL测试不代替真实Broker、Notifications聚合及T05整体合同。

T08续实施：[WMS owner账本核心](docs/implementation/97-t08-owner-ledger-core.md)及[测试证据](docs/implementation/98-t08-owner-ledger-core-tests.md)新增独立余额/永久记账身份/不可变流水、精确收发和成对移账事务、版本与业务身份防重。内部记账原语没有Host/API接线，不代替实体授权、主数据、保护量持久化与完整业务用例；T08仍未整体关闭。

T08保护续实施：[持久预留与账本保护](docs/implementation/99-t08-durable-stock-reservations.md)及[测试证据](docs/implementation/100-t08-durable-stock-reservations-tests.md)把保护量并集接入WMS独立事务：部分释放永久防重、Unknown不释放、预留与记账共享余额版本、确认消费与成对流水原子提交；跨余额execution冒用明确拒绝。仍是未装配Host的内部owner原语，不代表完整业务授权、主数据、Outbox或T08整体验收。

以下是本轮已整理的文档章节。每份功能规格都有测试设计，并在总追踪矩阵中关联需求 ID、设计章节、测试 ID 和验收结果；测试用例尚非自动化测试结果。

T08主数据续实施：[禁用仓库/库位创建基础](docs/implementation/115-t08-location-master-data.md)及[证据](docs/implementation/116-t08-location-master-data-tests.md)补齐不可变仓/Pack/schema归属、tenant复合约束、永久operation与创建事实同事务。新增14项领域/34项真实MySQL测试通过，实际列属性反例驱动修正Provider忽略binary注解问题；完整.NET **1313项通过**、Release构建0警告/0错误。未装配业务授权/Host/API/页面或启停/容器绑定/Outbox，T08及其他整包仍未关闭。

T08授权详情续实施：[当前主数据读取](docs/implementation/117-t08-current-master-data-reads.md)及[证据](docs/implementation/118-t08-current-master-data-reads-tests.md)以WMS创建事实和Platform当前会话/角色/creator部门衔接公开应用边界，Own/Departments/All逐次复验完整字段，无私表跨引用。新增22项真实MySQL联动，联合159项、领域32项及架构29项通过；构建零警告/错误。尚无Host生产注册/HTTP/列表/页面或创建授权，不把应用详情基础当作T08整体关闭。

T08详情HTTP续实施：[默认关闭的只读接线](docs/implementation/119-t08-master-data-detail-http.md)及[证据](docs/implementation/120-t08-master-data-detail-http-tests.md)接通真实Platform当前授权适配器、既有JWT subject和仓/位两个GET。新增6项配置/11项HTTP，最终完整.NET **1352项通过**、构建零警告/错误；跨tenant、撤权/登出、HTTPS/no-store和无写入口已测。仅显式部署装配，不自动迁移/授予权限；列表/树/页面、创建授权、完整主数据生命周期及T08整包仍未完成。

T08只读页面续实施：[仓库/库位详情客户端](docs/implementation/121-t08-master-data-detail-client.md)及[证据](docs/implementation/122-t08-master-data-detail-client-tests.md)在`/wms/locations`提供显式UUID详情查询，双层模块/字段门禁、严格投影、目标/主体切换与迟到丢弃、离线陈旧且不重放。新增38项客户端用例，前端全套**278 passed**及严格构建通过；真实HTTPS/MySQL/Chromium验证双资源与当前字段撤权403清除旧详情、三种宽度/键盘/无业务缓存。完整Modules525+Architecture29=**554 passed**，Release构建零警告/错误；本片未重跑Persistence/Route。仅详情子集，列表/树、创建授权、启停/容器绑定/Outbox与五个整包仍不关闭。

T08目录续实施：[当前授权仓库/库位目录](docs/implementation/123-t08-authorized-master-data-catalog.md)及[证据](docs/implementation/124-t08-authorized-master-data-catalog-tests.md)新增两个GET及实时发现入口：Platform逐字段范围交集解析真实creator，WMS在tenant内先授权过滤再排序分页，无total或私表跨引用。新增30项真实owner、18项HTTP、28项客户端；前端全套**306 passed**及严格构建，真实HTTPS Chromium目录选取后重读与双向撤权清旧通过。完整.NET **1399 passed**后补SQL物化完成时取消反例，修正后全部主数据/HTTP/浏览器/架构**145 passed**，不叠加冒充一次全套；Release零警告/错误。树、授权创建/写生命周期、库存查询/Outbox及整包仍未完成。

T08创建授权续实施：[默认禁用创建应用边界](docs/implementation/125-t08-current-authorized-master-data-creation.md)及[证据](docs/implementation/126-t08-current-authorized-master-data-creation-tests.md)新增显式writer，真实当前角色/会话/字段及creator当前部门复验后才执行永久operation；库位另需当前父仓read与显式schema证据，撤权也阻断重放且不泄漏旧Id/Version。新增38项真实owner，最终联合主数据124、领域/只读HTTP/真实浏览器50、架构29共**203 passed**，构建零警告/错误。无Host写端点/表单、默认schema/权限、迁移或依赖变化；T08完整生命周期与五包未关闭。

T05诊断续实施：[Platform消息只读客户端](docs/implementation/127-t05-platform-message-diagnostics-client.md)及[证据](docs/implementation/128-t05-platform-message-diagnostics-client-tests.md)接通现有受权GET，明确仅Platform、Broker确认不代表业务完成，当前All字段门禁/实时分页/撤权清旧/离线不重读。前端346项及严格构建通过，真实HTTPS Chromium+HTTP3项、MySQL查询1项通过。跨owner聚合、真实Broker和重放表单仍未完成，不关闭T05或五个整包。

T05最新续片：[Platform受权重放客户端](docs/implementation/129-t05-authorized-message-replay-client.md)/[证据](docs/implementation/130-t05-authorized-message-replay-client-tests.md)：准备前重读、理由/expected/原operation预览与二次确认，未知结果冻结原键不自动重发；前端379、严格构建、真实HTTPS浏览器/HTTP3和MySQL重放2通过。跨owner/过滤/Inbox计数/真实Broker及整包仍未完成。

T05筛选续片：[状态与UTC创建时间查询](docs/implementation/131-t05-filtered-message-diagnostics.md)/[证据](docs/implementation/132-t05-filtered-message-diagnostics-tests.md)在owner SQL先筛选后分页，客户端清旧/准备预读保留筛选与精确时间文本，原GET无新参数兼容。前端396/严格构建、HTTP/真实HTTPS浏览器13、MySQL18、架构29通过，Release零警告错误。跨owner/Inbox统计/真实Broker及整包仍未完成。

T05计数续片：[Platform owner消息计数](docs/implementation/133-t05-owner-message-counts.md)/[证据](docs/implementation/134-t05-owner-message-counts-tests.md)新增显式All统计字段授权的Outbox四状态与现存Inbox收据API/页面，失败不转零、重放后标陈旧且手工重读。前端427/严格构建、MySQL38、HTTP/真实HTTPS浏览器21、架构29通过；不表示统一快照/业务效果数/Broker健康，跨owner及整包仍未完成。

| 文档组 | 内容 |
|---|---|
| 项目治理 | [业务范围与现状矩阵](docs/01-business-scope-and-status.md)、[系统架构](docs/02-system-architecture.md)、[工程/注释/文档规范](docs/03-engineering-governance.md)、[需求追踪矩阵](docs/04-requirement-design-test-traceability.md)、[ADR](docs/adr/README.md) |
| Platform | [身份认证、RBAC、数据权限、多租户、配置、文件、审计与安全](docs/modules/platform-and-security.md) |
| Workflow/Form | [审批与动态表单](docs/modules/workflow-and-forms.md)、[Quartz/消息/outbox/邮件/告警与通知](docs/modules/notifications-and-jobs.md) |
| WMS/PDA | [WMS 与库存账本](docs/modules/wms-and-ledger.md)、[PDA](docs/modules/pda.md)：入库、出库/拣选/发运、盘点、移库；UniApp 离线只读 |
| Orchestration/WCS | [Orchestration](docs/modules/orchestration.md)：跨包运输与以 2D 监控地图为主入口的 WCS 执行/运维控制；[Stacker](docs/modules/wcs-stacker.md)、[FourWay](docs/modules/wcs-fourway.md)：包内执行与命令最终校验；[外部 WCS/设备适配器](docs/modules/external-wcs-and-device-adapters.md) |
| 环境搭建、仿真与现场可视化 | [DeviceComm](docs/modules/device-communication.md)、[环境/仿真/SCADA](docs/modules/environment-simulation-and-scada.md)：设备接入环境、地图 Deploy、仿真测试、Promote 真机切换与 SCADA 状态投影；SCADA 是地图数据源，不拥有运维控制 |
| Route 专项 | [现状与论文适配分析](docs/route/01-as-is-and-research-review.md)、[目标规划/调度](docs/route/02-target-routing-and-scheduling.md)、[测试目录与基准协议](docs/route/03-route-test-catalog.md) |
| 验收 | [跨模块 E2E、Provider 兼容、非功能测试、部署与可追踪验收矩阵](docs/acceptance/01-cross-module-and-provider-matrix.md) |

## 基线参考

- [Seven.Master 文档索引](../Seven.Master/doc/README.md)
- [Seven.Master WMS/WCS 实现说明](../Seven.Master/doc/20-WMS与WCS实现说明.md)
- [Seven.Master 功能开关](../Seven.Master/doc/14-功能开关.md)
- [Seven.Master 测试方法](../Seven.Master/doc/keypoint/09-测试方法与落库回归.md)
- [系统架构设计参考](../系统架构设计.md)
- [Route 研究资料目录](../Route)

## 当前软件复跑

在仓库根目录运行；真实MySQL测试需要本机已获授权的隔离库`wes_test`及[安全连接来源](docs/implementation/02-mysql-isolated-test-environment.md)，不要将凭据放进命令、配置或测试输出。

```powershell
dotnet restore WES.slnx --locked-mode
dotnet build WES.slnx --configuration Release --no-restore
dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1
dotnet format WES.slnx --no-restore --verify-no-changes
```

`-m:1`将依赖同一共享MySQL实例的项目测试顺序执行，不改变生产健康探针期限。Route内部纯逻辑测试不连接设备，Host默认就绪门禁仍保持503；运行测试不会解除业务/现场许可。失败输出必须处理，不能用InMemory或跳过测试代替真实成功。

## 设计原则

1. 以 Seven.Master 当前代码、文档、测试可证明的功能作为保留基线；对未实现、部分实现、预留和新增能力分别标记。
2. WES.Master 的功能设计和测试用例是后续实现的前置依据。任何功能变更都必须同步更新设计、追踪关系和验收用例。
3. 业务接口和单据扩展向上稳定；设备协议和供应商差异向下由适配器隔离。
4. WMS 是库存账本权威；各 WCS 包拥有自己的路网、交通与设备模型；跨包运输通过编排契约。
5. 文档先行，按依赖实施真实功能；不批量生成空工程、模板CRUD或无测试证据的“完成”。
