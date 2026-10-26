# T06 候选重置后的旧响应隔离测试记录

- 状态：候选读取器reset竞态定向测试通过；全量回归、构建和变更检查待执行，不代表完整 DTC-UI-022。
- 关联：[实施记录](73-t06-stale-directory-candidate-response.md)。
- 用例：DTC-UI-022 旧回包不覆盖当前候选状态的局部回归。

## 验证场景

1. 启动首次候选请求并延迟响应。
2. 调用读取器 `reset()`，确认候选和分页状态清空。
3. 发起新一代首屏读取并延迟响应。
4. 先返回旧请求结果，验证其不填充候选且不将新请求状态改为未加载。
5. 返回新请求结果，验证新候选正常呈现；另以新响应先到、旧响应后到的顺序验证旧结果不覆盖新候选。

## 实测结果

| 范围 | 命令 | 实测结果 |
|---|---|---|
| 候选读取器重置竞态 | `npm test -- --run src/features/platform/useDirectoryCandidates.spec.ts`（`clients\ops-web`） | 1个测试文件、2项通过 |
| Ops Web 全套回归 | `npm test -- --run`（`clients\ops-web`） | 20个测试文件、167项通过 |
| Vue/TypeScript 与生产包 | `npm run build`（`clients\ops-web`） | `vue-tsc --noEmit`与Vite生产构建通过 |
| 变更空白检查 | `git diff --check` | 通过 |

## 限制

使用受控前端响应，不模拟真实网络、身份提供方或远程搜索；该受控测试不覆盖 DTC-UI-022 的 A/AB 查询竞态、候选回填和后端写拒绝。
