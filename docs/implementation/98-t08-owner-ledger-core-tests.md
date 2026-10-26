# T08 owner账本核心测试

- 状态：实施中；关联[设计](97-t08-owner-ledger-core.md)。
- LEDGER-CORE-01：空身份/非法批次、精度不匹配和零/超额/负单量拒绝，余额只能从0开始。
- LEDGER-CORE-02：收入/支出精确小数与before/after、溢出/不足拒绝且余额/版本不变。
- LEDGER-CORE-03：只允许同库存维度不同位置的成对Transfer，源/目标版本均核验。
- LEDGER-CORE-04：真实MySQL复合维度唯一含无批次、tenant隔离、decimal(28,6)与UTC微秒。
- LEDGER-CORE-05：事务内余额与不可变posting/entry同时提交；重复operation/执行身份只一次，冲突payload拒绝。
- LEDGER-CORE-06：真实CAS竞争及保存中断不留半笔移账；已提交查询可用原身份核验。
- LEDGER-CORE-07：posting/entry修改删除、余额身份改写、跨tenant写拒绝；取消/数据库失败不变成功形态。

## RED与定向执行

1. 先加入StockBalanceTests，执行后观察缺少StockBalance的编译RED；实现只允许0初态、精确数量及固定维度后，领域定向50 passed（新增11项余额及原39项数量/保护回归）。
2. 先加入真实持久化测试，声明WMS自身EF/驱动依赖及测试项目引用后才运行`dotnet restore WES.slnx`更新锁文件；观察缺少WMS.Features/Infrastructure与posting类型的编译RED。实现后生成无连接设计时迁移，在授权wes_test上首批15项通过。
3. 追加数据库反例：默认排序将`LOT-A`/`lot-a`/`LÓT-A`判重复，以及可单独追加历史posting的虚假entry、可只保存posting身份而无记账。实际观察三项RED（25中22通过），不是只验证已有实现。新增同次posting/entry/余额精确effects保存门禁及批次二进制列迁移，原失败用例全部GREEN。

```powershell
dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~StockBalanceTests|FullyQualifiedName~StockProtectionTests|FullyQualifiedName~InventoryQuantityTests" -m:1
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~WmsLedgerTests -m:1
```

- 领域 **50 passed**，其中余额新增 **11**。
- 真实MySQL **25 passed、0 failed、0 skipped**：精确6位小数和UTC微秒往返、成对移账、原operation/换operation的execution身份防重、冲突语义拒绝、源/目标任一版本过期、并发同版本竞争、并发相同operation的一次效果、无批次唯一、批次大小写/重音精确身份、跨tenant拒绝、不可变历史与维度、保存后故障/取消整笔回滚、缺少版本/非法数量、有限数据库故障分类。
- 并发测试使用独立真实context与Task.WhenAll，不用单context串行模拟；只一个版本可成功，另一返回VersionConflict；重复operation则一个Posted、一个Replayed且posting相同。
- 故障在实际SaveChanges成功后、事务Commit前注入，验证源仍10、目标仍0、原收入仅一笔、用原命令恢复可完整入账；不以异常发生推测回滚。
- 数据库清理只删除每例随机tenant的三张WMS表数据；没有Platform表外键、生产连接或其他owner数据清理。

## 迁移及剩余门禁

`WmsOwnerLedger`新增WMS三张表、复合tenant主外键、维度/operation/execution唯一索引及decimal(28,6)/datetime(6)；`WmsExactBatchIdentity`保留标准模型排序元数据，并显式单列DDL修复Oracle 10.0.7遗漏的COLLATE。已读生成SQL，只有WMS表建表/索引与Batch列ALTER，无ALTER DATABASE；设计时默认历史表仅CLI输出，测试runtime明确使用`__WesWmsMigrations`。model/snapshot无pending变化。Down按entry→posting→balance顺序；退回不区分大小写时若已有冲突批次，数据库须拒绝，禁止自动合并库存。

所有修改C#及两个新迁移的格式检查、`git diff --check`通过；新增文档和索引链接已核对。

```powershell
dotnet test WES.slnx --configuration Release --no-restore -m:1
```

完整串行Release为Architecture **29**、Modules **420**、Persistence **406**、Route **228**，合计 **1083 passed、0 failed、0 skipped**，比前批1047新增36项（领域11、真实MySQL25）。

本片仍无业务授权/位置及容器主数据完整核验、预留/在途/Unknown持久化、WMS Outbox、API/页面；测试不是完整DTC或T08关闭证据。前端无修改，不另借历史Vitest数量代签。
