# T08 禁用库位创建客户端测试

- 状态：实施中；[设计](163-t08-disabled-location-creation-client.md)，预期不计passed。

| 子用例 | 步骤 | 预期 |
|---|---|---|
| LOC-CLIENT-01 | 五字段/父仓read不足，合法完整输入预览/确认 | 不足不可写；完整tuple/code规范化和二次确认，精确POST五字段/key |
| LOC-CLIENT-02 | UUID/code/name/schema边界、未知成功字段/非法JSON | 无效输入不外发；非法成功为unknown，不假回执 |
| LOC-CLIENT-03 | 422/404/405/403/409、503/网络/10秒、同键恢复后拒绝 | 明确拒绝或unknown；原tuple/key不变，无自动重发，不抹除先前未知 |
| LOC-CLIENT-04 | 身份/字段/kind改变，提交时离线/卸载/迟到 | 取消清旧，不交付迟到响应/自动请求，timer清理 |
| LOC-CLIENT-05 | 原页面读→开库位创建→尝试→显式读回执 | 创建互斥，旧目录/详情清除，不把write当read |
| LOC-CLIENT-06 | 真实HTTPS Chromium完整tuple键盘确认/手工GET/375..1440与200% | 实际201/原key/body、禁用单资源/事实/事件，无新库存；不是真实Pack验收 |

## RED与执行证据

先写LocationCreation.spec.ts，导入缺失MasterDataCreation.vue建立RED；随后将已有WarehouseCreation组件改名并扩展具名仓/位两个分支，不复制确认/未知恢复逻辑。最初三文件100项通过，但严格build指出测试overrides的Record<string,string>不兼容测试表自动推导的可选undefined字段；改为真实五字段Partial类型，不加cast或放宽TS。

新增34组件/边界和1页面整合测试：

- 全五字段/完整父仓read、code/name/schema边界、UUID不trim/非空，code大写、schema保留版本大小写、精确五字段body（无operation/主体字段）与永久key。
- 二次确认、422明确拒绝、503保守unknown、同tuple/key手工恢复、未知后再422仍显示先前不明；恶意多字段/未知decision/无效id/version成功形状不确认。
- 精确9999ms仍提交/10000ms进入unknown且取消等待、timer清理；字段/主体或kind改变清全tuple、取消并丢弃迟到成功；离线不排队/恢复不自动发。
- 页面创建互斥、旧详情/目录失效、写回执不自动read；显式GET403仍拒绝且不泄漏位置。

最终验证：

| 命令/范围 | 结果 |
|---|---|
| `npm test` | 全前端730/730，34文件；原仓库35项与目录/详情/其他页面回归通过 |
| `npm run build` | vue-tsc严格检查与Vite构建通过 |
| Release Modules选择器：`RealBrowserReadsWarehouseAndLocationAndClearsDetailsAfterCurrentRevocation|RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages|WmsLocationCreation|WmsWarehouseCreation|WmsMasterDataSettingsTests` | 129/129，包括四套WMS（新增库位写1）+Platform全页面真实HTTPS Chromium共5套、原65库位HTTP/启动+38仓库HTTP+21配置 |
| Release Architecture全套 | 29/29 |
| `dotnet build WES.slnx --configuration Release --no-restore --verbosity quiet` | 0警告/0错误 |

本片.NET合计158，无跳过；本机证据位于会话files的wms-location-client-validation，location-client-http-browser-final.trx与architecture-final.trx。不和前片395重复累加。WMS owner生产逻辑/模型未变，本片不重复运行238 owner或新建迁移；无新增依赖。

最后保留仓库原404/405错误文案后，重跑全前端730/严格build，并对精确最终dist再次执行全部5套真实HTTPS浏览器5/5（location-client-browser-exact-final.trx），不重复累加。修改C#的format verify、diff check及16条新增Markdown链接检查通过。

真实库位写浏览器使用显式启用的HTTPS Kestrel/wes_test和仅测试项目内schema，键盘Tab/Enter二次确认，实际201/no-store/原key/精确五字段，手工GET禁用详情；375/768/1440每个宽度叠加200%文字无整页溢出。最终数据库只新增BROWSER-LOC禁用位置、永久创建事实、原EventId Pending事件各一，固定父仓/Pack/schema与可信actor匹配，schema只验证一次，无库存写入。原只读变体继续严格限定WMS只有GET，仓库写仍单独隔离。

未运行全量.NET、真实Pack、其他Provider、PDA终端及生产/现场验收；没有schema候选目录/完整层级链、启停/容器/库存或完整父DTC。T04–T08整包仍未完成。
