# T08 持久预留测试

- 状态：实施中；关联[设计](99-t08-durable-stock-reservations.md)。
- RES-01：持久并集、同单元多标志、其他份额保护和精确Available。
- RES-02：同balance版本并发预留不可超配；过量/不匹配单位拒绝。
- RES-03：部分释放operation重放不重扣；在途/Unknown不因时间/失败自动释放。
- RES-04：Shipment/Transfer只能消费未保护量或自己精确匹配的非Unknown份额，账本/预留/余额同事务。
- RES-05：保存后故障/取消回滚；跨tenant与预留/变化不可任意删除；旧身份不复活。

## 红绿过程与真实数据库证据

1. 先加入持久保护、部分释放防重、Unknown/消费回滚、并发预留测试，执行`WmsLedgerTests`，实际观察缺少Reservations命名空间及command/result/action的编译RED。
2. 新增owner领域状态、不可变change、内部用例、共享balance版本、账本消费接线及独立迁移后，原25项账本与首批6项预留测试共同通过。
3. 再加入“另一余额的预留execution冒充无预留出库”反例，实际观察Expected ProtectedStock、Actual Posted。改为按tenant/execution查询永久身份并精确核验源余额，不只在本源余额的保护集合中查找，该反例转GREEN。
4. 追加部分成对移账、非法数量、数据库错误分类、完全消费后的重复Reserve、冲突部分释放、保存后故障/取消、不可变记录/引用与跨tenant隔离。
5. 追加两个确定性交错反例：先读旧余额5，再由独立context提交收入5与预留7，最后恢复原预留/出库查询。实际观察两项RED：把保护7与旧余额5计算导致“Protected quantity exceeds the WMS book quantity”。在保护集合读取后复验余额Version，保留ReadCommitted隔离与保存CAS，不吞掉真实数据损坏异常；两例改为有限VersionConflict并查实余额10/保护7。真实MySQL定向现为 **48 passed、0 failed、0 skipped**，其中原账本25项、本片新增23项。

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~WmsLedgerTests -m:1
```

- RES-01/02：Qty10，互不重叠的份额5和2，前者Reserved|InTransit时仍只保护7、Available3；无预留支出4拒绝。两个独立context并发预留7，只有一个成功，最终保护7而不是14。预留和无关支出争同一余额版本，不能同时成功；读到新保护后也可明确返回InsufficientStock，不假定所有并发拒绝都必须同一错误。
- RES-03：部分释放2后余量5；重放同operation不再减少，改数量拒绝，剩余可用量不足时再Reserve拒绝。在途/Unknown的ReleaseUnstarted拒绝；旧初始身份完全消费后，换operation相同Reserve只核验原身份，余量仍0，不重新分配。
- RES-04：匹配的非Unknown份额可消费；Unknown、错误line或另一余额身份拒绝。部分确认移账3同时产生-3/+3两条entry，源7/目标3/预留余4；重放不二次消费。
- RES-05：故障/取消注入在真正SaveChanges完成后、Commit前，随后用独立context查实预留和change均0、余额版本保持旧值；原命令可恢复。消费提交故障也恢复原余额/余量。跨tenant读不到预留/change且不能用他人余额创建保护；删除预留、删除change或改写原line均由owner保存门禁拒绝。数据库错误按瞬时/非瞬时安全分类，不透出注入的敏感原文。
- RES-02补充：真实SQL查询拦截只负责在指定读间隙运行另一独立WMS事务，不替代数据库或伪造保护量。原事务遇到混合水位时明确VersionConflict，不能把新保护视为旧账本数据损坏，不能重算并悄悄入账。

测试只连接授权wes_test，使用每例随机tenant；清理按change→reservation→entry→posting→balance，仅限本例tenant的WMS表。没有系统服务安装、真实设备操作或其他模块私表依赖。前端没有修改，不借历史浏览器或Vitest结果作为本片验收。

## 迁移和整包边界

已检查生成SQL：只新增两张WMS表及tenant复合外键/唯一索引；decimal(28,6)、UTC datetime(6)；没有ALTER DATABASE或跨owner外键。CLI默认历史表仅为设计时输出，真实测试使用WMS独立历史表。model/snapshot无pending变更。

```powershell
dotnet test WES.slnx --configuration Release --no-restore -m:1
```

最终完整串行Release回归：Architecture **29**、Modules **420**、Persistence **429**、Route **228**，合计 **1106 passed、0 failed、0 skipped**。相对账本核心1083基线新增23项真实MySQL测试；格式化与`git diff --check`通过。未改动前端，不重复以历史客户端结果宣称本片浏览器验收。

内部保护原语仍未开放HTTP、未核验业务授权/完整主数据、未接Outbox或运输完成证据；不能关闭DTC-WMS-004/010整体、T08或T04–T08全部工作包。SQL Server/PostgreSQL仍deferred。
