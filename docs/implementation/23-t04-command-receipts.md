# T04 非机密目录命令耐久回执

- 状态：owner持久化与具名非机密HTTP写接线已验证；成员重放使用最小角色引用证据并复验当前权限；显式启用的有界托管保留续实施见[87](87-platform-retention-maintenance.md)，生产运维验收仍未完成；owner：Platform directory；关联 T04、DTC-PLAT-004..006/008/011/014、WES-RULES-1.0。
- 测试证据：[T04命令回执测试记录](30-t04-command-receipts-tests.md)
- 仅具名非机密命令可选使用 UUID 操作上下文；不增加公开端点、通用 CRUD、Outbox 或生产自动迁移。凭据、密码、登录禁止使用回执。

## 设计及不变量

目录 owner 从实际非机密命令参数计算 SHA256（包括命令种类和预期版本，不含 correlation）。回执主键为 tenant + actor + operation UUID；同键不同种类/指纹显式冲突，同键相同参数在当前权限复验后返回首次提交的安全 UUID/version。回执仅保存这些数据和 UTC 创建时间，不保存请求或角色授权快照。

业务、审计与回执在同一显式 Serializable owner 事务提交。唯一主键和最多五次新事务尝试保证并发相同操作最多一次业务效果；重试仅限唯一键竞争/死锁/锁超时，每次清除 tracker，取消贯穿重试等待。预算耗尽返回 Duplicate/VersionConflict，不宣称有界时间内所有请求必成功。失败、取消、拒绝和版本冲突不保存回执；其他 provider 故障不伪装为成功。

重放必须重新读取当前 session、目录资格、角色及字段范围。用户创建重放使用**当前创建目标**的 owner/department，不仅原始 department。更新用户同时检查当前和请求 department；角色创建检查当前和请求规则。用户/部门/角色目标不存在或当前权限缺失时不得返回缓存身份/版本；字典删除重放仅在当前仍有全局 Delete 字段权限时返回原删除结果，不要求已删除记录重新出现。

**成员重放的授权证据：**最小回执不存储旧角色/旧授权快照。成功替换 membership 时，owner 在同一事务保存本次受影响角色 ID（原成员与请求角色的并集），不保存 grant 内容。重放重新读取目标用户、当前成员、请求角色和这些已记录角色的当前规则，并使用当前 session、字段范围和 grants 复验委派权限；缺少原受影响角色或任一当前规则不可委派时拒绝且不返回 ID/version。无关租户角色不参与判定，因此不扩大权限也不妨碍部分授权管理员重放；真实 MySQL 测试覆盖“已移除角色失去委派即拒绝”和“无关角色不阻断未变授权的重放”。现有不带 operation 的调用保持原行为。未来若引入角色删除/规则历史，必须复审此证据边界；不得在回执中存储 raw grants。

普通结果保留 90 天，到期后不再重放；操作 owner 在新提交事务中可替换同一到期回执。稳定账号、部门代码、角色代码、字典代码的领域唯一键始终保留且权威，不因回执到期删除。清理仅针对回执，按租户有界 tracked delete，禁止 ExecuteDelete。这里不引入后台任务或归档服务。

## 专用测试计划

`DirectoryReceiptTests` 仅通过已授权 `MySqlTestConnection` 使用 wes_test，生成独立 tenant 并精确清理：

- RCPT-01：独立 context 重放返回原结果，业务与审计各一次。
- RCPT-02：相同操作 UUID 不同参数显式冲突，无额外业务。
- RCPT-03：撤权、目标移出部门及会话失效后拒绝重放，结果不含缓存 ID/version。
- RCPT-04：同键并发至多一次效果；actor/tenant 隔离。
- RCPT-05：保存故障与取消原子回滚，重试可以提交。
- RCPT-06：90 天边界不重放且领域唯一身份不删除。
- RCPT-07：拒绝凭据回执；回执模型列仅包含批准字段；迁移仅新增 owner 表。

## 集成与证据

### 精确接线合同

```csharp
public sealed record DirectoryOperation(Guid OperationId);
public sealed partial class DirectoryService(
    PlatformDbContext database, TimeProvider clock, DirectoryOperation? operation = null);
```

HTTP 只解析具名非机密路由的 `Idempotency-Key` UUID，再传 operation；不要接收客户端 digest，不要将 correlation 当 operation。owner 从实际参数和可信 tenant/actor 计算指纹；包括命令种类、目标 UUID、expectedVersion 和相关参数，不含 correlation。成员 UUID 排序去重，角色规则/部门/字段集合稳定排序；字符串保持实际命令参数，不进行任意 JSON 属性排序猜测。相同 key 不同指纹/种类返回新增 `DirectoryDecision.IdempotencyConflict`（应映射 409；原枚举数值不变）。拒绝结果不携带 ID/version。

`CreateCredentialAsync` 在带 operation 时直接抛 `InvalidOperationException`，在读取/散列密码、变更用户前拒绝。不得给 credential/password/login/bootstrap 适配 operation。其余现有构造调用无需改动。

供 Platform 模块 ChangeRole/ChangeSetting owner 后续接线的小型 primitives（没有替它们修改 handler/事务）：

```csharp
public static readonly TimeSpan DirectoryCommandReceipts.Retention; // 90 days
public static string DirectoryCommandReceipts.Fingerprint(
    DirectoryCommandKind kind, params object?[] nonsecretValues);
public static Task<DirectoryCommandReceipt?> DirectoryCommandReceipts.FindAsync(
    PlatformDbContext database, TenantContext context, DirectoryOperation operation,
    DateTimeOffset now, CancellationToken cancellationToken = default);
public static void DirectoryCommandReceipts.Stage(
    PlatformDbContext database, TenantContext context, DirectoryOperation operation,
    DirectoryCommandKind kind, string fingerprint, Guid resultId, string resultVersion,
    DateTimeOffset now);
public static Task<int> DirectoryCommandReceipts.RemoveExpiredAsync(
    PlatformDbContext database, DateTimeOffset now, int limit = 100,
    CancellationToken cancellationToken = default);
```

三种持久化 primitive 均要求显式 owner transaction。`FindAsync` 按 tenant/actor/op 精确查询，到期仅标记删除，尚未 Save；活跃回执返回后调用者必须先比 kind/fingerprint、再读取当前 target 并复验 target scope/fields 和当前旧/请求 grants，然后才允许返回 safe result。`Stage` 仅暂存成功结果；业务+审计+receipt 一次 Save+Commit。`RemoveExpiredAsync` 仅暂存本租户最多 200 条到期回执删除（默认100）；调用 owner 自行 Save/Commit。未装配后台清理任务，不能宣称生产保留作业已上线；部署 owner 必须接线并监测清理运行。禁止以此 helper 对 setting secret 或 credential 参数计算摘要。

### 迁移与实际证据

- 新增 `20261024063825_DirectoryCommandReceipts`、EF designer/snapshot，仅新增八列 owner 表及 tenant/CreatedAt 索引；不修改/删除现有领域表。专用模型测试审查实际 Up 操作及 pending-model-change=false。生产仍需 DBA 审查/受控发布，绝不启动自动迁移。
- TDD：API 缺失编译红后，初始九项真实 MySQL 测试出现 **5 项行为红、4 项通过**（未实现回执的基线）。专用矩阵最近复跑 **31/31 通过，0 skipped**；见独立[测试记录](30-t04-command-receipts-tests.md)。
- 只使用已授权 `MySqlTestConnection` helper 和 wes_test；没有读取/输出明文连接配置或执行生产数据库命令。全solution和相关集成测试另见[就绪台账](readiness-register.md)；当前owner代码及HTTP接线见同批集成提交。
- 父任务同时修改 `PlatformAccessPersistenceTests`，其未完成的 `FailAfterAuditSql` / `InjectedAuditSqlFailureException` 定义一度阻止原 Persistence 工程编译。因此新增独立 `tests\DirectoryReceipts\Wes.DirectoryReceipts.Tests.csproj`，链接同一 `DirectoryReceiptTests`、原连接 helper/collection，使用原有相同版本测试依赖；不排除、更改或削弱父任务测试。该专用工程未加入全套 solution，原 Persistence 仍自然包含新测试。

复跑：

```powershell
dotnet test tests\DirectoryReceipts\Wes.DirectoryReceipts.Tests.csproj --no-restore --filter FullyQualifiedName~DirectoryReceiptTests --verbosity quiet
```

DirectoryService、ChangeRole、ChangeSetting及具名非机密写路由现已接入操作回执；`IdentityHttpTests`覆盖用户/角色/设置安全重放、参数冲突、撤权及缺失/非法key，独立[HTTP测试记录](30-t04-command-receipts-tests.md)提供6项定向结果。清理调度仍需部署owner接线与监测；成员透明重放限制保持显式未决，不标“全部完成”。
