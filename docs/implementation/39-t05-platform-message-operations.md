# T05 Platform Message Operations

- 状态：Platform owner-local Outbox 授权查询与授权重放已实现；T05 整包仍未完成。
- 依据：[Notifications 消息设计](../detailed-design/notifications-and-jobs.md)及[T05 工作包](../superpowers/plans/2026-10-23-wes-development-work-packages.md)。
- 测试记录：[Platform Message Operations 测试](40-t05-platform-message-operations-tests.md)。

## 已实现：owner-local Outbox 查询

新增 `GET /api/platform/messages`，只从认证主体的 tenant-scoped Platform owner context 读取本地 Outbox。查询要求当前有效会话具有 `platform.messages.read` 的 All 范围 grant，分页上限100；`EventId`、`Status`、`CreatedAt`、`Owner` 是必要投影权限，其余字段依各自字段 grant 单独返回。读模型没有 payload 属性，即使授权字段集包含 `Payload` 也不输出原始消息正文。

查询以 `CreatedAt`、`EventId` 稳定排序，使用独立只读 unit of work 和 tenant filter。HTTP 只接收 offset/limit，不接受 tenant、actor 或权限信息；未经授权返回 Forbidden。该端点没有跨 owner 读取，不代表 Notifications 管理页或跨模块运维聚合已实现。

## 已实现：授权同事件重放

新增 `POST /api/platform/messages/{eventId}/replay`。请求仅接受 `ExpectedAttempts`、`Reason`，幂等键复用标准 `Idempotency-Key` UUID header；tenant 与操作者只来自当前已验证身份。重放必须通过当前活动会话的 `platform.messages.replay` All-scope 授权，并具备 `EventId`、`Status`、`Attempts`、`Reason` 字段权限。

仅允许 owner 为 Platform、contract 为 `platform.audit` v1 且处于 `Quarantined` 的消息。expected-attempts 作为 CAS 前置条件；重放更新与不可变操作回执在同一 owner-local 事务中提交。操作回执以 tenant/operation 为键，保存 actor、事件、裁剪后的理由、先前尝试数、请求时刻与命令 fingerprint。相同键和相同请求重试返回同一成功结果；同键不同请求为冲突；并发状态竞争不会重复提交，已知唯一键竞争仅在回读到回执后归并为幂等结果。

消息只重置到 Pending/due-now，attempts 与自动退避预算归零；`EventId`、contract/schema、owner、aggregate/correlation/causation、occurrence instant 与 payload 原文保持不变。未知/非Platform消息、物理事件及非Quarantined状态不能通过此管理命令重放。响应不返回 payload 或操作理由。

## 未完成边界

- Inbox 保留清理与定期策略尚未实现；重放仅覆盖 Platform 安全审计事件，不表示其他 owner 的重放API已经建立。
- Notifications 聚合页、其它 owner 的 Outbox/Inbox API、真实 Broker、托管 publisher 周期任务和生产恢复演练仍未实现。
- 该读端点仅用于限定范围的诊断元数据，不是审计导出，也不提供 payload 读取能力。
