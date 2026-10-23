# T02 HTTP运行时与依赖门禁

- 状态：已实现并验证；T02当前软件基础门禁已落实，不新增业务范围或生产许可
- 依据：[软件契约§1..2](../contracts/01-business-and-execution-contracts.md)、[工作包T02](../superpowers/plans/2026-10-23-wes-development-work-packages.md)、[治理](../03-engineering-governance.md)、[系统运维设计](../detailed-design/system-operations.md)
- 参考：Seven HealthChecksExtensions与HTTP middleware；仅复用存活/依赖探测和关联日志意图，不复制其测试InMemory、隐式Provider默认、错误回显或Builder装配

## HTTP边界与日志

Host只拥有传输装配。`X-Correlation-Id`输入仅接受一个非空UUID（标准D格式），规范为D格式；缺失、重复、无效或全零均生成新UUID，不信任任意字符串。请求上下文与返回头使用同一个ID，ProblemDetails扩展`correlationId`与该头一致。Correlation不是身份/授权。

运行时异常边界将未知故障映射500/InternalError，超时映射503/DependencyUnavailable，绑定错误映射400/ValidationFailed；每种只用固定的可安全展示标题/详情，不含原始异常、SQL、设备payload、URL查询或配置。请求已取消不产生伪500，已经开始的响应不能重写，保留异常由宿主终止连接。尚未实现的路由404仍不变，但补ProblemDetails/ResourceNotFound。401/403等空响应按相同稳定错误code补齐；后续owner正常业务拒绝直接产出明确结果，不用抛异常当流程。

Serilog以DI局部logger接入，不使用跨测试宿主共享的静态logger。启动前局部bootstrap只写固定生命周期消息，DI接管后使用结构化日志；级别来自无secret的appsettings，基础框架请求/异常诊断不写原始路径或异常。业务日志模板严格白名单，仅correlation、固定code、HTTP状态/已知method与匹配route模板，不记录headers/body/query/path变量/exception对象。安全摘要日志与响应同时验证；不以删除错误日志隐藏故障。该策略仅覆盖已实现HTTP边界与健康探测，不声称后续任意第三方业务日志自动脱敏。

## 真实依赖与未完成装配

MySQL探测放Platform的Infrastructure（不把驱动/SQL放Host）。使用验证过的MySql.Data9.7.0，强类型DatabaseHealth options；连接仅受保护进程配置注入，生产Host不读取Seven文件。开发/测试装配按已授权来源在内存构造`wes_test`连接；本片显式限定库名wes_test，未来产品数据库选项需另行细化，不能在此阶段误连业务库。

检查只执行`SELECT 1`，超时有界、不建库不迁移，不缓存成功、不自动重试。缺配置Unhealthy；格式/目标错误在启动选项验证时显式拒绝且不含值；连接失败返回固定Unhealthy并记录仅错误编号，不附异常。调用取消向下传递，不把取消当依赖成功。

`/health/live`不探依赖；新增`/health/dependencies`仅运行ready依赖检查并输出固定Healthy/Unhealthy；`/health/ready`还包含“业务模块尚未装配”门禁，仍503。依赖连通不等于产品可用。健康响应无详细拓扑/配置。测试用同一探测器分别指向真实wes_test与明确本机不可达端点，再恢复正确连接；不停止MySQL服务，不改网络规则，不查询其他库。生产Host未实现reload保证，因此恢复测试重建只读探测器，不冒称支持热重载。

## 架构与反例

项目图继续检查跨owner私有依赖；扩展Roslyn语法/语义夹具检查Host直接依赖DbContext/驱动、实体状态或SQL和设备SDK，把健康/协议细节留在模块。以实际生产源码与故意违规独立fixture运行同一检查，不用源文件关键字grep替代结构化检查；具体可证明范围随实际工具结果记录，不虚称检测所有任意业务规则。

## 用例与完成标准

| CaseId | 验证 |
|---|---|
| FOUNDATION-CORRELATION-001 | 有效、缺失、无效、多值及空UUID；响应/错误/log scope ID一致；并发请求不串ID |
| FOUNDATION-ERROR-001 | 未知异常500、超时503、绑定400、404/401/403空响应ProblemDetails；固定code且不泄敏感标记 |
| FOUNDATION-LOG-001 | header/query/body/path与异常均带合成secret；实际Serilog捕获输出仅安全字段，同时有错误事件和correlation |
| FOUNDATION-READY-002 | 真实MySQL健康→错误端点未就绪→恢复健康；缺配置未就绪；live不随DB失败；应用未装配仍503 |
| FOUNDATION-CONFIG-001 | 非法连接/非wes_test/非法timeout启动失败；无原始值/内部异常回显 |
| ARCH-HOST-NEG | 实际Host纯装配/HTTP适配，故意夹持久化/设备/领域fixture拒绝 |

保留已有26条及MySQL29条回归。每片先行为红灯再实现，记录实际命令与计数。T02全部适用门禁实测后才评估T03；客户端、现场H、SQL Server/PostgreSQL与生产账号权限不在本片验收中。

## 实测结果与维护边界

### 红绿证据

- HTTP边界首轮14项红灯：缺关联头、错误无ProblemDetails、异常外抛/无安全日志、并发scope不存在。实现后额外捕获ASP.NET自动附加RequestPath泄露合成secret，最终sink前使用白名单移除继承属性，原始path/query/body/header/exception标记均不进入响应与实际Serilog事件。
- 依赖检查首轮8项红灯：未注册检查/验证，正常配置与故障端点均未产生预期状态。实现后真实MySQL健康→127.0.0.1:1故障→恢复健康通过；没有停止、重启、断开其他会话或改网络配置。
- 边界补充红灯后修复：非法整型timeout经ConfigurationBinder抛错时可能反射原值，改为组合根显式解析后仍绑定强类型Options，验证异常只含固定字段说明；owner自行返回的ProblemDetails保留业务code并补correlation；已取消请求不写500；每次探测前再校验目标以防options误改；5xx以Error而非Warning记录。
- Roslyn负fixture6项按预期因未拒绝非法symbol而红，正例和实际源码编译通过后实现解析。最终8项语义测试通过（6非法、1合法、1实际Host）；不把编译fixture元数据重复引用问题计作行为红灯。

### 可证明范围

Roslyn4.14.0编译输入后检查实际解析symbol，能识别直接、全限定与alias对EF/MySQL/SQL驱动、已知PLC SDK、Seven.Builder及Wes.*.Domain/Infrastructure的Host引用；注释/字符串不误报。项目图继续独立验证owner.Contracts和层方向。它不是任意业务规则证明器，无法识别改名反射、动态SQL字符串或未知供应商SDK，也不代替后续owner私表/扩展hook与完整DTC-SYS-006集成验收。

生产MySQL依赖检查是Platform首个实际Infrastructure能力，Host只调用PlatformModule装配入口；没有注册会话持久化/身份签发服务。未来加入模块不得用现成健康200移除ApplicationNotAssembled门禁，须将已验收必需模块及可用性状态纳入明确装配策略。

### 上手、诊断与回退

1. 仓库根运行`dotnet restore WES.slnx --locked-mode`、`dotnet build WES.slnx --no-restore`、`dotnet test WES.slnx --no-build`。真实MySQL测试依赖已批准的同级Seven配置；Host本身不读Seven。
2. `dotnet run --project src\Host\Wes.Host.csproj --no-build --urls http://127.0.0.1:5188`仅启动本机HTTP诊断。无配置时live200，dependencies503，ready503；未知API404 ProblemDetails。每个响应均有可用于安全日志检索的X-Correlation-Id。
3. 如为Host注入健康探测配置，只用受保护的`DatabaseHealth__ConnectionString`，指定wes_test，`DatabaseHealth__TimeoutSeconds`为1..5；不要把真实值写命令行、文档、appsettings或日志。成功依赖200也不等于业务ready。
4. Serilog.Console由DI管理生命周期并在Host停止时释放；bootstrap为局部固定消息logger，不与多个测试宿主共享静态Log.Logger。级别在无secret的appsettings中配置，安全属性白名单不可由配置放宽；未开远程sink、日志查询UI或OTLP，不声称完整观测平台上线。
5. 回退只涉及重新部署本机软件，不包含数据库降级、实例操作、设备控制或回滚地图。错误可用correlation+code检索；未知业务/设备结果仍回owner核验，不据日志修改完成事实。

T02软件基础门禁可供T03内部切片使用；完整父DTC、所有业务owner迁移/私表保护、生产签核与三Provider兼容仍按后续工作包分别验收。连续批次的最终构建、测试计数及本机HTTP实测见[review交接](06-review-handoff.md)。
