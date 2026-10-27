# T08 当前授权不可变库存流水查询

- 状态：实施中（本片软件已验证，T08整包未关闭）；PAGE-WMS-008/FUNC-WMS-007、REQ-WMS-005..011、RULE-01..08、UI-001..004/007/008/011/012。
- 前置：[库存读取](145-t08-authorized-inventory-query.md)、[账本核心](97-t08-owner-ledger-core.md)、[WMS设计](../detailed-design/wms-and-ledger.md)。
- 测试：[154](154-t08-authorized-stock-ledger-query-tests.md)。

新增独立`wms.ledger.read`当前All完整字段读取，不借库存读/主数据权限，不把posting actor或master creator当库存流水个人owner。Own/部门在明确仓库资源范围合同前拒绝。沿Platform公共tenant字段接口和WMS独立scope，Host不访问私表，装配仍使用既有默认关闭WMS读开关、HTTPS与可信当前tenant/actor/session。

GET `/api/wms/ledger`固定`{rows,offset,limit}`，offset0..10000、limit25/50/100；可选WarehouseId/LocationId/ItemId/ContainerId及Batch精确筛选（空串无批次、null不限）、SourceDocumentId、CorrelationId、Kind0..2、From/Before UTC半开时间范围。时间公元1000年起、微秒精度，非UTC/反向范围/空UUID/非法batch明确400。SQL先tenant及维度/来源/时间过滤再OccurredAt/Id升序分页；无总数、固定核对快照或跨请求一致水位承诺。

固定19字段：Id、PostingId、BalanceId、WarehouseId、LocationId、ItemId、Batch、ContainerId、UnitId、Precision、Kind、Quantity、Before、After、SourceDocumentId、SourceLineId、ExecutionUnitId、CorrelationId、OccurredAt。三数量为InvariantCulture固定六位字符串，Quantity带真实正负，Before/After非负；不转JS Number。维度与单位来自不可变余额身份，原因/来源来自永久posting，不返回actor/operation/私有对象。

单SQL join当前tenant Entries/Postings/Balances，无tracking/写/审计。验证存储entry前后差=有符号Quantity、绝对值=posting正量及单笔边界、kind/源目标方向、单位精度、correlation/time一致；损坏明确安全日志+失败，不clamp、不补0或伪造来源。既有compound FK保护tenant归属；本页不宣称整posting双腿核对或物理完成证据。

独立clean owner，dirty/事务/ambient拒绝；当前授权后及SQL后检查取消，provider失败安全503/500，不转空页。无模型/迁移/权限种子/依赖变化。客户端已接同批准`/wms/ledger`只读入口，精确解析/有符号BigInt校验、显式查询、撤权/取消/离线边界；无改账/删除/授权导出伪按钮。核对快照、导出与完整运输闭环仍未交付。

Host只接受含`Z`或`+00:00`的ISO时间，不受服务器本地时区影响，空值/省略时区/仅日期明确拒绝；参数省略才不筛选。保留.NET roundtrip第七位为0的等值微秒表示，不接受非零百纳秒。公开读取类别使用Features契约`StockLedgerKind`，不把Domain实体/枚举接到Host。部署与回滚仅属显式装配读入口；禁用既有WMS读装配可整体移除该入口，不删除任何账本事实。

## 客户端与T06关联修正

复用固定卡片/QueryState/RealtimePager，19字段是固定当前完整授权投影，无可选列偏好，不缓存业务行。来源仅显示稳定UUID；业务原单/运输/补偿owner尚未具名装配，不制造可用跳转。筛选改动/身份或字段撤销清旧取消；相同查询503保留显式stale，401/403/404/非法响应清旧；离线取消，重连不自动查询。UTC用验证后的微秒规范串排序/过滤，不借JS Date毫秒精度改变半开边界。

真实Chromium将375/768/1440三宽各自叠加200%字号。首375检查失败，DOM实测`body`宽640、viewport375；原共享`min-width:20rem`随根字号32px变640px。按共用UI可访问性目标将此最小宽度限制在当前viewport以内，保留普通宽度20rem意图，不裁切/隐藏溢出或放宽测试。流水卡片亦遵循已有消息卡片的任意长数量断行模式。此为T06相关外壳修正，不冒称全部具名页面已通过组合阈值，必须重跑原页面浏览器/前端回归。

最终全前端691/严格构建、真实owner106、HTTP46及四套HTTPS浏览器4、架构29通过，相关.NET合计185，不是完整.NET全套。Platform回归另定位并修复[新建用户加载门禁](155-t04-user-create-loading-gate.md)，保留原业务断言而非插入sleep。
