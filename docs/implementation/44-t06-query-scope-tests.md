# T06 QueryScope 契约测试记录

- 状态：契约单元测试通过；不代表任何查询 owner 已支持固定快照。
- 关联：[实施记录](43-t06-query-scope-contract.md)、[T06 工作包](../superpowers/plans/2026-10-23-wes-development-work-packages.md)。

## 自动化结果

| 范围 | 命令 | 结果 |
|---|---|---|
| 身份/授权/过滤/排序任一变化均改变 scope；大小写规范化保持等值；空身份及非法 fingerprint 拒绝 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~QueryScopeTests --verbosity minimal` | 3 项通过，0 失败，0 跳过 |
| Wes.Modules 全量回归 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore -c Release --verbosity minimal` | 223 项通过，0 失败，0 跳过 |
| 架构边界回归 | `dotnet test tests\Architecture\Wes.Architecture.Tests.csproj --no-restore -c Release --verbosity minimal` | 29 项通过，0 失败，0 跳过 |

本测试只验证值对象边界和等值语义。未验证 owner 是否从当前授权生成指纹、过滤/排序的 canonical encoding、持久化数据水位、token 重放、权限撤销或 600 秒 TTL。
