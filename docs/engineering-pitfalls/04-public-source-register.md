# 公开网络与GitHub来源登记

- 状态：实施中；检索/阅读日期：2026-10-28。
- 范围：公开官方文档、官方GitHub文档仓库和问题讨论；仅发送通用技术检索词，没有上传项目源码、配置或内部证据。
- 本次结果：新增DEF-014..015、INF-013..018、GEN-011..014，共12条；补充既有DEF-013来源。知识库由35条增至47条，新增内容都是**外部经验待核验**，不是47个WES缺陷或本轮业务测试通过。
- 方法：按公开技术主题检索GitHub issues，阅读匹配问题正文，并以官方文档核验机制；剔除无关、版本不适用或未取到正文的结论。引用为独立概括，不复制长篇原文。
- 边界：网页/main分支和issue状态会变化，日期为本次查阅时点；版本升级后重新核验。issue关闭不等于已发布修复，问题正文/评论中的workaround也不自动构成建议。所有条目内的“待执行验证”本次均未运行。

## WEB-001 HttpCompletionOption流式读取边界

- 来源：[Microsoft API / .NET10](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0)，已读Remarks与示例。
- 支撑DEF-014：ResponseHeadersRead只把该HTTP调用等到头部；后续内容读取需另管期限，MaxResponseContentBufferSize不按ResponseContentRead方式自动生效。

## WEB-002 HttpClient寿命DNS与Cookie

- 来源：[Microsoft HttpClient guidelines](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines)，已读DNS、连接池、推荐使用和Cookie警告。
- 支撑INF-018：DNS在新建连接时解析、连接寿命与factory handler复用、CookieContainer共享/回收；示例时长不是通用配置标准。

## WEB-003 EF context池化状态

- 来源：[Microsoft Advanced Performance Topics](https://learn.microsoft.com/en-us/ef/core/performance/advanced-performance-topics)，已读DbContext pooling及Managing state in pooled contexts。
- 支撑INF-013：实例复用、OnConfiguring仅创建时执行、tenant变化要特殊处理；context池化不同于驱动连接池。

## WEB-004 EF直接批量更新删除

- 来源：[Microsoft ExecuteUpdate and ExecuteDelete](https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete)，已读Change tracking、Transactions、Concurrency control。
- 支撑INF-014：立即执行、跟踪状态不更新、多调用不自动整组事务及手动并发token/影响行数检查；Provider支持仍需独立验证。

## WEB-005 EF事务重试与提交未知

- 来源：[dotnet/EntityFramework.Docs官方GitHub正文](https://github.com/dotnet/EntityFramework.Docs/blob/main/entity-framework/core/miscellaneous/connection-resiliency.md)，已读execution strategies/transactions、commit failure/idempotency及verification。
- 支撑INF-015：提交断线结果未知、重放整事务的必要性与风险、验证成功状态和结果集缓冲；示例Provider是SQL Server，不能直接代签MySQL。

## WEB-006 MySQL DDL隐式提交

- 来源：[Oracle MySQL8.4手册 / Statements That Cause an Implicit Commit](https://docs.oracle.com/cd/E17952_01/mysql-8.4-en/implicit-commit.html)，已读DDL及临时表例外。
- 支撑INF-016：多种DDL隐式提交，用户ROLLBACK不撤销CREATE TABLE；不能由脚本事务包裹推断整组DDL可回滚。
- 原dev.mysql.com同主题页面本次403，采用成功取得的Oracle同版本手册；不是访问受限内部资料的替代读取。

## WEB-007 EF查询过滤与必需导航

- 来源：[Microsoft Global Query Filters](https://learn.microsoft.com/en-us/ef/core/querying/filters)，已读Query filters and required navigations及示例SQL。
- 支撑INF-017：必需导航INNER JOIN可能使带Include查询减少结果；示例调整关系只是解释机制，不批准WES变更领域关联。

## WEB-008 Vue watchers

- 来源：[Vue官方Watchers](https://vuejs.org/guide/essentials/watchers.html)，已读watchEffect、Side Effect Cleanup、Flush Timing、Stopping a Watcher；另核对[官方GitHub文档](https://github.com/vuejs/docs/blob/main/src/guide/essentials/watchers.md)入口正文。
- 支撑GEN-011：首个await前的依赖追踪、3.5+同步注册onWatcherCleanup、回调onCleanup差异、异步创建watcher需管理停止。

## WEB-009 script setup的版本相关默认行为

- 来源：[Vue官方script setup](https://vuejs.org/api/sfc-script-setup.html)，已读Reactive Props Destructure、Default props values、defineModel默认值警告。
- 支撑DEF-015/GEN-012：Vue3.5解构编译转换、withDefaults引用类型工厂与解构默认值区别、父undefined/子default不同步。

## WEB-010 Vue reactive对象的解构限制

- 来源：[Vue官方Reactivity in Depth](https://vuejs.org/guide/extras/reactivity-in-depth.html)，已读proxy/解构/引用身份限制。
- 支撑GEN-012：普通解构局部绑定不再触发源proxy，非原始对象内部修改仍可能响应；不能概括为任何解构都会彻底失去响应性。

## WEB-011 EF分页

- 来源：[Microsoft Pagination](https://learn.microsoft.com/en-us/ef/core/querying/pagination)，已读唯一排序警告、offset/keyset及多键索引。
- 支撑GEN-013：排序须完全唯一，offset遇并发变化可能漏重；keyset的优势不等于任意页访问或跨请求固定快照。

## WEB-012 Playwright最佳实践

- 来源：[Playwright Best Practices](https://playwright.dev/docs/best-practices)，已读测试隔离、数据库控制、locators与web-first assertions。
- 支撑GEN-014：隔离cookies/存储/数据，优先用户可见定位和可等待断言；官方代码生成建议不迁入本项目。

## WEB-013 转发头可信代理兼容变更

- 来源：[Microsoft forwarded headers breaking change](https://learn.microsoft.com/en-us/aspnet/core/breaking-changes/8/forwarded-headers-unknown-proxies?view=aspnetcore-10.0)，已读生效版本、行为及建议。
- 补证DEF-013：ASP.NET Core8.0.17/9.0.6起未知代理转发头不再被接受，可能造成TLS终止后的循环跳转/认证失败；应配置可信代理而非取消信任边界。实际.NET10部署仍按对应版本/拓扑验证。

## WEB-014 Vue条件依赖的GitHub问题线索

- 来源：[vuejs/core#13688](https://github.com/vuejs/core/issues/13688)，本次状态open/reopened、标签need documentation；已读问题正文。
- 主题：用户询问watchEffect条件分支下依赖是否每次重新收集。与WEB-008一起提醒GEN-011显式测试条件/异步依赖；它是文档澄清请求，不是WES已复现缺陷，不把相关PR关闭当已发布修复。

## WEB-015 反代认证的GitHub问题线索

- 来源：[dotnet/aspnetcore#61435](https://github.com/dotnet/aspnetcore/issues/61435)，本次状态closed，标签含Needs: Author Feedback/Status: No Recent Activity，未关联关闭修复PR；已读问题正文及状态元数据。
- 主题：.NET9、Docker/Nginx TLS终止下OAuth redirect mismatch报告。只用于DEF-013的排查线索；没有复现该报告，也未以其猜测作为WES根因。
- 不采纳正文中清空KnownProxies/KnownNetworks或强制Scheme的尝试；可信边界建议以WEB-013为依据。此issue关闭不能解释为官方已修复并发布。

## 本次网络限制与未采用内容

聚合Web搜索曾返回504；改用官方页面直接阅读和GitHub公开问题检索。部分Microsoft/Raw GitHub页面遇到传输失败，未取到正文的HTTP resilience默认重试细节没有写入条目；MySQL deadlock页面也未取得有效正文，不为凑数量补写版本/重试结论。

网络资料仅补充机制检查，不作为代码审查结果、运行测试、生产许可或新的默认参数批准。后续触及相关能力时再执行条目验证；未发现WES实际问题前，不把每个公开issue复制成软件阻断项。
