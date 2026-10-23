# T04 权限、租户与配置审计内部切片

- 状态：内部基础已实现并验证；完整T04实施中，不把T03尚缺登录完整链路或T04页面验收当成已完成
- 依据：[Platform规格](../modules/platform-and-security.md)、[详细设计](../detailed-design/platform-and-security.md)、[PAR-01/02/09](../detailed-design/05-approved-rules-and-defaults.md)
- 范围：FUNC-PLAT-002/003/004/006的内部规则/持久化基础，对应DTC-PLAT-004/005/006/008/009/011/012的部分反例

## 实现思路

Seven的数据范围服务以空集合表示All、部分缺角色默认All，这是WES已禁止的语义，不直接移植。WES用显式All/Own/Departments，空部门集只表示无数据。每个grant绑定具体permission及可见field，**先匹配资源范围再合并field**；不能把角色A在D1的敏感字段权限与角色B在D2的普通读权限组合成对D2敏感字段可见。没有资源范围、当前session、明确租户或permission时拒绝。权限code不等于菜单或按钮可见性。

TenantContext只能从验证过的单一身份及UUID claims解析，不使用URL或默认0补齐；后台服务必须由受信任调用显式建立service context。主体验证与T03 JWT装配未完成前，不开放依赖该context的HTTP写端点。内部Authorization读取当前session、membership与role，不缓存，因此满足普通读不超过30秒缓存、高后果/快照即时复验上限。缺依赖抛出明确故障，不把数据库异常处理为允许。

Role代码租户内唯一，版本为不透明UUID；规则包含permission、scope、明确department集合与字段白名单，调用者可授予范围与字段必须逐条涵盖目标grant。敏感权限不使用roleId=1等特例。角色停用/变更后的下一次判定直接读取当前状态；role规则更新采用expectedVersion并有DB CAS。

内部`ChangeRoleHandler`沿用同一干净owner事务：当前`platform.roles.write`必须覆盖全租户角色的Rules/Enabled字段；操作者既要涵盖被编辑角色的现有权限，也要涵盖目标权限，不能用“先停用/先删除强权限”绕过管理范围。每次成功变更产生1条RoleChanged版本审计；旧版本/缺权限/扩部门/扩字段拒绝且不写。此入口仍不代替用户组织、角色创建/分配、页面预览或HTTP幂等合同。

配置由内部固定目录提供类型/规则/类别B/P/H，客户端不能声明“这是安全字段”。当前只实施PAR初值中的代表性非secret条目，未知key拒绝，非法值拒绝且不回退；H物理值不加默认、不允许客户端自报已验证。Secret不接收任意key/value，更不进入普通设置或audit。一次Settings变更和版本审计在同一Platform事务写入，版本冲突/审计失败均不能只改一半。新建/更新分开表达，不能以缺版本覆盖。

本片审计记录成功设置变更的固定动作、tenant/actor/target/correlation及前后版本，无任意payload/reason文本入口，避免把密码/连接串藏进“说明”；失败安全事件记录固定拒绝code日志，完整登录/权限安全事件尚未接齐。查询受当前权限及tenant过滤、明确时间边界和稳定排序；普通tracked EF的同步/异步SaveChanges拒绝修改或删除Audit。保留治理另走受控维护，不开放网页删除历史。原始SQL/bulk操作与DBA权限隔离不是本片的数据库级防篡改承诺。业务owner仍负责各自事务，本片不创建跨owner通用DbContext。

内部`ChangeSettingHandler`使用独立干净DbContext，先查当前session/role与`platform.settings.write`，再检查tenant全局资源的`Value`字段授权、expectedVersion和类型。Own/Departments不允许编辑无个人/部门归属的全租户设置。一次SaveChanges写设置和audit；真实并发冲突返回明确VersionConflict，不返回新版本；审计错误向上传播，不伪造成功。finally清理其独占图，避免失败后意外再次提交。该内部服务尚不承诺HTTP幂等存档/前端完整写合同。

内部`QueryAudit`只接收明确[from,to)与offset/limit（1..100条；这是内部受控查询上限，不代表已批准导出策略），SQL先tenant/All或Own过滤再分页，按OccurredAt/Id稳定排序。没有部门owner元数据，不将Departments提升为All。至少有Id和OccurredAt字段才能读取行，其余ActorId/TargetId/CorrelationId/Action/前后版本逐字段按当前scope投影，未授权返回null；Denied与获权但无记录分别表达。此查询是live分页，不宣称固定快照，也未实现导出。

## 数据与迁移

Platform新增roles、role_memberships、settings、audit表，全部带tenant复合key/索引；membership的tenant/role复合外键阻断跨租户引用。Role规则在owner内以严格JSON存储，消费者只拿判定/字段投影，不共享私有entity。只在已授权wes_test应用已审查owner迁移，不改库级字符集，不迁移旧账号。

真实测试采用新生成tenant/user/session/role。Audit不可改保护适用于普通EF路径；测试专用清理只在已授权测试数据库用精确已生成tenant参数删除自己的audit，再按外键顺序清理，不能把测试清理入口做成生产API。没有数据库权限隔离验收，不声称DBA管理员不能改历史。

## 用例

| CaseId | 断言 |
|---|---|
| ACCESS-SCOPE-001 | 无角色/缺permission/空部门/他租户拒绝；Own只可见本人；显式All只对本tenant |
| ACCESS-FIELD-001 | 不同scope的grant字段不跨scope合并；集合不可变；列偏好/页面与凭据投影完整链路不在本片验收 |
| ACCESS-DELEGATE-001 | 不可授予没有的permission/部门/field/All；目标空集不放大全域 |
| ACCESS-CONTEXT-001 | 未认证、多重身份/重复或非法claims、缺tenant/空UUID拒绝 |
| ACCESS-CURRENT-001 | 实际数据库role撤权/session撤销后下次判定立即拒绝；不同tenant同role code隔离 |
| ACCESS-TENANT-001 / ACCESS-VERSION-001 | 四类实体同步/异步跨租户写拒绝；真实membership复合FK 1452；角色DB CAS |
| ACCESS-ROLE-WRITE-001 | 角色用例实际检查管理字段及现有/目标可委派范围；拒绝扩权；角色+审计同事务；两handler并发只提交1版/1审计 |
| SETTINGS-VERSION-001 | 合法写原子形成新版本与1条audit；旧版本/非法值/secret/未知key不写 |
| SETTINGS-H-001 | H缺现场证据保持Unverified，不从客户端参数解禁 |
| AUDIT-IMMUTABLE-001 | 普通EF更新/删除历史拒绝；原子提交前故障不留设置或audit半写；tenant受限稳定读取 |
| AUDIT-ATOMIC-001 | 注入真实audit主键1062错误，设置值/版本均回滚，只保留原audit |
| AUDIT-QUERY-001 | Own先过滤再Take；All仍限tenant；时间上界排除；同时间Id次序稳定；字段脱敏；缺权限/非法边界拒绝 |
| ACCESS-MIGRATION-001 | 迁移只新增4个owner表/索引，无库级字符集修改/原始SQL/删表 |

## 红绿与验证证据

- 领域首轮：scope/claims 10失败、3通过；settings/audit 11失败；role 2行为失败。修复后28项领域用例通过（含后来补入的2项直接构造非法grant反例，先红后绿）。
- 新迁移范围测试先红（迁移集合空），生成并审查`20261023164738_AccessSettingsAudit`后转绿；原InitialSessions历史未修改。仅在wes_test应用，未执行业务库/生产DDL。
- 第一轮真实访问持久化用例9失败、3通过：当前授权空壳、audit可改、设置写空壳均被捕获；实现后12通过。审计查询3项先红后绿；再补真实DB审计失败/两handler并发/FK/跨租户写回归与混合scope的查询字段反例，最终本片持久化27通过。
- 使用Oracle10.0.7/EF10.0.7/MySQL8.0.12真实引擎。Platform DDL测试类加入同一不可并行collection；清理仅生成tenant，无InMemory代替、无服务重启。
- 角色用例后续切片：2项先红、6项拒绝反例通过；实现后8通过，补实际handler重叠CAS反例后9通过。本片累计28领域与36迁移/真实MySQL用例通过，仍不将完整父DTC/T04标passed。
- 本片及已有基线Release复测：Modules98、Persistence74、Architecture29，共201 passed、0 failed、0 skipped；Platform构建0警告/0错误、locked restore与格式检查通过。数字不代表T04页面验收，也不覆盖独立Route工作包的审查门禁。
- 曾在并发构建/多项目运行期间观察到一次1秒真实健康探针返回Unhealthy；该12项健康子集单独复测通过。未改生产探针期限、未重试包装成Healthy。最终先统一构建，再以`dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1`串行执行共享实例测试通过；并发压力下的健康抖动不据此宣称已消除。

本片不是T04整包完成：用户/组织CRUD、角色编辑/租户切换/配置/审计页面、受权HTTP/E2E和完整T03依赖仍必须接齐；不借内部API单元绿灯对外开放未认证管理功能。
