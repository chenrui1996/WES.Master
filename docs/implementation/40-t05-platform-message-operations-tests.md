# T05 Platform Message Operations 测试记录

- 状态：授权 Outbox 查询切片定向测试通过；重放测试尚未实现。
- Provider：隔离 MySQL `wes_test` 与 Platform HTTP host fixture。

## 验证

| 验证 | 结果 |
|---|---|
| Outbox query persistence | 1 passed；未授权返回显式拒绝；当前有效 All grant 可读，字段按 grant 投影，Payload 不输出，分页稳定且单页受限 |
| Outbox query HTTPS endpoint | 1 passed；未授权为 Forbidden，授权为 OK，响应不含 `payload` 属性 |
| 全 solution Release | 最近一次查询改动之前 809 passed；查询和后续重放完成后需重跑全量再记最终计数 |
| SQL Server/PostgreSQL/真实Broker | 未执行；不计为通过 |

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~PlatformOutboxQuery --verbosity quiet
dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~OutboxHttpRequiresDedicatedReadPermissionAndNeverExposesPayload --verbosity quiet
```
