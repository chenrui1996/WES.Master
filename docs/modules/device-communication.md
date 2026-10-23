# DeviceComm 设备通讯详细设计

- 状态：目标设计；Seven 有 Step7/Modbus TCP 与规则引擎基线，生产控制闭环与多写者尚非已完成能力
- 范围追踪：`SCOPE-DEV-01`
- 基线参考：[Seven DeviceComm](../../../Seven.Master/doc/16-设备通讯DeviceComm.md)、[设备驱动测试](../../../Seven.Master/Seven.Net8/Seven.Tests/Unit/DeviceCommDriverTests.cs)、[WCS 包与 Phase H](../../../Seven.Master/doc/19-WMS与WCS包.md)
- 测试前缀：`DEV-TC-*`

## 1. 职责与安全边界

DeviceComm 管理协议连接、设备身份、点位定义、读写、连接状态、规则执行、事件/告警和运行诊断。它是设备适配基础设施，不拥有 WMS 库存、WCS 任务状态、Route 决策或运输单据。

Seven 文档声明支持 Step7 与 Modbus TCP，且存在驱动地址解析测试；但 WCS 包文档把真实 TriggerPort/通讯替换与多写者列为 Phase H。WES 必须区分“通用驱动/配置能力存在”和“某一现场设备已通过联调/安全验收”。

**初期 Single Writer**：一个经授权的主实例运行设备写控制/规则宿主。多个 Web/API 副本可提供只读 API，但不能各自打开相同 PLC 或执行写规则。主实例选举/租约在验证之前不支持 active-active。

## 2. 连接/点位模型

- Connection 配置协议、端点、设备身份、Rack/Slot/UnitId 等协议参数、启用和期望自动连接；凭据只引用秘密存储。
- Point 有稳定唯一 Code、类型、地址/结构化地址、缩放/单位、读写能力和所属连接；协议地址必须按驱动语法验证，不能执行任意表达式。
- 运行态连接状态：`Disabled → Disconnected → Connecting → Connected → Faulted/Reconnecting`。状态含最后成功/失败时间、原因、尝试次数和连接 generation。
- 每条连接的底层 IO 明确并发模型；若协议会话不可并发，按连接串行化读写并设置有界队列/超时。
- Point 更新和配置变更有版本；规则执行期间读取一致配置快照，更新策略不得导致半旧半新执行。

## 3. 安全规则引擎

规则仅允许固定、可审计的 DSL 类型，例如 Monitor、MonitorThenRead、WriteThenAwait、Sequence，以及 `eq/ne/gt/gte/lt/lte/changed/rising/falling` 和 `all/any`。不允许任意代码、动态脚本、未经白名单校验的点位写入或不受限循环。

每条规则有 owner、设备/连接范围、触发源（周期/人工）、版本、读写点集合、最大步骤/持续时间、超时、停止条件、可重试分类和输出事件。人工触发需单独权限；输出 EventLog/SignalR/Alarm/HotStore 是受控副作用，有独立失败处理。

对 `WriteThenAwait` 和 `Sequence`：

1. 写命令前校验操作授权、运行模式、设备在线、点位可写、值范围和安全互锁。
2. 将执行 ID/步骤号/目标值持久化后再执行写操作；超时需查询设备状态，不能盲目重复。
3. 仅在条件满足且反馈来自当前执行 generation/sequence 时完成。
4. Abort/timeout/断连将执行标为 Unknown/Failed 并要求安全恢复策略；不把未确认写操作记为成功。

规则读取/写入 PLC 不是 safety PLC 逻辑；急停/安全联锁必须由经过安全评估的硬件/PLC 实施。

## 4. 重试、重连和状态广播

- 连接、IO、规则执行分别有超时和重试预算；指数退避加上限和抖动，避免断连风暴。
- 写操作必须声明幂等/非幂等。对非幂等控制命令，传输超时应进入结果未知，不自动重放。
- 连接重建提升 generation；旧 generation 的延迟响应不得更新当前点位/规则状态。
- SignalR 是实时观测，不是状态存储；客户端断线重连从授权查询加载最新状态。
- 断连告警需防抖/恢复清除/重复聚合策略，避免连接抖动造成告警洪水。
- DeviceComm Disabled 时调用返回明确 Disabled/NotSupported；不可由空替代实现返回伪造 Connected/成功。

## 5. 部署与运维

- 启动先验证启用模块需要的驱动/证书/地址/设备配置；必需配置错误使 readiness 降级或阻止控制宿主启动。
- 配置控制写入权限，与普通用户/查询权限分离；审计谁修改端点、点位、规则、启停连接、触发写入。
- Single Writer 部署展示当前写主、租约/心跳、设备连接状态和最近写命令；故障切换前需核验在途物理命令。
- 规则事件日志脱敏且限保留期；大量高速点位由采样/聚合而非无限持久化解决。
- 生产环境准备、Simulator 仿真及真机 Promote 流程见 [环境搭建、仿真与 SCADA](environment-simulation-and-scada.md)；WCS 控制模式与现场运维命令见 [Orchestration/WCS 运维设计](orchestration.md#8-wcs-运维协调边界)及各 Pack 规格。

## 6. 详细验收用例

| ID | 场景 | 期望 |
|---|---|---|
| DEV-TC-001 | Modbus/Step7 合法地址、边界地址、错误类型/位偏移 | 地址解析一致；非法值在连接前拒绝 |
| DEV-TC-002 | 缺失凭据/错设备身份/不安全 endpoint | 启动/连接失败明确；秘密不出现在日志/API |
| DEV-TC-003 | Connect/read/write 正常、超时、设备断连 | 状态机和错误时间正确；写结果未知不自动成功 |
| DEV-TC-004 | 同连接并发 IO、不同连接并发 IO | 协议并发限制符合声明，无响应串线 |
| DEV-TC-005 | 连接重试/指数退避/重连期间旧响应返回 | 重试有界；旧 generation 响应被拒绝 |
| DEV-TC-006 | 两个实例同时申请 SingleWriter | 仅一个能写；未选主实例不打开写连接或规则宿主 |
| DEV-TC-007 | Rule DSL 合法/未知操作符/非法点位/无限步骤 | 只执行白名单 DSL；非法规则发布失败 |
| DEV-TC-008 | Monitor / MonitorThenRead 正常和扫描周期超时 | 条件和输出快照正确；扫描不重叠失控 |
| DEV-TC-009 | WriteThenAwait 条件满足、超时、断连、写超时 | 结果与设备确认一致；未知状态进入核查，不盲目重复写 |
| DEV-TC-010 | Sequence 中间步骤错误/取消/重复触发 | 执行状态可追溯；幂等命令不重复，非幂等副作用不自动重放 |
| DEV-TC-011 | 未授权点位写入、越权规则触发、运行模式禁止 | API/执行器服务端拒绝且审计 |
| DEV-TC-012 | SignalR 断线/恢复、报警重复/恢复 | 状态从权威查询恢复；告警防抖与确认逻辑正确 |
| DEV-TC-013 | DeviceComm/HotStore/Alarm 组合开关关闭 | 明确不可用语义；不返回伪成功、不启动宿主 |
| DEV-TC-014 | 规则发布更新时已有执行实例 | 已运行实例使用固定版本；新实例使用新版本 |
| DEV-TC-015 | 物理设备 adapter 与虚拟 adapter contract suite | 同语义状态/错误一致；仿真通过不被标记为真实设备验收 |

## 7. 现场验收输入

每个设备需确认驱动/固件版本、网络隔离、允许读写点、数据类型/单位/缩放、最大扫描率、命令幂等性、超时、重连/安全状态、Single Writer 部署策略、急停责任和现场验收负责人。
