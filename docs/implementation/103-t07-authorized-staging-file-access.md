# T07 当前授权的暂存文件访问

- 状态：实施中；Owner Platform。
- 需求：REQ-PLAT-007..012 / FUNC-PLAT-005/006、RULE-12 / PAR-04，DTC-PLAT-007/008及VAL-05的文件owner访问子集。
- 依据：[文件设计](../detailed-design/platform-and-security.md)、[扫描与删除协调](101-t07-file-processing-coordination.md)、[权限设计](08-platform-access-settings-audit.md)。

## 范围与拒绝边界

在既有内部协调上建立具名暂存文件应用入口，供后续HTTP适配调用；不暴露DbContext或任意文件系统路径。每次操作使用TenantContext及CurrentAuthorization实时复验目录启用、会话、当前角色和字段/数据范围。资源归属取持久文件CreatorId及当前目录部门，不接受请求中的owner、部门、role或“已授权”布尔值。

此入口仅服务未绑定业务实体的Platform暂存文件。FileReferences实际存在或ReferenceCount非零时明确Forbidden，不能把“能读上传者”误当作“能读绑定业务实体”。业务附件须后续由其具名owner额外复验实体授权；本片不建立通用信任任意owner字符串的绕过入口。没有跨租户文件管理特权或隐式共享。

权限与字段：

| 用例 | permission | 必须授权字段 | 资源 |
|---|---|---|---|
| 单文件状态/元数据 | platform.files.read | Id、FileName、ContentType、ByteCount、State、Stored、Version、CreatedAt、ProcessingProblem | 文件持久creator及其当前部门 |
| 受权字节读取 | platform.files.download | Content | 同上；另须具备全部read字段 |
| 明确重试扫描 | platform.files.write | Scan | 同上，调用前和结果返回前当前授权复验 |
| 确认孤儿删除 | platform.files.write | Delete | 同上；内部协调另核验引用、24小时和版本 |

Own只匹配creator；Departments按当前目录部门，All仍限可信tenant。无授权、失效会话、被撤角色、creator目录身份无法解析、绑定文件均不返回元数据/字节。查询单文件不存在返回NotFound；不存在资源不访问存储。无隐含超级管理员。

## 下载与审计

只有Available且Stored才可读取内容；扫描不可用不阻断其他已有干净文件。IQuarantineStore按持久摘要/长度读取后，应用再次核验长度和SHA256，并重新查询当前文件版本、状态、绑定以及当前授权；读取间撤权/绑定返回Forbidden并清零已读缓冲。文件状态/版本已变化返回VersionConflict，无成功内容。

内容交付前显式事务追加FileDownloaded审计，记录固定动作、actor/target/correlation及文件版本，不写文件名、摘要、存储路径、字节或全文请求。审计保存/提交失败不交付内容；存储错误保持安全异常，DB错误安全分类。调用者取消传播并清零未交付内容。成功结果持有可释放的缓冲，禁止默认JSON序列化/ToString泄漏，HTTP适配完成发送后负责Dispose，不落普通DTO/日志。

扫描/删除在持久claim前复验当前权限；不在存储/扫描等待期间持有SQL事务。长操作中权限撤销不能逆转已开始的本机扫描/已授权删除，但不能返回内容/元数据，后续请求必须拒绝。安全状态仍按既有attempt/删除fence落库，不把撤权等同扫描Clean。

## 接线、验证和剩余

PlatformScope提供具名服务工厂，显式传入存储/扫描端口；未配置端口不能静默成功，不默认注册假scanner。现有Host未新增文件路由或生产配置。没有新表、仅扩展既有int审计动作，不改变旧动作数值或历史。

真实MySQL验证Own/部门/All、当前会话/角色撤销、跨租户与引用边界、内容准确/回收、安全审计/失败、读取中撤权及扫描/删除授权。见[测试证据](104-t07-authorized-staging-file-access-tests.md)。完整受权上传、业务引用/下载、HTTP/页面、真实扫描/ACL及T07整体仍待，不以本片关闭T04–T08。
