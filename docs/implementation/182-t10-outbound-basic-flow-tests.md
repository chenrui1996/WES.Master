# T10 出库基本链测试记录

- 状态：本基本软件子片已实现并验证；5项独立审查问题已修复并复审关闭，不代表完整T10或生产验收。
- Owner：WMS、Host传输适配、Ops Web具名页面。
- 设计：[181](181-t10-outbound-basic-flow.md)；[实施计划](../superpowers/plans/2026-10-28-outbound-basic-flow.md)。
- 适用：隔离真实MySQL wes_test与本地HTTPS Chromium，非生产/设备验收。

## 1. 可观测验收

| ID | 输入 / 故障 | 必须结果 |
|---|---|---|
| TC-T10-BASIC-01 | 多行其中一行不足；Qty10两单各预留7并发；重复审核 | 单次全行原子；最多一成功；失败无部分有效reservation/fact，原key无新增 |
| TC-T10-BASIC-02 | 计划7、拣5短2、非原人复核未执行2、合法直发5、关闭余2 | 10/7→10/7→10/5→5/0；计划7不改，部分发后保持Executing，显式关闭才ClosedPartial |
| TC-T10-BASIC-03 | 精度/科学记数/跨tenant/原key换body/版本变化 | 不舍入、不跨owner，无新副作用；原tuple返回原版本并重新核当前完整read |
| TC-T10-BASIC-04 | 多creator/部门逐字段；提交前后撤权；schema/仓位停用 | 当前完整交集，不把操作者当资源creator；保存后撤权整笔回滚 |
| TC-T10-BASIC-05 | Direct与RequiresTransport；已拣/在途/Unknown；原人短缺释放 | 无运输才直发；不把已执行当未执行取消；不自己复核；无假运输完成 |
| TC-T10-BASIC-06 | 多行并发、provider故障/取消、篡改历史、原预留消费 | 进度/fact/reservation/ledger全回滚或全提交；永久幂等不靠缓存；旧流水守卫保留 |
| TC-T10-BASIC-07 | 严格HTTP、默认关闭、页面预览、503响应丢、10秒、离线/主体切换 | 只发送冻结原路径/key/body，Unknown不自动换键/排队；成功后显式当前GET，不从写回执假造当前态 |

## 2. 前端红绿与基本证据

1. 最窄契约测试先运行时缺少`outbound.ts`，Vite导入失败RED；没有生成虚构TRX。
2. 契约实现后25项通过；首次严格构建发现模板直接引用未暴露的`crypto`，改为具名addDraftLine后通过。
3. 组合页面首轮83项中7失败，均为缺权测试错误查找本来必须隐藏的确认按钮；改为断言不存在且零请求，不放宽权限。
4. 新增释放/直发/关闭与取消用例后，当前前端选择器87项通过；严格TypeScript及Vite生产构建通过。
5. 补充活动行守恒、迟到查询/路由切换隔离与精确未发数量显示，当前全前端46文件/1106项通过（历史1051与当前重叠，不累计）。其中新增55项：新出库契约26、页面25、导航2、路由2。合法权限名按既有dot/alnum约束使用`wms.outbound.release`与`wms.outbound.close`，HTTP动作路径仍含连字符，不改语义；修正后窄选90及严格构建通过。

```powershell
Set-Location clients\ops-web
npm test -- src/features/wms/outbound.spec.ts src/features/wms/OutboundPage.spec.ts src/features/navigation.spec.ts src/router.spec.ts
npm test
npm run build
```

后端Provider、真实HTTPS浏览器、独立审查及最终Release/模型/格式门禁取得结果后在本记录追加；尚未取得结果前本片不关闭。

## 2.1 后端首轮证据

后端实施者已取得隔离MySQL出库9项及预留/账本/入库运输精选回归11项通过，Host Release0警告/0错误。覆盖基本数量链、全行原子、两单预留竞争、精度/租户/永久原tuple、保存故障与撤权回滚、在途/Unknown阻断。这是首轮精选，不当作完整旧账本/保护回归或HTTP验收。

新迁移`20261028070318_WmsOutboundBasicFlow`仅新增出库单及不可变操作事实两表、tenant唯一/FK/索引；已在授权wes_test测试fixture应用。增量Up/Down仅审，Down未执行，无生产连接。初次Modules编译因父会话浏览器fixture缺`Wes.Platform.Features.Identity` using失败，已补；后端/Host原构建不受此测试文件编译错误影响，不能将当时未运行的HTTP/配置浏览器记通过。

## 2.2 真实HTTPS组合链

第一次浏览器/配置门禁85配置项通过，真实浏览器在清理时因新增出库父仓FK失败；未将该失败算通过。按随机fixture tenant补充出库facts→documents定向清理，不关闭FK、不删共享库其他数据。

修后单独真实HTTPS Chromium **1/1通过**，`outbound-browser-green.trx`。真实JWT/当前权限、HTTPS Kestrel与MySQL下，通过具名导航完成草稿7→审核预留7→实拣5/短2→另一授权账号复核释放2→直发5→关闭未发2。浏览器中真实Shipment201落库后截断交付为503，页面保留Unknown，离线禁止/上线不自动重试，手工同key/body恢复200且decision1；原计划7保持、当前GET为Executing且已发5/预留0，关闭后ClosedPartial。fixture最后断言Qty5、两次posting/entry（入库10、发运5）、预留余0，未伪造运输事实。

375/768/1440宽度及200%文字无横向溢出，键盘确认可用，双账号独立browser context；浏览器无pageerror，业务身份/操作/访问令牌不写localStorage/sessionStorage。本变体不是全终端/PDA或完整PAGE矩阵。

## 2.3 独立审查与关闭条件

独立审查提出5项，本片保持未关闭：其他执行Unknown/InTransit未阻断剩余量分配；前端误要求发运量等于全部预留；8192字符事实上限不支持声明的100行；全短缺且复核释放后无法取消；header/facts分开读取可能把正常并发当历史损坏。

前端问题已先红后绿修复：新增“审核7、实拣5/短0、直接发5且保留未拣2待关闭”组合测试，原实现1项RED；改为发运量恰已拣、预留足够且已报告短缺全部复核，不要求等于所有预留，出库契约/页面52项GREEN及严格构建通过。其余4项后端修复/证据取得后补记，先前基本浏览器通过不能覆盖这些边界，不提前提交。

修后全前端默认并行复测1107项，1104通过、3项既有用户/会话分页及角色候选用例触发原5秒超时；没有业务断言失败。不能将此次标为全绿，也未修改超时/测试断言。相同代码使用`npm test -- --maxWorkers=1`整套复测 **46文件/1107项通过、0失败/跳过**（110秒），证据`outbound-client-tests-serial.log`；该结果支持共享环境并行争用解释，不删除先前失败记录。

后端4项追加5例真实RED/GREEN：来源容器/库位其他Unknown与InTransit正保护（2例）、合法100行创建与全行原子审核、全短缺由非原人复核释放后的安全取消、header/facts查询之间真实并发审核。统一保留输入上限65536字符，原longtext不需迁移；既有事务外读取使用同一repeatable-read快照，真实历史损坏仍显式拒绝，不catch成成功。100行用例通过真实合法登记/入库接出来源，不绕过记账守卫。修后精选持久回归21通过，全部22出库HTTP+85配置107通过，修后相关3HTTP+85配置88通过；这些重复选择器不累计。

独立复审仅核原5项及修复引入问题，结论无显著剩余问题；5项审查发现已关闭。最后完整相关Provider/HTTPS与Release/模型/格式门禁另行执行，不用复审代替测试。新增pending源/目标元组逐动作全矩阵及HTTP拦截器保存后撤权仍未新增（持久层撤权已覆盖），列为覆盖深化而非假通过。

最终独立真实MySQL相关回归 **392/392通过、0失败/跳过**（17分53秒），包括全部出库/100行、原账本、预留、入库运输和库位状态相关用例；证据`outbound-release-persistence.trx`。早先精选9/11/21与本轮重叠，不累计为新覆盖。

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~Outbound|FullyQualifiedName~InboundTransport|FullyQualifiedName~WmsLedgerTests|FullyQualifiedName~LocationState|FullyQualifiedName~Reservation"
```

## 2.4 最终发布门禁

- 相关真实MySQL **392/392**，含全部14个出库用例（含100行）及耦合账本/预留/库位/入库运输回归，0失败/跳过。
- 最终HTTP/配置/原入库执行与真实双账号HTTPS浏览器 **120/120**，0失败/跳过，`outbound-release-http-browser.trx`；重新验证修复后的真实主链与原key响应丢恢复。
- 架构 **29/29**，`outbound-release-architecture.trx`；完整solution Release **0警告/0错误**。
- EF无模型漂移；增量`outbound-up.sql`/`outbound-down.sql`复核仅新建/移除两张出库表及其索引/FK/owner迁移记录，不改旧账。Down仅审未执行。
- 首次whitespace门禁发现浏览器fixture对象初始化/测试缩进和EF生成迁移BOM；使用既有`dotnet format whitespace`仅格式化变更C#后verify通过，完整solution再次Release0/0。没有行为改动，不重算测试覆盖。
- 前端最终46文件/1107项单worker通过，严格TypeScript及生产构建通过，最终证据`outbound-client-tests-serial.log`/`outbound-client-build.log`。

测试/构建/SQL均仅本地授权wes_test及HTTPS夹具，无生产数据库、设备控制或现场签字。保存日志/TRX/临时SQL在会话验证目录，不进入仓库。上述选择器互有重叠，不相加宣称新增测试。

## 3. 软件范围与未运行项

明确不宣称：效期政策/FEFO、自动FIFO来源分配、同维度拆行、多次部分发运、出库真实运输/T15/Pack、设备完成取消竞态、完整父DTC所有变体、SQL Server/PostgreSQL、生产连接或现场安全验收。上述前五项是后续软件能力，不能伪装成外部验证关闭。

页面使用完整卡片而非密集表，列偏好不适用；来源UUID及精度/基础单位/计划/实拣/短缺/已释放/已发固定显示，不跨单位求和。当前来源目录候选/自动分配交互是显式后置，不替代服务端合法资源复验。
