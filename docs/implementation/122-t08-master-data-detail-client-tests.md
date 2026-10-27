# T08 主数据详情客户端测试

- 状态：实施中；关联[设计](121-t08-master-data-detail-client.md)。

| 用例 | 步骤 | 断言 |
|---|---|---|
| WMS-CLIENT-01 | 分别具备仓库/库位完整字段，显式输入UUID读取 | 精确路径/只读字段/版本/UTC/固定归属，无写按钮，不自动mount查询 |
| WMS-CLIENT-02 | 切类型/UUID/scope、撤字段或登出时旧响应迟到 | 立即清理取消，成功/错误均不能污染新目标 |
| WMS-CLIENT-03 | 同查询503、403、404、离线与恢复 | 503 Stale；403/404清数据并显式提示；离线不发请求/排队，恢复需手工重读 |
| WMS-CLIENT-04 | 空UUID、错响应Id/缺字段/假boolean/非法日期或schema | 不请求或拒绝整份数据，不暴露正常详情 |
| WMS-CLIENT-05 | Wms模块关、无read权限、仅仓或位权限 | 导航/直接路由一致，不能跨类型放大权限 |
| WMS-CLIENT-06 | 真实TLS浏览器键盘读仓/位、375/768/1440、撤权后重读 | 精确响应/页面，长字段无整页溢出，403后无旧详情/业务缓存，DB无新创建或库存事实 |

## 执行记录

1. RED：先增加组件用例，Vitest因`MasterDataPage.vue`不存在失败，未以空套件作为通过。
2. 首轮组件/路由/导航41项通过，严格类型检查和Vite构建通过。随后补齐非法JSON刷新、离线中断与Wms目录失败用例，最终新增**31组件+5路由+2导航=38项**，前端全套**278 passed**（原240），24个测试文件通过；`npm run build`通过。
3. 新.NET浏览器fixture首次编译被xUnit1030阻止：清理等待错误使用`ConfigureAwait(SuppressThrowing)`。改为保留原测试异常、取消受控协调任务并只接受自己的协调取消，不忽略DB错误。之后第一次HTTP/浏览器回归因错误扩大filesRoot配置条件导致WMS装配误开启文件能力，同时Platform-only浏览器误执行新WMS脚本；精确恢复文件条件、仅扩展模块目录测试注入，并按既有模式让非WMS装配不执行WMS专属脚本。
4. 修正后14项回归中**13 passed / 1 failed**：WMS真实浏览器能读取仓库，但精确标签定位资源类型超时。改为显式label/for分离select选项，避免嵌套选项污染标签定位；不改用脆弱CSS定位掩盖语义问题。下一次真实浏览器已完成双资源、响应式、离线和撤权，但最后断言错误假定安全code包含403；实际HTTP为403且既有middleware返回`PermissionDenied`，调整页面错误断言为正式code，保留403断言和旧详情必须清空的断言。
5. 真实WMS HTTPS Chromium最终**1 passed / 0 failed / 0 skipped**：无API网络替身，隔离MySQL+真实JWT+Platform当前authority；仓/位固定字段和UTC/版本、响应no-store、Tab/Enter与focus-visible、375/768/1440无整页溢出及桌面200%字体、离线陈旧/恢复不自动重读、另一DB owner撤字段后403清旧详情、仅3次GET、无业务/token本机存储。数据库创建事实仍2条、无账本entry、仓/位仍禁用。临时信号文件仅协调fixture何时通过独立owner撤真实角色，非API或授权替身；fixture退出删除准确两个文件及其专属目录。
6. 最终全solution Release构建**0警告/0错误**；完整Modules **525**、Architecture **29**，共**554 passed / 0 failed / 0 skipped**，包含新WMS及既有Platform/文件三套真实HTTPS浏览器。本片不重跑未改动的Persistence/Route套件，不把上一片1352整套结果冒充本片新结果。

失败与最终TRX保留在会话证据目录`wms-masterdata-client-validation`的first/corrected/accessible/browser/final子目录。非WMS的既有Platform-only浏览器装配不运行WMS专属脚本，但本片单独真实WMS装配必须执行通过，不能跳过来充当验收。

复跑（仓库根执行，须事先已有批准的测试依赖与wes_test权限）：

```powershell
Set-Location clients\ops-web
npm test
npm run build
Set-Location ..\..
dotnet build WES.slnx -c Release --no-restore
dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-build --no-restore
dotnet test tests\Architecture\Wes.Architecture.Tests.csproj -c Release --no-build --no-restore
```

完整Modules包括新WMS浏览器及既有Platform/文件浏览器，由隔离fixture启动自签TLS及真实本机MySQL；不手工设置生产URL。不代签真实Pack、其他页面完整200%字体/语言、PDA、现场安全或生产部署。
