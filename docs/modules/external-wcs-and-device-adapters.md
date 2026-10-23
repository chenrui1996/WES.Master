# 外部 WCS 与设备适配器详细设计

集成页面、功能流程与逐步骤用例：[外部WCS深化设计](../detailed-design/external-wcs-and-device-adapters.md)。原文仍为业务边界依据，深化文档为评审中、测试未执行。

- 状态：目标设计；Seven 有 HTTP/MQ/Codec/Pack 抽象，当前完整设备闭环和异常语义需加强
- 范围追踪：`SCOPE-WCS-04`、`SCOPE-DEV-01`、`SCOPE-BUS-01`
- 基线参考：[WMS/WCS 包](../../../Seven.Master/doc/19-WMS与WCS包.md)、[WMS/WCS 实现说明](../../../Seven.Master/doc/20-WMS与WCS实现说明.md)、[DeviceComm](../../../Seven.Master/doc/16-设备通讯DeviceComm.md)
- 测试前缀：`ADP-TC-*`

## 1. 边界与契约

External WCS Adapter 把 Orchestration Leg 转换为供应商请求，处理认证、传输、Codec、超时、相关 ID、响应和 callback，再归一为标准 Leg 事件。设备 Adapter 把 WCS 的语义命令转换成具体协议/点位/报文并将遥测/反馈归一化。两种边界不同，但均不得让供应商协议泄漏到 WMS/WCS 领域模型。

公开语义契约至少定义：命令种类及能力、命令 ID、设备/Pack/车辆、Leg/Task 关联、map version、序号、发送/接收时间、反馈状态、错误分类、取消/查询能力和可重试性。

## 2. 外部 WCS 传输与 Codec

- 每个供应商配置有唯一 PackId、传输类型、Codec 版本、endpoint、认证引用、超时与能力集合；密码/Token 不写普通配置回显或接口日志。
- HTTP 客户端采用有界超时、连接管理、限流和标准错误映射；MQ 使用版本化消息并声明幂等键/队列与重试策略。
- Codec 只做 schema 映射、类型/范围验证和版本兼容；不能决定 WMS 库存或跳过 Orchestration 状态验证。
- 出站请求发送前先持久化 command/attempt/correlation，收到响应后记录供应商任务 ID。超时表示结果未知，不直接当失败重发。
- Callback 校验来源认证（签名/mTLS/可信网络按供应商契约）、租户/Pack、LegId、外部任务 ID、时间窗、签名 nonce/重放和状态版本。
- Callback 进入统一事件管道；重复、乱序、未知 Leg、非法状态、Codec 版本异常均拒绝/隔离并记录脱敏诊断。
- 接口日志需有访问控制和保留期；默认只存必要摘要/脱敏 payload。原始设备报文如确需留存，单独加密、授权和容量控制。

Seven 现有 `ExternalWcsPack` 有 HTTP/MQ transport、Codec 注册与日志，测试覆盖发出编码请求和已完成 callback。当前 Pack 的 Cancel 是 no-op、Query 返回 null；WES 必须把未实现能力明确标记为 Unsupported，不能成功形状返回。设备回调端点认证、签名/重放保护和具体供应商适配清单仍需逐项核验。

## 3. Adapter 生命周期与故障语义

Adapter 生命周期：`Configured → Connecting → Ready → Degraded/Disconnected → Recovering → Ready | Disabled`。健康状态区分进程活、网络可达、设备会话可用和业务命令能力可用；单一 bool 不代表设备安全可执行。

| 失败类别 | 处理 |
|---|---|
| 参数/Codec schema 错误 | 拒绝提交，保持待处理/配置异常，提示版本或字段 |
| Unauthorized/signature invalid | 拒绝且安全审计，不自动重试 |
| Timeout/connection reset | command 进入 Unknown，查询/去重后决定重试 |
| Rate limit/temporary unavailable | 有界退避；过载时不接受无界新任务 |
| Unsupported command/cancel/query | 明确能力错误，不伪装成功/完成 |
| Duplicate/out-of-order callback | 去重或隔离；不得回退/跨越状态机 |
| Stale position/telemetry | 标陈旧；Route/调度禁止把它当当前物理位置 |
| Adapter restart | 恢复会话和 durable command 状态，先核对在途命令再发送新命令 |

协议 adapter 只能控制其授权设备集合；连接凭据由 Secret Store 管理；安全急停/碰撞防护不能被通用重试/重连覆盖。

## 4. 仿真契约一致性

InMemory/Simulator adapter 实现与真实设备相同的命令/反馈 schema、错误语义和相关 ID。仿真可调度反馈延迟、丢失、重复、乱序、故障和恢复，用于确定性测试。仿真通过仅证明软件场景覆盖，不证明 PLC、机械制动、定位精度或现场网络安全。

设备连接/点位/协议控制面见 [设备通讯](device-communication.md)；跨包运维协调由 [Orchestration](orchestration.md#8-wcs-运维协调边界) 承担，外部 WCS/目标 Pack 必须对命令能力、当前状态及安全条件做最终校验。WCS 包只使用语义 adapter，不直接持有驱动对象。

## 5. 详细验收用例

| ID | 场景 | 期望 |
|---|---|---|
| ADP-TC-001 | 每个 Pack 配置、Codec/transport 不匹配、禁用 Pack | 启动/派发显式校验；不可用配置不接任务 |
| ADP-TC-002 | HTTP 成功、4xx、5xx、超时、重定向/不可信 endpoint | 错误分类正确；超时标 Unknown；禁止 SSRF/秘密泄漏 |
| ADP-TC-003 | MQ 未启用、断连、重复投递、死信 | 未启用明确 NotSupported；恢复可重试；重复消息幂等 |
| ADP-TC-004 | 合法/非法/未知版本 Codec 请求和回调 | 合法归一；非法不推进业务状态并留诊断 |
| ADP-TC-005 | Callback 缺签名、签名错、过期 nonce、错误 Pack/Leg | 拒绝且审计；状态/库存无变化 |
| ADP-TC-006 | 重复/乱序/先 Completed 后 Progress | 状态不回退；重复回调仅产生一次结果 |
| ADP-TC-007 | 外部 WCS 不支持 Cancel/Query | Unsupported 显示清楚；订单进入结果未知/人工处置，而非取消成功 |
| ADP-TC-008 | 设备连接断开、重连、凭据失效、设备身份错配 | 状态可观察；重连前核查在途命令，不向错设备下发 |
| ADP-TC-009 | 实时/过期/重复/乱序遥测 | freshness/sequence 验证正确；过期坐标不能用于路线 |
| ADP-TC-010 | 设备命令超时及重启恢复 | 先 Query/去重；不重复触发物理动作 |
| ADP-TC-011 | 虚拟与真实 Adapter 执行相同契约测试 | 结果/错误/状态语义一致，平台特有能力明确报告 |
| ADP-TC-012 | 日志包含 Token/设备凭据/个人信息或大 payload | 脱敏/拒绝/加密留存规则生效，访问受权限控制 |
| ADP-TC-013 | Adapter 容量满、外部退避、排队期限超限 | 有界背压与告警；过期物理命令不被自动发送 |
| ADP-TC-014 | Cancel 与 Completed callback 并发 | 状态机按命令/版本处理；冲突安全暂停并人工确认 |

## 6. 供应商逐项验收清单

每个外部 WCS/协议需有独立协议版本与设备矩阵、支持命令、签名/身份模型、幂等/查询/取消能力、超时/重试策略、异常码映射、仿真 Codec、试运行/回滚手册及现场 E2E 证据。只有 `FakeVendorCodec` 测试不代表真实供应商兼容或生产就绪。

已批准[规则基线RULE-17/23、PAR-11](../detailed-design/05-approved-rules-and-defaults.md)将上述能力默认标Unverified、真实写连接禁用；缺认证/时窗/必需容量/时限显式拒绝，物理Unknown不自动重发，普通消息重试预算不适用于设备。迟到可信事实经核验/对账，不覆盖更新观测；认证过期先隔离/query，新控制deadline过期则不发。
