# PopGlot

PopGlot 是一个 Windows-first 的轻量 AI 翻译桌面助手。首要场景是程序员阅读英文报错、代码变量、命令、路径和技术文档；底层同时为普通屏幕翻译和未来 macOS/Linux Shell 保留清晰边界。

当前仓库已经包含可用的 Windows 核心交互：选中文字后按快捷键即可安全读取选区、恢复剪贴板并翻译；截图框选会捕获内存 PNG，并在获得明确授权后走视觉 Provider。划词和截图共享同一个结果浮窗、取消、错误、复制与本地历史模型。模型网络默认关闭，Safe Dev Mode 默认开启。

## 当前能力

- Windows 托盘常驻；默认 `Ctrl+Alt+W` 划词、`Ctrl+Alt+Space` 截图、`Ctrl+Alt+X` 关闭浮窗。
- 划词使用有界剪贴板事务模拟 `Ctrl+C`；复制成功、失败或取消都会按序列号规则恢复，且不会覆盖用户随后复制的新内容。
- 多显示器选区遮罩、真实内存截图与统一结果浮窗；`Esc` 随时取消。
- `Auto / LocalOcr / VisionDirect` 三种翻译模式和可解释路由。
- 统一 Provider 契约：OpenAI-compatible Chat Completions、OpenAI Responses、Anthropic Messages、Gemini GenerateContent。
- 可配置 Base URL、文本/视觉 Endpoint、模型、非敏感自定义请求头和显式文本/图片能力。
- API Key 只保存到 Windows Credential Manager，不写普通 JSON。
- 程序员 Token 保护基础：异常名、标识符、路径、URL、命令参数等。
- Rust Core 与 WPF Shell 通过小型 C ABI/JSON 契约连接。
- Safe Dev Mode 与独立网络许可：任一门禁关闭就不会发起模型请求。
- 用户主动的纯文本连接测试；绝不以测试功能上传截图。
- 深浅色、分组导航设置页，结果窗包含原文、译文、解释、受保护术语、复制、重试、固定和关闭。
- 本地 JSON 历史默认关闭；开启后最多 100 条/90 天，疑似密钥、过大内容和截图位图不记录。

当前限制：Windows 本地 OCR 适配器尚未接入，因此截图的 `LocalOcr` 路线会明确提示不可用；已授权的 `VisionDirect` 可以真实调用视觉模型。真正的跨应用鼠标 hover 取词也尚未实现：UI Automation 并不被所有浏览器、编辑器、终端和管理员窗口一致支持。四种协议通过本机 mock 验证，仓库没有使用真实 API Key 做云端调用。

## 使用

1. 在设置的“翻译服务”中选择 Provider，填写文本/视觉模型和 API Key。
2. 启用模型网络并关闭 Safe Dev Mode。截图还需勾选图片上传授权。
3. 在任意支持复制的应用中选中文字，按 `Ctrl+Alt+W`。
4. 或按 `Ctrl+Alt+Space` 框选屏幕区域。使用 `Esc` 或 `Ctrl+Alt+X` 关闭浮窗。

密码框、禁止复制的界面、不同权限级别窗口可能无法读取选区；PopGlot 会显示失败状态，不绕过 Windows 安全边界。交互依据与取舍见 [UX 决策](docs/UX_DECISIONS.md)。

## 开发环境

- Windows 11 x64
- Rust stable，目标 `x86_64-pc-windows-msvc`
- Visual Studio Build Tools 2022：MSVC x64/x86 与 Windows 11 SDK
- .NET 10 SDK x64
- PowerShell 7（脚本也尽量兼容 Windows PowerShell）

本机验证版本：Rust 1.98.0、Cargo 1.98.0、rustfmt 1.9.0、Clippy 0.1.98、.NET SDK 10.0.400、MSVC 14.44、Windows SDK 10.0.26100.0。

## 构建与运行

```powershell
# 完整验证
./scripts/verify.ps1

# 运行托盘应用
./scripts/run.ps1
```

也可以分别执行：

```powershell
cargo test --workspace --locked
cargo clippy --workspace --all-targets --locked -- -D warnings
dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj
dotnet run --project tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj
```

WPF 项目构建时会自动构建 `popglot-ffi` 并将 `popglot_ffi.dll` 复制到输出目录。

## 配置与隐私

非秘密设置位于 `%LOCALAPPDATA%\PopGlot`。API Key 使用 Windows Credential Manager 的通用凭据项 `PopGlot/OpenAICompatibleApiKey`；为兼容初始版本保留了该名称，它代表“当前活动 Provider 的 Key”，而非写死 OpenAI。

- `LocalOcr` 模式的产品契约是永不上传截图。
- `Auto` 只有在用户明确允许、视觉模型已配置且路由认为必要时才能上传截图。
- `VisionDirect` 失败后必须安全回退到本地 OCR + 文本模型。
- 默认 `network_enabled=false` 且 `safe_dev_mode=true`；缺少 Key 时也会在发出 HTTP 前失败。
- 保存配置不联网；“测试连接”仅在用户主动点击时发送最小文本，不包含截图。
- 划词会把用户选中的文字发送给已配置的文本模型；截图只有在图片上传授权开启时才会发送。
- 日志、测试夹具和 Git 仓库不得包含 API Key、用户截图或原始私人文本。

Provider 配置、迁移与连接测试见 [配置迁移说明](docs/CONFIGURATION_MIGRATION.md)，数据边界见 [隐私说明](docs/PRIVACY.md)。

## 工程原则

- 先实现可测的垂直切片，不为假想场景建立通用框架。
- 抽象只服务真实边界：跨平台 Core、平台服务、模型 Provider、FFI/RPC。
- 一个资源只有一个明确所有者；窗口、托盘、热键、计时器、位图、流和 HTTP 响应均必须可取消并显式释放。
- 所有外部输入都有大小上限、超时和可见错误；不允许无界缓存或无限重试。
- 不静默吞掉影响用户结果的异常；可恢复错误在 UI 中给出下一步。
- 命名、JSON 字段、错误封装和配置入口保持统一；格式与警告由验证脚本强制。
- 安全失败优先于“看起来成功”，尤其是代码 Token 校验和剪贴板恢复。

详细产品范围见 [PRODUCT_SPEC.md](PRODUCT_SPEC.md)，边界与资源规则见 [ARCHITECTURE.md](ARCHITECTURE.md)。
