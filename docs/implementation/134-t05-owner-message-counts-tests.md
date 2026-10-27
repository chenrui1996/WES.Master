# T05 owner 消息计数测试

- 状态：已验收，仅本片范围；T05与父DTC不关闭。
- 设计：[本片设计](133-t05-owner-message-counts.md)。

| 编号 | 步骤 | 预期 |
|---|---|---|
| MSG-COUNT-01 | 真实MySQL四状态、两个consumer同event、外tenant | 计数准确，Inbox按收据计，无跨tenant |
| MSG-COUNT-02 | 逐字段/Own/部门/会话撤销 | 明确拒绝，无部分结果 |
| MSG-COUNT-03 | 空owner、SQL错误/完成时取消、dirty scope | 真空为零，故障非零fallback，取消不交付 |
| MSG-COUNT-04 | HTTP匿名/原行权限/完整统计权限 | 401/403/200，固定响应、不含payload |
| MSG-COUNT-05 | 客户端非法结构/unsafe整数、撤权/迟到/离线/503 | 非法清旧，当前字段门禁、陈旧明确、不自动重读 |
| MSG-COUNT-06 | 真实浏览器读统计、重放提交 | 实际owner计数，提交后旧统计标陈旧，手工重读 |

## 执行结果

- RED：owner测试因缺少QueryMessageSummary编译失败；客户端测试因组件不存在失败。先实现owner后15项通过，后补提供者故障/提前取消/撤权总20项。
- 新增20项真实owner、8项HTTP、31项客户端。补故障拦截器时嵌套位置编译失败，改为与取消拦截器同级，未绕过类型/可见性检查。
- `dotnet build WES.slnx -c Release --no-restore`：0警告、0错误。
- 最终 `npm test`：28文件、**427 passed**；`npm run build` 的vue-tsc与Vite均通过。
- `dotnet test tests\Persistence\Wes.Persistence.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~MessageCounts|FullyQualifiedName~OutboxFilters|FullyQualifiedName~PlatformOutboxQuery_|FullyQualifiedName~PlatformOutboxReplay_"`：**38 passed、0 failed、0 skipped**。包含20新计数及18既有筛选/查询/重放；实际GROUP BY/LongCount、四状态与两个consumer同event、外tenant、逐字段、范围、角色/成员/会话撤销、空owner、两次SQL取消与提供者异常不转零。
- `dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~MessageCountsHttp|FullyQualifiedName~RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages|FullyQualifiedName~OutboxFilteredHttp|FullyQualifiedName~OutboxHttp|FullyQualifiedName~OutboxReplayHttp"`：**21 passed、0 failed、0 skipped**。8新HTTP逐字段/原行权限拒绝/新字段授权与撤权；保持13既有HTTP/真实浏览器。
- `dotnet test tests\Architecture\Wes.Architecture.Tests.csproj -c Release --no-build --no-restore`：**29 passed**。相关.NET共88通过，不冒充完整solution测试。
- 真实HTTPS Kestrel+wes_test+Chromium：fixture另行授予统计字段，不改默认部署权限；首读实际59 Pending/1 Quarantined/0 retained Inbox，响应200/no-store。原事件重放后统计明确陈旧、不自动查；手工再读隔离数为0。既有状态/时间筛选、重放唯一回执/原payload、响应式/键盘/200%文字与本机存储白名单仍通过。

会话证据位于 `files\message-counts-validation\{owner,http-browser,architecture,verified}`。本片无新迁移/依赖/生产连接，未重跑完整.NET/其他两套专属浏览器。计数不是Broker/consumer运行健康，真实Broker/跨owner仍未验收。
