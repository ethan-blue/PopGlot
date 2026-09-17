# PopGlot 离线帮助与使用文档中心

欢迎使用 PopGlot！PopGlot 是专为 Windows 设计的极速、本地优先、保护隐私的 AI 翻译与查词助手。

本文档中心提供全面的本地离线使用指南，无需联网即可随时在应用内查阅。

---

## 帮助目录

### 1. [快速上手指南 (Getting Started)](getting-started.md)
- 核心交互四入口：划词翻译 (`Ctrl+Alt+W`)、截图翻译 (`Ctrl+Alt+Space`)、主工作台 (`Ctrl+Alt+O`)、极速查词
- 首次无配置运行与「尚未配置翻译引擎」引导卡
- 内置公共翻译与个人大模型引擎的快速切换
- 常用设置项概览

### 2. [隐私边界与数据安全 (Privacy & Security)](privacy-and-security.md)
- 什么数据会出网 vs 什么数据绝对不出网
- 安全离线模式一键切断原理
- 内置公共翻译授权语义与撤销权
- API Key 硬件级加密托管（Windows 凭据管理器）
- 历史记录与生词本的本地容量边界与敏感词自动过滤

### 3. [快捷键全景与冲突排查 (Keyboard Shortcuts)](keyboard-shortcuts.md)
- 全局快捷键真实值表（划词、截图、OCR、关闭浮窗、呼出主窗口）
- 窗口内局部快捷键（两段式 Esc 阶梯、Ctrl+C、Ctrl+P、Enter 即译）
- 快捷键录制规则（`HotkeyRecorder` 修饰键规范）
- 注册冲突时的原子回滚保护机制与管理员权限窗口处理

### 4. [翻译引擎配置指引 (Provider Setup)](provider-setup/index.md)
- 主流服务商配置：DeepSeek、OpenAI、Anthropic Claude、Google Gemini
- 本地私有离线模型接入（Ollama、LM Studio、vLLM）
- 凭据安全存储与免凭据拦截机制
- 「验证连接」草稿测试与模型智能推荐档位（速度/均衡/质量）

### 5. [常见故障与自愈诊断 (Troubleshooting)](troubleshooting.md)
- 提示「尚未配置翻译引擎」的修复路径
- HTTP 401 鉴权失败与 API Key 排查
- HTTP 429 限流保护与一分钟自动冷却机制
- 「网络访问未启用 / 安全离线模式」的切换
- 生词本文件损坏只读保护（C02 合同）与一键重试
- 开机启动被 Windows 任务管理器禁用的重新启用流程
