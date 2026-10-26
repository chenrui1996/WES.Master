# T06 Platform角色与用户候选分页边界

- 状态：DTC-UI-022 Platform Roles/Users授权部门/角色候选分页首片；遵守每页25条的已批准交互合同，不代表完整 DTC-UI-022 或 T06 完成。
- 依据：[密集页面交互合同](../detailed-design/03-dense-page-interaction-contract.md) §4.1、§8 DTC-UI-022；[Platform页面/功能设计](../detailed-design/platform-and-security.md) PAGE-PLAT-004、DTC-PLAT-014。
- 测试记录：[Platform候选分页测试](72-t06-department-candidate-page-size-tests.md)。

## 实施范围

Platform Roles页授权部门候选，以及Platform Users页授权部门/角色候选，均使用 `useDirectoryCandidates` 加载并通过“加载更多”继续读取。该通用候选读取器原固定一次读取50条，与交互设计规定的远程选择器默认每页25条不一致。本切片将候选分页请求与偏移步长改为25，并在Roles与Users具名页面断言初始候选请求为25条、后续请求从offset=25开始；第二页追加不丢弃第一页候选或已显示选项，同时同步更正页面提示。

只改变共享候选目录分页粒度，不改变角色/用户业务目录自身的分页、候选授权、租户筛选或保存契约。

## 未完成边界

仅覆盖Platform Roles与Users的授权部门/角色候选分页首片；不声称其他远程选择器、异步搜索/AB竞态、已有选择独立回填或伪造ID服务端拒绝已经验收。完整DTC-UI-022及T06仍未关闭。
