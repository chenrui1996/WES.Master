# T05 Platform Message Operations 测试记录

- 状态：授权 Outbox 查询和 Platform-owner 重放的定向测试通过；全 solution Release 回归待本切片最终记录。
- Provider：隔离 MySQL `wes_test` 与 Platform HTTP host fixture。

## 验证

| 验证 | 结果 |
|---|---|
| Outbox query persistence | 1 passed；未授权返回显式拒绝；当前有效 All grant 可读，字段按 grant 投影，Payload 不输出，分页稳定且单页受限 |
| Outbox query HTTPS endpoint | 1 passed；未授权为 Forbidden，授权为 OK，响应不含 `payload` 属性 |
| Outbox replay persistence + migration | 3 passed；授权/拒绝、expected-attempt 冲突、同operation重复、不同理由键冲突、并发同operation幂等、原envelope identity/payload保留、回执模型及additive migration |
| Outbox replay HTTPS endpoint | 1 passed；匿名与无权限拒绝、无效operation UUID/理由拒绝、stale attempt冲突、有效重放、相同operation幂等与payload不回传 |
| 全 solution Release | `dotnet test WES.slnx --no-restore -c Release --verbosity quiet -m:1`：816 passed、0 failed、0 skipped（Architecture 29、Modules 220、Persistence 339、Route 228） |
| SQL Server/PostgreSQL/真实Broker | 未执行；不计为通过 |

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~PlatformOutboxQuery --verbosity quiet
dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~OutboxHttpRequiresDedicatedReadPermissionAndNeverExposesPayload --verbosity quiet
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~PlatformOutboxReplay --verbosity quiet
dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~OutboxReplayHttpIsAuthorizedIdempotentAndDoesNotAcceptEnvelopeData --verbosity quiet
```
