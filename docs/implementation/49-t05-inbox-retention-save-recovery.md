# T05 Inbox 保留清理的保存失败恢复

- 状态：同事务保存失败恢复及许可生命周期已实现；T05 整包仍未完成。
- 依据：[Inbox 保留原语](37-t05-platform-inbox.md)、[T05 工作包](../superpowers/plans/2026-10-23-wes-development-work-packages.md)。
- 测试记录：[保存失败恢复测试](50-t05-inbox-retention-save-recovery-tests.md)。

## 故障与根因

原持久层在 `ValidateWriteScope` 中通过 `HashSet.Remove` 一次性消耗 Inbox 清理删除许可，随后才执行 EF 保存。若 DELETE 在数据库命令执行阶段失败，实体仍为 Deleted，但许可已丢失；调用者在仍有效的同一 owner 事务中再次保存，会被错误拒绝为普通 append-only 删除。

真实 MySQL 故障注入验证了同步/异步两条路径：第一次保存到达 DELETE 命令并抛出 `DbUpdateException`，第二次保存未到 SQL 即被持久层错误拒绝。不是通过更换 Provider 或忽略异常规避。

## 修复

清理原语为每个选出的实体暂存当前 EF 显式事务的 `TransactionId`。写入校验只比较许可，不消耗它；只有在所属 owner、被允许的实体和原事务均匹配时可删除。

同步/异步 `SaveChanges` 仅在 base 保存成功返回后清空许可；失败仍向调用者抛出原异常。同一有效事务下，调用者可在确认故障可恢复后显式再次保存；没有添加自动重试或吞错。数据库若已回滚/中止事务，不能用旧许可在无事务或新事务中继续删，须重新建立合法清理批次，通常应使用新 owner context。

保存成功即结束本批许可，包括 `acceptAllChangesOnSuccess=false`；该选项下跟踪状态的 `AcceptAllChanges` 仍由调用者处理，不能将保留的 Deleted 跟踪状态当作第二次删除许可。成功后重新附加相同实体的直接删除也被拒绝。

## 不变边界

90 天阈值、tenant 隔离、1–200 条批次、显式 owner 事务及普通 append-only 保护均保留，无 schema 迁移。该原语仍不注册清理 Job、不自行提交事务，也不提供 Broker、自动重放或领域身份清理。
