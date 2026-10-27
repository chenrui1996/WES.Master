# T08 当前授权主数据目录测试

- 状态：实施中；关联[设计](123-t08-authorized-master-data-catalog.md)。

| 用例 | 验证步骤/预期 |
|---|---|
| WMS-CATALOG-01 | 真实MySQL Own/Departments/All与逐字段scope交集，授权后排序分页；缺creator永不返回 |
| WMS-CATALOG-02 | 另一owner移creator部门、撤角色/成员/字段/session后再次查询，不能复用旧authority |
| WMS-CATALOG-03 | 精确Code/Enabled/Warehouse筛选及25/50/100、offset上限；非法查询/跨tenant/空主体拒绝，无total/隐藏字段/写事实 |
| WMS-CATALOG-04 | 缺authority结果、非法creator、取消/数据库失败显式错误，不回成功空页 |
| WMS-CATALOG-05 | 真实HTTP HTTPS/JWT、默认关闭、精确shape/非法query、受权分页及撤权 |
| WMS-CATALOG-06 | 客户端显式目录/分页/筛选，选择后重新GET详情；切scope/类型/过滤、迟到响应、403/503/离线与非法页 |
| WMS-CATALOG-07 | 真实HTTPS Chromium目录选取后重新详情、范围撤权清空及响应式/键盘/无业务本机缓存 |

## 执行记录

1. RED：先增加真实owner测试，编译缺`QueryWarehousesAsync`/`QueryLocationsAsync`，证明确实需要目录实现；组件先测，因`MasterDataCatalog.vue`不存在失败，均未以空套件计通过。
2. 实现Platform公开的当前creator范围交集、WMS authority端口、SQL内过滤及有界分页；首次编译缺WmsDatabaseFailures的命名空间，补齐既有owner namespace后**18项真实MySQL**初测通过。追加10项tenant/异常/取消/授权空集边界后，与既有主数据创建及详情联合**84 passed**（原56+新增28）。
3. HTTP首批**16 passed**：默认关、精确shape/分页/筛选、invalid400无rows、HTTPS及当前撤权。追加真实foreign tenant及两类permission不可互借后新增HTTP共**18项**通过。
4. 客户端首次目录/详情联合48通过，补目录-详情交叉撤权及过滤响应校验、旧错误、分页尺寸/非法JSON后，前端全套**306 passed / 0 failed**（25文件，原278+新增28）；严格vue-tsc与Vite构建通过。组件用例包含显式读取/完整权限、scope/类型/过滤变化取消、旧成功/错误丢弃、503 Stale禁选、401/403/404清行、重复Id/错页/错形状/额外total/乱序或错筛选响应拒绝、离线恢复不自动重读、选择后当前详情重读与双向403清理。
5. 真实HTTPS Kestrel+隔离MySQL+真实JWT+Chromium，扩展既有WMS脚本后**1 passed / 0 failed / 0 skipped**。显式仓库/库位目录、行选择后单资源GET重新读取、精确固定字段/版本/UTC/no-store、真实Tab/Enter焦点、375/768/1440无整页溢出及桌面200%字体、离线禁选/恢复不自动请求、另一owner撤字段后403清详情及目录、仅5次GET且无本机业务/token缓存。无API网络替身；最后创建事实仍2、无entry、仓/位仍禁用。
6. 第一轮完整串行Release回归Architecture **29**、Modules **543**、Persistence **599**、Route **228**，共**1399 passed / 0 failed / 0 skipped**，包含三套真实HTTPS浏览器；全solution构建**0警告/0错误**。
7. 完整回归之后按“验证精确取消时点”再补两项真实MySQL反例：`DataReaderClosingAsync`在结果物化完成后取消，仓库与库位均未抛取消（**2 failed / 0 passed**）。根因为仅在authority后检查取消，未在SQL完成后复核。两个查询分支各补`ThrowIfCancellationRequested`，不改provider或放松断言；最终全solution重建**0警告/0错误**，全部主数据owner **86**、HTTP/真实WMS浏览器 **30**、Architecture **29**，共**145 passed / 0 failed / 0 skipped**。本片新增真实owner共**30项**（原28+两个迟到取消）；1399是修正前完整结果，145是修正后相关复验，不相加冒充一次完整套件。

会话TRX保存在`wms-masterdata-catalog-validation`的first/owner/http/browser/final/late-cancellation-red/verified目录；失败编译及初测均按实际记录，不抹除失败历史。新WMS浏览器在专属装配执行，非WMS装配不以skip代替该证据。

## 复跑

先构建客户端，再运行已获准的真实wes_test测试装配，不使用生产URL：

```powershell
Set-Location clients\ops-web
npm test
npm run build
Set-Location ..\..
dotnet build WES.slnx -c Release --no-restore
dotnet test tests\Architecture\Wes.Architecture.Tests.csproj -c Release --no-build --no-restore
dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-build --no-restore
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj -c Release --no-build --no-restore
dotnet test tests\Route\Wes.Route.Tests.csproj -c Release --no-build --no-restore
```

完整父DTC/页面生命周期及T04–T08未关闭；真实Pack、其他Provider按ADR-0002延后、实际终端/病毒库/Broker/部署与现场证据不由目录测试产生。
