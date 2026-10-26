# T06 候选重置后的旧响应隔离

- 状态：DTC-UI-022 候选状态保护测试首片；reset 后旧请求不得重新填充候选的受控回归已通过，不代表完整 DTC-UI-022 或 T06 完成。
- 依据：[密集页面交互合同](../detailed-design/03-dense-page-interaction-contract.md) §8 DTC-UI-022；[Platform页面/功能设计](../detailed-design/platform-and-security.md) PAGE-PLAT-003/004 与 DTC-PLAT-014。
- 测试记录：[候选重置旧响应测试](74-t06-stale-directory-candidate-response-tests.md)。

## 实施范围

`useDirectoryCandidates` 以本地 generation 令牌隔离 `reset()` 前后的候选读取。页面身份变化、离线、权限拒绝和关闭候选编辑流程均可能重置候选状态；延迟到达的旧响应不能覆盖重置后新读取的候选。本切片用候选读取器组件宿主和受控延迟 HTTP 响应直接验证该行为，并验证新请求结果保留、旧结果被忽略。

候选API当前仅提供有界分页，本项目没有已批准的远程候选搜索查询契约或页面搜索输入。本切片不增加搜索控件、搜索参数、后端排序或已选项独立回填；不得把此测试宣称为 A/AB 搜索乱序场景通过。

## 不在范围内

本切片不改变实现逻辑或服务端授权，也不验证真实身份切换、跨租户目录、伪造ID提交或其他选择器。完整 DTC-UI-022 与 T06 仍未完成。
