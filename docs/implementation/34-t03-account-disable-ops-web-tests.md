# T03/T04运维用户页凭据禁用测试

- 状态：通过；全solution Release及Ops Web测试/构建均已复验。
- 范围：受权凭据版本的持久层投影、真实HTTP合同及Ops Web确认动作。
- 测试数据库：仅已授权隔离MySQL `wes_test`，随机tenant/用户及合成凭据fixture。

## 测试合同

- `ReadUsers_ProjectsCredentialVersionOnlyWhenGranted`：字段未授权时JSON仍有空版本位但不返回实际版本；当前目标读权限增加`Credential`后，只投影该凭据UUID版本。
- `CredentialDisableHttpChecksVersionAndRevokesCurrentFamily`：真实HTTP响应验证安全投影、当前`Enabled`写权限、旧版本冲突、成功与旧请求重复；确认两条真实family已撤销且旧访问token被拒。
- `UsersPage.spec.ts`：用户必须先确认；请求精确使用`credentialVersion`而非目录`version`；204成功后重新读列表；缺当前`Enabled`写权限时不显示页面动作。
- `identity-api.spec.ts`：凭据禁用动作不发送DirectoryCommandReceipt专用的`Idempotency-Key`。

## 实际结果

- `ReadUsers_ProjectsCredentialVersionOnlyWhenGranted`：1 passed，0 failed。
- `DirectoryPersistenceTests`：22 passed，0 failed。
- `CredentialDisableHttpChecksVersionAndRevokesCurrentFamily`：1 passed，0 failed；HTTPS Host + `wes_test`真实owner。
- `UsersPage.spec.ts`及`identity-api.spec.ts`定向：21 passed，0 failed；全Ops Web：75 passed，0 failed。
- `npm run build`：vue-tsc严格类型检查与Vite生产构建通过。
- 全solution Release：Architecture 29、Modules 218、Persistence 317、Route 228，共**792 passed、0 failed、0 skipped**。
- 所有修改的C#源/测试文件格式校验及`git diff --check`通过。

## 复跑命令

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~ReadUsers_ProjectsCredentialVersionOnlyWhenGranted"
dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~CredentialDisableHttpChecksVersionAndRevokesCurrentFamily"
npm test -- --run src/features/platform/UsersPage.spec.ts src/features/identity/identity-api.spec.ts
npm run build
```

实现前持久层测试因JSON缺少`credentialVersion`属性而失败；Vue测试因页面没有禁用动作而失败。修复后各自通过。

## 未覆盖

本测试不替代人工200%缩放、真实PDA、生产密钥/数据库/代理配置、DBA审查或现场H验收；不代表T03/T04整包完成。
