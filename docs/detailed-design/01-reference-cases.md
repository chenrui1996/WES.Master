# 外部案例与采用边界

- 状态：评审中；读取日期2026-10-23
- 方法：仅使用公开官方文档/公开仓库；概括事实，不复制界面资产和大段源码。未上传本项目文档或代码。

| 来源/证据 | 已核验事实与借鉴点 | 应用设计包 | 不采用/限制 |
|---|---|---|---|
| [ERPNext Pick List](https://docs.frappe.io/erpnext/pick-list)，官方流程说明 | 从订单生成拣选清单、位置明细、扫码、完成后关联后续单据、库存变化后更新可用信息 | WMS/PDA：计划数/实拣数/后续发运分开，单据/位置可追溯 | 不自动采用其FIFO/临期策略，不新增制造/销售业务；WES库存与运输语义以原规格为准 |
| [ERPNext Stock Ledger](https://docs.frappe.io/erpnext/stock-ledger)，官方报表说明 | 出入/移库流水可按来源单据跳转；区分业务记录与余额 | WMS：流水到来源单据、变动后余额与异常核对 | 不引入成本/估值/会计台账，不允许编辑历史流水 |
| [OpenWMS root repository](https://github.com/openwms/org.openwms)，README，经GitHub API读取 | WMS/MFC、ERP交互、底层设备与适配分工；根仓库列举独立业务服务 | Orchestration/External/Device：清晰owner和上下游关联，不把供应商协议放进库存服务 | 不采用其Java、服务拆分或独立存储方案；README不是界面截图或现场性能证据 |
| [openTCS 官网](https://opentcs.org/en/index.html)，官方概述 | 提供协调车辆车队的开放控制系统基线 | Route/Pack：仅作车队控制案例背景 | 未读到可核验的Plant Overview详细界面/调度协议，因此不声称复用了其具体UI、算法或锁策略 |
| [Element Plus Table](https://element-plus.org/en-US/component/table.html)，组件官方文档 | height/max-height、固定列、选择和列分组 | Web/Simulator表格；原Seven已使用Element Plus | 不把hover tooltip作为完整信息唯一渠道；不臆造虚拟表组件属性 |
| [Element Plus Form](https://element-plus.org/en-US/component/form.html)，组件官方文档 | label-position、form-item、rules、原生Enter提交注意点 | Platform/WMS/Workflow/Simulator表单 | 前端校验不代替owner服务端校验；后续实现核对锁定版本API |
| [Keycloak 管理指南](https://www.keycloak.org/docs/latest/server_admin/index.html)，官方术语 | 用户、角色、组、realm/client、认证与授权分工 | Platform：角色/组织关联、会话与授权信息分层展示 | 不据此决定引入Keycloak，不把realm直接等同本项目Tenant，不改变JWT/权限基线 |
| [RabbitMQ Reliability](https://www.rabbitmq.com/docs/reliability)，官方可靠性指南 | 网络/进程/通道失败、恢复、数据安全由publisher/consumer共同承担 | Notifications/External：持久Outbox、投递与业务效果分开显示 | 自动重连不保证物理命令可安全重试，不宣称exactly-once |
| [Quartz.NET Jobs](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/more-about-jobs.html)，官方教程 | JobDetail与执行实例分开，每次执行新建实例；配置与执行状态不可随意存在实例字段 | Jobs/System：定义/触发/运行记录分开，显式租户和执行上下文 | 页面只是设计；不以4.x教程决定生产版本/持久化方案，仍须部署ADR和真实Provider验收 |

## 未作为证据的入口

- 通用Web搜索一次返回504；改为直接获取官方资料，不用搜索摘要代替证据。
- openTCS旧版用户指南与GitHub入口连接失败/文件未找到；只引用成功读取的官网概述。
- Flowable OSS入口404；bpmn-js、HslCommunication网页连接失败；不引用其具体实现结论。
- 未读取商业截图库；不将“像某产品”写成已验证页面依据。

## 各模块的参考责任

Platform使用身份/角色案例，Workflow使用现有Workflow规格与Element表单，不因未读取流程引擎文档宣称支持完整BPMN。Notifications使用RabbitMQ/Quartz事实，WMS/PDA使用ERPNext流程组织，设备/Pack/Route以WES现有设备规则与ADR为权威，OpenWMS/openTCS只作边界背景。Simulator/SCADA以现有部署协议和Element UI模式为依据。系统恢复手册来源是本项目系统验收，不声称从网页获得恢复正确性证明。

外部案例不覆盖现场定位误差、制动距离、设备联锁、供应商命令去重能力或目标吞吐门限；这些必须在模块的阻断清单与验收环境中单独确认。
