# T05 WMS 创建事件只读诊断

- 状态：已验收（仅本片owner/API只读诊断，非完整T05）。
- 范围：REQ-NTF-004..008、PAGE-NTF-003/FUNC-NTF-001第二owner读取基础；不替代跨owner页面/统计或真实Broker。
- 基线：[WMS原子创建Outbox](135-t05-t08-wms-creation-outbox.md)、[当前creator目录授权](123-t08-authorized-master-data-catalog.md)、[通知设计](../detailed-design/notifications-and-jobs.md)。
- 测试：[本片测试](142-t05-wms-creation-event-diagnostics-tests.md)。

## 契约与范围

在WMS公共主数据scope增加QueryCreationEventsAsync并接GET `/api/wms/master-data-events`，只在既有显式主数据读装配开启、已认证HTTPS时映射，不依赖仓库创建写开关。不访问Platform私表，不调用Platform Outbox，不汇总成“所有owner均已接入”。只读现有WMS创建事件，无payload/name/actor/tenant/consumer投影。

输入offset默认0、0..10000，limit默认25、只能25/50/100，非法400。成功固定`{rows,offset,limit}`，行固定EventId、Owner、ContractName、SchemaVersion、AggregateId、AggregateVersion、CorrelationId、OccurredAt、Status九个camelCase字段；Status只当前Pending=0，不构造不存在的attempt/published/inbox=0。排序OccurredAt/EventId，实时分页无总数/固定快照；同秒事件用稳定UUID次序，不承诺创建先后序。

权限独立`wms.messages.read`，九字段全部需要，不能用主数据read/write或Platform messages读权替代。WMS从自身永久创建事实得出creator，并通过既有公共IMasterDataAuthorization.ReadCreatorsAsync取得当前完整字段creator交集；可Own/Departments/All，不能由调用者提交creator。当前真实目录中不存在的creator即使All也不推定授权。过滤必须在WMS SQL tenant+creator join之后再分页，不能先分页再内存过滤。复用现有catalog的MySQL Guid集合显式表达式过滤，避免新增重复规则或跨owner查询。

当前授权未满足403；授权可见creator集合空为真实空列表，不冒充权限失败。错误/取消不转空。clean独立owner：任何tracked entity、当前事务或ambient事务拒绝；authority后及SQL物化完成后显式取消检查，迟到数据不交付。数据库错误沿WMS安全异常/日志与Host503/500，未识别状态/contract/version拒绝返回正常成功形数据。该入口不重放、不清理、无自动publisher或设备作用。

## 运维与验证

不新增依赖/表/迁移/权限种子；仅批准wes_test测试。生产开关/角色授权需管理员显式选择，此片不会自动启用写入口。回退可关闭既有WMS读装配或撤此独立权限，不能删除待发布事件。测试覆盖真实Own/部门/All交集、逐字段、当前撤权、跨tenant、SQL前分页、取消/故障和HTTP精确形状。客户端第二owner接线、WMS发布/消费、真实Broker窗口与完整DTC仍未关闭。

新增38真实owner和13真实HTTP，最终主数据/Outbox/诊断179、WMS领域/配置/HTTP/既有只读+创建真实HTTPS浏览器159、架构29共367通过。Release solution0警告/0错误，EF无模型漂移。新诊断API没有浏览器调用/客户端，不将既有浏览器回归冒称新API浏览器验收。
