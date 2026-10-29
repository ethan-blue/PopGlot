# PopGlot 定位与竞品调研

- 调研日期：2026-10-29
- 用途：供 README、发布说明、推广文案直接引用
- 事实来源：本文所有关于 PopGlot 的描述，均来自仓库既有的产品事实清单（版本 0.1.35，2026-10-29 发布）；竞品的 Star / 归档状态 / 最后 push / 许可证优先采用 GitHub REST API 实测数据
- 数据分级（贯穿全文与对比表）：
  - **【API】** = 2026-10-29 查自 GitHub REST API
  - **【页】** = 2026-10-29 查自 GitHub 仓库页面
  - **【三方】** = 第三方站点或厂商官网记录，可信度次之
  - 无任何来源的一律写「—」，不猜测
- 纪律：不写用户数、下载量、评分、性能对比数字；不使用「最强 / 第一 / 唯一 / 碾压」类措辞

---

## 1. 竞品画像

### 1.1 Pot（pot-app/pot-desktop，派了个萌的翻译器）

- 平台：Windows / macOS / Linux（含 Wayland 说明）
- 开源：是，GPL-3.0【API】
- 价格：免费
- 功能：划词翻译、输入翻译、截图 OCR、截图翻译、TTS、生词本（可导出 Anki / 欧路词典）、插件系统、本机 HTTP 接口（默认端口 60828）
- 本地/离线：官方接口列表含 Ollama；系统 OCR（Windows.Media.OCR / Apple Vision / Tesseract）；离线词典插件
- 活跃度【API】：**19,348 star，仓库 archived=true，最后 push 2026-07-04**。且 pot-app 组织下 15 个仓库（pot-desktop、pot-docs、Selection 及各插件仓库等）全部 archived=true，是整个组织归档，不是单个仓库误标
- 官网首页公告「Pot 现已不再维护」，作者以 Qt 重写的闭源继任产品 Manggo 继续（免费版有限量内置翻译，专业版人民币 49–68 元买断，一码 5 台设备）【三方】
- 最后版本 3.0.7（2025-05-10）【三方，未用官方接口核实】
- 隐私模型：不提供翻译本身，文本发往用户启用的引擎；历史与配置落本机 SQLite / 配置文件，无账号、无云同步
- 【三方】实测指出一条风险：默认多引擎并行中，作者自架的 lingva 前端与在线词典端点已失效，免费端点会随时间逐个失效且无人修复

### 1.2 Bob

- 平台：macOS
- 开源：**否**。现为 Mac App Store 商业产品；1.0.0 以下的「社区版」曾开源但官方声明不再维护
- 价格：商店版内购终身买断；社区版免费
- 功能：划词翻译、截图翻译、原图翻译（译文覆盖原位）、输入翻译、静默 OCR、可并排最多 12 个服务结果、插件生态
- 本地/离线：macOS 11 以上支持离线文字识别（OCR 侧）
- 活跃度：版本 1.21.2（第三方软件站记录更新日 2026-10-18）【三方】；GitHub Star 与 push 数据未取得，记「—」
- 隐私模型：—（未核实）

### 1.3 沉浸式翻译（immersive-translate/immersive-translate）

- 平台：**浏览器扩展**，不是桌面划词工具（Chrome / Edge / Firefox 等）。移动端与桌面客户端的说法未核实
- 开源：**否**。GitHub license 字段为 none【API】，即仓库没有开源许可；README 明确声明「沉浸式翻译并非开源软件，本仓库不包含沉浸式翻译的源代码」，旧开源项目已于 2023-01-17 归档
- 价格：免费增值；第三方评测记录 Pro 为 $69/年（2026-04 查询）【三方】
- 功能：网页双语对照、划词、悬停、输入框翻译；PDF / EPUB / TXT / 字幕翻译；图片与漫画翻译
- 本地/离线：—（未核实是否可接本机 Ollama）
- 活跃度【API】：19,525 star，活跃，最后 push 2026-10-23
- 隐私模型：文本发往用户选择的引擎；官方 README 称「在适用的官方翻译服务范围内，翻译内容不用于 AI 模型训练」。【三方】安全评测记录 2024 年 XSS 问题与 2025 年网页快照上传导致的公开可访问泄露事件，并提示敏感材料应改用本地方案

### 1.4 QTranslate（ahatem 重写版）

- 平台：Windows / macOS / Linux
- 开源：是，MPL-2.0（源码；GPL 主题单独分发）
- 价格：免费，接受赞助
- 功能：划词（Ctrl+Q）、原地替换、屏幕 OCR（Ctrl+I）、TTS、拼写检查、摘要与改写、回译、查词、文档翻译（DOCX / PDF / TXT / SRT / VTT）、RTL 布局、插件系统
- 本地/离线：官网明确「Point it at Ollama or LM Studio and nothing leaves your machine」；可接自托管 LibreTranslate；System Services 提供本机 OCR / 语音 / 拼写
- 密钥存储：插件通过 PluginContext 的 secrets 存取，与 settings / history / logs 一起存放在用户数据目录（安装模式写系统每用户目录，便携模式随文件夹）——非操作系统凭据管理器
- 活跃度【页】：269 star，最近提交 2026-10-26；官网 v1.5.0（2026-09-29）

### 1.5 CopyTranslator

- 平台：Windows / macOS / Linux（Electron）
- 开源：是，GPL-2.0【API】
- 价格：免费
- 功能：核心是「复制即翻译」与「拖拽复制」——监听剪贴板，不是系统级划词；专长在 PDF 复制的断行/换行修复、多段协作翻译、聚焦模式；v12.0.0 起支持大模型 AI 翻译
- 截图 OCR：无内置截图 OCR，需另配可选的离线 PaddleOCR 包
- 本地/离线：—（是否可指向本机 Ollama 未核实）
- 活跃度【API】：18,132 star，但最后 push 2026-02-23，约 8 个月未有新提交。Star 存量高与近期活跃度低同时存在，看数据时两列都要看

### 1.6 沙拉查词（Saladict）

- 平台：浏览器扩展（Chrome / Edge / Firefox），含内置 PDF 阅读器
- 开源：是（官网称 Open-sourced），许可证未核实
- 价格：免费
- 功能：网页与 PDF 内划词查词；聚合多部词典；生词本、查词历史；可导出到 WebDAV / 扇贝 / 欧路 / Anki Connect；多种触发模式
- 截图 OCR：无
- 本地/离线：—（依赖第三方词典与翻译服务）
- 活跃度：Chrome 商店记录版本 7.22.11（2026-10-21）【商店页】；无 GitHub 仓库数据，Star 记「—」
- 边界：只在浏览器与内建 PDF 阅读器内生效，覆盖不到桌面原生应用

### 1.7 欧路词典

- 平台：Windows / macOS / Linux / iOS / Android + 浏览器扩展
- 开源：否（无 GitHub 仓库，Star 记「—」）
- 价格：基础功能免费，VIP / AI 额度付费
- 功能：跨软件取词（LightPeek）、划词、拍照与截图翻译、AI 翻译与写作批改、生词本与复习计划、文档阅读、云同步
- 本地/离线：可下载/自制离线词库（MDict / 灵格斯 / Babylon 等格式），离线可查词；非本地大模型翻译
- 密钥存储：账号体系（云同步），非本地密钥
- 隐私模型：闭源商业，云端翻译与同步

### 1.8 DeepL 桌面版

- 平台：Windows / macOS（另有 iOS / Android / 网页）
- 开源：否（无 GitHub 仓库，Star 记「—」）
- 价格：免费版有额度；DeepL Pro 订阅。第三方整理（2026-10 查询）称免费版每月 5 万字符、每月 1 个文件翻译（≤5 MB），Pro Individual 约 €7.49/月【三方】
- 功能：选中文本翻译（具体触发方式未核实）、文档翻译、术语表
- 截图 OCR：无
- 本地/离线：—（桌面端需联网）
- 密钥存储：DeepL 账号
- 隐私模型：官方隐私政策称 Pro 用户文本仅作技术性临时保存、不用于训练 AI 模型；免费版完整 IP 最长保存 14 天。另有 2026 年起客户内容默认在 AWS 全球区域处理的架构变更说明

### 1.9 Google 翻译「桌面版」

- 平台：**无官方 Windows / macOS 原生桌面客户端**。Google 官方帮助社区回复（2024-09）明确「Google 不提供专门的 Windows 和 macOS 版 Google 翻译」，PC 上只能以网页 / PWA 形式使用
- 因此该列不构成桌面划词竞品，仅作为免费公共翻译接口的对照
- 开源：不适用；价格：网页版免费；Star：不适用

### 1.10 Windows 上的其它开源同类

- **STranslate**（STranslate/STranslate）：Windows / WPF，MIT，划词、截图 OCR（内置微信 OCR 可离线）、图片翻译、多服务并发、.NET 插件市场、HTTP API、TTS、生词本、自定义 Prompt。**活跃度【API】：8,442 star，archived=false，最后 push 2026-09-24**。与 PopGlot 同为 WPF、同为 Windows，是最直接的竞品。【三方】源码阅读结论：配置与 API 密钥以 JSON 保存在 %LocalAppData%\STranslate
- **eSearch**（xushengfeng/eSearch）：Windows / Linux / macOS，Electron，GPL-3.0【API】。截屏 + 离线 OCR（PaddleOCR）+ 搜索 + 翻译 + 贴图 + 以图搜图 + 滚动截屏 + 录屏 + 屏幕翻译，功能面比纯翻译工具宽得多。**活跃度【API】：7,371 star，最后 push 2026-10-24**。支持自定义翻译引擎与多模态大模型接口；是否可直接指向本机 Ollama / LM Studio 未核实
- **WinLens**（marco-beltrame）：Windows / C# WPF，开源（许可证未核实，Star 与 push 未取得，记「—」）。对屏幕文字做 OCR 并把译文覆盖回原位，适合游戏、原生应用、PDF、视频字幕等不可选文字；翻译后端支持 Google Translate、自建 LibreTranslate、Ollama / LM Studio
- **overlay-translator**（ciddwd）：Windows / C# .NET，开源（许可证未核实，Star 与 push 未取得，记「—」）。选区截图 → OCR（Windows Media OCR / PaddleOCR）→ 云 API 或本地 OpenAI 兼容服务（Ollama / LM Studio）→ 透明覆盖层渲染译文

---

## 2. 差异化结论

### 2.0 先划掉一条伪差异化

**「支持本地模型（Ollama / LM Studio）」已经不是差异化。** 可核实的清单：Pot 接口列表含 Ollama、QTranslate 官网明示 Ollama / LM Studio、STranslate 支持 Ollama、eSearch 支持自定义翻译引擎与多模态大模型接口、WinLens 与 overlay-translator 均支持 Ollama / LM Studio。任何文案都不得写「Windows 上唯一支持本地模型的划词翻译」。

剩下能站得住的，只剩**默认值**和**开关粒度**两层，不是能力有无。

### 2.1 三条站得住的差异化

**差异一：默认路径不出网，且有一个管到底的「总闸」。**
截图默认走本机 OCR，图片外发需要明确授权；开启「安全离线模式」后，含免费线路在内的所有外发请求都不发出，同时本机模型地址仍可用。
> 诚实边界一：这条的表述只能是「PopGlot 有这个开关」，不能是「只有 PopGlot 有」。我们未逐一核实竞品是否存在等价的总开关，因此对比表中也不能给竞品在「离线可用」一格打叉。
> 诚实边界二：真正的差异点是两处**默认值**——截图默认本机 OCR（而非默认走在线 OCR）、未配引擎时的免费兜底线路也被同一个开关管住（而非游离在开关之外）。这是默认配置与开关覆盖范围的差异。

**差异二：密钥与日志的处置落到操作系统层面。**
密钥存 Windows 凭据管理器，而不是写在应用自己的配置文件里；日志不记录原文、截图、密钥。可对照的是：STranslate 的密钥以 JSON 存在 %LocalAppData%\STranslate【三方源码阅读结论】，QTranslate 的 secrets 存在用户数据目录。
> 诚实边界：这是「存储位置」的差异，不等于对方不安全；且目前没有竞品把密钥写进 Windows 凭据管理器的公开证据（属「未见」，不是「确认没有」）。建议配套一份「如何自查密钥存放位置」的验证清单，把说法变成用户能自己跑一遍的动作。

**差异三：从「译出来」走向「读懂」，且不牺牲译文。**
工作台左原文右译文，译文下方是说明；「要点」是另一种阅读视角，不会清掉译文。竞品的主流形态是「多引擎并列对照」（Pot 多接口并行、QTranslate Comparison、Bob 最多 12 个服务并排）或「弹窗给一句译文」，PopGlot 给的是译文 + 说明的双层结构。
> 诚实边界：三条里这条最弱。它是产品形态差异，不是质量优势；没有可引用的译文质量对比数据，文案中不得暗示 PopGlot 译得更准。建议在 README 里配一张工作台截图自证，而不是靠形容词。

**备选第四条（若前三条在文案中显得单薄，可用）：安装与升级的零摩擦。**
Inno Setup 安装包覆盖升级自动探测旧位置、默认当前用户无需管理员、卸载保留用户数据；安装包与便携 zip 均自带 .NET，开箱即用。
> 诚实边界：QTranslate 的 Windows 包同样内含 Java 运行时，eSearch 为 Electron 自带运行时，因此「自带运行时」不成立；真正可说的是「覆盖升级自动探测旧位置」与「卸载保留用户数据」这两项安装器细节，但需要先确认竞品在这些细节上的实际表现，未核实前不要写成对比结论。

### 2.2 竞品更强的地方（诚实清单）

1. **平台覆盖**：PopGlot 只做 Windows。Pot、QTranslate、CopyTranslator、eSearch 覆盖 Windows / macOS / Linux；沉浸式翻译、沙拉查词覆盖浏览器全平台；欧路、DeepL 覆盖桌面与移动端。macOS 与 Linux 用户直接被排除在受众之外
2. **功能广度**：生词本与 Anki / 欧路导出（Pot、沙拉查词、STranslate）、TTS 朗读（Pot、QTranslate、STranslate、Bob）、文档整篇翻译（QTranslate 的 DOCX/PDF/SRT、沉浸式翻译的 PDF/EPUB/字幕）、图片翻译与原位覆盖（Bob 原图翻译、STranslate 图片翻译、WinLens / overlay-translator / eSearch 屏幕翻译覆盖层）、录屏与以图搜图（eSearch），PopGlot 目前均未覆盖
3. **插件与可扩展性**：Pot 的插件系统 + 本机 HTTP 接口、STranslate 的 .NET 插件市场 + HTTP API + SnipDo 集成、QTranslate 的运行时可插拔插件，生态厚度明显更高
4. **离线词典**：欧路可下载/自制海量离线词库，Pot 有 300 万词条的离线英汉词典插件，PopGlot 没有词典能力
5. **社区与知名度**：Pot 19,348 star、CopyTranslator 18,132 star、沉浸式翻译 19,525 star、STranslate 8,442 star、eSearch 7,371 star（均【API】，2026-10-29）。PopGlot 目前没有可引用的 Star 量级、用户数、评分，冷启动是真实劣势
6. **维护可持续性风险是双向的**：品类里已经有多个高 Star 项目进入归档或长期停更状态（Pot 组织整体 archived、CopyTranslator 最后 push 2026-02-23）。读者会用同一把尺子审视 0.1.x、单人维护的 PopGlot。文案中不得攻击对手的维护状态，只能陈述自己的发布节奏
7. **触发方式丰富度**：沙拉查词支持图标、双击、修饰键、悬浮等多种取词模式；Pot 支持剪贴板监听；PopGlot 目前以全局快捷键为主

---

## 3. 目标用户画像

### 画像一：读外文一手材料的知识工作者（研究员 / 工程师 / 分析师 / 学生）

- 场景：英文论文、技术文档、竞品页面、财报 PDF、代码注释里遇到不好懂的段落或长句
- 痛点：复制到网页翻译要切窗口，思路被打断；桌面 PDF 阅读器里的文字浏览器插件够不到；截图里的文字没法直接复制
- 现有工具不够：浏览器扩展只覆盖网页；复制型工具（CopyTranslator 类）要先复制再等结果；多引擎并列给了好几版译文，但不解释「为什么这么译」
- PopGlot 的切入点：划词 Ctrl+Alt+W、截图 Ctrl+Alt+Space 就地出结果，工作台给出译文 + 说明

### 画像二：有外发合规约束的岗位（法务 / 医疗 / 金融 / 政企与外包开发）

- 场景：合同条款、病历、客户数据、未发布的代码与内部文档
- 痛点：原文不能发给第三方；但完全离线又没有翻译可用；截图比文本更容易被默认上传
- 现有工具不够：多数工具默认把文本/图片交给在线 OCR 与在线引擎，离线方案要逐个服务改配置，改漏一处就外发；日志是否留下原文无从自查
- PopGlot 的切入点：截图默认本机 OCR，图片外发需明确授权；「安全离线模式」一键切断含免费线路在内的全部外发，本机模型地址仍可用；日志不记原文、截图、密钥

### 画像三：已有自建网关或本机模型的开发者

- 场景：手上已有 OpenAI 兼容网关、Anthropic / Gemini 直连，或本机跑着 Ollama / LM Studio
- 痛点：很多工具只给一个 OpenAI 输入框，Responses API 与各厂原生协议要绕代理；密钥明文写在配置文件里，换机器要重新填，也怕被同步到云端
- 现有工具不够：本地模型常被当作「又一个可选引擎」而非一等公民；密钥存储位置不透明
- PopGlot 的切入点：OpenAI 兼容 / OpenAI Responses / Anthropic / Gemini 四类协议 + Ollama、LM Studio 本机地址；密钥进 Windows 凭据管理器

---

## 4. 一句话定位

- 中文（面向 Windows，24 字）：**给不便外发原文的人：划词截图本机译，一键断网照样用。**
- 英文（14 words）：**For Windows users who can't send text out: select, snip, translate locally — offline included.**

拆解对照：

| 要素 | 中文 | 英文 |
| --- | --- | --- |
| 给谁 | 不便外发原文的人（Windows 用户） | Windows users who can't send text out |
| 解决什么 | 划词 / 截图就地翻译 | select, snip, translate |
| 凭什么信 | 本机译、一键断网照样用 | locally, offline included |

---

## 5. 竞品对比表

符号约定：`✓` 具备；`△` 具备但需额外配置或仅覆盖部分环节；`—` 无，或公开资料未证实（不猜测）

Star / 最后 push / 许可证三行的数据来源标注：**【API】** 2026-10-29 查自 GitHub REST API；**【页】** 2026-10-29 查自 GitHub 仓库页面。未取得官方数据的写「—」。

### 5.1 跨平台与主流竞品

| 维度 | PopGlot | Pot | Bob | 沉浸式翻译 | QTranslate | CopyTranslator | eSearch | 沙拉查词 | 欧路词典 | DeepL 桌面版 | Google 翻译桌面 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 平台 | Windows | Windows / macOS / Linux | macOS | 浏览器扩展 | Windows / macOS / Linux | Windows / macOS / Linux | Windows / macOS / Linux | 浏览器扩展 | Windows / macOS / Linux / iOS / Android | Windows / macOS | 无官方桌面客户端，仅网页 / PWA |
| 划词 | ✓ | ✓ | ✓ | ✓（网页与 PDF 内） | ✓ | △（拖拽复制，非系统划词） | △（框选后 OCR，非系统划词） | ✓（网页与 PDF 内） | ✓（跨软件取词） | ✓（触发方式未核实） | — |
| 截图 OCR | ✓（默认本机 OCR） | ✓（系统 OCR / 插件） | ✓（含 macOS 11+ 离线识别） | △（图片 / 漫画翻译，非屏幕截图） | ✓（System Services / Google Lens 插件） | —（需另配离线 OCR 包） | ✓（PaddleOCR 离线，开箱即用） | — | ✓（截图与拍照翻译） | — | — |
| 本地模型 | ✓（Ollama、LM Studio 本机地址） | ✓（接口列表含 Ollama） | —（未核实） | —（未核实） | ✓（Ollama / LM Studio） | —（未核实） | △（支持自定义翻译引擎与多模态大模型接口；是否含本机 Ollama / LM Studio 未核实） | — | — | — | — |
| 离线可用 | ✓（安全离线模式切断全部外发；需自配本机模型才有翻译，免费线路停用） | ✓（Ollama + 系统 OCR + 离线词典插件） | △（OCR 侧离线识别） | — | ✓（本机 OCR + 自托管 LibreTranslate / 本地模型） | △（配离线 OCR 包可识别） | △（离线 OCR；翻译仍需引擎） | — | △（离线词库可查词，非大模型翻译） | — | — |
| 密钥存储 | Windows 凭据管理器 | —（未核实） | —（未核实） | —（未核实） | 用户数据目录的 secrets（非系统凭据管理器） | —（未核实） | —（未核实） | —（未核实） | 账号体系（云同步） | DeepL 账号 | 不适用 |
| 开源许可 | MIT | GPL-3.0【API】 | 否（社区版曾开源，已停止维护） | 无开源许可（GitHub license=none【API】；仓库声明不含源代码，旧开源版 2023-01-17 归档） | MPL-2.0 | GPL-2.0【API】 | GPL-3.0【API】 | 是（许可未核实） | 否 | 否 | 不适用 |
| 价格 | 免费，无付费版 | 免费 | 商店版内购终身买断；社区版免费 | 免费增值（【三方】Pro $69/年，2026-04） | 免费，接受赞助 | 免费 | 免费 | 免费 | 免费基础 + VIP 付费 | 免费有额度 + Pro 订阅（【三方】Individual 约 €7.49/月，2026-10） | 网页版免费 |
| Star（2026-10-29） | — | 19,348【API】 | — | 19,525【API】 | 269【页】 | 18,132【API】 | 7,371【API】 | — | — | — | 不适用 |
| 最后 push（2026-10-29） | 0.1.35，2026-10-29 发布 | 2026-07-04，仓库 archived=true【API】 | — | 2026-10-23【API】 | 2026-10-26【页】 | 2026-02-23【API】 | 2026-10-24【API】 | — | — | — | 不适用 |

### 5.2 Windows 桌面开源同类

| 维度 | PopGlot | STranslate | WinLens | overlay-translator |
| --- | --- | --- | --- | --- |
| 平台 | Windows | Windows | Windows | Windows |
| 划词 | ✓ | ✓ | △（屏幕 OCR，不依赖划词） | △（屏幕 OCR 覆盖层，不依赖划词） |
| 截图 OCR | ✓（默认本机 OCR） | ✓（内置微信 OCR 可离线） | ✓ | ✓（Windows Media OCR / PaddleOCR） |
| 本地模型 | ✓（Ollama、LM Studio 本机地址） | ✓（Ollama） | ✓（Ollama / LM Studio） | ✓（Ollama / LM Studio） |
| 离线可用 | ✓（安全离线模式切断全部外发；需自配本机模型才有翻译） | △（离线 OCR；翻译需自配本地模型 / 接口） | △（离线 OCR；翻译需自配本地模型） | △（离线 OCR；翻译需自配本地模型） |
| 密钥存储 | Windows 凭据管理器 | 本地 JSON（%LocalAppData%\STranslate，【三方】源码阅读结论） | —（未核实） | —（未核实） |
| 开源许可 | MIT | MIT【API】 | 是（许可未核实） | 是（许可未核实） |
| 价格 | 免费，无付费版 | 免费 | 免费 | 免费 |
| Star（2026-10-29） | — | 8,442【API】 | — | — |
| 最后 push（2026-10-29） | 0.1.35，2026-10-29 发布 | 2026-09-24，archived=false【API】 | — | — |

> 读法提醒：STranslate、WinLens、overlay-translator、eSearch 与 PopGlot 在「本地模型 + 离线 OCR」这一组合上高度重合，属于直接竞争。README 里只能就「默认不出网的默认值」与「密钥/日志处置」这两点做区分，不能宣称「唯一支持本地模型」。

---

## 6. 机会窗口：Pot 官方仓库归档意味着什么

**可验证的事实（2026-10-29 查自 GitHub REST API）**：pot-app 组织下 15 个仓库全部 `archived=true`，其中 pot-desktop 为 19,348 star、最后 push 2026-07-04。作者官网公告不再维护，并以新作品继续开发。

**这对 PopGlot 的推广意味着三件事（中性陈述）：**

1. **品类需求是真实且持续的。** 一个划词翻译工具能积累到 19,348 star，说明「在桌面上选中文字就地翻译」这个需求有稳定的受众规模。PopGlot 不需要教育市场，只需要说明自己存在。
2. **「开源划词翻译工具」这个关键词下，正在出现一个供给缺口。** 目前档案状态与活跃状态并存：Pot 已归档、CopyTranslator 最后 push 2026-02-23；STranslate（2026-09-24）、eSearch（2026-10-24）、QTranslate（2026-10-26）仍活跃。对正在搜索替代方案的用户来说，候选列表正在重排，这是一个可以被新项目进入的窗口。
3. **沟通重点应落在「我是什么」，而不是「谁不行」。** 归档是一个中性的项目状态，不说明产品不可用，也不构成对 Pot 的任何评价。文案里只陈述 PopGlot 自己的事实（Windows、MIT、默认本机 OCR、安全离线模式、密钥进凭据管理器），把判断留给读者。

**必须一起写清的风险：**

- **归档不等于用户立刻流失。** Pot 的翻译能力来自用户自己配置的引擎，不依赖官方服务器，已安装用户可以继续长期使用，感知不到归档。
- **19,348 star 是存量用户基数的信号，不是空出来的市场。** 这些用户并没有被释放出来等待新工具。
- **他们更可能流向已在活跃维护的替代品。** STranslate（8,442 star，同为 Windows + WPF，最近 push 2026-09-24）在平台与技术栈上与 Pot 的重叠度最高，eSearch（7,371 star，跨平台，功能面更宽）也是现成选项。PopGlot 是三者之外的新名字，且 Star 量级无法引用。
- **同一个风险会反噬我们自己。** 读者看到品类里已有项目归档，会用同一把尺子看 0.1.x、单人维护的 PopGlot。因此 README 里应该给出可验证的维护信号（发布节奏、issue 响应、路线图），而不是回避这个话题。

**结论**：这是一个可以进入的窗口，不是一个可以收割的窗口。推广目标应该是「被看见、被试用」，不是「承接 Pot 的用户」。

---

## 7. 供文案使用的安全表述与禁用表述

可以用（有事实依据）：

- 「截图默认本机识别，图片外发需要你点头」
- 「安全离线模式：一键关掉所有外发请求，含免费线路」
- 「密钥交给 Windows 凭据管理器保管，日志不记原文、截图、密钥」
- 「未配置引擎时也能用，免费线路走不通会自动切换」
- 「覆盖升级会自动找到旧安装位置，卸载保留你的数据」
- 「MIT 许可，外壳 WPF，内核 Rust，两边用 C ABI 分开」

不可以用：

- 任何形式的「最 / 第一 / 唯一 / 碾压 / 吊打」
- 任何用户数、下载量、评分、性能或译文质量对比数字
- 「Windows 上唯一支持本地模型的划词翻译」（STranslate、WinLens、overlay-translator 均可接 Ollama / LM Studio，eSearch 支持自定义翻译引擎）
- 「竞品已停更 / 已死 / 快来迁移」——Pot 归档是中性事实，不构成对产品的评价，也不代表用户需要迁移
- 未经验证的跨平台承诺

---

## 8. 待补

> 待补：产品官网。当前无官网，README 与推广物料只能指向 GitHub 仓库，缺少「非开发者用户」的落地页
> 待补：Demo 动图 / 录屏。划词、截图、工作台、安全离线模式这四处若无可观素材，「凭什么信」只能靠文字，转化会打折
> 待补：一份可自查的验证清单（例如「如何在你的机器上确认密钥确实进了凭据管理器」「如何确认日志里没有原文」），把隐私承诺从声明变成用户能自己跑一遍的动作
> 待补：竞品「是否也存在等价的总开关」未逐一核实。在核实之前，差异一只能表述为「PopGlot 有这个开关」，不能表述为「只有 PopGlot 有」
> 待补：Bob、WinLens、overlay-translator 的 GitHub Star 与最后 push 尚未用 REST API 取得，表内记「—」；如需进 README，补测后填入
> 待补：eSearch 是否可直接指向本机 Ollama / LM Studio 未核实，表内记「△」
> 待补：Pot、CopyTranslator、STranslate、eSearch 是否有等价的「安全离线模式」总开关，未核实
