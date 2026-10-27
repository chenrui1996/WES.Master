# T08 禁用库位创建客户端

- 状态：实施中；PAGE-WMS-001/FUNC-WMS-001、REQ-WMS-001..004创建子集，UI-001..012适用子集，非完整DTC-WMS-001。
- 依据：[库位HTTP](161-t08-authorized-location-creation-http.md)、[仓库客户端先例](139-t08-disabled-warehouse-creation-client.md)、[共用交互](../detailed-design/00-shared-page-and-test-standard.md)；[测试](164-t08-disabled-location-creation-client-tests.md)。

## 复用与交互

沿既有`/wms/locations`与仓库创建面板，提取为同一个具名MasterDataCreation组件的仓/位两个确定分支，不复制确认/取消/未知恢复状态机，不构造动态CRUD或生成器。原仓库标签/请求/测试选择器保持兼容。两种创建互斥，打开/每次尝试失效并隐藏旧目录/详情；关面板不自动重读。

库位按钮需当前`wms.locations.write`全部五字段及`wms.warehouses.read`完整父仓字段；服务端仍按真实creator范围/父仓当前read/schema复验，不信客户端字段并集。已知父仓UUID、Pack UUID及schema是显式输入，不伪造可用Pack候选或schema目录；没有真实Pack候选adapter，不把该子集当完整层级选择链。UUID不trim、非空规范格式，code/name/schema边界复用既有严格校验；code规范大写，schema保留版本大小写。

Enter只生成冻结预览，展示完整code/name/父仓/Pack/schema、默认禁用及operation；第二次确认才POST精确五字段body和永久key。默认后端不开写，404/405明确提示具名写开关/schema装配，不当成功。SchemaRejected422是本次明确拒绝；503/5xx、网络错误、非法成功形状、离线中断或10秒未确认保持unknown，冻结原tuple/key，仅手工同键重试。后续拒绝不清除先前unknown警告；不换键掩盖未知，无离线排队/自动重试。

成功仅三字段回执，不等于启用/可分配或物理完成；用户显式点“用回执UUID读取当前库位”才通过原GET当前read授权（可403），不把write当read。主体/grants/组件kind改变同步取消清空所有输入/冻结tuple/回执，迟到结果不交付；关闭/导航/刷新前提示内存上下文会丢失，保留原key与完整输入。

语义form/label、可见键盘焦点，长UUID/名称换行，375/768/1440叠加200%文字无整页溢出。测试adapter只在真实HTTPS/MySQL fixture内，生产不增加默认schema或权限；不新增依赖/业务缓存、启停、库存、容器/运输控制。

本片已验证：新增34组件/边界及1页面整合测试，全前端730/严格构建通过；真实HTTPS/MySQL新增库位写入变体连同原三WMS/Platform浏览器及相关HTTP/配置129+架构29=158通过，Release0/0。真实201/完整body/原key、手工GET和最终owner单效果已核验；真实Pack、完整层级选择链/启停/容器或父DTC仍未交付。
