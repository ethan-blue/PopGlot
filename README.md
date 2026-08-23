# PopGlot

PopGlot 是一个 Windows-first 的轻量 AI 翻译桌面助手。首要场景是程序员阅读英文报错、代码变量、命令、路径和技术文档；底层同时为普通屏幕翻译和未来 macOS/Linux Shell 保留清晰边界。

当前仓库是可运行的初始垂直切片：它包含托盘、全局快捷键、跨屏选区、无边框状态浮窗、Provider 设置、Windows 凭据存储，以及由 Rust Core 驱动的双管线路由演示。**本版本没有网络传输实现，不会发送 API 请求或用户截图。**

## 当前能力

- Windows 托盘常驻；默认 `Ctrl+Alt+Space`，支持三档可持久化快捷键。
- 多显示器选区遮罩；`Esc` 取消，拖动完成后显示结果浮窗。
- `Auto / LocalOcr / VisionDirect` 三种翻译模式和可解释路由。
- OpenAI-compatible Base URL、文本模型、视觉模型配置模型。
- API Key 只保存到 Windows Credential Manager，不写普通 JSON。
- 程序员 Token 保护基础：异常名、标识符、路径、URL、命令参数等。
- Rust Core 与 WPF Shell 通过小型 C ABI/JSON 契约连接。
- Safe Dev Mode 预览：即使填写模型，也不会真实联网。

尚未实现真实截图位图捕获、本地 OCR、HTTP/SSE 传输、视觉请求和翻译历史；这些属于下一阶段，不应把当前演示误认为完整翻译器。

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
```

WPF 项目构建时会自动构建 `popglot-ffi` 并将 `popglot_ffi.dll` 复制到输出目录。

## 配置与隐私

非秘密设置位于 `%LOCALAPPDATA%\PopGlot`。API Key 使用 Windows Credential Manager 的通用凭据项 `PopGlot/OpenAICompatibleApiKey`。

- `LocalOcr` 模式的产品契约是永不上传截图。
- `Auto` 只有在用户明确允许、视觉模型已配置且路由认为必要时才能上传截图。
- `VisionDirect` 失败后必须安全回退到本地 OCR + 文本模型。
- 首个垂直切片没有 HTTP Transport，所有预览均为本地确定性结果。
- 日志、测试夹具和 Git 仓库不得包含 API Key、用户截图或原始私人文本。

## 工程原则

- 先实现可测的垂直切片，不为假想场景建立通用框架。
- 抽象只服务真实边界：跨平台 Core、平台服务、模型 Provider、FFI/RPC。
- 一个资源只有一个明确所有者；窗口、托盘、热键、计时器、位图、流和 HTTP 响应均必须可取消并显式释放。
- 所有外部输入都有大小上限、超时和可见错误；不允许无界缓存或无限重试。
- 不静默吞掉影响用户结果的异常；可恢复错误在 UI 中给出下一步。
- 命名、JSON 字段、错误封装和配置入口保持统一；格式与警告由验证脚本强制。
- 安全失败优先于“看起来成功”，尤其是代码 Token 校验和剪贴板恢复。

详细产品范围见 [PRODUCT_SPEC.md](PRODUCT_SPEC.md)，边界与资源规则见 [ARCHITECTURE.md](ARCHITECTURE.md)。
