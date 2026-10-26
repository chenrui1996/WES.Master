# T08 仓库库位主数据测试

- 状态：已验收（仅列明的内部创建基础）；关联[设计](115-t08-location-master-data.md)。
- WMS-MASTER-01：真实owner模型缺warehouse/location/change三表RED；迁移/模型一致。
- WMS-MASTER-02：稳定code/显示名校验、默认禁用、不可变归属、版本身份及精确schema绑定。
- WMS-MASTER-03：同tenant重复code/跨tenant同code、错误warehouse FK与owner写保护。
- WMS-MASTER-04：创建与change同事务；不能改历史或删除identity/复用历史change。
- WMS-MASTER-05：同operation重放/换体冲突、并发同code/同operation只一效果、真实保存失败/取消无半笔。
- WMS-MASTER-06：Pack schema未知/拒绝/取消无主数据；schema成功不等于启用或物理安全许可。
- WMS-MASTER-07：核验真实information_schema中五个code/schema列的binary排序规则及大小写schema查询，不把EF模型注解当成Provider实际DDL证据。

## 执行记录

| 用例组 | 输入与步骤 | 必须断言 | 结果 |
|---|---|---|---|
| WMS-MASTER-01 | 真实WmsDbContext初始模型查三表；随后执行两次增量迁移并检查pending | 初始缺warehouse为RED；最终模型无pending，新增三表/复合键/FK/索引，旧库存表不变 | passed |
| WMS-MASTER-02 | 新建`wh-01`与`a-01`、中文名；变更空ID、超长/非法code/name、schema大小写 | 代码大写、名称不损失、默认禁用、精确归属和UTC/版本；非法输入无写入 | passed |
| WMS-MASTER-03 | 相同tenant同code、另一tenant同code；真实foreign仓或不存在仓建位；直接写错误复合FK | 同tenant冲突不覆盖，跨tenant隔离，查不到仓不调用schema，非法FK无半笔；主键重赋值立即拒绝 | passed |
| WMS-MASTER-04 | 创建缺失/错配change、修改历史change、启用/换Pack/删除tracked实体、借历史仓补造creation | 一资源一精确同次新增事实，所有违规保存拒绝，历史identity/版本不变 | passed |
| WMS-MASTER-05 | 相同operation规范化重放/换actor或体；两个独立owner并发同operation/同code；真实Save后抛错或取消 | 仅一个创建效果，重试返回原Id/Version，换语义冲突；失败后资源与事实均回滚，新owner可恢复 | passed |
| WMS-MASTER-06 | schema Rejected/Unavailable、null adapter、未知枚举、adapter期间取消后迟到Valid；注入DB查询故障 | 未核验不落库，缺adapter/未知枚举明确异常，取消无新位；安全DB异常不包含原异常细节 | passed |
| WMS-MASTER-07 | 直接查询真实information_schema五列，再用`V1`/`v1`查询schema | 首轮5/5错误collation RED；修正迁移后全为utf8mb4_bin，`V1`无结果且`v1`精确命中 | passed |

- 新增领域 **14项**及真实MySQL owner **34项**通过；与既有账本/预留联合 **82 passed、0 failed、0 skipped**。首次14项owner中1项失败是测试把主键修改异常断言误放Save边界，修正为断言赋值即拒绝，没有放宽产品门禁。
- 全solution Release构建 **0警告、0错误**。最终编译源完整串行Architecture **29**、Modules **507**、Persistence **549**、Route **228**，合计 **1313 passed、0 failed、0 skipped**，包含既有两套真实HTTPS浏览器回归。
- EF `has-pending-model-changes`无变更；增量SQL从`20261026162008_WmsDurableReservations`到`20261026195413_WmsMasterDataBinaryColumns`人工核查新增三表/键/索引及五列binary修正，不改旧库存。曾误写前置迁移ID而SQL生成失败，改为仓库真实迁移ID后成功；失败不计验收。
- TRX及SQL保留在会话`files\wms-masterdata-validation\{targeted,final,migration.sql}`。本片没有修改前端源码，不声称新运行独立240项Vitest套件。

## 复跑

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~WmsMasterDataTests|FullyQualifiedName~WmsLedgerTests" -m:1
dotnet build WES.slnx --configuration Release --no-restore -m:1
dotnet test WES.slnx --configuration Release --no-build --no-restore -m:1
dotnet ef migrations has-pending-model-changes --project src\Modules\Wms\Wes.Wms.csproj --context WmsDbContext --configuration Release --no-build
```

仅授权wes_test真实MySQL；不输出凭据，不执行生产迁移。schema fixture只证明调用合同，不是真实Pack/schema验收；权限/Host/API/页面、主数据编辑启停、占用/锁/活动证据、容器绑定、Outbox与运输完成仍未验收，本片不关闭父DTC/T08或T04–T07。
