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

## INF-008 多owner迁移与滚动升级要分别证明兼容

- **外部经验待核验 / 基线约束**；来源：LES2通用框架§1/迁库核对。
- 风险：只迁一个context就认为全部就绪；删列/改名时旧实例仍运行；把外部三库名称当作WES部署结构。
- 处理：按WES实际context/owner明确project/startup/context、历史表及依赖顺序；不推断启动自动迁移。破坏性变更先评审Expand/Contract或明确停机策略，保留旧实例读写兼容和可恢复事实。
- 验证：生成并审查每个owner增量SQL、旧新版本并存/退出条件与恢复边界；不以迁移成功代替数据不变量检查。本次没有执行迁移或确认生产拓扑。
- 依据：[INF-003](#inf-003-迁移脚本的默认历史表可能与运行时owner不一致)、[工程治理 §7](../03-engineering-governance.md#7-版本与变更治理)。

## INF-009 数据库精度、删除与比较规则必须检查生成结果

- **外部经验待核验 / 基线约束**；来源：LES2默认上限§6/§9、通用框架§13。
- 风险：默认decimal精度截断/舍入；必填FK级联删除审计；内存编码比较与数据库大小写/尾空格规则不同；大IN列表碰参数数目/Provider翻译限制。
- 处理：精度/scale、FK删除行为、唯一约束和编码规范明确入模并审实际SQL。不要把SQL Server decimal(18,2)、2100参数或OPENJSON兼容级别当MySQL结论；也不要统一Trim/改collation改变已批准身份语义。
- 验证：最小精度/最大合法数、溢出/多余小数、删父保留事实、编码大小写/尾空格/唯一冲突、空/大ID集合；查询仍保留租户/权限过滤，不靠内存全表过滤“解决”翻译失败。
- 依据：[INF-001](#inf-001-mysql-guid集合contains可能在构建命令时失败)、[DEF-004](01-defaults-and-boundaries.md#def-004-数量的0null精度与上限不能混用)、[184迁移SQL审查](../implementation/184-t11-blind-count-tests.md)。本次未验证全部collation/删除矩阵。

## INF-010 IQueryable枚举、不可翻译函数与上下文生命周期要分开处理

- **外部经验待核验**；来源：LES2通用框架§2–3。
- 风险：枚举Reader时又在同一上下文查询，或在Where中调用不可翻译的密码验证函数；简单加ToList可能又造成全表/敏感数据拉取。
- 处理：先在数据库按精确身份/租户和最小字段缩小有界候选，再在正确层执行不可翻译操作；不无条件AsEnumerable、不启用MARS或懒加载掩盖问题。数据库是否支持多Reader不能替代DbContext生命周期设计，也不共享上下文并行操作。
- 验证：枚举中嵌套查询、并发调用、候选为空/多条、跟踪旧值与数据库新值；需要一致快照时不能仅用AsNoTracking代替事务。
- 依据：[INF-005](#inf-005-独立owner测试不能携带上一个写入者的tracked状态)、[INF-006](#inf-006-读取header和事实也需要一致事务快照)。本次未发现或宣称WES存在这些新缺陷。

## INF-011 内存锁、数据库事务与后台任务不是可互换的规避工具

- **外部经验待核验**；来源：LES2通用框架§6。
- 风险：持内存锁跨数据库/网络IO、周期扫描阻塞控制线程、嵌套锁顺序相反会死锁或长期阻塞；随手Task.Run又可能使用已释放request scope。
- 处理：内存锁只保护有界快照/状态变更，后续IO仍需版本/租约或原子owner校验；数据库资源fence为事务一致性设计，不能照搬“锁内不访问库”将其移除。后台任务需明确scope、取消、停机、并发上限、失败可见性。
- 禁用绕过：不迁入LES2的READ UNCOMMITTED扫描策略，不靠fire-and-forget、全表周期扫描或关闭保护锁求快；内存旧值在受保护位置取得，不能用反值猜测。
- 验证：交叉锁序、慢IO、重入、扫描重叠、取消/停机与scope释放；保持提交事实和保护一致。依据：[INF-002](#inf-002-等待资源锁后锁前查到的没有已经过时)、[183](../implementation/183-t11-blind-count-and-local-lock.md)。

## INF-012 日志丢弃与连接并发必须有独立的正确性边界

- **外部经验待核验**；来源：LES2通用框架§7/§11。
- 风险：满队列截断/丢弃的观测日志被当作业务审计或Inbox；连接集合在遍历时被并发修改；仅加null判断掩盖竞态。
- 处理：区分可有损诊断与必须耐久的业务事实/Outbox/Inbox；如诊断队列允许丢弃，记录容量/截断策略、丢弃计数和健康告警，不静默宣称完整。连接登记/移除使用合适同步与快照，发送时仍处理已断连。
- 限定：并发字典只保护单操作，不保证“查找→发送→移除”整个流程原子；推送也不是业务提交或当前权限权威。外部队列2000/单条256KB不变成WES默认。
- 验证：队列满、日志库不可用、敏感字段脱敏、断连重连与发送竞态；关键事实不得随诊断丢失。依据：[工程治理](../03-engineering-governance.md)、[GEN-006](03-development-and-testing.md#gen-006-验证准确门槛和副作用不能只看http成功)；实际实现按相关日志/实时工作包核验。

## INF-013 DbContext池化复用实例，租户与自定义状态不会重新配置

- 记录/更新：2026-10-28；**外部经验待核验（官方机制已查）**；适用：未来考虑AddDbContextPool/PooledDbContextFactory的优化。
- 风险：context跨请求复用，OnConfiguring只在实例创建时运行；把tenant/actor/权限快照留在实例里可能串上下文。DbContext池与驱动连接池不是同一机制，池大小也不是并发业务上限。
- 处理：没有隔离设计和基准证据不引入池化；确需使用时，逐次租用设置可信上下文、确保自定义状态和手工打开的底层连接/会话状态归还前恢复，不能假设EF替应用重置一切。不要为性能去掉租户过滤或当前权限核验。
- 待执行验证：tenant A→B→A复用同实例、取消/故障归还、手工连接开关、事务/会话状态及字段授权不串；和非池化方案比较真实负载。
- 来源：[WEB-003](04-public-source-register.md#web-003-ef-context池化状态)。不是WES已启用池化或已发生越租户的声明。

## INF-014 ExecuteUpdate/Delete不是SaveChanges的快速等价替代

- 记录/更新：2026-10-28；**外部经验待核验（官方机制已查）**；适用：EF关系Provider支持的直接批量DML。
- 风险：立即执行、不更新change tracker，不经过SaveChanges路径；多次调用不会自动组成一个整体事务，之后tracked SaveChanges还可能覆盖批量更新。并发token也不会像跟踪保存那样自动带入。
- 处理：不能用于绕过本项目唯一库存writer、append-only事实或保存守卫；即使普通维护数据使用，也要明确授权过滤、版本谓词/影响行数、事务与上下文刷新方式，并核Provider支持。raw SQL有相同绕过风险。
- 待执行验证：第二条语句失败无部分业务效果、旧tracked实体不会写回旧值、0行/多行更新显式处理、越租户拒绝；账本/审计守卫不能因“批量更快”消失。
- 来源：[WEB-004](04-public-source-register.md#web-004-ef直接批量更新删除)。不据官方性能示例批准改写WMS余额。

## INF-015 数据库执行策略重放要覆盖整个事务，并处理提交结果未知

- 记录/更新：2026-10-28；**外部经验待核验（官方机制已查）**；适用：接入Provider execution strategy/自动重试。
- 风险：提交时断线，事务可能已提交；策略假定回滚后重放可能重复业务。手动BeginTransaction与逐语句重试也不能随意混用；启用重试还可能使结果集内部缓冲增加内存。
- 处理：仅对已确认可重试的错误，在受控整个事务单元中恢复；复用永久operation/精确tuple/唯一约束并查成功事实，不换key、不把设备调用放进会被重放的delegate。客户端生成GUID本身不等于完整幂等方案。
- 待执行验证：提交前失败、提交成功但ACK丢失、验证成功状态时再次断线、整单多行及Outbox仅一次；Provider策略支持/分类未知时不能套用SQL Server范例称MySQL已通过。
- 来源：[WEB-005](04-public-source-register.md#web-005-ef事务重试与提交未知)、[GEN-001](03-development-and-testing.md#gen-001-已提交重放与新动作校验必须区分)。本次未启用新策略或修改既有Unknown合同。

## INF-016 MySQL迁移外面的BEGIN不保证全部DDL能回滚

- 记录/更新：2026-10-28；**外部经验待核验（MySQL8.4官方语义已查）**；适用：迁移/维护DDL，实际服务器版本需另核。
- 风险：CREATE/ALTER/DROP等很多DDL隐式提交；脚本有START TRANSACTION/COMMIT也不代表多条DDL连同迁移历史原子回滚。“atomic DDL”不等于用户可ROLLBACK整组DDL；临时表也存在不能回滚的例外。
- 处理：继续逐项审SQL，同时设计中途失败后结构/历史核对、可重入修复和备份恢复；不在业务记账事务中混入DDL，不把Down当无损撤销，不为验证向生产注入失败。
- 待执行验证：在授权隔离环境按实际MySQL版本验证第二步DDL失败后的表/索引/迁移历史，人工恢复流程须保留证据；历史“SQL审阅通过”不能代签已验证迁移原子性。
- 来源：[WEB-006](04-public-source-register.md#web-006-mysql-ddl隐式提交)。补充[INF-003](#inf-003-迁移脚本的默认历史表可能与运行时owner不一致)、[INF-008](#inf-008-多owner迁移与滚动升级要分别证明兼容)，不否定已取得的具体迁移结果。

## INF-017 全局查询过滤不是完整授权，也可能改变Include结果

- 记录/更新：2026-10-28；**外部经验待核验（官方机制已查）**；适用：EF租户/软删除query filters。
- 风险：必需导航产生INNER JOIN，关联行被过滤后主查询数量也变；IgnoreQueryFilters可能同时移除租户边界，新增过滤配置方式还与EF版本相关。
- 处理：明确各实体租户/删除语义与关联过滤一致性，对实际SQL做权限与计数检查；不能为了“找回缺行”关闭全部过滤或把必需关系改可选。命名过滤器的版本支持不替代当前字段/creator权限。
- 待执行验证：带/不带Include计数、关联被删/被拒、tenant A/B、分页count与明细、管理入口需要的最窄例外；保留复合租户FK与owner权限核验。
- 来源：[WEB-007](04-public-source-register.md#web-007-ef查询过滤与必需导航)。未断言WES目前依赖全局过滤实施授权。

## INF-018 HttpClient连接复用、DNS刷新与Cookie隔离要一起设计

- 记录/更新：2026-10-28；**外部经验待核验（官方机制已查）**；适用：未来外部系统/设备HTTP adapter。
- 风险：每请求new并dispose拥有handler的client会反复建连接并耗端口；长期连接不会按DNS TTL自动刷新；factory池化handler可能共享CookieContainer，回收时又丢cookie。
- 处理：使用既有具名/typed client或有明确PooledConnectionLifetime的长生命周期client，配置依据DNS/服务切换要求，不照抄示例2/15分钟。会话cookie、租户令牌、默认headers不能在并发请求间互相污染；需要cookie的场景单独设计隔离。
- 待执行验证：DNS/目标切换、并发负载/释放、handler回收、两个租户/会话互不串凭据；连接寿命与请求超时分别验证。
- 来源：[WEB-002](04-public-source-register.md#web-002-httpclient寿命dns与cookie)。本条不是新增HTTP依赖/重试策略的授权。
