# T07 暂存文件目录测试

- 状态：本切片已验收（软件范围）；关联[设计](113-t07-authorized-staging-catalog.md)。
- FILE-CATALOG-01：缺目录路由实际RED；有权目录安全投影、不读取存储或扫描、不增加审计。
- FILE-CATALOG-02：Own/部门/All和逐字段交集；空部门不放大、creator当前部门变更生效、缺字段403。
- FILE-CATALOG-03：tenant/引用隔离在SQL分页前生效，稳定顺序及25/50/100边界；非法offset/limit/state400。
- FILE-CATALOG-04：无授权拒绝、撤销会话拒绝；无匹配是空目录而非依赖失败。
- FILE-CATALOG-05：真实页面显式目录/过滤/分页、metadata形状及重复ID拒绝、旧响应/撤权清理。
- FILE-CATALOG-06：同查询失败保留Stale但不能选择；选择只走现有重新授权详情入口。
- FILE-CATALOG-07：真实HTTPS Chromium目录发现、键盘选择、下载字节与审计、响应式及原流程回归。

## 执行记录

- 首个真实HTTP请求实际405 RED（该路径原来只有POST），客户端实际9项缺目录入口RED后才接实现。新增**14项HTTP通过**：安全metadata/no-store、Own/当前部门/All、缺字段403、状态无匹配、后续空页，以及8个非法offset/limit/state400变体。最初非Departments的授权夹具错误携带部门集，修正测试夹具，未放宽角色领域校验。
- 新增**8项真实MySQL owner通过**：逐字段scope取交集、30条较新越权行不挤占自有文件页、25/50/100与相同时间稳定Id顺序、真实引用/引用计数各自独立排除、creator移出部门即消失、错tenant上下文/已撤会话拒绝、另一tenant相同creator不泄漏。无物理读取/扫描、无审计/Outbox副作用。foreign夹具首次漏建被外键要求的tenant，补建隔离测试tenant，未改外键。原暂存授权/上传与新增目录共同**52项通过**。
- 新增**12项客户端通过**：发现行后按Id重新读取最新版本、同查询503保持Stale且不能选择、换scope丢弃迟到成功、5种非法目录形状/重复Id拒绝、改状态即时清旧行、共享分页/改条数归零、403立即移除metadata、离线不发送且恢复不自动排队。前端全套**23文件、240 passed**；严格vue-tsc/Vite构建通过（79 modules）。
- 真实MySQL/私有存储/HTTPS Kestrel/Chromium：原中文1MiB上传、真实字节进度、scanner连接拒绝不解除隔离继续通过；从目录发现该上传及已有Available种子，键盘Enter选择后重新读取详情、核对目标Id，再真实下载原始safe字节/文件名且只有一条下载审计。375/768/1440横向溢出断言现在位于目录已真实加载之后。无目录/文件API网络替身；Available种子仍不作为真实病毒识别证明。
- 首轮完整no-build暴露Architecture输出目录仍引用旧Platform程序集；全solution重建后实际架构规则又拒绝Host直接引用私有Domain状态枚举。把有限wire状态码的枚举验证/转换留在Platform应用边界，Host只绑定输入；未改架构测试或放宽规则。再次完整Release构建**0警告/0错误**，Architecture定向29项通过。
- 最终对该全solution重建产物执行`dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1 --verbosity quiet --logger trx`：Architecture **29**、Modules **493**、Persistence **515**、Route **228**，共 **1265 passed、0 failed、0 skipped**，含两套真实HTTPS浏览器。TRX仅存会话工作区；格式/diff及新增文档链接检查通过。无新增依赖、schema或部署配置。
- T07业务实体绑定/附件权限、完整业务文件目录、真实ClamAV/病毒库/部署ACL仍未验收；其余工作包与生产/现场许可也不因本片通过而关闭。
