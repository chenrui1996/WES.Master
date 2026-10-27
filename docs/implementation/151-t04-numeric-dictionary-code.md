# T04 数字字典业务码与真实语言回退

- 状态：已验收（数字码与语言回退子集）；DTC-PLAT-006/012/014、FUNC-PLAT-004、PAGE-PLAT-006。
- 基线：[Platform详细用例](../detailed-design/platform-and-security.md)、[规则与默认值](../detailed-design/05-approved-rules-and-defaults.md)。
- 测试：[152](152-t04-numeric-dictionary-code-tests.md)。

按已批准DTC-PLAT-014“字典code0改label，0仍正确映射”修复字典项构造错误复用技术标识校验的问题。仅DictionaryEntry.Code允许ASCII字母或数字起始，后续字母/数字/点/下划线/横线，总长仍1..128且全串匹配。按string保留`0`/`01`/混合码，不转整数、不trim、不以显示文本为状态。分类、语言键、角色/部门code继续既有字母起始合同，不扩大其他授权/技术标识。

沿既有当前写字段、可信tenant、永久operation/版本回执、原子审计/Outbox、tenant+category+code唯一，不新增入口/权限/默认数据或迁移。旧合法code行为不变；非法/超长/control拒绝且不持久化。翻译仍请求语言→租户默认语言→诊断key；版本冲突不覆盖，引用存在时删除拒绝，禁止客户端强制解除引用。

除领域/真实MySQL/HTTP边界外，扩展现有真实HTTPS Chromium完整Platform夹具，在配置页面键盘创建`0`、保存纯文本翻译、换语言查回退/缺失、改文本保持code、删除有引用项被拒；最终第二owner核code/翻译/引用/审计。无业务API stub，不能以组件假数据代替。不会将此子集当完整T04/T06/T07关闭。

2026-10-27新增18领域/9真实HTTP/1真实owner测试通过；联合原字典HTTP28+完整目录owner23+真实HTTPS Chromium1+架构29=81通过，全解Release0警告/0错误。实际浏览器先按已批准流程创建`0`、两次改译、请求/默认/诊断key回退、纯文本不渲染HTML及引用删除409后清旧/显式读回，最终owner核恰好三条字典审计、稳定码/翻译/引用不变。结果和首次失败记录见[152](152-t04-numeric-dictionary-code-tests.md)。

无表/迁移/权限/配置/依赖变化；生产未执行操作。回退应停用新增写入或回退软件，不得删除已有数字业务码来适配旧校验；已保存code仍是原string，不能改成label或数值。其他两Provider、真实终端/生产和完整父DTC适用矩阵仍未验收。
