# T06 QueryScope 契约切片

- 状态：共享 scope 值对象已实现并通过定向单元测试；T06 整体未验收。
- 依据：[T06 工作包](../superpowers/plans/2026-10-23-wes-development-work-packages.md)、[RULE-25 查询规则](../detailed-design/05-approved-rules-and-defaults.md)。
- 测试记录：[QueryScope 契约测试](44-t06-query-scope-tests.md)。

## 契约

新增 `Wes.Platform.Contracts.QueryScope`，将固定查询快照的身份边界表示为不可变值对象：tenant、actor、session，以及当前授权、规范化过滤条件和稳定排序的 SHA-256 指纹。拒绝空身份或非 64 位十六进制指纹，并将有效指纹归一为小写，保证值相等比较稳定。

该对象只用于比较 owner 创建快照时的作用域与后续请求作用域；它不授予权限、不包含业务行或过滤明文，也不自行签发/存储快照 token。查询 owner 必须在每次使用时重新读取当前有效授权并计算授权指纹；过滤和排序指纹必须来自该 owner 的规范化白名单查询表达式。调用方未做到这些时，不得将此值对象当成授权或撤权检查。

## 后续工作

- 尚无 Platform 或其他业务 owner 接入本契约。
- 尚未实现水位/有序 ID 与所需读值物化、快照 token、默认 600 秒 TTL、599/600 秒过期边界或撤权即时失效。
- 实时 offset 变化提示、60 条动态增删改及具体业务汇总均未覆盖；不将 QueryScope 单测计作 DTC-UI-011 通过。
- RULE-25 的完整实现仍需 owner-local 快照与集成验证；T06 继续标记为部分完成。
