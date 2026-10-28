# 通用开发与测试

首次记录/更新：2026-10-28。维护方式见[索引](README.md)；记录的是项目中可复用的失败模式，不代替模块合同。

## GEN-001 已提交重放与新动作校验必须区分

- **已复现并修复**；适用：可信入库运输完成Inbox。
- 现象：运输已Applied，之后盘点锁生效；原完成事件恢复被新锁错误Blocked。
- 处理：先验证body/intent/可信adapter身份，对已Applied同一原事件恢复不可变posting，不再执行新移动；未Applied仍检查所有当前保护。绝不能将“重放优先”变成绕过当前身份/权限/关联校验的公共捷径。
- 验证：`AppliedTransportCompletionReplaysWhileLaterCountProtectsItsContainer`已有RED/GREEN；同时保留新执行被锁拒绝及换tuple冲突。
- 证据：[183](../implementation/183-t11-blind-count-and-local-lock.md)、[184 §5](../implementation/184-t11-blind-count-tests.md#5-独立审查与关联修复)。

## GEN-002 Unknown恢复上下文不能跟随查询组件卸载

- **已复现并修复**；适用：主数据/附件/出入库/盘点页面的父子组合。
- 风险：写提交使详情stale/清空，若确认面板在QueryState分支内会被卸载，丢失唯一原key/body；子组件反向关闭父预览也可能取消自身。
- 处理：原操作面板生命周期独立于详情loading/error/stale；冻结原kind/id/key/body，主体或目标变化清私有数据并隔离迟到响应。无实体不挂载新的业务写；恢复原Unknown按其合同核当前权限，不要求把原tuple替换成刷新后的版本。
- 验证：必须测真实父子组合“预览→提交清详情→Unknown保留→手工原键恢复→显式GET”，不是只测孤立确认组件。不上线自动补发，不存本地写队列。
- 证据：[172](../implementation/172-t07-current-entity-attachment-client-tests.md)、[174](../implementation/174-t08-warehouse-operational-state.md)、[184](../implementation/184-t11-blind-count-tests.md)。

## GEN-003 枚举/契约扩展必须贯穿所有消费者

- **已复现并修复**；适用：ledger新增AdjustmentIn=3/AdjustmentOut=4。
- 现象：后端合法产生调整流水，旧客户端严格parser不认识类型，导致历史整页拒绝。
- 处理：同步DTO/类型、parser白名单、正负符号守卫、筛选、标签和实际页面；保留未知值明确失败，不能直接把parser改成接受任意数字。
- 验证：真实调整响应形状、正入/负出及错误符号、筛选和标签。两例RED后修复；只改enum不是完成。
- 证据：[183](../implementation/183-t11-blind-count-and-local-lock.md)、[184 §5](../implementation/184-t11-blind-count-tests.md#5-独立审查与关联修复)。

## GEN-004 当前权限不等于创建时权限，也不只看单据creator

- **基线约束**；适用：受权业务owner及原tuple恢复。
- 规则：核当前主体、租户、字段和真实资源creator范围；父仓/位置/容器/物料等跨creator组合需要完整权限交集。预览/旧令牌/成功原操作不能永久授予新写权限。
- 处理：按合同在写入后提交前再次核验，撤权整事务回滚；读后复验清旧投影；独立review不能通过盲盘响应泄露账面量。
- 验证：逐字段缺权、不同creator/部门、保存后撤权、越租户、撤权后重放和复核清私有数据，不能仅测管理员happy path。
- 证据：[177](../implementation/177-t09-authorized-inbound-execution.md)、[183](../implementation/183-t11-blind-count-and-local-lock.md)、[184](../implementation/184-t11-blind-count-tests.md)。

## GEN-005 原子审核、物理完成与数据库回滚不是同一层

- **基线约束**；适用：下一TransferOrder及运输集成。
- 规则：审核全行同事务，不留部分有效预留；已实际完成的执行单元独立提交，不能因其他行失败“回滚物理搬运”。实物已到而记账失败须保留可恢复完成事实及来源/目标保护，显示待同步。
- Unknown/在途不能回退可用；部分已执行取消剩余要ClosedPartial，不伪造Completed/全单Cancelled。没有实际adapter不得补一个默认成功或人工Completed接口。
- 验证：两行任一不足全不审核、A完成B失败保留A、已到落库失败仅补一次、Unknown仍保护、取消只关闭安全未执行部分；列出的未来用例不是本知识库已执行证据。
- 证据：[规则基线 RULE-07/08](../detailed-design/05-approved-rules-and-defaults.md)、[179](../implementation/179-t09-durable-inbound-transport-design.md)、[TodoList](../implementation/TodoList.md)尚未完成软件事项。

## GEN-006 验证准确门槛和副作用，不能只看HTTP成功

- **已复现并修复 / 基线约束**；适用：所有切片收口。
- 失败模式：普通build代替架构编译、mock代替真实Provider、一个浏览器happy path代替并发/未知恢复、只看200不查余额/流水/事实/锁；T11首次完整回归揭示18个精选漏掉的问题。
- 处理：改共享底层必须覆盖原调用路径；行数上限测真实100行全链，响应丢失必须是服务端已提交后截断交付，再手工同key/body恢复，确认效果恰一次。
- 证据分别记录命令/选择器、环境、通过/失败/跳过、持久结果及未运行范围；重叠选择器不相加称新增覆盖。审查报告不能代替测试；旧失败保留，末尾追加修复最终结果。
- 证据：[工程治理 §6](../03-engineering-governance.md#6-测试质量门禁和-pr)、[182](../implementation/182-t10-outbound-basic-flow-tests.md)、[184](../implementation/184-t11-blind-count-tests.md)。

## GEN-007 工作树/附件路径/外部文档与真正缺文件要区分

- **已观察；文档路径检查约束**；适用：当前独占worktree与文档链接检查。
- 现象：附件有时把当前worktree前缀再次嵌套，报ENOENT；真实仓库docs仍存在。README/feature-switches的旧外部Seven.Master目标在此worktree也可能不可用。
- 处理：以当前仓库根为准定位一次实际路径；不创建重复worktree层、不改其他聊天目录、不把错误附件路径当业务文档缺失。检查文档相对链接时区分新增断链与基线缺外部目标，并如实报告两者。
- 验证：本次请求附件即出现重复嵌套；T11最终文档检查11份、0新增断链、7个基线外部目标不可用。历史数字仅对应当次工作树，不沿用作本次检查结果。
- 证据：[184 §6](../implementation/184-t11-blind-count-tests.md#6-最终发布门禁与关闭范围)。外部来源仍按内容访问限制处理，不能为补缺失而越权读取。
