# PopGlot 产品规格

## 产品定位

PopGlot 是 Windows-first 的轻量 AI 翻译桌面助手。它以全局快捷键完成划词与截图翻译，面向英语不熟且频繁阅读技术内容的用户。程序员场景优先，普通软件和屏幕翻译使用同一会话、状态与结果体系。

## P0 用户流程

1. 应用启动后进入托盘，不弹主窗口。
2. 用户选中文字并按划词快捷键，或按截图快捷键进入跨显示器选区。
3. 划词事务恢复原剪贴板；浮窗立即展示“读取、连接、流式翻译、完成/失败”状态。
4. 首个 Token 毫秒级直接流式呈现；流式增量期间呈现只读纯文本，终态无缝平滑切至 Rich Markdown 渲染。
5. 自动模式解释为什么选择本地 OCR 或视觉直译。
6. 结果按“中文翻译、保留术语、语义解释、可能原因、建议动作”分层。
7. 用户可以复制、固定、重试或关闭；`Esc` 随时取消。

## 流式状态与生命周期

翻译会话遵循严格的生命周期状态机：

```text
Idle → Connecting → Streaming → Finalizing → Completed
                                           ↘ Failed
                                           ↘ Cancelled
```

- **Connecting**：建立 HTTP 连接与鉴权握手，展示骨架加载态。
- **Streaming**：接收 SSE 流式增量并通过 40ms 节拍 Pump 至轻量文本层，支持自动跟随滚动。
- **Finalizing**：接收并解析 Trailer 结构化元数据，进行 Token 还原与校验。
- **Completed**：切换至 Rich Markdown 终态层，解除 UI Final Gate 门禁。
- **Failed / Cancelled**：保留已接收到的 Partial 文本供用户参考，显示对应错误或取消提示；**Partial 状态绝不触发剪贴板自动复制、TTS 朗读与历史记录写入**。

## 模型推荐体系

为了降低用户选择模型的门槛，PopGlot 提供多维度的模型推荐与评估能力：

- **偏好策略**：
  - `Speed`（极速）：优先推荐低延迟、高响应速度的模型（如 Flash、Mini 系列）。
  - `Balanced`（均衡）：平衡响应速度与技术翻译准确率。
  - `Quality`（高质）：优先推荐推理与多语言表达能力最顶级的模型。
- **证据来源与评估分级**：
  - `CatalogExplicit`：Provider 目录接口返回的显式能力声明。
  - `FamilyHeuristics`：模型家族命名与参数规模启发式规则。
  - `LocalBenchmark`：本地基准测试实测数据。
  - `FallbackUnknown`：无确凿证据时严格归为未知。
- **准则与契约**：
  - **未知模型不虚构**：缺少明确证据时明确标记为 Unknown，绝不主观猜测模型能力。
  - **健康状态不门控**：服务健康检查仅作为当前网络可用性的参考指标，不作为保存配置、选择模型或发起翻译的阻断性门控。

## 模式与设置

设置必须提供：

- API Base URL
- API Key
- Provider 类型与文本/视觉 Endpoint
- 文本模型与模型推荐偏好（极速/均衡/高质）
- 视觉模型
- 非敏感自定义请求头、文本/图片能力开关、网络许可
- `Auto / LocalOcr / VisionDirect`
- 自动模式是否允许上传截图
- 安全离线模式（总开关，覆盖内置免费引擎）
- 划词（`Ctrl+Alt+W`）、截图（`Ctrl+Alt+Space`）、关闭浮窗（`Ctrl+Alt+X`）、主窗口（`Ctrl+Alt+O`）四组独立全局快捷键
- 深色/浅色/跟随系统
- 本地历史开关与清除

能力探测使用程序生成的无隐私最小文本。保存设置本身不联网；测试连接必须由用户主动点击且绝不带截图。API Key 进入操作系统安全凭据存储。

## 程序员内容保护

Local OCR 路线在模型请求前保护变量、类名、函数名、命名空间、路径、URL、命令参数、环境变量、异常、错误码、版本和代码块。返回后校验每个占位符只出现一次，再逐字符恢复。无法校验时安全失败。

Vision Direct 提示词要求先准确转录，并禁止翻译或改写代码元素。代码截图同时运行轻量本地 OCR 获取校验候选；无法确认 Token 完整性时显示不确定提示或回退本地路线，不能静默展示疑似改写结果。

## 自动模式

- 没有视觉模型或未授权上传：Local OCR。
- 清晰、简单布局：Local OCR。
- 代码且 OCR 置信度可用：Local OCR。
- 复杂布局、图文混排、低画质或低 OCR 置信度：Vision Direct。
- 视觉请求失败且零可见 Delta：Local OCR + Text 流式重试。

每次选择都显示用户可理解的原因。强制 Local OCR 时绝不上传图片。

## Provider 契约与流式传输

使用统一 `TranslationProvider` 契约支持文本流式、视觉流式、能力探测与结构化结果：

- OpenAI-compatible Chat Completions：Bearer、SSE 流式事件与 `image_url`
- OpenAI Responses API：Bearer、SSE 流式事件与 `input_image`
- Anthropic Messages：`x-api-key`、`anthropic-version`、SSE 流式事件与 base64 image
- Gemini GenerateContent：`x-goog-api-key`、`alt=sse` 流式事件与 `inline_data`

配置包含类型、Base URL、文本/视觉 Endpoint、模型、非敏感附加请求头与显式能力。Transport 支持取消、总超时、流式事件 256 KiB 限制与 4 MiB 响应上限。采用 **Text-first + 随机 Trailer 分隔符** 协议实现首 Token 极速直出与流尾结构化解析。

## 隐私与回退

- 默认本地 OCR；历史默认开启，可随时关闭、搜索、逐条删除或清空。
- 截图只在内存处理，默认不落盘。
- Auto 上传必须获得单独授权。
- 日志不含截图、原文、Key 或请求正文。
- 视觉直译在零可见 Delta 时失败可回退；已有 Delta 时保留 Partial 不二次出网。
- 用户取消不重试；鉴权失败不盲目重试。
- 划词只在用户主动按快捷键后模拟一次复制；原剪贴板按序列号恢复，后续用户写入优先。
- 历史默认开启但仅存本机；最多 200 条、90 天、4 MiB，疑似秘密或过大内容跳过，partial 不入库。

## 性能预算

- 冷启动至托盘 P50 ≤ 600 ms、P95 ≤ 1.2 s。
- 热快捷键至选区 P95 ≤ 150 ms。
- 截图确认 P95 ≤ 100 ms。
- 1080p 本地 OCR P50 ≤ 500 ms、P95 ≤ 1.2 s。
- 客户端给流式网络请求增加的首 Token（TTFT）处理开销 ≤ 20 ms。
- 空闲工作集建议 ≤ 80 MiB、发布版硬门槛 ≤ 120 MiB。
- 空闲 CPU 接近 0；不轮询屏幕或剪贴板。

## 范围

### P0

- 托盘、四组快捷键、划词事务、选区与流式浮窗
- 真实截图捕获与视觉流式直译
- 有界本地 JSON 历史
- 本地 OCR、视觉直译、自动路由
- 程序员 Token 保护
- OpenAI-compatible、OpenAI Responses、Anthropic、Gemini 文本/视觉流式 Provider
- Text-first 协议、SSE 解析器、FFI 流式回调、C# 缓冲协调器
- 模型推荐偏好与基准评测子系统
- 安全配置、取消、超时、回退和性能基准

### P1

- 术语库、划词结果定位增强、跨应用 hover 取词
- 常用语言语法增强、普通软件布局翻译

### P2

- 连续区域翻译、离线本地大模型、IDE/浏览器集成
- macOS/Linux Shell 移植

## 验收

- 受保护标识符逐字符保留率 100%。
- 强制 Local OCR 的网络测试中没有图片请求。
- Auto 的每次路线选择都有稳定原因码。
- 视觉模型不支持图片时自动回退并告知用户。
- 多显示器、混合 DPI、快捷键冲突和 `Esc` 取消正确。
- 复制成功/失败/取消均不丢失原剪贴板；用户事务期间的新复制不被覆盖。
- 划词、截图使用统一流式状态、浮窗操作和历史入口。
- 流式阶段与 Partial 状态严格不触发自动复制、自动朗读与历史写入。
- 无 Key、断网、超时、429、5xx、坏 JSON 均有可恢复状态。
- 四种 Provider 的请求 JSON、鉴权、SSE 流式解析均由本地 mock 覆盖，全量 113 项 Windows 逻辑测试通过。
