# T08 库存客户端测试

- 状态：已验收（本切片），2026-10-27；下列为实际执行结果，不关闭父DTC或T08整包。
- 设计：[147](147-t08-inventory-query-client.md)。

| 子用例 | 断言 |
|---|---|
| WMS-INVENTORY-CLIENT-01 | 显式GET/完整All字段、无编辑/跨单位合计、精确字符串及固定15字段 |
| WMS-INVENTORY-CLIENT-02 | 3字段页/UUID/decimal/BigInt等式/精度/分页回声/重复/损坏扩展拒绝 |
| WMS-INVENTORY-CLIENT-03 | 精确筛选/无batch、分页、变更清旧/取消、离线/身份/迟到、403清旧/503 stale |
| WMS-INVENTORY-CLIENT-04 | Wms模块与独立权限导航/路由，真实HTTPS MySQL当前撤字段403、响应式/键盘/无业务storage |

## 执行记录

- RED：组件缺失；实现后50项首轮通过，但严格构建发现Record字段在回调内失去precision窄化。改为验证后计算BigInt quantum，六个数量保留显式强类型，再补6项分页/大小变更/非法范围/即时撤字段反例。
- `npm test`：32文件/581项通过；其中库存组件/API56项，新增导航1、路由3。覆盖完整15字段缺失、Own/部门拒绝、22+6位最大值无浮点、等式/精度/筛选/扩展损坏拒绝、旧响应/取消/离线、403清旧与503陈旧。
- `npm run build`：vue-tsc及Vite通过，无新增依赖。
- `dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RealBrowserReadsWarehouseAndLocationAndClearsDetailsAfterCurrentRevocation|FullyQualifiedName~WmsInventory"`：21通过＝18真实HTTP及3套HTTPS Chromium；TRX `inventory-client-validation\http-browser\15009_SHERRY377_2026-10-27_15_44_41.trx`保存于本机session evidence，不提交凭据/证书。
- 浏览器使用真实loopback HTTPS/Kestrel、真实登录/当前MySQL权限和真实收货数据，无业务API stub。库存变体精确显示`999999999.999999`单笔上限字符串，不冒充满decimal余额构造；375/768/1440宽度与200%字体、键盘提交、离线禁读/重连无自动GET、第二DB owner当前撤Available→下一GET403清旧通过；全场景9 GET/0 POST，storage无余额业务UUID。fixture最终核唯一收货流水/余额、0预留不变。
- `dotnet test tests\Architecture\Wes.Architecture.Tests.csproj -c Release --no-restore`：29通过；TRX `inventory-client-validation\architecture\15009_SHERRY377_2026-10-27_15_46_29.trx`。
- `dotnet build WES.slnx -c Release --no-restore`：0警告/0错误。无模型/迁移变化；owner74项结果引用[146](146-t08-authorized-inventory-query-tests.md)，本片未冒称重新执行。

不适用/未运行：实际PDA/扫码枪、真实Pack、其他两Provider、生产与现场；未交付固定核对快照/流水/导出，不以实时分页代替。
