# T06 Platform角色与用户候选分页测试记录

- 状态：Roles/Users候选分页定向测试已通过；全量回归、构建和变更检查待执行，不代表完整 DTC-UI-022。
- 关联：[实施记录](71-t06-department-candidate-page-size.md)。
- 用例：DTC-UI-022 首片；部门候选查询页长与增量追加。

## 验证场景

授权部门/角色候选目录含超过25条数据：

1. 分别打开Roles指定部门授权规则和Users部门/角色候选流程。
2. 确认每个候选首个请求使用 `offset=0&limit=25`，并提供后续读取入口。
3. 加载更多后确认请求使用 `offset=25&limit=25`，后页候选追加，第一页候选仍可见。
4. 确认末页不足25条时不再显示“加载更多”。

## 实测结果

| 范围 | 命令 | 实测结果 |
|---|---|---|
| Roles与Users候选分页 | `npm test -- --run src/features/platform/RolesPage.spec.ts src/features/platform/UsersPage.spec.ts`（`clients\ops-web`） | 2个测试文件、26项通过 |
| Ops Web 全套回归 | `npm test -- --run`（`clients\ops-web`） | 19个测试文件、165项通过 |
| Vue/TypeScript 与生产包 | `npm run build`（`clients\ops-web`） | `vue-tsc --noEmit`与Vite生产构建通过 |
| 变更空白检查 | `git diff --check` | 通过 |

## 限制

请求由隔离的前端测试响应模拟，不代替后端授权/租户隔离验证或真实浏览器候选搜索体验。
