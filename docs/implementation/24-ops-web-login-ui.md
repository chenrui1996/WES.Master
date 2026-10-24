# Ops Web 登录页视觉与交互实现

- 状态：已实现；测试结果与适用边界见[测试记录](25-ops-web-login-ui-tests.md)
- 范围：T03 页面呈现切片、PAGE-PLAT-001；不代表T03整包完成
- Owner：Ops Web
- 设计依据：[工业风格视觉基准](../detailed-design/06-ops-web-visual-style-baseline.md)、[Platform详细设计](../detailed-design/platform-and-security.md)

## 1. 实现范围

登录表单调整为桌面双栏、窄屏单栏：石墨侧栏展示WES身份、软件环境和现有授权/离线边界；表单面板保留租户、账号、密码、验证码、登录与当前会话恢复。没有增加虚构的在线、健康或现场安全状态，也没有改动认证API、凭据、挑战、限流、token或导航合同。

表单标签/说明继续使用真实语义元素；密码可通过具名按钮显示/隐藏，切换不清除或规范化输入，并允许浏览器密码管理器/autofill与粘贴。输入和按钮目标维持至少44px，验证码图像在窄屏限制最大宽度，按钮和表单状态保留现有disabled/busy/error语义。色彩、字号、边界沿用共享WES token。

## 2. 变更文件

- `clients/ops-web/src/features/identity/LoginPage.vue`：访问边界说明、分组表单、密码可见性控制与错误描述关联。
- `clients/ops-web/src/style.css`：登录双栏/窄屏布局及表单控件样式。
- `clients/ops-web/src/features/identity/LoginPage.spec.ts`：覆盖密码切换与提交值保持。
- `docs/detailed-design/06-ops-web-visual-style-baseline.md`：固化登录页布局与安全呈现规则。

## 3. 明确不包含

不引入新身份认证方式、跳过验证码/限流、修改密码策略、后端配置、可记住账号选项、远程字体/图标、动画或现场状态探针。现有认证契约与安全判定仍由服务端负责。
