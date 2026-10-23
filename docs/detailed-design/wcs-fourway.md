# FourWay 页面、功能与详细测试

- 状态：评审中；DTC均designed/未执行
- 权威：[FourWay](../modules/wcs-fourway.md)、[ADR-0001](../adr/ADR-0001-route-reservation-authority.md)
- 范围SCOPE-WCS-02；REQ-FW-001..014沿原范围追踪，不据范围编号虚构新规则
- 共用：[标准](00-shared-page-and-test-standard.md)、[Route包](route.md)、[公开案例边界](01-reference-cases.md)
- 产品阅读：车辆/层内交通/提升机地图面板；DENSITY=3、DATA_COMPLEXITY=3、CONSEQUENCE=3。

## 1. 页面

| PAGE定义 | 角色/入口 | 线框/字段/数据权威 | 查询/动作/返回 |
|---|---|---|---|
| PAGE-FW-001 | 包查看/调度；地图选Pack或`/wcs/fourway/:pack` | M：层切换/车/路/资源 → 车辆详情 → 当前任务。Vehicle、位置/方向/载荷、mode/联锁、seq/观测时间、任务/Leg、MapVersion；Pack权威，库存只读WMS | 层/车/任务/Unknown过滤；暂停/恢复/模式申请经运维，本包最终检查；到002/003保留地图层 |
| PAGE-FW-002 | 调度诊断；001的分配/停车页签 | O：层巷候选 → 阈值/车数/过滤依据 → 目标claim → 停车资源队列。strategyVersion、MinEmptySlots按层、MaxShuttleCount、owner/lease、Free/Reserved/Occupied | 无空位解释Suspended/等待时长；不提供“一键清空停车”或手改owner；原位回任务 |
| PAGE-FW-003 | 提升机运维/调度；`/wcs/fourway/:pack/hoists/:id` | L+O：口/层/AP/EP → 同口队列 → ToSrcAp/HoistLift/FromDesEp时间线 → 反馈/恢复证据 | 按Leg/容器/阶段/Unknown筛选；支持的暂停/取消申请；设备能力/口/容器/阶段必须服务端校验，未回读不可跳阶段 |

001/003适用UI-001..012；002为只读，UI-005/006/009/010不适用于直接写。001无资产是配置缺失；002停车无可用是等待，不是首次创建；003无排队可为zero-work。实时图层与列表均显示Stale/未定位项，不从筛选中隐去问题。

## 2. 功能

| FUNC定义 | PAGE/原TC | 主流程/状态/拒绝 |
|---|---|---|
| FUNC-FW-001 | 001/002；FW-TC-001..004 | 1Pack位置/交接点CanHandle；2按层空位阈值、车数上限过滤层巷；3稳定权重/轮转选合法目标；4任务绑定claim并协调WMS业务占位。错Pack/禁位/满额不接单；全链跨owner无分布式ACID |
| FUNC-FW-002 | 001；FW-TC-005..008/015..016 | 1唯一解析map/layer；2验证freshness/能力；3候选与资源窗；4durable grant+epoch后dispatch；5反馈释放。多图歧义拒绝，HotStore失效不双授予，未知占用保留 |
| FUNC-FW-003 | 002；FW-TC-009..011 | 1按组/优先与fairness选择；2原子Free→Reserved(owner)；3按批准设备语义观察真实进入/占用后→Occupied；4离开确认→Free。队列资源恢复唤醒不超过容量；不以lease过期清活车 |
| FUNC-FW-004 | 003；FW-TC-012..014 | 1ToSrcAp；2源口/设备/货物确认且口资源授权后HoistLift；3目标EP确认后FromDesEp；4目标到位才结束同一Leg。每次阶段事务验证版本/command与Outbox；同口互斥；丢失/错误容器/乱序不跳阶段 |
| FUNC-FW-005 | 001/003；FW-TC-008/015..018 | 1停新冲突授权；2查询耐久任务/命令与当前位置；3受权对账/恢复申请；4Pack重新检查联锁/epoch/物理事实；5继续或保持Suspended并升级。未知位置不自动估算，仿真基准不是物理安全证明 |

停车“开始移动”与“已经进入停车资源”不是同一事实；具体哪个反馈允许转Occupied由设备契约确认。所有资源变更owner匹配；cancel只可直接释放已确认未物理占用的reservation。源/目标层路线分别固定map引用，不用一个层图跨层直连。

## 3. 详细测试

夹具：FW1，层Z1/Z2，巷A1/A2，V1/V2，停车P1(cap1)，提升口AP1/EP2，HoistH1(cap1)，mapMV1/MV2；T1/T2引用不同容器。

| DTC定义/功能/原TC | 前置与编号步骤 | 断言 | 级别/层级/清理 |
|---|---|---|---|
| DTC-FW-001 / 001 / FW-TC-001..004 | 层阈值n、车上限m；各n-1/n/n+1、m-1/m/m+1变体；1并发争最后目标；2错Pack/禁位请求 | 阈值按层非全仓；满额剔除；仅1合法claim；不接ST位置；策略/拒绝依据可查，失败无部分预约 | P0/U+B+E/C1 |
| DTC-FW-002 / 002 / FW-TC-005..008 | 两条同代价路、共享冲突区R1；显式map/Layer/Active歧义、负权/无路变体；1规划；2V1/V2竞争；3取消/失败 | 固定输入路径稳定；歧义/非法无派发；R1不双授予；仅取消owner未占用claim被释放 | P0/U+B+C/C1 |
| DTC-FW-003 / 003 / FW-TC-009..011 | P1空、T1/T2等待、高优先持续流；1并发预约P1；2仅发启动反馈；3发进入/离开反馈；4唤醒 | 一次Reserved；未满足进入证据不误Occupied；离开才Free；等待可见；fairness在批准假设/上限内验证，未批准数值不标通过 | P0/B+C+E/C1 |
| DTC-FW-004 / 004 / FW-TC-012..014 | T1跨Z1→Z2，T2同口排队；1按三个阶段逐反馈；2各阶段重复/乱序/错容器；3H1超时 | 合法顺序每阶段1次；非法反馈推进0；H1同口不并行；最终Leg完成事件1；Unknown时T2不抢占口资源 | P0/B+C+E/C1 |
| DTC-FW-005 / 002/005 / FW-TC-015..017 | V1当前seq10且已占R1；变体过期/身份变化/新map/缓存丢失；1新dispatch；2重启；3越权强完/重发；4对账 | 不用旧坐标新派发；耐久owner恢复且R1保护；越权动作0；未核验不重发；WMS库存无伪造改变 | P0/B+C+E/C1 |
| DTC-FW-006 / 005 / FW-TC-018 | 固定map hash/seed7/车数与任务、受控延迟，记录硬件/Provider；1运行；2相同输入重放；3注入断连/停车满/提升机等待；4比较结果及资源历史 | 结果可重现并记录吞吐/p95/p99/等待/冲突拒绝；无容量超授；性能是否通过只按签核门限，不能以0软件冲突宣称物理安全 | P1/S+B/C1 |

## 4. 阻断

定位/反馈延迟、速度/缓冲/时间基准、停车进入信号、提升口布局/载荷/容量、aging上限、车辆身份变更流程需现场签核。先建资源/提升阶段状态测试，再接地图；跨层始终一个Leg，内部阶段不混入跨Pack编排。
