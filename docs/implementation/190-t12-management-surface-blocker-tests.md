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

## 4. 发布内核衔接续片（2026-11-01）

- 需求/设计：189 §2.1、REQ-WF-001..005、RULE-10、TC-T12-PUBLISH-01..03；不是新增权限合同或发布HTTP。
- 新增7项真实MySQL测试：首轮**4失败/3通过/0跳过**。合法重复候选编译去重后发布错误返回Invalid；手工构造CompiledWorkflow的空候选/环/未知节点三种非法图均错误Published。另三项不同候选/会签模式/schema内容已经正确拒绝，作为保留行为回归，不称新RED。
- 修复：锁内针对原草稿和固定已发布schema复用WorkflowValidation.Compile，比较规范化定义与调用方快照后只存规范化定义。没有复制图算法、忽略内容差异或将候选ID集合当当前授权证明。
- GREEN：完整WorkflowFormPersistenceTests **22通过/0失败/0跳过**（包括既有SaveChanges各重载6项，不与较早9项叠加）；内核**113/113**；架构**29/29**。
- 已独立查询断言：成功发布后历史候选仅一个、draft删除、4份逻辑receipt/audit且重放不新增；非法图/变参保持原draft版本、definition published零份、仍仅3份此前receipt/audit。夹具仅清随机两tenant数据，不影响其他测试数据。

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~WorkflowFormPersistenceTests"
dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName~DefinitionValidationTests"
dotnet test tests\Architecture\Wes.Architecture.Tests.csproj --configuration Release --no-build --no-restore
dotnet build WES.slnx --configuration Release --no-restore
dotnet ef migrations has-pending-model-changes --project src\Modules\WorkflowForm\Wes.WorkflowForm.csproj --startup-project src\Modules\WorkflowForm\Wes.WorkflowForm.csproj --configuration Release --no-build
dotnet format whitespace WES.slnx --no-restore --verify-no-changes --include src\Modules\WorkflowForm\Features\Definitions\WorkflowDefinitionStore.cs tests\Persistence\WorkflowFormPersistenceTests.cs
```

Release **0警告/0错误**、EF无模型变化、定向格式通过；TRX保留在会话t12-publish-integration验证目录。无新依赖/DDL/Host/UI，故本轮不重复迁移Up/Down或前端/HTTP/浏览器，不将其记为通过。已按原授权wes_test执行真实MySQL，无生产访问。

自查：原operation指纹/重放顺序、CAS、原schema固定引用、失败无新receipt/audit和事务边界保持；成功规范化不修改既有历史。更新踩坑条目**GEN-003、GEN-006**，未新增ID。管理权限/字段合同询问未获可用答复，仍gated；不能把本续片交付称为可部署管理功能或整体T12完成。

收口文档检查：7份变更Markdown、654处本地相对目标，0新增断链；6处外部Seven/Route/架构材料目标在HEAD基线已不可用。本轮没有重建这些外部文档。`git diff --check`通过；远端main原为53932c1，本地其后T12a/SaveChanges守卫提交由本轮完整22项回归验证，发布采用正常快进，不force-push。
