# T06 未解析角色候选拒绝测试记录

- 状态：已通过；真实HTTP/MySQL验证拒绝不存在角色ID且无用户版本、成员关联或审计变化；worktree配置路径解析和完整Identity HTTP类回归通过。
- 关联：[实施记录](75-t06-reject-unresolved-role-candidate.md)。
- 用例：DTC-UI-022 伪造候选ID提交不得产生业务写入。

## 验证场景

1. 通过真实HTTP登录，再授予测试主体用户/角色目录管理权限。
2. 读取目标用户版本及既有角色关联和审计数量。
3. 提交包含一个随机不存在角色ID的完整角色替换命令。
4. 断言HTTP拒绝；复读并断言用户版本、成员关联及业务审计数量不变。
5. 使用临时目录测试配置路径解析：普通仓库根与worktree容器布局必须只解析其规范项目根级配置位置。

## 实测结果

| 范围 | 命令 | 实测结果 |
|---|---|---|
| 角色引用HTTP拒绝 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore --filter FullyQualifiedName~MembershipCommandRejectsUnknownRoleIdWithoutBusinessWrites` | 1项通过；真实 MySQL/HTTP拒绝，且用户版本、角色关联、审计未变 |
| Platform Identity HTTP 回归 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore --filter FullyQualifiedName~Wes.Modules.Tests.Platform.IdentityHttpTests` | 65项通过 |
| 测试配置路径解析与目标保护 | `dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore --filter FullyQualifiedName~MySqlConnectionGuardTests` | 14项通过，包括普通仓库/worktree解析以及仅指向`wes_test`的保护 |
| C# 格式验证 | `dotnet format whitespace WES.slnx --verify-no-changes --no-restore --include tests\Modules\Platform\IdentityHttpTests.cs tests\Persistence\MySqlTestConnection.cs tests\Persistence\MySqlConnectionGuardTests.cs` | 通过 |
| 变更空白检查 | `git diff --check` | 通过 |

## 限制

测试验证仅覆盖不存在角色ID；不替代前端控件测试、跨租户/撤权场景或真实浏览器候选流。
