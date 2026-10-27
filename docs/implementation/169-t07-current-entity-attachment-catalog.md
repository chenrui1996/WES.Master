# T07 当前实体附件目录

- 状态：本目录owner/API续片已实现并验证，不作为T07整包关闭依据。
- 需求：PAGE-PLAT-007 / FUNC-PLAT-005，REQ-PLAT-007..012，DTC-PLAT-005/007/014及RULE-12 / PAR-04目录子集。
- 基线：[具名读取](165-t07-entity-authorized-file-reads.md)、[永久引用写](167-t07-durable-business-file-bindings.md)、[暂存目录](113-t07-authorized-staging-catalog.md)；先读就绪台账，未取得真实扫描/ACL许可。

## 目录合同

在固定仓库/库位附件集合增加GET `/api/wms/warehouses/{entityId}/files` 与对应 `locations`。仍由两个既有部署开关AND控制，默认关闭；只接受offset/limit/state，不接受任意owner、对象key或客户端“已授权”标记。可信HTTPS/主体、当前完整WMS实体read以及Platform文件九个metadata字段均适用，不借实体权限扩大文件scope。read不隐式grant下载或引用写。

复用现有StagingFileAccess的字段scope SQL交集和九字段投影，仅在内部显式提供实体/authority的实例改为精确持久引用且计数大于零的分支；公开StagingFiles仍只查询无引用暂存。业务入口由BusinessFileAccess显式暴露QueryAsync，不增加泛型CRUD/动态owner注册。共享页DTO保留StagingFilePage名字以兼容既有合同，XML说明其可复用于明确授权的业务实例。

先核当前实体权限，在SQL中按可信tenant、精确owner/entity、真实引用/计数、creator存在及每字段当前Own/Departments/All交集过滤，再稳定CreatedAt倒序/Id排序、Skip/Take。offset为0..10000，limit仅25/50/100，state可选有效生命周期数值；不返回总量、引用目录、路径或摘要。查询后再核当前实体authority，撤权时不交付旧页；仍是明确时点而非跨owner ACID/固定快照。

成功精确 `{decision,rows,offset,limit}`，rows仅九字段，200/empty不代表全系统无文件。缺当前实体/字段权限403，未认证或已撤销会话的HTTP身份门禁401，非法分页/状态/空UUID/不受支持query字段400；依赖错误沿用503/500。不读取存储、不扫描、不追加下载审计或消息。页中可用状态是持久事实，不是当前病毒库/物理存储健康认证；实际下载仍复验字节/权限/审计。

## 验证和回滚

先建立缺失Query合同的编译RED以及集合路由404的HTTP行为RED，再核精确绑定/类型/实体/tenant、逐字段scope先于分页、真实creator部门变动、分页稳定/大小/上限、状态、当前撤权、取消/端口错误不变成功。保留暂存目录/具名读取/引用写回归，检查模型无漂移和模块架构。

不新增表/迁移/权限字符串/feature switch或客户端。本片后继续实体附件工作区；真实ClamAV/病毒库/ACL、全父DTC和T04–T08整包仍缺。回滚撤去集合GET或关闭既有开关，保留文件、引用、审计和永久操作记录。见[测试记录](170-t07-current-entity-attachment-catalog-tests.md)。
