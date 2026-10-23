# WES.Master 项目启动文档

WES.Master 是面向仓储执行与设备调度的新项目。本阶段先确定业务边界、架构决策、开发治理和验证方式；**不创建解决方案、源代码或脚手架**。

## 权威设计

- [项目启动设计规格](docs/00-project-startup-design.md)：已确认的项目目标、业务范围、架构边界、Route 重构方向、工程规范和文档计划。
- [Agent 开发约束](AGENTS.md)：本阶段文档范围及后续实现不可偏离的架构边界。

## 文档工作区

以下是本轮已整理的文档章节。每份功能规格都有测试设计，并在总追踪矩阵中关联需求 ID、设计章节、测试 ID 和验收结果；测试用例尚非自动化测试结果。

| 文档组 | 内容 |
|---|---|
| 项目治理 | [业务范围与现状矩阵](docs/01-business-scope-and-status.md)、[系统架构](docs/02-system-architecture.md)、[工程/注释/文档规范](docs/03-engineering-governance.md)、[需求追踪矩阵](docs/04-requirement-design-test-traceability.md)、[ADR](docs/adr/README.md) |
| Platform | [身份认证、RBAC、数据权限、多租户、配置、文件、审计与安全](docs/modules/platform-and-security.md) |
| Workflow/Form | [审批与动态表单](docs/modules/workflow-and-forms.md)、[Quartz/消息/outbox/邮件/告警与通知](docs/modules/notifications-and-jobs.md) |
| WMS/PDA | [WMS 与库存账本](docs/modules/wms-and-ledger.md)、[PDA](docs/modules/pda.md)：入库、出库/拣选/发运、盘点、移库；UniApp 离线只读 |
| Orchestration/WCS | [Orchestration](docs/modules/orchestration.md)：跨包运输与以 2D 监控地图为主入口的 WCS 执行/运维控制；[Stacker](docs/modules/wcs-stacker.md)、[FourWay](docs/modules/wcs-fourway.md)：包内执行与命令最终校验；[外部 WCS/设备适配器](docs/modules/external-wcs-and-device-adapters.md) |
| 环境搭建、仿真与现场可视化 | [DeviceComm](docs/modules/device-communication.md)、[环境/仿真/SCADA](docs/modules/environment-simulation-and-scada.md)：设备接入环境、地图 Deploy、仿真测试、Promote 真机切换与 SCADA 状态投影；SCADA 是地图数据源，不拥有运维控制 |
| Route 专项 | [现状与论文适配分析](docs/route/01-as-is-and-research-review.md)、[目标规划/调度](docs/route/02-target-routing-and-scheduling.md)、[测试目录与基准协议](docs/route/03-route-test-catalog.md) |
| 验收 | [跨模块 E2E、Provider 兼容、非功能测试、部署与可追踪验收矩阵](docs/acceptance/01-cross-module-and-provider-matrix.md) |

## 基线参考

- [Seven.Master 文档索引](../Seven.Master/doc/README.md)
- [Seven.Master WMS/WCS 实现说明](../Seven.Master/doc/20-WMS与WCS实现说明.md)
- [Seven.Master 功能开关](../Seven.Master/doc/14-功能开关.md)
- [Seven.Master 测试方法](../Seven.Master/doc/keypoint/09-测试方法与落库回归.md)
- [系统架构设计参考](../系统架构设计.md)
- [Route 研究资料目录](../Route)

## 设计原则

1. 以 Seven.Master 当前代码、文档、测试可证明的功能作为保留基线；对未实现、部分实现、预留和新增能力分别标记。
2. WES.Master 的功能设计和测试用例是后续实现的前置依据。任何功能变更都必须同步更新设计、追踪关系和验收用例。
3. 业务接口和单据扩展向上稳定；设备协议和供应商差异向下由适配器隔离。
4. WMS 是库存账本权威；各 WCS 包拥有自己的路网、交通与设备模型；跨包运输通过编排契约。
5. 本阶段仅编写与审查文档，不生成业务代码或项目骨架。
