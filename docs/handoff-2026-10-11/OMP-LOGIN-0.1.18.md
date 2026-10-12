# OMP 管理账号与请求：0.1.18

PopGlot 通过本机 OMP 18.2.11 JSONL RPC 获取登录提供方并发起授权。账号、凭据保存及刷新、上游模型请求继续由 OMP 管理，不实现第二套 Google OAuth，也不读取 OMP OAuth 数据库记录。

## 使用

1. 在翻译引擎页打开「连接本机工具」，默认优先选择已安装的 OMP。
2. 点击「连接 / 刷新账号」。已登录账号直接选择模型、测试翻译、保存并使用，无需再次授权。
3. 新账号或授权失效时选择提供方，点击「通过 OMP 登录所选提供方」，在系统浏览器完成授权。
4. OMP 要求回调输入时粘贴授权码或回调地址。界面不接收密码；输入会在完成或取消时清空，不写入配置或日志。
5. OMP 最终确认登录后重新获取账号状态与模型。用 Hello world 测试翻译确认权限与额度，再保存使用。
6. 取消只结束 PopGlot 所启动的 OMP 进程。若取消前 OMP 已保存授权，刷新账号查看最终状态；断开引擎不会退出 OMP 账号。

## 实现边界

- `OmpRpcBackend.ListLoginProvidersAsync` 使用 `get_login_providers`，显示实际 available/authenticated 状态。
- `LoginAsync` 独立订阅登录 UI 事件，处理 `open_url`、授权 URL 之后的 `input`、匹配 ID 的 `extension_ui_response`；最终 `login` 结果、事件处理和策略监控并行等待，任何失败都会停止自有进程。
- 登录地址仅允许 HTTPS、默认端口、无 userinfo 的已知身份服务域名。未知地址或交互要求应在 OMP 终端完成登录后刷新，不能自动执行任意操作。
- 运行中网络/开发安全开关改变会中止登录。翻译通道仍拒绝交互授权和工具执行。
- `ServicesSection.OmpLogin.cs` 提供登录、回调、取消、超时和晚到结果隔离。`ServicesSection.CliConnections.cs` 在连接及登录后读取提供方状态；模型变化清除旧测试证据。
- 固定协议版本 18.2.11，其他版本继续明确拒绝。现有已保存引擎保持兼容，翻译仍经过 OMP。

## 验证范围

- 协议与 WPF 自动化：提供方可用/登录状态、浏览器 URL、匹配回调 ID、最终确认、登录错误、不安全地址、授权前输入拒绝、超时、回调等待取消、模型刷新。
- 本机 OMP 18.2.11 实测返回 77 个登录提供方（经过界面过滤），其中 3 个存在授权，包含 Antigravity；模型列表成功读取。不发起新的真人授权。
- 已有智谱账号的短句翻译两次未完成；第二次捕获提供方状态 429，属于请求失败证据，不能宣称该账号当前可翻译。界面会保留失败状态；服务额度或限流需另行处理。错误反馈只显示识别到的状态码，不显示原始提供方错误文本。
- `scripts/verify.ps1` 通过：Rust 测试、fmt、clippy、Windows 编译、47 项 PureTests、318 项 LogicTests。
- 最终 CLI 协议与 WPF 检查 21 通过、0 失败，包括运行中关闭联网中止登录及提供方 HTTP 错误脱敏。日志：`artifacts/cli-repair/v18-cli-final.log`；界面截图：`artifacts/cli-repair/omp-login-panel.png`。Google 新账号真人授权未验证。
- Google Antigravity 新账号浏览器授权与资格检查未验证；模拟授权结果不得作为真实登录证据。旧机器唤醒性能未在此改动中重新测量。

协议来源：[OMP 18.2.11 RPC 文档](https://github.com/can1357/oh-my-pi/blob/v18.2.11/docs/rpc.md)。
