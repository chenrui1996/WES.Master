# T08 当前库存只读客户端

- 状态：已验收（本实时只读切片）；PAGE-WMS-002、RULE-01..08、UI-001..004/007/008/011/012；T08整包保持实施中。
- 前置：[当前库存API](145-t08-authorized-inventory-query.md)，测试：[148](148-t08-inventory-query-client-tests.md)。

沿既有Vue3/严格TS、QueryState/RealtimePager、当前身份及Wms模块门禁，在批准`/wms/inventory`接入真实只读库存。导航需独立`wms.inventory.read`，按钮需All完整15字段；Own/部门和主数据权限不能借用，服务端每次再查live scope/字段。卡片固定显示UUID维度、单位/精度、精确batch及六种数量/版本，无编辑/删除/导出/自动分配按钮。该实时子集不是固定核对快照或PAGE完整验收。

可选warehouse/location/item/container UUID及batch全部/精确（空串表示无batch），输入变更取消清旧并归第一页，无自动请求；显式提交/换页，25/50/100及offset上限10000。响应严格3字段页/15字段行、非空UUID、precision0..6、batch原样、固定六位非负decimal(28,6)字符串、稳定无重复ID。用BigInt精确验证数量/保护量/可用量及精度，不经Number、不跨单位求和；重叠原因允许相加大于Protected，但每原因不大于Protected，Available+Protected=Quantity。无投影字段不伪造observed位置/保管权。

身份/grant改变、卸载、离线取消并丢弃迟到结果；筛选变动/401/403/404/非法响应清旧，503同筛选保留明确stale。离线禁读，重连只提示手工。不缓存业务数据/写队列。复用既有tokens/响应式卡片及44px目标，精确label绑定、键盘与读屏状态。真实MySQL/HTTPS Chromium验证实际收货最大单笔的字符串、三宽度/200%字体、当前撤Available后403清旧、storage无业务数据，不stub业务接口。

## 交付与运维

2026-10-27新增56项库存组件/API客户端、1项导航、3项路由测试；全前端581通过，严格TS/Vite通过。真实MySQL HTTP18及HTTPS Chromium只读/仓库创建/库存三变体共21通过，架构29通过，Release全解0警告/0错误。实际浏览器库存变体9个GET、0个POST；最终owner核余额与唯一收货流水不变、预留0，库存读取没有写副作用。详细证据见[148](148-t08-inventory-query-client-tests.md)。

需登录、Wms模块开启、独立库存权限和完整All字段；后端读装配继续默认关闭。无迁移、依赖或生产配置变更。停止暴露入口可关闭Wms UI模块或既有后端读装配，不能以隐藏菜单代替撤权；恢复后只由用户显式读取，不恢复旧库存缓存。错误沿既有安全API错误显示，403清旧；同查询暂时故障保留明确陈旧参考，不推断实际库存。

SQL Server/PostgreSQL按ADR-0002延后；真实PDA、Pack、固定核对快照、授权流水/导出、完整主数据/容器/业务单据/运输完成均不在本片验收范围。浏览器响应式结果不代签真实终端或现场。
