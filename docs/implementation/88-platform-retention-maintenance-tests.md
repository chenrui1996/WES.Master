# T04/T05 保留维护测试

- 设计：[87](87-platform-retention-maintenance.md)。
- 范围：RET-MAINT-01..08，仅本片；不等同T04/T05完整验收。

| ID | 场景与期望 |
|---|---|
| RET-MAINT-01 | 默认/显式禁用无注册、无owner解析、无timer；启用必须Identity及显式tenant/interval/batch |
| RET-MAINT-02 | 错误布尔、数字、范围、数组、UUID、重复tenant、未知配置失败，不回显值 |
| RET-MAINT-03 | 单tick只按白名单串行一批，不扫描/追赶；safe成功计数；瞬时失败单独记录并继续，非瞬时/程序错误不伪装成功 |
| RET-MAINT-04 | TimeProvider驱动间隔；停止取消等待/当前调用，取消有安全分类 |
| RET-MAINT-05 | 真实MySQL两类90天前/恰好90天/90天后边界，分别有界，其他tenant不变 |
| RET-MAINT-06 | 两类删除保存后失败或取消，独立新context确认全部回滚；预取消拒绝 |
| RET-MAINT-07 | 审计/Outbox/目录领域身份保留；原有Unknown不引入清理目标 |
| RET-MAINT-08 | 非干净owner拒绝；公开PlatformScope路径返回提交后的计数 |
| RET-MAINT-09 | 真实MySQL另一owner在当前owner选中后先提交清理；旧owner明确瞬时冲突，未提交计数不返回，领域事实保留，Host下周期可继续 |

真实数据库仅经现有`MySqlTestConnection.LoadAuthorizedTestConnection`访问`wes_test`，每例生成唯一tenant，清理只针对这些tenant；不输出秘密。测试迁移只针对授权测试数据库，运行时无迁移。

## RED/GREEN证据

2026-10-26，先写87/88及测试，再实现。初始RED：

```powershell
dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RetentionMaintenanceTests --verbosity quiet
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RetentionMaintenancePersistenceTests --verbosity quiet
```

Host测试因缺失`PlatformRetentionWorker`/`IPlatformRetentionRunner`/结果类型出现CS0246；Persistence测试因缺少公开维护方法/核心出现CS1061/CS0103（附带目标类型推断错误）。均为测试先于功能存在的编译RED，不算通过用例。

实现后首次Host 29 passed/2 failed，是测试错误地按ImmutableArray值身份比较相同元素，改为单元素断言。MySQL夹具最初缺少tenant FK父记录，补齐唯一tenant初始化；随后随机UUID首字符不满足已有目录code规范，改为固定字母前缀。未绕开Provider约束或更改业务规则。

最终GREEN：

```powershell
dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RetentionMaintenanceTests --verbosity quiet
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~RetentionMaintenancePersistenceTests|FullyQualifiedName~DirectoryReceiptTests.PlatformInboxRetention|FullyQualifiedName~DirectoryReceiptTests.RetentionCleanup|FullyQualifiedName~DirectoryReceiptTests.MembershipReceiptExpiry' --verbosity quiet
```

- Modules：32 passed、0 failed、0 skipped（新增配置边界、allowlist上限、runner成功/失败/取消、TimeProvider与停止测试）。
- Persistence：24 passed、0 failed、0 skipped（新增11项、原有相关保留回归13项）。真实MySQL验证90天边界、每类批量、tenant隔离；保存后失败/取消、提交前取消及Inbox DELETE期间瞬时/永久DbException均确认两类回执没有部分提交。DbException转换无inner exception/注入秘密，分类保留；审计/Outbox/领域目录实体计数不变。
- 两个Release定向测试命令同时编译其依赖图；无编译警告或错误。没有运行全套测试，集成者另行统一回归。

限定格式和diff检查：

```powershell
dotnet format whitespace WES.slnx --no-restore --include src\Host\Program.cs src\Host\Platform\PlatformRetentionSettings.cs src\Host\Platform\PlatformRetentionWorker.cs src\Modules\Platform\PlatformScope.cs src\Modules\Platform\PlatformRetentionMaintenance.cs tests\Modules\RetentionMaintenanceTests.cs tests\Persistence\RetentionMaintenancePersistenceTests.cs --verbosity quiet
dotnet format whitespace WES.slnx --no-restore --include src\Host\Program.cs src\Host\Platform\PlatformRetentionSettings.cs src\Host\Platform\PlatformRetentionWorker.cs src\Modules\Platform\PlatformScope.cs src\Modules\Platform\PlatformRetentionMaintenance.cs tests\Modules\RetentionMaintenanceTests.cs tests\Persistence\RetentionMaintenancePersistenceTests.cs --verify-no-changes --verbosity quiet
git diff --check -- src\Host src\Modules\Platform tests\Modules\RetentionMaintenanceTests.cs tests\Persistence\RetentionMaintenancePersistenceTests.cs docs\implementation\23-t04-command-receipts.md docs\implementation\37-t05-platform-inbox.md docs\implementation\49-t05-inbox-retention-save-recovery.md docs\implementation\87-platform-retention-maintenance.md docs\implementation\88-platform-retention-maintenance-tests.md
```

## 集成边界复核与复跑

- 补充.NET配置键大小写回归：大小写变体2项初始均抛出配置错误，修复白名单/存在性判断使用`OrdinalIgnoreCase`后通过。未知字段和非法值仍拒绝；维护Modules用例现为34项。
- 补充RET-MAINT-09：用真实MySQL两个owner，第二owner在第一owner完成查询后、Save前先提交删除。初始明确复现`DbUpdateConcurrencyException`越过安全分类，修复为瞬时安全失败后通过；不加入忙重试或吞异常。
- 最终定向Persistence命令沿用上表筛选：**25 passed**（新增维护12项 + 既有保留回归13项）；两类回执清理与事实计数保持正确。
- `dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~RetentionMaintenanceTests|FullyQualifiedName~Wes.Modules.Tests.Wms" -m:1`：**73 passed**，其中维护34项、WMS39项。
- 全部修改C#通过`dotnet format WES.slnx --no-restore --verify-no-changes --include <修改的C#路径>`及`git diff --check`；完整串行Release结果见[就绪台账](readiness-register.md)最新批次。

无新增依赖、迁移、临时报告或生产连接。未验证真实Broker、生产多实例容量/长时间故障恢复、SQL Server/PostgreSQL、审计归档或物理Unknown恢复；两owner有序竞争不代表生产集群单例验收。清理只选择两种普通收据，不存在领域Unknown删除分支，本片不宣称整包完成。
