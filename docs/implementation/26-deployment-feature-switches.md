# T02 部署功能开关首片实施记录

- 状态：首片已实现；不是T02或DTC-SYS-008整体完成
- 依据：[功能开关设计](../detailed-design/feature-switches.md)、[T02工作包](../superpowers/plans/2026-10-23-wes-development-work-packages.md)

## 范围

- Host 在组合阶段读取强类型、不可变 `Features` 快照；Modules 与 Capabilities 的所有已知键都必须显式提供布尔值。缺项、错型、未知键、重复键均以配置路径报告，异常不回显配置值。
- 当前六个模块 UI 开关只进入白名单catalog；它们不控制API注册、认证、授权或后端数据服务。
- `GET /api/config/features`匿名返回固定模块代码及启用状态，使用 `Cache-Control: no-store`，不公开运行能力、依赖、Provider或凭据。
- 运行能力开关目前均为false。没有能力owner完成注册；误将任何未注册能力设为true时Host组合失败，不使用成功No-op。
- `src/Host/appsettings.json`显式列出所有开关并安全关闭，部署覆盖沿用标准配置提供程序。

## 边界与未完成

- `Wes:Identity:Enabled`仍是身份部署配置，不映射为模块UI开关；身份API依旧由身份配置和授权策略控制。模块关闭不隐藏或取消服务端API。
- 本片未实现可选HostedService/外部连接的enable/disable生命周期、能力Unavailable/Degraded状态诊断、动态前端菜单使用catalog，亦未完成T06/T30/T32集成。
- 不支持运行期热更新；更新部署配置需重启Host。生产环境默认组合、权限签署和现场H门禁仍独立。

## 回退

回退时移除此首片新增的 `Features` 配置读取、catalog endpoint与对应服务，保留既有身份、安全和业务API装配；不得以回退开关为由开放未配置的身份端点或控制能力。
