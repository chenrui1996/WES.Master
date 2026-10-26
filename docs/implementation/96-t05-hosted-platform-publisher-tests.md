# T05 托管Platform发布测试

- 状态：定向及完整串行回归通过；不关闭T05。关联[设计](95-t05-hosted-platform-publisher.md)。
- PUB-HOST-01：默认/显式禁用不创建worker、runner或timer；非法已提供配置仍失败。
- PUB-HOST-02：tenant明确白名单、唯一/非空/连续数组、100上限；interval 1..3600、batch 1..100、大小写不敏感配置键；功能开关与Identity必须明确可用。
- PUB-HOST-03：缺少/重复/scoped transport拒绝装配；不创建成功fallback。
- PUB-HOST-04：一tick每个白名单tenant恰好一次，使用配置batch；输出有限持久投递计数而非业务完成。
- PUB-HOST-05：瞬时数据库故障/预算到期明确失败但继续下一tenant；永久错误/程序异常传播。
- PUB-HOST-06：Host停止取消当前tenant，不开始后续tenant，也不记录成功。
- PUB-HOST-07：公共Facade和现有lease/dispatcher真实MySQL回归；不把测试transport当真实Broker。

## 执行

先加入Host测试并执行，观察缺少公共Messaging命名空间/worker/runner/result类型的编译RED。按既有模块公共应用边界实现后：

```powershell
dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~PlatformPublisherTests|FullyQualifiedName~RetentionMaintenanceTests" -m:1
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~RetentionMaintenancePersistenceTests -m:1
```

- Modules **59 passed**：新增publisher25项，以及共用配置提取后的保留维护34项回归。
- Persistence **17 passed**：原保留维护12项及新增publisher5项。三个有限transport结果分别验证有界2/3事件只属于当前tenant、持久状态与计数一致；另两个数据库故障注入验证瞬时/永久分类、无inner/provider秘密。测试发送器仅存在测试项目，不证明真实Broker已接通。
- 手动TimeProvider触发单tenant60秒预算，验证明确tick-deadline、取消当前操作、只记录下一tenant的完成且两个timer均释放；Host停止测试验证不开始下一tenant、不记成功。
- 原transport只移动公共namespace/path，字段/结果、lease及retry业务语义未修改；原持久化测试同步引用公共边界，不访问跨模块私有持久化。
- 首次格式检查发现测试初始化布局与using排序不符，现有formatter已只修正两文件；无新增依赖、迁移、生产连接或真实transport配置。
- 最终全部修改C#的完整格式验证与`git diff --check`通过。

```powershell
dotnet test WES.slnx --configuration Release --no-restore -m:1
```

最终Architecture **29**、Modules **409**、Persistence **381**、Route **228**，合计 **1047 passed、0 failed、0 skipped**；较T07基础的1017增加30项（Host25、真实MySQL5）。前端无修改，未另跑Vitest/严格类型构建；现有HTTPS浏览器用例随Modules全套执行，不代表新增Broker或消息聚合页的E2E。

真实Broker断连/确认丢失、跨owner发布、Notifications页面与完整合同仍缺失；启用调度但未显式装配transport将明确失败，不能以本测试数关闭这些门禁。
