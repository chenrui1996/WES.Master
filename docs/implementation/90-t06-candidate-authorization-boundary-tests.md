# T06 候选授权与取消测试记录

## 编码前场景

1. 满25候选后append或new read收到401/403/authorized=false：清rows/more，当前错误明确可见；再授权读取恢复。
2. 新code查询未完成时旧候选立即不可选；保持code、25分页和稳定ID去重。
3. reset/外部abort/新查询后旧成功、旧失败不得修改新rows/more/loading或向消费页抛旧错误；已abort的signal不发送请求。
4. 当前503明确提示并清候选，避免无效数据可选。
5. Users真实角色选择及Roles已选部门摘要在候选拒绝后清除编辑目标/草稿；正常详情resolve及精确代码筛选回归保持。

## 执行证据

2026-10-26，在`clients\ops-web`执行（无依赖或lock变更）：

### RED（先新增测试，尚未改实现）

- `npm test -- src/features/platform/useDirectoryCandidates.spec.ts`：15项，4通过、11失败；复现append拒绝残留25行、新筛选保留旧行、旧失败继续抛出、abort仍loading及503旧行保留。
- `npm test -- src/features/platform/UsersPage.spec.ts src/features/platform/RolesPage.spec.ts --reporter=dot`：41项，37通过、4失败；两页面各403/denied后编辑表单仍存在，受保护标签及已选摘要未失效。

### GREEN

- `npm test -- src/features/platform/useDirectoryCandidates.spec.ts src/features/platform/UsersPage.spec.ts src/features/platform/RolesPage.spec.ts --reporter=dot`：3文件、61项通过。包含当前503明确提示、实际表单/目标清理、代码搜索拒绝错误不被reset后的watch抹除、旧代码搜索403不覆盖AB候选；保留已有分页、禁用项、详情引用解析和代码筛选测试。
- 最终相关回归命令：

  ```text
  npm test -- src/features/platform/useDirectoryCandidates.spec.ts src/features/platform/UsersPage.spec.ts src/features/platform/RolesPage.spec.ts src/features/identity/identity-api.spec.ts src/features/realtime-paging.spec.ts src/components/QueryState.spec.ts src/components/RealtimePager.spec.ts src/router.spec.ts --reporter=dot
  ```

  8文件、100项通过（候选/消费页61项及identity、实时分页、查询状态、路由39项）。
- `npm run build`：通过，执行`vue-tsc --noEmit && vite build`，69模块转换成功。
- 仓库根目录`git diff --check -- clients\ops-web\src\features\platform docs\implementation\89-t06-candidate-authorization-boundary.md docs\implementation\90-t06-candidate-authorization-boundary-tests.md`：通过。

### 集成复核

- 新增已abort signal的迟到调用回归：新查询尚未完成时，旧signal调用不能清掉其loading或中断它；新查询完成后也不能清行。先运行`npm test -- src\features\platform\useDirectoryCandidates.spec.ts -t "does not let a late call"`，1项失败、15项未选中（不是验收跳过）；修复为已取消调用直接返回null，不reset其他请求。
- 最终在`clients\ops-web`运行`npm test`：**20文件、201 passed**；`npm run build`：严格TypeScript检查与生产构建均通过。当前共享候选16项、相关Users/Roles46项由整套一起覆盖。
- `git diff --check`通过；后端串行回归结果统一见[就绪台账](readiness-register.md)，不是本前端切片的Provider契约证明。

未执行本片浏览器E2E，本切片没有后端/provider契约变更，不将完整DTC或T06标为完成。无已知切片阻塞项。
