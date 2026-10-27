# T05 WMS托管发布接线测试

- 状态：实施中；设计：[159](159-t05-hosted-wms-publisher.md)；未执行预期不计passed。

| 子用例 | 输入/步骤 | 预期 |
|---|---|---|
| WMS-HOST-PUBLISH-01 | 默认/显式关闭；畸形值/未知键/错误边界/重复tenant | 关闭无注册/owner/外发；非法配置启动明确失败且不泄漏值 |
| WMS-HOST-PUBLISH-02 | 缺Identity/Outbox/MessageQueue、缺/重复/scoped/仅keyed transport | 启动拒绝，不注册成功替身或借用Platform adapter |
| WMS-HOST-PUBLISH-03 | 两个allowlist tenant、batch7 | 串行各一次，五种有限结果，不循环排空；Unconfirmed有警告 |
| WMS-HOST-PUBLISH-04 | 安全瞬时/永久DB错误、程序异常、停止 | 瞬时继续；永久/程序错误向上；停止不开始后tenant、不记录成功 |
| WMS-HOST-PUBLISH-05 | 受控60秒deadline与停止worker | 释放timer、预算到期明确失败，下一tenant用新预算 |
| WMS-HOST-PUBLISH-06 | 真实MySQL runner取两个既有WMS事件，每次batch1，再空批/foreign | 新owner稳定各一效果，不改禁用资源；不访问foreign或迁移生产库 |
| WMS-HOST-PUBLISH-07 | 实际dotnet子进程在CreateBuilder前以命令行启用WMS publisher及两项capability | 当前Program在更早的运行owner校验明确拒绝；未登记Outbox/MessageQueue不因本片装配变为可用 |

真实Broker/消费Inbox/部署及现场验收仍未完成；本片只验证调度桥接，不伪造其依赖已上线。

## RED与测试装配纠正

先写配置/worker用例，缺失WmsPublisherWorker/IWmsPublisherRunner建立编译合同RED，随后实现；初轮52项通过，再加入事件/计数回归80项通过。

实际启动验证没有掩盖失败：最初WebApplicationFactory.ConfigureAppConfiguration启用测试未抛异常。既有Factory注释已说明该配置追加晚于top-level Program的部署快照读取，故这不是实际启用证据。删除该错误装配，改用独立dotnet子进程，在CreateBuilder之前提供公开开关/随机tenant，30秒超时只终止自己的子进程，不提供连接或身份秘密。

子进程最初仍错误期待WMS transport注册错误，实际启动更早在FeatureSwitchConfiguration拒绝未登记的MessageQueue/Outbox runtime owner。依据真实输出，测试改为精确断言这两条门禁，生产运行能力校验保持原样；独立服务集合仍精确验证缺少/重复/scoped/仅keyed transport拒绝。不能把这项测试描述成实际Program已经走到transport注册或已经可启用Broker。

上述失败保留在本机会话证据目录的host-composition.trx、host-composition-child.trx及host-composition-child-details.trx；它们不是通过记录。

## 最终执行结果

在已授权wes_test/真实MySQL环境串行运行，未连接生产或真实Broker。新增27项Host测试（含真实子进程1项）和1项真实MySQL runner；后者验证原EventId、两次batch1各一Published/Attempts1、空批/foreign无效果、资源仍禁用、无库存流水及默认无worker。

最终Release：

- Modules选择器`WmsPublisherTests|PlatformPublisherTests|WmsHostedPublisherRunner|WmsCreationEvent|WmsEventCounts|RealBrowserReadsWarehouseAndLocationAndClearsDetailsAfterCurrentRevocation|RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages`：85/85，包括原Platform调度25项、原HTTP28项及四套真实HTTPS Chromium。
- Persistence选择器`WmsPublisher|WmsDeliverySaveGate`：33/33，原耐久租约/CAS/确认保存/预算/迁移恢复回归。
- Architecture全套：29/29。合计147/147，均无跳过。
- `dotnet build WES.slnx --no-restore --configuration Release --verbosity quiet`：0警告/0错误；修改C#的dotnet format verify、diff check及19条新增Markdown链接检查通过。

本机证据目录：会话files下`wms-hosted-publisher-validation`；最终TRX为hosted-release-http-browser-final.trx、owner-release-final.trx、architecture-release-final.trx。Debug中间结果hosted-composition-final.trx为81/81，不和Release重复求和。

本片未改前端/依赖/模型/迁移，不重复执行前端单测/构建或生成新迁移；四套真实浏览器使用上片已构建dist并已重跑，不把上片695前端结果冒称本片新执行。未跑全量.NET、SQL Server/PostgreSQL、真实Broker/ACK丢失/OS崩溃、消费Inbox、部署/生产验收；T05父DTC及T04–T08整包仍未完成。
