# T05 受权消息重放客户端

- 状态：已验收，仅本片范围。
- 范围：REQ-NTF-004..008、PAGE-NTF-003/FUNC-NTF-001 中 Platform 安全审计重放界面。
- 基线：[消息运维](39-t05-platform-message-operations.md)、[只读客户端](127-t05-platform-message-diagnostics-client.md)、[详细设计](../detailed-design/notifications-and-jobs.md)。
- 测试：[本片测试](130-t05-authorized-message-replay-client-tests.md)。

## 决策与不变量

复用既有POST，不新增后端能力、默认权限或消息身份。只有当前 `platform.messages.replay` All=2 范围字段并集包含 EventId/Status/Attempts/Reason，且read的All字段含完整必要字段及 ContractName/SchemaVersion/Attempts，才可准备。选择按钮仅对当前新鲜页的 Platform/platform.audit/v1、Quarantined、attempts>=1 行出现。

点击准备后，先重新GET同一实时页并重新检查事件仍在当前授权页且满足条件，不把旧列表当写许可。翻页期间事件移走则明确无法准备；expectedAttempts来自这次新读取。后端仍执行当前权限、合同/状态/attempt CAS及事务重放，不声称客户端预读可防服务端竞态。

独立面板：输入3..256个可打印字符理由（按后端Trim后UTF-16长度）；预览固定eventID、expectedAttempts、理由与新operation UUID；二次确认后才发送。POST只含 expectedAttempts/reason，显式 Idempotency-Key 固定为预览UUID，不接收/发送tenant、actor、payload或新事件ID。

网络失败/503/取消/非法响应不能推断提交失败，显示“不明”，冻结请求内容，允许只由操作者显式重试原operation与原请求；不自动重放。400/401/403/404/409明确显示本次拒绝且要求重读；若此前结果不明，拒绝也不证明先前未提交。确认成功只接受既有 `decision:5, attempts:0|null` 两字段，表示已提交或原操作幂等识别，不代表Broker再次确认/消费/业务完成。

离线禁写且不排队，恢复不自动重试。当前身份/会话/grants变化清除旧预览与行、取消请求并忽略迟到结果；授权撤销不能复用本地许可。面板打开时禁用主列表读取/分页/其他准备动作；任何提交立即将原列表标陈旧，成功后清旧行但保留成功回执说明，显式关闭后才可重读。显式关闭/导航/刷新可能丢失内存操作上下文，界面提前显示operation供人工记录，并明确未知结果不可凭新键盲目再发。不保存请求或理由至localStorage/sessionStorage。

## 覆盖边界

页面无payload/物理消息重放，不能选择未知contract/schema。重放审计由原owner永久操作回执承担，保留原事件身份与payload；客户端不伪造跨owner审计或新事件。暂不实现跨owner聚合/过滤/Inbox统计，真实Broker故障验收仍阻断，T05不关闭。
