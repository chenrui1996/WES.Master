# T06 角色规则部门引用独立回填测试记录

- 状态：首片已验证；范围限定为 Platform Roles 的部门引用批量解析、逐字段授权及编辑器回填。
- 关联：[实施记录](79-t06-role-department-reference-resolution.md)。
- 用例：DTC-UI-022 已选项独立回填；DTC-PLAT-014 编辑已有规则的部门 ID。

## 验证场景

1. 真实 MySQL/HTTP：多个请求 ID 只返回当前租户、部门 `Id` 范围内的 owner 投影；范围外、不存在及越权引用不回显名称或区分原因。
2. 真实 MySQL/HTTP：`Name`、`Code`、`Enabled` 分别遵循逐资源字段权限；无部门 `Id` 权限拒绝；空ID、空列表和超过100个ID拒绝，重复ID安全去重。
3. Platform Roles：打开带有候选第一页之外部门 ID 的已有角色，调用独立批量解析并显示授权名称/代码，而不是原始 ID。
4. 未解析ID显示统一受限摘要；停用部门显示停用状态，不会变成新增候选；读取失败保持明确局部错误且不覆盖规则或版本。
5. 身份变化/关闭编辑时迟到回包不能覆盖新编辑对象；多于100个不同ID时按有界批次解析。

## 实测结果

| 范围 | 命令 | 实测结果 |
|---|---|---|
| Platform Roles回填与隔离 | `RolesPage.spec.ts` 定向测试 | 16项通过，覆盖已有规则独立回填、无权/未解析摘要、停用状态、编辑对象切换迟到响应、403及101项引用的100+1分批 |
| 部门Owner解析授权 | `DepartmentReferenceResolution` 真实MySQL/HTTP测试 | 2项通过，覆盖逐字段投影、授权范围、拒绝无Id权限及输入边界 |
| Identity HTTP/MySQL连接门禁 | Identity HTTP与MySQL连接守卫测试 | 70项通过；包含引用路由以及现有部门分页回归 |
| Ops Web全套回归/生产构建 | Ops Web全量测试及生产构建 | 20个测试文件、176项通过；`npm run build`（含`vue-tsc --noEmit`）通过 |
| 格式与变更空白检查 | 修改的C#文件定向`dotnet format whitespace`及`git diff --check` | 通过 |

## 限制

未运行隔离HTTPS Chromium；受控页面和HTTP/MySQL测试不覆盖其他模块引用选择器、DTC-UI-022整体或T06。一次对`Wes.Modules.Tests.csproj`使用`DirectoryPersistenceTests`筛选未匹配测试，实际目录投影回归由Identity HTTP类70项测试覆盖。
