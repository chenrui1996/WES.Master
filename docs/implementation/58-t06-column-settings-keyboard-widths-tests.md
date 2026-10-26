# T06 列设置键盘排序与宽度边界测试记录

- 状态：ColumnSettings 定向测试通过；DTC-UI-014整体与T06未关闭。
- 关联：[实施记录](57-t06-column-settings-keyboard-widths.md)。

| 范围 | 命令 | 实测结果 |
|---|---|---|
| 共享列设置、可访问键盘排序和宽度范围 | `npm test -- --run src/components/ColumnSettings.spec.ts`（`clients\ops-web`） | 1 个文件、6 项通过，0 失败 |

## 可观察断言

- `ArrowUp` 与 `ArrowDown` 改变设置列表草稿顺序；未应用前真实列投影不变；提交应用后反映新顺序。
- Identity 字段宽度159px和601px拒绝并保持200px默认值；160px和600px边界合法且持久化值与页面实际宽度一致。
- 测试由失败边界值与合法边界值共同断言，未以事件触发/方法调用代替最终 UI 与存储结果。
- 未运行其它页面宽屏/长值/真实Chromium专用列布置验收，不外推整页结论。
