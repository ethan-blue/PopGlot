# 翻译引擎配置指引 (Provider Setup)

PopGlot 原生内置支持四大主流大模型通信协议（OpenAI Compatible、OpenAI Responses、Anthropic Messages、Google Gemini），并完全兼容本地离线运行的开源模型服务（Ollama、LM Studio、vLLM 等）。

本文档将引导您如何在「设置 → 翻译引擎」中接入并管理您自己的大模型。

---

## 快速添加引擎步骤

1. 打开 PopGlot 主窗口，点击左下角「设置」（或托盘图标右键选择「设置」）；
2. 导航栏默认处于**「翻译引擎」**分区；
3. 点击页面右上角的**「添加引擎」**主要按钮；
4. 从下拉列表选择服务商预设，或选择「自定义兼容协议」；
5. 填写 API Key 与 Base URL，点击**「验证连接」**测试通道可用性；
6. 点击**「获取模型」**获取该服务商的模型目录并选定模型；
7. 点击底栏**「保存」**；首个添加的引擎将自动成为默认文字翻译引擎。

---

## 主流服务商接入配置规范

### 1. DeepSeek (推荐，性价比与中文表达极佳)
- **协议类型**：OpenAI 兼容 (Chat Completions)
- **Base URL**：`https://api.deepseek.com`
- **推荐模型**：`deepseek-chat` (DeepSeek-V3)
- **特点**：响应极快、代码与技术报错语义还原地道，性价比极高。

### 2. OpenAI (GPT-4o / GPT-4o-mini)
- **协议类型**：OpenAI 兼容 (Chat Completions) 或 OpenAI Responses
- **Base URL**：`https://api.openai.com`
- **推荐模型**：
  - 速度优先：`gpt-4o-mini`
  - 质量优先：`gpt-4o`
- **注意**：国内直连需要确保网络环境或通过合规 API 反向代理中转。

### 3. Anthropic Claude
- **协议类型**：Anthropic Messages
- **Base URL**：`https://api.anthropic.com`
- **推荐模型**：`claude-3-5-haiku-latest` (速度与技术准确)、`claude-3-5-sonnet-latest` (极高语言质感)。

### 4. Google Gemini
- **协议类型**：Gemini GenerateContent
- **Base URL**：`https://generativelanguage.googleapis.com`
- **推荐模型**：`gemini-1.5-flash`、`gemini-2.0-flash-exp`。

### 5. 本地私有模型 (Ollama / LM Studio，100% 离线隐私)
如果您需要在完全离线或敏感代码库环境下工作：
- **运行环境**：本机已启动 Ollama 或 LM Studio；
- **协议类型**：OpenAI 兼容
- **Base URL**：
  - Ollama：`http://localhost:11434/v1`
  - LM Studio：`http://localhost:1234/v1`
- **API Key**：本地服务无需密钥，直接留空即可；
- **推荐模型**：`qwen2.5:7b`、`llama3.1:8b`、`deepseek-coder` 等。

---

## 凭据管理与连接测试机制

### 1. 凭据存储与安全隔离
- 填入的 API Key 在保存时直接存入 **Windows Credential Manager**；
- 界面上的密码框支持「清除」操作，且未填入新 Key 时不会覆盖已存有效凭据。

### 2. 「验证连接」的运行机制
- **草稿独立测试**：点击「验证连接」时，系统使用当前表单中的草稿配置向端点发送一段极短的握手测试文本（如「ping」）；
- **绝不上传截图**：握手测试严格禁止携带任何图像或历史记录；
- **不强制绑定使用**：连接测试仅用于告知服务健康状态（展示 HTTP 响应码、延迟毫秒数与服务主机名），测试成功与否不作为保存配置的强制门禁；
- **免凭据拦截**：如果是远程服务且 API Key 为空，系统会在发出网络请求前直接拦截并提示，避免无效的 401 网络报错。

---

## 模型目录获取与智能推荐

### 1. 动态获取模型
点击「获取模型」按钮后，PopGlot 会向服务商的 `/models` 接口查询当前账户下所有可用模型 ID，并自动填充下拉列表。

### 2. 智能推荐档位
针对返回的模型列表，PopGlot 会结合官方规格、模型名称启发式规则及本地基准提供分级推荐：
- **速度优先 (Speed)**：自动推荐轻量级低延迟模型（如 `flash`、`mini`、`haiku` 等）；
- **均衡推荐 (Balanced)**：推荐日常性价比最高、兼顾理解力与响应速度的黄金主力模型；
- **质量优先 (Quality)**：推荐参数量完整的高能力模型（如 `gpt-4o`、`sonnet` 等）。

> **诚实标注原则**：如果服务商未返回模态信息或模型属于小众开源分支，PopGlot 会诚实将其标记为「未知 (Unknown)」，绝不主观伪造其视觉或文字支持能力。

---

## 默认引擎与路由分配

PopGlot 支持配置多个翻译引擎，并按场景指派：
- **默认文字翻译引擎**：负责处理划词翻译、工作台文本翻译、极速查词及本地 OCR 识别后的文本流式翻译；
- **默认视觉识别引擎**：当截图走视觉模型路线（`VisionDirect` 或 `VisionOcr`）时负责接收图片。
- **切换与生效**：在引擎列表中点击对应卡片，选择「设为默认」；切换即时对下一次请求生效，无需重启应用。
