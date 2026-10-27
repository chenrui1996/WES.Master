# T05 owner 选择与 WMS 创建事件客户端测试

- 状态：已验收（本片用例；跨owner统计/发布消费/父DTC整包未关闭）。
- 设计：[143](143-t05-owner-selected-message-client.md)。

| 子用例 | 断言 |
|---|---|
| WMS-EVENT-CLIENT-01 | 显式读取/精确9字段、Own按钮可用/逐字段缺失禁用、无投递/统计/重放/业务缓存 |
| WMS-EVENT-CLIENT-02 | 精确3字段页/9字段行、非空UUID/UTC/固定envelope/重复/页回声/分页边界，坏响应清旧 |
| WMS-EVENT-CLIENT-03 | stale/503、安全错误关联、401/403/404清旧、身份/grant/离线/卸载/迟到取消、手工恢复 |
| WMS-EVENT-CLIENT-04 | owner模块+权限成对OR、catalog错误、切换清旧/取消、不自动读、Platform重放锁选择 |
| WMS-EVENT-CLIENT-05 | 真实MySQL/HTTPS Chromium两变体读取/真实当前撤字段403、375/768/1440/200%字体/键盘/无业务storage |

## 执行结果

沿既有批准wes_test、MySQL8.0.12、.NET10.0.103及锁定前端依赖；无依赖/manifest/迁移变更，不安装服务、不连生产/未知Broker。新增45条WMS事件组件/边界、8条双owner宿主页、5条路由、1条导航，共59。字段逐一缺失、两种越owner权限不能借用、Own准入与独立服务端scope复验；合法实时分页、错误页回声、重复/超限/未知envelope/扩展payload与attempts、UTC/UUID反例；503保留stale、安全关联，401/403/404清旧；身份/grant/离线/卸载/切owner取消与迟到丢弃。宿主页验证模块/权限成对、默认owner/撤权、无自动读取、真实Platform重放准备后锁owner，另启停无关模块不能意外解锁原重放面板。

RED：缺`WmsCreationEvents.vue`导致组件集合不能加载，WMS导航/直接路由实际失败，保留既有Platform测试。首宿主测试误点重放预览而不是关闭，修正为明确type=button关闭动作；审查发现无关module列表变化不应解锁原Platform重放，拆分scope/owner watcher并补回归。首真实浏览器14通过/两WMS变体失败于owner label的可访问名含select option文本；将label与select按for/id分离，新增精确label绑定断言，保持浏览器exact可访问名不放宽，之后两变体实过。

```powershell
cd clients\ops-web
npm test
npm run build
cd ..\..
dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages|FullyQualifiedName~RealBrowserReadsWarehouseAndLocationAndClearsDetailsAfterCurrentRevocation|FullyQualifiedName~WmsCreationEvents"
dotnet test tests\Architecture\Wes.Architecture.Tests.csproj -c Release --no-restore
dotnet build WES.slnx -c Release --no-restore
```

| 最终集合 | 结果 |
|---|---|
| Vitest全前端（31文件） | 521 passed |
| vue-tsc/Vite | 通过 |
| WMS创建事件HTTP13+真实Platform浏览器1+WMS两变体浏览器2 | 16 passed，0 failed，0 skipped |
| Architecture | 29 passed，0 failed，0 skipped |
| Release solution | 0 warnings，0 errors |

真实浏览器保留原主数据目录/详情/禁用仓创建与永久事实/事件最终核验。随后在同已登录会话经真实导航进入消息页，仅有Wms owner，键盘Tab/Enter显式GET，断言当前2/3条事件实际UUID/时间、no-store和固定Pending；375/768/1440及200%字体无横溢、离线禁读/重连不自动读。第二DB owner分别先撤主数据Name，再撤独立消息role的EventId；原会话下一请求两处均真实403并清旧，没有stub或前端伪改grant。总WMS请求只读7GET，创建变体10请求中9GET+既有1POST。local/sessionStorage不含仓/位/Pack/事件UUID、名称、token。

会话TRX `files\wms-event-client-validation\{http-browser,browser-fixed,final,architecture}`保留首败与最终结果；总数不累加重跑。此片无后端owner逻辑修改，不重跑已于141/142验证的179 owner；未跑完整.NET/Route/Files浏览器。没有真实WMS publisher/Inbox、跨owner统计/统一快照或Broker ACK窗口，不把“未接入”变成零值成功，T05及T04–T08整包仍未关闭。
