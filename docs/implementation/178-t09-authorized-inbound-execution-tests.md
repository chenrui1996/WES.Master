# T09 当前受权审核、收货与容器保护测试记录

- 状态：本软件子片最终门禁通过；不据此宣告整个T09、最终运输或现场验收完成。
- 设计：[实施记录](177-t09-authorized-inbound-execution.md)，TC-T09-EXEC-01..07。
- 环境：.NET10.0.103、MySql.EntityFrameworkCore10.0.7/MySql.Data9.7.0、真实MySQL8.0.12隔离wes_test；Vue3/Vitest/严格TypeScript及HTTPS Chromium。测试schema为明确测试装配，不是实际Pack或物理到达证据；SQL Server/PostgreSQL延后。

## 已运行的红绿过程

1. 两个初始owner正向用例因缺InboundExecutionAccess编译RED；实现后2项通过。公共枚举XML注释与xUnit2031编译问题随后修正，首轮owner53项通过。
2. 严格HTTP/配置27项通过；单据state沿原数字枚举合同断言，未更改原GET合同。
3. 具名执行组件首轮缺文件RED；实际父页和Unknown恢复实现后通过。原只读notice断言同步新独立入口说明，仍断言无权限用户不出现写按钮。严格构建发现可清空快照未声明undefined，修正为明确联合类型。
4. 扩大owner首轮117项中112通过、5失败：4项测试在内部primitive创建后未释放tracked状态，被独立owner守卫正确拒绝；测试清tracker后再进入公开owner。异址测试在当前provider创建nullable GUID集合查询命令时失败，改为两个明确ID相等谓词。这5项最窄复测通过；随后本片117及旧账本/资源/库位状态相关529项通过（7分44秒）。
5. 新增真实HTTPS Chromium1项通过。服务端已经201、客户端收到503时保持原tuple；离线/上线不自动重试，原键恢复200 Replayed，真实库中无第二账本。源位实收5、目标未到达、Executing/transportId=null，当前角色撤Lines后原token GET403且页面清旧。
6. 最终前端44文件/1051项通过，`npm run build`严格类型与生产构建通过。相较前片1016新增35项，不把重复回归合计为新增。
7. 追加独立owner/安全provider/未知schema/实际停用/不同合法余额并发8项。最初provider注入用随机不存在单据，正确提前NotFound而未到目标查询；改用真实合法单据使故障确实注入父仓provenance读取，最终8项通过。历史125项由529内117及追加8证明，由下面最终127项覆盖，不重复合计。
8. 最后将全部预览字段改为具名中文标签，35项组件复测与严格生产构建通过；实际浏览器以该次dist重验。变更C# whitespace格式及verify-no-changes通过。
9. 最终复核补确定性同键异址并发：等待者首查确无posting，赢家实际保存并提交pending保护后再恢复等待者，原实现稳定RED（预期Replayed、实际Blocked）。修正为SourceRejected与Replayed均在原事务释放后重新核完整当前权限并查永久原tuple；无原事实才返回实际拒绝，不重写schema/账本。补真实另租户隔离；最终执行owner **127项通过**（4分44秒）。
10. 较宽HTTP/配置、旧入库与六套真实HTTPS浏览器 **192项通过**（9分57秒）。最后窄门禁28项首跑27通过、1个浏览器失败：断言默认5秒时仍在真实收货请求中，早于客户端10秒Unknown边界。改为显式等待已注入的503响应（route内仍严格断言服务端201）后验证Unknown，不放宽请求/账本/身份断言或生产超时；该浏览器最窄复测 **1项通过**（26秒）。保留首跑失败证据，不将其记成28项整批通过。
11. 最终架构 **29项通过**，全solution Release **0警告/0错误**；EF `has-pending-model-changes`无漂移；增量SQL复审只新增`wms_inbound_execution_operations`一表、两个FK/两个索引及迁移历史，不修改旧表。变更C# whitespace `--verify-no-changes`通过。仅隔离测试库执行迁移，不对生产或其他Provider作成功声明。

## 执行命令与结果身份

命令均从仓库根执行，前端命令在`clients\ops-web`执行；已有依赖未重新安装。

```powershell
dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~InboundExecution"
npm test
npm run build
```

真实owner相关回归用一个runner合并`WmsLedgerTests`与`WmsMasterDataTests.InboundExecution*.cs`、`Stock*.cs`、`LocationState.cs`中方法选择器；追加守卫先跑对应8项，修复同键并发后再跑本片全部127项，不重复整个无关平台套件。较宽HTTP/配置/旧入库与六套真实HTTPS浏览器合并选择`FullyQualifiedName~Inbound|FullyQualifiedName~WmsMasterDataSettingsTests|FullyQualifiedName~RealBrowserReadsWarehouseAndLocation`。最后窄选择`FullyQualifiedName~InboundExecution`取得27通过/1失败后，仅修浏览器同步并复跑`FullyQualifiedName~InboundExecutionRealBrowser`取得1通过。

会话验证目录中的`inbound-execution-owner-final.trx`为127通过、`inbound-execution-owner-ledger-resources-final.trx`为529通过、`inbound-execution-owner-guard-final.trx`为8通过；`inbound-execution-http-browser-regression-final.trx`为192通过、`inbound-execution-http-browser-final.trx`保留27通过/1失败、`inbound-execution-browser-final.trx`为修正同步后1通过、`inbound-execution-architecture-final.trx`为29通过，`inbound-execution-migration.sql`为已审增量SQL。`inbound-execution-concurrent-pending-red.trx`为并发RED；5项修复与首轮1项浏览器证据均与最终选择器重叠，不重复累计。报告不含连接凭据。

## 用例覆盖

| Case | 自动化证据 |
|---|---|
| EXEC-01 | 合法审核、同址足量与2+3部分/余量、异址真实收货待运输、后续状态改变仍恢复原版本 |
| EXEC-02 | 审核29与收货50个当前字段逐一缺失时新操作/原恢复均拒绝；真实不同单据/仓位/单位/物料/容器creator；保存后撤权全回滚 |
| EXEC-03 | 未登记物料/单位、精度或批次政策不符、旧余额无运营登记、源位审批后停用、schema拒绝/不可用/未知决策 |
| EXEC-04 | 同容器其他位置、他容器正库存、剩余预留、零余额不误占位；异址容器与源/目标持续保护、禁止禁用 |
| EXEC-05 | 同键同body并发、不同键同余额、不同合法余额同父单、原body/版本变化冲突、不可改删/收编旧事实 |
| EXEC-06 | 实际Save后故障/取消/撤权、provider安全异常与独立owner；客户端10秒边界、断线/迟到、非法身份/版本回执、Unknown后403不换键 |
| EXEC-07 | 独立默认关闭/非法配置、严格JSON/当前角色；实际父子组合不丢Unknown、完整预览、显式另读；真实HTTPS与窄屏/200%/键盘/无业务本地缓存 |

## 尚未覆盖及外部边界

本片不含运输请求/运输与Leg owner、真实设备反馈、最终到达持久移账或现场安全验收。真实Pack/schema、生产迁移和外部适配证据未运行；不是测试成功或外部放行。最终运输必须接入耐久事件及唯一移账，不能凭本片的Executing或领域状态释放待运输保护。
