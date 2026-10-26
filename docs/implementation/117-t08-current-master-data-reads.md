# T08 当前授权的主数据详情

- 状态：已验收（仅应用详情读取边界）；Owner WMS，授权目录Owner Platform；T08整包仍实施中。
- 需求：REQ-WMS-001..004 / FUNC-WMS-001 / PAGE-WMS-001；DTC-WMS-001/010/011与DTC-PLAT-004/005/012中的详情授权子集。
- 基线：[主数据创建](115-t08-location-master-data.md)、[WMS页面](../detailed-design/wms-and-ledger.md)、[架构](../02-system-architecture.md)。

## 实现边界

继续已批准的主数据查询链路，先完成具名仓库/库位详情的内部用例。不把WMS指向Platform私有模型或表；通过WMS声明的当前授权端口衔接Platform公开应用服务，真实联动由隔离MySQL测试验证。没有默认成功策略，没有客户端permission/department/creator断言。

`WmsMasterDataScope`是公开应用入口，仅暴露两种详情和Dispose，不泄漏DbContext/实体；部署connection和真实authority由composition root提供，不在构造函数迁移。`IMasterDataAuthorization`是WMS需要的明确外部端口，不引用Platform程序集。Platform公开`PlatformScope.VisibleOwnedResourceFieldsAsync`桥接现有CurrentAuthorization，返回字段集合而非私有目录数据。当前真实联动适配器只在测试装配中，尚未提供Host生产注册；不得因两个公开边界存在就声称HTTP链路已接通。

WMS从永久创建事实解析真实creator，不从请求接受owner。Platform公开`VisibleOwnedFieldsAsync`只接受可信资源tenant/creator，自己读取creator当前目录部门，并基于当前调用者会话/角色/成员/目录可用性逐次计算字段。Own只匹配真实creator；Departments匹配creator当前部门；All仍须同tenant和可解释的creator。缺creator不猜归属，缺一字段不返回半个固定投影。

仓库使用`wms.warehouses.read`及Id/Code/Name/Enabled/Version/CreatedAt；库位使用`wms.locations.read`及上述字段加WarehouseId/PackId/SchemaVersion。两个permission不可互换，大小写精确；必须拥有固定投影全部字段才返回Success。资源、创建事实均由tenant过滤后的SQL投影读取，不返回actor/operation/correlation或任何库存、物理地址。没有query总数、列表、导出或修改行为。

## 失败与可观测性

可信subject需明确tenant/actor/session；owner不匹配返回Forbidden并记录安全拒绝码，非法资源Id返回Invalid，tenant内无匹配资源/事实返回NotFound。无当前字段权限返回Forbidden，不缓存旧授权；端口缺失/非法结果是显式集成错误，不退回All。授权期间取消即使端口迟到返回也不交付数据。原始数据库错误安全映射，读取不写创建事实/审计/Outbox，不改变库存。

跨边界集合比较器不能重新定义字段身份：WMS按Ordinal重建字段集合，大小写宽松的`id`也不能授权`Id`。拒绝日志只含稳定decision，数据库失败日志只含安全错误码；安全异常无原Provider inner exception。两种资源permission严格分开，不能用读库位权限推断读仓库权限。

当前角色/部门/会话检查是逐次普通读取，不宣称跨模块原子快照、锁住撤权或提供授权租约。本片没有Host/API/页面或生产连接；后续HTTP装配须从已验证principal建立subject，并且只能注入真实Platform-backed策略。此片不开放编辑/启停/绑定/库存记账，也不关闭T08。

## 验证

见[测试目录](118-t08-current-master-data-reads-tests.md)。必须用真实Platform会话、角色、目录及WMS创建事实验证授权，不把受控端口当作真实权限证明。
