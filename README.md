# PopGlot

PopGlot 是一个 Windows-first 的轻量 AI 翻译桌面助手。首要场景是程序员阅读英文报错、代码变量、命令、路径和技术文档；底层同时为普通屏幕翻译和未来 macOS/Linux Shell 保留清晰边界。

当前版本 **0.1.2**。版本号从 0.0.1 起算，按 `docs/VERSIONING.md` 的规则只做增量递增（0.0.2、0.0.3…，大功能批次进 0.1.0）；此前误打的 `v0.3.0` tag 已撤回作废。逐版本变更见 [CHANGELOG.md](CHANGELOG.md)。

当前仓库已经包含可用的 Windows 核心交互：选中文字后按快捷键即可安全读取选区、恢复剪贴板并流式翻译；截图框选会捕获内存 PNG，交由本地 OCR 或（在获得明确授权后）视觉 Provider 处理。划词、截图和手动输入共享统一的低延迟流式浮窗、取消、错误、复制与本地历史模型。开箱即用：未配置密钥时走内置免费引擎；「安全离线模式」可一键切断全部外发请求。

## 下载与运行要求

### 系统要求

- **操作系统**：Windows 10 (版本 19041+) 或 Windows 11 x64
- **运行环境**：无需另行安装 .NET；Windows x64 便携包已自带 .NET 10 Desktop Runtime

### 产物校验（SHA256）

从 GitHub Releases 下载 `PopGlot-v0.1.2-win-x64.zip` 与对应 `PopGlot-v0.1.2-win-x64.zip.sha256` 后，可在 PowerShell 中运行以下命令校验完整性：

```powershell
# 计算下载包 SHA256 哈希
(Get-FileHash -Path .\PopGlot-v0.1.2-win-x64.zip -Algorithm SHA256).Hash.ToLower()

# 对比 sha256 文件内容
Get-Content .\PopGlot-v0.1.2-win-x64.zip.sha256
```

## 当前能力

- **低延迟流式响应与 Stream-Final 双层体验**：模型首批正文到达后立即增量显示，增量阶段轻量文本层平滑跟随；终态无缝切至 Rich Markdown 排版，字号行高平滑过渡无缩水。
- **UI Final Gate 动作防护**：流式阶段及 partial/错误状态严格禁用复制、自动复制、生词收藏、TTS 朗读与本地历史写入，仅在收到合法完整终态结果时触发，绝不自动落盘不完整内容。
- **四大 Provider 统一原生流式**：原生支持 OpenAI-compatible（Chat Completions）、OpenAI Responses、Anthropic Messages、Gemini GenerateContent 四大主流协议，含常用预设并支持自定义中转。
- **智能模型推荐偏好**：提供 `Speed`（极速）、`Balanced`（均衡）、`Quality`（高质）推荐偏好，结合 Provider 目录事实、模型家族启发式规则与本地实测基准提供科学推荐；未知模型明确标注未知而不虚构能力，健康检测结果仅供参考而不作为保存或使用的阻断门控。
- **Windows 托盘常驻与无感热键**：默认 `Ctrl+Alt+W` 划词、`Ctrl+Alt+Space` 截图、`Ctrl+Alt+X` 关闭浮窗、`Ctrl+Alt+O` 打开主窗口。快捷键可自由录制，任意 `Ctrl/Alt/Win` 组合都可用，设置页可查看并修改全部四组。
- **冷启动与延迟加载**：启动只创建托盘、主题与隐藏热键窗口；主窗口在首次使用时才构建，冷启动尽快到达可用托盘。
- **剪贴板安全事务**：划词使用有界剪贴板事务模拟 `Ctrl+C`；复制成功、失败或取消都会按序列号规则恢复，且不会覆盖用户随后复制的新内容。
- **多显示器与像素对齐**：多显示器选区遮罩、真实内存截图与统一结果浮窗；全流程按物理像素定位，混合 DPI 与副屏下浮窗和选区均精准对齐；`Esc` 先取消流式请求、再关闭浮窗。
- **16 种语言与智能路线**：浮窗与设置页共用统一语言表；`Auto / LocalOcr / VisionDirect` 三种截图线路由 Rust Core 统一裁决，设置页实时显示"当前实际线路"及原因。
- **Windows 内置离线 OCR**：已接入系统 OCR，按源语言挑选识别引擎；未安装语言包时给出明确指引。
- **API Key 安全存储**：凭据仅保存至 Windows Credential Manager，不写普通 JSON 或日志，各服务拥有独立凭据槽位。
- **程序员 Token 保护**：异常名、标识符、路径、URL、命令参数等在翻译前遮蔽、翻译后逐字节还原；弱标识符规则只在文本具备代码特征时启用，纯散文不误伤。
- **设置体验优化与主题高对比度**：主窗口侧栏底部新增同级「设置」入口，设置页默认直达「翻译引擎」；Dirty 状态纯值比较且改回原值自动恢复 Clean；深浅主题文本与关键边框全面通过 WCAG 2.1 AA 对比度审计。
- **安全离线模式与出网控制**：安全离线模式是一键切断全部外发的总开关；「测试连接」使用内存草稿，不保存设置、不改动已存凭据、绝不上传截图。
- **本地历史安全管理**：本地 JSON 历史最多 200 条/90 天/4 MiB，可搜索、删除、载回翻译页；疑似密钥、过大内容和截图位图绝不记录。

当前限制：真正的跨应用鼠标 hover 取词尚未实现——UI Automation 并不被所有浏览器、编辑器、终端和管理员窗口一致支持。四种协议通过本机 mock 验证，仓库测试没有使用真实 API Key 做云端调用。

## 使用

首次运行无需任何配置：不填 API Key 时自动使用内置免费引擎，但**首次使用前需要在「设置 → 隐私与数据」中授权一次**——内置免费引擎会把待翻译文本发送到 Google 公共翻译服务，不发送截图或凭据；未授权时不会发出任何请求，也不会静默改用其他在线服务，翻译会返回指向该设置的提示。授权状态可随时更改。

主窗口是纯工作台：**翻译**（左右对照的双栏输入/流式译文）与**资料库**（历史与生词的 Master–Detail）。全部设置位于独立设置窗口：**通用**、**服务**、**快捷键**、**隐私与数据**。服务页左侧展示已配置服务列表，右侧编辑器支持填写 API Key、选择模型、测试连接并实时获取推荐；出厂 Provider 模板只出现在「添加服务」流程。保存与启用分离：第一个服务保存即启用，之后需显式「设为文字默认」。

1. 在任意支持复制的应用中选中文字，按 `Ctrl+Alt+W`，译文将实时流式渲染呈现。
2. 或按 `Ctrl+Alt+Space` 框选屏幕区域。`Esc` 取消，`Ctrl+Alt+X` 关闭浮窗。
3. 想用自己的模型：在「设置 → 服务」添加服务（选预设或自定义），填模型名与 API Key，保存后设为默认；保持「启用大模型网络翻译」开启且「安全离线模式」关闭。
4. 截图要走视觉模型，还需在「隐私与数据」中开启图片上传授权。

密码框、禁止复制的界面、不同权限级别窗口可能无法读取选区；PopGlot 会显示失败状态，不绕过 Windows 安全边界。交互依据与取舍见 [UX 决策](docs/UX_DECISIONS.md)。

## 基准评测命令（Benchmark）

PopGlot 提供了完整的流式性能与延迟容忍度离线评测工具，以及带严格安全门控的在线 Provider 评测工具：

```powershell
# 1. 运行默认离线流式基准测试（本地 loopback HTTP 模拟真实 SSE 流）
cargo run -p popglot-core --bin stream_benchmark --

# 2. 运行指定场景、Provider 协议与延迟参数的离线基准测试
cargo run -p popglot-core --bin stream_benchmark -- --scenario split-utf8 --provider anthropic --iterations 20 --ttft-ms 25 --chunk-interval-ms 5

# 3. 运行全量离线场景并校验延迟容忍度门限
cargo run -p popglot-core --bin stream_benchmark -- --scenario all --validate --tolerance-ms 40

# 4. 在线 Provider 真实评测（要求环境变量注入 Key + 双重安全开关确认；缺省时默认 Dry-Run 模式并 exit code 2 退出）
$env:POPGLOT_BENCHMARK_API_KEY = "your-api-key"
cargo run --example live_provider_bench -- --live --i-understand-cost --subset minimal
```

详细指标定义、Prompt Fixtures 与安全约束见 [TRANSLATION_BENCHMARK.md](docs/TRANSLATION_BENCHMARK.md)。

## 开发环境

- Windows 11 x64
- Rust stable，目标 `x86_64-pc-windows-msvc`
- Visual Studio Build Tools 2022：MSVC x64/x86 与 Windows 11 SDK
- .NET 10 SDK x64
- PowerShell 7（脚本也尽量兼容 Windows PowerShell）

本机验证版本：Rust 1.98.0、Cargo 1.98.0、rustfmt 1.9.0、Clippy 0.1.98、.NET SDK 10.0.400、MSVC 14.44、Windows SDK 10.0.26100.0。

## 构建与运行

```powershell
# 完整验证（包含 Rust 检查、WPF 构建与全量 113 项 Windows 逻辑测试）
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

非秘密设置位于 `%LOCALAPPDATA%\PopGlot`（服务配置在 `product-config.json`，schema v6；核心设置在 `provider-settings.json`，schema v3）。API Key 使用 Windows Credential Manager，且**每个服务有独立凭据项**（`PopGlot/provider/<id>`）；旧的通用凭据项 `PopGlot/OpenAICompatibleApiKey` 为兼容初始版本保留，代表"当前活动 Provider 的 Key"。

- `LocalOcr` 模式的产品契约是永不上传截图。
- `Auto` 只有在用户明确允许、视觉模型已配置且路由认为必要时才能上传截图。
- `VisionDirect` 失败后必须安全回退到本地 OCR + 文本模型。
- `safe_dev_mode` 是总开关，覆盖包括内置免费引擎在内的一切外发请求。
- `network_enabled` 关闭后模型请求在发出 HTTP 前失败，内置免费引擎同样被拒绝；只有本地模型（Ollama / LM Studio 等）地址仍可工作。
- 保存配置不联网；「测试连接」仅在用户主动点击时发送最小文本到内存中的草稿配置，不保存、不覆盖凭据、不含截图。
- 流式增量阶段不落盘，未完成或错误中断的 partial 译文绝不写入历史、绝不自动复制或自动朗读。
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
