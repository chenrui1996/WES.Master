# 实施就绪与阻断台账

- 状态：实施中；本台账区别软件批准、工作包完成与现场许可
- 批准来源：本轮项目发起人明确授权文档→编码→测试、在main实施、现有全部详细设计/Route逐项语义/WES-RULES-1.0软件基线
- 后续授权：2026-10-23用户明确批准每个工作包完成并验证后commit/push到既有GitHub；不含生产连接/迁移/设备控制或代替安全/设备/DBA签字
- 数据库：[ADR-0002](../adr/ADR-0002-mysql-first-implementation.md)，MySQL先行；其余两种deferred

## 1. 范围与角色

最新T08客户端：[禁用库位创建](163-t08-disabled-location-creation-client.md)/[证据](164-t08-disabled-location-creation-client-tests.md)已交付当前完整write及父仓read按钮、完整tuple预览/二次确认、原key/body未知恢复、创建/查询互锁与显式GET回执。新增35前端，全套730/严格构建、相关.NET129含五套真实HTTPS浏览器+架构29=158与Release0/0通过；真实201及最终禁用位置/事实/事件单效果已核验。历史“库位创建无表单”已替代；真实Pack、完整层级/候选链、启停/容器/固定快照/完整生命周期及整包仍缺。

最新T08：[当前授权库位创建HTTP](161-t08-authorized-location-creation-http.md)/[证据](162-t08-authorized-location-creation-http-tests.md)已交付独立默认关闭LocationCreationEnabled、唯一具名singleton schema启动门禁、当前五字段/父仓read、严格body/header及永久key、原子禁用位置/事实/Outbox，65 HTTP/启动与8配置新增。最终Release 128含四套真实HTTPS浏览器+238主数据owner+29架构=395通过，构建0/0。历史“库位创建无HTTP”已替代；无真实schema adapter/Pack验收、创建表单/启停/完整生命周期及整包不关闭。

最新T05：[默认关闭WMS托管发布](159-t05-hosted-wms-publisher.md)/[证据](160-t05-hosted-wms-publisher-tests.md)已验证严格部署快照、显式tenant/batch/周期、新owner runner、60秒合作取消与五计数/未确认警告。新增27 Host/真实进程和1真实MySQL runner，最终Release 85含四套真实HTTPS浏览器+33发布owner+29架构=147通过，构建0/0。真实Program仍先拒绝未登记的Outbox/MessageQueue runtime owner，不以服务集合测试冒称实际Broker可用；剩余接线包括真实adapter/运行能力登记与消费Inbox，整包保持partial。

最新T05：[WMS owner发布器](157-t05-wms-owner-outbox-publisher.md)/[证据](158-t05-wms-owner-outbox-publisher-tests.md)已交付独立scope/transport合同、90秒耐久lease/CAS、认领即计attempt防崩溃无限重试、5次退避/15分钟预算/隔离及ACK未确认恢复。提交跨过lease/预算的两项行为RED已修正为发送前复验；旧Pending真实迁移，原列表九字段扩展四态、PendingCount只计Pending，客户端同步真实标签。新增33owner/3HTTP/4前端，最终238+32含四套HTTPS浏览器+29=299，前端695/严格构建、Release0/0。历史“WMS无publisher”仅托管/实际transport接线仍适用；Inbox、统一快照与整包缺口不关闭。

最新T08/T06：[当前授权库存流水](153-t08-authorized-stock-ledger-query.md)/[证据](154-t08-authorized-stock-ledger-query-tests.md)已交付独立All完整19字段、当前tenant SQL过滤/稳定时间分页、正负固定六位字符串与历史前后余额/来源、不修改事实。客户端显式查询/微秒UTC/撤权清旧，真实375px叠加200%文字定位并修复共享body最小宽度640px问题。关联[T04新建用户加载门禁](155-t04-user-create-loading-gate.md)/[证据](156-t04-user-create-loading-gate-tests.md)修复当前目录未就绪时可点却无动作。最终前端691/严格构建、owner106+HTTP46+四套HTTPS浏览器4+架构29=185通过、Release0/0。历史“授权流水无owner/API/客户端”已由本片替代；固定核对快照/导出、来源业务跳转与完整生命周期仍缺，不代签所有页面/PDA或整包关闭。

最新T04字典：[数字稳定码](151-t04-numeric-dictionary-code.md)/[证据](152-t04-numeric-dictionary-code-tests.md)已修复DTC-PLAT-014 code0被拒绝：仅字典业务码允许数字起始/保持string，不放宽角色/部门/分类/语言键。新增18领域/9真实HTTP/1真实owner，相关28+23+真实HTTPS浏览器1+架构29=81通过，Release0/0；真实改译/请求-默认-key回退/引用删除409及原全页面浏览器流程通过。T04父DTC适用矩阵和其他软件/运维门禁仍缺，整包不关闭。

当前整包未关闭原因（不把“软件未实现”伪装成仅等现场）：

| 工作包 | 尚未交付的软件/完整验证 | 需要外部真实证据 |
|---|---|---|
| T04 | 父DTC全变体/页面与命令适用矩阵及剩余集成验收，日志→Outbox→job全链路 | 生产最小权限/保留容量与运维验收 |
| T05 | 实际Broker adapter/运行能力登记、WMS消费Inbox、跨owner统一快照与完整聚合、完整投递恢复接线；默认关闭WMS调度桥已验证 | 批准隔离Broker、ACK丢失/崩溃窗口实测 |
| T06 | 全具名页面DTC矩阵、其他owner固定核对快照与相关客户端 | 真实PDA/OS/扫码枪矩阵 |
| T07 | 业务实体授权附件、绑定/解绑及完整业务文件目录 | 真实ClamAV/病毒库/识别/健康、部署ACL |
| T08 | 完整主数据启停/层级/容器、固定核对快照/导出、流水来源业务跳转及运输完成闭环 | 实际Pack schema/资源归属及相应现场门禁 |

上述缺口不能由现有自动化测试数量或本轮切片完成代签；当前实现可继续按依赖推进，但T04–T08整体仍为partial。

最新T05统计：[WMS创建事件计数](149-t05-wms-creation-event-counts.md)/[证据](150-t05-wms-creation-event-counts-tests.md)已交付第二owner独立统计字段/当前creator交集、SQL全量LongCount及共享owner计数UI。新增26真实owner/12HTTP/23前端，最终主数据205+HTTP/三套真实HTTPS浏览器36+架构29=270通过、前端604/严格构建、Release0/0。历史“WMS无统计”由本片替代；不伪造不存在的Inbox/投递状态、不跨owner求和；统一快照、发布/消费、真实Broker及其他四包缺口不变。

最新T08客户端：[设计](147-t08-inventory-query-client.md)/[证据](148-t08-inventory-query-client-tests.md)已交付实时库存入口、完整All字段/精确维度筛选、六位数量字符串与BigInt校验、撤权/取消/离线清旧；新增60前端，最终581/严格构建、真实HTTP18+HTTPS Chromium三变体3=21、架构29通过，Release0/0。历史“库存客户端未交付”仅固定核对快照/授权流水/导出部分仍适用；真实Pack、完整生命周期、其余T04–T07缺口不变，不关闭整包。

最新T08库存：[当前受权owner/API](145-t08-authorized-inventory-query.md)/[证据](146-t08-authorized-inventory-query-tests.md)已交付All完整字段、tenant精确维度SQL过滤后分页、同一RepeatableRead账面/保护量投影、重叠并集和六位decimal字符串，无改账端点。新增26真实owner/18HTTP，最终原账本预留+新读取74+HTTP18+架构29=121通过、Release0/0、无模型漂移。历史“库存查询无API”已由本片替代；客户端/固定核对快照/导出、真实Pack、主数据完整生命周期、容器/业务单据/运输完成仍缺，T04–T08不虚关闭。

最新T05双owner客户端：[设计](143-t05-owner-selected-message-client.md)/[证据](144-t05-owner-selected-message-client-tests.md)已将WMS创建事件接入原消息路由，Platform/Wms模块+permission成对OR、owner切换清旧/取消、Platform重放锁选择、独立当前WMS九字段读取及真实当前撤EventId后403。新增59前端/导航/路由，最终521/严格构建、HTTP/三套真实HTTPS浏览器16+架构29通过，Release零警告错误。历史“第二owner客户端缺失”已由本片替代；跨owner统计/统一快照、WMS publisher/Inbox与真实Broker仍未实现，T04–T08整包保持未关闭。

最新T05第二owner：[WMS创建事件诊断](141-t05-wms-creation-event-diagnostics.md)/[证据](142-t05-wms-creation-event-diagnostics-tests.md)已交付WMS owner/API九字段pending创建事件、独立`wms.messages.read`全字段、当前creator交集SQL前过滤与有界页，复用WMS目录过滤而不跨私表。新增38owner/13HTTP，最终179+WMS领域/HTTP/既有两套真实浏览器159+架构29=367通过，Release零警告错误/无模型漂移；客户端第二owner与统计/发布消费/真实Broker仍缺，T05整包不关闭。

最新T08客户端：[仓库创建表单](139-t08-disabled-warehouse-creation-client.md)/[证据](140-t08-disabled-warehouse-creation-client-tests.md)已交付当前write字段可见、精确预览/二次确认、10秒确认未知原键手工恢复、创建/查询互锁与显式读取回执。新增35客户端，全套462/严格构建通过，相关WMS领域/HTTP/真实HTTPS Chromium只读+写两变体146、架构29通过，Release零警告错误。此前“仓库表单未完成”已被本片替代；位置创建/真实Pack/树层巷/编辑启停/容器库存闭环与T08整包仍缺，其余T04–T07也不虚关闭。

最新T08 HTTP：[仓库创建入口](137-t08-authorized-warehouse-creation-http.md)/[证据](138-t08-authorized-warehouse-creation-http-tests.md)已交付单独默认关闭`WarehouseCreationEnabled`、当前write字段、严格JSON/key、201/200/409及真实单效果。新增38HTTP/7配置，相关WMS145+架构29=174通过，Release零警告错误；旧“Host创建未接线”仅库位/其他生命周期仍适用，仓库表单/真实Pack/编辑启停/整包未完成。

最新T05/T08：[WMS创建原子Outbox](135-t05-t08-wms-creation-outbox.md)/[证据](136-t05-t08-wms-creation-outbox-tests.md)已交付owner持久化门禁由新创建事实派生唯一待发布事件；永久operation重放不追加，故障资源/事实/事件三表均回滚，旧operation不自动回填。新增17真实owner，最终主数据141+WMS领域/HTTP/真实HTTPS浏览器100+架构29=270通过，Release零警告错误；实际FK/索引/UTC列与模型无漂移通过。历史“WMS无Outbox”仅其余业务/发布消费部分仍适用；跨owner统计、WMS publisher/Inbox与真实Broker未完成，五个整包仍不关闭。

最新T05计数：[设计](133-t05-owner-message-counts.md)/[证据](134-t05-owner-message-counts-tests.md)已交付Platform四状态与现存Inbox收据API/页面，新增20owner/8HTTP/31客户端。前端427/严格构建、MySQL38、HTTP/真实HTTPS浏览器21、架构29通过；显式统计字段，不自动授予原行读者，失败不转零、重放后陈旧且手工重读。此前“Inbox统计未完成”仅跨owner聚合部分仍适用；真实Broker/其他owner与五个整包仍未关闭。

最新T05消息筛选：[设计](131-t05-filtered-message-diagnostics.md)/[证据](132-t05-filtered-message-diagnostics-tests.md)已交付Platform状态/UTC创建时间SQL过滤后分页与客户端筛选。前端396/严格构建、HTTP/真实HTTPS Chromium13、MySQL18、架构29通过，Release零警告错误；原参数兼容，非法400，补SQL物化完成时取消检查。以下历史“过滤未完成”仅跨owner部分仍适用；Inbox统计/真实Broker及五个整包仍未关闭。

最新T05续片：[Platform受权重放界面](129-t05-authorized-message-replay-client.md)/[证据](130-t05-authorized-message-replay-client-tests.md)已交付，替代下述历史“重放表单未完成”状态。前端379及严格构建、真实HTTPS浏览器/HTTP3、MySQL重放2通过；同事件/原operation/当前字段/expected/理由预览、未知结果显式同键重试，最终owner查证唯一回执与原payload不变。跨owner/过滤/Inbox计数、真实Broker与T04–T08整包仍未完成。

最新T05局部交付：[Platform消息只读客户端](127-t05-platform-message-diagnostics-client.md)及[证据](128-t05-platform-message-diagnostics-client-tests.md)。新增40项客户端/路由/导航，前端全套346通过、严格构建通过；真实HTTPS Chromium与两项消息HTTP共3通过，真实MySQL查询1通过。修复首轮真实浏览器暴露的All范围值映射错误后复验，不放宽权限。跨owner/Inbox聚合、重放表单、真实Broker仍未完成；T04–T08整体不关闭。Broker/容器命令本次探测不可用，未安装服务或连接未知环境。

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
| T04目录及命令回执 | 目录owner/API/页面与具名非机密写回执已集成；**T04整体仍partial**。见[目录实施](18-t04-directory.md)、[目录测试](29-t04-directory-tests.md)、[回执实施](23-t04-command-receipts.md)、[回执测试](30-t04-command-receipts-tests.md) | 目录与相关授权持久层106项、回执专用工程31项及HTTP回执6项通过；成员重放以最小受影响角色ID证据精确复验当前授权，真实MySQL覆盖已移除角色失去委派时拒绝及无关角色不阻断部分授权重放。普通回执90天清理已补齐默认关闭、显式tenant白名单的托管软件入口，见[维护设计](87-platform-retention-maintenance.md)及[测试](88-platform-retention-maintenance-tests.md)；生产启用/容量、DBA/运维及人工验收仍未关闭 |
| T05 Platform Outbox/Inbox 原子首片 | 部分完成，见[Outbox实施](35-t05-platform-outbox.md)、[Outbox测试](36-t05-platform-outbox-tests.md)、[Inbox实施](37-t05-platform-inbox.md)、[Inbox测试](38-t05-platform-inbox-tests.md)及[消息运维](39-t05-platform-message-operations.md) | Platform安全审计允许列表与业务/审计共用owner事务生成Outbox；有限失败类别退避、15分钟预算、单消息并发lease claim、旧lease fencing、单消息publisher、有界到期扫描、tenant/consumer/event唯一Inbox及owner事务消费幂等已实现；`platform.messages.read`安全元数据查询及`platform.messages.replay`受权、同operation幂等、expected-attempt fencing的Platform审计事件重放已实现。Inbox有界清理与事务许可见[恢复实施](49-t05-inbox-retention-save-recovery.md)及[测试](50-t05-inbox-retention-save-recovery-tests.md)；默认禁用的90天保留维护现与目录回执同事务接线，34项托管/配置及12项新增真实MySQL测试通过，见[维护设计](87-platform-retention-maintenance.md)及[证据](88-platform-retention-maintenance-tests.md)。默认禁用的托管publisher调度和公共owner入口已接线，见[设计](95-t05-hosted-platform-publisher.md)/[证据](96-t05-hosted-platform-publisher-tests.md)；真实Broker注册、其他owner Outbox/Inbox及Notifications聚合页仍未实现，不替代Quartz作业单例或生产门禁 |
| 功能开关后续集成 | T06客户端模块catalog已接入导航与直接路由，见[实施记录](41-t06-query-state-and-module-catalog.md)及[测试记录](42-t06-query-state-and-module-catalog-tests.md) | 运行能力owner注册、T30诊断及T32全组合验收仍待依赖计划执行 |
| T06..T16 | T06共享查询状态/模块目录首片、QueryScope值对象、现有七类目录统一实时分页及Platform审计owner固定成员快照切片已实现；T06整体及T07..T16未完成 | Platform审计快照仅属owner局部首片，见[实施](51-t06-platform-audit-snapshot.md)及[测试](52-t06-platform-audit-snapshot-tests.md)；QueryScope值对象本身仍只表达指纹，不代替快照授权复验；其他owner、可变值冻结和完整DTC仍待后续，遵守原DAG |
| T08 Task 1数量领域基础 | **局部完成**：独立WMS纯领域项目及数量值对象已实现，不代表T08启动或解除硬依赖 | 16项定向Release测试通过；完整solution串行Release回归900 passed、0 failed、0 skipped。无Host装配、数据库、授权或消息接线。T04/T05/T06仍未整体完成；DTC-WMS-004/010/011、T08整体继续未关闭，证据见[实施](85-t08-quantity-domain-foundation.md)及[测试](86-t08-quantity-domain-foundation-tests.md) |
| T06候选与T08保护量续片 | **局部完成**：Users/Roles候选授权/取消边界及纯WMS执行单元并集投影 | 前端整套201项及严格构建通过，见[设计](89-t06-candidate-authorization-boundary.md)/[测试](90-t06-candidate-authorization-boundary-tests.md)；WMS新增23项保护量测试，数量基础仍16项，见[设计](91-t08-stock-protection-union.md)/[测试](92-t08-stock-protection-union-tests.md)。本片浏览器E2E及WMS持久化/CAS/租户授权/消息仍未集成，T06/T08与父DTC不整体关闭 |
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
- Audit、用户、角色、租户、设置/字典及会话目录已接入共享查询状态；同scope刷新失败保留旧结果并禁用基于旧版本的行操作，筛选变化立即清除结果，权限拒绝清除受限数据。列偏好按环境/租户/主体/page/table隔离，保存列ID、字段类别有界宽度及固定位置并按当前授权schema规范化；首个必显标识默认左固定，其余数据列可配置左/中/右区域，右固定操作列与其他固定列纳入50%宽度预算并提供超限提示，内容区720px及以下停用水平固定并提示，721px/720px边界已通过真实Chromium验证。Ops Web字体按用户默认字号放大，200%根字号、40%合成标题文本压力下页面无根级横向溢出且表格保持局部滚动。个人布局视图支持本机显式保存、恢复、重命名、确认删除、未保存副本提示及身份隔离；目前不持久化筛选/排序，等待类型化白名单条件合同。损坏/存储失败及跨标签变更显式提示。菜单/catalog、直达路由与会话独立性已覆盖自动测试。
- `QueryScope`值对象已验证身份和SHA-256指纹的不可变等值边界，见[契约实施](43-t06-query-scope-contract.md)及[测试记录](44-t06-query-scope-tests.md)。审计/登录证据实时分页的25条默认与25/50/100选择、请求scope失效及服务端撤权清空，60条数据在真实MySQL和HTTPS Chromium通过，见[分页实施](45-t06-audit-realtime-paging.md)及[测试记录](46-t06-audit-realtime-paging-tests.md)。Platform审计 owner 现已实现固定有序成员ID、当前授权复验、600秒TTL及599/600秒边界，并通过真实MySQL、HTTPS与Chromium验证，见[实施](51-t06-platform-audit-snapshot.md)及[测试记录](52-t06-platform-audit-snapshot-tests.md)。Platform Audit页的DTC-UI-001慢查询占位/刷新UTC读取时间首片已通过Controlled timer、组件和隔离HTTPS Chromium验证，见[实施](53-t06-audit-slow-query-state.md)及[测试记录](54-t06-audit-slow-query-state-tests.md)。Platform Users页DTC-UI-002首次/筛选空态及失败状态已有受控API证据，见[实施](55-t06-users-empty-states.md)及[测试记录](56-t06-users-empty-states-tests.md)。ColumnSettings键盘排序/宽度边界见[实施](57-t06-column-settings-keyboard-widths.md)及[测试记录](58-t06-column-settings-keyboard-widths-tests.md)；默认列恢复不清除筛选或业务行见[实施](67-t06-column-reset-isolation.md)及[测试记录](68-t06-column-reset-isolation-tests.md)；登出时清空当前列布局状态、同一主体重新认证后恢复个人偏好见[实施](69-t06-column-preference-logout-boundary.md)及[测试记录](70-t06-column-preference-logout-boundary-tests.md)；Platform Roles部门候选分页按25条读取见[实施](71-t06-department-candidate-page-size.md)及[测试记录](72-t06-department-candidate-page-size-tests.md)；跨标签事件保留未提交草稿见[实施](59-t06-column-preference-cross-tab-draft.md)及[测试记录](60-t06-column-preference-cross-tab-draft-tests.md)；主体、租户与环境变化时本机列偏好隔离和恢复见[实施](61-t06-column-preference-scope-isolation.md)及[测试记录](62-t06-column-preference-scope-isolation-tests.md)；存储禁用/配额失败时显式报错并保留当前布局见[实施](63-t06-column-preference-storage-failures.md)及[测试记录](64-t06-column-preference-storage-failures-tests.md)；授权schema变更后移除撤权列、默认加入新列并保留稳定ID布局见[实施](65-t06-column-preference-schema-migration.md)及[测试记录](66-t06-column-preference-schema-migration-tests.md)。以上均为局部首片；真实浏览器故障、其他owner、可变数据值快照、其余PAGE适用性及交互验收仍未实现，不代表T06完成。前端早期切片结果见[测试记录](42-t06-query-state-and-module-catalog-tests.md)。
- DTC-UI-022 Platform Roles/Users授权部门/角色候选按25条分页的受控页面回归已通过，见[实施](71-t06-department-candidate-page-size.md)及[测试](72-t06-department-candidate-page-size-tests.md)。共享候选读取器reset前延迟响应在reset后、及新响应先返回两种顺序下均不能覆盖当前候选状态，Ops Web全套167项、类型检查与生产构建通过，见[实施](73-t06-stale-directory-candidate-response.md)及[测试](74-t06-stale-directory-candidate-response-tests.md)。未知角色ID真实MySQL/HTTP提交被拒绝，用户版本/成员/审计不变；Identity HTTP类65项与MySQL连接守卫14项通过。测试辅助器仅按规范父目录定位受控配置并仍强制`wes_test`，见[实施](75-t06-reject-unresolved-role-candidate.md)及[测试](76-t06-reject-unresolved-role-candidate-tests.md)。Platform Roles部门候选精确Code查询首片已完成：服务端仅在Id/Code授权资源交集内过滤后分页，无Code字段授权拒绝；页面250ms去抖、A/AB乱序响应隔离，并保留已选部门摘要供草稿继续提交。真实MySQL/HTTP 3项、角色页面/候选读取器16项、Identity HTTP与MySQL连接守卫合计68项、Ops Web全套172项及生产构建通过；修改的C#格式与diff检查通过。见[实施](77-t06-role-department-code-candidates.md)及[测试](78-t06-role-department-code-candidates-tests.md)。隔离HTTPS Chromium、其他选择器、独立回填及完整DTC/T06仍待验收。
- DTC-UI-022 Platform Roles编辑已有规则的部门引用独立回填首片已通过真实MySQL/HTTP、页面回归和Ops Web构建验证：Identity HTTP/MySQL连接门禁70项、部门引用owner测试2项、Roles页面16项、Ops Web全套176项及构建通过；见[实施](79-t06-role-department-reference-resolution.md)及[测试](80-t06-role-department-reference-resolution-tests.md)。隔离HTTPS Chromium、其他选择器及完整DTC/T06仍待验收。
- DTC-UI-022 Platform Users编辑既有用户角色关联独立回填首片已通过真实MySQL/HTTP、受控页面与Ops Web构建验证：角色引用owner3项、Identity HTTP类73项、Users页面21项、Ops Web全套180项及生产构建通过；见[实施](81-t06-user-role-reference-resolution.md)及[测试](82-t06-user-role-reference-resolution-tests.md)。隔离HTTPS Chromium、其他关联选择器、DTC-PLAT-014其余步骤及完整DTC/T06仍待验收。

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

### T07 PAR-04 纯文件内容校验基础

- Platform 内纯校验器已完成：20 MiB实际字节边界、允许扩展名与MIME一致性、基础PDF/PNG/JPEG签名、严格UTF-8/NUL检查及常见可执行/容器签名与明显活动标记拒绝；返回稳定拒绝码，不保留输入内容。
- 定向测试38项通过；Platform/Host/测试项目Release依赖图构建成功，0 warning/0 error；整solution串行Release回归884 passed、0 failed、0 skipped；修改文件格式检查和`git diff --check`通过。实施与具体证据见[T07校验实施](83-t07-file-content-validation.md)和[T07校验测试](84-t07-file-content-validation-tests.md)。
- 仅完成PAR-04内容校验基础。脚本文本检测为有限启发式，不是安全扫描或深度格式验证；不含上传/隔离存储/扫描/授权下载/清理，不设置Available状态。T04/T06硬依赖、扫描及存储配置仍未关闭，T07整包继续未完成。

### T08 数量领域 Task 1 局部验证

- `Wes.Wms`为不含外部依赖的领域项目，加入solution与Modules测试引用；Host不引用/装配。库存数量、精度及单笔数量只表达RULE-01中可实现的纯数值合同，不含业务实体、授权、消息、存储或API。
- 初始测试先红：缺失`Wes.Wms`领域命名空间，CS0234；实现后定向Release 16项通过。WMS项目Release构建0警告/错误，完整solution串行Release为Architecture 29、Modules 287、Persistence 356、Route 228，共900 passed、0 failed、0 skipped；格式及diff检查通过。
- 该证据仅关闭T08 Task 1的纯领域基础。数值类型测试不是DTC-WMS-004/010/011整体验收，也不验证MySQL映射/舍入、持久化溢出、事务或并发；T08及T04/T05/T06硬依赖仍未完成。

### T04/T05/T06/T08 保留维护、候选边界与保护量并集

- T04/T05新增默认禁用的显式tenant白名单维护。每tenant每tick最多N条目录回执及N条已处理Inbox（N=1..200），相同UTC时刻计算90天边界，同owner事务保存并提交。未删除审计、Outbox或领域身份，未提供维护HTTP端点。配置大小写符合.NET语义，非法已提供值仍明确失败。真实MySQL证明另一owner先删同批时只报告安全瞬时冲突，下周期重试，不因正常竞争停止Host。见[设计](87-platform-retention-maintenance.md)/[测试](88-platform-retention-maintenance-tests.md)。
- T06 Users/Roles与共享候选收到当前401/403/拒绝时清除候选、翻页及实际编辑草稿；旧成功/失败和已取消signal的迟到调用不影响新范围。保留25条分页、稳定ID和授权历史引用回填。整套Ops Web **20文件、201 passed**；`npm run build`包含严格TS检查与生产构建，均通过。本片浏览器E2E未执行，不能借之前其他切片的浏览器记录冒充。见[设计](89-t06-candidate-authorization-boundary.md)/[测试](90-t06-candidate-authorization-boundary-tests.md)。
- T08新增不可变执行单元保护量及可用量投影：重复身份拒绝、同单元多标志只扣一次、互不重叠单元相加；Qty10/Reserved7内InTransit5得到Available3，Unknown仍保护。**新增23项**纯领域测试，与原16项数量测试共同回归；owner以后仍须在事务内证明同维度/单位及单元划分。无Host、EF或业务写命令接线。见[设计](91-t08-stock-protection-union.md)/[测试](92-t08-stock-protection-union-tests.md)。
- 集成复核按测试先行另外修复3个边界：配置键大小写错误拒绝、新查询被已取消旧调用reset、真实两个清理owner产生未分类EF并发异常。均先观察失败，再运行定向及最终回归，不删除失败用例或降低门限。
- 最终`dotnet test WES.slnx --configuration Release --no-restore -m:1`：Architecture **29**、Modules **344**、Persistence **368**、Route **228**，合计 **969 passed、0 failed、0 skipped**，替代900作为本批最终软件回归数量。新增维护配置/托管34项、真实MySQL维护12项、WMS保护23项，共69项；已有保留回归13项另随定向25项通过。
- 所有修改C#完整`dotnet format ... --verify-no-changes --include <修改路径>`、`git diff --check`通过；无新增依赖/迁移/生产配置或连接。真实测试仍只使用授权`wes_test`。
- **T04/T05/T06/T08整体均仍partial**：真实Broker/托管publisher、其他owner、Quartz作业单例及Notifications聚合、审计归档/容量与生产启用、WMS主数据/账本持久化/CAS/授权/页面及相关完整DTC仍待后续，不由969自动化用例代签。SQL Server/PostgreSQL仍deferred，生产/设备/现场H许可未改变。

### T07 隔离生命周期、私有存储、扫描协议与MySQL基础

- 已实现Quarantined/Available/Rejected/Deleting/Deleted领域状态、每次扫描独立attempt、60秒截止、600秒处理问题投影、24小时零引用删除fence及Deleted墓碑。摘要和实际长度不一致、旧attempt、扫描不可用或超时均不能清除隔离；这些原语仍需应用owner安全编排。
- 本地私有存储使用tenant/file UUID路径、CreateNew不覆盖、20MiB实际字节界限与读取摘要校验；ClamAV适配只使用loopback INSTREAM，扫描与摘要取同一输入快照，全流程60秒截止。没有Host注册、文件API、公开URL或假扫描fallback。
- MySQL迁移新增Platform自有文件/引用表及其复合tenant键、外键和索引。真实`wes_test`验证往返、tenant读写隔离、Version CAS、引用唯一、显式事务回滚，以及绑定与孤儿删除两种先后顺序只能一方提交，不留下部分引用。追加真实Provider反例发现默认大小写不敏感会混合owner引用；标准/原生排序元数据未实际修正列，保留已应用历史后用固定单列DDL修复，information_schema和精确查询/唯一键均验证通过。没有跨模块私表引用。
- 定向本地/协议 **78 passed**（含原内容校验38项及新增40项），真实MySQL新增 **8 passed**；最终完整串行Release为Architecture **29**、Modules **384**、Persistence **376**、Route **228**，合计 **1017 passed、0 failed、0 skipped**，替代969及本轮中间1016作为当前软件回归数量。所有修改C#和新迁移完整格式验证通过；model/snapshot无pending差异。详见[设计](93-t07-quarantined-file-lifecycle.md)/[测试证据](94-t07-quarantined-file-lifecycle-tests.md)。
- **T07仍partial**：上传/扫描/引用/删除的应用协调、实体实时授权、审计/受权下载、API/页面、存储中断/进程恢复及真实扫描验收尚未完成。当前未发现ClamAV或内置Defender扫描能力，未安装系统服务；TCP测试监听器不代表病毒识别通过。不因1017项回归将T04/T05/T06/T07/T08整体关闭，也不改变生产/现场门禁。

### T05 Platform 托管发布调度

- 默认禁用的Host周期已接通Platform公共owner应用入口，不引用模块Infrastructure/DbContext。transport/envelope移至公共Messaging边界，字段与有限结果语义不变；原lease/dispatcher/Inbox实现保持模块内部。
- 启用必须明确Identity、Outbox/MessageQueue能力、1..100唯一tenant白名单、1..3600秒周期、1..100 batch及唯一singleton transport。缺少真实adapter时拒绝启用，不提供None/成功fallback。每tenant每tick只一batch、60秒合作取消预算、90秒持久lease，整轮后固定延迟，不循环清空积压。
- 共用严格配置读取原语后，原保留维护34项Host及12项真实MySQL回归通过；新增publisher25项Host、5项真实MySQL验证配置/装配、allowlist/batch、预算/停止、有限持久状态及安全数据库失败。完整串行Release现为Architecture **29**、Modules **409**、Persistence **381**、Route **228**，合计 **1047 passed、0 failed、0 skipped**；修改C#格式及diff验证通过。见[设计](95-t05-hosted-platform-publisher.md)/[测试](96-t05-hosted-platform-publisher-tests.md)。
- **T05仍partial**：当前没有实际transport注册，真实Broker断连/ACK丢失、跨owner、Notifications聚合页与完整合同未验收。调度器不代表Broker投递或业务exactly-once，T04/T06/T07/T08也没有因此整体关闭。未新增依赖、迁移或生产连接。

### T08 WMS owner账本与持久化核心

- WMS新增自己的DbContext、三张余额/posting/entry表及独立迁移历史，无Platform或其他模块项目依赖/表外键。余额只从0开始、维度不可变；内部收发和同维度成对移账以显式事务生成永久业务身份/不可变流水和精确余额，不向Host暴露任意改账API。
- tenant/operation和tenant/kind/execution身份防重，重复命令或换operation的同执行单元只一次库存效果；冲突payload拒绝。双余额版本、真实并发CAS、Save成功后故障/取消整笔回滚均验证。新posting和entry必须带齐同次精确余额效果，拒绝空posting和向历史posting添加虚假事实。
- 实测修复MySQL默认排序合并大小写/重音批次的缺口，Batch列显式二进制排序；无批次使用非空空串保证唯一键有效。精确6位小数/UTC微秒、跨tenant读写及不可改历史均通过真实Provider测试。授权仍只能由后续业务owner实时核验，本数据库隔离不代替授权。
- 新增11项余额领域和25项真实MySQL测试，原39项数量/保护回归通过。完整串行Release现为Architecture **29**、Modules **420**、Persistence **406**、Route **228**，合计 **1083 passed、0 failed、0 skipped**；格式、diff、model/snapshot和迁移SQL检查通过。见[设计](97-t08-owner-ledger-core.md)/[证据](98-t08-owner-ledger-core-tests.md)。
- **T08仍partial**：主数据、业务授权、保护量/预留/在途/Unknown持久化、WMS Outbox与API/页面尚未完成；未将内部账本基础接入真实库存或生产Host，不以1083项回归关闭T04–T08。

### T08 持久预留与账本保护

- WMS新增永久执行份额及不可变reservation change，Reserved/InTransit/Unknown同份额按并集保护；部分释放使用永久operation防重，在途/Unknown不能ReleaseUnstarted，零余量身份不按TTL删除或复活。
- 预留与账本共同竞争balance Version；Shipment/Transfer只能使用Available或本执行单元的精确匹配非Unknown份额。确认消费、余额及成对entry/posting同事务，保存后故障/取消不留半笔效果。没有跨模块私表依赖或新包依赖。
- 新增跨余额execution冒用反例，先观察错误Posted后修复为ProtectedStock；新增ReadCommitted确定性交错反例，先观察新保护与旧余额混合计算异常，再增加保护读取后的版本复验，转为VersionConflict而非误报数据损坏。不自动重算/重复物理意图。
- 真实MySQL定向 **48 passed**（原25项账本、本片新增23项）。最终完整串行Release：Architecture **29**、Modules **420**、Persistence **429**、Route **228**，合计 **1106 passed、0 failed、0 skipped**。迁移仅新增WMS两表和tenant复合约束，model/snapshot一致；格式及diff检查通过，见[设计](99-t08-durable-stock-reservations.md)/[证据](100-t08-durable-stock-reservations-tests.md)。
- GitHub push仍遇TLS握手失败（默认OpenSSL及启用证书验证的schannel均失败），既有账本本地提交未确认远端；不关闭证书验证、不force-push、不把本地commit写作推送成功。
- **T08仍partial**：具名业务授权、完整主数据/单据状态/运输证据、WMS Outbox与API/页面未完成。内部ReleaseUnstarted仍只允许后续受权业务owner在已证明无执行效果时调用，不能直接接受HTTP bool。T04/T05/T06/T07/T08整体不因本片通过而关闭，生产/现场H及其他Provider门禁不变。

### T07 持久扫描与孤儿删除协调

- 新增内部Platform owner协调：先持久扫描attempt或Deleting fence再外部IO，结束后重新读取/CAS确认。预算使用原attempt截止剩余时间，慢保存不额外赠送60秒；迟到Clean、替代attempt或删除fence不能清除隔离或复活记录。
- 清理同时核验计数与真实引用表；年轻/活跃扫描/被引用文件拒删。存储删除失败保留Deleting，已删除对象而数据库确认失败也保留Deleting供缺失对象幂等重试，Deleted墓碑重放无外部IO。
- 新增20项真实MySQL协调测试，原8项文件持久化共28项通过；覆盖真正LocalQuarantineStore联动、开始claim失败无IO、证据错配、存储故障恢复、取消/迟到、并发单claim及删除后确认失败。完整串行Release现为Architecture **29**、Modules **420**、Persistence **449**、Route **228**，共 **1126 passed、0 failed、0 skipped**，见[设计](101-t07-file-processing-coordination.md)/[证据](102-t07-file-processing-coordination-tests.md)。
- GitHub连接恢复，普通push已将本地账本`0fe4984`及持久预留`9b18172`推送到既有origin/main（由`fd23831`前进到`9b18172`）；此前TLS失败是历史阻断，不再将这两笔写为未推送。未关闭证书校验、未force-push。
- **T07仍partial**：内部协调无Host/API注册；实体当前授权、受权上传/绑定/下载、页面与真实ClamAV健康/病毒库验收仍未完成。受控scanner不是恶意软件清除证明；本片不关闭T04–T08、不更改生产或现场安全许可。

### T07 当前授权暂存文件访问

- 新增PlatformScope具名暂存文件应用入口；复用当前目录/session/角色/字段授权，以持久creator及当前目录部门匹配Own/Departments/All。暂存权限不等于业务实体权限：实际存在引用或计数非零一律拒绝，未实现的业务附件授权不以tenant相同替代。
- 元数据、Scan/Delete与Content分别要求明确字段，读取中撤权/绑定/版本变化不交付字节；只有Available/Stored且真实SHA256/长度一致才可交付。下载审计显式提交后交付，审计错误/保存后取消清零缓冲并回滚；结果不默认JSON输出内容，Dispose清零。
- 新增21项真实MySQL授权测试，文件相关49项定向通过；完整串行Release为Architecture **29**、Modules **420**、Persistence **470**、Route **228**，共 **1147 passed、0 failed、0 skipped**。格式与diff检查通过，见[设计](103-t07-authorized-staging-file-access.md)/[证据](104-t07-authorized-staging-file-access-tests.md)。审计FileDownloaded追加int11，既有数值不改；无新表/迁移或依赖。
- **T07仍partial**：尚无上传、业务绑定/实体附件下载、HTTP/页面接线与真实扫描/部署ACL验收。现有应用边界仅授权暂存范围，不开放未知owner引用。T04/T05/T06/T07/T08整体保持真实状态，不调用完成工具代签。

### T07 持久受权上传

- 上传接入当前Upload字段/Own及当前部门授权，先原子持久隔离记录和创建审计，再执行私有存储CreateNew，重新授权后原子确认Stored和审计；成功仍隔离，不自动调用scanner。新tenant/operation永久身份按actor/精确名字/canonical type/长度/hash核验，换payload拒绝，跨tenant独立，终态不复活。
- 确认保存失败或取消后，同operation可核验已存在的精确字节恢复；缺失/部分/不同摘要仍明确失败、不覆盖、不伪造Stored。原始身份/内容元数据保存门禁不可修改，新增nullable UUID和tenant/operation唯一索引兼容旧NULL记录。
- 真实授权wes_test上传新增23项，原21项访问及28项协调/持久化合计定向72项通过；内容名字校验新增4反例。最终完整串行Release Architecture **29**、Modules **424**、Persistence **493**、Route **228**，共 **1174 passed、0 failed、0 skipped**。EF无pending模型变化，迁移SQL仅增加列/唯一索引，格式及diff验证通过。见[设计](105-t07-durable-authorized-uploads.md)/[证据](106-t07-durable-authorized-uploads-tests.md)。
- **整包状态不变**：T07业务实体绑定/附件授权、HTTP流限额/API/页面、真实ClamAV病毒库/部署ACL仍未验收。T04/T05/T06/T07/T08保持partial；本片无生产连接、服务安装或设备安全放行。

### T07 暂存文件HTTP传输

- 默认关闭的Wes:Files装配接通受权暂存上传/元数据/下载/扫描/孤儿删除；启用需Identity、已存在私有绝对目录及明确loopback scanner IP/端口。严格拒绝非法已提供配置和公开根重叠，Host仅调用公共module工厂，不引用Infrastructure。没有启动scanner或安装系统服务。
- raw body不使用JSON/base64或multipart临时文件；真实已知/未知长度M/M+1门禁，未知最多读取M+1字节、所有自有缓冲清零。中文显示名RFC3986 UTF-8一次解码并规范往返，非法编码拒绝；下载先owner审计再attachment/no-store/nosniff交付，响应失败也释放内容。临时HTTPS Kestrel已证明实际chunked门限和中文名往返，不只是TestServer。
- 新增 **46 passed**（23流/配置、23HTTP/Provider），包含当前撤权、跨tenant拒绝、安全503后原operation恢复、损坏500不审计、不可用扫描保持隔离、600秒问题和到龄删除。最终完整串行Release Architecture **29**、Modules **470**、Persistence **493**、Route **228**，合计 **1220 passed、0 failed、0 skipped**；格式验证通过，无新增依赖/迁移。见[设计及操作配置](107-t07-staging-file-http.md)/[证据](108-t07-staging-file-http-tests.md)。
- **T07仍partial**：Files页面/业务实体绑定与附件授权、真实ClamAV病毒库/识别/健康及部署ACL验收未关闭；现有Host readiness仍保留未装配业务门禁。T04/T05/T06/T08各自剩余范围也未完成，不因1220项回归整包关闭或代表生产/现场签字。

### T07 暂存文件客户端

- `/files`单文件工作区已接通raw字节上传/真实XHR进度、当前状态读取、按版本扫描/孤儿删除及受权二进制下载。取消/503仅用户显式同File/operation重试；scope撤权清实际选择控件/草稿/行，迟到响应与字节不创建下载URL，编辑UUID立即清未匹配旧行，失败刷新使旧投影stale且禁用动作。导航遵循Platform与read权限，不伪装完整全局分页文件目录。
- 前端 **22文件、220 passed**，严格vue-tsc/Vite构建通过。真实MySQL/私有存储/HTTPS Chromium验证中文1MiB上传、非零真实进度、scanner拒绝连接保持隔离、375/768/1440、键盘可见焦点/Enter下载、原字节/文件名及恰好一条下载审计。仅下载使用Available领域种子，不能当病毒识别；无API网络替身。
- 最终完整串行Release Architecture **29**、Modules **471**、Persistence **493**、Route **228**，共 **1221 passed、0 failed、0 skipped**，包含原平台及新文件真实浏览器回归。见[设计](109-t07-staging-file-client.md)/[测试](110-t07-staging-file-client-tests.md)。无新增依赖/迁移。
- **T07仍partial**：业务实体绑定/附件授权、完整文件目录、真实ClamAV病毒库/识别/健康及部署ACL验收未关闭。T04/T05/T06/T08各自剩余范围保持真实状态；不以软件片段或测试种子代签整包、生产或现场安全。

### T04 当前授权固定审计快照导出

- 仅业务审计未过期固定快照及显式安全列可导出，当前read与独立export字段/Own/All逐成员复验，Departments不放大。600秒TTL/原actor/session、永久tenant/operation、相同原selection明确重试，换列/快照拒绝；准备后/提交后再次授权，不从回执缓存二进制或授权。
- 新metadata回执/一条AuditExported/既有Outbox原子提交后才交付CSV；真实SQL失败回滚，无旧审计复用伪造新回执。新迁移仅建回执表及复合键/外键/索引，EF无pending；生产未执行DDL。
- 新增 **8项HTTP、14项真实MySQL owner及8项客户端**通过；全前端 **228 passed**及严格构建、真实HTTPS Chromium逐序核对60个固定成员/选列/文件名并断言数据库仅1条导出审计。最终当前已编译源的完整串行Release Architecture **29**、Modules **479**、Persistence **507**、Route **228**，共 **1243 passed、0 failed、0 skipped**。既有1秒依赖健康探测此前两次波动已据实记录，未改合同/掩盖失败。见[设计](111-t04-authorized-audit-export.md)/[证据](112-t04-authorized-audit-export-tests.md)。
- **整包状态不变**：T04其余DTC适用矩阵与生产/运维/人工证据、完整T06及T05/T07/T08剩余工作尚未全部验收；不得以此关闭五个整包或调用完成工具代签。

### T07 当前授权暂存目录

- 当前read全部metadata字段逐字段计算Own/creator当前部门/All交集，SQL内tenant/真实creator/引用计数和实际引用过滤后稳定分页；状态筛选、25/50/100及offset上限严格校验。仅未绑定暂存目录，不用文件权限绕过业务实体授权，不读取物理内容/扫描或产生审计。
- Files页面已加入显式目录、共享实时分页及状态筛选。选择只传Id并重新读取当前详情/版本；同查询503保留Stale但禁选，401/403或换scope/过滤即时清行，迟到响应不落入新范围；重复Id、非法metadata/分页形状拒绝，离线不排队，无新增本机缓存。
- 新增**14项HTTP、8项真实MySQL owner、12项客户端**通过；既有及新增授权文件owner共52项，前端全套**240 passed**及严格构建。真实HTTPS Chromium目录加载后验证375/768/1440、键盘选择后重读、原字节下载及单条审计。完整Release构建**0警告/0错误**，最终全套Architecture **29**、Modules **493**、Persistence **515**、Route **228**，共**1265 passed、0 failed、0 skipped**。首轮架构失败及修正见[证据](114-t07-authorized-staging-catalog-tests.md)，没有放宽边界测试；[设计](113-t07-authorized-staging-catalog.md)。
- **T07仍partial**：业务绑定/实体附件授权与完整业务文件目录、真实ClamAV/病毒库/识别/健康、部署ACL未验收；T04/T05/T06/T08剩余门禁不变。暂存发现能力完成不等于五个整包已关闭。

### T08 禁用仓库/库位创建基础

- WMS owner新增仓库/库位及永久operation创建事实，默认禁用且仓/Pack/schema归属固定；同次精确事实门禁、tenant复合FK/code唯一、并发同operation/同code仅一效果、保存后故障/取消回滚及安全DB错误已实测。schema未配置/拒绝/不可用不能默认成功，schema成功不是启用/分配许可。
- 真实information_schema反例发现Oracle10.0.7忽略CreateTable列collation，5/5列错误；追加显式binary修正迁移后列属性及schema大小写查询通过，不修改已执行迁移，不改旧库存表，不自动伪造历史主数据。EF无pending，增量SQL已查，无生产DDL。
- 新增**14领域+34真实MySQL**测试通过，与原账本/预留联合**82 passed**；最终全solution Release构建**0警告/0错误**，完整Architecture **29**、Modules **507**、Persistence **549**、Route **228**，共**1313 passed、0 failed、0 skipped**。见[设计](115-t08-location-master-data.md)/[证据](116-t08-location-master-data-tests.md)。
- **T08仍partial**：业务当前授权/Host/API/页面、编辑启停/占用/预约/锁/活动证据、层级/容器绑定、Outbox与运输完成证明未验收。测试schema是受控合同fixture，不代替真实Pack或物理安全签字；其余四包剩余门禁保持不变。

### T08 当前授权主数据详情

- WMS公开单资源读取owner边界与当前authority端口，不引用Platform程序集/私表。creator来自WMS永久创建事实；Platform公开字段查询自行查creator当前目录部门，再逐次核验调用者当前会话/成员/角色/目录，精确匹配warehouse/location permission和完整字段；缺creator/字段拒绝，不返回半投影。
- 新增**22项真实MySQL联动**通过，包括Own/Departments/All、另一owner移部门/撤角色/成员/session、实际跨tenant资源、错误字段比较器、缺策略/结果、取消及安全DB错误。联合主数据/账本/Platform回归**159 passed**，领域/授权**32 passed**、完整架构**29 passed**；Release全solution构建**0警告/0错误**。见[设计](117-t08-current-master-data-reads.md)/[证据](118-t08-current-master-data-reads-tests.md)。
- 本片无迁移/依赖/前端变化，仅运行相关定向验证，不声称已重跑整套.NET/Vitest。真实Platform-backed适配器仍仅用于测试装配，尚无Host生产注册、HTTP/目录/页面或创建授权；其他T08生命周期与五包剩余门禁保持未完成。

### T08 默认关闭的主数据详情HTTP

- Host已通过公开应用边界装配真实Platform当前authority，复用现有JWT `tid/sub/sid`解析。显式`Wes:WmsMasterData:Enabled`且Identity启用才映射两个GET，默认不注册；HTTPS/no-store、完整固定字段、当前角色/字段及登出撤销、实际跨tenant资源、禁止写动词已验证。不自动授予既有管理员权限或执行迁移。
- 新增**6项配置+11项真实MySQL/JWT HTTP**，最终完整串行Release Architecture **29**、Modules **524**、Persistence **571**、Route **228**，共**1352 passed、0 failed、0 skipped**；全solution构建**0警告/0错误**。首次7项500与复用既有JWT subject的修正据实记录，未改claims/断言；见[设计](119-t08-master-data-detail-http.md)/[证据](120-t08-master-data-detail-http-tests.md)。
- 本片无新迁移/前端变化，HTTP新用例使用TestServer，既有两套真实HTTPS浏览器由完整回归覆盖，不冒充WMS新增浏览器验收。列表/树/页面、当前创建授权、编辑启停/容器/Outbox及其他T08范围尚未完成；T04/T05/T06/T07/T08整体仍不关闭，生产/现场及ApplicationNotAssembled门禁不变。

### T08 仓库/库位只读详情客户端

- `/wms/locations`已交付PAGE-WMS-001详情子集，不把已知UUID查询当作目录/树。Wms客户端模块开关与仓/位任一read权限控制入口，页面分别要求完整6/9字段；真实API仍逐次复验资源范围。成功显示定义/版本/UTC和读取时间，不把Enabled当空闲/可分配/物理许可；无管理写入口。
- 切换类型/UUID/主体/字段撤销立即取消清旧，迟到成功/错误均丢弃；严格拒绝错Id/缺字段/假boolean/非法UTC或JSON；同查询失败仅显示明确陈旧投影，401/403/404清旧；离线不发送/排队，恢复需手工读取，无新增业务存储。
- 新增**31组件+5路由+2导航=38项**，前端全套**278 passed**和vue-tsc/Vite构建通过；真实HTTPS Kestrel+MySQL+Chromium验证双资源/精确字段、独立owner撤字段后403清旧、375/768/1440、键盘focus-visible、桌面200%字体及离线恢复无自动请求。完整Modules **525**、Architecture **29**，共**554 passed / 0 failed / 0 skipped**，包含三套真实HTTPS浏览器；Release全solution构建**0警告/0错误**。失败与修正、复跑命令见[设计](121-t08-master-data-detail-client.md)/[证据](122-t08-master-data-detail-client-tests.md)。
- 本片无迁移/依赖/生产授权变化，未重跑未改动的Persistence/Route；上片1352是历史完整结果，不叠加冒充本片全量。**T08及T04–T07仍partial**：主数据列表/树、当前创建授权、编辑启停及活动/占用/预约/锁证据、容器绑定、受权库存/流水/快照导出、单位/FIFO/FEFO/序列集成、Outbox与运输完成证明等未验收；真实Pack、其他页面/终端及生产/现场门禁保持阻断。

### T08 当前授权仓库/库位目录

- 两个只读目录GET复用默认关闭装配；Platform当前session/目录/角色逐字段计算Own/Departments/All交集并解析真实creator，WMS通过公开端口取UUID集合，在自己的tenant/创建事实SQL内授权后排序分页，禁止先分页再过滤，无total/隐藏creator。精确Code/Enabled/库位Warehouse筛选与25/50/100及offset上限明确，缺字段403、受权空范围200空行，不返回成功式错误页。
- 既有页面增加显式目录、共享实时分页及固定卡片；选择Id后重新GET当前详情，不将目录行当最新授权。切类型/过滤/scope立即取消清行，503同查询陈旧禁选，401/403/404或非法页清行；详情与目录403双向清理。离线不发送/排队、不自动恢复重读，无新增业务本机缓存。
- 新增**30真实owner+18 HTTP+28客户端**；前端全套**306 passed**及严格构建。真实HTTPS/MySQL/Chromium覆盖双目录/选择后重读/三种宽度/Tab/Enter/200%桌面字体/独立owner撤字段403清双侧。完整.NET Architecture29/Modules543/Persistence599/Route228共**1399 passed**后补两个SQL物化后取消反例，修正后主数据owner86+HTTP/浏览器30+Architecture29共**145 passed / 0 failed / 0 skipped**，Release重建**0警告/0错误**；两轮结果精确归属不叠加。见[设计](123-t08-authorized-master-data-catalog.md)/[证据](124-t08-authorized-master-data-catalog-tests.md)。
- 无迁移/依赖/默认授权/生产许可变化，旧单资源GET shape不变；**T08及其他四包仍partial**。树/层巷、当前创建授权、编辑启停/活动占用预约锁、容器、受权库存/流水/固定快照导出、规则集成、Outbox及运输完成仍待；真实Pack/现场/终端门禁不变。

### T08 当前授权的默认禁用创建应用边界

- 新公开WMS writer强制显式当前authority/schema/clock/logs，不自动迁移或注册Host写入口。主体tenant/actor/session仅来自可信上下文；当前`wms.warehouses.write` Code/Name或`wms.locations.write`全部五字段复验，库位另需当前父仓完整read，后续schema不可用/拒绝保持显式结果，不提供default-success adapter。
- 既有核心输入规则提取共享校验而不改语义；授权在永久operation重放/写事务之前，撤权后重放也Forbidden且无Id/Version。新scope显式恢复、CodeConflict/IdempotencyConflict不泄漏winner，资源与永久创建事实原子写；默认禁用不代表分配/现场安全，无库存、跨模块伪原子审计或Outbox新增效果。
- 新增**38项真实owner**，包含Own/Departments/All、逐字段/精确case/错误集合比较器、当前role/成员/session/字段撤销后的重放拒绝、真实foreign tenant父仓、schema/authority取消与安全DB错误、并发同operation/同code单效果。最终全部主数据 **124**、领域/配置/只读HTTP/真实HTTPS WMS浏览器 **50**、Architecture **29**，共**203 passed / 0 failed / 0 skipped**，Release全solution构建**0警告/0错误**；见[设计](125-t08-current-authorized-master-data-creation.md)/[证据](126-t08-current-authorized-master-data-creation-tests.md)。
- 本片无前端/HTTP/配置/模型/迁移/依赖变化，仅跑相关回归，不冒充再次完整.NET或Vitest。**T08及T04–T07仍partial**：真实Pack适配器与Host创建接线/表单、树、编辑启停/占用预约锁活动证据、容器、库存/流水/固定快照导出、规则与Outbox/运输完成等未验收。真实schema使用合同fixture，不代签Pack或生产/现场。
