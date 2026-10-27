# T08 当前授权主数据创建测试

- 状态：实施中；关联[设计](125-t08-current-authorized-master-data-creation.md)。

| 用例 | 步骤与预期 |
|---|---|
| WMS-CREATE-01 | 当前Own/Departments/All完整字段创建默认禁用仓/位，永久operation精确重放，creator仅为可信actor |
| WMS-CREATE-02 | read不能代替write、缺字段/错字段case/错部门、父仓无read或foreign tenant拒绝；无资源/事实/库存效果 |
| WMS-CREATE-03 | 另一owner撤字段/角色/成员/session后重复operation仍拒绝且不泄漏旧Id/Version |
| WMS-CREATE-04 | schema拒绝/不可用/取消、缺schema依赖、脏owner和错误上下文不成功；没有default-success adapter |
| WMS-CREATE-05 | 原创建并发/唯一键/幂等/事务回滚回归，authority或DB故障显式安全错误，不用失败变空成功 |

## 执行记录

1. RED：先增加公开writer用例，编译缺`WmsMasterDataWriter`，未跳过或以空套件计通过。
2. 初版14项真实MySQL/Platform联动中**12 passed / 2 failed**：测试构造Own grant时误带非空Departments，既有Platform规则在seed时正确拒绝。只修测试metadata：Own使用空Departments；不放宽角色校验。错部门反例保留完整Code/Name字段，确保测的是scope而非缺字段。
3. 追加上下文、输入、schema/authority违约、真实跨tenant父仓、权限不可互借、Set比较器、撤权后重放、并发同operation/同code及安全DB错误边界，本片新增**38项真实owner**。当前Own/Departments/All创建与精确重放通过，creator仅来自可信actor；默认禁用、无库存entry或跨owner审计写。schema使用明确标记的合同fixture，不是真实Pack签字或Host可用适配器。
4. 最终所有主数据创建/读取/目录/授权联合**124 passed**（原86+新增38）；相关领域/配置/只读HTTP/真实WMS HTTPS Chromium **50 passed**；完整Architecture **29 passed**，合计**203 passed / 0 failed / 0 skipped**。Release全solution构建**0警告/0错误**，新公开API没有Domain/Infrastructure泄漏或跨模块私有依赖。
5. 本片未修改前端、HTTP映射、配置、模型、迁移或依赖；未重新执行完整Vitest/全部.NET或其他两套浏览器，不把前片306客户端或1399完整.NET结果冒充本片新结果。既有WMS真实浏览器和只读HTTP继续通过，包括POST/PUT/DELETE不可用，不因增加应用writer暗中打开写入口。

会话TRX保存在`wms-masterdata-creation-access-validation`的first/owner/final目录；初轮seed失败保留。并发公共入口是两个独立writer/当前authority调用；事务核心的精确冲突/回滚窗口由既有同组真实MySQL回归覆盖，不把普通并发调用冒充所有调度顺序证明。

## 复跑

```powershell
dotnet build WES.slnx -c Release --no-restore
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~WmsMasterDataTests"
dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~MasterData|FullyQualifiedName~WmsCatalog|FullyQualifiedName~WmsWarehouseCatalog|FullyQualifiedName~WmsDetails|FullyQualifiedName~RealBrowserReadsWarehouse"
dotnet test tests\Architecture\Wes.Architecture.Tests.csproj -c Release --no-build --no-restore
```

真实浏览器复跑需要已有受控客户端构建；本片无客户端变化。真实Pack adapter、Host写装配/表单、编辑启停、占用/预约/锁/活动证据、容器/Outbox、库存/流水/快照导出、其余规则与父DTC、五包整体均仍未验收。
