# 默认值与边界

首次记录/更新：2026-10-28。各数值仅适用于所列合同，不推广为系统所有入口的统一默认。维护方式见[索引](README.md)。

## DEF-001 默认参数不等于安全许可

- **基线约束**；适用：配置、策略、设备能力。
- 风险：将缺值/0/false 当成“无限制”或“默认支持”；把开发初值当行业标准或现场批准。
- 规则：B 是已定业务结果，P 是有来源/版本的初值，H 缺证据保持 Unconfigured/Unverified 并阻断依赖控制。已有配置非法必须报错，不静默退回 P。
- 检查：覆盖缺失、合法覆盖、非法值/版本和 H 未配置；在途固定策略快照，但权限/联锁撤销实时生效。
- 证据：[WES-RULES-1.0 §1](../detailed-design/05-approved-rules-and-defaults.md#1-状态覆盖与生效)。不能据本条新增一个宽松兜底值。

## DEF-002 默认关闭与关闭后的持久保护不是一回事

- **基线约束**；适用：独立业务开关及盘点锁。
- 风险：新 HTTP 开关继承总开关误上线；关闭入口时连库存保护一起关闭，或用 Down 删除活动状态。
- 处理：新写入口显式默认关闭、依赖启动校验；关闭入口不自动撤销权限事实、未决运输或已生效盘点锁。Enabled 也不是实际 Pack/schema/现场许可。
- 验证：默认无路由/非法组合启动失败；关闭 HTTP 时已有保护仍拒绝新冲突写，恢复需兼容版本及当前权限。
- 证据：[功能开关设计](../detailed-design/feature-switches.md)、[183](../implementation/183-t11-blind-count-and-local-lock.md)、[184](../implementation/184-t11-blind-count-tests.md) TC-T11-COUNT-04/07。

## DEF-003 10秒 Unknown 不是服务端失败；1800秒提醒不是解锁

- **基线约束**；适用：174/183 定义的客户端写恢复和盘点。
- 处理：这些页面在10秒、断网、503或畸形回执后保留 Unknown，冻结原操作类型/目标/key/body；手工原键恢复，不自动重试或排队。
- 盘点在 `now >= snapshotAt + 1800s` 只显示提醒，不释放锁、不使计数自动失效；普通查询 token 的 TTL 不能挪作业务锁 TTL。
- 验证：10秒与迟到回执隔离；盘点1799.999/1800秒边界仍有保护；不以改大超时或自动解锁“修复”卡住。
- 证据：[174](../implementation/174-t08-warehouse-operational-state.md)、[183](../implementation/183-t11-blind-count-and-local-lock.md)、[184](../implementation/184-t11-blind-count-tests.md) TC-T11-COUNT-05/07。不是所有请求统一10秒超时。

## DEF-004 数量的0、null、精度与上限不能混用

- **已复现并修复 / 基线约束**；适用：库存绝对数、单次移动、计数单位。
- 实际问题：盘点客户端复用移动数量9位整数上限，合法大余额即使零差也无法记录；未盘 null 若兜底0会制造假计数。
- 处理：绝对数按 InventoryQuantity 的22位整数/最多6位小数范围；单次差异仍受 StockMovementQuantity 的9位整数/最多6位小数限制。响应按合同六位小数字符串，不改为 JS 浮点运算。
- 单位 Countable 时精度0；可分割单位显式0..6；由物料基准单位派生，不允许调用者自报精度、默默舍入或隐式换算。
- 验证：合法大绝对数/零差、超过移动范围的差异、零实盘、未盘null及单位精度边界；大数问题已有两个RED后修复。
- 证据：[176](../implementation/176-t08-legal-stock-resources.md)、[183](../implementation/183-t11-blind-count-and-local-lock.md)、[184 §5](../implementation/184-t11-blind-count-tests.md#5-独立审查与关联修复)。

## DEF-005 声明100行，就要验证全链容量而非只有数组校验

- **已复现并修复**；适用：T10出库；盘点的范围另由183定义。
- 实际问题：出库支持1..100行，但操作事实输入8192字符上限拒绝合法大单。
- 处理：T10统一输入上限65536字符，并用真实100行从合法登记/入库到全行原子审核验证；不能只扩 DTO 或截断事实。此上限不自动推广到其他模块。
- 盘点启动必须收集全部合法维度（含零），1..100行；超过100明确拒绝，不取前100伪装全盘。
- 证据：[182 §2.3](../implementation/182-t10-outbound-basic-flow-tests.md#23-独立审查与关闭条件)、[183](../implementation/183-t11-blind-count-and-local-lock.md)。新限制需测边界及持久事实大小。

## DEF-006 可空字段要按状态建模，不能全局设默认

- **已复现并修复**；适用：盘点严格客户端解析。
- 实际问题：完成/失效后后端释放 `lockOwnerId=null`，前端却要求所有非Draft均有owner，合法终态无法读取。
- 处理：Counting/Ready要求owner；Completed/Invalidated保留snapshotAt但owner必须null；Draft两者null。未计数quantity/counting identity/time为null，不补假值。
- 验证：Draft、Counting、Ready、Completed、Invalidated逐状态合法/非法组合；终态两例先RED后修复。
- 证据：[183](../implementation/183-t11-blind-count-and-local-lock.md)、[184 §4](../implementation/184-t11-blind-count-tests.md#4-续接集成验证)。
