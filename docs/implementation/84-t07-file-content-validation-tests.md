# T07 文件内容校验测试记录

- 状态：已验证；仅覆盖 PAR-04 的纯字节/声明校验器。
- 关联：[实施记录](83-t07-file-content-validation.md)、[执行计划](../superpowers/plans/2026-10-26-t07-file-content-validation.md)。
- 用例：DTC-PLAT-007/VAL-05 的格式与大小变体；不宣称覆盖扫描、存储或引用生命周期步骤。

## 验证场景

1. 合法 PDF、PNG、JPEG、UTF-8文本，及20 MiB准确边界接受；20 MiB+1、零字节拒绝。
2. 扩展名/MIME/签名不匹配、未知扩展、路径名、非法MIME参数拒绝。
3. 声明文本含无效UTF-8或NUL、HTML/SVG/script/XML标记时拒绝。
4. 常见PE/ELF、OLE宏文档、ZIP/GZIP/RAR/7z容器签名拒绝，即使扩展名/MIME伪装成允许类型。
5. 返回结果不包含输入文件名、路径、MIME或内容；校验成功不创建持久状态或文件引用。

## 实测结果

| 范围 | 命令 | 实测结果 |
|---|---|---|
| Platform 内容校验器 | `dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --filter FullyQualifiedName~FileContentValidatorTests` | 38 passed、0 failed、0 skipped；含20 MiB边界与拒绝用例。 |
| Platform/Host Release编译 | `dotnet build tests\Modules\Wes.Modules.Tests.csproj -c Release --no-restore` | 成功；0 warning、0 error。该命令构建Platform、Host及测试项目依赖图。 |
| 全solution Release回归 | `dotnet test WES.slnx -c Release --no-restore -m:1` | 884 passed、0 failed、0 skipped：Architecture 29、Modules 271、Persistence 356、Route 228。串行运行通过。首次并行solution运行因测试项目资产缺失及本机MySQL SSL I/O导致中止；还原缺失资产后串行复跑通过。 |
| C#格式与变更空白检查 | `dotnet format whitespace tests\Modules\Wes.Modules.Tests.csproj --verify-no-changes --include src\Modules\Platform\Features\Files\FileContentValidator.cs tests\Modules\Platform\FileContentValidatorTests.cs`；`git diff --check` | 两项均成功，无格式差异或空白错误。 |

## 限制

未运行扫描服务/真实对象存储/文件生命周期API/隔离HTTPS Chromium。纯内容特征判断不能替代恶意软件扫描或文件格式深度解析；脚本标记检测是有限启发式，不能分类所有可能的脚本文本。该记录不关闭T07 DTC-PLAT-007，也不取代T04/T06依赖。
