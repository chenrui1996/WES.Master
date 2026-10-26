# T06 列偏好跨标签草稿保护

- 状态：DTC-UI-016 跨标签更新不覆盖当前列设置草稿的首片通过组件测试；其余schema迁移、损坏/存储配额失败和完整 T06 仍未关闭。
- 依据：[密集页面交互合同](../detailed-design/03-dense-page-interaction-contract.md)、[T06 工作包](../superpowers/plans/2026-10-23-wes-development-work-packages.md)。
- 测试记录：[跨标签草稿测试](60-t06-column-preference-cross-tab-draft-tests.md)。

## 实现范围

共享列偏好 Hook 已对作用域匹配的 storage event 发出“其他标签页”提示而不热应用外部值。本切片将该路径接入实际 `ColumnSettings` 草稿：打开当前列设置并取消勾选姓名（只改未应用草稿），接收另一个标签页写入不同布局的 storage event 后，验证外部更新有明确提示、页面已应用列仍不变、当前草稿仍保持取消勾选；随后用户显式应用草稿才改变列选择。

本切片仅补足回归断言，不修改现有跨标签同步策略或存储格式。外部更新不会被静默应用到当前页面，也不会覆盖操作者未提交的编辑。

## 未完成边界

未在真实多浏览器标签生命周期或不同租户/身份同时登录场景执行此项；schema升级、禁用/损坏storage与配额不足的既有独立测试不因本切片而合并为全部 DTC-UI-016 通过。
