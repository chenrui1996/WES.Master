# T03账号禁用认证HTTP测试

- 状态：切片通过；T03/T04总体仍partial。
- 范围：将已验证的`DisableAccountHandler`经认证平台路由公开；不改变用户档案编辑的目录版本合同。
- 数据库：仅使用已授权隔离MySQL `wes_test`及随机tenant/目标；密码使用合成固定fixture。

## 红绿与覆盖

先新增HTTP行为测试并运行，观察目标路由未注册时返回404（测试预期412），证明测试确实能捕获缺失adapter；接入路由后该测试通过。

`IDENTITY-DISABLE-HTTP-001`覆盖：

- 一个已认证但没有`platform.users.write`目标`Enabled`字段权限的调用返回403，凭据不变。
- 过期/错误凭据版本返回412，凭据不变。
- 正确版本返回204；目标凭据禁用，单条`AccountDisabled`审计提交。
- 同一禁用动作以旧请求版本重放仍返回204，不增加重复审计。
- 目标账号的两个真实HTTP登录family均撤销；原访问token随后被当前family校验拒绝。
- 测试请求经过HTTPS Host、`X-WES-Client`和Bearer认证，调用实际MySQL owner，不mock handler/数据库。

## 复跑命令与结果

```powershell
dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~CredentialDisableHttpChecksVersionAndRevokesCurrentFamily" --logger "console;verbosity=normal"
```

结果：**1 passed，0 failed**。日志仅包含固定拒绝原因和HTTP状态，不包含账号、密码、token或凭据版本。

当前UI已有目录用户编辑的启停控件，其expectedVersion是目录用户版本并走目录事务；本新增HTTP adapter要求凭据版本，故未把列表行版本当成该请求版本，也未增加重复UI动作。独立owner持久层CAS/并发/故障证据仍见[禁用owner实施记录](16-audited-account-disable.md)。

## 边界

本记录只证明隔离库中的软件HTTP闭环，不证明生产权限策略/部署、生产迁移批准、审计保留与清理、人工缩放、真实PDA或现场H门禁。该结果不改变T03/T04 partial状态。
