# T07 扫描与孤儿删除协调测试

- 状态：实施中；关联[设计](101-t07-file-processing-coordination.md)。
- FILE-COORD-01：存储读/扫描前独立数据库context可见已提交attempt；四种扫描结果明确入库，不能以隔离结果返回Available。
- FILE-COORD-02：实际字节/扫描证据不匹配不放行；60秒迟到Clean仍TimedOut，外部取消保留隔离。
- FILE-COORD-03：版本过期、跨tenant、活跃attempt不访问外部端口；新attempt/Deleting后旧结果不改变状态。
- FILE-COORD-04：同时核验引用表和计数；未满24小时或活跃扫描不删除；已绑定文件不被清理。
- FILE-COORD-05：外部删除前数据库已Deleting；存储失败/墓碑保存失败保留恢复状态，缺失对象幂等重试后Deleted。
- FILE-COORD-06：数据库故障分类不泄露敏感原文；所有故障、取消与版本竞争不伪装成功。

协调测试仅验证软件owner与端口契约；真实病毒清除、完整上传/下载授权/API/页面和T07整体仍未验收。

## 红绿与故障证据

1. 先补持久claim、四种扫描结果、证据错配、外部删除失败恢复和真实引用表核验，运行后观察缺少FileProcessing类型的编译RED。实现内部协调后与原8项文件持久化测试共同通过。
2. 再补慢claim反例：真实SaveChanges成功后将server clock推进到开始后的60秒，预期不再访问存储/扫描。实际先观察Expected reads=0、Actual=1；将IO预算改为原attempt截止的剩余时间，超期立即取消，转GREEN。不是仅靠最终“仍隔离”掩盖额外执行时间。
3. 追加迟到Clean、调用者取消、替换attempt/删除fence后旧结果、损坏存储内容、跨tenant/旧版本/活跃attempt、存储中断后明确重试、已Available文件不被误禁、未满24小时与活跃扫描拒删、开始claim数据库故障、对象删除后数据库确认失败恢复。
4. 使用真正LocalQuarantineStore与真实MySQL共同执行确认字节读取、扫描结果入库和物理删除；随后真实存储读取明确失败且数据库留下Deleted墓碑。扫描器仍是受控端口，不把此测试称为病毒识别或真实ClamAV健康验收。随机临时根仅包含本例对象，finally删除本例具名根，无持久服务。
5. 两个独立真实context并发使用相同初始Version扫描，最多一个claim进入外部端口。用原子计数验证存储读和scanner各1次；另一个调用可为VersionConflict/StateConflict，或在首次已经完成时仅返回现存Available，不二次扫描。

```powershell
dotnet test tests\Persistence\Wes.Persistence.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~QuarantinedFilePersistenceTests -m:1
```

文件持久化与协调共 **28 passed、0 failed、0 skipped**（原8项、本片新增20项）。与WMS账本/预留48项曾合并验证75项通过，此后新增的并发扫描用例也在最终全量回归中通过，不把历史75当作最终计数。

```powershell
dotnet test WES.slnx --configuration Release --no-restore -m:1
```

最终完整串行Release：Architecture **29**、Modules **420**、Persistence **449**、Route **228**，合计 **1126 passed、0 failed、0 skipped**。本片修改C#已按仓库格式化，`git diff --check`通过。未修改EF模型或迁移；未改前端，不称浏览器/API授权已经验收。

数据库只使用授权wes_test及本例随机tenant，清理仍仅为Platform文件引用/文件/本例tenant；无生产迁移或新依赖。真实ClamAV/病毒库、受权上传/绑定/下载及API/页面尚未完成，T07整包不关闭。
