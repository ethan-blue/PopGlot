# PopGlot Design System & UI/UX Guidelines (v0.1.3+)

本文档定义 PopGlot Windows 客户端的完整设计规范、色彩系统（Design Tokens）、字体层级、网格排版自适应补偿机制及地道中文（去 AI 味 / 去翻译腔）交互文案规范。

> **事实来源**：色彩 token 的唯一权威定义在 `apps/PopGlot.Windows/ThemeService.cs`（`DarkTokens` / `LightTokens`），由测试 `ThemeAuditHelper.RunAudits` 强制对比度预算。本文档是节选说明；两者不一致时以代码为准并修文档。

---

## 目录
1. [色彩体系 (Color Tokens & WCAG AA 对比度)](#一色彩体系)
2. [字体栈与排版矩阵 (Typography Hierarchy)](#二字体栈与排版矩阵)
3. [多语言排版补偿机制 (Multi-Language Layout Compensation)](#三多语言排版补偿机制)
4. [去 AI 化交互文案辞典 (De-AI Copywriting Guidelines)](#四去-ai-化交互文案辞典)
5. [核心组件结构规范 (Component Anatomy)](#五核心组件结构规范)

---

## 一、色彩体系

PopGlot 采用高对比度、低视觉噪音的暗色与亮色调色盘：中性底色（60%）、次级容器与卡片（30%）、克制的低饱和蓝紫强调（10%）。文本 token 对相邻表面的对比度由测试强制 ≥4.5:1，关键控件边界 ≥3:1（AI-RULES §7.1）。

### 1.1 Dark Mode 设计令牌（与 ThemeService.cs 逐值一致）

| Token Name | Hex Code | 作用与语义 |
| :--- | :--- | :--- |
| `CanvasBrush` | `#101216` | 主窗口底色 |
| `SidebarBrush` | `#14171E` | 侧边栏与底栏背景 |
| `SurfaceBrush` | `#181B22` | 卡片容器背景 |
| `SurfaceMutedBrush` | `#14161D` | 列表背景、只读卡片 |
| `SurfaceRaisedBrush` | `#20242E` | 下拉浮层、二级悬浮卡片 |
| `SurfaceHoverBrush` | `#272C38` | 控件悬停态 |
| `SurfacePressedBrush` | `#353C4D` | 控件按下态 |
| `InputBrush` | `#181B22` | 文本输入区域底色 |
| `BorderSubtleBrush` | `#2D3342` | 次级分割线与边框 |
| `BorderStrongBrush` | `#6B768D` | 输入框与控件外轮廓（≥3:1） |
| `PrimaryBrush` | `#5562B3` | 主操作按钮底色（深一档品牌蓝，配白字 AA） |
| `PrimaryHoverBrush` | `#5B69BE` | 主操作按钮悬停 |
| `PrimaryPressedBrush` | `#4B579F` | 主操作按钮按下 |
| `PrimaryTextBrush` | `#F7F8FC` | 主操作按钮文字（≥4.5:1，含 hover/pressed） |
| `AccentBrush` | `#7C89D9` | 品牌高亮/链接/选中（不做按钮底色） |
| `AccentSoftBrush` | `#20243A` | 徽章/高亮衬底 |
| `AccentBorderBrush` | `#59649D` | 选中软边框 |
| `FocusBrush` | `#7C89D9` | 焦点环（对画布/表面 ≥3:1，区别于选中软边框） |
| `TextPrimaryBrush` | `#EEF0F4` | 一级正文/标题文字 |
| `TextSecondaryBrush` | `#A8B0BD` | 次级说明/副标题 |
| `TextTertiaryBrush` | `#939BAA` | 占位符/元数据/时间戳（≥4.5:1） |
| `TextDisabledBrush` | `#565F6E` | 禁用态文本 |
| `DangerBrush` | `#FF6B7D` | 危险/删除/报错文字 |
| `DangerSoftBrush` | `#401C25` | 危险状态衬底 |
| `DangerHoverBrush` | `#52222E` | 危险按钮悬停底（配 DangerHoverText 红字，暗色不用白字压亮红） |
| `SuccessBrush` | `#3DD68C` | 成功/健康状态 |
| `SuccessSoftBrush` | `#143826` | 成功状态衬底 |
| `WarningBrush` | `#F2B95C` | 警告/未保存修改 |
| `WarningSoftBrush` | `#3D2D14` | 警告状态衬底 |

亮色主题对应值（Canvas `#F6F7F9`、Primary `#5260B5`、Accent `#5563B8` 等）见 `ThemeService.LightTokens`，同样由测试强制对比度。

> 历史：v0.1.1 文档曾记录另一套 `#0A0B0F / #2563EB / #4D9FFF`（亮蓝大强调）配色，与当时实现即不一致；当前方向在 v0.1.3 整改中锁定为上表并纳入自动化审计。

---

## 二、字体栈与排版矩阵

### 2.1 字体栈 (Font Stack)
* **UI 界面字体**：`Segoe UI Variable Text, Segoe UI, "Microsoft YaHei UI", sans-serif`
* **等宽/代码/快捷键**：`Cascadia Mono, Consolas, "Courier New", monospace`

### 2.2 层级矩阵 (Hierarchy Matrix)

| 语义角色 | 字号 (px) | 行高 (Line-height) | 字重 (Weight) | 推荐色值 (Dark) | 适用场景 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **PageTitle (H1)** | 20px | 28px (1.40) | SemiBold (600) | `#EEF0F4` | 页面/窗口顶栏大标题 |
| **SectionTitle (H2)** | 13px | 18px (1.38) | SemiBold (600) | `#EEF0F4` | 模块分组标题（大写/强语义） |
| **RowTitle (Subhead)** | 13px | 19px (1.46) | Medium (500) | `#EEF0F4` | 设置项主名称、列表条目标题 |
| **Body (正文)** | 13px | 20px (1.54) | Regular (400) | `#EEF0F4` | 标准正文段落 |
| **Content Large** | 14.5px | 22px (1.51) | Regular (400) | `#EEF0F4` | 翻译结果展示区域 |
| **Caption (说明)** | 12px | 17px (1.42) | Regular (400) | `#A3A9B4` | 控件下方解释提示、空状态说明 |
| **Metadata (元数据)** | 11px | 16px (1.45) | Regular (400) | `#8A93A2` | 时间戳、字符计数、路由徽章 |
| **Kbd / Token** | 12.5px | 18px (1.44) | Medium (500) | `AccentBrush`（代码词条用 `TextSecondaryBrush`） | 快捷键录制框、保护代码词条 |

---

## 三、多语言排版补偿机制

针对中西文在排版特征上的差异，系统引入以下自适应补偿规则：

1. **中文方块字密度与行高补偿**：
   * 中文方块字字框饱满，无西文的上升部/下降部（Ascender/Descender），行高统一设定为 **1.45 ~ 1.54 倍**，防止多行文本产生粘连感。
2. **中西文混排盘古空格 (Pangu Spacing)**：
   * 中文字符与英文单词、数字之间自动保留 **0.05em 半角间隙**（如 `PopGlot 桌面翻译`、`耗时 120ms`），提升技术文档与代码报错的可读性。
3. **字符长度膨胀/缩减补偿**：
   * **EN → ZH**：中文短句缩短 35%~50%，按钮设置 `MinWidth="72px"` 配合弹性 Padding，防止两字操作按钮（如“翻译”、“删除”）过窄失真。
   * **ZH → EN**：西文长句膨胀 40%~60%，单行预览强制启用 `TextTrimming="CharacterEllipsis"`，状态栏采用 `Grid` 弹性列与固定列分离，严禁互相挤压重叠。

---

## 四、去 AI 化交互文案辞典

严格执行**动词先行、短句优先、剔除废话介词（基于/关于/为了/通过）、消除拟人化 AI 腔调**的原则。

| 原文 / AI 式冗余腔调 | 优化后地道文案 | 优化原则 |
| :--- | :--- | :--- |
| 配置翻译使用的 AI 模型或中转服务。配置保存后对后续翻译生效。 | 接入模型或中转接口，保存即时生效。 | 动词先行，去除啰嗦主谓说明 |
| 输入或粘贴要翻译的内容，Enter 立即翻译，Shift+Enter 换行… | 输入文本，Enter 翻译，Shift+Enter 换行… | 极简短句，提升操作引导速度 |
| 输入任意单词、长句、代码报错，回车立即翻译… | 搜索单词、句子或报错信息… | 提炼核心对象，移除废话助词 |
| 还没有配置翻译引擎。添加一个翻译引擎… | 未配置翻译引擎。<br>添加接口并设为默认即可开始翻译。 | 标题+说明分离，消除口语化“还没有” |
| 同一模型同时用于文字与图片（配置了文字模型即启用文字路线…） | 图文共用此模型<br>填入模型即启用对应功能。 | 削减 65% 废话解释，突出逻辑结果 |
| 通常无需修改；仅在中转服务使用不同接口路径时调整。 | 默认免配置；仅自建中转需修改。 | 笃定陈述，消除“通常无需”模糊词 |
| 请求头每行填写一项“名称: 值”；认证头由 PopGlot 安全管理。 | 每行格式为 Header: Value；认证头自动接管。 | 专业术语化，强化安全接管属性 |
| 点击别处自动关闭浮窗（关闭后浮窗会一直停留…） | 失焦自动关闭浮窗<br>关闭后保持常驻，需手动关闭。 | 短语结构化，去除解释性废话 |
| 翻译完成后自动复制译文（译文会写入剪贴板…） | 译后自动复制<br>翻译完成自动写入剪贴板。 | 6 字凝练动作核心 |
| 附带用法说明与语境解析（让模型在译文后方补充…） | 输出语境解析<br>补充语气、歧义与专业术语说明。 | 剔除“让模型…”拟人化 AI 腔调 |
| 保护代码标识符与变量名（翻译前自动遮蔽变量…） | 保护代码标识符<br>自动遮蔽变量与路径，译后精确还原。 | 动作提炼，突出还原确定性 |
| 总开关：开启后无论其他设置如何，都不会有任何外发请求 | 禁用一切网络请求（完全离线） | 明确最高优先级，消除口语助词 |
| 零配置兜底：只在「服务」页没有任何可用服务… | 无自定义服务时代替兜底。仅发送脱敏文本，不含截图与凭据。 | 逻辑分行阐述，严密严谨 |
| 最多 200 条，保留 90 天；疑似密钥与过大内容不记录 | 最多 200 条，保留 90 天；自动过滤密钥与超长文本。 | 将“疑似…不记录”改为主动态“自动过滤” |
| 拖动框选要翻译的区域 · Esc 或右键取消 · 按住 Shift 仅提取文字 (OCR) | 框选翻译区域 · Esc 取消 · 按住 Shift 仅提取文本 | 消除口语化“拖动”，符号化提炼快捷键 |

---

## 五、核心组件结构规范

### 5.1 圆角系统 (Corner Radii)
* **小控件 (Buttons / Inputs / Badges)**：`CornerRadius="6"`
* **容器/卡片 (Cards / Panes / Lists)**：`CornerRadius="10"`
* **独立浮窗 (Popups / Quick Search / Translation Panel)**：`CornerRadius="12"`
* **胶囊徽章 (Status Pills)**：`CornerRadius="10"` 或 `CornerRadius="15"`（圆形）

### 5.2 间距网格 (Spacing Grid)
* 基础网格基准为 **4px / 8px**：
  * 微间距 (Micro): `4px`, `6px`, `8px`（图标与文字间距、徽章内边距）
  * 组件内间距 (Component Padding): `10px`, `12px`, `14px`, `16px`
  * 模块间距 (Section Margin): `14px`, `18px`, `20px`, `28px`
