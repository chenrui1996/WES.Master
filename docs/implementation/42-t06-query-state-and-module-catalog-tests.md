# T06共享查询状态与模块catalog首片测试记录

- 状态：本文列出的前端切片验证通过；T06整体未验收
- 关联：[实施记录](41-t06-query-state-and-module-catalog.md)、[T06工作包](../superpowers/plans/2026-10-23-wes-development-work-packages.md)

## 自动化结果

工作目录：`clients\ops-web`

| 范围 | 命令 | 结果 |
|---|---|---|
| Catalog allowlist、无效结构/未知/重复模块、失败关闭与重试、并发读取；菜单过滤；直接导航、catalog失败、登录/会话路由；QueryState与Audit stale结果 | `npm test -- --run src/features/module-catalog.spec.ts src/features/navigation.spec.ts src/router.spec.ts src/components/QueryState.spec.ts src/features/platform/AuditPage.spec.ts` | 5个测试文件、17项通过，0失败 |
| 列偏好环境/主体/租户/page/table隔离、列ID与128–800px宽度持久化、固定首列偏好重载恢复、跨标签变更提示、越界宽度拒绝、必显列、损坏存储提示、授权schema撤权规范化；ColumnSettings应用/取消/恢复默认列与键盘顺序 | `npm test -- --run src/components/useColumns.spec.ts src/components/ColumnSettings.spec.ts` | 已验证：2个测试文件、11项通过，0失败 |
| 用户目录相同scope刷新失败保留结果且禁用旧版本行操作；筛选变化立即清除旧结果；既有用户目录用例 | `npm test -- --run src/features/platform/UsersPage.spec.ts` | 1个测试文件、16项通过，0失败 |
| 角色及租户目录相同scope刷新失败保留结果、危险行操作失效；既有用例 | `npm test -- --run src/features/platform/RolesPage.spec.ts src/features/platform/TenantsPage.spec.ts` | 2个测试文件、16项通过，0失败 |
| 强类型配置目录同scope失败保留stale、筛选变化清空旧行、403丢弃受限数据；字典目录同scope刷新失败保留stale；现有设置用例 | `npm test -- --run src/features/platform/SettingsPage.spec.ts` | 1个测试文件、7项通过，0失败 |
| 会话同范围刷新失败保留stale且禁用撤销；过滤变化/身份变化和管理权限拒绝不保留旧结果 | `npm test -- --run src/features/identity/SessionPage.spec.ts` | 1个测试文件、19项通过，0失败 |
| Ops Web完整Vitest套件 | `npm test -- --run` | 已验证：17个测试文件、108项通过，0失败 |
| Vue/TypeScript类型检查与生产打包 | `npm run build` | `vue-tsc --noEmit`通过；Vite生产构建成功 |
| Vue/TypeScript类型检查与生产打包 | `npm run build` | `vue-tsc --noEmit`通过；Vite生产构建成功 |

## 未覆盖

未测试T06共享查询授权快照API/owner合同、列宽/固定/视图/跨标签密集交互与固定快照过期阈值；也未运行浏览器E2E/真实部署配置矩阵。UI catalog不授予服务端权限。
