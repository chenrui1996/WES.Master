# T08 持久预留与账本保护

- 状态：实施中；Owner WMS。
- 依据：[WMS规则](../modules/wms-and-ledger.md) §1/4、[批准规则](../detailed-design/05-approved-rules-and-defaults.md) RULE-04/05/08、[账本核心](97-t08-owner-ledger-core.md)；SCOPE-WMS-02 / REQ-WMS-005..011、DTC-WMS-004/010的内部保护量子集。

## 行为

把既有StockProtection并集接入WMS事务，不再仅为脱离余额的投影。每个executionUnit为一个由owner确认不重叠的份额，绑定固定余额/document/line、OriginalQuantity、RemainingQuantity和Reserved/InTransit/Unknown组合；相同份额多标志只保护一次。长期保留零余量记录和初始身份，不按TTL释放、删除或重建。

预留必须不超过当前Available，标记InTransit/Unknown只增加保护理由；Unknown无自动清除操作。本内部用例仅允许仍纯Reserved、确认无执行效果的受权业务owner调用ReleaseUnstarted，部分释放后保留余量；实际可否未执行仍须后续业务状态/运输合同核验，本内部命令不接受HTTP bool作为证明。释放在途/Unknown一律StateConflict，时间/失败不会隐含释放。

所有预留变化与余额Version fence同事务；余额数量不变但版本必须变化。每次变化都有独立不可变change，记录operation/actor/correlation、动作、执行单元、数量和原业务引用。tenant/operation唯一，相同语义重放不再次部分释放；相同初始execution的重复Reserve也必须精确匹配原维度/原始数量/业务引用/actor，且已终结身份不得新建复活。

账本Shipment/Transfer读取当前持久保护：没有本执行单元的预留，只能消耗Available；存在本执行单元预留时必须匹配余额及document/line，数量不超Remaining，且不能有Unknown。禁止借用其他余额的预留身份转为无预留出库。允许确认后的在途份额在最终记账事务一次消费对应预留；消耗与余额/entry/posting原子提交。消费证据由不可变posting/entry承担，不伪造Reserve或ReleaseUnstarted的change。其他份额保持保护，不产生中间运输流水。Receipt不消费预留。

## 范围和验证

预留变化、记账竞争均以同一个balance Version串行；读后有其他预留/消费时CAS失败整笔回滚，不重算可用量或自动重复物理操作。新表只在WMS内部，无其他模块私表FK。普通改删change、删预留或绕过余额fence修改预留拒绝。真实MySQL验证10/7内InTransit5时Available3、Unknown不释放、并发不能超配、部分释放operation防重、消费/回滚/迟到重放。

沿用账本的ReadCommitted事务时，多次SELECT可能看到不同提交水位：读余额后新增收入及预留，保护量可能高于旧余额。读取保护集合/自有身份后、计算可用量前，必须再次核验原余额Version仍有效；变化时返回VersionConflict或核验精确重放，不能把混合水位当成真实超配而抛出数据损坏异常。之后发生的竞争仍由保存CAS防止写入，不借此自动重试。Reserve在事务内再次发现同execution身份时也只核验重放，不创建重复跟踪实体。

`wms_stock_reservations`使用tenant/execution复合主键与tenant/balance的WMS内外键，余量和原始量为decimal(28,6)。`wms_reservation_changes`有tenant/operation唯一键及tenant/execution外键，保存精确前后数量/理由。所有普通变化必须带同次完整change；普通余量减少必须有同次匹配的有符号账本事实，不能只保存幂等身份。事务内仅触碰版本而不改账本量的合法预留变更，不放宽原先“数量变化必须有entry”的门禁。

迁移`WmsDurableReservations`只新增两张WMS表和相应索引/外键，设计时不连接数据库；runtime仍使用`__WesWmsMigrations`。Down先删除change再删除reservation，会丢失长期防重及保护证据，只用于空隔离库或经owner独立批准的离线回退，不能作为在线释放预留的方法。当前不执行生产迁移。实现没有新增依赖或自动恢复/后台释放作业。

内部原语仍不等于业务授权/运输证据/主数据/Outbox或页面已完成，T08未关闭。证据见[测试记录](100-t08-durable-stock-reservations-tests.md)。
