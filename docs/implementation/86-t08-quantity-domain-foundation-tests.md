# T08 数量领域基础测试记录

- 状态：已验收（仅Task 1本地纯领域自动化范围）；不将其计为WMS Provider或父DTC通过。
- 关联：[实施记录](85-t08-quantity-domain-foundation.md)、[切片计划](../superpowers/plans/2026-10-26-t08-quantity-foundation.md)。
- 需求来源：WES-RULES-1.0 RULE-01；T08中的DTC-WMS-004/010/011数量精度和溢出变体。

## 测试场景

1. 库存量允许0、`decimal(28,6)`最大合法值和六位小数；拒绝负数、超列范围或第七位非零小数，不静默舍入。
2. 精确加法/减法保持数值；加法越界、减法为负必须显式失败。
3. 计件与可分割单位默认精度分别为0与3；显式精度0..6接受，范围外拒绝。
4. 单笔数量必须大于0、不得超过`999999999.999999`且满足配置精度；合法边界通过，超限/超精度/零/负值拒绝。
5. 尾随零不改变数量的数学可表示性；单位换算、JSON/API编码及数据库映射不在此切片内。

## 实测结果

| 范围 | 命令 | 实测结果 |
|---|---|---|
| TDD初始红灯 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --filter FullyQualifiedName~InventoryQuantityTests` | 预期失败，退出码1；空WMS项目编译成功后，测试报 `InventoryQuantityTests.cs(1,11): error CS0234`，`Wes`中不存在`Wms`命名空间。此时仅有测试和项目引用，生产领域类型尚未实现。 |
| WMS数量领域测试 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --filter FullyQualifiedName~InventoryQuantityTests` | 通过16项，失败0，跳过0。 |
| WMS项目Release构建 | `dotnet build src\Modules\Wms\Wes.Wms.csproj --configuration Release --no-restore` | 成功；0警告、0错误。 |
| 完整solution串行Release测试 | `dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1` | 通过900项，失败0，跳过0：Architecture 29、Modules 287、Persistence 356、Route 228。 |
| C#格式检查 | `dotnet format WES.slnx --no-restore --verify-no-changes --include src\Modules\Wms\Domain\InventoryQuantity.cs tests\Modules\Wms\InventoryQuantityTests.cs` | 通过，无格式差异。 |
| diff空白检查 | `git diff --check` | 通过，无空白错误。 |

定向测试覆盖0/最小正数/`decimal(28,6)`最大值、负数/超范围/第七位非零小数、尾随零、精确加减及上下溢、精度默认值和0..6范围、按数值可表示性判断、单笔正数/最大值/精度边界及非法值拒绝。以上均为纯领域单元测试，不连接数据库。

测试数据曾触发一次xUnit `xUnit1010`内联参数类型诊断；改为不经二进制浮点数的精确字符串输入后，最终定向Release测试和全solution串行Release测试均通过。该测试编译错误未改动生产合同，也未通过删减场景规避。

## 限制

本记录不证明MySQL `decimal(28,6)`映射/舍入语义、真实事务/并发不变量、账本可重算性、跨租户唯一键或页面/API行为。持久化测试须在获得明确数据库边界和T04/T05/T06前置证据后另立切片。
