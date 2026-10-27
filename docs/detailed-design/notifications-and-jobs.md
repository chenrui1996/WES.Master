# 通知、任务、缓存诊断页面与详细测试

- 状态：实施中；局部切片见下，父DTC不得由子集代签
- 权威：[原规格](../modules/notifications-and-jobs.md)；SCOPE-WF-03..05、SCOPE-PLAT-06；REQ-NTF-001..014
- 共用：[标准](00-shared-page-and-test-standard.md)；[RabbitMQ/Quartz参考](01-reference-cases.md)
- 产品阅读：告警列表/投递详情和后台执行诊断，高密度，DENSITY=3、DATA_COMPLEXITY=3、CONSEQUENCE=3（仅管理写入）。

## 1. 页面

| PAGE定义 | 用户/入口/返回 | 线框/字段/验证 | 查询/动作/边界 |
|---|---|---|---|
| PAGE-NTF-001 | 告警查看/确认员；`/notifications/alarms`，或地图关联 | L：告警表 → 事实/次数 → 独立投递记录 → 运维问题链接。code、来源、资源、severity、首次/最近时间、状态、版本、确认人；Notifications owner | 时间/来源/等级/Active/Acknowledged/Cleared筛选；确认、按来源策略清除；设备故障未恢复不人工伪造已恢复；回地图保持对象 |
| PAGE-NTF-002 | 通知查看/渠道管理员；`/notifications/deliveries` | L+F：渠道/状态 → 投递attempt → 安全错误/偏好。message/alert引用、渠道、受众摘要、尝试/下次重试时间、模板版本；地址脱敏 | 状态/渠道/时间过滤；受权重试/偏好保存；不更改告警事实，不将Sent当作人已阅读 |
| PAGE-NTF-003 | 消息运维员；`/notifications/messages` | O+F：Outbox/Inbox/死信计数 → 消息表 → 原因/版本/审计 → 重放申请。messageID、contract/version、cause/correlation、租户、attempt/age、摘要；payload默认脱敏 | 以模块owner、状态、年龄过滤；授权重放需理由/能力检查/预览，物理不可逆消息不能直接重放 |
| PAGE-NTF-004 | 任务管理员；`/jobs` | L+F：Job定义/触发计划 → run历史 → 进度/失败。jobKey、owner、服务主体/租户、schedule/timezone、并发/超时/misfire策略、版本；执行记录与定义分开 | 启停计划/受权触发/请求取消；仅注册白名单Job；不提交代码或任意类型名；取消是请求非已终止 |
| PAGE-NTF-005 | 内容管理员/受众；`/announcements` | L+F：草稿/已发布 → 标题正文/受众/有效期 → 预览。正文不执行脚本；开始<结束、受众由受权范围选择、版本 | 保存/发布/撤回，查看端按受众与有效期；PDA是否展示由策略确认，新闻不是紧急告警通道 |
| PAGE-NTF-006 | 受权诊断员；`/diagnostics/cache` | O：缓存/HotStore是否启用 → 域/投影版本/健康/重建进度。key分类而非秘密原值；来源owner、age、last rebuild；不显示库存缓存为权威 | 按owner/状态过滤；无任意改key/强清活跃预约按钮；恢复受控过程见系统/Route |

所有L页适用UI-001..008/011/012；有表单/管理动作的001..005另适用UI-009/010。006只读不适用UI-005/006/009/010写入场景，无首次创建缓存CTA；003积压0/001告警0为zero-work，错误不能伪装“无告警”。UI-007断网禁写、不排队；停消费者不等于允许延后物理命令自动发出。

## 2. 功能与契约

后续[WMS owner发布器](../implementation/157-t05-wms-owner-outbox-publisher.md)/[证据](../implementation/158-t05-wms-owner-outbox-publisher-tests.md)替代下述历史Pending-only/publisher未实现部分：耐久90秒lease、claim计数最多6次/15分钟预算、有限退避与隔离、提交后发送前复验、原event未确认恢复。原列表九字段扩展真实四态，统计仍三字段且只计Pending，客户端同步标签，不将Published当业务完成。新增33owner/3HTTP/4前端，最终238+32含四套真实浏览器+29=299、前端695和严格构建通过，Release0/0；默认无Host worker/transport外发，Inbox/真实Broker/统一快照/父DTC未关闭。

后续[WMS创建事件范围统计](../implementation/149-t05-wms-creation-event-counts.md)/[证据](../implementation/150-t05-wms-creation-event-counts-tests.md)已接通独立统计字段当前creator交集、SQL全量LongCount与共享计数组件；替代下述历史“WMS统计未接入”。WMS只返回Owner/ObservedAt/PendingCount，不伪造Inbox/投递状态，不与Platform相加；两owner仍独立观察，不构成统一快照。前端604、主数据205+HTTP/三套浏览器36+架构29=270及严格构建通过，WMS发布/消费和真实Broker仍未完成。

现有[消息owner选择与WMS事件客户端](../implementation/143-t05-owner-selected-message-client.md)/[证据](../implementation/144-t05-owner-selected-message-client-tests.md)替代历史“仅Platform页面/第二owner客户端缺失”：原路由通过Platform/Wms各自模块+当前permission成对OR，选择后只查该owner公共API。切换卸载取消/清旧，Platform重放面板打开锁owner；WMS只显示九字段创建事件，无虚构attempt/Inbox0/统计。前端521、真实HTTP/三套浏览器16、架构29通过；跨owner统一快照/统计、WMS发布消费与真实Broker仍未关闭。

第二owner只读基础现有[WMS创建事件API](../implementation/141-t05-wms-creation-event-diagnostics.md)/[证据](../implementation/142-t05-wms-creation-event-diagnostics-tests.md)：当前`wms.messages.read`九字段完整creator范围交集，tenant/creator在SQL先过滤后分页；仅现有Pending创建事实，不显示payload/虚构attempt/Inbox0。独立公共owner读取，无第二owner客户端/跨owner统一快照/统计或真实Broker验收。

T05/T08现增加[WMS创建原子Outbox](../implementation/135-t05-t08-wms-creation-outbox.md)/[证据](../implementation/136-t05-t08-wms-creation-outbox-tests.md)：仅新禁用仓/位创建事实在WMS owner事务派生`wms.master-data.created`/v1安全事件，operation重放不追加，旧事实不自动回填。不含WMS publisher/Inbox或跨owner诊断统计，Pending不是已投递/已启用/可分配；不代签DTC-NTF-001/002真实Broker窗口。

| FUNC定义 | PAGE/REQ/原TC | 主流程与失败恢复 |
|---|---|---|
| FUNC-NTF-001 | 003；REQ-NTF-004..008；NTF-TC-001..005/015 | 1owner事务保存业务+Outbox；2publisher持久竞争发送；3consumer以MessageId/业务状态去重；4瞬时退避/永久隔离；5重放核版本/权限/期限/物理能力。ACK丢失可重复投递但不可重复逻辑效果；业务结果不从Broker ACK推导 |
| FUNC-NTF-002 | 004；REQ-NTF-001..003；NTF-TC-006..008 | 1校验注册Job/计划/时区/租户/并发策略；2版本化保存；3触发生成executionID/触发来源；4持久租约/业务幂等执行；5取消/超时记录实际进度。旧schedule积压按批准misfire策略，不能启动即全量补跑 |
| FUNC-NTF-003 | 001/002；REQ-NTF-009..014；NTF-TC-009..011 | 1持久告警事实；2聚合窗口内保留occurrence计数；3独立创建渠道delivery；4确认/来源清除；5推送失败以查询恢复。告警/投递/问题三套事实独立，屏蔽通知不改变联锁；并发确认使用版本 |
| FUNC-NTF-004 | 005；REQ-NTF-009..014；NTF-TC-012 | 1草稿校验正文/受众/起止；2授权发布；3查询按当前受众/有效期；4撤回留审计并刷新缓存。过期/撤回不继续由离线历史当有效公告；不得承诺紧急送达 |
| FUNC-NTF-005 | 006；REQ-PLAT-013..014；NTF-TC-013..014 | 1查各domain缓存分类/来源；2标记Disabled/Unavailable/Stale；3只读可回源则限流查询；4安全状态依赖丢失暂停相关写；5派生缓存按owner重建。缓存miss不授权资源，不能修改ledger |

所有管理命令使用operationID、expectedVersion、reason/idempotencyKey；channel配置只引用secret。重放记录源MessageId与新attempt，不生成一个绕过去重的新业务身份。缓存TTL/Outbox保留不能短于经批准的重试/恢复窗口。

PAGE-NTF-003 当前局部交付为[Platform消息只读诊断](../implementation/127-t05-platform-message-diagnostics-client.md)，[测试证据](../implementation/128-t05-platform-message-diagnostics-client-tests.md)。页面必显 EventId/Owner/Status/CreatedAt，可选固定字段 ContractName/SchemaVersion/Attempts/NextAttemptAt/PublishedAt/LastErrorCategory，均按当前 All 字段权限；不含payload或业务完成推断。采用固定卡片与实时分页，无可配置列、关联候选或业务写入。跨owner聚合/过滤/Inbox计数/重放表单及父DTC仍未完整实现；仅Platform模块门禁不新增Notifications运行能力开关。

后续已补[Platform受权重放表单](../implementation/129-t05-authorized-message-replay-client.md)/[证据](../implementation/130-t05-authorized-message-replay-client-tests.md)：当前All读/重放字段、准备前重读、理由及expected-attempt预览、二次确认、原operation未知结果显式重试。仅Platform/platform.audit/v1隔离事件，不改变消息正文/身份，不支持物理消息。该局部覆盖UI写入场景，跨owner聚合/过滤/Inbox计数、真实Broker及父DTC仍未关闭。

Platform查询现另有[状态与创建时间筛选](../implementation/131-t05-filtered-message-diagnostics.md)/[证据](../implementation/132-t05-filtered-message-diagnostics-tests.md)：有限状态、UTC创建时间半开范围在owner SQL中先筛选再分页，客户端换筛选清旧、准备预读保留筛选。不带新参数兼容既有GET；未知值明确400。跨owner筛选/Inbox统计与真实Broker仍不在已交付范围。

Platform现另有[owner消息计数](../implementation/133-t05-owner-message-counts.md)/[证据](../implementation/134-t05-owner-message-counts-tests.md)：显式All统计字段授权的Outbox四状态与现存Inbox收据数，两次独立SQL读，不是统一快照/业务效果数/Broker健康。统计不受列表筛选影响，重放后旧统计标陈旧，手工重读；其他owner不返回0伪装已接入。跨owner统计/真实Broker及父DTC仍未关闭。

已定默认按[规则基线RULE-14..17](05-approved-rules-and-defaults.md)：普通幂等非物理消息5次退避/15分钟预算，Inbox/完成Outbox在线90天+领域唯一身份长期保护；Job分类、并发/时区/恢复与参数见PAR-06。通知聚合30秒/普通静默15分钟≤60分钟，高危即时不可静默；事实不抑制。公告7天，租户授权∩组织∩（用户∪角色），空受众拒发，PDA渠道显式启用。VAL-06/07为下表必测变体。

## 3. 详细测试

TA消息M1、业务计数器B1=0、告警A1 Active、Job J1为幂等测试业务计数任务；禁止使用真实设备Job。每变体C0重置。

| DTC定义/功能/原TC | 数据和编号步骤 | 断言 | 层级/清理 |
|---|---|---|---|
| DTC-NTF-001 / 001 / NTF-TC-001..003/ARCH-TC-004 | B1为隔离WMS入库IN1；1在业务提交前注入DB失败；2新基线事务成功但Broker断线；3恢复Broker；4消费提交后ACK前崩溃/重投 | 失败事务B1=0/Outbox0；成功基线业务记录1、Outbox可查；最终消费效果1；页面分开展示投递和业务状态 | P0/B+C+E/C0 |
| DTC-NTF-002 / 001 / NTF-TC-004..005 | 临时/永久/schema/物理Unknown独立变体；1按PAR-06受控时钟无抖动；2越权/授权重复重放；3过期控制/可信迟到完成/过认证时窗；4普通结果90天清理后同领域键提交 | 非物理临时错误仅5次等待1/5/30/120/600秒，15分钟后不新尝试；永久错0重试/Unknown物理0重发；控制过期拒发、迟到事实核验不更新旧位置，过认证报文隔离后query；结果清理仍领域唯一防二效。RVT-05/VAL-07/08必测 | P0/B+C+E/C0 |
| DTC-NTF-003 / 002 / NTF-TC-006..008 | J1两节点同逻辑fireTime，TA/TB；1并发；2到600秒超时取消/再30秒仍运行；3各类misfire/重启；4DST缺失/重复时段 | 业务效果1，Unknown保留运行保护不并行接着跑；诊断跳过/可重算汇总合并一次/物理不补跑；缺租户拒绝，DST缺失跳过/重复只首次UTC且可审计；Job恢复不靠attemptID二效 | P0/B+E/C0 |
| DTC-NTF-004 / 003 / NTF-TC-009 | A1来源S、资源R、窗口W已配置；1同occurrence重复送；2新occurrence在W内；3并发确认/来源清除 | occurrence去重，重复观察与次数符合策略；状态/确认来源可查；独立issue仍未解决；不因清除释放设备预约 | P0/B+E/C0 |
| DTC-NTF-005 / 003 / NTF-TC-010..011 | U1订阅TA、断线；邮件临时/永久错变体；1告警；2撤U1权限；3重连；4恢复邮件 | 告警持久1；delivery独立重试/失败；失权用户不收补推；回查只授权告警，Sent不标已读 | P0/B+C+E/C0 |
| DTC-NTF-006 / 004 / NTF-TC-012 | 公告N1受众TA、[T0,T0+1h)；1查前/中/边界；2撤回；3U3访问；4正文含测试脚本 | 仅有效受众/时段可见；撤回历史留存、当前列表消失；不执行脚本；无跨租户泄露 | P0/B+E/C0 |
| DTC-NTF-007 / 005 / NTF-TC-013..014 | ledger有grantG1，缓存缺失/旧G0/Disabled变体；1打开诊断；2请求grant；3调用禁用写；4重建 | 不从缓存授权；G1仍保护资源；Disabled明确返回；界面非健康/空成功；缓存恢复来自ledger | P0/B+E/C1 |
| DTC-NTF-008 / 001/002/003 / NTF-TC-015 | 初值待发N1000或最老age60秒；独立999/1000、59/60秒变体；1每30秒检查；2持续episode复查；3面板/指标/日志；4在PAR-10基准记录只读API分位 | 边界下无告警，任一达门限下次检查触发一次，计数/age真实且不丢消息；API每次p95≤500/p99≤1500ms否则失败，缺报告未执行；指标无无限业务标签，日志可关联。VAL-06必测 | P1/B+E/C0 |

### 3.1 密集交互与关联配置

遵循[交互合同](03-dense-page-interaction-contract.md)，数字为PAGE-NTF；`!`必显、`+`可选，G/F/R为共用测试配置。

| 页面 | 配置 | 默认顺序/可选列 | 新增/编辑/关联筛选与密集操作 |
|---|---|---|---|
| 001 | G+R | 告警业务码!、资源、等级!、状态!、最近观测!、次数、责任问题链接；+首次时间/确认人/版本 | 来源owner→授权资源候选；当前范围可继承为筛选而非新告警默认。按等级/状态分组，不因收起隐藏未处理计数；来源告警不能由“新增”人工造恢复 |
| 002 | G+F | 投递标识!、渠道、状态!、受众摘要、尝试次数、下次重试；+模板版本/安全错误 | 渠道类型→允许模板/受众/secret引用配置；事件→投递attempt从表只读；受众脱敏且仅授权集合，不按历史地址自动补全。按渠道/状态分组，Sent不改Read |
| 003 | G+F | messageID!、owner、契约/版本、处理状态!、age/attempt、关联ID；+安全原因/重试时间 | owner→允许契约版本→消息；切owner清旧消息选择。重放对象固定消息ID，不能改payload/业务身份；按owner/状态分组；无跨页“全重放” |
| 004 | G+F | jobKey!、owner、计划状态!、时区/schedule、最近run结果；+主体摘要/misfire策略；run从表：runID!、状态!、开始/结束 | 已注册Job→允许参数schema/执行主体/租户范围；换Job清不兼容参数，不沿用另一Job secret或自动补租户。定义/运行列配置各自独立；按owner/状态分组 |
| 005 | G+F | 标题!、发布状态!、生效/到期、受众摘要、更新时间；+版本/创建人 | 受权组织→角色/用户按可见范围选择；已定受众=租户授权∩组织∩（用户∪角色），空集拒绝，预览表达式及渠道。换范围清失效引用；默认7天/非必读，不静默扩送PDA |
| 006 | G+R | 缓存域!、owner、启用/健康!、数据age!、源版本、重建状态；+最后重建/错误摘要 | owner→其投影类别仅筛选；按owner/状态分组。无新增缓存键/任意值编辑/活预约批量清理，显示数量不是库存余额 |

| DTC定义/功能/原TC | 固定前置与编号步骤 | UI/持久化/副作用断言 | 级别/环境/清理 |
|---|---|---|---|
| DTC-NTF-009 / 001..005 / NTF-TC-004..006/009/012..014 | J1/J2参数schema不同、消息M1属于ownerA，告警A1 Active关联I1，受众TA部门D1/D2；1切owner仍携M1重放；2切Job并回旧候选；3改受众父范围；4收起告警组/调列；5看缓存投影 | 非法重放0、未触发Job；旧参数/受众清理且旧响应不复原；没有策略不猜受众；告警等级/状态/age必显且计数保留；I1未被自动解决，缓存/库存/任务写0 | P0/E+B/C0 |

## 4. 阻断与实现顺序

重试/保留、Quartz分类/集群形态、misfire/时区、公告受众/已读和通知窗口按规则基线已定；Broker目标拓扑/容量、具体Job定义、实际责任/告警来源信号、凭据和真实Provider仍需证据。初值不证明生产吞吐；先Outbox/Inbox失败用例、告警/投递独立，再管理页，不以清空失败记录掩盖故障。
