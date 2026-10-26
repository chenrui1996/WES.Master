# T06 角色规则部门候选代码查询测试记录

- 状态：本切片自动化验证通过；仅限 Platform Roles指定部门候选的字段授权、25条分页、输入竞态与草稿选值保持，不代表完整 DTC-UI-022 或 T06 完成。
- 关联：[实施记录](77-t06-role-department-code-candidates.md)。
- 用例：DTC-UI-022 代码查询、A/AB乱序、旧候选不可覆盖。

## 验证场景

1. 通过真实MySQL/HTTP验证精确部门代码过滤在分页前执行；未获Code字段权限时拒绝查询。
2. 页面加载多个候选页并显式选择一个部门，然后输入A再输入AB。
3. 验证输入在250ms后查询，offset重置为0，limit保持25，候选显示只匹配当前代码。
4. 让AB先返回、A后返回；确认只呈现AB结果，当前已选部门仍可见且未被静默移除。
5. 代码无匹配时显示候选空态而非接口失败；服务端授权拒绝与传输失败保持明确错误。

## 实测结果

| 范围 | 命令 | 实测结果 |
|---|---|---|
| Platform Roles候选搜索/竞态 | `Set-Location clients\ops-web; npm test -- --run src/features/platform/RolesPage.spec.ts src/features/platform/useDirectoryCandidates.spec.ts` | 16项通过：250ms去抖、Code权限隐藏、精确查询参数、A/AB逆序回包、旧结果隔离、25条offset连续、已选部门摘要及加入规则均通过。 |
| 部门代码字段范围与分页 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore --filter "FullyQualifiedName~DepartmentCandidateCodeLookup"` | 真实MySQL/HTTP 3项通过：精确Code过滤、分页前过滤、无Code字段授权拒绝、Id/Code资源范围交集。 |
| Platform HTTP回归 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore --filter "FullyQualifiedName~IdentityHttpTests|FullyQualifiedName~MySqlConnectionGuardTests"` | Identity HTTP与MySQL连接守卫合计68项通过。 |
| Ops Web全套回归/生产构建 | `Set-Location clients\ops-web; npm test; npm run build` | 20个测试文件、172项通过；`vue-tsc --noEmit`与Vite生产构建通过。 |
| C#格式与变更空白检查 | `dotnet format whitespace WES.slnx --verify-no-changes --no-restore --include src\Host\Platform\PlatformEndpoints.cs src\Modules\Platform\Features\Directory\DirectoryQueries.cs tests\Modules\Platform\IdentityHttpTests.cs`; `git diff --check` | 修改的C#文件格式与diff空白检查通过。全solution格式检查另发现两个未修改的历史Migration `CHARSET`诊断，本切片未触碰这些文件。 |

## 限制

本切片尚未运行隔离HTTPS Chromium对该新增选择器的验收，也未运行全solution回归；受控页面与真实MySQL/HTTP证据不覆盖其他模块选择器，不代表完整DTC-UI-022或T06已完成。
