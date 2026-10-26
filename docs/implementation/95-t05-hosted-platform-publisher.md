# T05 Platform 托管发布调度

- 状态：托管调度子集已验证，不关闭T05或DTC-NTF-001/002。
- 依据：[工作包T05](../superpowers/plans/2026-10-23-wes-development-work-packages.md)、[消息设计](../detailed-design/notifications-and-jobs.md)及现有[Platform Outbox](35-t05-platform-outbox.md)。

## 有界装配

补齐Host至Platform公共应用边界的周期调度，不让Host访问Platform.Infrastructure或DbContext。现有公开transport/envelope移至Platform公共Messaging命名空间，字段和发送结果语义不变；dispatcher/publisher/lease store仍是模块实现细节。

`Wes:PlatformPublisher`默认Enabled=false。启用必须同时明确配置Identity.Enabled、Features.Capabilities.Outbox/MessageQueue、1..100个唯一非空D格式tenant UUID、IntervalSeconds 1..3600及BatchSize 1..100；不枚举数据库tenant、不接受wildcard/空tenant。不接受未知或畸形已提供配置，即使禁用也不能悄悄忽略错误。与现有保留维护共用严格配置读取原语，保持保留维护既有60..86400秒/1..200的边界不变。

启用还必须装配唯一的singleton真实transport。没有transport、重复或非singleton注册时在启动装配阶段明确失败，不注入None/成功假实现。当前仓库尚无真实Broker适配，因此默认配置不创建publisher timer或owner；启用这个调度器不是Broker已完成验收。

每tick仅串行处理明确白名单，每tenant创建干净owner，调用既有dispatcher最多选择N条事件。固定90秒持久lease，单tenant本tick固定60秒取消预算，下一轮用新owner恢复；预算包括transport和数据库等待。取消依赖底层操作合作，不能据此宣称不可合作transport有硬实时上界。完成整轮后固定延迟，不循环排空积压、不并发共用DbContext。

保留原事件身份、lease fencing、最多5次退避/15分钟预算、永久隔离及Inbox幂等。Broker ACK仅表示投递，不等于业务完成；本tick中途失败可以已有前面事件的持久发布结果，不能回滚或重编号。ACK后DB失败/取消仍由lease过期和相同EventId重投恢复，不声称exactly-once。

## 失败语义

返回计数只包含已持久化结果。数据库可重试故障（含发布版本竞争）使用无provider明文的有限应用异常，记录tenant/correlation/固定类别，继续下一tenant，下tick重试。永久数据库错误或程序错误遵守StopHost，不能记录成功。60秒预算到期显式记录失败并继续下一tenant；Host停止取消立即传播，不开始后续tenant。transport须按公共有限结果契约报告预期发送失败，不能把未配置或断线报告为Acknowledged。

## 验证与门禁

先验证严格配置、缺少transport、生命周期、allowlist/batch、取消和预算RED，再接线。真实MySQL继续验证既有dispatcher/lease/重试回归。测试transport仅在测试项目，不是Broker验收；真实Broker断连/ACK丢失、跨owner、Notifications聚合页与相关合同仍待完成。证据见[测试记录](96-t05-hosted-platform-publisher-tests.md)。
