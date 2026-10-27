# T08 库位创建 HTTP 测试

- 状态：实施中；[设计](161-t08-authorized-location-creation-http.md)；预期不计passed。

| 子用例 | 输入/步骤 | 预期 |
|---|---|---|
| LOC-HTTP-01 | 默认读/仓创建与显式库位开关；非法配置、缺/重复/非singleton/仅keyed schema | 不隐式开写；非法组合启动失败，无默认schema替身 |
| LOC-HTTP-02 | 当前Own五字段及父仓read，完整schema tuple，创建/同键重放 | 201/200精确三字段，禁用位置+永久事实+Pending事件各一，无库存写入 |
| LOC-HTTP-03 | 缺任意写字段、父仓撤权/foreign、重放时角色/会话撤销 | 明确拒绝，不泄漏资源身份，不新增事实/事件 |
| LOC-HTTP-04 | schema拒绝/不可用/未知/取消 | 422/503/安全异常/取消，无持久效果，不成功fallback |
| LOC-HTTP-05 | 改body、重复code、两请求竞争、spoof身份 | 409或当前tenant唯一效果，原operation/event不漂移 |
| LOC-HTTP-06 | 非法body/UUID/schema/header、匿名/HTTP | 400/401且零写入；原仓库HTTP/真实浏览器不回归 |

真实schema实现、Pack归属及现场验收没有通过；测试adapter只在测试项目中验证组合与映射，不能解锁实际部署。

## RED与最终结果

先写配置测试，缺失LocationCreationEnabled建立编译合同RED。补配置后、生产HTTP接线前执行6项实际行为测试：缺失/重复/scoped/transient/仅keyed schema均没有按要求阻止启动，创建响应405而非201，6项全部失败；保留location-http-red.trx。随后接通显式schema装配与POST，首轮41项通过，严格body/header/授权/取消等扩展后76项通过；这些中间结果不与最终结果重复求和。

新增65项HTTP/启动用例，8项配置用例：

- 真实MySQL当前Own全部五字段/父仓read、foreign父仓404、撤销角色/父仓/会话后原键拒绝，read不隐式grant write、写入不隐式grant read。
- 精确201/200三字段/Location/no-store，位置+永久事实+Pending事件各一，两个实际并发HTTP同operation或不同operation同code单效果，改name/Pack/schema复用key409。
- schema收到可信tenant/父仓/Pack/固定schema/规范code；Rejected422、Unavailable503、未知enum或程序异常安全500，合作取消贯穿adapter且零写入。安全WmsDatabaseException注入只验证HTTP503/500映射，不冒充真实provider宕机。
- body缺项/null/错误类型/畸形JSON/未知或spoof身份字段、空或非法UUID，单值规范key/client、匿名/HTTP拒绝；code64/name128/schema64通过，各自+1拒绝。
- 关闭库位写时不解析或调用已提供schema，仓库写仍正常；启用缺/重复/非singleton/仅keyed schema在真实Program端点映射/启动完成之前拒绝，没有默认成功或Unavailable替身。

最终Release串行执行（只使用已授权wes_test）：

| 命令/选择器 | 结果 |
|---|---|
| Modules：`WmsLocationCreation|WmsWarehouseCreation|WmsMasterDataSettingsTests|RealBrowserReadsWarehouseAndLocationAndClearsDetailsAfterCurrentRevocation|RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages` | 128/128：新增HTTP65+配置总21（新增8）+原仓库HTTP38+四套真实HTTPS Chromium4 |
| Persistence：`WmsMasterDataTests` | 238/238，含原子创建Outbox/旧模型迁移/发布恢复及当前主数据/授权读取回归 |
| Architecture全套 | 29/29 |
| `dotnet build WES.slnx --configuration Release --no-restore --verbosity quiet` | 0警告/0错误 |

合计395项，无跳过。本机证据位于会话files的wms-location-http-validation，最终TRX为location-release-http-browser-final.trx、location-owner-release-final.trx、location-architecture-release-final.trx。

修改C#的dotnet format verify、git diff check及18条新增Markdown链接检查均通过。

本片不修改客户端，浏览器只回归原Platform全页面及WMS三变体；**没有测试尚未实施的库位创建表单**。不重复执行前端单测/构建，全量.NET/真实Pack/SQL Server/PostgreSQL/部署及现场验收未跑；无新增依赖/模型/迁移。T08父DTC及T04–T08整包仍未完成。
