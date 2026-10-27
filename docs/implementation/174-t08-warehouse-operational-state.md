# T08/T09 仓库逻辑启停

- 状态：仓库逻辑启停子集已实现并验证；T08/T09整包未关闭。
- 需求：FUNC-WMS-001 / PAGE-WMS-001，REQ-WMS-001..004，DTC-WMS-001的仓库状态子集。
- 基线：[WMS设计](../detailed-design/wms-and-ledger.md)、[就绪台账](readiness-register.md)、[基本交付顺序](TodoList.md)；本片是T09运营主数据依赖，不替代库位/容器、现场或父DTC。

## 目标与边界

在既有禁用仓库创建/目录/详情上增加具名逻辑启停，不再依赖任意EF属性写入。仓库Id/code/name/creator/CreatedAt不变；仅Enabled和Version可由匹配新事实推进。当前完整`wms.warehouses.read`六字段和真实创建者范围内`wms.warehouses.write`的`Enabled`精确字段缺一拒绝。创建的Code/Name写权限不隐式授权启停；不创建默认角色。

请求为operation、ExpectedVersion、Enabled、Reason；actor/tenant/session来自可信上下文。Reason为1..256无控制字符或边界空白的原因。新键只接受真正状态变化，不将已是目标状态伪造为新变更。永久事实记录原actor/operation/correlation、资源、before/after状态/版本、原因和UTC；同原键匹配完整actor/id/ExpectedVersion/Enabled/Reason返回原版本，即使当前已经再次变化，重放仍复验当前完整权限。换tuple或过期版本409。

## 事务、活动与兼容

独立WMS owner；Serializable事务先锁tenant/warehouse父行，检查永久键/版本与当前权限。禁用前检查本仓已启用库位、非零账面库存、通过真实balance归属的RemainingQuantity>0预留，以及Approved/Executing入库单；存在任何一项返回Blocked。零余额、释放完预留和Draft不代表活动执行，不单独阻止。Serializable读锁保护所检查记录/范围直到提交，与当前已有内部writer竞争时可显式失败，绝不将死锁/超时转成功。后续所有公开运营写必须在同事务重新核仓/位状态，不能用早先读到Enabled作为许可；现有内部primitive仍不是公开运营入口。

保存仓状态和新事实后再次核当前权限，撤权/取消/异常整次回滚。事实不可改删、脱离父行或伪造before/version，主数据保存守卫拒绝未配套事实的Enabled修改；原创建事实和创建回执不变。新迁移只增加状态事实表/tenant复合FK，不回填、默认启用或修改已有仓。回退应用不得删除历史；旧版本仍可读Enabled，但不会理解新启停写路径。

当前Outbox信封只支持创建事实，有严格创建FK，不把状态变化伪装成创建事件。本片不外发事件或调用Pack/Orchestration，新增事件合同与消费接线在实际依赖引入时完成。仓库Enabled只表示WMS逻辑状态，不证明子库位/容器合法或物理安全，不开启分配/运输/收货。未实现的跨模块活动所有权不得由本片代签。

## HTTP与客户端

独立默认关闭`Wes:WmsMasterData:WarehouseStateEnabled=false`，依赖WMS/Identity读装配，与创建互不隐式开启；非法bool/缺依赖启动失败。`PUT /api/wms/warehouses/{id}/state`严格必填ExpectedVersion/Enabled/Reason，未知成员拒绝，key/client/HTTPS认证沿用既有mutation协议。200仅decision/id/version；非法400、无权403、缺资源404、过期/不同tuple/同状态409、活动Blocked409、数据库安全503/500。关闭404，不删除事实或自动改回禁用。

客户端从新鲜仓库详情显式准备，冻结原id/version、相反目标状态和原因→全量预览→二次确认；不提供原始Enabled自由编辑。10秒Unknown、离线不排队、手工原键/body恢复；主体/目标变更取消清旧，迟到丢弃。面板独立于详情查询状态；提交清旧详情但不丢原恢复上下文；成功只展示原版本，显式GET读回当前事实。与创建/附件写互锁，不写本地业务缓存。

## 测试门槛

| Case ID | 步骤 | 必须结果 |
|---|---|---|
| TC-T08-WSTATE-01 | 创建禁用→启用→禁用→恢复第一键；重放原创建 | 原状态回执/创建事实保留，仅两状态事实，当前false，库存不变 |
| TC-T08-WSTATE-02 | 逐read六字段/Enabled缺失、真实creator非actor、两时点撤权/取消 | 不披露回执；保存后失败全回滚 |
| TC-T08-WSTATE-03 | 活动入库、非零余额/预留、已启用库位 | 禁用Blocked且版本/事实不变；零余额、Draft允许 |
| TC-T08-WSTATE-04 | 同版本并发、同原键相同/不同tuple、过期/无效/跨租户、保存后故障、事实篡改 | 仅一效果；重放或冲突明确；无部分提交/脏守卫绕过 |
| TC-T08-WSTATE-05 | 严格HTTP、默认关闭/配置依赖、当前会话/字段资源、完整回执shape | 非法请求无状态效果，创建开关不隐式启停 |
| TC-T08-WSTATE-06 | 真实父子组合、预览零请求、Unknown原键恢复、离线/超时/迟到、成功显式GET | 不因父详情刷新卸载；无业务本地缓存/自动重试；键盘及窄屏可操作 |

## 执行证据

本片已完成软件验证；不替代库位/容器、公开审核/收货、最终运输或现场验收。

### 部署、使用与回退

1. 部署者独立评审并应用`20261027173433_WmsWarehouseState`，仅新增`wms_warehouse_state_changes`及tenant复合FK/唯一索引，不改旧仓/库存，不回填默认启用。当前仅批准隔离`wes_test`，未连接生产或其他provider。
2. 显式开启WMS/Identity读装配及`WarehouseStateEnabled`，重启生效；管理员独立授予`wms.warehouses.write`的`Enabled`和完整read字段/实际资源范围，不创建或扩大默认角色。
3. 仓库与库位详情页先读取仓库新鲜详情→准备启用/禁用仓库→输入原因→预览完整资源/期望版本/目标/operation→确认。禁用若有已启用子位/正库存/剩余预留/Approved或Executing入库，返回`409 WarehouseActivityBlocked`，不强制清理活动。
4. 收到确认仅证明原操作已提交；显式读取当前详情验证当前状态。失联/10秒/无效成功响应是Unknown，在线后只可手工同键同body恢复；禁止换键盲发。客户端不保存业务本地缓存，关闭/刷新前记录完整原tuple。
5. 回退先关闭写开关并保留表/历史/Enabled；不得执行删除事实的Down迁移作为恢复。旧版仍能读取当前逻辑状态，但不支持新启停写路径；恢复Unknown须使用兼容版本。没有自动回到禁用或撤销库存/运输的操作。

### 本轮测试记录

- TDD：合同/owner缺失时真实编译RED；补实现后首项真实MySQL roundtrip通过。新增状态表在该次测试首次应用到`wes_test`，随后才核对生成SQL/迁移；顺序不理想，不能描述为已先完成迁移审查。实际Up仅新表/FK/索引、Down仅该表，后续部署仍必须先审查。
- owner最终相关：`WmsMasterDataTests|WmsLedgerTests` 527项通过，0失败/跳过；其中本片37项真实MySQL，覆盖逐字段/两时点撤权、真实creator、同键/不同键并发、实际活动、过期/假before/脱离事实、历史篡改/删除、异常/取消回滚、独立owner及安全provider错误。新状态不增加创建事实/Outbox，不改变原创建回执。
- 初次HTTP/配置定向36项通过（28 HTTP+8配置），默认关闭、严格必填bool/未知字段、真实当前角色/成员/字段、creator、传输/会话、原回执及活动阻断。
- 初次前端953项通过后严格构建发现测试误用`getComponent().exists()`；改为`findComponent().exists()`后严格构建通过。当时尚不能据此宣布完成；新增更细恢复用例后已完整复验，不把vitest通过替代类型构建。
- 最终前端41文件956项和严格生产构建通过，本片34项；真实父页开面板→提交清详情→Unknown不卸载→同tuple恢复→显式GET，以及精确10秒、离线中断、迟到、身份/原版本变化、逐字段、非法回执和先前Unknown后拒绝均覆盖。
- 扩大Modules首轮374项为373通过/1浏览器失败。实际状态写与原键恢复已走完，新增窄屏检查留下375px，后续旧测试等待CSS在窄屏隐藏的桌面导航按钮而超时；恢复1440px后暴露旧请求数断言，按真实新增3次PUT/2次GET精确改为17次请求，而非放宽阈值/权限或超时。修正后Release最终41项（28 HTTP/8配置/五套真实HTTPS浏览器）全部通过；未重跑完整374，前373与最终41有重叠，不累计。
- 仓库创建浏览器实际执行启用→另读→禁用已持久提交但响应改为503→离线/恢复不自动重试→原key/body手工恢复返回Replayed→另读当前false。核数据库仅两状态事实、同一before/after链，原创建/Outbox版本不变、子位仍禁用、无库存效果；键盘、375/768/1440及200%文字无横溢、无业务本地缓存。
- SQL复核发现既有WMS design-time factory使用默认`__EFMigrationsHistory`，不同于全部runtime owner的`__WesWmsMigrations`。参照Platform已有工厂方式修正，保持无连接metadata用途；新增精确脚本断言，Release最终WarehouseState 38项通过（与前527重叠），生成SQL现记录正确owner历史、仅新表/FK/索引，无旧表ALTER/UPDATE/DROP。未把脚本用于生产。
- 最终Release全solution零警告/零错误、架构29项、修改C#格式、EF无模型漂移通过。证据保存在本会话`basic-delivery-validation`：`warehouse-state-owner-final.trx`（527）、`warehouse-state-owner-release-final.trx`（38）、`warehouse-state-http-browser-final.trx`（41）及`warehouse-state-migration.sql`。Route、附件专用浏览器/全部Platform未重跑；SQL Server/PostgreSQL、真实Pack/Broker/PDA/设备/现场均未在本片验收，缺失主体继续按TodoList推进。
