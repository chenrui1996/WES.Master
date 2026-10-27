# T05 WMS 创建事件范围统计

- 状态：已验收（仅本片）；REQ-NTF-004..008、PAGE-NTF-003，既有owner选择器及计数设计的第二owner续片。
- 基线：[Platform计数](133-t05-owner-message-counts.md)、[WMS事件授权](141-t05-wms-creation-event-diagnostics.md)、[owner客户端](143-t05-owner-selected-message-client.md)。
- 测试：[150](150-t05-wms-creation-event-counts-tests.md)。

新增 `GET /api/wms/master-data-events/summary`，沿现有默认关闭WMS读装配、HTTPS、可信当前身份。WMS公共scope独立查询，不由Host/Notifications访问私表。固定成功响应为 `{owner:"Wms",observedAt:UTC,pendingCount:long}`，403没有统计；不仿造不存在的Leased/Published/Quarantined/Inbox零值，不统计payload或返回creator身份。

需当前`wms.messages.read`独立统计字段Owner/ObservedAt/PendingCount的完整creator交集，复用WMS永久创建事实与Platform当前creator公共授权；Own/部门/All均只覆盖真实当前creator，未知creator、外租户不能计入。列表字段权限不自动赠统计。当前scope为空是真实授权0；权限不足403，provider不可用/取消明确失败，不转0。

SQL在tenant/creator join后按owner/contract/schema/status分组LongCount，单条查询不物化事件行；未知状态或契约明确记录安全日志后失败，不能悄悄漏计或称Pending。ObservedAt为查询完成UTC，不是统一快照或与后续列表一致的承诺。当前只存在创建事件Pending，累计收据/业务效果/Broker健康不在本统计内。clean owner、事务/ambient拒绝、授权后/SQL后取消检查沿既有读取约束。

复用MessageCounts组件，通过明确owner联合类型保留Platform原契约及All门禁，WMS呈现独立范围说明/三字段响应，不求跨owner和、不构造通用零值。切换owner/主体清旧取消，不自动查；scope变化/非法结构/401/403/404清旧、503标陈旧、离线禁读/重连手工。统计字段完整但行字段不足仍可独立读统计，反之亦然；服务器仍是权威。

无表/迁移/依赖/默认授权/真实Broker/自动发布或业务写入。回退可撤统计字段或关闭WMS读装配，不能删待发布事实。未有生产/Broker/Pack/现场批准，不跨安全边界；跨owner统一快照、WMS发布/消费和真实Broker窗口仍独立未完成。

2026-10-27新增26项真实MySQL owner、12项HTTP及23项前端；最终WMS主数据205+消息HTTP/三套真实HTTPS Chromium36+架构29=270通过，前端604/严格TS/Vite通过，Release全解0警告/0错误。实际浏览器同时验证原列表、当前撤PendingCount后403清旧、只读/仓库创建/库存三变体无额外写副作用。Host传既有TimeProvider，公共scope未显式传clock时使用系统UTC，兼容既有调用。无生产或现场验收。
