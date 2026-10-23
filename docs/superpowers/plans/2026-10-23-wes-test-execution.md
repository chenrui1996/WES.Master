# WES 测试执行与验收 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans after implementation approval. Steps use checkbox (`- [ ]`) syntax for tracking. A test design is not a test result.

**Goal:** 将167条父DTC及其参数/风险变体落实为可执行、可选择、可追溯、按环境分别判定的测试与现场验收证据。

**Architecture:** owner行为优先，真实Provider事务与实际模块连通验证；纯算法oracle、虚拟设备、协议台架、UI和现场分层取证，不互相代替。

**Tech Stack:** .NET 10测试工程、WebApplicationFactory、受控TimeProvider、真实SQL Server/PostgreSQL/MySQL；Playwright延续Seven，组件测试按T06采用最小Vitest/Vue Test Utils。Testcontainers只作为隔离环境实现选项，准确引擎/镜像版本和运行时在T02验证，不用skill示例版本直接作生产支持结论。

**Spec:** [共用测试标准](../../detailed-design/00-shared-page-and-test-standard.md)、[密集交互合同](../../detailed-design/03-dense-page-interaction-contract.md)、[13条跨模块步骤](../../detailed-design/02-cross-module-scenarios.md)、[规则/参数/变体](../../detailed-design/05-approved-rules-and-defaults.md)、[Provider验收矩阵](../../acceptance/01-cross-module-and-provider-matrix.md)。

## Global Constraints

- 状态：实施中。用例初始designed，只有实际切片结果可以更新；首片结果见[Host实施记录](../../implementation/01-foundation-host.md)，不覆盖父用例完整状态。
- 按[ADR-0002](../../adr/ADR-0002-mysql-first-implementation.md)，B层首期以真实MySQL为必需，SQL Server/PostgreSQL单列deferred。保留原完整兼容矩阵，不将deferred当passed。
- AAA分开；测试assert行为/持久化和副作用，不断言实现方法调用次数替代业务结果，也不只断言HTTP200。
- B逐SQL Server/PostgreSQL/MySQL，C逐批准Adapter/协议版本，PDA E逐批准OS/UniApp/终端，H单独批准现场；InMemory、SQLite、虚拟Adapter不证明B/C真实/H。
- 原规则基线定义的边界为`[issuedAt, expiresAt)`，到边界即过期；使用受控钟，不用Thread.Sleep测10分钟/90天。
- 高后果拒绝需同时断言业务效果0/设备写0、原状态和资源保护不变、安全原因/审计可查。不能通过自动重试把首次失败藏掉。
- 只有实际执行且适用环境/断言全绿才passed；环境缺失blocked、尚未运行not-run、断言失败failed；waived不能覆盖安全/账本/租户/Unknown等不变量。

---

## 1. 层次、夹具与执行位置

| 层次 | 执行位置（批准实施后创建） | 主要内容 | 不足以证明 |
|---|---|---|---|
| U 纯逻辑/性质 | `tests\Modules\` | 数量、状态机、有限流程、Dijkstra/图/环oracle、aging | DB锁/事务/协议/物理安全 |
| B API+真实持久化 | `tests\Modules\`，WebApplicationFactory | HTTP绑定/认证/业务/DB、并发唯一键、事务故障、Outbox/Inbox/lease | 仿真/真实设备是否执行 |
| C 契约 | `tests\Contracts\` | 实际模块契约、虚拟/批准真Adapter、vendor sandbox、断连/验签/反馈 | 未测协议版本、物理安全 |
| E 组件/浏览器/PDA | 三client内组件测试、`tests\EndToEnd\specs\` | 状态/列/关联/权限、扫码、断网、地图可达性/回读 | DB原子性、其他OS/扫码枪 |
| S 确定性仿真 | Modules/Contracts/EndToEnd | 固定seed/clock/地图/hash、任务、反馈/故障 | 生产几何/制动/容量/安全认证 |
| PERF 负载/计量 | `tests\Performance\` | 各次分位、错误/超时/队列、正确性oracle和恢复目标 | 其他硬件、无限规模、无条件活性 |
| H 现场 | 获批台架/设备/终端、正式runbook | 真实能力/网络/停机/恢复、责任人签署 | 未签的设备/区域/负载扩大 |

统一夹具：T0=2026-10-23T08:00:00Z，TA/TB、WA/WB、U1业务/U2只读/U3他租户，审批/管理员另设明确不同主体；每个CaseId/variant/Provider有唯一RunId与业务键。属性测试保存seed和最小反例；并发用barrier控制提交点/先后，不用随机sleep赌竞态。

测试环境只允许隔离DB、测试Broker/Redis、虚拟或批准台架endpoint；fixture启动先验环境/身份/网络allowlist和禁生产出口，验证失败即停止测试。真实设备H不由日常CI自动打开写连接。秘密仅测试密钥引用，报告不得出现token、完整payload、个人信息或连接串。

C0清理：停止本次publisher/job与故障注入、撤销会话、复原时钟、还原隔离库/测试租户。C1清理先核无在途物理动作/保持Unknown保护并按批准手册安全恢复，再做C0；不得为让测试“干净”删除未知占用。

## 2. 精确执行入口与红绿步骤

以下是T02/T06应交付的入口约定，不是当前已有命令。命令从仓库根Windows PowerShell执行；相关build task存在时先用VS Code task构建。没有入口/依赖则完成对应建工程任务，不能静默换runner。

| 入口 | 拟创建文件/脚本 | 约定 |
|---|---|---|
| Architecture | `tests\Architecture\Wes.Architecture.Tests.csproj` | 扫实际生产项目依赖，负夹具不计入生产图 |
| Modules | `tests\Modules\Wes.Modules.Tests.csproj` | `[Trait("CaseId","DTC-WMS-004")]`等；三个Provider各有fixture测试类标`[Trait("Provider","PostgreSQL")]`等、复用断言而隔离状态；不假定xUnit v2单Theory数据行可直接附独立trait。每变体单独结果 |
| Contracts | `tests\Contracts\Wes.Contracts.Tests.csproj` | CaseId/Adapter/ProtocolVersion/Environment逐项选择 |
| E2E backend | `tests\EndToEnd\Wes.EndToEnd.Tests.csproj` | DTC-XMOD等实际owner链路与恢复，不替换业务handler |
| Browser | `tests\EndToEnd\package.json` | `test:e2e`执行Playwright；测试标题带父CaseId/PAGE/variant；配置从受控测试装配取得endpoint |
| Components | 三客户端各自`package.json` | `typecheck`=`vue-tsc --noEmit`、`test:unit`=适配该client平台的Vitest入口；PDA平台类型声明/组件API按真实UniApp版本验证，不强行跑DOM替代原生 |
| Performance | `tests\Performance\Wes.Performance.Tests.csproj` | 记录环境清单/样本/分位/完整失败，不跟普通单测共用计时环境 |

首次相关工作包创建入口时同时写一项可观察失败测试；应用API尚无则先建立可编译测试合同/host，再看“行为断言失败”，不能把NuGet错误/DB连接错当业务红灯。

典型WMS切片命令（前置：T02与该工作包测试已实现，fixture从批准secret取得准确Provider连接）：

```powershell
dotnet build .\WES.slnx
dotnet test .\tests\Modules\Wes.Modules.Tests.csproj --no-build --filter "(CaseId=DTC-WMS-004|CaseId=DTC-WMS-005)&Provider=PostgreSQL" --logger "trx;LogFileName=wms-postgresql.trx" --results-directory .\artifacts\tests\wms-postgresql
dotnet test .\tests\Architecture\Wes.Architecture.Tests.csproj --no-build --logger trx
npm --prefix .\clients\ops-web run typecheck
npm --prefix .\clients\ops-web run test:unit -- --run
npm --prefix .\tests\EndToEnd run test:e2e -- --grep "DTC-WMS-004|DTC-WMS-005"
```

SQL Server/MySQL分别用`Provider=SqlServer`、`Provider=MySql`执行同CaseId选择器及独立结果目录，不把字符串名称混用造成0测试。每个命令须检验发现case数>0及预期矩阵行数，0 discovered直接失败；三Provider可受资源配额约束并行，但不能共享可变库/业务键。

- [ ] 从设计表拷取该父DTC的固定前置/编号动作/唯一预期，写真实可执行test；先只运行此CaseId/变体/Provider，记录正确断言红灯。
- [ ] 实现最小切片，复跑同选择器，应从该断言失败变通过；同时跑相关拒绝/故障变体与架构门禁。
- [ ] 加上其他两Provider/适用Adapter及页面状态，日志留initial/final owner数据、账本/Outbox/物理写计数；不再依赖手工点通一个页面。
- [ ] 更新CaseId→自动化测试名/路径/variant/环境→RunId/result/evidence，缺项未通过；PR不允许0测试选择器或仅截图。

大范围构建只在涉及工程/契约/迁移后执行；纯UI改动跑该client typecheck/组件/相关页面，领域改动跑模块+真实DB+相关跨模块，避免每个微步骤都跑全负载。恢复/性能/H按阶段执行但不省发布门禁。

## 3. 全模块设计覆盖与责任

此表按唯一ID展开计数，不重复算参数变体。开发任务只是责任入口，详细步骤/字段仍读源设计，不以表格替代规格。

| Domain | 设计源 | PAGE范围 | FUNC范围 | DTC范围 | 开发责任 |
|---|---|---|---|---|---|
| PLAT | [Platform](../../detailed-design/platform-and-security.md) | PAGE-PLAT-001..008 | FUNC-PLAT-001..006 | DTC-PLAT-001..014 | T03,T04,T07 |
| WF | [Workflow](../../detailed-design/workflow-and-forms.md) | PAGE-WF-001..004 | FUNC-WF-001..004 | DTC-WF-001..009 | T12,T13 |
| NTF | [Notifications](../../detailed-design/notifications-and-jobs.md) | PAGE-NTF-001..006 | FUNC-NTF-001..005 | DTC-NTF-001..009 | T05,T14 |
| WMS | [WMS](../../detailed-design/wms-and-ledger.md) | PAGE-WMS-001..008 | FUNC-WMS-001..007 | DTC-WMS-001..011 | T08,T09,T10,T11 |
| PDA | [PDA](../../detailed-design/pda.md) | PAGE-PDA-001..006 | FUNC-PDA-001..006 | DTC-PDA-001..010 | T29 |
| BUS | [Orchestration](../../detailed-design/orchestration.md) | PAGE-BUS-001..005 | FUNC-BUS-001..005 | DTC-BUS-001..010 | T15,T28 |
| STK | [Stacker](../../detailed-design/wcs-stacker.md) | PAGE-STK-001..003 | FUNC-STK-001..004 | DTC-STK-001..007 | T20,T21 |
| FW | [FourWay](../../detailed-design/wcs-fourway.md) | PAGE-FW-001..003 | FUNC-FW-001..005 | DTC-FW-001..007 | T22,T23 |
| ADP | [External](../../detailed-design/external-wcs-and-device-adapters.md) | PAGE-ADP-001..003 | FUNC-ADP-001..004 | DTC-ADP-001..007 | T24 |
| DEV | [Device](../../detailed-design/device-communication.md) | PAGE-DEV-001..004 | FUNC-DEV-001..004 | DTC-DEV-001..007 | T16,T25 |
| SITE | [Simulator/SCADA](../../detailed-design/environment-simulation-and-scada.md) | PAGE-SITE-001..006 | FUNC-SITE-001..005 | DTC-SITE-001..010 | T26,T27,T34 |
| ROUTE | [Route](../../detailed-design/route.md) | PAGE-ROUTE-001..005 | FUNC-ROUTE-001..006 | DTC-ROUTE-001..014 | T17,T18,T19,T33 |
| SYS | [系统](../../detailed-design/system-operations.md) | PAGE-SYS-001..003 | FUNC-SYS-001..004 | DTC-SYS-001..007 | T02,T30 |

合计64 PAGE/65 FUNC/122模块DTC；另DTC-UI-001..032共32、DTC-XMOD-001..013共13，全部167。原265 TC从详细表的原TC字段保留，不重编号，也不把变体数冒充父用例数。

### 3.1 模块DTC的首个落地任务

| 首责Task | 父DTC | 验证焦点 |
|---|---|---|
| T03 | DTC-PLAT-001..003/010 | 身份/并发refresh/撤销/预算 |
| T04 | DTC-PLAT-004..006/008..009/011..014 | RBAC/租户/配置/审计/长期领域键/关联 |
| T07 | DTC-PLAT-007 | 上传/扫描/引用/清理 |
| T05 | DTC-NTF-001..002 | 本地业务Outbox/Inbox/重试/事实与控制 |
| T14 | DTC-NTF-003..009 | Job/告警/渠道/公告/缓存/积压 |
| T08 | DTC-WMS-001/010..011 | 库存/账本/主数据/精度/关联 |
| T09 | DTC-WMS-002..003 | 收货/上架及最终回写 |
| T10 | DTC-WMS-004..006 | 竞争预留/拣选短缺/取消 |
| T11 | DTC-WMS-007..009 | 局部锁/多行审核/部分实物 |
| T12 | DTC-WF-001/008 | 定义/表单版本/校验 |
| T13 | DTC-WF-002..007/009 | 实例竞争/hook/候选/撤回/关联 |
| T15 | DTC-BUS-001..004 | 订单/Leg/交接/取消/迟到 |
| T28 | DTC-BUS-005..010 | 地图/审批/问题/责任/关联 |
| T16 | DTC-DEV-001..007 | 虚拟语义/规则/耐久执行；T25逐批准真实驱动重跑全部 |
| T17 | DTC-ROUTE-001..002 | 图/候选/独立oracle |
| T18 | DTC-ROUTE-005..006 | 全量grant/物理冲突/竞争事务 |
| T19 | DTC-ROUTE-003..004/007..012/014 | 跨版/反馈/恢复/等待/关联 |
| T33 | DTC-ROUTE-013 | 属性部分T17先跑；定量基准本任务结论 |
| T20 | DTC-STK-001..003 | 分配/双深/请求点 |
| T21 | DTC-STK-004..007 | 挡路/段/恢复/关联 |
| T22 | DTC-FW-001/003 | 层分配/停车/公平 |
| T23 | DTC-FW-002/004..007 | 交通/提升/恢复/关联；006性能T33最终验证 |
| T24 | DTC-ADP-001..007 | 配置/出站/验签/Unknown/关联 |
| T26 | DTC-SITE-001..004 | 校验/prepare/active CAS/跨版；007软件拒绝先测 |
| T27 | DTC-SITE-005..006/009..010 | 仿真/Reset/SCADA/关联 |
| T34 | DTC-SITE-007..008 | Promote全部阶段/中断回退，007软件部分T26已测不代替现场 |
| T29 | DTC-PDA-001..010 | 扫码/仓储/离线/operation/终端 |
| T02 | DTC-SYS-001/006 | 健康/架构底座，后续全程重跑 |
| T30 | DTC-SYS-002..005/007 | 隔离恢复/单写/迁移升级/重启/关联 |

首责唯一使遗漏可查；跨域/真实性层次的重复运行是有意回归，不重复统计父DTC。T31迁移变体挂相关SYS/WMS父DTC，保留源业务范围证据，不能因此新增未立项业务。

### 3.2 共用UI与64页适用性

- T06首责DTC-UI-001..028/032；T28首责DTC-UI-029地图；T29首责DTC-UI-030扫码；T26首责DTC-UI-031长操作。UI32为通用回归，模块关联DTC不能替代它。
- T32为每个PAGE生成一条`PageId、配置G/F/R/M/W/P/S、UI CaseId、适用/不适用理由、variant、环境、结果、证据`的展开记录；64个PAGE必须各有行，且每个适用组合各有结果，不能只证明64这个数量。
- 必测列显隐/顺序/宽度/固定/默认恢复/偏好隔离、关联名称+稳定码/历史快照/0和false、级联父切换/旧回包/编辑回填/跨行竞态、已批多选/汇总/导出边界。
- 非Grid/PDA/登录/SCADA地图画布不增加桌面表操作；无导出/批控的页测试“不存在且服务端拒绝非法绕过”。隐藏列不是字段授权，盲盘禁字段不进响应/schema。
- 200%字体、中文/+40%翻译、键盘/非拖动替代、触控≥44px、可见焦点/返焦点、长码/单位/null、局部滚动及768/1024/1440按设计适用执行。PDA目标端按实机，不以桌面视口宽度替代。

## 4. RVT/VAL与规则回归门禁

所有29 RULE已在规则基线表关联正式DTC，T32须逐条核对规则→父DTC→执行变体；P表11组参数必须验默认、明确覆盖、非法值显式拒绝、版本绑定/来源，不只验一份环境配置。

| 风险变体 | 首验任务 | 必须观察到的结果 |
|---|---|---|
| RVT-01 | T18,T19,T26 | MV1/R1与MV2/R9同物理cap1不双grant；Unknown保护，缺拆并映射拒激活 |
| RVT-02 | T11 | A移3不回滚；B已到但DB失败不半写/不重运，补账一次，未执行余量才可关闭 |
| RVT-03 | T15,T32 | ReadyToReceive非激活，卸货后接受丢保持唯一handover/保管权Unknown；最终才WMS移账 |
| RVT-04 | T19,T26 | D2/D3同代数CAS一胜，旧任务固定原部署合法排空，新任务只active |
| RVT-05 | T05,T15,T24 | 迟到可信事实核验但不覆盖新位置；认证过期隔离/query，控制deadline过期0派发 |
| RVT-06 | T06,T32 | 60条页间变动，实时不承诺完整；快照固定读值/水位无丢重，600秒/撤权拒继续 |
| RVT-07 | T26,T28,T32 | 自批/换环境目标参数版本策略/撤权/600秒拒新执行，599秒须所有运行前提仍合法 |
| RVT-08 | T30,T31,T32 | 旧备份不知道外部已执行，不重发旧Outbox/C1，无法查实保持Unknown；RTO超4小时仍不解禁 |
| RVT-09 | T33 | 固定容量/环境3次各分位达标，超1ms失败，不平均/不以正确性失败换速度 |
| VAL-01 | T08..T11,T29 | 计件1/0.5、计量0.001/0.0001及上限；部分收发/短拣；inTransit与Reserved并集扣可用 |
| VAL-02 | T08,T10,T11,T29 | 单序列2拒/不同序列各1可；FEFO时间键/效期边界；局部锁1800秒仅提醒、解锁重盘/禁自批 |
| VAL-03 | T12,T13 | All/Any双提交顺序、禁自批/空候选/失资格、hook ACK丢逻辑一次 |
| VAL-04 | T03,T04,T29 | Access900/绝对28800、空闲900/300；登录验证码/锁/IP、只读授权30秒与高后果立即撤权、新密码边界 |
| VAL-05 | T06,T07,T29 | PDA缓存默认关/299与300、operationID例外；文件20MiB/+1、60秒扫描/600秒问题、引用清理 |
| VAL-06 | T14 | 30秒聚合不丢事实/高危即时禁静默/普通900与上限3600、公告7天/受众/PDA渠道、积压1000或60秒 |
| VAL-07 | T05,T14,T30 | 首次后5次相邻1/5/30/120/600秒，累计756秒；总deadline900秒、永久错0重试/物理Unknown0重发、Job600+30不误停 |
| VAL-08 | T04,T16,T28,T30 | P1响应300/升级900、复开历史；90天普通结果可清但领域键/Unknown保留；H缺项新控制0/RPO900/RTO14400 |

每行的所有实际参数/步骤还须读基线§5，不以本表摘要遗漏原断言。PAR-10非安全投影1/3秒不能当设备freshness；PAR-11 PLC最大32步/嵌套0不能代替每步await和总时限现场值。

## 5. 跨模块测试次序

T09/T10/T11等先以明确contract fixture验owner，不为等所有模块推迟事务测试；T32采用实际模块路径执行：

| 集成批 | DTC范围 | 前置实际能力 | 必需副作用/故障断言 |
|---|---|---|---|
| X1 仓储执行 | DTC-XMOD-001..004 | WMS/ST/FW/PDA、Route/device语义 | 收货→最终移账一次、交通等待、提升同口/阶段、两单抢7/短拣5，不提前目标入账 |
| X2 跨包/外部 | DTC-XMOD-005..006 | Orchestration/ST/FW/EXT | handover未知不二派、双方query、验签错无推进、回调重复无二效 |
| X3 地图/环境 | DTC-XMOD-007..009 | Device/告警/issue/map/manifest/SCADA | 故障链可追、无回读不恢复、prepare失败0激活、只读SCADA不授权控制 |
| X4 审批/恢复 | DTC-XMOD-010..013 | Workflow/WMS/消息、Pack控制、系统恢复 | hook重复效果1、强完门禁、旧备份水位/未知保护、alarm Cleared不解决issue/复开留痕 |

13项均P0，逐Provider/适用Adapter，不因同进程省略消息/事务崩溃窗口。X批次是诊断顺序不是允许跳过；迁移试运行与跨模块均通过才跑最终固定负载，纯算法基准/开发诊断可更早运行但不算发布证据。

## 6. 故障、恢复和迁移试验矩阵

| 注入窗口 | 入口任务/用例 | 断言/退出条件 |
|---|---|---|
| 业务事务前/内/后，Outbox发布前后/ACK前后 | T05/T09/T11/T13；NTF/WMS/WF | 本地事务无半写；允许重复投递不重复记账/决定；可查PendingSync |
| grant第2资源失败/lease接管/dispatch前后 | T18/T19；ROUTE-006..009 | 全量原子、旧epoch0新授权、已越Adapter边界不得推断未执行，Unknown保护 |
| 设备写已执行ACK丢、重连旧generation | T16/T25；DEV-002/004..006 | 无自动重发、旧观测不覆盖、query/人工核验；证实前不Ready |
| 跨Pack卸货后目的接受回执丢 | T15/T32；BUS-002/XMOD-005 | 唯一token与资源owner保护、账面/保管权/观测独立，中间流水0 |
| active CAS胜出但激活事件漏/重复 | T26；SITE-003/004 | 查持久manifest恢复、不重复导入，失败部署重新prepare/审批 |
| UI旧回包/丢响应/断网/撤权 | T06/T28/T29 | 稳定行/当前指纹、同operation查询不重发、无本地待写、盲盘/越权不泄露 |
| DB/Broker/Redis/Host/Device单项及相邻恢复 | T30/T32；SYS-005/XMOD-012 | 耐久owner重建、DB失联拒写、缓存非权威、Job不误并跑，按同command恢复 |
| 迁移缺字段/脏跟踪/重复ID/旧策略与在途 | T31；SYS-002/004/006及WMS变体 | 明确行级拒绝与差异，不舍弃历史跟踪；账本重算/单位分组/重复导入不二账/二发 |
| 备份早于外部已执行，旧Outbox仍待发 | T30/T31；SYS-002/XMOD-012 | 隔离出口、对账外部水位/唯一身份，只补账；无法查则Unknown并保留资源 |

恢复每日全备7天及日志链/RPO≤15分钟/RTO≤4小时是P目标，不是当前保证。实际测起点为演练声明的故障时刻，RPO比较故障前最后可恢复已提交业务水位，RTO直到所有必要对账/控制解禁前提成立；若长期Unknown，RTO失败但保护继续。

## 7. 性能执行协议

T33沿用PAR-10：1000资产、50车、100并发用户、100事件/秒、单Pack图10000节点/30000边。固定build/规则/map/planner/provider/driver/CPU/RAM/存储/网络/任务混合/seed；预热5分钟、稳态30分钟、独立3次，三Provider各有自己的完整结果，不能只选最快一次/一种数据库。

| 指标 | p95 | p99 | 采样定义 |
|---|---:|---:|---|
| 只读API | ≤500ms | ≤1500ms | 客户端请求至完整响应；记录失败/超时与总样本，非只计成功快请求 |
| 候选规划 | ≤100ms | ≤500ms | 固定输入验证后到候选/明确NoPath结果，分别报告地图与冲突档 |
| DB grant | ≤50ms | ≤200ms | 完整资源请求进入授权到事务提交/拒绝；冲突率与DB等待单列 |
| 地图本地选择→检查器骨架 | ≤100ms | ≤250ms | 实际浏览器事件到骨架可见，不是后端耗时替代 |
| 非安全投影端到端 | ≤1秒 | ≤3秒 | 权威事实产生到客户端对应版本可见，时钟已校准或同测量基准 |

测试工具须声明分位算法/采样时钟，保存每次原始脱敏样本或可复核分桶。各指标恰好门限通过，超1ms失败，不平均分位；错误/超时/丢事件/冲突超授/账本不一致不得被吞吐抵消。另报告P50/内存/吞吐/队列age/拒绝率/公平等待分布，未给批准上限的观察项不虚构通过阈值。

先证明功能正确/记录测量，再定位规划/DB/网络/HotStore/设备成本；优化不改变grant原子性/owner/H/公平政策。等待15分钟是告警，不是任务完成SLA。现场负载超过基准须另有容量计划和批准，不能把此档当无限许可。

## 8. H门禁与现场责任

| 证据组 | 提供/复核角色 | 取得前阻断什么 |
|---|---|---|
| 定位/制动/速度/物理buffer、时钟误差/freshness | 设备/安全/Route | 依赖空间时间模型的新控制，不能用显示1/3秒代替 |
| 车型/载荷/深浅/每层车数/停车进入离开/提升口容量联锁 | 设备/Pack/现场 | 对应分配/停车/提升阶段，不猜cap1/无限 |
| submit/query/去重/cancel/停止、认证/nonce、PLC步await/总时限 | 供应商/Device/集成 | 未证真实能力、Unknown自动恢复与控制写 |
| 单写宿主/协议fence/网络隔离、生产身份/endpoint | 运维/Device/安全 | 多副本生产设备出口及Promote |
| 精确Provider/EF驱动/隔离锁语义/恢复日志链 | DBA/后端/运维 | 该Provider生产支持/升级/还原，不影响隔离纯逻辑开发 |
| OS/UniApp/扫码枪/安全存储/清除/屏幕手套 | PDA/现场/安全 | 该终端生产写/持久只读缓存，不等同所有UniApp端 |
| 具名主备/业务迁移策略/正式runbook/真实目标硬件 | 业务/运维/QA/发布 | 控制投用/切换与性能达标声明 |

每组记录证据hash/版本/适用对象/时间/签署者/复审条件。T34每阶段需不同人提交复核，批准绑定目标/环境/参数摘要/资源与策略版本，10分钟有效/单operation；运行前置仍复验。仿真参数不能升级成生产默认。

## 9. 执行频率、证据与放行

| 时机 | 必跑 | 停止/处置 |
|---|---|---|
| 每个切片本地 | 相关红绿、格式/类型、对应CaseId与架构 | 行为未绿不标完成；未跑环境如实记录 |
| PR集成 | 改动涉及模块真实Provider B、相关C/E、权限/事务/幂等/故障 | 至少相关环境必需项全绿；契约变化所有消费者回归，不只编译 |
| 定时完整回归 | 全167展开软件变体、三Provider、隔离Broker/缓存/虚拟设备 | 失败分owner定位，保留首个RunId/反例，重跑不覆盖首次失败记录 |
| 候选发布 | 全回归+迁移/恢复/性能+已批准真实Adapter/终端矩阵 | 缺一适用必需项blocked；只缩发布范围须批准并禁用未支持入口 |
| 现场阶段 | 该阶段H/Promote/停机/恢复/操作员任务与runbook | 任何未知物理事实停止扩大、保留保护，不能自动回退DB掩盖命令 |

不新增未经团队选择的CI平台，本轮只规定门禁语义；T02按现用工程服务建立对应流水线。测试运行频率不把真实设备H塞进无人值守定时任务。

证据记录必须含：

```json
{
  "caseId": "DTC-WMS-009",
  "variant": "RVT-02-B-arrived-posting-failed",
  "taskId": "T11",
  "pageIds": ["PAGE-WMS-007"],
  "ruleVersion": "WES-RULES-1.0",
  "provider": "PostgreSQL",
  "environmentKind": "isolated-integration",
  "result": "not-run"
}
```

该JSON只是记录形状示例，不是测试结果；执行记录还须包含实际RunId/build/provider-driver版本/Adapter或终端版本、时刻、初始快照、动作/响应、最终owner查询、断言与副作用计数、脱敏日志/trace、证据hash、清理结论及负责人。result只在真实运行后更新，不能拷示例填passed。

发布报告按Provider/Adapter/终端分别汇总passed/failed/blocked/not-run与适用组合总数。代码覆盖率可辅助发现遗漏，但不发明一个百分比替代167 DTC/变体、265原TC与64页适用性。高风险失败没有“总分足够即上线”。

## 10. 本轮计划静态自审

- 计划目标是检查T01..T34唯一/依赖存在且无环、波次高于所有直接前置；依赖表不是源代码调用关系。
- 覆盖检查展开本计划§3与§3.1，和详细设计实际定义集合逐项比较，要求64 PAGE/65 FUNC/122模块DTC，首责无遗漏/重复；UI32+跨模块13合167，RVT9/VAL8与规则基线集合一致。
- 全部Markdown本地文件链接应解析；拟创建源路径只用代码格式，不能冒充已有文件链接。
- 计划自审不等于自动化/Provider/设备/性能测试通过。完成本轮文档后登记实际检查结果，保留所有开发复选框未勾选。

### 10.1 2026-10-23静态核对结果

- 34个任务定义/工作包标题唯一连续，所有直接依赖均存在、拓扑无环；16个最早就绪波次（0..15），每项波次严格晚于全部硬前置。
- 测试覆盖表与原设计定义逐项比对：64 PAGE、65 FUNC、122模块DTC一致；122条模块DTC各有唯一首责，32条UI和13条XMOD覆盖完整、父DTC总数仍167。逐工作包正文也覆盖全部64 PAGE/65 FUNC，不只由索引表代替开发入口。
- RVT-01..09与VAL-01..08均逐行映射；原设计265 TC映射保持完整。任务/设计引用、Markdown表格列数、严格UTF-8、203个本轮6份新增/修改文档的本地链接目标核对通过，不包括锚点和外部站点。
- 原详细设计静态回归通过，当前详细包索引关联的本地文件链接目标600个有效；Git差异空白检查与VS Code文档Problems通过。
- 全部开发复选框未勾选，无业务代码/项目/迁移/自动化测试实现、无构建或现场测试执行、未提交/推送。会话校验脚本仅为本轮文档核对，不是未来CI已部署的声明。
