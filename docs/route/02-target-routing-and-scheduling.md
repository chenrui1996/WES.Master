# Route 目标规划与调度架构

- 状态：目标架构；现场参数与安全假设待现场输入验证
- 依据：[现状与研究适用性](01-as-is-and-research-review.md)、[启动设计](../00-project-startup-design.md)
- 优先级：安全约束 → 状态可信度/活性 → 公平和可恢复 → 吞吐/路径成本

## 1. 设计边界

Route Engine 仅在拥有并能观测的 WCS 包内规划。Stacker、FourWay 各自有 Route Engine、设备状态模型和资源映射；共享接口、版本管理、资源预约骨架、验证器和测试工具，不共享未经证明等价的拓扑或设备工艺。External WCS 不进入本图。

逻辑流水线：

```text
地图导入/编辑
  → Map Validator
  → immutable MapVersion / resource index
  → Vehicle State + freshness validation
  → Candidate Route Planner (per vehicle)
  → Conflict/resource reservation planner (fleet)
  → Atomic Reservation Grant
  → Command Dispatcher
  → sequenced device feedback
  → release/advance/replan/recovery
  → metrics + operator-visible decision trace
```

候选路径算法（Dijkstra、A* 或时间依赖版本）不得决定安全授予；路线成本与资源冲突判定是分离步骤。算法选型需通过现场样例基准，不在设计阶段仅凭名称选型。

## 2. 地图与资源模型

### 2.1 版本化地图

每个不可变 MapVersion 包含：包/层/坐标系、节点、可行驶有向边、长度/基础代价、方向/容量/速度范围、设备能力约束、资源冲突组、合法等待/停车点、交接/提升机入口、禁行区、定位/几何误差和版本元数据。

校验必须拒绝重复 ID、缺失端点、非法权重/容量、负/非有限数、孤立设备点、错误层/Pack、未定义冲突区引用和无法映射到设备指令的路径。部署后版本不可变；任务记录固定 MapVersionId。迁图需明确活动任务继续旧图、暂停重规划或迁移对账策略。

### 2.2 资源

将可冲突区域建模为独立资源：Node occupancy、DirectedEdge/undirected corridor、Intersection/conflict zone、narrow aisle、parking position、hoist/hoist port、设备专用执行段。多个边可映射到相同 conflict set；不能假设“边 ID 相同”才会冲突。

资源参数要表明容量、可否对向共享、是否可排队、进入/退出条件、安全缓冲、释放证据和设备约束。资源容量与时间模型需由真实几何/控制器验证。

## 3. 车辆状态与任务意图

每个车辆快照至少包含 VehicleId、Pack/MapVersion、位置/边段、方向、载荷/尺寸/能力、当前命令/任务、当前占用、模式/故障/联锁状态、LastObservedAt、sequence 和来源。位置只有在可信度/新鲜度通过配置门限后可用于规划。状态冲突或版本倒退必须显示 Unknown/Stale 并阻止新冲突预约。

在线目标请求包含 GoalId、Leg/Task、起点/目标、容器/载荷、时间/优先级、租户、取消/deadline、允许等待/路段和幂等键。目标接收先通过可达性、设备能力、安全资源、状态可信度和当前活跃约束验证，再形成候选路线。

## 4. 候选路线规划

- 路径边成本只表达优化偏好，例如距离、预估行驶时间、能耗、作业代价、已知拥堵；成本不得让违规资源路径变得“可接受”。
- 权重必须有限且非负，地图非法数据拒绝，不静默裁剪成零。
- 代价相同采用稳定 tie-break（map version、route ID、node ID 等固定规则），重放可得到同一路径。
- 过滤车辆不支持的方向/提升/转向/载荷/区域限制；路径包含完整资源占用序列。
- 若无候选可行路径，返回 `NoPath/Blocked/StaleState/Unsupported` 等明确原因；禁止默认把起终点当单段直接下发。
- 路线计划记录 planner version、输入状态版本、地图版本、成本分解、候选数量和拒绝原因，支持离线复现。

## 5. 时间窗预约与原子授权

候选路线转为资源时间窗请求 `(resourceId, [enter, exit), owner, mapVersion, vehicle, task)`。进入/退出时长来自经验证的几何/速度/通信/执行模型，使用统一时间基准并留出安全缓冲。

### 5.1 Reservation authority（ADR-0001）

预约权威按 [ADR-0001](../adr/ADR-0001-route-reservation-authority.md) 执行：每个 WCS Pack 使用持久数据库保存 writer lease/epoch 和预约账本；同一 Pack 仅一个有效写者；HotStore/Redis 只作派生索引/缓存。

| Requirement | 必须满足 | 测试 |
|---|---|---|
| REQ-ROUTE-028 | Pack 必须在持久账本中原子记录并验证资源预约后，才能派发依赖该预约的设备命令；HotStore 不得作为授权依据。 | ROUTE-TC-040, ROUTE-TC-043..045 |
| REQ-ROUTE-029 | 预约写入和新的命令派发必须校验单调递增的当前 writer epoch；过期 epoch 被拒绝，故障切换先对账，物理占用未知时不得仅按时间释放。 | ROUTE-TC-041..042, ROUTE-TC-046 |

1. 每个 Pack 的 writer lease 与 epoch 保存在耐久数据库。获取/接管 lease 必须在同一数据库一致性边界内原子增加 epoch；数据库不可用或有效 epoch 未确认时，控制写入 fail closed。
2. 单个 route grant 在一个数据库事务中校验所有资源容量、冲突关系、已有授权窗口、锁/联锁和 owner state，并持久化整条路径的 reservation token。资源按稳定顺序处理；有任一资源不满足时，事务不留下有效的部分 grant。
3. 成功后返回唯一 reservation token、writer epoch、MapVersion 和命令授权窗口。重复 grant 对同一 owner/request 幂等；释放只允许匹配 owner 或受控 recovery actor。
4. 每个命令 envelope 携带 writer epoch、reservation token、task/vehicle、MapVersion 和 command ID。Dispatcher 在耐久状态转入 dispatching 前验证 epoch 与 grant。Lease 接管之后，旧 epoch 不能建立新 grant 或取得新的派发授权。
5. 接管期间已经跨越 Adapter 边界或状态不确定的命令，不能假定未执行。新 writer 必须查询/对账命令和物理状态；在确定前保持相关资源受保护并阻止冲突派发。
6. 时间窗或 writer lease 过期不能单凭时钟自动释放处于物理未知状态的资源；先以权威设备反馈/车辆位置核实，过期 lease 触发 reconciliation。
7. 进程重启恢复预约来自耐久状态；HotStore 可加速重建，但不作为唯一授权真相。SQL Server、PostgreSQL、MySQL 必须分别验证 epoch takeover、唯一约束、事务隔离、竞争 grant 和崩溃恢复。
8. clock drift、设备时钟、控制器响应延迟未量测时，时间窗只能作为候选调度工具，不构成安全证明。

## 6. 等待、活性、在线目标和公平

- 等待图节点代表任务/车辆/约束资源；边表示释放条件依赖。定义等待边的添加/删除时机，区分暂时排队与真正环。
- 候选采用 constraint-level cycle detection 之前，先证明 WES 的 resource/precedence 模型与算法前提一致；算法漏报/误报都要有反例测试。
- 检测环时只可执行已设计的安全恢复：换路、撤销尚未进入物理阶段的预约、让车退到安全停车点、调度其他独立任务或升级给运维。不得自动让车“反向退让”而不验证其安全路径。
- 优先级采用有限 aging/公平策略；高优先级不能无限饿死低优先级，低优先级也不能破坏安全硬约束。
- 在线目标在当前路段/不可抢占设备段完成或到安全停车点后再改道；变更保留旧路线/预约版本和取消确认。
- 活性目标要写明前提：有可行路线、可达安全停靠点、状态/通讯持续更新、资源最终释放、设备可执行且没有永久故障。任何前提不成立都不能声称 bounded completion。

## 7. 命令/反馈状态机与恢复

建议路径执行状态：`Planned → Reserving → Reserved → Dispatched → Running → SegmentCompleted → Completed`，旁路为 `Waiting/Blocked/Unknown/CancelRequested/Failed/RecoveryRequired`。状态转换由命令 ID、反馈 sequence、MapVersion、车辆位置、reservation token 联合校验。

- 下发前确保资源已授予；设备确认/telemetry 才推进当前边/节点。
- 重复反馈幂等，乱序/错车辆/错 map/task 反馈隔离。
- 通信超时进入 Unknown；查询设备状态/传感器对账前不释放物理资源、不重复下发非幂等命令。
- Cancel 先申请中止/停在可验证位置，收到确认/定位后释放尚未占用的后续窗口。
- Scheduler 重启后恢复 task/lease/feedback offset，查询设备当前状态，重建 occupancy；不能仅重放所有“未完成”命令。
- 故障手动恢复需 reason/actor/前后状态/设备确认和审计；恢复动作优先保障物理安全。

## 8. Stacker / FourWay 专属约束

| 关注点 | Stacker | FourWay |
|---|---|---|
| 拓扑 | 巷道、执行堆段、单双深、挡路位、设备点码 | 层内多车网、方向/转向能力、停车位 |
| 设备工艺 | `ExeStackCode` 段推进、双深移库 | Shuttle 路径、停车预约、同层提升机三阶段 |
| 资源模型 | 堆段/通道/货位组/设备专属区 | edge/node/conflict zone/parking/hoist port |
| 失败恢复 | 浅深位状态、卡位/挡路、设备堆段确认 | 等待环/停车回收/提升机位置未知/多车状态同步 |
| 统一内容 | map validation contract、版本、预约语义/测试 harness、command/feedback ID | 不共用实际图/代价/几何/转向规则 |

## 9. 可观测与配置门禁

每次 route 选择记录 MapVersion、起终点、车辆状态新鲜度、候选路径/拒绝原因、代价分项、预约冲突资源/时间窗、等待图变更、重规划次数和最终命令/反馈 ID。高基数详情放受控日志/trace，不用车辆/任务 ID 做 metric label。

上线前需确认地图来源/版本、车辆最大数、任务率、遥测 freshness、定位误差、通信/时钟误差、速度/制动、占用区几何、响应时间、安全 PLC 权责、停车/回退能力及恢复值班流程。数值来源有设备/现场证据和 owner。

## 10. 不变量和可声明性质

可测试软件不变量（在输入可信且 reservation authority 正确的条件下）：

- 一资源时间窗内 grant 数不超过经过确认的容量/冲突规则。
- 只有持有有效 reservation 的命令才能进入对应资源。
- 当前物理位置未知/过期时不新增需要它的冲突预约。
- map/task/command/feedback 版本不匹配不推进执行状态。
- 同一逻辑命令最多产生一次不可逆业务副作用。
- 释放只影响 owner 自有预约；物理占用需有明确释放证据。

以上不是物理无碰撞证明。安全和活性分析必须将现实运动、误差、网络和硬件边界纳入独立评审。
