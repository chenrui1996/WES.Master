# T06 审计实时分页测试记录

- 状态：本片测试通过；T06 整体及固定快照未验收。
- 关联：[实施记录](45-t06-audit-realtime-paging.md)。

## 自动化证据

| 范围 | 命令 | 实测结果 |
|---|---|---|
| Ops Web 全套，含审计 13 项：60 条分页、25/50/100、实时新增 offset 位移、跨页失败/重试、重查第一页、撤权、筛选/身份竞态、超量响应、既有只读投影 | `npm test -- --run`（`clients\ops-web`） | 17 个文件、131 项通过，0 失败 |
| Vue/TypeScript 与生产包 | `npm run build`（`clients\ops-web`） | `vue-tsc --noEmit` 与 Vite 构建成功 |
| 真实 MySQL 审计查询：60 条分页/新增位移/当前撤权，既有 Own/字段投影、tenant/时间范围/稳定排序及非法请求拒绝 | `dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~PlatformAccessPersistenceTests.AuditQuery --verbosity minimal` | 5 项通过，0 失败，0 跳过 |
| 隔离 HTTPS Host + Chromium：真实身份/owner API、60 条 correlation 查询、25+25+10 不重复、50/100 回首屏、375/768/1024/1440 无根级横向溢出，以及既有具名运维页面验收 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages --verbosity minimal` | 1 项通过，0 失败，0 跳过 |

## 红绿与修正

- 新增前端失败测试最初重现 8 项缺口：缺少可选择的 25 条默认分页、撤权仍保留旧证据、筛选变化未结束 busy 等。修复后全套通过。
- 浏览器初轮在每页条数定位处超时：显式 label 包含 option 文本，测试改用真实可访问 `combobox` role/name，而非放宽断言或加等待。
- 第二轮发现同一浏览器后续登录事件查询沿用了审计专用 correlation，正确返回空集合；测试在切换证据类型后显式清除此条件。最终浏览器全流程通过。

## 未覆盖

审计不可改，不用它证明可修改数据的增删改固定快照。600 秒 TTL、599/600 边界、owner 快照权限指纹、其他目录分页基准、所有 64 页、三 Provider 与真实终端仍不在本片通过范围。
