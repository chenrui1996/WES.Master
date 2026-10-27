# T07 当前实体附件工作区

- 状态：已验收（基本软件切片）；完整组合浏览器/外部验证后置，不作为完整 T07 验收依据。
- 需求：PAGE-PLAT-007 / FUNC-PLAT-005，DTC-PLAT-005/007/008/014及DTC-UI-001..012适用子集。
- 基线：[实体目录](169-t07-current-entity-attachment-catalog.md)、[永久引用写](167-t07-durable-business-file-bindings.md)、[页面合同](../detailed-design/platform-and-security.md)。

## 固定上下文与读取

在WMS仓库/库位已读取详情下展示附件区。实体kind/id只能从该当前详情传入，不从任意owner字符串/对象key决定路由。无完整文件metadata字段不显示可操作附件区；导航/字段存在不是实际scope授权，所有请求由服务端复验。组件在挂载时不自动查询。

附件目录显式读取、当前分页25/50/100和固定九字段，选中后重新读取当前实体附件元数据。刷新失败仅陈旧且不可操作；401/403、主体/类型/实体变化清旧数据、取消等待并拒绝迟到响应。离线只留陈旧参考，无写队列/自动重放。可用状态文案不得沿用暂存“未绑定”；只在当前读权、下载Content及Available/Stored和新鲜版本同时满足时允许下载。复用现有字节/MIME证据及objectURL释放；不把回执当当前成员关系。

## 显式绑定、解绑和恢复

当前目标实体完整read、对应WMS write字段Attach/Detach和Platform同名write字段共同控制按钮；read不自动grant写，write不自动grant文件read。绑定候选复用当前授权暂存目录，选择后重读原暂存文件，只能Available/Stored，不能跳过隔离/扫描或自动绑定上传结果。原上传/扫描仍由既有暂存工作区处理，不伪造自动清毒。

绑定/解绑先冻结kind/entity/file/action/expectedVersion/operation元组并预览，二次确认后发送严格PUT/DELETE与单值client/key。提交中不允许更换实体/候选、刷新版本或新建命令。10秒、取消等待、断线、网络/5xx或无法验证的成功响应均表示结果不明，保留原键/原tuple，只能显式恢复原操作；不能从陈旧/新读出的版本重建未知请求。迟到结果不能覆盖后来状态。

成功仅接收精确三字段 `{id,version,attached}`，核对原file/action和合法版本；显示“原操作已确认”，清旧列表/选择，用户显式重读当前附件。未知之后的拒绝不证明前次未提交；不自动解锁换键。提供记录原tuple后明确退出，说明退出/刷新/导航不撤销已提交事实，内存不跨主体保留。

人工恢复入口继承当前固定实体，只输入记录的原file、动作、expectedVersion、operation；先校验/预览，再二次确认同一个原操作。不能自动生成“恢复key”，不能按当前文件版本覆盖原版本，不添加业务数据到localStorage/sessionStorage/URL或离线缓存。当前不同actor不能恢复他人回执，由服务端永久记录再次核验。

父页锁仅禁止目标切换、创建和重新读取，不反向传入附件 active，否则预览会令自身失效。附件组件位于详情 QueryState 之外，仅在有具名实体及读取字段时挂载，离线保留同一实例与未知元组。未知恢复仅要求在线、当前完整读取/对应写字段及未变化的冻结 scope，不要求父详情 fresh；新读取、新预览和下载仍要求 active。服务端始终重新校验当前资源授权。

## 可访问性和验证

原生form/fieldset/label/button、唯一标题/控件ID、status/alert、键盘顺序及44px目标；继承密度/主题，不做绝对定位弹层。375px、200%文字及键盘浏览器检查；所有下载/URL/请求在失效或卸载后不交付。

前端基线730/730已重跑通过。先缺组件/合同RED，再覆盖默认无请求、明确上下文/权限、目录陈旧/分页/错误、候选重读、预览/取消、精确body/header、原键未知恢复/拒绝后仍不明、手工恢复、当前授权变化/离线/迟到以及objectURL释放。严格构建后在真实HTTPS Chromium + MySQL核验两个实体类型的当前目录、绑定/下载/解绑和永久单效果；受控干净种子不是实际ClamAV验收。见[测试记录](172-t07-current-entity-attachment-client-tests.md)。
