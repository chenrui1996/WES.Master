# T07 持久具名附件写测试记录

- 状态：本写续片已验证通过；T07和T04–T08整包仍未完成。
- 设计：[绑定与解绑](167-t07-durable-business-file-bindings.md)。
- 用例：DTC-PLAT-007/008/014、RULE-12 / PAR-04 的引用写子集。

先以缺失永久绑定应用合同建立 RED；实现后在已授权 MySQL wes_test 验证精确引用/计数/版本/审计/永久回执的单事务、当前两个 owner 的 Attach/Detach、相同原键重放/换参冲突、竞争与失败回滚。迁移必须检查新增表、索引、外键及模型无漂移；API 验证严格 body/header、HTTPS/认证、默认关闭、三字段回执及旧读取不回归。

## 已观察的 RED 与修正

- 缺少永久绑定服务/权限接口/动作枚举时合同编译 CS0246；先建立失败，后实现有限应用入口。
- 扩展真实 owner 用例时 xUnit2031 拒绝 Where 后 Assert.Single，改用 predicate overload，不关闭分析器。
- 第一轮边界27/28通过，强制交错的不同 operation/同文件失败；先前 command interceptor 未取得 provider 数字，不作为根因证据。SaveChangesFailed 仅采集 provider 数字的测试诊断确证1213死锁：先插带文件 FK 子行，再升级父文件锁。修为先锁精确 tenant/file，再复验和落子行；28/28通过，未吞错或自动重放新写。
- migration 命令首次因不支持的 `--no-connect` 被拒绝，第二次因多 DbContext 需显式 context 被拒绝；显式 PlatformDbContext 后生成，未安装或恢复依赖。
- SQL评审发现 design-time factory 原默认 history 与真实 Platform owner 不同；修正为 `__WesPlatformMigrations`，保留无连接生成，测试覆盖无连接、正确 history 和模型无漂移。SQL仅新增本 owner 回执表/索引/Restrict FK，不改旧文件或推断旧 owner；只由 fixture 在授权 wes_test 迁移，不是生产。
- 首批HTTP26/26通过。扩展真实creator范围、write不隐式read、解绑前禁止孤儿清理/解绑后真实物理删除和永久回执；首轮Release owner373/373通过。尚在最终回归，不提前累计为最终通过数。

## 来源实体权限补充

增加先失败的 owner/API 回归：即使目标实体当前 Attach 和文件 Attach 均允许，来源实体 read 撤销仍不能通过另一个目标暴露已绑定内容；旧泛 owner 未登记来源也拒绝。实际RED为owner预期Forbidden却Success、两项HTTP预期403却201，不是用编译失败代替这一行为问题。修复后1 owner/2 HTTP均通过。恢复全部已登记来源权限才允许多引用；其中一个来源可读不替代全部来源。解绑自己有权的精确目标不要求其他来源可读。来源必须来自持久引用行并在父文件锁内复验，不能接受调用者提供的“已授权来源”或只核目标。

## 最终结果

环境：.NET10.0.103、MySQL8.0.12，既有授权隔离wes_test；没有生产连接、真实Broker/Pack/ClamAV验收，也未安装/恢复依赖。

| 验证 | 实际结果 |
|---|---|
| 新绑定owner用例 | 30项真实MySQL；原子引用/计数/版本/审计/永久回执、当前字段/会话/双owner/来源、相同原键/六类换参、91天后新会话恢复、版本/成员状态、未扫描/计数不一致、审计失败/取消回滚、不可改删回执、同/异key强制竞争、父锁后撤权 |
| 新绑定HTTP用例 | 32项；仓/位PUT/DELETE、精确body/header/三字段回执、当前两owner动作/真实creator Own范围、write不隐式read、同/异key竞争、原键恢复、来源撤权/未登记owner、解绑前禁止孤儿清理/解绑后真实对象删除且保留回执 |
| 首轮相关Release回归 | owner373 + HTTP/六套既有HTTPS浏览器218 + 架构29 = 620/620；包括WMS主数据238及仓/位创建旧合同，未因共享header校验重命名退化 |
| 来源修正后的最终文件owner | `FullyQualifiedName~AuthorizedFileAccessTests\|FullyQualifiedName~QuarantinedFilePersistenceTests`，136/136，含新增30项与旧暂存/上传/读取/扫描/清理并发 |
| 来源修正后的最终HTTP/浏览器 | `FullyQualifiedName~WmsFileBindingHttp\|FullyQualifiedName~WmsBusinessFiles\|CaseId~HTTP-FILE\|CaseId~CLIENT-FILE\|FullyQualifiedName~FileCatalog\|CaseId=WMS-CLIENT-06\|FullyQualifiedName~RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages`，117/117；新写32、旧具名读取19，以及六套既有真实HTTPS Chromium（WMS四变体、文件一套、全部Platform页面一套） |
| 最终架构与构建 | 架构29/29，`dotnet build WES.slnx --no-restore -c Release`零警告、零错误 |
| 最终静态检查 | 修改/新增C# `dotnet format --verify-no-changes --no-restore`及`git diff --check`通过；新增/改写行的27个本地文档链接均存在 |

来源修正后的最终相关合计136+117+29=282项通过，无失败/跳过。首轮620、单独3项行为GREEN及中间28/26等结果不重复累计到282。本片没有前端修改，浏览器使用上片最终730组件/严格构建的dist，不声称新增附件UI已验收。未重跑全解决方案全部测试；只跑与改动有关的owner/HTTP/浏览器/架构。

初次扫描全部改动文档的702个既有链接时发现7个跨worktree的历史Seven.Master/Route等相对链接在本worktree不可用；未改这些既有链接，不以新增链接检查掩盖此边界。最终27项检查限定本片新增/改写行，不包含旧外部工程引用。

本机会话证据目录 `files\business-file-binding-validation` 保留编译/并发失败、仅provider数字1213诊断、首轮owner/HTTP、`binding-source-owner-red` / `binding-source-http-red`及对应GREEN，以及最终 `binding-file-owner-final` / `binding-file-http-browser-final` / `binding-file-architecture-final` TRX；迁移评审SQL保存在 `files\entity-file-read-validation\binding-migration.sql`。失败与中间通过数均保留，不覆盖成成功记录。

## 验证边界

真实 ClamAV/病毒库/ACL、业务附件目录/客户端和整包验收仍未完成，不由内部干净文件 seed 或协议替身代签。迁移只在授权wes_test应用；永久回执/审计及文件tombstone的生产容量、备份和保留验收仍须外部证据。回滚软件不删除永久记录；未登记来源owner不提供成功默认策略。
