# PopGlot

PopGlot 是一个 Windows-first 的轻量 AI 翻译桌面助手。首要场景是程序员与专业用户阅读英文报错、代码注释、变量、命令、路径和技术文档；底层同时为普通屏幕翻译和未来跨平台 Shell 保留清晰边界。

当前版本 **0.1.5**。版本号从 0.0.1 起算，按 `docs/VERSIONING.md` 的规则只做增量递增（0.0.2、0.0.3…，大功能批次进 0.1.0）；此前误打的 `v0.3.0` tag 已撤回作废。**0.1.6 候选改动**（翻译风格与提示词模板闭环、会话暂存接续等）已在主干工作树实现并通过全量验证，但版本号尚未提升、未发布；未发布内容在 [CHANGELOG.md](CHANGELOG.md) 的「未发布（Unreleased）」小节单独记录，不冒充已发布。

当前仓库已经包含完整的 Windows 核心交互：选中文字后按快捷键即可安全读取选区、恢复剪贴板并流式翻译；截图框选会捕获内存 PNG，交由本地离线 OCR 或（在获得明确授权后）视觉翻译引擎处理。划词、截图、极速查词和手动工作台共享统一的低延迟流式响应、取消、错误自愈、复制与本地历史模型。开箱即用：未配置密钥时可使用内置公共翻译；开启「安全离线模式」可一键切断全部外发请求。

---

## 离线帮助与文档中心

PopGlot 提供了完整的本地离线帮助文档（无需联网即可查阅）：
- [离线帮助文档中心 (Help Center)](docs/help/index.md)
- [快速上手指南 (Getting Started)](docs/help/getting-started.md)
- [隐私边界与数据安全 (Privacy & Security)](docs/help/privacy-and-security.md)
- [快捷键全景与冲突排查 (Keyboard Shortcuts)](docs/help/keyboard-shortcuts.md)
- [翻译引擎配置指引 (Provider Setup)](docs/help/provider-setup/index.md)
- [常见故障与自愈诊断 (Troubleshooting)](docs/help/troubleshooting.md)

---

## 下载与运行要求

### 系统要求

- **操作系统**：Windows 10 (版本 19041+) 或 Windows 11 x64
- **运行环境**：无需另行安装 .NET；Windows x64 便携包已自带 .NET 10 Desktop Runtime

### 产物校验（SHA256）

从 GitHub Releases 下载 `PopGlot-v0.1.5-win-x64.zip` 与对应 `PopGlot-v0.1.5-win-x64.zip.sha256` 后，可在 PowerShell 中运行以下命令校验完整性：

```powershell
# 计算下载包 SHA256 哈希
(Get-FileHash -Path .\PopGlot-v0.1.5-win-x64.zip -Algorithm SHA256).Hash.ToLower()

# 对比 sha256 文件内容
Get-Content .\PopGlot-v0.1.5-win-x64.zip.sha256
```

---

## 当前核心能力

- **低延迟流式响应与平滑渲染**：模型首批正文到达后立即增量显示，流式阶段 60~80ms 智能节流防抖，消除布局颠簸；终态无缝切至 Rich Markdown 排版，字号行高平滑过渡无缩水。
- **UI Final Gate 动作防护**：流式阶段及 partial/错误状态严格禁用自动复制、生词收藏、TTS 自动朗读与本地历史写入；允许用户手动复制已有片段（Partial Copy），仅在收到合法完整终态结果时触发自动化动作。
- **四大翻译引擎协议原生支持**：原生支持 OpenAI-compatible（Chat Completions）、OpenAI Responses、Anthropic Messages、Gemini GenerateContent 四大主流通信协议，含常用预设并支持自定义本地私有服务（Ollama / LM Studio / vLLM）。
- **智能模型推荐偏好**：提供 `Speed`（极速）、`Balanced`（均衡）、`Quality`（高质）推荐偏好，结合服务商目录事实与本地基准推荐最佳模型；未知模型诚实标注为 Unknown，健康检测结果仅供参考而不作为保存阻断门控。
- **翻译风格与提示词模板（0.1.6 候选，未发布）**：内置「忠实 / 自然 / 正式」三种风格，支持自定义提示词模板（仅存本机、自动保留修订历史）；四变量白名单纯编译器，编译预览全程本地零网络；风格偏好仅作用于语气与用词，永不覆盖代码保护与格式；设置窗口「翻译与提示词」页与主窗口/极速查词均可管理和快速切换。
- **会话暂存与接续（0.1.6 候选，未发布）**：浮窗与极速查词关闭时自动暂存最近会话（最多 5 条 / 2 MiB / 30 分钟），恢复零重发；清空历史时同步清空暂存仓。
- **多端响应式与自适应布局**：
  - **主工作台响应式断点**：`<720 DIP` 自动折叠侧栏为 48 DIP 紧凑图标模式并转为垂直堆叠阅读；`720–959 DIP` 为标准双栏工作台；`≥960 DIP` 开启宽屏双栏阅读增强（1:1.25 配额）；
  - **设置窗口单列自适应**：在 `<700 DIP` 窄窗口下自动收拢为单列垂直堆叠布局，杜绝表单与按钮拥挤裁切。
- **可拖拽分栏与虚拟化渲染**：
  - 资料库（`LibrarySection`）支持 `GridSplitter` 自由拖动调整列表与详情列宽；
  - 条目删除后自动选中邻近项，消除关闭详情闪烁；
  - 启用 UI 虚拟化（VirtualizingStackPanel）与 ICollectionView 差量过滤，上千条历史极速加载零卡顿。
- **自愈型错误反馈与修复路径**：
  - 彻底终结报错「死胡同」：无可用引擎时，工作台与极速查词就地展示引导卡片并提供「添加翻译引擎」一键跳转；
  - 翻译失败去除全屏通红，保留正文色并提供「打开设置」修复通道；
  - 生词本损坏只读保护：当数据文件异常时自动锁定只读保护，展示原因并提供「重试加载」，绝不覆写清空用户资产。
- **高对比度无障碍支持**：
  - 深度接入 Windows `SystemParameters.HighContrast` 高对比度系统模式；
  - 动态使用系统高对比色覆盖核心画刷（WindowText / Highlight），自动跳过阴影装饰，保障视力障碍用户清晰阅读。
- **UI 主线程全异步 I/O**：
  - 生词本收藏、历史记录读写全部迁移至单写者后台队列串行写盘，点击收藏主线程 0ms 阻塞；
  - 服务切换存盘、开机自启注册表操作全部改为后台异步任务，配合防重入锁，操作丝滑无卡顿。
- **Windows 托盘常驻与无感快捷键**：
  - 默认 `Ctrl+Alt+W` 划词、`Ctrl+Alt+Space` 截图、`Ctrl+Alt+X` 关闭浮窗、`Ctrl+Alt+O` 打开主窗口；
  - 支持键盘录制任意包含 `Ctrl/Alt/Win` 的修饰键组合，发生系统占用冲突时具备原子回滚保护；
  - 浮窗内两段式 `Esc` 阶梯响应（首击取消网络请求，次击关闭浮窗）。
- **极速查词与多屏 DPI 像素对齐**：
  - 极速查词居中当前光标所在屏幕工作区，输入 150ms 自动防抖；
  - 悬浮球物理像素与 DIP 换算纠偏并增加屏幕四边夹逼（不出屏）；
  - 浮窗标题栏按钮热区达标（≥32 DIP），被用户拖动后停止自动追随光标。
- **剪贴板安全事务**：划词使用有界剪贴板事务模拟 `Ctrl+C`；复制成功、失败或取消都会按序列号规则恢复，且不会覆盖用户随后复制的新内容。
- **Windows 内置离线 OCR**：已接入系统原生 OCR，识别完全在本地完成；强制本地模式（`LocalOcr`）绝不上传任何截图图片。
- **API Key 安全凭据保险箱**：凭据由 Windows Credential Manager（凭据管理器）硬件级保护，不写入普通 JSON 或日志。
- **统一规范的用户文案**：全应用文案规范统一为「翻译引擎」与「翻译偏好」，彻底消除旧版专有名词混杂。
- **安全离线模式与出网控制**：安全离线模式是一键切断全部外发的总开关；「测试连接」使用内存草稿，不保存设置、不改动已存凭据、绝不上传截图。
- **本地历史安全管理**：本地 JSON 历史最多 200 条/90 天/4 MiB，可搜索、删除、载回翻译页；疑似密钥、过大内容和截图位图绝不记录。

---

## 快速使用说明

首次运行无需任何配置：不填 API Key 时可使用内置公共翻译，**首次使用前需要在「设置 → 隐私与数据」中授权一次**——内置公共翻译会把待翻译文本发送到 Google 公共翻译服务，不发送截图或凭据；未授权时不会发出任何请求。

主窗口是纯工作台：**翻译**（左右对照的双栏输入/流式译文）与**资料库**（历史与生词的 Master–Detail）。全部设置位于独立设置窗口：**翻译引擎**、**通用**、**快捷键**、**隐私与数据**。

1. **划词翻译**：在任意应用中选中文字，按 `Ctrl+Alt+W`，浮窗将贴近光标实时流式展现译文；
2. **截图翻译**：按 `Ctrl+Alt+Space` 框选屏幕区域；按住 `Shift` 截图可直接提取 OCR 纯文本到剪贴板；`Esc` 退出；
3. **连接自己的大模型**：在「设置 → 翻译引擎」点击「添加引擎」，填入 API Key 与 Base URL，验证连接并设为默认；
4. **截图使用视觉模型**：若需使用视觉多模态大模型识别截图，需在「设置 → 隐私与数据」中开启图片上传授权。

---

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

---

## 开发环境

- Windows 11 x64
- Rust stable，目标 `x86_64-pc-windows-msvc`
- Visual Studio Build Tools 2022：MSVC x64/x86 与 Windows 11 SDK
- .NET 10 SDK x64
- PowerShell 7（脚本也尽量兼容 Windows PowerShell）

本机验证版本：Rust 1.98.0、Cargo 1.98.0、rustfmt 1.9.0、Clippy 0.1.98、.NET SDK 10.0.400、MSVC 14.44、Windows SDK 10.0.26100.0。

## 构建与运行

```powershell
# 完整验证（Rust 检查、WPF 构建、纯逻辑 PureTests 与全量 Windows 逻辑 LogicTests）
./scripts/verify.ps1

# 运行托盘应用
./scripts/run.ps1
```

也可以分别执行：

```powershell
cargo test --workspace --locked
cargo clippy --workspace --all-targets --locked -- -D warnings
dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj
dotnet run --project tests/PopGlot.Windows.PureTests/PopGlot.Windows.PureTests.csproj
dotnet run --project tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj
```

WPF 项目构建时会自动构建 `popglot-ffi` 并将 `popglot_ffi.dll` 复制到输出目录。

---

## 配置与隐私

非秘密设置位于 `%LOCALAPPDATA%\PopGlot`（服务配置在 `product-config.json`，schema v7；核心设置在 `provider-settings.json`，schema v3）。0.1.6 候选起，翻译风格模板另存于同目录的 `prompt-templates.json`（仅本机、独立于上述两份配置）。API Key 使用 Windows Credential Manager，且**每个翻译引擎有独立凭据项**（`PopGlot/provider/<id>`）。

- `LocalOcr` 模式的产品契约是永不上传截图；
- `Auto` 只有在用户明确允许、视觉模型已配置且路由认为必要时才能上传截图；
- `VisionDirect` 是显式用户选择：所选视觉服务不可执行时明确阻断并给出原因，绝不静默改走其他线路；
- `safe_dev_mode` 是总开关，覆盖包括内置免费引擎在内的一切外发请求；
- `network_enabled` 关闭后模型请求在发出 HTTP 前失败，内置免费引擎同样被拒绝；只有本地模型（Ollama / LM Studio 等）地址仍可工作；
- 保存配置不联网；「测试连接」仅在用户主动点击时发送最小文本到内存中的草稿配置，不保存、不覆盖凭据、不含截图；
- 流式增量阶段不落盘，未完成或错误中断的 partial 译文绝不写入历史、绝不自动复制或自动朗读；
- 日志、测试夹具和 Git 仓库不得包含 API Key、用户截图或原始私人文本。

更多安全细节见 [隐私与数据安全说明](docs/help/privacy-and-security.md) 与 [docs/PRIVACY.md](docs/PRIVACY.md)。

---

## 工程原则

- 先实现可测的垂直切片，不为假想场景建立通用框架；
- 抽象只服务真实边界：跨平台 Core、平台服务、模型 Provider、FFI/RPC；
- 一个资源只有一个明确所有者；窗口、托盘、热键、计时器、位图、流和 HTTP 响应均必须可取消并显式释放；
- 所有外部输入都有大小上限、超时和可见错误；不允许无界缓存或无限重试；
- 不静默吞掉影响用户结果的异常；可恢复错误在 UI 中给出下一步；
- 命名、JSON 字段、错误封装和配置入口保持统一；格式与警告由验证脚本强制；
- 安全失败优先于“看起来成功”，尤其是代码 Token 校验和剪贴板恢复。

详细产品范围见 [PRODUCT_SPEC.md](PRODUCT_SPEC.md)，边界与资源规则见 [ARCHITECTURE.md](ARCHITECTURE.md)。
