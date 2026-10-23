# Route 测试目录与验证协议

- 状态：目标测试规格；数值性能门限等待现场输入
- 关联：[Route 现状](01-as-is-and-research-review.md)、[Route 目标设计](02-target-routing-and-scheduling.md)
- 前缀：`ROUTE-TC-*`；每个实现需求还需在总追踪矩阵关联 REQ ID。

## 1. 分层验证

1. **纯函数/单元**：图校验、成本、确定性 tie-break、约束图算法、状态转换。
2. **持久化/并发集成**：事务、唯一约束、原子 grant/release、重启恢复、provider 语义。
3. **协议契约**：虚拟/真实 Adapter 的命令与反馈 schema、重复/乱序/超时语义。
4. **确定性仿真**：多车地图、冲突区、线上任务、故障注入、等待与恢复；固定 seed/map version。
5. **现场验收**：经风险评估的空载/载荷/低速运行、急停/通信失效和操作恢复；需安全负责人批准。

所有仿真需保存代码版本、planner 版本、地图 hash、车辆参数、任务输入、时钟/种子、注入故障、结果和日志引用。性能测量需隔离 DB/Redis/设备调用成本并说明硬件。

## 2. 地图与输入验证

| ID | 场景 | 通过条件 |
|---|---|---|
| ROUTE-TC-001 | 正确有向图、多个 map version/Pack/Layer | 图隔离且解析到明确版本 |
| ROUTE-TC-002 | 重复节点/边 ID、缺失端点、重复资源 ID | 加载拒绝并指出冲突 |
| ROUTE-TC-003 | 负/NaN/Infinity/超范围权重、零/负容量 | map validation fail closed，不裁剪成 0 或默认容量 |
| ROUTE-TC-004 | Edge 指向未知冲突区/层/设备能力 | 版本不发布，报告引用链 |
| ROUTE-TC-005 | 单点图、同起终点、无路径、不同连通分量 | 每种返回明确结果；无图不隐式派发单段 |
| ROUTE-TC-006 | LayerCode/Active/显式 MapVersion 冲突或没有唯一 active | 确定拒绝/要求选择，不退到数据库首条 |
| ROUTE-TC-007 | 地图发布时已有活动任务 | 任务继续固定版本或按迁移策略暂停/安全重规划 |

## 3. 路径成本与属性

| ID | 场景 | 通过条件 |
|---|---|---|
| ROUTE-TC-008 | 唯一路径、多个加权路径、车辆能力不同 | 最优可执行候选符合定义，成本分项可解释 |
| ROUTE-TC-009 | 多条完全相同成本路径 | 固定输入产生稳定相同路线 |
| ROUTE-TC-010 | 不支持方向/转向/载荷/设备段 | 候选被明确剔除，不进入预约 |
| ROUTE-TC-011 | 边成本变化/拥堵输入变更 | 仅优化成本，不越过安全/能力硬约束 |
| ROUTE-TC-012 | Stacker ExeStackCode 合段与 FourWay 多段节点 | 设备段映射正确；不得丢失资源/反馈对应 |
| ROUTE-TC-013 | 容量满、动态封闭边、地图升级 | 不可用边不被规划/预约；活动任务版本规则有效 |

## 4. 冲突区和预约并发

| ID | 场景 | 通过条件 |
|---|---|---|
| ROUTE-TC-014 | 同一有向边同向资源争用 | 容量/时间窗策略符合地图定义 |
| ROUTE-TC-015 | 对向移动但使用不同 edge IDs | 共享冲突资源检测阻止不安全同时授权 |
| ROUTE-TC-016 | 两条路径穿越同一交叉区、节点、窄通道 | 同一冲突域时间窗正确互斥 |
| ROUTE-TC-017 | 两任务时间窗不重叠/边界相接/安全缓冲重叠 | 仅策略允许的窗口 grant；边界误差按缓冲处理 |
| ROUTE-TC-018 | 两个实例并发争取 writer lease 并对同一资源发起 grant | 仅当前 fencing epoch 的写者可提交；冲突资源不超容量；在真实 provider 运行 |
| ROUTE-TC-019 | 多资源路径第二资源冲突/事务失败 | 整条 route 不产生任何有效部分 grant；重试可幂等完成或明确失败 |
| ROUTE-TC-020 | 重复 Grant/Release、错误 owner 释放 | 幂等；非 owner 不能释放他人资源 |
| ROUTE-TC-021 | Lease 过期但物理车辆仍在资源内/位置未知 | 不自动释放；进入核验并保护资源 |
| ROUTE-TC-022 | HotStore/Redis 不可用、进程重启/owner 索引丢失 | 耐久预约可恢复；缓存/进程内索引丢失不造成双 grant |
| ROUTE-TC-040 | 多个请求/调度实例竞争同一冲突资源 | 仅一个符合容量限制的 grant 在耐久账本中提交；失败请求无有效部分预约（REQ-ROUTE-028） |
| ROUTE-TC-041 | 旧 writer epoch 尝试 grant、release 或开始新的 dispatch | 所有旧 epoch 写入/授权被拒绝；原 grant 不被错误释放（REQ-ROUTE-029） |
| ROUTE-TC-042 | 每 Pack lease 到期并由新 writer 接管 | epoch 原子递增；旧 writer 被 fencing；新 writer 完成 reconciliation 前不派发（REQ-ROUTE-029） |
| ROUTE-TC-043 | grant 已提交但 dispatch 前进程崩溃 | 新 writer 查验 command ID、durable grant 和设备状态；不重复派发或遗失资源授权（REQ-ROUTE-028） |
| ROUTE-TC-044 | grant/recovery 时 HotStore 不可用或内容陈旧 | durable ledger 仍决定授权结果；HotStore 恢复只从权威状态重建（REQ-ROUTE-028） |
| ROUTE-TC-045 | Host 重启后恢复 active grant、owner、MapVersion 和 writer epoch | 从持久账本恢复并核对物理状态；不得依赖缓存重建唯一授权事实（REQ-ROUTE-028） |
| ROUTE-TC-046 | lease 已过期但车辆/资源物理占用未知 | 不因 lease/时间窗过期释放；保持保护并进入对账/运维恢复（REQ-ROUTE-029） |

## 5. 在线车辆状态与执行反馈

| ID | 场景 | 通过条件 |
|---|---|---|
| ROUTE-TC-023 | 位置/方向/载荷/状态正常且 freshness 有效 | 只生成适配车辆能力路线 |
| ROUTE-TC-024 | telemetry stale、缺失、sequence 倒退或来源错误 | 状态标 Unknown/Stale；不派发依赖该位置的新冲突路径 |
| ROUTE-TC-025 | 多源位置冲突、map version 不同 | 不擅自平均/选择；暂停并进入恢复诊断 |
| ROUTE-TC-026 | 命令确认丢失但设备已执行，重启后恢复 | Query/反馈对账，不能二次下发非幂等动作 |
| ROUTE-TC-027 | 反馈重复、乱序、错车辆/任务/段/map version | 不错误推进/释放他人资源；有隔离记录 |
| ROUTE-TC-028 | 完成当前段并进入下一段 | 当前资源释放证据有效，下一资源先 grant 后 dispatch |
| ROUTE-TC-029 | Cancel 与完成、超时与成功回调并发 | 单一合法状态转移；物理位置未知时不释放占用 |
| ROUTE-TC-030 | 活动任务重规划、目标在线变更 | 安全点/不可抢占阶段规则成立，旧/新预约无资源空窗 |

## 6. 活性、公平与恢复

| ID | 场景 | 通过条件 |
|---|---|---|
| ROUTE-TC-031 | 典型互锁等待环 | Wait-for/constraint detector 找出独立 oracle 证实的环 |
| ROUTE-TC-032 | 无环的临时排队/共享容量资源 | 不误判死锁或错误抢占 |
| ROUTE-TC-033 | 动态目标/加入新任务导致优先关系变化 | 优先顺序重算安全；已执行不可撤销阶段保留 |
| ROUTE-TC-034 | 一个高优先级流持续到达 | 有界 aging/fairness 条件下低优先级最终获得机会（假设明确） |
| ROUTE-TC-035 | 预约/设备/通信永久故障，恢复前提不满足 | 不声称活性；暂停/升级并给出可恢复诊断 |
| ROUTE-TC-036 | 推荐恢复动作可行、不可行、需要人工 | 自动动作仅在验证安全时执行；否则人工升级 |
| ROUTE-TC-037 | Stacker 深位被挡、移库前后任务重排 | 先安全移浅位；原检索任务只在确认后恢复 |
| ROUTE-TC-038 | FourWay 停车满、提升机同口占用和交叉等待 | 等待/唤醒/环检测考虑这些非路边资源 |
| ROUTE-TC-039 | 恢复期多副本/重复 scheduler scan | 只有有效 writer epoch 可取得新的 reservation/dispatch 权限；重放不重复动作 |

## 7. 性质测试与差分测试

- 随机生成有限图后验证路径所有边存在、总代价正确、tie-break 确定、硬约束不被成本项覆盖。
- 对随机 grant/release 序列验证 owner 隔离、资源容量、幂等和状态机可达性。
- 对对向、交叉、节点共享和 resource-set 冲突构造 differential oracle；禁止仅比较同一实现的 expected/actual。
- 对 constraint-level cycle detector，以穷举小图状态空间作为 oracle，验证无漏环/误报，再扩展随机规模。
- 将固定样例与旧实现做行为差分时，差异必须按安全修正/业务语义变化分类，不默认旧行为为正确结果。

## 8. 仿真与性能协议

性能基准至少覆盖代表性小/中/大地图、不同车辆数、任务到达率、障碍/陈旧状态比例、冲突密度、地图规模和并发调度者。分别测量：

- map validation/load 时间与内存；
- 单车候选生成 P50/P95/P99 和规划图规模；
- 冲突预约 grant 延迟、冲突失败/等待比率；
- 任务吞吐、完成时间分布、利用率和公平性；
- 等待图重建/检测延迟；
- 重启恢复时长、失联与反馈积压恢复时间；
- Provider/HotStore/网络时延和设备通信单独成本。

阈值必须在现场参数和部署硬件确定后经负责人批准；此文不硬编码不具依据的数值。安全/活性断言必须与性能结果分开报告。
