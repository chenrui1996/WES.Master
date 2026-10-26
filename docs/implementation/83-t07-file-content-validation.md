# T07 文件内容校验 foundation

- 状态：本切片已实现并验证；这是 T07 的独立、无副作用校验切片，不代表文件生命周期可用或 T07 完成。
- 基线：[PAR-04](../detailed-design/05-approved-rules-and-defaults.md)；[Platform 文件生命周期设计](../modules/platform-and-security.md) §4、FUNC-PLAT-005、DTC-PLAT-007、PLAT-TC-011/015..018。
- 依赖状态：T07 整包的 T04/T06 硬前置仍未整体关闭；真实存储/扫描服务与秘密装配也没有证据。本切片不接入其集成路径，不改变该状态。
- 测试记录：[T07 文件内容校验测试](84-t07-file-content-validation-tests.md)。
- 执行步骤：[T07 内容校验执行计划](../superpowers/plans/2026-10-26-t07-file-content-validation.md)。

## 目标与设计

在 Platform owner 内建立纯、确定、可测试的内容声明校验器，为后续上传适配器提供 PAR-04 的单文件大小、扩展名、MIME 与内容特征校验。校验器只接收调用方已读取的文件名、声明 MIME 与有界字节内存；不读写文件、不调用数据库或网络、不生成对象名，也不保存原始内容。

允许类型固定为 PDF、PNG、JPEG 与 UTF-8 纯文本，扩展名和 MIME 必须一致：`.pdf`/`application/pdf`、`.png`/`image/png`、`.jpg`或`.jpeg`/`image/jpeg`、`.txt`/`text/plain`。纯文本可无 charset 或明确 `charset=utf-8`；拒绝其他 MIME 参数。内容必须分别匹配 PDF `%PDF-`、PNG 标准签名、JPEG 起始标记或严格 UTF-8。文本拒绝 NUL 与明显 HTML/SVG/script/XML 标记。拒绝路径分隔符、控制字符、未知扩展名，以及常见可执行、OLE 宏文档和压缩容器签名。

大小按实际内容字节数计算，最大值含边界为20 MiB；空内容拒绝。输出只含稳定判定码，不包含文件名、MIME、内容片段或路径。文本脚本/活动内容检测只覆盖明显标记（如 shebang、script/XML 标记和脚本 URL），不能可靠识别任意语言的脚本，也不等价于解析器沙箱或恶意软件扫描。校验通过仅表示扩展名/MIME/基础签名与编码约束一致，不是安全扫描结论；绝不能据此标记文件 Available。

## 明确边界

- 本切片不添加 Upload/Scan/Download/Clean endpoint、文件实体、迁移、存储适配器、扫描接口或后台任务。
- 真实上传接线必须在接收阶段就实施流式20 MiB上限，避免把任意大body先完整缓冲后再调用本校验器。
- 缺少真实扫描配置必须保持不可用；存储失败、扫描失败/超时、处理问题、引用授权与孤儿清理须在后续有独立证据的切片实现。
- 前置包和集成证据不足时不把独立校验器升级描述成可用文件生命周期，也不扩大授权范围。
