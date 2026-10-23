# 2D 运维工作台、运输编排与问题详细设计

- 状态：评审中；DTC均designed/未执行
- 权威：[Orchestration](../modules/orchestration.md)、[架构](../02-system-architecture.md)
- 范围SCOPE-BUS-01、SCOPE-OPS-01；REQ-BUS-001..012、REQ-SITE-012..016、REQ-OPS-001..005/008..010
- 共用：[标准](00-shared-page-and-test-standard.md)、[Pack设计](wcs-stacker.md)、[FourWay](wcs-fourway.md)、[案例](01-reference-cases.md)
- 产品阅读：现场调度/问题处置的统一地图与关联队列；DENSITY=3、DATA_COMPLEXITY=3、CONSEQUENCE=3。

## 1. 页面信息架构

| PAGE定义 | 角色/入口 | 线框/字段与来源 | 查询/动作/返回 |
|---|---|---|---|
| PAGE-BUS-001 | 查看/调度/运维分权；默认`/operations/map` | M：环境/仓/Pack/部署 → 地图图层 → 对象检查器 → 下方任务/问题/未定位队列。资源稳定ID、坐标绑定、类型、状态、sourceVersion/freshness；SCADA只读空间投影，告警来自Notifications，任务来自Pack/Bus | 图层设备/车辆/库位/任务/预约/告警、区域/层/Pack/状态筛选；聚合展开；点击队列定位或显示未定位原因；常用动作进004，不离开地图 |
| PAGE-BUS-002 | 运输查看/协调员；地图任务或`/transport/orders` | L：订单表 → 串行Leg链/交接 → 当前attempt/问题 → WMS结果。业务引用/容器/源目标、优先/组、状态、版本、Leg序号/Pack/依赖、外部taskID | 单号/容器/Pack/状态/时间；暂停/取消按能力申请；无手工改Completed；复杂详情深链回地图恢复范围/对象 |
| PAGE-BUS-003 | 问题处理/班组主管；地图下队列或`/operations/issues` | L+F：问题/未定位队列 → 来源/occurrence → 诊断/责任 → 处置/验证/历史。severity、首次/最近、asset/Leg/alarm/deployment引用、assignee/team、版本、证据 | 状态/未指派/未定位/陈旧/责任组过滤；确认、指派、调查、升级、待验证、解决/复开；告警确认是单独动作，不能合并为“关闭” |
| PAGE-BUS-004 | 具备动作权限操作者/复核者；001/003内动作面板 | F：环境/目标/当前状态 → 影响预览 → 理由 → 所需审批 → 执行/回读。operationID、目标版本、能力、mode/联锁、ownerPack、grant/command引用、最后回读时间 | 常用暂停/恢复/取消、启停/模式、申请点禁用按实际能力；强完/重发/解除类要求增强门禁，无法软件绕过安全PLC；默认取消，完成后回原对象 |
| PAGE-BUS-005 | 跨包配置/诊断员；`/transport/handover` | L+F：HandoverLink → 有向Pack/位置关系 → 校验/引用中任务。linkID、源/目标Pack与交接点、能力/启用/版本；实体配置owner按编排契约 | 受权配置合法链接，拒绝引用冲突/错误方向；运行中变更不能悄改已规划Leg；不编辑Pack内部路网 |

```text
环境/仓/Pack/部署  数据更新时间  查询/图层
地图（节点/设备/车辆/任务/预约） | 对象/任务/告警详情
                              | 联锁/能力/反馈
                              | 常用操作 / 复杂诊断
任务 | 问题 | 未定位/Unknown    | 处置/指派/验证
```

### 地图可发现性和焦点

地图+队列应可查看全部已知资产：过滤显示“当前范围/过滤数量”，未知或未定位保留独立队列；不能因为图层关闭使未处理问题永久消失。车辆/重叠节点聚合后提供列表展开；选择队列项与画布对象互相同步但不改业务事实。键盘用户从范围→图层→对象列表→检查器→动作→结果；Canvas不能成为唯一内容或操作路径。

状态矩阵：001..005均适用UI-001..012；002/003有L分页，001对象定位不是分页选择，005配置无实时流可不适用UI-012；004无first-empty/filtered-empty/列表选择。001部分Pack离线只失败该图层并显示Unknown；不能整张地图一片绿色。布局窄屏优先地图/问题队列切换，检查器覆盖但可返回，禁止堆三层模态框。

## 2. 功能/状态与持久化

| FUNC定义 | 页面/需求/原TC | 主流程及拒绝 |
|---|---|---|
| FUNC-BUS-001 | 002/005；REQ-BUS-001..012；BUS-TC-001..005/010 | 1验业务引用/源目标/Pack归属；2同址交WMS无运输分支；3单Pack单Leg、跨Pack按合法有向交接配置；4确定性选择；5持久order/Leg/幂等/Outbox后才首派。无Pack/断链接/歧义拒绝且无设备副作用 |
| FUNC-BUS-002 | 001/002；REQ-BUS-001..012；BUS-TC-006..009/013..016 | 1持久竞争待dispatch；2以稳定commandID接受；3超时Unknown查询；4事件验来源/Pack/Leg/seq/version并去重；5前段完成与交接确认后激活唯一后段；6全Leg完成后独立WMS同步。订单完成与WMS回写结果分栏 |
| FUNC-BUS-003 | 002/004；REQ-BUS-001..012；BUS-TC-011..012 | 1记录CancelRequested；2向活动Pack申请；3核终止/当前位置；4停未执行Leg、保留已完成事实；5让WMS按事实补偿。Completed竞态按当前版本和物理核验处理，Unknown不直接释放库存/占用 |
| FUNC-BUS-004 | 001/004；REQ-OPS-001..005、REQ-SITE-012..016；SITE-TC-012..023 | 1选对象回查owner状态/版本；2授权/理由/影响预览/现场复核；3持久operation/审批引用；4Pack最终检查模式/联锁/epoch/资源；5语义命令；6权威回读/必要WMS对账；7显示确定结果或未知/升级。不支持动作明确拒绝 |
| FUNC-BUS-005 | 001/003；REQ-OPS-008..010、REQ-OPS-002/004；SITE-TC-024..028 | 1按(sourceSystem,occurrenceID)幂等建立issue；2确认/指派/调查；3处置引用command；4AwaitingVerification收集新鲜WCS/Device及必要WMS证据；5Resolved；6新权威证据可审计复开或新occurrence新问题。告警Cleared不自动Resolved |

运维问题初始状态严格沿用：

```text
Open -> Acknowledged -> Assigned -> Investigating -> AwaitingVerification -> Resolved
任一未解决状态 -> Escalated -> 经授权Assigned/Investigating
Resolved -> 经权威证据与审计复开（保留原历史）
```

issue transition命令带issue/version/targetState/actor/reason/evidenceRefs/幂等键；证据含来源、观测时间、对象/命令关联、结果及必要对账版本。指派/升级保留原/新责任、原因/时间；SLA数值未批准不自动认定超时违规。解决与告警清除、命令Accepted三者不能同事务随意合并。

## 3. 详细测试

夹具TA/WA；PackST/FW，交接H1方向ST→FW；orderO1有L1/L2串行；任务/容器C1；issueI1关联alarmA1，seq10、version1。设备采用隔离Adapter。

| DTC定义/功能/原TC | 数据和编号步骤 | 页面/持久化/副作用断言 | 级别/层级/清理 |
|---|---|---|---|
| DTC-BUS-001 / 001 / BUS-TC-001..005/010 | 1同键并发提交单包/两包/同址变体；2缺起点/无Pack/断H1/反向H1/同分候选 | 合法1order与一套Leg、确定路线；同址不建设备任务；非法无残留活动Leg/物理命令；前段依赖可查 | P0/B+E/C1 |
| DTC-BUS-002 / 002 / BUS-TC-006..009/014..016 | L1接受后响应丢；1重启dispatcher；2Query；3重复/乱序/错误Pack/跨租户事件；4正确完成L1；5L1失败独立变体 | 无第二逻辑接受；错误不推进；正确仅激活一次L2；失败后L2不派发；一串行活动Leg不双dispatch | P0/B+C+E/C1 |
| DTC-BUS-003 / 002 / BUS-TC-013/E2E-TC-005 | L1/L2完成；1WMS回写已执行但ACK丢；2恢复消费重投；3查看订单/WMS状态 | order完成和WMS同步状态分开；最终库存移动一次；不因同步失败重复物理运输；审计引用完整 | P0/B+C+E/C1 |
| DTC-BUS-004 / 003 / BUS-TC-011..012 | L1完成、L2 Running；1取消同时发Completed；2取消结果Unknown；3回读核验 | 保留L1事实；合法版本终态唯一；Unknown不假Cancelled/释放；WMS预留只由WMS按最终事实处置 | P0/B+C+E/C1 |
| DTC-BUS-005 / 004 / SITE-TC-012..016/023 | 设备活动任务/联锁ON、两操作员旧版本；1切模式/禁申请点；2强完缺理由/审批；3并发旧v写；4重启恢复 | 不安全拒绝或按既定安全暂停；未审批/旧v写0；软件EStop解除不代表硬件安全；先对账再恢复无重复命令 | P0/B+C+E/C1 |
| DTC-BUS-006 / 004 / SITE-TC-019..022 | 1000个测试资产含10未定位、20Stale及重叠区域（仅压力夹具非SLO）；1关闭部分图层；2队列过滤/定位；3键盘选择；4离线控制 | 全部资产可从范围/队列发现、计数可解释；未定位不丢；陈旧/离线不能用缓存控制；有权命令经owner且只展示回读结果 | P0/E+B+C/C1 |
| DTC-BUS-007 / 005 / SITE-TC-024..026 | 同(sourceS,occ1)送3次；另occ2；A1清除；I1无位置/陈旧；1送达；2确认/指派；3升级；4过期版本转派 | occ1一个issue，occ2独立；告警清除不解决；未定位可见；指派/升级历史完整、旧版拒绝，无命令/库存副作用 | P0/B+E/C0 |
| DTC-BUS-008 / 005 / SITE-TC-027..028 | I1 AwaitingVerification；变体仅Accepted/旧回读/错对象/WMS差异；1解决；2新鲜正确设备证据+WMS核对；3同occurrence新证据复开；4新occurrence | 无效证据拒绝解决；有效后仅一次Resolved；复开留历史；新occurrence不静默并入旧问题；审计/责任/来源保留 | P0/B+C+E/C1 |
| DTC-BUS-009 / 004/005 / E2E-TC-007/009/011/013 | 设备故障→alarm→issue，合法处置commandC1；1地图定位；2申请/审批；3命令超时；4新鲜回读；5必要WMS对账 | 链路告警/问题/command/Leg/WMS可追；超时不解决；最终证据才闭环；所有拒绝有安全原因/关联ID，普通用户不能绕接口控制 | P0/B+C+E；H另批/C1 |

## 4. 未决输入与交接

现场动作能力/复核等级、联锁/急停边界、handover握手证据、状态新鲜度、最大资产规模、地图性能门限、issue SLA/升级对象需签核。不要把“尽可能一个地图处理”解释为把所有配置塞进画布；复杂诊断可展开但保持对象/定位。实现先运输/issue状态机与owner契约，再地图投影与动作面板。
