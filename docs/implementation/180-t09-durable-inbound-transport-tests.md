# T09 耐久运输与最终移账测试记录

- 状态：本WMS契约软件子片已实现并验证；独立审查问题修复并复审通过，不代表T15实际编排、现场或整个T09验收。
- 设计：[179耐久运输设计](179-t09-durable-inbound-transport-design.md)；执行：[实现计划](../superpowers/plans/2026-10-28-inbound-transport.md)。
- Owner：WMS；FUNC-WMS-002/007、DTC-WMS-002/003、RULE-01/02/08。
- 适用：真实MySQL隔离wes_test、WMS模块间可信契约夹具；不是实际Orchestration、Pack、Broker、设备或现场安全验收。SQL Server/PostgreSQL按ADR-0002延后。

## 验收合同

| Case | 输入/步骤 | 必须验证的持久结果 |
|---|---|---|
| TC-T09-TRANSPORT-01 | 当前受权异址实收5；原key重送；同址独立变体；保存后撤权/故障/取消 | 异址唯一固定意图和待投递消息、Received/ledger/执行保护同事务；同址0意图；原key无新增；回滚无残余 |
| TC-T09-TRANSPORT-02 | 可信适配器已接受但响应丢，原intent重投/重启；重复关联 | adapter按原intent解析一个逻辑transport；唯一持久关联/历史和固定版本；仅关联不改变库存、不标Arrived |
| TC-T09-TRANSPORT-03 | 正确/错误producer、tenant、schema、intent、execution、transport、finalLocation和evidence；同event换body | 未核实不收编为可信完成，不移账；明确拒绝/依赖不可用/冲突；原消息持久负载不被后续输入修改 |
| TC-T09-TRANSPORT-04 | 源5/合法目标0，最终完成原事件重复、不同事件同execution竞争、保存后故障/取消 | 唯一Transfer和两条entry；源0/目标5、保护消费、Arrived历史、Inbox Applied及单据Completed原子提交；失败无部分提交 |
| TC-T09-TRANSPORT-05 | 缺合法目标维度；其他容器占位；容器额外库存/预留；修复后原完成事件恢复 | 保留可信完成PendingSync及来源真实收货/保护，不重派运输；条件满足后原消息只同步一次，不擅自移动其他库存 |
| TC-T09-TRANSPORT-06 | 在途读取availability；其他执行预留同容器；普通释放/禁用；最终同步后再读 | 保护并集不重复扣减；在途不可再分配；未完成不释放；完成后按匹配的Applied/Transfer/到达事实释放pending，仍按正库存决定禁用 |
| TC-T09-TRANSPORT-07 | clean-owner/跨tenant、改删意图/Inbox、GET恢复、旧执行/账本/预留/库位回归 | 不绕过owner或篡改历史；真实GET transport/arrived/状态一致，当前授权不放宽；旧主数据Outbox不混入运输消息 |

## 执行与证据规则

2026-10-28本轮前端兼容基线已运行：`clients\ops-web`执行`npm test`，44文件/1051项通过；随后`npm run build`严格TypeScript与Vite生产构建通过。此时未改动前端业务代码；该结果证明既有客户端基线，不替代后续新运输owner或GET恢复验证。

## 已取得的红绿与Provider证据

1. 初始最窄`FullyQualifiedName~InboundTransport`因缺少`InboundTransportIntents`编译RED；测试首稿错误解构既有同址helper，随后改为专用异址helper。编译失败未生成TRX，不将不存在的RED报告记作证据。
2. 初次单项GREEN1通过；扩展测试49通过。编译阶段修正公共成员`nameof`冲突、补充可信adapter类型using及两个xUnit分析器要求，未放宽业务断言。
3. 相关真实MySQL首轮528通过，最终合并 **533/533通过、0失败/跳过**（12分44秒），包含63个运输用例、127个原执行用例、252个账本类用例及库位/仓库/维度相关覆盖。重叠不重复累计。
4. 迁移生成的UTF-8 BOM经现有whitespace格式器修正后，完整运输选择器再次 **63/63通过、0失败/跳过**（2分55秒）；随后WMS Release构建0警告/0错误，EF无模型漂移，全部变更C# whitespace verify及diff check通过。
5. 迁移`20261028052502_WmsInboundTransport`的Up/Down与增量SQL复审：仅新增意图/完成Inbox两表、tenant唯一/FK与WMS历史；不改Received专用ExecutionId唯一索引，不改旧余额。Up由隔离测试fixture执行；Down只审SQL，未在共享库运行。
6. 独立审查发现最终同步只核原容器整量、未拒绝另容器占用目标位，违反TC-T09-TRANSPORT-05；本片因此未关闭。追加目标正库存/相关预留/未决执行的拒绝与PendingSync保留测试，并修复锁内共用检查；红绿结果取得后补记。
7. 修复前版本的最终HTTP/配置/旧入库与六套真实HTTPS浏览器 **192项通过**（7分35秒）、架构 **29项通过**、全solution Release **0警告/0错误**；对应`transport-http-browser-final.trx`/`transport-architecture-final.trx`。这组证明现有API/浏览器兼容，不代替第6项目标占用修复的专项复测。
8. 第6项追加9个源/目标正库存、相关预留及未决收货保护用例：原实现 **9/9 RED失败**；锁内共用`WholeContainerAsync`修复后 **9/9 GREEN通过**，保留原执行自身保护排除。随后相关回归 **268项通过**，含最终全部 **72个运输用例**；独立复审判定原问题关闭、无新增阻断。
9. 修复后的HTTP/配置及真实审核收货HTTPS浏览器 **89项通过**，架构 **29项通过**，全solution Release **0警告/0错误**；EF无模型漂移、变更C# whitespace verify通过。对应`transport-http-browser-postfix.trx`及`transport-architecture-postfix.trx`。较早533/192与本轮268/89重叠，不累计为新增。

实现表示法：意图表同时保存固定原消息与一次性运输关联，`TransportLinked`不可变历史保留接受证据；不另外建设接受Inbox第三表。完成使用独立Inbox。原消息显式重投依赖可信接收方按intent去重；本片无自动Broker租约/退避/死信runner，不将现有主数据Outbox统计误称运输投递状态。

修复专项证据为`transport-blocked1-red.trx`（9失败/0通过）、`transport-blocked1-green.trx`（9通过）和`transport-blocked1-regression.trx`（268通过，8分17秒），均在会话验证目录。复审只核原阻断与修复引入的新问题，判定spec/quality通过；未重跑已取得的测试冒充额外证据。

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~InboundTransport|FullyQualifiedName~InboundExecution|FullyQualifiedName~WmsLedgerTests|FullyQualifiedName~LocationState|FullyQualifiedName~WarehouseState|FullyQualifiedName~StockDimension"
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~InboundTransport
dotnet build src\Modules\Wms\Wes.Wms.csproj --no-restore -c Release
dotnet ef migrations has-pending-model-changes --project src\Modules\Wms\Wes.Wms.csproj --startup-project src\Modules\Wms\Wes.Wms.csproj --configuration Release --no-build
```

会话验证目录中的`transport-regression-final.trx`对应533、`transport-targeted-final.trx`对应63，`transport-up.sql`/`transport-down.sql`对应已审SQL；`transport-build.log`/`transport-model-check.log`为最后模块构建/模型检查。历史`transport-green-initial.trx`/`transport-green-full-first.trx`/`transport-regression-first.trx`被最终覆盖，不重复累计；报告不含连接凭据。

先运行最窄失败测试，再实现；修复后按变化范围合并相关选择器，不把重复回归合计为新增用例。代码/模型完成后运行架构、Release构建、EF无漂移和增量SQL复审。浏览器继续只调用原审核/收货/详情接口，不新增人工完成入口。

运行命令、实际数量、红绿故障、迁移范围与未运行项在取得结果后补记；没有结果前不以计划代替通过证据。运行痕迹放会话验证目录，不把TRX、凭据、dist或临时SQL放进源码仓库。

## 未运行的外部边界

本片以可信契约夹具验证WMS语义，不验证真实设备搬运或物理碰撞自由，不证明有界完成。T15实际订单/Leg、跨Pack交接、实际适配器认证与现场握手仍由对应工作包交付；没有实际适配器时不装配默认Verified/Ready，不给出运输成功或生产放行。
