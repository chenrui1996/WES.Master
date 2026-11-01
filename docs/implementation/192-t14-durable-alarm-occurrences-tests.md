# T14 occurrence 接收验证记录

- 状态：原始事实接收子片已实现验证；2026-11-01；设计：[191](191-t14-durable-alarm-occurrences.md)。父T14未完成。
- 只验证Notifications原始事实持久接收；无HTTP/管理权限/设备源认证/投递/聚合/确认清除/页面结果。

## 1. RED与修复

1. 文档、项目/测试引用先建立；新依赖manifest后合法restore。最早缺类型编译RED不是运行断言。修正占位constructor未读参数CS9113和xUnit2031后，迁移成功，本片32项均实际运行RED（NotImplementedException），随后store/四Save重载实现32/32 GREEN。
2. 增补确定性并发：共享reader barrier等待两次源身份SELECT都执行完才让接收者进入INSERT。两项同输入/变参竞争各只一条事实，另一路经历1062并第三次SELECT回读赢家；不是只测Task.WhenAll可能实际顺序完成。源第七位tick、Provider受控失败、received微秒和四Save重载越租户负例补齐。
3. ambient事务负例先1RED、同批其他5GREEN：仅检查DbContext局部transaction会让TransactionScope内接收成功，未显式拒绝。入口添加`Transaction.Current`后拒绝且事实0。
4. SQL审查发现C# `UseCollation`未生成DDL：实际列binary元数据与大小写来源分别2RED。MySQL专用annotation维护模型，后续空scaffold表明Provider不为annotation-only变化生成ALTER；按Platform先例在scaffold迁移中显式修改本表四列charset/collation。修复后实际information_schema四列、大小写来源各自Recorded/Replayed及全部回归通过。未修改其他owner的既有迁移/列。

## 2. 最终验证

| 检查 | 实际结果 / 范围 |
|---|---|
| `AlarmOccurrenceTests`真实MySQL | 44/44；同源永久Replay/全输入变参Conflict、每event原事实、迟到、owner/tenant隔离、取消/受控故障、确定性1062竞争、四Save重载改删/越租户、tracked/局部及ambient事务拒绝、严格ID/文本/枚举/时区与精度 |
| 与`WorkflowFormPersistenceTests`合并 | 66/66（44+既有22），0失败/0跳过；不与早期32/58/64重复累计 |
| 架构 | 29/29；现有项目图会扫描实际新Notifications项目 |
| Release完整解决方案 | 0警告/0错误；最终格式后复核 |
| Notifications EF模型漂移 | 无；不把此项替代实际SQL/元数据验证 |
| Up/Down SQL | 独立`__WesNotificationsMigrations`；新表tenant双PK/源唯一/查询索引、源bigint ticks/received datetime(6)、后续四列显式binary ALTER；Up只在授权隔离wes_test应用，Down仅生成审查不执行 |
| 格式/文本 | 新Notifications全部C#（含scaffold迁移）、本片测试定向whitespace及verify；`git diff --check`与本轮Markdown本地目标检查 |

本轮12份Markdown、935个本地目标检查：10处缺目标均为HEAD已存在的外部Seven.Master/系统架构/Route材料，0新增断链；旧缺目标保留报告，不为通过检查创建外部目录或伪造内容。

复验命令：

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~AlarmOccurrenceTests|FullyQualifiedName~WorkflowFormPersistenceTests"
dotnet test tests\Architecture\Wes.Architecture.Tests.csproj --configuration Release --no-restore
dotnet build WES.slnx --configuration Release --no-restore
dotnet ef migrations has-pending-model-changes --project src\Modules\Notifications\Wes.Notifications.csproj --startup-project src\Modules\Notifications\Wes.Notifications.csproj --configuration Release --no-build
```

fixture仅从既有授权测试连接加载器取得连接，校验wes_test；随机两tenant定向清理本表，不输出凭据、不删除其他tenant/owner数据。新项目lock与Persistence lock随manifest同步；TRX/SQL留在会话验证目录，不提交缓存。

## 3. 收口与未执行

更新坑库INF-005（ambient事务）、INF-009（Provider实际collation/annotation-only scaffold）、GEN-006（确定性竞争/实际DDL而非模型代理）。这些是已复现子集，其余外部风险矩阵不因本片通过变成已验证。

本片没有前端/Host/权限改动，未重跑全前端、全部Modules/全部Persistence/Route、真实浏览器；不以旧数字代签。未运行真实协议源认证、进程/OS崩溃或保存提交后回执丢失、设备恢复/联锁、真实邮件/SignalR、Quartz双节点、SQL Server/PostgreSQL、生产迁移或现场试验。

本片只关闭内部原始事实接收；T14聚合/生命周期/静默/当前授权/投递/Jobs/公告/缓存页面仍主体待办，T12管理合同及T13/T15依赖保留。测试不能证明通知已送达、人已读、设备已清除或物理安全。
