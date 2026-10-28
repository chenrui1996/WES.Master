# 底层与基础设施

首次记录/更新：2026-10-28。下述 Provider 限制是已验证环境中的行为，不是所有 EF/数据库版本的普遍结论。维护方式见[索引](README.md)。

## INF-001 MySQL Guid集合Contains可能在构建命令时失败

- **已复现并修复**；适用：本项目EF Core10/MySql.EntityFrameworkCore10.0.7/MySql.Data9.7.0相关Guid集合查询。
- 现象：新增源/目标及位置/容器查询后，17条原Transfer/入库最终运输路径出现命令构建NullReferenceException；178也记录过nullable Guid集合问题。不是凭异常名称就能判定 DbContext 并发。
- 处理：固定两个ID用显式等式OR；多ID复用WMS内 `WmsIdentityPredicate.MatchesAnyId` 等式表达式，空集合恒false。不复制helper、不把查询拉到内存绕开授权/范围过滤。
- 验证：空/单/多ID和nullable源目标；真实MySQL相关327项修复回归及最终923项通过。升级Provider后先复现/实测，再决定是否移除规避。
- 证据：[178](../implementation/178-t09-authorized-inbound-execution-tests.md)、[184 §4–6](../implementation/184-t11-blind-count-tests.md)。

## INF-002 等待资源锁后，锁前查到的“没有”已经过时

- **已复现并修复**；适用：零余额登记同key并发，及类似永久回执写入口。
- 现象：第二请求等待fence期间第一请求已提交；第二请求拿锁后直接查新写冲突，把自己的成功余额判为Conflict，实际应Created+Replayed。
- 处理：按既有父仓→位置→容器锁序取得fence；等待后重查原operation，核精确tuple/当前权限并恢复赢家回执，再检查真正的新写冲突。不能用“忽略冲突”或再次创建恢复。
- 验证：同key同body、同key换body、不同key同维度并发；确认只一份余额和事实。最终923项回归包含修复路径。
- 证据：[184 §4–6](../implementation/184-t11-blind-count-tests.md)、[183](../implementation/183-t11-blind-count-and-local-lock.md)。

## INF-003 迁移脚本的默认历史表可能与运行时owner不一致

- **已复现并修复**；适用：WMS EF design-time factory/SQL生成。
- 现象：design-time默认 `__EFMigrationsHistory`，runtime却使用 `__WesWmsMigrations`，构建成功也不能证明脚本可用。
- 处理：设计时和运行时显式保持owner历史一致；WMS命令project/startup均使用WMS项目，EF配置用 `--configuration Release`，避免把 `-c` 当作构建configuration（EF将其用于context）。
- 验证：无模型漂移之外，还审增量Up/Down的历史表、DDL、tenant约束/索引和旧账是否被改；不要仅依赖C#迁移中的意图，实际生成SQL才是审阅对象。Down只审不执行；迁移应用仅授权隔离wes_test。
- 证据：[174](../implementation/174-t08-warehouse-operational-state.md)、[184 §6](../implementation/184-t11-blind-count-tests.md#6-最终发布门禁与关闭范围)。

## INF-004 隐式using和格式生成产物会掩盖门禁差异

- **已复现并修复**；适用：Host端点独立Roslyn架构编译、EF迁移格式。
- 现象：普通Host构建通过，架构编译却因Counts缺显式Routing using失败；新迁移BOM/新增C#缩进也导致whitespace门禁失败。
- 处理：明确引用所需命名空间，保持独立架构检查；使用既有 `dotnet format whitespace` 只处理变更C#，再verify。不关闭架构测试或放宽格式器。
- 验证：Release完整解决方案、架构测试和changed-file格式分别通过，三者不能互相代替。
- 证据：[182 §2.4](../implementation/182-t10-outbound-basic-flow-tests.md#24-最终发布门禁)、[184 §4–6](../implementation/184-t11-blind-count-tests.md)。

## INF-005 独立owner测试不能携带上一个写入者的tracked状态

- **已复现并修复**；适用：组合内部primitive与当前受权owner的持久测试。
- 现象：178首轮4例在内部primitive创建后残留tracked状态，被独立owner保存守卫拒绝。
- 处理：按真实工作单元隔离上下文，或在适用测试中清理已结束工作单元的tracking后再调用独立owner；不能在生产保存守卫中加绕过或吞掉异常。
- 验证：重新从数据库读取最终状态/事实，保留非法直接改写被拒绝的负例。不要把此条当成随意Clear以丢弃未提交业务的许可。
- 证据：[178](../implementation/178-t09-authorized-inbound-execution-tests.md)首轮117项修复记录。

## INF-006 读取header和事实也需要一致事务快照

- **已复现并修复**；适用：出库header/facts组合查询。
- 现象：两次查询之间另一合法事务审核，读出新旧混合数据，被误判历史损坏。
- 处理：使用同一repeatable-read快照构造完整投影，仍保留真实历史损坏拒绝。不catch为成功、不放宽事实守卫。
- 验证：在header/facts之间插入真实并发写，读结果完整一致；同时测试真实篡改仍失败。
- 证据：[182 §2.3](../implementation/182-t10-outbound-basic-flow-tests.md#23-独立审查与关闭条件)。

## INF-007 共享测试环境的资源争用与业务失败要分开定位

- **已复现；单worker复测通过**；适用：T10前端整套回归。
- 现象：并行1107项中3项既有分页/候选用例触发原5秒超时；同代码单worker1107全部通过。这支持该次资源争用解释，不证明所有超时都是环境问题。
- 处理：保留失败记录，先看断言/时序；用 `npm test -- --maxWorkers=1` 复测，不删测试、不先调大超时。.NET关联命令串行，真实浏览器运行期间不重建dist。
- 新父子表还需按随机fixture tenant、子事实→父单顺序定向清理；曾有出库浏览器业务走完但清理FK失败，仍应判整次失败。不得关闭FK或清共享库其他数据。
- 证据：[182 §2.2–2.4](../implementation/182-t10-outbound-basic-flow-tests.md)、[184](../implementation/184-t11-blind-count-tests.md)。
