# T05 WMS owner-local Outbox发布器

- 状态：实施中；REQ-NTF-004..008、PAGE-NTF-003/FUNC-NTF-001、DTC-NTF-001/002相关子集；RULE-14/PAR-05。
- 依据：[通知规格](../modules/notifications-and-jobs.md)、[创建Outbox](135-t05-t08-wms-creation-outbox.md)、[契约](../contracts/01-business-and-execution-contracts.md)。
- 测试：[158](158-t05-wms-owner-outbox-publisher-tests.md)。

## 范围和保守装配

复用已有Platform“独立owner、耐久租约、有限transport结果、有界批处理”模式，不跨模块访问私表或共享DbContext。不新增框架/依赖/通用总线；WMS自己的公共transport envelope保持135定义不变，payload始终只含批准的非物理创建事实。公共独立publisher scope不暴露DbContext/实体，也不自动迁移。没有Host worker/Broker adapter/消费者时不发生任何发布；不使用成功替身。真实Broker/ACK丢失/消费者Inbox仍是后续独立证据，不据控制transport探针关闭T05。

## 持久状态与竞争

只扩展delivery：Pending(0)→Leased(1)→Published(2)/Pending/Quarantined(3)。首次创建仍Pending；envelope、源事实、eventId永久不变，不删除/重建事件。新增DeliveryVersion CAS nonce、Attempts、FirstFailureAt、LeaseId/Until、NextAttemptAt、PublishedAt、有限LastErrorCategory。专用owner保存入口只准修改这些delivery字段，普通SaveChanges仍拒绝修改/删除已提交消息；源事实创建门禁不放宽。

90秒lease先独立提交，随后才调用transport；不将外部IO包在DB事务内。提交完成后、实际发送前再次检查lease和预算：提交跨过90秒不发送，跨过15分钟先持久隔离而不是额外发一次。只有当前未过期lease的ACK且delivery CAS提交成功才报Published。租约过期或ACK后保存争用报Unconfirmed（不是失败/成功推断），保留已提交状态；异常/取消或崩溃不吞掉，原eventId在租约过期后恢复，消费者仍需Inbox去重。每个batch1..100、SQL当前tenant过滤、NextAttemptAt/EventId稳定排序，顺序处理，owner clean/transaction/ambient拒绝，provider失败安全日志/异常。

与Platform现有计数时点的必要差异：Attempts在耐久认领时递增，计入发送途中崩溃，不让“没有结果的发送”无限不占预算。最多初次加5次重试，共6次实际认领；瞬时失败后1/5/30/120/600秒，在线0..20%抖动、测试固定样本。首次确认失败开始15分钟预算；过期lease视为一次不明尝试并启动该预算。永久契约拒绝立即隔离，达到次数/预算保留Quarantined。无自动物理重发/管理重放或TTL删除。

## 迁移与既有读合同

新增WMS delivery列、CAS与due索引；已有Pending行保留原envelope/occurredAt，NextAttemptAt从OccurredAt回填、DeliveryVersion从EventId初始化，不伪造ACK/历史attempt。只在授权wes_test迁移；生产DDL/回滚需DBA批准，Down不能丢交付状态。

九字段事件页形状不变，Status明确扩展到四个已批准delivery状态；客户端以固定标签显示，不把Published当业务完成。三字段summary形状不变且PendingCount仍仅统计Pending，Leased/Published/Quarantined不是Pending，也不伪造Inbox/跨owner总和；存储未知状态仍明确失败。publisher没有Host自动装配，默认既有页面仍读Pending；新客户端与状态合同同批发布，不静默假定旧pending-only检查可沿用。

## 运行和故障观察

仅显式基础设施组合可创建`WmsPublishingScope(connection, trustedTenant, transport, clock, logs)`，调用`DispatchDueAsync(1..100, cancellationToken)`；不得从HTTP请求接收连接串/transport/任意tenant。结果五个计数为Published/RetryScheduled/Quarantined/NotClaimed/Unconfirmed，不是业务效果数或共同快照。没有Host周期调用与Broker adapter时即使迁移已应用也不会外发；本片不新增部署开关或示例秘密。

transport须把预期拒绝映射到有限结果，合作取消，使用有界IO与安全异常；未知程序异常向上冒泡，不转成ACK/空批次，调用方不得记录原始SDK错误/正文。数据库异常复用WMS安全异常，不带provider正文/inner exception；CAS认领竞争不视为成功发送，迟到/未确认记录固定LeaseLost类别。诊断列表只公开原九字段，不借状态扩展泄露lease、nonce、attempt或payload；隔离人工处理/受权重放仍未实现，不能删除或直接改行充当修复。

MySQL迁移先补列再从原EventId/OccurredAt初始化，不执行发送。datetime(6)覆盖nullable时间；抖动通过十进制整数微秒计算，避免double秒加法把1.2秒截成1.199999秒。UTC clock支持1000..9998年以保留lease/预算运算余量；测试固定时钟，不让数据库或UI替换事实发生时间。MySQL DDL不是跨语句原子回滚，部署需备份/DBA执行及失败续迁方案；本次只使用已授权测试库和唯一临时迁移探针表，Down明确拒绝破坏投递证据。
