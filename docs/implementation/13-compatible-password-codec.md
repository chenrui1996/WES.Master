# T03 Seven兼容凭据编码与非截断新密码

- 状态：内部密码编码器已实现并验证；不是账号目录/完整登录或改密事务
- Owner：Platform；依据：[PAR-02 / VAL-04](../detailed-design/05-approved-rules-and-defaults.md)、[T03工作包](../superpowers/plans/2026-10-23-wes-development-work-packages.md)、[新密码纯策略](05-platform-session-core.md)

## 基线证据与方案

只读核对本地Seven.Net8的BcryptPasswordHasher及Infrastructure依赖，实际为BCrypt.Net-Next4.2.1、普通BCrypt、work factor12。未查询旧账号表/复制任何用户散列/读取生产秘密。用库的net462公开API在PowerShell生成**合成测试密码**的固定salt参考向量，并实测72个ASCII字节后改后缀仍能通过旧模式；相同输入在SHA384增强模式下不再别名。此现象不能在新设密码中继续保留，否则违反128 Unicode标量且不截断的已批准要求。

采用同一维护中的库，不自制KDF：旧格式明确标记`SevenBcryptV1`，只调用原Verify；新格式明确标记`BcryptSha384V1`，SHA384增强预散列后BCrypt/work12，完整UTF-8输入参与。两种模式都有相同BCrypt前缀，**不能从前缀猜模式/失败后try both**；导入和后续持久化必须保存独立受信任scheme列，未知scheme显式拒绝。该变化只用于新设/受控改密，不静默改值、trim、Unicode规范化或升级旧散列；未来轮换须增加格式版本。通用skill的ASP.NET Identity新存储建议不覆盖本项目既定Seven兼容合同，也不在此引入第二套用户系统。

旧格式验证即便正确也返回`LegacyVerifiedRestricted`，不是可生产写的正常验证。完整登录/授权owner后续必须持久标记受控改密门禁，在完成新策略及凭据事务前不授予生产写。这个enum不是HTTP响应/令牌claims，不能单凭编码器结果签发会话。旧BCrypt的72字节别名保留为显式兼容限制，不能声称本片修复所有旧账户；必须改密换格式才能消除。

## 编码器边界

新设先调用现有PasswordPolicy，必须有15..128 Unicode标量、账号不相同、泄漏与最近5个历史均Clear；Unknown或Prohibited不会生成Hash。Clear只是受信任调用者已取得证据，本片没有凭空实现泄漏服务/历史查询。验证不对旧密码强加新设长度策略，保持兼容；非法UTF-16明确Invalid，不把替代字符编码当同一密码。新/旧都不向外反射输入或散列。

导入只接受已核对的12成本、2a/2b/2y及规范BCrypt编码；非法/未知格式为明确配置/迁移错误，不当作普通错密继续尝试或运行任意cost31消耗CPU。其他成本记录需T31逐项确认后扩充受控格式支持，不直接改数字伪造密码。散列封装默认ToString及JSON不显示编码值；持久化owner未来须显式使用私有边界字段，不把散列实体当响应DTO。

## 测试与接线门禁

| CaseId | 输入/动作 | 预期 |
|---|---|---|
| IDENTITY-CREDENTIAL-LEGACY-001 | Seven公开API合成固定向量、正确/错误输入 | 正确仅LegacyVerifiedRestricted，错误Invalid，无明文替换 |
| IDENTITY-CREDENTIAL-UNICODE-001 | 72字节相同前缀不同后缀、128个非BMP标量、trim/规范化变体 | 新模式全部字符参与，旧别名仅旧模式显式保留 |
| IDENTITY-CREDENTIAL-POLICY-001 | 14/129标量、历史/泄漏Unknown或Prohibited、账号相同 | 原PasswordDecision且Hash=null，无无证据成功 |
| IDENTITY-CREDENTIAL-FORMAT-001 | 随机salt、未知scheme/cost/坏hash、JSON/格式化 | 不重用salt、不猜格式/反射散列、不忽略存储故障 |

无DB表/迁移/HTTP变化；新增依赖精确沿用Seven4.2.1并锁完整传递图。账号/凭据持久化、最近5次历史、泄漏服务、改密与全部会话撤销原子事务、未知账号等价工作量、完整风险并发与JWT/页面仍待接线，T03整体状态不变。

## 真实验证记录

1. manifest新增依赖后执行`dotnet restore WES.slnx --force-evaluate`；只有Platform直接依赖及Host/三个测试项目传递锁新增BCrypt4.2.1，没有顺带升级其他库。
2. 实现前运行`dotnet test tests/Modules/Wes.Modules.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~PasswordCredentialCodecTests`：15项中13项断言失败、2项通过，编译成功；不是以缺依赖/编译失败代替行为红灯。
3. 实现后初始15项全部通过；再补旧72字节别名限制、独立net462 SHA384固定向量，以及salt/checksum非规范尾位拒绝，最终新增17项全部通过。新设完整128个非BMP标量、后缀/空白/规范化差异、模式不可替换、坏输入及默认输出不反射散列均被验证。
4. fresh locked restore与Release build通过：0 warning、0 error。全solution串行`dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1`：Architecture29、Modules135、Persistence153、Route228，合计545 passed、0 failed、0 skipped；其中Persistence继续使用授权隔离`wes_test`。
5. `dotnet format WES.slnx --no-restore --verify-no-changes`、IDE诊断及`git diff --check`通过；两个DbContext均无pending model changes。文档/PAGE/FUNC/DTC映射与开发依赖验证随本切片复跑。

这些证据仅关闭编码器自身的内部缺口，不证明账号历史/泄漏服务的Clear证据已实现，也不构成完整T03、部署或现场H签字。
