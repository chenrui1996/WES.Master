# T08 当前授权仓库/库位目录

- 状态：实施中；Owner WMS；PAGE-WMS-001、FUNC-WMS-001、REQ-WMS-001..004详情发现子集。
- 依据：[主数据基础](115-t08-location-master-data.md)、[当前详情权限](117-t08-current-master-data-reads.md)、[只读客户端](121-t08-master-data-detail-client.md)。

## 目录合同

补齐显式实时目录，非树、非库存、非固定快照。GET `/api/wms/warehouses`及`/api/wms/locations`沿用默认关闭的WmsMasterData部署装配、HTTPS/JWT/no-store。query为offset默认0、limit默认25且仅25/50/100、code可选精确ASCII代码（统一大写）、enabled可选boolean；库位另有warehouseId可选非空UUID。offset限制0..10000，非法值400且无成功rows；仓库查询不接受warehouseId筛选。

成功仅返回rows/offset/limit，rows为既有固定6/9字段投影；按Code、Id稳定排序，无total、无creator/部门/审计/隐藏数量。缺任一字段的当前grant为403。完整字段存在但范围没有资源时为200空rows，不伪装成缺权限或系统全空。分页是实时读取，不证明跨页固定水位，选择行后必须调用当前单资源详情重新授权。

## 跨owner授权

Platform增加公开应用查询：复验当前调用者目录、session、角色/成员后，逐必需字段匹配Own/Departments/All，对Platform自有目录中的creator进行字段范围交集，返回完整投影允许的creator UUID集合与明确Authorized标志。All仍不凭空允许缺失creator。creator部门在每次查询重新解析，不接受客户端部门或scope。

WMS通过自身authority端口取得结果，只消费UUID不接触Platform表/Domain/DbContext。WMS在自己的创建事实及tenant查询内按creator集合过滤，再join资源、应用筛选、排序及分页；禁止先分页再逐行授权造成缺页或泄露total。集合表达式遵循既有Oracle Guid列限制，不使用已知不可靠的primitive Guid集合Contains。无需缓存或新数据库表/迁移。

Platform与WMS之间不是分布式ACID快照，撤权以每次调用当前读取为准，不声称锁住并发撤权。该授权集合仅在请求内，不缓存、不交付HTTP；查询取消或authority异常不产生成功页。SQL故障保留现有安全错误/关联标识，无成功空页fallback。

## 客户端接线

在既有详情页增加显式“读取目录”、代码/启用状态筛选及共享实时分页。仓库/库位切换、筛选、scope变化清目录并取消旧请求，迟到响应丢弃。只允许从当前已确认页选择Id，再当前详情重读；503保留Stale行但禁选，403/401或非法响应清行；离线不发送/排队，恢复需手工读取。无本机业务缓存。目录空提示仅限当前范围/筛选，不引导无创建权限者写入。

单资源详情仍可按已知UUID读取；目录不触发创建/启停/绑定/库存或审计Outbox写，不把主数据Enabled当空闲。树/层巷、授权创建及写生命周期继续未完成。

## 验证

见[测试目录](124-t08-authorized-master-data-catalog-tests.md)。软件验证不得替代真实Pack/生产/现场安全证据。

## 接线与运行

- 无依赖升级、迁移或默认权限变化。现有详情装配保持兼容：`Wes:WmsMasterData:Enabled=true`启用两个目录及原两个详情GET；`Features:Modules:Wms=true`只打开UI入口，Identity前置仍必需，不自动迁移或导入账号/资源。
- `IMasterDataAuthorization`扩展完整投影creator查询方法，Host生产桥接及仓库内所有测试adapter同批更新；该进程内模块端口不是外部HTTP请求允许提交scope的契约。既有单资源HTTP shape保持不变。
- 页面菜单及路由不变。选择仓库或库位，输入可选精确代码/主数据状态（库位可加固定仓库UUID），显式“读取目录”。分页为实时且无total；卡片固定显示代码、名称、UUID和主数据状态，版本/UTC/Pack/schema在选择后重新授权的详情读取，固定卡片非可配置表格，不加入无业务用途的列偏好。
- 切换类型/筛选/scope、撤字段或登出立即取消清目录；目录403也清当前详情，详情403也清目录，不能只清一侧而保留可点击旧投影。503同查询旧行仅供陈旧参考且禁选，恢复后显式读取。无客户端写队列或本机业务存储。
- 业务读取不新增审计/Outbox/库存事实；HTTP失败沿用安全code与关联标识，Provider故障不暴露原SQL或连接材料。回退可关闭UI入口或既有API装配/撤权限，不删除身份与创建事实。

## 仍未交付

树及区/层/巷模型、创建当前授权、编辑/启停和活动/占用/预约/锁检查、容器绑定、库存/流水受权查询与固定快照导出、单位/FIFO/FEFO/序列集成、WMS Outbox与运输完成业务证明仍待后续；不因目录完成关闭PAGE-WMS-001全部、父DTC或T04–T08整包。真实Pack与物理许可继续阻断，不使用测试schema代签。
