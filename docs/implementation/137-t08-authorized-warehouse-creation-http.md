# T08 当前授权仓库创建 HTTP

- 状态：已验收（仅本片HTTP边界，非完整T08）。
- 范围：REQ-WMS-001..004、PAGE-WMS-001/FUNC-WMS-001创建子集；不代签完整DTC-WMS-001。
- 基线：[当前writer](125-t08-current-authorized-master-data-creation.md)、[原子Outbox](135-t05-t08-wms-creation-outbox.md)、[契约](../contracts/01-business-and-execution-contracts.md)。
- 测试：[本片测试](138-t08-authorized-warehouse-creation-http-tests.md)。

## 方案与显式装配

复用现有`/api/wms/warehouses`目录路径增加POST，不新建另一套业务状态机；既有未版本化已实施路径沿用，不将本片伪称冻结了全新v1接口。Host只将可信身份、服务端关联UUID、规范幂等键和code/name传给WMS writer；唯一性、规范化、当前授权、永久重放和原子事务继续由WMS拥有。

新增部署布尔项`Wes:WmsMasterData:WarehouseCreationEnabled`，默认false；true必须已有`Wes:WmsMasterData:Enabled=true`和Identity。非法值明确启动失败。只启用既有只读装配不自动开放写入；原read配置/响应不变。UI开关不等于运行权限，不授予任何默认角色。未装配时集合仍只有GET，因此POST为405；整个WMS端点未装配则404。

不开放locations POST。writer需要的schema参数由Host提供只支持仓库的明确拒绝适配器：任何位置schema调用抛NotSupportedException，不返回Valid/Unavailable伪装已装配真实Pack。不注册真实设备/数据库外连或成功替身。仓库创建本来不调用schema，此适配器不会改变原warehouse路径。

## 输入、结果和未知恢复

仅已认证HTTPS，写入header `X-WES-Client`必须单值ops-web或pda；该标识不是授权凭据。`Idempotency-Key`须单值、D格式非空UUID。body精确`{code,name}`，未知字段（包含tenant/actor/session/creator、enabled、schema、operation等）400；缺失/null/类型错误、代码和名称边界沿writer规则400。新定义始终由当前可信actor创建并禁用，不允许请求指定其他owner/租户。URL参数/伪造tenant头不作为身份。

Created返回201及`Location: /api/wms/warehouses/{id}`；Replayed返回200，相同三字段`{decision,id,version}`，decision沿公开MasterDataWriteDecision枚举（0/1）。不返回未获read授权的代码/名称等资源投影，Location不是读授权。Forbidden403；Invalid400；CodeConflict/IdempotencyConflict409；不返回原资源Id/version。warehouse路径出现NotFound/SchemaRejected/SchemaUnavailable意味着Host/owner合同违约，不能映射成成功，明确异常交由既有安全错误边界记录。

WMS安全数据库异常：transient503，其余500，固定安全标题且关联ID；不泄露SQL/连接。取消和未知网络/提交结果不自动重试、不生成新operation；调用者保留原key与body，恢复后显式同键请求，当前权限重新验证。POST不表示仓库启用、生产可用或物理完成，无编辑/启停/删除API。

## 验证与部署

先测试默认关闭、显式装配、201/200精确合同和永久重放、改body409、真实并发单效果、当前字段/角色/会话撤销、严格输入/客户端标识/HTTPS/跨tenant、owner资源+事实+事件精确一致。无新增依赖/迁移；只在批准wes_test测试。生产开关/DBA/权限仍独立批准，回退关写开关并重启，保留所有已有资源/永久事实/Outbox，不执行删除或Down。客户端创建表单、真实Pack、其他生命周期/父DTC后续独立实现。

已新增38真实HTTP及7配置用例；最终相关WMS领域/HTTP/既有真实HTTPS浏览器145、架构29通过，Release solution零警告错误。此处浏览器只回归原目录/详情/撤权，未测试尚未实施的创建表单。未改WMS owner生产逻辑，不重复计入上一片141 owner通过结果。
