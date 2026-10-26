# T08 仓库与库位主数据 owner

- 状态：已验收（仅内部禁用主数据创建基础）；Owner WMS；T08整包仍实施中。
- 需求：SCOPE-WMS-01 / REQ-WMS-001..004 / FUNC-WMS-001 / PAGE-WMS-001；DTC-WMS-001/010/011中的主数据子集。
- 依据：[WMS规格](../modules/wms-and-ledger.md)、[页面合同](../detailed-design/wms-and-ledger.md)、[账本核心](97-t08-owner-ledger-core.md)、[MySQL先行](../adr/ADR-0002-mysql-first-implementation.md)。

## 范围与实现思路

现有账本使用明确仓库/库位UUID但没有主数据owner；先补仓库、固定仓库/Pack/schema的库位及不可变主数据变更事实，不把历史账本的UUID自动编造为合法主数据，也不迁移/修改既有余额。此片是后续受权位置API与主数据引用校验的持久基础，不开放任意库存移动或Pack执行。

仓库Code、库位Code租户内唯一，ASCII稳定标识统一大写，显示名称允许Unicode但禁空白边界/控制字符。库位归属WarehouseId/PackId/schema版本/位置代码不可变；不提供已占位换仓/换Pack功能。仓库/库位默认禁用，创建本身不能证明可分配。库位需先通过显式注入的Pack schema边界验证对应warehouse/pack/schema/code，不内置供应商地址规则，不提供默认成功验证器。

后续仓库/库位显示名称修改及库位启停须使用期望版本CAS。库位启停必须由具名内部用例重新核验当前仓库启用、原Pack schema及外部活动/占用/预约/锁证据；依赖未配置/不可用保持拒绝，不能用“表里暂时没任务”代替执行owner证明。此片只实现内部禁用创建和owner持久化读取，不提供业务查询/编辑/启停API；任何tracked修改或删除都由保存门禁拒绝，Version列本身不代表已实现更新CAS。

## 数据与事务

新增WMS私有warehouse/location及master-data change三表；location使用tenant/warehouse复合FK，schema/code有界，所有identity保留，不物理删除。不可变change永久绑定tenant/operation、actor、correlation、资源Id、精确创建后版本与创建的安全字段，用于创建留痕和同operation重试，不保存任意payload。LocationId为空表示仓库创建，非空表示库位创建；本片不存在修改前版本或通用变更类型，不伪造Platform审计/Outbox已接通。创建必须与其change同次原子提交，不能复用旧change伪造另一资源；历史change不可改。code唯一冲突不是成功或覆盖。

WMS内部创建命令不接受客户端授权布尔值；暂不注册Host。仓库创建有界输入、空ID/重复code/换operation语义/其他tenant拒绝；库位创建先当前仓库、Pack schema核验再同事务创建，仓库禁用不阻止录入禁用主数据，但不能启用或分配。成功回执只代表主数据已持久化，绝不代表设备/物理位置已安全配置。后续授权入口必须逐次复验，不从change缓存权限。

本切片没有自动将余额维度改为主数据FK，避免破坏先前隔离fixtures和未迁移历史；后续WMS具名业务写必须先核验当前主数据，不能把内部账本原语当已授权业务入口。MySQL迁移只增加新表/键/索引，不读取生产数据；回退应用保留主数据和变更证据，不DROP历史。

### Provider及恢复边界

`WmsLocationMasterData`新增三表；真实列属性反例证明Oracle10.0.7忽略CreateTable列collation注解，实际五列均为`utf8mb4_0900_ai_ci`。因此追加`WmsMasterDataBinaryColumns`显式ALTER至`utf8mb4_bin`，不重写已执行的迁移；实际schema大小写查询与列属性复验通过。两迁移不改旧库存表。MySQL DDL并非事务回滚保证，SQL预览中的START TRANSACTION不等于迁移原子性；未执行任何生产DDL。

内部用例要求干净owner和无既有事务。operation重放仅返回原Id/Version，不再次调用schema、不缓存权限；不同actor/名称/代码/仓/Pack/schema拒绝。1062仅在回滚后精确读取同operation或同code证据，不能解释的冲突抛安全数据库错误，不默认成功。保存后异常或取消回滚资源和事实，可用新owner显式重试；不存在自动无限重试。可诊断结果有Invalid/NotFound/CodeConflict/IdempotencyConflict/SchemaRejected/SchemaUnavailable，未知schema枚举明确抛错；原始DB异常内容不外泄。

回滚应用不得运行Down删除三表；应保留已创建identity/事实，关闭后续业务入口，按具名DBA审批另定迁移恢复。设计时SQL历史表默认`__EFMigrationsHistory`，测试owner显式使用`__WesWmsMigrations`；生成脚本仅作DDL检查，不当作已审核的部署脚本。

## 验证

先验证现有真实owner模型缺表RED，再实现。领域输入/不可变归属、租户复合唯一与FK、operation永久防重、真实并发同code/同operation、原子保存失败/取消/恢复、安全错误、没有Pack schema默认成功，以及现有账本/预留回归；精确结果见[测试](116-t08-location-master-data-tests.md)。此片不关闭T08或T04–T07，主数据授权/Host/API/页面、启停占用证据、容器绑定、Outbox及现场参数仍未验收。
