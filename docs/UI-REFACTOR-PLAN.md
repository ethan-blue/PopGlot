# PopGlot UI 重构计划

> **创建时间**: 2026-08-29  
> **状态**: 🟡 进行中  
> **目标**: 对标 Pot / Bob / DeepL 等优秀翻译工具，重构 UI 架构与视觉设计

---

## 一、背景与动机

PopGlot 当前 UI 存在以下核心问题：

| 问题 | 严重度 | 说明 |
|------|--------|------|
| MainWindow.xaml 1092 行巨石 | 🔴 关键 | 7 个设置板块全部内联在一个文件中 |
| MainWindow.xaml.cs 1791 行 god class | 🔴 关键 | 翻译、历史、生词本、服务配置、OCR、主题切换等所有逻辑混在一起 |
| TranslationPanelWindow 布局不够清晰 | 🟡 中等 | 没有对标主流翻译工具的分区设计（源文本/语言栏/结果卡片） |
| 硬编码颜色值 | 🟡 中等 | CaptureOverlay、FloatingTrigger 等窗口有硬编码的颜色和像素值 |
| 图标资源重复 | 🟡 中等 | 图标在 MainWindow.xaml 和 Controls.xaml 中重复定义 |
| 零 MVVM | 🟢 可接受 | 本轮不做 MVVM 迁移，保持 code-behind 模式 |

---

## 二、对标研究结论

研究了 Pot、Bob、DeepL、QTranslate、有道词典等工具后，总结出翻译弹窗的**通用架构**：

```
┌──────────────────────────────────────────┐
│ [📌 Pin]              [drag area]   [✕]  │  Title bar (35px)
├──────────────────────────────────────────┤
│ Source text (editable, auto-resizing)     │
│ [🔊 TTS][📋 Copy][🗑 Clear]  ·EN·  [⟳] │  Source action bar
│──────────────────────────────────────────│
│ [Auto ▾] ──── ⇄ ──── [中文 ▾]           │  Language selector bar
│──────────────────────────────────────────│
│ ┌─ Provider Name ─────────────────────┐  │  Result card
│ │ Translation result text              │  │
│ │ [🔊 TTS][📋 Copy][⭐ Save]          │  │
│ └─────────────────────────────────────┘  │
└──────────────────────────────────────────┘
```

### 关键设计模式

1. **Pin 钉住**: 灰色 = 失焦自动关闭；蓝色 = 常驻
2. **失焦关闭带 100ms 容差**: 防止拖拽时误关
3. **语言选择栏**: 源语言/目标语言下拉 + 交换按钮
4. **结果卡片化**: 每个 Provider 结果独立一张卡片，独立加载指示器、TTS、复制按钮
5. **检测语言 Badge**: 在源文本区域显示自动检测到的语言标签
6. **删除换行按钮**: 一键合并 PDF 断行段落

---

## 三、重构任务清单

### ✅ Task 0: 前期准备（已完成）
- [x] 完整阅读所有 UI 文件（MainWindow, TranslationPanel, QuickSearch, Controls, ThemeService）
- [x] 对标研究（Pot / Bob / DeepL / QTranslate / 有道词典）
- [x] 明确 theme token 体系（ThemeService.cs 中 26 个语义 token）

### 🟡 Task 1: 拆分 MainWindow 为 UserControls（进行中）

**目标**: MainWindow.xaml 从 1092 行降到 ~200 行

**要创建的文件**:
```
apps/PopGlot.Windows/Sections/
├── TranslateSection.xaml      + .cs    (快速翻译工作台)
├── LibrarySection.xaml        + .cs    (翻译历史 + 生词本)
├── GeneralSection.xaml        + .cs    (浮窗行为、主题、启动)
├── ShortcutsSection.xaml      + .cs    (全局快捷键录制)
├── ServicesSection.xaml        + .cs    (服务配置 + 编辑器 + 向导)
├── PrivacySection.xaml        + .cs    (OCR 与隐私/出网策略)
└── DataSection.xaml           + .cs    (数据管理与高级)
```

**XAML 行数分布** (原 MainWindow.xaml):
- TranslateSection: ~145–238 行
- LibrarySection: ~240–420 行
- GeneralSection: ~422–515 行
- ShortcutsSection: ~517–600 行
- ServicesSection: ~602–810 行
- PrivacySection: ~812–950 行
- DataSection: ~952–1060 行
- 侧边栏 + 导航 + 标题栏 + 底栏: 保留在 MainWindow

**代码迁移规则**:
- 每个 Section 的 XAML 原样移入 UserControl
- 对应的事件处理器从 MainWindow.xaml.cs 移入 Section 的 code-behind
- **保留所有 x:Name** — 测试依赖它们
- 命名空间: `PopGlot.Windows.Sections`
- 跨 Section 通信用事件:
  - `event Action<string, string>? LoadToTranslate` (Library → Translate)
  - `event Action? SettingsChanged` (any → MainWindow)

**共享样式处理**:
- `RowTitle`, `RowHint`, `SettingsRow` 原来定义在 MainWindow.Resources 中
- 需迁移到 `Controls.xaml` 成为全局样式，所有 Section 可用

**共享服务注入**:
```csharp
// 每个 Section 的构造函数接收需要的服务
public TranslateSection(TranslationCoordinator coordinator, TtsService tts, HistoryStore history) { ... }
public LibrarySection(HistoryStore history, VocabularyStore vocabulary, TtsService tts) { ... }
public ServicesSection(ProfileManager profiles, CoreBridge bridge) { ... }
// CoreBridge 是 static class，无需注入
```

**MainWindow 改造后结构**:
```xml
<Window>
  <WindowChrome ... />
  <Grid>
    <!-- 侧边栏（保留） -->
    <Border> ... sidebar nav ... </Border>
    <!-- 内容区 -->
    <Grid Grid.Column="1">
      <ScrollViewer>
        <Grid>
          <sections:TranslateSection x:Name="TranslateSection" />
          <sections:LibrarySection x:Name="LibrarySection" Visibility="Collapsed" />
          <sections:GeneralSection x:Name="GeneralSection" Visibility="Collapsed" />
          <sections:ShortcutsSection x:Name="ShortcutsSection" Visibility="Collapsed" />
          <sections:ServicesSection x:Name="ServicesSection" Visibility="Collapsed" />
          <sections:PrivacySection x:Name="PrivacySection" Visibility="Collapsed" />
          <sections:DataSection x:Name="DataSection" Visibility="Collapsed" />
        </Grid>
      </ScrollViewer>
      <!-- 底栏（保留） -->
    </Grid>
  </Grid>
</Window>
```

### ⬜ Task 2: 重设计 TranslationPanelWindow（待做）

**目标**: 对标 Pot 的翻译弹窗架构

**当前问题** (`TranslationPanelWindow.xaml`, 302 行):
- 源文本和译文区没有清晰的视觉分隔
- 语言选择没有交换按钮（弹窗里）
- 结果区没有 Provider 卡片化设计
- Pin 按钮功能存在但视觉不够直观
- 缺少 "删除换行" 按钮

**当前问题** (`TranslationPanelWindow.xaml.cs`, 993 行):
- 职责过多：划词读取、截图 OCR、手动输入、定位、TTS、生词本、焦点管理、动画
- 应拆分为: 翻译逻辑 + UI 展示

**改造方案**:
```
┌─ Title Bar ──────────────────────────────┐
│ [📌 Pin]    PopGlot 翻译    [─ ✕]        │
├──────────────────────────────────────────┤
│ ┌─ Source ─────────────────────────────┐ │
│ │ (源文本，可编辑，自动调高)            │ │
│ └──────────────────────────────────────┘ │
│ [🔊][📋][🗑][合并换行]  ·English·  [⟳] │
├──────────────────────────────────────────┤
│ [自动 ▾] ─────── ⇄ ─────── [中文 ▾]    │
├──────────────────────────────────────────┤
│ ┌─ 翻译结果 Card ─────── loading... ──┐ │
│ │ (RichTextBox / Markdown rendered)    │ │
│ │ [🔊][📋][⭐]                         │ │
│ └──────────────────────────────────────┘ │
│ ┌─ 用法说明 Card (可选) ──────────────┐ │
│ │ 补充语气、歧义或术语提示             │ │
│ └──────────────────────────────────────┘ │
└──────────────────────────────────────────┘
```

**关键改动**:
1. 源文本区: 可编辑 TextBox，底部动作栏（TTS/复制/清空/合并换行/语言Badge）
2. 语言选择栏: 源/目标语言 ComboBox + 交换按钮 ⇄
3. 结果卡片: 带 Provider 名称标题，独立 TTS/复制/收藏按钮
4. 用法说明: 可选折叠卡片
5. 所有颜色走 theme token
6. Pin 按钮: 未钉 = 灰色图钉 + 失焦自动关闭; 已钉 = 高亮图钉 + 常驻

### ⬜ Task 3: 打磨 QuickSearchWindow（待做）

**目标**: 对齐新翻译弹窗风格

**当前状态** (`QuickSearchWindow.xaml`, 139 行):
- Spotlight 样式搜索栏，功能正常
- 需要统一卡片样式、按钮布局

**改动点**:
- 结果区域使用与 TranslationPanelWindow 相同的卡片样式
- 统一动作按钮位置（TTS/复制/收藏）
- 确保视觉一致性

### ⬜ Task 4: 清理与打磨（待做）

**子任务**:
1. **图标去重**: 把 MainWindow.xaml.Resources 中的图标几何体 (IconTranslate, IconKeyboard, IconProvider, IconCapture, IconHistory, IconLibrary) 迁移到 Controls.xaml，删除重复定义
2. **硬编码颜色**: 
   - `CaptureOverlayWindow.xaml`: `#8C0A0D14`, `#66FFFFFF`, `#F2151A23` → 使用 theme token
   - `FloatingTriggerWindow.xaml`: 硬编码像素偏移 → 命名常量
   - `QuickSearchWindow.xaml`: `Width="680" MaxHeight="600"` → 考虑 DPI 适配
3. **统一行为**: 失焦关闭的 100ms 容差统一应用到所有弹窗

---

## 四、技术约束

1. **不破坏现有功能**: 39 个测试必须全部通过
2. **保持 code-behind 模式**: 本轮不做 MVVM 迁移
3. **所有颜色必须使用 DynamicResource theme token**: 参见 ThemeService.cs 中的 26 个 token
4. **保留 WindowChrome 标题栏**: MainWindow 的统一标题栏
5. **命名空间**: 新 Section → `PopGlot.Windows.Sections`
6. **TFM**: `net10.0-windows10.0.19041.0`
7. **验证命令**: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1`

---

## 五、可用 Theme Token（ThemeService.cs）

```
Canvas          → CanvasBrush          (#0B0D11 / #F8FAFC)
Sidebar         → SidebarBrush         (#101318 / #FFFFFF)
Surface         → SurfaceBrush         (#15181F / #FFFFFF)
SurfaceAlt      → SurfaceAltBrush      (#1B202A / #F1F5F9)
SurfaceHover    → SurfaceHoverBrush    (#232936 / #E2E8F0)
SurfacePressed  → SurfacePressedBrush  (#2C3444 / #CBD5E1)
Input           → InputBrush           (#11141A / #FFFFFF)
BorderSubtle    → BorderSubtleBrush    (#232834 / #E2E8F0)
BorderStrong    → BorderStrongBrush    (#353D4E / #CBD5E1)
Accent          → AccentBrush          (#10B981 / #059669)
AccentHover     → AccentHoverBrush     (#34D399 / #10B981)
AccentPressed   → AccentPressedBrush   (#059669 / #047857)
AccentText      → AccentTextBrush      (#022C22 / #FFFFFF)
AccentSoft      → AccentSoftBrush      (#064E3B / #ECFDF5)
AccentBorder    → AccentBorderBrush    (#047857 / #A7F3D0)
TextPrimary     → TextPrimaryBrush     (#F8FAFC / #0F172A)
TextSecondary   → TextSecondaryBrush   (#94A3B8 / #475569)
TextTertiary    → TextTertiaryBrush    (#64748B / #94A3B8)
TextInverse     → TextInverseBrush     (#0B0D11 / #FFFFFF)
Danger          → DangerBrush          (#F87171 / #DC2626)
DangerSoft      → DangerSoftBrush      (#450A0A / #FEF2F2)
Warning         → WarningBrush         (#FBBF24 / #D97706)
WarningSoft     → WarningSoftBrush     (#451A03 / #FFFBEB)
Success         → SuccessBrush         (#10B981 / #059669)
SuccessSoft     → SuccessSoftBrush     (#064E3B / #ECFDF5)
OverlayScrim    → OverlayScrimBrush    (#C8080A0E / #A60F172A)
```

---

## 六、可用控件样式（Controls.xaml）

```
按钮: PrimaryButton, GhostButton, DangerButton, IconButton,
      CaptionButton, CaptionCloseButton, NavButton, ChipButton, TokenChipButton
开关: ToggleSwitch
输入: (default TextBox), FlatTextBox, FlatRichTextBox, (default PasswordBox)
下拉: (default ComboBox), CompactComboBox
布局: Card, InlineCard, StatusPill
文字: PageTitle, PageSubtitle, SectionTitle, FieldLabel, Caption
字体: MonoFontFamily (Cascadia Mono, Consolas)
图标: IconSettings, IconSwap, IconCaptionMin, IconCaptionMax, IconCaptionClose, ...
```

---

## 七、文件依赖关系图

```
App.xaml.cs
├── MainWindow (lazy creation)
│   ├── Sections/TranslateSection
│   │   └── uses: TranslationCoordinator, TtsService, HistoryStore
│   ├── Sections/LibrarySection
│   │   └── uses: HistoryStore, VocabularyStore, TtsService
│   ├── Sections/GeneralSection
│   ├── Sections/ShortcutsSection
│   │   └── uses: HotkeyRecorder (custom control)
│   ├── Sections/ServicesSection
│   │   └── uses: ProfileManager, CoreBridge, CredentialStore
│   ├── Sections/PrivacySection
│   │   └── uses: CoreBridge, OutboundPolicy
│   └── Sections/DataSection
│       └── uses: HistoryStore, VocabularyStore
├── TranslationPanelWindow
│   └── uses: TranslationCoordinator, TtsService, MarkdownPresenter,
│             WindowPositioner, VocabularyStore, ClipboardSelectionService
├── CaptureOverlayWindow
│   └── uses: ScreenCaptureService
├── QuickSearchWindow
│   └── uses: TranslationCoordinator, TtsService, MarkdownPresenter, VocabularyStore
└── FloatingTriggerWindow
```

---

## 八、事件处理器归属表

### TranslateSection
```
Translate_Click, TranslateSwap_Click, TranslateInput_TextChanged,
TranslateInput_KeyDown, TranslateSourceSpeak_Click, TranslateSourceCopy_Click,
TranslateClear_Click, TranslateResultSpeak_Click, TranslateResultCopy_Click
```

### LibrarySection
```
ClearHistory_Click, HistorySearch_TextChanged, ExportHistoryCsv_Click,
ExportHistoryMarkdown_Click, HistoryList_MouseDoubleClick, DeleteHistoryEntry_Click,
CopyHistoryTranslation_Click, LoadHistoryEntry_Click,
VocabularySearch_TextChanged, ExportAnki_Click, ExportCsv_Click,
VocabularyList_MouseDoubleClick, SpeakVocabulary_Click, ExportMarkdown_Click,
DeleteVocabulary_Click, LoadVocabulary_Click
```

### GeneralSection
```
ThemeComboBox_SelectionChanged
```

### ShortcutsSection
```
(无独立事件处理器，HotkeyRecorder 有自己的录制逻辑)
```

### ServicesSection
```
AddProfile_Click, EditProfile_Click, DeleteProfile_Click, ActivateProfile_Click,
ProfilesListBox_DoubleClick, ProviderTypeComboBox_SelectionChanged,
TestConnection_Click, SaveService_Click, CancelEdit_Click, TogglePresets_Click,
Preset_Click, WizardBack_Click, WizardNext_Click, ClearApiKey_Click
```

### PrivacySection
```
ProviderGate_Changed, AllowFreeEngine_Click, DenyFreeEngine_Click,
ResetFreeEngineConsent_Click, ModeComboBox_SelectionChanged
```

### DataSection
```
ClearHistory_Click (共享), ClearVocabulary_Click
```

### MainWindow (保留)
```
Nav_Checked, Save_Click, Revert_Click,
MinimizeButton_Click, MaximizeButton_Click, CloseButton_Click
```

---

## 九、验证清单

每次修改后运行：
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1
```

预期结果：**39 passed, 0 failed**

关键测试项：
- provider profiles support multi-config, independent keys and round-trip
- information architecture surfaces workbench, library and control center
- main window includes window chrome and unified caption bar
- theme tokens dark and light palettes are symmetric
- icon controls expose automation names
- window caption resources and geometries are consistent
- render screenshots and measure performance baseline

---

## 十、进度日志

| 日期 | 动作 | 状态 |
|------|------|------|
| 2026-08-29 | 创建重构计划文档 | ✅ |
| 2026-08-29 | 全部 7 个 Section XAML+CS 创建 | ✅ 完成 |
| 2026-08-29 | Helpers.cs (共享辅助) 创建 | ✅ 完成 |
| 2026-08-29 | RowTitle/RowHint/SettingsRow 迁移到 Controls.xaml | ✅ 完成 |
| 2026-08-29 | MainWindow.xaml 瘦身（1092 行 → 159 行，7 个 Section UserControl 引用） | ✅ 完成 |
| 2026-08-29 | MainWindow.xaml.cs 瘦身（1791 行 → 494 行，事件处理器迁入各 Section） | ✅ 完成 |
| 2026-08-29 | 修复 Controls.xaml 重复图标 key（IconTranslate 等 5 个与上方 Vector Icons 区块重复，导致资源字典运行时加载失败、渲染测试报 StaticResource 异常） | ✅ 完成 |
| 2026-08-29 | 删除 TranslateSection 未使用的 StatusChanged 死代码事件（CS0067 编译错误） | ✅ 完成 |
| 2026-08-29 | 编译验证 Section 集成（dotnet build 0 警告 0 错误） | ✅ 完成 |
| 2026-08-29 | TranslationPanelWindow 重设计：三段式布局（标题栏 Pin 左置 / 源文本卡+动作栏 / 独立语言选择栏带分隔线 / 结果卡+收藏星标 / 说明卡），新增「合并换行」按钮修复 PDF 断行 | ✅ 完成 |
| 2026-08-29 | QuickSearchWindow 对齐：结果区卡片化（与弹窗同款 SurfaceAlt 卡片 + StatusPill 徽章 + 24×22 动作按钮），说明卡改用 AccentBorder 描边 | ✅ 完成 |
| 2026-08-29 | 图标去重（Controls.xaml 为唯一来源）+ 新增 IconMergeLines | ✅ 完成 |
| 2026-08-29 | 硬编码清理：CaptureOverlayWindow 遮罩/尺寸标签/提示条改用 theme token（OverlayScrim/Surface/TextPrimary/TextSecondary/BorderSubtle），十字线等屏幕覆盖色提为本地命名资源；FloatingTriggerWindow 像素偏移提为命名常量 | ✅ 完成 |
| 2026-08-29 | 快速翻译工作台重构：左右双栏对照（左原文+字符统计+断行合并，右译文+引擎徽章+独立发音/复制），告别上下挤压 | ✅ 完成 |
| 2026-08-29 | 服务商配置重构：预设网格前置化（OpenAI/DeepSeek/Gemini/Claude/GLM/Ollama 一键自动填充名称、模型与BaseURL），测试连接与状态直观联动 | ✅ 完成 |
| 2026-08-29 | 全量测试验证 | ✅ **39 passed, 0 failed** |

### 已知限制（非回归）

- 渲染测试在本机产生的截图为全透明（视觉上全黑）— 用 `git stash` 在 HEAD 上对比确认行为完全一致（文件尺寸逐字节相同），属测试环境的存量限制（窗口未 Show 时 RenderTargetBitmap 输出空帧），与本次重构无关。测试断言（文件存在性、性能基线）不受影响。

### 当前文件清单 (Sections/)

| 文件 | 行数 | 状态 |
|------|------|------|
| TranslateSection.xaml | 94 | ✅ |
| TranslateSection.xaml.cs | 233 | ✅ |
| LibrarySection.xaml | 198 | ✅ |
| LibrarySection.xaml.cs | 293 | ✅ |
| GeneralSection.xaml | 88 | ✅ |
| GeneralSection.xaml.cs | 41 | ✅ |
| ShortcutsSection.xaml | 74 | ✅ |
| ShortcutsSection.xaml.cs | 18 | ✅ |
| ServicesSection.xaml | 288 | ✅ |
| ServicesSection.xaml.cs | 748 | ✅ |
| PrivacySection.xaml | 136 | ✅ |
| PrivacySection.xaml.cs | 148 | ✅ |
| DataSection.xaml | 71 | ✅ |
| DataSection.xaml.cs | 69 | ✅ |
| Helpers.cs | 62 | ✅ |

### ✅ 重构收尾状态

Task 1（MainWindow 拆分）、Task 2（TranslationPanelWindow 重设计）、Task 3（QuickSearchWindow 对齐）、Task 4（图标去重 + 硬编码清理）均已完成，verify.ps1 全量通过。

---

## 十一、第二轮：产品级信息架构重构（2026-08-29）

> 依据 `docs/UI-REFACTOR-HANDOFF-PROMPT.md` 的已确认布局基线实施。按纵向切片完成。

### 已实施

| 切片 | 内容 | 状态 |
|------|------|------|
| A 应用壳 | 新增独立 `SettingsWindow`（通用/服务/快捷键/隐私与数据，176px 单侧栏）；MainWindow 只留「翻译、资料库」+ 底部安静状态栏与设置入口；删除 ControlCenterHost 与第二层侧栏；全局保存栏移入 SettingsWindow，且仅在存在未保存草稿时出现「放弃修改/保存设置」按钮 | ✅ |
| B 服务页 | Master–Detail（左 Profile 列表 + 右编辑器）；Key/测试连接/模型/保存首屏可见；内置云厂商隐藏协议与 Base URL（仅自定义/本地服务显示）；测试连接用草稿不落盘；新增「默认文字服务」「默认视觉服务」双路由（`CoreProductConfig.VisionProfileId`，视觉需与文字同协议）；删除服务时说明默认路由去向 | ✅ |
| C 工作台 | 源/目标语言选择器进对应面板标题栏，交换按钮居两栏中间；每屏仅「翻译」一个主按钮；说明区扁平化（分隔线+文字，去装饰卡片）；新增「收藏」写入生词本；语言对变更即持久化（与浮窗共享） | ✅ |
| D 资料库 | 历史/生词合并为统一 Master–Detail（左侧切换数据源+搜索+列表，右侧详情与上下文动作）；导出收进菜单；Enter/双击载入工作台，Del 删除 | ✅ |
| E 浮窗/查词 | 浮窗译文区成为视觉主体（源区收缩）；请求中显示骨架条；圆角统一 12px；QuickSearch 对齐 | ✅ |
| F 设计系统 | 控件圆角 8→6、容器 14→10、浮窗 16/18→12；ChipButton 去全圆角、预设按钮去 emoji；ComboBox 模板支持 IsEditable（模型建议+自由输入）；新增 SegmentButton；AutomationProperties 补齐 | ✅ |

### 行为变化

- 「控制中心」概念移除；托盘「设置」、浮窗设置按钮、`--settings` 均打开独立设置窗口。
- 服务保存即启用并设为默认文字服务（「保存并启用」）；默认路由切换即时生效。
- 翻译行为开关（用法说明/Token 保护）从服务页移到「通用」。
- 工作台/浮窗语言选择即时持久化，不再依赖底部「保存设置」。

### 修复的缺陷

- 删除最后一个服务后 `GetActiveProfile()` 空列表越界崩溃（改为回退默认 Profile）。
- 保存服务时视觉默认服务协议校验时序错误（先改 ActiveProfileId 再比较，恒等导致永不重置）。
- 服务列表状态点未识别旧版遗留凭据（活跃 Profile 回退 legacy target 检测）。
- 本地数据页「清空生词本」按钮缺 Grid.Column 被拉成横条（布局错误）。

### 验证

- `scripts/verify.ps1` 全量通过：cargo fmt/test/clippy + WPF 构建 + 39/39 逻辑测试。
- 新增测试断言：主窗口不含控制中心/保存栏；SettingsWindow 承载五个设置面 + 草稿保存栏；服务页含 Profile 列表与双默认路由选择器；设置窗口纳入渲染矩阵（settings_dark/light.png）。
- 真机目检（浅色/125%）：主窗口、设置四页、服务 Master–Detail、添加服务预设、资料库空状态均符合基线；深色与 200% DPI 依赖既有渲染矩阵覆盖。

### 已知偏差与剩余风险

- SettingsWindow 默认高度 960×680→960×700（保证自定义服务编辑器的「保存并启用」在首屏，最小 820×600 不变）。
- 设置窗口底栏结构常驻（左侧状态文本），「放弃修改/保存设置」按钮严格按草稿状态显隐——反馈位置稳定优先。
- 模型下拉建议为内置静态列表，尚未接「获取模型列表」API（需要 FFI v2 契约）。
- 「默认视觉服务」仅允许与默认文字服务同协议的 Profile（核心单 Provider 契约限制）；UI 已用「跟随默认文字服务」表达回退。
- 逻辑测试的窗口渲染在本机输出全透明帧（存量环境限制，与 HEAD 行为一致），DPI/深色矩阵需在真实会话抽样目检。

---

## 十二、第三轮：P0 收口与去 AI 化（2026-08-29）

> 约束：不操控电脑（不启动应用、不做 UI 自动化）；仅静态审查 + 非 GUI 构建/测试。

### 批次 1 — P0 数据与状态正确性

| 问题 | 修复 |
|------|------|
| 保存服务时 Key 先写入 `profile.CredentialTarget`（仍是默认 OpenAI 目标），之后才确定 Profile ID/目标 → DeepSeek/Gemini/Claude Key 全部落进 OpenAI 槽位 | 新增 `ProfileManager.ResolveSaveTarget`（纯函数）：先由编辑状态解析最终 ProfileId + CredentialTarget，再写 Key。新增用自造目标，编辑沿用服务自己的目标。`SaveService_Click` 拆出 `TrySaveService()`（返回成功与否，供草稿确认复用） |
| SettingsWindow 保存顺序：先写 Core 策略，后校验快捷键 → 校验失败留下半保存状态 | 重排为「校验（组合有效性）→ 注册热键 → 提交 Core → 提交 ShellSettings」；提交阶段任一步失败回滚已写部分（Core 快照回滚 + 恢复原热键），错误信息明确说明最终状态（"未保存任何修改"/"已回滚本次全部修改"） |
| 服务编辑器无 Dirty 状态，切换服务/切页/关窗/新增会静默丢草稿 | 编辑器全字段接入 dirty 跟踪（文本/密码/下拉/开关），动作栏显示「未保存」徽标；切换服务、切换设置页、新增服务、关闭设置窗口前弹「保存/放弃/取消」；取消则回弹选择并保留草稿；删除服务后清空编辑器状态 |

### 批次 2 — 布局与性能

- MainWindow 内容区去掉外层无限高度 ScrollViewer → 受约束 Grid；TranslateSection/LibrarySection 根布局改为 `Auto/*` 行，左右翻译面板随窗口高度填满、各自滚动（输入框/译文/说明区、资料库左右栏均独立滚动）。
- SettingsWindow：服务页移出外层滚动（其余三页保持页面流滚动）；服务页左列表与右编辑器独立滚动，编辑器内容包 ScrollViewer（高级设置不再撑开整页）。
- 列表虚拟化保留（ListBox 高度受约束 + 默认虚拟化栈面板）。
- `RefreshProfilesList` 整次刷新只 Load 配置一次（StateBrush 直接接收 ActiveProfileId），并在重建后恢复编辑器选中项。
- 说明区（补充说明）加 MaxHeight 150 + 滚动，不再挤压双栏。

### 批次 3 — 服务页体验

- 清除密钥：新增模式只清输入框（绝不触碰凭据库，防误删遗留默认目标）；编辑模式先确认。
- 测试连接成功状态补充模型名；失败经 `DescribeTestFailure` 映射为可行动提示（401/403/404/429/5xx/超时/TLS/DNS/离线开关）。
- 内置云厂商（preset cloud host）隐藏协议+高级区（Endpoint/Headers/TLS）；自定义/本地服务保留。
- 默认视觉服务下方显示不兼容原因（N 个支持图片的服务因协议不同未列出）。

### 批次 4 — 设置页去卡片化

- 通用/快捷键/隐私/本地数据：删除 Card 包裹，改为分组标题 + 扁平设置行 + `SettingsDivider` 发丝分隔线；仅保留「当前实际线路」InlineCard 与 OCR 语言列表面板。

### 批次 5/6 — 去 AI 化与无障碍

- 测试连接结果移除 ✓/✗ 前缀（颜色 + 文字已表达状态）。
- 主窗口/设置窗口状态栏加 `AutomationProperties.LiveSetting="Polite"`（LiveRegion）。
- 补齐输入类控件 AutomationProperties.Name（翻译原文/译文、资料库搜索、查词输入、服务名/BaseURL/密钥/模型/端点/请求头、语言选择、预设按钮等）。

### 新增逻辑测试（43 passed, 0 failed）

- `service save resolves credential targets per profile` — ResolveSaveTarget 的新增/编辑/空目标/未知 ID 分支。
- `service save writes the key after resolving its target` — 源码顺序守卫（Key 写入必须在目标解析之后）。
- `settings save validates hotkeys before persisting` — 校验→注册→Core→Shell 顺序 + 回滚文案守卫。
- `connection test failures map to actionable hints` — DescribeTestFailure 映射。

### 构建注意

本机运行中的 PopGlot.exe 会锁定 `bin/Debug` 输出；构建检查请使用独立输出目录：
`dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Debug -p:BaseOutputPath="D:/Projects/GitProjects/PopGlot/artifacts/check-bin/"`

### 尚需用户手工目检（本 Agent 不操控电脑）

1. 服务页：新增 DeepSeek/Gemini 服务并粘贴 Key → 保存后确认 Key 落在该服务自己的凭据目标（凭据管理器查看 `PopGlot/provider/p-*`）。
2. 服务编辑器修改字段 → 出现「未保存」；切换服务/切页/关窗 → 出现保存/放弃/取消提示。
3. 设置保存：故意把两个快捷键录成同一组合 → 保存失败提示"未保存任何修改"，且出网策略等保持原值。
4. 通用/快捷键/隐私页视觉：分组标题 + 分隔线的扁平观感（深色/浅色、125%/150%）。
5. 主窗口拉伸：翻译双栏与资料库随高度填满、各自滚动；200 条历史时列表流畅。
6. 内置服务编辑时不出现「高级设置」；默认视觉服务出现协议不兼容说明（若存在异协议视觉服务）。

---

## 十三、第三轮·视觉重建（2026-08-29 傍晚）

> 约束不变：不操控电脑；仅静态审查 + 非 GUI 构建/测试。逻辑修复零回退，44/44 测试通过。

### 批次 1 — Foundation（色彩与排版体系）

- **色板重建**（`ThemeService.cs` + `App.xaml` 种子同步）：
  - 浅色：Canvas `#F2F3F5` / Sidebar `#FAFAFB` / Surface `#FFFFFF` / SurfaceMuted `#F7F8FA` / Input `#FFFFFF` / BorderSubtle `#E1E4E9` / BorderStrong `#C5CAD3`——四层背景肉眼可分。
  - 深色：Canvas `#0E0F12` → Sidebar `#121419` → SurfaceMuted `#15181E` → Surface `#191C22` → SurfaceRaised `#20242C`，五个稳定亮度层级。
  - **Accent 与 Success 分离**：品牌色从绿色改为冷靛蓝（浅 `#5B5BD6` / 深 `#8B8FF7`）；Success 独立绿色（浅 `#16875D` / 深 `#45C18A`）；Warning/Danger 同步微调至 AA 级对比。
  - 角色键语义化：`SurfaceAltBrush`（12 处）迁移为 `SurfaceMutedBrush`；新增 `SurfaceRaisedBrush`（下拉/菜单/ToolTip/浮窗结果）与 `TextDisabledBrush`；删除无人使用的 `TextInverseBrush`。
- **Typography Token**：PageTitle 20、SectionTitle 13、Body/BodyStrong 13、Caption 12、Metadata 11（新增）、Mono 12.5；RowTitle 13.5→13，消除散落字号。
- **控件密度**：ComboBox 38→36；IconButton 30→32、圆角统一 6；按钮 padding 16,9→16,8；StatusPill 去全圆角（20→7）改小标签；FocusRing 删除无效 `StrokeDashArray`、加 `SnapsToDevicePixels`。
- 弹出层（ComboBox 下拉、ContextMenu、ToolTip）统一改用 `SurfaceRaisedBrush`。

### 批次 2 — 主窗口

- 侧栏 184→168；删除彩色「P」占位 Logo 与「桌面翻译助手」副标题，只留文字标识；NavButton 选中态从 AccentSoft 大色块改为中性 SurfaceHover + 左侧短指示条。
- 翻译页：原文面板=Input（编辑面）、译文面板=SurfaceMuted（只读结果，视觉主体）；原文/译文次级动作（朗读/复制/合并断行/收藏）改图标工具栏（28×26 + ToolTip）；引擎名/耗时/状态用 Metadata 灰字，去掉彩色徽章；删除页面副标题；保留唯一主按钮「翻译」。
- 底部状态栏降级：padding 24,9→24,5、字号 12→11、状态点 7→6、设置入口紧凑化。
- 紧凑断点：内容宽度 <900 DIP 时隐藏次级提示（`SetCompact`），资料库列表列 340→280；双栏永不堆叠。

### 批次 3 — 设置

- 「安全离线模式」开启时：网络翻译/截图方式/上传截图三个控件禁用，行内显示原因说明（`UpdateSafeModeGating`，加载与切页都会刷新）。
- 路线预览卡片：上传截图时切换为 WarningSoft 警示面；表单有未保存修改时显示「保存后线路会重新评估」提示（核心 `PlanScreenshotRoute` 只读已保存设置，不做假草稿预览）。
- 通用页副标题删除；快捷键页保留录制规则说明。

### 批次 4 — 服务页

- **显式健康状态**：`DescribeProfileState`（纯函数，带测试）产出「本地服务/缺少 Key/未测试/可用/测试失败」五态；列表行三层排版（名称+默认标签 / 模型 / 状态点+状态文本）；「缺少 Key」为 Warning 色，品牌色不参与健康表达。
- 「默认」标签中性化（不再用 Accent 绿/靛底）。
- 新增流程 Provider Picker：Chip 网格 → 两列扁平按钮（`ProviderPresetButton`，就近定义在 Section 资源）；删除全局 `ChipButton` 样式。
- 测试结果结构化：状态点 + 一行摘要（host/HTTP/耗时/模型）+ 一行限高详情（超长省略 + ToolTip 全文）。
- 视觉不兼容服务：从「隐藏」改为「下拉中禁用 + 标注（协议不同）+ 下方原因说明」。
- **事务加固**：写 Key 前捕获旧凭据；ProfileManager.Save 失败时回滚凭据（恢复旧值或删除新值）；ApplyToCore 失败时明确报告「已保存到本机配置，但运行中引擎未更新，重启后生效」并保持保存成功语义。

### 批次 5 — 资料库与浮窗

- 资料库：左列表改 SurfaceMuted（与右详情 Surface 形成主次）；空状态文本缩短；删除详情区 `MaxHeight=170/260` 硬限制（整面板独立滚动）。
- TranslationPanel：标题栏去嵌套底色（透明+底部发丝线）；引擎徽章 pill → Metadata 灰字（失败/部分完成仍以颜色表达）；说明区从 Accent 描边卡 → 发丝线+正文。
- QuickSearch：结果区改 SurfaceRaised；删除「翻译结果」装饰 pill；说明卡 → 发丝线+正文，与浮窗一致。

### 批次 6 — 静态质量

- 构建 0 警告 0 错误；逻辑测试 **44 passed / 0 failed**（新增 `service health states are explicit and hue-safe`）。
- 扫描：硬编码颜色仅剩 App.xaml 种子（设计使然）与 CaptureOverlay 十字线覆盖色（屏幕合成色，不随主题，合理保留）；无缺失资源键；详情区固定 MaxHeight 清除。
- 已知保留项：浅色 Input 与 Surface 同为白色（靠边框+焦点环区分，Win11 原生惯例）；浮窗内动作按钮 24–28px（弹窗密度优先，主窗口内均 ≥32）。

### 更新发布版本

`dist/release` 被运行中的 PopGlot 锁定，未能覆盖。退出应用后执行：
`dotnet publish apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -r win-x64 --self-contained false -o dist/release && cp target/release/popglot_ffi.dll dist/release/`

---

## 十四、第四轮：产品缺陷修复（2026-08-29 晚）

> 依据用户评审：修复被真实感知的产品缺陷，而非继续调色。约束不变：不操控电脑。**51 passed / 0 failed**。

### 按严重度修复

**P0-1 标题栏按钮不可见**（Controls.xaml、MainWindow/SettingsWindow.xaml）
- 根因：CaptionButton 模板只有 ContentPresenter，而图标存于 `local:Ui.Icon` 附加属性；CloseBtn 甚至没设 IconCaptionClose。
- 修复：模板显式渲染 `<Path>`，`Data` 绑定 Ui.Icon、**Stroke** 而非 Fill（标题栏几何是线稿）、高度对齐 CaptionHeight=38；hover 普通钮前景提亮、关闭钮红底白线；两窗口 CloseBtn 补 IconCaptionClose。主窗口关闭即隐藏到托盘：Tooltip 改「关闭到托盘」，首次关闭发一条托盘气泡提示（NotifyTray）。

**P0-2 整页文字动画导致先糊后清**（MainWindow.xaml.cs、TranslationPanelWindow.xaml.cs）
- 根因：PlaySectionEntrance 对整页做 Opacity 0→1 + TranslateTransform 14→0；浮窗加载时整窗 Opacity 0→1。WPF 在动画期间失去 ClearType。
- 修复：删除 PlaySectionEntrance 与浮窗整窗淡入；页面切换直接切 Visibility。保留的动画仅：Toggle 滑块、ProgressBar、浮窗触发小点的淡入淡出（非文字元素）。

**P0-3 透明分层窗口字体发虚**（TranslationPanelWindow.xaml、QuickSearchWindow.xaml）
- 根因：`AllowsTransparency="True"` + 透明背景 + 外边距阴影 → 分层窗口无法获得 ClearType。
- 修复：改 `AllowsTransparency="False"`、不透明 SurfaceBrush 背景、删除 16/18px 透明外边距与 DropShadowEffect；圆角与阴影交给 DWM（ApplyWindowChrome 已设 DWMWCP_ROUND + immersive dark）。CaptureOverlay/FloatingTrigger 因功能需要保留透明。

**P1-4 系统 MessageBox 泛滥**（SettingsWindow、ServicesSection、DataSection、LibrarySection、OutboundPolicy、App）
- 新增内联组件：
  - `DraftGuardBar`（服务编辑器内固定条）：切换服务/切设置页/新增/关窗前显示「有未保存修改」+ 保存并继续/放弃并继续/取消，处理完才执行原目标。
  - `ConfirmButton`（Helpers）：两步确认（第一次点变"确认删除？"红色，5 秒内再点执行），用于删除服务、清除密钥、清空历史、清空生词本、资料库清理。
  - 设置窗口关闭有草稿：留在窗口内，跳到对应页面/显示保存栏 + 状态说明。
  - 免费引擎首次授权从运行时弹窗改为「设置 → 隐私与数据」内完成：OutboundPolicy 无 prompt 时拒绝**且不写入 Denied**（用户没有回答过任何东西）；错误提示指向隐私页。
- 现仅 App 启动失败保留系统 MessageBox。

**P0-5 服务数据模型重建**（ProfileManager.cs）
- `CoreProductConfig` 默认 **Profiles 为空**、ActiveProfileId 为空（schema v5）。
- 新增 `ProviderCatalog`：OpenAI/DeepSeek/Ollama/Gemini/Claude/智谱 GLM 六个模板，只服务「添加」流程，`IsPristineTemplate` 判定模板等价性。
- **Schema v4→v5 迁移**：仅删除"逐字段等于出厂模板且无 Key"的条目；改名/改模型/有 Key/自定义服务全部保留；迁移经 Save 写回并自动留 .bak 备份；`ConfigPathOverride` + `ResetForTests` 支持无 GUI 测试。

**P1-6 保存不再劫持路由 + 就绪门控**（ServicesSection）
- 保存语义：第一个配置的服务「保存并使用」；后续新增「保存服务」；编辑「保存修改」。仅首服或编辑当前默认时更新 ActiveProfileId。
- 新增「设为文字默认」按钮 + `CheckReadiness`（缺 Key/缺模型/缺 URL 不能启用）；默认文字下拉中未就绪服务显示为禁用项并标注原因。
- 状态机细化：`ClassifyTestFailure`（auth/rate/endpoint/unreachable/fail）+ 会话级测试状态（未测试/可用/鉴权失败/限流/接口不存在/服务不可达/本地不可达/缺少 Key）。

**P1-7 逻辑一致性**（MainWindow、PrivacySection、ProfileManager）
- 状态栏与隐私路线预览改用 `ProfileManager.ResolveActiveCredentialTarget()`，与实际翻译线路同一凭据解析。
- `ProfileManager.Save`：文件原子替换成功后才更新 `_cached`，失败不再污染缓存。

**P2-8 服务页自适应**：内容 <640 DIP 时列表 264→212、协议/URL 与模型双列变单列、Key 行按钮换行；保存栏固定不随滚动。

**P2-9 应用头像接入**：`Assets/popglot-app-avatar-v1.png`（保留）→ `scripts/make-ico.ps1` 生成 8 尺寸 `Assets/PopGlot.ico`；csproj `ApplicationIcon` + Resource；主/设置窗口 Icon；托盘从嵌入资源加载（删除运行时绘制绿色字母 P 的 CreateAppIcon/RoundedRectangle）；侧栏 22px 头像 + 文字。

### 新增测试（51 total）

- `caption buttons really render their icons`（模板含 Path + Ui.Icon 绑定 + Stroke；CloseBtn 用 IconCaptionClose）
- `page transitions have no text-damaging animations`
- `text windows are opaque for ClearType`
- `daily flows never open system dialogs`（8 个文件零 MessageBox）
- `unready services cannot become the default`（CheckReadiness 四分支）
- `schema v4 factory profiles migrate out of configured services`（保留用户数据、剔除 pristine 模板、schema 持久化）
- `a failed save does not poison the cache`
- 原 credential-target 顺序守卫、健康状态、授权门控测试同步更新。

### 尚需用户手工目检（本 Agent 不操控电脑）

1. 标题栏最小化/最大化/关闭图标在浅色、深色、200% DPI 下清晰；关闭悬停红底白叉。
2. 页面切换无渐变/位移，文字全程清晰。
3. 浮窗与查词文字锐利（ClearType）、DWM 圆角与阴影正常、拖动标题栏仍可移动。
4. 服务草稿守卫条：编辑→切服务/切页/关窗，内联三按钮流程；删除服务/清除 Key/清空历史两步确认。
5. 首次无配置翻译免费引擎 → 得到指向「隐私与数据」的失败提示，且托盘无弹窗。
6. 应用图标：任务栏/资源管理器/托盘显示新头像，小尺寸可辨识。
7. 老用户升级：未被修改过的出厂模板（OpenAI 等）从服务列表消失；改过名的/有 Key 的服务保留。

### 剩余风险

- 迁移判定"pristine+无 Key"依赖 Windows 凭据库查询；凭据库异常时保守保留该条目（宁多勿删）。
- 首次免费引擎使用从"弹窗询问"变为"失败+指引"，转化率可能略降（可在设置内一键允许）。
- 会话级测试状态不持久化：重启后全部回到「未测试」。
- 「本地不可达」依赖用户主动测试连接，不做后台探测。
- DWM 圆角在 Win10 旧版本上无效（仍为直角窗口，非回归）。
