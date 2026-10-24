# T03/T04 受权登录安全事件查询

- 状态：内部只读投影已验证，后续身份HTTP/具名审计页面已接入；不是导出/保留治理交付
- 依据：[PAGE-PLAT-008/FUNC-PLAT-006/DTC-PLAT-008](../detailed-design/platform-and-security.md)、[已实现登录事件owner](15-coordinated-password-login.md)、[当前受权审计查询](08-platform-access-settings-audit.md)

## 范围与选择

复用`platform.audit.read`及CurrentAuthorization/RolePolicy，不新增并行权限体系或返回持久实体。独立QueryLoginEvents查询已持久LoginSecurityEvent，区分Authorized=false与受权空结果，按`[from,to)`、时间/Id升序、offset及1..100 limit提供有界页。可按有限Kind、非空CorrelationId筛选，筛选字段也必须有权限，不能用被隐藏字段作查询oracle。所有过滤在分页前执行，租户来自可信context，调用方不能传tenant或账号/来源digest作为筛选。

登录事件没有不可变actor/部门归属：未知账号和失败事实只有假名化digest，不能把当前凭据映射或请求账号猜成历史身份。此切片只采纳All范围grant，Own/Department均不扩大；已有普通业务审计Own语义不变。后续若增加经审查的不可变主体关联，应另立测试/兼容合同，不用“失败记录无actor”伪造用户。

必需Id/OccurredAt字段，其他Kind、CorrelationId、AccountDigest、SourceDigest、SessionId分别按当前字段白名单投影，缺权限为null。Id/时间及筛选权限不可从Own或其他permission拼入All。digest可关联、可推算，是敏感假名化数据不是匿名标识；默认ToString须遮蔽，授权JSON才可包含获准字段，不返回账号、密码/hash、IP原文、token、全文请求或EF实体。

## 失败、资源与边界

非递增时间、负offset、limit越界、未知Kind及空correlation明确抛参数异常，不静默夹紧。当前会话/角色每请求读取，legacy受限/idle/撤销family拒，后续撤权立即拒新查询；不声称所有并发授权事务瞬时串行化。拒跨租户context、dirty图、显式/ambient外层事务，以免在旧snapshot下冒称当前授权；不清除调用方图。查询AsNoTracking且只投影，成功/拒绝/故障不写安全事件/审计或更改会话活动。取消及真实provider错误向上传播，不返回假空页；日志只有固定拒绝code。

不新增表/索引/迁移；沿用tenant/time/Id索引。offset页不是固定快照导出，追加历史事件时不承诺跨页快照一致性。365天保留、归档/容量治理与导出审计未在本片实现，不增加删除接口，也不将查询成功当作这些能力通过。

## 测试合同

| CaseId | 输入/动作 | 预期 |
|---|---|---|
| IDENTITY-LOGIN-EVENT-QUERY-001 | 两tenant、同时间多Id、区间边界、offset/limit、Kind/Correlation | 租户/时间/过滤先于分页，稳定顺序、上界排除、无tracking或副作用 |
| IDENTITY-LOGIN-EVENT-AUTH-001 | 无权限、Own/Department、缺必需列、跨scope字段、legacy/idle、角色撤销 | 明确拒绝；可用All字段独立裁剪，不用隐藏字段筛选泄露存在性 |
| IDENTITY-LOGIN-EVENT-FAULT-001 | 坏参数/跨租户、取消、真实SQL坏表、dirty/外层事务 | 显式异常、无成功形空结果、不清调用者状态 |

先运行可编译行为桩观察实际断言失败，再实现并在授权wes_test验证；受权HTTP/具名页面由后续集成提供，整体证据见[身份HTTP记录](19-t03-authenticated-http.md)及[运维页面记录](20-t03-t04-operations-client.md)。生产/现场H门禁保持不变。

## 实测证据与复跑

初始行为桩22项中12失败、10通过；真实断言覆盖受权结果/过滤/字段裁剪、参数/取消及独立unit守卫，编译错误不计红灯。实现后22绿，随后增加实际105条匹配记录的100条默认页/5条后页，以及Own必需列不能补全All的回归；最终24项真实MySQL查询测试通过。坏表仅在测试命令interceptor中重定向SELECT，取得真实1146，不创建/删除表，不伪造数据库异常。

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~LoginEventQueryPersistenceTests
dotnet build WES.slnx --configuration Release --no-restore
dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1
dotnet format WES.slnx --no-restore --verify-no-changes
```

最终fresh Release构建0 warning/error；全量Architecture29、Modules145、Persistence260、Route228，共662 passed、0 failed、0 skipped。format与IDE诊断通过；模型/文档映射/DAG在提交前独立复验。100是实际取页阈值，不只测试参数101被拒；过滤结果在源数据中确有其他tenant/结果/边界记录。

本owner片仅增加内部查询，无迁移、无新默认grant；后续Host已接入受权查询，客户端仅展示当前服务端投影。回滚不删除安全历史，不影响现有登录/禁用写owner。365天保留/容量治理及导出审计仍缺；不能将这些24项测试、单项HTTP/页面集成或全量回归冒称完整父DTC/T03/T04交付。
