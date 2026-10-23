# T03 会话领域与真实并发首片

- 状态：内部首片已实现并验证；T03整体仍实施中，不开放登录/签发API，不将PAGE-PLAT-001/002计作完成
- 依据：[Platform详细设计](../detailed-design/platform-and-security.md)、[PAR-01/02](../detailed-design/05-approved-rules-and-defaults.md)、[软件契约](../contracts/01-business-and-execution-contracts.md)
- owner：Platform；参考Seven AuthService与BCrypt workFactor12实现，但不复制吞审计失败、直接返回实体或无限会话的路径

## 采用方式

先以Platform自己的Session聚合实现时限、活动、撤销和refresh消费，再用真实MySQL证明版本竞争；不以进程锁作为耐久单次刷新保证。对外业务端点在账号凭据、验证码、登录预算、JWT、审计和授权门禁齐备前不暴露。不建立Host会话状态或第二身份库。

每个session固定tenant/user/clientKind、创建时间和绝对8小时期限；生成新的UUID版本，仅作为不透明并发标记。refresh只保存SHA-256摘要，原始随机值只在申请/返回边界短时持有，不进入实体、数据库或审计。聚合保存已消费摘要以检测旧token重放，重放撤销这一关联session family；其他独立session不受影响。不能为了幂等返回旧明文token。

`Access`有效期为签发后900秒与session绝对期限的较小者；未来/过期/已撤销均无效。Web/PDA最后真实活动后900/300秒锁定，边界相等即失效；refresh不更新活动、不延长绝对期限。客户端活动不能越过服务端当前状态，不对时钟倒退静默修正。idle锁定不是撤销已经派发物理命令。

## 持久化与冲突恢复

PlatformDbContext只包含本owner会话表，tenant为必需非空UUID；按当前tenant过滤查询，缺租户在构建上下文时失败，不用TenantId=0或关过滤器补齐。session版本为EF并发token，token摘要唯一约束带tenant，子表带tenant/session复合外键；数据库不能把另一租户的token挂到本session。

新会话/refresh/撤销在本owner事务提交；当前片只提供领域与持久化原语，未假造完成HTTP用例。在两个独立上下文读取相同版本后并发刷新，数据库最多允许一个新token提交；失败者必须读取最新session，若旧token已消费则撤销family并提交，不能只报409而继续让已泄露family活跃。该流程用真实MySQL反例验证，不声称任意进程内调用就是完整刷新服务。

本片迁移只创建`platform_sessions`与`platform_refresh_tokens`，专属`__WesPlatformMigrations`，不调用EnsureCreated/EnsureDeleted，不修改探针或其他owner表。集成测试使用生成的tenant/user/session及摘要，清理只按本测试已知tenant/session主键，先子后父；不删库、不清别人的会话。

## 测试

同批提纯登录预算的无状态策略：账号/IP滚动窗口使用持久化时间戳作为输入，策略本身不维护进程单例状态。账号第3失败后要求验证码，第5失败暂锁15分钟；未知账号必须由后续持久化服务使用同一策略，不能查不到用户就跳过。IP的30/60秒窗口跨账号/租户，不信Forwarded头。本片不以纯策略绿灯宣称分布式限流已完成，实际消费/并发及验证码一次性事务仍待后续接线。

新密码长度校验按有效Unicode标量计算，不能按UTF-16长度或BCrypt的72字节上限截断。策略接收泄漏/最近五次核验的明确结果；未知（依赖未配置）返回不可验证，不默认为“未泄漏”。原密码保持原值，不trim/normalize后存储。此策略尚不决定新哈希算法或导入旧用户。

| CaseId | 测试及独立预期 |
|---|---|
| IDENTITY-SESSION-001 | Access899秒有效/900失效、未来issuedAt拒绝、绝对期限刷新不延长 |
| IDENTITY-SESSION-002 | Web899/900与PDA299/300；真实活动更新而refresh不更新；过期/撤销/倒退时钟拒绝 |
| IDENTITY-REFRESH-001 | 有效摘要消费一次产生新摘要；旧摘要重放撤销同family，未知摘要不修改session |
| IDENTITY-REFRESH-002 | 真实MySQL两上下文并发CAS：一个提交/一个冲突；失败者按最新状态检测重放并撤销；另session不变 |
| IDENTITY-TENANT-001 | 两tenant同sessionId互不可见；缺tenant拒绝，外租户子token不能挂入 |
| IDENTITY-SECRET-001 | refresh随机不可预测且只持久化摘要；实体/格式化输出不含原值 |
| IDENTITY-REVOKE-001 | 单session登出幂等且不影响另session；改密/禁用全session撤销的实际HTTP事务留后续 |
| IDENTITY-BUDGET-001 | 3/4/5失败、900秒锁边界、滚动窗口旧失败过期；IP30/31及60秒边界；未来时间戳拒绝 |
| IDENTITY-PASSWORD-001 | 14/15/128/129 Unicode标量、emoji及非法surrogate；相同账号/已泄漏/历史复用拒绝；核验未知拒绝而非假通过 |

本片不是登录端点、JWT验证、页面或完整DTC-PLAT-001..003通过证据。新密码15..128 Unicode标量与Seven classic BCrypt的UTF-8长度限制必须在凭据切片明确处理，不能用截断“兼容”；泄漏密码词库缺失时不得假称已验收。批准设计不代表借用Seven用户数据或导入旧密码。

## 已实现边界与实测过程

- Session时限/撤销/摘要不变量初始13失败/3通过，随后通过；补充“持续活跃PDA仍8小时绝对过期”和“后继摘要不可复用”，共18个领域会话用例。初始红灯不是数据库故障。
- RefreshSecret初始2项失败，随后验证标准SHA-256 `abc`独立向量、32字节随机原值及安全格式化。公开Value仅为传输值，不能作为普通DTO/审计字段，更不能对对象做日志destructure。
- LoginPolicy/PasswordPolicy初始13失败/4通过，随后17项通过；它们是无状态规则，不是已部署的登录限流器或真实泄漏密码库。
- PlatformDbContext提供租户查询过滤、同步/异步写入owner验证、UTC datetime(6)转换和版本CAS。Oracle Provider对显式Guid→char(36)映射不兼容，改用UUID字符串的varchar(36)，未改变对外UUID语义。EF工具生成迁移/快照，校验model无待迁移变化；没有手改已应用schema来假造通过。
- 迁移审查测试先拒绝自动生成的AlterDatabaseOperation；在首次应用前移除该数据库级变更，保留两张Platform表的utf8mb4标记。只读设计时factory无连接字符串、不打开连接，固定非零设计tenant不代表共享运行身份。
- RefreshSessionHandler在干净的owner unit of work中加载、轮换并提交后才返回后继secret；并发冲突最多重读一次，已消费旧摘要触发family撤销并提交。重复冲突显式报错，不把不确定提交包成成功；仅捕获DbUpdateConcurrencyException，其他持久化错误向上暴露。
- 实际MySQL包括：tenant查询/拒越界写、缺tenant拒绝、复合外键跨tenant拒绝（1452）、两DbContext竞争（1成功/1冲突）、两个实际handler用保存屏障强制重叠（1返回后继/1重放撤销）、提交前注入失败（无成功/记录不变），以及完整清理。底层初版映射配置失败不冒充行为红灯；handler本身曾明确观察有效credential被错误判Invalid的断言红灯后才实现。

每个测试实例只拥有新生成的两个tenant，清理按这些精确tenant下的已加载session与子token键删除并断言均为0，不清理任何其他tenant；保留Platform两表及专属迁移历史。测试没有创建实际人员账号或JWT，也未导入Seven记录。

## 团队上手与仍待接线

```powershell
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project src\Modules\Platform\Wes.Platform.csproj --no-build
dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-build --filter "FullyQualifiedName~Platform"
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-build --filter "FullyQualifiedName~Platform"
```

EF工具10.0.7及Design依赖已锁定。生成迁移不是生成业务代码；每次仍须审查Up/Down、owner范围及数据恢复含义。Shared实例不执行Down/删库；真实前向升级测试是本片证据，备份恢复/生产部署不在此范围。

后续T03必须接入账号/旧哈希兼容、新凭据完整证据、一次性验证码耐久消费、账号/IP预算原子落库、JWT最小claims与当前会话复验、登录/刷新/改密/撤销API、审计以及Vue页面/E2E，才能完成工作包。纯策略和已测试的handler不替代这些功能。当前Host不会成功响应登录/refresh请求，保持404而非伪Accepted/成功。
