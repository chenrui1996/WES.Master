# T07 当前实体附件目录测试

- 状态：本目录owner/API续片已验证通过；业务附件客户端及整包仍缺。
- 设计：[当前实体目录](169-t07-current-entity-attachment-catalog.md)。

目标：缺Query合同编译RED和缺集合GET行为RED；真实MySQL/API验证精确持久引用、当前实体与逐字段文件scope SQL交集后分页、两具名类型/tenant/当前creator部门、取消/错误/撤权不交付旧页。回归旧暂存目录、具名读取与永久绑定，不用内部干净种子代签真实ClamAV或业务客户端。

## RED与GREEN

- 两个具名集合GET实际404而预期200，得到2项运行时RED；不把缺合同编译失败代替HTTP行为。
- BusinessFileAccess缺QueryAsync，得到CS1061合同RED；补显式入口后owner1/1、HTTP2/2通过。
- 扩展后30真实MySQL、25 HTTP通过；追加HTTPS/认证/未批准POST后，26项随相关333项回归通过。最后明确拒绝空offset/state（不让nullable binding默认为未指定），补两项后新HTTP28/28及架构29/29在精确最终源通过。没有关闭断言或添加默认成功结果。

## 最终Release结果

| 验证 | 实际结果 |
|---|---|
| 新目录owner | 30项真实MySQL；精确owner/大小写/entity/tenant、引用与计数双证据、九字段分别缺失、每字段All/Own交集先于分页、真实creator部门变动、25/50/100及10000边界/稳定UUID排序、状态、权限两时点、取消/端口错误不返回成功页、无存储读取/审计/Outbox |
| 新目录HTTP | 28项；两个固定集合、精确四字段页和九字段行、no-store、非法/空值分页和状态、重复键/调用者owner/实体参数、会话/字段/实体撤权、类型隔离/缺实体、筛选与分页、无伪total、read不grant下载、双开关关闭、空UUID、HTTPS/认证/集合POST405 |
| 最终文件owner | `FullyQualifiedName~AuthorizedFileAccessTests\|FullyQualifiedName~QuarantinedFilePersistenceTests`，166/166；包含既有暂存/上传/具名读取/永久引用写及模型无漂移 |
| 相关HTTP/既有浏览器 | `FullyQualifiedName~WmsFileCatalogHttp\|FullyQualifiedName~WmsFileBindingHttp\|FullyQualifiedName~WmsBusinessFiles\|CaseId~HTTP-FILE\|CaseId~CLIENT-FILE\|FullyQualifiedName~FileCatalog`，138/138；含当时26项新目录API及一套既有真实HTTPS Chromium文件上传/扫描/下载/暂存目录回归 |
| 精确最终API与架构 | 空query门禁补齐后重跑全部新HTTP28/28、架构29/29；没有修改owner查询或旧暂存/引用写/下载代码，未再次重复整个138集合 |
| 最终架构/构建 | 架构29/29，`dotnet build WES.slnx --no-restore -c Release`零警告、零错误 |

相关合计166+138+29=333项通过，无失败/跳过；精确最终API28及中间1/2/30/25不重复累计到333。环境仍为.NET10.0.103、授权隔离MySQL8.0.12/wes_test，未新增依赖/迁移，不连接生产。目录片没有客户端变更；旧浏览器使用既有dist，不冒称新附件工作区已验收。未重跑不相关WMS创建/全部解决方案测试或四套WMS浏览器。

会话 `files\business-file-catalog-validation` 保留 `catalog-http-red`、first、boundaries、相关 `catalog-file-owner-final` / `catalog-file-http-browser-final` / `catalog-architecture-final` 及精确最终 `catalog-http-exact-final` / `catalog-architecture-exact-final` TRX；CS1061保留在命令记录。修改/新增C#格式无差异检查通过；文档链接和提交核验单独记录，不把未跑步骤预写成通过。

提交前精确最终全解决方案Release构建0警告/0错误，新增/改写行的17个本地文档链接均存在，`git diff --cached --check`通过。后一客户端切片的文档和原型明确不暂存到本目录commit，也不计入本片验收。

## 保留边界

真实ClamAV/病毒库/健康/ACL、当前实体附件客户端及父DTC完整验收仍缺。当前实体authority仅按查询时点复验，不承诺跨owner锁住后来发生的撤权、固定快照或真实扫描服务可用。公开暂存工厂仍只允许未绑定文件，目录读取不授予写/下载，也不返回其他引用或秘密。
