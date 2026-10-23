# Route 现状与研究资料适用性

- 状态：Seven 源码与仓库内论文的证据盘点；非生产安全结论
- 范围：所有自研 WCS 包的寻路、交通预约、活性和执行反馈；不将外部 WCS 路网并入自研模型
- 关联：[`Stacker WCS`](../modules/wcs-stacker.md)、[`FourWay WCS`](../modules/wcs-fourway.md)、[Route 目标架构](02-target-routing-and-scheduling.md)、[Route 测试目录](03-route-test-catalog.md)

## 1. 文件清单与责任

### 1.1 Stacker

| 文件 | 当前职责 |
|---|---|
| [StackerRouter.cs](../../../Seven.Master/Seven.Net8/Seven.Infrastructure/Wcs/Packs/Stacker/StackerRouter.cs) | 加权 Dijkstra；过滤满容量边；边权重与占用成本形成代价 |
| [StackerPathDispatcher.cs](../../../Seven.Master/Seven.Net8/Seven.Infrastructure/Wcs/Packs/Stacker/StackerPathDispatcher.cs) | 加载启用的 Route/RouteFlow，求路、按 ExeStackCode 合并执行段、持久化流量并派发首段 |
| [StackerDepthGuard.cs](../../../Seven.Master/Seven.Net8/Seven.Infrastructure/Wcs/Packs/Stacker/StackerDepthGuard.cs) | 深位取货前检测浅位阻挡并请求移库 |
| [StackerDoubleDeepRules.cs](../../../Seven.Master/Seven.Net8/Seven.Infrastructure/Wcs/Packs/Stacker/StackerDoubleDeepRules.cs) | 双深货位可达/空位规则 |
| [StackerDestinationService.cs](../../../Seven.Master/Seven.Net8/Seven.Infrastructure/Wcs/Packs/Stacker/StackerDestinationService.cs) | 地点申请与段反馈推进 |
| [StackerSchedulerHostedService.cs](../../../Seven.Master/Seven.Net8/Seven.Infrastructure/Wcs/Packs/Stacker/StackerSchedulerHostedService.cs) | 事件订阅与后台扫描 |
| [StackerRouterTests.cs](../../../Seven.Master/Seven.Net8/Seven.Tests/Wcs/StackerRouterTests.cs) | 测试低权重路线、满容量跳边、同点路径 |
| [StackerPathDispatcherTests.cs](../../../Seven.Master/Seven.Net8/Seven.Tests/Wcs/StackerPathDispatcherTests.cs) | 测试路线拆段/首段派发/完成推进等 |

现状约束：

- `StackerRouter` 把负权重裁剪为 0，容量阈值把小于 1 的容量按 1 处理；这会掩盖图数据错误，不是可接受的地图校验策略。
- 使用 `Occupancy * 0.01` 将边占用量折成路径成本；该常数不代表行驶时间、冲突区或未来占用。
- `PriorityQueue` 仅以 double cost 入队；相同成本的路径 tie-break 规则不保证由业务显式确定。
- `StackerPathDispatcher` 在没有 Route 或没有找到路径时会下发单段起终点；这是兼容行为，不是安全可达证明。
- RouteFlow 作为当前流量占用证据，按设备段关联；重规划/失败/取消时必须确认所有 Flow 与任务一致清理。

### 1.2 FourWay

| 文件 | 当前职责 |
|---|---|
| [FourWayRouter.cs](../../../Seven.Master/Seven.Net8/Seven.Infrastructure/Wcs/Packs/FourWay/FourWayRouter.cs) | 纯函数加权 Dijkstra，返回节点路径 |
| [FourWayPathDispatcher.cs](../../../Seven.Master/Seven.Net8/Seven.Infrastructure/Wcs/Packs/FourWay/FourWayPathDispatcher.cs) | 解析地图版本、持久化路径节点、尝试预约首边、按反馈推进/重试 |
| [FourWayTrafficGuard.cs](../../../Seven.Master/Seven.Net8/Seven.Infrastructure/Wcs/Packs/FourWay/FourWayTrafficGuard.cs) | 当前最小单边占用；HotStore `fw:flow:` 或进程内 fallback |
| [FourWayHoistOrchestrator.cs](../../../Seven.Master/Seven.Net8/Seven.Infrastructure/Wcs/Packs/FourWay/FourWayHoistOrchestrator.cs) | 跨层三个阶段与同提升口执行队列 |
| [FourWaySchedulerHostedService.cs](../../../Seven.Master/Seven.Net8/Seven.Infrastructure/Wcs/Packs/FourWay/FourWaySchedulerHostedService.cs) | 周期性重试 Routing、唤醒停车和提升机挂起任务 |
| [FourWayPathDispatcherTests.cs](../../../Seven.Master/Seven.Net8/Seven.Tests/Wcs/FourWayPathDispatcherTests.cs) | 路径、首边 grant/释放/后续 grant、Routing 重试、取消释放等 |
| [FourWayParkingTests.cs](../../../Seven.Master/Seven.Net8/Seven.Tests/Wcs/FourWayParkingTests.cs) | 车辆停车预约和恢复 |
| [FourWayHoistE2ETests.cs](../../../Seven.Master/Seven.Net8/Seven.Tests/Wcs/FourWayHoistE2ETests.cs) | 跨层阶段、单口排队/唤醒和 WMS 回写 |

现状约束：

- `FourWayRouter` 将负权重裁剪为 0；图模型只含 from/to/weight，未包含冲突区几何、节点占用、转向/车辆能力和占用时间。
- `FourWayPathDispatcher` 选图按显式 ID、LayerCode、Active 再退到首条记录；映射缺失、无边或无路径时会回退单段派发。首条记录回退和单段 fallback 都应由 WES 设计替换为显式验证/策略。
- 当前 `FourWayTrafficGuard` 主要按 edgeId 持有单个 owner；若对向路线用不同 edge ID 表示，当前模型不能据此证明冲突已被阻止。节点、交叉口、狭窄区和时间窗也未在此最小模型中表达。
- HotStore 模式下另有进程内 owner→edge 索引以释放不可枚举的键；进程重启/多写者时，索引与 durable 预约事实的一致性需专门设计。fallback 是进程内字典，不是跨实例协调。
- `Routing` 的周期扫描可重试未获得边的任务；可重试派发必须依赖 command ID 和设备状态核查，不能仅依据 DB 状态判断设备未执行。
- 提升机停车/执行队列是独立资源约束，不能仅靠最短路规避。

### 1.3 外部边界

外部 WCS 的路网/交通算法由供应商系统负责。WES 只向其公开 Orchestration Leg/设备语义适配契约；不得为方便而将供应商路线偷偷拼接到 Stacker/FourWay 图中。

## 2. 现有验证范围

Seven 的 `StackerRouterTests` 当前为小型路径选择/同点测试；`FourWayPathDispatcherTests` 覆盖多段、单段 fallback、首边阻塞后重试与流量释放；FourWay E2E 覆盖跨层和提升机队列。此证据说明既有路径，不代表碰撞几何、安全性、死锁活性、吞吐或大图性能已被证明。

当前尚未从现有简单测试证明的重点包括：地图坏数据 fail-closed、对向边不同 ID 冲突、交叉/共享冲突区、多个时间窗重叠、遥测陈旧、重启后预约恢复、多写者原子性、优先级饥饿界限、在线目标变更和可证明的活性条件。

## 3. 仓库研究论文

### 3.1 在线目标发布与安全/活性协调

[On Provably Safe and Live Multirobot Coordination With Online Goal Posting](../../../Route/On_Provably_Safe_and_Live_Multirobot_Coordination_With_Online_Goal_Posting_0546.pdf)

适用思想：将机器人目标/任务发布纳入协调协议；以优先关系/约束来避免冲突，并对安全与活性条件分别推理。对 WES 的价值在于提醒：动态到达任务不能只按局部最短路随时下发，目标接受/顺序应与交通约束一起验证。

迁移前提：其环境模型、运动/占用定义、通信/状态观测、任务到达与等待/停止能力必须同现场对照。只有在 WES 地图资源、车辆运动和反馈语义能满足论文假设时，才能讨论相应性质；不能直接引用论文结论声称真实设备绝对无碰撞或必然完成。

### 3.2 约束层环检测

[Constraint-Level Cycle Detection for Efficient Liveness Certification in Online Multirobot Coordination](../../../Route/constraint_graph_intro_positioning.pdf)

适用思想：死锁/等待环可以在约束层建模，而不总是要求展开所有低层状态；论文给出结构化检测方法和 `O(M log M)` 量级分析。可作为 WES 等待/前序约束图的候选研究方向。

迁移前提：等待边、资源容量、机器人可释放动作、任务优先关系必须与实际预约/设备行为同构；若路线会动态重规划、存在非抢占设备段/提升机、未知位置或外部控制器，简化的约束图可能漏掉真实死锁。复杂度结论只适用于论文定义的数据结构和输入假设。

## 4. 研究结果分级

| 内容 | 可纳入设计的程度 | 尚需证据 |
|---|---|---|
| 目标发布/协调与最短路分离 | 作为架构原则 | 现场任务到达/优先级策略 |
| 优先关系与约束图 | 可作为 candidate design | 把资源与等待关系映射到真实设备动作 |
| 约束层环检测 | 研究候选，不是现成证明 | 对 WES 图模型给出健全性证明与独立测试 |
| `O(M log M)` 性能说法 | 仅记录论文的模型内复杂度 | M 定义、构造开销和现场最大约束数基准 |
| 无碰撞/活性保证 | 不可直接宣称 | 运动学、定位精度、时钟、通信和安全硬件证据 |

## 5. 已识别设计重构重点

1. 从“每车找到一条权重最小路径”升级为“候选路线 + 车辆可执行性 + 共享资源时间窗预约”。
2. 将节点/边/交叉区/窄通道/停车位/提升机口统一抽象成有 owner 和 lease 的资源，同时保留各 Pack 不同几何/工艺。
3. 把地图版本选择、路线失败和无图场景改成显式校验/配置，不通过首记录或无路单段 fallback 隐式通行。
4. 将预约从缓存/进程内字典提升为支持并发 grant/release、owner 恢复、过期核验的耐久模型；缓存仅可作加速层。
5. 使用车辆状态/方向/载荷/当前段/遥测时间作为决策输入；数据陈旧或未知时停止扩张调度。
6. 将死锁检测、活性/公平性和路径成本目标分开验收；周期扫任务不等价于活性证明。
7. 反馈绑定任务/段/命令/map version/sequence；重复、乱序和网络不确定性进入恢复而非盲重试。
8. 保留 Stacker 双深/执行堆段、FourWay 提升机/停车工艺的领域隔离；不以统一算法强行消除设备差异。

## 6. 风险声明

目前 Seven 源码与两篇论文只足以给出重构方向和可验证假设。未取得真实地图/车辆/定位/制动/通讯数据、未完成仿真和现场联调之前，WES 规范不得写“零碰撞”“始终无死锁”“所有任务有界完成”或确定性能 SLO。
