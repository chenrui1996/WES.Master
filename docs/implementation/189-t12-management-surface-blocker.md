# T12a 定义/表单 owner-local 持久化实施记录

- 状态：已实现并验证本持久化子片；其余管理面仍 gated；2026-11-01。
- 范围：T12a tenant-scoped drafts、不可变 definition/schema published snapshots、expectedVersion、operationId回执和owner-local审计事实；无权限、文件授权、HTTP或UI；T13明确不在范围内。
- 上一已验收子片：[187](187-t12-definition-validation-core.md)/[188](188-t12-definition-validation-core-tests.md)纯验证内核。
- 独立测试证据：[190](190-t12-management-surface-blocker-tests.md)。

## 1. 本轮结果

依Controller裁定，通用[MutationContext/operation identity](../contracts/01-business-and-execution-contracts.md)已规定tenant+owner+operation作用域、payload mismatch、expectedVersion冲突和同payload重放；`Audit.Append`规定业务owner在本地事务中持久事实/引用，Platform提供读取/索引。因此这些不再作为阻断。本子片实现独立WorkflowForm owner DbContext、迁移、草稿、immutable published snapshots、永久operation receipt和同事务audit fact。真实MySQL测试/命令见[190](190-t12-management-surface-blocker-tests.md)。

仍未实现且不在T12a范围：

1. **管理授权/API/UI gated。** 当前Platform有当前授权投影，但没有已批准Workflow管理permission/field映射。未建立端点或授予权限，不采纳客户端身份/tenant/候选声明。
2. **当前文件引用授权 gated。** Platform公开附件合同要求具名entity owner；Workflow动态表单字段缺少相应当前可访问引用适配器。Published schema不代表文件引用已获授权，本片不读取/验证引用权限。
3. **发布验证集成未端到端完成。** 内部发布存储接受纯内核已编译的不可变snapshot；没有Host入口。生产管理流程仍须接通Platform当前候选/权限适配，不能由请求候选ID造出验证证明。
4. **T12全体验收未完成。** 尚无严格HTTP、默认关闭部署装配、两Vue页面/状态、真实HTTP/client测试及所有DTC-WF-001/008前置证据；DTC父项及T12维持未完成。T13未开始。

## 2. 持久化规则

- 每个DbContext固定非空tenant，实体主键/索引/查询过滤均包含tenant；没有Platform私表访问。
- Draft以tenant+id唯一；更新必须expectedVersion与当前opaque draft version一致。每次保存出新version；published snapshots按tenant+id+序号独立存储，不原位修改。
- Definition发布只接受与当前draft序列化内容一致的编译snapshot，并校验其schema id/version存在于同tenant已发布schema历史；definition不追随schema“最新版”。
- operationId为tenant+WorkflowForm owner内永久唯一。payload SHA-256指纹纳入动作、目标、expectedVersion及业务body，忽略correlation/transport；匹配重试返回原结果为Replayed，变参复用返回IdempotencyConflict，竞争提交由唯一约束/锁与冲突路径收敛。
- 每次实际draft/publish改变在同一MySQL事务新增一条WorkflowForm operation receipt和append-only audit fact；published版本也引用其operation。冲突/验证失败不产生回执或审计。
- DbContext拒绝修改/删除published snapshots、operation receipts和audit facts；draft修改/删除须有本事务对应operation及audit事实。

## 3. 剩余门禁与部署

- Platform/Workflow owner仍须批准Workflow management permission/field map与当前文件引用授权API，方可建立受权HTTP读写和UI。
- 已提交operationId重试可找回原结果；没有暴露Operation查询端点。提交结果未知时调用方使用原operationId/body重试，不生成新operation。
- 此slice未注册Host/DI或任何端点、开关、授权、候选适配，不触碰部署默认值。只有内部WorkflowForm owner存储能力，无可部署管理功能。
- 报告仍须明确不关闭DTC-WF-001/008或父T12，也不代表T13。

## 4. 变更/迁移/回退

- 新增WorkflowForm owner内`wf_*`表与独立`__WesWorkflowFormMigrations`历史表；测试只在已有授权`wes_test`真实MySQL执行迁移。
- 无Host注册/生产配置变更；生产数据库未连接、迁移未应用。
- 新表可通过该owner migration down删除，但不得在含数据环境回退；需先按数据保留政策处理Workflow facts。没有执行Down或生产迁移。
