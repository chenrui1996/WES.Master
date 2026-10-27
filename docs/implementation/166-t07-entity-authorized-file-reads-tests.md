# T07 具名实体附件读取测试证据

- 状态：本读取续片验证通过；不得作为整包关闭依据。
- 设计：[具名业务实体附件读取](165-t07-entity-authorized-file-reads.md)。
- 用例：DTC-PLAT-007/008/014、RULE-12 / PAR-04 的业务附件读取子集。

## RED 与验证目标

先增加依赖缺失业务应用合同的真实 MySQL 测试，执行定向 `dotnet test` 得到 CS0246：缺少 FileEntityReference/IBusinessFileAuthorization/BusinessFileAccess。实现后该测试1/1通过；这是缺失合同编译 RED，不冒称运行时行为 RED。必须核对当前实体授权与精确绑定，不用文件 All grant 或字符串匹配替代 owner 授权。

覆盖元数据/下载成功、错误 owner/entity、无绑定、计数错误、实体/文件撤权、IO 中解绑及重绑版本冲突、取消/端口错误不交付和审计。保留现有暂存全套测试，验证绑定文件仍被拒绝。

## 最终结果

环境：.NET10.0.103、MySQL8.0.12，既有授权隔离 `wes_test`；不连接生产、不新增依赖或迁移。

| 验证 | 实际结果 |
|---|---|
| 新业务文件 MySQL 用例 | 26项；元数据/审计下载、两个 owner、无/错绑定、计数错误、十个字段分别缺失、两种当前授权拒绝、IO 中撤权/解绑/重绑及安全端口错误、取消回滚清零、错误 tenant/缺文件 |
| 最终 Release 文件 owner | `FullyQualifiedName~AuthorizedFileAccessTests\|FullyQualifiedName~QuarantinedFilePersistenceTests`，106/106；含原暂存、持久上传、实际引用/扫描/删除并发回归 |
| 新具名 HTTP | 21项；默认两个开关AND、仓/位固定九字段及精确审计字节、当前两个 owner 撤权/解绑、错误类型/实体、All 文件范围下缺失/外租户实体、HTTPS/认证/空UUID、物理对象缺失503/损坏500及无写路由405 |
| 最终 Release HTTP/既有浏览器 | `FullyQualifiedName~WmsBusinessFiles\|CaseId~HTTP-FILE\|CaseId~CLIENT-FILE\|FullyQualifiedName~FileCatalog\|CaseId=WMS-CLIENT-06`，86/86；含真实 HTTPS Kestrel 的20MiB/+1及五套既有 HTTPS Chromium（WMS四变体、文件一套） |
| 独立架构编译修复后 | 29/29；随后对精确最终源重跑新 HTTP 21/21，不重复计数 |
| Release build | `dotnet build WES.slnx --no-restore -c Release`，零警告、零错误 |
| 静态校验 | 修改源文件 `dotnet format --verify-no-changes --no-restore`、`git diff --check`通过；17个新增文档链接存在性核验通过 |

相关合计106+86+29=221项通过。前端源未改，不重复跑730组件/前端构建；实际浏览器使用上片最终已构建 dist，本片不声称有新业务附件 UI 验收。

## 保留的失败与修复

- 第一轮 Debug 文件授权回归78/78；随后的 Host 编译因缺少 WmsDatabaseException 的 Ledger using 失败，补精确 namespace 后新 HTTP 首批13/13。
- 扩展 HTTP 第一次20/21：外租户测试种子漏建强制 MasterDataChange，被既有 owner 保存门禁拒绝。补匹配创建事实，不放宽门禁或改预期；最终21/21。
- 第一轮 Release 架构28/29：普通 Web 编译可用的隐式 RouteGroupBuilder namespace 不在独立 Roslyn 编译的 using 集。补 `Microsoft.AspNetCore.Routing` 显式引用，不修改架构断言；29/29。

本机会话证据目录 `files\entity-file-read-validation` 保留 owner-first/owner-release-final、http-first/http-boundaries/http-browser-release-final/http-exact-final、architecture-final/architecture-corrected-final TRX。失败报告与中间通过数不重复合入最终统计。

## 不适用与剩余

未改模型，不新增 provider migration；本片无客户端，不以 API 测试替代页面/真实浏览器。业务引用由测试 fixture 内部建立，不代表绑定/解绑应用入口交付。真实扫描、病毒库/ACL、完整业务文件目录与整包验收仍未完成。
