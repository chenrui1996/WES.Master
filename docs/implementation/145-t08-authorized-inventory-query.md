# T08 当前授权库存与保护量读取

- 状态：已验收（本片owner/API只读范围）；沿已批准PAGE-WMS-002、REQ-WMS-001..026的库存不变量、RULE-01..08、用户自主按计划实施授权。
- owner：WMS；前置：[账本](97-t08-owner-ledger-core.md)、[保护量](99-t08-durable-stock-reservations.md)、[当前权限](../03-engineering-governance.md)。
- 测试：[146](146-t08-authorized-inventory-query-tests.md)。

## 设计与边界

补齐现有账本的公共只读owner及HTTP，不增加改账/库存初始化端点。库存是tenant拥有的维度而不是某次收货actor的个人资源；不得把最后posting或主数据creator误当库存所有者。当前只支持`wms.inventory.read`的All范围字段授权，Own/部门在没有已批准仓库资源范围合同前明确拒绝，不扩大其权限。Platform公共入口只返回当前live session/role的All字段；WMS要求固定完整投影，前端菜单不是授权。每次新owner重新检查，无权限/异常不返回成功空数组。不会自动添加权限种子。

GET `/api/wms/inventory`沿现有显式默认关闭WMS读装配，HTTPS、认证、tenant/session/actor均来自可信principal；offset0..10000、limit25/50/100，精确warehouseId/locationId/itemId/containerId及batch过滤（空batch是无批次，不trim/大小写合并）。过滤在SQL先执行再排序Id/分页；无total、无跨请求固定快照/导出。返回`rows/offset/limit`，行固定：

`Id WarehouseId LocationId ItemId Batch ContainerId UnitId Precision Quantity Reserved InTransit Unknown Protected Available Version`。

数量输出为固定六位小数十进制字符串，避免JavaScript Number损失decimal(28,6)；base-unit/precision明确，不跨单位求和。账面维度/版本不是物理位置或当前分配授权。无executionCustodian/observedLocation服务时不伪造其值。

## 一致性与失败

clean owner拒tracked/已有/ambient transaction。当前授权完成后，WMS在自有RepeatableRead只读事务中先取有界余额页，再只取该页的非零remaining保护份额（负量作为损坏明确拒绝）；两次读取一致，不能将旧余额与新保护混合。不锁写、不自动retry、不持久业务snapshot。用既有`StockAvailability.Calculate`精确计算重叠并集，Reserved/InTransit/Unknown可重叠、Protected只计一次，零余量墓碑不计。损坏kind/重复execution/保护超过账面明确异常，不clamp或负量转0。每个IO和物化后取消不交付，provider异常安全日志并映射500/503，不输出SQL/secret。

## 验证与部署

先真实MySQL owner RED，后实现与HTTP RED/接线。验证精确六位/极大值、重叠和墓碑、精确batch大小写、tenant、SQL过滤后分页、全部字段/All与当前撤session/role/field、clean/ambient、取消/数据库失败、写入并发两查询一致性。仅wes_test，不连接生产/设备，不修改依赖或迁移。关闭既有WMS读装配可撤API，不删除账本/预留。客户端/固定核对快照/导出、其余T08生命周期及完整T04–T08尚未由本片完成。

新增26真实owner及18真实HTTP；最终原账本/预留48+新增26=74、HTTP18、架构29共121通过。Release solution0警告/0错误，EF无模型漂移，修改C#精确范围whitespace格式化通过。满decimal(28,6)只在隔离存储边界探针构造，不通过放宽单笔999999999.999999上限伪造合法收货。
