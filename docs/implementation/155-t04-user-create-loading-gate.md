# T04 新建用户当前目录门禁

- 状态：实施中（局部修复已验证）；PAGE-PLAT-003/FUNC-PLAT-002、REQ-PLAT-004、UI-001/004/007/008/011/012。
- 依据：[Platform设计](../detailed-design/platform-and-security.md)、[共用页面规范](../detailed-design/00-shared-page-and-test-standard.md)。
- 测试：[156](156-t04-user-create-loading-gate-tests.md)。

流水/共享页面外壳回归中，真实Platform浏览器在重进用户页后立即点击“新建用户”，等待表单超时。现有`open`已拒绝busy及queryStale，而新建按钮未绑定此状态；目录/候选初始IO未完成时按钮可点击却静默不动作。其他逐行编辑/凭据/角色按钮已有同样disabled门禁。

仅给新建按钮补`busy || queryStale`，与既有服务端/客户端规则对齐，不放宽写入或自动重试。读取中明确不可用、成功完成可用、当前目录失败保持不可用，显式重新读取成功后恢复。保留open防御检查、当前write字段、候选授权/原始identity、离线拒绝和版本语义；不改加载API或绕过校验。

本片是UI与既有guard一致性修复，不关闭全部Platform父DTC或T04整包；现场及运维证据不受影响。
