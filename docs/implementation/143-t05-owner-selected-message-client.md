# T05 消息诊断的 owner 选择与 WMS 创建事件客户端

- 状态：已验收（本片双owner选择/独立读取）；沿已批准PAGE-NTF-003/FUNC-NTF-001与用户自主继续实施授权。
- 前置：[第二owner API](141-t05-wms-creation-event-diagnostics.md)、[Platform客户端](127-t05-platform-message-diagnostics-client.md)及后续筛选/计数/重放。
- 需求：REQ-NTF-004..008、PAGE-NTF-003、UI-001..008/011/012；证据：[144](144-t05-owner-selected-message-client-tests.md)。

## 边界与选择

在现有`/notifications/messages`增加owner选择，不新增业务路由或跨owner私表查询。不合并两种不相容行投影，不建设通用schema框架。Platform继续原列表/过滤/统计/受权重放；WMS仅九字段Pending创建事件，明确没有publisher/Inbox/统计/重放，不用0/空投递状态代替未实现。此片不是跨owner统一快照/总计、真实Broker或T05整体验收。

导航与路由复用固定的Platform/platform.messages.read、Wms/wms.messages.read成对条件：存在同一owner的模块启用且当前permission才可进入；绝不能将Platform启用与WMS权限拼成授权。模块catalog失败保持显式unavailable；无权限仍会话页。页面只提供当前可访问owner，默认Platform（若可用）否则Wms。切换卸载前owner，取消查询、清行/筛选/回执；Platform重放面板打开期间锁owner选择，必须原流程明确关闭，不因切换丢失未知结果操作。身份/grant改变或模块禁用会强制清旧并重新选当前owner，不自动读取。

## WMS读取

当前`wms.messages.read`九字段并集仅为按钮可用性，Own/Departments/All交集与真实creator仍以API复验为准，不能仅要求All或借主数据/Platform字段。GET精确`rows/offset/limit`，逐行精确九字段、Wms/固定contract/schema1/status0、非空UUID、合法UTC、无重复，坏JSON/扩展字段/身份/页回声错误全拒绝并清旧。不把错误变空成功。复用现有UUID/UTC校验及QueryState/RealtimePager，25/50/100，offset0..10000。

不自动读、不存业务到local/sessionStorage、不排队。同查询503保留明确stale；401/403/404/非法响应清旧；换页先清再查。身份/grant、owner、卸载、离线取消并丢弃迟到结果；在线恢复须手工。实时分页无总数/跨请求无丢重保证，Pending不是已投递/业务完成/启用许可。

## 验证

先客户端/路由/导航RED，再实现；当前字段逐一缺失、Own可用、成对owner门禁、换owner/身份/模块取消、重放锁选择、非法响应/分页/离线/stale等。完整前端严格构建与回归；扩展批准MySQL真实HTTPS Chromium既有WMS两变体，实际读取当前两条创建事件，再由第二owner真实撤事件字段得到403，不stub业务接口，保留主数据创建/撤权与响应式/200%字体/键盘/存储断言。

最终新增59客户端/路由/导航用例，全前端521通过、vue-tsc/Vite通过。13真实诊断HTTP+Platform既有页面链路浏览器1+WMS只读/创建两变体浏览器2=16通过，架构29通过、Release solution0警告/0错误。实际WMS列表随创建变体为2/3条；7/10次WMS请求中仅创建变体含一个原有POST，其余GET。首真实浏览器暴露owner label包含option文本导致精确可访问名不符，改为独立label/select并保留精确名称断言后复验；未放宽测试匹配。
