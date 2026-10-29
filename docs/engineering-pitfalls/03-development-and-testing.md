# 通用开发与测试

首次记录/更新：2026-10-28。维护方式见[索引](README.md)；记录的是项目中可复用的失败模式，不代替模块合同。

## GEN-001 已提交重放与新动作校验必须区分

- **已复现并修复**；适用：可信入库运输完成Inbox。
- 现象：运输已Applied，之后盘点锁生效；原完成事件恢复被新锁错误Blocked。
- 处理：先验证body/intent/可信adapter身份，对已Applied同一原事件恢复不可变posting，不再执行新移动；未Applied仍检查所有当前保护。绝不能将“重放优先”变成绕过当前身份/权限/关联校验的公共捷径。
- 验证：`AppliedTransportCompletionReplaysWhileLaterCountProtectsItsContainer`已有RED/GREEN；同时保留新执行被锁拒绝及换tuple冲突。
- 证据：[183](../implementation/183-t11-blind-count-and-local-lock.md)、[184 §5](../implementation/184-t11-blind-count-tests.md#5-独立审查与关联修复)。

## GEN-002 Unknown恢复上下文不能跟随查询组件卸载

- **已复现并修复**；适用：主数据/附件/出入库/盘点页面的父子组合。
- 风险：写提交使详情stale/清空，若确认面板在QueryState分支内会被卸载，丢失唯一原key/body；子组件反向关闭父预览也可能取消自身。
- 处理：原操作面板生命周期独立于详情loading/error/stale；冻结原kind/id/key/body，主体或目标变化清私有数据并隔离迟到响应。无实体不挂载新的业务写；恢复原Unknown按其合同核当前权限，不要求把原tuple替换成刷新后的版本。
- 验证：必须测真实父子组合“预览→提交清详情→Unknown保留→手工原键恢复→显式GET”，不是只测孤立确认组件。不上线自动补发，不存本地写队列。
- T11移库真实浏览器另暴露“创建成功→另读→准备下一单”仍带上次两行，脚本增行后产生空第三行，预览拒绝。新草稿必须显式初始化新行身份/输入，不能无意复用已确认输入；同时不得因此清除正在恢复的原Unknown。该连续两单流程已补组件RED/GREEN并通过真实HTTPS/持久效果验证，见[186](../implementation/186-t11-transfer-order-tests.md)。
- 证据：[172](../implementation/172-t07-current-entity-attachment-client-tests.md)、[174](../implementation/174-t08-warehouse-operational-state.md)、[184](../implementation/184-t11-blind-count-tests.md)。

## GEN-003 枚举/契约扩展必须贯穿所有消费者

- **已复现并修复**；适用：ledger新增AdjustmentIn=3/AdjustmentOut=4。
- 现象：后端合法产生调整流水，旧客户端严格parser不认识类型，导致历史整页拒绝。
- 处理：同步DTO/类型、parser白名单、正负符号守卫、筛选、标签和实际页面；保留未知值明确失败，不能直接把parser改成接受任意数字。
- 验证：真实调整响应形状、正入/负出及错误符号、筛选和标签。两例RED后修复；只改enum不是完成。
- 证据：[183](../implementation/183-t11-blind-count-and-local-lock.md)、[184 §5](../implementation/184-t11-blind-count-tests.md#5-独立审查与关联修复)。

## GEN-004 当前权限不等于创建时权限，也不只看单据creator

- **基线约束**；适用：受权业务owner及原tuple恢复。
- 规则：核当前主体、租户、字段和真实资源creator范围；父仓/位置/容器/物料等跨creator组合需要完整权限交集。预览/旧令牌/成功原操作不能永久授予新写权限。
- 处理：按合同在写入后提交前再次核验，撤权整事务回滚；读后复验清旧投影；独立review不能通过盲盘响应泄露账面量。
- 验证：逐字段缺权、不同creator/部门、保存后撤权、越租户、撤权后重放和复核清私有数据，不能仅测管理员happy path。
- T11原键并发还须覆盖“初次读无原fact→等待资源fence→赢家提交→权限撤销→锁后查到原fact”。锁后直接返回原回执会误用等待前权限；修复为持锁重读原单/全部真实creator资源并复核当前action/read，不把新动作state/schema条件加到已提交重放。16项确定性RED/GREEN及授权保留/schema不可用仍合法重放见[186](../implementation/186-t11-transfer-order-tests.md)。
- 证据：[177](../implementation/177-t09-authorized-inbound-execution.md)、[183](../implementation/183-t11-blind-count-and-local-lock.md)、[184](../implementation/184-t11-blind-count-tests.md)。

## GEN-005 原子审核、物理完成与数据库回滚不是同一层

- **基线约束**；适用：下一TransferOrder及运输集成。
- 规则：审核全行同事务，不留部分有效预留；已实际完成的执行单元独立提交，不能因其他行失败“回滚物理搬运”。实物已到而记账失败须保留可恢复完成事实及来源/目标保护，显示待同步。
- Unknown/在途不能回退可用；部分已执行取消剩余要ClosedPartial，不伪造Completed/全单Cancelled。没有实际adapter不得补一个默认成功或人工Completed接口。
- 验证：两行任一不足全不审核、A完成B失败保留A、已到落库失败仅补一次、Unknown仍保护、取消只关闭安全未执行部分；列出的未来用例不是本知识库已执行证据。
- 证据：[规则基线 RULE-07/08](../detailed-design/05-approved-rules-and-defaults.md)、[179](../implementation/179-t09-durable-inbound-transport-design.md)、[TodoList](../implementation/TodoList.md)尚未完成软件事项。

## GEN-006 验证准确门槛和副作用，不能只看HTTP成功

- **已复现并修复 / 基线约束**；适用：所有切片收口。
- 失败模式：普通build代替架构编译、mock代替真实Provider、一个浏览器happy path代替并发/未知恢复、只看200不查余额/流水/事实/锁；T11首次完整回归揭示18个精选漏掉的问题。
- 处理：改共享底层必须覆盖原调用路径；行数上限测真实100行全链，响应丢失必须是服务端已提交后截断交付，再手工同key/body恢复，确认效果恰一次。
- 证据分别记录命令/选择器、环境、通过/失败/跳过、持久结果及未运行范围；重叠选择器不相加称新增覆盖。审查报告不能代替测试；旧失败保留，末尾追加修复最终结果。
- 证据：[工程治理 §6](../03-engineering-governance.md#6-测试质量门禁和-pr)、[182](../implementation/182-t10-outbound-basic-flow-tests.md)、[184](../implementation/184-t11-blind-count-tests.md)。
- T12补证（2026-10-29）：错误定位也要验证精确path，不能只断言“存在错误”。坏key不加入去重集合时，以集合Count当字段index会把下一行错误指回前行；`SchemaIssuePaths_UseOriginalIndicesAfterInvalidKeys`先1RED后按原数组索引修复。100字段实际数据及100节点实际遍历验证不截断，见[188](../implementation/188-t12-definition-validation-core-tests.md)。没有据纯内核通过宣称MySQL发布或真实文件鉴权已完成。

## GEN-007 工作树/附件路径/外部文档与真正缺文件要区分

- **已观察；文档路径检查约束**；适用：当前独占worktree与文档链接检查。
- 现象：附件有时把当前worktree前缀再次嵌套，报ENOENT；真实仓库docs仍存在。README/feature-switches的旧外部Seven.Master目标在此worktree也可能不可用。
- 处理：以当前仓库根为准定位一次实际路径；不创建重复worktree层、不改其他聊天目录、不把错误附件路径当业务文档缺失。检查文档相对链接时区分新增断链与基线缺外部目标，并如实报告两者。
- 验证：本次请求附件即出现重复嵌套；T11最终文档检查11份、0新增断链、7个基线外部目标不可用。历史数字仅对应当次工作树，不沿用作本次检查结果。
- 证据：[184 §6](../implementation/184-t11-blind-count-tests.md#6-最终发布门禁与关闭范围)。外部来源仍按内容访问限制处理，不能为补缺失而越权读取。
- T12文档检查补证：本地Windows PowerShell的Get-Content缺Encoding时，UTF-8中文文件链接被错解为含`?`的路径，GetFullPath报非法字符；显式`-Encoding UTF8`后13文档1000目标检查完成，0新增断链。不能把解码错误记为仓库缺文件或用此绕过内容限制，见[188](../implementation/188-t12-definition-validation-core-tests.md)。

## GEN-008 页面/App适配复用业务owner，不复制旧反射框架

- **外部经验待核验 / 基线约束**；来源：LES2通用框架§4/§9–10。
- 风险：App伪造Web字典调用反射入口；嵌页重复加载运行库破坏插件；保存列布局/重建组件时丢事件与模板。
- 处理：WES维持具名DTO、可信主体和薄HTTP/客户端适配，复用同一业务owner，不迁入dic反射、Kendo生成式CRUD或新jQuery。Vue通过现有组件生命周期装配，避免重复运行时/全局初始化副作用。
- 列偏好仅包含允许的列宽/顺序/显隐，并有版本和重置语义；不存业务行/令牌/待写输入，不能显示未授权字段。隐藏列也不等于服务端字段授权。
- 验证：路由反复进入/退出、重复初始化、事件只执行一次、偏好升级/损坏/重置、主体切换清理和字段撤权。
- 依据：[工程治理 §3](../03-engineering-governance.md#3-前端规范vue-与-uniapp)、[密集页面合同](../detailed-design/03-dense-page-interaction-contract.md)。本次不新增前端代码或框架。

## GEN-009 认证迁移不等于复制旧密码种子或管理员特例

- **外部经验待核验 / 基线约束**；来源：LES2通用框架§2/§5/§12。
- 风险：前端旧哈希/缓存与服务端算法不兼容、测试种子进入新环境、将管理员标签等同全部资源权限；HTTPS被密码哈希错误替代。
- 处理：按WES身份设计核对登录/改密/其他客户端协议、服务端验证格式和迁移策略；不复制外部种子密码、旧MD5或固定管理员授权。密码不入普通日志，部署连接仍必须有可信TLS。
- 权限保存后的服务端授权/会话失效与菜单缓存分别验证；不能照搬“重新登录才生效”以允许旧会话越过当前撤权，也不能复制旧系统本地仓库过滤例外。
- 验证：旧新格式/非法格式、旧客户端缓存、身份与角色撤销、多资源creator、登录与业务请求HTTPS边界；本次未审计密码实现或改算法。
- 依据：[GEN-004](#gen-004-当前权限不等于创建时权限也不只看单据creator)、[工程治理](../03-engineering-governance.md)。外部密码方案不是本条对WES现状的声明。

## GEN-010 全量保存只对“确实完整的集合”成立

- **外部经验待核验**；来源：LES2通用框架§3/§5。
- 风险：授权树按需加载只提交已展开节点，却用全量替换删掉其他授权；半选/不可见子节点语义不明；父子更新缺导航就错误推断删除。
- 处理：合同明确全量替换或增量patch，声明半选/未加载/缺字段/空集合语义，使用稳定ID和版本。无法确定集合完整时显式拒绝，不把读取失败当“清空全部”。更新与删除通过受权owner，不合并前端任意实体字典。
- 验证：未展开子树、分页只读部分、取消勾选、明确空集合、缺失节点ID、并发变更、越权节点和保存失败回滚；前端状态不能替代服务端完整性校验。
- 依据：[DEF-012](01-defaults-and-boundaries.md#def-012-缺字段null零值与json数字都有各自语义)、[工程治理](../03-engineering-governance.md)。本次没有断言WES使用旧授权树或其全量保存机制。

## GEN-011 async watcher的依赖收集、清理注册和卸载停止有不同边界

- 记录/更新：2026-10-28；**外部经验待核验（Vue官方机制已查）**；适用：Vue3异步详情/候选查询。
- 风险：watchEffect只收集同步段访问的依赖，首个await后才读的主体/路由不自动追踪；3.5+ onWatcherCleanup必须同步注册；异步回调中创建的watcher不自动绑定组件卸载。
- 处理：安全敏感查询显式watch完整上下文/getter，在发起IO时注册取消，另核响应generation/主体指纹。回调参数onCleanup与onWatcherCleanup的限制不同，不混写；尽量同步创建watcher，异步创建时持有stop并显式清理。
- 待执行验证：主体/路由/字段权限在等待中切换、旧请求不可取消仍返回、卸载后完成、条件分支改变依赖、watcher重复创建。AbortController不能代替迟到结果核验。
- 来源：[WEB-008/014](04-public-source-register.md#web-008-vue-watchers)、[GEN-002](#gen-002-unknown恢复上下文不能跟随查询组件卸载)。GitHub讨论是文档语义线索，不称Vue当前版本存在已确认缺陷。

## GEN-012 reactive解构与Vue3.5 props编译转换不能混为一谈

- 记录/更新：2026-10-28；**外部经验待核验（官方语义已查）**；适用：Vue3响应式对象/组件props与composable。
- 风险：普通reactive属性解构成局部原始值，后续访问不再触发原proxy；但Vue3.5的script setup内defineProps解构有编译转换，不能笼统禁止或假定跨函数传值仍可追踪。
- 处理：按需要传ref/getter或保留props/对象属性访问；不得把watch的源写成已求值的普通值。非原始对象内部仍可能响应，proxy与原对象引用也不相同，身份/key用稳定业务ID而非对象引用猜同一行。
- 待执行验证：父prop更新、composable读取、字段换值/嵌套修改、列表换对象、构建器与Vue/compiler版本匹配；不要仅凭TypeScript通过认为运行响应式正确。
- 来源：[WEB-009/010](04-public-source-register.md#web-009-script-setup的版本相关默认行为)。本次不变更现有组件。

## GEN-013 分页稳定排序不等于跨请求固定快照

- 记录/更新：2026-10-28；**外部经验待核验（EF官方文档已查）**；适用：实时目录、导出、审计核对。
- 风险：仅按时间排序，同一时刻多行会跨页跳行；offset在并发插删时可能漏/重复，排序完全唯一也不能保证跨请求读取同一数据集。数据库主键存在不表示默认ORDER BY。
- 处理：按合同使用唯一复合排序和对应索引；需要前后顺序遍历时评估keyset，任意页跳转仍需权衡offset。不能私自把现有分页API改cursor；要求固定核对结果时另用已批准快照机制，不能把keyset称为一致性快照。
- 待执行验证：相同时间100行、页间插删/排序字段更新、权限撤销、空页与最后一页、导出不漏重；游标绑定租户/过滤/版本并受验证，不能借其绕过当前授权。
- 来源：[WEB-011](04-public-source-register.md#web-011-ef分页)、[DEF-010](01-defaults-and-boundaries.md#def-010-分页和实时通道都不能默认自然有界)。未声明发现WES当前目录分页缺陷。

## GEN-014 浏览器自动等待、测试隔离与真实效果验证各管一层

- 记录/更新：2026-10-28；**外部经验待核验（Playwright官方建议已查）**；适用：真实HTTPS组合测试。
- 风险：用固定sleep或即时isVisible快照断言造成时序不稳；测试间共享cookie/存储/账号业务数据互相影响；反复重试动作可能掩盖重复提交副作用。
- 处理：用语义role/label定位和await web-first assertion等待目标状态，隔离browser context/tenant/业务数据；服务端事实/流水/锁仍单独核验。UI可点击不代表当前受权，DOM成功也不代表数据库成功。
- 待执行验证：延迟响应、双账号独立context、前例失败后的清理、真实201提交后截断交付的恢复；外部依赖可有明确contract夹具，但不能mock掉本次声称真实验证的WMS写。
- 来源：[WEB-012](04-public-source-register.md#web-012-playwright最佳实践)、[GEN-006](#gen-006-验证准确门槛和副作用不能只看http成功)。不新增代码生成器，也不以框架自动等待代替业务Unknown策略。
