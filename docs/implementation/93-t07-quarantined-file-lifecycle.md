# T07 隔离文件生命周期

- 状态：实施中；不得据本文关闭T07。
- Owner：Platform。依据[文件设计](../detailed-design/platform-and-security.md) FUNC-PLAT-005 / REQ-PLAT-007..009 / DTC-PLAT-007及[PAR-04 / RULE-12](../detailed-design/05-approved-rules-and-defaults.md)。
- 本地原语可独立测试；T04/T06整体验收、真实扫描配置仍是集成关闭前置。

## 状态与证据

文件记录绑定非空tenant/file/creator/correlation及受控名称、媒体类型、实际字节数和SHA256摘要。对象身份由服务端生成，不存用户指定路径。初态Quarantined；没有持久存储确认不能开始扫描。只有当前扫描attempt的Clean回执且字节摘要/长度与记录一致才Available；Infected为Rejected；Unavailable/Timeout维持Quarantined。不能以内容校验、HTTP200或扫描服务未配置替代Clean。

每次扫描产生独立attempt UUID及开始UTC，默认60秒截止；`now >= started+60s`的Clean也拒绝，迟到回执不能清除隔离。新attempt替代旧attempt后，旧成功或失败回执不改变当前状态。时间不得早于上传/上次转换，禁止倒退绕过截止。失败仍可明确重试，AlreadyAvailable不因另一个上传失败降级。

上传后600秒仍Quarantined时显示处理问题；该信号为投影，不释放保护或假定感染。引用只能绑定Available且租户/实体授权由应用owner实时复验；绑定/解绑和扫描/清理需在同owner事务CAS。领域记录只跟踪引用计数，调用owner必须通过唯一引用表保证每条引用一次，不接受客户端计数。

24小时只是孤儿清理的最早门限，不是自动删除许可。只有无当前扫描、零引用、owner在事务内核验引用表后才可进入Deleting；Deleting不再允许开始扫描或新绑定。存储删除失败留Deleting供受控恢复；确认对象删除后Deleted。不删除记录身份，防迟到扫描将其复活。文件已绑定则拒绝清理，不因扫描失败清理其他文件。

## 实施边界

已补领域转换、可验证存储/扫描适配边界、EF映射及实际引用表；后续仍须添加应用owner、API与页面。当前步骤不是完整生命周期交付。所有成功必须绑定真实字节及扫描证据；测试协议监听器只在测试项目，不注册到Host，也不是病毒清除证据。

扫描适配采用ClamAV官方[clamd协议](https://docs.clamav.net/manual/Usage/ClamdProtocol.html)的`zINSTREAM\0`、4字节大端分块、零长度终止块和NUL结束回执。不向扫描器传文件名/路径。clamd的TCP不认证、不加密，因此此首个适配仅允许显式loopback IP及端口，禁止远程主机/DNS，不向第三方传业务内容。部署方须隔离本机扫描socket并验证病毒库更新和`StreamMaxLength >= 20MiB`，本代码不能证明签名库健康。完整字节复制后计算摘要并传相同副本，防调用方缓冲区在await期间变化造成“扫描A、引用B”。只接受精确`stream: OK`为Clean，合法FOUND为Infected，错误/畸形/超长/截断/网络故障为Unavailable，60秒截止为TimedOut；显式日志只有固定失败类别，不含扫描输出或内容。外部取消传播，不伪装扫描结果。60秒包含连接、发送与读取全过程，以TimeProvider驱动。

本地隔离存储要求部署显式提供已存在的绝对目录，必须位于Web静态根目录之外且ACL仅允许owner进程。对象路径只由tenant UUID与file UUID构成，拒绝根/tenant/对象的reparse point，不用原文件名、不提供URL。不覆盖已有对象；失败/中断可能保留部分对象，不能确认Stored，后续恢复按持久记录删除或核验，不能将存在性当成功。读入/下载/扫描前复验实际长度和摘要；删除幂等仅意味着对象缺失，不代表引用检查通过，必须先提交Deleting fence。目录/文件的本机ACL是抵御检查与打开间路径替换的部署前置，代码检查不声称能抵御拥有该目录写权限的本机管理员。

持久化采用Platform自己的`platform_files`与`platform_file_references`，复合tenant键、UTC微秒时间、Version并发令牌。引用以tenant/file/owner/entity复合唯一，且tenant/file外键限制跨租户引用或删除仍有引用文件。owner/entity只是跨模块稳定引用，不与其他模块私表建立外键；owner代码按领域字符串的ordinal身份比较，不能让MySQL默认不区分大小写排序规则把不同owner的引用查询混合，Owner列显式使用`utf8mb4_bin`。文件记录不允许普通物理删除，清理保留Deleted身份。引用计数与唯一绑定必须由应用用例同事务更新，并通过文件Version竞争清理/新增引用；真实MySQL验证同版本下绑定先提交和删除先提交两种顺序，另一方必冲突且不留下部分引用。映射和约束不替代应用授权。迁移只在测试装配应用到授权wes_test，运行时不自动迁移。`PlatformQuarantinedFiles`迁移仅新增这两张表及其tenant键、外键和索引，未改变其他模块schema。

真实Provider复核发现Oracle 10.0.7对标准列`UseCollation`的ALTER没有执行所需的排序变更，其原生`ForMySQLHasCollation`元数据也未生成ALTER。两个失败方案保留在已应用的`FileReferenceOwnerOrdinalIdentity`和`FileReferenceOwnerProviderCollation`迁移历史中，不回写测试库历史。最终`FileReferenceOwnerBinaryColumn`用固定、单列DDL修正新引用表的Owner列，模型同时保留标准和Provider元数据；不接受用户输入构造DDL。测试直接检查information_schema实际列排序规则，并验证大小写不同owner查询不串、复合唯一键也按精确身份工作。迁移Down恢复该列的表默认排序；若已有仅大小写不同的重复引用，数据库必须明确拒绝回滚，不能自动合并/删除绑定。

## 测试与剩余门禁

测试先覆盖60/600/86400秒边界、持久存储前置、错误attempt、摘要不符、时间倒退、引用保护、失败恢复和删除墓碑，先观察编译/行为RED再实现。确切运行结果见[测试记录](94-t07-quarantined-file-lifecycle-tests.md)。真实扫描服务未获得配置/安装授权，恶意软件清除验收保持blocked，不以协议测试冒充扫描。

后续[扫描与孤儿删除协调](101-t07-file-processing-coordination.md)已把持久attempt/删除fence接入存储与扫描端口，补充保存前后中断、迟到结果与并发claim验证。它仍是无Host装配的内部owner入口，不替代本文保留的实体授权、上传/引用/下载、API/页面及真实病毒清除门禁。
