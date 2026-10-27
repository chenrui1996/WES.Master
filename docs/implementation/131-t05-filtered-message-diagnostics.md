# T05 消息诊断状态与时间筛选

- 状态：已验收，仅本片范围。
- 范围：REQ-NTF-004..008、PAGE-NTF-003，Platform owner 既有查询的受控扩展。
- 基线：[消息设计](../detailed-design/notifications-and-jobs.md)、[客户端](127-t05-platform-message-diagnostics-client.md)、[重放](129-t05-authorized-message-replay-client.md)。
- 测试：[本片测试](132-t05-filtered-message-diagnostics-tests.md)。

增加可选 `status`（0 Pending/1 Leased/2 Published/3 Quarantined）、`createdFrom`（含）与 `createdBefore`（不含）查询参数。时间必须UTC，至少公元1000年以匹配MySQL DATETIME；可单边，不自动添加时间窗口，双边必须前<后。不带参数保持既有排序、响应与分页行为。非法值明确400，不忽略或默认成功。筛选由Platform owner在当前授权/tenant范围SQL中执行后，再按CreatedAt/EventId排序分页，不能先分页再内存过滤。状态/创建时间已是必要读取字段，不新增隐藏字段查询oracle。

公开应用边界增加可选filter参数，不引用其他owner私有数据。原签名的现有位置参数继续有效；无新依赖/迁移。查询后显式检查取消，避免SQL已物化才取消仍交付成功投影。

页面使用固定状态下拉和两个显式UTC时间输入，首次不查、修改任一筛选同步取消/清旧/回第一页。准备重放的预读必须使用当前同一筛选与分页；不把筛选前的旧行当候选。预览面板打开时禁改筛选，须显式关闭后再改，保留原键未知结果边界。客户端校验响应每行符合已请求的状态/时间范围，非法响应清旧。

客户端保留请求UTC文本精度，不经JavaScript Date重新序列化而截断亚毫秒；响应范围比较以已验证的UTC日期和补齐至7位的小数串比较。Date仅用于日历有效性验证，不用其毫秒精度代替精确边界。页面输入标注UTC，API仍独立验证；真实MySQL测试本片使用整秒边界，不冒充DATETIME亚微秒存储能力认证。

仍仅Platform，不新增跨owner/Inbox统计、total、快照或Broker健康。时间查询只表达创建时间，不是假装实时“当前消息年龄”；T05/完整DTC仍未关闭。
