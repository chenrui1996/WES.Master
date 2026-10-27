# 软件实施契约基线 v1

- 状态：已批准设计的实施细化；未实现接口不宣称可调用
- 范围：T01跨owner语义；[WES-RULES-1.0](../detailed-design/05-approved-rules-and-defaults.md)优先约束
- owner：各数据owner维护自身契约；Host只绑定/授权/映射，不拥有业务状态

## 1. 版本、类型与边界

公共业务HTTP使用`/api/v1/<owner>/...`，业务字段camelCase。内部接口按`<Owner>.Contracts.V1`命名，消息`schemaVersion=1`；它们是同一语义的不同传输，不强行给内部调用造HTTP。任何删除、收紧既有有效输入、改字段/状态语义须新版本与迁移期；新增可选字段不改变旧语义。读取未知枚举不能当Completed；控制消费者不支持schema时拒绝并隔离消息。

类型：`Id`为不含秘密的非空UUID字符串；`Version`为服务端不透明字符串、不得数值自增猜测；`Instant`为RFC3339 UTC（后缀Z）；`Quantity`为十进制字符串，按decimal(28,6)与物料精度验证、不隐式舍入；`Hash`为SHA-256规范化语义负载摘要。ID大小写/字节规范由owner生成后保持原值。绝不由客户端提交主体授权集合。

| 合同 | 必填字段 | 条件/约束 |
|---|---|---|
| RequestContext | correlationId | 服务端生成/校验；主体、tenantId、当前权限从已验证身份解析，业务tenant参数必须与范围一致 |
| MutationContext | operationId, expectedVersion（更新时） | key作用域tenant+owner+operation；摘要包含业务参数/目标/策略版本，不含可变transport头；首次创建没有expectedVersion |
| DurableEvent | eventId, schemaVersion, tenantId, owner, aggregateId, aggregateVersion, correlationId, causationId, occurredAt, payload | owner事务Outbox产生；Inbox按tenant+producer+eventId去重，不用receive时间代替发生时间 |
| ExecutionIdentity | warehouseId, packId, transportId, legId, taskId, taskDeploymentId, mapVersion, policyVersion | 每个执行对象固定版本；普通平台请求不携带这些字段 |
| CommandIdentity | commandId, operationId, epoch, grantId, segmentId, deviceId | 仅真实支持的命令；下发前Pack重验当前租约/联锁/全量grant |
| FeedbackIdentity | eventId, commandId, taskId, segmentId, deviceId, sourceGeneration, sourceSequence, occurredAt, receivedAt, schemaVersion | 可信Adapter认证来源，旧generation/序列不推进当前控制；迟到事实保留可对账 |

`Idempotency-Key`承载operationId，更新的`If-Match`承载expectedVersion；缺必需版本返回428，版本冲突412，换参数复用key返回409 `IdempotencyPayloadMismatch`。同key同摘要返回原operation的当前结果而非重复业务；Unknown不创建新operation重发。完成后按保留规则清理技术缓存，领域唯一键/tombstone仍防重复。

## 2. 结果、错误、取消与时限

已实施具名附件读取增量：GET `/api/wms/warehouses/{entityId}/files/{fileId}` 及 `/content`，另有固定 `locations` 两路径；仅WMS读装配与Files两个既有开关均开启。固定owner/type不从请求采信，必须精确持久绑定、当前实体完整read及Platform文件九字段read/Content交集。沿用200安全投影或attachment/no-store/nosniff字节、403拒绝/404缺文件/400非法ID/409状态/412版本/503依赖/500完整性错误；下载IO后复验且审计先于交付。详见[实体附件读取](../implementation/165-t07-entity-authorized-file-reads.md)，旧泛owner值不自动迁为具名类型。

具名写增量：[永久附件绑定/解绑](../implementation/167-t07-durable-business-file-bindings.md)。在同一固定 `/files/{fileId}` 路径 PUT绑定、DELETE解绑；严格JSON body仅 `expectedVersion`（此具名写合同使用body版本，不接受If-Match替换），单值合法 `Idempotency-Key` 与 `X-WES-Client`。当前目标实体完整read及对应WMS write字段Attach/Detach，另需按真实文件creator/当前部门核验Platform write同名字段；write不自动grant文件read。给已绑定文件增加目标还须全部持久来源实体当前read，未登记来源拒绝。成功精确 `{id,version,attached}`，初次绑定201+Location，解绑/原键重放200，no-store；缺版本428/非法400/版本冲突412/成员状态或换参409，依赖错误沿用503/500。永久操作以tenant+operation唯一且核actor/完整语义元组，不按普通90天缓存删除；解绑或物理删除后恢复仍是原事实，不重建引用。引用/计数/版本/审计/回执在Platform同事务；跨owner授权不声称分布式ACID。复用默认双开关，POST仍405，无业务附件目录或泛CRUD。

同步读取200；完成创建201并给Location；异步接收202返回`{operationId,status,version,correlationId}`及操作查询Location。`status`仅`Accepted/Running/Succeeded/Rejected/Unknown`；具体领域状态另在DTO表达。Accepted不是实物完成。GET operation只对原租户/授权主体开放；不存在或无可见范围不泄露对象。

拒绝用ProblemDetails并扩展`code/correlationId`，禁止堆栈/SQL/设备payload/秘密。400 `ValidationFailed`，401 `AuthenticationRequired`，403 `PermissionDenied`，404 `ResourceNotFound`，409 `InvalidState/IdempotencyPayloadMismatch/ResourceConflict`，412 `VersionConflict`，422 `UnsupportedCapability`，428 `VersionRequired`，429 `RateLimited`，503 `DependencyUnavailable/ControlUnconfigured`。未知执行结果为可查询operation的Unknown，不包装成成功或“未执行”。

取消客户端等待只取消尚未提交的IO，不撤销已提交事实。独立取消用例复验expectedVersion/当前权限；已发物理动作转取消请求并查询事实，不假报Canceled。非物理读取沿规则总deadline10秒；物理query/await/停止期限为H参数，未配置拒绝控制。Outbox非物理重试与Unknown物理0自动重发遵守RULE/PAR，不使用Host默认HTTP重试代替。

## 3. 身份、授权与审计

| 接口/HTTP规划 | 请求/响应字段 | owner与行为 |
|---|---|---|
| Identity.Login / POST platform/sessions | account,password,challengeId?,challengeAnswer?,clientKind(Web/Pda) → accessToken,expiresAt,sessionId；refresh秘密仅受保护传输 | Platform执行登录预算/一次验证码/旧密码改密限制；令牌不入审计，登录失败不泄露账号存在 |
| Identity.Refresh / POST platform/sessions/refresh | 受保护refresh凭据 → 新access/refresh与expiresAt | 轮换事务只有一个后继；重放撤相关会话；不是幂等缓存明文token |
| Identity.Revoke / DELETE platform/sessions/{id} | expectedVersion → 撤销结果 | 当前授权校验，撤销持久化；已下发物理命令事实不消失 |
| Authorization.Evaluate（内部） | principalId,tenantId,permission,resourceRef,operationId? → allowed,decisionVersion,reasonCode | 高后果/固定快照每次查当前授权，不信客户端role；普通只读缓存上限30秒 |
| Audit.Append（owner事务边界） | actorRef,tenantId,action,resourceRef,operationId,beforeVersion?,afterVersion?,safeReason,correlationId | 业务owner持久化事实与可靠审计引用，Platform提供审计读取/索引；不创建跨owner分布式事务 |

## 4. WMS与运输协作

对应[库位创建客户端](../implementation/163-t08-disabled-location-creation-client.md)只发送上述精确tuple/永久key，经预览和二次确认；503等未知结果冻结原tuple/key手工恢复，422仅拒绝本次、不抹除先前未知。三字段回执不当完整resource投影，必须显式GET重新复验read，不默认schema实现/授予权限，不代表可用Pack、启用/可分配或物理完成。

已实施路径增量：POST `/api/wms/locations`只在独立默认关闭LocationCreationEnabled且具名schema装配通过后开放。精确`{code,name,warehouseId,packId,schemaVersion}`，可信subject/永久key、当前五字段及父仓read，默认禁用位置/事实/Outbox原子提交。201+Location或200精确三字段重放；SchemaRejected422/Unavailable503等详见[库位HTTP合同](../implementation/161-t08-authorized-location-creation-http.md)。无默认schema实现或实际Pack验收，不将接口扩展点当现场可用，亦不代表启用/库存或物理完成。

不可变流水只读合同：[当前精确定义](../implementation/153-t08-authorized-stock-ledger-query.md)。GET `/api/wms/ledger`沿既有默认关闭WMS读装配，独立`wms.ledger.read`当前All完整19字段，不借库存读权或posting actor。维度/批次/来源/关联/类型/显式UTC微秒半开时间SQL前过滤、OccurredAt/Id升序实时页；Quantity有符号、Before/After非负固定六位字符串，来源/余额均真实永久事实。Host仅公开Features读取类别，不引用领域posting；非法/撤权/存储损坏明确失败，无任意改账/删除API。客户端无损BigInt/微秒校验、清旧取消及离线手工恢复，不构造来源跳转、固定核对快照或导出。

当前只读库存合同：[精确定义](../implementation/145-t08-authorized-inventory-query.md)。GET `/api/wms/inventory`沿显式WMS读装配，可信当前tenant/actor/session及All完整15字段，精确维度筛选、有界实时分页、固定六位decimal字符串；同一owner一致读保护份额并集，不包含写入/分配许可、物理位置或跨请求核对快照，不把未知观察量填0。

对应[实时客户端](../implementation/147-t08-inventory-query-client.md)只解析精确页/行字段及数量字符串，以BigInt校验等式和单位精度；独立Wms模块+permission路由、All完整字段按钮，当前403清旧，输入/身份变动取消，无业务缓存或重连自动请求。不把实时分页代替固定核对快照/授权流水/导出。

WMS创建事件[范围计数](../implementation/149-t05-wms-creation-event-counts.md)：GET `/api/wms/master-data-events/summary`在当前`wms.messages.read`独立Owner/ObservedAt/PendingCount字段creator交集内SQL全量统计，精确三字段响应，不受列表分页影响；不足403、不借其他owner权限。PendingCount仅统计Pending，不计Leased/Published/Quarantined，不构造不存在的Inbox或跨owner总和。ObservedAt不是共同水位；默认关闭装配/当前身份/取消/安全错误沿现有WMS读取合同。

已实施只读增量：GET `/api/wms/master-data-events`在既有显式主数据读装配下提供独立当前`wms.messages.read`/creator授权的九字段创建事件元数据与offset/limit，无payload/Inbox/虚构投递状态；[精确合同](../implementation/141-t05-wms-creation-event-diagnostics.md)。这不是业务transport事件、跨owner统计或Broker健康。

已实施路径增量：POST `/api/wms/warehouses`只在单独默认关闭创建装配下开放，精确`{code,name}`和可信身份/永久key，201+Location或200精确重放；安全拒绝及版本兼容见[仓库HTTP合同](../implementation/137-t08-authorized-warehouse-creation-http.md)。不将既有未版本化路径自行迁移到规划v1，不新增位置/启停或运输实现。

局部实现事件 `wms.master-data.created`/schemaVersion=1，Owner=`Wms`；仅默认禁用仓/位新创建事实，非启用/库存/物理控制命令。envelope与安全payload、tenant/operation事件身份、不可变/回放及迁移兼容详见[WMS创建Outbox](../implementation/135-t05-t08-wms-creation-outbox.md)。后续[owner发布器](../implementation/157-t05-wms-owner-outbox-publisher.md)不改envelope，只扩展耐久delivery及九字段Status=0..3；旧Pending由原EventId/OccurredAt初始化CAS/到期，不生成历史事实。[默认关闭托管调度](../implementation/159-t05-hosted-wms-publisher.md)已有严格tenant/batch/周期及新owner runner；新客户端须先与四态合同同步，再经真实adapter/运行能力登记与门禁验收显式启用。当前Program仍拒绝未登记的Outbox/MessageQueue运行能力，默认不外发，无consumer/Inbox或历史事实回填，不能将下表运输合同标作已实现。

| 契约/路径规划 | 请求必填（另含适用context） | 输出/owner与不变量 |
|---|---|---|
| Wms.QueryStock / GET wms/stocks | warehouseId,已授权筛选,稳定排序,cursor?/snapshotId? | stockId,materialId,bookLocationId,quantity,reserved,inTransit,available,version；关联投影也受字段权限；保护数量按并集扣减 |
| Wms.Reserve（内部） | documentId,lines[{lineId,stockId,quantity,unitId}],policyVersion | reservationId,lines,version；WMS全行原子预留+Outbox，任何不足整单不写；不跨Pack事务 |
| Wms.RequestTransport / POST wms/transport-intents | documentId,executionUnitId,containerId,sourceLocationId,targetLocationId,quantity,unitId,policyVersion | intentId/operationId；WMS本地事务创建唯一intent+Outbox，Orchestration Inbox接收 |
| TransportIntent（事件） | intentId,documentId,executionUnitId,containerId,sourceLocationId,targetLocationId,quantity,unitId,policyVersion | Orchestration生成唯一transport/Leg；不接收任意库存entity或自行修改库存 |
| TransportCompletion（事件） | completionId,intentId,executionUnitId,transportId,finalLocationId,evidenceRefs[],completedAt | WMS Inbox按唯一实物执行单元补账一次；到达但DB失败保持PendingSync/保护，不重新运输 |
| Wms.CancelRemainder / POST wms/documents/{id}/cancel-remainder | 未执行line/executionUnit列表,reason,expectedVersion | 仅确认未执行余量释放；A已完成不回滚、B已实物不可取消；全部计划且同步才Completed，否则ClosedPartial |

数量/跟踪/短拣/盘点沿既有详细设计。此合同只冻结跨owner输入输出，不另建第二套业务API；具体页面CRUD DTO在各功能切片编码前细化并链接对应FUNC/DTC。

## 5. 编排、交接与Pack能力

| 契约 | 字段 | 成功/拒绝/Unknown与边界 |
|---|---|---|
| Pack.GetCapabilities | packId → capabilityVersion,operations[],supportsQuery,supportsDedup,supportsStop,verifiedEvidenceRefs[] | 仅已证实能力为true；缺查询/去重/停车证据不默认支持 |
| Pack.AcceptLeg | ExecutionIdentity,sourceResourceId,targetResourceId,containerId,quantity,unitId,capabilityVersion | 对应Pack持久化任务+Inbox/Outbox；版本/能力/部署不符拒绝，Orchestration不直写Pack任务表 |
| Pack.RequestAction | ExecutionIdentity,CommandIdentity,action,parametersHash,approvalId?,expectedVersion | Pack最终校验当前授权/epoch/设备能力/联锁/grant；只允许白名单动作，不转发任意报文 |
| Pack.QueryOperation | packId,operationId → status,evidenceRefs[],version | Unknown保持资源保护，不据查询失败释放或重发 |
| Handover.Ready | handoverId,fromLegId,toLegId,resourceId,expectedVersion | ReadyToReceive只表达准备，不激活下一Leg |
| Handover.Confirm | handoverId,fromEvidenceRef,toEvidenceRef,containerId,expectedVersion | Orchestration本地事务转唯一executionCustodian、激活下一Leg+Outbox；双方同一handover证据不足保持Unknown |

跨Pack没有共享ledger/ACID。交接中间节点不发WMS移账；bookLocation、executionCustodian、observedLocation、inTransit分别投影。取消/完成竞争依持久化版本与证据只推进一次；不把Broker ACK当交接证据。

## 6. 设备语义与反馈

DeviceIntegration负责协议映射/连接、可信来源、generation与序列；反馈事件包含FeedbackIdentity及`state/observedPosition/faultCodes/evidenceRef`。Pack拥有业务推进/最终命令校验，协议Adapter不得直接更新库存/Leg。

命令支持集由capabilityVersion冻结；Unsupported显式422且设备写0。Timeout先Query，可能执行但无证据则Unknown，禁止自动重发。进程重启先恢复耐久命令/epoch和现场事实，缺fence不放行。SCADA投影陈旧与异常可见，不以缓存“在线”作为联锁。

## 7. 地图部署与owner prepare

| 契约 | 请求/响应 | 事务/恢复 |
|---|---|---|
| Owner.PrepareArtifact | deploymentId,ownerId,artifactHash,mapVersion,policyVersion,expectedOwnerVersion → preparedToken,expiresAt,validationIssues[] | owner只写自己的prepared版本；缺物理映射/非法容量拒绝；10分钟过期不能续用 |
| Simulator.ActivateManifest | deploymentId,expectedActiveDeploymentId,expectedGeneration,ownerTokens[],manifestHash,approvalId → activeGeneration | Simulator本地CAS+Outbox；各owner按冻结artifact响应，不跨私表事务 |
| Deployment.Policy | DrainAffected或有证据CompatibleContinue；affectedPackIds[] | 默认整Pack排空；旧任务固定taskDeployment/map，可在当前epoch/冲突核验后取得仅排空后续grant |

失败重做preview/prepare/审批；旧token不可换摘要复用。默认Promote只读；生产warning分类逐项审查，安全warning不可豁免。无现场H输入不开放真实控制。

## 8. 只读投影、问题与告警

投影DTO最少`assetId,ownerId,version,sourceGeneration,sourceSequence,observedAt,receivedAt,freshness,status,correlationRefs[]`；读端仅展示/关联，不能更新owner事实。分页/快照/列偏好遵守密集交互合同；隐藏列不解除字段授权。

Issue由Orchestration维护：`issueId,category,severity,assetRef,transportRef?,state,assigneeRef?,version,evidenceRefs[],alarmRefs[],operationRefs[]`。认领/分派/处置/提交验证/关闭/复开为具名用例，均expectedVersion+当前权限+审计，不能用通用setStatus绕过状态机。验证依据owner回读、问题原因与恢复证据；Alarm.Cleared及Command.Accepted均不允许自动Resolved。原模块状态名/迁移守卫是唯一状态机来源。

Alarm由Notifications拥有事实/聚合/确认/清除，`alarmId,episodeId,sourceOwner,sourceEventId,occurredAt,severity,acknowledgedAt?,clearedAt?,issueId?`。确认不等于清除，清除不等于问题解决。高危即时送达不被30秒普通聚合静默。

## 9. 反例与交付边界

| 反例 | 必须结果 | 责任测试 |
|---|---|---|
| TA key被TB重用 | 仍按租户隔离，不能回TA结果或改TA状态 | T04/T05授权/幂等测试 |
| 同key换目标/策略摘要 | 409且原operation不变、设备写0 | T05/T15/T28 |
| map缺physicalConflictId或旧map另占同物理域 | 拒grant/控制且不释放已有保护 | T18，RVT-01 |
| 取消与完成同时提交 | 唯一合法终态，实物事实优先对账而非二次运输 | T15/T21/T23 |
| 双Pack交接仅一方确认 | 下一Leg不激活，唯一保管权不丢 | T15，RVT-03 |
| active代际/prepare摘要变更 | CAS/版本拒绝，旧任务仍固定部署 | T26，RVT-04 |

这些是待实现自动化测试的断言，不是本轮已通过证据。首片Host只有健康HTTP，不提供上述业务接口。新成员先读本合同→对应模块FUNC/DTC→owner实现/测试→实施记录，禁止按合同表批量生成空handler。
