# T05 WMS 创建事件诊断测试

- 状态：已验收（本片用例；跨owner客户端/完整父DTC仍未完成）。
- 设计：[本片设计](141-t05-wms-creation-event-diagnostics.md)。

| 编号 | 步骤 | 预期 |
|---|---|---|
| WMS-EVENT-READ-01 | 新仓/位事件、Own/部门/All、逐字段交集、真实跨tenant | 精确九字段、完整当前creator交集SQL前过滤、不显示payload |
| WMS-EVENT-READ-02 | 顺序/offset/limit、无creator/未知creator/缺grant | 稳定有界实时页；授权空与403分离 |
| WMS-EVENT-READ-03 | 当前角色/字段/会话/成员/部门撤销 | 每次复验，不能缓存旧授权范围 |
| WMS-EVENT-READ-04 | clean/ambient/取消/Provider故障 | 明确拒绝或安全异常，不交付迟到成功/空fallback |
| WMS-EVENT-READ-05 | HTTP默认未装配、读写权分离、HTTPS/401、参数、行形状 | 404/403/400/401及精确200/no-store，仓库写开关不是读许可 |

## 执行与结果

沿既有.NET10.0.103、EF/MySQL10.0.7、MySql.Data9.7.0与批准MySQL8.0.12/wes_test真实测试装配，凭据不入仓库/日志。无新增依赖/迁移/安装。新增`WmsMasterDataTests.EventReads.cs`38项owner测试及`IdentityHttpTests.WmsCreationEvents.cs`13项HTTP。

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CreationEventReads"
dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WmsCreationEventsExposeOnlyCurrentNineFieldMetadata"
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WmsMasterDataTests"
dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Wms|FullyQualifiedName~RealBrowserReadsWarehouseAndLocationAndClearsDetailsAfterCurrentRevocation"
dotnet test tests\Architecture\Wes.Architecture.Tests.csproj -c Release --no-restore
dotnet build WES.slnx -c Release --no-restore
dotnet ef migrations has-pending-model-changes --project src\Modules\Wms\Wes.Wms.csproj --context WmsDbContext --configuration Release --no-build
```

owner RED为不存在QueryCreationEventsAsync；首实现缺既有WmsDatabaseFailures所在namespace导致编译失败，补using后31初始owner通过。HTTP RED为实际GET404而非200。再补可信上下文及四种不支持存储envelope变体、HTTP接线后最终：

| 集合 | 实际结果 |
|---|---|
| WMS主数据/Outbox/创建事件owner | 179 passed，0 failed，0 skipped |
| WMS领域、配置、HTTP及既有两变体真实HTTPS Chromium | 159 passed，0 failed，0 skipped |
| Architecture | 29 passed，0 failed，0 skipped |
| Release solution | 0 warnings，0 errors |
| EF模型 | 无pending changes |

WMS-EVENT-READ-01..04实际owner通过：真实Own/Departments/All逐字段范围交集、未知creator与foreign tenant不泄露，SQL先creator过滤再offset；27资源验证默认25+2并非只测小样本。当前部门、会话、角色、字段、成员撤销都再次查证；九字段逐一缺失403，主数据权限不能代替消息权限；clean/tracked/current/ambient拒绝，authority后/SQL物化完成取消不交付；Provider异常安全传播，不成功空数组。直接SQL只在隔离fixture构造Owner/contract/schema/Status99损坏反例，读取明确异常，不用于生产维护或绕过持久化门禁。

WMS-EVENT-READ-05 HTTP通过：精确3字段页/9字段行与真实源事实一致，无payload/name/actor/tenant/Inbox或虚构attempt；独立消息权、当前撤字段403、匿名/登出401/HTTP400、分页错误400、未装配404；GET成功不开放POST/仓库写开关。

TRX会话`files\wms-event-read-validation\{owner,http-red,final-owner,http-browser,architecture}`保留首轮和RED，最终367相关.NET通过不叠加历史轮。没有新客户端/新API浏览器调用，既有浏览器只回归主数据read/创建/撤权；没有重跑前端462（本片未改前端）、完整.NET/Route/Platform或Files浏览器。真实Broker/发布消费/跨owner聚合与整包仍缺，不用本片测试代签。
