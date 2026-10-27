# T08 仓库创建 HTTP 测试

- 状态：已验收（本片适用用例，非完整T08/父DTC）。
- 设计：[本片设计](137-t08-authorized-warehouse-creation-http.md)。

| 编号 | 步骤 | 预期 |
|---|---|---|
| WMS-CREATE-HTTP-01 | 默认关闭、只读装配、显式写装配、非法配置 | 不自动开放；新配置非法/无依赖启动拒绝 |
| WMS-CREATE-HTTP-02 | 当前Own Code/Name写授权创建、同key重放、并发 | 201/200、相同身份/版本、单资源/事实/事件 |
| WMS-CREATE-HTTP-03 | 缺字段、撤角色/会话、同key改body、代码冲突 | 403/401/409，无越权身份回显或二效 |
| WMS-CREATE-HTTP-04 | 匿名/HTTP/客户端标识/key/JSON边界、越权输入 | 明确拒绝；body不能指定身份/启用；无业务增量 |
| WMS-CREATE-HTTP-05 | 原WMS read与浏览器回归、架构 | 不改变只读HTTP/权限/生命周期及owner边界 |

## 执行与证据

沿既有.NET10.0.103、EF/MySQL10.0.7、MySql.Data9.7.0、真实MySQL8.0.12/wes_test授权测试装配，凭据不入配置/日志。无新增依赖、迁移或安装。新增`IdentityHttpTests.WmsWarehouseCreation.cs`38项真实HTTP/owner联查、`WmsMasterDataSettingsTests`7项配置。

```powershell
dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WmsWarehouseCreation|FullyQualifiedName~WmsMasterDataSettingsTests"
dotnet build WES.slnx -c Release --no-restore
dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~Wms|FullyQualifiedName~RealBrowserReadsWarehouseAndLocationAndClearsDetailsAfterCurrentRevocation"
dotnet test tests\Architecture\Wes.Architecture.Tests.csproj -c Release --no-build --no-restore
```

RED为未定义WarehouseCreationEnabled编译失败；实现后首轮44项通过，再增加长度精确阈值、重复header、URL/header伪身份与无读授权、登出后重放共7项。最终：

| 集合 | 实际结果 |
|---|---|
| WMS领域、配置、所有相关HTTP、既有真实HTTPS Chromium | 145 passed，0 failed，0 skipped |
| Architecture | 29 passed，0 failed，0 skipped |
| Release solution | 0 warnings，0 errors |

WMS-CREATE-HTTP-01..04实际通过：默认405、显式201及Location/no-store/三字段、200重放同ID/version、409不泄露身份；真实并发同键/同代码单效果；每个write字段、当前角色/会话撤销、严格JSON/超长/客户端/key/HTTPS拒绝；当前Own创建和真实actor/tenant事实；伪URL/header不改变tenant、write不赠read。每次拒绝owner联查原资源/事实/事件数量不增。没有位置创建或启停端点。

WMS-CREATE-HTTP-05仅回归原浏览器目录/详情/撤权，未伪称真实浏览器提交新仓；后者随表单独立实现。没有完整.NET/前端回归，没有本片Host真实断库503/500故障窗口验收（writer安全异常已有先前owner测试，但不能替代本片HTTP窗口）；没有WMS publisher/Inbox/Broker/真实Pack/生产或物理控制验收。

TRX保存在会话`files\wms-create-http-validation\{first,http-browser,architecture}`；最终174项相关.NET通过不包含上一片141 owner或前端427历史结果。
