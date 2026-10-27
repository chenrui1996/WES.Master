# T05 受权重放客户端测试

- 状态：已验收，仅本片；T05与父DTC不关闭。
- 设计：[本片设计](129-t05-authorized-message-replay-client.md)。

| 编号 | 步骤/输入 | 预期 |
|---|---|---|
| MSG-REPLAY-01 | 当前read/write完整字段，隔离安全事件 | 准备前重读；理由/原event/expected/operation预览后才POST |
| MSG-REPLAY-02 | 缺字段/Own/错schema/非隔离/陈旧页 | 不显示可写按钮，不发POST |
| MSG-REPLAY-03 | 准备重读后事件移走/变状态/attempt变化 | 移走明确报错，变化以新值判断，不沿用旧expected |
| MSG-REPLAY-04 | 首次503或非法结果、显式重试 | 原body/原键精确不变；不自动重发，不可编辑 |
| MSG-REPLAY-05 | 409/403/主体变更/离线 | 拒绝或清旧，迟到结果丢弃，离线不排队 |
| MSG-REPLAY-06 | 成功回执及同operation重试 | 仅明确Replayed识别成功，绝不显示业务已完成 |
| MSG-REPLAY-07 | 非法理由/HTML输入 | 无效禁预览，预览为文本，不执行HTML |

组件HTTP替身与真实owner/HTTP/浏览器验证分别记录；不会把替身当真实Broker证据。

## 执行结果

- RED：`npm test -- src\features\notifications\MessageReplayPanel.spec.ts` 因组件未实现失败。
- 首轮组件集成测试64通过/2失败：Vitest `it.each` 将数组行展开为位置参数，修正为具名 `{ rows }` 数据，不放宽响应解析。随后全套375通过。
- 首轮真实浏览器1失败/相关HTTP2通过：脚本试图在离线恢复后直接点击陈旧行的禁用准备按钮，正确被客户端拦住。补显式断言禁用、手工重读再准备，不放宽新鲜度规则。
- 最终 `npm test`：27文件、**379 passed**、0失败；相对前片346增加33。`npm run build` 的vue-tsc与Vite均通过。
- `dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages|FullyQualifiedName~OutboxHttp|FullyQualifiedName~OutboxReplayHttp"`：**3 passed、0 failed、0 skipped**。本次测试源先以不带no-build命令编译；最终复验复用该编译输出。
- 真实HTTPS Kestrel+MySQL+Chromium：fixture另行授予专用replay权限并将安全测试事件隔离，不授予部署管理员默认权限；页面实际GET重读后展示event/expected/operation/理由，Tab/Enter确认发送实际POST，断言原operation header、expectedAttempts=1与精确reason，HTTP200后清旧行，显式重读显示待发布。375/768/1440宽度预览无整页溢出。最终由.NET另一owner查证：原event保留、payload不变、Pending/attempts=0、唯一重放回执与理由一致。既有平台写入/审计导出/登出回归仍通过。
- `dotnet test tests\Persistence\Wes.Persistence.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~PlatformOutboxReplay_"`：**2 passed、0 failed、0 skipped**，真实当前授权/事件身份与同operation竞争回归。

证据保存在会话目录 `files\message-replay-client-validation\{first,verified,owner}`，保留首轮失败。不将组件网络失败注入当真实Broker断连或ACK丢失；不声称完整.NET/其他两套专属浏览器重跑，Host/模型/迁移/依赖无变化。
