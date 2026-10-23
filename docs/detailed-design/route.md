# Route 诊断页面、功能与详细测试

- 状态：评审中；DTC均designed/未执行
- 权威：[目标设计](../route/02-target-routing-and-scheduling.md)、[原测试目录](../route/03-route-test-catalog.md)、[ADR-0001](../adr/ADR-0001-route-reservation-authority.md)
- 共用：[标准](00-shared-page-and-test-standard.md)、[案例边界](01-reference-cases.md)
- 产品阅读：Pack/仿真中的路网/授权/等待/恢复诊断；DENSITY=3、DATA_COMPLEXITY=3、CONSEQUENCE=1（恢复动作进入高后果运维流）。不新增Route独立控制模块。

## 1. 页面与字段

| PAGE定义 | 入口/角色 | 线框/字段/来源 | 操作/查询 |
|---|---|---|---|
| PAGE-ROUTE-001 | Pack/Simulator路网页签；路网查看/工程员 | O/M：固定map/层 → 节点/边/冲突域 → 校验问题。map/hash/Pack/Layer、方向、非负成本、容量、能力、等待点/资源映射；Pack路网权威 | 节点/边/资源过滤、定位；工程编辑跳Simulator草稿，不改active；无图/多active显式错误 |
| PAGE-ROUTE-002 | Pack任务“规划依据”；调度诊断 | O：输入快照 → 候选路线 → 成本分项/剔除 → 资源请求。vehicle/task/goal、map/planner版本、能力/freshness、完整资源序列 | 按决策ID/任务查；离线回放不实际grant/dispatch；无路径与Blocked/StaleState/Unsupported分开 |
| PAGE-ROUTE-003 | Pack预约账本页签；受权查看 | L+O：资源/窗口 → owner/token/epoch → grant/dispatch/释放证据。耐久ledger、writer lease、命令窗口/command、MapVersion | 按资源/owner/状态/时间；无手改epoch/删除活占用按钮；缓存索引仅辅助、标来源 |
| PAGE-ROUTE-004 | 地图等待/恢复详情；调度/运维 | O：wait-for/约束图 → 等待理由/环证据 → 安全恢复建议 → 回读。任务/资源依赖、priority/age、不可抢占阶段、恢复前提 | 临时排队与环区分；建议不是自动倒车命令，任何实际恢复交运维/Pack校验；回原地图 |
| PAGE-ROUTE-005 | Simulator结果/基准页签；算法评审 | O：输入manifest → 样例/seed → 正确性断言 → 性能分布 → 反例。软件/map/hash/硬件/DB/缓存/网络、p50/p95/p99、等待/恢复、计量单位 | 按样例/场景/版本比较；报告门限来源/未批准状态，不展示无依据“最优/无碰撞”；重放不连生产 |

所有页适用UI-001..004/007/008/011；001..004实时查询适用UI-012；无直接业务写，不适用UI-005/006/009/010，实际恢复在BUS动作页另验。不存在map不显示空白健康；003查询失败不能表示无预约；005无结果显示未运行而不是通过。

## 2. 功能/契约

| FUNC定义 | PAGE/需求/原TC | 编号流程及边界 |
|---|---|---|
| FUNC-ROUTE-001 | 001；REQ-ROUTE-001..004；ROUTE-TC-001..007 | 1唯一解析map/Pack/Layer；2验证ID、有限非负权重/容量、方向/能力/资源引用；3不可变发布；4活动任务固定旧版本。无图/无路不默认为单段可行 |
| FUNC-ROUTE-002 | 002；REQ-ROUTE-005..008；ROUTE-TC-008..013/023..025 | 1核快照freshness/来源/版本/载荷；2按硬约束过滤；3按有限非负成本产生候选；4固定tie-break并记录分解。Dijkstra/A*选型待样本基准，候选不是安全grant |
| FUNC-ROUTE-003 | 003；REQ-ROUTE-009..014/028；ROUTE-TC-014..022/040/044 | 1候选转资源[enter,exit)及buffer；2单writer事务验证全部容量/冲突/owner/epoch；3全量持久grant；4dispatcher验证token/epoch后授权；5匹配owner与离开证据才释放。任一冲突/事务错无有效部分grant |
| FUNC-ROUTE-004 | 003/004；REQ-ROUTE-015..019/029；ROUTE-TC-023..030/041..046 | 1回读校验command/task/device/segment/map/seq；2合法推进/释放；3超时Unknown；4接管epoch原子递增；5重建耐久owner/命令/物理占用再派发；6在线目标仅安全点切换。已过Adapter边界的旧命令不能被epoch“撤销事实” |
| FUNC-ROUTE-005 | 004；REQ-ROUTE-020..024；ROUTE-TC-031..039 | 1维护释放依赖；2用独立oracle核环；3区分排队；4按明确aging/fairness和硬前提调度；5仅验证过的安全换路/撤未占用/安全停车，不能安全恢复则升级。无前提不能声称有界完成 |
| FUNC-ROUTE-006 | 005；REQ-ROUTE-025..027；原目录§7..8 | 1固定seed/map/planner/vehicle/输入/时钟；2独立oracle/性质与差分；3逐维负载/故障；4分离规划/DB/缓存/网络/设备成本；5保存反例/门限签核。无硬件/阈值不填“性能通过” |

grant请求包含owner/request、map、vehicle/task、资源与半开窗口；响应token/epoch/授权窗口。时钟/几何缓冲未经现场量测仅为软件测试参数，不证明机械安全。每Pack独立ledger/topology，ExternalWCS不进入本地图；Stacker和FourWay共享契约而不共享设备工艺。

## 3. 可复现测试图与故障夹具

纯逻辑图G1：节点A/B/C/D，边A→B成本1、B→D成本1、A→C成本1、C→D成本1；tie-break按节点ID升序，期望ABD。G2将BD成本改4，期望ACD成本2。无路删除BD/CD；单点图A；能力变体禁C分支。

资源夹具：AB与反向BA不同edgeID，但都映射窄道R1容量1；AC与BD可映射交叉资源X1(cap1)；R2用于多资源原子失败；停车P1/hoistH1作为非路边资源。时间以可控时钟秒计，基础窗[10,20)，buffer=0或1为测试变量，非现场默认。V1/V2/任务T1/T2、MV1、epoch7；ledger与HotStore可单独故障。

## 4. 详细测试

所有行独立运行；B必须三Provider，C逐批准Adapter；物理证据用仿真仅验证协议状态，现场H另签核。

| DTC定义/功能/原TC | 前提及编号步骤 | 断言 | 级别/层级/清理 |
|---|---|---|---|
| DTC-ROUTE-001 / 001 / ROUTE-TC-001..004/006 | 1加载G1/MV1指定Pack层；2变体重复节点/edge/resource、缺端点、负/NaN/Inf权重、0容量、未知冲突域、错层/能力、多active | 正确图隔离可查；每非法变体拒绝并定位引用，不裁剪为0/默认1；非法版本不发布、不grant/dispatch | P0/U+B+E/C0 |
| DTC-ROUTE-002 / 001/002 / ROUTE-TC-005/008..011 | 1G1固定快照运行10次；2G2；3禁C能力；4无路/单点/同起终点；5提高违规路径优化成本偏好 | G1始终ABD成本2；G2 ACD成本2；禁C不取C；无路明确无派发；同起终点无无意义移动；成本不能覆盖能力/禁行 | P0/U+S/C0 |
| DTC-ROUTE-003 / 001/004 / ROUTE-TC-007/013/030 | T1已MV1且当前段不可抢占；1激活MV2/封闭边；2改在线目标；3到安全点后请求重规划 | 已执行段不改图/强撤；新计划与旧预约衔接无保护空窗；记录策略/版本/取消确认；不可安全重规划则暂停 | P0/B+C+S/C1 |
| DTC-ROUTE-004 / 002/004 / ROUTE-TC-012/023..025 | ST两ExeStack段/FW多点；seq10新鲜；变体过期/旧seq/错来源/方向载荷错/map冲突；1规划/映射 | 完整资源/段反馈不丢；能力正确；过期/冲突不平均坐标、不新增依赖该位置grant，状态Unknown/Stale可见 | P0/U+C+E/C1 |
| DTC-ROUTE-005 / 003 / ROUTE-TC-014..017 | V1/V2同向R1/反向不同edgeID/共享X1；1同时请求[10,20)；2第二窗[20,30)，buffer0；3独立buffer1变体 | 重叠冲突不双grant；不同edgeID仍受R1限制；半开边界可按buffer0相接，buffer1重叠拒绝；容量证据可查 | P0/U+B/C1 |
| DTC-ROUTE-006 / 003 / ROUTE-TC-018..020/040 | R2被T2占，T1需R1+R2；1两实例争lease；2并发grant；3第二资源事务失败；4重复grant/release；5错owner释放 | 仅当前writer合法；失败无有效部分R1；同请求同token/逻辑效果；错owner不释放；资源容量始终≤1 | P0/B/C1 |
| DTC-ROUTE-007 / 003/004 / ROUTE-TC-021..022/044..046 | T1持R1物理Unknown，缓存空/伪空闲变体；1让窗/lease过期；2申请T2；3重启重建 | R1不因时间释放；T2冲突拒绝；耐久owner/map/epoch恢复；缓存不是grant依据；保留RecoveryRequired | P0/B+C+E/C1 |
| DTC-ROUTE-008 / 004 / ROUTE-TC-026..029/043 | grant已提交；变体dispatch前crash/发送已生效ACK丢；1重启；2Query；3发错车/段/map、重复/乱序；4合法完成当前段/下一段；5取消竞态 | 先核在途状态、不盲重发；非法不推进/释放；下一段先grant后dispatch；终态一次且只释放自有已确认离开资源 | P0/B+C+S/C1 |
| DTC-ROUTE-009 / 004 / ROUTE-TC-039/041..042 | A epoch7、新B接管epoch8；有旧命令已过Adapter；1旧A grant/release/新dispatch；2B未对账派发；3查物理状态 | 旧epoch新授权全拒绝；不能据fence断言旧物理命令未执行；B未对账不派发；耐久记录/占用受保护 | P0/B+C/C1 |
| DTC-ROUTE-010 / 005 / ROUTE-TC-031..033/035..036 | 依赖T1→R2→T2→R1→T1；无环链及共享容量变体；1构图；2与独立枚举环oracle比；3动态加入目标；4移除安全恢复前提 | 真环报告、排队不误判；动态不可撤销阶段保留；不可安全倒退/换路时升级不自动动作；永久故障不宣称活性 | P0/U+S/C1 |
| DTC-ROUTE-011 / 005 / ROUTE-TC-034 | 低优先Tlow，连续高优先流；批准aging阈值A/等待上限B、资源最终可用且无永久故障；1按固定seed注入流；2运行至B；3独立故障破坏前提 | 前提成立Tlow在批准界内机会可验证；硬约束不放宽；破坏前提时报告不适用/阻断，不伪造通过 | P1/S；A/B未批blocked/C0 |
| DTC-ROUTE-012 / 005 / ROUTE-TC-037..038 | ST深位被挡；FW停车满/H1同口占用；1请求任务；2释放合法前置资源；3制造交叉等待 | 等待图含非路边资源/移位依赖；未证实移浅前不检索；停车/口不超容量；释放后公平唤醒且Unknown不误回收 | P0/B+C+S/C1 |
| DTC-ROUTE-013 / 006 / 原目录§7..8 | seed1..20随机有限非负图、独立暴力小图路径/冲突oracle、固定负载分档；1跑性质/差分；2保存最小反例；3重复基准；4汇总硬件/分位/DB网络成本 | 每条边/成本/owner/容量合法；oracle独立于被测算法；差异按安全修正/行为变化分类；无签核门限不标性能通过，无现场证据不标无碰撞 | P1/U+S+B/C0 |

## 5. 既有 REQ-ROUTE 范围键的语义核对

旧001..027在总追踪是范围键而非逐条定义。以下为基于现有章节的**形式化候选**，不是借本轮页面批准新增算法决策；Route负责人需签核后再转规范性需求。028..029已由ADR确定，不重新解释。

| 候选REQ | 既有语义（必须行为） | 详细验证入口 |
|---|---|---|
| REQ-ROUTE-001 | 解析到明确Pack/Layer/不可变map版本 | DTC-ROUTE-001 |
| REQ-ROUTE-002 | 拒绝重复ID及缺失端点/资源引用 | DTC-ROUTE-001 |
| REQ-ROUTE-003 | 拒绝非有限/负权重和非法容量/能力映射 | DTC-ROUTE-001 |
| REQ-ROUTE-004 | 无图/无路fail closed且活动任务版本固定 | DTC-ROUTE-002/003 |
| REQ-ROUTE-005 | 按车辆方向/载荷/能力过滤候选 | DTC-ROUTE-002/004 |
| REQ-ROUTE-006 | 采用有限非负成本并记录成本分解 | DTC-ROUTE-002 |
| REQ-ROUTE-007 | 相同输入tie-break确定 | DTC-ROUTE-002 |
| REQ-ROUTE-008 | 完整映射资源与设备执行段 | DTC-ROUTE-004 |
| REQ-ROUTE-009 | 相同资源同向竞争遵守容量 | DTC-ROUTE-005 |
| REQ-ROUTE-010 | 不同edge的对向/交叉共享冲突域受约束 | DTC-ROUTE-005 |
| REQ-ROUTE-011 | 资源窗口/边界/缓冲按明确时间模型 | DTC-ROUTE-005 |
| REQ-ROUTE-012 | 多资源grant原子且授权后才派发 | DTC-ROUTE-006 |
| REQ-ROUTE-013 | grant/release幂等、owner隔离 | DTC-ROUTE-006 |
| REQ-ROUTE-014 | lease/缓存丢失不错误释放未知占用 | DTC-ROUTE-007 |
| REQ-ROUTE-015 | 快照有freshness/来源/序列校验 | DTC-ROUTE-004 |
| REQ-ROUTE-016 | 冲突状态/地图版本不确定则暂停核验 | DTC-ROUTE-004 |
| REQ-ROUTE-017 | 超时先查询，命令/反馈不串任务与段 | DTC-ROUTE-008 |
| REQ-ROUTE-018 | 下一段先授权、取消与终态唯一推进 | DTC-ROUTE-008 |
| REQ-ROUTE-019 | 在线目标仅安全点重规划、恢复先对账 | DTC-ROUTE-003/009 |
| REQ-ROUTE-020 | 环检测相对独立oracle无漏报/误报 | DTC-ROUTE-010 |
| REQ-ROUTE-021 | 动态优先与aging在声明前提内公平 | DTC-ROUTE-011 |
| REQ-ROUTE-022 | 前提不成立不宣称活性，恢复不越安全边界 | DTC-ROUTE-010/011 |
| REQ-ROUTE-023 | Stacker挡路/段工艺纳入依赖 | DTC-ROUTE-012 |
| REQ-ROUTE-024 | FourWay停车/提升口纳入依赖 | DTC-ROUTE-012 |
| REQ-ROUTE-025 | 性质/差分保存seed与最小反例 | DTC-ROUTE-013 |
| REQ-ROUTE-026 | 性能报告记录环境/分布/门限依据 | DTC-ROUTE-013 |
| REQ-ROUTE-027 | 软件/仿真结论与现场物理安全分开验收 | DTC-ROUTE-013 |

## 6. 实现阻断

算法选型、几何误差/缓冲/时钟、速度制动、车型能力、A/B公平门限、query/去重/安全停车能力和基准负载须现场/算法/设备负责人共同签核。文档可先用于纯逻辑/故障契约测试设计，不能替代硬件风险评估。性能目标与安全不变量独立验收，不能用吞吐更高接受超容量grant。
