# T05 WMS 创建事件统计测试

- 状态：已验收（本片），2026-10-27；设计：[149](149-t05-wms-creation-event-counts.md)；不关闭完整父DTC或T05整包。

| 子用例 | 输入/步骤 | 预期 |
|---|---|---|
| WMS-EVENT-COUNT-01 | 真实MySQL Own/部门/All、未知creator、外tenant、超过25事件 | 只统计当前完整字段creator、无分页截断、不披露身份 |
| WMS-EVENT-COUNT-02 | 逐字段删除、列表权限/其他owner权限、字段交集、当前角色/目录/会话撤销 | 当前拒绝或真实空范围，不缓存、不借权 |
| WMS-EVENT-COUNT-03 | dirty/transaction/ambient、无效subject、授权后/SQL后取消、provider故障、未知存储契约/状态 | 明确失败且无成功零值、无副作用 |
| WMS-EVENT-COUNT-04 | 真实HTTP装配/HTTPS/登录、精确3字段/no-store、独立当前统计字段 | 200正确数量或403/401/404/400，无POST |
| WMS-EVENT-COUNT-05 | MessageCounts两owner及切换、缺字段、损坏计数/UTC/结构、离线/迟到/403/503 | 不求和、不伪造状态、取消清旧/明确陈旧、不自动查 |
| WMS-EVENT-COUNT-06 | 真实HTTPS Chromium手工读取、当前第二DB owner撤统计字段 | 精确3字段/真实数量、403清旧，原事件/库存/仓库流程不变 |

## 执行证据

- owner RED：缺少ReadCreationEventSummaryAsync，编译明确失败；HTTP RED：未装配测试通过，其余11项实际404不符合200/403/401/400；客户端RED：新增WMS/owner变更断言17项失败，不把失败当通过。
- owner实现后26项真实MySQL通过：范围/独立逐字段/当前撤权、27条全量而非25条列表、授权真实0、未知creator/外tenant排除、所有四类损坏存储契约拒绝、SQL后取消及provider失败不变0。首次实现缺少安全错误helper命名空间，补明确引用后编译及测试通过。
- HTTP12项通过，精确3字段、clock/no-store、独立统计权限不能借列表或Platform、当前逐字段/角色/成员撤销、身份/HTTPS、真实foreign tenant及禁止POST。
- 最终 `dotnet test tests\Persistence\Wes.Persistence.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WmsMasterDataTests|FullyQualifiedName~MessageSummary"`：205通过（本次实际匹配WMS主数据，不将未匹配的Platform计数owner冒称重跑）。TRX `wms-event-counts-validation\owner-regression\15009_SHERRY377_2026-10-27_15_59_03.trx`。
- `dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RealBrowserReadsWarehouseAndLocationAndClearsDetailsAfterCurrentRevocation|FullyQualifiedName~WmsEventCounts|FullyQualifiedName~WmsCreationEvents|FullyQualifiedName~MessageCounts"`：36通过，含原Platform统计HTTP、WMS列表HTTP、新统计HTTP及三套真实HTTPS Chromium。TRX `wms-event-counts-validation\http-browser\15009_SHERRY377_2026-10-27_15_56_46.trx`。
- 三套浏览器均真实MySQL角色/登录、真实HTTPS/Kestrel，无业务stub；键盘手工读取精确2/3/2条，WMS计数只三字段，375/768/1440及200%字体、离线禁读/重连无自动GET；真实第二owner同时撤事件EventId和统计PendingCount后各自下一请求403清旧。原事件/仓库创建/库存流程保留；WMS请求只读9GET，创建11GET+1POST，库存11GET，无多余写入。最终owner事实/Outbox/唯一库存posting/余额不变。
- 新增23项客户端，原Platform统计契约/All门禁及重放陈旧机制保留；全前端32文件604通过，严格vue-tsc/Vite通过。超过JS安全整数明确拒绝，不取近似或0。
- 架构29通过，TRX `wms-event-counts-validation\architecture\15009_SHERRY377_2026-10-27_16_00_27.trx`；`dotnet build WES.slnx -c Release --no-restore`零警告错误；精确范围格式化及diff检查通过。

证据文件在本机session evidence；无凭据/证书入仓。未运行其他两Provider、真实Broker/Pack/PDA/生产/现场；无新模型/迁移，未冒称完整.NET全套。本片不是跨owner统一快照、通用全状态统计或完整消息投递验收。
