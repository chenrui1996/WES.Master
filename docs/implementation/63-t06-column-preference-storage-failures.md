# T06 列偏好存储不可用与配额失败

- 状态：DTC-UI-016 本机偏好存储错误提示与布局可用性回归切片；不代表完整 DTC-UI-016 或 T06 完成。
- 依据：[密集页面交互合同](../detailed-design/03-dense-page-interaction-contract.md) §2.2、DTC-UI-016。
- 测试记录：[存储失败测试](64-t06-column-preference-storage-failures-tests.md)。

## 实施范围

为已有 `useColumns` 存储错误处理增加具名回归：模拟浏览器禁用存储时读取/写入抛出 `SecurityError`，以及存储配额耗尽时保存抛出 `QuotaExceededError`。验证用户获得明确“未保存”提示，但仍可继续使用当前列布局；写入失败不得覆盖先前有效存储值或伪报保存成功。

本切片只覆盖浏览器存储 API 的已知 `DOMException` 失败，不改变偏好版本、存储格式或业务行为。

## 未完成边界

不覆盖真实浏览器隐私模式/设备配额差异、所有浏览器异常类型及schema v1到后续版本迁移；损坏 JSON 和跨标签草稿已有独立局部测试，但不代表 DTC-UI-016 整项完成。
