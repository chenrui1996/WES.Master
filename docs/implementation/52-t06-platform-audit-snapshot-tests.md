# T06 Platform 审计固定成员快照测试记录

- 状态：本切片 Release 构建、定向 MySQL/HTTPS/Chromium 与前端验证通过；T06 整包仍未验收。
- 关联：[实施记录](51-t06-platform-audit-snapshot.md)。

| 范围 | 命令 | 实测结果 |
|---|---|---|
| 全 solution Release 编译 | `dotnet build WES.slnx --no-restore -c Release -m:1` | 成功，0 warning、0 error |
| 全 solution Release 测试回归 | `dotnet test WES.slnx --no-build -c Release -m:1 --verbosity minimal` | Architecture 29、Modules 224、Persistence 354、Route 228；共 835 项通过，0 失败，0 跳过 |
| 真实 MySQL 固定成员和撤权续页持久层用例 | `dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-build -c Release --filter "FullyQualifiedName~AuditSnapshot_"` | 2 项通过，0 失败，0 跳过 |
| 真实 HTTPS API：60 条固定成员、并发插入、599/600 秒边界 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-build -c Release --filter "FullyQualifiedName~AuditSnapshot_KeepsOriginalRowsAndExpiresAtThe600SecondBoundary"` | 1 项通过，0 失败，0 跳过 |
| 隔离 HTTPS Kestrel + Chromium：60 条数据跨三页、具名 Platform 页面及响应式框架 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-build -c Release --filter "FullyQualifiedName~RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages"` | 1 项通过，0 失败，0 跳过 |
| Audit 页面和共享分页器 Vitest | `npm test -- --run src/features/platform/AuditPage.spec.ts src/components/RealtimePager.spec.ts`（`clients\ops-web`） | 2 个文件、19 项通过，0 失败 |
| Vue/TypeScript 与生产包 | `npm run build`（`clients\ops-web`） | `vue-tsc --noEmit` 与 Vite 构建成功 |
| 变更空白检查 | `git diff --check` | 通过 |

## 覆盖与限制

- MySQL 验证固定成员在插入更早时间的新审计事实后仍不改变；真实 HTTPS 验证 60 条唯一 ID 按 25/25/10 续页、599 秒可读和恰好 600 秒返回 Gone；撤销访问角色后续页被拒绝。
- Chromium 在隔离 HTTPS 测试 host 上实际创建固定快照并遍历 60 条唯一成员，同时继续执行具名 Platform 页面及视口检查。
- 前端单测覆盖创建/续页、快照过期清理、筛选变化失效及实时/固定快照状态提示。
- 全 solution Release 回归通过，但这不代表所有计划中的 DTC 已实现或验收；未覆盖其他数据库 Provider、其他 owner 的快照、可变数据值冻结及生产/现场验收。
