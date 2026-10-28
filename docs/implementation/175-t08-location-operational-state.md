# T08/T09 库位逻辑启停

- 状态：实施中；本片软件已验证，接续[仓库启停](174-t08-warehouse-operational-state.md)，不关闭T08/T09或代签现场验收。
- 需求：FUNC-WMS-001、PAGE-WMS-001、REQ-WMS-001..004、DTC-WMS-001库位状态子集。
- 基线：[WMS规格](../modules/wms-and-ledger.md)、[详细设计](../detailed-design/wms-and-ledger.md)、[就绪台账](readiness-register.md)、[交付顺序](TodoList.md)。

## 合同与边界

复用仓库启停的预览/确认/Unknown/原键恢复流程和严格请求形状，新增具名库位owner及独立默认关闭`LocationStateEnabled`，不复制通用CRUD或引入生成器。`PUT /api/wms/locations/{id}/state`必填ExpectedVersion/Enabled/Reason；未知成员、无效UUID/key/client、非HTTPS拒绝。200仅decision/id/version；0 Changed、1 Replayed，后续Forbidden/Invalid/NotFound/Conflict/Blocked及SchemaRejected/SchemaUnavailable均不带回执，分别403/400/404/409/409/422/503。

权限：真实库位创建者范围内`wms.locations.write`精确Enabled字段及完整read九字段；真实父仓创建者范围内完整仓read六字段。Code/Name等创建权限不授予启停；不默认授角色。新操作及原操作恢复均执行两时点核权，保存后撤权/取消/异常整次回滚。

仅Enabled/Version可变，仓/Pack/schema/code/name/creator/CreatedAt不变。追加永久tenant/operation、actor/correlation、资源/父仓、before/after状态/版本、原因和UTC事实；理由沿用1..256、无控制/边界空白。原键完整tuple匹配返回原版本，即使当前再次变化或父仓/schema后来不可用，也只恢复历史事实，不重新执行。当前权限仍必须满足。不同tuple/旧版本/新键同状态409。

独立Serializable事务按父仓→库位锁顺序；与仓库禁用在同父仓串行。启用必须在锁内确认父仓Enabled，并经现有`IWarehouseLocationSchema`验证持久的tenant/warehouse/Pack/schema/code tuple；拒绝422、不可用503，无默认合法schema。禁用不因schema不可用无法停用，但若本位正库存、剩余预留或既有异址入库收货事实的源/目标尚待最终运输，则Blocked。当前没有耐久到达事实，异址receipt仍保持保护，不以纯领域到达或HTTP状态释放；将来最终运输持久片按事实推进。Serializable读锁保护检查直到提交。

本片不创建/迁移容器、物料单位或余额，不写库存、不代表位置空闲/可分配/现场安全。容器绑定/占位owner尚未实现，后续必须在新增占用入口时接同父仓/库位事务守卫，不得以此片代签不存在的活动owner。内部ledger/receipt原语仍不是运营入口。新状态不伪造为创建Outbox事件；事件合同在实际消费依赖接入时另行实现。

## 部署、使用与回退

需要WMS/Identity读装配及一个明确的非keyed singleton schema，沿用库位创建的装配校验；启停开关独立于创建/仓库状态开关，依赖缺失或非法bool启动失败。关闭404且不删除状态事实、不自动禁用。新迁移只增加库位状态事实表/复合FK/唯一索引，先审查再应用批准隔离wes_test，不操作生产。

页面从新鲜库位详情准备相反状态，展示仓/Pack/schema固定关联、原因、期望版本、key，全量预览后确认。10秒/离线/无效成功响应Unknown；原tuple只存在内存，在线手工恢复，不排队/自动重试。父查询清详情不得卸载恢复面板；查询/创建/附件互锁、权限/主体变化清旧，成功后另读当前详情。回退关闭入口但保留表/事实；不得Down删历史，恢复Unknown必须使用兼容版本。

## 测试门槛

| Case ID | 场景 | 必须结果 |
|---|---|---|
| TC-T08-LSTATE-01 | 启用/禁用/恢复首键及原创建 | 两状态事实，永久原版本，原创建/Outbox不变，库存无效果 |
| TC-T08-LSTATE-02 | 库位/父仓真实creator、逐字段、保存后撤权/取消 | 不越权披露、不部分提交 |
| TC-T08-LSTATE-03 | 父仓禁用、schema拒绝/不可用、正库存/预留/异址待运输 | 不启用不合法tuple、不禁用活动库位，零余额允许 |
| TC-T08-LSTATE-04 | 同键/不同键同版本并发、父仓禁用竞争、脏owner/历史篡改 | 仅一效果、显式冲突或安全数据库失败、无伪造事实 |
| TC-T08-LSTATE-05 | 严格HTTP/配置/当前会话/原回执shape | 独立默认关闭、无schema启动失败，非法请求无效果 |
| TC-T08-LSTATE-06 | 共享父子UI/Unknown恢复/离线/迟到/键盘/窄屏 | 原目标/key/body保留、另读当前事实、无本地业务缓存 |

## 执行证据

- TDD：先写roundtrip，真实编译因合同缺失RED；补实现后首项真实MySQL通过。生成并审查`20261028024735_WmsLocationState`的Up仅新表/FK/索引、Down仅该表，再由测试首次应用批准隔离`wes_test`；未连接生产。迁移与现有runtime一致使用`__WesWmsMigrations`。
- 初次owner定向48项通过；随后完整`WmsMasterDataTests|WmsLedgerTests`576项通过，0失败/跳过。覆盖原状态/创建回执保留、逐字段及两时点撤权、保存后故障/取消回滚、父仓状态/schema合法性、真实正库存/预留/待运输目标、同键/不同键并发及父仓禁用竞争、脏事务/历史篡改。真实双creator/非creator操作者、跨租户及schema取消/异常/未知枚举和精确迁移脚本补充验证另列最终结果，不把旧576当作新增测试已运行。
- HTTP/配置初次定向52项通过（44 HTTP/启动+8配置），覆盖严格必填bool/未知成员、当前角色/成员/资源完整字段、默认关闭和独立开关、唯一singleton schema、HTTP/当前会话/原回执shape、schema拒绝/不可用。
- 共享组件由仓库状态具名组件/API提取为仓/位两个固定资源类型，并保留原仓库恢复行为。最终前端42文件982项通过，其中新库位26项，包含真实父子Unknown不卸载、原locations目标/key/body手工恢复、显式location GET、固定关联预览、全部库位/父仓字段、精确10秒及迟到响应、非法成功回执。初次新增测试误用`get().exists()`导致严格类型检查失败；改为`find().exists()`后26项及严格类型检查通过，生产构建已通过。不能用vitest替代类型检查。
- 最终Release owner定向53项全部通过，含上述五项补充（双creator/非creator操作者与真实跨租户、精确加表脚本、schema取消/异常/未知枚举），与576重叠不合计。精确脚本断言及生成SQL复核：仅新增`wms_location_state_changes`/tenant复合FK/唯一索引，历史写入`__WesWmsMigrations`，无旧表ALTER/UPDATE/DROP。
- 最终Release相关Modules293项全部通过，0失败/跳过，包括库位/仓库启停、库位创建/schema启动、WMS领域与配置及五套实际HTTPS Chromium浏览器。库位创建变体真实启用→另读→禁用已持久提交但客户端503→离线/上线不自动重试→原key/body手工恢复返回Replayed→另读当前false；键盘及375/768/1440、200%文字无横溢，结束恢复1440px。数据库仅两条库位状态事实、严格before/after链，原创建/Outbox版本不变、无库存效果。父仓在隔离fixture通过受权HTTP启用，不以默认seed或库位状态代开父仓；原字段撤权场景仍由原read角色验证。
- 最终Release全solution零警告/零错误、架构29项、修改C#格式及EF无模型漂移通过。EF首次命令误用`-c Release`（该工具解释为DbContext），未发生数据库修改；改为`--configuration Release`后通过。格式器修正新HTTP测试换行和迁移编码后复验通过。
- 证据在本会话`basic-delivery-validation`：`location-state-owner-final.trx`（576）、`location-state-release-owner-final.trx`（53）、`location-state-http-targeted.trx`（52）、`location-state-release-modules.trx`（293）及`location-state-migration.sql`。前端982与新增26、各.NET定向有重叠不累计；生产构建后只修改测试，不覆盖运行中浏览器dist。
- Route、附件专用浏览器/全部Platform未重跑；SQL Server/PostgreSQL、真实Pack/Broker/PDA/设备/现场未验收。本片只完成库位逻辑状态子集，合法容器/账维度与物料单位、当前受权审核/收货、最终运输持久移账仍按TodoList推进，T08/T09未整体关闭。
