# T04/T05 Platform 显式保留维护

- 状态：已验收（仅本地有界软件切片）；补齐既有90天清理的应用边界和托管调度，不关闭T04/T05整包。
- 依据：[批准计划第1节](../superpowers/plans/2026-10-26-platform-retention-candidates-stock-protection.md)、[23](23-t04-command-receipts.md)、[37](37-t05-platform-inbox.md)、[49](49-t05-inbox-retention-save-recovery.md)、WES-RULES-1.0 PAR-09、DTC-PLAT-011、DTC-NTF-002的普通结果保留子集。
- 测试：[88](88-platform-retention-maintenance-tests.md)。

## 所有权与事务

Host只调用Platform公开应用边界，不接触DbContext或内部表。每tenant每tick经`IdentityDatabase.Open`创建、释放新的owner scope；不发现租户、不关闭tenant过滤器。`PlatformScope.RemoveExpiredReceiptsAsync`协调现有DirectoryCommandReceipts及PlatformInboxRetention，在同一个显式事务中暂存两类删除，SaveChanges后Commit，成功后才返回两类计数。拒绝已有跟踪实体或已有事务的非干净scope；所有查询、保存、提交传递取消。测试专用internal核心入口用于真实Provider故障注入，不暴露HTTP维护入口。

两类收据都以同一TimeProvider UTC时刻计算`timestamp <= now - 90days`。批量上限分别作用于两种收据：一个tenant/tick最多N条目录回执和N条Inbox（合计≤2N）；目录回执附属委托证据按既有外键级联删除。审计、Outbox、领域身份及Unknown不在清理集合内。取消/保存/提交失败回滚两类删除，不返回成功计数，不重用失败context。提交响应丢失可能造成提交结果未知，日志只能记失败，不能声称已回滚。

## 默认关闭及部署配置

新增`Wes:PlatformRetention`，仓库默认只配置`Enabled:false`。启用需`Wes:Identity:Enabled:true`及有效Identity部署配置，且显式提供：

```json
{
  "Enabled": true,
  "TenantIds": ["11111111-1111-1111-1111-111111111111"],
  "IntervalSeconds": 3600,
  "BatchSize": 100
}
```

示例tenant仅占位，不是授权。IntervalSeconds为十进制整数60..86400（避免忙循环并至少每日可调度）；BatchSize整数1..200；TenantIds为1..100个不重复、非空D格式UUID。100租户是每tick有界工作量的部署保护，不是租户发现上限。配置键遵循.NET配置的大小写不敏感语义，允许环境变量提供相同键的不同大小写，但不放宽字段白名单或值约束。未知字段、错误标量/数组、重复tenant、非法布尔、空值、非法范围均启动失败，错误消息不回显配置。禁用时缺失附属设置合法，但已提供的设置仍须合法；禁用不注册维护服务、不打开数据库、不创建计时器。

## 调度、失败与取消

启用后立即一个tick，按白名单顺序串行，每tenant每tick一批，完成后用TimeProvider等待配置间隔；不并行、不无限追赶、不持久化补跑，不提供集群单例保证。这是无业务/物理副作用的owner保留维护，不代替DTC-NTF-003的Quartz耐久作业、单例及misfire实现。多实例可能读到同一过期批次；另一owner先提交造成的EF并发删除冲突归类为瞬时失败，下周期用新context重试，不因正常竞争停止Host，也不返回未提交的成功计数。

Platform将已知数据库异常转换为不带inner exception/连接信息的安全失败。瞬时数据库失败记录`database-transient`，继续下一个tenant，下一周期才重试；非瞬时数据库失败记录`database-permanent`并按BackgroundService标准StopHost契约停止，意外程序错误不广泛捕获，同样按标准契约停止。取消记录`cancelled`并传播，不当作成功或数据库重试。每tenant调用日志只包含tenant、生成的correlation、固定分类及提交后两类计数，无原始异常、连接串、payload或凭据。

无自动迁移、Broker、publisher替身、业务TTL释放或生产授权。应用关闭取消当前批次及等待。既有禁用Host测试无新增后台活动。

## 未关闭边界

真实Broker、其他owner、审计归档/保留治理、生产容量与运维验收、SQL Server/PostgreSQL、过期消息重投现场语义仍未完成。整体状态见[就绪台账](readiness-register.md)，不将此清理任务作为上述缺项的成功替代。
