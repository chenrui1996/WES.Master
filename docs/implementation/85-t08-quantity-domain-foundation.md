# T08 数量领域基础切片

- 状态：已验收（仅Task 1纯领域数量合同）；T08主数据/账本未集成或完成。
- 依据：[T08工作包](../superpowers/plans/2026-10-23-wes-development-work-packages.md)、[WES-RULES-1.0 RULE-01](../detailed-design/05-approved-rules-and-defaults.md)、[WMS账本设计](../modules/wms-and-ledger.md) §1、[WMS详细测试设计](../detailed-design/wms-and-ledger.md)。
- 硬依赖：T08整体依赖T04、T05、T06；就绪台账中三者仍未整体关闭。本切片仅提供不引用这些模块、不执行IO的WMS领域类型，暂不接入租户授权、Outbox/Inbox、页面或持久化。
- 测试记录：[T08数量领域测试](86-t08-quantity-domain-foundation-tests.md)。
- 执行计划：[T08数量领域切片计划](../superpowers/plans/2026-10-26-t08-quantity-foundation.md)。

## 目标与合同

为后续WMS主数据和账本提供强类型、不可变的数量基础，避免十进制精度、非负库存和逐笔数量上限依赖调用方自觉。类型只表示以物料基准单位计量的数值，不包含单位换算、物料身份、仓库/租户、账本事件或授权语义。

- `InventoryQuantity` 表示可持久化库存/保护量：非负，最多6位有效小数，不舍入；范围必须符合 `decimal(28,6)`，最大值为 `9999999999999999999999.999999`。支持精确加减；减至负数或超出列范围明确失败。
- `QuantityPrecision` 表示单位精度：计件默认0位，可分割单位默认3位；显式精度只允许0..6。用数值可表示性验证数量，不因输入Decimal携带无意义的尾随零而错误拒绝，不做四舍五入。
- `StockMovementQuantity` 表示正的单笔数量：上限为 `999999999.999999`，并必须符合调用方提供的精度。冲销/减少通过账务方向表达，不以负数量绕过正数限制。

## 明确边界

- 不创建库存余额实体、流水/保护/位置/容器实体、业务命令、查询、API、前端页面或设备/运输合同。
- 不创建EF配置、迁移、DbContext、SQL约束或数据库并发实现；真实MySQL十进制列/溢出与事务测试留待后续持久化切片。
- 不加入Provider抽象，不声称SQL Server/PostgreSQL兼容；遵从ADR-0002的MySQL优先决策。
- 不引用Platform/WCS私有类型；不执行租户授权或生成业务成功结果。
- 这些本地类型测试不是DTC-WMS-004/010/011整体验收，T08及其依赖仍保持未完成。

## 实现结果

Task 1新增独立 `Wes.Wms` 领域项目，并将其加入solution及Modules测试项目引用；Host没有引用或装配该项目。项目只依赖.NET基础类型。领域合同集中在 `Domain/InventoryQuantity.cs`：

- `InventoryQuantity` 只接受非负、数值上可由六位小数精确表示且不超过 `9999999999999999999999.999999` 的值。尾随零按数值而非Decimal内部scale判断。加减均精确执行；负结果和超过范围的结果明确抛出 `OverflowException`。
- `QuantityPrecision` 只接受0..6，提供计件精度0与可分割单位默认精度3；`CanRepresent`检查舍入前后的数值相等，不执行舍入。
- `StockMovementQuantity` 仅接受正数，不超过 `999999999.999999`，并符合传入的单位精度；方向/冲销不通过负数表达。

所有创建入口均拒绝非法值而不静默舍入。此领域实现不证明数据库列映射、provider舍入/溢出、事务、并发或任何WMS账本行为。T08 Task 1局部完成；DTC-WMS-004/010/011、T08及T04/T05/T06硬依赖保持未关闭。

## 代码与测试证据

- 先只添加项目/solution/测试引用与测试，再运行Release定向测试。初始RED为预期缺失领域命名空间 `Wes.Wms`（CS0234）；具体命令和结果见[测试记录](86-t08-quantity-domain-foundation-tests.md)。
- 增加16个定向单元测试后，定向Release测试、WMS项目Release构建、完整solution串行Release测试、修改C#文件格式检查及 `git diff --check` 均通过；详见[测试记录](86-t08-quantity-domain-foundation-tests.md)。
- 不需要数据库迁移、部署或回滚步骤；该项目尚未进入Host运行时组合。
