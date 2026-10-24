# T03 耐久登录协调与会话签发栅栏

- 状态：内部登录协调已实现并验证；HTTP/JWT和具名页面由后续集成切片接通，见[身份HTTP](19-t03-authenticated-http.md)与[Ops Web页面](20-t03-t04-operations-client.md)
- 依据：[T03](../superpowers/plans/2026-10-23-wes-development-work-packages.md)、[PAR-01/02及VAL-04](../detailed-design/05-approved-rules-and-defaults.md)、[改密与既有family撤销](14-durable-password-change.md)

## 范围与顺序

复用已有来源预算、账号风险、挑战MAC、密码编码和Session，新增一个协调owner，而非让HTTP分别调用几个Accepted接口再自行签发身份。调用者仅提供受信tenant数据库、账号owner已规范化值、可信连接来源及支持的client；不收客户端“密码已验证/验证码有效”布尔值、role/menu集合或任意目标user。后续集成现已提供账号规范化adapter、目录身份、可信连接来源HTTP、JWT和具名页面；生产可信代理拓扑及环境授权仍是独立门禁。

1. 先独立提交来源预算；所有账号、错验证码/暂锁/未知账号同样消耗来源预算。超过30/60秒不继续密码核验。
2. 在不查账号存在性的情况下确保一个同租户账号risk行存在。首次冲突仅限本行真实1062，重读最多8轮；数据库/取消错误不映射成正常登录拒绝。
3. 有提交的挑战则先独立消费并commit，错误/重放直接拒绝；没有提交则按当前risk决定是否需要挑战。成功挑战仅属于此一次用例调用，后续CAS失败不重试核密、不跨调用复用；失败、取消或未知提交响应不恢复已消费挑战/来源预算。
4. 读取当前risk并检查锁/验证码门槛，再查凭据。真实旧/新模式都运行work12；未知账号用固定合成增强模式dummy做同量级BCrypt工作，不声称网络级严格恒时。禁用账号也走密码工作但永远拒绝。缺账户、错误密码和禁用统一外部Rejected；内部事件保留安全原因，响应不返回阈值/存在性。
5. 真实错密在risk当前CAS冲突后只合并**已准入并实际验证过的失败事实**，不重跑密码/挑战。滚窗仍最多5项；已到第5锁时后来的并发在途失败保留独立安全事件，计数饱和5且不延长锁，不能丢失失败事件或反复延长锁。最多8轮，耗尽明确故障，不能伪装“已记录”。这仅是owner内部完成核验后的路径，不替换原公开risk Read/RecordFailure的重新准入合同。
6. 正确密码在同一事务CAS更新risk版本与credential版本，再插入session/digest和成功安全事件。risk变化使成功拒绝、不自动重试；改密/禁用变化使旧密码签发事务CAS失败并完整回滚。login先赢则后来的改密/禁用与同一凭据版本冲突，需明确重试；其重试扫描必看见新family再撤销。只有真正提交成功才返回server-side交付secret，默认JSON/ToString不输出secret。

## 持久状态及当前授权

- `PasswordCredential.Enabled`：新可信开户/import默认启用；本轮迁移对**已有行**默认false，失败关闭，不能把未知历史状态默认为启用。后续管理员操作须有当前权限与审计，本片不提供匿名启用。登录签发touch同一个UUID Version，不改密码ChangedAt/历史，不把正常登录当改密。
- `Session.RequiresPasswordChange`：legacy正确创建受限family，当前授权返回空grants；只可走当前主体会话/登出/旧密受控改密等独立自助接口。新格式正常family为false。迁移已有session默认true，须重新认证，不升级旧令牌权限。refresh仍沿原family保留这个持久限制，不从客户端claims覆盖。
- `platform_login_events`：tenant/UUID主键、账号/来源假名化digest、correlation、有限结果、UTC微秒和可选session ID。无密码、答案、hash、raw token、原请求或自由payload；与对应失败risk/成功session同SaveChanges。只追加，通用EF写保护禁止改/删。没有伪造unknown-user actor/tenant-zero。

新增加两个明确列与一个owner事件表，不能AlterDatabase或修改其他业务表。事件包含可推算假名化值，不宣称匿名；生产访问、365天安全审计保留/容量清理及受权查询仍须实现。当前会话grants拒legacy，未来高后果入口也必须复验，不把JWT自带旧permission当权威。

## 故障、取消与测试

要求两个context均clean、无显式/ambient外层事务，禁止Platform AutoTransactionBehavior.Never。risk首次初始化、失败事实合并和成功签发均由本用例**显式开启并提交owner事务**，不依赖Provider对混合批处理的隐式事务推断。finally仅清自己图；提交异常原样传播，日志只有固定code。安全事件写入失败时本地对应risk/session也不能部分成功；已独立提交来源/挑战仍保留。来源限流/挑战/锁拒绝也须耐久记录安全结果，不能因事件失败返回成功形状。

| CaseId | 动作/输入 | 必须验证 |
|---|---|---|
| IDENTITY-LOGIN-OWNER-001 | 正确新/旧、错误/未知/禁用账号 | 只正确提交一family；旧受限且无role grants；secret默认不序列化 |
| IDENTITY-LOGIN-RISK-001 | 3后挑战、5后锁、120/900/60秒边界、30/31来源 | 不绕过、不多核密/签发；错误挑战不记密码失败但消耗来源/挑战 |
| IDENTITY-LOGIN-CONCURRENT-001 | 并发失败/同挑战两请求、正确核密时risk竞争、login/改密两种提交顺序 | 实际失败事件不漏、5项饱和且锁不延长；成功CAS冲突不发secret；旧密码不能在成功改密后创建可用family |
| IDENTITY-LOGIN-FAULT-001 | 真实事件1062、取消、坏schema/倒退时钟、外部事务 | owner原子回滚、已提交来源/挑战不补偿、明确故障、无秘密回显 |
| IDENTITY-LOGIN-MIGRATION-001 | 新迁移反查操作、默认值与tenant隔离 | 仅两个列/owner事件表及索引；旧行默认失败关闭；append-only保护 |

先写失败测试，再实现与真实MySQL验证。不将T03/父DTC或生产验收标完成。当前身份HTTP/拒绝/本地Provisioning和受控浏览器结果见[独立测试记录](28-t03-authenticated-http-tests.md)。

## 已取得的目标测试证据

- `LoginCommitFenceTests`行为桩：3失败、0通过；实现共享凭据/risk CAS与在途失败合并后3通过。5项饱和但在途失败事件逐项保留；第五次的900秒锁不随随后在途失败延长。
- `20261024034150_LoginOwner`在应用前独立元数据检查通过：恰为2个明确默认值列、1个owner事件表、1个索引；审查没有AlterDatabase/无关表改动，才由隔离fixture应用。EF生成文件通过仓库已有formatter规范编码/namespace，不改已应用迁移语义。
- 首批协调行为桩编译成功后14项全部失败；实现后真实事件1062反例出现1失败/13通过：错误密码的risk修改在事件插入失败时未整体回滚。改为本owner显式事务后14项全部通过；没有放松断言或把风险写入当“反正更安全”的例外。
- 扩充至27项真实DB测试及1项迁移门禁全部通过：6个并发在途错密不漏事件、同挑战最多1成功、正确proof与risk竞争不发secret、login/改密两提交顺序及明确重试后全family撤销；2a/增强格式真实核验与持久legacy限制、赋予真实角色后依旧无grants且refresh不解除限制。
- 故障路径覆盖真实event1062、成功/失败写前取消且已消费source/challenge不退款、8次真实risk CAS耗尽明确抛错、核验后时钟倒退、首次risk真实PK竞争和外部事务/dirty graph保护。没有以屏障超时或编译错误代替行为红灯。

最终fresh `dotnet build WES.slnx --configuration Release --no-restore`：0 warning/error。`dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1`：Architecture29、Modules145、Persistence211、Route228，共613 passed、0 failed、0 skipped。`dotnet format WES.slnx --no-restore --verify-no-changes`、IDE诊断、`git diff --check`和双DbContext pending-model检查通过；文档映射/DAG随提交前复跑。

## 发布/回滚与剩余门禁

本片没有Host登录装配、不向匿名客户端开放内部结果；不返回密码策略/真实拒绝原因枚举给匿名调用者。生产迁移需要DBA审批，升级会默认禁用旧凭据行、限制旧会话，须提前列受控重新认证/逐账户迁移方案，不能为无中断盲改默认true。回滚代码优先保留安全事件和限制列，Down会删除证据/状态，不自动执行。真实测试只应用授权隔离`wes_test`。

相较前片，login/改密的新family竞态已用同一凭据版本CAS关闭；风险、挑战和真实密码也已由单一owner协调，而非独立Accepted拼装。身份JWT/HTTP、验证码PNG、本地目录bootstrap、具名页面及真实浏览器登录/管理路径现已通过软件验证；T03仍未整体完成，真实泄漏语料与服务表现、365天保留/容量治理、生产可信代理和密钥/迁移批准、现场H与生产部署许可仍未关闭。内部测试及[当前回归台账](readiness-register.md)不替代这些门禁。
