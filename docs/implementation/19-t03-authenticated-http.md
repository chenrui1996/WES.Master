# T03 身份HTTP闭环

- 状态：实施中
- 基线：T03 / PAGE-PLAT-001/002 / FUNC-PLAT-001 / DTC-PLAT-001..003/010、VAL-04；[治理](../03-engineering-governance.md)
- 测试证据：[T03身份HTTP测试记录](28-t03-authenticated-http-tests.md)

## 装配与边界

仅`Wes:Identity:Enabled=true`显式装配身份端点。默认关闭保留Host fail-closed/readiness语义；启用必须提供连接、独立JWT签名key和challenge MAC key、issuer/audience及本地泄漏SHA256语料路径，无默认secret或自动种子。迁移由部署者受控运行，不在Host启动自动迁移。数据库连接仅在服务端配置，测试只使用wes_test。

JWT采用HS512签名，最小sub/tid/sid/iat/exp/iss/aud及精确签发wti，900秒独占上界、zero clock skew、验证签名/issuer/audience/算法；不携角色/字段授权。NumericDate envelope向上取整避免不足一秒提前失效，wti与iat一致且owner使用wti验证精确900秒，不延长授权。每个受保护请求重查当前family并使用AcceptsAccess，不依赖JWT未到期就放行。legacy允许自助会话/改密，普通权限仍由owner拒绝。当前tenant/user/department/credential资格在登录/请求/refresh中复验。token仅JSON交付给客户端内存，不放URL/storage/日志。Refresh只存SHA256；浏览器refresh通过HttpOnly/Secure/SameSite=Strict的受保护cookie，cookie携tenant/session/secret的DataProtection密文，不把raw refresh返回JSON。

不采信X-Forwarded-For或TenantId Header；peer只读已建立连接RemoteIpAddress，未装可信代理时不启用代理头。匿名tenantId只是已配置/目录有效租户的凭据路由，不是授权；用户必须通过该tenant内凭据证明身份。账号由唯一目录normalizer处理，密码不trim/normalize。所有输入有长度/枚举/非空UUID边界。返回地址只接受应用固定站内路由白名单，不接受scheme/host/双斜杠/backslash/编码逃逸。

## 端点合同

路由前缀`/api/identity`；JSON使用camelCase，所有响应Cache-Control=no-store。写请求要求HTTPS与`X-WES-Client: ops-web`或`pda`；不注册CORS，跨站浏览器不能简单表单调用cookie刷新。refresh/challenge/login缺失header也拒，测试以HTTPS。

| 方法/路由 | 输入 | 成功输出 |
|---|---|---|
| POST challenge | tenantId/account | id/expiresAt/imageDataUrl（PNG，不输出答案） |
| POST login | tenantId/account/password/client(Web=0,Pda=1)/challengeId?/answer?/returnUrl? | accessToken/expiresAt/requiresPasswordChange/returnUrl；同时设置refresh cookie |
| POST refresh | 无body，受保护cookie | 同login token形状；只在durable rotation成功后换cookie |
| GET me | Bearer | tenantId/userId/sessionId/requiresPasswordChange/credentialVersion/current grants（不包含hash/token） |
| GET sessions | Bearer | 当前自有family安全投影；无refresh digest或token |
| POST activity | Bearer | 204，仅真实用户交互调用，refresh/poll不延长空闲 |
| POST logout | Bearer | 204，当前自有family撤销/audit且清cookie |
| POST password | Bearer + expectedVersion/oldPassword/newPassword | 204，服务端读取泄漏证据并交owner复验历史/旧密，成功清cookie必须重登 |

错误响应使用既有ProblemDetails及correlation，匿名认证失败统一401，不暴露未知账号/锁定/验证码gate差别。获取验证码可始终提供，不需要通过错误类型猜gate；图片是PNG像素，不含答案文本/metadata。签发同样消耗可信source预算，120秒一次绑定不变。验证码不是替代密码、速率预算或无障碍身份验证；无障碍替代通道需要另行批准，不虚构支持。

自有`GET sessions`返回安全array；管理`GET admin-sessions`返回`{authorized,rows}`，要求当前`platform.sessions.read`。共同支持state=active/idle/expired/revoked、创建时间[from,to)、offset/limit（默认50，1..100）；管理查询另支持userId。State/UserId筛选须拥有对应字段，防止隐藏字段成为查询oracle。授权范围先于分页，管理行按真实用户当前部门逐行字段投影。`POST sessions/{id}/revoke`携expectedVersion；当前家族、本人其他家族与管理员目标均重新核对真实归属/部门及版本，不以客户端departmentId或空部门伪造范围。

## 泄漏检查与秘密

采用本地SHA256大写逐行语料，不向第三方发送密码/摘要。路径/格式/读取失败明确Unavailable，不回Clear；语料非空，限制输入与行格式，原文不进入日志。校验全密码UTF8 bytes，不截断；语料来自部署者的经审核快照，需在runbook记录版本/来源/更新时间。调用方不能把Clear作为HTTP输入。测试用临时合成语料，不代表真实泄漏语料已取得。

## 测试与发布

先编写HTTP测试并观察可编译缺端点/未签发等行为红灯；使用真实MySQL，随机tenant与测试生成密钥/语料，仅fixture清理。覆盖错误同形、成功cookie/JSON秘密边界、JWT899/900秒/签名/租户/issuer/algorithm、refresh重放/撤销、idle边界/真实activity、旧格式改密限制、改密泄漏/历史拒绝、header/HTTPS/代理伪造、站内跳转及取消/provider故障。前端对接具名页面，不以API测试替代浏览器验收。

启用身份不解禁业务/设备控制或现场H，readiness仍包含尚未装配的业务门禁。生产需要真实secret装配、DataProtection持久key ring、审核泄漏语料与DBA迁移；合成测试数据不作为投产许可。实际证据完成后追加。

持久cookie key使用固定应用名和purpose；Production要求已存在的绝对KeyRingPath，当前Windows适配器以运行服务身份DPAPI保护磁盘key。Development测试可显式使用ephemeral provider，不能据此宣称生产恢复。首次本地目录建立仅开放双门禁的非HTTP交互进程，不提供匿名注册；完整输入、备份/轮转、反向代理限制见[部署与恢复手册](21-t03-t04-deployment.md)。HTTP/拒绝/本地Provisioning的66项Release测试、受控HTTPS Chromium验收及全solution回归结果见独立[测试记录](28-t03-authenticated-http-tests.md)；这些软件证据不解除生产门禁。
