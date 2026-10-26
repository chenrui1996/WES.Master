# T07 暂存文件HTTP传输边界

- 状态：本切片已验收（软件范围）；T07整包仍实施中；Owner Platform/Host。
- 需求：REQ-PLAT-007..012、FUNC-PLAT-005/006；DTC-PLAT-007/008、PAR-04/VAL-05的暂存传输子集。
- 依据：[持久上传](105-t07-durable-authorized-uploads.md)、[当前暂存授权](103-t07-authorized-staging-file-access.md)、[平台详细设计](../detailed-design/platform-and-security.md)。

## 范围与装配

沿用现有HTTPS/Bearer/current-session、写入X-WES-Client白名单和关联UUID，不允许客户端tenant/actor/path。仅暴露未绑定暂存文件；实体附件绑定和下载仍需业务owner授权，不能以上传者权限放开。

新增显式默认关闭的Wes:Files部署配置：Enabled、StorageRoot、ScannerAddress、ScannerPort。启用需Identity开启、已存在绝对私有目录和明确loopback scanner IP/端口，不安装或启动系统服务。配置项、类型和边界严格校验，即使关闭也不忽略非法已提供值。私有目录不得与Host ContentRoot/WebRoot及配置的ops-web静态根重叠；实际ACL和其他进程公开映射仍由部署验收，不能靠路径检查代签。模块提供具名本地adapter工厂及安全故障分类，Host不引用Platform Infrastructure。

## HTTP合同

- POST `/api/platform/files/staging`：原始请求body为文件字节；Content-Type为文件声明MIME；单一X-WES-File-Name传RFC3986规范百分号转义的UTF-8显示名（.NET `Uri.EscapeDataString`），Idempotency-Key为永久规范非空UUID。服务端只解码一次并验证规范往返，非法百分号/UTF-8不持久化为另一个名字；中文文件名可经真实ASCII HTTP头传输。无JSON/base64或multipart临时文件。响应200只表示Stored隔离证据，不表示附件或病毒清除。无名字/身份、非identity Content-Encoding拒绝。Content-Length已知超M先拒绝；未知长度仍逐段读取至最多M+1字节判界，M=20971520。取消或读取失败不调用上传应用入口；所有自有缓冲finally清零。长度不信任客户端声明。
- GET `/api/platform/files/staging/{id}`：仅当前受权元数据投影。
- GET `/api/platform/files/staging/{id}/content`：仅Available且当前read/download授权，owner先提交下载审计才交付；Content-Disposition attachment、nosniff、no-store、无range或public URL。transport执行后无论断连/成功都Dispose清零；不把FileDownload JSON序列化。
- POST `/api/platform/files/staging/{id}/scan`、DELETE `/api/platform/files/staging/{id}`：JSON expectedVersion，实时Scan/Delete字段授权，复用持久协调；不会收客户端扫描结论。无scanner/超时仍隔离，600秒问题由元数据投影显示。

未启用返回404；启用配置缺项启动明确失败。应用Forbidden/NotFound/Invalid/版本缺失/冲突映射403/404/400/428/412，状态/过早/旧attempt/operation冲突409；超M为413。存储Unavailable/瞬时DB故障503，证据损坏/非瞬时失败500，统一安全ProblemDetails不含路径/摘要/SQL/原文件名。上传应用仍复验当前Upload权限，HTTP认证不替代字段/数据范围；存储未启动前授权失败无文件或审计写入。

## 验证与边界

先红HTTP路由/限额测试，再实现；真实授权wes_test和LocalQuarantineStore验证上传、防重、当前撤权、下载审计及字节。流读单元测试核实未知长度M/M+1、短读、取消/异常及清零；真实Kestrel验证chunked边界以免TestServer掩盖服务器限制。详见[测试记录](108-t07-staging-file-http-tests.md)。

默认配置不变，不接生产/真实设备。loopback地址校验不是scanner健康/病毒库验收；不把测试Clean或种子Available记录当真实病毒清除。UI、实体绑定、真实ClamAV和ACL仍未关闭，T07和依赖整包状态不变。

## 运维接入与回退

仓库默认`Wes:Files:Enabled=false`，不自动创建目录、不运行迁移或安装scanner。经部署owner审查私有目录ACL、静态映射和病毒库后，使用服务器配置明确提供四项：

```json
{
  "Wes": {
    "Files": {
      "Enabled": true,
      "StorageRoot": "D:\\WesPrivate\\Files",
      "ScannerAddress": "127.0.0.1",
      "ScannerPort": 3310
    }
  }
}
```

示例不是已启用部署；目录须已存在且不能与Host或ops-web公开根重叠，Identity亦须明确启用。地址/端口不做启动成功扫描或病毒库健康的替代证明；服务不可用时扫描命令可以完成一次受控尝试，但响应file仍Quarantined，客户端必须读状态而非把HTTP200当附件可用。旧扫描版本412需重新读取，不自动重复物理操作；上传503可按原operation/同内容显式重试。409操作身份冲突不得换内容冒用原operation。

文件名字头使用RFC3986转义；JavaScript客户端可在`encodeURIComponent(name)`后对`!'()*`额外百分号转义，对应.NET `Uri.EscapeDataString`。不要发送原始中文头、二次解码或提供对象key。上传200后按返回id读取处理状态，只有另行Scan取得Available后暂存下载才可用；绑定实体附件仍未开放。

关闭Enabled并重启只撤HTTP入口，不删除对象、永久操作身份或审计；数据迁移回退限制仍见[上传设计](105-t07-durable-authorized-uploads.md)。下载提交的审计表示交付尝试已授权，不承诺客户端收到全部字节；响应断连也会释放清零服务器缓冲。
