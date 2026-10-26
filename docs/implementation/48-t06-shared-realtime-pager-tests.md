# T06 共享实时分页测试记录

- 状态：现有分页调用点的本片验证通过；不表示 T06 整包完成。
- 关联：[实施记录](47-t06-shared-realtime-pager.md)。

| 范围 | 命令 | 实测结果 |
|---|---|---|
| Ops Web 全套；共享 pager 3 项、非审计调用点 18 项及既有审计/权限/会话/业务编辑/列偏好回归 | `npm test -- --run`（`clients\ops-web`） | 19 个文件、152 项通过，0 失败 |
| Vue/TypeScript 与生产包 | `npm run build`（`clients\ops-web`） | `vue-tsc --noEmit` 与 Vite 构建成功 |
| 新生产包的隔离 HTTPS Host + Chromium 具名运维页面、审计 60 条分页、25/50/100、375/768/1024/1440 布局及既有键盘/字体/密度/写入回读 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj --no-restore -c Release --filter FullyQualifiedName~RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages --verbosity minimal` | 1 项通过，0 失败，0 跳过 |

## 红绿与范围

- 实施前，六个真实页面的新增默认分页断言均失败：请求仍为 `limit=50`；共享组件尚不存在。实施后六个页面的 25 条默认、第二页 offset=25、50/100 回首屏与本页行数均通过。
- 六个调用点分别注入页大小更改后的 503，验证旧页清空、下一页禁用及明确错误。另验证三个目录筛选取消晚到读取、字典空后续页仍可返回、同页面设置/字典分页大小独立，以及字典分类变化结束 busy 并拒绝旧结果。
- 原会话单测中用于测试分页的任意大小 1 改为 25 条独立会话，并保持实际 offset 断言；服务端参数范围没有更改。关联候选 50 条加载测试未调整。
- 当前 MySQL 审计的 5 项证据见[前一切片记录](46-t06-audit-realtime-paging-tests.md)，本片未修改后端生产代码或重跑全 solution；不累加旧结果冒充一次完整回归。

固定快照/599–600 秒、所有 PAGE、其他 Provider 和真实终端不在通过范围。纯组件/受控 API 测试也不能替代各业务 owner 的事务验收。
