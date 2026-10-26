# T06 用户角色关联独立回填测试记录

- 状态：首片已验证；范围限定为 Platform Users 角色引用 owner 授权与当前关联回填。
- 关联：[实施记录](81-t06-user-role-reference-resolution.md)。
- 用例：DTC-UI-021/022/024；DTC-PLAT-014 第2步已有角色按ID回填。

## 验证场景

1. 真实 MySQL/HTTP：只解析输入 ID 内当前租户角色；`Id` 授权必需，`Code`/`Enabled` 按当前角色字段授权投影；不存在角色不返回。
2. 真实 MySQL/HTTP：无角色 `Id` 读取授权返回拒绝；空列表、空 GUID、超过100个原始 ID 拒绝，重复 ID 不重复返回。
3. Users 页面：已有角色不在候选首屏时独立回填代码/状态；只读显示不产生业务写，原关联保持。
4. 缺失、无权或字段不可见引用显示统一受限摘要，不回显原始 ID；停用角色显示状态且不能作为新候选，移除须显式操作。
5. 关闭/切换编辑对象时迟到响应不得污染新表单；超过100个引用按有界批次解析；接口失败明确显示局部错误。

## 实测结果

| 范围 | 命令 | 实测结果 |
|---|---|---|
| Platform Users 角色回填 | `npm test -- --run src/features/platform/UsersPage.spec.ts` | 1个测试文件、21项通过；含候选页外引用、显式移除、停用/无权状态、403局部错误、编辑切换迟到响应及101项分批 |
| 角色 owner 授权解析 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --filter FullyQualifiedName~RoleReferenceResolution` | 3项真实MySQL/HTTP测试通过；含授权字段投影、未授权字段省略、重复ID、缺失ID、无Id权限及批次边界 |
| Identity HTTP/MySQL连接门禁 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --filter FullyQualifiedName~IdentityHttpTests` | 73项通过，0失败，0跳过 |
| Ops Web全套回归/生产构建 | `npm test -- --run`；`npm run build`（`clients\ops-web`） | 20个测试文件、180项通过；`vue-tsc --noEmit`与Vite生产构建通过 |
| 格式与变更空白检查 | 修改的C#文件定向`dotnet format whitespace WES.slnx --no-restore --verify-no-changes --include ...`；`git diff --check` | 通过 |

## 限制

未运行隔离HTTPS Chromium；受控页面与HTTP/MySQL测试不覆盖DTC-PLAT-014全部步骤、其他关联选择器、DTC-UI-022整体或T06。
