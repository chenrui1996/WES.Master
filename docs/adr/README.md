# Architecture Decision Records（ADR）

ADR 记录影响长期架构、模块所有权、数据/接口兼容、设备安全或团队协作的决策。ADR 是补充证据，不替代详细设计/测试。规则见 [工程治理](../03-engineering-governance.md)。

## 1. 何时需要 ADR

至少以下情形必须新增/更新 ADR：

- 新增、合并或拆分模块，改变依赖方向/数据所有权。
- 更换部署形态、事务/Outbox边界、消息 Broker 或支持数据库/provider。
- 更改身份/租户/数据授权、外部 API/Event 兼容策略或动态表单执行能力。
- 修改 PDA 离线策略、设备命令语义、Single Writer/联锁、安全职责。
- 采用 Route 关键算法、时间窗/预约 authority、死锁/活性模型或现场安全假设。
- 调整 Simulator Deploy/Promote/回滚、不可逆迁移或高风险运维覆盖。

纯实现细节且不改变行为/边界可由设计/PR 说明，不滥用 ADR。

## 2. 文件与状态

文件名：`ADR-NNNN-short-title.md`，编号递增、不复用。状态：`proposed / accepted / rejected / superseded / deprecated`。在本目录维护清单和每项状态链接。

## 3. 决策模板

```markdown
# ADR-NNNN: 标题

- 状态：proposed
- 日期：YYYY-MM-DD
- 决策人/复审人：
- 关联 SCOPE/REQ/风险：

## 背景与问题
## 决策
## 约束/不变量
## 备选方案与取舍
## 安全、数据、兼容与运维影响
## 验证/测试/证据
## 后果与回滚/迁移策略
## 复审条件与期限
```

## 4. 当前已批准基线

以下先由启动设计文件记录；只有后续需要精化/改变时再立独立 ADR，避免重复决策：

- .NET 10 模块化单体；WMS/WCS 核心 DDD + 用例纵切。
- SQL Server/PostgreSQL/MySQL provider 支持目标。
- 运维/Simulator 为 Vue 3 + Vite；PDA 保留 UniApp + Vue 3。
- PDA 离线只读缓存，不保存/重放写操作。
- Stacker/FourWay 路网独立；安全约束优先，在线状态与冲突资源纳入 Route。
- WMS 是库存账本权威；Builder/源码生成器移除。
- 当前交付仅文档，不创建代码/脚手架。

## 5. ADR 索引

| ID | 标题 | 状态 | 关联范围 | 复审条件 |
|---|---|---|---|---|
| [ADR-0001](ADR-0001-route-reservation-authority.md) | Route 预约权威：耐久账本与每 Pack 单写者 fencing | accepted | SCOPE-OPS-02, REQ-ROUTE-028..029 | Provider 并发语义、现场可用性或设备命令确认能力发生变化时复审 |
