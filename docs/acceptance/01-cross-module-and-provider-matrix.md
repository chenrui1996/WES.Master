# 跨模块与 Provider 验收矩阵

- 状态：验收规格；无测试已执行/通过声明
- 关联：[系统架构](../02-system-architecture.md)、[追踪矩阵](../04-requirement-design-test-traceability.md)、[Route 测试目录](../route/03-route-test-catalog.md)

## 1. 跨模块端到端场景

| ID | 流程 | 验收检查 |
|---|---|---|
| E2E-TC-001 | Inbound → WMS 收货/组盘 → Orchestration → Stacker → Device Adapter → Feedback → WMS | 单据、Leg、包任务、货位占用、唯一库存流水均可追踪；重复反馈不重复入账 |
| E2E-TC-002 | Inbound → FourWay 同层多段 → Traffic Reservation → feedback → WMS | 每段先获授权再下发；资源释放与库存到位一致 |
| E2E-TC-003 | FourWay 跨层 → Hoist AP/Lift/EP 三阶段 → WMS | 同提升口互斥、暂停/失败恢复有效、最终只有一个完成回调 |
| E2E-TC-004 | 出库审核 → 原子预留 → 拣选（Web/PDA）→ 直发或运输 → 账本完成 | 不足库存不能预留；预留/实物扣账时点正确，PDA 与 Web 权限一致 |
| E2E-TC-005 | 双 Pack 交接 → 首 Leg 完成 → 第二 Leg 激活 → WMS completion | HandoverLink/Pack 方向正确；失败后段不误执行 |
| E2E-TC-006 | 外部 WCS 调用 → 签名回调 → External Adapter → Bus | Codec/schema/身份/幂等验证有效；供应商 DTO 不进入 WMS |
| E2E-TC-007 | 设备断连 → DeviceComm 告警 → Orchestration/WCS 运维查看与处置 → 状态恢复 | 告警事实、设备状态、控制权限和恢复记录关联一致；包内命令由对应 WCS Pack 最终校验 |
| E2E-TC-008 | Simulator map validate → staged owner preparation → manifest activation → simulation fault injection → review → promote-preview | 无效图/owner preparation 不完整时不能激活；参与者版本/hash 与活动 manifest 匹配；活动任务固定 MapVersion；模拟与真实契约一致；localhost/不支持设备不能 Promote |
| E2E-TC-009 | SCADA stale state → Orchestration/WCS 运维诊断 → 安全暂停/恢复 | SCADA 只读；陈旧状态不诱发新派发；控制操作由 Orchestration/WCS 授权并审计 |
| E2E-TC-010 | Workflow 审批 + WMS 业务回调 + Outbox 通知 | 表单版本/审批状态和业务状态一致；重复消费无重复副作用 |
| E2E-TC-011 | 运维手工 Force-complete / Cancel | 权限、理由、设备物理事实、WMS 对账和审计缺一不可 |
| E2E-TC-012 | Host/DB/Broker/HotStore/Device restart | 耐久订单/账本/预约可恢复；物理状态未知时不重复发命令 |
| E2E-TC-013 | 2D 地图发现告警 → 幂等关联运维问题 → 指派/升级 → 就地处置 → 回读并关闭或复开 | 问题由 Orchestration 持久化、告警由 Notifications 独立管理；可定位与未定位资产均可见；命令由 owning module 校验；只有新鲜设备/WCS 回读及必要 WMS 对账成功后问题才解决，复发保留历史 |

WCS 运维专项用例 `SITE-TC-012..016` 对应 `REQ-SITE-012..016`，由 Orchestration 管理跨包协调流程，各 WCS Pack 对模式、联锁、任务状态与物理反馈做最终校验。`SITE-TC-017..018` 分别验收 `REQ-OPS-006` 隔离环境恢复与生产设备隔离、`REQ-OPS-007` 单写者/fencing 写入门禁；它们属于系统/WCS 验收，不属于 SCADA/Simulator 功能规格。要求和用例均为设计状态，尚无测试通过声明。

| ID | 验收范围 | 预期 |
|---|---|---|
| SITE-TC-017 | 备份恢复到隔离环境（REQ-OPS-006） | WMS 账本、WCS 活动任务和已部署地图版本可一致恢复；网络/设备身份配置阻止连接生产设备且无生产写命令 |
| SITE-TC-018 | 运行多副本但缺少有效 SingleWriter/lease/fencing 配置（REQ-OPS-007） | readiness/控制入口明确拒绝控制写入；只开放部署策略允许的只读能力；旧 writer epoch 不能继续授权 |
| SITE-TC-024 | 同一来源 occurrence 重复送达（REQ-OPS-008） | 只创建一条运维问题；最近观测和事件历史幂等更新 |
| SITE-TC-025 | 告警已确认/清除但问题尚未解决（REQ-OPS-010） | Notifications 告警状态独立变化；运维问题不自动解决 |
| SITE-TC-026 | 未定位/陈旧问题从地图工作台查看、派单和升级（REQ-OPS-009） | 问题队列可见；数据质量、授权、责任变更和升级历史完整 |
| SITE-TC-027 | 命令已接受但无有效新鲜回读（REQ-OPS-010） | 问题保持待验证/升级；不能以命令状态解决问题 |
| SITE-TC-028 | WCS/设备回读和必要 WMS 对账后复发（REQ-OPS-010） | 有证据才解决；复发按来源 occurrence 规则重开/新建且不丢历史 |
| SITE-TC-029 | 必需地图 owner 准备失败 | 不激活新 deployment；旧活动 deployment 继续有效，失败参与者/原因可查 |
| SITE-TC-030 | owner Prepared 确认缺失/过期或版本/hash 不匹配 | 激活拒绝；不接受陈旧确认或不匹配 artifact |
| SITE-TC-031 | 新 deployment 激活后需回滚 | 新建补偿 deployment 指向已验证不可变版本；历史不覆写，活动任务由 WCS 对账 |
| SITE-TC-032 | activation event 重复、丢失或参与者恢复 | 消费者幂等；可按 manifest 对账恢复，不发生重复激活副作用 |
| SITE-TC-033 | 活动任务跨地图部署版本切换 | 活动任务保持原版本或经 Pack 安全迁移；不得隐式重写执行路径 |

## 2. 数据库 Provider 矩阵

对每个模块至少运行 schema migration、核心事务、并发/唯一约束和查询排序/分页用例。每个版本/数据库驱动均记录部署所需版本。

| 验收项 | SQL Server | PostgreSQL | MySQL | 完成证据 |
|---|---|---|---|---|
| 新库安装/完整 migration/升级链 | 待执行 | 待执行 | 待执行 | 报告/数据库版本 |
| 回滚/前向恢复或声明不可逆 | 待执行 | 待执行 | 待执行 | migration runbook |
| 幂等键/业务唯一约束/租户复合键 | 待执行 | 待执行 | 待执行 | 并发报告 |
| WMS 预留/调账/完成事务 | 待执行 | 待执行 | 待执行 | 状态/ledger assertions |
| Outbox/Inbox 原子性和多 worker 竞争 | 待执行 | 待执行 | 待执行 | crash/retry report |
| Route 持久预约账本/epoch fencing/lease takeover/竞争 grant/崩溃恢复 | 待执行 | 待执行 | 待执行 | provider-specific concurrency/restore report |
| 字符串排序、精度、时间/时区、JSON 字段 | 待执行 | 待执行 | 待执行 | provider deviation log |
| paging/filter/group/aggregate 查询 | 待执行 | 待执行 | 待执行 | contract suite |
| 备份、还原、数据一致性校验 | 待执行 | 待执行 | 待执行 | restore drill |

只有某一 provider 的全部必需项通过，才可将其标为已支持生产部署；若特性确有 provider 限制，必须在配置/设计中 fail fast，不能运行时静默降级。

## 3. Adapter/集成兼容矩阵

| 边界 | contract case | 实现环境 | 状态 |
|---|---|---|---|
| Virtual `IEquipmentTriggerPort` | command/feedback ID、duplicate/late/error | 可控时钟/故障注入 | 待实现验收 |
| Step7 Driver | 地址、值类型、connect/read/write/reconnect | PLC Simulator/隔离测试台 | 基础解析测试存在；现场待验收 |
| Modbus TCP Driver | Area/address/endianness/range/timeout | Modbus Simulator/隔离测试台 | 基础地址测试存在；现场待验收 |
| External WCS HTTP | codec、auth/signature、idempotency、timeout | Mock + Vendor sandbox | Fake Codec 测试存在；真实供应商待验收 |
| External WCS MQ | schema、重复、dead-letter/replay | RabbitMQ Test Container/隔离 Broker | 待执行 |
| UniApp PDA API | auth、scan、online writes、read-only offline | H5 与目标终端 | 页面/API 存在；E2E 待执行 |
| Simulator/Promote | map version、deploy/reset/preview/rollback | isolated test tenant/environment | 单元/API 覆盖待完整复核 |

## 4. 非功能验收

| 领域 | 验收要求 | 阈值/责任 |
|---|---|---|
| Security | 认证/授权、租户隔离、secret redaction、callback signature、依赖漏洞、审计 | 安全负责人定规则/证据 |
| Reliability | Broker/DB/Redis/Device 故障、超时/重试、重启、备份恢复、RPO/RTO | 运维负责人现场输入 |
| Route Safety | stale state、对向/交叉、deadlock、取消/未知物理位置 | Route/设备/安全负责人联合签核 |
| Performance | API P95/P99、Outbox lag、Route planning/grant、吞吐、公平性 | 现场设备数/任务率/硬件明确后量测 |
| Accessibility/UX | Web/PDA 键盘/扫码、状态/错误、可读性、对比度、窄屏 | 前端负责人按目标终端验收 |
| Observability | correlated logs/traces、低基数 metrics、告警/Runbook | SRE/运维定义 dashboard/SLO |
| Platform/Deployment | 部署/回滚、schema upgrade、secret rotation、single-writer 与健康/指标基础设施 | 生产演练记录；现场 WCS 紧急覆盖由 Orchestration/WCS 负责并经对应 Pack 校验 |
| Documentation | 每个功能设计、测试、部署/恢复及未决项齐全 | PR checklist 自动/人工检查 |

不得在现场参数未定前填入猜测性能阈值。阈值须有来源、测试环境、采样时间、责任人和复审日期。

## 5. 验收签署记录模板

| ID/范围 | 环境/版本 | 证据链接 | 结果 | 未决风险 | Owner/签字人 | 日期 |
|---|---|---|---|---|---|---|
| E2E/REQ IDs | build/map/provider/adapter version | test run / report / log | pass/fail/blocked/waived | issue/mitigation/期限 | 名称/角色 | YYYY-MM-DD |
