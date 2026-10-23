# 连续实施批次Review交接

- 日期：2026-10-23
- 状态：供review的已验证增量，**不是34个工作包全部完成的最终产品**
- 范围：T02软件基础与T03身份核心；总进度以[就绪台账](readiness-register.md)为准，原[34包计划](../superpowers/plans/2026-10-23-wes-development-work-packages.md)没有被替换或缩减

## 1. 本批交付

| 内容 | 实现/证据 | 不应误解为 |
|---|---|---|
| HTTP运行时 | UUID关联、安全ProblemDetails、真实结构化日志属性白名单、取消/异常边界 | 所有后续业务日志已自动脱敏或审计系统上线 |
| 依赖健康 | Platform内只读MySQL probe、强类型配置、真实不可达/恢复、三种健康路由 | dependencies200就能业务/设备派发 |
| 架构门禁 | 项目引用图+Roslyn语义反例/实际Host编译 | 任意动态SQL/反射/未知SDK均可检测，或所有owner边界已验收 |
| 会话领域 | 15分钟Access、Web/PDA idle、8小时绝对期限、摘要消费/撤销 | 已能登录、签JWT或操作WMS |
| 真实持久化 | Platform两表/迁移、tenant过滤/写保护、数据库FK、真实竞争与冲突重读撤销 | 完整身份模块、全量账号迁移或最小权限已验 |
| 纯安全策略 | 账号/IP滚动窗口、新密码Unicode和明确证据拒绝 | 分布式限流/验证码服务或泄漏密码数据源已上线 |

没有批量生成空CRUD、替代状态机、生成式页面或虚拟成功端点；未启动其他31个工作包来制造覆盖率。

## 2. 本机复跑

从WES.Master根目录执行，SDK基线10.0.103：

```powershell
dotnet tool restore
dotnet restore WES.slnx --locked-mode
dotnet build WES.slnx --no-restore --nologo
dotnet test WES.slnx --no-build --nologo
dotnet ef migrations has-pending-model-changes --context PlatformDbContext --project src\Modules\Platform\Wes.Platform.csproj --no-build
dotnet format WES.slnx --no-restore --verify-no-changes
```

真实MySQL测试必须能读取已授权同级Seven配置，只在内存强制wes_test；不会查询原业务库或把凭据写入WES。不要用缺环境跳过MySQL后宣称整个解决方案通过。其他机器/CI接入受保护配置入口的实现尚未完成，不能认为复制WES文件夹即可脱离当前授权环境跑数据库测试。

本机HTTP：

```powershell
dotnet run --project src\Host\Wes.Host.csproj --no-build --urls http://127.0.0.1:5188
```

默认未注入探测连接：live200、dependencies503、ready503；未知API404且返回安全ProblemDetails/关联ID。Host故意不读取Seven或装配未完成身份/业务模块，不能把这个命令当业务演示或生产启动手册。后续装配只能依据真实验收替换ApplicationNotAssembled门禁。

## 3. 本批测试证据

完整解决方案实跑：**137通过、0失败、0跳过**（Modules70、Architecture29、Persistence38）。覆盖范围包括新旧基础测试、身份领域/策略、实际MySQL探针与会话；不是167父DTC或34工作包全部通过。各切片红绿记录见[运行时](04-host-runtime-foundation.md)、[身份核心](05-platform-session-core.md)；前轮55条为历史基线，不重复相加。

- locked restore及构建通过，0警告/0错误；第一次总验证发现项目引用改变导致Architecture锁文件过期，显式重算锁文件后重新locked restore，不绕过锁模式。
- EF模型/快照一致性检查通过（No changes have been made to the model since the last migration）。生成迁移的BOM/namespace与新增测试import顺序已用既有dotnet format修复，最终format verify通过，未关闭分析器或降低warning-as-error。
- 实际独立Host在127.0.0.1:5188启动并通过HTTP断言：live200/Healthy、dependencies503/Unhealthy、ready503/Unhealthy；POST身份端点404/ResourceNotFound，安全ProblemDetails的correlation与请求一致；四条响应关联UUID均有效。测试后已停止本轮Host进程，不留常驻后台进程。
- IDE Problems无错误；git diff --check通过。计划脚本验证34任务DAG、16波次与64 PAGE/65 FUNC/122模块DTC首责；设计脚本验证167父DTC、265原TC映射和本地链接。本文/就绪台账补实际状态，不把原计划复选框或父DTC一概勾选。
- 真实库清理断言均通过；不会使用全库count=0来证明清理，更不会删除之前已存在的其他tenant数据。两个真实handler竞争由保存屏障强制重叠，不以顺序调用冒充并发；另有相同sessionId跨tenant与数据库复合外键反例。

## 4. Review重点与剩余范围

1. **未完成总体范围：** T01仍有具名角色/现场输入；T02软件基础供开发使用但不代签完整父DTC；T03仅内部核心，登录凭据、验证码、预算原子持久化、JWT、改密/禁用全撤销、审计/API/页面/E2E仍待实现；T04..T34均未完成。当前没有Vue/PDA业务界面、WMS账本或Route实现。
2. **数据库限制：** MySQL8.0.12较旧，已有ExecuteDelete别名语法与Guid/char映射差异。已通过的具体操作有真实结果，不承诺Provider全部API兼容；不得直接升级/重启共享实例。SQL Server/PostgreSQL延后。
3. **权限与许可证：** 用户授权沿用既有来源账号，未更改该账号；客户端限库校验不等于数据库最小权限。Oracle Provider的生产分发许可须团队审查；包元数据/许可证不被删除，开发测试通过不替代合规签核。
4. **身份安全：** 新密码128 Unicode标量不兼容classic BCrypt72字节截断语义；已提供不截断的形状策略，后续完整哈希/旧验证器兼容需明确设计和回归，不能直接复用Seven工作流吞异常/回显令牌日志。泄漏/历史核验Unknown一律不接受，不提供“缺资源就放行”默认值。
5. **现场H：** 协议能力、物理参数、真机切换/恢复与具名安全签核保持阻断；未发任何设备命令，不以数据库或领域测试当作物理安全证明。

## 5. 数据库变更与操作边界

- 保留wes_test原有探针表/历史；新建仅`platform_sessions`、`platform_refresh_tokens`和`__WesPlatformMigrations`，初始迁移已在应用前拒绝并移除数据库级AlterDatabase操作。
- 只写合成session/摘要记录，按本测试生成的精确tenant/session或runId清理，并验证子表/根表均无本轮遗留。
- 未读取旧业务数据、未修改Seven配置、未复制凭据到仓库、未改全局配置/网络、未创建账号/数据库、未停其他会话/服务。
- 沿用main实施授权，保留原有未提交设计；没有自动commit/push，也未声称最终产品可生产投用。
