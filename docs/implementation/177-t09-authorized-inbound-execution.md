# T09 当前受权审核、收货与容器保护

- 状态：本软件子片已实现并验证，[独立测试记录](178-t09-authorized-inbound-execution-tests.md)；接续[入库草稿与内部事务](173-t09-inbound-basic-flow.md)及[合法资源](176-t08-legal-stock-resources.md)。最终运输仍未接通，不关闭整个T09或现场验收。
- Owner：WMS；FUNC-WMS-002、PAGE-WMS-003、REQ-WMS-012..014、DTC-WMS-002/003执行子集、RULE-01/02及TC-T09-EXEC-01..07。
- 批准基线：[WMS规格](../modules/wms-and-ledger.md)、[详细设计](../detailed-design/wms-and-ledger.md)、[就绪台账](readiness-register.md)；按发起人已批准顺序继续，不修改原单据状态机。

## 目标与边界

在既有Draft创建/修订和内部审核/收货事务之外建立独立当前受权运营owner、默认关闭HTTP与具名页面。审核只允许合法Draft和真实启用仓库，所有计划行须同tenant已登记物料/真实基准单位/精度和批次政策；不凭旧primitive计划中的随机UUID取得执行许可。收货只接受合法登记余额、同仓启用收货位/目标位与当前固定Pack/schema，实际物料/单位/批次从当前计划及余额派生，调用者不得另填精度或物料替换。

不新增库存owner，不开放余额编辑、不控制设备、不把登记/收货等同物理到达。零余额不占位。账面位置和执行保护由真实正余额、剩余预留及未完成异址收货事实在事务内判定；容器不能同时有两个账面位置，其他容器的正库存或执行保护阻止占用同位。已有容器/位置的待运输保护不能靠再次收货、改目标或逻辑启停绕过。实际收货位等于目标时按原状态机直接完成该执行单元；异址只确认收货位账本并保留待运输，后续唯一运输及有效最终到达持久移账须另行接通，不能伪造Completed。

## 当前授权与永久请求身份

复用可信MasterDataSubject及IMasterDataAuthorization，不默认授角色：

- `wms.inbound.approve`：State；以真实单据creator核验。
- `wms.inbound.receive`：LineId/ExecutionId/Quantity/ContainerId/ReceiptLocationId/TargetLocationId/BalanceId；以真实单据creator核验。
- 两操作均须真实单据creator的完整`wms.inbound.read`八字段，以及真实父仓creator完整六字段、所有涉及的真实物料/单位creator完整read；收货额外须真实容器及收货/目标位creator完整read。两个位置即使同址也不替代资源范围检查。

保存后在同事务重新核全部当前权限，失败整次回滚；原键恢复也重新核当前完整权限，但不重新执行schema、Enabled、容量或位置分配以否认历史事实。审核/收货分别有ExpectedVersion，收货另有ExpectedBalanceVersion；数量沿用严格0至6位十进制string，不舍入/科学记数/超收。

新增不可变`wms_inbound_execution_operations`，记录tenant/原operation/真实actor/correlation、具名kind、orderId、精确规范输入、原ResultId/AfterVersion及UTC。与对应InboundFact在同一WMS事务保存，tenant FK指向原事实，不收编旧primitive事实。原键绑定actor、操作类型、单据、原版本及全部body；只允许原tuple恢复，仅correlation可变化。不同body/版本/单据/执行单元明确Conflict，不能借内部primitive较宽重放规则披露原回执。表只补公开请求身份，不替代单据历史/唯一ledger。

## 事务与并发

沿既有InboundWriter/PostStock增加仅owner内部的事务守卫/附加事实回调，保留原调用默认行为；不用外围嵌套事务、通用Repository或新库存写实现。新执行在primitive事务打开后、余额/单据写入前取得父仓→有序去重库位→容器锁，再复验完整当前资源/状态/schema及容器账面位置/执行保护。父仓锁序与启停/维度一致；父单版本防不同余额并发超收，余额版本及ledger原operation继续生效。

审核/收货业务拒绝通过明确SourceRejected决策返回，不把失败变成成功空回执；最终授权失败必须回滚单据/业务事实/执行操作/账本全部效果。取消与安全provider异常沿既有owner处理，原键可在新owner下恢复。新执行操作必须恰有同事务新增匹配source fact，不能单独添加/篡改/删除。迁移只增加新表与FK/索引，不改旧事实或回填运营许可；先审Up/Down/SQL，再只应用批准隔离wes_test。

并发同键请求在最初原回执查询后，另一owner可能恰好提交异址收货并建立待运输保护。等待者若随后被该保护拒绝，应在原事务释放后重新核当前完整权限、查精确永久原请求；已有完全相同的已提交tuple返回Replayed，不将自己的先前成功误报成Blocked。不同tuple仍Conflict，无原事实才返回实际运营拒绝；该对账不重执行schema、不新建收货或取消保护。

## HTTP与具名客户端

两个独立默认关闭开关InboundApprovalEnabled/InboundReceiptEnabled，需master-data/Identity；收货还须唯一非keyed singleton IWarehouseLocationSchema。不由草稿或资源开关隐式开启，关闭保留GET及事实。具名POST `/api/wms/inbound/{id}/approval`、`/receipts`使用HTTPS/当前JWT、X-WES-Client和单一Idempotency-Key，严格拒绝未知/缺失必填body成员。可信actor/correlation只来自服务端；成功仅decision/id/version，当前单据/余额须另读。错误区分403/400/404/409、schema422/503与安全provider500/503。

入库详情页从新鲜完整详情准备审核或收货，具名表单显示原单据/行/物料/单位/批次及全部用户输入和版本；预览无写请求，二次确认后提交。10秒、离线、非法成功回执为Unknown，冻结原kind/id/key/body，在线仅手工原键恢复、不本地缓存/自动重试。主体变化清旧，迟到GET/POST隔离，父查询不得卸载未知上下文；严格回执后才能开始下一操作，并显式重新GET。已收货但待运输必须可见，不显示目标已到达。

## 测试门槛

| Case | 场景 | 必须结果 |
|---|---|---|
| TC-T09-EXEC-01 | 合法审核、同址/异址部分及足量收货、永久原tuple恢复 | 单据/原回执/ledger一致；同址完成、异址待运输 |
| TC-T09-EXEC-02 | 逐字段/真实不同creator与保存后撤权 | 拒绝披露或全回滚，不默认授予 |
| TC-T09-EXEC-03 | 旧primitive/跨tenant/单位精度批次不符、停用或schema失效 | 无不合法执行或账本 |
| TC-T09-EXEC-04 | 同容器异位、他容器占位、预留/待运输源目标 | 串行锁内拒绝，零余额不误当占位 |
| TC-T09-EXEC-05 | 同键同body/异body、同单不同余额、版本竞争、异址赢家建立保护的并发窗口 | 单效果、明确冲突、不超收；精确原tuple仍Replayed |
| TC-T09-EXEC-06 | 保存后故障/取消/失联、非法回执与原键恢复 | 原子回滚或严格原回执；无自动换键 |
| TC-T09-EXEC-07 | 严格HTTP/开关/实际父页/真实HTTPS浏览器 | 完整预览/另读、未知上下文不丢失，窄屏/键盘可用 |

## 已接通的实现

独立公开WMS writer拥有私有DbContext及当前authority；Host只做严格绑定、可信subject/operation/correlation和HTTP决策映射。新增原请求事实表与原业务事实在同一事务提交，既有内部primitive默认调用保持原行为。零余额登记、库位启停与收货共用固定父仓→有序库位→容器锁；库位禁用与收货共用持久待运输保护查询，不从客户端状态或临时变量推断完成。

具名入库执行组件与typed client已接入实际父页：从新鲜详情准备，审核/收货各需当前完整字段提示；完整单据/全部行与精确原输入预览不产生请求。提交后父页清详情但保留执行组件、暂停目录/详情切换；不因清空record卸载Unknown上下文。10秒、离线或无效成功回执冻结原kind/id/key/body，恢复连接不自动提交；Unknown后403仍保留原tuple、允许手工核对而不能换键。仅严格原回执解锁并显示原执行版本，当前状态须显式另读。主体/原单版本改变立即清空旧数据、取消请求并忽略迟到结果。

本片通过异址真实MySQL测试暴露并修正了库位creator查询：对最多两个明确源/目标ID使用直接相等谓词，避免nullable GUID coalesce与参数集合展开在当前provider命令创建时失败。同址/异址均仍核实际每个creator，未用客户端creator或任一资源代替另一个。错误未吞掉、未降级为成功。

真实HTTPS Chromium已执行审核→另读→异址收货（服务端201后客户端503）→离线/上线不自动重试→原键原body恢复200 Replayed→另读Executing/实收5/待收0/未关联运输→当前真实角色撤Lines→403清旧。精确7个WMS请求、3个POST，耐久核验2个业务事实/2个执行请求/1个posting/1个entry及源位余额5；schema仅首收货核源目标两次，恢复不重新执行。375/768/1440、200%文字、键盘和无业务本地缓存通过。

最终回归结果与红绿过程见[独立测试记录](178-t09-authorized-inbound-execution-tests.md)。不得仅因文档、内部primitive或开关存在关闭T09；运输请求、最终到达和后续主体工作继续独立验证。

## 部署、关闭与回退

- 先审迁移Up/Down与增量SQL，再应用到授权数据库；本轮只使用隔离wes_test，不含生产迁移。
- 开启审核/收货需分别显式设置两个独立部署开关，并配置当前角色及全部真实资源read；不默认授予角色。收货须显式装配唯一singleton schema，缺失/重复/错误生命周期启动失败；没有真实schema不以Valid替身上线。
- UUID、原余额版本由合法登记回执/受权库存读取取得，不从页面猜测；本片保留显式UUID输入，候选与回填增强继续列TodoList，不改变服务端核验。
- 关闭任一写开关只移除对应POST，不撤销已提交收货或删除事实。Unknown恢复需在相同原tuple和当前权限下重新开放对应入口，不能通过新键或直接改余额“修复”。
- 开始产生SourceRejected/公开执行身份事实后，不可回退到不认识当前模型/事实的旧二进制；先关闭入口、核对在途/Unknown及已提交事实。Down仅供尚无业务事实的隔离验证库，不作为生产数据回退方案。
- 异址收货已经入账但仍待运输；后续运输创建失败必须保留真实收货，只有耐久且精确最终到达与唯一移账事实才能释放保护。物理安全、设备联锁与生产授权仍不由本软件结果代签。
