# T06 Platform 审计固定成员快照

- 状态：Platform 审计 owner 固定成员快照切片已实施；T06 与 DTC-UI-011 整体仍未验收。
- 依据：[T06 工作包](../superpowers/plans/2026-10-23-wes-development-work-packages.md)、[共享分页实施](47-t06-shared-realtime-pager.md)。
- 测试记录：[固定快照测试](52-t06-platform-audit-snapshot-tests.md)。

## 实现范围

Platform 审计 owner 新增短期固定成员核对快照，不提供导出、不集中缓存其他 owner 数据。创建快照时，在 RepeatableRead 事务中复验当前 `platform.audit.read` 权限并冻结符合查询条件的有序审计 ID；仅持久化成员 ID、查询条件和身份/授权/过滤/排序指纹，不复制审计行内容。稳定顺序为 `OccurredAt`、`Id`。

快照限制为 10,000 条、600 秒。达到 10,000 条时允许创建；超限会被有界读取识别并拒绝。续页要求相同租户、actor、session 和匹配的当前授权/过滤/排序指纹；每页按冻结 ID 读取原有 append-only 审计事实，并仍按当前字段授权投影。撤权、授权范围变化、身份不符及 `now >= ExpiresAt` 均拒绝继续读取。过期记录仅在创建时按批次清理最多 100 条。

新增 owner API：

- `POST /api/platform/audit/snapshots`：创建快照并返回第一页、总成员数和过期时间。
- `GET /api/platform/audit/snapshots/{snapshotId}?offset=&limit=`：读取冻结成员的后续页。

前端 Audit 页面提供创建快照/返回实时查询操作；筛选变化或授权拒绝后清除旧快照结果，过期时明确提示。共享分页器标识固定成员快照状态，不将其描述为实时 offset。快照标识只保存在当前页面内存中。

## 未完成边界

这是 Platform 审计 owner 的首个快照切片，不是通用跨 owner 查询缓存。登录安全事件、其他目录 owner、可变数据的值冻结、导出合同及其余 PAGE 的适用性仍需分别设计和验证；T06 的其他 DTC、响应式与完整视觉验收仍未关闭。append-only 的 Platform 审计事实使本切片只需验证新插入事实不混入原有成员，不据此声称可变数据快照已完成。
