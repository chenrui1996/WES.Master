# 功能开关详细设计

- 状态：已纳入软件实现基线；分包实现与测试中，不代表全量验收或部署许可
- 范围：SCOPE-OPS-02；REQ-ARCH-008；FUNC-SYS-005；DTC-SYS-008
- Owner：Host（部署组合与校验）、各模块（能力边界）、Ops Web（入口投影）
- 参考：[Seven 功能开关](../../../Seven.Master/doc/14-功能开关.md)、[系统架构](../02-system-architecture.md)、[系统运维](system-operations.md)、[工程治理](../03-engineering-governance.md)

## 1. 目标与边界

功能开关用来组合单个部署可用的 UI 入口和可选运行能力，不是授权、租户或业务策略系统。Host 在启动时读取部署级 `Features` 配置、校验后固定本进程的能力快照；客户端只接收允许公开的 UI 模块清单。配置来自受控部署配置（JSON/environment variables），不由数据库业务配置、租户、用户或浏览器写入。

本设计区分两个开关类别：

| 类别 | 控制对象 | 关闭效果 | 服务端/API 边界 |
|---|---|---|---|
| `Modules`（模块 UI） | Ops Web 模块菜单及对应客户端路由入口 | 隐藏入口；直接导航显示不可用/无权入口状态 | 模块服务、API、数据与迁移仍保留；每个请求继续执行认证、授权、租户/数据范围和审计 |
| `Capabilities`（运行能力） | 可选 hosted service、外部连接、可选 endpoint/consumer | 不注册、不启动或不建立连接；相关操作以明确的 Disabled/503 状态拒绝 | 仅关闭已声明可选能力；不得通过空结果、成功 No-op 或隐式替代实现伪装成功 |

默认不得为任意业务模块添加整体后端关闭开关。只有当依赖闭包、Host 注册、HTTP/事件契约、数据库迁移/所有权、定时任务及关闭时在途工作都能独立验证，且不存在跨模块半关闭状态时，才可在 ADR 和专属 DTC 下申请整体模块运行能力开关。否则模块开关仅为 UI 入口开关。

## 2. 配置与生命周期

[T09耐久运输续片](../implementation/179-t09-durable-inbound-transport-design.md)不新增操作者可用的Completed接口，也不借`Outbox`开关装配默认可信适配器。既有入库审核/收货开关只许可对应当前授权操作；收货形成的耐久意图不等于运输已受理。实际Orchestration适配器、运行能力与生产门禁未接通前不启动运输dispatcher/consumer；关闭交付能力不能删除已收货库存、意图、可信完成Inbox或在途保护，恢复必须使用原身份对账。现有主数据Outbox开关和诊断仍只针对其原创建事件，不混入运输消息。

[当前实体附件目录](../implementation/169-t07-current-entity-attachment-catalog.md)的两个固定集合GET沿用下述同一双开关AND；任一关闭不映射集合，关闭/恢复不改引用/文件/永久回执。没有新增目录开关、默认授权或扫描健康成功fallback。

具名仓库/库位附件读取及[永久绑定/解绑](../implementation/167-t07-durable-business-file-bindings.md)复用 `Wes:WmsMasterData:Enabled` 与默认关闭 `Wes:Files:Enabled` 的AND，不增加开关或动态授权策略；任一关闭不映射GET/PUT/DELETE这些路由，文件身份/引用/永久操作回执保留。UI模块开关不替代当前双owner权限，上传者也不能绕过已有来源实体read。详见[具名实体附件读取](../implementation/165-t07-entity-authorized-file-reads.md)；启用不代表业务附件UI或真实病毒库/ACL已验收，撤去路由不是删除永久回执的许可。

配置采用强类型、明确定义的嵌套节，所有已注册开关在部署配置中显式给值：

```json
{
  "Features": {
    "Modules": {
      "Platform": true,
      "Wms": true,
      "Orchestration": true,
      "WcsStacker": false,
      "WcsFourWay": false,
      "Simulator": false
    },
    "Capabilities": {
      "Quartz": false,
      "SignalR": true,
      "MessageQueue": false,
      "Outbox": false,
      "Mail": false,
      "ObjectStorage": false,
      "DeviceCommunication": false
    }
  }
}
```

上表是配置形状示例，不是默认部署组合或已实现能力清单。实际名称由 Host 的强类型选项和可枚举注册表唯一确定；未知键、重复注册、缺失值、无效类型及不可能的依赖组合必须在启动时报告配置路径并失败，不静默补默认值。密钥和连接凭据通过秘密管理注入，不属于 feature catalog。

- 配置优先级遵循 Host 的标准配置提供程序顺序；部署环境变量可按层级覆盖，例如 `Features__Capabilities__DeviceCommunication=true`。最终值在启动校验后固定，变更须滚动/重启 Host，不能通过数据库热更新或匿名 API 改写。
- 能力依赖按逻辑 AND：feature 开关和该能力自身的细项配置/必需依赖均满足才可启动。明确禁用是 `Disabled`；开关开启但配置非法是启动失败 `Misconfigured`；开启且外部依赖暂不可用是 `Degraded/Unavailable`，不得标成 Disabled 或 Ready。
- 运行时状态区分 `Ready`、`Disabled`、`Degraded/Unavailable`。必需依赖失败按 readiness/控制门禁失败处理；可选能力故障可以不终止进程，但其能力必须明确不可用并返回稳定错误，不能有成功型 fallback。
- 模块 UI 开关可在页面重载/重新获取 catalog 后生效。它不动态卸载 Host 模块、不撤销已签发身份、不更改数据库结构或历史数据。

WMS发布调度细项见[默认关闭托管设计](../implementation/159-t05-hosted-wms-publisher.md)：`Wes:WmsPublisher:Enabled=false`，禁用时不注册worker/runner或打开owner；提供的非法配置仍拒绝。启用需要Identity、Outbox、MessageQueue以及具名singleton transport的AND依赖，UI `Modules:Wms`不控制该worker或替代权限。当前Program更早拒绝未登记的Outbox/MessageQueue runtime owner，不能只设布尔值或注入测试transport就声称可用；后续实际adapter/运行能力注册须验收依赖闭包。停止保留耐久lease/未确认事实，重启复用原EventId按owner恢复，不自动迁移/排空或将ACK当业务完成。

## 3. 服务端注册与客户端读取

WMS库位创建有独立默认关闭的`Wes:WmsMasterData:LocationCreationEnabled`，与仓库创建开关互不隐式开启；需要master-data/Identity读装配及唯一具名非keyed singleton schema，开启但依赖未装配时启动失败。关闭时不解析adapter/映射写端点；已有位置、永久事实及Outbox保留。UI模块开关不替代当前五字段/父仓授权或真实Pack门禁。详见[库位创建HTTP](../implementation/161-t08-authorized-location-creation-http.md)。

WMS入库草稿另有独立默认关闭的`Wes:WmsMasterData:InboundDraftCreationEnabled`；要求同一master-data/Identity读装配，不由仓库/库位创建开关开启。提供非法bool或开启但读装配缺失时启动失败。关闭不映射POST，不改已有草稿/永久operation及库存；不影响既有入库GET。客户端当前字段入口不是部署开关证明，关闭时POST沿用创建端点未装配合同，显示明确不可用且不自动重试。开启仍逐次核当前完整入库写/真实父仓读取；不会自动开放审核/收货、启用仓位、创建运输或声称现场许可。正在提交后关闭/响应中断按原操作恢复，不能删除持久身份或换新键盲发。详见[入库设计与证据](../implementation/173-t09-inbound-basic-flow.md)。

草稿修订独立使用`Wes:WmsMasterData:InboundDraftRevisionEnabled=false`，不隐式开启创建、审核或收货，且创建开关不隐式开启修订；仍要求WMS/Identity读装配并拒绝非法配置。关闭不映射`PUT /api/wms/inbound/{id}/draft`（404），不删除既有revision、改变库存或关闭GET。开启后每次核真实单据creator的当前完整写/read权限，ExpectedVersion与Draft状态仍是必须条件；原操作恢复也不得绕权。原修订回执版本不等于最新版本，客户端须另读；结果不明保留完整原键/body，不自动换键。新增事实kind为`Revised`，旧版读应用不支持其恢复；产生revision之后即使关闭入口，也只能回退到理解该kind的版本，不能通过删除历史或恢复旧Plan完成回滚。数量/HTTP/恢复合同及TC-T09-REV-01..06见[入库设计](../implementation/173-t09-inbound-basic-flow.md)。

仓库逻辑启停独立使用`Wes:WmsMasterData:WarehouseStateEnabled=false`，不由仓库/库位创建或入库草稿开关隐式开启；需要master-data/Identity读装配，非法bool/依赖缺失启动失败。关闭不映射`PUT /api/wms/warehouses/{id}/state`（404），保留既有Enabled/Version及永久启停事实，不自动禁用仓或撤销已提交操作。开启仍逐次核真实creator的完整read六字段和write的Enabled；Code/Name创建权限不授予启停。部署前单独评审新表迁移，Host/owner不自动迁移；关闭后未知操作只能在兼容入口恢复后以原key/body手工核验。仅改仓库逻辑状态，不开放子库位、库存/收货、分配、运输或设备控制；活动阻断/回退及测试见[设计与证据](../implementation/174-t08-warehouse-operational-state.md)。

库位逻辑启停另用`Wes:WmsMasterData:LocationStateEnabled=false`，与创建/仓库状态开关独立；依赖master-data/Identity及唯一非keyed singleton `IWarehouseLocationSchema`，即使关闭库位创建也必须核验schema装配，缺失/重复/不合法生命周期启动失败。关闭时不映射`PUT /api/wms/locations/{id}/state`，不删除事实或自动停用。服务端两时点核真实库位creator的Enabled写/完整read九字段与真实父仓creator的完整read六字段；父仓→库位串行锁内启用验证父仓Enabled及持久Pack/schema tuple，禁用检查正库存/剩余预留/未完成异址收货运输。独立迁移仅新增状态事实表，禁止以Down删除历史；无生产自动迁移/默认合法schema，也不授予收货/运输许可。使用、Unknown恢复与回退见[设计与证据](../implementation/175-t08-location-operational-state.md)。

合法单位/物料/容器与零余额账维度登记独立使用`Wes:WmsMasterData:StockResourceRegistrationEnabled=false`，不由创建、仓/位启停或入库草稿开关隐式开启。需master-data/Identity及唯一非keyed singleton `IWarehouseLocationSchema`；即使关闭库位创建/启停，也不得省略schema装配检查。关闭不映射四个具名POST，保留永久资源/维度事实以及受权单位/物料/容器GET。新增四表迁移必须单独审查、由批准环境应用，不改旧余额、不回填合法资源，不自动迁移生产；有事实后禁止Down删历史。未知结果仅在兼容入口可用后原key/body手工恢复，不清历史或自动换键。零余额登记不构成位置占用/可分配或收货许可；公开收货还须唯一容器账面位置/执行保护。详见[合法资源设计与证据](../implementation/176-t08-legal-stock-resources.md)。

入库审核与收货分别使用`Wes:WmsMasterData:InboundApprovalEnabled=false`和`InboundReceiptEnabled=false`，不由草稿、资源登记或仓位启停隐式开启；均需master-data/Identity读装配，非法bool/依赖缺失启动失败。收货还须唯一非keyed singleton `IWarehouseLocationSchema`，审核本身不装配假Valid schema。关闭仅不映射对应`POST /api/wms/inbound/{id}/approval`或`/receipts`，不改变GET、已提交库存或永久原请求事实；不关闭后自动撤销收货。开启后每次核当前单据及所有真实资源creator完整字段，保存后复验失败全回滚；Enabled/零维度均不能替代执行权限。客户端入口不是开关状态证明，404/405明确不可用、Unknown仍保留原kind/id/key/body，恢复同一入口后仅手工原键核验。开始有业务事实后不可Down删表或回退不认识当前模型的二进制；无生产自动迁移/设备控制，异址收货仍待运输。见[实施](../implementation/177-t09-authorized-inbound-execution.md)/[测试](../implementation/178-t09-authorized-inbound-execution-tests.md)。

1. Host 在组合根绑定 `FeaturesOptions`、校验每一项及依赖关系，并创建不可变运行快照。
2. 每项运行能力由其 owner 显式声明启动注册、健康/ready 影响、关闭行为、请求错误语义及 in-flight/shutdown 策略；没有声明的能力不能被任意字符串开关控制。
3. 匿名 `GET /api/config/features` 只返回前端允许公开的模块代码与启用布尔值，不返回运行能力、Provider/连接、Secret、内部依赖状态或按租户/用户定制的数据。端点输出采用显式白名单 DTO。
4. Ops Web 在加载动态菜单/路由前获取 catalog，过滤关闭模块的入口，并在会话建立后可重新获取以反映部署重启。请求失败必须显示配置不可用状态，不将错误转为空配置或默认全开。
5. 隐藏入口不是授权判断。直接访问 UI 路由由客户端显示不可用状态；调用 API 必须独立进行认证、授权、租户/数据范围、字段投影及审计校验。运行能力关闭时，owner API 对依赖该能力的操作返回稳定的 `503 capability_disabled`（或明确的服务不可用错误合同），不得用 404/空数组混淆资源不存在或授权拒绝。

## 4. 安全、不变量与运维

- Feature flags 不能关闭认证、授权、租户隔离、数据范围、字段级授权、审计、幂等/去重或数据库所有权检查；也不能放宽审批、双人复核、writer lease/fencing、设备联锁或任何 H 现场安全门禁。
- 开关关闭只影响未来能力注册/调用，不抹除持久业务事实、Outbox、审计、未知物理动作或在途状态。关闭前需按 owner 定义 drain/stop；未能确认物理结果时继续保留 Unknown 与资源保护。
- 运维健康/诊断页面区分“按部署明确禁用”与“应启用但失效”。只允许获权查看运行状态；匿名 catalog 不等于匿名诊断端点。
- 配置快照与校验摘要可记录在受控启动日志/部署证据中，但不得记录秘密值；审计日志不把 feature flags 作为业务变更事件。
- 改动默认配置或开关依赖属于部署行为变更，应更新部署/回滚说明并验证关闭和恢复路径。关闭安全关键能力时不得以“能力已关闭”作为控制安全已验证的证明。

## 5. 模块接入准入

每个新增模块/能力在其工作包中先判断是否需要开关，并记录选择及理由。只有满足下列条件，才可纳入运行能力开关：

1. 单一 owner 和完整依赖闭包已确定；开关职责不跨越其他模块私有实现。
2. 开启/关闭时的服务注册、API/事件、迁移、后台任务、外部连接、启动顺序和健康状态均有明确合同。
3. 关闭时在途请求/任务/设备命令有有界停止或保留未知事实的策略；重启恢复不会丢账、重复业务效果或重发未知物理动作。
4. Disabled/Misconfigured/Degraded 的运维诊断及用户错误明确，测试覆盖开关切换、依赖失配、恢复、授权绕过和数据副作用。
5. 关闭范围不包含平台安全骨干、跨模块数据 owner 或未隔离的物理控制闭环。

不满足任一项则只允许 UI 入口开关（若适用），或暂不增加开关；不得以“未来可配置”为由抽象出未验证的通用开关框架。需要改变模块运行边界时新增 ADR、REQ/FUNC/DTC 并更新依赖闭包和迁移计划。

## 6. 详细测试

功能开关测试用例由[系统运维](system-operations.md)中的 `DTC-SYS-008` 唯一定义，覆盖：

- 在隔离环境准备完整有效配置；分别关闭一个 UI 模块、关闭一个运行能力、设置 feature/detail AND 不匹配、注入缺项/错类型/未知键，并模拟已启用能力的依赖故障。
- UI 开关仅隐藏菜单/路由入口，授权 API 行为不变；未授权直调仍拒绝。
- 关闭运行能力时不得注册服务或建立连接，依赖操作明确 Disabled/503；配置缺项、错类型或未知键时启动失败并标明路径；AND 任一项为 false 即 Disabled；依赖故障为 Degraded 而非 Disabled。
- 重启后新快照生效，旧进程不热变；认证、授权、租户隔离、审计、幂等及 H 门禁始终成立，且无业务数据、Outbox 或 Unknown 副作用丢失。
- 按能力声明逐项覆盖 Enabled/Disabled/AND/DependencyUnavailable/InvalidConfiguration。可控 fake dependency 仅验证装配/合同；关键业务/API和持久化断言使用真实 Host 与真实 Provider。不得仅断言菜单隐藏或配置绑定成功。

## 7. 追踪与完成条件

追踪链为 `SCOPE-OPS-02 → REQ-ARCH-008 → FUNC-SYS-005 → DTC-SYS-008 / ARCH-TC-011 → T02/T06/T30/T32 → 实际 RunId`。验收前状态保持 `designed`。逐包统计新增1个 FUNC、1条模块 DTC，不新增 PAGE；详细设计基线因此更新为64 PAGE、66 FUNC、123条模块 DTC、全部168条 DTC。历史审查记录中的旧统计作为当时证据保留，不回写成当前基线。