# T08 当前主数据详情测试

- 状态：已验收（仅列明的应用详情读取范围）；关联[设计](117-t08-current-master-data-reads.md)。

| 用例 | 步骤与输入 | 必须断言 |
|---|---|---|
| WMS-ACCESS-01 | 真实Platform活动会话/All完整字段，创建仓/位后查询 | 精确Id/字段/版本/固定归属；默认禁用不冒充可分配 |
| WMS-ACCESS-02 | Own读取真实自有/别人创建事实；客户端不提供creator | 自有Success，别人Forbidden，字段不可混用不同资源 |
| WMS-ACCESS-03 | Departments读取同部门creator，再由另一owner移部门 | 同一读取服务下次立即Forbidden，不能复用旧部门 |
| WMS-ACCESS-04 | 当前角色移除字段/禁用、撤成员、撤销session | 旧服务下次拒绝，不缓存完整字段或角色 |
| WMS-ACCESS-05 | 错tenant/actor/session、非法Id、缺creator、异tenant真实资源 | 明确拒绝/未找到，不跨tenant查私表，不泄露投影 |
| WMS-ACCESS-06 | 缺策略、错误字段比较器、授权期间取消、真实DB读取故障 | 明确异常或拒绝、取消不交付、安全DB错误，无成功fallback |
| WMS-ACCESS-07 | 查询前后核对创建事实/账本/审计 | 普通详情无写副作用；WMS不引用Platform私有类型 |

## 执行记录

- 首轮测试编译确认尚缺MasterDataSubject/IMasterDataAuthorization/MasterDataAccess（CS0246），此为缺合同RED，不计为运行时用例通过。
- 新增**22项真实MySQL用例**通过。WMS与Platform均使用已授权wes_test、随机tenant隔离，真实创建事实/目录/角色/会话；公开WmsMasterDataScope与PlatformScope桥接亦实测。受控authority仅用于非法比较器、null结果和迟到取消反例，不冒充真实授权证据。
- 与既有主数据创建、账本/预留、Platform当前授权/审计导出共同运行 **159 passed、0 failed、0 skipped**；定向领域/授权 **32 passed**；完整架构 **29 passed**，共 **220项定向与架构验证通过**。全solution Release构建 **0警告/0错误**，限定文件格式及diff检查通过。
- 本片无新迁移/依赖/前端变化，没有重复全套.NET或独立240项Vitest；前一提交的完整1313项结果属于其对应源，不把它改称当前全套结果。TRX保留于会话`files\wms-masterdata-access-validation\{targeted,modules}`。

## 复跑

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~WmsMasterDataTests|FullyQualifiedName~WmsLedgerTests|FullyQualifiedName~PlatformAccessPersistenceTests" -m:1
dotnet build WES.slnx --configuration Release --no-restore -m:1
dotnet test tests\Architecture\Wes.Architecture.Tests.csproj --configuration Release --no-build --no-restore -m:1
dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName~AuthorizationTests|FullyQualifiedName~AccessRoleTests|FullyQualifiedName~MasterDataTests" -m:1
```

所有用例组均在上述范围passed，不代表父DTC整体通过。尚无Host生产适配器注册、HTTP/API/页面、目录列表、创建当前授权、编辑启停/占用/容器绑定或Outbox；不关闭T08或其他四包，不改变现场安全/生产许可。
