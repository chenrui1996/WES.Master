# T03 凭据历史与当前主体受控改密

- 状态：内部owner切片已实现并验证；不是可公开调用的改密API/完整登录
- Owner：Platform；依据：[PAR-02 / VAL-04](../detailed-design/05-approved-rules-and-defaults.md)、[平台设计](../detailed-design/platform-and-security.md)、[T03计划](../superpowers/plans/2026-10-23-wes-development-work-packages.md)、[密码编码器](13-compatible-password-codec.md)

## 已批准范围的实现细化

延续现有凭据编码、Session及原子审计，而非另起身份系统。本片是既有T03的有界扩展：为一个受信tenant/user保存版本化密码状态，提供当前主体携旧密码、期望版本与泄漏证据的受控改密；不增加账号规范化规则、用户创建/禁用、HTTP、JWT或外部泄漏查询。迁移/开户owner必须提供其已规范化账号和已批准编码值，本片不读旧用户表，也不将内部构造函数当匿名开户入口。

最近5个密码按**当前值在内、由新到旧共5个已设置值**保存；第6旧值从禁止复用窗口退出。每个值保存自己的显式scheme和salt，不比较新散列字符串判断复用；候选逐个用编码器验证，旧72字节别名也判历史匹配，任何损坏记录显式失败，不能跳过后返回Clear。泄漏证据Unknown/Prohibited仍拒绝。账号使用凭据owner的受信规范值做账号相同检查与digest，不擅自trim/大小写转换；未来用户目录须使用同一规范值与唯一身份，不以显示名替代。

`PasswordCredential`与编码器同处Identity切片，持有私有、有界、版本化历史编码；不把秘密类型扩散到通用Domain或响应DTO。表`platform_password_credentials`以tenant/user为主键、tenant/account digest唯一；UUID版本CAS，UTC微秒。普通JSON/格式化不输出历史或当前hash，不存原密码、原请求、泄漏查询响应或幂等密码摘要。

## 当前主体事务与并发

受信TenantContext仅允许改ActorId自己的凭据；当前自有family必须仍Active。须携合法expectedVersion、旧密码及correlation，legacy正确可进入受控改密，但不因此获得生产写。先验证当前版本/会话和旧密码，再检查新密码政策及真实本地5项历史，最后产生新格式hash。成功在**同一SaveChanges事务**更新凭据、撤销查询到的该用户全部family并追加PasswordChanged及逐family版本链SessionRevoked审计。其他用户/租户不变；已撤销family不重复审计。日志仅安全code，失败结果不含密码/账号/hash。

CAS冲突返回VersionConflict，不自动复用旧密码判断或Clear证据重试。旧版本、错旧密码、无当前会话与过期/锁屏会话不改状态；存储、坏历史、时钟、取消异常明确传播。拒dirty context、显式/ambient外部事务及AutoTransactionBehavior.Never，禁止“已完成”但外层稍后回滚/拆分审计；只清理本handler图。响应丢失可能已提交，调用者应重新认证查询，不保存旧/新密码以自动重放。

**尚未关闭的并发门禁：**当前尚无账号目录/登录family签发owner；其未来必须以同一凭据版本做最后CAS栅栏并检查禁用/受限状态。否则改密扫描后绕过owner直接新增family会逃过撤销，本片不声称可以阻止任意代码绕过该约束。现有family的refresh先提交会使改密CAS失败，用户明确重新提交后方可完成；改密先提交则refresh重读为Inactive。完整端到端签发/禁用与高后果授权接线仍是T03/T04门禁。

## 测试与迁移门禁

| CaseId | 输入/动作 | 预期/副作用 |
|---|---|---|
| IDENTITY-PASSWORD-HISTORY-001 | 相同密码、前5项、第6旧项、旧格式别名、非BMP候选 | 前5拒、退出窗口可通过其他政策；不比较随机salt；不泄露历史 |
| IDENTITY-PASSWORD-CHANGE-001 | 自有Active会话、正确旧密/版本、新政策Clear | 新格式/新版本、全部既有自有family撤销、其他主体不变、原子审计 |
| IDENTITY-PASSWORD-SCOPE-001 | 错密、缺/非自有/锁屏family、旧版本、跨租户、坏政策 | 无凭据/会话/审计副作用；通用安全拒绝 |
| IDENTITY-PASSWORD-CONCURRENT-001 | 并发两改密、与refresh两种提交顺序 | 最多一个凭据版本成功；冲突不自动重试；最终成功改密后原family不可用 |
| IDENTITY-PASSWORD-FAULT-001 | audit真实1062、CAS、取消、DB异常、dirty/外部事务、坏历史/倒退时钟 | 全事务回滚或显式失败，不伪装政策通过，不清调用者图 |
| IDENTITY-PASSWORD-MIGRATION-001 | owner增量迁移/回读/重启/租户复合唯一 | 只创建凭据表，不修改既有表/库；规范scheme/hash/history与版本耐久保存 |

先观察领域与handler行为红灯，再生成并审查增量迁移；实际应用只允许隔离`wes_test`，不对生产自动迁移。本片不勾T03父步骤或父DTC整体完成。

## 实际红绿与回归

1. 领域行为桩先运行`dotnet test tests/Modules/Wes.Modules.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~PasswordCredentialTests`：5项断言失败、0项通过；实现后5项通过。后续再补2b/2y前缀在旧/新两种显式模式的真实固定向量验证，最终本类7项通过。
2. 生成`20261024030524_PasswordCredentials`；独立无连接迁移门禁确认只有1张凭据表和1个租户账号唯一索引，Down只删除本表，不含AlterDatabase/已有表变更。审查后才由fixture向授权隔离`wes_test`应用；没有手工修改已应用迁移语义。
3. handler行为桩先运行非屏障子集：20项中14项断言失败、6项通过；编译成功，未用超时/缺包当行为红灯。实现后含两提交顺序的refresh竞争、两改密实际CAS及审计真实1062在内的24项迁移/DB测试全部通过。
4. 新增反例发现缺失scheme曾被JSON默认成legacy、第二次读时钟倒退曾可形成早于凭据的审计：目标5项中2项明确失败、3项通过；补JsonRequired和提交前单调时钟检查后转绿。再加入哈希期间899→900秒会话锁定拒绝、每次独立context回读的5/第6旧值边界、已撤销family不重复审计与真实拒绝日志/结果脱敏，最终本片30项迁移/真实DB测试通过。
5. EF生成的本轮迁移用仓库已有`dotnet format --include`规范编码/文件作用域namespace，再重跑迁移门禁；没有改写迁移操作。最终`dotnet format WES.slnx --no-restore --verify-no-changes`、IDE诊断与`git diff --check`通过。
6. 最终fresh Release build：0 warning、0 error；`dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1`：Architecture29、Modules142、Persistence183、Route228，合计582 passed、0 failed、0 skipped；两个DbContext均无pending model changes。文档映射/依赖DAG随提交前复跑。

## 部署、回滚与剩余门禁

此处没有Host装配/公开endpoint或自动生产迁移。新表包含敏感历史hash，后续发布须由owner/DBA验证最小权限、受保护备份及保留策略；不将hash放入审计/普通导出。生产回滚优先回滚代码并保留表/安全事实，不直接执行会删除凭据历史的Down；任何数据回滚必须有受保护备份、核验与受控恢复方案。

本片实际关闭了本地历史Clear证据与当前主体改密的既有family原子撤销缺口，但**外部泄漏检查仍未实现**，调用者不能伪造Clear；账号目录/禁用、并发新family签发的凭据版本栅栏、完整风险准入、JWT/HTTP/页面/E2E仍待完成。旧模式受控改密前禁生产写必须由后续当前身份/授权链路贯彻，不能将此内部方法当作已经上线的身份系统。生产/现场H门禁保持未验收。
