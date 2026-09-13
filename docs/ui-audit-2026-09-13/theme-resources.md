# PopGlot UI 专项审计报告：资料库/数据/快捷键/隐私分区 + 主题资源字典

> 审计日期：2026-09-13
> 审计人：OpenCode (Teammate)
> 审计范围：`apps/PopGlot.Windows/Sections/LibrarySection.xaml(.cs)`、`DataSection.xaml(.cs)`、`ShortcutsSection.xaml(.cs)`、`PrivacySection.xaml(.cs)`、`Themes/Controls.xaml`、`App.xaml` 及全库颜色/资源引用。
> 审计基线：`docs/DESIGN_SYSTEM.md` (v0.1.3+)、`docs/review-2026-09-12/README.md` (G03/G07)、`docs/review-2026-09-12/PRODUCT-EXPANSION-REVIEW-V2.md` 第 10–15 节、`docs/UI-REVIEW-2026-09-03.md`。

---

## 目录
1. [审计概述与统计](#一审计概述与统计)
2. [主题资源体系盘点](#二主题资源体系盘点)
3. [全库硬编码颜色完整清单](#三全库硬编码颜色完整清单)
4. [控件模板状态完整性审计](#四控件模板状态完整性审计)
5. [深浅主题覆盖与 C18（热切换与高对比）差距分析](#五深浅主题覆盖与-c18热切换与高对比差距分析)
6. [四个分区页面专项审计（对齐/间距/空态/错误态）](#六四个分区页面专项审计对齐间距空态错误态)
7. [UI 审计缺陷条目清单（ID / 级别 / 文件:行 / 现象 / 规格条款 / 最小修复建议）](#七ui-审计缺陷条目清单)
8. [Top 5 核心修复优先级排序](#八top-5-核心修复优先级排序)

---

## 一、审计概述与统计

本次审计对 PopGlot Windows 客户端的主题资源字典体系（`App.xaml`、`Controls.xaml`、`ThemeService.cs`）、四个业务分区页面（资料库、本地数据、快捷键、隐私与安全）以及全库所有 15 个 XAML 与 C# 渲染后台进行了逐行扫描。

### 发现统计
- **缺陷总数**：19 项
- **P0 严重级别**：4 项（严重状态错误/文字不可读/错误态伪装/无障碍破坏）
- **P1 次级重要**：7 项（动态主题断裂/高对比缺失/种子缺失/悬停反馈丢失/交互受阻）
- **P2 一致性/细节**：8 项（圆角脱轨/字号散落/间距无系统/按压反馈缺失/代码规范）

---

## 二、主题资源体系盘点

### 2.1 颜色与画刷 Token 体系（`ThemeService.cs` vs `App.xaml`）
PopGlot 设计系统以 `ThemeService.cs` 为调色盘权威（包含 37 个语义 Token：中性底色 60%、次级容器 30%、克制蓝紫强调 10%）。

| 语义角色 Token | Dark 暗色色值 | Light 亮色色值 | 作用与规范对照 | App.xaml 种子状态 |
| :--- | :--- | :--- | :--- | :--- |
| `CanvasBrush` | `#101216` | `#F6F7F9` | 窗口底层基色 | ✅ 已声明 |
| `SidebarBrush` | `#14171E` | `#FAFAFC` | 侧边栏/底栏背景 | ✅ 已声明 |
| `SurfaceBrush` | `#181B22` | `#FFFFFF` | 主输入/卡片背景 | ✅ 已声明 |
| `SurfaceMutedBrush` | `#14161D` | `#F8F9FA` | 列表背景、只读卡片 | ✅ 已声明 |
| `SurfaceRaisedBrush` | `#20242E` | `#FFFFFF` | 下拉浮层、二级悬浮卡片 | ✅ 已声明 |
| `SurfaceHoverBrush` | `#272C38` | `#EDEFF3` | 控件悬停态 | ✅ 已声明 |
| `SurfacePressedBrush` | `#353C4D` | `#D7DDE6` | 控件按下态 | ✅ 已声明 |
| `InputBrush` | `#181B22` | `#FFFFFF` | 文本输入框底色 | ✅ 已声明 |
| `ResultSurfaceBrush` | `#181B22` | `#FFFFFF` | 译文展示阅读底色 | ✅ 已声明 |
| `BorderSubtleBrush` | `#2D3342` | `#E2E5E9` | 次级分割线/次要边框 | ✅ 已声明 |
| `BorderStrongBrush` | `#6B768D` | `#8590A0` | 输入框/控件外轮廓（≥3:1） | ✅ 已声明 |
| `AccentBrush` | `#7C89D9` | `#5563B8` | 品牌高亮/选中/链接 | ✅ 已声明 |
| `AccentHoverBrush` | `#8F9BE3` | `#4855A4` | 品牌高亮悬停 | ✅ 已声明 |
| `AccentPressedBrush` | `#6976C4` | `#3D478E` | 品牌高亮按下 | ✅ 已声明 |
| `AccentTextBrush` | `#071224` | `#FFFFFF` | 品牌高亮衬底文字 | ✅ 已声明 |
| `AccentSoftBrush` | `#20243A` | `#EEF0FA` | 徽章/选中行背景衬底 | ✅ 已声明 |
| `AccentBorderBrush` | `#59649D` | `#AAB1D9` | 选中项边框 | ✅ 已声明 |
| `FocusBrush` | `#7C89D9` | `#5260B5` | 键盘焦点环（≥3:1） | ❌ **缺失** |
| `PrimaryBrush` | `#5562B3` | `#5260B5` | 主提交按钮底色（深一档，配白字） | ✅ 已声明 |
| `PrimaryHoverBrush` | `#5B69BE` | `#4652A0` | 主按钮悬停 | ✅ 已声明 |
| `PrimaryPressedBrush` | `#4B579F` | `#3B4589` | 主按钮按下 | ✅ 已声明 |
| `PrimaryTextBrush` | `#F7F8FC` | `#FFFFFF` | 主按钮文字（≥4.5:1） | ✅ 已声明 |
| `TextPrimaryBrush` | `#EEF0F4` | `#15171C` | 一级正文/标题文字 | ✅ 已声明 |
| `TextSecondaryBrush` | `#A8B0BD` | `#4D545F` | 次级说明/副标题 | ✅ 已声明 |
| `TextTertiaryBrush` | `#939BAA` | `#656F7C` | 占位符/元数据/时间戳 | ✅ 已声明 |
| `TextDisabledBrush` | `#565F6E` | `#A6ACB7` | 禁用态文本 | ✅ 已声明 |
| `DangerBrush` | `#FF6B7D` | `#C93148` | 危险/删除/报错文字 | ✅ 已声明 |
| `DangerSoftBrush` | `#401C25` | `#FCEBEE` | 危险状态衬底 | ✅ 已声明 |
| `DangerHoverBrush` | `#52222E` | `#C93148` | 危险按钮悬停底色 | ❌ **缺失** |
| `DangerHoverTextBrush`| `#FF6B7D` | `#FFFFFF` | 危险按钮悬停文本 | ❌ **缺失** |
| `DangerPressedBrush` | `#4E1E28` | `#B02A3E` | 危险按钮按下底色 | ❌ **缺失** |
| `DangerPressedTextBrush`| `#FF6B7D` | `#FFFFFF` | 危险按钮按下文本 | ❌ **缺失** |
| `WarningBrush` | `#F2B95C` | `#9C5B00` | 警告/未保存状态 | ✅ 已声明 |
| `WarningSoftBrush` | `#3D2D14` | `#FFF3DB` | 警告状态衬底 | ✅ 已声明 |
| `SuccessBrush` | `#3DD68C` | `#0B7350` | 成功/健康状态 | ✅ 已声明 |
| `SuccessSoftBrush` | `#143826` | `#E3F6EF` | 成功状态衬底 | ✅ 已声明 |
| `OverlayScrimBrush` | `#C8101216`| `#A615171C`| 全屏遮罩半透明底色 | ✅ 已声明 |

**系统性评估**：
1. **命名系统性**：Token 遵循 `[Role][Modifier]Brush` 规范（如 `PrimaryHoverBrush`、`SurfaceMutedBrush`），语义清晰明确，分工严谨。
2. **种子缺失缺陷**：`App.xaml` 遗漏了 5 个在 `Controls.xaml` 样式和运行时所依赖的关键 Token（`FocusBrush`、`DangerHoverBrush`、`DangerHoverTextBrush`、`DangerPressedBrush`、`DangerPressedTextBrush`）。若 XAML 在静态解析或设计时加载，会发生资源解析断裂。
3. **阴影 Token 缺失**：缺少 `ShadowColor` / `ShadowBrush` Token，导致全库浮层阴影一律内联硬编码 `#000000`。

### 2.2 字体与排版 Token 体系（`Controls.xaml`）
- **字体栈资源**：
  - `UiFontFamily`: `Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI`
  - `MonoFontFamily`: `Cascadia Mono, Consolas, Courier New`
- **排版样式 Scale**：
  - `PageTitle`: 20 DIP / SemiBold
  - `PageSubtitle`: 12.5 DIP / TextSecondary / Margin 0,5,0,0
  - `SectionTitle`: 13 DIP / SemiBold
  - `Body`: 13 DIP / LineHeight 20
  - `BodyStrong`: 13 DIP / SemiBold
  - `FieldLabel`: 12 DIP / Medium / Margin 0,0,0,6
  - `Caption`: 12 DIP / TextTertiary
  - `Metadata`: 11 DIP / LineHeight 16 / TextTertiary
  - `MonoText`: 12.5 DIP / MonoFontFamily
  - `RowTitle`: 13 DIP / Medium
  - `RowHint`: 12 DIP / Margin 0,3,20,0

**评估**：排版层级通过 Style 组织，符合 `DESIGN_SYSTEM.md` §2.2 规范；但缺少独立的 `FontSize` Token 资源（如 `FontSizeH1` 等 Double 资源），导致业务页面偶尔直接手写魔法字号（例如 `LibrarySection.xaml:16` 强行覆盖 `FontSize="15"`）。

### 2.3 间距与圆角 Token 体系（`Controls.xaml`）
- **圆角资源**：
  - `ControlRadius`: `6`
  - `CardRadius`: `10`
  - `PaneRadius`: `10`
- **间距资源**：**完全缺失**。全库无任何 `Spacing*` 或 `Thickness` 资源定义，各页面与模板使用散落数值（`4`, `6`, `8`, `10`, `12`, `14`, `16`, `18`, `20`, `24`, `28`），未提炼为标准网格资源。

---

## 三、全库硬编码颜色完整清单

全库扫描排查所有绕过 Theme Token 体系的 `#RRGGBB`、`#AARRGGBB` 及 C# 代码内联 Named Brush。

### 3.1 XAML 中的硬编码颜色与内联画刷

| 文件路径 | 行号 | 硬编码色值 / 属性 | 现象与用途 | 治理建议 |
| :--- | :--- | :--- | :--- | :--- |
| `apps/PopGlot.Windows/Themes/Controls.xaml` | 1055 | `Color="#000000"` (DropShadowEffect) | ComboBox 下拉弹窗阴影底色 | 提取为 `ShadowColor` Token |
| `apps/PopGlot.Windows/Themes/Controls.xaml` | 1158 | `Color="#000000"` (DropShadowEffect) | 语言选择器下拉弹窗阴影底色 | 提取为 `ShadowColor` Token |
| `apps/PopGlot.Windows/Themes/Controls.xaml` | 1605 | `Color="#000000"` (DropShadowEffect) | 右键菜单 ContextMenu 阴影底色 | 提取为 `ShadowColor` Token |
| `apps/PopGlot.Windows/Themes/Controls.xaml` | 1724 | `Color="#E81123"` (`CaptionCloseHoverBrush`) | 窗口关闭按钮悬停红（系统惯例豁免） | 予以保留，建议在 ThemeService 统一声明 |
| `apps/PopGlot.Windows/Themes/Controls.xaml` | 1725 | `Color="#C4101F"` (`CaptionClosePressedBrush`)| 窗口关闭按钮按下红（系统惯例豁免） | 予以保留，建议在 ThemeService 统一声明 |
| `apps/PopGlot.Windows/Themes/Controls.xaml` | 1726 | `Color="#FFFFFF"` (`CaptionCloseGlyphBrush`)| 窗口关闭按钮图标白（系统惯例豁免） | 予以保留，建议在 ThemeService 统一声明 |
| `apps/PopGlot.Windows/CaptureOverlayWindow.xaml` | 13 | `Color="#66FFFFFF"` (`CaptureCrosshairBrush`)| 截图十字准星半透明白色 | 截图工具专用，建议纳入 ThemeService 扩展 |
| `apps/PopGlot.Windows/CaptureOverlayWindow.xaml` | 16 | `Color="#F2FFFFFF"` (`CaptureMarqueeBrush`)| 截图选取选框高亮白色 | 截图工具专用，建议纳入 ThemeService 扩展 |
| `apps/PopGlot.Windows/CaptureOverlayWindow.xaml` | 17 | `Color="#01000000"` (`CaptureHitTestBrush`)| 全屏透明命中测试底色 | 截图专用透明通道，符合 WPF 点击穿透规范 |
| `apps/PopGlot.Windows/CaptureOverlayWindow.xaml` | 61 | `Color="#000000"` (DropShadowEffect) | 截图尺寸指示卡片阴影 | 提取为 `ShadowColor` Token |
| `apps/PopGlot.Windows/FloatingTriggerWindow.xaml`| 18 | `Color="#000000"` (DropShadowEffect) | 悬浮球光晕阴影底色 | 提取为 `ShadowColor` Token |

*(注：`App.xaml` 16–47 行及 `ThemeService.cs` 165–250 行系系统 Token 权威与种子定义处，不列入业务违规硬编码)*

### 3.2 C# 代码中的硬编码 Named Brush

| 文件路径 | 行号 | 代码片段 / 值 | 现象与危害 | 治理建议 |
| :--- | :--- | :--- | :--- | :--- |
| `apps/PopGlot.Windows/Services/MarkdownPresenter.cs` | 289 | `?? Brushes.White` | `TextPrimaryBrush` 缺失兜底：硬编码白 | 严禁兜底硬编码白（亮色下白底白字），应抛异常或 fallback 到系统前景色 |
| `apps/PopGlot.Windows/Services/MarkdownPresenter.cs` | 290 | `?? Brushes.Gray` | `TextSecondaryBrush` 缺失兜底：硬编码灰 | 改用系统画刷或确保资源字典完整 |
| `apps/PopGlot.Windows/Services/MarkdownPresenter.cs` | 291 | `?? Brushes.Teal` | `AccentBrush` 缺失兜底：硬编码水鸭青 | 移除无依据的 Teal 硬编码 |
| `apps/PopGlot.Windows/Services/MarkdownPresenter.cs` | 292 | `?? Brushes.DarkSlateGray` | `InputBrush` 缺失兜底：硬编码暗石板灰 | 移除无依据的 DarkSlateGray 硬编码 |
| `apps/PopGlot.Windows/Services/MarkdownPresenter.cs` | 293 | `?? Brushes.DimGray` | `BorderSubtleBrush` 缺失兜底：硬编码暗灰 | 移除无依据的 DimGray 硬编码 |
| `apps/PopGlot.Windows/Services/MarkdownPresenter.cs` | 436 | `?? Brushes.White` | 内联代码 `TextPrimaryBrush` 缺失兜底：白 | 严禁兜底硬编码白 |
| `apps/PopGlot.Windows/Services/MarkdownPresenter.cs` | 437 | `?? Brushes.Teal` | 内联代码 `AccentBrush` 缺失兜底：水鸭青 | 移除无依据的 Teal 硬编码 |
| `apps/PopGlot.Windows/Services/MarkdownPresenter.cs` | 438 | `?? Brushes.DarkSlateGray` | 内联代码 `AccentSoftBrush` 缺失兜底：灰 | 移除无依据的 DarkSlateGray 硬编码 |

### 3.3 C# 代码中割裂 DynamicResource 绑定的反模式清单（`(Brush)FindResource`）
在代码后台对控件的 Foreground/Background 直接赋予 `(Brush)FindResource("...")` 会设置本地值（Local Value），直接截断 XAML 原有的 `DynamicResource` 响应链，使控件在主题热切换时彻底失效：
- `MainWindow.xaml.cs`: 113, 171, 177, 196 (EngineDot.Background); 333; 547 (StatusTextBlock.Foreground); 556 (StatusDot.Background)
- `SettingsWindow.xaml.cs`: 643 (StatusTextBlock.Foreground); 650 (StatusDot.Background)
- `TranslationPanelWindow.xaml.cs`: 132, 540, 550, 561, 569, 570, 585, 592, 593, 608, 616, 620, 621, 633, 636, 667-669, 713, 714, 747, 748, 808, 812, 841, 924, 927, 946, 949 (大量设置 Foreground/Background/Fill)
- `QuickSearchWindow.xaml.cs`: 261, 269, 277 (FooterStatus); 289 (IncompleteBadge); 492 (StarIcon.Fill)
- `FloatingTriggerWindow.xaml.cs`: 67, 68, 74, 75 (ButtonSurface.Background / BorderBrush)
- `PrivacySection.xaml.cs`: 125, 126, 127, 128 (RouteCard.Background, RouteBadge.Background / BorderBrush / Foreground)
- `ServicesSection.xaml.cs`: 697, 705, 1370-1372, 1473-1475, 1617-1619 (各类动态状态芯片)
- `Sections/Helpers.cs`: 92, 93 (`ConfirmButton` 将按钮刷死为 DangerSoftBrush / DangerBrush)

---

## 四、控件模板状态完整性审计

对照 `PRODUCT-EXPANSION-REVIEW-V2.md` 第 13 节《控件状态合同与误触控制》及 WCAG AA 交互要求，对 `Themes/Controls.xaml` 中定义的控件模板进行全状态（Default / Hover / Pressed / Focused / Disabled / Selected）完整性审核：

| 控件 / 模板 | Default | Hover (MouseOver) | Pressed (IsPressed) | Focused (Keyboard) | Disabled (IsEnabled=False) | 核心缺漏与问题 |
| :--- | :---: | :---: | :---: | :---: | :---: | :--- |
| **Button** | ✅ | ✅ | ✅ | ✅ FocusRing | ⚠️ 双重淡化 | Surface 0.45 透明度叠加 TextDisabledBrush，文字对比度不足 |
| **PrimaryButton** | ✅ | ✅ | ✅ | ✅ FocusRing | ⚠️ 单独 0.45 | 仅设 Surface 0.45，白字 AA 在暗底可能发虚 |
| **GhostButton** | ✅ | ✅ | ✅ | ✅ FocusRing | ⚠️ 继承 Button | 继承自 Button，同样存在双重淡化 |
| **DangerButton** | ✅ | ✅ | ✅ | ✅ FocusRing | ⚠️ 双重淡化 | 设 Surface 0.45 叠加 TextDisabledBrush |
| **IconButton** | ✅ | ✅ | ✅ | ✅ FocusRing | ⚠️ 双重淡化 | 设 Surface 0.45 叠加 TextDisabledBrush |
| **SegmentButton** | ✅ | ✅ | ✅ | ✅ FocusRing | ⚠️ 双重淡化 | Checked 状态良好；Disabled 双重淡化 |
| **NavButton** | ✅ | ✅ | ❌ **缺失** | ✅ FocusRing | ⚠️ 双重淡化 | **无 IsPressed 触发器**，点击无下沉/深色反馈 |
| **NavActionButton** | ✅ | ✅ | ✅ | ✅ FocusRing | ⚠️ 双重淡化 | 具有 IsPressed；Disabled 0.65 不透明度叠加 TextDisabled |
| **HotkeyRecorder** | ✅ | ⚠️ 状态冲突 | ✅ (基类) | ✅ Focusable | ⚠️ 继承 Button | **录制中 Hover 会被基类覆盖为灰色**，丢失录制高亮 |
| **TextBox** | ✅ | ❌ **无效果** | — | ✅ Accent 边框 | ⚠️ 双重淡化 | **Hover 依然设 BorderStrongBrush**（与默认完全一样，无视觉响应） |
| **FlatTextBox** | ✅ | — | — | — | ⚠️ 双重淡化 | 继承自 TextBox |
| **ResultTextBox** | ✅ | — | — | — | ⚠️ 双重淡化 | 仅设 Surface 0.5 叠加 TextDisabledBrush |
| **PasswordBox** | ✅ | ❌ **无效果** | — | ✅ Accent 边框 | ⚠️ 双重淡化 | **Hover 依然设 BorderStrongBrush**（与默认完全一样，无视觉响应） |
| **ComboBox** | ✅ | ✅ (Toggle) | ✅ (Toggle) | ✅ Accent 箭头 | ⚠️ 双重淡化 | Root 0.5 叠加 TextDisabledBrush |
| **ComboBoxItem** | ✅ | ✅ Highlight | ❌ **缺失** | ❌ 缺失 | ⚠️ 双重淡化 | **无 IsPressed 触发器**，无独立键盘焦点指示 |
| **LanguagePicker** | ✅ | ✅ | ✅ | ✅ FocusRing | ⚠️ 双重淡化 | 紧凑型选择器状态完备；Disabled 双重淡化 |
| **ToggleSwitch** | ✅ | ✅ (Knob 0.85)| ❌ **缺失** | ✅ FocusRing | ⚠️ 0.45 淡化 | **关键 BUG：初始 Checked=True 时圆点无法居右（U08 根因）**；无按下态 |
| **SwitchCheckBox**| ✅ | ✅ (Knob 0.85)| ❌ **缺失** | ✅ FocusRing | ⚠️ 0.45 淡化 | **同上 BUG：初始 Checked=True 时圆点居左**；无按下态 |
| **CheckBox (常规)**| ✅ | ⚠️ Checked失效| ❌ **缺失** | ✅ FocusRing | ⚠️ 0.45 淡化 | **无 IsPressed 触发器**；选中后 Hover 边框无变化 |
| **RadioButton** | ✅ | ✅ AccentBorder| ❌ **缺失** | ✅ FocusRing | ⚠️ 0.45 淡化 | **无 IsPressed 触发器** |
| **ListBoxItem** | ✅ | ✅ | ❌ **缺失** | ❌ 内部无焦点 | ❌ **无 Disabled** | **无 IsPressed 触发器**；**缺少 IsEnabled 触发器**；圆角 9 脱轨 |
| **Expander** | ✅ | ✅ Header | ❌ **缺失** | ✅ FocusRing | ❌ **无 Disabled** | **HeaderButton 无 IsPressed**；**整个控件无 IsEnabled 触发器** |
| **MenuItem** | ✅ | ✅ Highlight | ❌ **缺失** | — | ⚠️ 0.45 淡化 | **无 IsPressed 触发器**；**无 IsChecked 状态支持** |

---

## 五、深浅主题覆盖与 C18（热切换与高对比）差距分析

### 5.1 深浅主题覆盖与对比度隐患
1. **暗色危险悬停文字（已解决但不通用）**：
   暗色主题通过引入 `DangerHoverBrush` (`#52222E`) 与 `DangerHoverTextBrush` (`#FF6B7D`) 解决了亮红底白字对比度不足（2.6:1）问题；但 `Helpers.cs` 的 `ConfirmButton` 在代码中仍然粗暴设置 `FindResource("DangerSoftBrush")` 与 `FindResource("DangerBrush")`，绕过了 hover/pressed 阶梯。
2. **TextDisabledBrush 双重淡化**：
   暗色主题下 `TextDisabledBrush` 为 `#565F6E`（在 `#181B22` 表面对比度为 2.8:1），当被容器 `Opacity="0.45"` 再次淡化后，等效色值退化为 `#363B45`，对比度降至 **1.6:1**，文字彻底无法辨识。
   亮色主题下 `TextDisabledBrush` 为 `#A6ACB7`（在 `#FFFFFF` 表面对比度为 2.3:1），再次被 `Opacity="0.45"` 淡化后退化为 `#D7DCE3`，对比度降至 **1.4:1**，严重违反可访问性门槛。

### 5.2 C18（主题热切换与高对比）与现状的差距对比

| 规格条款要求（C18 / N14） | 当前实现事实 | 差距与风险 |
| :--- | :--- | :--- |
| **主题切换后已有文本立即更新** | `MarkdownPresenter.cs` 在生成 `FlowDocument` 时将画刷直接固化（Frozen Brush）赋给 `Run.Foreground` | **严重失败（U06）**：暗色模式下生成的译文，切到亮色后依然是浅白字（`#EEF0F4`）显示在纯白底（`#FFFFFF`）上，文字彻底隐形 |
| **窗口边框与 DWM 沉浸模式同步更新** | 仅 `MainWindow` 与 `SettingsWindow` 监听了 `ThemeChanged` | **次级窗口断裂**：`TranslationPanelWindow`、`QuickSearchWindow`、`FloatingTriggerWindow` 不响应 `ThemeChanged`，标题栏与阴影停留旧主题 |
| **Windows 高对比模式（High Contrast）支持** | `ThemeService.cs` 完全没有检查 `SystemParameters.HighContrast` | **功能完全缺失**：系统开启高对比黑/白模式时，PopGlot 依然强制渲染私有调色盘，无法融入 Windows 辅助技术体系 |
| **动态资源解除冻结与旧文档无刷新迁移** | 代码后台大量直接调用 `(Brush)FindResource(...)` 赋值 | **局部属性锁死**：多达 70 处属性赋值割裂了 DynamicResource，导致大量指示灯、徽章与图标切换主题后不更新 |

---

## 六、四个分区页面专项审计（对齐/间距/空态/错误态）

### 6.1 资料库分区（`LibrarySection.xaml` / `.cs`）
1. **空态与错误态严重混淆（C02 核心缺陷）**：
   - 当生词本文件损坏、被外部占用、无权限或超过 32MiB 时（`_vocabulary.LoadState != Ok`），主列表区域依然显示 `LibraryEmptyTitle.Text = "生词本还是空的"`，副标题提示用户“在翻译浮窗或查词栏点击「收藏」”。
   - 尽管右上角显式增加了 `RetryVocabularyButton`（“重试加载”），但在核心内容区向用户传递了“数据为空”的假象，掩盖了数据未读出的真实情况。
   - 在搜索过滤无匹配结果时（`query != ""` 且匹配数为 0），依然显示“暂无历史记录 / 生词本还是空的”，未提示“无搜索匹配项”。
2. **删除条目后失去选中项（体验缺陷）**：
   - `DeleteRow()` 在调用 `ReloadHistory()` / `ReloadVocabulary()` 后，强制将 `LibraryListBox.SelectedIndex = -1`，直接关闭右侧详情面板展示占位符“未选择条目”，未按规格实现“删除后邻近选择”。
3. **Master-Detail 分栏无拖拽调节（布局缺陷）**：
   - 左侧栏声明了 `Width="320" MinWidth="220" MaxWidth="460"`，但第 1 列仅放置了一个 `1px` 宽的静态 `Rectangle`，没有 `GridSplitter`，声明的最大最小宽度形同虚设。
4. **字号与圆角不统一**：
   - 标题声明 `FontSize="15"` 强行覆盖了 `PageTitle` 的 20 DIP 标准。
   - 外框与详情卡片多处出现 `CornerRadius="4"`（Line 50, 154, 188, 200），脱离设计系统的 6/10/12 标准。
   - 搜索框高度 `Height="28"`，低于标准表单控件的 32–36 DIP 规格。

### 6.2 本地数据分区（`DataSection.xaml` / `.cs`）
1. **顶部分隔线冗余突兀**：
   - `DataSection.xaml` 第 8 行放置了 `<Border Style="{StaticResource SettingsDivider}" Margin="0,0,0,14" />`。由于在 `SettingsWindow.xaml` 中与 `PrivacySection` 串联在同一 StackPanel 内，若作为独立页面或被复用时顶部会产生奇怪的多余横线。
2. **破坏性操作缺乏信息透明度**：
   - “清空历史”与“清空生词本”仅通过 `ConfirmButton` 两步点击确认，未在界面展示“当前共有 X 条历史 / Y 个生词”，用户无法得知即将清空多少数据资产。
3. **设置项间距不完全一致**：
   - 分割线 Margin 分别为 `14`, `12`, `12`，间距微调未对齐标准 8px 网格。

### 6.3 快捷键分区（`ShortcutsSection.xaml` / `.cs`）
1. **录制控件 Hover 样式覆灭高亮态**：
   - `HotkeyRecorder` 继承自 `Button`，在录制中（`IsRecording=True`）应用 `AccentSoftBrush` 与 `AccentBrush` 边框。但鼠标移入时，基类 `Button` 模板的 `IsMouseOver=True` 触发器强行将背景覆盖为 `SurfaceHoverBrush`，边框覆盖为 `BorderStrongBrush`，录制视觉反馈被瞬间抹杀。
2. **右侧固定列宽过紧**：
   - 设置行右列固定 `Width="180"`，而 `HotkeyRecorder` 内部设置了 `MinWidth="150"`，在 DPI 放大（150%~200%）或大字号无障碍模式下，如 `Ctrl + Shift + Alt + Windows` 等长按键名会出现文字裁切。
3. **快捷键冲突无就地提示**：
   - 界面未预留快捷键冲突警告位置，冲突时仅能靠底部状态栏发 Toast 提示，用户无法直接在对应快捷键下方看到错误原因。
4. **缺少“重置默认快捷键”入口**：
   - 用户自定义快捷键后无一键恢复出厂设置的途径。

### 6.4 隐私与安全分区（`PrivacySection.xaml` / `.cs`）
1. **数据承诺卡片内部子卡片完全隐形**：
   - 外层承诺卡片采用 `InlineCard`（背景为 `SurfaceMutedBrush`）。
   - 内部两列子容器（第 21 行与第 28 行）同样设置 `Background="{DynamicResource SurfaceMutedBrush}"`，且**未设置边框**（无 `BorderBrush` 和 `BorderThickness`）。在暗色与亮色模式下，内部两个方块与外层背景色完全同化，卡片层次感彻底丧失。
   - 子容器使用了 `CornerRadius="8"`（脱离 6/10 规范）。
2. **动态线路卡片在后台割裂主题动态绑定**：
   - `PrivacySection.xaml.cs` 125–128 行在计算线路后，使用 `(Brush)FindResource(...)` 直接给 `RouteCard.Background`、`RouteBadge.Background` 等赋值，导致主题热切换后该卡片无法变色。
3. **“恢复询问”按钮层级**：
   - 虽在 2026-09-03 评审后加了 `1px` 分割线，但三个按钮（允许、拒绝、恢复询问）并排占用宽度过长，在窄窗口下容易挤压左侧“内置公共翻译”说明文案。
4. **OCR 语言包列表卡片语法瑕疵**：
   - 第 200 行使用了 `CornerRadius="{DynamicResource ControlRadius}"`，对结构体使用 `DynamicResource` 违反规范，应统一为 `{StaticResource ControlRadius}`。

---

## 七、UI 审计缺陷条目清单

| 缺陷 ID | 优先级 | 文件:行号 | 缺陷现象描述 | 违反规格条款 | 最小修复建议 |
| :--- | :---: | :--- | :--- | :--- | :--- |
| **TR-01** | **P0** | `Themes/Controls.xaml`: 1207–1236, 1279–1308 | `ToggleSwitch` / `SwitchCheckBox` 初始绑定 `IsChecked="True"` 时圆点停在左侧 X=0，仅轨道变色（U08 根因）。WPF `Trigger.EnterActions` 在初始加载时不触发，且缺少静态属性 Setter。 | V2 §13 (开关: on=右、off=左；圆点位置+轨道共同表意)；U08 | 在 `<Trigger Property="IsChecked" Value="True">` 中增加属性设置 `<Setter TargetName="KnobShift" Property="X" Value="20" />`，保证无论是否播放动画，最终状态均准确居右。 |
| **TR-02** | **P0** | `Services/MarkdownPresenter.cs`: 289–293, 436–438, 500, 522 | `FlowDocument` 译文生成时提取静态画刷绑定至 Run/TextBlock。切换主题后已渲染内容不刷新，暗色白字在亮色主题下直接变成“白底白字”彻底不可读（U06 根因）。 | V2 §10.2 U06；README G07；C18 (主题切换后已有文本立即更新) | 在 `FlowDocument` 呈现宿主上监听 `ThemeService.ThemeChanged`，触发当前文档重排或使用动态资源绑定重新设置 Run Foreground。 |
| **TR-03** | **P0** | `Sections/LibrarySection.xaml.cs`: 198–214; `LibrarySection.xaml`: 65–73 | 生词本加载失败/只读保护时（`LoadState != Ok`），主区域仍然显示“生词本还是空的”，提示用户去划词收藏，将严重故障伪装为数据为空；且重试按钮远离主视区。 | README G07 (错误有修复路径)；V2 §12 (空态与无匹配混淆)；C02 | 在 `LibrarySection.ApplyFilter()` 中判断 `_vocabulary.LoadState`，非 Ok 时在主空态区域展示醒目的“生词本加载受阻”警告卡片，内嵌“重试加载”主按钮与错误说明。 |
| **TR-04** | **P0** | `Themes/Controls.xaml`: 367–368, 457–458, 534–535, 783–784, 1079–1080 等 12 处 | 全库控件在 `IsEnabled="False"` 时同时应用 `Surface.Opacity = 0.45` 与 `Foreground = TextDisabledBrush`，导致文字被双重淡化，暗色对比度暴跌至 1.6:1，亮色跌至 1.4:1，严重破坏无障碍可读性。 | V2 §10.2 U07 (避免双重淡化导致标签消失)；DESIGN_SYSTEM §1.1 | 在控件模板 Disabled Trigger 中，移除 `TargetName="Surface" Property="Opacity"`，仅保留 `Foreground="{DynamicResource TextDisabledBrush}"` 与边框弱化；或保留 Opacity 但不单独压暗文本。 |
| **TR-05** | **P1** | `App.xaml`: 16–48 | `App.xaml` 种子资源缺少 `FocusBrush`、`DangerHoverBrush`、`DangerHoverTextBrush`、`DangerPressedBrush`、`DangerPressedTextBrush` 5 个关键 Token。 | DESIGN_SYSTEM §1.1；Controls.xaml: 68 | 在 `App.xaml` 的 ResourceDictionary 中补齐该 5 个 Token 的暗色种子定义，确保设计时与未执行 ThemeService 前解析正常。 |
| **TR-06** | **P1** | `Themes/Controls.xaml`: 776–778, 927–929 | `TextBox` 与 `PasswordBox` 的 `IsMouseOver` 触发器设置 `Surface.BorderBrush` 为 `BorderStrongBrush`，与默认状态完全一致，导致输入框悬停没有任何视觉反馈。 | V2 §13 (输入框: 默认/悬停/按下 边界可识别) | 将输入框 Hover 触发器中的边框改为 `{DynamicResource AccentBorderBrush}`（与 ComboBox 保持一致）。 |
| **TR-07** | **P1** | `ThemeService.cs`: 36–45 | `ThemeService` 仅读取注册表 `AppsUseLightTheme`，完全未响应和支持 `SystemParameters.HighContrast`，不具备高对比主题覆盖。 | V2 §10.3；C18 (高对比优先级与系统颜色映射) | 在 `ThemeService.ApplyResolved()` 中增加高对比模式判断，若系统处于高对比，优先应用系统颜色并移除所有装饰阴影。 |
| **TR-08** | **P1** | `TranslationPanelWindow.xaml.cs`, `QuickSearchWindow.xaml.cs`, `FloatingTriggerWindow.xaml.cs` | 从属窗口未监听 `ThemeService.ThemeChanged`，主题切换后窗口 DWM 沉浸深色标题栏和边框不会同步更新。 | C18；V2 §15 最低检查集 3 | 在上述窗口的构造函数中订阅 `ThemeService.ThemeChanged`，触发 `ThemeService.ApplyWindowChrome(this)`。 |
| **TR-09** | **P1** | `MainWindow.xaml.cs`: 113, 547; `TranslationPanelWindow.xaml.cs`: 540, 620 等 70 余处 | C# 后台广泛使用 `element.Foreground = (Brush)FindResource(...)` 直接给依赖属性赋值，破坏了 XAML 的 DynamicResource 动态更新链。 | C18 (禁止把动态画刷冻结为局部值) | 改为调用 `element.SetResourceReference(TextBlock.ForegroundProperty, "...")`，保持动态资源引用活性。 |
| **TR-10** | **P1** | `Sections/LibrarySection.xaml`: 53–56; `LibrarySection.xaml.cs`: 359–374 | 资料库分栏第 1 列为静态 Rectangle，无 `GridSplitter`，声明的 Min/MaxWidth 无法调整；且删除条目后重置 `SelectedIndex = -1`，未选邻近项。 | V2 §12 资料库 (删除后邻近选择、Master-Detail 布局) | 将分栏分隔线换为 `GridSplitter`；在 `DeleteRow()` 中记录删除位置并在刷新后选中 `Math.Min(index, count - 1)`。 |
| **TR-11** | **P1** | `Sections/LibrarySection.xaml.cs`: 198–215 | 资料库搜索框有内容但搜索结果为 0 时，空态文本依然显示“暂无历史记录 / 生词本还是空的”，产生假象。 | V2 §12 资料库 (空态与无匹配混淆)；UI05 | 当 `!string.IsNullOrEmpty(query)` 时，空态标题改为“未找到匹配结果”，副标题提示“请尝试更换关键词”。 |
| **TR-12** | **P2** | `Themes/Controls.xaml`: 1444, 1571, 1602; `LibrarySection.xaml`: 50, 154, 188, 200; `PrivacySection.xaml`: 21, 28 | 全库存在大量非标准圆角：`CornerRadius="9"` (ListBoxItem)、`"7"` (ToolTip)、`"8"` (ContextMenu / 隐私子卡片)、`"4"` (资料库多处容器)。 | DESIGN_SYSTEM §5.1 (小控件 6, 容器卡片 10, 浮窗 12) | 统一收拢：小控件与项统一为 `6`，卡片与弹出面板统一为 `10`，清理所有 `4/7/8/9` 魔法数值。 |
| **TR-13** | **P2** | `Sections/LibrarySection.xaml`: 16–17 | `LibrarySection` 页面大标题显式覆盖 `FontSize="15"`，与全局 `PageTitle` (20 DIP) 规范严重冲突。 | DESIGN_SYSTEM §2.2 (PageTitle 20px)；UI-REVIEW P2 | 移除本地 `FontSize="15"` 覆盖，恢复为标准的 `PageTitle` 样式。 |
| **TR-14** | **P2** | `Themes/Controls.xaml`: 618–638, 962–975, 1346–1362, 1390–1405, 1449–1457, 1492–1505 | `NavButton`、`ComboBoxItem`、`CheckBox`、`RadioButton`、`ListBoxItem`、`Expander` Header 均缺少 `IsPressed` 按下态视觉反馈。 | V2 §13 (控件状态合同：默认/悬停/按下) | 为上述模板补充 `<Trigger Property="IsPressed" Value="True">`，将背景设置为 `{DynamicResource SurfacePressedBrush}`。 |
| **TR-15** | **P2** | `Themes/Controls.xaml`: 715–721 | `HotkeyRecorder` 在录制中状态（`IsRecording=True`）时，若鼠标悬停，会被基类 Button 模板的 `IsMouseOver` 冲掉高亮背景和边框。 | 体验缺陷 | 在 HotkeyRecorder 样式中增加 MultiTrigger（`IsRecording=True` 且 `IsMouseOver=True`），保持高亮并使用 `AccentHoverBrush`。 |
| **TR-16** | **P2** | `Sections/PrivacySection.xaml`: 21, 28 | 承诺卡片内的两个子容器背景直接设为 `SurfaceMutedBrush`，且无边框，与父卡片背景同化，两卡片在视觉上完全隐形。 | DESIGN_SYSTEM §1.1；体验缺陷 | 将子容器背景改为 `{DynamicResource SurfaceBrush}`，并增加 `BorderBrush="{DynamicResource BorderSubtleBrush}" BorderThickness="1"`。 |
| **TR-17** | **P2** | `Sections/PrivacySection.xaml`: 200 | `Border CornerRadius="{DynamicResource ControlRadius}"` 对结构体使用 DynamicResource。 | XAML 规范缺陷 | 修改为 `{StaticResource ControlRadius}`。 |
| **TR-18** | **P2** | `Themes/Controls.xaml`, `App.xaml` 全局 | 全库无任何 Margin / Padding / Spacing 资源 Token 定义，模板与各分区页面写满散落数值。 | V2 §10.2 U09 (建立有限间距/字号等级)；DESIGN_SYSTEM §5.2 | 在 `Controls.xaml` 中建立 `Spacing*` 标尺（4, 8, 12, 16, 20 DIP）并在后续重构中逐步替换。 |
| **TR-19** | **P2** | `Themes/Controls.xaml`: 1055, 1158, 1605; `CaptureOverlayWindow.xaml`: 61; `FloatingTriggerWindow.xaml`: 18 | 全库 DropShadowEffect 一律硬编码 `Color="#000000"`，缺乏统一的 `ShadowColor` / `ShadowBrush` Token。 | V2 §10.3 (阴影角色) | 在 `ThemeService` 中增加 `ShadowColor` Token（暗色 `#000000`，亮色带轻微微调），统一替换硬编码。 |

---

## 八、Top 5 核心修复优先级排序

依据对用户正常使用、可访问性门槛以及界面核心功能的破坏严重程度，列出最优先安排修复的 Top 5 缺陷：

```
+-----------------------------------------------------------------------------------------+
|                                  TOP 5 核心修复路线                                     |
+-----------------------------------------------------------------------------------------+
| 1. [TR-01 / P0] 修复 ToggleSwitch / SwitchCheckBox 静态加载圆点居左脱靶 (U08 根因)      |
|    - 影响范围：设置窗口、隐私分区、通用分区所有拨杆开关。                               |
|    - 修复动作：在 Controls.xaml 的 IsChecked=True 触发器中增加 KnobShift.X=20 静态 Setter。 |
+-----------------------------------------------------------------------------------------+
| 2. [TR-02 / P0] 修复 FlowDocument 动态切主题文字变黑/变隐形问题 (U06 / C18)              |
|    - 影响范围：翻译结果展示面板、查词面板、主工作台译文区。                             |
|    - 修复动作：订阅 ThemeChanged，热切换时重新刷新文档前景色，消除白底白字致命对比度问题。 |
+-----------------------------------------------------------------------------------------+
| 3. [TR-04 / P0] 修复全库控件模板 Disabled 状态双重淡化导致的文本不可读 (U07)            |
|    - 影响范围：全库 Button、TextBox、ComboBox、SegmentButton 禁用态。                    |
|    - 修复动作：移除 Disabled 触发器中的父级 Surface.Opacity 叠加，保证单层 TextDisabledBrush。|
+-----------------------------------------------------------------------------------------+
| 4. [TR-03 / P0] 修复 LibrarySection 生词本损坏/只读错误态被伪装为“生词本是空的” (C02/U12)|
|    - 影响范围：资料库分区生词本核心交互。                                               |
|    - 修复动作：主列表区区分 LoadState != Ok，直接展示错误详情卡片并内嵌重试加载主按钮。 |
+-----------------------------------------------------------------------------------------+
| 5. [TR-09 / P1] 消除代码后台 (Brush)FindResource 破坏 DynamicResource 绑定的反模式      |
|    - 影响范围：各窗口状态指示点（StatusDot）、线路徽章（RouteBadge）、复制成功图标等。 |
|    - 修复动作：全面改用 element.SetResourceReference()，并为从属窗口补齐 ThemeChanged。 |
+-----------------------------------------------------------------------------------------+
```
