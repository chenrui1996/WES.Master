# T06 审计页慢查询加载与刷新状态测试记录

- 状态：本首片定向、全前端和隔离HTTPS浏览器验证通过；DTC-UI-001整体及T06仍未关闭。
- 关联：[实施记录](53-t06-audit-slow-query-state.md)。

| 范围 | 命令 | 实测结果 |
|---|---|---|
| QueryState 与 Audit 页面定向 Vitest | `npm test -- --run src/components/QueryState.spec.ts src/features/platform/AuditPage.spec.ts`（`clients\ops-web`） | 2 个文件、23 项通过，0 失败 |
| Ops Web 全套 Vitest | `npm test -- --run`（`clients\ops-web`） | 19 个文件、158 项通过，0 失败 |
| Vue/TypeScript 与生产包 | `npm run build`（`clients\ops-web`） | `vue-tsc --noEmit` 与 Vite 构建成功 |
| 隔离 HTTPS Kestrel + Chromium Platform 页面回归 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-build -c Release --filter "FullyQualifiedName~RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages"` | 1 项通过，0 失败，0 跳过 |
| 变更空白检查 | `git diff --check` | 通过 |

## 可观察断言

- Controlled-time 测试在 200ms 时断言无占位内容，到 300ms 后断言具名 loading slot 出现；请求达到 2 秒时审计占位表仍可见，并具有 4 个授权字段占位列、5 行、20 个占位单元格。响应完成后占位内容移除。
- 单独验证 300ms 前请求已完成时，不再出现迟到的占位内容。
- 受控审计刷新期间，已加载业务行仍可见、loading 状态仍显示上次成功读取 UTC 时间；刷新失败后继续保留同一证据和该时间。
- 新增逻辑不修改 API、服务端权限或持久层；这不是其他页面或真实网络延迟下的全 PAGE 验收。

## 红绿证据

- 新延迟占位测试首次执行时因 QueryState 尚无 loading slot 而失败；实现后定向 23 项通过。
- 新增刷新时间断言首次执行时失败，观察到刷新错误文案存在但没有成功读取时间；补充成功读取时间保存与同scope stale 展示后通过。
