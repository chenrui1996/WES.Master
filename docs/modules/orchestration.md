# 运输编排详细设计

地图页面、字段、功能与问题闭环的逐步骤用例：[Orchestration深化设计](../detailed-design/orchestration.md)。原文仍为业务边界依据，深化文档为评审中、测试未执行。

- 状态：目标设计；Seven 已有 Bus 单进程实现为基线，可靠性/幂等/恢复需增强
- 范围追踪：`SCOPE-BUS-01`、`SCOPE-WMS-06`
- 基线参考：[WMS/WCS 实现说明](../../../Seven.Master/doc/20-WMS与WCS实现说明.md)、[WMS/WCS 包架构](../../../Seven.Master/doc/19-WMS与WCS包.md)
- 测试前缀：`BUS-TC-*`

在途/交接、迟到事实、动作审批和issue初始目标按[WES-RULES-1.0](../detailed-design/05-approved-rules-and-defaults.md)的RULE-08/17/19/24实施；这是设计签核，不代表设备握手/状态新鲜度已验证。

## 1. 责任边界

Orchestration 接受 WMS/扩展业务给出的运输意图，负责包选择、Leg 规划、交接、下发、事件归一、暂停/取消/失败、重试与完成通知。它不拥有库存数量，也不实现单个 WCS 包内部 Route、停车位、提升机或设备报文。

WMS 保持来源/目标/容器/单据引用及账本权威；Orchestration 拥有 Transport Order、Leg、路线规划结果、执行和补偿状态。WCS Pack 拥有其执行任务及 Route 运行状态。跨包统一的是版本化 Leg/执行契约，不是包内对象。

## 2. 运输订单和 Leg 状态机

建议订单状态：`Planning → Planned → Executing → Completed | Failed | Cancelled | Suspended`。Leg 状态：`Pending → Dispatching → Accepted → Running → Completed | Failed | CancelRequested → Cancelled | Suspended`。合法状态转换通过一个应用用例执行并持久化；终态事件重复到达不得重复推进。

每个订单记录唯一幂等键、业务引用、租户、容器/货物引用、起终点、优先级/组、地图/策略版本（如需）和创建原因。每个 Leg 记录序号、PackId、起终点/交接点、依赖 Leg、执行能力、外部关联 ID、尝试次数和错误摘要。

## 3. 计划与包选择

1. 校验起终点、业务引用、容器、仓库/Pack 归属和请求幂等键。
2. 创建逻辑运输订单。若源/目标同址，采用 WMS 明确的无运输完成路径，不生成设备任务。
3. 查找可处理起终点的 Pack；单包直达建立单 Leg。
4. 跨包根据有效的 HandoverLink 构造有向多段 Leg；每一段的起终点必须满足包范围和交接配置。多个候选路径按显式业务优先级/成本选择，平局结果确定。
5. 没有包/交接/有效路径时，记录失败原因和可诊断状态；WMS 只收到明确失败，不提前移动账本。
6. 先持久化订单、Leg、版本/幂等记录，再由可靠 dispatcher 接受首段。不得在数据库提交前产生不可回滚的设备副作用。

Pack selection 不以任意前缀猜测供应商能力。Pack `CanHandle` 是授权候选判断，最终 `AcceptLeg` 再次校验启用状态和 Leg 所属。

## 4. 下发、事件和交接

- Dispatcher 使用持久化状态/租约抢占待下发 Leg，成功后记 Accepted/外部 command ID；超时按“结果未知”恢复：先 Query/去重，不盲目创建第二任务。
- Pack事件包含LegId、Command/MessageId、源generation/seq、业务版本、来源Pack、发生/接收时间。跨包/未知引用隔离，重复去重；过期新控制拒发。可信迟到完成事实持久核验/对账，不笼统丢弃，不覆盖更新遥测；认证过期报文隔离后query可信owner，不能绕过认证。
- Progress 只推进允许的状态并保存最后进度。Completed 必须验证该 Leg 当前可完成，之后只激活唯一下一 Leg；交接点确认是两个包之间唯一所有权转移边界。
- 下一段在前一段达到完成且交接规则满足后才激活。前段失败时后续保持 Pending/被阻塞，并进入失败/恢复流程，不自动发起后段。
- 订单全部 Leg 完成后，经幂等 WMS completion contract 通知库存/单据闭环。通知失败重试且状态可查；不把 WCS 成功等同 WMS 已完成。

交接唯一记录绑定transfer/容器/源目标Pack/口/Leg/版本。目的ReadyToReceive仅为能力/资源证据，不激活下段；源交接完成且目的接受同一版本，才原子推进执行保管权及唯一下一Leg。源已卸货但目的回执丢，保管权Unknown且保留唯一未决handover token/资源保护，先query，不能两包各自派发或重复卸货。物理口的唯一控制owner必须由设备契约证实。

首期WMS最终到达才移账，途中保持来源账面归属但inTransit/不可再分配；地图分栏账面位、执行保管权、观测位置/时间/质量。Orchestration不另建中间库存、不让第二订单再次使用该容器。

## 5. 取消、失败和补偿

- 取消请求首先记录 `CancelRequested`，向正在执行的 Pack 请求取消；只有 Pack 确认终止或人工恢复流程确认安全后才进入 `Cancelled`。
- 已完成 Leg 不回滚为未执行；已激活后续但未执行的 Leg 可撤销。库存预留释放由 WMS 按订单/物理状态决策。
- 设备断联/状态未知时进入 `Suspended/Unknown` 和安全处置，不伪造 Failed/Completed；允许状态查询、受控重试或人工复核。
- 自动重试只针对可证明幂等、未产生物理副作用的阶段；重试限额、退避和终止条件固定并记录。
- 补偿是新的业务事件/运输动作，不删除原执行历史；每次操作写入操作者、原因和关联 ID。

## 6. 并发和一致性

- `(租户, 幂等键)`、订单业务引用/Leg 序号等具备唯一性约束。
- 同一订单任一时刻至多一个需要串行执行的活动 Leg；跨包状态推进采用版本检查/原子更新。
- 消息/回调至少一次；事件消费去重和状态机验证保障逻辑唯一效果。
- Outbox/Inbox 归各自数据 owner，数据库提交与事件写入按同模块事务；不依赖 WCS 内存事件订阅完成关键状态持久化。
- 取消与完成并发时，只接受第一条符合版本和物理状态的合法终态；另一条转入查询/人工处理，不反向覆盖。

## 7. 详细验收用例

| ID | 场景 | 期望 |
|---|---|---|
| BUS-TC-001 | 同 Pack 单 Leg 运输 | 订单/Leg 持久化后才派发；完成只触发一次 WMS 回调 |
| BUS-TC-002 | 两包一个交接点 | 第一个 Leg 完成后才派发第二个；交接位置与方向正确 |
| BUS-TC-003 | 无可用 Pack、无 HandoverLink、多候选交接路径 | 明确失败或按已定义稳定规则选择；无设备副作用 |
| BUS-TC-004 | 同址请求、非法空起终点 | 同址按无运输路径处理；非法请求拒绝且不建残留订单 |
| BUS-TC-005 | 同一业务幂等键并发提交 | 只有一个订单/Leg 集合和一次逻辑派发 |
| BUS-TC-006 | 首次 Accept 成功但 Host 保存/响应失败 | 设计的 Outbox/command ID 可恢复并去重，无孤儿执行任务 |
| BUS-TC-007 | Pack 接受拒绝、超时、结果未知 | 可诊断状态，按 Query/重试策略恢复，不盲目重复下发 |
| BUS-TC-008 | Progress/Completed/Failed 重复或乱序 | 状态机防回退；重复完成不重复激活下一 Leg 或记库存 |
| BUS-TC-009 | 第一 Leg Failed | 后续 Leg 保持 Pending/阻塞，WMS 未误完成 |
| BUS-TC-010 | 跨 Pack 交接点缺失/配置重复/方向错误 | 规划失败并列明原因，不越权调用包 |
| BUS-TC-011 | 取消和 Completed 同时到达 | 只有符合版本/物理状态的一条终态生效，冲突转人工检查 |
| BUS-TC-012 | 已执行一段后取消多段订单 | 已完成事实保留；未执行段停止；预留由 WMS 按事实处理 |
| BUS-TC-013 | WMS 完成回调暂时失败/重复 | 可重试且只移动/扣减一次库存 |
| BUS-TC-014 | 单节点重启/多副本争抢 Outbox/Leg | Lease/唯一约束防双 dispatch；活动状态可恢复 |
| BUS-TC-015 | 外部回调跨租户/未知 Leg/错误 Pack | 拒绝并审计；不推进订单 |
| BUS-TC-016 | SQL Server/PostgreSQL/MySQL 并发事件处理 | 状态版本和唯一约束均成立 |

## 8. WCS 运维协调边界

WCS 运维工作流属于 Orchestration/WCS，而不是 SCADA/Simulator。Orchestration 提供跨包订单/Leg 查询、状态汇总、运维动作申请、审批/幂等关联及结果协调；Stacker/FourWay 对包内模式、联锁、任务状态、设备能力和物理反馈做最终校验与执行。运维 Web 是客户端，不构成独立业务权威。身份授权和不可变审计由 Platform 契约提供。

运维命令以资源（Pack、设备、Leg/Task）为范围，必须验证调用者权限、当前版本/状态、幂等键、操作理由及所需复核。未知设备结果、过期遥测或生效联锁时拒绝推进到成功/完成；跨模块部分成功进入可查询的协调/补偿状态。软件命令不得代替或解除安全 PLC 的物理保护。

### 8.1 2D 地图优先的执行与运维工作台

运维 Web 以 2D 监控地图为默认主工作台，目标是在地图上尽可能显示全部可定位设备、车辆、库位、活动任务、资源预约、告警/异常及数据新鲜度。图层、筛选、聚合/展开和按区域导航用于应对密集设备和重叠对象；未定位或非空间设备必须在关联列表/问题队列中可见，并标明未映射/数据未知，不能因地图上不可见而被当作正常或不存在。

选择设备、任务或告警后，在地图上下文中显示状态、关联单据/Leg、告警原因、最近反馈、责任 Pack 和建议处置。告警确认/派单、任务暂停/恢复/取消、设备启停/模式切换等经授权的常用操作可从该工作台发起。复杂诊断可展开侧栏/详情，但应保留地图定位与当前对象上下文；不要求每个设置表单都挤进地图画布。

问题处理遵循“发现 → 地图定位/关联 → 诊断 → 授权操作或指派 → 回读 WCS/设备结果 → 解决并关闭，或保持未解决并升级”的闭环。命令接受/发送不等同设备动作成功；未收到有效回读、仍有联锁、状态未知或处置失败时，问题不得标记为已解决。所有可见异常均须有来源、时间、当前状态和责任归属；未支持自动处置的情况保留为待人工处理，不能假称系统已解决。

地图只是统一操作入口，不是新的状态或命令权威。SCADA 提供只读空间投影；Notifications 提供告警；Orchestration 汇总跨包任务/问题并协调操作；对应 WCS Pack 对包内任务、设备能力、联锁和物理状态做最终授权校验与执行；DeviceIntegration 负责协议命令/反馈。高风险操作（强制完成、联锁解除、真机命令重发等）须更强权限、理由、影响预览、二次确认及按现场策略双人审批，并记录不可变审计；硬件安全 PLC 保持物理安全最终边界。

以下是该操作工作台的规范性需求：

| Requirement | 必须满足 |
|---|---|
| REQ-OPS-001 | 运维 Web 必须以 2D 地图为默认主工作台，提供分层/筛选/聚合等方式查看全部已知可定位设备、车辆、任务、资源预约和告警。 |
| REQ-OPS-002 | 所有未定位、非空间、Unknown、Stale 或离线资产/问题必须在地图关联列表中可见并标注状态，不得因地图未绘制而被视作不存在或健康。 |
| REQ-OPS-003 | 地图必须支持告警确认/派单、任务暂停/恢复/取消、设备启停/模式切换等获授权的常用操作；命令必须由 owning module 校验权限、版本、联锁、幂等键和状态。 |
| REQ-OPS-004 | 问题必须保留来源、时间、状态、责任归属与处置记录；只有有效设备/WCS 回读和必要对账完成后才可关闭，失败/未知结果须保持未解决并可升级。 |
| REQ-OPS-005 | 强制完成、联锁解除、真机命令重发等高风险操作必须执行增强授权、理由、影响预览、二次确认及适用的双人审批；不得绕过安全 PLC。 |

现场 WCS 运维行为还需满足以下要求：

| Requirement | 必须满足 |
|---|---|
| REQ-SITE-012 | 活动任务期间变更模式或禁用申请点时，必须由 owning WCS Pack 校验；不安全操作应拒绝，或按已定义流程到达安全状态。 |
| REQ-SITE-013 | Force-complete、取消和命令重发必须校验授权、理由、状态及要求的复核；不合规时拒绝，不能伪造完成。 |
| REQ-SITE-014 | 软件急停状态或解除动作不得表述为硬件安全已确认，除非收到权威安全状态回读。 |
| REQ-SITE-015 | 并发运维状态变更必须使用版本/并发校验；过期命令不能覆盖新状态。 |
| REQ-SITE-016 | 重启恢复必须先对账耐久任务与物理状态，再释放占用或重新派发命令。 |

| ID | 场景 | 期望 |
|---|---|---|
| SITE-TC-012 | 有活动任务时切换模式/禁用申请点 | Orchestration 协调请求；目标 Pack 按联锁规则拒绝或安全暂停；记录前后状态和原因 |
| SITE-TC-013 | Force-complete/取消/重发缺理由或越权 | Orchestration 与目标 Pack 均拒绝不合规命令；不伪造 WMS/WCS 完成；结果可审计 |
| SITE-TC-014 | 软件 EStop/解除与硬件 EStop 状态不一致 | 不报告硬件已安全；未收到授权安全状态确认时禁止解除联锁/恢复派发 |
| SITE-TC-015 | 两名运维并发变更控制模式/状态 | 版本冲突被检测；后提交者刷新并重新确认，不覆盖先前变更 |
| SITE-TC-016 | Host/数据库/设备重启且存在活动任务 | Orchestration 与 Pack 对账耐久任务及物理事实后恢复；不清除活跃占用或重复下发 |
| SITE-TC-019 | 大型地图含多类/重叠设备、未定位设备和缺失遥测 | 可通过图层/筛选/聚合浏览全部资产；未定位项进入列表并明确 Unknown/Unmapped/Stale，不被隐藏或视为健康 |
| SITE-TC-020 | 从地图对象发起告警确认、任务暂停/恢复/取消、设备启停/模式切换 | 操作使用 owning module 契约；服务端检查授权、版本、联锁、幂等和状态；地图显示最终回读结果 |
| SITE-TC-021 | 设备/任务异常通过地图处置，命令失败、超时或物理状态未知 | 问题保持待处理/处理中或升级状态并可追踪；仅有效回读和对账后才关闭 |
| SITE-TC-022 | 无定位信息、离线或陈旧遥测时尝试控制设备 | 保留问题可见；禁用不安全操作或明确拒绝，不以缓存状态推断物理事实 |
| SITE-TC-023 | 强制完成、解除联锁、真机命令重发及并发审批 | 执行角色/理由/二次确认/双人策略；未满足时拒绝并审计，绝不绕过安全 PLC |

## 8.2 Operational issue lifecycle

Orchestration owns persistent operational issues that require assignment, escalation, cross-module coordination, or verified resolution. Notifications remains authoritative for alarm/event delivery and alarm acknowledgement/clear state. The issue links to, but does not replace, its source alarm or domain event.

An issue records a stable source type and source occurrence ID, first/last observed time, severity, current issue version, affected asset/location/Pack/task/Leg/alarm/deployment references when known, assignee/team, transition history, action/command references, verification evidence, and correlation IDs. Deduplication uses `(source system, source occurrence ID)`: repeated delivery updates the existing issue's observation history idempotently. A distinct occurrence is not silently merged; explicit reopen requires an actor, reason, and audit entry.

The initial lifecycle is:

```text
Open → Acknowledged → Assigned → Investigating → AwaitingVerification → Resolved
  └──────────────── any unresolved state ─────────────────────────────→ Escalated
Escalated → Assigned or Investigating (after authorized reassignment/recovery)
Resolved → Open (only on new authoritative evidence, with reason and audit)
```

Every transition checks authorization, expected issue version, permitted prior state, and required reason/evidence. Assignment, escalation target, due/SLA policy, actor, timestamp, and correlation are retained in the issue history. Notification acknowledgement or clear does not automatically acknowledge or resolve the issue.

Resolution requires fresh authoritative readback from the owning WCS Pack/device and confirmation of the intended device/task state. If stock, location occupancy, or a WMS document may have changed, the owning WMS contract must reconcile those facts before resolution. A command being sent or accepted is not proof of physical completion. Stale, missing, contradictory, or unknown state keeps the issue in `AwaitingVerification` or `Escalated`. Unlocated, unmapped, stale, and offline issues remain visible in the workbench's associated issue queue.

The following requirements extend the map-first workbench contract:

| Requirement | 必须满足 |
|---|---|
| REQ-OPS-008 | Operational issues must retain source occurrence, provenance, affected-resource links, observation history, and idempotent duplicate-delivery behavior. |
| REQ-OPS-009 | Unresolved issues must remain discoverable, assignable, and escalatable independent of map location or alarm delivery state; every transition is authorized and auditable. |
| REQ-OPS-010 | Resolution requires fresh authoritative WCS/device evidence and applicable WMS reconciliation; recurrence/reopen preserves prior history. |

| ID | 场景 | 期望 |
|---|---|---|
| SITE-TC-024 | 同一告警/事件 occurrence 重复投递 | 只生成一个运维问题；最近观测和事件历史幂等更新 |
| SITE-TC-025 | 告警已确认或清除但问题未解决 | 告警生命周期独立变化；问题保持未解决直到满足关单证据 |
| SITE-TC-026 | 未定位或陈旧问题从地图工作台查看、派单并升级 | 关联问题队列仍可见，标明定位/数据质量状态；指派和升级均校验权限并保留责任变更历史 |
| SITE-TC-027 | 命令已接受但回读缺失、陈旧、失败或矛盾 | 不得关闭问题；保持待验证或升级并保存诊断上下文 |
| SITE-TC-028 | 有效 WCS/设备回读及必要 WMS 对账后再次发生 | 仅证据齐全后解决；复发可按来源 occurrence 重开/新建且保留历史 |

## 9. 评审前置项

明确包选择成本/优先级、HandoverLink 生命周期、Pack 健康不可用的备用策略、最大重试/超时、物理任务“结果未知”的 operator ownership、取消可用设备能力以及 WMS completion 与消息 Outbox 的事务边界。
