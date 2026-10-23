# DeviceIntegration 页面、功能与详细测试

- 状态：评审中；DTC均designed/未执行
- 权威：[DeviceComm](../modules/device-communication.md)；SCOPE-DEV-01；REQ-DEV-001..010
- 共用：[标准](00-shared-page-and-test-standard.md)、[案例限制](01-reference-cases.md)
- 产品阅读：受权集成工程师的配置/运行诊断，不是普通运维裸写台；DENSITY=3、DATA_COMPLEXITY=3、CONSEQUENCE=3。

## 1. 页面

| PAGE定义 | 入口/角色 | 线框/字段/校验来源 | 查询/动作 |
|---|---|---|---|
| PAGE-DEV-001 | `/devices/connections`；连接配置员 | L+F：连接 → 配置版本 → 状态/generation → 最近失败。协议、端点引用、设备身份、Rack/Slot或UnitId、secret引用、启用/期望连接；按驱动语法验证 | 协议/连接状态过滤；保存草稿/连接管理需权限与生产审查；无“连接成功=设备安全”提示 |
| PAGE-DEV-002 | 连接详情点位页；只读观察员/配置员分权 | L+F：点位表 → 地址/类型/单位/缩放 → 值/质量/时间。PointCode唯一、所属连接、结构化地址、可读写能力、范围、配置版本；配置不是实时值 | 名称/地址/质量过滤；批量配置导入如无批准用例不提供；生产不开放任意值裸写 |
| PAGE-DEV-003 | `/devices/rules`；规则管理员 | L+F：白名单DSL → 步骤/点位/触发源 → 版本验证。Monitor/MonitorThenRead/WriteThenAwait/Sequence、运算符、最大步骤/时间、停止条件、授权写集合 | 保存/验证/发布新版本；受控触发必须引用审批/操作原因；禁止任意脚本/无界循环 |
| PAGE-DEV-004 | 地图设备/`/devices/executions/:id`；受权诊断 | O：执行/command → 步骤/generation → 读写/await证据 → 超时/Unknown。执行ID、点位摘要、请求/确认时间、writer状态；owner Device | 运行/失败/Unknown过滤；查询/停止申请按支持能力，停止请求不代表物理停止；回地图保留设备 |

001/002/003适用UI-001..011（只有启用已批准控制能力时涉及C1）；004适用UI-001..008/011/012，有停止申请则另UI-010。002实时值与配置字段分区，Unknown不渲染0；003旧版执行继续旧配置；只读诊断没有“清除错误继续写”。

## 2. 功能

| FUNC定义 | PAGE/原TC | 主流程与失败 |
|---|---|---|
| FUNC-DEV-001 | 001/002；DEV-TC-001..005/013 | 1按协议解析地址/类型/范围；2验证端点/设备身份/secret；3连接generation递增；4按连接声明串行/有界并发IO；5读错误/断连有界退避。旧generation响应不能更新新会话；Disabled明确不可用 |
| FUNC-DEV-002 | 003；DEV-TC-007..008/014 | 1白名单DSL与点位权限/上限验证；2发布不可变版本；3触发固定快照；4Monitor周期不重叠失控并输出受控事件。规则不拥有库存/任务终态；更新不影响旧执行 |
| FUNC-DEV-003 | 003/004；DEV-TC-009..011 | 1校验writer/授权/运行模式/联锁/点位可写/值；2持久execution/step/目标/command；3语义Adapter写；4当前generation/sequence反馈满足await才完成；5写超时/中途错/取消Unknown或Failed并查询。非幂等写不自动重试 |
| FUNC-DEV-004 | 001/004；DEV-TC-006/012..015 | 1单写宿主检查；2恢复在途执行与物理状态；3推送中断从查询恢复；4告警防抖/聚合；5逐真实/虚拟Adapter运行同语义套件。多API副本不各自开PLC写连接，不把Route epoch未经验证当作协议安全保证 |

状态：Disabled/Disconnected/Connecting/Connected/Faulted/Reconnecting；连接层Connected不把业务层Unknown改为成功。规则反馈契约包含execution、step、command、device、generation/seq、occurred/received、schema；写凭据只由Adapter获取。生产人工触发必须在既有授权运维流程或批准维护窗口，不提供绕过Pack的同一设备并行控制通道。

## 3. 详细测试

固定隔离PLC模拟器D1、Modbus Unit1和Step7 Rack/Slot，PointP1可读、P2批准可写、P3只读，RuleR1两步骤、writerA、generation5；实际地址和值范围取驱动/现场签核夹具，不编造通用PLC地址语义。

| DTC定义/功能/原TC | 前置/编号步骤 | 断言 | 级别/环境/清理 |
|---|---|---|---|
| DTC-DEV-001 / 001 / DEV-TC-001..002 | 各驱动合法/边界/非法位偏移/类型/缺凭据/错身份；1验证配置；2连接 | 非法在IO前拒绝；正确地址映射与驱动规范一致；错身份不Ready、不写；UI/日志无secret | P0/U+C+E/C1 |
| DTC-DEV-002 / 001 / DEV-TC-003..005 | 同连接100次交错读写、另一连接并行；1执行；2断连/延迟旧gen5响应；3重连gen6 | IO遵守协议并发限制/队列上限；响应不串线；旧响应不改gen6值；退避有界；写超时不自动成功 | P0/C+B+E/C1 |
| DTC-DEV-003 / 002 / DEV-TC-007..008/014 | 合法Monitor/MonitorThenRead；非法op/越权点/无限步骤变体；1发布；2执行时发布v2；3周期超时 | 非法发布0；旧执行固定v1、新执行v2；扫描不无界叠加；输出快照与触发条件一致 | P0/U+B+C+E/C1 |
| DTC-DEV-004 / 003 / DEV-TC-009..010 | R1写P2后await；变体满足/中途错/写已生效响应丢失/取消/重复触发；1启动K1；2注入；3Query；4恢复/同K重试 | 持久执行先于写；效果最多1；await仅当前代反馈通过；未知不重放；后续步骤不跳过，审计/失败原因完整 | P0/B+C+E/C1 |
| DTC-DEV-005 / 003/004 / DEV-TC-006/011 | A/B双写宿主，U2无控制权，P3只读/模式禁止变体；1同时申请写；2直接触发R1；3失writer后新写 | 仅授权主能写；其他实例不打开写连接/宿主；拒绝副作用0；模式/联锁不被UI补齐；有安全审计 | P0/B+C+E/C1 |
| DTC-DEV-006 / 004 / DEV-TC-012..013/015/ARCH-TC-006 | SignalR断线、告警抖动，DeviceComm/HotStore/Alarm开关组合；1执行；2重连；3对虚拟/每个已实现且批准的真实Adapter跑同语义命令/反馈场景及不支持能力变体 | 查询恢复当前状态；禁用明确、不假Connected；告警按批准防抖；不支持明确拒绝而非成功；虚拟通过不标真实通过；每协议版本单独证据 | P0/C+E；真实H需批准/C1 |

## 4. 输入门禁

允许点位/缩放/endianness、扫描率/IO串行规则、DSL步数/时限、命令幂等、物理恢复/停止能力、网络隔离和单写者部署必须设备方签核。规则输出失败可观测，不能让消息渠道失败导致再次写PLC。上手优先地址验证/连接代次→规则验证→写前耐久→未知恢复→页面。
