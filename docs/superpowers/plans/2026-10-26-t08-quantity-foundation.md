# T08 WMS数量领域基础实施计划

**目标：** 建立 WMS owner 内不可变数量值类型，执行 RULE-01 的精度、范围与非负规则，为后续库存/流水实体提供稳定基础。

**范围定位：** T08完整包硬依赖T04、T05、T06。就绪台账记录三者尚未整体完成，因此本计划仅处理无外部依赖的纯领域值对象；不集成模块运行时、授权、消息、页面或数据库，不标记T08已完成。

**基线：** [WES-RULES-1.0 RULE-01](../../detailed-design/05-approved-rules-and-defaults.md)、[WMS账本设计](../../modules/wms-and-ledger.md)、[WMS详细测试设计](../../detailed-design/wms-and-ledger.md)、[ADR-0002](../../adr/ADR-0002-mysql-first-implementation.md)。

## 约束

- 余额数量按 `decimal(28,6)` 表示，拒绝负数、超出数据库可表示范围及第七位非零小数；不得舍入。
- 单笔数量必须正数，最大`999999999.999999`，并受物料精度限制。
- 计件默认精度0；可分割计量默认精度3；显式精度范围0..6。
- 换算默认关闭；本切片不引入单位换算。
- 数值领域类型不得依赖EF Core、Platform/WCS内部类型、环境配置或IO。
- MySQL为后续首个数据库；本切片不实现迁移/持久层，也不将MySQL验证外推到其他Provider。

## 预检扫描表

本计划一个实现任务，没有跨任务共享文件/接口的任务对；审查该任务产出与消费关系如下：

| Task | 产出 | 同任务消费/验收 | 自洽性检查 |
|---|---|---|---|
| Task 1 | 独立WMS项目；`InventoryQuantity`、`QuantityPrecision`、`StockMovementQuantity`；定向单元测试；追踪及测试证据文档 | 单元测试消费领域API；测试项目和solution包含WMS项目，但Host不引用/注册它 | 类型负责库存精度/边界，逐笔值负责正数与策略精度；数据库/HTTP需求明确延后，不存在伪称已满足的代码接口 |

**Ruling:** T08完整包依赖T04/T05/T06仍未关闭，因此将本任务限制为纯领域基础，禁止读取或变更其他模块内部状态——如错误把该切片视为集成完成，将导致绕过权限、事件或客户端合同；代价是T08整体进度不因本切片解除依赖阻塞。

## Task 1：数量领域合同与测试 — 已完成，审查通过

- [x] 创建WMS领域项目并将其加入solution及Modules测试引用，不加入Host运行时装配。
- [x] 先编写测试并运行确认RED，再实现非负 `decimal(28,6)` 库存数量、范围精度值对象及符合该精度的正单笔数量。
- [x] 覆盖合法最大值/最小值、超界、负数、零、精度边界、尾随零、加减与算术越界；所有拒绝须显式。
- [x] 运行定向Release测试、项目Release构建、完整solution串行Release测试及格式/`git diff --check`。
- [x] 完成[实施文档](../../implementation/85-t08-quantity-domain-foundation.md)、[测试文档](../../implementation/86-t08-quantity-domain-foundation-tests.md)、就绪台账、工作包与需求追踪矩阵的局部证据更新。
- [x] 确认未修改数据库、外部服务、共享配置或生产装配；仅本领域切片完成，不关闭DTC-WMS-004/010/011、T08或其硬依赖。
- [x] 任务审查：规格符合；代码质量批准，无需修复。审查范围为 `8cedd36..ae7af78`。
- [x] 实现提交：`ae7af78e2dffdd8bfb1f7ad2064346606f93d938`，已含Copilot co-author trailer。

## 文件范围

- `src/Modules/Wms/Wes.Wms.csproj`
- `src/Modules/Wms/Domain/InventoryQuantity.cs`
- `tests/Modules/Wms/InventoryQuantityTests.cs`
- `tests/Modules/Wes.Modules.Tests.csproj`
- `WES.slnx`
- `docs/implementation/85-t08-quantity-domain-foundation.md`
- `docs/implementation/86-t08-quantity-domain-foundation-tests.md`
- `docs/implementation/readiness-register.md`
- `docs/superpowers/plans/2026-10-23-wes-development-work-packages.md`
- `docs/04-requirement-design-test-traceability.md`
