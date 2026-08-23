# PopGlot 产品规格

## 产品定位

PopGlot 是 Windows-first 的中英 AI 翻译桌面助手。它以全局快捷键唤起截图翻译，面向英语不熟且频繁阅读技术内容的用户。程序员场景优先，普通软件和屏幕翻译使用同一可扩展管线。

## P0 用户流程

1. 应用启动后进入托盘，不弹主窗口。
2. 用户按可配置全局快捷键进入跨显示器选区。
3. 浮窗立即展示“截图、OCR、分析、翻译、完成/失败”状态。
4. 自动模式解释为什么选择本地 OCR 或视觉直译。
5. 结果按“中文翻译、保留术语、语义解释、可能原因、建议动作”分层。
6. 用户可以复制、固定、重试或关闭；`Esc` 随时取消。

## 模式与设置

设置必须提供：

- API Base URL
- API Key
- Provider 类型与文本/视觉 Endpoint
- 文本模型
- 视觉模型
- 非敏感自定义请求头、文本/图片能力开关、网络许可
- `Auto / LocalOcr / VisionDirect`
- 自动模式是否允许上传截图
- Safe Dev Mode

能力探测使用程序生成的无隐私最小文本。保存设置本身不联网；测试连接必须由用户主动点击且绝不带截图。API Key 进入操作系统安全凭据存储。

## 程序员内容保护

Local OCR 路线在模型请求前保护变量、类名、函数名、命名空间、路径、URL、命令参数、环境变量、异常、错误码、版本和代码块。返回后校验每个占位符只出现一次，再逐字符恢复。无法校验时安全失败。

Vision Direct 提示词要求先准确转录，并禁止翻译或改写代码元素。代码截图同时运行轻量本地 OCR 获取校验候选；无法确认 Token 完整性时显示不确定提示或回退本地路线，不能静默展示疑似改写结果。

## 自动模式

- 没有视觉模型或未授权上传：Local OCR。
- 清晰、简单布局：Local OCR。
- 代码且 OCR 置信度可用：Local OCR。
- 复杂布局、图文混排、低画质或低 OCR 置信度：Vision Direct。
- 视觉请求失败：Local OCR + Text。

每次选择都显示用户可理解的原因。强制 Local OCR 时绝不上传图片。

## Provider 契约

P0 使用统一 `TranslationProvider` 契约支持文本翻译、视觉翻译、能力、诊断和结构化结果，首批原生协议为：

- OpenAI-compatible Chat Completions：Bearer、文本内容与 `image_url`
- OpenAI Responses API：Bearer、`input_text` 与 `input_image`
- Anthropic Messages：`x-api-key`、`anthropic-version`、base64 image source
- Gemini GenerateContent：`x-goog-api-key`、text parts 与 `inline_data`

配置包含类型、Base URL、文本/视觉 Endpoint、模型、非敏感附加请求头与显式能力。Transport 支持取消、总超时、4 MiB 响应上限和最多一次瞬时错误重试。流式输出延后到浮窗存在真实增量消费需求时实现。

## 隐私与回退

- 默认本地 OCR，历史默认关闭。
- 截图只在内存处理，默认不落盘。
- Auto 上传必须获得单独授权。
- 日志不含截图、原文、Key 或请求正文。
- 视觉能力错误、超时、限流、坏响应或 Token 校验失败均可回退。
- 用户取消不重试；鉴权失败不盲目重试。

## 性能预算

- 冷启动至托盘 P50 ≤ 600 ms、P95 ≤ 1.2 s。
- 热快捷键至选区 P95 ≤ 150 ms。
- 截图确认 P95 ≤ 100 ms。
- 1080p 本地 OCR P50 ≤ 500 ms、P95 ≤ 1.2 s。
- 客户端给网络请求增加的 P95 开销 ≤ 50 ms。
- 空闲工作集建议 ≤ 80 MiB、发布版硬门槛 ≤ 120 MiB。
- 空闲 CPU 接近 0；不轮询屏幕或剪贴板。

## 范围

### P0

- 托盘、单实例、快捷键、选区与浮窗
- 本地 OCR、视觉直译、自动路由
- 程序员 Token 保护
- OpenAI-compatible、OpenAI Responses、Anthropic、Gemini 文本/视觉 Provider
- 安全配置、取消、超时、回退和性能基准

### P1

- 剪贴板/划词翻译、本地历史、术语库
- 多 Provider、常用语言语法增强
- 普通软件布局翻译、更新与便携版

### P2

- 连续区域翻译、离线模型、IDE/浏览器集成
- 能力市场、更多语言、macOS/Linux Shell

## 跨平台边界

WPF 仅是 Windows Shell。Rust Core 不依赖 WPF 或 Win32。未来 macOS/Linux 只重写捕获、快捷键、托盘、浮窗、注入、凭据和平台 OCR；翻译、路由、保护、Provider、历史和配置继续复用。

## P0 验收

- 受保护标识符逐字符保留率 100%。
- 强制 Local OCR 的网络测试中没有图片请求。
- Auto 的每次路线选择都有稳定原因码。
- 视觉模型不支持图片时自动回退并告知用户。
- 多显示器、混合 DPI、快捷键冲突和 `Esc` 取消正确。
- 无 Key、断网、超时、429、5xx、坏 JSON 均有可恢复状态。
- 截图和响应超过大小上限时安全拒绝。
- 无密钥可正常启动且在 HTTP 前失败；默认网络关闭、Safe Dev Mode 开启。
- 四种 Provider 的请求 JSON、鉴权、响应解析均由本地 mock 覆盖，不需要真实云端凭据。
