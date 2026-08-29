# PopGlot 架构

## 目标

PopGlot 首发使用 WPF 获得可靠的 Windows 桌面交互，但 Rust Core 必须能够在没有 WPF、Win32 或 CLR 的环境中编译和测试。未来 Shell 可以替换，识别编排、翻译、模型路由、Token 保护、历史和配置逻辑不重写。

## 当前结构

```text
apps/PopGlot.Windows       WPF Shell、托盘、四快捷键、剪贴板事务、截图、浮窗、控制中心、资料库、服务 Profile
crates/popglot-domain      领域 DTO、自动路由、受保护 Token、Provider Profile 数据模型
crates/popglot-core        配置、应用编排、统一 Provider 与有界 HTTP
crates/popglot-ffi         窄 C ABI；唯一允许裸指针的 Rust crate
scripts                    构建和验证入口
```

`popglot-domain` 和 `popglot-core` 禁止依赖 WPF、Win32、HWND、注册表或 Windows 路径。配置目录由 Shell 传入；API Key 的安全保存由 Windows Shell 实现。

## 契约

当前 C ABI 使用 UTF-8 JSON Envelope：

```json
{
  "ok": true,
  "data": {},
  "error": null
}
```

所有 Rust 返回字符串均由 `popglot_free_string` 释放。C# `CoreBridge.Invoke` 在 `finally` 中完成释放，不把原生指针暴露给 UI。Provider 配置当前为 `schema_version=3`；反序列化经过手工实现的迁移——出网权限（`network_enabled`、`allow_image_upload_in_auto`）缺失时一律按 `false` 处理，旧配置不会自动获得联网或上传图片的权利，其余字段保持默认值回退，无法解析的配置备份为 `provider-settings.corrupt-*.json` 并经 `popglot_take_startup_notice` 通知 Shell，而不是阻断启动。翻译类导出接收显式的源/目标语言参数（可传 null 表示沿用已保存的语言对），因此 UI 上的语言选择会真实影响模型 prompt。

当独立 Core 进程成为真实需求时，同一领域操作可以映射到 Named Pipe/Unix Domain Socket；在此之前不引入后台守护进程、RPC 框架或共享内存。

## 平台端口

Windows Shell 当前已实现剪贴板、截图、快捷键、凭据与本地 OCR 适配；文本注入仍是后续端口：

- `ScreenCapturePort`
- `PlatformOcrPort`
- `GlobalHotkeyPort`
- `ClipboardPort`
- `SecureCredentialPort`
- `TextInjectionPort`

Windows WPF/Win32 实现这些接口；未来 macOS、Linux 重写截图、热键、托盘、浮窗、注入和安全存储。Rust Core 继续复用。

## 统一翻译会话

划词与截图都创建一个 `TranslationPanelWindow` 会话。会话状态固定为读取选区/截图、翻译、完成、失败、取消，不建立事件总线或通用工作流框架。新会话关闭并取消旧会话；全局关闭快捷键和浮窗 `Esc` 走同一释放路径。

划词顺序如下：

```text
capture clipboard snapshot → SendInput(Ctrl+C) → wait ≤ 450 ms
→ validate Unicode text ≤ 64 KiB → restore snapshot if sequence still belongs to PopGlot
→ text Provider → shared result panel
```

若事务期间用户或其他应用产生更新的剪贴板序列号，PopGlot 不覆盖新内容。无法深拷贝某个原始剪贴板格式时，在发送 `Ctrl+C` 前安全拒绝。UIA/hover 不作为 P0 主路径，因为跨浏览器、终端、自绘控件和不同权限窗口的文本模式并不一致。

截图顺序如下：

```text
overlay selection → CopyFromScreen → bounded in-memory PNG
→ configured vision Provider → shared result panel
```

截图不落盘。Windows 本地 OCR 已接入 `Windows.Media.Ocr`，并按所选源语言挑选识别引擎；系统未安装任何语言包时显示真实限制，而不是静默上传或显示假结果。

## 双翻译管线

### Local OCR + Text

```text
CaptureFrame → local OCR → layout normalization → token protection
→ text model → placeholder validation → restoration → result
```

该路线不上传截图。模型遗漏、复制或修改任一占位符时最多严格重试一次；仍失败则展示安全降级结果，不输出被改写的代码。

### Vision Direct

```text
CaptureFrame → image limits/redaction → vision model
→ structured transcription/translation → local token verification → result
```

视觉请求由统一 Provider 契约映射为各家原生图片内容。视觉失败、不支持图片、限流、超时、坏 JSON、安全拦截或 Token 校验失败时回退 Local OCR + Text。代码截图在 OCR 置信度可用时优先本地路线。

### 自动路由

路由由 Core 的 `select_route` 统一裁决，Shell 通过 `popglot_plan_screenshot_route` 询问而不是自行推导，避免两侧规则漂移。路由只使用可解释输入：视觉模型是否可达（含凭据与离线开关）、上传授权、本地 OCR 是否可用、代码概率、复杂布局、图片质量和 OCR 置信度。`RoutingDecision` 返回稳定原因码和中文解释。用户强制选择 `LocalOcr` 时，任何错误都不得触发图片上传。

## Provider 与 HTTP 边界

`TranslationProvider` 只负责能力、请求构造和结构化响应解析；`ProviderClient` 统一负责网络许可、凭据门禁、URL/请求头校验、鉴权、超时、取消、响应上限、有限重试、错误分类和脱敏诊断。

| 类型 | 默认路径 | 图片结构 | 鉴权 |
| --- | --- | --- | --- |
| OpenAI-compatible | `/chat/completions` | `image_url` | `Authorization: Bearer` |
| OpenAI Responses | `/responses` | `input_image.image_url` | `Authorization: Bearer` |
| Anthropic Messages | `/v1/messages` | `image.source` base64 | `x-api-key` + `anthropic-version` |
| Gemini GenerateContent | `/v1beta/models/{model}:generateContent` | `inline_data` | `x-goog-api-key` |

兼容接口并不假设完全兼容：文本与视觉 endpoint、Base URL 和最多 16 个非敏感请求头可配置。普通配置明确拒绝 `Authorization`、Cookie 和各家 API Key 头，秘密只由凭据端口在发送时注入。所有 Provider 返回同一结构化结果 DTO；模型必须保留代码、标识符、路径、命令、URL 和错误码。

当前 Transport 不实现 SSE，因为垂直切片没有消费流式增量的 UI；避免为尚不存在的消费方保留复杂状态。后续接入流式浮窗时在同一契约增加有界事件流。

## 资源、限制与故障策略

当前 Provider 实现持有一个可复用、无 Cookie 的 `reqwest::Client`。请求与响应由异步作用域拥有，取消或超时会释放 future 和连接资源。WPF 位图、Graphics 和编码流使用词法作用域释放。规则如下：

- 单次截图最多 16,000,000 像素，PNG 编码后最多 8 MiB；超过时提示重新框选。
- 同时最多一个前台翻译请求；新会话先取消并关闭旧会话。
- 启动只创建单实例、托盘、主题和一个隐藏热键窗口；主窗口在首次使用时构建，资料库、OCR 状态与服务列表按需刷新。剪贴板等待与重试全部使用异步延迟，不在 UI 线程上 Sleep。
- 图片最多 8 MiB、序列化请求最多 12 MiB、响应正文最多 4 MiB。
- HTTP 连接超时 5 秒、总请求 45 秒；仅对连接/超时以及 408、429、500、502、503、504 重试一次，`Retry-After` 最多等待 2 秒。
- 不建立结果内存缓存；本地历史默认关闭，启用后最多 100 项、90 天、2 MiB，截图位图不入库。
- 截图位图、编码流、HTTP Request/Response、取消源和计时器使用词法作用域或 `Dispose`/`await using`。
- WPF `App` 拥有并释放托盘与热键；浮窗拥有并取消自身生命周期令牌。
- 剪贴板恢复只在序列号仍属于 PopGlot 时执行，不能覆盖用户后续内容。
- 日志只记录请求 ID、阶段、耗时、大小和脱敏错误码，不记录图片、Key 或完整原文。

## 错误模型

核心错误分为配置、捕获、OCR、Provider、超时、取消、响应校验和隐私拒绝。UI 必须区分“用户取消”和“失败”，并为可恢复问题提供明确动作。跨 FFI 的 panic 被转换为失败 Envelope，不能越过 ABI。

## 质量门禁

- `cargo fmt --check`
- Rust workspace tests
- Clippy 全 targets 且 warnings-as-errors
- WPF warnings-as-errors 构建
- 无密钥启动冒烟测试
- 后续 CI 在 Windows/macOS/Linux 编译纯 Rust Core，Windows 另行构建 WPF Shell
- 零依赖 Windows 逻辑测试：剪贴板事务、边界定位、状态、快捷键迁移与历史过滤

## 依赖与协议依据

网络层使用少量、成熟的 Rust 组件：`reqwest`/`tokio`/`tokio-util`、`serde`、`base64`、`futures-util`、`tracing`。它们采用 MIT 或 MIT/Apache-2.0 等与本项目兼容的许可证；准确版本由 `Cargo.lock` 固定，验证命令使用 `--locked`。协议结构依据各家官方 API 文档，不复制第三方 SDK 源码。

- OpenAI Responses：<https://developers.openai.com/api/reference/cli/resources/responses/methods/create>
- OpenAI Chat Completions：<https://developers.openai.com/api/reference/cli/resources/chat/subresources/completions>
- Anthropic Vision/Messages：<https://platform.claude.com/docs/en/build-with-claude/vision>
- Gemini 图片理解/GenerateContent：<https://ai.google.dev/gemini-api/docs/image-understanding>
