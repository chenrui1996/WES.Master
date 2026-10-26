# T05 Inbox 保留清理保存恢复测试

- 状态：定向测试和完整串行 Release solution 回归通过；T05 整包仍未完成。
- 关联：[实施记录](49-t05-inbox-retention-save-recovery.md)。

## 定向真实 MySQL

命令：`dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~PlatformInbox --verbosity minimal`

结果：16 项通过，0 失败，0 跳过。

- 新增 4 个保存故障重试变体：同步/异步 × acceptAllChanges true/false。批次含两条过期收据；在真实 Provider 的 DELETE 命令拦截点仅注入第一次故障，验证 `DbUpdateException`/内部故障类型、Deleted 跟踪状态和到达命令次数，再次保存/提交后从新 context 确认两条均已删除。
- 新增 4 个事务生命周期变体：同步/异步 × 无事务/替换事务。暂存后回滚原事务，旧许可不能继续写；新 context 确认收据仍在。
- 新增 2 个成功后许可失效变体：同步/异步。保存成功后重新附加同一实体并直接删除，必须拒绝。
- 原有 Inbox tenant/consumer/event 幂等、原子提交、append-only、90 天边界、批次及回滚测试继续通过。

修复前的同步/异步两项故障重试测试均在第二次保存时重现 `InvalidOperationException`，定位到写范围校验提前消耗许可；修复后新增 10 项和既有 6 项均通过。

## 完整回归

命令：`dotnet test WES.slnx --no-restore -c Release -m:1 --verbosity minimal`

| 工程 | 通过 | 失败 | 跳过 |
|---|---:|---:|---:|
| Architecture | 29 | 0 | 0 |
| Modules（含真实 HTTPS Chromium） | 223 | 0 | 0 |
| Persistence（真实 MySQL） | 352 | 0 | 0 |
| Route | 228 | 0 | 0 |
| 合计 | 832 | 0 | 0 |

同轮 Ops Web 全量 19 个文件、152 项通过，严格类型检查和生产构建通过；详细前端范围见[共享分页记录](48-t06-shared-realtime-pager-tests.md)。不将前端数量与 solution 数量混作一次 runner 的输出。

## 限制

命令拦截点是在真实 Provider 的 DELETE 执行前注入失败，证明保存失败后的许可生命周期，不冒充网络断开、死锁、提交结果未知或生产恢复演练。数据库已中止的事务必须按 owner 恢复流程重新建立；本片不承诺任何失败都可在原事务重试。其余 Provider、真实 Broker/托管调度与生产许可仍未验收。
