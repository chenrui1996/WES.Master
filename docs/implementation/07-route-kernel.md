# T17 Route 候选内核

- 状态：已验收（T17纯内核软件范围；最终SPEC PASS / CODE PASS，父级集成与全解决方案复验通过）；软件基线为 T17、FUNC-ROUTE-001/002、REQ-ROUTE-001..008/025/027，RULE-13/18/20。
- 权威：[架构](../02-system-architecture.md)、[Route目标](../route/02-target-routing-and-scheduling.md)、[详细用例](../detailed-design/route.md)、[已批准规则](../detailed-design/05-approved-rules-and-defaults.md)。
- 范围：纯图校验、不可变快照/hash、明确版本选择、Dijkstra候选、资源/执行段输出、跨版本物理身份覆盖校验、独立小图oracle。无数据库、Host、设备、预约或激活副作用。

## 实施思路与所有权

初版曾置于 `src\Modules\Route`，但未满足计划要求的Pack私有所有权及真实复用审查，不能据此声称T17完整。2026-10-24按review及新增授权，将全部生产模型/校验/物理映射/候选算法迁到 **`src\Modules\Wcs\FourWay\Route`**，工程为`Wes.Wcs.FourWay.Route.csproj`，namespace为`Wes.Wcs.FourWay.Routing`；测试仍为`tests\Route`，仅引用该私有工程。根 Directory.Build.props 的net10.0/nullable/警告门禁不变。不保留中央Route工程、共享拓扑/validator/physical map或转发facade；不创建虚构的第二调用方。FourWay拓扑加载拒绝Stacker；原实验性Stacker合段分支已删除。其他模块不得直接复用这些Pack私有模型。

第二轮review授权后，新增独立 **`src\Modules\Wcs\Stacker\Route\Wes.Wcs.Stacker.Route.csproj`**、namespace `Wes.Wcs.Stacker.Routing`，测试工程直接引用两个私有工程。Stacker使用自己的aisle/device/ExeStackCode模型、校验、物理映射及Dijkstra实现；非FourWay包装器、非空adapter，不引用FourWay程序集。两者保留有意重复，不抽取共享算法；没有真实生产双消费者/共同语义评审证据时不宣称复用审查完成。父任务负责解决方案接入及总追踪；当前两Pack均不引用Platform/Host/其他模块。

这里的“实际消费”限定T17纯逻辑：Stacker测试从自有图和固定任务快照经过map选择→自有Dijkstra→自有ExeStack/device分段及资源输出完整调用链，使用独立oracle核验，不调用FourWay planner作为实际或期望值。不冒称已经接入现场运行任务、Adapter或页面。双深移库、grant/dispatch/反馈推进仍是Stacker后续工作包，不在T17伪造。

定义显式版本/Pack/层/坐标系/成本单位及能力目录；节点、边、资源、物理冲突域均做引用校验。非法数据在构造不可变artifact前拒绝，错误携对象引用链。Canonical hash用有序、无歧义序列化，所有行为字段参与；不以调用方提供的hash代替内容计算。选择活动图只接受唯一匹配，不退到首条；固定旧版本必须显式指定。

候选采用非负有限double成本Dijkstra；同成本先最少边数，再节点路径Ordinal字典序，再边ID路径。边数严格增加，使零成本环不能改善标签；不以epsilon合并不同成本。输入/状态/地图/planner版本、成本分项、拒绝边原因、完整节点/边/资源出现顺序和执行段均输出；失败不造单段路径。动态封闭/禁行与能力/方向/载荷都是硬过滤，非成本项。权重和累加溢出显式拒绝/诊断，不作无穷路线。

物理映射使用Pack内稳定physicalConflictId集合；改名、拆并须提供完整旧新资源lineage并证明集合覆盖一致。几何/容量/对向共享/设备绑定语义改变属于不兼容。该校验只验证输入的符号覆盖，不能证明真实几何正确、授予CompatibleContinue或启用新图；默认DrainAffected/全Pack与Unknown保护由后续部署/ledger实现。

## 测试与完成边界

先写测试及无行为stub取得真实失败，再逐片实现/复测。DTC-ROUTE-001/002覆盖G1/G2、坏数据、无图/多active/同点/无路及能力硬约束；DTC-ROUTE-003/004仅本包的物理映射/输出纯逻辑部分；DTC-ROUTE-013只跑性质与seed1..20独立穷举/Floyd差分。不调用生产planner、过滤器或比较器作为oracle。

数据库/Adapter/E2E不适用本纯包；Pack投影、真实复用评审、部署策略、耐久全活跃版本预约检查、状态采集与物理H证据不在本包，不能因单测通过标这些父DTC完整验收。A*未启用，无启发式或性能最优性宣传；PAR-10/T33性能、碰撞自由和活性证明均未执行。

## 输入、输出与失败合同

`Topology.Create`拒绝重复ID（含引用集合重复）、缺端点/资源/能力/物理域、默认或含null的集合、非法Pack/层/方向、非有限或负成本分项/总和、非法载荷/容量、超过物理/逻辑资源容量、孤立设备点及空设备执行映射；保留全部`ValidationIssue(Code, Reference)`，不修复输入。单点/合法断图可建artifact，是否有路由候选算法显式回答。

`MapSelector.Resolve`按Pack/层和显式版本或唯一active解析；无图、无active、多active、未知/歧义版本、同版本不同hash均拒绝。显式旧图选择仅服务固定版本的输入解释，**不授予新任务使用旧部署的权限**。新任务active门禁、CAS和任务固定版本持久化由后续Pack/Simulator实现。

`PlanRequest`保存decision/task/goal、起终点、VehicleSnapshot（车辆/Pack/层/map/state版本、位置、方向、载荷、能力、来源、观测时间、generation/sequence/quality）、显式受控Now/freshness、来源期望及序列下界、动态封闭边/资源。所有集合不可变；没有隐藏时钟/随机数。新generation允许sequence重置；相同generation不接受低于下界的sequence。时间新鲜区间为`[observedAt, observedAt + freshnessLimit)`，未来观测、未知/冲突状态或错源不作为可信规划依据。

方向模型是Pack声明的Forward/Reverse/Turn/Lift执行原语及必需能力，不推导真实转弯几何、速度或提升机时间；Pack必须将路径依赖转向合法性编码到正确的状态扩展图/显式边中，不能把本内核当运动学规划器。

`RoutePlan`总是包含status、planner/map/hash/costUnit、输入hash/不可变输入、candidateCount、节点、边成本分项、资源序列、执行段及拒因。candidateCount为返回的候选数（0或1），不是所有简单路径的枚举计数。FourWay保留每条边；Stacker只允许请求DeviceId拥有的边，再将连续相同ExeStackCode+DeviceCode+CommandMapping的边合段，相同ExeStackCode隔段重现或换command不合并；不支持一次请求跨设备接力。资源按起点→边→终点逐次出现，重复物理ID不丢失，段保留资源出现序号；同点候选仅节点资源，无移动边/执行段。`RouteCost.Total`按遍历顺序累加边总成本，分项各自累加；double算术不冒充十进制精确计量。

失败区别`NoMap/NoPath/Blocked/StaleState/Unsupported/InvalidInput/MapMismatch`；失败候选数为0、节点/边/资源/段为空并有拒因。未过滤拓扑本来不可达为NoPath，拓扑可达但硬约束阻断为Blocked；A*等算法或累计double溢出为Unsupported。非法artifact抛带引用的校验异常，取消传播OperationCanceledException，不返回部分路线。null顶层API对象属于调用合同错误（ArgumentNullException），不是无图fallback。原始非法数值只用于离线诊断，调用方不能把含NaN的无效输入原样作为公共JSON响应。

## 可复现与独立oracle

- `Topology.Hash`为所有行为字段、版本和Pack身份的Ordinal排序canonical JSON之SHA-256，不依赖枚举顺序或当前Culture；`InputHash`规范化无序输入集合。非安全输入hash只用于复现，不是签名或授权。
- 20个固定seed，每seed20张2..6节点图（共400图），遍历全部起终点组合；含自环、平行边、零成本环、断图、方向/载荷/能力/禁行/封闭资源变体。穷举全部简单路径给出成本/边数/字典序候选，Floyd-Warshall矩阵独立核成本/可达性；随机数只在测试中使用。
- 性质检查完整边连接、简单路径、分项、每个资源出现、段/边覆盖、反转输入枚举后的路线/拒因/hash重放一致。另用手算G1/G2/零环/无路校准oracle，不以相同实现计算expected/actual。
- 真差分失败时删除边至**单边删除1-minimal**，再删孤立非起终点节点（不声称全局最小）；保留闭边输入一致性，将seed/trial、planner、最小map/hash、输入、expected/actual/Floyd写到`tests\Route\Counterexamples\*.json`。本次无算法差分失败，故没有伪造反例文件。合成缺陷单测验证缩小器确定性与1-minimal性。

## 执行证据（.NET SDK 10.0.103）

所有命令从仓库根运行，测试工程为`tests\Route\Wes.Route.Tests.csproj`。VS Code测试工具尚未发现未接入slnx的新工程，实际使用既有xUnit 2.9.3/Test SDK 18.7.0/runner 3.1.5的`dotnet test`入口；未为迎合skill示例升级到xUnit v3。

| 阶段 | 选择器（`--filter`） | 实测 |
|---|---|---|
| 图红灯 | `FullyQualifiedName~TopologyTests.Valid_artifact` | 1失败/0通过，Create stub抛NotImplementedException |
| 图绿灯 | `FullyQualifiedName~TopologyTests` | 当时30通过 |
| 候选红灯 | `FullyQualifiedName~G1_replays_ABD` | 1失败/0通过，Plan stub抛NotImplementedException |
| 物理身份红灯 | `FullyQualifiedName~Rename_and_opposite` | 1失败/0通过，SharedDomains stub抛NotImplementedException |
| 图/候选/身份绿灯 | 无过滤 | 当时71通过 |
| oracle红灯 | `FullyQualifiedName~Independent_oracles_match` | 1失败/0通过，Exhaustive stub抛NotImplementedException |
| oracle绿灯 | `FullyQualifiedName~OracleTests` | 22通过（20seed + 手算oracle + 缩小器） |
| 强化回归红灯 | `FullyQualifiedName~Malformed_reference\|FullyQualifiedName~Same_version_with_different\|FullyQualifiedName~Invalid_artifact` | 3失败/37通过：null动态引用崩溃、同version异hash被选中、边容量超资源未拒绝 |
| 强化修复绿灯 | 无过滤 | 当时107通过，0跳过 |

首次基础测试曾有集合表达式缺目标类型编译错误，另有xUnit把ImmutableArray当值比较的断言重载错误，已修正为显式序列断言；这些不算行为缺失的红灯证据。

首次切片实测（迁移前历史证据）：**110通过、0失败、0跳过**；Release library构建0警告/0错误；两个工程whitespace校验exit 0；编辑器Problems无错误。新工程锁文件已生成且locked restore成功。绿色TRX保存在`tests\Route\TestResults\t17-green.trx`（既有gitignore排除的测试报告，不入源码提交）。

```powershell
dotnet restore tests\Route\Wes.Route.Tests.csproj --locked-mode --nologo
dotnet test tests\Route\Wes.Route.Tests.csproj --no-restore --nologo -c Release --filter 'CaseId=DTC-ROUTE-001|CaseId=DTC-ROUTE-002|CaseId=DTC-ROUTE-003|CaseId=DTC-ROUTE-004|CaseId=DTC-ROUTE-013' --logger 'console;verbosity=minimal' --logger 'trx;LogFileName=t17-green.trx' --results-directory tests\Route\TestResults
dotnet build src\Modules\Wcs\FourWay\Route\Wes.Wcs.FourWay.Route.csproj --no-restore --nologo -c Release
dotnet format whitespace src\Modules\Wcs\FourWay\Route\Wes.Wcs.FourWay.Route.csproj --verify-no-changes --no-restore --verbosity quiet
dotnet format whitespace tests\Route\Wes.Route.Tests.csproj --verify-no-changes --no-restore --verbosity quiet
```

## 范围追踪与交接

| 基线 | 本包证据 | 不冒认的后续验收 |
|---|---|---|
| FUNC-ROUTE-001、REQ-001..004、DTC-001/002 | TopologyTests、MapSelector及无路/同点测试 | 地图导入/发布API、唯一active耐久指针、旧任务绑定、PAGE-001 |
| FUNC-ROUTE-002、REQ-005..008、DTC-002/004 | G1/G2及禁C、硬约束/快照/成本/完整资源与段输出 | Pack真实能力采集、反馈状态机、PAGE-002 |
| RULE-13/20、DTC-003纯逻辑部分 | PhysicalConflictMapTests的改名/拆并覆盖、不兼容/跨Pack拒绝、对向/共享域 | 几何证据、DrainAffected/CompatibleContinue审批、全活跃图耐久grant及Unknown保护 |
| REQ-025/027、DTC-013纯性质 | 独立穷举/Floyd、seed1..20、反例缩小与持久化路径 | T33性能、真实H、物理安全/活性证明 |

仅修改用户授权的旧Route目录（移除）、两个Pack-private Route、tests/Route及本记录；助手未改slnx、共享设计/追踪/就绪台账或Host/Platform，无数据库/设备访问，无commit/push。父任务已将两个生产工程及测试工程接入WES.slnx，不再引用旧中央工程；父任务管理总追踪/就绪记录与发布提交。两Pack纯内核的测试证据不代表现场/业务运行端完成；最后一节为最新验收状态，以下review记录保留当时的部分完成判断。没有现场输入不阻断这些纯软件测试，但永不因此启用控制。

## 2026-10-24 Review修订

### 所有权 SPEC PARTIAL（第一轮历史状态，后续Stacker实现见末节）

采用review批准的保守方案：不制造两个adapter空壳来声称复用。将现有实现直接搬入FourWay私有目录及assembly，删除旧中央工程与无用Stacker分支，测试增加assembly/namespace/无其他Wes依赖断言及Stacker显式UnsupportedPack变体。生产无共享算法工程。

**第一轮当时剩余任务（现已关闭）**：Stacker私有Topology/PhysicalConflictMap/Dijkstra与ExeStack资源/段消费测试当时缺失，现已由末节两Pack实现及最终review关闭。未抽取共享算法，无需以虚构消费者声称完成复用审查。FourWay/Stacker实际运行用例、图发布/诊断投影与后续执行工作包不在本次纯内核验收范围。

### QUALITY：执行连通性遗漏

旧`ExecutionSemantics`只比较边的能力/容量/命令字段，未包含有向端点且使用HashSet，可能把连接改变或重复边数量改变误判兼容。

- 红灯：`FullyQualifiedName~Rewired_connectivity|FullyQualifiedName~Unambiguous_endpoint`实测**2失败/0通过**，两种重接仍错误返回SemanticallyCompatible=true；测试在修复前运行。
- 修复：端点按节点资源展开的稳定physicalConflictId去重/Ordinal排序规范化，比较有向From→To以及既有执行字段；比较排序后的完整序列（multiset）而非集合，保留并行执行连通性的数量。
- 同一物理端点身份在图中唯一时，合法节点ID改名仍兼容；多个节点共用同一物理域时不能由该域证明端点身份，保留local node ID进行保守比较，模糊改名不出兼容结论。该判断只为符号软件审查，不替代几何/运动学证明或激活审批。
- 回归包括同字段/同physical IDs重接、唯一端点改名、唯一端点重接、模糊端点改名拒绝、并行边数量变化；原对向测试已保留新旧相同BA边，不再用“增加/删除连通性也兼容”的错误期望。

迁移及修复后首次Debug全测**113通过/0失败/0跳过**；补充回归后最终Release选择器全测 **116通过、0失败、0跳过**（仍含seed1..20/400图全起终点差分）。命令沿用上节、生产工程路径已换为FourWay-private，TRX为`tests\Route\TestResults\t17-review-green.trx`。locked restore成功；Release库build **0警告/0错误**；生产/测试whitespace验证均exit 0；Problems无错误。旧`src\Modules\Route`只剩的本切片bin/obj已清理，目录不存在，测试锁文件仅引用`wes.wcs.fourway.route`而非旧中央工程。

第一轮review结束时仅FourWay所有权落地，T17当时为部分完成；该历史状态已由后续Stacker实现、设备约束修复及末节最终SPEC/CODE PASS取代。未修改父任务管理的slnx或共享文档，无commit/push。

## 2026-10-24 Stacker私有内核补齐（第二轮授权，最新）

### 实现及实际纯逻辑消费

新增`src\Modules\Wcs\Stacker\Route\Wes.Wcs.Stacker.Route.csproj`（父任务接入slnx，助手不改共享入口）。没有ProjectReference或PackageReference，无FourWay/共享Route引用；依赖根net10.0/nullable/warnings配置。测试工程增加该引用，锁文件更新；不重建中央Route服务。

- `Topology`：Stacker独立aisle/Pack/device目录、ExeStackCode/DeviceCode/command映射、有限非负成本、载荷/方向/能力/资源/物理域校验，不把FourWay层拓扑当Stacker巷道。错误含reference，缺失执行设备目录引用拒绝，不以非空字符串当真实可映射证据。不可变canonical hash覆盖这些Pack字段，枚举顺序不影响hash。
- `DijkstraPlanner`：消费自有Topology及带task/Leg/goal/map/state/device/heading/观测证据的Stacker快照；纯非负成本、最少边数、Ordinal节点再边ID稳定tie。无图/断图/同点/Blocked/StaleState/Unsupported/版本错/取消/溢出显式；不存在失败单段fallback。初始独立planner version为`stacker-dijkstra-1`，最终设备归属修复后为`stacker-dijkstra-2`（见末节）。
- 结果以自有ExeStackCode、DeviceCode、CommandMapping做**连续**合段，保留每条原边、成本分项、全部资源出现序号/physicalConflictIds。跨设备或换命令不误合段、S1→S2→S1不跨中间段合并；没有dispatch或grant副作用。
- `PhysicalConflictMap`：Stacker独立的稳定域交集、全量lineage、拆并覆盖、容量/几何/设备绑定/对向语义与设备目录兼容检查；执行语义比较含有向端点、边映射的物理域、ExeStack/device/command及multiplicity。唯一物理端点支持节点改名，模糊身份保守拒绝；不授予CompatibleContinue。
- 实际消费测试从MV1固定任务经自有MapSelector选择旧artifact（MV2已active），直接执行自有planner并检查旧ExeStack/旧device资源输出；误把MV2传给旧快照得到MapMismatch。独立测试闭合路径→ExeStack段→资源索引，并验证device目录引用，不增加用于假称“复用”的adapter。
- LocationGroup阻断投影覆盖双深组不可用的纯输入边界；解除该投影只产生候选，不创建移库、不写库存、不声明浅位已清。真实浅深检测、独立StackerTransfer/WMS确认和原检索恢复属于FUNC-STK-002/T20，未实现亦不计为T17通过。

### TDD与独立差分

1. `--filter FullyQualifiedName~StackerRouteTests`：实现前实测**2失败/1通过**，两个行为测试在Topology stub抛NotImplementedException；随后真实实现自有图、Dijkstra与ExeStack段消费，3通过。初次实现期间的括号/嵌套类型编译错误已修正，不计行为红灯。
2. `--filter FullyQualifiedName~Stacker_renames_preserve`：实现前**1失败/0通过**，SharedDomains stub抛NotImplementedException；物理映射实现后该组变绿。
3. `--filter FullyQualifiedName~Invalid_Stacker_artifact`：新增未知DeviceCode回归先实测**1失败/38通过**（错误设备被允许发布），增加明确DeviceCodes目录引用校验及快照device校验后变绿，并加缺目录/重复目录变体。
4. `StackerOracleTests`独立穷举简单路径和Floyd矩阵，不调用生产过滤器/比较器；seed1..20每seed20张2..6节点图，全部起终点对，共400张Stacker图，包含自环/平行边/零成本环、不同ExeStack/device/command、载荷/方向/能力/动态禁行。另用手算G1/G2/零环/断图校准oracle。与FourWay的400张图合计800张，不能把测试图数量当现场容量或性能基准。
5. 反转输入顺序后验证hash/路线/拒因重放，检查节点简单性、edge→segment覆盖、每个资源出现索引恰好归属、相邻可合并段确实合并；同点仅保留资源不造移动段。失败时确定性删边至1-minimal，保存`tests\Route\Counterexamples\stacker-seed-*.json`的map/hash/input/seed/planner/expected/actual/Floyd；合成缺陷验证缩小器，当前无真差分反例，不造文件。

最后一次Debug单独Stacker选择器**110通过、0失败、0跳过**。最终双Pack Release case选择器实测 **226通过、0失败、0跳过**，TRX为`tests\Route\TestResults\t17-both-packs-green.trx`；Stacker/FourWay Release库各自构建均**0警告/0错误**；两生产工程及测试工程whitespace验证均exit 0；locked restore成功、编辑器Problems无错误。最后一轮没有改FourWay端点代码、slnx或父任务Runtime测试。

```powershell
dotnet restore tests\Route\Wes.Route.Tests.csproj --locked-mode --nologo
dotnet test tests\Route\Wes.Route.Tests.csproj --no-restore --nologo -c Release --filter 'CaseId=DTC-ROUTE-001|CaseId=DTC-ROUTE-002|CaseId=DTC-ROUTE-003|CaseId=DTC-ROUTE-004|CaseId=DTC-ROUTE-013' --logger 'console;verbosity=minimal' --logger 'trx;LogFileName=t17-both-packs-green.trx' --results-directory tests\Route\TestResults
dotnet build src\Modules\Wcs\Stacker\Route\Wes.Wcs.Stacker.Route.csproj --no-restore --nologo -c Release
dotnet build src\Modules\Wcs\FourWay\Route\Wes.Wcs.FourWay.Route.csproj --no-restore --nologo -c Release
dotnet format whitespace src\Modules\Wcs\Stacker\Route\Wes.Wcs.Stacker.Route.csproj --verify-no-changes --no-restore --verbosity quiet
dotnet format whitespace src\Modules\Wcs\FourWay\Route\Wes.Wcs.FourWay.Route.csproj --verify-no-changes --no-restore --verbosity quiet
dotnet format whitespace tests\Route\Wes.Route.Tests.csproj --verify-no-changes --no-restore --verbosity quiet
```

### T17剩余边界

“Stacker私有内核缺失”已由真实自有模型/校验/算法/映射/消费测试关闭，未采用共享抽取，因此**不需要用伪消费者证明共享复用**。T17纯内核清单已有实现与测试；父任务现已确认SPEC PASS / CODE PASS，Pack所有权、FourWay端点及Stacker设备约束均已通过复审，并完成slnx集成和全解决方案复验。T17纯内核软件范围已验收，无待关闭的本包代码/规格门禁。

不把T17扩张成T18耐久预约、T19等待/活性、T20真实Stacker任务/双深/dispatch反馈、T33性能或H现场验收；这些缺项不是已经通过的T17证据，也不是本次制造PLC/DB调用的理由。没有已知未实现的本次授权纯内核代码项；如父级review发现契约遗漏须继续按具体证据处理，不能由测试数量代替review。

## 2026-10-24 最终Stacker设备归属复审修复

前次226绿灯不覆盖正确的单设备可执行性：设备目录存在校验只能说明设备已定义，不能允许CRANE1候选使用CRANE2的边。旧分段测试还错误接受跨device路径，本次按review先改为拒绝并新增真实回归。

- 红灯命令：`dotnet test tests\Route\Wes.Route.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Other_device_edges|FullyQualifiedName~Only_other_device|FullyQualifiedName~ExeStack_grouping' --logger 'console;verbosity=minimal'`，实测 **3失败/0通过**。便宜CRANE2路径错误胜出、仅其他device可达仍返回Planned、混合device线性路径错误返回执行段，均在修复前证实。
- 修复：Stacker的每条候选边先核`edge.DeviceCode == request.Device.DeviceId`，不满足记录`DeviceMismatch`硬拒因；不能用低成本越过。原拓扑可达但该设备无可执行路径时返回Blocked，所有候选节点/边/段/资源为空，candidateCount=0。不新增handoff/multidevice语义。
- 独立穷举/Floyd oracle增加其自身的单device条件，随机图仍包含CRANE1/CRANE2混合边；每个成功段/边额外断言其device等于请求DeviceId。手算/资源/方向/载荷/能力/ExeStack连续分组性质保留。其他device全路径测试还验证同一图以合法CRANE2快照请求可成功，不是一概删除第二设备。
- 规划语义已变，版本提升为`stacker-dijkstra-2`，防止旧回放错误套用新约束。此次只改Stacker planner、两份Stacker测试及本记录，不动FourWay端点、slnx或共享文档。
- 同一红灯选择器修复后 **3通过/0失败**；最终全Route Release **228通过、0失败、0跳过**（含两Pack seed1..20差分）。`dotnet restore tests\Route\Wes.Route.Tests.csproj --locked-mode --nologo`成功；`dotnet build src\Modules\Wcs\Stacker\Route\Wes.Wcs.Stacker.Route.csproj --no-restore --nologo -c Release`为**0警告/0错误**；Stacker/test项目whitespace验证exit 0；编辑器无错误。
- 最终完整测试命令：`dotnet test tests\Route\Wes.Route.Tests.csproj --no-restore --nologo -c Release --logger 'console;verbosity=minimal' --logger 'trx;LogFileName=t17-device-ownership-green.trx' --results-directory tests\Route\TestResults`；结果为`tests\Route\TestResults\t17-device-ownership-green.trx`。

该中等正确性缺陷已修复，旧“跨设备也可规划”的预期已删除；父任务最终review已确认SPEC PASS / CODE PASS，并以修复后的全解决方案430通过重新验证，不以此前aggregate427绿灯覆盖缺陷。现场执行/接力/预约/安全证明仍未实现且不在此次授权范围。无commit/push。

## 2026-10-24 01:21 父级最终验收（最新结论）

父任务明确回传最终 scoped reviewer **SPEC PASS / CODE PASS**：此前Pack所有权、端点物理身份/连通性、单设备可执行性及独立oracle问题均已关闭。父任务已把`Wes.Wcs.FourWay.Route.csproj`、`Wes.Wcs.Stacker.Route.csproj`与`Wes.Route.Tests.csproj`集成到`WES.slnx`。**T17纯内核软件已验收**，不是仅凭单项目测试的暂定通过。

父任务报告的新鲜验证结果（本次仅更新文档，不重复执行或冒称本子任务执行）：

| 父级验证 | 最终实测 |
|---|---|
| 全解决方案locked restore | 通过 |
| 全解决方案Release build | 通过；0 warning、0 error |
| 串行全解决方案Release tests | **430 passed、0 failed、0 skipped** |
| 分工程测试数量 | Architecture **29** + Modules **99** + Persistence **74** + Route **228** = **430** |
| 全解决方案format check | 通过 |
| 设计校验/DAG脚本 | 均通过 |

以下为上述解决方案验证的明确复跑命令（父任务本轮只回传结果，未附原始命令行；故不将这些复跑命令伪称其逐字shell记录）：

```powershell
dotnet restore WES.slnx --locked-mode --nologo
dotnet build WES.slnx --no-restore --nologo -c Release
dotnet test WES.slnx --no-build --no-restore --nologo -c Release -m:1 --logger 'console;verbosity=minimal'
dotnet format WES.slnx --verify-no-changes --no-restore --verbosity quiet
```

本子任务精确执行过的Route命令及TRX见上一节；父级设计/DAG脚本通过结论由父任务提供，其原始命令/报告由父任务验收记录保存，不编造未提供的脚本入口或参数。父任务管理readiness、总追踪、README及T17发布提交；本次仅改本文件，无代码/测试/manifest/slnx修改、无commit。

**验收边界不变**：通过的是不可变图、校验/选择、稳定physicalConflictId、确定性候选及独立差分oracle等T17纯软件内核。不表示T18耐久预约、grant/dispatch/反馈执行、完整WCS、live Adapter、现场H、性能/碰撞自由/有界活性或T04–T34整体交付已完成。
