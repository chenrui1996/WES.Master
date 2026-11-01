# T14 告警原始 occurrence 耐久接收

- 状态：原始事实接收子片已实现验证；2026-11-01。Owner：Notifications；父T14未完成。
- 依据：[已批准软件计划T14](../superpowers/plans/2026-10-23-wes-development-work-packages.md)、[告警规格](../modules/notifications-and-jobs.md)、[详细设计](../detailed-design/notifications-and-jobs.md)、[RULE-14](../detailed-design/05-approved-rules-and-defaults.md)。
- REQ-NTF-009..014、FUNC-NTF-003、DTC-NTF-004/VAL-06原事实子集；[测试记录](192-t14-durable-alarm-occurrences-tests.md)。

## 1. 范围、设计选择和停止条件

T12管理合同仍gated，不阻断独立T14。沿已批准告警事实与投递/issue分离规则，先实现真正的tenant-scoped耐久接收，不添加自动送达/清除/设备联锁效果。新Notifications模块只拥有原始occurrence；不会引用Platform/WMS/Pack私表，不改旧消息或旧账。

只提供内部强类型owner入口，不注册Host/HTTP/UI/worker，客户端不能自报来源证明或tenant。后续可信adapter认证源/tenant/资源后调用；本片不声称源认证/用户授权已实现。需要当前权限的确认/清除/静默、30秒聚合及delivery/SignalR/邮件/公告/Quartz仍是T14主体待办，不关闭父DTC/T14。停止条件为下列真实MySQL验收、完整Release/架构/迁移SQL/格式和文档同步，而非整个项目完成。

## 2. 身份、原事实及错误

- 每个context固定非空可信tenant；主键tenant+owner生成occurrenceId，唯一键tenant+sourceOwner+sourceEventId。来源事件UUID非空；owner为1..64 ASCII字母开头后字母数字下划线，严格全字符串匹配。
- 输入含sourceEventId、sourceOwner、sourceKind（Device/Manual）、resourceId非空、code（1..64 ASCII字母开头后字母数字下划线/点/短横）、severity（Information/Warning/High/Critical）、occurredAt、correlationId非空、summary（1..512 UTF-16代码单元、非纯空白）。不存任意原协议payload/凭据；summary由可信来源脱敏，后续受权查询仍须字段权限。
- occurredAt须UTC（offset0）且不晚于接收clock时刻；源时刻以UTC ticks bigint精确保留，避免datetime(6)截断第七位改变源事实/重放判断；接收时刻UTC显式向下取微秒后按MySQL datetime(6)保存。occurredAt只表示源事实时间，不覆盖设备新状态；迟到事实允许持久化且保留原时刻。源未来时刻拒绝，不静默改成当前。
- occurrence immutable：source tuple对应所有输入（包括correlation/summary/时刻）精确一致才Replayed原id；变参同源event为IdempotencyConflict，不改旧事实；不同event即使同code/resource/time也各落一条，不将聚合窗口当事实去重。
- 返回Recorded/Replayed、immutable原事实；Invalid只返回稳定code、无值；Conflict无成功值。未知枚举/空ID/越界/非法来源/时间拒绝，错误不暴露原输入。owner必须干净独立工作单元，无tracked变更/外部transaction，避免连带提交其他写入。
- MySQL唯一约束收敛同事件并发；仅duplicate key路径清理失败tracking并读赢家比较输入，其他Provider异常显式抛出，不catch成成功、空数据或重试物理动作。所有IO传CancellationToken。
- DbContext所有SaveChanges重载拒绝改/删原事实及跨tenant添加。内部append允许独立插入，单次原事实插入为原子事务；同源ID长期保留，没有普通TTL删除/“无告警”默认。

## 3. 迁移与验收

新增单表`ntf_alarm_occurrences`、独立历史`__WesNotificationsMigrations`，source owner/code二进制排序，唯一tenant/source/event索引、tenant/time/id查询索引。设计时与运行时一致；增量Up/Down SQL审查，仅授权wes_test应用Up，Down只审不执行。

| ID | 真实场景 | 断言 |
|---|---|---|
| TC-T14-OCC-01 | 同event原输入重复/变summary或resource或time | 原id重放、一条事实；变参Conflict，不覆盖 |
| TC-T14-OCC-02 | 不同event同code/resource；迟到源时刻 | 每条都保留，源时刻不替换receivedAt |
| TC-T14-OCC-03 | 同event同时接收/变参竞争 | 恰一赢家，其他Replay或Conflict，零重复 |
| TC-T14-OCC-04 | 同event另一tenant/越tenant直接Save | 正常owner隔离；非法跨tenant副作用0 |
| TC-T14-OCC-05 | 接收后save故障/预取消 | 无半份事实；取消显式传播 |
| TC-T14-OCC-06 | 四Save重载改/删/非空工作单元 | 原事实不可变，不偷提交其他变更 |
| TC-T14-OCC-07 | 文本/枚举/ID/UTC/未来边界 | 1/512允许、513拒绝；非法无事实 |

先RED再实现；适用INF-003/004/005、DEF-011/012/016、GEN-006。本片不添加所有权之外的授权、告警状态机或新默认送达行为。

## 4. 实施结果与接线边界

实际入口为`Features\Alarms\AlarmOccurrenceStore.RecordAsync`，模型与store为模块内部类型；未添加Host引用/注册、HTTP、用户权限种子或worker。本片固定数据库owner与取消/异常合同，不提供可被客户端自报来源的公共接收入口。

- 44项本片真实MySQL测试通过，连同既有Workflow22共66；架构29通过。具体RED/GREEN、最终门禁及未执行检查见[192](192-t14-durable-alarm-occurrences-tests.md)。
- 外部事务包括`Database.CurrentTransaction`与`Transaction.Current`，不能借ambient事务带入owner。补测1项先RED后修复。
- 首次迁移的通用collation只出现在C#模型，不出现在Provider实际SQL；大小写来源与实际列元数据2项RED。保留已在隔离测试库应用的首次历史，通过后续`BinaryAlarmOccurrenceColumns`迁移修复，不回滚/清共享历史。模型配置MySQL专用collation/charset；Provider不生成annotation-only ALTER，按既有Platform先例显式限定本表四列的ALTER。最终SQL和数据库四列binary collation都验证，不能只以无模型漂移作证。
- 原时刻bigint ticks保留第七位；返回前receivedAt向下取微秒，首次回执与持久重放完全一致。唯一键竞争只捕获1062并回读赢家，不重试INSERT/设备动作；其他数据库失败抛安全异常且不保留Provider内层敏感消息。

下一主体为T14原事实之上的聚合/告警生命周期、当前授权和真实渠道接线；Jobs/公告/缓存诊断按原计划保留。T12管理permission/field合同仍gated，T13实例/hook、T15运输依赖和T09–T11既有软件缺口不被本片关闭；生产/现场许可不变。
