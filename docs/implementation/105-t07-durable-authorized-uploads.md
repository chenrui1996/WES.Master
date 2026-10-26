# T07 受权上传与持久操作身份

- 状态：本切片已验收（软件范围）；T07整包仍实施中；Owner Platform。
- 需求：REQ-PLAT-007..012 / FUNC-PLAT-005/006，RULE-12 / PAR-04、DTC-PLAT-007/008及VAL-05上传子集。
- 依据：[当前暂存访问](103-t07-authorized-staging-file-access.md)、[文件状态与存储](93-t07-quarantined-file-lifecycle.md)、[批准规则](../detailed-design/05-approved-rules-and-defaults.md)。

## 命令、身份和授权

上传只创建未绑定暂存文件，当前可信tenant/session/actor由Platform CurrentAuthorization核验；`platform.files.write`必须授权`Upload`，资源owner为当前actor、部门为其当前目录部门，不采信客户端owner。新文件可被Own范围上传，不要求All。拒绝越权后不访问对象存储或建立操作身份。

命令包含非空operation/correlation、显示文件名、声明MIME及已由传输层限定大小的字节；应用层仍明确拒绝0或超过20971520，并在首次await前复制同一内容快照，用现有扩展名/MIME/基础签名/UTF-8联合校验，不舍弃现有安全拒绝。名称还必须满足领域255字符/无路径或冒号/末尾空白点规则，校验入口和持久实体一致，非法输入不变成领域异常。持久MIME从已校验声明取canonical小写类型，不保存可选charset参数。

文件新增可空UploadOperationId以兼容已有隔离记录；本入口必填，tenant/operation唯一。永久保留原file、actor、文件名、canonical type、实际长度、摘要及创建证据，普通业务不可修改，Deleted也不删除身份。重放需actor/名字/type/长度/hash全部精确相同，correlation是本次请求证据不改原历史。换payload/actor复用operation显式IdempotencyConflict；跨tenant同operation独立。新operation表示新的上传，不以hash去重不同业务文件。

## 非ACID与恢复

1. 干净owner在ReadCommitted显式事务复验当前Upload权限，查询永久身份。新命令原子保存Quarantined、Stored=false文件和FileUploadCreated审计，提交后才触碰存储；审计/记录失败无对象写入。UUID由server生成，不接受对象路径。
2. 只向现有IQuarantineStore执行不可覆盖Write。失败为Unavailable时仅允许调用ReadVerified核验该相同file的已存在精确字节：对象可能由前次成功写入而数据库未确认；只有真实长度/hash一致才继续，不把路径存在当成功。缺失/不可访问/部分对象或错误摘要保持明确存储失败及隔离记录，不覆盖、不自动换file或operation。
3. 写确认/验证证据必须与上传快照相同，取消传播；之后重新读取文件及实时授权。权限撤销、已绑定或Deleting/Deleted不确认成功。ConfirmStored和FileUploadStored审计在另一短事务原子提交；故障保留原隔离记录及可能已存在对象，用同operation明确重试验证，不删除记录或掩盖未知结果。
4. 已Stored的精确重复上传仅返回原file当前状态/版本，不重写、不重新扫描、不新增上传审计；新绑定业务文件拒绝暂存重放。并发同operation只一个记录，唯一竞争回滚后精确查原语义；不能把任意1062当成功。确认CAS竞争只核验已Stored的原记录，否则VersionConflict，不自动重复物理动作。

成功结果表示已持久隔离/存储证据，不代表扫描Clean或业务附件已成功。上传不自动调用scanner；之后用已有明确受权Scan，未配置/不可用扫描不Available。未交付私有快照清零，普通日志无文件名/摘要/内容。不能提供默认None存储或成功scanner。

## 迁移、审计与边界

仅修改Platform文件表：可空UUID列、tenant/operation唯一索引；已有NULL记录不填假操作或合并。Down丢弃永久上传防重语义，仅空隔离库/owner批准离线回退，不在线恢复已删除文件。固定审计动作追加到已有int枚举末尾，原0..11不变，无通用请求payload。

测试先红后绿，真实授权wes_test及LocalQuarantineStore验证内容、身份、审计原子、写后确认失败/取消恢复、跨tenant与并发；详见[证据](106-t07-durable-authorized-uploads-tests.md)。本片不含HTTP上传流限额、业务实体绑定、页面、真实病毒清除或部署ACL验收，不关闭T07/T04–T08。
