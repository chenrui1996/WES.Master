# T05 Platform 消息诊断客户端

- 状态：已验收，仅本片范围。
- 范围：REQ-NTF-004..008、PAGE-NTF-003、FUNC-NTF-001 的 Platform owner 只读切片；不关闭 DTC-NTF-001/002 或 T05。
- 基线：[消息详细设计](../detailed-design/notifications-and-jobs.md)、[既有 owner API](39-t05-platform-message-operations.md)、[就绪台账](readiness-register.md)。
- 测试：[本片测试](128-t05-platform-message-diagnostics-client-tests.md)。

后续：[受权重放界面](129-t05-authorized-message-replay-client.md)已在独立切片实现；以下“不提供重放按钮”描述仅指本片初始交付，不是当前页面永久限制。只读查询的原字段/分页/权限合同保持不变。

## 设计与操作

在 `/notifications/messages` 显示明确限定为 Platform 的 Outbox 元数据。沿用 Platform 模块 UI 开关及当前 `platform.messages.read`，不新建 Notifications 假 owner，不跨模块查询内部表，不添加依赖、迁移或默认授权。数据来自既有 `GET /api/platform/messages?offset=N&limit=N`；API 契约不变。选择此增量而非搭建通用聚合器，是因为目前只有 Platform 提供已实现的受权消息查询边界。

首次不自动查询；操作者显式读取。分页复用实时分页，25/50/100，客户端 offset 上限10000。此为客户端有界浏览限制，不改变服务端既有范围。没有 total、固定快照、Broker 健康、Inbox/其他 owner 计数或业务完成推断。Published 仅表示 Broker 确认，不表示消费成功或业务完成；0 行只表示当前查询无行。

必显字段 EventId、Owner、Status、CreatedAt 要求当前同权限 All 范围 grant 字段并集包含完整四字段。可选固定元数据 ContractName、SchemaVersion、Attempts、NextAttemptAt、PublishedAt、LastErrorCategory 按当前 All 字段并集显示；本地不缓存业务行或列偏好，不提供 payload、详情或重放按钮。重放 API 保持既有行为，后续表单必须另行完成理由、预览与并发验收。

API 成功必须是明确 `authorized: true` 和有界 rows，每行严格匹配既有 10 字段合同、非空 UUID、有限状态/错误类别、非负整数和真实 UTC 时间；不信任未知正文、字段或 owner。可选字段授权缺失时即使服务端错误返回也不能展示。必要字段缺失、非法 JSON/响应均清旧并明确报错，不用空页伪装失败。

主体/会话/grant 改变同步取消并清旧；取消请求及序列/身份指纹共同阻止迟到响应。401/403/404 清旧；同查询临时错误可保留明确陈旧参考。离线取消在途、旧行标陈旧、禁用查询与分页，不排队、不自动恢复查询。再次查询由服务端复验当前授权。页面不声称跨请求授权快照。

## 验证边界与部署

复用现有语义按钮、QueryState、RealtimePager 和样式 token；卡片允许 UUID/长值换行，验证移动/桌面、键盘与错误状态。客户端组件测试使用可控 HTTP 响应，只证明客户端合同；真实 owner/HTTP 既有测试单独回归，不当作真实 Broker 验收。

本机本次探测未发现 docker、podman、rabbitmqctl、rabbitmq-server、erl 命令；未安装服务或连接未知环境。真实 Broker 故障窗口、其他 owner Outbox/Inbox、跨 owner 聚合、消息重放界面及生产/现场证据仍未完成。发布为普通客户端构建；回滚此页不改变消息事实或服务端 API。
