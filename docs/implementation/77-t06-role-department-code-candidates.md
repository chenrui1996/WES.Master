# T06 角色规则部门候选代码查询

- 状态：DTC-UI-022 Platform Roles部门候选精确代码查询首片；仅覆盖角色规则新增时的部门选择器，不代表完整 DTC-UI-022 或 T06 完成。
- 依据：[密集页面交互合同](../detailed-design/03-dense-page-interaction-contract.md) §4.1、§8 DTC-UI-022；[Platform页面/功能设计](../detailed-design/platform-and-security.md) PAGE-PLAT-004、DTC-PLAT-014。
- 测试记录：[角色规则部门候选代码查询测试](78-t06-role-department-code-candidates-tests.md)。

## 实施范围

角色规则“指定部门”候选支持按授权可读的精确部门代码查找，保留25条默认分页和“加载更多”。查询条件变化立即清除当前结果并使旧响应失效；输入经250ms去抖后读取第一页，较新输入（例如A后AB）的响应只允许当前请求更新候选。已在草稿中选定的部门ID及其可见摘要在搜索结果变化时保留，加入规则时仍只接受此前从当前授权owner候选加载且启用/可委派的ID。

新查询参数沿用部门目录owner与当前受信租户；只有同时具备部门ID和Code字段授权的资源才可参与代码匹配，范围按两项授权的交集收窄后再过滤和分页。请求无Code字段权限时拒绝，不能通过结果有无探测隐藏部门。现有角色目录的 `code` 精确筛选语义不变。

## 不在范围内

本切片不增加模糊名称搜索，不扩展到Users部门/角色选择器或其他模块，不实现选中引用的独立owner回填，也不宣称其他PAGE/真实浏览器验收。完整 DTC-UI-022 与 T06 仍未完成。

## 实施结果

- `GET /api/platform/departments` 新增可选精确 `code` 参数；Owner先计算Id与Code字段授权范围的交集，再在SQL查询中过滤并分页。请求Code但没有Code字段授权时按未授权拒绝。
- 角色页仅在当前主体拥有部门Code字段授权时显示代码输入；输入立即重置候选并使旧请求失效，250ms后从offset 0、limit 25读取。候选读取器取消前请求并以generation阻止迟到回包覆盖，加载更多保留当前代码过滤和连续offset。
- 搜索结果变化时保留草稿已选ID及此前获授权的代码/名称摘要；加入规则只接受仍可委派、启用且有已授权摘要的显式选择。身份/授权拒绝清除未提交编辑状态；Owner保存时仍重新校验授权。
- 代码及测试记录见[测试记录](78-t06-role-department-code-candidates-tests.md)。没有隔离HTTPS Chromium证据；本切片不据此关闭DTC-UI-022或T06。
