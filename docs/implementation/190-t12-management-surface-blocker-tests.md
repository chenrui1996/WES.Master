# T12a 定义/表单 owner-local 持久化测试记录

- 状态：T12a持久化子片已验证；其余T12管理面仍 gated；2026-11-01。
- 对应实施记录：[189](189-t12-management-surface-blocker.md)。
- 既有纯验证内核另见[188](188-t12-definition-validation-core-tests.md)，数字不合并。

## 1. RED/GREEN

| 门禁 | 结果 | 原因/边界 |
|---|---|---|
| 初始测试 RED | 编译失败 | 新增的6个真实MySQL验收测试引用尚不存在的store/DbContext；证明新测试入口缺失。 |
| 初始实现 GREEN候选 | 6项中3通过、3失败 | MySQL发现same-transaction audit匹配错误及draft删除约束错误，已修复。 |
| 扩展测试首轮 | 9项中5通过、4失败 | 增加并发创建/直接不可变性/definition S2历史测试后，发现MySQL竞争死锁路径及两个测试断言/版本fixture错误；加入竞争回读冲突收敛并修正fixture。 |
| 最终真实MySQL测试 | **9通过、0失败、0跳过** | 最终命令见下节。 |
| HTTP/UI/授权集成 | NOT RUN | 本slice不创建端点/权限/文件适配器；仍受189所列gates约束。 |

## 2. 最终验证命令与结果

执行环境使用既有`MySqlTestConnection`授权fixture，仅连接并迁移隔离库`wes_test`，不输出或读取凭据：

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --filter "FullyQualifiedName~WorkflowFormPersistenceTests" --logger "console;verbosity=quiet"
```

- 最终：**9 passed, 0 failed, 0 skipped**；真实MySQL执行迁移与tenant隔离表操作。
- 覆盖：schema S1/S2不可变历史、definition固定schema版本/S1历史回放、expectedVersion冲突无写入、同operation同body replay与body mismatch冲突、tenant隔离、definition publish重放、并发发布唯一胜者、并发同operation创建只落一份receipt/audit、published/audit拒绝修改，以及SaveChanges后事务中断时draft/receipt/audit原子回滚。
- 既有fixture调用`Database.MigrateAsync()`后执行以上用例，迁移成功应用于真实MySQL `wes_test`。

```powershell
dotnet ef migrations has-pending-model-changes --project src\Modules\WorkflowForm\Wes.WorkflowForm.csproj --startup-project src\Modules\WorkflowForm\Wes.WorkflowForm.csproj --configuration Release
```

- 输出：`No changes have been made to the model since the last migration.`

```powershell
dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-build --filter "FullyQualifiedName~DefinitionValidationTests" --logger "console;verbosity=quiet"
```

- 输出：**113 passed, 0 failed, 0 skipped**（既有纯验证内核回归）。

```powershell
dotnet test tests\Architecture\Wes.Architecture.Tests.csproj --configuration Release --no-build --logger "console;verbosity=quiet"
```

- 输出：**29 passed, 0 failed, 0 skipped**。

```powershell
dotnet format WES.slnx whitespace --verify-no-changes --no-restore --include src\Modules\WorkflowForm\Features\Definitions\WorkflowDefinitionStore.cs src\Modules\WorkflowForm\Infrastructure\Persistence\WorkflowFormDbContext.cs src\Modules\WorkflowForm\Infrastructure\Persistence\WorkflowFormEntities.cs src\Modules\WorkflowForm\Infrastructure\Persistence\WorkflowFormDesignTimeFactory.cs src\Modules\WorkflowForm\Infrastructure\Persistence\Migrations\20261101082426_WorkflowFormDefinitions.cs src\Modules\WorkflowForm\Infrastructure\Persistence\Migrations\20261101082426_WorkflowFormDefinitions.Designer.cs src\Modules\WorkflowForm\Infrastructure\Persistence\Migrations\WorkflowFormDbContextModelSnapshot.cs tests\Persistence\WorkflowFormPersistenceTests.cs
dotnet build WES.slnx --configuration Release --no-restore
git diff --check
```

- 格式验证通过；full solution Release **0 warnings, 0 errors**；`git diff --check`通过。
- 初次format verify发现WHITESPACE诊断；已用`dotnet format WES.slnx whitespace --no-restore --include ...`修复后复验通过。

## 3. 尚未运行/未覆盖

未实现/未验证本T12 slice以外的权限撤权、候选资格/final validation owner adapter、跨租户Platform文件引用授权、HTTP严格body与状态映射、部署默认开关/DI、两Vue页面及浏览器交互。也未执行任何T13审批/实例/hook测试。本记录只验证T12a持久化子片，不关闭DTC-WF-001/008或父T12。
