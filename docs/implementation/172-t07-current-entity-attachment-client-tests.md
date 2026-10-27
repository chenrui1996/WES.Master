# T07 当前实体附件客户端测试

- 状态：已验收（基本软件切片）；完整浏览器组合矩阵后置，不代签整包/外部验收。
- 设计：[附件工作区](171-t07-current-entity-attachment-client.md)。

## 已执行证据

| 范围 | 结果 |
|---|---|
| 基线前端 | 原 34 文件 / 730 项通过 |
| 本片最终前端 | `npm test`：36 文件 / 770 项通过 |
| 类型与生产构建 | `npm run build`：vue-tsc 与 Vite 通过 |
| 真实 MySQL 基本 owner 回归 | WmsLedgerTests、WmsMasterDataTests、AuthorizedFileAccessTests：482 通过，0 失败/跳过 |
| HTTP / 既有 HTTPS Chromium / WMS 领域回归 | `FullyQualifiedName~RealBrowser\|FullyQualifiedName~WmsFile\|FullyQualifiedName~Wes.Modules.Tests.Wms`：178 通过，0 失败/跳过；含六个既有浏览器变体，不含新附件组合专用浏览器 |
| 架构 | 29 项通过 |

RED/GREEN：缺组件首次 RED；未知结果 fresh=false 导致不能原键恢复的 RED 修复；在线但父详情陈旧的恢复 RED 修复。新增真实父子组合测试再次发现 preview→locked→父 active=false 令确认按钮失效；修复为锁父操作但不反向关闭附件。组件移到父 QueryState 外，恢复无实体不挂载的 v-if，不使用空 UUID/默认类型或组件实例深比较掩盖问题。以实际元素身份、冻结请求 body/key 及离线/在线后原操作恢复验证生命周期。

前端新增共 40 项：合同 21、附件组件 16、父页 3（相对原基线）；共有下载函数的原 FilesPage 26 项保持通过。覆盖 PUT/DELETE 二次确认、候选重读/隔离拒绝、严格响应、非法参数不发请求、丢响应/超时/迟到、人工恢复不换键、未知后拒绝仍未知及父页锁定。

证据 TRX 位于当前会话 `files\basic-delivery-validation\basic-owner.trx` 和 `basic-http-browser.trx`，不提交凭据、日志或生产连接。各轮包含重叠测试，不累加为独立覆盖数。

## 基本交付与后置范围

按 2026-10-27 [TodoList](TodoList.md)先基本交付；新附件组合的专用真实浏览器全矩阵后置，既有 HTTPS Chromium 回归不冒称该新组合已做浏览器端到端验收。真实 PUT/DELETE、永久单效果和当前引用/计数/审计仍由对应 HTTP/MySQL 测试验证；mock 或 clean seed 不证明 ClamAV。真实病毒库/ACL 为外部验证，关闭本轮阻断而非记录通过。
