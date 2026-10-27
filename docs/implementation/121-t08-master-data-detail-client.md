# T08 主数据只读详情页面

- 状态：实施中；Owner WMS；PAGE-WMS-001详情子集，REQ-WMS-001..004 / FUNC-WMS-001。
- 依据：[WMS页面](../detailed-design/wms-and-ledger.md)、[HTTP合同](119-t08-master-data-detail-http.md)、[共享交互](../detailed-design/00-shared-page-and-test-standard.md)。

## 交付边界

在既有`/wms/locations`入口提供明确标注的仓库/库位只读详情，不虚构目录、树、占用或容器。用户选择资源类型、输入已知UUID并显式读取当前授权定义；不把“没查到”描述为系统无主数据，不向只读用户引导创建。无编辑/启停/绑定/导出或库存动作。

导航和直接路由都受Wms模块UI开关及仓库/库位任一read权限限制；页面按当前所选类型检查全部固定字段，仅作为UX门禁，API仍独立复验Own/Departments/All。UI开关不替代`Wes:WmsMasterData:Enabled`；未装配API的404明确为资源不存在或入口未装配，不当作空系统。

复用现有同源API客户端、QueryState及样式token。展示代码/名称/资源Id/启用状态/版本/创建UTC；库位再显示固定仓库Id、PackId与schema。状态仅称主数据启用/禁用，不能把Enabled当作位置空闲、可分配或物理安全许可。

## 状态与隐私

首次显示输入说明；读取中300ms后共享loading；成功显示详情及上次成功读取时间。只在同主体、同类型、同UUID重读失败时保留明确Stale信息；403清除旧数据，401按既有会话失效处理，404清除并提示无法解析资源/装配。格式错误、错资源Id、缺字段/非法字段拒绝整份响应，不显示为正常详情。

任何类型/UUID/主体/权限切换立即中止旧请求并清空旧投影；忽略已经取消但迟到的成功和错误。离线只保留已加载详情为Stale，取消进行中请求；恢复在线不自动重读或重放。无localStorage/sessionStorage新增，不持久化资源、UUID或待写内容。卸载取消。

UUID必须是非空canonical D形状；响应严格校验UUID/Code/安全名称/boolean/版本UUID/UTC日期及库位完整归属。服务端文本仅按Vue文本插值，不解释HTML。移动端长UUID/schema换行，表单标签/状态区域/键盘提交/焦点沿用语义HTML与共享样式。

## 测试

见[测试目录](122-t08-master-data-detail-client-tests.md)。组件及路由验证之后，以真实HTTPS Kestrel、隔离MySQL、真实登录/当前授权和Chromium验证读取与撤权；不能用API网络替身冒充端到端。

## 运行与回退

1. 在既有受控环境完成[HTTP前置](119-t08-master-data-detail-http.md)和主数据迁移；本片无新迁移、包依赖或自动种子，也不增加生产许可。
2. 由有权操作者显式装配`Wes:WmsMasterData:Enabled=true`和`Features:Modules:Wms=true`，保留Identity前置。一个是API接线，一个是仅控制客户端入口的开关，不混为同一功能开关。重启后按现有登录获得当前grant；不自动扩展任何管理员权限。
3. 仓库至少具备`wms.warehouses.read`的Id/Code/Name/Enabled/Version/CreatedAt；库位需要独立的`wms.locations.read`及以上字段和WarehouseId/PackId/SchemaVersion。页面不能证明资源范围，服务端仍读取真实creator与当前部门。
4. 导航“仓库与库位详情”，或直接进入`/wms/locations`；按UUID读取。404应由操作者核对资源及装配，不以空系统处理。错误沿用安全code/关联标识；不记录业务payload。
5. 回退可撤相关read权限，或关闭Wms客户端入口；如关闭详情API则保留所有主数据/永久创建事实，不能运行Down删历史。无待提交写操作、消息或客户端业务缓存需清理。

## 当前验收边界

本片只验收PAGE-WMS-001详情子集，关联投影均为只读UUID，不伪造可导航实体。目录/树/过滤/普通管理员创建、版本化编辑启停、占用/预约/锁/活动任务和容器绑定仍待后续；列偏好、分页、固定快照和导出在本单资源页面不适用。审计/Outbox/库存均无新增写入，真实Pack与物理安全证据不由页面读取产生。T04–T08整包保持未关闭。
