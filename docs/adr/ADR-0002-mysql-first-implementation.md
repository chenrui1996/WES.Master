# ADR-0002: MySQL优先实施，其他Provider延后兼容

- 状态：accepted
- 决策人：项目发起人，本轮实施对话明确批准
- 关联：T01/T02、[架构](../02-system-architecture.md)、[开发计划](../superpowers/plans/2026-10-23-wes-development-and-testing.md)

## 背景

原计划把真实SQL Server/PostgreSQL/MySQL同时验证作为T02门禁。本机.NET SDK 10.0.103可用，但docker命令不可用；发现运行中的MySQL80服务，不据服务名称推断准确引擎版本、端口、隔离性或使用权限。项目发起人明确要求MySQL最优先，确认另外两种延后而非移除。

## 决策与约束

1. 首期持久化只装配一个经过验证的MySQL EF Core Provider，不提前建立三套迁移/空抽象。SQL Server/PostgreSQL保持deferred；恢复时逐owner添加迁移与真实兼容测试。
2. T02及后续工作包原“三Provider同时通过”的首期门禁改为真实MySQL通过；其余门禁与硬依赖不变。不能拿延后当MySQL事务、并发、租户隔离免测。
3. 不因熟悉Pomelo而将不兼容的EF主版本包强塞入.NET10工程。正式加入持久化前核对准确包的目标框架、EF依赖范围、许可证和引擎支持，restore/build与真实库共同证明；索引存在某版本不等于兼容已验证。
4. 没有获准的隔离库与最小权限凭据不连接现有服务，不读取其他项目secret，也不创建/删除库。凭据由环境/Secret Store注入，不在聊天或仓库收集。
   - 2026-10-23本机开发测试例外：用户已创建并授权`wes_test`，随后明确要求沿用指定Seven配置中的用户名/密码。仅测试装配可读取该来源并在内存强制库名，不修改原配置、不保存/输出凭据、不访问原业务库。此明确授权覆盖此前专用账号前置，不代表生产最小权限通过，详见[隔离库方案](../implementation/02-mysql-isolated-test-environment.md)。
5. 当前可实施不依赖数据库的Host/架构测试切片；数据库未验证则readiness返回503，未装配数据库不得靠进程存活宣称ready。不得加入InMemory/SQLite替代。

## 影响、取舍与回退

缩短首个可验证增量的关键路径，但首期不声称产品具有三Provider兼容性。保留decimal(28,6)、UTC时序、显式乐观并发、owner本地事务及唯一键不变量；MySQL特定差异留在owner持久化内部，不暴露给契约。

未来兼容必须补唯一键/排序规则/时间精度/死锁重试/事务隔离/全量grant/Outbox及迁移恢复矩阵，不能仅更换连接串。无法证明MySQL Provider兼容时暂停相关切片并提交选型证据，不静默降低EF/.NET版本。

## 验证与复审

无DB切片不访问设备、数据库或生产出口。真实库版本、provider/驱动锁版、专用测试主体/库和迁移清理范围在[就绪台账](../implementation/readiness-register.md)具备证据后才能关闭。首次持久化及再次启用其他Provider时复审本ADR。
