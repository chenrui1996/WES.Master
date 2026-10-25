# T05 Platform Message Operations

- 状态：Platform Outbox 授权查询切片已实现；授权重放仍在进行中，T05 整包未完成。
- 依据：[Notifications 消息设计](../detailed-design/notifications-and-jobs.md)及[T05 工作包](../superpowers/plans/2026-10-23-wes-development-work-packages.md)。
- 测试记录：[Platform Message Operations 测试](40-t05-platform-message-operations-tests.md)。

## 已实现：owner-local Outbox 查询

新增 `GET /api/platform/messages`，只从认证主体的 tenant-scoped Platform owner context 读取本地 Outbox。查询要求当前有效会话具有 `platform.messages.read` 的 All 范围 grant，分页上限100；`EventId`、`Status`、`CreatedAt`、`Owner` 是必要投影权限，其余字段依各自字段 grant 单独返回。读模型没有 payload 属性，即使授权字段集包含 `Payload` 也不输出原始消息正文。

查询以 `CreatedAt`、`EventId` 稳定排序，使用独立只读 unit of work 和 tenant filter。HTTP 只接收 offset/limit，不接受 tenant、actor 或权限信息；未经授权返回 Forbidden。该端点没有跨 owner 读取，不代表 Notifications 管理页或跨模块运维聚合已实现。

## 未完成边界

- `platform.messages.replay` 授权、同 EventId/payload 重放、operation 幂等和操作者理由收据仍未实现。
- Notifications 聚合页、其它 owner 的 Outbox API、真实 Broker、托管 publisher 周期任务和生产恢复演练仍未实现。
- 该读端点仅用于限定范围的诊断元数据，不是审计导出，也不提供 payload 读取能力。
