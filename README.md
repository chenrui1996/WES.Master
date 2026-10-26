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

T05续实施：[托管Platform发布调度](docs/implementation/95-t05-hosted-platform-publisher.md)及[测试证据](docs/implementation/96-t05-hosted-platform-publisher-tests.md)接通默认禁用的显式tenant/batch周期和公共owner发布边界；缺少显式transport时拒绝启用，不提供None/成功fallback。调度器、lease和真实MySQL测试不代替真实Broker、Notifications聚合及T05整体合同。

以下是本轮已整理的文档章节。每份功能规格都有测试设计，并在总追踪矩阵中关联需求 ID、设计章节、测试 ID 和验收结果；测试用例尚非自动化测试结果。

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
