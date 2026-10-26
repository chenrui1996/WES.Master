# T06 列偏好登出内存边界测试记录

- 状态：列偏好组合式逻辑在身份登出/恢复时的本地状态边界测试通过；不代表 DTC-UI-015 或 T06 整体通过。
- 关联：[实施记录](69-t06-column-preference-logout-boundary.md)。
- 依据：DTC-UI-015；登出清除当前身份上下文的易失状态，本机个人布局仅在身份匹配时可恢复。

## 验证场景

1. U1 登录后保存自定义列布局，确认布局只写入 U1 的本机作用域键。
2. 登出时 `sessionState.subject` 为空；当前组件不再显示 U1 的列布局。
3. 本机偏好保留且不含行数据/凭据。
4. U1 重新认证后恢复原布局。
5. 若切换至 U2，U1 的布局不得显示给 U2。

## 实测结果

| 范围 | 命令 | 实测结果 |
|---|---|---|
| 登出与相同主体恢复 | `npm test -- --run src/components/useColumns.spec.ts`（`clients\ops-web`） | 1 个文件、21 项通过，0 失败 |
| Ops Web 全套回归 | `npm test -- --run`（`clients\ops-web`） | 19 个文件、165 项通过，0 失败 |
| Vue/TypeScript 与生产包 | `npm run build`（`clients\ops-web`） | `vue-tsc --noEmit` 与 Vite 构建成功 |
| 变更空白检查 | `git diff --check` | 通过 |

## 可观察断言

- `forgetIdentity()` 后当前组件不再暴露上一主体的列布局。
- U1 的本机列偏好仍留在 U1 专属键中，不含会话ID。
- 同一主体重新认证后，布局从其原偏好恢复。

## 限制

身份与存储通过受控组件测试替身，不等同真实登录HTTP、浏览器导航或全页面数据清理验收。
