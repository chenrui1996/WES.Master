# T07 暂存文件客户端

- 状态：本切片已验收（软件范围）；Owner Platform/Ops Web。
- 需求：PAGE-PLAT-007 / FUNC-PLAT-005/006、DTC-PLAT-007/008、DTC-UI-001..011在暂存单文件工作区的适用子集。
- 依据：[HTTP合同](107-t07-staging-file-http.md)、[平台页面](../detailed-design/platform-and-security.md)、[共享页面规范](../detailed-design/00-shared-page-and-test-standard.md)。

## 范围与交互

实现`/files`具名暂存工作区：选择文件→显式上传→读取元数据/隔离状态→显式扫描→Available后下载，或按版本确认删除孤儿。另一入口按已知文件UUID读取；不伪装已有全局分页列表，不暴露任意路径或未知业务owner，绑定附件仍被服务器拒绝。显示“未绑定暂存文件，不代表业务附件”，无业务引用选择/跳转。此单记录详情使用语义dl，无可排序表、列偏好/导出/分页，相关列表DTC不适用而非自动通过。

上传必须当前Upload字段，读取和导航遵循现有Platform module及read权限。File只留页面内存，不进入local/session storage或URL；0/M+1、未知扩展名、非法显示名在发送前显式报错，服务器依旧权威验证签名/MIME。允许pdf/png/jpeg/txt，空浏览器MIME按批准扩展名声明，不降级绕过实际类型验证。文件名按HTTP合同RFC3986 UTF-8编码。一次选定File对应一个随机operation，失败/取消提示结果可能未知；仅用户显式重试该原File才复用operation，不自动发送，选新文件建立新身份。显示真实XMLHttpRequest.upload已传/总字节；字节发完只提示等待存储确认，不造扫描百分比、不宣称Available。

## 共享传输和撤权

扩展现有identity-api的同源/当前身份代际、ProblemDetails/401清身份、无自动重试：JSON默认行为不变，只有显式上传进度请求使用XHR发送Blob；独立apiBlob复用同一错误和代际校验，消费body后再次核验。保留已提供Content-Type，不将原始文件改成application/json；普通JSON写入继续默认JSON。

页面按subject/session/grants变化、卸载和取消abort请求；sequence+scope指纹拒绝迟到回调。401/403清文件/草稿/operation/文件选择控件；当前新读取失败不显示已上传或沿用旧行执行写操作，同记录旧投影可显示stale但动作禁用。当前请求撤权导致的迟到响应不得落入新主体页面。

编辑待查询UUID时立即清除不再匹配的旧行及删除确认，不能在新查询目标下执行上一文件的动作；空白及UUID大小写不改变同一身份。

下载使用受权blob、当前行文件名和显式浏览器download，不公开对象URL、不新窗口执行文件、不持久缓存。只在权限、当前id/version/状态及scope仍一致时创建临时URL；点击后及时revoke，卸载不留URL。删除二次确认说明24小时/无引用/不可恢复，使用读取的版本，不自动覆盖412。扫描HTTP200也必须读取返回状态，Quarantined/Rejected不得显示扫描成功或允许下载。

## 验证与边界

先测试共享传输对raw Content-Type、真实progress/取消/代际及blob体读取的行为，再实现；页面测试覆盖隔离成功、503原operation显式重试、扫描不清隔离、撤权/迟到/离线、确认删除及安全下载URL释放。前端全套/严格构建及受控HTTPS Chromium文件交互验收，记录[证据](110-t07-staging-file-client-tests.md)。

业务实体绑定/全局文件目录/真实病毒库与ACL仍不在本片验收范围，不关闭T07或父DTC整体。上传进度不是扫描或物理控制证据。
