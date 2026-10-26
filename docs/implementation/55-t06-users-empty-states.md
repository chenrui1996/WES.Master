# T06 Users 首次空态与筛选空态

- 状态：Platform Users 页 DTC-UI-002 适用场景首片有自动化证据；其他页面/工作队列适用性和 T06 整体仍未验收。
- 依据：[共用页面测试标准](../detailed-design/00-shared-page-and-test-standard.md)、[T06 工作包](../superpowers/plans/2026-10-23-wes-development-work-packages.md)。
- 测试记录：[Users 空态测试](56-t06-users-empty-states-tests.md)。

## 实现范围

给 Users 页现有的空态状态机增加受控 API 回归：首次已成功读取但没有可读用户时，明确表示尚无用户并显示当前授权允许的新建入口；填写过滤条件但尚未提交时，提示需要提交；提交后返回零行时，显示无匹配用户并提示清除筛选；清空筛选并重新读取后恢复首次空目录状态。受控服务返回 503 时，页面显示错误而不是空态。

该页面不是待办队列；“没有可执行工作”的 zero-work 语义不适用于用户目录，因此本记录不据此验收该分支。此次没有产品逻辑变更。

## 未完成边界

只覆盖 Platform Users 的首次空目录、已提交筛选无结果与查询失败；不代表 WMS/Workflow/PDA 的空任务、无库存、无待办或其他零工作状态已验收，也不覆盖完整 DTC-UI-002。
