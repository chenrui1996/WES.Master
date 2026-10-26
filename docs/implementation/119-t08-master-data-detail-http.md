# T08 主数据详情HTTP装配

- 状态：已验收（仅默认关闭的详情HTTP装配）；WMS拥有查询，Platform拥有当前身份/授权，Host只负责composition及HTTP适配；T08整包仍实施中。
- 需求：REQ-WMS-001..004 / FUNC-WMS-001 / PAGE-WMS-001详情子集；DTC-WMS-001/010/011、DTC-PLAT-004/005/012。
- 依据：[当前详情应用边界](117-t08-current-master-data-reads.md)、[架构](../02-system-architecture.md)、[功能开关](../detailed-design/feature-switches.md)。

## 受控接线

默认不存在WMS HTTP路由。部署显式设置`Wes:WmsMasterData:Enabled=true`才注册GET `/api/wms/warehouses/{id}`和`/api/wms/locations/{id}`，并要求Identity已启用；非法布尔值即使关闭时也明确启动失败。此设置是部署装配开关，不是业务配置或设备能力开关，不替代模块UI开关/授权。

复用部署IdentitySettings的连接材料，WMS只通过自身公开WmsMasterDataScope打开owner，Platform只通过IdentityDatabase.Open和公开当前资源字段查询。Host不能引用两个模块的Domain/Infrastructure，也不在启动时迁移或扫描业务tenant。生产当前authority适配器逐次打开Platform owner，没有默认成功实现或权限缓存。

只接受已验证principal中的tenant/actor/session，query/header中的tenant/creator/department不能改授权。GET需HTTPS，响应no-store。Success返回对应固定详情投影；Forbidden403、NotFound404、Invalid400；安全WMS数据库异常按transient503/其余500映射，未知错误由既有ProblemDetails边界处理。不能借GET变为创建/编辑/启停/绑定/记账；无POST/PUT/DELETE端点。

本片不包含列表/树/页面、当前授权创建、占用/锁/容器/运输或真实Pack验证，不声称T08或全系统ready。既有ApplicationNotAssembled门禁保持503。回滚只关闭部署入口并重启，不删除主数据或创建事实；无迁移/生产DDL。

## 部署与复核

需先按既有Identity部署秘密/密钥环规则完成身份装配，并由受权DBA在目标隔离环境准备WMS迁移；此片不会自动迁移/创建库。仅审核通过的环境可增加以下非secret部署配置并重启：

```json
{ "Wes": { "WmsMasterData": { "Enabled": true } } }
```

再由当前授权管理员显式授予[应用合同](117-t08-current-master-data-reads.md)所列permission/字段，不自动给既有管理员新增WMS权限。请求必须提供既有登录签发的有效Bearer access token；角色、creator和部门不进入token授权快照。关闭时删去开关或设false并重启，两个路由不注册；无历史数据删除或自动权限撤销。该配置示例不是生产部署/连接许可。

Host使用现有`IdentityRuntime.Subject`解析已验证的`tid/sub/sid`。首次HTTP验证误用了另一公开principal解析器所需的`tenantId`claim，造成7项500；修正为复用Host既有映射，未修改JWT合同、断言或放宽权限。Host只增加WMS公开工程引用，不允许跨模块Domain/Infrastructure引用；完整架构规则通过。

## 验证

见[测试目录](120-t08-master-data-detail-http-tests.md)，使用真实MySQL owner和既有JWT登录测试装配；不把TestServer的HTTPS scheme描述为真实TLS浏览器验收。
