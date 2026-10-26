# T07 暂存文件客户端测试

- 状态：本切片已验收（软件范围）；关联[设计](109-t07-staging-file-client.md)。
- CLIENT-FILE-01：同源/身份/离线、raw MIME保留、真实上传字节progress、取消及无自动重试。
- CLIENT-FILE-02：body读取后当前身份/grant代际复验，错误/401当前处理；blob不当JSON消费。
- CLIENT-FILE-03：选择/限额/名字/类型，隔离上传确认不显示Available；503/取消只有显式原operation重试。
- CLIENT-FILE-04：读取/扫描/下载/删除当前字段权限、版本冲突、Unknown扫描进度和隔离不能下载。
- CLIENT-FILE-05：scope更换/撤权/卸载清草稿与迟到响应；无文件/业务行持久缓存；临时下载URL释放。
- CLIENT-FILE-06：真实HTTPS Chromium上传中文名、显示隔离、进度/键盘/窄屏、显式读取、扫描不可用仍隔离。

## 实际执行

- 共享传输4项先实际RED（raw MIME被JSON覆盖、缺少二进制下载及真实progress），实现后通过。页面14项验证隔离上传、UTF-8名字、当前字段、503原operation显式重试、取消中断、离线不发送/恢复不排队、扫描仍隔离、删除确认、403清草稿、迟到响应/字节丢弃、失败刷新使旧行stale且禁用所有动作、临时URL释放及未匹配查询目标清行。其中编辑UUID未清旧行先RED，再修复同步目标门禁。
- `npm test`：**22文件、220 passed**；包含新增4项共享传输、14项页面及1项导航能力门禁。`npm run build`：严格vue-tsc及Vite生产构建通过，72 modules；无新增依赖。
- 真实授权MySQL/私有临时存储/HTTPS Kestrel/Chromium用例 `RealBrowserUploadsQuarantinedFilesAndDownloadsOnlyAvailableAuditedBytes` 通过：真实身份登录、模块目录/导航、1MiB中文名文件XHR上传、采样真实非零上传进度、隔离元数据、不可用scanner保持隔离及Unavailable、取消删除确认、375/768/1440无横向溢出、键盘Tab可见焦点和Enter下载、文件名及原始字节校验、无业务持久缓存/页面异常。仅最后下载使用明确标记的领域Available种子；独立确认数据库下载审计恰好1条，不将种子当病毒识别证明。浏览器无API route.fulfill替身。
- 首次新浏览器测试404导航暴露测试fixture过早捕获FeatureSwitches；仅Files测试装配从最终配置重建snapshot，未更改生产feature合同。另一次程序化focus不能证明键盘focus-visible，改用真实Tab并保留焦点断言；未删除无障碍断言。
- 最终 `dotnet test WES.slnx --configuration Release --no-restore -m:1 --verbosity quiet`：Architecture **29**、Modules **471**、Persistence **493**、Route **228**，共 **1221 passed、0 failed、0 skipped**。包含原平台浏览器和新增文件浏览器，Files spec仅在明确文件fixture启用，不能把普通fixture跳过该spec计作文件验收。C#修改文件已格式化，无新增迁移。
- 真实ClamAV病毒库/识别/健康及部署ACL、业务实体绑定和附件授权、完整PAGE-PLAT-007列表及其他页面DTC仍未验收。此片不关闭T04/T05/T06/T07/T08，也不批准生产连接或安装服务。
