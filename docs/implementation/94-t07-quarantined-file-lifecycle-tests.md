# T07 文件生命周期测试记录

- 状态：实施中；关联[设计](93-t07-quarantined-file-lifecycle.md)。
- FILE-LIFE-01：存储未确认不能扫描；Clean当前attempt且内容证据一致才Available。
- FILE-LIFE-02：Infected拒绝，Unavailable/Timeout仍隔离；失配/未知结果拒绝。
- FILE-LIFE-03：59.999999秒可通过，60秒不通过；599.999999秒无处理问题、600秒有。
- FILE-LIFE-04：旧attempt的成功/失败不污染新attempt；时间倒退拒绝。
- FILE-LIFE-05：Available才可引用；引用必须解除且24小时后经owner核验才允许删除；正在扫描禁止清理。
- FILE-LIFE-06：Deleting禁止新绑定/扫描，存储确认删除后Deleted且不能复活。
- FILE-LIFE-07：tenant/creator/correlation/文件身份、长度、摘要、声明的非法值明确拒绝。

## 执行证据

1. 领域与协议/存储测试先观察缺失类型编译RED，再分别实现。最新定向命令：

   ```powershell
   dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~LocalQuarantineStoreTests|FullyQualifiedName~ClamAvFileScannerTests|FullyQualifiedName~QuarantinedFileTests|FullyQualifiedName~FileContentValidatorTests" -m:1
   ```

   **78 passed、0 failed、0 skipped**：内容校验38、生命周期17、ClamAV协议16、本地存储7。新测试40项，不把原38项重复计为新增。

2. 协议测试使用测试进程内TCP监听器验证实际INSTREAM字节、64KiB分块、精确Clean/FOUND、畸形/截断/超长/连接失败；手动TimeProvider驱动60秒截止，并分别验证调用者取消传播、缓冲区在await期间被修改时扫描与摘要仍来自同一快照。监听器不是ClamAV引擎，不证明病毒识别/病毒库健康。

3. 本地存储测试使用每例唯一临时目录，验证精确字节/hash、tenant路径隔离、不覆盖、篡改/截断拒绝、幂等tenant内删除、20MiB/+1和取消/配置拒绝。清理只删除该例创建的具体目录。不声称这些测试覆盖本机管理员可写ACL下的路径竞态或进程崩溃后的应用恢复。

4. 持久化测试先观察缺少FileReference编译RED。使用已锁定EF工具和无连接设计时工厂生成迁移；首次附加不受支持的`--no-connect`参数明确失败，移除后成功，无安装或生产连接。检查Up仅创建两张文件表、复合tenant键、引用约束和两个索引；Down先引用表后文件表。

   ```powershell
   dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~QuarantinedFilePersistenceTests -m:1
   ```

   初始真实授权`wes_test`上7项通过：UTC/状态/扫描证据roundtrip与tenant读隔离、两个context的Version CAS、复合唯一引用及跨tenant外键拒绝、跨tenant写/普通物理删除拒绝、显式事务回滚，以及引用新增与孤儿删除两种提交顺序。失败的一方整个SaveChanges回滚，不留下引用与计数不一致。

   追加owner代码精确身份测试，先观察`Owner == "platform"`错误读出`Platform`引用的真实RED；分别使用标准和原生Provider列排序元数据后仍RED。读取生成SQL确认原生元数据迁移只有历史插入、没有ALTER，未将EF命令成功当作实际schema修正。保留已应用历史，添加固定单列DDL迁移后，直接检查information_schema中的`utf8mb4_bin`、精确owner查询及两个大小写不同owner的唯一绑定，最终 **8 passed、0 failed、0 skipped**。此测试不是事后内存过滤，数据库本身隔离owner身份。

5. 所有新增/修改手写C#的`dotnet format WES.slnx --no-restore --verify-no-changes --include <修改路径>`通过。扩大检查到新迁移时，发现EF模板的BOM/块namespace不符合仓库规范；使用现有formatter仅修正四个新迁移，再验证全部修改C#通过。模型与snapshot无pending变化，最终迁移脚本已核对只包含固定单列ALTER和迁移历史。未增依赖、未注册Host扫描器或启用上传/下载端点，未改变生产配置。

6. 最终完整串行Release回归：

   ```powershell
   dotnet test WES.slnx --configuration Release --no-restore -m:1
   ```

   首次完整回归1016项；追加owner大小写用例并修复后，最终完整回归Architecture **29**、Modules **384**、Persistence **376**、Route **228**，合计 **1017 passed、0 failed、0 skipped**。较上批969新增48项，等于本批40项本地/协议及8项真实MySQL。未修改Ops Web，本批未另跑其Vitest/类型构建，不能把历史201项当本批独立执行。

## 仍未通过的整体门禁

实体实时授权、上传/扫描/引用/清理应用协调、存储中断/进程恢复、真实病毒清除、HTTP/页面仍未作为通过证据。当前机器未发现ClamAV/Broker工具或服务；另检查内置Windows Defender命令、服务及标准工具路径也无可用扫描能力。没有据此安装系统服务、降低扫描门禁或生成成功fallback。真实扫描仍blocked，T07整体不关闭。
