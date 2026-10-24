# T03/T04 当前授权的用户禁用与全部family撤销

- 状态：实施中；Platform内部管理用例，不是完整用户目录/公开API
- 依据：[平台用户/会话设计](../detailed-design/platform-and-security.md)、[PAR-01/02及VAL-04](../detailed-design/05-approved-rules-and-defaults.md)、[登录协调共享CAS](15-coordinated-password-login.md)

## 有界范围与权限

复用当前RBAC、可信TenantContext、凭据Enabled/UUID版本、Session撤销与固定audit。提供`DisableAccountHandler`，只禁用指定本租户user的凭据，不修改密码、角色、组织、设备命令或历史账本，不自动启用/解锁或创建账号。调用者带target UUID、expectedVersion和correlation；密码/token不属于请求。

每次调用以CurrentAuthorization读取当前有效自有family及当前角色，要求`platform.users.write`对目标owner的`Enabled`字段授权。支持All与Own的已有语义；**没有权威部门目录的当前切片不猜部门**，Department范围不匹配而拒绝，后续目录owner接线后再验证。缺会话、跨租户context、无字段权限、空范围或legacy受限family均不能借管理员页面/claims绕过；即使目标已经禁用，也必须先过当前授权。当前权限读检查是每请求的权威检查，非对全部并发权限事务的全网瞬时串行化声明。

## 状态、幂等与原子性

读取本租户目标凭据与该user全部family；不存在返回安全Rejected。若Enabled=false且全部family已撤销，返回Completed、不更版本/重复审计；此时同动作旧版本重试不引入新写。其他情况必须匹配expectedVersion；禁用或修复“已禁用但仍有未撤销family”的安全状态都touch同一credential版本。

在**显式本地事务**中提交Enabled=false、UUID新版本、所有未撤销family的撤销，以及一条AccountDisabled和逐family SessionRevoked版本链审计。已撤销family不改、不重复审计；其他用户/租户不变；family可idle/expired，仍显式终结。自禁用允许的前提是Own/All确有Enabled授权，成功后自己的管理session也撤销，不能再管理或改密。缺用例不增加“最后一个管理员”自创业务限制。

login/禁用以同一credential CAS竞争：禁用先提交，旧snapshot登录整体回滚、不发secret；login先提交，旧禁用返回VersionConflict，不自动沿用旧授权重试。管理员显式重新取得版本与当前权限后重试，查询会包含新family并撤销。refresh先提交也会使family CAS冲突，调用者重新核验后重试；禁用先提交则refresh重读Inactive。禁用成功后下一次当前授权返回空grants、正确密码也不能签发新family。

拒dirty图、显式/ambient外层事务和AutoTransactionBehavior.Never。真实SQL/取消故障向上明确传播；只捕获CAS为VersionConflict；提交丢响应不承诺未写，不补偿启用或重放密码。不借用一个隐式SaveChanges事务推断安全原子性。日志只有固定code，audit只UUID/版本/时间，无账号/密码/hash/token。

## 测试合同

| CaseId | 输入/动作 | 预期/副作用 |
|---|---|---|
| IDENTITY-DISABLE-OWNER-001 | 当前授权、正确版本、多个family、另一用户/租户 | 仅目标禁用/全部family撤销，与固定审计原子；重复同效果不重审计 |
| IDENTITY-DISABLE-SCOPE-001 | 无权限/无Enabled/Own他人/缺部门/legacy/锁屏/跨租户/旧版本 | 拒绝或明确冲突，目标/审计不变；后续撤权立即拒新管理调用 |
| IDENTITY-DISABLE-CONCURRENT-001 | 两禁用、禁用与login/refresh两顺序 | CAS不丢安全状态；显式重试包含先赢的新family；最终禁用后无可用session |
| IDENTITY-DISABLE-FAULT-001 | 真实audit1062、取消/DB异常、dirty/外层事务、坏时钟 | 整体回滚或明确失败；不启用回退、不泄露秘密、不清调用者图 |

不新增schema迁移，AccountDisabled为固定审计字符串enum新项，既有值顺序保持不变；两个模型快照须仍一致。真实验证只在已授权`wes_test`，生产管理/发布/现场H不随此内部用例解禁。实际红绿/回归在验证后追加。

## 实测证据与复跑

初始可编译行为桩18项中9失败、9通过；失败覆盖成功禁用/孤留family修复/自禁用、旧版本冲突及实际SQL故障/取消/守卫，不把编译失败算红灯。实现后18项全部通过。随后扩为22项禁用测试，并在登录测试补3项：禁用与login/refresh两方向真实提交竞争、两个禁用竞争、已撤销family不重审计/过期family终结，以及禁用后真实密码登录/有存活family时改密仍拒绝。两类合并52项全部通过；测试屏障只调度SQL顺序，不伪造CAS异常。

禁用与refresh采用credential→有序family版本写入，遇到先赢refresh不自动重试旧授权；已禁用孤留family修复仍旋转credential版本。SetEnabled现在每次受控调用均touch版本，无写幂等由owner判断，不能靠同值UPDATE的affected-rows猜测并发。所有安全写与固定audit用显式本地事务；真实audit主键1062保持凭据/全部family不变。

复跑（仓库根目录，连接只由测试装配内存装配到wes_test）：

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~DisableAccountPersistenceTests|FullyQualifiedName~LoginAttemptPersistenceTests"
dotnet build WES.slnx --configuration Release --no-restore
dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1
```

fresh Release build为0 warning/error；完整回归Architecture29、Modules145、Persistence236、Route228，共638 passed、0 failed、0 skipped。首次全量运行期间同时运行formatter，既有1秒依赖探针恢复断言出现Unhealthy；不修改生产超时或断言，在无并行formatter条件下完整复跑638全绿。格式检查已通过；模型/设计映射与DAG作为提交前独立门禁执行。

回滚不启用已禁用账号、不恢复已撤销family或删除审计；代码前滚修复优先。本片只是T03/T04内部管理用例，不交付用户目录/部门归属、启用/创建/重置、受权安全事件查询/保留治理、HTTP/JWT或页面E2E，不标整包完成。
