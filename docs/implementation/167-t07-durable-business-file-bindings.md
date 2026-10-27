# T07 持久具名附件绑定与解绑

- 状态：本写续片已实现并验证，不作为 T07 整包关闭依据。
- 需求：REQ-PLAT-007..012 / FUNC-PLAT-005，DTC-PLAT-007/008/014、RULE-12 / PAR-04。
- owner：Platform 拥有引用、文件 fence、审计和回执；Wms 拥有实体存在性、来源及当前授权。
- 基线：[具名读取](165-t07-entity-authorized-file-reads.md)、[平台设计](../detailed-design/platform-and-security.md)、[契约](../contracts/01-business-and-execution-contracts.md)。

## 用例和授权

补固定仓库/库位附件绑定和解绑应用/HTTP，不接受任意 owner、跨租户、对象路径或调用者“已授权”标记。复用具名读取的类型码。WMS 在自己内部按真实创建来源解析实体 creator，要求完整实体 read 和同一实体当前 `wms.warehouses.write` 或 `wms.locations.write` 的明确 `Attach`/`Detach` 字段；创建的 Code/Name 权限不自动包含附件写权限。只返回有限授权决定，不向其他 owner 暴露来源或私表。

Platform 另以文件持久 creator/当前部门核验 `platform.files.write` 的同名 Attach/Detach 字段；文件 read/download 不隐式授权写，附件写也不隐式授权文件 read。每次新操作和旧回执恢复都核验当前主体、会话及两个 owner 的权限。回执不是授权缓存。

给已绑定文件增加另一个引用时，还必须复验全部已有来源实体的当前 read：不能在失去来源实体权限后，仅凭上传者/文件 Attach 权限把文件搬到自己能读的目标，绕过原附件权限。来源取持久引用行，不接受请求提供；Host 仅识别固定仓库/库位两类，未登记的旧泛 owner 来源明确拒绝，不猜默认策略。允许当前拥有全部来源和目标权限的多引用；解绑只需其精确目标的当前 Detach，不删除/泄露其他来源引用。

## 原子性、并发与永久操作

操作输入为 tenant/actor 可信上下文、固定目标、fileId、operation UUID、correlation UUID、原样 expectedVersion 和明确 Attach/Detach。缺版本428、格式非法400、版本冲突412、已是目标成员状态409；只有 Available/Stored 可改变引用。开始新操作前实际引用总数必须等于文件计数，证据不一致显式安全失败，不修成成功默认值。

在 Platform ReadCommitted 事务内复验权限、原版本与精确引用；引用增删、计数与新 Version、FileAttached/FileDetached 审计和永久 FileBindingOperation 同时保存。版本 CAS 与唯一引用/操作键解决竞争，失败无部分提交；并发冲突回滚后只查询已提交原操作、重新授权恢复，不盲目自动执行新写入。

真实 MySQL 强制交错证实：两个事务先插子引用再升级文件更新锁会得到1213。写入口因此在插子记录前以参数化、精确 tenant/file 的 `FOR UPDATE` 锁住单个文件；provider SQL 只留在 Platform Persistence。等待锁后重新读取文件、原操作和当前双 owner 权限，再核版本/成员，不使用旧授权快照。锁不跨存储或扫描 IO，也不锁 WMS/Platform 授权表；取消与命令超时继续传播。SQL Server/PostgreSQL 仍按 ADR-0002 延后，不声称该锁实现跨 provider 等价。

新增 owner 本地永久操作表，tenant+operation 唯一，记录 actor、固定类型/实体/file、动作、原 expectedVersion、原结果版本、关联 audit/correlation 和时间，均是有限非秘密元数据。另一个 actor 或换任意语义参数复用键409。相同键返回原三字段回执，不再增减计数或追加审计；不使用90天 Directory 回执冒充永久防重。

回执只证明原操作已提交，不保证成员关系后来未变化；未知结果必须冻结原键/目标/版本手工恢复，得到回执后显式重新读取当前附件。WMS 当前授权是明确查询时点，不能声称跨 owner ACID 或锁住后来发生的撤权。

## HTTP、迁移与回滚

仍由 WMS 读装配和 Files 既有部署开关共同控制、默认关闭。固定 `warehouses`/`locations` 的已有 `/files/{fileId}` 路径：PUT 绑定、DELETE 解绑，精确 body 仅 `expectedVersion`；可信 HTTPS/Bearer、单值合法 X-WES-Client 与 Idempotency-Key。不从 body 接收主体或类型。成功仅 `{id,version,attached}`；初次绑定201，重放及解绑200。无服务器排队/自动重试。

迁移只增加 Platform 本地表/外键/索引，不改旧文件状态或推断旧泛 owner 类型；新增审计枚举追加在末尾，旧值不动。EF 迁移先评审 SQL，再在授权隔离 wes_test 验证；绝不自动应用生产。回滚软件可撤去写端点；已有永久回执/审计及引用须保留，不把数据表 Drop 当作普通运维回滚。

## 验证与边界

见[测试记录](168-t07-durable-business-file-bindings-tests.md)。真实 MySQL/API 覆盖当前交集、原子失败、永久重放/参数冲突、版本/成员状态、竞争、撤权恢复、清理 fence 与新旧读取。业务附件目录/客户端、真实 ClamAV/病毒库/ACL、完整父 DTC 与 T04–T08 整包仍需独立验收。
