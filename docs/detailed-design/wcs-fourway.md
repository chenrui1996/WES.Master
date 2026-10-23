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
| DTC-FW-003 / 003 / FW-TC-009..011 | P1空、T1/T2等待、高优先持续流；1并发预约；2仅启动反馈；3进入/离开；4排队59/60/540/900秒；另测缺进入信号/容量 | 一次Reserved，启动非Occupied、有效离开才Free；优先级0在59秒0/60秒1/540秒9，封顶后FIFO+任务ID，不保证900秒完成但须报警；缺H阻断真实新任务，不把cap1当现场默认 | P0/B+C+E/C1 |
| DTC-FW-004 / 004 / FW-TC-012..014 | T1跨Z1→Z2，T2同口排队；1按三个阶段逐反馈；2各阶段重复/乱序/错容器；3H1超时 | 合法顺序每阶段1次；非法反馈推进0；H1同口不并行；最终Leg完成事件1；Unknown时T2不抢占口资源 | P0/B+C+E/C1 |
| DTC-FW-005 / 002/005 / FW-TC-015..017 | V1当前seq10且已占R1；变体过期/身份变化/新map/缓存丢失；1新dispatch；2重启；3越权强完/重发；4对账 | 不用旧坐标新派发；耐久owner恢复且R1保护；越权动作0；未核验不重发；WMS库存无伪造改变 | P0/B+C+E/C1 |
| DTC-FW-006 / 005 / FW-TC-018 | 固定map hash/seed7/车数与任务、受控延迟，记录硬件/Provider；1运行；2相同输入重放；3注入断连/停车满/提升机等待；4比较结果及资源历史 | 结果可重现并记录吞吐/p95/p99/等待/冲突拒绝；无容量超授；性能是否通过只按签核门限，不能以0软件冲突宣称物理安全 | P1/S+B/C1 |

### 3.1 队列列配置与关联诊断

遵循[交互合同](03-dense-page-interaction-contract.md)，数字为PAGE-FW；G/R/M为共用测试配置，`!`必显、`+`可选。停车/提升机状态是只读事实，不引入表格内编辑状态。

| 页面 | 配置 | 默认顺序/可选列 | 关联筛选/密集操作与禁用边界 |
|---|---|---|---|
| 001 | M；车辆/任务队列G | 车码!、层/位置、载荷、mode/联锁!、状态/质量!、观测时间!、任务；+方向/seq/MapVersion/Leg | 仓→FourWay Pack→层→车/任务；车任务切层时显示实际所在层，不冒充被移到筛选层；按层/状态分组但Unknown队列始终可发现，动作仍按当前车owner |
| 002 | G+R | 申请/目标资源码!、层/巷、阈值/车数、Free/Reserved/Occupied!、owner!、观测时间/质量!；+lease/策略版本 | 申请→分配候选快照；Pack/层→停车资源；只改变显示与队列查询，不因列隐藏owner/lease到期而清占用。不把候选层切换当在线换目标指令 |
| 003 | R；同口队列G | 提升机/口码!、源层AP/目标层EP、容器、当前阶段!、反馈/Unknown!、Leg；+command/版本/等待时长 | hoist→对应层/口仅过滤合法资源，Leg→固定三阶段链；按口/阶段分组查看，阶段业务序号不可拖动改变。无新增/重排提升队列或任意配AP/EP按钮 |

| DTC定义/功能/原TC | 固定前置与编号步骤 | UI/持久化/副作用断言 | 级别/环境/清理 |
|---|---|---|---|
| DTC-FW-007 / 001..005 / FW-TC-005/009..014/016 | FW两层L1/L2同名停车P1，V1真实L1，LegT1已HoistLift/口Unknown；1切层候选旧回包；2调整停车列；3按口分组/展开；4点Leg阶段/返回；5尝试拖行改阶段 | 不把L1车/资源当L2；owner/占用/质量必显；T1阶段不跳、Unknown队列可见；无拖动写操作，grant/release/库存/设备命令写0，返回保留实际层与合法过滤 | P0/E+B+C/C1 |

## 4. 阻断

按[已签核RULE-18/22及PAR-10/11](05-approved-rules-and-defaults.md)，aging每60秒+1至9、额外保留空位0，物理安全空间不可省；每层最大车数/停车信号/提升口容量/定位/制动/freshness仍H，缺项禁真实新任务，不猜容量1/无限。身份代次变化先保护对账。DTC-FW-001补未配置/非法阈值拒绝，DTC-FW-006采用PAR-10三次基准且报告未执行不冒充通过。跨层仍一Leg，内部阶段不混入跨Pack编排。
