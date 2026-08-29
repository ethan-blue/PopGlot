# Provider 配置与迁移

## 配置位置

非秘密设置由 Windows Shell 指定目录，当前为 `%LOCALAPPDATA%\PopGlot\provider-settings.json`。Rust Core 不自行读取 Windows 环境变量，也不依赖注册表。配置 `schema_version` 当前为 `3`。

API Key 不进入 JSON。Windows Shell 将当前活动 Provider 的 Key 保存为 Windows Credential Manager 通用凭据 `PopGlot/OpenAICompatibleApiKey`。此名称来自首个版本，为避免丢失已有凭据暂不迁移；语义已经是“当前活动 Provider Key”。切换 Provider 后应在设置页替换它。

## v1 到 v2

旧配置只有 Base URL、文本/视觉模型、模式和安全开关。反序列化时新增字段使用默认值：

- `provider_type`: `OpenAiCompatible`
- endpoint: `/chat/completions`
- `supports_text` / `supports_vision`: `true`
- `network_enabled`: **`false`（缺失的联网权限视为未授权）**
- `extra_headers`: 空
- `anthropic_version`: `2023-06-01`

因此升级后不会因旧配置而自动联网，也不会自动获得截图上传权限。出网类权限（`network_enabled`、`allow_image_upload_in_auto`）在旧文件中缺失时一律按 `false` 处理，只有用户显式保存过的 `true` 才会保留；其余字段（endpoint、模型、语言对等）继续使用各自的默认值。用户必须显式启用网络，并关闭安全离线模式，才可能发送请求。

## v2 到 v3

v3 增加了语言与输出偏好，缺失字段沿用默认值，旧文件可直接读取：

- `source_language`: `auto`
- `target_language`: `zh-CN`
- `include_explanation`: `true`
- `protect_code_tokens`: `true`

`safe_dev_mode` 在 v3 起真正生效：此前它被持久化却从未被检查，现在是所有外发请求（含内置免费引擎）的总开关。

无法解析的 `provider-settings.json` 不再阻断启动，而是回退到默认配置——损坏的配置文件曾会让应用完全打不开。

## 默认协议

| Provider | Base URL | Endpoint |
| --- | --- | --- |
| OpenAI-compatible | `https://api.openai.com/v1` | `/chat/completions` |
| OpenAI Responses | `https://api.openai.com/v1` | `/responses` |
| Anthropic Messages | `https://api.anthropic.com` | `/v1/messages` |
| Gemini GenerateContent | `https://generativelanguage.googleapis.com` | `/v1beta/models/{model}:generateContent` |

Base URL 必须是 HTTPS；仅回环与 RFC1918 私有网段允许 HTTP（按真实主机解析判定，`relay-10.example.com` 这类公网域名不会被误判为内网）。Endpoint 必须是绝对路径。Gemini 的 `{model}` 由经过路径字符校验的文本或视觉模型名替换。

自定义请求头每行使用 `Header: Value`。最多 16 个，不能配置鉴权、Cookie 或换行；Key 始终由安全凭据层注入。

## 测试连接

1. 选择 Provider，填写 endpoint 和文本模型。
2. 保存对应 API Key。
3. 保持“启用大模型网络翻译”开启，并关闭“安全离线模式”。
4. 主动点击“测试连接（仅文本）”。

测试只发送内置的最小翻译句，不上传截图或用户选区。仓库测试只使用本机 mock；发布者不能把 mock 通过描述成真实云端连通性验证。

## Windows Shell v1 / v2 到 v3

`windows-shell.json` 经历了两次升级，v3 可以直接读取全部旧格式：

| 版本 | 快捷键表示 |
| --- | --- |
| v1 | 单一 `ShortcutId`（仅截图） |
| v2 | `SelectionShortcutId` / `ScreenshotShortcutId` / `CloseShortcutId`，取值限于六个预设 id |
| v3 | `SelectionHotkey` / `ScreenshotHotkey` / `CloseHotkey`，可读组合字符串如 `Ctrl+Alt+W` |

v2 的 `ctrl-alt-w` 形式与 v3 的 `Ctrl+Alt+W` 由同一个解析器处理，因此旧文件无需转换步骤。v1 的 `ShortcutId` 继续被当作截图快捷键。

v3 新增字段：

- `ClosePanelOnFocusLoss`: 默认 `true`
- `CopyTranslationAutomatically`: 默认 `false`
- `StartWithWindows`: 默认 `false`（写入 HKCU 的 Run 项，不需要提权）
- `HistoryEnabled`: 默认 `true`

快捷键现在允许任意组合，但必须至少包含 `Ctrl`、`Alt` 或 `Win` 之一并搭配一个普通键——纯 `Shift` 组合会在全系统吞掉正常输入。重复快捷键在写盘前拒绝；系统注册冲突时回滚到上一组已注册快捷键，并在托盘气泡中说明是哪一个冲突。

设置通过临时文件写入后原子改名，写盘中途崩溃不会让用户丢失配置。

## 服务 Profile 配置（product-config.json）

`%LOCALAPPDATA%\PopGlot\product-config.json` 保存服务 Profile 列表：每个服务拥有稳定 id、显示名称、协议、地址、模型、能力与独立凭据引用 `PopGlot/provider/{id}`（Windows Credential Manager 通用凭据）。该文件与核心设置使用同样的持久化契约：临时文件、flush、原子替换、保留 `.bak`。

首次运行到 v4 结构时，若文件不存在，应用会从当前 `provider-settings.json` 播种出唯一的服务并设为默认——已配置好的模型不会被打厂预设覆盖；全新安装（默认 OpenAI 地址 + 默认模型 + 无密钥）则展示五个厂商预设。历史上保存在旧统一目标 `PopGlot/OpenAICompatibleApiKey` 的密钥继续可读：当 Profile 自己的目标为空时自动回退，直到用户下次编辑密钥为止。删除服务会同时删除其独立凭据；测试连接永远使用内存草稿，不写该文件。
