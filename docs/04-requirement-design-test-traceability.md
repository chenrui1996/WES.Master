# 需求、设计与测试追踪矩阵

- 状态：项目总追踪规范与初始映射；随详细设计/测试验收持续更新
- 来源：[业务范围与现状矩阵](01-business-scope-and-status.md)

## 实施批次记录

项目发起人本轮明确批准现有全部模块详细设计、REQ-ROUTE-001..027逐条语义及WES-RULES-1.0作为软件基线，并允许在main实施。该批准不是测试passed、现场具名签字或生产许可。REQ-ROUTE-001..027的逐行签核及阻断输入见[就绪台账](implementation/readiness-register.md)；契约见[接口基线](contracts/01-business-and-execution-contracts.md)。当前MySQL先行，其他Provider延后按[ADR-0002](adr/ADR-0002-mysql-first-implementation.md)。

| 切片 | 范围/设计 | 测试/证据状态 |
|---|---|---|
| T01 软件实施基线 | 全部现有PAGE/FUNC及Route语义；不含预留功能扩容 | 文档签核；现场主备/真实环境仍blocked |
| T02 Host首片 | FUNC-SYS-001/004基础、架构§7、DTC-SYS-001/006部分 | 仅健康/边界切片；结果见[实施记录](implementation/01-foundation-host.md)，不将父DTC整体标passed |
| T02 MySQL基础片 | 同一FUNC/DTC的真实Provider基础、ARCH-TC-005首期部分 | [Provider记录](implementation/03-mysql-provider-foundation.md)：仅wes_test探针迁移/事务/约束/tenant/并发/清理，不代表业务owner、最小权限或完整T02验收 |
| T02 HTTP运行时/架构片 | FUNC-SYS-001/004、DTC-SYS-001/006、ARCH-TC-001/002的适用基础部分 | [运行时记录](implementation/04-host-runtime-foundation.md)：真实依赖断开/恢复、关联ID/安全错误与日志、Roslyn已知违规symbol反例；不代签其他owner/完整父DTC |
| T03 会话/策略内部片 | FUNC-PLAT-001、DTC-PLAT-002/003/010及VAL-04的部分场景 | [会话记录](implementation/05-platform-session-core.md)：绝对/idle/access期限、耐久CAS/重放撤销、租户/摘要、登录与新密码纯策略；尚无登录/验证码/JWT/页面和完整链路，不标父DTC整体passed |

## 1. ID 规则

| 类型 | 语法 | 示例 |
|---|---|---|
| 范围项 | `SCOPE-{DOMAIN}-{NN}` | `SCOPE-WMS-02` |
| 需求 | `REQ-{DOMAIN}-{NNN}` | `REQ-WMS-014` |
| 测试 | `{DOMAIN}-TC-{NNN}` | `WMS-TC-005` |
| 页面/面板 | `PAGE-{DOMAIN}-{NNN}` | `PAGE-BUS-001` |
| 功能用例 | `FUNC-{DOMAIN}-{NNN}` | `FUNC-WMS-002` |
| 详细测试记录 | `DTC-{DOMAIN}-{NNN}` | `DTC-WMS-003` |
| ADR | `ADR-{NNNN}` | `ADR-0003` |
| 缺陷/风险 | 团队 issue ID + owner | `WES-123 / Route lead` |

一条 REQ 用可验证的 shall/必须语义描述一个行为或不变量。每个 SCOPE 至少一条 REQ；一条 REQ 至少一个正向测试和适用的拒绝/并发/故障测试。不可测试的安全/活性需求须明确假设、分析证据与独立评审责任人。

## 2. 状态

- 需求：`proposed / approved / implemented / verified / deferred / rejected`
- 测试：`designed / automated / passed / failed / blocked / waived`
- 范围：沿用 [scope matrix](01-business-scope-and-status.md) 的 `保留 / 补齐/复核 / 预留 / 移除 / 待核`
- `waived` 必须有决策人、理由、风险接受和到期/复审日期，不能仅为通过 CI。

## 3. 初始范围到设计/测试映射

需求 ID 为本项目建立的追踪键。以下行表示详细设计覆盖和测试目录入口，不表示相关功能已实现或测试已通过。

| Scope ID | REQ ID | 详细设计 | 测试目录 | 状态/备注 |
|---|---|---|---|---|
| SCOPE-PLAT-01 | REQ-PLAT-001..003 | [Platform 与安全](modules/platform-and-security.md) §2 | PLAT-TC-001..004 | 设计中 |
| SCOPE-PLAT-02 | REQ-PLAT-004 | [Platform 与安全](modules/platform-and-security.md) §3.1 | PLAT-TC-005 | 设计中 |
| SCOPE-PLAT-03 | REQ-PLAT-005..006 | [Platform 与安全](modules/platform-and-security.md) §3.2-3.3 | PLAT-TC-006..008 | 租户策略待 ADR |
| SCOPE-PLAT-04 | REQ-PLAT-007..009 | [Platform 与安全](modules/platform-and-security.md) §4 | PLAT-TC-011, 015..018 | 配置/字典/国际化/文件 |
| SCOPE-PLAT-05 | REQ-PLAT-010..012 | [Platform 与安全](modules/platform-and-security.md) §5 | PLAT-TC-012..013, 019..020 | 审计/日志/健康/指标 |
| SCOPE-PLAT-06 | REQ-PLAT-013..014 | [通知/定时任务](modules/notifications-and-jobs.md) §4 | NTF-TC-013..014 | HotStore 归属需核验 |
| SCOPE-WF-01 | REQ-WF-001..005 | [Workflow 与表单](modules/workflow-and-forms.md) §2-3 | WF-TC-001..011 | 条件/并行能力待确认 |
| SCOPE-WF-02 | REQ-WF-006..008 | [Workflow 与表单](modules/workflow-and-forms.md) §4 | WF-TC-012..014 | 运行时，不生成源码 |
| SCOPE-WF-03 | REQ-NTF-001..003 | [通知/定时任务](modules/notifications-and-jobs.md) §2 | NTF-TC-006..008 | 集群策略待确认 |
| SCOPE-WF-04 | REQ-NTF-004..008 | [通知/定时任务](modules/notifications-and-jobs.md) §1 | NTF-TC-001..005, 015 | Broker/保留期待确认 |
| SCOPE-WF-05 | REQ-NTF-009..014 | [通知/定时任务](modules/notifications-and-jobs.md) §3, 5 | NTF-TC-009..012, 015 | 新闻闭环待核 |
| SCOPE-WMS-01 | REQ-WMS-001..004 | [WMS 账本](modules/wms-and-ledger.md) §2 | WMS-TC-002, 011 | 设计中 |
| SCOPE-WMS-02 | REQ-WMS-005..011 | [WMS 账本](modules/wms-and-ledger.md) §1, 3-6 | WMS-TC-001, 005, 010..017 | 账本权威 |
| SCOPE-WMS-03 | REQ-WMS-012..014 | [WMS 账本](modules/wms-and-ledger.md) §3 | WMS-TC-001..004, 015 | 运输回写 |
| SCOPE-WMS-04 | REQ-WMS-015..019 | [WMS 账本](modules/wms-and-ledger.md) §4 | WMS-TC-005..010, 015 | 保留既有拣选并补 E2E |
| SCOPE-WMS-05 | REQ-WMS-020..023 | [WMS 账本](modules/wms-and-ledger.md) §6 | WMS-TC-012..013 | 盘点并发待核 |
| SCOPE-WMS-06 | REQ-WMS-024..026 | [WMS 账本](modules/wms-and-ledger.md) §5, 7 | WMS-TC-014, 016, 018..020 | TransferOrder 与扩展不重复账本 |
| SCOPE-PDA-01 | REQ-PDA-001..006 | [PDA](modules/pda.md) §3-4 | PDA-TC-001..007, 013..015 | 现有页面/API待 E2E |
| SCOPE-PDA-02 | REQ-PDA-007..010 | [PDA](modules/pda.md) §2, 4.5 | PDA-TC-008..012 | 离线只读已确认 |
| SCOPE-BUS-01 | REQ-BUS-001..012 | [Orchestration](modules/orchestration.md) §2-6 | BUS-TC-001..016 | 幂等/恢复需实现 |
| SCOPE-WCS-01 | REQ-STK-001..012 | [Stacker](modules/wcs-stacker.md) §2-5 | STK-TC-001..015 | Route 见独立矩阵 |
| SCOPE-WCS-02 | REQ-FW-001..014 | [FourWay](modules/wcs-fourway.md) §2-6 | FW-TC-001..018 | Route 见独立矩阵 |
| SCOPE-WCS-03 | REQ-WCS-RES-001 | [范围矩阵](01-business-scope-and-status.md) | 无（预留） | 需启动独立需求/ADR |
| SCOPE-WCS-04 | REQ-ADP-001..009 | [外部 WCS/设备适配器](modules/external-wcs-and-device-adapters.md) §2 | ADP-TC-001..007 | 当前取消/查询不完整 |
| SCOPE-DEV-01 | REQ-DEV-001..010 | [DeviceComm](modules/device-communication.md) §2-5 | DEV-TC-001..015 | Phase H 现状待确认 |
| SCOPE-DEV-02 | REQ-SITE-001..004 | [环境搭建、仿真与 SCADA](modules/environment-simulation-and-scada.md) §1, 3, 5 | SITE-TC-001..003 | SCADA 只读地图投影；地图优先运维 UI 见 Orchestration |
| SCOPE-DEV-03 | REQ-SITE-005..011 | [环境搭建、仿真与 SCADA](modules/environment-simulation-and-scada.md) §2, 5 | SITE-TC-004..011, 029..033 | Deploy、仿真、真机 Promote 与地图版本协调 |
| SCOPE-OPS-01 | REQ-SITE-012..016, REQ-OPS-001..005, REQ-OPS-008..010 | [Orchestration](modules/orchestration.md) §8.1-8.2、[Stacker WCS](modules/wcs-stacker.md) §5、[FourWay WCS](modules/wcs-fourway.md) §6 | SITE-TC-012..016, 019..028 | 2D 地图优先；问题由 Orchestration 管理，跨包协调在 Orchestration，包级校验在 WCS |
| SCOPE-OPS-02 | REQ-OPS-006..007 | [架构](02-system-architecture.md) §7.1、[验收矩阵](acceptance/01-cross-module-and-provider-matrix.md) | SITE-TC-017..018 | 隔离恢复和 SingleWriter/fencing 属于系统与 WCS 验收 |
| SCOPE-DB-01 | REQ-ARCH-001..004 | [架构](02-system-architecture.md) §5 | ARCH-TC-005, WMS-TC-017 | 三 provider |
| SCOPE-EXT-01 | REQ-ARCH-005..007 | [架构](02-system-architecture.md) §3, 4 | ARCH-TC-001..004 | 保留扩展意图 |
| SCOPE-REMOVE-01 | REQ-GOV-001 | [工程治理](03-engineering-governance.md) §1, 7 | ARCH-TC-001 + review checklist | 源码生成移除 |

### 3.1 Site and operations requirement-level traceability

每条要求均须有明确设计 owner 和测试入口。此表记录的是设计覆盖，不代表功能已实现或用例已执行。

| Requirement | Design owner | Test ID(s) | Status |
|---|---|---|---|
| REQ-SITE-001 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §1, 3, 5 | SITE-TC-001 | designed |
| REQ-SITE-002 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §3, 5 | SITE-TC-002 | designed |
| REQ-SITE-003 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §3, 5 | SITE-TC-003 | designed |
| REQ-SITE-004 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §1, 3, 5 | SITE-TC-001, SITE-TC-003 | designed |
| REQ-SITE-005 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §2.1, 5 | SITE-TC-004 | designed |
| REQ-SITE-006 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §2.1, 5 | SITE-TC-005, SITE-TC-029..032 | designed |
| REQ-SITE-007 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §2.1, 5 | SITE-TC-006, SITE-TC-033 | designed |
| REQ-SITE-008 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §2.2, 5 | SITE-TC-007 | designed |
| REQ-SITE-009 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §2.2, 5 | SITE-TC-008 | designed |
| REQ-SITE-010 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §2.3, 5 | SITE-TC-009, SITE-TC-010 | designed |
| REQ-SITE-011 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §2.3, 5 | SITE-TC-011 | designed |
| REQ-SITE-012 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-012 | designed |
| REQ-SITE-013 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-013 | designed |
| REQ-SITE-014 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-014 | designed |
| REQ-SITE-015 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-015 | designed |
| REQ-SITE-016 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-016 | designed |
| REQ-OPS-001 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-019 | designed |
| REQ-OPS-002 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-019, SITE-TC-022, SITE-TC-026 | designed |
| REQ-OPS-003 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-020 | designed |
| REQ-OPS-004 | [Orchestration](modules/orchestration.md) §8.1, 8.2 | SITE-TC-021, SITE-TC-025, SITE-TC-027, SITE-TC-028 | designed |
| REQ-OPS-005 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-023 | designed |
| REQ-OPS-006 | [架构](02-system-architecture.md) §7.1、[验收矩阵](acceptance/01-cross-module-and-provider-matrix.md) §1 | SITE-TC-017 | designed |
| REQ-OPS-007 | [架构](02-system-architecture.md) §7.1、[验收矩阵](acceptance/01-cross-module-and-provider-matrix.md) §1 | SITE-TC-018 | designed |
| REQ-OPS-008 | [Orchestration](modules/orchestration.md) §8.2 | SITE-TC-024 | designed |
| REQ-OPS-009 | [Orchestration](modules/orchestration.md) §8.2 | SITE-TC-026 | designed |
| REQ-OPS-010 | [Orchestration](modules/orchestration.md) §8.2 | SITE-TC-025, SITE-TC-027, SITE-TC-028 | designed |

## 4. Route 追踪矩阵

| Route requirement | 设计章节 | 测试目录 |
|---|---|---|
| REQ-ROUTE-001..004 地图结构/版本/错误 fail-closed | [目标设计](route/02-target-routing-and-scheduling.md) §2 | ROUTE-TC-001..007 |
| REQ-ROUTE-005..008 车辆能力与确定性候选路线 | 同上 §3-4 | ROUTE-TC-008..013 |
| REQ-ROUTE-009..014 冲突资源/时间窗/原子预约 | 同上 §2, 5 | ROUTE-TC-014..022 |
| REQ-ROUTE-015..019 反馈/在线目标/重规划/恢复 | 同上 §6-7 | ROUTE-TC-023..030 |
| REQ-ROUTE-020..024 活性/死锁/公平/设备差异 | 同上 §6, 8 | ROUTE-TC-031..039 |
| REQ-ROUTE-025..027 基准/现场验收/安全边界 | [测试目录](route/03-route-test-catalog.md) §1, 8 | ROUTE-TC-001..039 + SITE-TC-007, 017 |
| REQ-ROUTE-028 持久预约账本是 grant 权威 | [目标设计](route/02-target-routing-and-scheduling.md) §5.1、[ADR-0001](adr/ADR-0001-route-reservation-authority.md) | ROUTE-TC-040, 043..045 |
| REQ-ROUTE-029 单写者 fencing、接管对账和未知占用保护 | 同上 | ROUTE-TC-041..042, 046 |

## 5. 新功能追踪模板

新增范围前先分配 SCOPE/REQ ID，然后在此表添加一行：

| Scope ID | REQ ID | Requirement (shall) | Owner | Design section | Test ID(s) | Status | Evidence/ADR |
|---|---|---|---|---|---|---|---|
| SCOPE-XXX-01 | REQ-XXX-001 | 系统必须…… | 模块/负责人 | `docs/modules/... §...` | `XXX-TC-001` | proposed | issue/ADR link |

测试 case 应在模块设计中的对应详细章节描述输入、前置状态、步骤、持久化/外部副作用、预期错误和 cleanup。自动化后回填 runner、provider/设备环境、结果日期和报告链接。

## 6. 变更规则

### 页面/功能深化追踪入口

[详细设计覆盖索引](detailed-design/README.md)逐包关联SCOPE、PAGE、FUNC、DTC与本矩阵既有REQ/TC。原TC不改号；共用UI测试与跨模块DTC独立保留。模块数字简写仅限同域，不能跨域推断ID。设计映射、规范性需求批准和测试执行是不同状态。

Legrand/GitHub密集页面对照补充见[交互合同与评审缺口](detailed-design/03-dense-page-interaction-contract.md)：DTC-UI-013..032关联全部适用PAGE的列配置/个人视图/关联/筛选；13个模块各增一条关联交互DTC，关联原FUNC和TC。逐页矩阵以本域PAGE数字引用，不重复定义ID。个人偏好仅本机用户/租户/环境隔离，不增加团队共享、离线写入或新业务状态转换。

2026-10-23项目发起人批准[WES-RULES-1.0](detailed-design/05-approved-rules-and-defaults.md)：REV-01..04的RULE-07/08/13/20已回写WMS/Orchestration/部署/Route及ADR范围澄清；其他RULE-01..29按B业务/P初始参数/H缺现场证据阻断分类。RVT-01..09和VAL-01..08为既有正式DTC必测变体，数量仍64 PAGE/65 FUNC/167 DTC，不新建第二套需求号；每项回归入口见规则基线§5。批准不是逐条REQ/Provider/设备或性能验收，原TC映射仍需证据。

后续Task映射见[开发/测试总计划](superpowers/plans/2026-10-23-wes-development-and-testing.md)及[测试覆盖§3](superpowers/plans/2026-10-23-wes-test-execution.md)：T01..T34为工程工作包编号，不是新增业务REQ或测试结果。追踪链扩为SCOPE/REQ→FUNC/PAGE→Task→DTC/variant→实际RunId/证据；现阶段所有开发工作包及执行结果仍未开始。

Route旧范围键001..027已给出[逐条语义候选与详细测试](detailed-design/route.md)，待负责人签核，不把候选等同已批准要求；028..029继续以ADR-0001为权威。

每次更改业务行为、状态/权限、API/event、provider、Route 或安全边界时，同 PR 更新本矩阵、范围矩阵和对应设计/测试。移除 requirement 前须记为 deferred/rejected，并由 owner 说明影响；测试变更不应让未满足的 requirement 消失。
