# T06 列偏好存储不可用与配额失败测试记录

- 状态：SecurityError与QuotaExceededError的受控存储故障测试通过；不代表 DTC-UI-016 或 T06 整体通过。
- 关联：[实施记录](63-t06-column-preference-storage-failures.md)。
- 依据：DTC-UI-016；存储失败应显式提示，不阻止本次会话内使用，也不得假报成功。

## 验证场景

1. 浏览器存储读取与写入均因 `SecurityError` 被禁用：默认授权列仍可显示和修改，明确提示本次设置不会持久保存。
2. 已有有效布局可读取，但保存因 `QuotaExceededError` 失败：新布局在本页仍生效，状态提示保存失败，原有持久值保持不变。

## 实测结果

| 范围 | 命令 | 实测结果 |
|---|---|---|
| Storage SecurityError 与配额异常 | `npm test -- --run src/components/useColumns.spec.ts`（`clients\ops-web`） | 1 个文件、19 项通过，0 失败 |
| Ops Web 全套回归 | `npm test -- --run`（`clients\ops-web`） | 19 个文件、162 项通过，0 失败 |
| Vue/TypeScript 与生产包 | `npm run build`（`clients\ops-web`） | `vue-tsc --noEmit` 与 Vite 构建成功 |
| 变更空白检查 | `git diff --check` | 通过 |

## 限制

异常由内存存储替身注入；测试不声称覆盖真实浏览器隐私策略、磁盘损坏或任意非 `DOMException` 故障。若代码遇到未识别异常应继续显式抛出，不吞掉失败。

## 可观察断言

- 存储禁用时显示默认授权列，当前会话内修改仍生效，并提示偏好无法持久保存。
- 配额耗尽时新布局仍对当前页面生效，提示偏好未保存，先前有效偏好保持不变。
