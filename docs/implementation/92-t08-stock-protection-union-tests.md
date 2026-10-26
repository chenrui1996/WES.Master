# T08 执行单元保护量测试记录

- 状态：已验收（仅纯领域保护量计算），不关闭父DTC。
- 关联：[设计](91-t08-stock-protection-union.md)、[批次计划](../superpowers/plans/2026-10-26-platform-retention-candidates-stock-protection.md)。

## 用例

- PROT-01：Qty10，Reserved7包含InTransit5，投影10/7/5/Protected7/Available3。
- PROT-02：完全不重叠的Reserved4与InTransit3，Protected7/Available3，不按max猜交集。
- PROT-03：一个单元同时Reserved/InTransit/Unknown只保护一次；Unknown单独仍不可用。
- PROT-04：零库存/无保护、六位小数、最大合法库存边界保持精确。
- PROT-05：空ID、零量、None/未知标志、重复单元、超量保护或合计溢出拒绝。
- PROT-06：输入null/非法元素显式拒绝，输入集合变化不更改已有结果，顺序不改变数值投影。

## 实测结果

| 检查 | 命令 | 结果 |
|---|---|---|
| RED | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~StockProtectionTests -m:1` | CS0246：缺少`StockProtectionKind`/`StockProtection`；代码尚未实现，符合测试先行 |
| 初轮GREEN | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~Wes.Modules.Tests.Wms" -m:1` | 31 passed：新增保护量15项 + 原数量16项 |
| 最终定向 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~RetentionMaintenanceTests\|FullyQualifiedName~Wes.Modules.Tests.Wms" -m:1` | 73 passed；WMS39项（新增保护23 + 数量16）、维护34项，0 failed/0 skipped |
| 项目构建 | `dotnet build src\Modules\Wms\Wes.Wms.csproj --configuration Release --no-restore` | 0 warning / 0 error |
| 格式 | `dotnet format WES.slnx --no-restore --verify-no-changes --include src\Modules\Wms\Domain\StockProtection.cs tests\Modules\Wms\StockProtectionTests.cs` | 通过；最终整批修改C#完整分析格式亦通过 |

第二轮定向复测曾遇到同期T04/T05测试先行添加的`RetentionMaintenanceTests`缺失待实现类型，未执行测试；这是并行实施中的构建中间态，不是WMS通过证据。待另一切片具备完整实现后，统一复跑定向与全套，不删除其测试或屏蔽编译错误。

完整串行Release回归的最终数量和复跑命令见[就绪台账](readiness-register.md)最新批次。保护量23项覆盖全部七种有效标志组合、最大账面量完全保护、精确六位小数、重复单元和溢出/超量拒绝；不以测试数量替代未实现的持久化/事务门禁。

## 未覆盖

真实MySQL的decimal映射、租户隔离、CAS/重复业务键/账本、并发预留、运输身份和页面尚未集成。本函数要求调用owner提供同库存维度内经过验证的互不重叠单元；仅单元ID不同无法证明实物不同。没有基于TTL或取消消息释放保护的路径。
