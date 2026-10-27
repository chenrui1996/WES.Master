# T05 WMS托管发布接线

- 状态：实施中；REQ-NTF-004..008/FUNC-NTF-001、DTC-NTF-001/002的调度子集，不代签父DTC。
- 依据：[已批准消息设计](../detailed-design/notifications-and-jobs.md)、[既有托管模式](95-t05-hosted-platform-publisher.md)、[WMS owner发布器](157-t05-wms-owner-outbox-publisher.md)；[测试](160-t05-hosted-wms-publisher-tests.md)。

## 有界装配与替代方案

沿既有Platform公开runner/worker模式接通WMS，不另建调度框架/通用Broker，不将WMS私表交给Platform或Host。复用Host已有TenantWorkerSchedule严格读取原语；不改变Platform调度或owner状态机。配置为启动快照，不使用可编辑租户业务配置或运行时热切换。

`Wes:WmsPublisher`默认Enabled=false，禁用时不注册runner/worker/timer/transport或打开owner。启用须显式Identity.Enabled及Outbox/MessageQueue capabilities、唯一非空D格式1..100个TenantIds、IntervalSeconds 1..3600、BatchSize 1..100；未知键、畸形已提供项（即使禁用）、重复/wildcard租户一律拒绝。沿既有WMS读取装配复用IdentitySettings中的部署连接材料，不新增连接发现/凭据默认值。与WMS读取/仓库创建开关独立，不隐式开放API或启用业务资源；UI模块开关不代替backend发布开关。

装配前须存在恰好一个非keyed singleton IWmsOutboxTransport，不能借用Platform transport或注入None/Clean/ACK替身；缺少/重复/非singleton时启动明确失败。仓库没有真实Broker adapter，因此正常默认启动不会外发；开启配置本身不是上线许可或验收。

实际Program更早执行FeatureSwitchConfiguration运行owner门禁：当前MessageQueue/Outbox尚未登记完整runtime owner，真实子进程启用这些capability即在该门禁失败，不能到达worker启动。本片不放宽/伪造其注册；服务集合测试只证明具名publisher装配自身的transport校验，不能冒称真实Host具备Broker能力。后续真实adapter组合必须同时登记并验证runtime owner及相应依赖/关闭恢复合同，仅提供一个transport实例仍不足以解锁现有Program。

## Tick、失败和停止

串行遍历明确allowlist；每tenant每tick只取一个有界batch，用新WmsPublishingScope，不排空积压/共用DbContext/自动迁移。每tenant固定60秒合作取消预算，停止取消立即传播，不开始后续tenant；全轮结束后按Interval固定延迟。不能根据合作取消宣称不可合作transport有硬实时上界。

公共scope维持157的90秒lease、最多6次claim/15分钟预算和稳定event。Host只记录tenant/本tick correlation及五种有限结果，不记录payload、连接或provider/SDK异常。Unconfirmed显式警告，不当作已确认投递；不把任何ACK当业务完成。可重试安全数据库错误明确记录后继续下一tenant，下tick用新owner恢复；永久数据库错误/未知程序错误向上遵守StopHost，不吞掉失败。60秒预算到期明确记录并继续下一tenant。

## 验证边界

先写严格配置/注册生命周期/白名单batch/未确认/异常/停止和受控deadline的RED，再实现。真实MySQL通过Host runner连续调用验证每次新owner、独立tenant/有界结果；既有Platform调度与WMS owner回归须通过。测试transport仅存在测试项目；无真实Broker/网络ACK丢失/操作系统崩溃/消费者Inbox，T05及T04–T08整包不关闭。

本片已验证：新增27 Host/实际进程及1真实MySQL runner，最终Release相关147项（含四套真实HTTPS浏览器）通过，构建零警告/错误。精确命令、测试装配纠正及未跑边界见160。
