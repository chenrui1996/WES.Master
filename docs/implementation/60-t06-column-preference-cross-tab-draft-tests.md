# T06 列偏好跨标签草稿保护测试记录

- 状态：useColumns/ColumnSettings 受控 storage event 回归通过；DTC-UI-016整体与T06未关闭。
- 关联：[实施记录](59-t06-column-preference-cross-tab-draft.md)。

| 范围 | 命令 | 实测结果 |
|---|---|---|
| 跨标签首选项事件与当前未提交列设置草稿 | `npm test -- --run src/components/useColumns.spec.ts`（`clients\ops-web`） | 1 个文件、17 项通过，0 失败 |
| Ops Web 全套回归 | `npm test -- --run`（`clients\ops-web`） | 19 个文件、160 项通过，0 失败 |
| Vue/TypeScript 与生产包 | `npm run build`（`clients\ops-web`） | `vue-tsc --noEmit` 与 Vite 构建成功 |
| 变更空白检查 | `git diff --check` | 通过 |

## 可观察断言

- 当前身份范围发生外部 storage event 后，页面显式提示跨标签更新。
- 外部首选项不能改变页面已应用的列配置，也不能覆盖组件内尚未应用的草稿。
- 只有当前操作者点击应用后，草稿才成为页面列配置。
- 这是 Vue 组件与受控 StorageEvent 测试，不声称跨浏览器真实多标签系统测试已通过。
