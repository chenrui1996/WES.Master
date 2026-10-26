# T08 执行单元保护量并集

- 状态：已验收（仅WMS纯领域投影切片），不含持久化/并发或T08整体。
- 基线：[RULE-01/05/08及REV-03](../detailed-design/05-approved-rules-and-defaults.md)、[WMS不变量§1](../modules/wms-and-ledger.md)、[工作包T08](../superpowers/plans/2026-10-23-wes-development-work-packages.md)。
- 追踪：SCOPE-WMS-02 / REQ-WMS-005..011 / FUNC-WMS-007 / DTC-WMS-004/010及VAL-01的保护量计算子集。
- 测试：[保护量测试记录](92-t08-stock-protection-union-tests.md)；数量基础：[上一切片](85-t08-quantity-domain-foundation.md)。

## 领域设计

`StockProtection`表示owner已经划分的互不重叠库存执行单元：非空`ExecutionUnitId`、正的`InventoryQuantity`、一个或多个`Reserved/InTransit/Unknown`标志。它是不可变值对象，不提供Release/过期/写数据库方法。未受保护库存不需要输入记录。只有在owner验证后已知无保护的单元才能从输入移除；不能因时间、运输失败或消息丢失自行移除Unknown。

`StockAvailability.Calculate(quantity, protections)`只计算当前同一库存维度/基准单位的投影：`Quantity`账面量保持不变，`Reserved`、`InTransit`、`Unknown`分别统计包含对应标志的单元，`Protected`对每个单元只计一次，`Available=Quantity-Protected`。不同单元相加而不是取最大值。输入有重复单元ID时拒绝，即使数量或标志相同，也不隐式合并或猜测输入者想表达的交集。

部分重叠须显式拆成互不重叠的单元。例如Qty10、Reserved7含InTransit5：单元A=5同时Reserved/InTransit、单元B=2仅Reserved，Protected7/Available3。Reserved4与完全不同单元的InTransit3则Protected7，而不是max(4,3)=4。

数量复用`InventoryQuantity`约束；这不是单笔移动输入，真正的移动仍须使用`StockMovementQuantity`的上限和物料精度校验。空单元ID、零量、None/未知标志、重复单元和保护总量超过Qty明确抛异常，不Clamp、不舍入、不返回0冒充安全结果。投影不持有输入集合引用，外部集合变动不会改变既有投影。

## 边界及后续接线

本类不是库存权威API，也不接收HTTP输入。未来WMS事务用例必须从同一tenant/warehouse/location/item/batch/container维度加载经过CAS/唯一约束保护的数据，证明单元互不重叠与预留/运输身份，不得用客户端给出的“不同ID”当作实物不同。领域函数本身无法证明不同ID没有指向相同物料部分。

本切片无持久化、EF、迁移、Outbox、预留写命令、FIFO/FEFO、序列策略或设备控制；不引入冻结业务。T04/T05/T06硬依赖、T08整包及真实MySQL事务/并发验收保持未完成。新代码尚未加入Host运行时，不需要生产迁移或参数回滚。
