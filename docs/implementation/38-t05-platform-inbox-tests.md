# T05 Platform owner-local Inbox 测试记录

- 状态：Inbox 软件切片通过定向和 Persistence 回归；T05 整包仍未完成。
- 数据库：授权隔离 MySQL `wes_test`，使用随机生成 tenant fixture；迁移仅由测试上下文应用。

## 红绿与验证

新增模型/重复投递测试先因 Inbox 类型、DbSet 和 consumer API 不存在而编译失败。实现后，真实 MySQL 验证第一次投递提交收据和 owner 审计效果，顺序重投不再调用 handler；修改同 EventId 的 payload 会拒绝为身份复用。

并发测试使用两个独立 owner context，并在 handler 屏障处确保竞争发生；handler 各自进入，但唯一键仅允许一个 Inbox/审计/outbox 事务提交，另一个回滚并得到 `Duplicate`。故障注入验证 handler 异常时收据、审计和 Outbox 均为零。迁移测试断言只新增 Inbox 表与索引、主键为 tenant/consumer/event，EF model 无待迁移变化。

90天保留测试在真实隔离MySQL中验证恰好90天的边界、较新的收据保留、每批上限、跨tenant隔离、事务回滚及普通代码直接删除仍被拒绝。测试没有启用后台清理Job；当前只验证owner-local清理原语。

| 验证 | 结果 |
|---|---|
| `PlatformInbox` 定向测试 | 6 passed，0 failed |
| Persistence Release 全量 | 341 passed，0 failed，0 skipped |
| 全 solution Release 串行回归 | 本切片未重跑；不把之前回归结果当作本轮验证 |
| 真实 Broker / consumer ACK 丢失 | 未执行；隔离数据库重投测试不代表 Broker 验收 |
| Inbox 保留/清理 | 90天owner-local有界清理原语通过真实MySQL测试；托管清理Job和真实Broker过期重投未执行 |
| 其它 Provider / owner | 未执行；不计为通过 |

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~PlatformInbox --verbosity quiet
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --verbosity quiet -m:1
```
