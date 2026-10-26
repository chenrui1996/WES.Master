# T07 暂存文件HTTP测试

- 状态：本切片已验收（软件范围）；关联[设计](107-t07-staging-file-http.md)。
- HTTP-FILE-01：默认不开放、启用配置严格校验、私有根与静态根不得重叠，缺能力不成功fallback。
- HTTP-FILE-02：当前Bearer/HTTPS/client、Upload字段授权；缺名字/operation/非法MIME或名字拒绝，无持久副作用。
- HTTP-FILE-03：真实body M精确成功、已知/未知M+1拒绝、短读/取消/IO异常不建身份、缓冲清零。
- HTTP-FILE-04：上传仍隔离、精确重放原身份/版本、换payload409、安全响应无摘要/路径、no-store。
- HTTP-FILE-05：当前read/download、隔离409、Available精确字节/attachment/nosniff、审计；撤权403/跨tenant404；响应执行失败也释放。
- HTTP-FILE-06：scan/delete版本/权限/最小年龄、未配置实际扫描服务不Available，绑定文件403。

据实追加执行、红绿和Provider/真实Kestrel证据；不将协议测试或预置Available当病毒清除。

## 执行证据

- 新上传和大小测试先实际返回404（预期200/413），确认未实现路由而非错误断言。接通后配置装配采用最终DI IConfiguration，避免WebApplicationFactory装配配置晚于注册时被误当关闭；未开放部署仍404。
- 另追加中文线协议反例：真实HTTPS上传保存了百分号字面名，非法`%FF`/`%2G`还返回200；三项实际RED后，以一次规范UTF-8百分号解码/往返校验修正，复跑通过。
- 定向`dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-restore --filter "CaseId~HTTP-FILE" -m:1 --verbosity quiet`：**46 passed、0 failed、0 skipped**。其中23项HTTP/MySQL/真实存储，23项流/配置；后者验证未知长度仅读取20971521字节即413，而非读完整30MB再拒绝，以及短读/声明不符/取消/IO失败/所有自有分配清零。
- 临时自签证书真实HTTPS Kestrel动态loopback端口启动后先验证`/health/live=200`，HttpClient仅信任该证书thumbprint；chunked中文文件名往返及20971520成功/20971521拒绝，无第二个超限文件身份。服务器生命周期随测试结束释放，不留监听进程。
- 当前Bearer/HTTPS/client、Upload字段撤权、跨tenant同creator、隔离下载409、Available附件字节/审计/nosniff/no-store均通过。Available由测试种子模拟，只验证传输/授权，不是病毒清除。直接执行真实owner下载结果，模拟响应写失败和正常写，两者均清零保留的内容视图。
- 删除私有根后上传503且安全ProblemDetails不含路径/文件名，恢复目录后原operation完成同一记录；破坏Available对象返回500、不传损坏内容、不生成下载审计。绑定但不监听的本机socket用于确定性扫描Unavailable，HTTP响应仍Quarantined；600秒处理问题、24小时后重新登录并显式版本孤儿删除均通过，不将不可用scanner当Clean。
- 集成中修正测试另一tenant种子缺目录主键的外键失败；没有放宽Provider约束。首次完整回归还发现Roslyn架构编译缺少显式Routing using，补生产文件using而未修改/放宽架构测试。
- 最终`dotnet test WES.slnx --configuration Release --no-restore -m:1 --verbosity quiet`：Architecture **29**、Modules **470**、Persistence **493**、Route **228**，合计 **1220 passed、0 failed、0 skipped**。修改文件完整格式验证通过；无新增依赖或迁移，测试仍只使用授权wes_test及各自随机tenant/临时目录。

未验收：Files页面/实体附件回填与绑定owner、真实ClamAV健康/病毒库/EICAR识别、部署ACL与存储映射评审；现有全Host readiness门禁不因本切片自动变健康。T04/T05/T06/T07/T08整包仍partial。
