# 外部 WCS 页面、功能与详细测试

- 状态：评审中；DTC均designed/未执行
- 权威：[外部WCS规格](../modules/external-wcs-and-device-adapters.md)；SCOPE-WCS-04；REQ-ADP-001..009
- 共用：[标准](00-shared-page-and-test-standard.md)、[OpenWMS/RabbitMQ案例](01-reference-cases.md)
- 产品阅读：集成维护员的连接/能力与命令诊断，DENSITY=3、DATA_COMPLEXITY=2、CONSEQUENCE=3。

## 1. 页面

| PAGE定义 | 入口/角色 | 线框和字段/验证 | 查询/动作 |
|---|---|---|---|
| PAGE-ADP-001 | `/integrations/wcs`；受权配置员 | L+F：供应商Pack → transport/Codec/能力 → 配置差异。PackId、HTTP/MQ、Codec version、endpoint引用、secret引用、timeout、能力submit/query/cancel、版本；配置owner External | Pack/供应商/状态过滤；保存/验证草稿、按生产切换流程启用；endpoint白名单/禁止任意跳转，秘密不回显 |
| PAGE-ADP-002 | 地图外部Pack任务或`/integrations/wcs/commands/:id`；诊断员 | O：Leg/内部command → 外部task/attempt → Accepted/Unknown/终态 → 回调来源。commandID、时间、错误分类、schema、query/cancel是否支持、correlation；供应商payload仅脱敏摘要 | 受权查询/取消申请经Orchestration；Unsupported明确显示；回地图/订单保持选择；不是通用HTTP发送工具 |
| PAGE-ADP-003 | `/integrations/wcs/callbacks`；安全/集成查看员 | O：时间/来源 → 合法/隔离回调 → 验证失败字段/安全摘要。eventID、Pack/Leg、externalTask、seq、nonce摘要、认证结果、处理结果 | 按错误/版本/任务过滤；不可编辑重签回调；受控重放沿消息/运维工作流，不开放绕过验签按钮 |

001适用UI-001..011；002/003适用UI-001..008/011/012，003无管理写因此UI-005/006仅查询状态不适用写断言；002动作适用UI-010。协议断线是Degraded/Disconnected，不显示空任务；无能力的操作隐藏或说明Unsupported，服务端也必须拒绝。

## 2. 功能

| FUNC定义 | PAGE/原TC | 处理与契约 |
|---|---|---|
| FUNC-ADP-001 | 001；ADP-TC-001..004/012 | 1校验Pack/Codec/schema/transport/endpoint安全；2版本化配置，credential仅引用；3连接与能力探测；4明确Configured/Ready/Degraded/Disabled。不能以网络可达等于可接受业务命令 |
| FUNC-ADP-002 | 002；ADP-TC-002..003/007..010/013 | 1Leg授权/能力/版本；2持久commandID/attempt/关联后发送；3保存外部taskID；4超时Unknown先query/去重；5不支持query/cancel转人工处理，禁止null/空成功。队列有界，过期物理命令不复发 |
| FUNC-ADP-003 | 003/002；ADP-TC-004..006/014 | 1验证来源认证/签名/时间/nonce；2核Pack/Leg/externalTask/seq/schema；3归一为owner事件；4Inbox去重与合法状态推进；5隔离错序/未知。Cancel与Completed竞态必须回查物理/任务事实，不覆盖终态 |
| FUNC-ADP-004 | 001/002；ADP-TC-008..012 | 1Adapter重启恢复耐久命令；2核对设备/供应商身份与在途task；3虚拟与真实逐能力contract suite；4恢复Ready或保持Unknown。fake Codec通过不能标某供应商已支持 |

出站/入站包含版本化schema、command/event/Leg/Pack、租户、occurred/received时间、correlation/causation、幂等身份；Codec只做映射，不能修改库存/决定业务终态。原始报文如需留存需独立审批加密/保留策略；错误不含secret。HTTP重定向、DNS/endpoint变更须验证目标，不允许凭据转发至不可信地址。

## 3. 详细测试

固定供应商沙箱VND1、PackEXT、LegL1、commandC1、externalTaskX1、TA；沙箱可记录收到请求次数与动作效果次数。

| DTC定义/功能/原TC | 前置与编号步骤 | 断言 | 级别/环境/清理 |
|---|---|---|---|
| DTC-ADP-001 / 001 / ADP-TC-001/004 | 正确CodecV1；变体Codec缺/transport错/Disabled/schema未知；1保存验证；2派Leg | 合法配置可Ready；错误不接任务、外部请求0；字段/版本错误可查，不默认FakeCodec | P0/B+C+E/C1 |
| DTC-ADP-002 / 002 / ADP-TC-002/010 | 沙箱接受C1但丢响应；1下发；2重启；3同command查询；4重试；另测4xx/5xx/限流/恶意redirect | Unknown不等于失败；query/幂等核验后效果最多1；分类/退避有界；不访问不可信目标/泄漏凭据 | P0/B+C+E/C1 |
| DTC-ADP-003 / 002 / ADP-TC-003/007/013 | MQ Disabled/断连、query=false/cancel=false、队列上限N；1请求；2积压N+1；3恢复并过deadline | 明确Unsupported/背压；拒绝无外部效果；恢复不发送过期物理命令；取消不支持时L1未误Cancelled | P0/B+C+E/C1 |
| DTC-ADP-004 / 003 / ADP-TC-005..006 | X1有效签名，T1完成/T2更新遥测/T3才到完成；另缺签/错签/过期nonce/错Pack/错Leg；1回调；2重复；3迟到Progress/过deadline控制 | 可信迟到完成持久核验后只推进一次、不覆盖T2位置、不回退终态；过期认证隔离并query可信owner，绝不绕验签；新控制过deadline物理写0，非法WMS0变化且脱敏审计。RVT-05必测 | P0/B+C+E/C1 |
| DTC-ADP-005 / 002/003 / ADP-TC-014 | L1 Running；1并发Cancel与Completed；2查询X1；3重复两事件 | 仅合法当前版本生效；未知保持待处置；最终与沙箱物理事实一致；WMS最多1次完成效果 | P0/B+C/C1 |
| DTC-ADP-006 / 004 / ADP-TC-008..009/011..012/ARCH-TC-006 | 虚拟与批准供应商Adapter同套命令/反馈数据；旧seq/旧身份/秘密payload变体；1断连重连；2发送旧反馈；3契约对比；4查UI/日志 | 错身份不发新命令、旧反馈不更新位置；支持能力语义一致，未支持明确；日志无测试secret；记录各版本证据不扩大供应商支持声明 | P0/C+E/C1 |

### 3.1 列配置与供应商关联

遵循[交互合同](03-dense-page-interaction-contract.md)，数字为PAGE-ADP；G/F/R为共用测试配置，`!`必显、`+`可选。供应商ID总与Pack/Codec上下文关联，不建立全局同名任务映射。

| 页面 | 配置 | 默认顺序/可选列 | 新增/编辑/关联筛选与密集操作 |
|---|---|---|---|
| 001 | G+F | Pack业务码!、供应商、传输方式、配置/运行状态!、Codec版本!、submit/query/cancel能力!；+超时/安全endpoint摘要 | External Pack→批准transport/Codec/schema→所需配置字段/secret引用；不兼容改选清旧字段并预览影响。候选只取注册白名单，endpoint/凭据不进视图；切配置不表示已切生产 |
| 002 | R；attempt/反馈表G | command业务号!、Leg、供应商task、状态/Unknown!、query/cancel支持!、最后回读!；+schema/attempt/correlation | 内部command→该Pack外部task→受权回调；多供应商同task码显示Pack，不能自由输入externalTask替换绑定；不支持query/cancel明确，不给通用裸发请求 |
| 003 | G+R | eventID!、来源Pack、externalTask/Leg、验签/隔离结果!、处理结果!、接收时间!；+schema/seq/安全原因 | Pack→Codec/schema→回调结果筛选；按处理/认证结果分组，nonce仅摘要。回调未知关联可诊断但不点“修复关联”绕过验签；原报文/secret不在列设置器 |

| DTC定义/功能/原TC | 固定前置与编号步骤 | UI/持久化/副作用断言 | 级别/环境/清理 |
|---|---|---|---|
| DTC-ADP-007 / 001..004 / ADP-TC-001/003..005/007/012 | PackA/B外部task均X1，CodecC1不支持cancel、C2支持；1换Codec/transport并旧候选迟到；2编辑历史command；3回调分组与列设置；4伪造跨Pack关联提交；5保存视图 | 不兼容字段/旧能力不沿用，不凭换列显示Cancel可用；历史X1只链本Pack；验签/Unknown必显；非法提交无物理命令/归一事件/库存写；secret/payload不进本机偏好 | P0/E+B+C/C1 |

## 4. 阻断

按[已签核RULE-17/23/PAR-06/11](05-approved-rules-and-defaults.md)，物理Unknown默认不自动重发；submit/query/cancel/去重/安全停止默认Unverified，未配置认证/nonce/容量/超时显式阻断，不将消息重试5次或Job600秒套设备。DTC-ADP-001..003/006补未证能力/缺必需值外部新请求0，实际协议/Codec/固件/限流/试运行回滚仍逐供应商H。过期控制与迟到可信事实分开；物理协议另归DeviceIntegration。
