# 跨模块详细验收场景

- 状态：评审中；DTC均designed/未执行
- 权威：[总验收矩阵](../acceptance/01-cross-module-and-provider-matrix.md)
- 前提/清理：[共用基线](00-shared-page-and-test-standard.md)；业务数量/位置按[WMS](wms-and-ledger.md)，地图/manifest按[环境](environment-simulation-and-scada.md)，设备为隔离Adapter。
- 优先级均P0；E+B+C，B逐SQL Server/PostgreSQL/MySQL；真实设备H须另外批准。模块B断言不能被截图或Fake返回代替。

## 1. 证据采集与隔离

每条采用唯一run/业务键，固定T0/租户TA/仓WA，保存owner初始及最终查询、ledger/reservation/Outbox、transport/Leg/task/command/feedback、manifest与审计证据。C1清理先确认无在途物理动作，不能删除真实占用保护。每个故障变体重新建立隔离基线。

## 2. 详细用例

| DTC定义/原用例 | PAGE/FUNC入口 | 固定初始数据与编号步骤 | 端到端断言与禁止副作用 | 清理 |
|---|---|---|---|---|
| DTC-XMOD-001 / E2E-TC-001 | PAGE-WMS-003；FUNC-WMS-002、FUNC-BUS-002、FUNC-STK-003 | ST可用，IN1计划5，R1/D1库存0；1收货5；2申请上架；3ST逐段反馈；4重复最终反馈；5从单据跳地图/流水 | 先R1+5，完成后R1=0/D1=5；唯一运输/任务链；各段先grant后dispatch；重复反馈不再入账；单据/地图回读一致 | C1 |
| DTC-XMOD-002 / E2E-TC-002 | PAGE-FW-001；FUNC-FW-002、FUNC-WMS-002 | FW同层A→B→D，各资源cap1，IN1已收5；1发上架；2下一边被占；3合法释放后继续；4查WMS | 冲突等待不先发命令；每段资源授权可查；未到D前库存不提前到目标；最终一次移库且无资源泄漏 | C1 |
| DTC-XMOD-003 / E2E-TC-003 | PAGE-FW-003；FUNC-FW-004 | 两跨层任务共AP1/H1，C1/C2；1同时请求；2仅T1逐阶段正确反馈；3T2发错容器/旧阶段反馈；4故障恢复 | 同口互斥；T1完整三阶段才一次Leg完成；T2错误不跳阶段；Unknown口不被新任务抢占；库存随正确终态一次闭环 | C1 |
| DTC-XMOD-004 / E2E-TC-004 | PAGE-PDA-004、PAGE-WMS-004；FUNC-WMS-003、FUNC-PDA-003 | Qty10，两单各预留7；1并发审核；2成功单拣5短缺2；3直发/运输独立分支；4重复提交 | 仅一单预留成功；Qty/Available非负；拣选≠发运；直发合法扣5一次，运输分支不手扣；Web/PDA同样授权/错误语义 | C1 |
| DTC-XMOD-005 / E2E-TC-005 | PAGE-BUS-002；FUNC-BUS-001/002 | ST→H1→FW两Leg、5件；1首段执行；2提前L2完成；3目的ReadyToReceive、ST卸货但FW接受回执丢；4第二单申请同容器；5query双方确认再完成L2；6重投WMS通知 | 提前事件隔离，ReadyToReceive不是L2激活；未知仅一个handover token/保管权Unknown，量不可二次分派；地图账面/在途/观测分开，中间流水0；双方同版本证据才激活一次；最终WMS移动一次。规则基线RVT-03必测 | C1 |
| DTC-XMOD-006 / E2E-TC-006 | PAGE-ADP-002/003；FUNC-ADP-002/003 | EXT沙箱X1接受C1；1HTTP/MQ分别下发；2错签/错Pack回调；3正确完成两次；4迟到Progress | 非法无业务推进；合法归一事件一次逻辑效果；供应商DTO不进入WMS；原任务不回退，secret不在诊断页 | C1 |
| DTC-XMOD-007 / E2E-TC-007 | PAGE-BUS-001、PAGE-DEV-004；FUNC-DEV-004、FUNC-BUS-005 | 设备D1 Running，通信断；1断连生告警/issue；2地图查看；3受权查询/恢复；4新鲜回读 | 连接/告警/问题分别有事实；无回读不显示恢复成功；控制由Pack校验，Device重连不自动重复写；issue保留证据 | C1 |
| DTC-XMOD-008 / E2E-TC-008 | PAGE-SITE-001/002/003/005；FUNC-SITE-001..004 | MV1 active、MV2草稿；1非法图Validate/Deploy；2修正后prepare有失败owner；3全准备激活；4仿真注入；5Promote preview为localhost | 非法/部分prepare激活0；合法manifest版本hash一致；活动旧任务版本不改；仿真可重现；回环Promote拒绝无生产切换 | C1 |
| DTC-XMOD-009 / E2E-TC-009 | PAGE-SITE-006、PAGE-BUS-001；FUNC-SITE-005、FUNC-BUS-004 | 设备状态过期且SCADA有缓存；1看只读投影；2转运维尝试恢复/dispatch；3最新owner查询失败 | SCADA Stale明确且无写接口成功；不以缓存授权；操作受阻/保持Unknown；不能因地图仍显示车而派发 | C1 |
| DTC-XMOD-010 / E2E-TC-010 | PAGE-WF-003、PAGE-WMS-003；FUNC-WF-002、FUNC-WMS-002、FUNC-NTF-001 | 业务单与流程D1v1/schemaS1；1提交；2发v2；3完成旧实例审批；4hook故障/重复Outbox消费 | 旧实例版本固定；审批结果与业务同步状态分开；恢复后业务一次批准；待办/通知逻辑不重复、无任意WorkTable写表 | C0 |
| DTC-XMOD-011 / E2E-TC-011 | PAGE-BUS-004；FUNC-BUS-004、FUNC-STK-004/或FUNC-FW-005 | 活动任务T1，U2无权限，U1有权但无理由/设备Unknown；1各不合规强完/取消；2补齐审批和权威证据；3WMS对账 | 缺一前提均拒绝；合法过程审计、Pack最终门禁；接受不是完成；WMS不由运维UI伪造完成/扣账 | C1 |
| DTC-XMOD-012 / E2E-TC-012 | PAGE-SYS-001/003；FUNC-SYS-003、FUNC-ROUTE-004 | Qty10、G1/C1已发Unknown、OutboxM1；1逐组件重启/接管；2独立恢复到C1发送前备份但外部已执行C1，保持网络隔离/禁派发；3对账水位差 | 重启耐久不丢/旧epoch不新授权；恢复不自动重发旧Outbox/C1；外部事实未查明保持Unknown与保护，WMS核验后只补一次；4小时未解禁判RTO未满足而不强制放行。规则基线RVT-08必测 | C1 |
| DTC-XMOD-013 / E2E-TC-013 | PAGE-BUS-001/003；FUNC-BUS-005、FUNC-NTF-003 | occ1重复3次、未定位I1、alarmA1；1关联/指派升级；2A1确认/清除；3处置Accepted但无回读；4新鲜WCS+WMS对账；5权威复发 | occ1一个问题、队列可见；告警清除/Accepted均不解决；证据齐全才Resolved；复开留历史，新occurrence独立；责任/审计完整 | C1 |

## 3. 通过门禁

若任一owner查询、账本重算、command副作用次数或物理回读不能取得，记录blocked与缺失证据，不能以UI显示成功、HTTP200、Broker ACK或仿真动画替代。数值吞吐/延迟另按Route和非功能签核门限，不与本表逻辑断言混为一项。
