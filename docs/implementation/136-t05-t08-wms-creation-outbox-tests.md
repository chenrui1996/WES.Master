# T05/T08 WMS 创建 Outbox 测试

- 状态：已验收（本片用例；父DTC和整包仍未完成）。
- 设计：[本片设计](135-t05-t08-wms-creation-outbox.md)。

| 编号 | 步骤 | 预期 |
|---|---|---|
| WMS-OUTBOX-01 | 创建仓/位、重放、并发同operation/code | 每个已提交创建恰好一事实/事件，原event稳定，无二效 |
| WMS-OUTBOX-02 | 实际SQL后保存失败/取消/事务回滚 | 新资源/事实/事件均0，显式重试不重复派生 |
| WMS-OUTBOX-03 | 直接孤立/外tenant/篡改/删除事件 | 持久化门禁拒绝，不能伪造正文或孤立事件 |
| WMS-OUTBOX-04 | 迁移模型/SQL/真实FK/tenant过滤 | 独立WMS表、复合键/FK、无跨owner或历史回填 |
| WMS-OUTBOX-05 | 既有当前授权/HTTP只读/目录/浏览器 | 原权限、默认禁用及只读合同不退化 |

## 环境与执行

- .NET10.0.103、EF/MySql.EntityFrameworkCore10.0.7、MySql.Data9.7.0；仅批准的MySQL8.0.12/wes_test，使用已有测试装配读凭据，未记录秘密。
- 新迁移 `20261027021448_WmsMasterDataOutbox`，owner历史表`__WesWmsMigrations`，由既有测试fixture显式MigrateAsync应用；无Host启动迁移。
- 测试源：`tests\Persistence\WmsMasterDataTests.Outbox.cs`（新增17项）；同operation/code竞争与跨tenant同operation断言扩展于既有主文件。

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~CreationOutbox"
dotnet ef migrations add WmsMasterDataOutbox --project src\Modules\Wms\Wes.Wms.csproj --context WmsDbContext --output-dir Infrastructure\Persistence\Migrations
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WmsMasterDataTests"
dotnet ef migrations has-pending-model-changes --project src\Modules\Wms\Wes.Wms.csproj --context WmsDbContext --configuration Release --no-build
dotnet build WES.slnx -c Release --no-restore
dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~Wms|FullyQualifiedName~RealBrowserReadsWarehouseAndLocationAndClearsDetailsAfterCurrentRevocation"
dotnet test tests\Architecture\Wes.Architecture.Tests.csproj -c Release --no-build --no-restore
```

首命令为RED：不存在OutboxMessages/事件类型，编译明确失败。迁移工具生成后立即`--no-build`检查曾误用旧Debug程序集报告漂移；重新编译并明确Release后无漂移，真实owner模型断言同样通过。不得将生成前的程序集当最终模型证据。

首轮135通过；增加DDL/新事件篡改后139通过/1失败：真实MySQL元数据将referenced列名规范成小写，按数据库列名大小写不敏感比较修正此断言（业务payload/合同/版本仍精确匹配）。增加旧operation无回填变体后最终：

| 集合 | 实际结果 |
|---|---|
| WMS主数据owner（含17新增Outbox） | 141 passed，0 failed，0 skipped |
| WMS领域、HTTP、真实HTTPS Chromium | 100 passed，0 failed，0 skipped |
| Architecture | 29 passed，0 failed，0 skipped |
| Release solution | 0 warnings，0 errors |
| EF模型/真实DDL | 无漂移；复合FK/有序索引/datetime(6)实查通过 |

共270项相关.NET测试，不是完整.NET回归。真实浏览器继续使用现有已构建ops-web，覆盖目录/详情/撤权/真实API；本片无客户端变更，未重跑前端单测/构建、Platform或Files专属浏览器。没有重新计入上一片427客户端测试。

WMS-OUTBOX-01..04为本片实际通过；WMS-OUTBOX-05为既有路径回归通过。库位payload验证完整schema/Pack/新资源；未提交故障三表全0；同步门禁与异步入口共享验证，显式同context保存失败重试复用事件；历史operation无事件变体由隔离测试删事件构造，不代表真实迁移回滚/生产数据导入。真实FK检查是元数据和正常插入，不宣称验证了生产DBA权限/部署回滚。

本机证据保存在会话`files\wms-outbox-validation\{owner,verified,final,http-browser,architecture}` TRX；前两目录保留首轮结果，最终以final/http-browser/architecture为准。未新增依赖、未restore/install、未连Broker/生产/设备，未执行Down破坏待发送事件。
