# T08 禁用仓库创建客户端

- 状态：已验收（仅本片仓库创建UI，不是T08整包）。
- 范围：REQ-WMS-001..004、PAGE-WMS-001/FUNC-WMS-001仓库创建，UI-001..012适用子集；非完整DTC-WMS-001。
- 依据：[HTTP](137-t08-authorized-warehouse-creation-http.md)、[WMS设计](../detailed-design/wms-and-ledger.md)、[共用交互](../detailed-design/00-shared-page-and-test-standard.md)。
- 测试：[本片测试](140-t08-disabled-warehouse-creation-client-tests.md)。

## 交互

既有`/wms/locations`仓/位页面增加独立仓库创建面板，不新设业务路由；保留原目录与详情，不误开放库位或启停。当前subject有`wms.warehouses.write`的Code/Name字段并集才显示创建按钮，实际Own/Departments/All及可信actor由服务端复验，菜单/按钮不是授权。没有write权限保持原只读行为，无默认授予；后端写装配仍默认关闭，405/404明确说明部署入口未开放，不伪装成功。

代码1..64 ASCII字母数字/-_.、名称1..128且无边界空白/control，与既有严格读取解析共享校验。输入不自动trim名称，预览将代码规范大写，并冻结code/name/operation。预览展示“创建后禁用，不代表可分配或物理安全许可”和原操作UUID，第二次显式确认才POST精确body并显式Idempotency-Key。没有自动提交、自动重试、断网排队或本地业务缓存。

状态draft→preview→submitting→confirmed/rejected/unknown；网络/5xx/非法成功响应/提交中离线或10秒确认总deadline是unknown，原body/key冻结，仅手工同键重试。deadline仅取消等待，不推断服务端未提交；身份改变/卸载/响应完成清timer。400/401/403/404/405/409是本次拒绝，若此前unknown保留“先前仍需查证”。不允许面板内换键掩盖未知结果；关闭/刷新/导航前展示内存上下文会丢失、需记录key和原输入。当前subject/grants变化同步取消/清输入、请求、回执；迟到结果不交付。401沿全局退出，403保留当前会话但显示明确拒绝。

打开面板即取消清旧目录/详情并隐藏查询区，每次尝试再次失效，避免成功或未知变更之后继续显示新鲜旧列表或在途旧结果；不自动重读。确认回执只严格接受三字段decision=0/1、非空id/version UUID；显示禁用事实与原key，不当完整resource投影。用户另点“用回执 UUID 读取当前仓库”才关闭面板并通过原GET当前read授权读取（可403），不把write当read或自动切到可用。

语义form/label，Enter只预览，按钮二次确认，键盘焦点可见；小屏换行和长名称/UUID、375/768/1440及200%文字无整页溢出。无任意HTML/路径，固定同源API、字段白名单；不新增持久化状态或依赖。

## 边界

本片补仓库表单，不实现树/区层巷、库位创建/真实Pack schema、编辑启停、容器/占用/库存或完整父DTC。真实浏览器须用明确启用写入口的HTTPS/MySQL fixture，原只读fixture继续证明所有WMS请求为GET；不以fetch替身代替实际创建证据。

新增34组件/边界用例与1页面整合；前端全套462通过，vue-tsc/Vite构建通过。真实HTTPS/MySQL Chromium只读与写入两变体均通过，写入变体实际201/原key/body、手工GET及最终owner精确单资源/事实/事件验证；合并WMS领域/配置/HTTP共146及架构29通过，Release solution零警告错误。与前片重复覆盖不跨轮累加。
