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

## DEF-007 请求没进端点，先按整条链路核对容量限制

- **外部经验待核验**；适用：大JSON、上传、批量提交及反向代理。来源：LES2默认上限§1–4。
- 风险：入口前的代理/服务器/解析器先拒绝，端点断点不会触发；只调大业务数组上限无效。实际正文预算由整条链路最小限制决定。
- 检查表：记录代理正文/URL限制、Kestrel请求行/头总量/数量/正文限制、解析器限制、业务大小/行数限制，以及生效配置和版本。表单键数量不是业务行数，单字符串表单值也可能超长；JSON不走表单键限制，但仍受正文/深度限制。
- LES2材料中的表单1024键、单值4MB、multipart 128MB、Kestrel正文30,000,000字节、Nginx正文1MB仅作为排查候选，**未确认WES部署采用这些值**。不据此直接抬高WES配置。
- 验证：每层有效上限的M-1/M/M+1字节、缺失/伪造Content-Length、chunked及多字节字符；保留实际拒绝层、状态码与关联日志。当前仅查到WES附件采用独立有界流读取，不代表代理链已验证。
- 本地依据：[FileUploadBody](../../src/Host/Platform/FileUploadBody.cs)。大列表优先稳定JSON合同、受限分页/分批；不能为绕过上限把SQL或内部context名称搬到URL。

## DEF-008 深度、表单绑定和JSON解析不是同一套限制

- **外部经验待核验**；适用：树形授权、层级配置、导入。
- 风险：MVC集合绑定/校验、System.Text.Json、Newtonsoft及旧JavaScriptSerializer各有不同机制；旧材料中的1024集合、32/64层或2MB不是WES统一值。
- 处理：先确认实际端点的解析路径，再定义字节、数组长度、嵌套深度和字段集合；超限显式拒绝，不能设int.MaxValue或改成无限递归。
- 验证：最大合法深度与下一层、宽而浅的大集合、未知/重复字段和非法JSON；不同Content-Type不得绕过同一业务边界。本次未运行这些新增容量用例，后续触及相关入口时核验。
- 依据：[工程治理](../03-engineering-governance.md)、[DEF-005](#def-005-声明100行就要验证全链容量而非只有数组校验)；不因换JSON而宣称无限容量。

## DEF-009 连接、命令、HTTP、代理与业务期限分别记账

- **外部经验待核验；局部代码已查阅**；来源：LES2默认上限§5。
- 风险：连接串等待长不代表SQL执行可等同样久；代理断开、客户端取消也不能证明业务没提交。TaskCanceledException不能单凭类型判为命令失败或允许重发。
- WES已查事实：隔离持久测试将ConnectionTimeout设5秒、DefaultCommandTimeout设10秒；这是测试连接配置，不是生产默认，更不是SQL Server的30秒结论。LES2的100秒HttpClient或特定路径3秒代理值不移植为WES事实。
- 处理：分别记录连接/命令/调用总期限/代理读写/业务租约/会话期限与重试策略；取消令牌贯穿IO，写入结果不明仍按Unknown恢复，不通过无限超时隐藏慢查询。
- 验证：连接未建、SQL执行超时、用户取消、已提交后代理断流；断言有无事实和副作用次数。依据：[测试连接](../../tests/Persistence/MySqlTestConnection.cs)、[DEF-003](#def-003-10秒-unknown-不是服务端失败1800秒提醒不是解锁)。

## DEF-010 分页和实时通道都不能默认“自然有界”

- **外部经验待核验**；来源：LES2默认上限§7–8。
- 风险：UI pageSize不约束服务端；page/pageSize非法若返回全表，或实时Hub接收整仓状态，容易放大内存/数据库负载。入站消息限制不等于出站推送限制。
- 处理：服务端独立约束页大小、稳定排序与授权后分页；拒绝非法值，不退化全量。实时通道启用时核对方向、单消息、频率、连接数、背压与增量/快照方案，不直接复制外部32KB作为双向统一预算。
- 验证：缺省/0/负数/超大分页、跨页权限、超大单消息、慢消费者与重连；相关能力未接入时记录未接入，不新增默认成功Hub。
- 依据：[工程治理](../03-engineering-governance.md)、[入库目录合同](../implementation/173-t09-inbound-basic-flow.md)。本次未审计所有查询端点或证明实时通道已实现。

## DEF-011 文化、UTC与时区必须在边界显式定义

- **外部经验待核验 / 基线约束**；来源：LES2默认上限§9。
- 风险：无参ToString/Parse随进程文化变化；本地时间写入无时区字段后含义丢失，容器与开发机时区差异会误算期限。文化中间件放在终止请求的端点后也不会生效。
- 处理：业务时刻遵循已有UTC/受控TimeProvider合同；传输固定格式，十进制字符串不按浏览器文化重解释。仅展示时转换时区；不靠设置TZ修复已混写的数据，也不把DateTime.Now的Kind误写成Unspecified。
- 验证：zh-CN/en-US、UTC/本地时区、临界午夜、期限边界；解析后必须还是同一时刻/数量。中间件顺序需检查实际Host与扩展，不凭Program单文件推断全链。
- 依据：[规则基线](../detailed-design/05-approved-rules-and-defaults.md)、[Host入口](../../src/Host/Program.cs)。本次未执行跨文化/时区矩阵。

## DEF-012 缺字段、null、零值与JSON数字都有各自语义

- **外部经验待核验 / 基线约束**；来源：LES2默认上限§9、通用框架§8。
- 风险：缺失bool/枚举/数量若落到CLR默认值，会被误当显式false/0；忽略null序列化可能改变更新语义；大整数或精确小数进入JS Number会丢精度。
- 处理：按具名DTO严格声明必填、允许null、命名大小写和枚举表示，缺字段显式拒绝或遵循已批准patch语义；不得从CLR默认反推用户输入。超过JS安全整数范围的标识采用合同定义的string；库存数量继续精确十进制string，不返回EF导航实体避免循环/懒加载与越权。
- 验证：字段缺失/null/false/0分别覆盖；未知/重复属性、枚举未知值、`2^53`边界及精确小数往返。不得全局IgnoreNull或接受任意enum来“兼容”错误报文。
- 依据：[183严格合同](../implementation/183-t11-blind-count-and-local-lock.md)、[GEN-003](03-development-and-testing.md#gen-003-枚举契约扩展必须贯穿所有消费者)。现有盘点严格测试不代表所有未来DTO都天然严格。

## DEF-013 缺配置、TLS终止和Cookie默认不能从旧环境类推

- **外部经验待核验**；来源：LES2默认上限§9。
- 风险：样例false但代码缺省true会在新环境意外开启能力；反代TLS终止后应用误判HTTP，引发重定向/认证失败或安全降级。
- 处理：核对强类型缺省、样例、环境覆盖和启动校验一致；只信任明确代理的转发头。分别检查scheme/host、Secure/SameSite、CORS与客户端凭据模式，不能信任任意X-Forwarded-Proto或关闭HTTPS要求。
- 限定：端口不同表示cross-origin，不必然cross-site；SameSite故障不能只凭端口变化判断。WES当前受权API也不能按LES2 Cookie登录方式假设。
- 验证：直连HTTPS、可信反代、伪造转发头、跨站/同站跨源、缺配置和非法配置；实际部署拓扑未验证，不宣称生产可用。
- 依据：[功能开关设计](../detailed-design/feature-switches.md)、[Host入口](../../src/Host/Program.cs)、[GEN-004](03-development-and-testing.md#gen-004-当前权限不等于创建时权限也不只看单据creator)。
