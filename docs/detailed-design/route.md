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
| FUNC-ROUTE-002 | 002；REQ-ROUTE-005..008；ROUTE-TC-008..013/023..025 | 1核快照freshness/来源/版本/载荷；2按硬约束过滤；3有限非负成本以首期Dijkstra产候选；4稳定tie-break及成本分解。A*仅后续前提证明/基准通过再启，候选不是安全grant |
| FUNC-ROUTE-003 | 003；REQ-ROUTE-009..014/028；ROUTE-TC-014..022/040/044 | 1候选转资源[enter,exit)及buffer；2单writer事务验证全部容量/冲突/owner/epoch；3全量持久grant；4dispatcher验证token/epoch后授权；5匹配owner与离开证据才释放。任一冲突/事务错无有效部分grant |
| FUNC-ROUTE-004 | 003/004；REQ-ROUTE-015..019/029；ROUTE-TC-023..030/041..046 | 1回读校验command/task/device/segment/map/seq；2合法推进/释放；3超时Unknown；4接管epoch原子递增；5重建耐久owner/命令/物理占用再派发；6在线目标仅安全点切换。已过Adapter边界的旧命令不能被epoch“撤销事实” |
| FUNC-ROUTE-005 | 004；REQ-ROUTE-020..024；ROUTE-TC-031..039 | 1维护释放依赖；2用独立oracle核环；3区分排队；4按明确aging/fairness和硬前提调度；5仅验证过的安全换路/撤未占用/安全停车，不能安全恢复则升级。无前提不能声称有界完成 |
| FUNC-ROUTE-006 | 005；REQ-ROUTE-025..027；原目录§7..8 | 1固定seed/map/planner/vehicle/输入/时钟；2独立oracle/性质与差分；3逐维负载/故障；4分离规划/DB/缓存/网络/设备成本；5保存反例/门限签核。无硬件/阈值不填“性能通过” |

grant请求包含owner/request、map、vehicle/task、资源与半开窗口；响应token/epoch/授权窗口。按已批准[规则基线](05-approved-rules-and-defaults.md) RULE-13/18/20：图资源映射稳定physicalConflictId集合，grant核全活跃图占用，MapVersion不隔离物理容量；默认DrainAffected，CompatibleContinue须证据。首期Dijkstra，同分稳定；priority0..9、每60秒aging+1封顶9，等待15分钟仅报警。物理时间/几何buffer缺配置则阻断控制，仿真值不能Promote。每Pack独立ledger/topology，不形成跨Pack原子grant。

## 3. 可复现测试图与故障夹具

纯逻辑图G1：节点A/B/C/D，边A→B成本1、B→D成本1、A→C成本1、C→D成本1；tie-break按节点ID升序，期望ABD。G2将BD成本改4，期望ACD成本2。无路删除BD/CD；单点图A；能力变体禁C分支。

资源夹具：AB与反向BA不同edgeID，但都映射窄道R1容量1；AC与BD可映射交叉资源X1(cap1)；R2用于多资源原子失败；停车P1/hoistH1作为非路边资源。时间以可控时钟秒计，基础窗[10,20)，buffer=0或1为测试变量，非现场默认。V1/V2/任务T1/T2、MV1、epoch7；ledger与HotStore可单独故障。

## 4. 详细测试

所有行独立运行；B必须三Provider，C逐批准Adapter；物理证据用仿真仅验证协议状态，现场H另签核。

| DTC定义/功能/原TC | 前提及编号步骤 | 断言 | 级别/层级/清理 |
|---|---|---|---|
| DTC-ROUTE-001 / 001 / ROUTE-TC-001..004/006 | 1加载G1/MV1指定Pack层；2变体重复节点/edge/resource、缺端点、负/NaN/Inf权重、0容量、未知冲突域、错层/能力、多active | 正确图隔离可查；每非法变体拒绝并定位引用，不裁剪为0/默认1；非法版本不发布、不grant/dispatch | P0/U+B+E/C0 |
| DTC-ROUTE-002 / 001/002 / ROUTE-TC-005/008..011 | 1G1固定快照运行10次；2G2；3禁C能力；4无路/单点/同起终点；5提高违规路径优化成本偏好 | G1始终ABD成本2；G2 ACD成本2；禁C不取C；无路明确无派发；同起终点无无意义移动；成本不能覆盖能力/禁行 | P0/U+S/C0 |
| DTC-ROUTE-003 / 001/004 / ROUTE-TC-007/013/030 | T1在MV1不可抢占段；MV2资源改名但同physicalConflictId；1默认DrainAffected请求激活；2独立已验证CompatibleContinue变体激活/申请T2同域；3旧T1下一段/改目标；4缺映射/几何变更；5到安全点重规划 | 默认须排空后激活、不强撤占用；兼容变体可共存但T2冲突grant拒绝；旧T1固定原部署并核当前epoch/grant；缺映射/不兼容未排空激活拒绝；重规划无保护空窗。RVT-01/04必测 | P0/B+C+S/C1 |
| DTC-ROUTE-004 / 002/004 / ROUTE-TC-012/023..025 | ST两ExeStack段/FW多点；seq10新鲜；变体过期/旧seq/错来源/方向载荷错/map冲突；1规划/映射 | 完整资源/段反馈不丢；能力正确；过期/冲突不平均坐标、不新增依赖该位置grant，状态Unknown/Stale可见 | P0/U+C+E/C1 |
| DTC-ROUTE-005 / 003 / ROUTE-TC-014..017 | V1/V2同向R1/反向不同edgeID/共享X1；1同时请求[10,20)；2第二窗[20,30)，buffer0；3独立buffer1变体 | 重叠冲突不双grant；不同edgeID仍受R1限制；半开边界可按buffer0相接，buffer1重叠拒绝；容量证据可查 | P0/U+B/C1 |
| DTC-ROUTE-006 / 003 / ROUTE-TC-018..020/040 | R2被T2占，T1需R1+R2；1两实例争lease；2并发grant；3第二资源事务失败；4重复grant/release；5错owner释放 | 仅当前writer合法；失败无有效部分R1；同请求同token/逻辑效果；错owner不释放；资源容量始终≤1 | P0/B/C1 |
| DTC-ROUTE-007 / 003/004 / ROUTE-TC-021..022/044..046 | T1持R1物理Unknown，缓存空/伪空闲变体；1让窗/lease过期；2申请T2；3重启重建 | R1不因时间释放；T2冲突拒绝；耐久owner/map/epoch恢复；缓存不是grant依据；保留RecoveryRequired | P0/B+C+E/C1 |
| DTC-ROUTE-008 / 004 / ROUTE-TC-026..029/043 | 整条候选路径grant已提交；变体dispatch前crash/发送已生效ACK丢；1重启query；2错车/段/map、重复/乱序；3合法当前段/下一段；4取消竞态 | 先核在途、不盲重发；非法不推进/释放；下一段验证已有全量grant/当前epoch及窗口再dispatch，若失效先安全重规划/全量再grant，不把原子grant拆成逐段事务；终态一次，仅释确认离开的自有资源 | P0/B+C+S/C1 |
| DTC-ROUTE-009 / 004 / ROUTE-TC-039/041..042 | A epoch7、新B接管epoch8；有旧命令已过Adapter；1旧A grant/release/新dispatch；2B未对账派发；3查物理状态 | 旧epoch新授权全拒绝；不能据fence断言旧物理命令未执行；B未对账不派发；耐久记录/占用受保护 | P0/B+C/C1 |
| DTC-ROUTE-010 / 005 / ROUTE-TC-031..033/035..036 | 依赖T1→R2→T2→R1→T1；无环链及共享容量变体；1构图；2与独立枚举环oracle比；3动态加入目标；4移除安全恢复前提 | 真环报告、排队不误判；动态不可撤销阶段保留；不可安全倒退/换路时升级不自动动作；永久故障不宣称活性 | P0/U+S/C1 |
| DTC-ROUTE-011 / 005 / ROUTE-TC-034 | Tlow base0、Thigh base9同竞争集；1受控钟推进59/60/540秒；2同effective比先排队时间/ID；3推进900秒；4资源永久故障；5另给有证明的可用性/服务上限夹具 | effective依次0/1/9，同级旧任务先；900秒等待告警不强派/不宣称完成；永久故障不报有界活性通过；只有显式具备服务界及前提的变体才验其完成界，生产B仍需证据 | P1/U+S/C0 |
| DTC-ROUTE-012 / 005 / ROUTE-TC-037..038 | ST深位被挡；FW停车满/H1同口占用；1请求任务；2释放合法前置资源；3制造交叉等待 | 等待图含非路边资源/移位依赖；未证实移浅前不检索；停车/口不超容量；释放后公平唤醒且Unknown不误回收 | P0/B+C+S/C1 |
| DTC-ROUTE-013 / 006 / 原目录§7..8 | seed1..20随机图与独立小图oracle；1性质/差分；2保存反例；3按规则PAR-10负载预热5分钟/稳态30分钟独立3次；4记录硬件/各Provider/网络、样本量/各次分位；5门限/门限+1ms变体 | 图/owner/容量全合法；按PAR-10分别验规划p95≤100/p99≤500ms、grant≤50/200ms，任一次越界失败，不能平均分位数；缺真实环境/报告标未执行，不因批准目标标通过；安全失败不由性能抵消。RVT-09必测 | P1/U+S+B/C0 |

### 4.1 诊断表列与版本关联

遵循[交互合同](03-dense-page-interaction-contract.md)，数字为PAGE-ROUTE；G/R/M为共用测试配置，`!`必显、`+`可选。所有Route运行事实只读，不增加grant、epoch、release的表格编辑入口。

| 页面 | 配置 | 默认顺序/可选列 | 关联筛选/密集操作与禁用边界 |
|---|---|---|---|
| 001 | M；节点/边表G | 对象码!、Pack/层、方向/端点、成本/容量!、校验结果!、map/hash!；+能力/资源映射 | Pack→MapVersion→层/节点/边/资源限定版本；修改图去Simulator草稿。不同版本同edgeID不合并，表列顺序不改拓扑序，资源码显示冲突域引用 |
| 002 | R；候选表G | 决策/候选号!、完整路径、成本/单位、可行/剔除!、原因!、输入map/planner版本!；+分项/freshness | task→固定决策快照→候选/资源序列；当前地图过滤不改旧计划依据。按剔除原因分组只能浏览，选择候选行不grant/dispatch |
| 003 | G+R | 资源码!、物理冲突身份!、状态/物理占用!、owner!、窗口/单位!、epoch!、map!、command/释放证据；+token摘要/lease/缓存来源 | Pack/MapVersion仅过滤诊断记录，不过滤grant安全检查；同物理冲突域提示其他活跃版本的保护/计数，受权沿版本引用查明。Unknown/空闲分开，不按到期批删；token显示不是授权 |
| 004 | R；wait-for队列G | 等待任务!、资源依赖!、等待/环类型!、不可抢占/Unknown!、等待时长/观测时间!；+age/优先/建议前提 | wait-for snapshot→任务/资源/证据，引用不在当前页照样可解析；建议进BUS受权动作，不能拖行提升真实优先级、自动反向或撤占用 |
| 005 | G+R | 场景/run!、输入版本/hash!、seed、断言状态!、门限批准状态!、p50/p95/p99及单位；+硬件/DB/网络摘要 | run→固定manifest→planner/负载/故障，比较仅相容维度；不同环境/单位/未批门限显式不可比较，不能平均分位数冒充总指标；无新增生产回放入口 |

| DTC定义/功能/原TC | 固定前置与编号步骤 | UI/持久化/副作用断言 | 级别/环境/清理 |
|---|---|---|---|
| DTC-ROUTE-014 / 001..006 / ROUTE-TC-007/023..024/030/040..046 | MV1/MV2同资源R1、T1固定MV1、epoch7/Unknown占用；runA秒/runB毫秒且门限未批；1换map旧resolve回；2调列/按状态分组；3沿候选/command回链；4试拖等待任务；5比较run | 不跨版本拼ledger/路径；owner/epoch/Unknown/版本必显；候选选择或拖动不grant/release/派发；返回固定T1版本；不兼容指标不可合并，性能不假标通过；所有耐久预约/库存/物理命令写0 | P0/E+B+C/C1 |

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

首期Dijkstra、跨版物理域、部署排空/兼容、aging及基准目标已按[规则基线](05-approved-rules-and-defaults.md)定案。几何误差/缓冲/时钟、制动/车型、query/去重/安全停车和生产服务上限仍需现场证据；默认Unconfigured阻断依赖控制，不取仿真buffer0/1当生产值。REQ逐条语义候选仍待需求owner签核；基准目标批准不等于性能通过。
