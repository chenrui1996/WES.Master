# T05/T08 WMS 主数据创建 Outbox

- 状态：已验收（仅本片owner-local创建暂存，不是T05/T08整包）。
- 范围：REQ-NTF-004..008、PAGE-NTF-003/FUNC-NTF-001后续owner基础，REQ-WMS-001..004/PAGE-WMS-001/FUNC-WMS-001创建原语；不关闭完整DTC-NTF-001/002、DTC-WMS-001或WMS业务闭环。
- 基线：[消息/业务契约](../contracts/01-business-and-execution-contracts.md)、[创建授权](125-t08-current-authorized-master-data-creation.md)、[WMS设计](../detailed-design/wms-and-ledger.md)。
- 测试：[本片测试](136-t05-t08-wms-creation-outbox-tests.md)。

## owner原子创建与事件合同

WMS自身DbContext在验证每个新增默认禁用仓库/库位及其精确创建事实后，为每个事实派生一个待发布事件。采用Platform既有“owner持久化门禁由新增不可变事实派生Outbox”的模式，不能依赖HTTP写端点记得额外写消息。资源、永久operation/actor/correlation事实与Outbox同次SaveChanges并在原显式创建事务中提交；保存/提交前故障均不得留下部分成功。

新表 `wms_outbox_messages`，tenant/event复合主键；EventId等于主数据永久OperationId（一项operation只创建一个资源），同tenant/EventId外键引用创建事实的OperationId并Restrict。事件不能孤立插入/修改/删除，派生时既有Added事件必须逐字段与新事实匹配且只有一个。保存失败后同context显式重试复用已派生事件，不重复追加；成功创建的operation重放不再新增。不同tenant可使用同operation UUID。

固定envelope：EventId、TenantId、Owner=`Wms`、ContractName=`wms.master-data.created`、SchemaVersion=1、AggregateId（新仓或新库位）、AggregateVersion、CorrelationId、CausationId=null、OccurredAt、Payload、Status=`Pending`。该首片只提供不可变待发布暂存，不提前添加无运行路径的租约/重试字段，不声称已发布。后续publisher须另行设计有界退避、状态修改许可、租约竞争和ACK不明处理。

payload固定为 `{kind:"Warehouse"|"Location",warehouseId,locationId:null|UUID,packId:null|UUID,schemaVersion:null|string,code,enabled:false}`，仅安全主数据事实，不包含name、actor、会话或私有实体；schema与code保持原创建规范大小写。它是非物理事实，不是启用/可分配/设备命令或库存授权。consumer后续需当前权限/业务状态核验，不得直接当当前master data快照。

## 迁移与既有部署

新增仅WMS表/外键/tenant状态时间索引，无Platform/Notifications私表引用。不为历史创建事实自动伪造发生于过去的待发送事件；旧operation重放仍只返回原身份，迁移不会发布历史洪峰。无Host写端点、transport、scheduler或自动迁移；只在批准wes_test应用迁移验证。生产须DBA独立审查DDL/权限/回滚，MySQL DDL隐式提交，EF CLI默认历史表不能当实际owner部署脚本。

Outbox保留随源事实长期保留，首片无删除/清理；未来已发布保留需要单独审批，不删永久operation。回滚需先证明无消费者依赖及无未交付事实，不能用Down丢弃待发布事件。SQL Server/PostgreSQL按ADR-0002延后。

T05跨owner查询/统计、WMS消费/发布、真实Broker与T08运输/库存完成闭环仍未实现。现有Platform统计不混入WMS或用0冒充已接入。

## 已执行结果

新增17项真实MySQL用例，并扩展既有并发/跨tenant/晚期失败断言。最终WMS主数据owner141、WMS领域/HTTP/真实HTTPS Chromium100、架构29共270通过，无失败/跳过。Release完整solution构建0警告/0错误，EF编译后模型无漂移；实际information_schema证实复合FK、状态时间索引与datetime(6)。第一次额外DDL断言发现MySQL8返回被引用列名小写，按MySQL列名大小写不敏感语义修正断言，不改变生产代码或放宽业务字符串比较。详细命令、红灯和未跑范围见测试记录。
