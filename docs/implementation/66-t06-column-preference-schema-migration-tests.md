# T06 列偏好授权 schema 变化迁移测试记录

- 状态：列 schema 变化下的本机偏好规范化测试通过，不代表 DTC-UI-016 或 T06 整体通过。
- 关联：[实施记录](65-t06-column-preference-schema-migration.md)。
- 依据：DTC-UI-016；旧列撤权、新列加入、标签更名及稳定 ID。

## 验证场景

存入旧 schema 的版本化列偏好：授权列 `id/name/secret`，选择和宽度包含 `secret`，`name` 保持稳定 ID。按新 schema 加载：`secret` 已撤权，`name` 标签已更名，新增 `email` 为必显列。验证：

1. 当前选择不包含已撤权的 `secret`，必显 `email` 被纳入。
2. 稳定 ID `name` 保留用户选择和合法宽度，即使展示标签改变。
3. 规范化写回只包含当前 schema 字段及对应列宽，不保留撤权字段。

## 实测结果

| 范围 | 命令 | 实测结果 |
|---|---|---|
| 旧偏好载入新 schema | `npm test -- --run src/components/useColumns.spec.ts`（`clients\ops-web`） | 1 个文件、20 项通过，0 失败 |
| Ops Web 全套回归 | `npm test -- --run`（`clients\ops-web`） | 19 个文件、163 项通过，0 失败 |
| Vue/TypeScript 与生产包 | `npm run build`（`clients\ops-web`） | `vue-tsc --noEmit` 与 Vite 构建成功 |
| 变更空白检查 | `git diff --check` | 通过 |

## 限制

偏好由内存存储替身加载；测试不等同于不同应用版本的真实升级部署。schema 提供者仍须来自当前服务端投影与授权，不允许由本机偏好扩大字段权限。

## 可观察断言

- 旧 schema 中已撤权 `secret` 列和其宽度不再进入当前布局或规范化后的持久偏好。
- 新增列 `email` 按默认规则进入选择；稳定 ID `name` 的显隐顺序及合法宽度在标签更名后保留。
- schema 基线变更后偏好立即规范化写回，下一次加载不会再次读取旧授权字段。
