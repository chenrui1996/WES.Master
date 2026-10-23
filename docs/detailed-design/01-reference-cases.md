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

## 本轮密集页面补充证据（2026-10-23）

| 来源/实际读取位置 | 已核验事实 | WES采用与不采用 |
|---|---|---|
| [Legrand列设置组件](../../../Legrand.Master/Legrand.Vue3/src/components/basic/ViewGrid/ViewGridCustomColumn.vue)、[配置逻辑](../../../Legrand.Master/Legrand.Vue3/src/components/basic/ViewGrid/ViewGridCustomColumn.js) | 拖动列顺序、勾选显隐、至少一列、应用/取消、localStorage、恢复；缓存key按table.name | 保留便利性；补主体/租户/环境隔离、必显、schema迁移与保存失败提示；不照搬表名唯一key |
| [Legrand表格](../../../Legrand.Master/Legrand.Vue3/src/components/basic/VolTable.vue)的formatter与表头、[用户页面](../../../Legrand.Master/Legrand.Vue3/src/views/system/Sys_User.vue) | 字典key映射名称、多选、固定列/排序、用户角色/组织树与隐藏列；读取formatter约1429行及用户页字段 | 设计稳定ID关联/名称回填；不继承Token搜索、原始HTML formatter、展示时变更row或浮点库存求和 |
| [Legrand表单](../../../Legrand.Master/Legrand.Vue3/src/components/basic/VolForm.vue)约95–180、1015行，[部门扩展](../../../Legrand.Master/Legrand.Vue3/src/extension/system/system/Sys_Department.js) | 远程候选、自定义url、onChange、树；从当前部门新增时设父级默认值 | 采用受控远程选择/上下文继承；补请求指纹、父子校验、旧响应隔离。不是已证明所有业务页均有完整联动 |
| [GitHub Projects表格布局](https://docs.github.com/en/issues/planning-and-tracking-with-projects/customizing-views-in-your-project/customizing-the-table-layout) | 列显隐/顺序、筛选分组/排序；跨组拖动会改变分组字段值 | 采用列设置/受控分组；不采用拖动改业务状态/物理优先级、不引Primer或GitHub查询DSL |
| [GitHub Projects视图管理](https://docs.github.com/en/issues/planning-and-tracking-with-projects/customizing-views-in-your-project/managing-your-views) | 新建/复制视图、未保存标记、显式保存与视图管理 | 采用明确个人视图工作流；本轮只做本机用户隔离偏好，不据此新增团队共享/跨端同步 |

Legrand为本地只读源码证据，未启动其页面；GitHub为成功读取的官方说明，非本项目浏览器验证。TanStack raw/官网尝试连接失败或404，GitHub文件入口未找到，未用其具体API支撑设计结论。详细缺口与取舍见[密集交互补充](03-dense-page-interaction-contract.md)。

## 原案例的模块责任

后续[行业复审与未决规则建议](04-industry-review-and-open-rule-recommendations.md)补充本轮直接核验的10项一手资料（Dynamics 365、Odoo、Open-RMF、VDA5050、RabbitMQ、Quartz.NET、Alertmanager、PostgreSQL、Keycloak），逐项记录核验事实、采用边界与失败入口；不把产品可配置行为当成WES已批准策略，不重复扩大本文件原证据结论。

Platform使用身份/角色案例，Workflow使用现有Workflow规格与Element表单，不因未读取流程引擎文档宣称支持完整BPMN。Notifications使用RabbitMQ/Quartz事实，WMS/PDA使用ERPNext流程组织，设备/Pack/Route以WES现有设备规则与ADR为权威，OpenWMS/openTCS只作边界背景。Simulator/SCADA以现有部署协议和Element UI模式为依据。系统恢复手册来源是本项目系统验收，不声称从网页获得恢复正确性证明。

外部案例不覆盖现场定位误差、制动距离、设备联锁、供应商命令去重能力或目标吞吐门限；这些必须在模块的阻断清单与验收环境中单独确认。
