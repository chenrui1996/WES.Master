# T02 MySQL Provider基础切片

- 状态：已实现并验证；仅真实Provider测试基础，不代表T02整体或登录功能完成
- 范围：FUNC-SYS-001/004、DTC-SYS-001/006的持久化基础子集、ARCH-TC-005首期MySQL部分
- 前置：[隔离库授权](02-mysql-isolated-test-environment.md)、[ADR-0002](../adr/ADR-0002-mysql-first-implementation.md)、[工作包T02](../superpowers/plans/2026-10-23-wes-development-work-packages.md)

## 思路与边界

仅在测试工程增加真实MySQL EF fixture，不建立通用生产DbContext/Repository，不把测试探针实体搬进WMS或Platform。生产Host仍无持久化模块，ready保持503；测试库连接通过不自动放行生产就绪。

测试评估固定MySql.EntityFrameworkCore 10.0.7；官方NuGet nuspec的net10.0依赖EF Core/Relational 10.0.7、MySql.Data 9.7.0，已由实际restore/锁文件确认；Pomelo官方索引当前最高9.0.0，不混配EF10。候选包许可证为GPL-2.0-only WITH Universal-FOSS-exception-1.0，保留上游包声明；本片仅测试评估，不代表团队已完成生产分发许可证审查。依赖锁文件、实际构建及真实引擎测试才是本片兼容证据，不据版本号推定完整兼容。来源：[Oracle包依赖](https://api.nuget.org/v3-flatcontainer/mysql.entityframeworkcore/10.0.7/mysql.entityframeworkcore.nuspec)、[Pomelo索引](https://api.nuget.org/v3-flatcontainer/pomelo.entityframeworkcore.mysql/index.json)。

从用户指定Seven配置读取连接只在测试装配进行，允许JSON注释/尾逗号；解析异常输出固定诊断，不包含配置片段、密码或原连接。使用驱动连接串builder在内存强制Database=`wes_test`，保留授权端点/用户名/密码；禁止本地文件导入、凭据持久回显，限制连接与命令超时，不启用重试掩盖故障。每次连接前再验证目标。生产Host不增加同级工程依赖。

## 探针数据、迁移与清理

- 唯一测试表`wes_foundation_probe`与专属迁移历史`__WesFoundationMigrations`；不创建/删除数据库，不复用业务迁移历史。
- 探针记录包含runId、tenantId、id、code、decimal(28,6)数量和显式并发version；tenant过滤在上下文参数化，不把租户隔离结果冒充业务授权。
- 显式迁移仅建这张探针表，主键含租户，唯一索引约束run/tenant/code。查询与清理均按当前runId；DDL串行，固定表及历史保留以便幂等复跑。
- 测试清理仅删除当前run已创建记录，并验证该run为0、其他run/租户样本仍在；不整库清空，不执行Down或删库。Down定义仅保留供审查，本轮未生成或执行回滚脚本。
- 仅连接`wes_test`，引擎版本通过当前连接获取；不执行业务库查询、kill、全局配置或实例重启。连接/迁移错误必须报告失败，缺配置不能跳过假绿。

## 先写测试

| CaseId | 输入/步骤 | 核心断言 |
|---|---|---|
| MYSQL-GUARD-001 | 合成业务库来源连接构造测试连接 | 数据库强制wes_test，端点和凭据不变且不回显；不修改来源 |
| MYSQL-GUARD-NEG | 空/非法来源或连接目标为业务库/空/大小写变体 | 打开前显式拒绝，错误不含敏感片段 |
| MYSQL-CONNECT-001 | 授权真实连接 | DATABASE()为wes_test，输出仅版本；同一guard每次打开前执行 |
| MYSQL-MIGRATE-001 | 显式迁移并再次执行 | 指定迁移已应用、无待迁移；仅测试表及专属history |
| MYSQL-TXN-001 | 写入后主动失败并回滚 | 新上下文读不到该记录，成功提交记录可读 |
| MYSQL-UNIQUE-001 | 同run/tenant重复code | 第二次持久化拒绝，第一次仍恰好一条；不同tenant可同码 |
| MYSQL-TENANT-001 | 同run两个tenant有相同id | 独立上下文只读到当前tenant，不串上下文模型缓存 |
| MYSQL-CONCURRENCY-001 | 两上下文读取同version后竞争更新 | 仅一次成功，另一DbUpdateConcurrencyException，数量/version只变一次 |
| MYSQL-CLEANUP-001 | 两run写入后删除本run | 本run为0，另run不变，最终仅清理自己创建的两run |

## 实测证据与团队上手

入口为`tests\Persistence\Wes.Persistence.Tests.csproj`，已加入WES.slnx。每个数据库用例有Provider=MySQL及CaseId trait；guard/JSON合成用例不依赖数据库。所有上下文的创建在连接前检查库名；JSON错误、驱动解析及首次连接错误输出固定错误/驱动编号，不附原始配置异常；不启用EF敏感数据日志。上游EF运行时的其他错误仍按测试失败报告，不能声称已实现整套业务日志脱敏。

### 红绿过程

| 阶段 | 实测结果 |
|---|---|
| 连接目标guard红灯 | 10项失败：来源未强制测试库、非法来源/误连目标未拒绝 |
| guard绿灯/JSON红灯 | 原10项通过；JSON读取及错误诊断5项失败 |
| JSON及只读连接绿灯 | 16项通过；DATABASE()=wes_test，VERSION()=8.0.12 |
| 迁移红灯 | 指定迁移不在已应用列表，实际列表为空；不是以连接/编译失败冒充红灯 |
| 约束/过滤/并发红灯 | 19通过/3失败：重复code未拒绝、两个旧version同时成功、跨tenant查询多行 |
| 修复后 | 初始22项全部通过；加入非法端口/JSON形态/缺文件、未持有run清理拒绝及实际引擎/精度验证后继续复测 |
| 最终验证 | MySQL工程29通过/0失败/0跳过；完整解决方案21架构+5 Host+29持久化=55通过；随后再次单独复跑MySQL工程29通过，fixture清理断言均通过 |
| 工程/文档门禁 | locked restore通过；构建0警告/0错误；format verify、IDE Problems、git diff --check通过；34任务DAG、64 PAGE/65 FUNC/167 DTC及265原TC追踪脚本通过 |

### 明确修复的技术问题

1. 首次尝试向EF传入已打开的驱动连接时，Provider创建检查连接会因PersistSecurityInfo=false丢失密码。该连接故障不计行为红灯；改为让EF持有内存中的原始已限库连接字符串，仍保持PersistSecurityInfo=false，并在上下文建立时显式打开连接以安全诊断失败，不把凭据暴露策略改为true。
2. 手写迁移的初版snapshot缺少完整Version列映射，Provider生成SQL失败；补全列类型/必填元数据，不计作业务红灯，不绕过迁移。
3. 实测8.0.12不支持Provider对ExecuteDelete生成的带AS别名DELETE。清理改为对有界、已过滤run的记录进行tracked RemoveRange，最终SQL按主键删除；这是明确的测试清理实现，不是悄悄捕获错误再回退。首次清理失败遗留的唯一已知run已单独定向清理并断言为0，临时恢复测试已移除。
4. 初始建表迁移已经应用后，新增唯一索引使用第二条迁移，未改写已应用Up；初始/第二条迁移的schema分别冻结，snapshot跟随最新schema。

固定保留表`wes_foundation_probe`（InnoDB，Quantity实际为decimal(28,6)）与`__WesFoundationMigrations`、两条探针迁移历史；每轮测试只清理本fixture生成的run，断言为0，清理反例保留另一合成run作为哨兵后同样在teardown清理。上述DDL不是Platform/WMS真实表，不能冒充业务迁移验收。

### 复跑命令

从WES仓库根目录运行：

```powershell
dotnet restore WES.slnx --locked-mode
dotnet build WES.slnx --no-restore --nologo
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-build --nologo
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-build --filter "Provider=MySQL" --nologo
dotnet test WES.slnx --no-build --nologo
dotnet format WES.slnx --no-restore --verify-no-changes
```

凭据取自本机授权的同级Seven文件，未复制到仓库；其他开发机缺该授权配置时显式失败，不跳过真库测试假绿。CI以后必须改为其获准的独立Secret配置入口，不能把本机路径视作产品部署依赖。IDE入口仍未发现测试，证据以dotnet test为准。

### 未覆盖与下一入口

生产身份最小权限未验；MySQL8.0.12较旧且已发现bulk SQL差异，不能据这组测试宣称EF全部API兼容；后续各owner必须对实际查询/迁移/并发分别回归。实例升级/重启/断网不在本次授权内。SQL Server/PostgreSQL继续deferred；许可证生产分发审查、实例级恢复、真实设备/H及完整父DTC均非本片证据。

T02仍须完成Host错误映射/关联日志与脱敏、真实必需依赖断开/恢复readiness测试、Host不夹业务/SDK等更深架构门禁，再评估T03；不以测试DbContext已连通就将完整T02勾完。

本轮未提交/推送，未改Seven配置、未复制凭据到WES配置、未修改原业务库、未创建账号或数据库、未重启服务。唯一数据库变更是wes_test内的两条探针迁移及已清理的本轮合成数据。
