# T06 未解析角色候选拒绝

- 状态：DTC-UI-022 后端拒绝伪造角色ID的HTTP/MySQL验收首片已通过；真实owner拒绝未解析引用且不改用户版本、角色关联或审计。测试辅助器已支持规范普通仓库/worktree配置路径。不代表完整 DTC-UI-022 或 T06 完成。
- 依据：[密集页面交互合同](../detailed-design/03-dense-page-interaction-contract.md) §8 DTC-UI-022；[Platform页面/功能设计](../detailed-design/platform-and-security.md) PAGE-PLAT-003、DTC-PLAT-014。
- 测试记录：[未解析角色候选拒绝测试](76-t06-reject-unresolved-role-candidate-tests.md)。

## 实施范围

用户角色替换命令以客户端提交的角色ID集合为不可信输入。通过已授权主体调用真实 Platform HTTP adapter，将随机且不存在的角色ID加入有效用户的当前角色集合；命令必须显式拒绝，且不得修改用户版本、已有角色关联或新增业务审计。

验证重用现有 Platform owner、真实 MySQL 测试库、当前授权和普通命令操作ID。没有必要改变已存在的授权或幂等契约；若测试揭示缺陷，修复必须继续由 Platform owner 在当前租户和事务边界内完成。

真实HTTP fixture还需在普通仓库布局与 `*.worktrees\<worktree>` 布局下解析同一项目根级 Seven.Master 测试配置。路径解析限于已知父目录结构，不扫描磁盘或尝试其他配置源；测试连接仍强制目标 `wes_test`。

## 不在范围内

本切片不证明客户端候选控件无法被篡改；客户端校验仅改善交互，不构成安全边界。也不覆盖真实跨租户角色ID、禁用角色、不可委派授权、候选搜索/回填或其他选择器。完整 DTC-UI-022 与 T06 仍未完成。
