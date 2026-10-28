# T11 盲盘及局部锁测试记录

- 状态：本盘点基本软件子片已实现验证并关闭；最终结果以§6为准，历史失败/待取得记录保留。T11父任务与TransferOrder未关闭。
- Owner：WMS、Host传输适配、Ops Web具名页面。
- 设计：[183](183-t11-blind-count-and-local-lock.md)；[实施计划](../superpowers/plans/2026-10-28-blind-count.md)。
- 范围：单位置盲盘/局部锁/非本人差异复核；隔离wes_test和本地HTTPS，不是现场/生产验收。TransferOrder另续，不将主体缺失列为外部验证关闭。

## 1. 验收目录

| ID | 输入 / 故障 | 必须结果 |
|---|---|---|
| TC-T11-COUNT-01 | 初始10，实盘12/8/10，重复确认 | +2/-2/零差；两个非零流水各一次，零不造流水；不能直接覆盖余额 |
| TC-T11-COUNT-02 | 盲盘GET、独立review、非零自批/缺权限 | 盲盘无book/delta/balance/version私有量；review独立字段权限；非零最终计数人不能批准 |
| TC-T11-COUNT-03 | 多行未数齐、其中一行故障或撤权 | 不自动调账，全行ledger/事实/父Completed/解锁同事务回滚 |
| TC-T11-COUNT-04 | 有效局部锁、legacy源/目标记账、新维度、预留、收发及活动状态变化并发 | 新业务不能穿透；共享fence使start和受保护动作串行；已成功原tuple仍精确重放而非二次执行 |
| TC-T11-COUNT-05 | 1799.999/1800秒、人工解锁、旧版本adjust、重启重盘 | 仅提醒不自动释放；计数失效；新快照/新行/必须重新计数 |
| TC-T11-COUNT-06 | tenant/真实creator/部门/两时点撤权、原key换tuple、并发start/record/adjust/invalidate | 当前完整authority、无越租户，单有效锁及CAS，原回执永久保存且不泄漏私有量 |
| TC-T11-COUNT-07 | 严格HTTP/默认关闭、真实双账号浏览器、201落库后503、离线/超时/迟到、复核版本变化 | 全输入冻结/手工原key恢复，无换key/自动排队，另读当前；旧复核不能调账 |

## 2. 前端红绿与当前证据

1. 新契约RED：`counts.ts`不存在，Vite导入失败；补客户端后30项通过。新页面RED：缺`CountConfirmation.vue`，导入失败，未记录为业务通过。
2. 具名页面/确认实现后契约及页面42项通过；严格构建发现未知snapshot字段未收窄、QueryState缺必填label，修复为显式string/null guard并使用组件真实props/slots，没有放宽类型。
3. 补充不同应盘行清扫码输入、迟到review/主体撤权清私有数据、review版本不匹配阻断与重启不携带旧行后，契约/页面46项通过。独立review必须显式GET，未盘不是0；最终计数人非零自批隐藏。
4. 完整前端单worker回归 **48文件/1158项通过、0失败/跳过**（126.92秒），strict TypeScript与Vite生产构建通过，包含新的Playwright脚本静态检查。历史T10的1107与本轮重叠不合计。证据保存在会话`basic-delivery-validation\count-client-tests-serial.log`、`count-client-build.log`，不提交工具日志。
5. 直接运行`playwright --list`被现有受控fixture配置正确拒绝（未指定受控目标）；没有放宽guard/伪造fixture环境绕过，不将discover预检当浏览器通过。真实浏览器须由隔离IdentityHttpTests fixture启动，当前待取得。

```powershell
Set-Location clients\ops-web
npm test -- src\features\wms\counts.spec.ts src\features\wms\CountsPage.spec.ts
npm test -- --maxWorkers=1
npm run build
```

## 3. 尚未取得的最终门禁

后端新owner/真实MySQL局部锁与全行adjust、严格HTTP/配置、实际双账号HTTPS浏览器、相关旧ledger/reservation/inboundtransport/outbound/库位回归、独立审查、架构/Release/EF无漂移/增量SQL/格式及commit/push尚未收口。取得后逐项追加真实证据；当前前端通过不代表183全部实现、T11或项目已完成。

## 4. 续接集成验证

续接时后端代码已持久化，但前代理运行上下文和执行报告不可用，未沿用未取得的成功声明。重新运行真实MySQL盘点精选 **13/13通过**（45秒，`count-owner-current.trx`）；严格HTTP、配置和真实HTTPS浏览器联合 **130/130通过**（3分4秒，`count-http-browser-current.trx`）。选择器包括既有其他Counts命名用例，不把130全部宣称新增盘点测试。

集成中发现完成/人工失效后后端释放锁负责人为null，但前端严格parser错误要求所有非Draft均有负责人。追加两个真实RED（Completed/Invalidated）后，只对Counting/Ready要求有效负责人，终态必须null，保留原snapshotAt；更新规范和页面fixture。契约/页面 **48/48通过**，严格构建通过。最后全前端 **48文件/1160项通过、0失败/跳过**（66.47秒，`count-client-tests-final.log`），不累计早先1158。

真实HTTPS Chromium通过具名UI建立一个位置的全部3行（每行合法入库10），实盘12/8/10；盲盘响应逐行精确字段白名单不含私有账面，原计数人显式review后不能自批，第二授权账号复核+2/-2/0，真实adjustment201提交后交付截断503，离线禁止/上线不自动重试，原键/原body恢复200且decision1，当前GET Completed。fixture最终核对8/10/12及5个posting/5个entry（合法入库3、非零调整2）、无active锁、原始count操作事实6；零差未造零流水。375/768/1440及200%文字无横溢、键盘确认、无业务写缓存通过。非完整PDA/现场矩阵。

完整相关持久回归和独立审查仍在进行；上节“尚未取得”作为当时记录保留，本节只覆盖已重新取得的结果。没有production连接或执行Down。

完整相关持久回归首次 **922项中904通过/18失败**（15分53秒），证据`count-release-persistence.trx`。17个失败集中于新增PostStock源/目标联合查询触发Provider命令构建NullReferenceException，影响原Transfer及最终入库运输；另1个同key并发零维度登记返回Conflict而非Replayed。不是可忽略的环境超时，不准提交或放宽测试。架构门禁首轮 **28通过/1失败**，Host普通构建依赖隐式using掩盖新增Counts端点缺少显式Routing命名空间，Roslyn独立编译如实拒绝。后续修复/复测另记。

## 5. 独立审查与关联修复

独立审查定位新增primitive Guid集合Contains触发既有MySQL Provider限制，不能归咎DbContext并发或改测试。PostStock改为两个nullable源/目标的显式等式；资源位/容器复用从既有Outbound提取的WMS-local等式表达式（空集合恒false）。维度登记等待资源fence后必须重新查原operation，否则自身赢家余额被当作其他冲突；拆分fence获取与新写保护检查，先精确原tuple恢复、再评估新写count保护。原失败中的Transfer、入库最终到达、same/different-key维度并发及count保护精选 **6/6通过**。

审查另确认三项关联问题，均先RED后修：

1. 已Applied可信运输完成在后来盘点锁下重放错误Blocked；新增真实MySQL用例确认RED1，再在body/intent/adapter验证后、任何新资源冲突前恢复不可变原posting。
2. 盘点实际数量错误复用9位移动上限；两个大绝对库存数RED，改为22位InventoryQuantity，差异仍受原移动上限。
3. 新调整kind3/4使既有ledger客户端整页拒绝；两个实际调整响应形状/筛选/标签RED，补齐类型、符号守卫、筛选及具名标签。

Host新增显式Routing import，不放松架构编译；初次格式门禁发现新增C#缩进/初始化与EF迁移BOM，使用既有格式器修正，不改业务规则。前端相关133项通过及strict build通过，最后完整 **48文件/1164项通过、0失败/跳过**（67秒，`count-client-tests-reviewed.log`）。Provider完整修复回归、独立复审及最终发布门禁取得后追加，不用这些选择器代签全范围。

修复后的全部相关Transfer/ledger/入库运输及盘点锁后原运输事件恢复联合 **327/327通过、0失败/跳过**（4分45秒，`count-review-repair-green.trx`）；原17个Provider失败路径及同key维度并发均在此选择器内。格式修正后verify通过，架构重新完整 **29/29通过**（`count-architecture-final.trx`）。同一审查者仅复核6项原问题及修复引入回归，结论没有显著剩余问题，6项在代码审查层关闭；最终全范围Provider/HTTP仍单独门禁，不以审查替代执行证据。

## 6. 最终发布门禁与关闭范围

在全部关联修复完成后，对相同工作树重新执行，不复用初次失败或代理未取得的报告：

| 门禁 | 最终结果 | 会话证据 / 范围 |
|---|---|---|
| 真实MySQL关联回归 | **923/923通过、0失败/跳过**，19分23秒 | `count-release-persistence-final.trx`；全部WmsMasterDataTests、WmsLedgerTests及Reservation选择器 |
| HTTP/配置/真实HTTPS浏览器联合 | **165/165通过、0失败/跳过**，10分52秒 | `count-release-http-browser-final.trx`；Counts、WmsMasterDataSettingsTests、Outbound、InboundExecution、InboundTransport选择器，不全称新增盘点用例 |
| 前端完整单worker回归 | **48文件/1164项通过、0失败/跳过**，75.06秒 | `count-client-tests-release.log`；另执行`npm run build`，strict TypeScript/Vite生产构建通过 |
| 架构 | **29/29通过、0失败/跳过** | `count-architecture-release.trx`；包含Host独立Roslyn编译 |
| 完整Release解决方案 | **0警告/0错误** | `dotnet build WES.slnx --no-restore -c Release` |
| EF模型 / 增量SQL | 无待迁移模型变化；Up/Down生成并逐项审阅 | `count-migration-up.sql` / `count-migration-down.sql`；仅两新表、tenant唯一/FK/索引及`__WesWmsMigrations`历史，无旧账UPDATE/回填；Down只审不执行 |
| 格式 / 差异 | changed C# whitespace verify及`git diff --check`通过 | 使用既有`dotnet format whitespace`，未新增工具或依赖 |
| 文档链接 | 11份变更文档无新增断链 | 基线README的6个外部Seven.Master/架构/Route目标及`feature-switches.md`的1个Seven.Master目标在此worktree不可用；7处链接均未改动，不把基线缺目标报为本片全链接通过 |
| 独立审查 | 原6项代码层关闭，复审无显著剩余问题 | Provider查询/维度并发、Host显式using、已Applied回执恢复、绝对实盘数量及ledger新类型均有修复和相关验证 |

实际执行选择器如下；结果相互重叠，不累计为新增覆盖数：

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~WmsMasterDataTests|FullyQualifiedName~WmsLedgerTests|FullyQualifiedName~Reservation"
dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~Counts|FullyQualifiedName~WmsMasterDataSettingsTests|FullyQualifiedName~Outbound|FullyQualifiedName~InboundExecution|FullyQualifiedName~InboundTransport"
dotnet test tests\Architecture\Wes.Architecture.Tests.csproj --no-restore --no-build -c Release
```

§1七项验收目录均由上述owner/HTTP/客户端/真实浏览器相关用例覆盖；§4的18个持久失败和1个架构失败已修复，最终选择器全部通过，不放宽断言或制造fallback。最初后端代理RED过程记录不可恢复，不补造该过程证据；本会话重新取得真实执行结果，已记载的后续RED/GREEN单独保留。

本位置级盘点/锁子片可关闭；下一主体为多行TransferOrder的全行原子审核、逐实物单元记账、已到待同步/Unknown保护及ClosedPartial，不将缺失主体记为外部验证关闭。多库位大计划调度、提醒渠道升级和PDA专页见[TodoList](TodoList.md)。实际T15/Pack、PDA/扫码枪真机、生产/现场与其他Provider未运行，不声称这些通过。迁移仅在隔离wes_test由fixture应用，未执行Down、生产迁移或设备命令；开关仍默认false，关闭HTTP不解除持久锁。
