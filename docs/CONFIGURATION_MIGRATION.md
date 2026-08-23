# Provider 配置与迁移

## 配置位置

非秘密设置由 Windows Shell 指定目录，当前为 `%LOCALAPPDATA%\PopGlot\provider-settings.json`。Rust Core 不自行读取 Windows 环境变量，也不依赖注册表。配置 `schema_version` 当前为 `2`。

API Key 不进入 JSON。Windows Shell 将当前活动 Provider 的 Key 保存为 Windows Credential Manager 通用凭据 `PopGlot/OpenAICompatibleApiKey`。此名称来自首个版本，为避免丢失已有凭据暂不迁移；语义已经是“当前活动 Provider Key”。切换 Provider 后应在设置页替换它。

## v1 到 v2

旧配置只有 Base URL、文本/视觉模型、模式和安全开关。反序列化时新增字段使用默认值：

- `provider_type`: `OpenAiCompatible`
- endpoint: `/chat/completions`
- `supports_text` / `supports_vision`: `true`
- `network_enabled`: `false`
- `extra_headers`: 空
- `anthropic_version`: `2023-06-01`

因此升级后不会因旧配置而自动联网。用户必须显式启用网络，并关闭 Safe Dev Mode，才可能发送请求。

## 默认协议

| Provider | Base URL | Endpoint |
| --- | --- | --- |
| OpenAI-compatible | `https://api.openai.com/v1` | `/chat/completions` |
| OpenAI Responses | `https://api.openai.com/v1` | `/responses` |
| Anthropic Messages | `https://api.anthropic.com` | `/v1/messages` |
| Gemini GenerateContent | `https://generativelanguage.googleapis.com` | `/v1beta/models/{model}:generateContent` |

Base URL 必须是 HTTPS；仅为本地开发允许 `http://localhost` 和 `http://127.0.0.1`。Endpoint 必须是绝对路径。Gemini 的 `{model}` 由经过路径字符校验的文本或视觉模型名替换。

自定义请求头每行使用 `Header: Value`。最多 16 个，不能配置鉴权、Cookie 或换行；Key 始终由安全凭据层注入。

## 测试连接

1. 选择 Provider，填写 endpoint 和文本模型。
2. 保存对应 API Key。
3. 启用“允许模型网络请求”，关闭 Safe Dev Mode。
4. 主动点击“测试连接（仅文本）”。

测试只发送内置的最小翻译句，不上传截图或用户选区。仓库测试只使用本机 mock；发布者不能把 mock 通过描述成真实云端连通性验证。

## Windows Shell v1 到 v2

`windows-shell.json` 从单一 `ShortcutId` 升级为三组快捷键、历史开关和主题：

- `SelectionShortcutId`: 默认 `ctrl-alt-w`
- `ScreenshotShortcutId`: 继承旧 `ShortcutId`，无旧值时为 `ctrl-alt-space`
- `CloseShortcutId`: 默认 `ctrl-alt-x`
- `HistoryEnabled`: 默认 `false`
- `Theme`: 默认 `System`

迁移不会启用历史或网络。重复快捷键在写盘前拒绝；系统注册冲突时回滚到上一组已注册快捷键。
