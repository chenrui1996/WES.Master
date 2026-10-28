# T08/T09 合法资源与零余额账维度

- 状态：实施中；本片基本软件子集已验证，接续[库位启停](175-t08-location-operational-state.md)，T08/T09未整包关闭。
- Owner：WMS；FUNC-WMS-001/002、REQ-WMS-001..004、PAGE-WMS-001/003、RULE-01/02及TC-T08-RESOURCE-01..06。
- 基线：[WMS规格](../modules/wms-and-ledger.md)、[数量和跟踪政策](../detailed-design/05-approved-rules-and-defaults.md)、[就绪台账](readiness-register.md)与[基本顺序](TodoList.md)。

## 本片边界

先提供独立、当前受权的资源登记owner，不开放任意余额初始化。具名操作登记基准单位、物料、仓内容器及零余额账维度；不新增通用CRUD、生成器或跨模块表访问。仓/位启停仍由现有owner负责；此片不是收货入口，不自动授权、启用仓位、下发运输、声明位置空闲或物理到达。

单位包含Code/Name、明确Countable及Precision；计件必须0，可分割单位显式0..6，无隐式换算或舍入。物料固定BaseUnitId和BatchRequired（新物料默认无批次；本合同显式布尔），精度从单位派生，不能由账维度调用者指定。首个入口不支持序列/效期，不能借空批次绕过启用批次政策；将来新增跟踪政策需另片，不覆盖旧物料。容器固定仓库，不猜容器位置/容量或跨仓关系。

零余额维度需真实tenant仓/位/物料/单位/容器，仓/位Enabled、同仓容器/库位，当前schema确认持久tuple；批次必须符合物料政策。仅创建Quantity=0的StockBalance与不可变登记事实；已有精确维度不附加第二登记或改变余额。允许同容器建立不同位置的零余额维度以供以后最终移账，但这不代表同时占用两位置。实际收货须新增唯一容器账面位置/执行保护并在同事务检查，不能直接把本片当收货许可。既有内部primitive/测试余额不自动成为运营登记资源；不回填或删除旧数据，不给旧表追加会拒绝既有数据的FK。

## 授权、事务与幂等

复用IMasterDataAuthorization及可信subject。新单位/物料/容器的写字段分别为`wms.units.write` Code/Name/Countable/Precision、`wms.items.write` Code/Name/BaseUnitId/BatchRequired、`wms.containers.write` Code/Name/WarehouseId。新资源creator固定actor。关联读取须真实creator完整投影：单位、物料、容器各自read；仓六字段、位九字段。同键恢复也重新核当前完整写及全部实际关联read，返回原Id/Version而不是当前余额版本。拒绝决策记录安全日志，数据库失败沿用WmsDatabaseFailures，不泄漏SQL。

维度写权限为`wms.dimensions.write` WarehouseId/LocationId/ItemId/Batch/ContainerId，关联完整read在检查后与保存后各核一次；维度creator为原actor，不冒用物料或父仓creator。原键绑定actor及全部规范化输入；Code沿用现有uppercase规范，name/batch保留精确值。不同输入Conflict。资源行自身永久保留operation/actor/correlation/UTC/version作为创建事实；维度另有不可变精确余额登记事实。

全流程独立owner/事务，ReadCommitted避免不存在code/operation探测的gap-lock升级；单位/物料/容器关联不可变且有tenant FK与唯一键，重复键冲突仅重新解析一次已提交赢家，不把数据库失败变回执。维度校验同仓后按父仓→库位→容器显式FOR UPDATE，与启停同锁序，锁内重新检查Enabled直至提交；零余额本身不构成禁用活动。保存后二次核权失败/取消/异常整次回滚。单位/物料/容器及维度登记不可改删；未来生命周期必须具名追加事实，不用公开setter。本片不对历史内部ledger全部写入口追加资源检查，否则会改变既有内部合同；新的公开收货入口必须只接受本片合法登记并复核实时状态和容器保护。

## 部署和剩余接线

新迁移只增加资源及登记表/索引/新表间tenant复合FK，不改旧余额。先审查Up/Down及生成SQL，再应用批准的隔离wes_test；禁止生产自动迁移，禁止Down删除已产生事实。旧应用回退可读既有库存但不能使用新资源入口。

本片接通owner/持久化与严格HTTP：master-data读装配下提供具名单位/物料/容器已知UUID GET，仅返回各自完整read投影、查询后复验当前真实creator权限，不返回actor/operation/correlation。四个具名POST `/api/wms/units`、`/items`、`/containers`、`/dimensions`由独立默认关闭`StockResourceRegistrationEnabled`控制，需唯一singleton schema（即使另两个库位写开关关闭），不由草稿/启停开关隐式开启。POST严格拒绝未知成员，所有值类型成员显式必填、header原operation与HTTP trace correlation、可信身份不可由body指定。Created201/Replayed200仅decision/id/version；Forbidden403、Invalid400、NotFound404、Conflict/Blocked409、schema422/503，provider500/503，不用成功空回执。维度201 Location指向现有inventory集合入口；调用者另以完整维度筛选查询，不把集合地址当余额详情，不虚构不存在的详情路由。

主数据页新增具名资源面板，与原创建/启停/附件互锁；单位/物料/容器/空账维度分别有完整具名字段和当前字段授权提示，不使用动态生成CRUD。先预览全部输入和原operation，再确认POST；10秒/离线/非法成功响应Unknown，冻结kind/key/body且仅在线手工恢复，不换键或自动重试；主体/权限变化清旧、迟到响应隔离，无业务本地缓存。已确认原资源另行GET读取当前投影；维度仅提示按已有库存查询另读，不把原版本当当前余额。页面基础支持键盘/窄屏；真实浏览器范围按实际证据记录，扩展矩阵在TodoList。

不得把owner/API存在写为页面已验证，不能关闭T08/T09或受权审核/收货/最终运输。关闭写开关后GET继续按当前授权，保留事实；Unknown只能在兼容入口重开后用原key/body手工恢复，不清历史或自动重试。

## 测试门槛

| Case | 场景 | 预期 |
|---|---|---|
| TC-T08-RESOURCE-01 | 单位/物料/容器/维度正向、各原键永久恢复 | 同原回执、仅零余额、无ledger/运输效果 |
| TC-T08-RESOURCE-02 | 精度/计件、真实单位、批次政策、无效身份 | 拒绝，不替换单位/精度，不入账 |
| TC-T08-RESOURCE-03 | 父仓/库位停用、不同仓/租户、schema拒绝/不可用 | 无不合法维度，无默认schema |
| TC-T08-RESOURCE-04 | 当前逐字段/真实creator、保存后撤权/取消/故障 | 不越权披露，全回滚 |
| TC-T08-RESOURCE-05 | 同键/不同输入/同code/同维度并发 | 单效果、明确冲突或安全provider失败 |
| TC-T08-RESOURCE-06 | 脏owner、事实篡改/删除、加表迁移 | 拒绝绕过，不改旧表，不回填合法资源 |

## 执行证据与剩余范围

- 先写真实owner RED，缺少合同/owner时编译失败；实现后首轮32项有2项故障注入测试失败：原拦截器只针对主数据provenance表，单位登记不查询该表。改用真实容器父仓查询触发同一故障，不改变生产错误处理。随后92项通过。
- 逐字段/真实不同creator、原键在真实ledger记账后仍保留原版本、取消/故障回滚、schema拒绝/不可用/未知枚举等扩展后，相关真实MySQL主数据/账本Release回归682项通过。加入当前受权GET后，最终本片owner109项通过。两者互有重叠，不合计。
- 严格HTTP与独立开关配置32项通过。首次编译发现新读partial缺少安全数据库异常helper的namespace引用，修正后编译及测试通过，无成功型fallback。
- 新前端34项包含四个真实表单分支、逐字段、非法回执、原键Unknown、先Unknown后拒绝、精确9999/10000ms和迟到GET/POST清理。新增断言实际暴露Unknown恢复成功后仍锁住“下一项”的状态错误；仅在取得严格合法原回执后清除uncertain，先前未知后403仍禁止换键。全前端43文件/1016项、严格类型检查及生产构建通过。
- 相关Release Modules636项通过，包含五套真实HTTPS浏览器。追加精确持久维度断言及格式化后，最终本片HTTP/配置与上述五浏览器37项通过。库位创建/启停变体现为精确26个WMS请求、6POST/3PUT；单位提交成功但客户端503→离线/上线不重发→原key/body恢复，单位/物料/容器显式GET及零维度登记，再另读库位和禁用。375/768/1440及200%文字无横溢、键盘确认和恢复1440通过；实际schema调用3次、四资源各1、零余额/原版本/真实creator与关联全一致、无新增ledger/运输效果。
- 架构首轮28/29：Roslyn独立编译不包含Web SDK自动Routing导入，发现新partial缺显式namespace。补显式导入后最终29/29，未修改测试规则。Release构建0警告0错误、变更C#格式检查及EF无模型漂移通过。
- 迁移Up/Down已审查后仅在批准隔离wes_test应用：新增四表、索引/tenant复合FK，不改旧表、不回填旧资源。最终SQL先因前驱迁移ID输入错误未生成；核实实际`20261028024735_WmsLocationState`后生成复审通过，仅四个CREATE TABLE、配套索引/FK与`__WesWmsMigrations`登记，无旧表ALTER/数据回填。

上述重叠结果不合计；TRX及SQL保存在本会话`basic-delivery-validation`证据目录。基本owner/API/具名客户端已交付；目录候选增强列于TodoList。唯一容器账面位置/执行保护、公开审核/收货与最终运输仍是后续主体接线，不由零维度代替。无生产迁移、真实Pack、其他数据库Provider或现场验收；无关Route/全部Platform/附件专用浏览器未重跑，不关闭项目或父DTC。
