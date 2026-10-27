# T08 当前授权主数据创建应用边界

- 状态：实施中；Owner WMS；PAGE-WMS-001、FUNC-WMS-001、REQ-WMS-001..004创建子集。
- 依据：[内部创建基础](115-t08-location-master-data.md)、[当前详情授权](117-t08-current-master-data-reads.md)、[当前目录](123-t08-authorized-master-data-catalog.md)。

## 目标与边界

为既有内部默认禁用创建补齐公开应用授权边界，不改变默认禁用、不可变归属、永久operation/创建事实、唯一代码及事务合同。不开放HTTP写入口或创建表单，不默认授予权限，不增加位置启用/库存或设备动作。真实Pack adapter和完整写生命周期仍是后续独立门禁。

显式writer scope持有干净WMS owner，构造时必须传入真实当前authority、受控clock、schema adapter及日志；无可用默认schema。公开request仅包含operation/correlation、code/name及库位可选warehouse/Pack/schema，不包含tenant/actor/session/部门/grants。tenant/actor来自可信MasterDataSubject，与writer owner不匹配或为空即拒绝。创建入口先校验输入，再当前授权，之后才调用既有永久operation重放/写事务；权限撤销也必须阻断已成功operation的重放，不回显旧Id/Version。

每个持久创建命令或显式重放使用新writer scope；提交之后保留owner跟踪事实，重复使用该scope明确抛错，不静默清除未决状态或在失败后自动重试。构造只组装owner，不建立生产连接或迁移；request输入范围复用原创建核心的同一校验函数，避免应用与持久化层各自维护一套不一致规则。

## 当前授权

- 仓库创建需`wms.warehouses.write`的Code、Name全部字段。
- 库位创建需独立`wms.locations.write`的Code、Name、WarehouseId、PackId、SchemaVersion全部字段；另外必须能通过现有`wms.warehouses.read`完整固定字段读取当前租户内父仓。父仓禁用不阻止创建同样禁用的位置定义，但绝不赋予激活/占用/分配许可。
- 新定义creator始终为调用者actor。Platform通过既有公开authority自行读取该actor的真实当前部门，再计算Own/Departments/All逐字段范围；客户端不能代填creator或部门。返回字段按Ordinal重建，缺字段即Forbidden，无部分创建。
- 每次调用复验当前会话/成员/角色/目录；跨模块授权不是锁住撤权的分布式事务。库位在授权与父仓检查之后由必需schema端口验证完整tuple，Unavailable/Rejected保持显式结果，不能成功fallback。

## 结果、重试与副作用

只在Created/Replayed返回Id/Version；Forbidden/Invalid/NotFound/CodeConflict/IdempotencyConflict/SchemaRejected/SchemaUnavailable均不返回资源投影。同operation显式恢复由原创建核心处理，不自动无限重试。构造依赖缺失或authority违约抛明确异常，数据库错误按既有WMS安全异常转换/记录且不泄露SQL或连接材料，取消贯穿authority/父仓/schema/事务，不返回迟到成功。

原子写仍仅限资源与同次永久创建事实；无Platform审计伪原子、Outbox事件、库存/容器/占用变化。失败不保留新资源，物理效果不存在。writer不自动迁移；失败后的owner按既有创建核心规则处置，新的显式scope可恢复持久operation。

## 验证与运行

见[测试目录](126-t08-current-authorized-master-data-creation-tests.md)。没有新依赖或迁移；本片只提供应用边界，不把尚无真实Pack适配器的Host位置创建标为已装配。回退不删除已经创建的身份/永久事实。
