# T04 新建用户当前目录门禁测试

- 状态：实施中（局部修复已验证）；设计：[155](155-t04-user-create-loading-gate.md)。

| 子用例 | 步骤 | 预期 |
|---|---|---|
| PLAT-USER-CREATE-GATE-01 | 控制真实组件初始用户查询延迟，候选完成但目录未回，再完成目录 | 新建明确disabled，不出现无效可点动作；目录成功后enabled，点击显示表单 |
| PLAT-USER-CREATE-GATE-02 | 初始完成，显式刷新503，再显式刷新成功 | 当前目录失败时disabled；恢复成功后enabled，无自动命令/重放 |

真实HTTPS Platform全具名页面浏览器重进用户页、点击创建/凭据/关联角色，最终第二owner核对原有单效果断言，验证不以延时sleep回避竞态。

实际RED：UsersPage原25项通过、新增2项均因disabled未定义失败；只补既有guard对应disabled，不改open/命令/HTTP校验。最终全前端33文件691通过、严格vue-tsc/Vite通过；真实HTTPS Platform及WMS三变体4通过、架构29通过、Release0/0。详见[同批证据](154-t08-authorized-stock-ledger-query-tests.md)。没有重新运行全部Platform持久化或全部.NET测试，不把本片当父DTC/整包验收。
