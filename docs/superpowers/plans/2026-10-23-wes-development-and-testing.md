# WES 全模块开发与测试 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task after explicit implementation-phase approval. Steps use checkbox (`- [ ]`) syntax for tracking. Do not create agents or worktrees without authorization.

**Goal:** 按依赖交付全部保留业务、64个PAGE、65个FUNC及167条DTC的实现与可复核证据，而不是按模块名批量生成CRUD。

**Architecture:** 沿用模块化单体，模块内Vertical Slice、复杂业务以领域不变量保护。WMS独占账本，Orchestration拥有跨包运输/问题，各Pack独占交通与最终控制，Simulator协调manifest而不写其他owner私表。

**Tech Stack:** .NET 10、EF Core、SQL Server/PostgreSQL/MySQL；Vue 3/Vite/TypeScript/Element Plus、UniApp；沿用Seven的身份/消息/Quartz/SignalR等通用设计。测试选型/确切版本在T02锁定，真实Provider集成不能由InMemory替代。

**Spec:** [设计索引](../../detailed-design/README.md)、[架构](../../02-system-architecture.md)、[治理](../../03-engineering-governance.md)、[规则基线](../../detailed-design/05-approved-rules-and-defaults.md)、[ADR-0001](../../adr/ADR-0001-route-reservation-authority.md)。

## Global Constraints

- 状态：实施中；项目发起人已批准在main实施及现有软件设计基线。首批状态/缺项见[实施台账](../../implementation/readiness-register.md)，不能把工作包部分切片计作整体完成。
- 数据库执行调整：按[ADR-0002](../../adr/ADR-0002-mysql-first-implementation.md)，原计划全部“三Provider”首期开发门禁先要求真实MySQL，SQL Server/PostgreSQL延后兼容；下文保留全产品兼容目标，不据MySQL结果推定兼容通过。
- 后端基线 .NET 10、nullable、明确 DTO/领域类型、async/CancellationToken。
- 运维端/Simulator 使用 Vue 3 + Vite + TypeScript；PDA 使用 UniApp + Vue 3 + TypeScript。
- WMS 是物料数量的唯一权威。业务模块不直接修改其他模块私表/DbContext，不创建第二账本或跨Pack共享运行态。
- 不使用 Builder、业务源码模板或依赖生成器才能维护的功能。公共组件是具名复用，不是元数据生成页面。
- PDA 离线仅只读缓存，不保存/重放写操作；持久业务缓存默认关闭。Accepted、Broker ACK、动画和日志均不等于业务/物理完成。
- 单个route grant全量原子，MapVersion不隔离物理容量；每Pack独立耐久ledger/单writer epoch。Unknown占用不得随TTL清除。
- B业务定案/P初始参数/H证据门禁按WES-RULES-1.0；非法配置显式失败，不静默回退。缺现场输入禁对应生产控制，隔离仿真不视为现场支持。
- 原REQ-ROUTE-001..027已逐项记录项目发起人软件语义批准，见实施台账；现有模块设计已批为软件基线。设计批准不代替Provider/设备/性能/页面包验收。
- 本计划不包含自动提交/推送或真实部署授权。实施时每个可审核切片可形成独立提交，但须遵守用户当时授权和仓库协作流程。

---

## 1. 文档分工与使用方式

| 文档 | 用途 |
|---|---|
| 本文 | 依赖图、交付波次、阶段门禁、角色和变更规则 |
| [逐工作包开发计划](2026-10-23-wes-development-work-packages.md) | T01..T34的文件职责、消费/产出契约、可审核步骤、失败测试与交付断言 |
| [测试执行与证据计划](2026-10-23-wes-test-execution.md) | 167条DTC的责任入口、适用环境、RVT/VAL、故障/恢复/性能与放行 |
| [详细设计索引](../../detailed-design/README.md) | 页面字段/列配置、功能流程、DTC步骤的唯一细节来源；本计划不复制第二套业务规则 |

下列源路径是逐片创建的职责约定，不表示所有文件已存在；已实现入口仅以实施记录为准。现有设计文件以相对链接引用。各任务中的数段均含两端，例如001..003代表001、002、003。

先按T01确认范围/契约，再按依赖选ready任务。不要按表格行号把所有任务串行，也不要按功能看似独立而跳过硬依赖。同一工作包内按具名切片分PR，每片均有自己的失败测试→最小实现→复测→文档证据闭环。

## 2. 三种依赖，不把协作关系误画成编译环

| 类型 | 含义 | 处理 |
|---|---|---|
| C 契约先行 | 调用者与被调用者可同时实现，但契约必须版本冻结 | T01定义公开命令/事件/错误与owner；不先共享私有entity |
| D 硬依赖 | 前置产物及其相关测试通过后，后置任务才可完成集成 | 下表“硬依赖”是完整直接前置；传递依赖不重复列 |
| H 现场输入 | 供应商/设备/终端/DBA证据决定是否可真实执行 | 不阻塞纯算法/隔离仿真设计，但阻断对应真实投用；见测试计划H清单 |

WMS→运输意图→Orchestration→Pack→完成事实→WMS形成的是业务闭环，不是工程依赖环。契约与各owner handler分开实现，用可控contract fixture提前测试；T32必须换成实际模块连通。测试替身只能存在测试装配，生产装配遇到缺模块必须不可用，禁止成功型默认实现。

Route不是新增顶层业务模块：T17可提纯无状态算法；T18/T19的ledger、拓扑、writer和运行状态仍放各Pack私有边界。SCADA只读与运维地图的客户端可复用渲染，不合并状态owner。

## 3. 完整依赖表与波次

“牵头角色”不是已指定人员。项目owner在T01把角色绑定具名主/备；一人可兼任多个角色，双人高后果复核仍须不同自然人。波次按依赖计算最早ready位置，不是承诺的周数。

| Task | 可审核交付 | 硬依赖 | 最早波次 | 牵头角色 |
|---|---|---|---:|---|
| T01 | 实施范围逐项签核、接口/数据owner与H台账 | 无 | 0 | 架构/业务/QA |
| T02 | 最小Host、工程/测试装配、架构/Provider基础门禁 | T01 | 1 | 后端/QA/运维 |
| T03 | 登录/会话/撤销/限流 | T02 | 2 | Platform |
| T04 | RBAC/租户/配置/审计与策略版本 | T03 | 3 | Platform |
| T05 | 各owner事务Outbox/Inbox与投递恢复 | T04 | 4 | 后端/消息 |
| T06 | 共用页面状态、密集交互、授权查询/快照合同 | T04 | 4 | 前端/后端/QA |
| T07 | 隔离文件生命周期 | T04,T06 | 5 | Platform |
| T08 | WMS主数据/账本/保护数量与查询 | T04,T05,T06 | 5 | WMS |
| T09 | 收货/上架意图与完成记账 | T08 | 6 | WMS |
| T10 | 出库预留/拣选/短拣/直发/取消 | T08 | 6 | WMS |
| T11 | 局部锁盘点与逐单元TransferOrder | T09,T10 | 7 | WMS |
| T12 | 表单版本/有限流程定义发布 | T04,T06,T07 | 6 | Workflow |
| T13 | 流程决定/竞争/撤回/Outbox业务hook | T12,T05,T09 | 7 | Workflow/WMS |
| T14 | Jobs/告警/渠道/公告/缓存诊断 | T05,T06 | 5 | Notifications |
| T15 | 运输订单/Leg/交接/取消及WMS回写协调 | T08,T05,T06 | 6 | Orchestration |
| T16 | 语义设备契约/虚拟Adapter/规则及耐久执行 | T04,T05 | 5 | Device/QA |
| T17 | 不可变图/physicalConflictId/Dijkstra/独立oracle | T02 | 2 | Route |
| T18 | 每Pack全量grant/lease/epoch真实Provider验证 | T17,T04,T05 | 5 | Route/DBA |
| T19 | dispatch/反馈/接管/Unknown/等待公平 | T18,T16 | 6 | Route/Device |
| T20 | Stacker巷道/双深/轮转/分配claim | T08,T17,T06 | 6 | Stacker |
| T21 | Stacker挡路/ExeStack段/恢复/包级控制 | T20,T19,T15 | 7 | Stacker |
| T22 | FourWay层巷分配/停车资源 | T08,T17,T06 | 6 | FourWay |
| T23 | FourWay交通/提升三阶段/恢复/包级控制 | T22,T19,T15 | 7 | FourWay |
| T24 | External配置/出站/验签回调/Unknown | T15,T16,T06 | 7 | 集成 |
| T25 | Step7/Modbus逐驱动、单写宿主与隔离台架 | T16,T06 | 6 | Device/设备方 |
| T26 | 工程校验、owner准备、active CAS/排空/Promote门禁 | T17,T15,T21,T23,T24 | 8 | Simulator/各owner |
| T27 | 确定性仿真/Reset/只读SCADA | T26,T16 | 9 | Simulator/前端 |
| T28 | 单2D地图、问题生命周期和受权控制闭环 | T14,T15,T21,T23,T24,T26,T27 | 10 | Orchestration/前端 |
| T29 | UniApp业务/扫码/断网/operation恢复 | T09,T10,T11,T15,T06,T14,T28 | 11 | PDA/WMS |
| T30 | 全链运行诊断/单写验证/恢复演练/发布手册 | T18,T16,T26,T28,T14,T07 | 11 | 运维/DBA/QA |
| T31 | Seven迁移映射/校验/隔离试迁与切换演练 | T11,T13,T30 | 12 | 迁移/WMS/业务 |
| T32 | 13条跨模块E2E、全部页面适用性与三Provider回归 | T13,T21,T23,T24,T26,T27,T28,T29,T30,T31 | 13 | QA/全模块 |
| T33 | 固定容量性能/性质/恢复目标实测 | T32 | 14 | QA/Route/运维 |
| T34 | 真实现场验收、分阶段Promote与上线/回退签署 | T25,T33 | 15 | 现场/安全/业务/发布 |

可提前的子切片：T29登录/空壳任务页在T03/T06后、T28只读地图在SCADA投影合同冻结后、T30健康底座在T02内完成。它们只能标“切片完成”，不改变本表整个工作包的完成依赖，不得提前放行完整业务或生产控制。

T30/T32可以先取得虚拟设备上的软件恢复/集成结论，因此T30不硬依赖台架交付T25；真实驱动/固件恢复矩阵仍随T25证据取得后重跑，直到T34前全部满足。不得因硬件缺失阻塞隔离软件测试，也不得用这次虚拟结果勾选真实C/H行。

## 4. 里程碑、交付范围和退出门禁

| 里程碑 | 必需任务集合 | 可演示成果 | 必须取得的证据 |
|---|---|---|---|
| M0 实施就绪 | T01 | 明确范围、已批准页面切片/REQ、角色与H阻断 | 契约版本、迁移映射责任、精确Provider/终端/供应商验收清单；不能只签总标题 |
| M1 工程可信底座 | T02..T07 | 授权登录、通用状态、真实DB事务消息/隔离文件 | 越权/跨租户拒绝、事务故障无业务或事件半写、参数边界、静态架构门禁 |
| M2 人工仓储闭环 | T08..T14 | 收发/短拣/盘点/多行移库、审批/通知 | 账本重算、三Provider竞争、All/Any、已实物到待补账不重运；不以人工流程冒充设备闭环 |
| M3 执行内核 | T15..T25 | 虚拟设备上ST/FW/EXT执行与恢复；批准台架协议 | 全量grant/epoch/Unknown保护、双包handover、反馈故障/幂等；真实能力逐项未证则blocked |
| M4 可操作产品 | T26..T30 | 部署/仿真/SCADA/地图运维/PDA/诊断联动 | active CAS/排空、64页适用状态、双人绑定、问题证据闭环、断网无待写/重放 |
| M5 预生产可信 | T31..T33 | 脱敏隔离迁移、跨模块回归及固定负载报告 | 167父DTC展开结果、全部265原TC可追溯；三Provider分别结论、RVT/VAL和性能各次分位、恢复水位对账 |
| M6 生产许可 | T34 | 只读联调→受控单设备→批准扩大 | 全部相关H证据/主备/runbook/网络隔离、业务与设备安全签署；阶段变更重新批准，无自动扩大 |

M1..M3可在隔离测试环境里形成可运行增量，但不是生产许可。任何高风险不变量失败暂停相关链路，不暂停无关只读文档工作。DB ready不等于控制解禁，RTO目标不能反向迫使Unknown放行。

## 5. 排期与人员容量

不虚构团队人数、起止日期、设备交期或并行吞吐。T01由具名负责人给每个工作包记录：剩余切片数、后端/前端/QA/设备方投入、乐观/常规/悲观人日、依赖可用日期。由此计算最长依赖路径与资源冲突；开发和测试共同估算，不能用编码完成日期充当交付日期。

按本计划，后段必经T26→T27→T28→T29/T30→T31/T32→T33→T34；上游T21/T23/T24均可能成为关键路径。T17纯算法可与Platform并行；T09/T10、T20/T22、T24/T25可在各自依赖就绪后并行。不同任务同时修改Host装配/迁移组/公开契约时由owner串行集成，禁止同时自由改共同契约。

每次迭代只拉取已ready且容量容纳的切片；QA从切片失败测试开始参与，不在末尾集中“补测”。硬件/供应商证据缺失列blocked及影响任务，不把S/Mock结果算H通过。计划评审时再落具名排期，本轮不创建猜测日期。

## 6. 文件责任与构建约定

- `src\Host\Program.cs`仅装配；`src\Modules\<Owner>\Features\<Slice>\`放具名请求/校验/handler/endpoint；领域状态与持久化按实际复杂度各归owner。不因本计划路径创建空文件/空项目。
- `src\Modules\<Owner>\Contracts\`为公开契约；跨模块引用只依赖契约。`src\Modules\Wcs\Stacker\`和`FourWay\`各存自己的Route/Infrastructure/迁移；只有证明纯无状态共用的算法可提取，不共享ledger服务。
- `clients\ops-web\src\features\`、`clients\simulator\src\features\`、`clients\pda\src\pages\`分别承接具名PAGE。一个PAGE可为面板，不强制64个路由；前端不持有设备/库存事实。
- `tests\Architecture\`、`tests\Modules\`、`tests\Contracts\`、`tests\EndToEnd\`、`tests\Performance\`保存对应测试；[测试计划](2026-10-23-wes-test-execution.md)定义拟议精确入口及执行命令。T02创建实际入口后才可运行，当前不得伪称命令已存在/通过。
- Seven当前测试工程为net8/xUnit v2并使用EF InMemory；新工程遵守.NET10并重新验证包兼容，不照搬InMemory证据，不仅因skill示例而强升xUnit v3。Seven前端已用Playwright且没有组件单测runner；T06可为具体状态机测试引入最小Vitest/Vue Test Utils，不顺便替换既有业务UI栈。

## 7. 每片完成定义与变更控制

- [ ] owner在PR列出Task、SCOPE/REQ、FUNC/PAGE、父DTC/变体/Provider/Adapter/终端与策略版本。
- [ ] 先看到针对预期行为失败的测试；编译失败只证明装配缺失，行为红灯应在可编译基线中显示正确断言失败，不能拿基础设施连不上作预期红灯。
- [ ] 最小实现后相关单元/API/真实DB/契约/组件与页面适用性测试通过；非法/越权/取消/重启/Unknown与副作用计数有断言。
- [ ] 新增契约公开类型/参数/副作用有文档；复杂状态机/事务/Route注释解释why并链接设计，不注释每行赋值。
- [ ] 同PR更新实现思路、数据/事务/错误/恢复、使用/部署/回退说明与总追踪；不只列类名。
- [ ] 真实环境未跑标not-run，依赖缺失标blocked，失败标failed；不得用waived覆盖高风险不变量。
- [ ] 评审确认没有跨owner写、生成式CRUD、敏感日志/截图、本地待写、未证物理参数或新功能扩范围。

## 8. 后续执行边界

三份计划是全产品执行基线；逐工作包签核只授权已确定切片，不自动授权真实设备、生产迁移或推送。开始实施时采用executing-plans按依赖分批推进；如以后明确选择代理并行，再按批准范围使用subagent-driven-development，不默认启动代理池。

原计划自审目标：任务依赖无环、所有65 FUNC/64 PAGE/167 DTC均有开发/测试入口、29 RULE/11 PAR/9 RVT/8 VAL纳入回归、265原TC链路不丢。静态核对不等于自动化通过；后续实际结果按实施记录逐片追加。
