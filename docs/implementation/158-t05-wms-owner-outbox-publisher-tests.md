# T05 WMS owner-local发布器测试

- 状态：实施中；owner发布器子集已验证，不关闭T05；设计：[157](157-t05-wms-owner-outbox-publisher.md)。

| 子用例 | 步骤 | 预期 |
|---|---|---|
| WMS-PUBLISH-01 | 创建真实资源/事实/Outbox后ACK，重放原创建，再发布 | 稳定原envelope，Published耐久/只有一次；业务资源仍禁用、无库存效果 |
| WMS-PUBLISH-02 | 临时失败六次、按DB NextAttemptAt推进、到点前调用 | 1/5/30/120/600秒+受限抖动；六次后隔离，不无限重试 |
| WMS-PUBLISH-03 | 永久拒绝/15分钟边界 | 立即/预算耗尽隔离并保留原event |
| WMS-PUBLISH-04 | 两个独立owner争租约、原owner迟到ACK/租约接管 | 只有当前lease成功，旧ACK不覆盖、不报Published |
| WMS-PUBLISH-05 | 取消/transport异常/ACK后DB失败，90秒后恢复 | 原lease耐久；新owner原event恢复，不伪造原结果 |
| WMS-PUBLISH-06 | 非法batch/dirty/transaction/ambient、外tenant、非法存储envelope/状态 | 无越界发布；明确错误/拒绝且日志无payload/SQL |
| WMS-PUBLISH-07 | 普通保存修改delivery/专用保存改envelope、创建回滚/重放 | 一律拒绝绕过；原创建原子性不变 |
| WMS-PUBLISH-08 | 真实迁移/模型/Nullable UTC微秒、旧Pending初始化 | 无模型漂移、原事实不变、CAS/索引实际生效 |
| WMS-PUBLISH-09 | 当前授权列表/summary、四态客户端、未知状态/撤权 | 原形状与当前creator范围不变；PendingCount精确，未接Inbox不构造0 |

真实Broker/真实ACK丢失、进程崩溃和消费业务Inbox未获证据时保持blocked；可控transport与故障注入只证明owner持久化窗口，不代签外部环境。

## RED到GREEN与实际证据

1. 先写157/158及真实owner测试；`dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore --filter FullyQualifiedName~WmsPublisher`因缺少WMS发布器/transport/envelope公共类型编译失败，属于合同RED，不虚报为运行时用例失败。
2. 初始15真实MySQL发布测试通过后，新增四态reader用例先失败：原Pending-only读取拒绝Leased/Published/Quarantined。同步事件列表、PendingCount及客户端，九字段/三字段形状和当前creator授权不变。客户端49项中新增4项实际先红后绿；未知状态改用4作拒绝样本，不把新合法状态1继续当非法。
3. 并发/迁移/失败边界首轮27/28：double秒加法及测试预期本身分别出现1.1999990/1.1999999，而批准上界应为1.200000秒。改用整数tick断言和十进制整数微秒退避，0%/20%端点实际通过，不放宽误差阈值。
4. 复查增加“claim提交跨过90秒/15分钟”两项真实故障注入，`claim-commit-red.trx`两项均实际失败；修复发送前复验后，全owner238通过。lease过期0次外发，预算过期持久隔离，不靠延长预算让测试变绿。

最终证据根为本机session `files\wms-publisher-validation`；不提交连接、数据库原始结果或扫描/Broker假凭证：

| 实际运行 | 结果 | 证据 |
|---|---|---|
| Release完整WmsMasterDataTests | 238/238（原205+新增33），无skip | `owner-complete-final.trx` |
| WMS事件/统计HTTP及四套真实HTTPS Chromium | 32/32（HTTP28+浏览器4），无skip | `http-browser-complete-final.trx` |
| 架构门禁 | 29/29 | `architecture-final.trx` |
| 全前端 | 33文件695/695 | `npm test`终端 |
| 严格客户端构建 | vue-tsc/Vite通过 | `npm run build`终端 |
| 全solution Release | 0 warnings / 0 errors | `dotnet build WES.slnx --configuration Release --no-restore`终端 |
| 限定C# whitespace验证、EF pending-model检查 | 通过；无模型漂移 | 终端 |

上述相关.NET合计299项，不是完整.NET全套。最终32项包含原事件当前权限/HTTPS/跨tenant、12项独立计数、3项新增投递态HTTP，以及原Platform全页面和三种WMS真实浏览器变体；WMS只读变体由真实owner将两消息置Published/Quarantined，库存变体保留1个不明Leased+1个Pending，创建变体保持原3个Pending。实际HTTP状态/九字段/标签及PendingCount分别0/1/3，撤权403、键盘、离线/手工重读、原响应式和文字放大回归通过。控制transport只在测试项目，未注册为默认运行能力。

真实迁移在授权wes_test应用；唯一临时旧形状探针表执行同一迁移Up操作，保留原EventId/OccurredAt/Payload/Status，初始化版本/到期并保持Attempts0、所有lease/结果空；finally删除指定探针表。竞争用两个独立owner并在首次claim SQL前暂停，使另一owner实际赢得CAS；另验证迟到ACK、ACK保存CAS失配、取消/transport中断/ACK保存失败保留lease，随后原event恢复。普通保存/专用入口伪造payload或不更新版本均拒绝，provider错误无敏感sentinel、无成功空批次。

未运行/未完成：SQL Server/PostgreSQL延后；真实Broker/网络ACK丢失/操作系统进程崩溃/消费者业务去重、Host托管调度、WMS Inbox和跨owner统一快照/完整恢复仍缺。没有生产迁移、服务安装或真实设备控制，T04–T08整包均未关闭。
