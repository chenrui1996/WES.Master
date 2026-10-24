# T02 部署功能开关首片测试记录

- 状态：所列切片测试通过；完整DTC-SYS-008未完成
- 关联：[功能开关设计](../detailed-design/feature-switches.md)、[实施记录](26-deployment-feature-switches.md)

## 自动化结果

| 范围 | 命令 | 结果 |
|---|---|---|
| 缺项、未知键、无效布尔值不回显、未注册运行能力拒绝、不可变快照、匿名catalog仅含白名单模块且no-store | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore --filter "CaseId=DTC-SYS-008"` | 6 passed，0 failed |
| 模块UI开关全部为false时，真实身份HTTP API仍能登录、撤销并验证当前授权 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore --filter "FullyQualifiedName~IdentityHttpTests.LoginCreatesDurableFamilyAndProtectedCookieThenLogoutRevokesAccess"` | 1 passed，0 failed；使用授权隔离wes_test与合成fixture |
| 完整串行Release回归 | `dotnet test WES.slnx --configuration Release --no-restore -m:1` | Architecture 29、Modules 217、Persistence 316、Route 228；共790 passed、0 failed、0 skipped |

首轮全套回归发现旧 `RuntimeBoundaryTests` 直接装配Host运行时但没有提供现在必需的 `Features` 配置，相关16个用例在启动时失败。测试fixture补齐全部显式关闭的开关后，17项功能开关/HTTP边界定向测试通过，并重跑全套回归确认。

## 未覆盖

尚未验证已注册可选能力的关闭/启动/依赖故障与恢复、独立诊断角色、客户端菜单和路由接入、T30恢复演练、T32页面组合、全部Provider或现场H。上述测试不得计作DTC-SYS-008全量通过。
