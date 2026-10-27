# T07 具名业务实体附件读取

- 状态：本读取续片已实现并验证；文件 owner Platform，业务实体 owner Wms，T07 整包未完成。
- 需求：REQ-PLAT-007..012 / FUNC-PLAT-005，DTC-PLAT-007/008/014、RULE-12 / PAR-04。
- 基线：[平台与安全](../detailed-design/platform-and-security.md)、[暂存访问](103-t07-authorized-staging-file-access.md)、[模块边界](../02-system-architecture.md)。

## 当前授权与范围

补充已绑定附件的单文件元数据与下载，不把上传者权限或调用方提供的 owner 字符串当作业务实体授权。Platform 的具名业务文件入口必须显式提供不可变 owner/entity 引用以及实体当前授权端口；缺失端口不能成功。Host 仅映射固定 `wms-warehouse` 与 `wms-location`，不接受请求 body/query 指定授权策略。

授权是三个条件的交集：可信 tenant 内实际 FileReference 精确匹配 owner/entity/file 且 ReferenceCount 大于零；业务 owner 通过公开应用入口重新读取该实体并通过其完整当前字段权限；Platform 当前文件 read 九字段及下载 Content 权限。文件范围仍使用持久 creator 与其当前部门，不自动扩大为实体范围。WMS 只返回授权决定，不向 Platform 暴露私有实体、creator、DbContext 或表。

不新增数据库表、迁移或默认授权，不改变已有审计动作数值。原暂存入口仍拒绝所有已绑定文件，新的具名入口不暴露扫描、孤儿删除或上传方法。

旧内部引用的 `Wms` 等泛 owner 值不自动解释为仓库/库位；只有固定具名类型和精确实体对应的引用能够通过新入口。已有 DTO 类型名 `StagingFileView` 为兼容保留，其九字段安全投影复用于业务读取，不返回引用目录。

## 下载、并发与错误

复用暂存访问已经验证的下载流程，不复制摘要校验、缓冲所有权或审计逻辑。读取存储前与 IO 后的 ReadCommitted 事务内都核验精确绑定、当前实体权限和当前文件权限；Available/Stored、版本、字节长度/SHA256 及下载审计提交仍是交付前提。撤权、解绑、版本变化、取消、审计/存储失败不返回字节，已读未交付缓冲清零。

这不是 WMS 与 Platform 的分布式事务：WMS 授权是两个明确时点的当前查询，不声称锁住外部 owner 或保证查询之后永不撤权。精确绑定和文件版本仍由 Platform 自己读取；后续绑定/解绑写入口须原子改变引用行、计数和版本 fence。

已存在文件但条件不满足返回 Forbidden；可信 tenant 内缺失文件返回 NotFound。数据库错误沿用安全分类，实体授权端口错误不得转为成功/默认允许。日志不输出文件名、路径、内容、摘要或业务引用。

## HTTP 与回滚

仅 `Wes:WmsMasterData:Enabled` 与既有 `Wes:Files:Enabled` 均开启时映射：

- `GET /api/wms/warehouses/{entityId}/files/{fileId}`
- `GET /api/wms/warehouses/{entityId}/files/{fileId}/content`
- 同结构的 `locations` 两路由。

沿用 HTTPS、已认证可信主体、no-store、attachment/nosniff、固定九字段元数据和受控二进制响应；不接受扫描结果、路径、任意 owner、分页目录或写命令。文件开关默认关闭；关闭任一部署入口撤去这些路由，无数据迁移回滚。

## 验证及未完成边界

见[测试证据](166-t07-entity-authorized-file-reads-tests.md)。真实 MySQL 覆盖授权交集、精确引用、IO 中撤权/解绑/重绑、内容清零/审计以及原暂存拒绝不回归；HTTP 覆盖两具名 owner、当前实体授权和默认关闭。

新增26真实 MySQL、21具名 HTTP；相关 Release 106 文件 owner + 86 HTTP/五套既有 HTTPS Chromium + 29 架构 = 221 项通过。独立架构编译的显式路由 using 缺失已修复，精确最终 HTTP 再跑21/21，不重复累加。Release 构建零警告、零错误。

此片不创建业务引用，不能由内部测试种子冒称真实绑定/解绑已交付。完整业务文件目录/客户端、绑定/解绑、真实 ClamAV/病毒库/ACL、T07 及 T04–T08 整包均保持未完成。
