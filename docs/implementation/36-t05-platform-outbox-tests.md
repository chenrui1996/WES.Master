# T05 Platform owner-local Outbox 测试记录

- 状态：首片自动化软件验证通过；T05 整包仍未完成。
- 数据库：仅使用授权隔离 MySQL `wes_test` 与生成租户 fixture；测试运行自动应用 Platform owner migration。

## 红绿与验证

新增两个行为测试先在缺失 Outbox model/setting-event 接线时失败：model 测试断言缺少 owner entity；setting 写入测试断言审计已提交但没有对应事件。实现后，模型、目录用户事件和设置事件定向测试通过。
退避测试先因缺少失败分类及 `RecordFailure` 行为而编译失败；实现后以真实 MySQL 保存、重载 Outbox 行，验证 1/5/30/120/600 秒计划、首次失败时刻持久化及第六次失败进入隔离。
竞争领取测试先因缺少 lease store 与 lease 字段而编译失败；实现后由两个真实 MySQL owner context 并发争抢同一事件，验证恰有一个 lease 成功，且新的 lease 列和过期索引为 additive migration。

| 验证 | 结果 |
|---|---|
| Outbox model 和 Platform additive migrations | 通过；仅新增 `platform_outbox_messages`、索引及 `ContractName` 列，EF model 无 pending migration |
| 创建用户与审计事件 | 通过；contract/version、事件 ID/聚合 ID/版本/correlation 与源审计一致，状态为 Pending，payload 不含账号 |
| P 设置写入 | 通过；设置、审计及安全事件关联一致；无权/冲突/无效写未增加事件 |
| 瞬时失败退避/自动预算隔离 | 通过；持久化首失败时刻，最多五次重试后隔离；尚无调度器/publisher 驱动 |
| Publisher lease 竞争/过期 | 通过；两个 MySQL owner context 同时 claim 同一消息仅一个成功，过期 lease 可重新领取；超预算待重试消息在 claim 时持久隔离 |
| SQLite/替代Provider或实际Broker | 未执行；不计为通过 |
| Persistence Release 回归 | 323 passed，0 failed，0 skipped |
| 全 solution Release 串行回归 | Architecture 29、Modules 217 passed/1 failed、Persistence 323、Route 228；797 passed。唯一失败为真实浏览器登录事件页面未找到表格行；T05 定向测试、Persistence、Architecture、Route 均通过，模块浏览器失败不计为 T05 验收通过 |

事务失败回滚沿用既有 `FailAfterAuditSql` / `DatabaseAuditFailure` owner 回归；它们验证失败不提交 setting/role 或审计。此记录不代表真实 RabbitMQ 故障窗口、consumer ACK 丢失/重复、重试 deadline、授权重放、性能或生产恢复已验收。

可复跑命令（从仓库根目录）：

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore --verbosity quiet
dotnet test WES.slnx --no-restore -c Release --verbosity quiet
```
