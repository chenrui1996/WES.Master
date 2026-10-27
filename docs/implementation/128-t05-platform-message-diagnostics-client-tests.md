# T05 Platform 消息诊断客户端测试

- 状态：已验收，仅本片客户端及既有 owner/HTTP 相关回归；T05未关闭。
- 设计：[本片设计](127-t05-platform-message-diagnostics-client.md)。

| 编号 | 输入/步骤 | 预期 |
|---|---|---|
| MSG-UI-01 | 显式读取有效 Platform 元数据 | 首次零请求；只调用既有 GET；展示必要字段，不出现 payload/重放 |
| MSG-UI-02 | 字段/All 范围缺失、撤权、换主体 | 必要授权不足禁读；撤权同步清旧；迟到结果不恢复 |
| MSG-UI-03 | 非法 JSON/字段/UUID/UTC/状态/重复身份/过长页 | 明确无效并清旧，无成功形空页 |
| MSG-UI-04 | 同查询503、401/403/404 | 503保留陈旧；认证/授权/缺失清旧，安全关联标识可见 |
| MSG-UI-05 | 离线及恢复，读取中离线 | 禁读/分页，取消丢弃迟到结果，恢复不自动重读 |
| MSG-UI-06 | 满页、下一页、改大小 | 共享实时分页、重新请求、改变大小回第一页，不伪造total |
| MSG-UI-07 | 不可选字段返回非null、HTML文案 | 未授权字段不展示，正文只文本，不执行HTML |
| MSG-UI-08 | 直接路由、导航 | Platform模块与read权限共同门禁 |

## 执行结果

- RED：`npm test -- src\features\notifications\MessagesPage.spec.ts` 因组件尚未存在而失败，随后实现。
- 最终 `npm test`：26文件、**346 passed**，0失败；相对前片306增加40项客户端/路由/导航用例。`npm run build` 的 vue-tsc 和 Vite 均通过，无新增依赖。
- 首次类型检查发现 Playwright style handle 类型为 Node 而非 Element，改用已有 parentNode.removeChild 模式；未用类型断言绕过。
- 首轮真实 HTTPS 浏览器1失败、相关HTTP2通过：按钮被权限门禁禁用，定位为错误地将 scope=0 当 All。核对真实 `DataScope` 与既有审计页，改为 All=2，测试 Own=0/Departments=1 均禁读，未放宽权限。
- 修正后 `dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages|FullyQualifiedName~OutboxHttp|FullyQualifiedName~OutboxReplayHttp"`：**3 passed、0 failed、0 skipped**。fixture中只有Platform脚本适用，其余两个专属浏览器脚本按既有模式跳过，不计作本片通过。
- 浏览器使用真实自签HTTPS Kestrel、真实登录及wes_test读取，消息API未被stub：新页首次0请求；键盘Tab/Enter发出唯一GET、HTTP200/no-store、25条Platform行及真实eventID展示；375/768/1440宽度、桌面200%字体无整页溢出；离线按钮禁用/陈旧，恢复不自动重读。现有平台写入/审计/导出/登出流程仍通过，既有最终本机存储白名单断言仍适用。
- `dotnet test tests\Persistence\Wes.Persistence.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~PlatformOutboxQuery_RequiresCurrentAllScopeGrantAndProjectsSafeMetadata"`：**1 passed、0 failed、0 skipped**，真实MySQL owner字段投影回归。

证据位于会话目录 `files\message-diagnostics-validation\{browser,corrected,owner}`，首次失败保留。未重跑完整.NET/其他两套专属浏览器，无后端/模型/迁移变化。组件响应替身只证明客户端合同；真实角色撤权清旧的本片新增断言属于组件层，不冒充浏览器撤权或真实Broker证据。
