# T05 Platform owner 消息计数

- 状态：已验收，仅本片范围。
- 范围：REQ-NTF-004..008、PAGE-NTF-003，Platform Outbox 状态与现存 Inbox 收据统计。
- 基线：[消息详细设计](../detailed-design/notifications-and-jobs.md)、[Inbox语义](37-t05-platform-inbox.md)、[消息筛选](131-t05-filtered-message-diagnostics.md)。
- 测试：[本片测试](134-t05-owner-message-counts-tests.md)。

新增 `GET /api/platform/messages/summary`，沿用可信会话/tenant与no-store，独立干净只读owner scope。使用既有 `platform.messages.read`，但明确要求 All 范围的 Owner/ObservedAt/PendingCount/LeasedCount/PublishedCount/QuarantinedCount/InboxReceiptCount 全部字段；原消息行权限不自动获得计数，不新增默认权限。缺任一字段返回403，不返回部分统计或成功空对象。

成功响应 `{ authorized:true, summary:{ owner:"Platform", observedAt:UTC, pendingCount, leasedCount, publishedCount, quarantinedCount, inboxReceiptCount } }`。数据库用按状态分组LongCount与Inbox LongCount，64位非负计数；不拉业务行、payload或consumer身份到客户端。现存Inbox收据是tenant/consumer/event身份数，不是唯一业务效果数、待消费消息数或历史累计成功数，90天清理可使其下降。未识别状态明确失败，不悄悄漏计。

统计覆盖当前租户完整Platform owner，不采用列表状态/时间/分页筛选；页面明确标注此点。Outbox与Inbox是顺序独立读取，ObservedAt为查询结束UTC，不是ACID统一快照或跨owner截止水位，也不保证与后续列表一致。每次数据库读取完成后检查取消，错误显式传播；不把不可用转换为零。

独立客户端计数区，首次不查、手工读取、无自动轮询；严格验证固定结构/owner/UTC/安全整数范围。超过JavaScript安全整数明确拒绝而不四舍五入或改成0。当前统计字段不足禁查；主体/grant/会话变化取消清旧并丢弃迟到结果；403/401/404/非法响应清旧，503可保留标陈旧。离线标陈旧、禁查、不排队、恢复不自动查询。重放提交让已读计数标陈旧，成功后必须手工重读，避免旧隔离数被当作新状态。

本片无新迁移/依赖、默认授权、Broker连接或跨模块私表访问，不改库存/物理控制。Inbox统计无重放或删除入口；未知业务owner不返回0冒充已接入。跨owner统计、真实Broker与T05整包仍未完成。
