# T12 有限流程与运行时表单校验内核

- 状态：已验收（仅本纯校验子片）；2026-10-29。Owner：WorkflowForm。
- 依据：[模块规格](../modules/workflow-and-forms.md)、[详细设计](../detailed-design/workflow-and-forms.md)、[RULE-10/11](../detailed-design/05-approved-rules-and-defaults.md)、[开发计划T12](../superpowers/plans/2026-10-23-wes-development-work-packages.md)。
- 测试记录：[188](188-t12-definition-validation-core-tests.md)。本片只建立后续持久化/页面共享的纯校验内核，不整体关闭T12。

## 1. 选择、边界及依赖

仓库尚无WorkflowForm模块。直接先建页面或持久化JSON会使前后端各自猜测图/schema规则；先建立独立owner编译/验证入口，输出脱离调用方可变集合的只读快照，供后续持久owner复用。不是内存仓库，不产生“已发布”或审批成功回执。

本片实现：受限流程图及候选校验、schema版本固定引用、表单定义/数据校验、互斥分支求值及显式优先级选择。引用新owner项目，无生产Host装配/开关/HTTP/UI，不访问WMS或Platform私表，不产生源码、动态代码或SQL。纯确定性测试无需Provider；真实MySQL不可变发布/并发及页面留T12续片，实例/实时资格/转派/Outbox/hook留T13。授权上下文、合法文件引用/可用候选集合必须由未来受权owner提供，调用本内核本身不授予身份、候选或文件权限。

## 2. 具体模型与规则

- schema与流程均有非空tenant/id/version及业务类型代码。流程固定schemaId/schemaVersion，必须匹配同tenant/业务类型的已校验schema，不隐式选最新版。
- schema含1..100字段，稳定ASCII key（1..64，以字母开头，余为字母数字下划线），标签资源key须合法；字段唯一，类型白名单Text/Decimal/Boolean/File，未知枚举拒绝。字段有required/writable、敏感级别、显式允许条件引用；Restricted/File不允许条件引用，不执行任意表达式。
- Text最多4096个UTF-16代码单元，有显式1..4096最大长度，required时非空且不能仅空白；标签资源key为1..128个ASCII字母/数字/下划线/点且以字母开头；标识不接受尾部换行。Decimal固定十进制string，最多22整数/6小数、精度0..6，无指数/空白/舍入/浮点/前导加号/多余整数前导零；可选上下界必须自洽且能按该精度精确表示；Boolean仅JSON布尔；File仅非空D格式UUID string。类型不适用的长度/数值约束拒绝，类型修改后旧default若不兼容拒绝，不静默清除。
- 默认值若提供须类型/范围合法。File默认引用不允许，以免把旧主体的文件访问许可固化为新授权。required的null无效，optional的null为显式空值；缺失且无默认的required拒绝。
- 流程3..100节点，唯一Start与End，至少一个Approval；Start/Approval恰一条无条件出边，End无出边。ExclusiveBranch恰两条指向不同节点的出边：一个白名单条件和一个明确otherwise。目标节点必须存在，全部节点从Start可达、无环并能到End；不存在任意并行/循环或未知节点跳过。节点ID与所有非空引用严格；每Approval输入候选1..100项并去重，全部属于owner提供的当前合法集合，默认All；未知会签模式拒绝。首片仅显式用户候选，角色/组织解析仍属续片。
- 条件仅允许一个schema白名单字段及Equal/NotEqual；Decimal另允许GreaterThan/GreaterOrEqual/LessThan/LessOrEqual。比较值按实际字段类型/范围验证。求值使用固定schema的受权业务快照，缺字段、错误类型、越界值明确返回错误，不走otherwise当成功。
- 候选绑定阶段给定提交人及当前合法集合；候选含提交人或已失资格则明确失败，不自动过滤/换人或批准；真正task资格实时核验仍属T13。
- 对已匹配流程按显式非负priority降序选唯一最高者；最高并列返回Ambiguous，无匹配返回NotFound，不能靠集合顺序选择。候选必须同tenant/业务类型，同版本身份不可重复；本入口不替代未来owner对适用条件的受权匹配。

## 3. 表单数据边界与快照

数据入口接收schema、同tenant的owner上下文、客户端可写values和owner独立只读businessValues。两集合均最多100键；未知键、重复键（使用逐项集合检查而不是先转Dictionary覆盖）、客户端只读字段、无当前写字段权限全部拒绝。owner只读输入不可夹带可写字段；required只读字段缺失拒绝，不用客户端值或default补造权威字段。

File字段要求owner上下文中同tenant已核验可访问的UUID集合匹配，无默认允许或自报tenant；这里不下载文件，不声称已接真实Platform文件鉴权。未来持久化/读取/下载必须再次通过公开owner合同复验。

所有成功快照中的输入JsonElement复制并脱离原JsonDocument生命周期，字段/节点/候选/边集合深复制/只读封装。schemaS2不能更改既有S1快照解释；没有“发布最新版”静态全局缓存。输出DefinitionResult：成功才有值，否则仅包含稳定code/path问题，不返回部分已合法数据作成功；不会静默丢弃非法字段。

输入为模块内部强类型模型，而非宽松HTTP JSON绑定；外部未知/重复对象属性、null集合、请求字节/JSON深度、读字段权限投影、格式/显示条件/保留策略及角色组织候选均留后续具名合同。不得把本片的字段Writable布尔和owner集合证明当作Platform授权适配器已接通。

## 4. 用例与完成门槛

| ID | 输入 | 断言 |
|---|---|---|
| TC-T12-CORE-01 | 线性/有限排他图、All默认/Any、重复候选 | 唯一可达终点、候选去重、快照不可被原集合修改 |
| TC-T12-CORE-02 | 缺终点/断开/环/并行/未知类型/空候选/失资格/自批 | code/path明确拒绝，无成功结果 |
| TC-T12-CORE-03 | 字段白名单、缺值/类型错、Decimal边界、分支命中/否则 | 不运行脚本，不用缺值当否则 |
| TC-T12-CORE-04 | 匹配流程不同priority/平级/空/越tenant或business | 唯一明确结果，顺序无关 |
| TC-T12-CORE-05 | schema类型/范围/default/同名键、100与101字段 | 精确边界和明确拒绝，不清不兼容default |
| TC-T12-CORE-06 | 数据0/超精度/只读/额外/重复/未授权/跨tenant文件 | 整体无值，合法输入精确保留 |
| TC-T12-CORE-07 | S1数据、S2改字段、原集合/JsonDocument销毁 | S1仍按原版校验，快照稳定 |

先运行真实RED，再最小实现及GREEN；完整Release/架构/定向format与文档链接检查。适用DEF-004/005/006/012，GEN-003/004/006/010及INF-004；新发现随本片回写。没有数据库、网络或设备副作用，不能据纯测试宣称DTC-WF-001..009父用例或持久版本已通过。
