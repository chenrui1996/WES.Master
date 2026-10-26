# T06 恢复默认列的作用域隔离测试记录

- 状态：`ColumnSettings` 恢复默认列与筛选/业务值隔离组件测试通过；不代表 DTC-UI-014 或 T06 整体通过。
- 关联：[实施记录](67-t06-column-reset-isolation.md)。
- 依据：DTC-UI-014；恢复默认列不得清除筛选或业务值。

## 验证场景

设置非默认列布局、筛选值 `WA` 与只读业务行 `R-1`。触发“恢复默认列”后，在草稿阶段及显式应用后分别验证：

1. 草稿确认前当前已应用布局不变。
2. 显式应用后列布局回归默认选择与宽度。
3. 筛选值仍为 `WA`，业务行标识及数量仍为原值。

## 实测结果

| 范围 | 命令 | 实测结果 |
|---|---|---|
| 恢复默认列与筛选/业务值隔离 | `npm test -- --run src/components/ColumnSettings.spec.ts`（`clients\ops-web`） | 1 个文件、8 项通过，0 失败 |
| Ops Web 全套回归 | `npm test -- --run`（`clients\ops-web`） | 19 个文件、164 项通过，0 失败 |
| Vue/TypeScript 与生产包 | `npm run build`（`clients\ops-web`） | `vue-tsc --noEmit` 与 Vite 构建成功 |
| 变更空白检查 | `git diff --check` | 通过 |

## 限制

测试使用 Vue Test Utils 受控页面，不声称通过真实浏览器或全部页面响应式验收。恢复默认只涉及当前列面板，不执行服务端请求或业务写。
