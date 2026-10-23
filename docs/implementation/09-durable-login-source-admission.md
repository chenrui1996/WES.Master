# T03 可信来源登录预算耐久切片

- 状态：内部来源准入切片已实现并验证；不是完整登录、账号锁定、验证码或T03验收
- 依据：[PAR-02](../detailed-design/05-approved-rules-and-defaults.md)、[Platform详细设计](../detailed-design/platform-and-security.md)、[会话首片](05-platform-session-core.md)
- 目标：把已测试的IP滚动60秒最多30次规则接到真实MySQL原子准入，不再仅有无状态策略

## 边界与实现思路

来源预算必须跨账号/租户累计，包括未知账号、错误验证码、暂锁账号的请求；因此不能把来源行放在租户过滤下，也不能借TenantId=0关闭所有业务过滤。新增Platform私有`LoginSourceDbContext`，只映射来源预算一张安全基础表，不含人员账号、业务对象、角色或会话表，使用独立迁移历史。既有`PlatformDbContext`租户强制校验不放宽。该专用范围落实PAR-02已有跨租户来源语义，不是创建共享业务租户。

可信来源由后续HTTP边界从已验证的连接peer/获批代理解析；本片只接受`IPAddress`而非任意Forwarded文本，不读代理头。IPv4-mapped IPv6统一到IPv4，IPv6按地址字节（忽略本机scope索引）确定身份。存储SHA-256地址摘要，不记录原IP；这只是可字典推算的假名化，不宣称匿名。日志只含固定code/冲突轮次，无地址、摘要或请求内容。尚无HTTP端点/代理部署，不冒称防伪来源链路完成。

每个来源行最多保存30个已准入UTC instant及最近观察时间；窗口是`(now-60s,now]`，不是固定分钟桶。数据库精度datetime(6)，进入规则前统一截到微秒，避免持久化精度损失使60秒边界提前放行。超过预算明确拒绝，不延长已有观察窗口、不写无限失败流水。时钟倒退或损坏/未来观察显式失败，不清空预算来换取可用性。配置不接受客户端更改30/60初值。

`AdmitLoginSourceHandler`必须使用干净、无外部事务（包括System.Transactions环境事务）的专用DbContext。读取/创建来源行、判定、追加观察、新UUID版本一起SaveChanges，**提交完成后**才返回Admitted。已有行用版本CAS；首次建行用主键唯一约束；只针对本次单行的CAS冲突/明确主键1062重读，有限8轮后显式故障，不把高竞争或数据库失联变成放行。8轮是内部有界竞争重读保护，不是放宽30/60业务预算，也不是设备/一般失败重试策略。其他数据库异常/取消直接传播；finally清理本用例图，不留下失败观察供后续误提交。进程锁/缓存不是预算权威。

来源预算是登录流程第一步，不与后续密码验证回滚：即使账号不存在/验证码失败，也已消费一次来源请求。响应丢失后重试仍是一条新登录请求，不能以幂等缓存绕过30次预算。账号失败窗口、单次验证码、凭据验证、完整安全审计、JWT和页面仍在后续接线，源预算通过本身永不签发会话。

提交结果不确定（例如数据库已提交但响应中断）时向调用者传播故障，不承诺取消/断网一定没有消费，也不补偿清空观察。保存前注入失败/取消可验证无写入；数据库事务保证该行原子性，不等于分布式提交结果必然可知。

## 数据、迁移与维护

`platform_login_sources`：SourceDigest主键varchar(64)、ObservationsData严格JSON、LastObservedAt UTC datetime(6)、Version并发UUID。专属`__WesLoginSourceMigrations`不触碰已应用的Platform迁移历史。生成迁移须确认只新增此表及其索引，无AlterDatabase/原始SQL/删其他表；只在授权wes_test应用。

新增第二DbContext后EF CLI必须显式`--context PlatformDbContext`或`--context LoginSourceDbContext`，以免错误选择迁移owner。设计时factory只创建元数据、不读取凭据/打开连接。

本片没有后台清理或公开预算重置API。过期观察只在下次访问该行时淘汰，行数仍随来源增长；正式公网登录上线前须随账号/验证码接线落实受控安全维护、来源容量/分布压力与入口防滥用验收，不能将固定30个时间戳等同总存储有界。测试只清理本次生成来源摘要，不全表删除、不重启共享服务。

## 测试计划

| CaseId | 可观测反例/预期 |
|---|---|
| IDENTITY-SOURCE-KEY-001 | 相同IPv4/IPv4-mapped归同键；不同IPv6地址分离；scope不构成可绕过身份；非IP输入无接口；格式化不带地址 |
| IDENTITY-SOURCE-WINDOW-001 | 第1..30次准入、第31次拒绝；59.999999秒拒绝、60秒放行；非固定桶；同瞬间重复仍计数；微秒精度往返一致 |
| IDENTITY-SOURCE-CLOCK-001 | 倒退时钟/未来或损坏持久观察显式失败，原预算不清空 |
| IDENTITY-SOURCE-DB-001 | 新context/handler可读持久预算，其他来源独立；全局context没有租户键/角色/会话映射 |
| IDENTITY-SOURCE-CONCURRENT-001 | 强制两handler在第30次边界重叠，恰1提交/1限流；首次同源同时创建不丢计数；重读不丢同瞬间记录 |
| IDENTITY-SOURCE-FAULT-001 | 保存前故障/未知DB错/取消不返回Admitted；无部分消费；重复竞争预算耗尽明确故障；不向日志写地址/凭据 |
| IDENTITY-SOURCE-MIGRATION-001 | 专属迁移只新增1表，不放宽Platform租户保护，不混用迁移历史；测试清理仅生成主键 |

## 红绿及真实验证证据

- 领域5项首先全部行为红灯：canonical来源键为空、准入总拒绝、无窗口/时钟语义；实现后5项通过，包括独立SHA-256地址字节向量，不靠同一helper同时生成预期与实际。
- 独立迁移范围测试先因空迁移红灯；生成后再次明确拒绝EF自动附加的AlterDatabaseOperation。首次应用前移除该库级操作，仅创建本owner表；未篡改已应用迁移，也未修改原Platform schema/history。
- 最终检查再将迁移测试改用实际design-time factory，发现其历史表仍是provider默认`__EFMigrationsHistory`，断言明确红灯。修正factory也显式使用`__WesLoginSourceMigrations`后20项来源DB/迁移测试通过，避免仅测试运行时options而漏掉EF CLI入口。
- 第一轮真实准入测试13失败/0通过，缺提交、首次/第30次并发、取消/脏context/坏schema和安全日志均被捕获。实现后13通过，再补环境事务拒绝、真实CAS八轮耗尽、非竞争DB错直接传播、保存中取消、落后时钟和独立迁移历史回归；最终来源DB/迁移20项通过。
- CAS耗尽用第二个真实DbContext在每次保存前提交另一个请求，实际SQL版本竞争连续8次失败；只计入8个竞争者请求和原1次，不把失败者计入成功或默认为限流。第30次并发用保存屏障强制重叠，恰1准入/1拒绝；同时首次创建恰1行/2次观察。
- Oracle MySql.EntityFrameworkCore10.0.7/EF10.0.7/MySql.Data9.7.0，真实MySQL8.0.12，仅wes_test。无新增依赖，无生产连接、旧账号读取或设备动作。测试按本fixture两项随机来源摘要精确清理。
- 集成Release：Architecture29、Modules104、Persistence94、Route228，共455 passed、0 failed、0 skipped；构建0警告/0错误。locked restore、全solution格式与两个owner迁移快照一致性通过。IDE测试发现未能定位测试，以上均来自真实dotnet CLI，不把“No tests found”计作通过。
- `TenantContext`与既有PlatformDbContext的租户语义未修改；来源上下文只含来源实体，测试明确禁止其混入Session/AccessRole/TenantId=0等共享业务模型。

## 团队复跑

先执行README中的统一locked restore/build，再运行以下命令。迁移生成/检查指定context，数据库应用必须另取得环境许可；本片不增加Host自动Migrate。

```powershell
dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-build --filter FullyQualifiedName~LoginSourceBudgetTests
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-build --filter FullyQualifiedName~LoginSource
dotnet ef migrations has-pending-model-changes --context PlatformDbContext --project src\Modules\Platform\Wes.Platform.csproj --configuration Release --no-build
dotnet ef migrations has-pending-model-changes --context LoginSourceDbContext --project src\Modules\Platform\Wes.Platform.csproj --configuration Release --no-build
dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1
```

下一步仍需补齐：账号滚动失败/锁定与未知账号等价路径、验证码120秒一次消费、兼容凭据/密码历史及泄漏核验、统一错误及安全事件审计、JWT/可信代理边界、真实登录/会话页面和E2E。UTC来源需在多实例部署时验证；本片检测倒退并拒绝，但不证明任意时钟偏差下都具备准确真实时间限流。HTTP上线前还须验证行增长/受控清理和故意锁号缓解，不将内核通过当作完整生产防滥用方案。
