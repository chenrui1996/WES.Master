# T08 当前授权库位创建 HTTP

- 状态：实施中；REQ-WMS-001..004、PAGE-WMS-001/FUNC-WMS-001创建子集，非完整DTC-WMS-001。
- 依据：[已批准WMS设计](../detailed-design/wms-and-ledger.md)、[当前writer](125-t08-current-authorized-master-data-creation.md)、[仓库HTTP先例](137-t08-authorized-warehouse-creation-http.md)、[原子Outbox](135-t05-t08-wms-creation-outbox.md)；[测试](162-t08-authorized-location-creation-http-tests.md)。

## 方案与门禁

在既有`/api/wms/locations`增加POST，复用WMS公开writer，不新增业务状态机。新增独立部署布尔项`Wes:WmsMasterData:LocationCreationEnabled`，默认false；启用须已有master-data和Identity。非法提供值即使禁用也明确拒绝；仓库创建开关不隐式启用库位写入，UI开关不等于权限。整个WMS未装配404，只有读装配时POST405。

启用时组合根必须存在恰好一个非keyed singleton `IWarehouseLocationSchema`，在映射端点/启动完成之前验证，缺少/重复/scoped/transient/仅keyed均失败。不注册默认Valid/Unavailable替身；现有WarehouseOnlySchema仍仅服务仓库创建，不用于库位。仓库没有真实Pack schema adapter，本片只交付显式组合扩展点，不标记实际Pack已接通或可上线。

## HTTP与owner合同

仅已认证HTTPS与单值`X-WES-Client=ops-web|pda`，单值非空D格式UUID `Idempotency-Key`。精确body为`{code,name,warehouseId,packId,schemaVersion}`；未知字段包括tenant/actor/creator/enabled/operation一律400。UUID、code/name/schema边界沿既有writer，所有身份来自可信subject；URL/header不指定tenant/actor。

独立当前`wms.locations.write`全部五字段、当前完整父仓read由owner复验；不赋予任何默认角色。每次新writer，父仓禁用可创建同样禁用的位置定义；Pack完整tuple由具名adapter验证，不触发控制或资源启用。永久同operation重放仍复验当前权限/父仓可读性，原事件不重复，不自动重试。

Created201并返回Location `/api/wms/locations/{id}`；Replayed200，均仅原三字段`{decision,id,version}`，不隐含详情read授权。Forbidden403、Invalid400、NotFound404、CodeConflict/IdempotencyConflict409、SchemaRejected422、SchemaUnavailable503；拒绝不回显资源身份或schema正文。安全数据库错误沿既有503/500固定标题，未知异常/取消交由已有安全边界，不伪装拒绝或成功。写入header输入拒绝记录固定分类；owner继续负责授权/事务日志。

业务效果仅默认禁用位置+永久创建事实+安全Outbox同owner原子提交，不改库存/容器/运输。未知提交结果保留原key/body，恢复后显式同键；schema不可用不能改成成功。同一已提交operation恢复不要求再次产生新的schema验证事实，沿既有永久创建合同，但当前授权必须复验。

## 验证与回退

先写配置/启动门禁与真实MySQL HTTP失败用例，再实施。覆盖当前全部字段/父仓权限/撤权、外租户父仓、tuple、schema拒绝/不可用/取消、精确响应、永久重放/冲突/并发、严格body/header、默认关闭与读/仓创建回归。不新增依赖/迁移，不连接真实Pack或生产。回退关闭库位写开关并重启，保留所有位置/永久事实/Outbox，不执行删除或Down。客户端创建、真实Pack、启停/层级/容器/完整父DTC后续独立完成。

本片已验证：新增65项真实MySQL HTTP/启动及8配置用例；最终Release相关128含四套HTTPS浏览器+WMS主数据owner238+架构29=395通过，构建0警告/0错误。明确的6项行为RED已修正，无新依赖/迁移或默认schema实现，详见162。
