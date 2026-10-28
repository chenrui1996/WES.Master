# T11 TransferOrder测试记录

- 状态：基本软件子片已验收，父T11未整体关闭。设计：[185](185-t11-transfer-order.md)。前文中间状态/失败保留，最终门禁以末节为准。
- 范围：WMS单据/预留/逐单元耐久到达与记账恢复、当前授权HTTP/具名客户端；真实T15/Pack、生产、物理安全与其他Provider未验收。
- 下列为本片实际执行记录；后续按实际命令/选择器/失败/修复/最终结果追加，不沿用盘点通过数。完成前保持实施中。

## 初始边界核对

1. 已查唯一PostStock、预留、资源fence与既有可信入库完成Inbox模式；不创建第二账本writer。
2. 现有余额Transfer固定同容器，基本公开路径保留整容器单正维度限制，不能声称来源10移动3且容器在两位合法；多维度/拆容器仍为主体软件依赖。
3. 适用踩坑条目见185；当前未新增可复用已确认缺陷。

## 前端RED/GREEN与状态合同

2026-10-29，本worktree，Node现有依赖，无新增包：

- `npm test -- --run src/features/wms/transfers.spec.ts --maxWorkers=1`：初次RED，缺少具名client模块；实现后29通过/1失败（错误消息断言要求“全部”，实际“完整”），改为与合同一致的消息断言。
- `npm test -- --run src/features/wms/TransfersPage.spec.ts --maxWorkers=1`：初次RED，缺少具名页面；实现后client+页面41项全通过。
- 初次严格build发现两项类型错误（回调中unknown属性未持久窄化、TestUtils的get返回值没有exists）；改为固定已窄化precision局部变量及find断言，未加any/双重cast或降低strict。
- `npm run build; npm test -- --maxWorkers=1`：strict vue-tsc/Vite通过，全量50文件/1205项通过；这是扩展路由/100行等补充用例之前的中间回归，不当作最终收口。
- 扩展后`npm test -- --run src/features/wms/transfers.spec.ts src/features/wms/TransfersPage.spec.ts src/features/navigation.spec.ts src/router.spec.ts --maxWorkers=1`：4文件/95项通过；随后`npm run build`通过。
- 再完成父状态互斥及实际设计token/窄屏按钮样式核对后，`npm run build; npm test -- --maxWorkers=1`：strict vue-tsc/Vite通过，全量50文件/1215项通过，54.56秒，无失败/跳过。浏览器测试TypeScript也在该strict build中检查；dist此后冻结等待受控浏览器，不将脚本仅类型检查算执行通过。

已覆盖精确read/receipt形状及未知字段拒绝、十进制精度/数量守恒/执行-同步分离、全部源目标版本冻结、多行草稿、真实UI100行上限、扫码完整tuple、branch1禁止人工伪完成、Pending/Failed/Unknown不关闭、父查询stale不卸载原操作、503/10秒/离线Unknown原key/body手动恢复、不自动重试/不存本地写队列、畸形201/后续409不能抹掉先前未知、主体/字段撤权/路由变化与迟到隔离、明确另GET、路由/模块/权限门禁。

受控HTTPS浏览器脚本已添加但此时尚未运行；不得把上述组件模拟当作真实MySQL或浏览器结果。检查GEN-002/004/005/006与DEF数量边界，当前无新增通用问题。

## 后端owner RED/GREEN与受控边界

2026-10-29，本worktree，.NET命令串行，既有依赖，无新增包；连接仅由`MySqlTestConnection.LoadAuthorizedTestConnection`取得并限制`wes_test`，未打印连接、未执行Down、未启动生产或设备通信：

- 初始`dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~TransferApprovalInsufficientSecondWholeContainer --logger "console;verbosity=normal"`：合法A3/B2夹具建立后真实RED1，`Committed`预期对`Unsupported`；完成owner/EF迁移后GREEN1。其断言是B计划3但库存2时Draft保留且无任何预留，不以逐行独立Reserve替代整单事务。
- A3/B1逐单元恢复测试`FullyQualifiedName~TransferAThreeRemainsPosted`首次真实RED1，发现`MarkInTransit`审计quantity必须为0而不是计划全量；按既有预留合同修复后GREEN1。A dispatch/扫描/唯一Transfer成功，B独立到达事务已提交后注入PostStock保存异常；A保持已移3，B已到Pending且仍保护1，关闭拒绝；新owner原arrival键返回原最小回执，显式sync仅补B一次，全部Synced后父Completed，最终2个Transfer及零活跃预留/保护。
- 首轮`FullyQualifiedName~TransfersHttp`真实HTTP18通过：所有路由默认关闭，HTTPS/Identity/session/client/key、当前read及完整资源授权、原回执与精确20字段line、整单审核和严格JSON未知/重复/缺失/数值quantity拒绝。后续扩展固定动作权限与缺schema启动失败，最终数量以后续记录为准。
- 扩展owner矩阵首轮31项中27通过/4失败：盘点Start遇新transfer保护时异常未映射为Blocked；补上Count owner显式拒绝，未放松保护。扩展38项首轮33通过/5失败：100行夹具连续PostStock未清owner以及保护后试图新建盘点草稿的测试前置不符；修正夹具owner清理并在审核前建立盘点草稿，保护后验证Start拒绝。受影响10项随后全部通过，含真实100行create/read/approval及100笔源预留，不用UI按钮上限代替DEF-005后端容量。
- EF实际生成`20261028164458_WmsTransferOrders`及`20261028165850_WmsTransferOutbox`与snapshot；只由测试夹具应用。三表分别保留不可变计划/动作事实/唯一单元记账pending Outbox；后者不冒充现有主数据发布worker已发布或真实Broker已接入。

新增守卫覆盖全部写后撤权回滚、真实不同document/resource creator、永久同键并发重放/异键冲突、整容器单正维度限制、精确扫描、人工分支和运输Unsupported、Draft/全未执行安全Cancelled、InTransit/Unknown/已到待同步不可关闭、共享源/目标/容器保护及直接EF计划/状态/历史/伪执行/账本/预留绕写拒绝。保护接入PostStock、预留、维度登记、入出库共同fence、仓/库位启停及盘点，不依赖HTTP开关保持启用。

`ControlledTransferBrowserPreservesReceiptsAndExactLedger`已实际启动临时证书Kestrel/健康探测并执行既有Playwright Chromium脚本。首次RED：第一流程已显示A同步/B安全关闭的ClosedPartial，第二创建在`transfers.spec.ts:116 -> create():47`超时找不到`confirm-transfer`；页面快照有三行（第三行空），原两行草稿未清而脚本再次增加行，未形成第二次预览。不将该失败记为通过；待前端owner修复后重跑并核验最终3个Transfer、精确余额及零保护。

- 关联真实MySQL回归：`dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~WmsMasterDataTests|FullyQualifiedName~WmsLedgerTests" --logger "console;verbosity=normal"`，961通过/0失败/0跳过，20.38分钟。覆盖全部既有账本/主数据/入库执行及可信运输/出库/盘点与当时38个Transfer测试；后续直接EF防绕写、目标占用及夹具分位加固再由最终Transfer focused覆盖，不重复累计。
- 后续审查确认：仅检查所选目标零余额不足以排除同位置其他容器的正库存；审核和sync按既有收货边界补目标整体空位、外部预留及整源单正维度全量复验，不能把两个执行容器同时承诺到同一目标位置。A/B夹具改为各自独立的来源/目标位置，HTTP字段/权限/枚举和浏览器环境键不变。直接EF ledger/源预留释放也须精确同事务Transfer事实，Synced必须对应源全量清零/目标从零入账及唯一Outbox。
- 最终owner矩阵：`dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --no-build --filter "FullyQualifiedName~WmsMasterDataTests.Transfer" --logger "console;verbosity=normal"`，54通过/0失败/0跳过，4.4673分钟。包含最终目标占用/同位冲突、空lineId拒绝、全部写后撤权回滚、直接EF防绕写与A3/B1到达故障/新owner同步；不是把中间51与后补3重复累计为额外覆盖。
- 最终严格HTTP/配置及盘点保护关联回归：`dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~TransfersHttp|FullyQualifiedName~TransfersSettings|FullyQualifiedName~CountsHttp" --logger "console;verbosity=normal"`，53通过/0失败/0跳过，6.7246分钟。随后补逻辑同名不同大小写的根`Code/code`及嵌套`SourceBalanceId/sourceBalanceId`重复用例，单独`FullyQualifiedName~TransfersHttpRejectsEveryMissingDuplicateUnknownAndNumericQuantityField`再次1通过；既有严格binder已拒绝，未另改全局JSON选项或既有Outbound合同。
- 对全部已改/新增C#文件定向执行`dotnet format WES.slnx --no-restore --include <owned changed C# paths> --verbosity quiet`，再加`--verify-no-changes`通过；`git diff --check`通过。
- `dotnet build WES.slnx --configuration Release --no-restore --verbosity quiet`通过，0警告/0错误。`dotnet test tests\Architecture\Wes.Architecture.Tests.csproj --configuration Release --no-restore --no-build --logger "console;verbosity=minimal"`，29通过/0失败/0跳过。
- `dotnet ef migrations has-pending-model-changes --project src\Modules\Wms\Wes.Wms.csproj --startup-project src\Modules\Wms\Wes.Wms.csproj --configuration Release --no-build`通过，无待迁移模型变化。以`20261028090852_WmsBlindCounts`为起点、`20261028165850_WmsTransferOutbox`为终点生成并审查双向增量SQL：Up仅增加documents/facts/outbox及其约束/唯一索引，使用模块独占`__WesWmsMigrations`；Down顺序为outbox→facts→documents，仅供审查，**未执行**。生成SQL核对后清理，不替代EF迁移源码/snapshot。

后端软件focused验证就绪；受控浏览器仍为上述真实RED，最终3次记账/6条entry/零保护断言尚未执行到，不得宣布浏览器或整个T11关闭。前端owner修复草稿复用后，以`dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~ControlledTransferBrowserPreservesReceiptsAndExactLedger" --logger "console;verbosity=normal"`重跑；fixture通过`WES_E2E_*`传入既有`transfers.spec.ts`，不会运行生产连接或伪设备完成。运输分支、真实T15/Pack、Outbox发布worker/Broker、拆容器/多正维度、其他Provider及物理验收仍未交付或未验证，不转成成功fallback。未commit/push/删除worktree。

### 后续review：fence后原键回放授权刷新（待执行）

复核确认create及通用动作在取得fence后查到并发winner事实时，直接返回Original而未重新检查当前read/action/resource字段权限。新增`WmsMasterDataTests.TransfersReplayAuthority.cs`的16个真实MySQL并发测试：winner保存后持有事务/fence暂停；waiter完成旧权限检查后等待winner；第一条仅在全部fence之后执行的查询撤销waiter的action/read或五类实际resource creator权限；断言原键不能绕过当前授权，并必须在fence后重新读取原document/resource。另两项保持授权但使waiter的schema不可用，要求合法原键仍返回原回执，不重做只适用于新动作的状态/运行/schema检查。此时父owner正在执行最后关联回归，依约只添加测试，**未运行任何dotnet/build**；这些新增测试尚未声明RED或GREEN，待父owner允许后先验证真实RED，再修复并GREEN。

父owner允许串行.NET后，2026-10-29本worktree实际执行，以下结果覆盖上段“待执行”状态：

- `dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~TransferConcurrentOriginal" --logger "console;verbosity=normal"`：首次真实RED16/16。首次修复后15通过/1失败，暴露测试屏障仅等初次授权结束，winner可能先于waiter原键查询提交，导致waiter走fence前回放。把屏障移到waiter初次OperationId事实查询已返回空reader之后，未放松任何断言；暂时移除两处修复再验证确定性RED16/16（53.3278秒），恢复修复后GREEN16/16（53.5905秒）。
- 修复仅位于`src\Modules\Wms\Features\Transfers\TransferAccess.cs`：create和通用动作的fence后原键分支统一调用`AuthorizedOriginalAsync`，在仍持有fence的事务中重新加载原document/history和全部dimensions/实际creator，要求完整资源集合及当前read/action/resource字段权限，再校验原tuple返回原版本。缺资源/撤权不返回成功；不执行新动作版本/state/schema/operational/库存变更限制，原Applied回执保持永久原样。
- 最终全Transfer：`dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~WmsMasterDataTests.Transfer" --logger "console;verbosity=normal"`，**70通过/0失败/0跳过，5.2039分钟**。
- 严格HTTP/settings与受控浏览器合并一次：`dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~TransfersHttp|FullyQualifiedName~TransfersSettings|FullyQualifiedName~ControlledTransferBrowserPreservesReceiptsAndExactLedger" --logger "console;verbosity=normal"`，**32通过/0失败/0跳过，6.3664分钟**。其中真实HTTPS Chromium `ControlledTransferBrowserPreservesReceiptsAndExactLedger`通过（1分37秒）；父owner此前修复fresh draft，真实两流程完成，原201响应丢失503后同key200；fixture独立MySQL核验ClosedPartial/Completed、A原source3/target0、Bsource0/target1、唯一3个Transfer/6条entry/3条Outbox、零活跃预留/保护。此前浏览器RED保留为历史，当前已GREEN。
- 最终`dotnet build WES.slnx --configuration Release --no-restore --verbosity quiet`，0警告/0错误；Architecture相同Release/no-restore/no-build命令29通过/0失败/0跳过；EF相同project/startup/Release/no-build模型漂移检查无变化；全部改动C#定向format与`--verify-no-changes`通过，`git diff --check`通过。
- 父owner报告修复前最后关联977/977、零跳过、21分53秒；该次不是本owner重跑，不冒充已覆盖后续replay修复。依约不再跑全977，以本次70/32/29及Release/EF/format验证增量；前端reason256对齐由父owner负责，后端仍为原256，不扩接口。所有.NET命令已结束，无commit/push/全T11关闭操作。

复审范围：`src\Modules\Wms\Features\Transfers\TransferAccess.cs`中两处post-fence分支及`AuthorizedOriginalAsync`；`tests\Persistence\WmsMasterDataTests.TransfersReplayAuthority.cs`中16项测试、commit pause、初次原键空查询屏障及post-fence读取/权限观察器。软件边界仍以185为准，不将运输/发布worker/物理验收标为已完成。

## 浏览器集成修复与后续验证

上述RED历史保留，以下结果覆盖其未通过状态：

- 新增连续创建回归`starts a fresh single-row draft after a previous multi-row creation has been confirmed`，首次RED：新草稿单号仍为`TR-FIRST`，证实不是仅脚本假设。修复显式新草稿初始化单头、全部行和新行UUID，不触碰原Unknown确认； focused页面17项全通过，strict vue-tsc/Vite通过。
- `dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~ControlledTransferBrowserPreservesReceiptsAndExactLedger" --logger "console;verbosity=minimal"`：1通过/0失败/0跳过，1分51秒。实际临时HTTPS Kestrel/健康探测、Chromium、当前登录/具名导航、双行创建/全行审核/扫描执行/明确另GET、A真实201已提交后截断交付为503、离线阻止发送/恢复不自动重试、手动同key/body200恢复、第一单ClosedPartial及第二单逐单元Completed全部通过；375/768/1440及200%文字无横向溢出。夹具独立查真实MySQL最终原A源3/目标0、B源0/目标1、恰3个Transfer/6条entry/3条耐久Outbox、零有效预留与保护，不以页面成功提示代替效果断言。
- `npm test -- --maxWorkers=1`：最终前端50文件/1216项通过，92.41秒，无失败/跳过。此前1215为修复前历史，数字不累计。
- 踩坑回写GEN-002连续新草稿生命周期，设计185同步明确；未新增条目ID。目标整位置占位/外部预留及单源全量复验写回185，保持原HTTP键/权限/枚举不变。

最终关联MySQL复跑和独立审查仍待收口；本记录此时不宣称整包T11、实际运输、生产或其他Provider已通过。

## 独立审查修复轮

独立只读审查提出两项关联正确性问题，未以通过测试覆盖审查：

1. 同key并发请求在等待资源fence后发现赢家已提交的原fact时，早返回原回执缺少锁后当前权限复验；创建与动作路径均需按真实creator重读资源并核当前read/action权限。后端正补确定性撤权竞态RED/GREEN，不以初次授权或成功原tuple长期授予权限。
2. 客户端原因上限512与owner256不一致。三个reason动作各新增256允许/257拒绝边界，首次3个RED（257错误通过），改client校验、提示与textarea为256；设计185明确相同边界。测试union展开首次strict类型失败，改为已判别command的reason直接赋值，没有cast或降低strict。

第二项修复后`npm run build; npm test -- --maxWorkers=1`通过，最终前端50文件/1219项，80.80秒，0失败/跳过。新草稿回归同时强化为实际前单两行→下一单一条新空行，不只检查旧单号。此前1216/1215为历史结果，不重复累计；该修复不改变具名HTTP结构或状态枚举。

父会话在完整目标占位/直接EF守卫加固后，以`dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --no-build --filter "FullyQualifiedName~WmsMasterDataTests|FullyQualifiedName~WmsLedgerTests" --logger "console;verbosity=minimal"`复跑关联真实MySQL，977通过/0失败/0跳过，21分53秒。该轮覆盖54个Transfer及既有全部关联调用，替代先前961历史数量；运行期间后端仅准备锁后撤权16项测试，不并发build或测试，该轮未包含后续锁后授权修复。后者仍需独立RED/GREEN与合同/浏览器复验。

## 最终门禁与关闭范围

上述“待执行/未关闭”均为过程历史，不是当前结论。后续锁后授权修复的实际命令和16RED→16GREEN、70/32/29及Release/EF/格式证据完整记录在前文“后续review”节；没有将先前977当作包含后续修复，也没有重复累计重叠选择器。

- 独立原审查两项问题均修复后交回同一只读审查者做范围限定复审；锁后原键权限复验、256字符对齐均为ADDRESSED，连续新草稿修复无显著新破坏。范围内规格符合与代码质量均PASS，无剩余显著正确性问题；复审未重复跑测试，其判断与执行证据分别记录，不相互替代。
- 关联真实MySQL最后977/977（锁后授权修复前）；修复后全Transfer70/70，其中锁后竞态16；最终严格HTTP/配置/真实HTTPS浏览器32/32；架构29/29。全量前端50文件/1219项及strict vue-tsc/Vite通过。所有数字重叠不合计；无跳过。
- 最终Release0警告/0错误，EF无模型漂移，全部变更C#格式与diff whitespace通过；两步增量迁移Up/Down SQL已审，Down未执行，仅授权wes_test应用三新表，不改旧库存事实。
- 当前基本子片可以关闭：人工整容器单正维度多行审核/逐单元耐久到达/只补账与Unknown/ClosedPartial、共同库存写保护、默认关闭HTTP及具名客户端均具真实证据。
- 父T11仍未全部完成：多维度容器编组/拆容器及来源10移动3、branch1可信运输接线/实际T15、耐久Transfer Outbox发布worker/Broker仍为软件依赖。其他Provider、PDA真机、生产/现场安全未验证，不将其写为通过。既有开关默认false不变，关闭入口不解除已有保护。
- 文档同步设计/合同/追踪/README/readiness/TodoList与父子计划；知识库沿用GEN-002/004补录本轮已复现经验，未新增ID。无其他代替用户的新业务裁决。
- 最终11份已改/新增Markdown的882个本地相对链接检查，0新增断链；README中6个既有Seven/外部设计/Route目标在此worktree不可用，均核对HEAD基线已存在，不伪装本轮成功。`git diff --check`通过。
