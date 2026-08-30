# PopGlot 0.1.0 发布说明

> **版本**：`0.1.0`
> **发布日期**：2026-08-31
> **适用平台**：Windows 10 (19041+) / Windows 11 (x64)

PopGlot 0.1.0 是一个重大的架构升级版本。本版本核心引入了全新的**低延迟流式输出架构**（SSE 增量解析 + Text-first 协议 + 原生 FFI Callback + C# 流式缓冲协调器）、**智能模型推荐偏好体系**、**翻译流式基准评测子系统**，并对设置交互与主题对比度进行了深度加固。全量 113 项 Windows 逻辑测试持续保持全绿。

---

## 🌟 版本亮点

- **低延迟首字流式响应（Low TTFT）**：采用全新设计的 **Text-first + 随机 Trailer 分隔符** 协议，模型首批正文到达后立即增量显示；配合 Core 轻量 SSE 增量解析器，显著减少等待完整响应的主观停顿。
- **四大 Provider 统一原生流式支持**：原生适配 OpenAI-compatible（Chat Completions）、OpenAI Responses、Anthropic Messages 与 Gemini GenerateContent 四大协议的流式输出，支持自定义中转地址与请求头。
- **Stream-Final 双层渲染与平滑过渡**：首 delta 到达时即刻呈现轻量只读文本流式层并支持跟随滚动；终态无缝平滑切至 Rich Markdown 排版，字号行高严格统一无视觉缩水。
- **UI Final Gate 动作门禁**：在流式增量阶段以及 partial 状态下，严格禁用自动复制、TTS 自动朗读与本地历史写入；所有外部动作与落盘仅在收到完整合法终态 Envelope 时单次触发。
- **智能模型推荐偏好体系**：提供 `Speed`（极速）、`Balanced`（均衡）、`Quality`（高质）三种偏好策略，基于 Provider 目录事实、模型家族启发式规则与本地基准综合评估；恪守“未知模型不虚构能力”契约，健康状态仅供参考而不作为保存和使用的阻断门控。
- **翻译基准评测子系统**：新增离线基准测试工具 `stream_benchmark`（模拟网络抖动、UTF-8 跨 chunk 切分与 TTFT 测量）与带严格安全门控的在线评测工具 `live_provider_bench`（默认 dry-run 退出 exit code 2，要求 `--live` 与 `--i-understand-cost` 双重显式确认，命令行禁传密钥）。
- **设置体验优化与主题高对比度**：主窗口侧栏底部新增同级「设置」入口，设置页默认直达「翻译引擎」；Dirty 状态采用规范化纯值比较；关键边框与文字全面通过 WCAG 2.1 AA 对比度审计。

---

## 🛠️ 详细修复与改进

### 1. 流式传输与核心架构
- **增量 SSE 解析器**：Rust Core 内置 `SseDecoder`，支持跨 chunk 字节流边界还原多字节 UTF-8 字符（中文、Emoji），设置 256 KiB 单事件硬上限防御。
- **Text-first 协议与 Trailer 解析**：正文流式直出，流尾随随机防冲突分隔符附带结构化 JSON 元数据；元数据格式异常或网络早闭时优雅保全已接收正文，平滑降级。
- **FFI 流式 Callback 桥接**：导出 `popglot_translate_text_stream_v1`、`popglot_translate_text_draft_stream_v1` 与 `popglot_translate_vision_draft_stream_v1`，支持基于 C ABI 回调的 delta 传输与主动 abort 中止。
- **C# 缓冲协调器（Coordinator）**：`TranslationStreamBuffer` 采用 O(1) 短锁入队，无 UI 线程阻塞；`TranslationCoordinator` 采用 40ms 定时 Pump 增量，引入全局 Epoch 隔离机制杜绝连续划词/输入的“串台”竞争。

### 2. 交互与渲染优化
- **双层字号行高对齐**：流式文本层与终态 Markdown 层字号（15px）与行高（22px）严格统一，彻底解决流式结束瞬间文字跳动的视觉瑕疵。
- **主窗口侧栏设置入口**：主窗口侧栏底部新增同级「设置」按钮，便于一键调出设置窗口。
- **设置页状态管理修复**：设置页 Dirty 状态改为快照纯值比较，改回原值自动恢复 Clean；修复保存失败时卡在 Loading 状态的问题。
- **输入框占位符渲染**：修复输入框聚焦为空时占位符意外消失的问题。

### 3. 主题与无障碍（Accessibility）
- **高对比度加固**：引入 `ThemeContrast` 工具类，浅色与深色主题下的 `TextTertiary`、发丝边框与占位文本全面通过 WCAG 2.1 AA 级对比度测试。
- **读屏器体验提升**：三处翻译界面终态状态文本接入 Polite LiveRegion，流式过程不产生无意义的逐字打扰。

---

## 🔒 隐私与安全保障

- **Partial 结果不落盘**：流式进行中与 partial/未完成状态下的内容绝对不写入 `%LOCALAPPDATA%\PopGlot\history.json`，绝不触发剪贴板自动复制与语音朗读。
- **基准评测安全隔离**：`live_provider_bench` 默认处于离线 Dry-Run 模式（退出码 2），严禁通过命令行参数传 Key，仅支持环境变量临时注入，评测报告严格脱敏。
- **安全离线模式总开关**：开启后立即切断包括内置免费引擎、云端模型与云端 TTS 在内的全部出网请求。
- **程序员 Token 逐字节保护**：变量名、路径、代码块等弱标识符在翻译前自动遮蔽，翻译完成后逐字节比对还原。

---

## 📦 升级与向后兼容

- **配置无缝兼容（无需迁移）**：本版本沿用 Schema v6（`product-config.json` v6，`provider-settings.json` v3），已有服务列表、模型配置、自定义 Header 与 Credential Manager 凭据完全无缝继承，无需执行任何配置迁移。
- **回滚与备份机制**：配置保存依旧保持原子替换与 `.bak` 备份机制。

---

## 💻 系统要求

- **操作系统**：Windows 10 (Version 19041+) 或 Windows 11 x64
- **运行环境**：[.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)（便携版基于框架依赖构建，请确保已安装 x64 桌面运行时）

---

## 🔍 下载包 SHA256 校验方法

从 GitHub Releases 下载便携包 `PopGlot-v0.1.0-win-x64.zip` 与校验文件 `PopGlot-v0.1.0-win-x64.zip.sha256` 后，可通过 Windows PowerShell 进行完整性验证：

```powershell
# 1. 计算本地下载压缩包的 SHA256 哈希
(Get-FileHash -Path .\PopGlot-v0.1.0-win-x64.zip -Algorithm SHA256).Hash.ToLower()

# 2. 读取官方发布的 SHA256 校验文件对比
Get-Content .\PopGlot-v0.1.0-win-x64.zip.sha256
```

若两条输出的哈希字符串完全一致，则表明下载文件完整且未被篡改。

---

## ⚠️ 已知限制与 Benchmark 说明

- **跨应用 Hover 取词**：受限于各宿主应用（部分终端、自绘画布窗口、不同权限应用）对 Windows UI Automation 的实现差异，跨应用鼠标 Hover 取词尚未覆盖所有场景，推荐使用全局快捷键进行确定性划词翻译（`Ctrl+Alt+W`）与截图翻译（`Ctrl+Alt+Space`）。
- **离线流式基准实测摘要（Windows 本机 Loopback，2026-08-30）**：
  - 本次主控在 Windows 本机 Loopback 环境下（10 iterations + 2 warmup，注入 TTFT 30ms、chunk 间隔 5ms，203 字符样本，seed 42，prompt v1）实测基线结论：
    - **Realistic 场景**：TTFT p50 31.46 ms / p95 31.88 ms，端到端耗时 p50 84.08 ms / p95 85.63 ms，Mock 传输解析吞吐 3840.33 chars/s；
    - **UTF-8 跨 Chunk 拆分保全**：`split_utf8` 场景下多字节字符（中文/Emoji）100% 无损还原（TTFT p50 32.77 ms，总耗时 p50 101.13 ms，吞吐 2981.35 chars/s）；
    - **异常防御与降级**：缺失 Trailer 时优雅保全正文（`missing_trailer` 4292.66 chars/s 并触发预期告警），损坏 SSE 流场景（`corrupted_sse`，parse_errors 10，chars/s 0）预期故障被 100% 正确拦截；全场景综合判定 `overall_passed: true`。
  - **重要声明**：以上度量为本地 Loopback 回环网络、延迟注入与本地解析/缓冲管道吞吐（`chars/s` 仅表示 Mock 文本输送解析吞吐，**非大模型推理 tokens/s**），**绝不代表任何公网真实云端大模型服务表现**。
  - 完整 6 大场景实测数据表与评测规范详见 [TRANSLATION_BENCHMARK.md](./TRANSLATION_BENCHMARK.md)。
