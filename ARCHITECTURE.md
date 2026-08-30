# PopGlot 架构

## 目标

PopGlot 首发使用 WPF 获得可靠的 Windows 桌面交互，但 Rust Core 必须能够在没有 WPF、Win32 或 CLR 的环境中编译和测试。未来 Shell 可以替换，识别编排、翻译、模型路由、Token 保护、流式传输、历史和配置逻辑不重写。

## 当前结构

```text
apps/PopGlot.Windows       WPF Shell、托盘、四快捷键、剪贴板事务、截图、流式浮窗、控制中心、资料库、服务 Profile、Coordinator
crates/popglot-domain      领域 DTO、自动路由、受保护 Token、Provider Profile 数据模型、流式协议
crates/popglot-core        配置、应用编排、统一 Provider、SSE 解析、Text-first 组装、基准评测与有界 HTTP
crates/popglot-ffi         窄 C ABI、流式 Callback 桥接；唯一允许裸指针的 Rust crate
scripts                    构建和验证入口
```

`popglot-domain` 和 `popglot-core` 禁止依赖 WPF、Win32、HWND、注册表或 Windows 路径。配置目录由 Shell 传入；API Key 的安全保存由 Windows Shell 实现。

## 契约与 C ABI

当前 C ABI 包含单次同步调用的 UTF-8 JSON Envelope 与增量流式调用的 Callback 机制：

```json
{
  "ok": true,
  "data": {},
  "error": null
}
```

### 流式 C ABI 与 Callback 契约

流式接口导出声明为：
- `popglot_translate_text_stream(..., callback, user_data)`
- `popglot_translate_vision_stream(..., callback, user_data)`
- `popglot_test_connection_stream(..., callback, user_data)`

回调函数签名遵循 C ABI：
```c
int32_t (*PopGlotStreamCallback)(const uint8_t* delta_ptr, size_t delta_len, void* user_data);
```
- **Delta 传递**：Rust 侧将已解码的有效 UTF-8 增量切片以非空指针和字节长度传给宿主；
- **主动 Abort 控制**：宿主回调返回 `0` 表示继续接收流，返回非零值（如 `1`）时 Rust 立即中止当前 HTTP 流并释放连接；
- **生命周期与 Panic 隔离**：C# 侧使用 `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]` 静态 thunk 与 `GCHandle` 保持委托生命周期；Rust 侧导出函数由 `catch_unwind` 包裹，并在 RAII 请求票据（Ticket）析构中自动清理取消注册表，杜绝裸指针与原生 panic 越过 ABI 边界。
- **终态 Envelope**：流式结束后返回完整的 JSON Envelope，统一由 `popglot_free_string` 释放。

所有 Rust 返回字符串均由 `popglot_free_string` 释放。C# `CoreBridge.Invoke` 与流式包装在 `finally` 中完成释放，不把原生指针暴露给 UI。

## Text-first 流式协议与 SSE 解析

### 1. Incremental SSE 解析器（`SseDecoder`）
Core 内置无外部巨型框架依赖的增量 SSE 解析器：
- 逐字节流式扫描行切分（`\n` / `\r\n`）；
- 防御性有界控制：单行最大限制与单事件最大限制（默认 256 KiB），超限安全报错阻断；
- 跨 Chunk UTF-8 边界拼接：自动处理多字节 UTF-8 字符（如中文 3 字节、Emoji 4 字节）被 TCP 或 HTTP Chunk 截断在边界的情况，确保吐出的每个 delta 均为完整合法字符。

### 2. Text-first + 随机 Trailer 分隔符协议
为了达成极致的首字响应时间（TTFT，Time to First Token）并兼顾结构化元数据（保留术语、语法解释、建议操作），PopGlot 采用 **Text-first + 随机 Trailer** 设计：
- **首 Token 零延迟直出**：模型 Prompt 约束先流式输出纯译文正文，无需在开头输出任何 JSON 包装；
- **防冲突随机 Trailer 分隔符**：每次请求由客户端在 Prompt 中注入随机生成的防冲突分隔符（例如 `PGMETA_xxxxxxxx_DELIMITER`），模型在正文结束后输出该分隔符并在 Trailer 附带紧凑 JSON 元数据；
- **流式流尾解析与优雅保全**：`TextFirstAssembler` 实时检测分隔符边界；收到分隔符后将后续内容作为元数据解析，若模型未输出分隔符、格式异常或网络早闭，流式组件保全全部已接收的正文内容，平滑降级为无额外解释的成功结果，绝不丢失翻译正文。

```text
[HTTP SSE Chunks]
  → SseDecoder (跨 chunk UTF-8 拼接)
  → ProviderStreamEvent 解析 (OpenAI / Anthropic / Gemini)
  → TextFirstAssembler (实时剥离 Text Delta → FFI Callback)
  → Trailer 识别与解析 (保留术语 / 语法解释 / 建议操作)
  → 组装最终 TranslationResult DTO
```

## C# 流式缓冲协调器与 UI Final Gate

```text
FFI Callback (C ABI Native Thread)
  → TranslationStreamBuffer (O(1) 短锁、无 UI 阻塞、防抖缓冲)
  → TranslationCoordinator (40ms 定时 Pump、Epoch 防串台、生命周期管理)
  → UI Stream-Final 双层渲染 (增量文本层 → Rich Markdown 终态层)
  → UI Final Gate (动作门禁：自动复制 / TTS / 收藏 / 历史写入仅在 Final 触发)
```

### 1. `TranslationStreamBuffer`（短锁缓冲）
- 专为高频 C ABI 回调设计，`AppendDelta` 仅进行毫秒级短锁入队与字节统计，绝不调用 UI Dispatcher，绝不进行文件/网络 I/O；
- 具备硬上限截断保护，防止异常超大响应耗尽内存；
- 提供 `DrainAvailable` 与 `DrainFinal`，支持端到端 TTFT 与增量计数度量。

### 2. `TranslationCoordinator`（会话协调与 Epoch 隔离）
- 统一协调 Connecting、Streaming、Finalizing、Completed、Failed、Cancelled 状态机；
- 采用 40ms 节拍定时 Pump 增量至 UI 线程，消除逐 token 刷新导致的 UI 频繁重排与卡顿；
- **Epoch 隔离防护**：每次发起新翻译递增全局 Epoch。所有异步回调、流式 delta pump 与终态消息均比对 Epoch，过期或已被覆盖的旧请求数据自动丢弃，杜绝快节奏输入或连续划词时的“串台”竞争；
- 终态校准与 Partial 保全：若流被取消或异常中断，Coordinator 留存已接收的 Partial 文本并展示部分结果提示，同时禁止发起无意义的二次错误重试。

### 3. UI Stream-Final 双层渲染与 UI Final Gate
- **双层平滑渲染**：首 delta 到达时，TranslateSection、TranslationPanelWindow 与 QuickSearchWindow 切换至轻量只读文本流式层，支持长文本自动跟随滚动；收到终态 Envelope 后无缝切换为 Rich Markdown 呈现层。两层严格统一字号（15px）与行高（22px），消除完成瞬间的跳动与缩水；
- **UI Final Gate 动作门禁**：
  - 流式增量阶段以及 partial/未完成状态下，严格禁用自动复制到剪贴板、TTS 语音朗读、生词收藏与本地历史写入；
  - 只有收到包含完整 Token 校验的最终合法 Envelope 时，Final Gate 才允许执行上述动作；
  - 历史记录在整个会话生命周期中严格保证**单次且仅在最终态落盘**。

## 双翻译管线

### Local OCR + Text（支持文本流式）

```text
CaptureFrame → local OCR → layout normalization → token protection
→ text stream Provider → SSE / TextFirst → stream deltas → restoration → result
```

该路线不上传截图。模型遗漏、复制或修改任一占位符时最多严格重试一次；仍失败则展示安全降级结果，不输出被改写的代码。

### Vision Direct（支持视觉流式）

```text
CaptureFrame → image limits/redaction → vision stream model
→ SSE / TextFirst → stream deltas → local token verification → result
```

视觉请求由统一 Provider 契约映射为各家原生流式图片内容。视觉直译若在**零可见 Delta** 时遇到能力错误、不支持图片、限流、超时或坏 JSON，自动安全回退至 Local OCR + Text 流式管线；若已流出部分 Delta，则不发起二次网络请求，保留 Partial 并提示用户。

### 自动路由

路由由 Core 的 `select_route` 统一裁决，Shell 通过 `popglot_plan_screenshot_route` 询问而不是自行推导，避免两侧规则漂移。路由只使用可解释输入：视觉模型是否可达（含凭据与离线开关）、上传授权、本地 OCR 是否可用、代码概率、复杂布局、图片质量和 OCR 置信度。`RoutingDecision` 返回稳定原因码和中文解释。用户强制选择 `LocalOcr` 时，任何错误都不得触发图片上传。

## Provider 与 HTTP 边界

`TranslationProvider` 统一实现非流式与流式（`translate_stream`）契约；`ProviderClient` 统一负责网络许可、凭据门禁、URL/请求头校验、鉴权、超时、取消、响应上限、有限重试、错误分类和脱敏诊断。

| 类型 | 默认路径 | 图片结构 | 鉴权 | 流式传输 |
| --- | --- | --- | --- | --- |
| OpenAI-compatible | `/chat/completions` | `image_url` | `Authorization: Bearer` | SSE (`stream: true`) |
| OpenAI Responses | `/responses` | `input_image.image_url` | `Authorization: Bearer` | SSE (`stream: true`) |
| Anthropic Messages | `/v1/messages` | `image.source` base64 | `x-api-key` + `anthropic-version` | SSE (`stream: true`) |
| Gemini GenerateContent | `/v1beta/models/{model}:streamGenerateContent?alt=sse` | `inline_data` | `x-goog-api-key` | SSE (`alt=sse`) |

兼容接口并不假设完全兼容：文本与视觉 endpoint、Base URL 和最多 16 个非敏感请求头可配置。普通配置明确拒绝 `Authorization`、Cookie 和各家 API Key 头，秘密只由凭据端口在发送时注入。所有 Provider 返回同一结构化结果 DTO；模型必须保留代码、标识符、路径、命令、URL 和错误码。

## 资源、限制与故障策略

当前 Provider 实现持有一个可复用、无 Cookie 的 `reqwest::Client`。请求与响应由异步作用域拥有，取消或超时会释放 future 和连接资源。WPF 位图、Graphics 和编码流使用词法作用域释放。规则如下：

- 单次截图最多 16,000,000 像素，PNG 编码后最多 8 MiB；超过时提示重新框选。
- 同时最多一个前台翻译请求；新会话先取消并关闭旧会话。
- 启动只创建单实例、托盘、主题和一个隐藏热键窗口；主窗口在首次使用时构建，资料库、OCR 状态与服务列表按需刷新。剪贴板等待与重试全部使用异步延迟，不在 UI 线程上 Sleep。
- 图片最多 8 MiB、序列化请求最多 12 MiB、流式单事件最大 256 KiB、完整响应正文最多 4 MiB。
- HTTP 连接超时 5 秒、总请求 45 秒；仅对连接/超时以及 408、429、500、502、503、504 重试一次，`Retry-After` 最多等待 2 秒。
- 本地历史默认开启（`HistoryEnabled = true`），最多 200 条、90 天、4 MiB，截图位图不入库，partial 状态绝不入库。
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
- 零依赖 Windows 逻辑测试：113 项逻辑测试覆盖剪贴板事务、边界定位、状态机、快捷键迁移、历史过滤、流式管道、推荐服务与主题对比度。

## 依赖与协议依据

网络层使用少量、成熟的 Rust 组件：`reqwest`/`tokio`/`tokio-util`、`serde`、`base64`、`futures-util`、`tracing`、`regex`、`getrandom`。它们采用 MIT 或 MIT/Apache-2.0 等与本项目兼容的许可证；准确版本由 `Cargo.lock` 固定，验证命令使用 `--locked`。协议结构依据各家官方 API 文档，不复制第三方 SDK 源码。

- OpenAI Responses：<https://developers.openai.com/api/reference/cli/resources/responses/methods/create>
- OpenAI Chat Completions：<https://developers.openai.com/api/reference/cli/resources/chat/subresources/completions>
- Anthropic Vision/Messages：<https://platform.claude.com/docs/en/build-with-claude/vision>
- Gemini 图片理解/GenerateContent：<https://ai.google.dev/gemini-api/docs/image-understanding>
