# T05 Platform owner-local Inbox 消费首片

- 状态：部分完成；实现 Platform owner 的消费收据及单事件幂等事务，不代表 T05 整包完成。
- 依据：[T05 工作包](../superpowers/plans/2026-10-23-wes-development-work-packages.md)、[Outbox 首片](35-t05-platform-outbox.md)。
- 测试记录：[T05 Inbox 测试](38-t05-platform-inbox-tests.md)。

## 实现

Platform 增加 `platform_inbox_receipts`，主键为 tenant、稳定 consumer ID 和 EventId；同时保存 contract/schema、完整 wire envelope SHA-256 digest 与处理时间。租户 query filter 与 owner DbContext 保持隔离，EF migration 只新增 Inbox 表和查询索引。

`PlatformInboxConsumer` 接收已验证 tenant 相同的稳定 envelope，在一个显式 owner 数据库事务内先检查收据、登记收据并调用 owner handler，最后一起保存并提交。重复投递返回 `Duplicate` 而不重新应用已提交结果；竞争投递由复合唯一键仲裁，失败事务回滚后复查已提交收据。相同 EventId 若 contract、schema 或 envelope 内容变化会明确拒绝，避免把身份复用误当正常重复。

每次处理使用全新、tenant-scoped Platform DbContext；consumer handler 必须只暂存该 owner 数据库变更，不得执行事务外副作用。并发竞争时 handler 可以被多个 consumer 调用，但只有一个事务的业务写入与 Inbox 收据可提交；外部发布仍须由 Outbox 承担，不能由 handler 直接完成。该语义是至少一次投递下的数据库效果幂等，不声称端到端 exactly-once。

## 90天保留软件切片

`PlatformInboxRetention.RemoveExpiredAsync`按`ProcessedAt <= now - 90 days`选择到期收据，稳定按处理时间、consumer和event排序，每批限制1..200条。调用者必须提供owner数据库事务，并负责调用`SaveChanges`后提交或回滚；数据库查询过滤和持久层owner校验共同限制当前tenant。常规代码仍不能直接修改/删除Inbox收据，只有清理原语选出的、当前owner下已跟踪且确实过期的收据可删除。

现有Inbox migration已包含`TenantId, ProcessedAt, Consumer`索引，本切片无schema迁移。此原语不注册周期Job、不自行提交事务、不触碰Outbox或业务效果；清理Job调度、运行时owner注册和过期重投现场验证仍未完成。清理只在PAR-09既定58天最大恢复窗口之外开始，不代替领域唯一身份/tombstone。

## 未完成边界

- 尚无实际消息订阅/ACK 适配器、consumer 注册、托管 publisher 周期扫描或 broker 崩溃窗口验证。
- Platform 有界 Outbox due scanner 见[Outbox实施记录](35-t05-platform-outbox.md)，尚未接入托管周期调度或运行时 transport。
- Inbox 90天保留的owner-local有界清理原语已实现；尚无托管保留/清理Job，不会自动执行；不得通过任意删除收据触发重放。
- 本首片只实现 Platform owner Inbox；其它业务 owner 的 Outbox/Inbox、授权查询/重放及 Messages 运维页仍未完成。
- 只在隔离 MySQL 验证；没有真实 Broker、SQL Server/PostgreSQL、生产权限或容量证据。
