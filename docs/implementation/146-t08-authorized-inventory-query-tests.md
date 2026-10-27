# T08 当前授权库存读取测试

- 状态：已验收（本片owner/API；客户端/固定核对快照与整包未完成）。
- 设计：[145](145-t08-authorized-inventory-query.md)。

| 子用例 | 断言 |
|---|---|
| WMS-INVENTORY-01 | 固定维度/版本/精确六位quantity，重叠Reserved/InTransit/Unknown并集、零余量不计，无写入 |
| WMS-INVENTORY-02 | 精确warehouse/location/item/container/batch，SQL过滤后分页，tenant隔离 |
| WMS-INVENTORY-03 | 完整All字段、可信主体、授权后/SQL后取消、clean/ambient、provider安全错误 |
| WMS-INVENTORY-04 | RepeatableRead两查询中间真实并发保护变动不混合，损坏保护不clamp |
| WMS-INVENTORY-05 | HTTP默认未装配/HTTPS/401、参数400、当前撤权403、精确页/数量字符串 |

## 执行证据

环境为既有.NET10.0.103、EF/MySQL10.0.7、MySql.Data9.7.0与批准wes_test/MySQL8.0.12，凭据沿已有测试装配内存来源，不入库/日志。无依赖、迁移或服务安装。新`WmsLedgerTests.Reads.cs`26项真实owner及`IdentityHttpTests.WmsInventory.cs`18项真实HTTP。

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WmsLedgerTests"
dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WmsInventory"
dotnet test tests\Architecture\Wes.Architecture.Tests.csproj -c Release --no-restore
dotnet build WES.slnx -c Release --no-restore
dotnet ef migrations has-pending-model-changes --project src\Modules\Wms\Wes.Wms.csproj --context WmsDbContext --configuration Release --no-build
```

owner RED为不存在Inventory命名空间/读取类；首22项实现后通过。后追加4项损坏负量/未知kind、满decimal范围、SQL完成取消；首编译误将取消拦截器嵌进authority，已移至同级修正。HTTP首RED先暴露fixture错误：不能一次收货整个库存decimal最大值，既有单笔上限是999999999.999999；改合法单笔上限而不改业务规则，再实际观察GET404而预期200。接线后通过。

| 最终集合 | 实际结果 |
|---|---|
| WMS ledger/reservation原48+本片26 | 74 passed，0 failed，0 skipped |
| 真实库存HTTP | 18 passed，0 failed，0 skipped |
| Architecture | 29 passed，0 failed，0 skipped |
| Release solution | 0 warnings，0 errors |
| WMS EF模型 | 无pending changes |

WMS-INVENTORY-01/02：10.125001账面、同份额3.000001三种重叠原因均显3.000001，Protected仍3.000001、Available7.125000；已释放2的零墓碑不计，不写账/不track。27条精确Batch筛选25+2，另有小写batch及无批次，不混合；warehouse/location/item/container组合过滤后分页。真实foreign tenant数据不泄露。满22整数+6小数的数量通过仅隔离测试rawSQL存储探针验证不丢精度；不是生产记账入口或合法posting证明。

WMS-INVENTORY-03/04：固定15字段逐一缺失拒绝；HTTP逐一验证缺失字段不能借Own补齐，Own/部门均403。真实role禁用/field撤销/membership删除、logout，原主体下一次请求拒绝；跨tenant/空actor/session、tracked/current/ambient拒绝，授权后和SQL后取消不交付。provider错误转安全异常，不含sensitive sentinel。真实第二owner在余额读取后、保护查询前提交7预留：旧页10/0/10及旧version一致，下次新页10/7/3及新version，不抛混合水位损坏也不读新份额配旧余额。直接SQL仅在隔离fixture构造超过账面、负份额、未知kind反例，均明确异常，不clamp/转0。

WMS-INVENTORY-05：默认404、认证401、HTTP400、logout后401，非法paging/UUID/空UUID/batch400；成功精确3字段页/15字段行、no-store、固定六位字符串，不接受客户端tenant/assertions；POST405。未跑前端/真实浏览器（本片无客户端）、完整.NET/Route/主数据全回归，不叠加历史结果。TRX在会话`files\inventory-query-validation\{owner,http-red,ledger,http,final-http,architecture}`；T04–T08整体仍未关闭。
