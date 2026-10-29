# T12 定义/表单内核测试记录

- 状态：已验收（仅本纯校验子片）；2026-10-29；设计：[187](187-t12-definition-validation-core.md)。
- 范围：纯WorkflowForm校验及不可变快照。无Host/HTTP/UI、真实发布/实例、MySQL/Platform文件集成或物理设备结果。
- 已查RULE-10/11与DEF-004/005/006/012、GEN-003/004/006/010、INF-004；以下均为本片实际执行，不沿用T11数字。

## 1. RED/GREEN及发现

1. 新增独立WorkflowForm项目与强类型内部模型、测试，新增项目引用后允许正常restore。首轮编译发现xUnit公开测试参数不能使用internal枚举；测试参数改为int，不为测试公开生产枚举。
2. 77项真实调用未实现内核，**77失败/0通过**，异常均为占位NotImplementedException，记录为缺失实现RED，不包装成业务断言已通过。实现后**77/77**。
3. 补全六个Decimal运算符的等值边界、文本Ordinal/布尔分支、100节点链实际遍历、100字段真实数据、候选容量和快照隔离。
4. 新增7项边界RED：4项key/label/business/node尾换行被正则`$`接受，3项required Text把空串/仅空白当合法。修复为`\A...\z`全字符串锚点和required非空白，扩展集**105/105**。
5. 新增坏key之后第二字段非法label定位RED：错误path错误使用已收集合法key数，两个错误均指fields[0]；改按原始索引定位，保留第二字段fields[1]。最终联合回归包含该例。

## 2. 最终命令及结果

环境为现有Windows/.NET10.0.103；无数据库/网络/设备副作用：

```powershell
dotnet test tests\Modules\Wes.Modules.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~DefinitionValidationTests|FullyQualifiedName~Wes.Modules.Tests.Wms"
dotnet build WES.slnx --configuration Release --no-restore
dotnet test tests\Architecture\Wes.Architecture.Tests.csproj --configuration Release --no-restore
```

- Modules联合回归：**302通过、0失败、0跳过**，含本片113项及WMS领域/开关189项；范围有重叠时不累计。
- 完整解决方案Release：**0警告、0错误**；新模块纳入solution，Modules只增加真实项目引用，lockfile由restore生成，没有新第三方依赖。
- 架构：**29通过、0失败、0跳过**，真实生产项目图包含新owner叶项目；Host语义边界保持。
- changed-file `dotnet format whitespace ... --verify-no-changes`和`git diff --check`通过。
- 格式化后最终复跑同一联合选择器仍**302/302**，TRX逐项核实T12为**113/113**；架构复跑**29/29**。13份变更Markdown检查1000处本地相对目标，**0新增断链**；67处不可用目标均为HEAD已存在的外部Seven/Route/架构材料引用，未修改为伪造本地文件；本检查不声明所有历史锚点已验证。
- 实际实现/用例：[内核](../../src/Modules/WorkflowForm/Definitions/)、[可执行测试](../../tests/Modules/WorkflowForm/DefinitionValidationTests.cs)。TRX保存在会话验证目录，不含入库凭据，不提交缓存或bin/obj。

自查核对：所有入口internal、无跨模块依赖、无发布成功回执；非法输入无部分成功值，保留原字段索引；图无环/唯一sink且全可达，不出现无限遍历或并行；S1/default/条件JSON深复制，分支缺值不当otherwise；当前候选/可写字段/文件集合仅作为受信owner输入，不充当已接通Platform权限。

## 3. 验收边界与续片

TC-T12-CORE-01..07完成纯内核子集，对应REQ-WF-001..008、FUNC-WF-001/004及DTC-WF-002/005/008的规则子集。**不把DTC父用例改为通过**：尚无定义草稿/不可变发布/审计/并发/恢复事实、真实S1历史实例、页面/HTTP或Platform授权/候选/文件公开合同适配。

无MySQL模型/迁移变化，故不执行真实Provider、EF脚本/漂移/故障矩阵；无前端/Host更改，故不重复前端1219或HTTP/浏览器。未来发布owner须补上述证据，不能复用本纯测试代签。

下一依赖工作：T12持久定义/schema版本、当前受权管理入口、严格JSON/默认关闭HTTP、具名页面及真实MySQL/HTTPS验证；角色组织候选、显示/格式/保留策略留相同父任务。审批实例/AllAny竞争/资格实时核验/hook仍属T13。T11保留原软件待办，不因本片交付而关闭。

踩坑库：新增**DEF-016**（正则尾锚/空白必填），更新**GEN-006**（验证错误定位与完整容量）、**GEN-007**（Windows PowerShell文档检查显式UTF-8）。索引同步；没有隐藏未解决的通用缺陷。
