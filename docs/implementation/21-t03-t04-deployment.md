# T03/T04 身份与平台部署/恢复手册

- 状态：实施中；owner：Platform / Host 部署者；生产批准仍由具名运维、安全和DBA负责。
- 范围：T03/T04、PAGE-PLAT-001..006/008；不授予业务控制、设备控制或现场H门禁。
- 关联：[HTTP合同](19-t03-authenticated-http.md)、[目录owner](18-t04-directory.md)、[页面合同](20-t03-t04-operations-client.md)、[治理](../03-engineering-governance.md)。

## 1. 发布前输入与禁止项

当前经实施验证的数据库为MySQL，依照ADR-0002，不把本轮结果解释为SQL Server/PostgreSQL认证。测试只连接隔离的`wes_test`；生产迁移、账号建立、现场批准和生产泄漏语料都不是自动化测试的副作用。Host不自动迁移，不附带默认账号/密码/管理员规则。

部署者通过受控secret/configuration来源提供以下键；不要把实际值放入仓库、发布日志、命令行历史或客户端bundle：

| 配置键 | 约束与owner |
|---|---|
| `Wes:Identity:Enabled` | 显式开启；默认关闭 |
| `Wes:Identity:ConnectionString` | Platform owner连接；DBA建立最小运行权限，迁移权限使用独立流程 |
| `Wes:Identity:SigningKey` | Base64，64..256字节独立随机HS512密钥 |
| `Wes:Identity:ChallengeKey` | Base64，32..256字节独立随机challenge MAC密钥；不得与签名密钥相同 |
| `Wes:Identity:Issuer` / `Audience` | 明确部署绑定，不跨环境复用身份 |
| `Wes:Identity:BreachCorpusPath` | 本机受控、非空、UTF8无BOM；每行64位大写SHA256十六进制摘要 |
| `Wes:Identity:KeyRingPath` | Production必须设置为已存在的受控绝对目录 |
| `Wes:Web:Root` | 可选；只允许已构建bundle的绝对目录，必须存在index.html，绝不能指向源码/secret目录 |

生产泄漏语料由安全owner记录来源、版本、完整性摘要、更新日期和恢复办法；测试的合成语料没有真实泄漏覆盖承诺。不可读/格式错/空语料导致503，不能作为密码Clear证据。当前实现逐次完整扫描，部署前以实际语料测量性能；未经测量不得声称大型语料吞吐达标。

## 2. 持久key ring与HTTPS

当前持久化适配器限定Windows：固定DataProtection应用名`WES.Master.Identity`，服务身份级DPAPI保护磁盘key，refresh另使用独立purpose。不配置生产key ring、使用相对/不存在目录会启动失败；不能以临时key、明文key或development环境绕过生产门禁。非Windows/跨身份/多机共享key保护没有本轮认证，必须另行批准并实现平台保护器。

由运维提前创建专用目录，只赋运行服务身份所需读写权限，排除普通用户、Web静态根和客户端构建输出。密钥备份必须同时考虑DPAPI服务身份/profile的恢复条件；仅复制XML到另一账号/机器不是恢复方案。独立安全owner验证ACL、备份、灾备和密钥轮转；本轮测试不代签这些环境条件。

Kestrel端点必须真实HTTPS。当前未注册ForwardedHeaders，来源预算只使用连接的RemoteIpAddress，不相信客户端X-Forwarded-For/X-Tenant。反向代理不能仅终止外部TLS再把HTTP送入当前适配器；使用经批准的端到端HTTPS拓扑。新增可信代理配置前需有显式可信代理名单、来源预算与伪造头测试，不得笼统信任任意代理。

浏览器refresh为`__Host-wes-refresh`，Secure/HttpOnly/SameSite=Strict/Path=/；access仅保留内存。所有身份/平台API no-store，无CORS，不在URL传token。静态托管带CSP、nosniff、no-referrer，未知/api不会回退为SPA。不要开放请求body/Authorization/Cookie响应日志。

## 3. 迁移与首次本地建立目录

1. DBA审核Platform与LoginSource两个context的增量SQL、备份、历史表和回退影响；先在隔离库演练。历史表分别为`__WesPlatformMigrations`和`__WesLoginSourceMigrations`。
2. 应用前确认批准的目标库，受控执行迁移；不把测试fixture的自动迁移复制为生产启动行为。
3. 准备明确的`Wes:Provisioning`元数据：Enabled、TenantId、UserId、TenantName、DefaultLanguage、DepartmentCode、DepartmentName、Account、DisplayName、RoleCode及ReviewedGrants。规则必须经过管理/安全owner审阅，不能推导超级管理员权限或塞入通配默认值。
4. 在受控交互终端以`--wes-provision=true`启动Host。只有该进程参数及Enabled双门禁通过才读取密码；密码在非回显终端输入，禁止重定向、命令参数或文件输入。该分支不启动HTTP listener、不自动迁移。
5. 目录owner原子建立租户/部门/用户/独立凭据/管理角色/成员关系、三项已批准非secret配置与审计；失败返回非零状态，不能强行当成已建立。
6. 关闭Provisioning门禁并移除临时部署配置。使用具名账号首次登录，核对当前权限和审计；不要保留默认凭据、泄漏语料Clear假值或额外引导端点。

## 4. 发布、故障恢复与验收

前端使用锁文件恢复依赖并执行单测/strict build，将构建结果作为只读部署资产；后端使用已验证Release产物。写操作无离线队列、无自动重试；版本412要重读，幂等冲突不能换key盲目重放。凭据、改密、登录与refresh不进入普通写收据或请求摘要。

升级前备份数据库与受保护key ring，记录应用/迁移版本。兼容回退只回退经验证的应用版本；不得用Down migration删除已有身份/审计/收据数据。key ring遗失时cookie不可恢复，应按事故流程撤销受影响会话并重新认证，而非解密失败后创建成功形状的会话。JWT签名key替换使旧access失效，challenge MAC替换使未完成challenge失效；滚动切换需要独立评审，当前不宣称多key无停机轮转。

验证顺序：live存活、dependency健康、具名账号登录、当前授权查询、只读页面、受控版本写、审计、退出后旧access/cookie拒绝。`/health/ready`仍包含未装配的应用门禁，不能为本轮演示移除而把系统标为可控制生产。

自动化证据与环境门禁分列：真实浏览器/HTTPS Kestrel/隔离MySQL是开发证据；Windows两个独立Host provider读取受保护key ring是进程装配恢复证据，不等于跨机器灾备演练。生产secret、语料、ACL/服务身份、DBA批准、现场H和PDA实机矩阵仍由各自owner关闭。
