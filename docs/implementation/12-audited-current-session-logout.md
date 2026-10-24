# T03 当前会话登出与原子审计

- 状态：实施中；内部用例已实现并验证，不是HTTP/登录页面或完整T03验收
- Owner：Platform；依据：[FUNC-PLAT-001 / DTC-PLAT-003及VAL-04](../detailed-design/platform-and-security.md)、[会话核心](05-platform-session-core.md)、[审计基础](08-platform-access-settings-audit.md)

## 用例和不变量

使用既有验证身份产出的TenantContext，只撤销`TenantId / ActorId / SessionId`匹配的当前refresh family；没有客户端目标用户、任意sessionId或“撤全部用户”入口。自助登出不需要角色授予，角色被撤销不能阻碍自己的会话撤销。内部用例允许已idle/绝对过期的已知自有会话被终结，但HTTP适配器仍必须按签名/issuer/audience/期限等验证身份，不能信任解析而未验证的claims。TenantContext本身不改。

session.Revoke和有限动作`SessionRevoked`的PlatformAudit同一个SaveChanges事务。审计只含tenant/actor/session/correlation及前后版本/UTC，没有令牌、IP、密码、任意reason/payload。没有数据库提交就不返回Completed；原审计append-only保护保持不变。此前已撤销重试返回Completed且不再换版本/写重复审计，未知/非自有会话统一Rejected且无写入；另一family、其他用户和其他租户不受影响。

logout与refresh共享session版本CAS：若refresh先赢，logout清掉失败图/审计、重读最新版本再撤；若logout先赢，refresh的冲突重读只能Inactive且无新秘密。两logout并发最多一个实际撤销/审计，失败者重读已撤销状态。最多8次CAS重读，耗尽明确错误并固定code日志，不把持续竞争当Completed。仅已分类DbUpdateConcurrencyException重读，其他SQL/审计错误、取消直接传播。无进程锁/缓存为权威。

前置条件为可信非零context、匹配数据库tenant、非零correlation、干净且无显式/环境事务的独占DbContext；若调用者将EF AutoTransactionBehavior设置为Never，显式拒绝，不能让session与audit静默拆为两次独立提交。前置拒绝不清他人图，自己所有图finally清理。记录失败不留待误提交；提交结果未知时不承诺未撤销，重试以当前持久状态收敛，不更改已提交事实。登出仅身份状态，不发送设备动作、不删除已发命令/业务事实。

## 测试与发布

| CaseId | 动作/反例 | 预期 |
|---|---|---|
| IDENTITY-LOGOUT-SCOPE-001 | 自助登出、同用户另一family、其他用户/租户/不存在 | 仅当前自有family撤销；未知/非自有统一拒；既有refresh不再可用 |
| IDENTITY-LOGOUT-AUDIT-001 | 重试/两logout重叠；注入真实审计PK冲突 | 一次撤销一条版本链审计；审计SQL失败时原session版本/状态不变 |
| IDENTITY-LOGOUT-CONCURRENT-001 | 强制logout/refresh两种提交顺序；持续真实CAS竞争 | 最终当前family撤销且无可用新秘密；耗尽明确错误，失败logout审计不残留 |
| IDENTITY-LOGOUT-FAULT-001 | 取消/脏context/显式及环境事务、倒退时钟、未知SQL异常 | 无成功形状回退/不清他人状态、不隐藏错误，不泄漏令牌或身份 |

审计枚举新增字符串值，无表字段/索引变化，不生成空迁移；双context模型检查和真实保存读取验证兼容。测试只用wes_test及fixture随机tenant，审计清理按既有测试授权用精确tenant参数化SQL，不为生产增加删除能力。HTTP登出路由、改密/禁用撤全部family及相应原子凭据更新仍未接线，不以本片代替完整T03。

## 红绿及实际结果

- 首轮真实MySQL测试11项中10失败/1通过；自助登出无Completed、无取消/owner/事务保护、无审计错误传播均被断言捕获，未知/非自有拒绝的1项原已通过。两项refresh竞争因空登出实现未进入保存而触发屏障10秒上限；不将这两项的超时本身当并发正确性的红绿证据。实现后11项均通过，再补inactive及8轮真实CAS竞争到14项。
- 独立新增AutoTransactionBehavior.Never反例首先因“不抛异常”明确红灯；加前置拒绝后全量通过，避免外部选项使session/audit拆提交。最终本片15项真实MySQL测试通过。
- 两方向refresh竞争用保存前屏障与首个提交后信号强制顺序：refresh先赢可得到旋转结果但logout随后撤销其family；logout先赢使refresh重读Inactive且无新秘密。两方向最终当前会话AcceptsAccess=false，非自有family保持Active。
- 真实audit主键1062失败时，session.RevokedAt仍null、Version不变且只保留既有审计；不捕获为版本冲突。持续竞争通过第二个DbContext每轮真实提交用户活动触发8次SQL CAS冲突，最终明确故障、8次竞争活动保留、logout审计0且无pending图。日志无refresh值/摘要/actor/session标识。
- Oracle MySql.EntityFrameworkCore10.0.7/EF10.0.7/MySql.Data9.7.0，真实MySQL8.0.12，仅wes_test；无新增依赖/迁移、HTTP路由/设备动作。
- 全solution最新Release：Architecture29、Modules118、Persistence153、Route228，共528 passed、0 failed、0 skipped，构建0警告/错误；格式、两个owner模型快照与设计映射/计划DAG检查通过。当前仍未完成完整T03/T04或后续未实施包。

```powershell
dotnet build WES.slnx --configuration Release --no-restore
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-build --no-restore --filter FullyQualifiedName~LogoutSessionPersistenceTests
dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1
dotnet ef migrations has-pending-model-changes --context PlatformDbContext --project src\Modules\Platform\Wes.Platform.csproj --configuration Release --no-build
dotnet ef migrations has-pending-model-changes --context LoginSourceDbContext --project src\Modules\Platform\Wes.Platform.csproj --configuration Release --no-build
dotnet format WES.slnx --no-restore --verify-no-changes
```
