# 全模块页面、功能与测试文档实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为全部保留模块交付页面线框、可实现功能规则和可执行详细测试，保持现有领域权威。

**Architecture:** 原模块规格与 ADR 继续约束行为；`docs/detailed-design` 深化交互与测试，不形成第二套业务事实。全部设计通过 PAGE/FUNC/DTC 与现有 SCOPE/REQ/TC 关联。

**Tech Stack:** Markdown；设计目标 Vue 3/Vite/TypeScript/Element Plus、UniApp；仅文档验证，不生成应用代码。

**Spec:** [已确认规格](../specs/2026-10-23-module-page-function-test-design.md)

## Global Constraints

- 仅文档与文本线框；不实现业务代码，不创建项目骨架或可执行原型。
- Web/Simulator 沿用 Vue 3 + Vite + TypeScript + Element Plus；PDA 沿用 UniApp + Vue 3 + TypeScript。
- WMS 是库存权威；Pack 是包内控制与 Route 权威；Orchestration 协调跨包与问题；Notifications 持有告警事实。
- 数据库预约账本是 grant 权威；单写者 epoch/fencing、未知占用保护遵守 ADR-0001。
- 不自动切换模式、解除联锁、提升权限或满足控制前提。
- 不离线排队/保存/重放写操作，不以 optimistic UI 证明库存或物理成功。
- 所有新增详细用例为 `designed / 未执行`，不得宣称浏览器、Provider 或设备测试通过。
- 外部案例只引用公共资料，不上传私有文件；来源不可访问时明确记录。
- 不自动 push；每个提交含规定 Co-authored-by trailer。

## 文件分工

| 文件 | 职责 |
|---|---|
| `docs/detailed-design/README.md` | 全部设计包、PAGE/FUNC/DTC 数量及范围入口 |
| `docs/detailed-design/00-shared-page-and-test-standard.md` | 字段、交互、状态、测试记录规范及跨页共用用例 |
| `docs/detailed-design/01-reference-cases.md` | 公共参考、读取日期、借鉴与排除内容、失效来源 |
| `docs/detailed-design/02-cross-module-scenarios.md` | 13条原E2E场景的跨owner步骤、断言与证据 |
| `docs/detailed-design/platform-and-security.md` | Platform 页面/功能/测试 |
| `docs/detailed-design/workflow-and-forms.md` | Workflow/Form 页面/功能/测试 |
| `docs/detailed-design/notifications-and-jobs.md` | 消息、告警、任务、公告、缓存诊断 |
| `docs/detailed-design/wms-and-ledger.md` | WMS 主数据、库存、入出库、盘点、TransferOrder |
| `docs/detailed-design/pda.md` | UniApp 扫码、业务提交、断网只读 |
| `docs/detailed-design/orchestration.md` | 地图、订单/Leg、处置命令、问题闭环 |
| `docs/detailed-design/wcs-stacker.md` | Stacker 任务与资源、包级最终验证 |
| `docs/detailed-design/wcs-fourway.md` | FourWay 交通、停车、提升机与恢复 |
| `docs/detailed-design/external-wcs-and-device-adapters.md` | 外部 WCS 能力及可查询命令 |
| `docs/detailed-design/device-communication.md` | 语义设备、连接、反馈、脱敏诊断 |
| `docs/detailed-design/environment-simulation-and-scada.md` | 地图、部署、仿真、Promote、只读 SCADA |
| `docs/detailed-design/route.md` | 路网/候选/账本/fencing/恢复/基准诊断 |
| `docs/detailed-design/system-operations.md` | 健康、日志、隔离恢复和部署操作手册 |
| 现有 README、模块入口、总追踪和验收目录 | 链接深化设计，保留原标识与权威 |

## Task 1: 共用规范与参考证据

**Interfaces:** 消费已确认规格和 Seven 前端依赖；产出各模块共同引用的 UI/DTC 规范。

- [x] 编写共用标准：PAGE/FUNC/DTC 结构、稳定排序/选择、权限/错误/断网、危险动作、回读和数据新鲜度规则。
- [x] 设计共用用例：加载与空态区分、局部失败、权限拒绝、并发、超时查结果、长文本/键盘/窄窗口；每条有固定前提、步骤、断言和清理。
- [x] 以已核验官方 ERPNext、OpenWMS、Element Plus 案例建立参考表；补查 openTCS 公共仓库与相关模块案例。每条区分借鉴与不采用。
- [x] 校验共用 ID 唯一、来源内容真实、相对链接存在。

## Task 2: Platform / Workflow / Notifications / 系统运维

**Interfaces:** 消费 Task 1 规范；产出四个独立设计包，前缀分别 PLAT、WF、NTF、SYS。

- [x] 通读对应四份既有规格及系统部署/验收约束；枚举所有保留功能与未决策略。
- [x] 为每组页面写入口、角色、线框、字段/列/过滤、操作前提、状态和共用测试适用表。
- [x] 为登录/刷新/权限、审批版本/并发/回调、告警/消息/Quartz/公告、恢复与诊断分别写事务、拒绝、幂等及恢复。
- [x] 编写详细 DTC：固定数据、编号步骤、持久化和副作用断言、清理与环境；覆盖原测试目录，不把未决参数写成已批准行为。
- [x] 核对 PAGE/FUNC/DTC 关系和引用；报告覆盖数量与遗留阻断项。

## Task 3: Stacker / FourWay / External WCS / DeviceIntegration

**Interfaces:** 消费共用规范、Pack 契约、ADR-0001；前缀 STK、FW、ADP、DEV。

- [x] 通读四个原模块规格，提取设备能力、资源/联锁、命令状态和未知结果规则。
- [x] 设计地图上下文中的包级面板及受控诊断页，不增加独立运维权威或裸协议发命令捷径。
- [x] 写清双深、层内交通、停车/提升机、供应商查询/取消缺失、设备命令/反馈的主流程与拒绝恢复。
- [x] 编写 DTC：设备状态数据、并发/故障注入、授权/幂等、物理回读、持久化预约与命令次数、Provider/Adapter 条件。
- [x] 校验不绕过 Pack/fencing/安全 PLC，不把仿真当作现场安全证据；报告覆盖数量和输入阻断。

## Task 4: WMS / PDA / Orchestration

**Interfaces:** 消费 Task 1 规范和既有 WMS/运输/问题状态机；前缀 WMS、PDA、BUS。

- [x] 设计 WMS 主数据、库存与流水、入出库/收发/拣选、盘点、TransferOrder 页面与关联追溯。
- [x] 设计 PDA 每个业务的扫码焦点、数量单位校验、超时查结果、只读缓存与恢复联网。
- [x] 设计 2D 工作台区域预算、对象图层、未定位队列、订单/Leg、运维动作及问题状态/验证/复开。
- [x] 写清功能事务与状态；使用具体数量验证记账/预留/运输/补偿互不混淆，告警清除不自动解决问题。
- [x] 编写 DTC 并逐一覆盖业务正向、拒绝、并发、重复、超时、设备未知及对账失败；校验原用例映射。

## Task 5: Simulator / SCADA / Route

**Interfaces:** 消费部署 manifest 协议、Route ADR 与原 Route 目录；前缀 SITE、ROUTE。

- [x] 设计地图编辑/校验/版本、prepare/activate/补偿部署、仿真注入/结果、真机 Promote 与只读 SCADA 页面。
- [x] 设计 Route 路网/候选/冲突/预约/epoch/等待/恢复/基准面板，归属 Pack 或 Simulator，不创建额外 grant 权威。
- [x] 为地图部署失败/重复恢复/旧任务版本、仿真重现、Promote 拒绝写明确步骤和断言。
- [x] 为原 Route 场景深化固定图、车辆、时间、grant/release、接管/未知占用和基准证据测试；参数未获现场确认时写明阻断条件。
- [x] 核对 REQ-ROUTE 既有范围映射，区分可根据原章节形式化的语义和需另行决策的行为，不宣称旧 ID 缺口自动关闭。

## Task 6: 整合、追踪与验证

**Interfaces:** 消费全部设计包；产出完整可导航文档集与准确验证结果。

- [x] 建立设计包覆盖索引；每个 SCOPE 保留/预留/移除有入口或明确排除。
- [x] 给既有模块和 Route 文档增加深化入口；在总追踪和验收目录加入 PAGE/FUNC/DTC 追踪规则。
- [x] UTF-8 读取全部新增文件，检查 PAGE/FUNC/DTC 定义唯一、引用存在、每个功能有页面及 DTC、相对链接存在；核对文件清单与数量。
- [x] 人工核对库存、Pack 最终控制、ledger/fencing、地图 manifest、问题证据与离线只读不变量。
- [x] 运行 VS Code Problems；仅报告文档检查，不跑不存在的应用测试。
- [x] 更新计划完成状态和 todos，提交本轮文档；报告未推送、未决输入及未运行测试。

## Self-Review

- 所有 13 个设计包均有任务与目标文件；测试记录不是测试代码，本计划无应用实现步骤。
- 验收来自规格 §7：Task 1 统一标准与来源；Tasks 2–5 页面/功能/测试；Task 6 追踪、链接和不变量。
- 无新架构、组件库或离线写入方案；关键现场参数只能以显式决策门禁管理。

## 文档验证记录

- 17份深化Markdown：13个模块包、共用规范、参考案例、跨模块场景与索引；64个PAGE、65个FUNC、134个DTC（109模块+12共用+13跨模块），共263个唯一定义。
- 32个SCOPE均有覆盖入口/明确排除；原模块/Route/验收255条TC与架构10条TC，共265条均可追溯到详细DTC。
- 只读验证已通过：定义唯一/连续、展开范围引用存在、每FUNC有PAGE与DTC、页面有功能、表格列数、严格UTF-8、453个本地文件链接目标存在。链接检查不验证锚点或外部站点可用性。
- VS Code Problems无错误；Git diff whitespace检查通过。校验中发现的表格错列和架构/PDA等价性映射遗漏已修正后重验。
- 人工检查的关键边界：WMS唯一账本、Pack最终控制、耐久ledger/fencing、不自动释放未知占用、manifest本地激活与owner准备、问题证据解决、PDA离线只读；均保留原批准边界。
- 不包含浏览器屏幕、真实Provider、Adapter、仿真或现场测试执行证据。所有模块包仍为评审中，Route旧REQ形式化候选及各模块现场/业务参数待owner签核。
