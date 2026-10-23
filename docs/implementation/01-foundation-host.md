# T02 首片：最小Host与架构门禁

- 状态：首片已实现并验证；T02整体仍实施中，仅FUNC-SYS-001/004、DTC-SYS-001/006的工程基础子集
- 依据：[架构§7](../02-system-architecture.md)、[治理](../03-engineering-governance.md)、[T01就绪台账](readiness-register.md)
- 不含：业务API/认证、库存/Route算法、客户端、真实数据库迁移及设备连接

## 实现思路

Host只作为装配根，使用ASP.NET Core现有健康检查机制。`/health/live`只证明HTTP进程可服务；`/health/ready`检查必需依赖集合，尚未装配/验证数据库时返回503，不能因零个检查而默认Healthy。响应仅固定健康状态，不包含连接串、异常或拓扑。

首片没有数据库Provider或连接配置入口，明确记录`DatabaseNotConfigured`；后续持久化切片替换为真实MySQL连接/迁移状态检查并补断连/恢复测试。本片不能通过配置布尔值伪造ready；设备控制没有任何端点/驱动装配，写入次数为0来自未实现能力而非安全认证。TimeProvider在DI注册，后续用例显式注入可控钟。

架构门禁读取实际生产csproj依赖，同时对故意非法的项目依赖夹具运行同一验证器：模块之间只可引用目标owner的Contracts项目；Domain/Application不能依赖Host/Infrastructure；Host禁止Builder项目注册。程序集/namespace/SQL访问更深层检查随真实模块加入，不声称仅项目图已能证明所有数据所有权。

本片解析项目文件中显式的ProjectReference，包括XML namespace及条件引用（保守地全部校验），不执行MSBuild条件求值。仅接受仓库内存在的csproj字面路径；空引用、属性/通配符/多路径表达式、缺失目标或越出仓库均显式失败。跨owner仅放行目标owner下具名Contracts目录中的`.Contracts.csproj`。不扫描导入targets生成的边、PackageReference、程序集类型或设备SDK；将来引入这些依赖前补相应门禁，不能把本片结果解释为完整ARCH-TC-001通过。

## 行为测试与反例

| CaseId | 场景 | 必须结果 |
|---|---|---|
| FOUNDATION-LIVE-001 | 未配置数据库启动HTTP | live=200，ready不能随之变200 |
| FOUNDATION-READY-001 | 未装配数据库 | ready=503、固定Unhealthy、不回显内部配置 |
| FOUNDATION-SURFACE-001 | 请求尚未实现控制路由 | 404，无伪Accepted或业务成功 |
| FOUNDATION-CLOCK-001 | 从实际Host作用域解析TimeProvider | 已注册，可由未来测试装配替换 |
| ARCH-TC-001 | 实际生产项目图 | 无跨owner私有依赖 |
| ARCH-TC-001-NEG | 故意跨owner Infrastructure/DbContext/Host引用 | 同一检测器报告违规，不能只检查正例空图 |
| ARCH-BUILDER-NEG | Host引用Builder | 显式拒绝；不以项目名称隐藏生成器为通过证明 |
| ARCH-GRAPH-001 | 临时XML含相对引用/namespace或无引用 | 正确提取边并送入同一验证器；合法叶节点无边 |
| ARCH-GRAPH-NEG | 空/表达式/缺失/仓库外引用 | 显式InvalidDataException，不跳过后报告成功 |

API测试使用真实Host的WebApplicationFactory，不访问数据库、不替换真实库为InMemory。架构测试用临时XML项目图夹具，不把违规项目加入生产解决方案。测试按AAA区分；断言状态码/输出/违规边，不断言mock被调用。

## 运行、恢复与限制

本片仅适合本机开发。无用户登录/权限实现，因此不对外绑定接口、不放行生产；部署程序必须分别看live和ready。缺配置保持未就绪，不重试物理命令、不自动迁移数据库。停止进程无业务数据可丢弃；后续有事务/Outbox后必须扩展恢复手册。

## 实测证据

本轮使用.NET SDK 10.0.103、net10.0；Host仅使用共享框架。测试依赖锁定Microsoft.AspNetCore.Mvc.Testing 10.0.1、Microsoft.NET.Test.Sdk 18.7.0、xunit 2.9.3、xunit.runner.visualstudio 3.1.5；各工程保留packages.lock.json。

| 阶段 | 实际命令/入口 | 结果 |
|---|---|---|
| 初始构建 | 父工作区任务`WES: build`，构建WES.slnx | restore与3个工程构建成功 |
| 初始红灯 | `dotnet test WES.slnx --no-build --nologo` | 架构6失败/4通过；HTTP及DI 3失败/2通过；分别为违规未拒绝、端点404和时钟null |
| 项目图补充红灯 | `dotnet test tests\Architecture\Wes.Architecture.Tests.csproj --nologo --verbosity quiet` | 15失败/6通过；包括XML边未读取及非法引用未报错，非编译/环境失败 |
| 最小实现绿灯 | `dotnet build WES.slnx --no-restore --nologo`及`dotnet test WES.slnx --no-build --nologo --verbosity quiet` | 构建0警告/0错误；架构21通过，HTTP及DI 5通过，合计26通过/0失败/0跳过 |
| 格式/静态 | `dotnet format WES.slnx --no-restore --verify-no-changes --verbosity minimal`、IDE Problems及`git diff --check` | 均无错误 |
| 设计追踪脚本 | 会话原有development-plan及detailed-design验证脚本 | 34任务DAG/16波次、64 PAGE/65 FUNC/167 DTC、265原TC映射通过；链接按当轮文件检查 |

命令均可从仓库根目录执行。IDE测试入口仍返回“No tests found”，故行为测试证据来自真实dotnet test执行；重启发现器不作为交付前提。构建任务曾因工具查找失败，后续构建使用相同解决方案的命令行，不把工具故障计作行为红灯。

新成员上手：先`dotnet restore WES.slnx --locked-mode`，再执行上表构建、测试和格式命令。若以`dotnet run --project src\Host\Wes.Host.csproj --no-build`启动，仅本机访问live=200/Healthy、ready=503/Unhealthy是当前预期；不能以live通过宣称可投用。HTTP测试使用实际Host的内存TestServer，不代表外网部署、身份授权或网络隔离已验收。

没有执行真实MySQL、SQL Server/PostgreSQL、完整父DTC、客户端或现场H；这些始终不计passed。首片未连接数据库、未启动设备适配器、未迁移数据，未提交或推送。
