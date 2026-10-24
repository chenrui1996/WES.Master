# T03 账号登录风险耐久切片

- 状态：实施中；内部风险状态已实现并验证，不是完整登录/T03验收
- Owner：Platform；依据：[PAR-02 / VAL-04](../detailed-design/05-approved-rules-and-defaults.md)、[身份详细设计](../detailed-design/platform-and-security.md)、[来源准入](09-durable-login-source-admission.md)

## 思路与边界

同一可信租户、规范化账号的密码失败使用耐久滚动15分钟窗口；前3次密码失败可直接验证，从第4次尝试须已成功消费的一次性验证码，第5次密码失败触发从该次失败起15分钟暂锁。缺/错验证码不验证密码、不增加账号密码失败，但来源请求已独立计数。锁定期间拒绝且不延长锁定；第900秒可重新验证，不用日历分钟桶。错误/未知账号走同一风险键及规则，不查用户外键；不把风险状态查询结果直接暴露给客户端。

本片接受**凭据owner已规范化**的非空账号字符串，以UTF-8 SHA-256作为假名化键；不自行引入另一套大小写/trim/Unicode规范化，与来源摘要一样不宣称匿名。尚未装配账号目录，HTTP接线前须落实同一个规范化器用于凭据查找和此键。账号/口令原值不进入此表、普通日志或异常。

数据位于既有租户强制的`PlatformDbContext`，复合主键TenantId/AccountDigest，无TenantId=0及用户存在性外键。最多5个微秒UTC失败instant、LockedUntil、LastFailureAt和不透明Version；拒绝损坏/未来状态及倒退时钟，不以清空数据代替故障。来源跨租户上下文保持来源专用，不混入账号。

## 两阶段与并发

先读取风险快照（含Version及Gate），在当前gate允许时由后续身份用例验证密码；只有密码实际错误才提交失败。写入必须携带读到的Version（未建行时null），并重新计算当前gate。读取不创建空行，所有有效失败都更新Version；首次插入唯一键竞争/已有行CAS冲突返回明确`VersionConflict`，**不自动将旧密码/验证码判断套用到新版本**，调用者必须重新执行整个风险与验证流程。失败提交不产生会话；bool验证码参数是内部已验证事实，不是HTTP客户端输入。真正验证码消费及凭据原子接线仍未完成，禁止直接开放本接口。

本片不提供成功清零/管理员解锁，因为相关生命周期须随凭据及完整安全审计另行接线；没有偷偷添加“成功登录抹除风险”的未批准规则。SaveChanges成功后才返回Recorded；脏context、显式/环境事务拒绝，避免风险计数被外层登录失败回滚。前置拒绝不清理他人图，自己所有的图在finally清理。取消/未知数据库错误原样传播；提交结果不确定时不承诺没有消费，不自动补偿。固定风险code日志不包含账号摘要/账号/密码。

`VersionConflict`不代表错误密码已计数；后续完整登录owner必须解决并发尝试的重新准入/验证、一次性验证码不可重用及真实失败不漏记，不能将本片独立两阶段调用等同完整原子登录。正确密码的会话签发也必须重新验证风险/凭据版本、当前禁用状态及撤销规则，不能仅凭Read的VerifyPassword签发。当前无签发调用者，不开放HTTP；这些并发成功/失败端到端场景仍是T03完成门禁。

每行5项失败有界，不等于账号风险总行数有界；大量未知账号可增加总行数。上线前仍须结合来源准入落实规范化输入长度、容量/安全清理、入口限流和故意锁号告警，不对公网防滥用作提前验收。

## 测试与迁移

| CaseId | 输入/动作 | 预期 |
|---|---|---|
| IDENTITY-ACCOUNT-WINDOW-001 | 同瞬间连续失败1..5，第三次后缺验证码，第五次后再试 | 3后需验证码，5后锁；缺验证码/锁期间无变化 |
| IDENTITY-ACCOUNT-LOCK-001 | 前4次距第五次较远，锁899.999999/900秒 | 锁以第五次起算，不因旧失败出窗提前解除，不延长 |
| IDENTITY-ACCOUNT-STATE-001 | 微秒/时区/倒退时间、损坏JSON/锁状态 | 精度往返一致，拒绝坏schema且不泄漏内容 |
| IDENTITY-ACCOUNT-DB-001 | 新context、同账号不同tenant、不同账号/未知账号 | 状态耐久，隔离相同，无账号外键或原值 |
| IDENTITY-ACCOUNT-CONCURRENT-001 | 同时首次插入/第5次失败、过期Version重放 | 恰1Recorded/1VersionConflict；重读发现最新gate，不能重用旧判断 |
| IDENTITY-ACCOUNT-FAULT-001 | 保存故障/取消、脏context/环境事务 | 不伪造Recorded，前置拒绝不清他人状态 |
| IDENTITY-ACCOUNT-MIGRATION-001 | 审查Platform增量迁移 | 仅新增账号风险表，不改旧表/库级设置/来源历史 |

迁移仅在授权wes_test执行，清理仅fixture自己生成的租户与账号键。T03/T04整包与后续工作包不会因此自动完成。

## 实际证据与团队复跑

- 领域第一轮4项均行为失败（键未区分、锁定/窗口缺失、未写时间），实现后通过；再补独立PowerShell SHA-256向量、非法UTF-16拒绝和滚动验证码门槛边界，最终5项通过。
- 迁移范围测试先因缺少AccountLoginRisk增量失败；生成并人工审查后通过。Up仅CreateTable `platform_login_accounts`，Down仅Drop本owner新表，复合主键及并发列具备当前租户过滤，没有用户外键、AlterDatabase、原始SQL或来源模型扩张。旧迁移未修改。
- 真实MySQL handler第一轮15项均行为失败（未持久化、未拒坏schema/事务、并发无成功、未知账号错误gate），实现后转绿。曾有1项验证端读取引入tracked实体，按既定独占context合同改为AsNoTracking，而非放宽生产guard。
- 最终账号迁移/持久化20项通过，覆盖同时首次建行、第五次失败CAS、过期版本拒绝、同键跨租户读写、取消、未知DB错原实例传播、失败更新图清理、坏JSON及锁状态、微秒与未来时钟。并发竞争明确恰1Recorded/1VersionConflict，不冒称两个实际密码失败已全部计数。
- 隔离环境：Oracle MySql.EntityFrameworkCore10.0.7、EF10.0.7、MySql.Data9.7.0，真实MySQL8.0.12，仅wes_test；无新增依赖/账号导入/秘密配置/设备动作。
- 全solution最新Release：Architecture29、Modules109、Persistence114、Route228，共480 passed、0 failed、0 skipped；构建0警告/错误。locked restore、格式、两个owner迁移快照一致性与文档追踪/DAG校验通过。

```powershell
dotnet restore WES.slnx --locked-mode
dotnet build WES.slnx --configuration Release --no-restore
dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-build --no-restore --filter FullyQualifiedName~LoginAccountBudgetTests
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-build --no-restore --filter FullyQualifiedName~LoginAccount
dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1
dotnet ef migrations has-pending-model-changes --context PlatformDbContext --project src\Modules\Platform\Wes.Platform.csproj --configuration Release --no-build
dotnet ef migrations has-pending-model-changes --context LoginSourceDbContext --project src\Modules\Platform\Wes.Platform.csproj --configuration Release --no-build
dotnet format WES.slnx --no-restore --verify-no-changes
```

上述迁移检查不应用数据库；实际测试fixture只有已授权wes_test可Migrate，不新增Host自动迁移。发布/回滚须由完整身份owner确认风险状态保留与服务停机顺序，不能直接删表当作运行中解锁工具。
