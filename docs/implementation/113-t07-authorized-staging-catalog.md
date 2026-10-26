# T07 当前授权的暂存文件目录

- 状态：本切片已验收（软件范围）；Owner Platform。
- 需求：PAGE-PLAT-007 / FUNC-PLAT-005，DTC-PLAT-005/007/014及DTC-UI-003/007/008的适用子集。
- 依据：[平台页面设计](../detailed-design/platform-and-security.md)、[暂存访问边界](103-t07-authorized-staging-file-access.md)、[暂存客户端](109-t07-staging-file-client.md)。

## 目的与边界

现有页面只能凭已知UUID读取暂存文件，不能发现当前有权访问的已有上传。补充只读实时目录，沿用原完整metadata授权合同，不创建业务附件入口、不允许凭文件权限越过实体授权。业务绑定目录仍须对应owner批准的实体读取契约；此片不将暂存目录冒充全部业务附件。

GET `/api/platform/files/staging?offset=0&limit=25&state=0`。offset为0..10000，limit为25/50/100，state可省略或使用既有0..4生命周期枚举；非法参数400，不回退默认查询。响应含decision/rows/offset/limit，无未授权总量、摘要、对象路径、任意URL或业务引用。

必须持有当前`platform.files.read`全部既有metadata字段；每字段分别计算All、真实creator Own或creator当前目录部门的并集，再取字段交集。先在owner SQL内按tenant、creator存在、ReferenceCount为0且无实际引用过滤，再稳定按CreatedAt降序/Id升序分页，不先分页再丢弃越权行。缺任一字段403；范围合法但无匹配时200空目录。无隐式全局或后台身份。依赖失败沿用明确安全错误；取消不产生写入或审计。普通只读沿用当前授权查询合同，不宣称跨请求快照或跨请求无丢重。

## 客户端

现有Files页面增加具名目录区，显式读取、状态筛选及共享25/50/100实时分页；同查询刷新保留标记为Stale的旧证据，但不能选择，失败不是空成功。改过滤/分页/主体/租户/授权清旧行并取消请求，迟到响应不落入新范围。401/403立即清目录；离线不排队。选择只传文件ID给现有详情入口，并重新读取当前授权/版本，目录行不能直接启动下载/扫描/删除。字段/文件数据仅驻内存，不新增storage。

复用现有metadata解析规则及共享QueryState/RealtimePager。响应数量不能超过请求limit，offset/limit必须与请求一致，重复ID或非法metadata整批拒绝。窄屏采用可换行具名条目，全部按钮有键盘入口。没有schema、依赖或部署配置变化；回退应用即可，不删除文件身份或审计。

## 验证与未关闭项

先实际HTTP缺路由RED，再接owner SQL；验证权限字段交集、Own/部门/All、creator部门变更、tenant、业务引用、过滤前分页、严格边界、安全投影及撤权。前端先缺目录RED，后验证响应形状、竞态、Stale禁选择与重新读取；真实HTTPS Chromium从目录选择已有干净文件并实际下载。结果见[测试目录](114-t07-authorized-staging-catalog-tests.md)。

业务实体绑定/附件下载、真实ClamAV病毒库/识别/健康、部署ACL继续独立待验；未取得证据不得关闭T07或其他工作包。
