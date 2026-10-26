# T07 文件扫描与孤儿删除协调

- 状态：实施中；Owner Platform。关联REQ-PLAT-007..009 / FUNC-PLAT-005、DTC-PLAT-007及PAR-04 / RULE-12。
- 依据：[生命周期设计](93-t07-quarantined-file-lifecycle.md)、[批准规则](../detailed-design/05-approved-rules-and-defaults.md)、[工作包](../superpowers/plans/2026-10-23-wes-development-work-packages.md)。

## 范围和前置

本片只接通内部扫描及确认孤儿删除协调，不新增上传、下载、绑定、HTTP或后台定时入口。调用者必须是已完成实体实时授权或经部署许可的Platform owner；不能将此内部入口直接映射请求或把tenant相同当成实体授权。未装配Host，不授予扫描/清理的生产许可。上传元数据与存储确认仍由后续受权上传协调完成。

采用现有IQuarantineStore和IFileScanner，不复制本地存储/ClamAV协议逻辑，也不提供未配置扫描成功的替代。开始扫描或删除前必须保存对应文件Version fence；外部IO发生在已提交的短数据库写入之后，不能将外部存储和MySQL伪装为ACID事务，也不在网络等待时持有SQL事务。

## 扫描流程

1. 干净且tenant显式的owner读取记录；要求非空file/opaque UUID版本、Stored和Quarantined。Available/Rejected已有终态只返回持久状态，不再扫描或降级。当前60秒内活跃attempt不能被覆盖；过期attempt可由新版本明确重试。
2. BeginScan并保存CAS后才读取存储。取得内容后核验实际字节数和SHA256，使用同一副本扫描，扫描证据还必须精确匹配该内容和持久记录。不能因适配器返回Clean就信任不同摘要。
3. 60秒合作取消预算覆盖存储读与扫描，按已持久attempt的原始截止计算剩余时间；慢数据库保存不能再赠送60秒。预算已耗尽时不开始外部IO。外部取消传播，不写成功。自身预算超时作为TimedOut，记录仍隔离。即使适配器迟到返回Clean，领域截止仍强制TimedOut。
4. IO后清除本次跟踪并重新读取记录，只有当前attempt且仍Quarantined才能应用结果。新attempt、删除fence或其他终态使旧结果明确ObsoleteAttempt，不改现状。完成保存CAS冲突返回VersionConflict，不重放物理动作。
5. 存储失败留已持久化的attempt/隔离记录供60秒后明确恢复；错误直接使用既有安全存储异常。摘要证据错误是安全固定异常且不放行。取消/进程退出也不凭空标Available；600秒处理问题仍由原领域投影负责。

## 删除与恢复

读取无引用记录，既检查ReferenceCount又查询唯一引用表；24小时只是最早门限，活跃扫描及有引用均拒绝。先保存Deleting fence再调用幂等存储删除；对象确认缺失后重新读取并CAS保存Deleted墓碑。Deleting期间不能新绑定或开始扫描，已有迟到回执不能复活记录。

存储中断保留Deleting；对象已删除而墓碑保存失败时也保留Deleting，后续使用当前版本重试删除缺失对象并确认墓碑。Deleted重复处理不做外部IO。确认阶段仍核对引用证据，不以客户端布尔值或仅计数确认无引用。不扫描/删除其他tenant对象，不以内部错误变成功结果。

## 验证与剩余门禁

测试使用真实授权wes_test、独立context验证外部IO前fence确已持久化，并用受控端口模拟清理失败、迟到扫描与取消；它们是协调故障注入，不是真实病毒清除证明。数据库异常按既有瞬时分类包装，不暴露provider原文；无自动重试、无新增依赖/迁移。证据见[测试记录](102-t07-file-processing-coordination-tests.md)。

实体实时授权、受权上传/引用/下载、API/页面、真实ClamAV健康/病毒库及部署存储ACL仍是T07完整关闭前置，本片不关闭T07或T04–T08。
