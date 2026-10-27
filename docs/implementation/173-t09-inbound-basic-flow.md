# T09 入库基本流程

- 状态：实施中；不因本片领域测试通过宣称 HTTP/页面或持久账本集成完成。
- 需求：FUNC-WMS-002 / PAGE-WMS-003；DTC-WMS-002/003，VAL-01。
- 基线：[WMS 规格](../modules/wms-and-ledger.md)、[规则](../detailed-design/05-approved-rules-and-defaults.md)、[交付顺序](TodoList.md)。

## 最小完整目标与顺序

先构建 WMS 入库单与收货单元的状态不变量，再接 owner 事务、当前授权 HTTP 和具名页面。基本链路为多行草稿→审核→分次收货→收货位唯一入账→同址完成或异址待运输；异址仅在核验最终到达后移账。T15 未接入时明确待运输，不能显示假运输创建成功。

本片先实现内部领域状态机及纯领域测试，不开放未经持久幂等/当前权限保护的写 API。后续事务必须将收货事实、不可变账本、原操作回执与 Outbox 同一次提交，不得先调用会自行提交的 PostStock 再另写单据。原 ledger 校验/保护逻辑应复用，不复制第二个记账器。

## 领域合同

- 显式 tenant/warehouse、单号及有界多行；行固定 item/unit/precision/batch，计划为正且精确、不舍入。收货必须匹配完整维度，零超收。
- 收货单元固定原 operation、actor、execution、行、容器、收货/目标位及数量。相同原键/执行及语义重放返回原单元，换 payload/actor 拒绝，不以 correlation 改写事实。
- 收货按 2+3 满足计划 5；2+4 拒绝且无残余。部分收货不自动关闭剩余。同址无需 transport intent，异址保持原收货事实和唯一意图。
- 最终到达要求原单元、已关联运输、源/目标及容器一致；错反馈不推进，重复正确反馈无第二状态效果。只有全部计划已收到且全部单元到达才 Completed。
- 领域对象不访问 Pack、HTTP 或数据库，不声称进程内去重等于持久幂等。存储需 tenant 复合键、版本 fence 和永久唯一 operation/execution；未实现前不开放应用入口。

## 测试与后续接线

先以缺失类型建立 RED，再验证 5 分收 2+3、2+4/错物料/精度拒绝、同址无运输、异址等待、原键重放/冲突、错运输/容器/位置、重复到达和多行不提前完成。owner 接线还需 MySQL 原子回滚、并发超收、永久重放和当前权限测试；页面/HTTP 未实现保持待办。

## 本片已执行证据

- 缺失 InboundOrder / InboundReceiptCommand / InboundPlanLine 的编译 RED 已实际执行。
- 实现后首轮 117 通过/1 失败：数量值对象按既有合同抛 ArgumentOutOfRangeException，测试误要求精确 ArgumentException；改为允许 ArgumentException 派生类型，未放宽业务数量规则。
- 最终 `FullyQualifiedName~Wes.Modules.Tests.Wms`：118 通过、0 失败/跳过，其中本片新增 6 项。覆盖既有数量、保护、余额和主数据领域测试。
- 架构 29 项通过；完整 Release 构建 0 警告/0 错误，新增 C# whitespace 检查通过。
- 无迁移/HTTP/公开写入口，未将纯领域测试当作持久并发、库存事务或完整 T09 的验收。
