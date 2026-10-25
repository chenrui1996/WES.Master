# T05 Platform owner-local Outbox 首片

- 状态：部分完成；只覆盖 Platform owner 的安全审计事件原子 Outbox 持久化，不代表 T05 投递闭环完成。
- 依据：[T05 工作包](../superpowers/plans/2026-10-23-wes-development-work-packages.md)、[通知/消息合同](../modules/notifications-and-jobs.md)、[DurableEvent 合同](../contracts/01-business-and-execution-contracts.md)。
- 测试记录：[T05 Outbox 测试](36-t05-platform-outbox-tests.md)。

## 实现

Platform DbContext 保存新增 `PlatformAudit` 时，在同次 `SaveChanges` 中为明确允许发布的非敏感事实生成 `PlatformOutboxMessage`。审计、业务写入与 Outbox 共用 owner context 和上层事务，不以 Broker ACK 推导业务提交。事件 ID 与源审计 ID 相同，contract 为 `platform.audit`、schemaVersion 为 1，tenant/event 组成 owner 主键；EF 查询过滤继续绑定受信任 tenant。

本切片允许的动作是 `RoleChanged`、`MembershipChanged`、`SettingChanged`、`UserChanged`、`DepartmentChanged`、`TenantChanged`、`DictionaryChanged`。载荷只包含动作及前后不透明版本，不含用户名、自由文本、凭据、原始请求或 grant 快照。`SessionRevoked`、`PasswordChanged`、`AccountDisabled` 和 `CredentialCreated` 不发布。`platform_outbox_messages` 及 `ContractName` 的后续增量迁移只增加 Platform owner schema；不自动迁移生产库。

所有事件初始为 `Pending`，创建时间和发生时间来自源审计的 UTC 时刻。schema 保存发布状态、尝试次数、下次尝试、完成时刻和有限错误分类；当前仅实现失败后的重试/隔离转换，发布领取、确认和完成转换仍待 publisher 接线。

当前另有 Outbox 失败状态转换：仅有限的 Broker 不可用/超时类别采用 1/5/30/120/600 秒退避，最多五次重试；初次发布加五次重试均失败后转 `Quarantined`。首次失败时刻持久化作为 15 分钟自动预算起点；永久合同/授权错误直接隔离。调用方仍须在事务中保存状态。该实体状态策略不等于已实现调度器，也没有 publisher 在投递前检查到期时间。

Platform owner 另提供单消息 lease claim：按 tenant query filter 读取，并以 `Status` 和 `LeaseId` 乐观并发令牌争抢；同时竞争最多一个 claimant 成功。lease 到期可被重新领取，过自动预算的待重试消息在 claim 时持久隔离。成功/失败状态转换均验证当前未过期的 lease identity，旧 publisher 不能覆盖新 owner 状态。另有一次一条消息的 publisher 流程，使用显式 transport outcome 完成确认/退避/隔离事务。

## 未完成边界

- 尚无 Inbox 表/去重消费者、定时扫描器、RabbitMQ/MassTransit 具体transport或运行时注册；现有 publisher 只能由受控调用方针对单个 event ID 调用，测试使用的 transport 是隔离软件替身。
- 尚无 1/5/30/120/600 秒调度、15 分钟总 deadline、永久隔离、运维查询/授权重放或 MessagesPage。
- 其它 owner 尚无本地 Outbox/Inbox；本片不可推断为跨模块事务或 exactly-once。
- `Features:Capabilities:Outbox`/`MessageQueue` 仍因没有完整 runtime owner 而启动拒绝；当前不启用或宣称生产投递。
- 未安装/启动 RabbitMQ，未验证真实 Broker、生产容量、SQL Server/PostgreSQL、DBA 发布或现场设备。

该切片仅证明 Platform 业务事实和安全 Outbox 事件可以同 owner 持久化；T05 按计划继续实施与验收。
