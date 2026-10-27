# T08 授权库存流水测试

- 状态：实施中（本片软件已验证）；设计：[153](153-t08-authorized-stock-ledger-query.md)；父DTC/T08整包不关闭。

| 子用例 | 输入/步骤 | 预期 |
|---|---|---|
| WMS-LEDGER-READ-01 | 真实收货/发运/移库、微量/单笔上限 | 19字段、正负固定六位字符串、前后差与来源正确、不可变历史不借当前余额 |
| WMS-LEDGER-READ-02 | 27条/同秒、精确维度/batch大小写/来源/correlation/kind/UTC半开 | SQL先过滤后分页、有序无重复、外tenant不可读 |
| WMS-LEDGER-READ-03 | 完整独立All字段、Own/部门/其他permission、逐字段撤销/当前会话 | 拒绝不借权、无成功空替代 |
| WMS-LEDGER-READ-04 | 非法页/UUID/batch/date/kind、dirty/transaction/ambient、取消/故障 | 无写、明确拒绝/安全失败、不交付迟到 |
| WMS-LEDGER-READ-05 | SQL隔离构造坏entry量/前后差/kind/来源关联/单位 | 安全日志明确失败，不伪造有效流水 |
| WMS-LEDGER-READ-06 | 真实HTTP装配/HTTPS/当前身份、精确响应、缺字段/跨tenant | 正确200/400/401/403/404/405/no-store，无改账端点 |

## 红绿与实际结果

1. 新owner测试先编译失败：缺StockLedgerAccess/IStockLedgerAuthorization；新前端先因缺模块不能收集。此仅是新合同缺失证据，不冒称运行时行为红灯。
2. 真实MySQL首跑105通过/1测试失败：期望的负数序列误用数值顺序而实际断言按Ordinal排序；修正测试的Ordinal期望，未放宽业务量或查询顺序。
3. 架构首跑28通过/1失败：Host引用Domain枚举。改为独立Features读取契约及显式owner映射，原架构检查不变。
4. 新HTTP含省略时区/仅日期/空时间，全部400。编写UTC辅助方法时一轮patch位置错误导致编译失败，已修复并复测，不视为业务RED。
5. 真实375px叠加200%文字初失败；仅让流水长标题断行不足，DOM定位body实际640px才修共享最小宽度为`min(20rem,100%)`。原viewport检查保留，随后各宽组合均通过。
6. 完整Platform浏览器发现既有新建按钮加载中可点但open拒绝，见[155](155-t04-user-create-loading-gate.md)/[156](156-t04-user-create-loading-gate-tests.md)；补disabled并保留全部原断言，无sleep/重试伪绿。

| 实际范围 | 最终结果 | 本机会话证据相对根 |
|---|---|---|
| WmsLedgerTests全类，含32新增流水读取 | 106通过/0跳过 | `stock-ledger-query-validation\owner-contract-final\15009_SHERRY377_2026-10-27_16_29_32.trx` |
| 新流水27HTTP+原库存18HTTP | 45通过 | `http-browser-complete\15009_SHERRY377_2026-10-27_16_39_48.trx`中45项HTTP；该轮Platform浏览器失败，不把整个49项文件冒称全绿 |
| 追加真实foreign posting及伪造tenant query/header | 1通过，HTTP累计46 | `foreign-http-final\15009_SHERRY377_2026-10-27_16_49_26.trx`；foreign收货事实真实存在，但当前tenant来源/维度查询只得到授权空页 |
| 最终Platform全具名页面+WMS只读/创建/库存三变体 | 4通过/0跳过 | `four-browser-final\15009_SHERRY377_2026-10-27_16_46_21.trx` |
| 架构 | 29通过 | `architecture-delivery\15009_SHERRY377_2026-10-27_16_47_18.trx` |
| 前端33文件 | 691通过（流水81+路由3+导航1，新建门禁2） | `npm test`最终输出 |
| 严格vue-tsc/Vite、Release solution | 通过；0警告/0错误 | 最终构建输出 |

证据根为当前会话`files\stock-ledger-query-validation`；owner完整根含该前缀，其余表行均相对它。相关.NET覆盖106+46+4+29=185，不是完整.NET全套。新增流水HTTP合计28；使用真实已授权wes_test/第二owner撤权，故障注入/存储损坏为该随机tenant隔离探针；未连生产、未安装服务、未新增依赖/模型/迁移。

浏览器保留原读/唯一创建效果核对，新增ledger GET200/GET403（库存变体共13个WMS请求），19字段/来源显示、数量原值、键盘focus-visible、各宽200%文字、离线不自动刷新、无localStorage/sessionStorage业务数据。来源跳转/固定核对快照/授权导出/运输回调/完整父DTC以及真实Pack/PDA/物理证据未执行或未实现。
