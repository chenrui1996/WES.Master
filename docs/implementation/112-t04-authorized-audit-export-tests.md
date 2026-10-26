# T04 固定审计快照导出测试

- 状态：本切片已验收（软件范围）；关联[设计](111-t04-authorized-audit-export.md)。
- AUDIT-EXPORT-01：固定成员全部导出、稳定顺序、显式列/UTC/空值、无payload/secret；空结果只有表头。
- AUDIT-EXPORT-02：当前独立export及read字段、Own/All、不放大Departments，跨tenant/session及撤权拒绝。
- AUDIT-EXPORT-03：相同operation同体仅一条回执/审计，换列/快照/主体冲突，并发无重复；原operation失败后明确恢复。
- AUDIT-EXPORT-04：600秒精确边界、准备中撤权/取消、审计SQL失败不交付且无孤立回执。
- AUDIT-EXPORT-05：HTTP客户端标识/身份/操作UUID、白名单列验证、attachment/no-store/nosniff和不可改审计。
- AUDIT-EXPORT-06：页面显式快照/列选择、scope/列/快照改变丢弃迟到字节、临时URL释放、离线不排队、真实浏览器下载及持久审计。

## 实际证据

- 新HTTP合同先实际6项RED，随后接线。**8项HTTP通过**：105条跨页固定成员、并发插入不混入、显式列/安全headers、同operation字节相同且审计1条、缺export字段403、非法列/必选列/重复列400、缺operation400、换列/快照409、换session404、成功后600秒边界410。缺operation反例首次错误使用自动补operation的既有测试Client，改用真实不带该handler的Client，未放宽服务端要求。
- **14项真实MySQL owner通过**：跨页、空文件表头、Own/Departments不放大、不借export恢复read隐藏字段、审计保存前失败恢复、两个owner同operation恰好一审计/回执、撤权后重放拒绝、取消不建审计、实际审计INSERT后故障使回执/审计/Outbox全部回滚、读取实际SQL中撤销export后丢弃准备数据。旧审计复用构造第二回执先RED，再增加同次新增审计匹配保存门禁。最终补验可信另一tenant不能解析原快照，以及UTC时间/空metadata的精确CSV字节表示。
- 105条用例暴露Oracle Provider对Guid数组Contains参数的真实NullReferenceException，复用已有`ScopeExpressions.MatchIds`修复，未加catch/fallback或改为客户端全表过滤。
- 页面测试先4项RED（实际页面缺选择器/导出表单）。新增**8项客户端测试通过**：显式快照及选列/URL释放、独立export授权、503原operation明确重试、撤权或改查询时abort/迟到字节丢弃、错误媒体类型拒绝、选列改变建立新身份、离线不发送/不排队。前端全套 **23文件、228 passed**，严格vue-tsc/Vite构建通过（75 modules）。
- 既有真实HTTPS Kestrel/Chromium平台流程已扩展：服务器显式授予export字段；浏览器时钟对齐该隔离fixture的确定性服务器时钟，不替换API/下载。第三页上显式选择Action，实际CSV为61行，表头与60个原快照ID逐序一致、所有动作RoleChanged；检验服务端固定文件名并删除临时下载，375/768/1024/1440无横向溢出；最后数据库确认仅1个回执及1个AuditExported。原设置写入/审计、用户创建/角色/禁用及登出仍通过。
- 两次完整回归中既有1秒依赖健康探测出现间歇性失败，未改健康合同、增加重试或隐藏失败。独立定向及完整Modules479随后通过。最终对**当前已编译源代码**执行`dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1 --verbosity quiet --logger trx`：Architecture **29**、Modules **479**、Persistence **507**、Route **228**，共 **1243 passed、0 failed、0 skipped**；包含两个真实浏览器场景，TRX仅存会话工作区。此前失败保留为环境时序风险，不据此次通过宣称该探测从不波动。
- EF无pending模型变更，新增迁移SQL只建export表/键/外键/索引；无新依赖。此片不关闭T04/T06父DTC整体，生产、DBA/运维/人工验收及其余工作包剩余项仍按就绪台账。
