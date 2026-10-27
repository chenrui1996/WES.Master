# T05 消息筛选测试

- 状态：已验收，仅本片；T05与父DTC不关闭。
- 设计：[本片设计](131-t05-filtered-message-diagnostics.md)。

| 编号 | 步骤 | 预期 |
|---|---|---|
| MSG-FILTER-01 | 真实MySQL混合状态/时间，page limit=1 | 过滤后分页，from含/before不含，无筛选兼容 |
| MSG-FILTER-02 | 非法状态/UTC/日期范围，缺当前字段 | 明确拒绝，无成功空页或隐藏字段oracle |
| MSG-FILTER-03 | 另一tenant混合数据、撤权 | 不返回foreign事件，当前权限门禁不变 |
| MSG-FILTER-04 | SQL reader关闭时取消 | 抛取消，不交付迟到投影 |
| MSG-FILTER-05 | HTTP状态/时间组合与非法值 | 200正确投影、400非法、403失权 |
| MSG-FILTER-06 | 客户端换筛选/分页/准备预读 | 清旧取消、回第一页、预读保持当前筛选；响应不符清旧 |

真实数据库、HTTP、组件与浏览器结果在执行后分别记录，不相互冒充。

## 执行证据

- RED：真实owner测试首先编译失败，缺少 `OutboxQueryFilter`；客户端49通过/2失败，缺筛选控件。随后实现，不忽略非法输入。
- 新增15项真实MySQL owner、10项HTTP、17项客户端用例。
- `dotnet build WES.slnx -c Release --no-restore`：**0 warnings、0 errors**。
- `npm test`：27文件、**396 passed**；`npm run build` 的vue-tsc与Vite均通过。
- `dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~OutboxFilteredHttp|FullyQualifiedName~OutboxHttp|FullyQualifiedName~OutboxReplayHttp|FullyQualifiedName~RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages"`：**13 passed、0 failed、0 skipped**。包含10新HTTP与既有查询/重放/真实HTTPS浏览器3；最终客户端精度处理修正后再次执行13通过。
- `dotnet test tests\Persistence\Wes.Persistence.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~OutboxFilters|FullyQualifiedName~PlatformOutboxQuery_|FullyQualifiedName~PlatformOutboxReplay_"`：**18 passed、0 failed、0 skipped**。实际SQL筛选后limit/offset、半开时间、全部4种有限状态、外tenant、必要字段拒绝及reader closing时取消通过，既有查询/重放3回归通过。
- `dotnet test tests\Architecture\Wes.Architecture.Tests.csproj -c Release --no-build --no-restore`：**29 passed、0 failed、0 skipped**。相关.NET合计60，不等于完整solution测试。
- 真实Chromium在隔离HTTPS Kestrel/wes_test：改筛选同步清旧，status=3与明确UTC范围发出真实GET只取1条隔离事件；准备前再次GET保留同筛选；真实POST后手工清status筛选再GET显示Pending。保留离线不自动查询、三宽度/200%字体/键盘及原事件唯一回执校验。
- 客户端额外验证100ns级文本边界，不用Date毫秒精度舍入请求或误判相邻响应；该组件合同不代表MySQL存储精度认证。

会话证据目录 `files\message-filter-validation\{owner,http-browser,verified,final}`。无新迁移/依赖/生产连接，没有重跑完整.NET/其他两套专属浏览器；真实Broker、跨owner与Inbox统计仍未完成。
