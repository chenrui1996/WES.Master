# T04 数字字典码测试

- 状态：已验收（本子集），2026-10-27；设计：[151](151-t04-numeric-dictionary-code.md)。

| 子用例 | 输入/步骤 | 预期 |
|---|---|---|
| DICTIONARY-CODE-01 | `0`/`01`/数字开头混合码、原合法码、128/129字符 | string原值保留；原边界不放宽 |
| DICTIONARY-CODE-02 | 空/null/边界空白/control/HTML/非ASCII/非法首字符；数字分类/语言键/部门/角色 | 明确拒绝，其他技术标识仍字母起始 |
| DICTIONARY-CODE-03 | 真实MySQL受权创建0、换label、语言回退、引用后删除/旧version | code与引用不变，拒绝无新增副作用 |
| DICTIONARY-CODE-04 | 真实HTTP创建数字码/无权限/非法body/重复 | 200或准确拒绝，tenant/code唯一、无非法审计 |
| DICTIONARY-CODE-05 | 真实HTTPS Chromium创建0/改译/换语言/诊断key/引用删除 | 对应响应与页面一致、纯文本不执行、无强制删除、最终owner核证 |

## 执行结果

- RED：领域0/01/1Ready与数字开头128字符明确ArgumentException；最初新HTTP夹具漏SignIn导致401，按既有模式补登录后重跑，3个数字码均真实400而非200，确认实际业务校验缺陷，不把认证失败当需求RED。
- 实现仅DictionaryEntry.Code局部验证，完整ASCII字符/128边界及首尾锚定，未修改DirectoryValue.Code。原角色/部门/分类/语言键仍拒数字标识。
- `dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~DictionaryCodeTests|FullyQualifiedName~NumericDictionaryHttp|FullyQualifiedName~DictionaryHttpUsesStableCodes"`：28通过＝新增领域18/HTTP9+原字典HTTP1；包含0/01原string、非法输入/无权无写、同分类重复409、原label版本及语言回退。TRX `numeric-dictionary-validation\http-domain\15009_SHERRY377_2026-10-27_16_05_44.trx`。
- `dotnet test tests\Persistence\Wes.Persistence.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~DirectoryPersistenceTests`：23通过，含新数字code在真实MySQL创建、默认语言回退、引用后删除拒绝，恰好3条字典审计及3条Outbox，无非法删除副作用。TRX `numeric-dictionary-validation\owner\15009_SHERRY377_2026-10-27_16_06_16.trx`。
- 真实HTTPS Chromium首轮因“语言键”同时命中列选择checkbox与创建textbox失败，改为精确textbox角色/名称；第二轮误期待空表仍渲染，按现有QueryState空态合同改为明确断言旧表移除。未改服务端错误、放宽准确名称或伪造成功响应。
- `dotnet test tests\Modules\Wes.Modules.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~RealBrowserUsesHttpsOwnerIdentityAndAllNamedPlatformPages`：1通过，TRX `numeric-dictionary-validation\browser-final\15009_SHERRY377_2026-10-27_16_09_15.trx`。真实登录/当前MySQL授权/字典/审计/配置/用户等API无替身；已有模块catalog fixture仍仅用于Platform UI组合，不冒充所有运行模块接线。
- 浏览器键盘创建0、`<b>Zero state</b>`仅纯文本无b元素、再改中文；en请求有自身Ready文本，0回默认zh-CN，de-DE两者回默认，缺翻译显示`[task.missing]`。已有引用Ready删除409 ResourceConflict、清旧表、手工重读仍保留Ready；三宽度无横溢。最终第二owner核0原值/3项字典/3条字典审计/Ready两翻译及引用保留；原用户/配置/审计导出/消息重放最终断言仍全部通过。
- 架构29通过，TRX `numeric-dictionary-validation\architecture\15009_SHERRY377_2026-10-27_16_09_49.trx`；精准格式验证及`dotnet build WES.slnx -c Release --no-restore`零警告错误。合计81相关.NET通过，无模型/迁移/依赖变化。

本机session evidence不入仓凭据/证书。前端生产代码未改，既有604及严格构建记录见[150](150-t05-wms-creation-event-counts-tests.md)，本片不冒称重新执行Vitest全套。未运行其他两Provider、真实PDA/生产/现场，父DTC其他变体和T04–T08整包仍未完成。
