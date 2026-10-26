# T07 暂存文件当前授权测试

- 状态：实施中；关联[设计](103-t07-authorized-staging-file-access.md)。
- FILE-AUTH-01：当前session/目录/角色必须有效；Own/当前部门/All准确，跨tenant及绑定文件拒绝。
- FILE-AUTH-02：完整read字段与独立Content下载权限；扫描和删除分别要求Scan/Delete，不以读权限写文件。
- FILE-AUTH-03：只有Available/Stored可交付精确字节；内容不进入JSON或ToString，Dispose清零缓冲。
- FILE-AUTH-04：存储读取期间撤权、创建引用或版本变化，重新复验后拒绝且无字节结果。
- FILE-AUTH-05：FileDownloaded审计必须提交后才交付；数据库故障/取消不返回内容或泄露原文。
- FILE-AUTH-06：受权扫描/删除复用既有持久协调，外部端口前授权，无默认scanner成功。

## 红绿与结果

先加入授权/字节/审计测试，执行后观察缺少FileDownload、FileAccessDecision和应用方法的编译RED。实现具名StagingFileAccess及PlatformScope工厂、可释放字节结果和既有int枚举末尾的FileDownloaded后，首批15项真实MySQL测试通过；再补当前部门变更、读取间版本变化、错误存储字节、受权扫描/删除及审计保存后取消，共 **21 passed、0 failed、0 skipped**。

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~AuthorizedFileAccessTests|FullyQualifiedName~QuarantinedFilePersistenceTests" -m:1
dotnet test WES.slnx --configuration Release --no-restore -m:1
```

- 文件当前授权21项及原生命周期/协调28项合计 **49 passed**。
- 完整串行Release：Architecture **29**、Modules **420**、Persistence **470**、Route **228**，合计 **1147 passed、0 failed、0 skipped**。格式化及`git diff --check`通过。
- Own只能读真实creator；Departments按目录当前部门核验，creator迁出授权部门立即拒绝。角色禁用/会话撤销不返回元数据，缺Content或完整read字段不访问存储，其他tenant文件NotFound。
- 即使ReferenceCount错误为0，存在真实FileReference也禁止暂存下载。存储读期间撤权或新增绑定不交付内容、不写成功审计，已读数组明确清零；无引用的文件Version变更返回VersionConflict，不交付旧快照。
- 只有Available且Stored才读取；Quarantined下载不访问存储，明确受权扫描提交Clean后才能下载。读权限不授予Scan/Delete；合法Delete通过持久fence产生Deleted，不调用scanner。
- 下载返回精确`safe`字节，普通JSON和ToString不包含内容；Dispose后原保留memory全0。错误字节拒绝且清零。审计写失败安全异常不含注入敏感原文；真正SaveChanges后取消使显式事务回滚、无审计且无字节结果。
- 审计现有int数值保持0..10，新动作FileDownloaded追加为11；查询使用Enum.IsDefined验证，现有审计页按数值展示/筛选，没有另一个静态动作白名单需要同步。本片无模型/迁移变化，未执行生产配置或依赖安装。

测试仅授权wes_test及本例随机tenant，自行创建目录/角色/session，不借用真实业务用户；清理限本例文件/引用/audit及目录fixture。当前不代表HTTP/API/页面或真实病毒清除验收，实体绑定授权/上传/完整T07仍未完成。
