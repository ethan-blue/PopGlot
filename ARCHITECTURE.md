# PopGlot 架构

## 目标

PopGlot 首发使用 WPF 获得可靠的 Windows 桌面交互，但 Rust Core 必须能够在没有 WPF、Win32 或 CLR 的环境中编译和测试。未来 Shell 可以替换，识别编排、翻译、模型路由、Token 保护、历史和配置逻辑不重写。

## 当前结构

```text
apps/PopGlot.Windows       WPF Shell、托盘、快捷键、选区、浮窗、凭据
crates/popglot-domain      领域 DTO、自动路由、受保护 Token
crates/popglot-core        配置、应用编排、Provider 请求构造、安全预览
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

所有 Rust 返回字符串均由 `popglot_free_string` 释放。C# `CoreBridge.Invoke` 在 `finally` 中完成释放，不把原生指针暴露给 UI。新增字段应保持向后兼容，并在引入流式 RPC 前加入显式 `schema_version`。

当独立 Core 进程成为真实需求时，同一领域操作可以映射到 Named Pipe/Unix Domain Socket；在此之前不引入后台守护进程、RPC 框架或共享内存。

## 平台端口

后续真实实现按需要引入以下小型接口，而不是预先建立空框架：

- `ScreenCapturePort`
- `PlatformOcrPort`
- `GlobalHotkeyPort`
- `ClipboardPort`
- `SecureCredentialPort`
- `TextInjectionPort`

Windows WPF/Win32 实现这些接口；未来 macOS、Linux 重写截图、热键、托盘、浮窗、注入和安全存储。Rust Core 继续复用。

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

视觉请求使用 OpenAI-compatible `image_url` 内容块。视觉失败、不支持图片、限流、超时、坏 JSON 或 Token 校验失败时回退 Local OCR + Text。代码截图在 OCR 置信度可用时优先本地路线。

### 自动路由

路由只使用可解释输入：视觉模型是否配置、上传授权、代码概率、复杂布局、图片质量和 OCR 置信度。`RoutingDecision` 返回稳定原因码和中文解释。用户强制选择 `LocalOcr` 时，任何错误都不得触发图片上传。

## 资源、限制与故障策略

当前垂直切片不持有截图位图或 HTTP 资源。加入真实实现时必须遵守：

- 单次截图最多 16,000,000 像素，编码后最多 12 MiB；超过时提示重新框选或在不损害代码可读性的前提下分块。
- 同时最多处理 2 个翻译请求；新请求可以取消最旧的非固定请求。
- HTTP 连接超时 5 秒、首字节 15 秒、总请求 45 秒；仅对可安全重放的瞬时错误重试一次。
- 响应正文最多 4 MiB，单个 SSE 事件最多 256 KiB。
- 内存结果缓存最多 32 MiB 或 100 项，取先达到者；不得建立无界列表。
- 历史记录默认关闭；启用后默认最多 500 项、90 天，截图默认不入库。
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
