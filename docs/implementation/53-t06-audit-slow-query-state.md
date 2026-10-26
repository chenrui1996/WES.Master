# T06 审计页慢查询加载与刷新状态

- 状态：Platform 审计页 DTC-UI-001 慢查询/刷新状态首片完成；其他 PAGE 适用性及 T06 整体仍未验收。
- 依据：[共用页面测试标准](../detailed-design/00-shared-page-and-test-standard.md)、[T06 工作包](../superpowers/plans/2026-10-23-wes-development-work-packages.md)。
- 测试记录：[慢查询状态测试](54-t06-audit-slow-query-state-tests.md)。

## 实现范围

共享 `QueryState` 在 loading 状态保留即时、仅供辅助技术读取的状态消息；持续 300ms 仍未完成时才呈现可选 `loading` slot，短读取不出现视觉骨架闪烁。请求在阈值前完成或组件卸载时，延迟状态被取消。未提供 slot 的消费者沿用显式加载消息。

Platform Audit 页为该 slot 提供按当前授权列投影组织的表格占位结构，列、行均无真实业务数据，且以 `aria-hidden` 排除占位内容；减少动态效果设置时关闭脉冲动画。实际请求成功后占位结构退出。

同scope后台刷新继续显示原证据，不以占位内容覆盖数据；刷新期间和失败时显示上次成功读取的 UTC 时间。切换查询/身份使旧数据失效时，同时清除上次读取时间，避免把旧范围的时间当成当前结果的新鲜度。

## 未完成边界

此证据只覆盖 Platform Audit 页和共享状态组件，不表示所有目录、业务页或PDA均已接入具名骨架、读取时间或完成 DTC-UI-001。登录安全事件也未单独进行延迟页面验收。其他页面的骨架形状、首屏/筛选空状态、失败恢复和全套 DTC 仍需逐 PAGE 评估。
