# T04/T05/T06/T08 续实施计划

- 状态：实施中。
- 批准基线：[开发总计划](2026-10-23-wes-development-and-testing.md)、[工作包](2026-10-23-wes-development-work-packages.md)、[规则](../../detailed-design/05-approved-rules-and-defaults.md)、[就绪台账](../../implementation/readiness-register.md)。
- 本批只推进以下三个有界切片，不将四个工作包或其父DTC整体标记完成。

## 1. T04/T05：显式启用的Platform保留清理

复用已有目录回执及Inbox各90天保留原语，补齐应用边界和托管周期任务。配置默认关闭；启用必须显式配置非空tenant白名单、间隔和1..200批量上限，依赖已配置Identity连接，不发现或遍历其他租户。每租户每周期只处理一个有界批次，串行执行，不无限追赶。使用服务端受控时钟；每次创建新owner context，显式事务中删除回执和已处理Inbox，Save与Commit后才返回计数；失败不能伪装成功，取消贯穿且独立记录安全失败日志，不输出连接串、payload或异常秘密。没有HTTP维护端点，不删除审计、Outbox、领域身份或Unknown；不添加Broker/publisher成功替身。

设计及测试证据由[87](../../implementation/87-platform-retention-maintenance.md)、[88](../../implementation/88-platform-retention-maintenance-tests.md)记录。T04/T05整体仍保留真实Broker、其他owner、归档/审计保留治理及运维验收缺项。

## 2. T06：关联候选授权丢失与旧响应边界

复用现有`useDirectoryCandidates`和Users/Roles消费路径。401/403或明确拒绝的候选响应使当前候选及翻页能力立即清空；新筛选时旧范围不可选择；取消/重置及旧请求成功或失败均不得污染新查询。普通暂时失败与授权丢失区分，错误仍交给现有页面显式提示。组件/受控Promise覆盖丢权、abort、旧请求和重新授权，保持25条分页与稳定ID去重。

设计及测试证据由[89](../../implementation/89-t06-candidate-authorization-boundary.md)、[90](../../implementation/90-t06-candidate-authorization-boundary-tests.md)记录。不增加前端授权权威，不将未测浏览器/完整DTC标为通过。

## 3. T08：按执行单元的保护量并集

在现有独立WMS领域项目内建立不可变保护量与可用量投影。每条输入代表owner已分割的互不重叠执行单元，具有非空唯一执行单元ID、严格正数量及Reserved/InTransit/Unknown保护标志。相同单元的多个保护标志只扣一次；不同单元相加；重复单元ID、未知标志、超量保护、负/超精度数量拒绝而非截断。投影包含Qty/Reserved/InTransit/Unknown/Protected/Available，使用现有`InventoryQuantity`精确运算。Qty10、其中5同时Reserved+InTransit，另2Reserved，结果Available3；Unknown仍受保护。

这不是通过聚合Reserved/InTransit总量取max猜交集；调用owner以后必须在事务内证明单元划分和唯一性。本片无持久化、预留命令、自动释放、TTL、运输/设备控制或Host注册。

设计及测试证据由[91](../../implementation/91-t08-stock-protection-union.md)、[92](../../implementation/92-t08-stock-protection-union-tests.md)记录。T08硬依赖仍待整体关闭，不宣称真实MySQL事务/并发或账本已实现。

## 验证与交付

- [ ] 各切片编码前完成设计与测试场景，先观察失败测试再实现。
- [ ] 分别运行最窄相关测试、Release/strict TS构建与格式检查。
- [ ] 更新工作包、就绪台账、需求追踪；每片形成独立提交。
- [ ] 串行完整Release测试、Ops Web全部组件测试及生产构建。
- [ ] 核对变更和未关闭项；按已有授权正常推送现有远端，不强推、不触碰其他worktree。
