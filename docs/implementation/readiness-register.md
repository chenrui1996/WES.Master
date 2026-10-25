# 实施就绪与阻断台账

- 状态：实施中；本台账区别软件批准、工作包完成与现场许可
- 批准来源：本轮项目发起人明确授权文档→编码→测试、在main实施、现有全部详细设计/Route逐项语义/WES-RULES-1.0软件基线
- 后续授权：2026-10-23用户明确批准每个工作包完成并验证后commit/push到既有GitHub；不含生产连接/迁移/设备控制或代替安全/设备/DBA签字
- 数据库：[ADR-0002](../adr/ADR-0002-mysql-first-implementation.md)，MySQL先行；其余两种deferred

## 1. 范围与角色

现有13个设计包64 PAGE/66 FUNC/168父DTC及265原TC映射为开发基线，不将设计用例计作passed。功能开关已纳入SCOPE-OPS-02设计及T02/T06/T30/T32计划；DTC-SYS-008已有配置校验与匿名catalog测试，完整能力组合尚未验收。BoxSort仍预留；冻结/拆合托/补货等未立项示例不自动变现成功能。Seven物料/单据/库存跟踪/角色迁移保留业务意义，脱敏映射、旧密码兼容、在途冻结/回填与对账由T31验证，不直接搬表。

| 责任 | 当前明确事实 | 主/备与关闭阶段 |
|---|---|---|
| 软件需求批准 | 项目发起人已批准现有设计 | 不能推定其同时是现场安全签字人 |
| 实施/测试执行 | 本轮助手执行，结果据实记录 | 人员具名主备待团队指定；不编造人名/投入/交期 |
| DBA/隔离库与迁移 | 用户已创建并授权wes_test；按后续明确授权沿用Seven来源凭据，实测MySQL8.0.12 | DBA主备待指定；本机探针测试已具授权，生产最小权限/发布签字仍不虚构 |
| 设备/协议/安全 | 尚无新增具名证据 | 设备与安全主备待指定；T25/T34依赖控制前关闭 |
| PDA/浏览器终端 | 尚无批准真实OS/扫码枪矩阵 | 客户端/QA主备待指定；对应E/H验收前关闭 |
| Seven迁移/业务对账 | 保留范围已定，未导入数据 | 业务/迁移主备待指定；T31前关闭 |

软件契约细化见[契约基线](../contracts/01-business-and-execution-contracts.md)。角色待具名不虚构为组织排期；可推进不依赖外部环境的受控软件切片，不把整个T01/M0标完成。

## 2. Route逐项软件语义签核

下表批准者均为项目发起人，来源为本轮明确选择“批准现有设计作为软件实现基线，现场H门禁保持不变”。`approved`仅需求状态，测试仍designed；不合并为一次标题签核。

| 需求 | 软件签核语义 | 状态/适用测试 |
|---|---|---|
| REQ-ROUTE-001 | Pack/Layer/不可变map版本明确解析 | approved / DTC-ROUTE-001 |
| REQ-ROUTE-002 | 重复ID、缺端点/资源引用拒绝 | approved / DTC-ROUTE-001 |
| REQ-ROUTE-003 | 非有限/负成本、非法容量/能力映射拒绝 | approved / DTC-ROUTE-001 |
| REQ-ROUTE-004 | 无图/无路fail closed，任务版本固定 | approved / DTC-ROUTE-002/003 |
| REQ-ROUTE-005 | 车辆方向/载荷/能力过滤 | approved / DTC-ROUTE-002/004 |
| REQ-ROUTE-006 | 有限非负成本及分解 | approved / DTC-ROUTE-002 |
| REQ-ROUTE-007 | 同输入确定tie-break | approved / DTC-ROUTE-002 |
| REQ-ROUTE-008 | 完整资源与设备执行段映射 | approved / DTC-ROUTE-004 |
| REQ-ROUTE-009 | 同向资源竞争遵守容量 | approved / DTC-ROUTE-005 |
| REQ-ROUTE-010 | 对向/交叉共享物理冲突域 | approved / DTC-ROUTE-005 |
| REQ-ROUTE-011 | 时间窗/边界/缓冲模型明确 | approved / DTC-ROUTE-005；物理值H未签 |
| REQ-ROUTE-012 | 全量grant原子，授权后派发 | approved / DTC-ROUTE-006 |
| REQ-ROUTE-013 | grant/release幂等与owner隔离 | approved / DTC-ROUTE-006 |
| REQ-ROUTE-014 | lease/缓存丢失不释放Unknown | approved / DTC-ROUTE-007 |
| REQ-ROUTE-015 | freshness/来源/generation/sequence校验 | approved / DTC-ROUTE-004；现场阈值H |
| REQ-ROUTE-016 | 冲突/map不确定暂停核验 | approved / DTC-ROUTE-004 |
| REQ-ROUTE-017 | 超时先query，反馈不串task/segment | approved / DTC-ROUTE-008；真实query能力H |
| REQ-ROUTE-018 | 下一段先授权、取消终态一次 | approved / DTC-ROUTE-008 |
| REQ-ROUTE-019 | 安全点重规划、恢复先对账 | approved / DTC-ROUTE-003/009；真实安全点H |
| REQ-ROUTE-020 | 环检测相对独立oracle无误漏 | approved / DTC-ROUTE-010 |
| REQ-ROUTE-021 | 优先/aging声明前提内公平 | approved / DTC-ROUTE-011 |
| REQ-ROUTE-022 | 不虚称活性，不越恢复安全边界 | approved / DTC-ROUTE-010/011 |
| REQ-ROUTE-023 | Stacker挡路/工艺依赖 | approved / DTC-ROUTE-012 |
| REQ-ROUTE-024 | FourWay停车/提升口依赖 | approved / DTC-ROUTE-012 |
| REQ-ROUTE-025 | 性质/差分seed及最小反例留存 | approved / DTC-ROUTE-013 |
| REQ-ROUTE-026 | 环境/分布/门限基准证据 | approved / DTC-ROUTE-013 |
| REQ-ROUTE-027 | 软件仿真与物理安全分开验收 | approved / DTC-ROUTE-013 |

## 3. 技术/现场门禁

| 门禁 | 实测/缺项 | 影响与关闭证据 |
|---|---|---|
| SDK | .NET10.0.103、Node24.9.0、npm11.6.0可用 | 精确工程包按实际manifest/restore锁定，不推定客户端兼容通过 |
| 容器 | docker命令不可用 | 不安装系统软件，不宣称Testcontainers通过 |
| MySQL | 用户已建并批准wes_test；授权读取Seven来源凭据，仅内存限库，实测8.0.12 | [隔离库方案](02-mysql-isolated-test-environment.md)已更新；探针迁移/事务/并发已验证，不读写原库、不更改账号，不代表最小权限验收 |
| Provider选型 | 测试评估Oracle MySql.EntityFrameworkCore10.0.7/EF10.0.7/MySql.Data9.7.0，锁版及真实引擎已验证 | [实际结果/限制](03-mysql-provider-foundation.md)；8.0.12的ExecuteDelete语法差异已记录；生产许可证审查及各owner实际API仍逐项验证；其他Provider deferred |
| H物理参数 | 制动/定位/速度/缓冲/容量/时钟/freshness未证 | 对应真实控制保持Unconfigured，禁用；T17纯逻辑不作为H证据 |
| H协议/租约 | 去重/query/停止/fence/nonce/固件未证 | Unknown保护不放宽；真实Adapter/真机切换blocked |
| H投用/恢复 | 具名主备、runbook、网络隔离及恢复对账未证 | T34生产许可blocked，DB连接成功也不解禁 |

## 4. 执行状态与下一入口

| 工作包 | 当前状态 | 下一条件 |
|---|---|---|
| T01软件基线/契约 | 已记录批准与跨owner边界；非整体完成 | 后续切片提交前细化本owner DTO；环境/人名不得填假值 |
| T02无数据库Host切片 | 已实现并验证，见[详细思路与证据](01-foundation-host.md) | 26项测试通过；live200/ready503、未开放控制端点；不代表T02或父DTC整体完成 |
| T02真实MySQL基础切片 | 已实现并验证，见[Provider记录](03-mysql-provider-foundation.md) | 测试隔离、迁移幂等/事务/唯一键/tenant/乐观并发/清理已通过；保留生产权限与兼容限制，不计业务owner验收 |
| T02软件运行时/架构基础门禁 | 已实现并验证，见[HTTP运行时记录](04-host-runtime-foundation.md) | 安全ProblemDetails/关联日志、真实依赖故障恢复、已知非法symbol反例通过；可供T03内部实施，完整父DTC/业务扩展与生产许可仍另验 |
| T02部署级功能开关基础 | 首片已实现并验证，见[实施记录](26-deployment-feature-switches.md)及[测试记录](27-deployment-feature-switches-tests.md) | 严格启动配置校验、不可变快照和匿名白名单catalog已测；当前无运行能力owner，启用时明确失败；T02/DTC-SYS-008整体仍未完成 |
| T03身份首片 | 内部身份owner、HTTP合同、JWT/cookie/会话、改密、受控本地bootstrap及具名页面已接通；66项定向测试和隔离HTTPS浏览器验收通过，见[HTTP实施](19-t03-authenticated-http.md)、[测试](28-t03-authenticated-http-tests.md) | T03仍未整体完成；真实生产secret/key ring/泄漏语料、代理拓扑、容量/保留及DBA/运维验收仍独立待办；不开放业务/设备控制，不误标T03完成 |
| T03可信来源准入 | 内部耐久IP预算已实现并验证，见[来源准入记录](09-durable-login-source-admission.md)；不是整包完成 | 跨账号/租户30次/60秒、首次唯一键与第30次CAS并发、保存失败/取消/坏schema/时钟反例、独立owner迁移通过；账号预算/验证码/凭据/JWT/代理与HTTP页面链路仍未接齐 |
| T03账号风险状态 | 内部耐久失败窗口/暂锁已实现并验证，见[账号风险记录](10-durable-account-login-risk.md)；不是整包完成 | 5领域与20迁移/真实DB测试通过；同租户账号3后验证码门槛、5后暂锁900秒、滚动边界与CAS保护；仍须完整owner处理冲突重新准入/真实失败不漏记、验证码一次消费、凭据/JWT/审计及HTTP页面，不把风险读取当签发许可 |
| T03验证码生命周期 | 内部耐久签发/一次消费已实现并验证，见[验证码记录](11-durable-login-challenges.md)；不是整包完成 | 9领域/保护与24迁移/真实DB测试通过；120秒、正确/错误同绑定一次、跨绑定拒、MAC保护与并发最多一接受；渲染/秘密装配、限流容量、完整凭据/风险并发接线及HTTP/页面E2E仍未验收 |
| T03当前会话登出 | 内部撤销/原子审计已实现并验证，见[登出记录](12-audited-current-session-logout.md)；不是整包完成 | 15项真实DB测试通过；当前自有family与一条版本链审计原子、幂等/refresh两方向竞争/审计SQL失败回滚、8次CAS耗尽明确失败；不含HTTP、改密/禁用全量撤销及完整登录链路 |
| T03兼容凭据编码 | 内部编码器已实现并验证，见[编码器记录](13-compatible-password-codec.md)；不是账号/登录整包完成 | 17项测试通过；Seven兼容仅受限验证，新密码完整Unicode/SHA384增强BCrypt、显式scheme/规范成本格式及秘密输出保护；Clear证据仍由未来历史/泄漏owner取得，不能直接签发会话 |
| T03耐久历史/受控改密 | 内部owner已实现并验证，见[改密记录](14-durable-password-change.md)；不是公开API/整包完成 | 7项领域/格式与30项迁移/真实DB测试通过；最近5个含当前值、逐原scheme历史检查、旧密/自有Active会话/版本复验，凭据与既有全部自有family撤销/审计原子；新family签发栅栏、外部泄漏检查、目录/禁用及HTTP/页面仍待接线 |
| T03耐久登录协调/签发栅栏 | 内部协调owner已实现；后续身份HTTP/JWT与具名页面已集成，见[登录记录](15-coordinated-password-login.md)、[HTTP合同](19-t03-authenticated-http.md) | 3项领域与28项迁移/真实DB测试通过；来源/挑战独立消费、账号门槛、未知账号dummy/真实新旧核密、在途失败事件合并、risk/凭据/new-family显式原子事务；已关闭login/改密new-family竞态。真实泄漏语料/生产密钥和代理、365天保留与容量治理、生产及现场H仍待批准；T03未整体完成 |
| T04权限/配置/审计内部基础 | 已实现并验证，见[详细思路与红绿证据](08-platform-access-settings-audit.md)；不是整包完成 | 29领域用例、36迁移/真实MySQL用例通过；当前role/session复验、字段scope/集合身份、角色可委派与原子审计、配置CAS与原子audit、受权审计查询已覆盖；用户/部门/字典目录owner和具名HTTP/页面已接通；版本化P设置写入现由隔离HTTPS浏览器复验并断言持久配置与审计，T04其余命令集成及运维验收仍待办 |
| T03/T04当前授权凭据禁用 | credential-disable owner、认证HTTP adapter与具名用户页面动作已实现并验证，见[禁用记录](16-audited-account-disable.md)、[HTTP测试](32-t03-account-disable-http-tests.md)及[页面切片](33-t03-account-disable-ops-web.md)；不等同完整目录/T03/T04 | 22项禁用与3项登录侧新增真实DB测试通过；用户凭据版本仅在Credential读取字段授权下投影。页面显式确认后按凭据版本调用，owner复验权限/版本；HTTP覆盖无权拒绝、版本冲突、重复效果、两个family撤销及旧token拒绝。档案编辑启停保持目录用户版本合同 |
| T03/T04受权登录安全事件查询 | 内部只读投影和受权HTTP/具名页面已接通，见[查询记录](17-authorized-login-event-query.md)、[HTTP合同](19-t03-authenticated-http.md) | 24项真实MySQL查询测试；All字段权限、筛选/有界分页、撤权、真实SQL失败/取消通过；保留/容量/导出治理与生产验收仍缺 |
| T03登录页视觉实现 | 已完成独立UI切片，见[实现记录](24-ops-web-login-ui.md)及[测试记录](25-ops-web-login-ui-tests.md) | 仅呈现层；不视为T03认证/API/E2E整包验收；浏览器视口无横向溢出、登录页组件5项和Ops Web构建已通过 |
| T03/T04具名运维页面与身份HTTP | 软件切片已通过，见[页面实施](20-t03-t04-operations-client.md)、[页面测试](31-t03-t04-operations-client-tests.md)、[T03 HTTP测试](28-t03-authenticated-http-tests.md)、[账号禁用HTTP测试](32-t03-account-disable-http-tests.md)及[账号禁用页面切片](33-t03-account-disable-ops-web.md) | Ops Web页面新增版本化P设置真实写入/刷新/审计断言和凭据禁用确认闭环；严格构建、隔离HTTPS Chromium通过；200%人工缩放、真实PDA与生产环境仍未验收 |
| T04目录及命令回执 | 目录owner/API/页面与具名非机密写回执已集成；**T04整体仍partial**。见[目录实施](18-t04-directory.md)、[目录测试](29-t04-directory-tests.md)、[回执实施](23-t04-command-receipts.md)、[回执测试](30-t04-command-receipts-tests.md) | 目录与相关授权持久层106项、回执专用工程31项及HTTP回执6项通过；成员重放以最小受影响角色ID证据精确复验当前授权，真实MySQL覆盖已移除角色失去委派时拒绝及无关角色不阻断部分授权重放；生产清理调度、DBA/运维及人工验收仍未关闭 |
| T05 Platform Outbox/Inbox 原子首片 | 部分完成，见[Outbox实施](35-t05-platform-outbox.md)、[Outbox测试](36-t05-platform-outbox-tests.md)、[Inbox实施](37-t05-platform-inbox.md)、[Inbox测试](38-t05-platform-inbox-tests.md)及[消息运维](39-t05-platform-message-operations.md) | Platform安全审计允许列表与业务/审计共用owner事务生成Outbox；有限失败类别退避、15分钟预算、单消息并发lease claim、旧lease fencing、单消息publisher、有界到期扫描、tenant/consumer/event唯一Inbox及owner事务消费幂等已实现；`platform.messages.read`安全元数据查询及`platform.messages.replay`受权、同operation幂等、expected-attempt fencing的Platform审计事件重放已实现。隔离MySQL/HTTPS定向测试通过；完整串行Release 816 passed、0 failed、0 skipped。托管publisher周期调度、真实Broker/运行时注册、Inbox清理、其他owner Outbox/Inbox及Notifications聚合页仍未实现；生产与三Provider门禁未通过 |
| 功能开关后续集成 | T06客户端模块catalog已接入导航与直接路由，见[实施记录](41-t06-query-state-and-module-catalog.md)及[测试记录](42-t06-query-state-and-module-catalog-tests.md) | 运行能力owner注册、T30诊断及T32全组合验收仍待依赖计划执行 |
| T06..T16 | T06共享查询状态与模块目录首片已实现，T06整体及T07..T16未完成 | 按原DAG逐项；T06与T05在T04软件基线后可独立并行；后续工作仍遵守各自硬依赖 |
| T17 Route纯内核 | 软件工作包验收完成，见[实现、红绿及审查证据](07-route-kernel.md)；不是完整Route/WCS验收 | FourWay/Stacker各自私有topology/physical mapping与确定性候选路径；连接物理语义/设备归属缺陷先红后修复，最终SPEC/CODE审查PASS；228 Route测试与父集成通过；预约/派发/反馈及H分别由后续包验收 |
| T18..T34 | 未开始 | 等待各自DAG前置；现场H/真实终端/生产DBA等门禁仍独立阻断，不用软件单测替代 |

上一批次交接与精确测试/风险见[review交接](06-review-handoff.md)，本批增量以本台账及各切片记录为准。用户要求连续实施、每包完成后提交GitHub、缺资源可参考Seven，已按授权推进且未反复提问；这不授权改动旧业务库、跳过现场H或将内部切片计作完整工作包。按原计划允许可独立拒绝切片单独提交，但提交名和追踪必须明确foundation/partial，不冒称T04等整包完成。

本轮未估算具名团队排期，未执行现场动作。任何缺项只阻断依赖它的切片，不用虚拟成功填空，也不将全部34项任务一次性勾完。

## 5. 本批软件验证与发布

- 按已授权范围持续交付：`7952ffe`权限/配置审计foundation、`519020e`实际角色委派用例、`223a589`字段集合身份回归修复均已正常推送main；这三次提交均明确T04仍partial。T17独立工作包另行提交，不混入未验代码。
- 最终软件验证：locked restore通过；全solution Release构建0警告/0错误；`dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1`得到Architecture29、Modules99、Persistence74、Route228，共430 passed、0 failed、0 skipped。
- 全solution格式检查、EF迁移快照一致性、设计映射/计划DAG与文件链接检查通过。MySQL仅使用授权wes_test。一次并发运行健康探针抖动的观察与最终串行复跑条件保留在[T04记录](08-platform-access-settings-audit.md)，不隐瞒初次失败、不提高生产超时换绿。
- 上述430是当前软件自动化测试数量，不等于167父DTC整体、64 PAGE或T04–T34全量交付；当前仍没有完整登录/后台页面链路、真实设备控制与生产投用许可。

### 2026-10-24续实施

- 新增T03耐久来源准入，未以TenantId=0放宽业务上下文。5领域与20迁移/真实MySQL用例通过。
- 本次全solution Release：Architecture29、Modules104、Persistence94、Route228，共455 passed、0 failed、0 skipped，0构建警告/错误；既有测试行为保留，两个DbContext快照一致。
- 此处455替代上一批430作为最新软件回归数量；T03/T04整体及后续未实施工作包仍不改完成状态。

### 2026-10-24账号风险续实施

- 新增租户内账号风险状态与Platform增量迁移，未修改TenantContext/来源上下文边界；记录包含完整身份尚缺的并发接线门禁。
- 最新全solution Release：Architecture29、Modules109、Persistence114、Route228，共480 passed、0 failed、0 skipped，0构建警告/错误；locked restore、格式与双context快照一致。
- 480替代上一批455作为当前软件回归数量，不代表T03整包或完整公网防滥用通过，完整计划勾选保持不变。

### 2026-10-24验证码续实施

- 新增租户内验证码120秒签发/一次消费与keyed MAC保护；原账号/来源/租户隔离不放宽，无明文答案列或HTTP泄漏入口。
- 最新全solution Release：Architecture29、Modules118、Persistence138、Route228，共513 passed、0 failed、0 skipped，0构建警告/错误；格式与双context快照一致。
- 513替代480作为当前软件回归数量；完整T03、可用验证码渲染及生产秘密装配仍未验收。

### 2026-10-24当前会话登出续实施

- 当前自有会话登出与有限SessionRevoked审计同事务提交；TenantContext未修改，refresh竞争不复活family，其他family不受影响。
- 最新全solution Release：Architecture29、Modules118、Persistence153、Route228，共528 passed、0 failed、0 skipped，0构建警告/错误；格式与双context快照一致。
- 528替代513作为当前软件回归数量；内部验证码与登出连续实施已验证，仍不将T03完整登录/HTTP/页面或后续T04–T34标成完成。

### 凭据编码器续实施验证

- Seven实际依赖4.2.1/work12已只读核对；合成固定向量验证旧72字节别名与新SHA384模式差异，未读取旧用户密码/散列。
- 先观察15项中的13项行为红灯，再实现并补充至17项通过；新/旧scheme不猜测、不try both，旧成功保持受限，不静默升级。
- 最新fresh locked restore及全solution Release：Architecture29、Modules135、Persistence153、Route228，共545 passed、0 failed、0 skipped，0构建警告/错误；格式、IDE诊断、双context快照一致。
- 545替代528作为当前软件回归数量；账号目录、历史5次、泄漏服务、完整登录/JWT/页面及现场H仍未验收。

### 耐久凭据与受控改密续实施验证

- 当前值计入最近5个、旧scheme逐项核历史的默认细化已回写PAR-02；不另创账号规范化/开户入口，不读旧用户数据。
- 先观察领域5项行为红灯、owner非屏障20项中14项失败；再验证真实CAS、refresh两提交顺序及审计1062回滚。补充缺scheme和哈希期间时钟倒退的2项明确红灯后修正，验证899→900秒锁定和跨context历史第5/6旧值边界。
- 最终7项领域/格式、30项迁移/真实DB用例通过；增量迁移只增凭据表和复合唯一索引，在授权`wes_test`应用。没有公开改密endpoint/生产迁移。
- 最新全solution Release：Architecture29、Modules142、Persistence183、Route228，共582 passed、0 failed、0 skipped，0构建警告/错误；格式、IDE诊断与双context模型一致。
- 582替代545作为当前软件回归数量；本地历史证据已实现，外部泄漏Clear、用户目录/禁用、并发新family签发栅栏、完整登录/JWT/HTTP/页面/E2E仍未验收，不将T03或T04–T34冒称整体完成。

### 登录协调与会话签发栅栏验证

- 来源/账号/挑战/凭据/新family已由一个内部owner协同；不接收客户端预验证布尔值。当前grants拒持久legacy受限family，refresh不解除限制。
- 先取得3项领域红灯与14项DB行为红灯，再实现；真实event1062暴露risk/event隐式事务回滚不完整，改为显式owner事务后断言通过。没有把“多记一次风险”作为原子性例外。
- 3项领域、28项迁移/真实DB验证通过，涵盖6并发错密、单挑战双请求、risk CAS及8次耗尽、首次PK竞争、login/改密两种提交顺序、角色与refresh不能绕过受限状态、独立消费不退款及核验后时钟倒退。
- 最新全solution Release：Architecture29、Modules145、Persistence211、Route228，共613 passed、0 failed、0 skipped，0构建警告/错误；格式、IDE诊断、双context模型一致。
- 613替代582；已关闭新family签发/改密竞态，用户禁用管理、受权登录事件查询/365天保留与容量治理、泄漏服务、完整账号目录、JWT/可信代理/HTTP/验证码渲染/页面E2E仍待完成。旧行迁移默认禁用/受限，生产升级需受控方案，不把测试库通过当部署许可。

### 当前授权用户禁用验证

- 行为桩18项9红9绿→实现18绿→扩展禁用22项及登录侧3项；两个相关类52项全部通过，含真实audit1062、取消及提交竞争，不伪造数据库并发异常。
- 最新全solution Release：Architecture29、Modules145、Persistence236、Route228，共638 passed、0 failed、0 skipped，构建0 warning/error。首次与formatter并行时既有1秒探针恢复断言失败；不放宽门限，无并行formatter完整复跑通过。
- 638替代613；内部禁用管理与login/refresh竞争缺口已关闭。受权登录事件查询/365天保留及容量治理、泄漏服务、目录/部门、JWT/可信代理/HTTP/渲染/页面E2E仍未完成；T03/T04整包、生产与现场H状态不变。

### 受权登录事件查询验证

- 22项可编译行为桩12红10绿→实现22绿→新增实际100条分页门限及跨scope必需列回归，24项真实MySQL查询测试通过；没有将编译错误/伪造SQL异常当数据库证据。
- 最新全solution Release：Architecture29、Modules145、Persistence260、Route228，共662 passed、0 failed、0 skipped，构建0 warning/error，format/IDE通过。
- 662替代638；内部受权登录事件查询缺口已关闭。365天保留/归档容量、导出审计、泄漏服务、目录/部门、JWT/可信代理/HTTP/验证码渲染/页面E2E仍待实施；不标T03/T04或后续工作包整体完成。

### T02 部署功能开关首片

- 严格配置路径校验、不可变快照与匿名白名单catalog已有6项定向测试；禁用所有模块UI开关时，真实身份HTTP仍通过认证/授权集成测试。
- 首轮全套回归中旧运行时测试fixture缺少必填Features，16项启动失败；补齐测试配置后定向17项与最终串行Release全套通过。
- 当前全solution为Architecture29、Modules217、Persistence316、Route228，共790 passed、0 failed、0 skipped。后续运行能力owner、T06菜单集成及T30/T32仍未验收；790不代表完整DTC-SYS-008或T02完成。

### T06共享查询状态与模块catalog首片

- 共享`QueryState`覆盖loading/error/empty/stale/ready；Audit保持同一查询与身份范围的上一组结果，在刷新失败时显示错误并保留数据，筛选/身份变化仍清除旧结果。
- Ops Web严格校验匿名模块catalog的完整allowlist、重复/未知代码与启用值；HTTP/JSON/网络错误时不创建默认开放配置。导航同时要求模块开关和现有权限，Platform直达路由在权限判定前检查模块状态；关闭/不可用均转至明确状态页。登录、会话入口和API授权保持独立。
- Audit、用户、角色、租户目录已接入共享查询状态；同scope刷新失败保留旧结果并禁用基于旧版本的行操作，筛选变化立即清除结果。列偏好按环境/租户/主体/page/table隔离，仅存列ID并按当前授权schema规范化；损坏/存储失败显式提示。菜单/catalog、直达路由与会话独立性已覆盖自动测试。
- 共享查询快照合同/查询密集交互、599/600秒固定快照边界，以及其余T06验收未实现；本记录只确认可审核前端切片，不代表T06完成。结果与命令见[测试记录](42-t06-query-state-and-module-catalog-tests.md)。

### 当前授权账号禁用HTTP adapter

- 新增`POST /api/platform/users/{id}/disable`，复用PlatformScope中的禁用owner；当前授权、可信tenant、凭据版本与事务审计继续由owner负责。
- HTTP真实MySQL测试先红（未注册路由返回404），接线后通过；覆盖无权403、旧版本412、成功204、重复请求不重复审计、目标两个family撤销和旧访问token拒绝。现有用户档案UI继续使用目录用户版本，不引入错误的版本转换。
- 修改文件的C#格式验证通过；最新全solution Release回归Architecture29、Modules218、Persistence316、Route228，共**791 passed、0 failed、0 skipped**。
- 791替代790作为最新自动化软件回归数量；T03/T04仍为partial，真实生产凭据/key ring/泄漏语料、代理与保留治理、DBA/现场H、PDA及人工200%缩放等门禁仍未关闭。

### 当前授权凭据禁用页面闭环

- 用户目录仅向具备该目标用户`platform.users.read`的`Credential`字段权限者投影不含秘密的凭据版本；查询只选择用户ID和版本。缺该字段权限不暴露版本，也不显示禁用入口。
- 具备当前`Enabled`写权限的页面动作显式二次确认后，按凭据版本调用已有HTTP owner；不会误用用户档案版本。HTTP 204成功形态只在该命令允许，权限/版本仍由服务端复验。
- 定向字段投影、真实HTTP、Ops Web 75项测试、严格类型构建及格式检查通过；隔离HTTPS真实浏览器现覆盖显式确认、204、刷新后的禁用状态及数据库持久化。全solution现为Architecture29、Modules218、Persistence317、Route228，共**792 passed**。792替代791。
- 最新串行Release全solution验证完成：792 passed、0 failed、0 skipped；一次并行全solution运行中的1秒MySQL readiness探针失败已单独复跑通过，串行全套通过，门限未修改。
- 不增加密码启用/重置，不替代生产、PDA、人工缩放、保留治理或现场H验收；T03/T04仍为partial。
