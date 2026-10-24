# T03 验证码耐久一次消费

- 状态：实施中；内部签发/消费切片已实现并验证，不是可用验证码页面或完整T03
- Owner：Platform；依据：[PAR-02 / VAL-04](../detailed-design/05-approved-rules-and-defaults.md)、[PAGE-PLAT-001 / DTC-PLAT-001](../detailed-design/platform-and-security.md)、[账号风险记录](10-durable-account-login-risk.md)

## 实现思路

验证码从创建起120秒有效，窗口为`[CreatedAt,CreatedAt+120s)`。同绑定的第一次提交无论答案正确/错误都消费；重放、未知ID、过期、跨租户/账号/来源统一Rejected，不给客户端枚举细节。绑定不符不消费别人的挑战，避免跨账号恶意销毁。原始答案不入库/日志/审计/普通DTO；不提供绕过验证码的成功默认值。一次消费只证明该挑战提交正确，不是登录成功或会话签发许可。

新表放既有强制租户的PlatformDbContext，复合TenantId/Id，包含账号/可信来源摘要、UTC微秒CreatedAt、ConsumedAt、Version及AnswerMac。验证码ID用密码学随机UUID，答案用CSPRNG生成6位不混淆大写字母/数字；此内部编码不改变密码策略。期限固定120秒，拒绝坏schema及早于创建/消费的时钟。存储HMAC-SHA256而非短答案的裸散列，避免仅获表即可离线穷举。MAC用显式版本域和固定长度绑定（tenant/id/account/source）及完整答案字节，定时安全比较；不trim/改大小写以免产生两套答案规则。

HMAC密钥必须由后续受控安全配置装配提供，至少256位；构造即复制，不带默认值、不写配置样例。所有实例同一已批准活动密钥；轮换需暂停签发、等待120秒窗口或另行实现key ring，不能临时随机密钥冒充跨实例持久验签。密钥对象释放清零。密钥不可用/配置非法直接失败，不回退明文或无验证码。本片测试使用自造非生产密钥，无真实秘密输出。

## 事务与返回

签发在SaveChanges成功后才交给**服务器侧受控渲染器**包含答案的对象；其格式化固定REDACTED，并明确不作为HTTP返回模型。尚无渲染器/图片/无障碍交互与HTTP路由，不将答案直接JSON返回、不用前端生成答案；完整验证码投用及抗自动识别效果仍未验收。新挑战不暗中撤销所有旧挑战，各ID各一次且各自120秒过期；重取策略由完整页面/限流owner接线。

消费在同一owner SaveChanges中更新ConsumedAt+UUID Version，提交后才返回Accepted。两个正确答案并发最多一个Accepted；错误先提交会烧掉挑战，正确竞争者也只能Rejected，不对CAS冲突自动重试/签发第二份有效事实。未知数据库错误/取消传播；提交结果不确定不承诺没有消费。自己独占干净上下文，禁止显式和环境事务防止后续密码错误回滚挑战；失败finally清自己的图，不动调用者原图。失效/重放返回固定code日志，不写ID、摘要、答案、MAC或driver异常。

来源准入必须先于后续验证码消费，错/缺验证码也计来源预算；验证码失败不记为密码失败。完整身份owner仍须把当前账号风险、凭据验证/禁用、失败不漏记及会话签发做并发一致接线，不能将内部bool/enum作为客户端可自报授权。当前不会装配公开登录路由，TenantContext不改。

## 测试与发布门禁

| CaseId | 步骤/反例 | 预期 |
|---|---|---|
| IDENTITY-CHALLENGE-LIFETIME-001 | 119秒/119.999999秒/120秒，UTC微秒往返 | 前者可消费，120秒拒；期限不延长 |
| IDENTITY-CHALLENGE-ONCE-001 | 正确/错误/空答案后重用正确答案 | 第一次正确仅一次Accepted；错误/空值也消费 |
| IDENTITY-CHALLENGE-BINDING-001 | 同ID不同租户/账号/可信来源、未知ID | 统一Rejected，原挑战不被跨绑定销毁 |
| IDENTITY-CHALLENGE-CRYPTO-001 | 固定独立HMAC向量、绑定/答案变化、短key、key释放/复制 | 无裸答案、绑定不可替换、非法配置不回退 |
| IDENTITY-CHALLENGE-CONCURRENT-001 | 保存屏障并发正确/正确、错误/正确 | 最多一次正确Accepted且一版消费，失败者不获得有效事实 |
| IDENTITY-CHALLENGE-FAULT-001 | 签发/消费保存失败、预取消/保存中取消、脏context/外部事务 | 不伪造交付/Accepted；坏schema/时钟明确故障，不泄漏秘密 |
| IDENTITY-CHALLENGE-MIGRATION-001 | Platform增量迁移/模型/清理检查 | 仅新表、tenant复合键与过滤、CAS；不触碰来源owner/旧表，清理限fixture随机租户 |

总行数维护/容量、签发入口限流、防恶意消耗、渲染器安全/可访问性、HMAC安全存储和多实例轮换、真实HTTP/页面E2E仍是投用门禁。测试只能使用授权wes_test，未修改生产连接或导入旧账号。

## 红绿及实际证据

- 领域5项首先全部行为红灯（无消费、无expiry/绑定/时钟保护），实现后转绿；保护/交付3项先因空MAC、无key校验及答案格式化泄漏红灯，实现后转绿，再补非法绑定/Unicode/输入界限，最终9项领域/密码学测试通过。
- HMAC独立向量由PowerShell内置SHA256/HMACSHA256计算，固定测试键0..31，固定tenant/id/账号/IPv4字节及ABC234，预期`1CFFFA264870C6E029FF0F69D1C98508DCF9130B8A38AFED34FBCB8273538591`；同时验证所有绑定、答案、密钥变化都会改变MAC，调用者修改原key数组不改变已装配key，Dispose后显式拒绝。
- 迁移范围先因空增量红灯，生成/人工审查后仅新增本owner挑战表。真实MySQL handler初轮18项失败/0通过（无持久行/消费、并发不接受、无取消/事务保护）；实现后全部转绿，再补错误提交强制先赢、长/非法Unicode答案、缺密钥/绑定和新保护实例重启，最终24项迁移/真实MySQL测试通过。
- 强制时序测试使错误答案在两个竞争者都已读到旧版本后先提交，正确竞争者随后真实CAS失败，两者均Rejected，后续正确重放仍Rejected；没有用进程锁代替数据库事实。
- Oracle MySql.EntityFrameworkCore10.0.7/EF10.0.7/MySql.Data9.7.0，真实MySQL8.0.12，仅wes_test。无新增依赖/明文答案列/Host路由/自动迁移；清理只限fixture两个随机租户的挑战行。
- 全solution Release：Architecture29、Modules118、Persistence138、Route228，共513 passed、0 failed、0 skipped；构建0警告/错误。全solution格式、两个owner快照及设计/计划文档检查通过。513不代表完整登录链路、验证码视觉安全或64个页面完成。

```powershell
dotnet build WES.slnx --configuration Release --no-restore
dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-build --no-restore --filter FullyQualifiedName~LoginChallenge
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-build --no-restore --filter FullyQualifiedName~LoginChallenge
dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1
dotnet ef migrations has-pending-model-changes --context PlatformDbContext --project src\Modules\Platform\Wes.Platform.csproj --configuration Release --no-build
dotnet ef migrations has-pending-model-changes --context LoginSourceDbContext --project src\Modules\Platform\Wes.Platform.csproj --configuration Release --no-build
dotnet format WES.slnx --no-restore --verify-no-changes
```

应用/回滚数据库仍需对应环境许可；运行中不得删表回滚以重置消费事实。跨实例秘钥轮换/灾备须保留同一活动key或确认所有旧挑战过期，禁止仅更换单实例key后声称无损恢复。
