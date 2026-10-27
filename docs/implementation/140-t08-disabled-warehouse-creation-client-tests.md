# T08 禁用仓库创建客户端测试

- 状态：已验收（本片用例；父DTC/完整T08仍未完成）。
- 设计：[本片设计](139-t08-disabled-warehouse-creation-client.md)。

| 编号 | 过程 | 预期 |
|---|---|---|
| WMS-CREATE-UI-01 | 无grant/缺字段、合法/非法输入、预览/确认 | 无越权发送；精确范围、仅确认后显式key POST |
| WMS-CREATE-UI-02 | unknown、503/断网/非法成功响应、手工重试 | 原body/key冻结、无自动重发、拒绝不掩盖先前未知 |
| WMS-CREATE-UI-03 | 提交中身份/grant改变、迟到成功/错误、卸载 | 取消清旧不交付跨主体结果，不新增本机缓存 |
| WMS-CREATE-UI-04 | 真HTTPS+MySQL+Chromium，键盘、375/768/1440/200% | 实际201、原key/body、禁用单效果；显式GET再读 |
| WMS-CREATE-UI-05 | 原只读fixture与前端全套/构建 | 原目录/详情/撤权不退化，无write角色时所有请求仍GET |

## 执行

沿既有Vue3/TypeScript/Vitest/Playwright、.NET10.0.103及真实MySQL8.0.12/wes_test授权环境。未新增依赖/迁移/安装。fixture明确在写入变体开启单独仓库创建开关并授予Own Code/Name；真实业务接口无route.fulfill或成功schema替身。

```powershell
Set-Location clients\ops-web
npm run test -- --run src\features\wms\WarehouseCreation.spec.ts
npm run test
npm run build
# 回仓库根执行
dotnet build WES.slnx -c Release --no-restore
dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~Wms|FullyQualifiedName~RealBrowserReadsWarehouseAndLocationAndClearsDetailsAfterCurrentRevocation"
dotnet test tests\Architecture\Wes.Architecture.Tests.csproj -c Release --no-build --no-restore
```

RED为缺WarehouseCreation.vue，测试导入失败。首轮目标87通过/1失败：单行HTML input会去掉换行，故测试传入newline不再是该控件实际输入；改以DEL验证控件非法control，另外直接调用共享边界验证newline仍拒绝，未放宽生产校验。随后全套458通过，增加deadline精确9999ms未过/10000ms进入unknown、timer卸载清理、replay回执、非法JSON后最终：

| 集合 | 实际结果 |
|---|---|
| 前端全套 | 462 passed，29测试文件 |
| vue-tsc/Vite | 通过 |
| 真HTTPS+MySQL+Chromium两变体 | 2 passed（包含于下行146） |
| WMS领域、配置、HTTP及真实浏览器 | 146 passed，0 failed，0 skipped |
| Architecture | 29 passed，0 failed，0 skipped |
| Release solution | 0 warnings，0 errors |

新增34组件/边界与1页面整合测试。组件检查未确认不发、准确64/128上限、缺字段不发、原key/body显式重试、五种非法success回应、五种拒绝、先前未知保留、撤权/主体改变/离线/超时/卸载取消、迟到成功不交付及不自动读回。页面测试检查打开创建清旧/隐藏读区、提交只POST、单独点击回执才GET。

浏览器保留原只读变体5次全GET、目录/详情/撤权/离线不自动读；写入变体额外1POST+2GET共8次，核201/no-store/原operation/精确code/name、禁用提示、显式读取新仓/返回旧库位后真实撤权403。两个变体均覆盖Tab/Enter焦点、375/768/1440、200%字体无整页溢出及本机存储无业务身份/名称；最终.NET owner验证真实actor、同operation唯一事实/事件、新仓禁用/版本一致、库存流水无增量。

本片TRX会话`files\wms-create-client-validation\{browser,final,architecture}`；最终175相关.NET不叠加首轮2或上一片174。未跑完整.NET/Route/Platform或Files专属浏览器、真实PDA/Pack/生产/病毒库/Broker；未声称完成其他T04–T08或完整父DTC。
