# T08 主数据详情HTTP测试

- 状态：已验收（仅列明的只读HTTP详情范围）；关联[设计](119-t08-master-data-detail-http.md)。

| 用例 | 输入/步骤 | 预期 |
|---|---|---|
| WMS-HTTP-01 | 显式启用后真实JWT读取仓/位 | 200精确固定字段/no-store，无creator/operation/库存泄露 |
| WMS-HTTP-02 | 不启用、匿名、HTTP scheme、未知/空UUID | 404/401/400/404/400，不默认开放 |
| WMS-HTTP-03 | 成功后另一owner撤字段/角色或logout | 下次403/401，不复用旧token角色 |
| WMS-HTTP-04 | query/header伪造tenant/creator，调用未提供的写动词 | 不能变更可信subject，写入口不存在，无额外事实 |
| WMS-HTTP-05 | 缺Identity或非法Enabled配置 | 启动验证明确失败；不安装/连接生产能力 |

## 执行记录

- 新增**6项部署配置**及**11项真实MySQL/JWT HTTP**测试通过。Success逐项核对固定6/9字段的精确键名、Id/Code/Name/UTC/版本、禁用状态和归属，不只断言200；普通GET不增加创建事实，响应no-store，无actor/operation/quantity字段。
- 默认关闭404、匿名401、HTTP scheme400、空UUID400及未找到404；当前角色/Name字段撤销403、logout后401。真实foreign tenant同码仓库不能由query tenant伪造读到，客户端creator/header不能改主体；POST/PUT/DELETE均405且资源仍禁用。
- 起始缺Host配置类型的编译RED已观察。首次实际HTTP **7 failed、9 passed**，根因为Host JWT使用`tid`而新适配器误用了要求`tenantId`的另一解析器；改为既有IdentityRuntime.Subject后原16项全部通过，随后新增真实foreign tenant反例纳入最终全套。没有改JWT claims或放宽测试预期。
- 修改Host项目引用后才执行solution restore；没有升级/新增第三方包。最终全solution Release构建**0警告/0错误**，完整串行Architecture **29**、Modules **524**、Persistence **571**、Route **228**，共**1352 passed、0 failed、0 skipped**，包含既有两套真实HTTPS浏览器回归。本片新增HTTP为TestServer scheme验证，不冒充新WMS浏览器/TLS端到端验收。
- 限定文件格式和diff/文档链接检查通过。TRX保留在会话`files\wms-masterdata-http-validation\{targeted,corrected,full}`；失败记录保留，不用最终GREEN抹除。

## 复跑

```powershell
dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~WmsMasterDataSettingsTests|FullyQualifiedName~IdentityHttpTests.WmsDetails" -m:1
dotnet build WES.slnx --configuration Release --no-restore -m:1
dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1
```

本片无新迁移/前端源码，未重新运行独立240项Vitest。列表/树/页面、当前创建授权、编辑启停/容器绑定/Outbox及真实Pack能力尚未完成，T08和其他四个整包不据此关闭。
