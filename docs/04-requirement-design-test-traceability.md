# 需求、设计与测试追踪矩阵

- 状态：项目总追踪规范与初始映射；随详细设计/测试验收持续更新
- 来源：[业务范围与现状矩阵](01-business-scope-and-status.md)

## 实施批次记录

项目发起人本轮明确批准现有全部模块详细设计、REQ-ROUTE-001..027逐条语义及WES-RULES-1.0作为软件基线，并允许在main实施。该批准不是测试passed、现场具名签字或生产许可。REQ-ROUTE-001..027的逐行签核及阻断输入见[就绪台账](implementation/readiness-register.md)；契约见[接口基线](contracts/01-business-and-execution-contracts.md)。当前MySQL先行，其他Provider延后按[ADR-0002](adr/ADR-0002-mysql-first-implementation.md)。

| 切片 | 范围/设计 | 测试/证据状态 |
|---|---|---|
| T09 当前受权入库目录 | FUNC-WMS-002/PAGE-WMS-003，DTC-WMS-002/003读取子集，TC-T09-CAT-01..05；[设计及证据](implementation/173-t09-inbound-basic-flow.md) | 新增24owner/10HTTP/32前端；最终owner167、Modules147含24HTTP/118领域/5真实浏览器、前端856/严格构建、架构29/Release0/0通过。SQL分页前creator逐字段交集、精确筛选、读后范围复验、实际选择重读及双侧403清旧；替代上一片相关回归但不重复累计，不关闭公开写/运营主数据/运输或T09 |
| T09 当前授权只读入库详情 | FUNC-WMS-002/PAGE-WMS-003，DTC-WMS-002/003及UI读取适用子集；[设计及证据](implementation/173-t09-inbound-basic-flow.md) | 新增23owner/14HTTP/54前端及1真实HTTPS浏览器；最终owner143、Modules137含领域118及五WMS浏览器、前端824/严格构建通过。独立字段/真实creator与部门、读后核验、完整一致快照/精确数量及待运输；已知UUID详情不等于目录/公开写/运营主数据或完整T09 |
| T09 入库持久事务续片 | FUNC-WMS-002/PAGE-WMS-003，DTC-WMS-002/003、VAL-01 事务子集；[设计及证据](implementation/173-t09-inbound-basic-flow.md) | 最终真实MySQL账本120、WMS领域118/架构29、Release0/0及模型无漂移；较早联合主数据353通过，重叠不累计。单据/事实/ledger同事务、原回执、同单不同balance并发与故障/取消回滚；仅内部primitive，运营主数据/当前授权HTTP/页面/Outbox运输及最终到达持久移账仍待实施 |
| T09 入库领域首片 | FUNC-WMS-002/PAGE-WMS-003，DTC-WMS-002/003、VAL-01 领域子集；[设计及证据](implementation/173-t09-inbound-basic-flow.md) | 新增6项，WMS领域118/架构29通过、Release0/0；多行/部分收货/超收拒绝/同址无运输/异址待确认及精确到达。尚无持久 owner、当前授权 HTTP 或页面，不宣称 T09 基本链路已完成 |
| T07 当前实体附件客户端基本软件 | PAGE-PLAT-007/FUNC-PLAT-005，DTC-UI 与引用写适用子集；[设计](implementation/171-t07-current-entity-attachment-client.md) | [证据](implementation/172-t07-current-entity-attachment-client-tests.md)：新增40前端，全前端770及严格构建、真实MySQL owner482、HTTP/既有浏览器/WMS领域178、架构29通过，重叠不累加；原元组二次确认/未知恢复及实际父子组合验证，专用组合浏览器和外部证据按[TodoList](implementation/TodoList.md)后置 |
| T07当前实体附件目录 | REQ-PLAT-007..012/FUNC-PLAT-005，PAGE-PLAT-007及DTC-PLAT-005/007/014目录子集；[设计](implementation/169-t07-current-entity-attachment-catalog.md) | [证据](implementation/170-t07-current-entity-attachment-catalog-tests.md)：新增30真实owner/28 HTTP，相关333含既有HTTPS文件浏览器、精确最终新API28/架构29、Release0/0，不重复累计；当前实体两时点、精确引用和逐字段scope SQL分页前交集，无物理IO/新迁移。客户端/真实病毒库/ACL及整包仍缺 |
| T07持久具名附件绑定/解绑 | REQ-PLAT-007..012/FUNC-PLAT-005，DTC-PLAT-007/008/014引用写子集；[设计](implementation/167-t07-durable-business-file-bindings.md) | [证据](implementation/168-t07-durable-business-file-bindings-tests.md)：明确双owner写字段、全部来源实体当前read、永久同键恢复及引用/计数/版本/审计/回执单事务；真实1213由父文件锁顺序根治。严格PUT/DELETE复用默认双开关，无完整目录/客户端或真实病毒库/ACL，不代签父DTC/整包 |
| T07具名实体附件读取 | REQ-PLAT-007..012/FUNC-PLAT-005，DTC-PLAT-007/008/014读取子集；[设计](implementation/165-t07-entity-authorized-file-reads.md) | [证据](implementation/166-t07-entity-authorized-file-reads-tests.md)：新增26真实MySQL/21 HTTP，相关Release221含五套既有HTTPS浏览器/架构29、构建0/0；精确绑定+当前WMS实体+文件权限交集，复用IO后复验/审计/清零。无绑定/解绑应用入口、业务目录/客户端或真实病毒库/ACL，不代签父DTC/整包 |
| T08禁用库位创建客户端 | PAGE-WMS-001/FUNC-WMS-001、REQ-WMS-001..004创建子集、UI-001..012适用子集；[设计](implementation/163-t08-disabled-location-creation-client.md) | [证据](implementation/164-t08-disabled-location-creation-client-tests.md)：新增35前端，全套730/严格构建、相关.NET158含五套真实HTTPS浏览器、Release0/0。完整tuple/二次确认/原键未知恢复、显式当前GET及真实owner单效果；无真实Pack/完整层级链/启停/容器或父DTC验收 |
| T08当前授权库位创建HTTP | REQ-WMS-001..004、PAGE-WMS-001/FUNC-WMS-001创建子集；[设计](implementation/161-t08-authorized-location-creation-http.md) | [证据](implementation/162-t08-authorized-location-creation-http-tests.md)：新增65 HTTP/启动及8配置；六项实际行为RED后最终Release128含四套真实HTTPS浏览器+238主数据owner+29架构=395通过、构建0/0。独立默认关闭、具名schema启动门禁/当前授权/永久幂等及原子Outbox；无真实Pack/表单/完整生命周期或父DTC验收 |
| T05 WMS默认关闭托管发布 | REQ-NTF-004..008/FUNC-NTF-001、DTC-NTF-001/002调度子集；[设计](implementation/159-t05-hosted-wms-publisher.md) | [证据](implementation/160-t05-hosted-wms-publisher-tests.md)：新增27 Host/真实进程和1真实MySQL runner；最终Release 85含四套HTTPS浏览器+33发布owner+29架构=147通过、构建0/0。严格allowlist/batch/周期、新owner、60秒合作取消；实际Program仍拒绝未登记Broker运行能力，无实际adapter/Inbox/统一快照或整包验收 |
| T05 WMS owner发布器 | REQ-NTF-004..008、PAGE-NTF-003/FUNC-NTF-001、RULE-14/PAR-05；[设计](implementation/157-t05-wms-owner-outbox-publisher.md) | [证据](implementation/158-t05-wms-owner-outbox-publisher-tests.md)：新增33真实owner/3HTTP/4前端；最终238+HTTP/四套HTTPS浏览器32+架构29=299、前端695/严格构建及Release0/0。原envelope不变、耐久claim/CAS/有限预算/未确认恢复、四态九字段/精确PendingCount；无默认Host worker或Broker/Inbox，父DTC和整包不关闭 |
| T08授权流水/T06文字放大 | PAGE-WMS-008/FUNC-WMS-007、REQ-WMS-005..011、UI-001..004/007/008/011/012；[设计](implementation/153-t08-authorized-stock-ledger-query.md) | [证据](implementation/154-t08-authorized-stock-ledger-query-tests.md)：新增32真实owner/28HTTP/85前端；最终owner106+HTTP46+四套HTTPS浏览器4+架构29=185、前端691/严格构建及Release0/0。375px+200%原阈值实测修共享最小宽度，非全页面DTC/固定快照/导出/业务原单跳转或整包验收 |
| T04用户新建加载门禁 | PAGE-PLAT-003/FUNC-PLAT-002、REQ-PLAT-004；[设计](implementation/155-t04-user-create-loading-gate.md) | [证据](implementation/156-t04-user-create-loading-gate-tests.md)：新增2组件测试实际先红，按钮disabled对齐原busy/stale guard；最终前端691及真实HTTPS全Platform创建/凭据/角色流程通过，非完整父DTC验收 |
| T04 数字字典码与真实回退 | DTC-PLAT-006/012/014、FUNC-PLAT-004、PAGE-PLAT-006；[设计](implementation/151-t04-numeric-dictionary-code.md) | [证据](implementation/152-t04-numeric-dictionary-code-tests.md)：新增18领域/9HTTP/1真实owner，相关81.NET含真实HTTPS code0/改译/请求-默认-key回退/引用删除409/原全页面流程通过，Release0/0；不是完整父DTC或整包验收 |
| T05 WMS创建事件范围统计 | REQ-NTF-004..008、PAGE-NTF-003；[设计](implementation/149-t05-wms-creation-event-counts.md) | [证据](implementation/150-t05-wms-creation-event-counts-tests.md)：新增26真实owner/12HTTP/23前端，主数据205+HTTP/三套HTTPS浏览器36+架构29=270、全前端604/严格构建、Release0/0通过；独立三字段Pending，非跨owner统一快照/Inbox/投递窗口或整包验收 |
| T08 实时库存客户端 | PAGE-WMS-002、RULE-01..08、UI-001..004/007/008/011/012；[设计](implementation/147-t08-inventory-query-client.md) | [证据](implementation/148-t08-inventory-query-client-tests.md)：新增60前端、全套581/严格构建通过，真实HTTP/三套HTTPS浏览器21、架构29通过、Release0/0；不是固定核对快照/授权流水/导出或父DTC整体验收 |
| T08 当前库存owner/API | PAGE-WMS-002、库存账本不变量/RULE-01..08；[设计](implementation/145-t08-authorized-inventory-query.md) | [证据](implementation/146-t08-authorized-inventory-query-tests.md)：新增26真实owner/18HTTP，74+18+架构29=121通过；一致读/无损六位数/All逐字段、当前撤权；不是固定核对快照/导出/客户端或父DTC整体验收 |
| T05 消息owner选择与WMS事件客户端 | REQ-NTF-004..008、PAGE-NTF-003/FUNC-NTF-001、UI-001..008/011/012；[设计](implementation/143-t05-owner-selected-message-client.md) | [证据](implementation/144-t05-owner-selected-message-client-tests.md)：新增59前端/导航/路由、最终521及严格构建；真实HTTP/三套HTTPS Chromium16+架构29通过，Release零警告错误；不代表跨owner统计/统一快照/WMS发布消费/真实Broker或父DTC整体验收 |
| T05 WMS创建事件诊断owner/API | REQ-NTF-004..008、PAGE-NTF-003/FUNC-NTF-001；[设计](implementation/141-t05-wms-creation-event-diagnostics.md) | [证据](implementation/142-t05-wms-creation-event-diagnostics-tests.md)：新增38owner/13HTTP，相关179+159+29=367通过、Release零警告错误、无模型漂移；无payload/虚构Inbox或投递状态；不是跨owner客户端/统计/真实Broker或父DTC整体验收 |
| T08 禁用仓库创建客户端 | REQ-WMS-001..004、PAGE-WMS-001/FUNC-WMS-001；[设计](implementation/139-t08-disabled-warehouse-creation-client.md) | [证据](implementation/140-t08-disabled-warehouse-creation-client-tests.md)：新增35客户端，前端462/严格构建通过，WMS领域/HTTP/真HTTPS只读+写浏览器146+架构29通过；精确预览/原键/未知/撤权/显式读回，非完整DTC-WMS-001 |
| T08 仓库创建HTTP | REQ-WMS-001..004、PAGE-WMS-001/FUNC-WMS-001；[设计](implementation/137-t08-authorized-warehouse-creation-http.md) | [证据](implementation/138-t08-authorized-warehouse-creation-http-tests.md)：新增38真实HTTP/7配置，相关145+架构29=174通过；Release零警告错误；默认关闭单独写装配，非位置创建/表单/完整DTC-WMS-001 |
| T05/T08 WMS创建原子Outbox | REQ-NTF-004..008/FUNC-NTF-001、REQ-WMS-001..004/FUNC-WMS-001；[设计](implementation/135-t05-t08-wms-creation-outbox.md) | [证据](implementation/136-t05-t08-wms-creation-outbox-tests.md)：新增17真实owner，主数据141+领域/HTTP/真实浏览器100+架构29=270通过；Release零警告错误，无模型漂移、实查DDL；仅创建原语，非DTC-NTF-001/002或DTC-WMS-001整体验收 |
| T05 Platform owner消息计数 | REQ-NTF-004..008、PAGE-NTF-003；[设计](implementation/133-t05-owner-message-counts.md) | [证据](implementation/134-t05-owner-message-counts-tests.md)：新增20真实owner/8HTTP/31客户端；前端427/严格构建、MySQL38、HTTP/真实浏览器21、架构29通过；非Broker健康或业务效果累计，跨owner/父DTC不代签 |
| T05 Platform消息筛选 | REQ-NTF-004..008、PAGE-NTF-003；[设计](implementation/131-t05-filtered-message-diagnostics.md) | [证据](implementation/132-t05-filtered-message-diagnostics-tests.md)：新增15真实owner/10HTTP/17客户端；前端396及严格构建、HTTP/真实浏览器13、MySQL18、架构29通过；不代签完整.NET、跨owner/Inbox/Broker |
| T05 Platform受权重放客户端 | REQ-NTF-004..008、PAGE-NTF-003/FUNC-NTF-001局部；[设计](implementation/129-t05-authorized-message-replay-client.md) | [证据](implementation/130-t05-authorized-message-replay-client-tests.md)：前端379及严格构建、真实HTTPS浏览器/HTTP3与MySQL重放2通过；同事件/原键/理由/expected预览与未知结果重试；父DTC/跨owner/Broker不代签 |
| T05 Platform消息诊断客户端 | REQ-NTF-004..008、PAGE-NTF-003、FUNC-NTF-001局部；[设计](implementation/127-t05-platform-message-diagnostics-client.md) | [证据](implementation/128-t05-platform-message-diagnostics-client-tests.md)：前端346、真实HTTPS浏览器/HTTP3及MySQL查询1通过；首轮scope映射错误已修，父DTC/跨owner/Broker仍未关闭 |
| T01 软件实施基线 | 全部现有PAGE/FUNC及Route语义；不含预留功能扩容 | 文档签核；现场主备/真实环境仍blocked |
| T02 Host首片 | FUNC-SYS-001/004基础、架构§7、DTC-SYS-001/006部分 | 仅健康/边界切片；结果见[实施记录](implementation/01-foundation-host.md)，不将父DTC整体标passed |
| T02 MySQL基础片 | 同一FUNC/DTC的真实Provider基础、ARCH-TC-005首期部分 | [Provider记录](implementation/03-mysql-provider-foundation.md)：仅wes_test探针迁移/事务/约束/tenant/并发/清理，不代表业务owner、最小权限或完整T02验收 |
| T02 HTTP运行时/架构片 | FUNC-SYS-001/004、DTC-SYS-001/006、ARCH-TC-001/002的适用基础部分 | [运行时记录](implementation/04-host-runtime-foundation.md)：真实依赖断开/恢复、关联ID/安全错误与日志、Roslyn已知违规symbol反例；不代签其他owner/完整父DTC |
| T03 会话/策略内部片 | FUNC-PLAT-001、DTC-PLAT-002/003/010及VAL-04的部分场景 | [会话记录](implementation/05-platform-session-core.md)：绝对/idle/access期限、耐久CAS/重放撤销、租户/摘要、登录与新密码纯策略；尚无登录/验证码/JWT/页面和完整链路，不标父DTC整体passed |
| T03 可信来源耐久准入 | FUNC-PLAT-001、PAR-02、DTC-PLAT-001/010及VAL-04中来源预算部分 | [来源准入记录](implementation/09-durable-login-source-admission.md)：5领域与20迁移/真实MySQL用例通过；同可信IP跨账号/租户30/60滚动窗口、并发唯一键/CAS、失败/取消/时钟/损坏schema保护，不标完整登录/防滥用/父DTC通过 |
| T03 账号风险耐久状态 | FUNC-PLAT-001、PAR-02、DTC-PLAT-001/010及VAL-04中账号窗口部分 | [账号风险记录](implementation/10-durable-account-login-risk.md)：5领域与20迁移/真实MySQL用例通过；3后需验证码、5后锁900秒、租户隔离/版本冲突/取消保护；实际密码核验、冲突重准入和验证码原子接线仍未验收，不标父DTC整体通过 |
| T03 验证码耐久一次消费 | FUNC-PLAT-001、PAR-02、DTC-PLAT-001/010及VAL-04中验证码部分 | [验证码记录](implementation/11-durable-login-challenges.md)：9领域/保护与24迁移/真实MySQL用例通过；120秒、答案MAC/租户账号来源绑定、错误也消费与真实CAS；尚非渲染/完整登录/E2E验收，不标父DTC整体通过 |
| T03 当前会话登出 | FUNC-PLAT-001、DTC-PLAT-003及VAL-04中单family撤销部分 | [登出记录](implementation/12-audited-current-session-logout.md)：15项真实MySQL用例通过；自助owner限定、撤销/audit原子、refresh两方向竞争、幂等与失败门禁；HTTP及改密/禁用撤销范围仍待完整接线，不标父DTC整体通过 |
| T03 Seven兼容凭据编码器 | FUNC-PLAT-001、PAR-02 / VAL-04的新密码与旧密码受限兼容部分 | [编码器记录](implementation/13-compatible-password-codec.md)：17项用例通过；旧模式仅受限验证，新SHA384增强BCrypt保留完整128个Unicode标量、独立scheme、固定向量与不反射散列；账号历史/泄漏证据及完整登录仍待接线，不标父DTC整体通过 |
| T03 耐久历史与当前主体改密 | FUNC-PLAT-001、DTC-PLAT-003及PAR-02 / VAL-04的历史/改密撤销部分 | [改密记录](implementation/14-durable-password-change.md)：7项领域/格式与30项迁移/真实DB用例通过；5项含当前值、逐原scheme检查历史，凭据/既有family撤销/版本链审计原子、CAS/refresh竞争与SQL失败回滚；新family签发栅栏、泄漏服务及HTTP/页面仍待接线，不标父DTC整体通过 |
| T03 登录协调与签发栅栏 | FUNC-PLAT-001、DTC-PLAT-001..003/008/010及VAL-04中内部协调部分 | [登录记录](implementation/15-coordinated-password-login.md)：3项领域与28项迁移/真实DB用例通过；来源/挑战消费、账号门槛与真实核验同用例协同、在途错密不漏事件、risk/凭据CAS及login/改密两顺序、新旧格式会话持久限制；JWT/HTTP/渲染/页面/禁用管理仍另验，不标父DTC整体通过 |
| T04 权限/配置/审计内部片 | FUNC-PLAT-002/003/004/006、DTC-PLAT-004..006/008..009/011..012部分 | [权限配置审计记录](implementation/08-platform-access-settings-audit.md)：29领域与36真实DB/迁移用例通过，当前授权/范围字段及集合身份、4表tenant保护、角色可委派/CAS/版本审计、配置与audit原子性/不可改/受权查询；不代表组织/用户/字典语言/页面/E2E或完整T04验收 |
| T03/T04 当前授权用户禁用 | FUNC-PLAT-001/002、DTC-PLAT-003..006/008/010及VAL-04禁用撤全部family部分 | [禁用记录](implementation/16-audited-account-disable.md)：22项禁用真实DB及3项登录侧新增用例；当前Enabled字段/资源范围、原子凭据/全部family/audit、幂等修复、login/refresh两方向CAS、故障/取消与跨租户隔离通过；完整回归638项。不包含用户目录/启用重置/HTTP/页面，不标父DTC整体通过 |
| T03/T04 受权登录事件查询 | PAGE-PLAT-008、FUNC-PLAT-006、DTC-PLAT-008内部查询部分 | [查询记录](implementation/17-authorized-login-event-query.md)：24项真实MySQL测试，当前All字段权限、隐藏字段筛选拒绝、租户/时间/类型/关联先过滤再稳定分页、真实100条门限、撤权/故障/取消通过；全量662项。无不可变主体/部门时不猜Own归属；导出/保留治理/HTTP/页面仍另验 |
| T17 Route纯内核软件包 | REQ-ROUTE-001..008、DTC-ROUTE-001..004及性质/差分的静态候选路径部分 | [Route内核记录](implementation/07-route-kernel.md)：Stacker/FourWay私有不可变图、物理映射谱系、能力/方向/负载/设备约束、确定性最短路及完整资源/执行段；228自动化测试、独立oracle/seed/shrinker和SPEC/CODE审查通过。T17软件包完成不将包含预约/实时反馈的父DTC或全部Route/H验收标passed |
| T08数量领域Task 1局部基础 | WES-RULES-1.0 RULE-01；DTC-WMS-004/010/011中的纯数量合同子集 | [数量领域实施](implementation/85-t08-quantity-domain-foundation.md)及[测试证据](implementation/86-t08-quantity-domain-foundation-tests.md)：16项定向测试覆盖非负`decimal(28,6)`范围/精度、显式加减溢出、精度0..6与正单笔上限；全solution 900项回归通过。仅此纯领域子集由本地自动化验证；父DTC与T08整体仍未验收，数据库、事务、并发、权限及集成行为未覆盖 |
| T08 owner账本核心 | FUNC-WMS-007 / REQ-WMS-005..011；DTC-WMS-004/010/011数量/记账子集 | [设计](implementation/97-t08-owner-ledger-core.md)/[证据](implementation/98-t08-owner-ledger-core-tests.md)：新增11项余额领域及25项真实MySQL测试，固定维度/精确批次、收入/支出/原子移账、不可变posting/entry、永久operation/execution防重、双余额CAS及保存后中断回滚。内部原语不向Host暴露任意改账接口；业务授权、主数据、保护量持久化、Outbox和API/页面未完成，不关闭T08 |
| T08持久预留与账本保护 | SCOPE-WMS-02 / REQ-WMS-005..011、RULE-04/05/08；DTC-WMS-004/010及VAL-01内部保护子集 | [设计](implementation/99-t08-durable-stock-reservations.md)/[证据](implementation/100-t08-durable-stock-reservations-tests.md)：新增23项真实MySQL测试，含并集Available3、部分释放operation防重、Unknown不释放、余额共享CAS/混合水位拒绝、部分移账原子消费与故障/取消回滚、跨tenant/不可变身份及跨余额execution冒用拒绝；原25项账本共同回归48项通过。不代表业务授权/运输完成证明、主数据、Outbox/API/页面或T08整体完成 |
| T08禁用仓库/库位创建基础 | SCOPE-WMS-01 / REQ-WMS-001..004 / FUNC-WMS-001；DTC-WMS-001/010/011主数据创建子集 | [设计](implementation/115-t08-location-master-data.md)/[证据](implementation/116-t08-location-master-data-tests.md)：14领域+34真实MySQL测试，永久operation/精确创建事实、禁用身份与tenant复合约束、精确schema边界、并发/回滚/取消/安全DB错误；真实列属性RED推动显式binary修正，完整1313项通过。无业务授权/Host/API/页面、编辑启停、占用/容器绑定、Outbox或真实Pack证明，不关闭父DTC/T08 |
| T08当前授权主数据详情 | REQ-WMS-001..004 / FUNC-WMS-001；DTC-WMS-001/010/011、DTC-PLAT-004/005/012详情授权子集 | [设计](implementation/117-t08-current-master-data-reads.md)/[证据](implementation/118-t08-current-master-data-reads-tests.md)：公开WMS/Platform应用边界，真实创建事实creator/当前部门、当前会话/角色/成员/完整字段复验，Ordinal及取消/安全DB错误；新增22真实MySQL、联合159项/领域32项/架构29项通过。尚未装配Host/HTTP/列表/页面、创建授权或完整主数据生命周期，不关闭父DTC/T08 |
| T08只读主数据详情HTTP | REQ-WMS-001..004 / FUNC-WMS-001；DTC-WMS-001/010/011、DTC-PLAT-004/005/012传输子集 | [设计](implementation/119-t08-master-data-detail-http.md)/[证据](implementation/120-t08-master-data-detail-http-tests.md)：默认关闭/显式Identity前置，真实当前authority桥接和既有JWT subject；6配置+11真实MySQL HTTP，精确6/9字段、HTTPS/no-store、撤权/登出及真实跨tenant/无写入口。最终完整1352项通过；列表/树/页面、创建授权、启停绑定/Outbox和完整父DTC/T08未完成 |
| T08仓库/库位只读详情页面 | PAGE-WMS-001 / REQ-WMS-001..004 / FUNC-WMS-001；DTC-WMS-001/010/011、UI-001..004/007/008/011/012详情适用子集，WMS-CLIENT-01..06 | [设计](implementation/121-t08-master-data-detail-client.md)/[证据](implementation/122-t08-master-data-detail-client-tests.md)：38项客户端新增、全套278及严格构建通过；真实HTTPS/MySQL/Chromium读取双资源、独立owner撤字段403清旧、三种宽度/键盘/离线不重放/无业务缓存。完整Modules525+Architecture29共554项通过，Release零警告/错误；不把单资源详情当列表/树、写生命周期、完整父DTC或五包关闭 |
| T08当前授权主数据目录 | PAGE-WMS-001 / REQ-WMS-001..004 / FUNC-WMS-001；DTC-WMS-001/010/011、UI-001..004/007/008/011/012发现子集，WMS-CATALOG-01..07 | [设计](implementation/123-t08-authorized-master-data-catalog.md)/[证据](implementation/124-t08-authorized-master-data-catalog-tests.md)：creator完整字段范围交集、tenant内SQL授权后分页，无total；30真实owner/18 HTTP/28客户端新增，前端306及真实HTTPS选择后重读/撤权清旧通过。完整1399后新增SQL完成时取消反例，修正后145项相关复验通过，Release零警告/错误；不关闭树/写授权/生命周期、库存/Outbox及完整父DTC或整包 |
| T08当前授权创建应用边界 | PAGE-WMS-001 / REQ-WMS-001..004 / FUNC-WMS-001；DTC-WMS-001/010/011创建子集，WMS-CREATE-01..05 | [设计](implementation/125-t08-current-authorized-master-data-creation.md)/[证据](implementation/126-t08-current-authorized-master-data-creation-tests.md)：当前write完整字段、可信creator、库位父仓当前read/schema、撤权后重放拒绝，默认禁用及原子永久事实；38真实owner新增，联合124+领域/HTTP/真实浏览器50+架构29共203通过。无HTTP写接线/表单/默认schema，不关闭真实Pack、其他生命周期/父DTC及整包 |
| T07隔离文件基础 | REQ-PLAT-007..009 / FUNC-PLAT-005、PAR-04 / RULE-12；DTC-PLAT-007、VAL-05子集 | [设计](implementation/93-t07-quarantined-file-lifecycle.md)/[证据](implementation/94-t07-quarantined-file-lifecycle-tests.md)：新增40项领域/存储/ClamAV协议和8项真实MySQL测试，覆盖精确字节证据、attempt截止、tenant键、owner大小写隔离/真实列排序规则、绑定唯一及绑定/清理两种CAS顺序。协议不是病毒清除，应用协调、当前实体授权、HTTP/页面和真实扫描仍未完成；不关闭T07或父DTC |
| T07扫描/孤儿删除协调 | REQ-PLAT-007..009 / FUNC-PLAT-005、PAR-04 / RULE-12；DTC-PLAT-007及VAL-05协调子集 | [设计](implementation/101-t07-file-processing-coordination.md)/[证据](implementation/102-t07-file-processing-coordination-tests.md)：新增20项真实MySQL协调测试，原8项文件持久化共28项通过；持久claim先于IO、原截止预算、证据错配/迟到/取消、并发单claim、真实私有存储及对象删除后数据库确认失败恢复。不替代实体授权、上传/引用/下载、HTTP/页面或真实ClamAV病毒清除，不关闭T07 |
| T07当前授权暂存访问 | REQ-PLAT-007..012 / FUNC-PLAT-005/006、RULE-12 / PAR-04；DTC-PLAT-007/008和VAL-05暂存子集 | [设计](implementation/103-t07-authorized-staging-file-access.md)/[证据](implementation/104-t07-authorized-staging-file-access-tests.md)：21项真实MySQL测试，真实creator/当前部门、Own/All/部门与字段权限、撤权/绑定后拒绝、Available验证/可释放字节、审计提交后才交付及故障/取消清零；完整回归1147项通过。只限未绑定暂存文件，不代替实体附件授权、上传/HTTP/页面或真实扫描，T07仍partial |
| T07持久受权上传 | REQ-PLAT-007..012 / FUNC-PLAT-005/006、RULE-12 / PAR-04；DTC-PLAT-007/008和VAL-05上传子集 | [设计](implementation/105-t07-durable-authorized-uploads.md)/[证据](implementation/106-t07-durable-authorized-uploads-tests.md)：新增23项真实MySQL上传与4项名字校验反例；永久tenant/operation防重、当前Own授权、先审计后存储、精确字节恢复、部分对象拒绝覆盖、并发唯一及20MiB边界；完整1174项通过。仅持久隔离成功，不代替HTTP流限额、业务实体授权、页面或真实病毒清除，T07仍partial |
| T07暂存HTTP传输 | REQ-PLAT-007..012 / FUNC-PLAT-005/006；DTC-PLAT-007/008、PAR-04/VAL-05暂存传输子集 | [设计](implementation/107-t07-staging-file-http.md)/[证据](implementation/108-t07-staging-file-http-tests.md)：新增46项流/配置/HTTP/MySQL/真实私有存储测试，HTTPS Kestrel验证中文名及chunked M/M+1、当前授权/跨tenant、永久防重、恢复503/损坏500、下载审计和交付清零、不可用扫描仍隔离及到龄孤儿删除。完整1220项通过；页面、业务实体绑定与真实扫描/ACL仍未验收，不关闭T07 |
| T07暂存客户端 | PAGE-PLAT-007 / FUNC-PLAT-005/006；DTC-PLAT-007/008、DTC-UI-001..011暂存单文件适用子集 | [设计](implementation/109-t07-staging-file-client.md)/[证据](implementation/110-t07-staging-file-client-tests.md)：真实XHR字节进度、UTF-8名字、显式重试、当前scope/查询目标门禁、隔离扫描、审计下载与URL释放、版本删除。前端220项/严格构建、真实HTTPS Chromium和.NET 1221项通过；非完整文件列表，业务实体授权与真实扫描/ACL仍未验收，不关闭父DTC/T07 |
| T04固定审计导出 | PAGE-PLAT-008 / FUNC-PLAT-006；DTC-PLAT-008、DTC-UI-011固定水位导出子集 | [设计](implementation/111-t04-authorized-audit-export.md)/[证据](implementation/112-t04-authorized-audit-export-tests.md)：当前read/export字段和scope交集、显式列、原600秒成员、永久operation、审计/Outbox提交后CSV，SQL失败回滚/撤权/并发防重。8 HTTP+14真实MySQL owner、前端228项及真实HTTPS Chromium通过；最终当前编译源.NET1243项通过，既有健康探测间歇失败如实保留；不关闭T04/T06整体 |
| T07当前授权暂存目录 | PAGE-PLAT-007 / FUNC-PLAT-005；DTC-PLAT-005/007/014、DTC-UI-003/007/008暂存目录子集 | [设计](implementation/113-t07-authorized-staging-catalog.md)/[证据](implementation/114-t07-authorized-staging-catalog-tests.md)：SQL逐字段scope/tenant/引用过滤先于分页；状态/共享分页、选择后重读、Stale禁选及撤权/旧响应边界。新增14 HTTP+8真实MySQL+12客户端、前端240项/严格构建、真实HTTPS Chromium和完整.NET1265项通过。业务附件授权、真实扫描/ACL及五个整包剩余门禁仍未关闭 |
| T04/T05普通回执保留维护 | PAR-09 / VAL-08、DTC-PLAT-011、DTC-NTF-002普通结果保留子集 | [维护设计](implementation/87-platform-retention-maintenance.md)及[测试](implementation/88-platform-retention-maintenance-tests.md)：默认禁用，显式tenant白名单/有界周期，目录回执与Inbox各90天、同owner事务提交；34项配置/托管测试及12项新增真实MySQL测试，另13项保留回归通过。两owner竞争正确归类瞬时失败，未删审计/Outbox/领域身份；不是Quartz作业、真实Broker或生产清理许可，T04/T05仍partial |
| T05 Platform托管发布调度 | FUNC-NTF-001、DTC-NTF-001/002及VAL-07子集 | [设计](implementation/95-t05-hosted-platform-publisher.md)/[证据](implementation/96-t05-hosted-platform-publisher-tests.md)：25项Host测试、5项新增真实MySQL测试；显式启用/能力/唯一transport检查、tenant白名单、单batch、60秒预算/停止取消与有限持久结果。未配置transport明确失败；没有真实Broker、跨owner或Notifications聚合验收，不关闭T05 |
| T06候选撤权与旧请求边界 | DTC-UI-021/022/024在Platform Users/Roles及共享候选的适用子集 | [候选设计](implementation/89-t06-candidate-authorization-boundary.md)及[测试](implementation/90-t06-candidate-authorization-boundary-tests.md)：当前拒绝清候选和实际编辑草稿，旧成功/失败/已取消signal不污染新范围，保留25条分页/稳定ID；整套前端201项及严格构建通过。不代表本片浏览器E2E或完整T06验收 |
| T08保护量并集纯领域基础 | SCOPE-WMS-02 / REQ-WMS-005..011、RULE-01/05/08及REV-03；DTC-WMS-004/010与VAL-01计算子集 | [并集设计](implementation/91-t08-stock-protection-union.md)及[测试](implementation/92-t08-stock-protection-union-tests.md)：23项新增领域测试；按互不重叠执行单元的保护标志计算并集，Qty10/Reserved7含InTransit5得到Available3，Unknown继续保护。调用owner仍须证明单元划分/同库存维度；没有API、持久化、租户授权或并发预留，不关闭父DTC或T08 |

## 1. ID 规则

| 类型 | 语法 | 示例 |
|---|---|---|
| 范围项 | `SCOPE-{DOMAIN}-{NN}` | `SCOPE-WMS-02` |
| 需求 | `REQ-{DOMAIN}-{NNN}` | `REQ-WMS-014` |
| 测试 | `{DOMAIN}-TC-{NNN}` | `WMS-TC-005` |
| 页面/面板 | `PAGE-{DOMAIN}-{NNN}` | `PAGE-BUS-001` |
| 功能用例 | `FUNC-{DOMAIN}-{NNN}` | `FUNC-WMS-002` |
| 详细测试记录 | `DTC-{DOMAIN}-{NNN}` | `DTC-WMS-003` |
| ADR | `ADR-{NNNN}` | `ADR-0003` |
| 缺陷/风险 | 团队 issue ID + owner | `WES-123 / Route lead` |

一条 REQ 用可验证的 shall/必须语义描述一个行为或不变量。每个 SCOPE 至少一条 REQ；一条 REQ 至少一个正向测试和适用的拒绝/并发/故障测试。不可测试的安全/活性需求须明确假设、分析证据与独立评审责任人。

## 2. 状态

- 需求：`proposed / approved / implemented / verified / deferred / rejected`
- 测试：`designed / automated / passed / failed / blocked / waived`
- 范围：沿用 [scope matrix](01-business-scope-and-status.md) 的 `保留 / 补齐/复核 / 预留 / 移除 / 待核`
- `waived` 必须有决策人、理由、风险接受和到期/复审日期，不能仅为通过 CI。

## 3. 初始范围到设计/测试映射

需求 ID 为本项目建立的追踪键。以下行表示详细设计覆盖和测试目录入口，不表示相关功能已实现或测试已通过。

| Scope ID | REQ ID | 详细设计 | 测试目录 | 状态/备注 |
|---|---|---|---|---|
| SCOPE-PLAT-01 | REQ-PLAT-001..003 | [Platform 与安全](modules/platform-and-security.md) §2 | PLAT-TC-001..004 | 设计中 |
| SCOPE-PLAT-02 | REQ-PLAT-004 | [Platform 与安全](modules/platform-and-security.md) §3.1 | PLAT-TC-005 | 设计中 |
| SCOPE-PLAT-03 | REQ-PLAT-005..006 | [Platform 与安全](modules/platform-and-security.md) §3.2-3.3 | PLAT-TC-006..008 | 租户策略待 ADR |
| SCOPE-PLAT-04 | REQ-PLAT-007..009 | [Platform 与安全](modules/platform-and-security.md) §4 | PLAT-TC-011, 015..018 | 配置/字典/国际化/文件 |
| SCOPE-PLAT-05 | REQ-PLAT-010..012 | [Platform 与安全](modules/platform-and-security.md) §5 | PLAT-TC-012..013, 019..020 | 审计/日志/健康/指标 |
| SCOPE-PLAT-06 | REQ-PLAT-013..014 | [通知/定时任务](modules/notifications-and-jobs.md) §4 | NTF-TC-013..014 | HotStore 归属需核验 |
| SCOPE-WF-01 | REQ-WF-001..005 | [Workflow 与表单](modules/workflow-and-forms.md) §2-3 | WF-TC-001..011 | 条件/并行能力待确认 |
| SCOPE-WF-02 | REQ-WF-006..008 | [Workflow 与表单](modules/workflow-and-forms.md) §4 | WF-TC-012..014 | 运行时，不生成源码 |
| SCOPE-WF-03 | REQ-NTF-001..003 | [通知/定时任务](modules/notifications-and-jobs.md) §2 | NTF-TC-006..008 | 集群策略待确认 |
| SCOPE-WF-04 | REQ-NTF-004..008 | [通知/定时任务](modules/notifications-and-jobs.md) §1 | NTF-TC-001..005, 015 | Broker/保留期待确认 |
| SCOPE-WF-05 | REQ-NTF-009..014 | [通知/定时任务](modules/notifications-and-jobs.md) §3, 5 | NTF-TC-009..012, 015 | 新闻闭环待核 |
| SCOPE-WMS-01 | REQ-WMS-001..004 | [WMS 账本](modules/wms-and-ledger.md) §2 | WMS-TC-002, 011 | 设计中 |
| SCOPE-WMS-02 | REQ-WMS-005..011 | [WMS 账本](modules/wms-and-ledger.md) §1, 3-6 | WMS-TC-001, 005, 010..017 | 账本权威 |
| SCOPE-WMS-03 | REQ-WMS-012..014 | [WMS 账本](modules/wms-and-ledger.md) §3 | WMS-TC-001..004, 015 | 运输回写 |
| SCOPE-WMS-04 | REQ-WMS-015..019 | [WMS 账本](modules/wms-and-ledger.md) §4 | WMS-TC-005..010, 015 | 保留既有拣选并补 E2E |
| SCOPE-WMS-05 | REQ-WMS-020..023 | [WMS 账本](modules/wms-and-ledger.md) §6 | WMS-TC-012..013 | 盘点并发待核 |
| SCOPE-WMS-06 | REQ-WMS-024..026 | [WMS 账本](modules/wms-and-ledger.md) §5, 7 | WMS-TC-014, 016, 018..020 | TransferOrder 与扩展不重复账本 |
| SCOPE-PDA-01 | REQ-PDA-001..006 | [PDA](modules/pda.md) §3-4 | PDA-TC-001..007, 013..015 | 现有页面/API待 E2E |
| SCOPE-PDA-02 | REQ-PDA-007..010 | [PDA](modules/pda.md) §2, 4.5 | PDA-TC-008..012 | 离线只读已确认 |
| SCOPE-BUS-01 | REQ-BUS-001..012 | [Orchestration](modules/orchestration.md) §2-6 | BUS-TC-001..016 | 幂等/恢复需实现 |
| SCOPE-WCS-01 | REQ-STK-001..012 | [Stacker](modules/wcs-stacker.md) §2-5 | STK-TC-001..015 | Route 见独立矩阵 |
| SCOPE-WCS-02 | REQ-FW-001..014 | [FourWay](modules/wcs-fourway.md) §2-6 | FW-TC-001..018 | Route 见独立矩阵 |
| SCOPE-WCS-03 | REQ-WCS-RES-001 | [范围矩阵](01-business-scope-and-status.md) | 无（预留） | 需启动独立需求/ADR |
| SCOPE-WCS-04 | REQ-ADP-001..009 | [外部 WCS/设备适配器](modules/external-wcs-and-device-adapters.md) §2 | ADP-TC-001..007 | 当前取消/查询不完整 |
| SCOPE-DEV-01 | REQ-DEV-001..010 | [DeviceComm](modules/device-communication.md) §2-5 | DEV-TC-001..015 | Phase H 现状待确认 |
| SCOPE-DEV-02 | REQ-SITE-001..004 | [环境搭建、仿真与 SCADA](modules/environment-simulation-and-scada.md) §1, 3, 5 | SITE-TC-001..003 | SCADA 只读地图投影；地图优先运维 UI 见 Orchestration |
| SCOPE-DEV-03 | REQ-SITE-005..011 | [环境搭建、仿真与 SCADA](modules/environment-simulation-and-scada.md) §2, 5 | SITE-TC-004..011, 029..033 | Deploy、仿真、真机 Promote 与地图版本协调 |
| SCOPE-OPS-01 | REQ-SITE-012..016, REQ-OPS-001..005, REQ-OPS-008..010 | [Orchestration](modules/orchestration.md) §8.1-8.2、[Stacker WCS](modules/wcs-stacker.md) §5、[FourWay WCS](modules/wcs-fourway.md) §6 | SITE-TC-012..016, 019..028 | 2D 地图优先；问题由 Orchestration 管理，跨包协调在 Orchestration，包级校验在 WCS |
| SCOPE-OPS-02 | REQ-OPS-006..007、REQ-ARCH-008 | [架构](02-system-architecture.md) §7.1、[功能开关](detailed-design/feature-switches.md)、[验收矩阵](acceptance/01-cross-module-and-provider-matrix.md) | SITE-TC-017..018、ARCH-TC-011、DTC-SYS-008 | 隔离恢复、SingleWriter/fencing及部署功能组合属于系统验收 |
| SCOPE-DB-01 | REQ-ARCH-001..004 | [架构](02-system-architecture.md) §5 | ARCH-TC-005, WMS-TC-017 | 三 provider |
| SCOPE-EXT-01 | REQ-ARCH-005..007 | [架构](02-system-architecture.md) §3, 4 | ARCH-TC-001..004 | 保留扩展意图 |
| SCOPE-REMOVE-01 | REQ-GOV-001 | [工程治理](03-engineering-governance.md) §1, 7 | ARCH-TC-001 + review checklist | 源码生成移除 |

### 3.1 Site and operations requirement-level traceability

每条要求均须有明确设计 owner 和测试入口。此表记录的是设计覆盖，不代表功能已实现或用例已执行。

| Requirement | Design owner | Test ID(s) | Status |
|---|---|---|---|
| REQ-SITE-001 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §1, 3, 5 | SITE-TC-001 | designed |
| REQ-SITE-002 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §3, 5 | SITE-TC-002 | designed |
| REQ-SITE-003 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §3, 5 | SITE-TC-003 | designed |
| REQ-SITE-004 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §1, 3, 5 | SITE-TC-001, SITE-TC-003 | designed |
| REQ-SITE-005 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §2.1, 5 | SITE-TC-004 | designed |
| REQ-ARCH-008 | [功能开关](detailed-design/feature-switches.md) §1..7、[架构](02-system-architecture.md) §7..8 | ARCH-TC-011、DTC-SYS-008 | designed |
| REQ-SITE-006 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §2.1, 5 | SITE-TC-005, SITE-TC-029..032 | designed |
| REQ-SITE-007 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §2.1, 5 | SITE-TC-006, SITE-TC-033 | designed |
| REQ-SITE-008 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §2.2, 5 | SITE-TC-007 | designed |
| REQ-SITE-009 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §2.2, 5 | SITE-TC-008 | designed |
| REQ-SITE-010 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §2.3, 5 | SITE-TC-009, SITE-TC-010 | designed |
| REQ-SITE-011 | [环境/仿真/SCADA](modules/environment-simulation-and-scada.md) §2.3, 5 | SITE-TC-011 | designed |
| REQ-SITE-012 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-012 | designed |
| REQ-SITE-013 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-013 | designed |
| REQ-SITE-014 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-014 | designed |
| REQ-SITE-015 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-015 | designed |
| REQ-SITE-016 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-016 | designed |
| REQ-OPS-001 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-019 | designed |
| REQ-OPS-002 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-019, SITE-TC-022, SITE-TC-026 | designed |
| REQ-OPS-003 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-020 | designed |
| REQ-OPS-004 | [Orchestration](modules/orchestration.md) §8.1, 8.2 | SITE-TC-021, SITE-TC-025, SITE-TC-027, SITE-TC-028 | designed |
| REQ-OPS-005 | [Orchestration](modules/orchestration.md) §8.1 | SITE-TC-023 | designed |
| REQ-OPS-006 | [架构](02-system-architecture.md) §7.1、[验收矩阵](acceptance/01-cross-module-and-provider-matrix.md) §1 | SITE-TC-017 | designed |
| REQ-OPS-007 | [架构](02-system-architecture.md) §7.1、[验收矩阵](acceptance/01-cross-module-and-provider-matrix.md) §1 | SITE-TC-018 | designed |
| REQ-OPS-008 | [Orchestration](modules/orchestration.md) §8.2 | SITE-TC-024 | designed |
| REQ-OPS-009 | [Orchestration](modules/orchestration.md) §8.2 | SITE-TC-026 | designed |
| REQ-OPS-010 | [Orchestration](modules/orchestration.md) §8.2 | SITE-TC-025, SITE-TC-027, SITE-TC-028 | designed |

## 4. Route 追踪矩阵

| Route requirement | 设计章节 | 测试目录 |
|---|---|---|
| REQ-ROUTE-001..004 地图结构/版本/错误 fail-closed | [目标设计](route/02-target-routing-and-scheduling.md) §2 | ROUTE-TC-001..007 |
| REQ-ROUTE-005..008 车辆能力与确定性候选路线 | 同上 §3-4 | ROUTE-TC-008..013 |
| REQ-ROUTE-009..014 冲突资源/时间窗/原子预约 | 同上 §2, 5 | ROUTE-TC-014..022 |
| REQ-ROUTE-015..019 反馈/在线目标/重规划/恢复 | 同上 §6-7 | ROUTE-TC-023..030 |
| REQ-ROUTE-020..024 活性/死锁/公平/设备差异 | 同上 §6, 8 | ROUTE-TC-031..039 |
| REQ-ROUTE-025..027 基准/现场验收/安全边界 | [测试目录](route/03-route-test-catalog.md) §1, 8 | ROUTE-TC-001..039 + SITE-TC-007, 017 |
| REQ-ROUTE-028 持久预约账本是 grant 权威 | [目标设计](route/02-target-routing-and-scheduling.md) §5.1、[ADR-0001](adr/ADR-0001-route-reservation-authority.md) | ROUTE-TC-040, 043..045 |
| REQ-ROUTE-029 单写者 fencing、接管对账和未知占用保护 | 同上 | ROUTE-TC-041..042, 046 |

## 5. 新功能追踪模板

新增范围前先分配 SCOPE/REQ ID，然后在此表添加一行：

| Scope ID | REQ ID | Requirement (shall) | Owner | Design section | Test ID(s) | Status | Evidence/ADR |
|---|---|---|---|---|---|---|---|
| SCOPE-XXX-01 | REQ-XXX-001 | 系统必须…… | 模块/负责人 | `docs/modules/... §...` | `XXX-TC-001` | proposed | issue/ADR link |

测试 case 应在模块设计中的对应详细章节描述输入、前置状态、步骤、持久化/外部副作用、预期错误和 cleanup。自动化后回填 runner、provider/设备环境、结果日期和报告链接。

## 6. 变更规则

### 页面/功能深化追踪入口

[详细设计覆盖索引](detailed-design/README.md)逐包关联SCOPE、PAGE、FUNC、DTC与本矩阵既有REQ/TC。原TC不改号；共用UI测试与跨模块DTC独立保留。模块数字简写仅限同域，不能跨域推断ID。设计映射、规范性需求批准和测试执行是不同状态。

Legrand/GitHub密集页面对照补充见[交互合同与评审缺口](detailed-design/03-dense-page-interaction-contract.md)：DTC-UI-013..032关联全部适用PAGE的列配置/个人视图/关联/筛选；13个模块各增一条关联交互DTC，关联原FUNC和TC。逐页矩阵以本域PAGE数字引用，不重复定义ID。个人偏好仅本机用户/租户/环境隔离，不增加团队共享、离线写入或新业务状态转换。

部署功能开关按[功能开关设计](detailed-design/feature-switches.md)新增REQ-ARCH-008、FUNC-SYS-005、DTC-SYS-008及ARCH-TC-011；不新增PAGE，也不复用已占用的DTC-SYS-007。当前详细设计统计为64 PAGE/66 FUNC/123模块DTC，加32条UI与13条XMOD，合计168条DTC；历史批次报告保留其生成时的统计快照。

2026-10-23项目发起人批准[WES-RULES-1.0](detailed-design/05-approved-rules-and-defaults.md)：REV-01..04的RULE-07/08/13/20已回写WMS/Orchestration/部署/Route及ADR范围澄清；其他RULE-01..29按B业务/P初始参数/H缺现场证据阻断分类。RVT-01..09和VAL-01..08为既有正式DTC必测变体；功能开关新增后当前统计为64 PAGE/66 FUNC/168 DTC，数量变化不新建第二套规则号；每项回归入口见规则基线§5。批准不是逐条REQ/Provider/设备或性能验收，原TC映射仍需证据。

后续Task映射见[开发/测试总计划](superpowers/plans/2026-10-23-wes-development-and-testing.md)及[测试覆盖§3](superpowers/plans/2026-10-23-wes-test-execution.md)：T01..T34为工程工作包编号，不是新增业务REQ或测试结果。追踪链扩为SCOPE/REQ→FUNC/PAGE→Task→DTC/variant→实际RunId/证据；现阶段所有开发工作包及执行结果仍未开始。

Route旧范围键001..027已给出[逐条语义候选与详细测试](detailed-design/route.md)，待负责人签核，不把候选等同已批准要求；028..029继续以ADR-0001为权威。

每次更改业务行为、状态/权限、API/event、provider、Route 或安全边界时，同 PR 更新本矩阵、范围矩阵和对应设计/测试。移除 requirement 前须记为 deferred/rejected，并由 owner 说明影响；测试变更不应让未满足的 requirement 消失。

### 2026-10-24 T04 directory owner implementation evidence

REQ-PLAT-004..009 → FUNC-PLAT-002..004 → T04 → DTC-PLAT-004..006/008/011/014 的具名后端目录、角色成员、租户切换准入、字典语言/引用保护实现及 MySQL scoped runner 证据见 [18-t04-directory](implementation/18-t04-directory.md)。包括当前目录资格 fail-closed、逐资源字段投影、旧/新部门范围复验、独立凭据创建、显式本地 bootstrap、原子追加审计、实际并发成员写与保存后回滚；不覆盖全部 DTC 的页面/HTTP/其他 Provider 验收，不将 T04 整包或相关 REQ 标为已验收。
