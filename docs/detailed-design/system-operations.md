# 系统运维页面、操作手册与详细测试

- 状态：评审中；DTC均designed/未执行
- 权威：[架构](../02-system-architecture.md)、[跨模块验收](../acceptance/01-cross-module-and-provider-matrix.md)
- 范围SCOPE-OPS-02、SCOPE-DB-01、SCOPE-EXT-01；REQ-OPS-006..007、REQ-ARCH-001..008
- 共用：[标准](00-shared-page-and-test-standard.md)；[参考边界](01-reference-cases.md)
- 产品阅读：运维员的只读状态/证据控制台与受控部署手册；DENSITY=3、DATA_COMPLEXITY=2、CONSEQUENCE=1（操作手册步骤为3）。

## 1. 页面与非页面操作

| PAGE定义 | 角色/入口/返回 | 线框/字段/数据owner | 操作/查询 |
|---|---|---|---|
| PAGE-SYS-001 | 受权运维查看员；`/system/health` | O：环境/版本 → liveness/readiness → 依赖/模块影响。Host版本、Provider/driver版本、必需/可选依赖、最后探测、错误摘要；secret不回显 | 环境/模块过滤，受控刷新；必需依赖失败与可选Disabled不同；关联Job/Device页面，不提供开PLC或强制健康按钮 |
| PAGE-SYS-002 | 日志/审计查看员；`/system/diagnostics` | O：关联ID/时间 → 脱敏日志 → order/Leg/command/message证据链。日志、指标非业务权威，缺数据明确未知 | 有界时间范围/分页；受权导出留审计；跳转owner详情复核，不据日志一行改任务完成 |
| PAGE-SYS-003 | 发布/恢复负责人；`/system/deployment-evidence` | O：发布清单 → 配置/迁移/backup/restore报告引用 → 签核/阻断。环境、build/schema版本、目标Provider、manifest、writer状态、证据hash；不可上传secret | 证据查看/批准后的文件引用；部署/还原在受控runbook执行，不创建网页一键生产还原/lease接管按钮 |

三页适用UI-001..004/007/008/011；001/002适用UI-012，003若无推送则不适用。无业务编辑/创建，所以UI-005/006/009/010不适用于页面；导出是受权管理动作，应检验去重/审计。无依赖/证据是“尚未采集”，不是健康；003未签核时明确不可生产投用。

## 2. 功能/受控runbook

| FUNC定义 | 页面/需求 | 编号流程与权威 |
|---|---|---|
| FUNC-SYS-001 | 001/002；REQ-PLAT-010..012、REQ-ARCH-001..007 | 1按Host/模块依赖探测；2暴露安全摘要；3用correlation查受控日志；4到owner状态核验。必需DB/租约未确认不得开放控制；readiness与进程存活分开；日志查询限制范围/脱敏 |
| FUNC-SYS-002 | 003；REQ-OPS-006、SITE-TC-017 | 1审批restore工单并记录backup时间/owner版本/Outbox水位；2准备隔离数据库、secret、网络及禁生产endpoint规则；3恢复WMS/WCS/manifest等耐久记录；4禁写启动并核对账本余额/任务/grant/地图版本/待事件；5虚拟Adapter验证；6双人复核报告后才允许隔离测试。网络隔离须独立于UI配置，任何连生产迹象立即阻断 |
| FUNC-SYS-003 | 001/003；REQ-OPS-007、SITE-TC-018 | 1检查每Pack writer及Device写宿主单例边界；2数据库原子lease/epoch；3失租约/失DB立即拒绝新写授权；4新writer先核对在途command与物理占用；5只开放策略允许的只读能力。不把Pack的epoch自动当作所有协议连接的安全fence，Adapter能力需证明 |
| FUNC-SYS-004 | 003；REQ-ARCH-001..004 | 1预生产逐Provider迁移/升级演练；2记录备份与前向恢复/不可逆声明；3停/隔离写者及处理在途任务；4按批准runbook部署；5架构依赖/扩展边界、readiness/契约/版本核验；6失败按前向修复或可行回滚。地图回滚另建补偿manifest，不能DB降版掩盖物理状态 |
| FUNC-SYS-005 | 001及Ops Web入口；REQ-ARCH-008；[功能开关设计](feature-switches.md) | 1 Host启动绑定并验证部署级`Features`；2构建不可变运行快照；3只公开白名单模块UI catalog；4区分Disabled/Misconfigured/Degraded；5逐模块评估独立关闭准入。匿名端点不返回运行能力或依赖细节；不得关闭权限、租户隔离、审计、幂等或H门禁 |

操作手册证据含操作人/复核人、目标环境、批准单、时间、版本、前后快照、脱敏命令输出、结果/阻断及回滚决定。本文不提供未核验的生产CLI，执行前需填写现场部署工具、权限与准确资源名；缺少正式runbook阻断生产，不授权临时猜命令。

已批准[规则基线RULE-26..28/PAR-09](05-approved-rules-and-defaults.md)：每日全备保留7天并保留可恢复日志链，恢复点目标间隔≤15分钟，RPO15分钟/RTO4小时含外部事实对账和控制解禁；普通结果90天/日志30天/审计至少365天，活跃Unknown不清。准确Provider/恢复工具和物理fence仍H，恢复默认网络隔离/禁生产出口/禁派发，不因旧Outbox待发就补发已发生命令。RVT-08/VAL-08是正式DTC必测，不是本轮演练结果。

## 3. 详细测试

| DTC定义/功能/原TC | 前置/编号步骤 | 断言与证据 | 级别/环境/清理 |
|---|---|---|---|
| DTC-SYS-001 / 001 / PLAT-TC-012..013 | 必需DB、可选邮件Disabled；1看正常；2断DB；3断可选邮件；4查脱敏错误 | 进程可活而readiness失败；必需依赖失败拒绝控制；可选项标Disabled/影响范围；无凭据；业务事实不被健康页改动 | P0/B+E/C0 |
| DTC-SYS-002 / 002 / SITE-TC-017 | 备份Qty10/L1/G1/D1，备份后C1已外部执行但DB不知；网络deny生产；1恢复只读；2核账本重算/owner/Outbox水位差；3query外部事实；4误配endpoint；5量RPO/RTO | 网络/身份拦误配且生产写0；不重发旧C1/Outbox，无法查实持续Unknown保护；对账后只补一次；恢复点损失>900秒或至控制解禁>14400秒报告目标失败，不为达标放行。RVT-08必测 | P0/三Provider恢复演练+C/C1 |
| DTC-SYS-003 / 003 / SITE-TC-018/ROUTE-TC-041..042 | 两副本A/B，同Pack旧epoch7、grantG1保护未知物理占用；1并发获取；2当前writer失DB；3新writer接管至更高epoch；4旧A写；5未对账B派发 | 一个有效writer；失DB/旧epoch授权0；新writer未对账派发0；G1仍保护；只读能力按部署策略保留 | P0/B+C/C1 |
| DTC-SYS-004 / 004 / ARCH-TC-005/007/WMS-TC-017 | 逐Provider空库/旧schema数据及模块迁移顺序清单；1按owner顺序安装/升级；2在迁移失败点停止；3按批准策略回退/前向恢复；4重查数量/唯一键/时间精度及非owner schema差异；5汇总各模块事务/并发套件 | 安装升级报告含版本；失败不标成功；可恢复到声明状态；账本/预约不损坏，无非owner私有schema变更；Provider偏差有报告/批准；不可逆项不得展示“可回滚” | P0/三Provider演练/C0 |
| DTC-SYS-005 / 001/003 / E2E-TC-012/ARCH-TC-009 | Outbox发布中、Job运行中、transport活动、Adapter重连中有Unknown命令；1分别在上述窗口重启Host/Broker/缓存/Device；2关联ID查链；3owner对账 | 耐久记录不丢；消息允许重投但业务效果1；Job按批准恢复策略或明确未解决；活动运输不丢，未知物理动作不重发；日志不代替确认；页面显示组件恢复/待人工状态 | P0/B+C+E/C1 |
| DTC-SYS-006 / 004 / ARCH-TC-001..002 | 架构验收夹具包含合法扩展hook、非法跨模块Infrastructure/DbContext依赖、Builder菜单/API依赖变体；1静态依赖扫描；2以扩展调用公开WMS契约；3注入非法引用/非owner跨表写；4检查部署模块/路由注册 | 合法扩展不形成第二账本；非法引用/跨表写在架构门禁失败，非owner写入为0；Host不含领域规则；Builder源码生成入口/依赖不进入目标部署；不能仅隐藏菜单 | P0/静态架构检查+B/C0 |

### 3.1 密集交互与关联配置

遵循[交互合同](03-dense-page-interaction-contract.md)，数字为PAGE-SYS；G/R/W为共用测试配置。W仅约束已存在的手册证据/签核流程，不增加网页生产恢复执行器。

| 页面 | 配置 | 默认顺序/可选列 | 关联筛选与证据交互 |
|---|---|---|---|
| 001 | G+R | 环境!、组件!、liveness/readiness!、必需/可选!、最后探测!、影响；+版本/Provider版本 | 环境→已部署Host→模块/依赖；按Host/健康分组，Disabled和故障不同；切环境清旧组件/选择，错误不靠隐藏列变健康 |
| 002 | G+R | 时间!、等级、owner、关联ID!、结果/错误摘要；+脱敏主体/对象引用 | 环境/owner/有界时间→合法关联链；精确correlation查询与关键词分开，敏感搜索不进视图/URL；无编辑日志，导出仍按原授权 |
| 003 | G+W | 发布/恢复记录!、环境!、版本!、Provider、验证/审批状态!、时间；+证据hash/runbook引用 | 目标环境→批准build/schema/Provider/runbook→对应隔离backup/证据；恢复目标必须独立声明隔离，不由筛选默认生产环境。只读历史选中不自动执行runbook |

| DTC定义/功能/原TC | 固定前置与编号步骤 | UI/持久化/副作用断言 | 级别/环境/清理 |
|---|---|---|---|
| DTC-SYS-007 / 001/002/004 / PLAT-TC-012..013/ARCH-TC-005/007 | 环境SIM/PROD、同名HostH1、SIM备份B1/目标ProviderP1，PROD证据E2无权；1存个人列/环境视图；2切PROD；3SIM旧resolve回；4在证据表选B1与不匹配Provider；5点历史记录返回 | 环境/健康/审批不可隐藏；旧Host/备份引用不跨环境回填，E2不泄露；不匹配证据显式阻断签核；视图/选择没有部署/还原/lease写，返回保留合法条件；无生产执行捷径 | P0/E+B/C0 |
| DTC-SYS-008 / 005 / REQ-ARCH-008/ARCH-TC-011 | 有效完整配置；分别设 UI 模块关闭、运行能力关闭、AND不匹配、配置缺项/错型/未知键及依赖故障；通过重启应用新配置 | UI关闭仅隐藏入口，服务/API保持原认证授权；运行能力关闭不注册且依赖操作显式Disabled/503；非法配置启动失败；AND任一false即Disabled；依赖故障是Degraded；重启后新快照生效；所有安全门禁持续有效，无数据、Outbox或Unknown事实丢失 | P0/Host+API+Ops Web+C0 |

## 4. 阻断输入

RPO/RTO和保留初始目标已按规则基线定案但未达标验证。准确部署/Provider版本与事务参数、恢复/外部对账手册、单写宿主/fence、现场负责人仍需H证据；默认只读降级仅允许受权owner查询，缺关键DB时明确不可用，不回成功空对象。系统页面是证据入口，不新增网页生产恢复器。
