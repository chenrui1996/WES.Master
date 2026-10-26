# T08 WMS owner账本与持久化核心

- 状态：实施中。Owner：WMS；不关闭T08整体。
- 依据：[WMS领域规格](../modules/wms-and-ledger.md) §1/7、[详细设计](../detailed-design/wms-and-ledger.md) FUNC-WMS-007 / DTC-WMS-004/010/011、RULE-01/08及[MySQL先行](../adr/ADR-0002-mysql-first-implementation.md)。

## 范围和调用前置

在既有精确数量和保护量并集基础上建立WMS自己的余额、不可变记账身份及流水。无公开HTTP任意改余额接口，不向Host暴露DbContext或内部记账命令；不接生产连接。Platform私有表不是WMS外键目标。授权、物料/位置主数据、单据状态、运输完成证据必须在后续具名用例中验证，不能把本内部原语直接映射为API或把客户端bool当证明。

余额固定绑定tenant/warehouse/location/item/batch/container/baseUnit与精度0..6；batch为空仅表示无批次，用非空规范空串持久化，不能用NULL唯一键漏洞产生重复余额。批次身份按精确字符串比较，不trim、不大小写或重音归一化，MySQL Batch列显式`utf8mb4_bin`；Provider不实际生成列排序DDL时用固定单列迁移修正并实测查询/唯一键。其余ID必须非空。所有UUID由owner解析，不以展示码/任意URL替代。新余额数量0，无公开setter，改变数量必须生成不可变entry；不以构造非零余额导入库存。

本片记账类型为Receipt/Shipment/Transfer。数量必须满足既有单笔上限、基准单位精度、decimal(28,6)且不舍入。Transfer必须不同余额、同tenant/warehouse/item/batch/container/unit/precision，仅位置不同，原子源减/目标加且一对entry共享posting。没有中间handover记账。Shipment不能扣负或消费受保护量；本片尚未建立预留生命周期，余额只能在未装配业务的零保护基础上使用，不声称已完成保护与发运整合。

## 事务、幂等与长期身份

每次内部命令绑定非空tenant/actor/correlation/operation/sourceDocument/sourceLine/executionUnit。posting永久保存原语义（kind/余额ID/数量/actor/source引用）及提交时间，不按90天删除。tenant/operation唯一，tenant/kind/executionUnit另有唯一约束，确保换operation的重复完成也不重记。相同身份且相同语义重放返回原posting ID，预期版本/correlation只是本次请求证据，不重写原流水；不同payload/actor/source引用冲突显式拒绝。

每个写入要求全部目标余额的opaque UUID版本。首次命令使用显式数据库事务保存posting、entry和所有余额；CAS失败全部回滚，不能提交半边移账。两条并发命令争余额，至多一个旧版本成功，另一个返回VersionConflict并需owner重新核验，不自动重算物理意图。数据库唯一竞争回滚后只核验精确业务身份，不把任意1062当成功。

entry记录tenant/posting/balance、有符号数量（收入正、支出负）、精确before/after、UTC微秒时间、correlation；tenant/posting/balance唯一及tenant复合外键防跨租户。posting与entry普通修改/删除一律拒绝；余额身份/维度/精度不可变，只能通过领域方法更新数量和Version。保存前拒绝跨tenant写，不提供IgnoreQueryFilters式公共读取。新的posting必须在显式事务中带齐全部entry和对应余额变更；新entry必须属于同次新增posting、匹配确切余额before/after/方向和数量。不能只追加历史posting的虚假事实，也不能只保存防重身份而遗漏库存效果。

数据库异常明确分类为安全应用异常，不暴露SQL/凭据/provider原文；调用者取消贯穿IO并传播。未知提交结果不推断失败/成功，owner可用原operation核验，不能新建operation盲目重发。

## 配置、迁移与验收

复用已验证Oracle MySQL EF 10.0.7/驱动9.7.0，WMS自有WmsDbContext、wms_表及独立迁移历史；不引用Platform项目。设计时工厂无连接、无seed。测试只用授权wes_test及每例随机tenant，清理仅这些tenant的WMS数据。增加依赖后才restore更新锁文件。首期真实MySQL验证精确小数/唯一维度/tenant隔离、不可变历史、CAS、成对移账回滚、重复operation/业务执行身份与故障注入，不使用SQLite/InMemory代签。

本片不装配Host、不提供API/页面，不声明收发单据、主数据、预留/在途/Unknown全生命周期或WMS Outbox已完成。剩余硬依赖及整体验收继续按T04/T05/T06/T08工作包，证据见[测试记录](98-t08-owner-ledger-core-tests.md)。

后续[持久预留切片](99-t08-durable-stock-reservations.md)已接入Reserved/InTransit/Unknown及源余额Version保护，替代本片首批验证时的“零保护前提”；完整业务授权、主数据、运输证据与HTTP装配仍未完成，不能据内部消费接线解除整体门禁。
