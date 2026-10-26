# T04 当前授权的固定审计快照导出

- 状态：本切片已验收（软件范围）；Owner Platform。
- 需求：FUNC-PLAT-006 / PAGE-PLAT-008 / DTC-PLAT-008，DTC-UI-011固定水位导出适用子集。
- 依据：[平台设计](../detailed-design/platform-and-security.md)、[固定成员快照](51-t06-platform-audit-snapshot.md)、[工程治理](../03-engineering-governance.md)。

## 已批准范围的有界实现

沿用600秒、最多10000行的Platform固定审计成员快照，不提供任意SQL、实时全库导出或登录安全事件导出。用户先建立快照，再显式选择列并申请CSV；同一个快照按稳定时间/ID顺序读取全部成员，新插入事实不混入文件。不公开内部payload、名称、token、secret、异常或任意用户字符串。

除现有`platform.audit.read`，还需当前`platform.audit.export`对每个成员和所选字段的授权；Own只能覆盖操作者自己的审计，Departments因无权威部门元数据不能放大。列严格白名单Id/OccurredAt/ActorId/TargetId/CorrelationId/Action/BeforeVersion/AfterVersion，Id和OccurredAt必选，2..8个不重复字段。不得借导出恢复读取时被隐藏的字段。空结果仍需对应导出及读取授权，生成带表头的空CSV，不伪造数据。

## 交付、幂等及失败

POST `/api/platform/audit/snapshots/{snapshotId}/export`，JSON显式columns及非空Idempotency-Key。operation永久绑定tenant/actor/session/snapshot/有序列集合；合法当前快照下复用身份但换主体、快照或列拒绝409。不可见tenant/session快照先404；当前权限变化、撤销或过期仍独立拒绝，成功回执不缓存二进制或绕过当前授权。

先逐100行读取/授权/生成有界CSV，再重新核验当前read/export范围、字段及TTL。提交导出身份和一条有限`AuditExported`审计（含原correlation及snapshot引用）后才交付；数据库失败、取消或重新授权拒绝不交付。并发同operation由tenant复合主键竞争，失败后只核验精确既存身份及当前权限，不吞任意数据库错误。回执仅保存UUID、选列和时间，保持不可修改，与审计原子；不包含原始数据或下载地址。允许原operation明确重试取得相同成员/列，不自动重放；审计表示授权交付尝试，不宣称客户端收齐。

CSV为UTF-8、CRLF、逐单元双引号，时间为UTC invariant roundtrip、UUID规范D、Action固定枚举名、null为空。全部输出值来自有限metadata schema，无公式型自由文本。HTTP attachment固定服务端文件名、no-store/nosniff，无Range；前端只在scope/快照/列仍相同才创建临时下载URL并释放。新动作追加枚举尾部，保留既有0..13值。新增独立owner表，不改已有数据/快照或审计记录；不跨模块Infrastructure。

`AuditExported`目标是原snapshot，AfterVersion明确记录本次export operation UUID而非伪造业务实体修改版本；该动作不改原审计。回执复合外键指向同tenant新审计，保存门禁要求同次新增且actor/target/operation/action精确匹配，不能复用旧审计伪造新回执；导出审计照既有机制同事务产生Outbox。快照仍按600秒清理，回执不引用易失snapshot外键、不保存任何行值，保留长期操作防重身份。

## 启用、迁移与回退

当前操作者的审查授权须显式包含`platform.audit.export`及所需字段，且同时持有对应`platform.audit.read`；不自动给既有管理员提权。通过既有受控ReviewedGrants/可委派角色流程授予，不修改部署Feature开关或绕过授权。界面只在业务审计固定快照且独立导出字段可用时显示选择器；登录事件和实时分页仍不提供导出，列/快照/scope变化撤销旧请求，离线不排队，失败明确重试原selection/operation。

新增MySQL迁移`20261026182011_AuditExportReceipts`仅建metadata回执表、tenant/operation复合主键和tenant/audit外键/索引；真实wes_test正向迁移、并发唯一竞争及故障回滚已通过。EF无pending模型变化，SQL已检查无现有业务表修改；CLI脚本默认`__EFMigrationsHistory`不是运行时`__WesPlatformMigrations`，不能直接当生产迁移流程。没有执行生产DDL。回退优先撤销export授权/回滚应用，保留已提交审计及操作身份；已产生回执后不以DROP TABLE删除历史证据。

## 验证及剩余门禁

先实际HTTP RED，再实现owner及传输；真实授权wes_test验证精确列/成员、空结果、权限/字段/Own、重复与换体、并发、TTL边界、当前撤权、真实审计SQL故障/取消无交付。前端先组件RED后接线，严格构建及真实HTTPS Chromium下载校验。详见[测试](112-t04-authorized-audit-export-tests.md)。没有生产连接/现场签字，不以此关闭T04或完整T06。
