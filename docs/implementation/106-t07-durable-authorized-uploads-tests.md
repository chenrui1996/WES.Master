# T07 持久受权上传测试

- 状态：本切片已验收（软件范围）；关联[设计](105-t07-durable-authorized-uploads.md)。
- UPLOAD-01：当前Upload权限、目录/会话/数据范围，非法名字/类型/0/M+1拒绝；无权限无存储副作用。
- UPLOAD-02：记录及创建审计先于外部写；快照/摘要/长度/type一致，成功仍Quarantined；只一次操作身份。
- UPLOAD-03：同operation精确重放不写/不扫描，换payload或actor冲突，跨tenant身份独立，终态不复活。
- UPLOAD-04：存储不可覆盖，写后确认失败/取消可用相同operation核验已有精确字节恢复；部分对象失败不伪装成功。
- UPLOAD-05：并发相同operation只有一个文件；记录/审计原子，CAS拒绝不自动重复物理动作。
- UPLOAD-06：普通修改操作身份或原始元数据拒绝，审计安全，不输出文件内容或路径。

## 红绿与执行证据

- 先运行新增上传测试，观察缺少UploadOperationId/UploadAsync/FileUploadResult的编译失败；实现后继续补故障和并发反例。统一名字校验前，冒号、末尾空格/点及超过255字符的4个反例实际失败，复用领域显示名规则后转绿。
- `AuthorizedFileAccessTests`新增23项上传测试，与原21项当前授权访问共同通过44项。包含同operation另一actor/精确文件名冲突、另一tenant独立身份和真实20971520字节精确边界；20MiB成功仍Quarantined，不将存储成功当Clean。
- 使用真实LocalQuarantineStore验证CreateNew不覆盖、确认保存失败/取消后核验原字节恢复、部分对象不能覆盖或伪装Stored；并发只能一个永久记录。测试只清理各自随机tenant及自己的临时存储目录。
- 与`QuarantinedFilePersistenceTests`同runner定向共 **72 passed、0 failed、0 skipped**。内容校验反例纳入Modules完整回归。
- 新迁移`20261026171406_FileUploadOperationIdentity`经真实授权wes_test运行；EF检查无pending model变化。逐段生成的SQL仅增加可空varchar(36) UploadOperationId及tenant/operation唯一索引，没有改写旧记录或其他owner表；运行时继续使用Platform专用迁移历史，CLI脚本中的默认历史名不是生产装配。
- 最终串行命令：`dotnet test WES.slnx --configuration Release --no-restore -m:1 --verbosity quiet`。Architecture **29**、Modules **424**、Persistence **493**、Route **228**，合计 **1174 passed、0 failed、0 skipped**；格式化及`git diff --check`通过，无新增依赖。

这些结果不是HTTP流限额、真实扫描或完整T07验收；业务实体绑定/受权附件下载、API/页面、部署ACL与真实病毒库健康仍未验收，T04/T05/T06/T07/T08不因此整体关闭。
