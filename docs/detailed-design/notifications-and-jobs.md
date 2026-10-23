# 通知、任务、缓存诊断页面与详细测试

- 状态：评审中；DTC均designed/未执行
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

| FUNC定义 | PAGE/REQ/原TC | 主流程与失败恢复 |
|---|---|---|
| FUNC-NTF-001 | 003；REQ-NTF-004..008；NTF-TC-001..005/015 | 1owner事务保存业务+Outbox；2publisher持久竞争发送；3consumer以MessageId/业务状态去重；4瞬时退避/永久隔离；5重放核版本/权限/期限/物理能力。ACK丢失可重复投递但不可重复逻辑效果；业务结果不从Broker ACK推导 |
| FUNC-NTF-002 | 004；REQ-NTF-001..003；NTF-TC-006..008 | 1校验注册Job/计划/时区/租户/并发策略；2版本化保存；3触发生成executionID/触发来源；4持久租约/业务幂等执行；5取消/超时记录实际进度。旧schedule积压按批准misfire策略，不能启动即全量补跑 |
| FUNC-NTF-003 | 001/002；REQ-NTF-009..014；NTF-TC-009..011 | 1持久告警事实；2聚合窗口内保留occurrence计数；3独立创建渠道delivery；4确认/来源清除；5推送失败以查询恢复。告警/投递/问题三套事实独立，屏蔽通知不改变联锁；并发确认使用版本 |
| FUNC-NTF-004 | 005；REQ-NTF-009..014；NTF-TC-012 | 1草稿校验正文/受众/起止；2授权发布；3查询按当前受众/有效期；4撤回留审计并刷新缓存。过期/撤回不继续由离线历史当有效公告；不得承诺紧急送达 |
| FUNC-NTF-005 | 006；REQ-PLAT-013..014；NTF-TC-013..014 | 1查各domain缓存分类/来源；2标记Disabled/Unavailable/Stale；3只读可回源则限流查询；4安全状态依赖丢失暂停相关写；5派生缓存按owner重建。缓存miss不授权资源，不能修改ledger |

所有管理命令使用operationID、expectedVersion、reason/idempotencyKey；channel配置只引用secret。重放记录源MessageId与新attempt，不生成一个绕过去重的新业务身份。缓存TTL/Outbox保留不能短于经批准的重试/恢复窗口。

## 3. 详细测试

TA消息M1、业务计数器B1=0、告警A1 Active、Job J1为幂等测试业务计数任务；禁止使用真实设备Job。每变体C0重置。

| DTC定义/功能/原TC | 数据和编号步骤 | 断言 | 层级/清理 |
|---|---|---|---|
| DTC-NTF-001 / 001 / NTF-TC-001..003/ARCH-TC-004 | B1为隔离WMS入库IN1；1在业务提交前注入DB失败；2新基线事务成功但Broker断线；3恢复Broker；4消费提交后ACK前崩溃/重投 | 失败事务B1=0/Outbox0；成功基线业务记录1、Outbox可查；最终消费效果1；页面分开展示投递和业务状态 | P0/B+C+E/C0 |
| DTC-NTF-002 / 001 / NTF-TC-004..005 | 临时错/永久schema错/超预算变体；1投递；2U2越权重放；3授权两次重放；4旧版本/过期物理消息重放 | 分类分别有界重试/隔离；越权0；合法重复重放逻辑效果最多1；旧schema/过期控制拒绝，审计每次管理尝试 | P0/B+C+E/C0 |
| DTC-NTF-003 / 002 / NTF-TC-006..008 | J1同key同fireTime两节点，TA/TB隔离；1同时触发；2执行中请求取消/超时；3重启，另测旧计划升级 | 幂等效果1；进度/取消请求与真正终态分开；缺租户不执行；无隐式重复fire，misfire按固定批准策略 | P0/B+E/C0 |
| DTC-NTF-004 / 003 / NTF-TC-009 | A1来源S、资源R、窗口W已配置；1同occurrence重复送；2新occurrence在W内；3并发确认/来源清除 | occurrence去重，重复观察与次数符合策略；状态/确认来源可查；独立issue仍未解决；不因清除释放设备预约 | P0/B+E/C0 |
| DTC-NTF-005 / 003 / NTF-TC-010..011 | U1订阅TA、断线；邮件临时/永久错变体；1告警；2撤U1权限；3重连；4恢复邮件 | 告警持久1；delivery独立重试/失败；失权用户不收补推；回查只授权告警，Sent不标已读 | P0/B+C+E/C0 |
| DTC-NTF-006 / 004 / NTF-TC-012 | 公告N1受众TA、[T0,T0+1h)；1查前/中/边界；2撤回；3U3访问；4正文含测试脚本 | 仅有效受众/时段可见；撤回历史留存、当前列表消失；不执行脚本；无跨租户泄露 | P0/B+E/C0 |
| DTC-NTF-007 / 005 / NTF-TC-013..014 | ledger有grantG1，缓存缺失/旧G0/Disabled变体；1打开诊断；2请求grant；3调用禁用写；4重建 | 不从缓存授权；G1仍保护资源；Disabled明确返回；界面非健康/空成功；缓存恢复来自ledger | P0/B+E/C1 |
| DTC-NTF-008 / 001/002/003 / NTF-TC-015 | 注入1000消息积压、最老时间T0，配置告警阈值N；1跨阈值；2查面板/指标/脱敏日志 | 数量/age真实；阈值触发记录；指标无单号/用户/设备无限标签；定位通过受控日志关联而非标签爆炸 | P1/B+E/C0 |

## 4. 阻断与实现顺序

Broker生产策略、重试/保留/容量、Quartz持久化/集群形态、misfire/时区、公告受众/已读、聚合窗口、清除权需批准。先做Outbox/Inbox与失败用例，再做告警/投递双状态，最后做管理页与计划控制；不提供“清空所有失败记录”掩盖故障的操作。
