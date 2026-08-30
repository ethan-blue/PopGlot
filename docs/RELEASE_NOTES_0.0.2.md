# PopGlot 0.0.2 发布说明（草稿）

> **版本**：`0.0.2`
> **计划发布日期**：2026-08-30
> **适用平台**：Windows 10 (19041+) / Windows 11 (x64)
> **说明**：本文档为 0.0.2 版本正式发布前的发布说明草稿，发布时可直接作为 Release Notes / Release Body。

PopGlot 0.0.2 是一个以**稳定性加固、数据安全与离线门禁强化**为核心的维护版本。本版本全面收紧了离线策略与崩溃防护，完善了词库持久化原子性与损坏保全机制，优化了服务删除与运行时配置同步，并为便携分发构建了严格的发布版本一致性与校验门禁。

---

## 🌟 版本亮点

- **TTS 离线策略严防越界**：网络禁用或处于安全离线模式时，`TtsService` 强制使用 Windows 本地语音合成引擎，拦截一切云端语音请求。
- **词库原子存储与损坏保全**：生词本写入采用临时文件与原子替换机制；当检测到损坏文件时自动复制备份为 `<path>.corrupt-<timestamp>`，杜绝静默清空用户词库。
- **服务删除与运行时同步**：删除服务时先保存配置，再尽力（best-effort）清理对应凭据（保存失败时凭据保持），并在删除默认文字服务时通过 `ApplyToCore` 同步更新或清空底层运行配置。
- **模型目录敏感 Header 过滤与 TLS 规范**：`ModelCatalogService` 统一过滤保留敏感请求头并适配各 Provider 标准请求头与安全 TLS 设置，提升模型拉取安全性与稳定性。
- **跨 FFI Panic 防护**：Rust Core 导出接口（取消与内存释放）增加 `catch_unwind` 与空指针防御，防止原生异常穿透 FFI 边界造成桌面进程崩溃。
- **健康探测并发 Single-flight**：服务连通性测试引入并发单飞去重机制，避免频繁切换或重复点击导致的请求拥堵与状态抖动。
- **发布四方版本一致性校验**：CI 发布流水线强制校验 Release Tag、C# csproj、Rust Cargo.toml 与 CHANGELOG.md 四方版本一致性，并自动生成 SHA256 校验和文件。

---

## 🛠️ 详细修复与改进

### 1. 语音与路由门禁
- **TTS 离线拦截**：`TtsService` 增加实时策略校验，离线模式下不发起远程 TTS 请求，纯本地合成保障隐私。
- **文本/视觉快照一致性**：修正服务编辑态草稿与运行时路由解析的快照同步逻辑，确保请求使用的 API 地址、模型名与凭据槽位严格对应。
- **模型目录协议与安全头过滤**：`ModelCatalogService` 统一过滤 `Authorization`、`Cookie`、`x-api-key` 等保留敏感头，规范鉴权头注入并强制公网 HTTPS。

### 2. 存储与数据一致性
- **生词本持久化加固**：`VocabularyStore` 写入流程增加 `temp file -> atomic replace`，遇到 JSON 解析失败时复制 `<path>.corrupt-<timestamp>` 备份文件供排查。
- **服务删除与运行时同步**：删除服务时联动更新 `ActiveProfileId` 与 `VisionProfileId`，先落盘配置再清理凭据，并通过 `ApplyToCore` 同步生效。
- **配置 Schema v5 → v6 升级**：以 `TextModel` 与 `VisionModel` 字段显式推导文本与视觉支持能力，解决早期版本因标志位不同步导致可见模型不可用的问题。

### 3. 前端生命周期与稳定性
- **设置窗口主题事件退订**：`SettingsWindow` 关闭时显式退订 `ThemeService.ThemeChanged` 事件，消除长期运行下的主题事件监听泄漏。
- **测试套件稳定性与覆盖**：修复测试夹具中异步等待轮询形参副本的问题，增加并发保存、缓存污染隔离与敏感头过滤等逻辑测试。

---

## 🔒 隐私与离线保证

- **零配置免费引擎授权**：未配置 API Key 时走内置免费引擎，首次使用不弹出侵入式弹窗，未授权时提示用户前往「设置 → 隐私与数据」主动开启。
- **安全离线模式总开关**：开启后立即切断包括内置免费引擎、云端模型与云端 TTS 在内的全部出网请求。
- **程序员 Token 保护**：变量名、路径、代码块等弱标识符在翻译前自动遮蔽，翻译完成后逐字节比对还原。
- **凭据隔离保存**：每个服务使用独立 Windows Credential Manager 凭据存储，API Key 不写入任何 JSON 配置文件或日志中。

---

## 📦 升级与向后兼容

- **配置无缝迁移**：应用启动时会自动将现有的 `%LOCALAPPDATA%\PopGlot\product-config.json`（Schema v5）升级为 Schema v6，已有服务列表、自定义请求头与凭据配置完整保留。
- **回滚与备份**：配置迁移前自动保留 `.bak` 备份文件（保留迁移前的原始文件）。

---

## 💻 系统要求

- **操作系统**：Windows 10 (Version 19041+) 或 Windows 11 x64
- **运行环境**：[.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)（请确保已安装 x64 桌面运行时）

---

## 🔍 下载包 SHA256 校验方法

从 Release 页面下载便携包 `PopGlot-v0.0.2-win-x64.zip` 与校验文件 `PopGlot-v0.0.2-win-x64.zip.sha256` 后，可通过 Windows PowerShell 进行完整性验证：

```powershell
# 1. 计算本地下载压缩包的 SHA256 哈希
(Get-FileHash -Path .\PopGlot-v0.0.2-win-x64.zip -Algorithm SHA256).Hash.ToLower()

# 2. 读取官方发布的 SHA256 校验文件对比
Get-Content .\PopGlot-v0.0.2-win-x64.zip.sha256
```

若两条输出的哈希字符串完全一致，则表明下载文件完整且未被篡改。

---

## ⚠️ 已知限制

- **全局 Hover 取词**：受限于各宿主应用（部分终端、自绘画布窗口、不同权限应用）对 Windows UI Automation 的实现差异，跨应用鼠标 Hover 取词尚未覆盖所有场景，推荐使用全局快捷键进行确定性划词翻译（`Ctrl+Alt+W`）与截图翻译（`Ctrl+Alt+Space`）。
- **云端服务连通性**：本版本测试套件基于本机 mock 验证协议兼容性与隐私边界，真实外部服务连接质量仍取决于用户配置的 API 服务商网络连通性。
