# PopGlot UI 专项审计报告：翻译面板 / 极速查词 / 悬浮球 / 截图覆盖 / 主窗口

> 审计日期：2026-09-13
> 审计人：OpenCode (Teammate)
> 审计范围：
> - `apps/PopGlot.Windows/TranslationPanelWindow.xaml(.cs)`
> - `apps/PopGlot.Windows/QuickSearchWindow.xaml(.cs)`
> - `apps/PopGlot.Windows/FloatingTriggerWindow.xaml(.cs)`
> - `apps/PopGlot.Windows/CaptureOverlayWindow.xaml(.cs)`
> - `apps/PopGlot.Windows/MainWindow.xaml(.cs)`
>
> 审计基线：
> - `docs/review-2026-09-12/README.md`（重点 G03/G05/G07/G08/G09）
> - `docs/review-2026-09-12/PRODUCT-EXPANSION-REVIEW-V2.md` 第 10–15 节（U01–U12、布局与控件状态合同）
> - `docs/DESIGN_SYSTEM.md` (v0.1.3+)
> - 参考 `docs/UI-REVIEW-2026-09-03.md`
>
> 审计原则：只读审查，基于当前工作树未提交代码，逐行比对 XAML 尺寸、状态触发器、流式渲染机制与异常路径。

---

## 目录
1. [审计概述与统计](#一审计概述与统计)
2. [重点专题一：浮窗关键按钮尺寸逐个实测（G07 ≥32 DIP 与误触间距）](#二重点专题一浮窗关键按钮尺寸逐个实测)
3. [重点专题二：可交互元素四态完整性与辨识度（Hover / Pressed / Disabled / Focus）](#三重点专题二可交互元素四态完整性与辨识度)
4. [重点专题三：关闭/固定/复制/朗读/重试入口的语义与可点性](#四重点专题三关闭固定复制朗读重试入口的语义与可点性)
5. [重点专题四：无可用服务时弹出的面板空态、错误反馈与修复路径（C17 方向）](#五重点专题四无可用服务时弹出的面板空态错误反馈与修复路径)
6. [重点专题五：流式翻译渲染的视觉稳定性（闪白、跳动、错位）](#六重点专题五流式翻译渲染的视觉稳定性)
7. [重点专题六：窗口 DPI 与多显示器环境下的对齐与裁剪线索](#七重点专题六窗口-dpi-与多显示器环境下的对齐与裁剪线索)
8. [重点专题七：色彩、字号、间距与圆角硬编码清单](#八重点专题七色彩字号间距与圆角硬编码清单)
9. [UI 审计缺陷条目清单（ID / 级别 / 文件:行 / 现象 / 规格条款 / 最小修复建议）](#九ui-审计缺陷条目清单)
10. [Top 5 核心修复优先级排序](#十top-5-核心修复优先级排序)

---

## 一、审计概述与统计

本次审计针对 PopGlot Windows 客户端的 5 个关键交互窗口（翻译浮窗、极速查词、划词悬浮触发球、全屏截图覆盖、主工作台窗口）及其后台代码（Code-Behind）进行了全面深度的只读源码审计。

### 发现统计
- **缺陷总数**：19 项
- **P0 严重级别**：4 项（严重影响主流程、错误状态导致功能阻断、流式阅读严重跳动/闪白）
- **P1 次级重要**：9 项（按钮尺寸不达标、误触风险、四态缺失、多屏与DPI偏移、断点未折叠）
- **P2 一致性/细节**：6 项（字号与网格脱轨、TTS状态反馈缺失、硬编码画刷、菜单双重淡化）

---

## 二、重点专题一：浮窗关键按钮尺寸逐个实测

依据 `docs/review-2026-09-12/README.md` G07 及 `PRODUCT-EXPANSION-REVIEW-V2.md` §11.1：
> “关键浮窗按钮至少 32 DIP，覆盖旧规则 28 DIP 例外；浮窗图标按钮点击区至少 32×32 DIP，图形 16–18 DIP 左右，关闭与相邻按钮留至少 8 DIP 间隔。”

对目标窗口中所有按钮的 XAML 声明尺寸进行逐个实测：

### 2.1 翻译面板窗口 (`TranslationPanelWindow.xaml`)
| 控件名称 | XAML 行号 | 元素类型 | 声明尺寸 (W×H DIP) | 是否达标 (≥32 DIP) | 缺陷分析与相邻间距 |
|---|---|---|---|---|---|
| `PinToggle` | 48 | `ToggleButton` | 30 × 30 | ❌ **不达标** | 固定浮窗开关，低于 32 DIP 门槛 |
| `SwapLangButton` | 98 | `Button (IconButton)` | 30 × 30 | ❌ **不达标** | 交换源/目标语言方向按钮，点击区偏小 |
| `ExpandButton` | 112 | `Button (IconButton)` | 30 × 30 | ❌ **不达标** | 在主窗口展开按钮 |
| `SettingsButton` | 121 | `Button (IconButton)` | 30 × 30 | ❌ **不达标** | 打开设置按钮 |
| `CloseButton` | 130 | `Button (IconButton)` | 30 × 30 | ❌ **不达标** | 关闭按钮仅 30×30；与 Settings 间距仅 `Margin="2,0,0,0"`（**严重违背 ≥8 DIP 误触隔离要求**） |
| `SourceSpeakBtn` | 179 | `Button (IconButton)` | 30 × 30 | ❌ **不达标** | 原文朗读按钮 |
| `SourceCopyBtn` | 186 | `Button (IconButton)` | 30 × 30 | ❌ **不达标** | 原文复制按钮 |
| `MergeLines` | 193 | `Button (IconButton)` | 30 × 30 | ❌ **不达标** | 合并换行按钮 |
| `SourceClear` | 200 | `Button (IconButton)` | 30 × 30 | ❌ **不达标** | 原文清空按钮 |
| `RetryButton` | 208 | `Button (IconButton)` | 30 × 30 | ❌ **不达标** | 重试按钮（Ctrl+R） |
| `ResultSpeakBtn` | 252 | `Button (IconButton)` | 30 × 30 | ❌ **不达标** | 译文朗读按钮 |
| `ResultCopyBtn` | 258 | `Button (IconButton)` | 30 × 30 | ❌ **不达标** | 译文复制按钮 |
| `StarToggle` | 264 | `ToggleButton` | 30 × 30 | ❌ **不达标** | 收藏到生词本开关 |
| `SourceLangCombo` / `TargetLangCombo` | 93, 103 | `ComboBox` | Height="28" | ❌ **不达标** | 样式 `LanguagePickerComboBox` 高度仅 28 DIP |

> **实测结论**：`TranslationPanelWindow` 内 **全部 13 个图标按钮** 无一例外被显式硬编码为 `30×30 DIP`，全线跌破 G07 规定的 32 DIP 门槛；且关闭按钮与设置按钮的间距仅 2 DIP，误点概率极高。

### 2.2 极速查词窗口 (`QuickSearchWindow.xaml`)
| 控件名称 | XAML 行号 | 元素类型 | 声明尺寸 (W×H DIP) | 是否达标 (≥32 DIP) | 缺陷分析 |
|---|---|---|---|---|---|
| `CloseButton` | 54 | `Button (IconButton)` | 30 × 30 | ❌ **不达标** | 顶部输入栏关闭按钮 |
| `SpeakButton` | 95 | `Button (IconButton)` | 30 × 30 | ❌ **不达标** | 朗读译文按钮 |
| `CopyButton` | 102 | `Button (IconButton)` | 30 × 30 | ❌ **不达标** | 复制译文按钮 |
| `StarButton` | 110 | `Button (IconButton)` | 30 × 30 | ❌ **不达标** | 收藏生词按钮 |

### 2.3 悬浮触发球 (`FloatingTriggerWindow.xaml`)
| 控件名称 | XAML 行号 | 元素类型 | 声明尺寸 (W×H DIP) | 是否达标 (≥32 DIP) | 缺陷分析 |
|---|---|---|---|---|---|
| `ButtonSurface` | 13 | `Border (Circle)` | 32 × 32 | ✅ **达标** | 核心圆形图标为 32×32 DIP；但宿主窗口 46×46 且全透明背景可响应点击 |

---

## 三、重点专题二：可交互元素四态完整性与辨识度

根据规范（V2 §10.3、§13、DESIGN_SYSTEM.md），可交互元素必须具备 Hover（悬停）、Pressed（按下）、Disabled（禁用）、Focus（键盘焦点）四态，且“选中（Selected/Checked）与焦点（Focus）可同时辨识”。

### 3.1 致命缺陷：局部属性赋值破坏 Hover/Pressed/Focus 图标变色
- **机理剖析**：
  在 `TranslationPanelWindow.xaml` 中，所有 `IconButton` 的内容均采用以下写法：
  ```xml
  <Button x:Name="ExpandButton" Style="{StaticResource IconButton}">
      <Path Width="11" Height="11" Fill="{DynamicResource TextSecondaryBrush}" Data="..." />
  </Button>
  ```
  在 `Controls.xaml:522–532` 中，`IconButton` 的 Style 触发器通过设置 `Foreground = TextPrimaryBrush` 来响应 Hover、Pressed 与 Focus。
  然而，在 WPF 的依赖属性优先级中，**元素本身的局部值（Local Value）高于样式触发器（Style Trigger）**。由于 `<Path>` 上直接显式设置了 `Fill="{DynamicResource TextSecondaryBrush}"`，导致宿主 Button 的 Foreground 变化根本无法渗透到内部 Path！
  同样，在 `TranslationPanelWindow.xaml.cs:927, 949` 中：
  ```csharp
  ResultCopyIcon.Fill = (Brush)FindResource("TextSecondaryBrush");
  ```
  复制完成后直接给 `Fill` 赋硬值，进一步将图标颜色死锁在暗灰色，鼠标悬停完全无明暗反馈。
- **结果**：全窗几乎所有图标按钮在悬停、按下、获得焦点时，仅有浅浅的背景框变化，图标自身无法点亮至 `TextPrimaryBrush`，视觉辨识度极差。

### 3.2 关键开关控件的状态缺失
1. **`PinToggle`（固定浮窗）**：
   - 具有 Hover、Checked 状态。
   - ❌ **缺失 Pressed 状态**：点击按下时无任何凹陷或加深反馈。
   - ❌ **缺失 Disabled 状态**：模板中无 `IsEnabled="False"` 触发器。
   - ❌ **缺失 Focus 状态**：未设置 `FocusVisualStyle`，且无键盘焦点触发器。若键盘 Tab 聚焦到已 Checked 的固定按钮上，用户完全无法区分其是仅仅被选中还是当前拥有键盘焦点。
2. **`StarToggle`（收藏生词）**：
   - 具有 Hover、Checked、Disabled 状态。
   - ❌ **缺失 Pressed 状态**。
   - ❌ **缺失独立 Focus 环**：未指定 `FocusRing`，选中态与焦点态混淆。
3. **`FloatingTriggerWindow`（悬浮球）**：
   - 仅通过 Code-Behind 处理了 MouseEnter / MouseLeave。
   - ❌ **全无 Pressed、Disabled、Focus 状态**；窗口不可获焦，完全无法被全键盘用户使用。
4. **`MainWindow` 标题栏按钮**：
   - `MinimizeBtn`、`MaximizeBtn`、`CloseBtn` 均在样式中声明了 `Focusable="False"`，全键盘 Tab 无法导航至窗口控制。

---

## 四、重点专题三：关闭/固定/复制/朗读/重试入口的语义与可点性

### 4.1 复制入口（Copy）—— 违反 G06/N04 Partial 显式复制规格（🔴 P0）
- **现象定位**：`TranslationPanelWindow.xaml.cs:915`、`TranslationPanelStreamGate.cs:125–127`
  ```csharp
  public bool CanPerformResultActions =>
      Stage == TranslationPanelStage.Completed && !string.IsNullOrWhiteSpace(StreamedText);
  ```
  当流式翻译生成中断（Cancelled）或网络失败（Failed）但已经接收到部分译文时：
  `TranslationPanelWindow` 执行 `RenderFailedWithPartial` 或 `RenderCancelledWithPartial`，调用 `SetResultActionsEnabled(false)`，直接禁用了 `ResultCopyBtn`。
- **违背条款**：
  `docs/review-2026-09-12/README.md` G06 明确裁决：
  > “**G06 partial 与保真：V2 N04 明确允许独立、显式“复制已生成部分”，覆盖旧规则全面禁用 partial 结果动作的限制**；自动复制/朗读/历史/收藏仍禁止。”
- **危害**：网络慢或模型被截断时，屏幕明明显示了一半译文，用户想要手动复制这部分内容，却发现"复制译文"按钮被禁用变灰，严重阻断日常生产力。

### 4.2 固定入口（Pin）
- `PinToggle` 的 ToolTip 在固定状态和未固定状态恒为静态字符串 `"固定浮窗"`，既不指示“已固定，失焦不隐藏”，也不变为“取消固定”。
- `AutomationProperties.Name` 同样为静态 `"固定浮窗"`，读屏器无法播报当前固定状态。

### 4.3 朗读入口（TTS Speak）
- `TranslationPanelWindow` 中朗读状态已与 `TtsService.SpeakingStateChanged` 联动，但停止朗读时向 `Icon.Fill` 赋硬值引入了前述的悬停死锁。
- `QuickSearchWindow` 中的 `SpeakButton` 则**完全未监听** `TtsService.SpeakingStateChanged`。播放长语音时，图标既不显示高亮声波，Tooltip 也不更新为“停止朗读”，用户在播放期间无法中断朗读。

### 4.4 重新翻译入口（Retry）
- `RetryButton`（Ctrl+R）在点击后启动异步任务，但在请求进行中（In-flight / Busy 状态），该按钮**未被禁用**，也没有 busy 旋转动画。用户连续敲击或快速多次点击会导致并发重复请求，触发未定义的竞态竞争。

---

## 五、重点专题四：无可用服务时弹出的面板空态、错误反馈与修复路径（C17 方向）

### 5.1 极速查词（QuickSearch）空态与错误崩溃撞车（🔴 P0）
- **现象定位**：`QuickSearchWindow.xaml:160–170` 与 `QuickSearchController.cs:193–206, 242–250`
  当用户初次启动未配置 API Key 或无可用服务时在极速查词按 Enter：
  1. `session.TranslatedText` 为空，`hasPartial` 为 `false`。
  2. `ResultContainer` 容器被完全收起折叠（`Visibility = Collapsed`），**窗口完全不展示结果卡片**。
  3. 错误信息被放入底部状态行 `FooterStatus.Text`。
  4. 底栏布局 XAML 如下：
     ```xml
     <Border Grid.Row="2" ...>
         <Grid>
             <TextBlock x:Name="FooterStatus" Style="{StaticResource Metadata}" Text="..." />
             <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                 <TextBlock Text="Ctrl+C 复制 · Ctrl+P 朗读 · Esc 退出" Style="{StaticResource Metadata}" />
             </StackPanel>
         </Grid>
     </Border>
     ```
     **该 Grid 根本没有定义 ColumnDefinitions！** `FooterStatus` 占满整行，直接硬生生与右侧的快捷键提示文本重叠叠加，文字撞车糊成一团！
  5. 更严重的是：**极速查词窗口中没有任何设置入口、齿轮图标或修复按钮！** 用户看到一段被截断糊掉的红字，完全没有任何路径前往设置或开启免费引擎，陷入死胡同。
- **违背条款**：C17 方向（错误反馈必须提供可操作的修复路径）；DESIGN_SYSTEM.md §3（西文长句单行预览强制 Ellipsis，状态栏弹性列与固定列分离，严禁互相挤压重叠）。

### 5.2 翻译面板（TranslationPanel）“全屏通红”与缺乏修复路径（P1）
- **现象定位**：`TranslationPanelWindow.xaml.cs:804–822` `RenderFailure`
  当报错（如“尚未配置模型服务”）时：
  - `EngineBadge`（徽章）染红（`DangerBrush`）
  - `TranslationTextBox`（正文）染红（`DangerBrush`）
  - `ExplanationText`（说明）染红（`DangerBrush`）
  - `StatusDot`（状态灯）染红（`DangerBrush`）
  大面积红字刺激视觉，直接违反 V2 §10.2 U05 规划要求（“提示正文用正常色，单一主要失败原因”）。
  且虽然右上角有微型设置图标，但正文中提示的“可检查网络或设置后重试”只是一行只读文本，**没有提供可直接点击的“打开设置”按钮或超链接**。

---

## 六、重点专题五：流式翻译渲染的视觉稳定性

### 6.1 流式文本框背景闪烁与闪底（🔴 P0）
- **机理分析**：
  在 `TranslationPanelWindow.xaml:299` 和 `QuickSearchWindow.xaml:122` 中，流式渲染容器为：
  ```xml
  <TextBox x:Name="TranslationTextBox" Style="{StaticResource FlatTextBox}" IsReadOnly="True" ... />
  ```
  查阅 `Controls.xaml:786–788`，`FlatTextBox` 派生自基础 TextBox 模板，其中定义了：
  ```xml
  <Trigger Property="IsReadOnly" Value="True">
      <Setter TargetName="Surface" Property="Background" Value="{DynamicResource SurfaceMutedBrush}" />
  </Trigger>
  ```
  而在 Markdown 流式完成转为富文本时：
  `TranslationRichBox` 显式设置了 `Background="Transparent"`！
- **破坏表现**：
  在流式输出期间，译文区域底色呈现暗灰色的 `SurfaceMutedBrush`（`#14161D` / `#F8F9FA`）；当流式完成切换到 RichTextBox 的瞬间，底色突然闪变回父级容器的 `SurfaceBrush`（`#181B22` / `#FFFFFF`），在暗色和亮色模式下均会产生强烈的**底色闪烁（Flash）**。
- **正解**：`Controls.xaml:805` 早已专为阅读器和流式容器设计了 `ResultTextBox`（注释明确注明：“Keeps background transparent so the parent reading canvas shows through seamlessly, without the IsReadOnly trigger forcing a muted input-box surface”），但两个浮窗均错误引用了 `FlatTextBox`。

### 6.2 流式文本增长引发浮窗跳动移位（🔴 P0）
- **机理分析**：
  在 `TranslationPanelWindow.xaml.cs:100` 中：
  ```csharp
  SizeChanged += (_, _) => PositionNearAnchor();
  ```
  流式翻译逐字推进时，随着段落文本折行，浮窗高度 `ActualHeight` 不断增加，触发 `SizeChanged`。
  `PositionNearAnchor` 每次都重新调用 `WindowPositioner.NearAnchor` 循环检测 4 个候选位置（下方、右方、左方、上方）。
  当浮窗高度增长到某一行、导致“下方”候选位置的下边缘触碰屏幕工作区底边界（`workArea.Bottom - Edge`）时，`NearAnchor` 会**立刻判定下方不适用，突然将整窗翻转瞬移到光标上方或另一侧！**
- **破坏表现**：用户正在阅读流式文本，浮窗突然在屏幕上跳动闪现到另一个位置，阅读视线彻底断裂。
- **违背条款**：README.md G07 / V2 §12（“翻译浮窗：结果不跳动”）、§13（“动效不移动正文，流式期间不重排”）。

### 6.3 极速查词 `SizeToContent="Height"` 的边缘抖动（P1）
- `QuickSearchWindow` 声明了 `SizeToContent="Height"`，在输入和流式生成阶段窗口高度每一帧都在实时变化，引起 Win32 窗口外边框的高频重绘与边缘闪烁；流式结束后 TextBox 到 FlowDocument 的几像素排版差异还会引发窗口下沿瞬间抽搐。

---

## 七、重点专题六：窗口 DPI 与多显示器环境下的对齐与裁剪线索

### 7.1 物理像素与 DIP 混淆计算（P1）
- **定位**：`apps/PopGlot.Windows/WindowPositioner.cs:19–20`
  ```csharp
  private const double Gap = 14;
  private const double Edge = 12;
  ```
  定位器注释明确指出内部所有数值均为物理像素。然而，`Gap = 14` 在 100% 缩放下为 14 DIP；在 200% 缩放的 4K 显示器上，实际仅为 **7 DIP**，浮窗几乎贴死在文字选区上，缺乏呼吸感。
- **定位**：`FloatingTriggerWindow.xaml.cs:30–31`
  ```csharp
  Left = screenPos.X + CursorOffsetX;
  Top = Math.Max(MinScreenMargin, screenPos.Y - CursorOffsetY);
  ```
  此处直接将传入的 `screenPos`（来自 Win32 光标物理像素）直接赋值给 WPF 的 `Window.Left/Top`（WPF 期望 DIP）。在 150% 或 200% 缩放下，悬浮图标出现位置严重漂移。且缺少右边界与下边界的屏幕工作区裁剪，靠近屏幕边缘时悬浮球直接飞出屏幕。

### 7.2 多屏跨 DPI 截图覆盖错位与假手柄（P1）
- **定位**：`CaptureOverlayWindow.xaml.cs:213–215`
  ```csharp
  var scale = ScreenGeometry.ScaleOf(this);
  var pixelWidth = (int)Math.Round(rect.Width * scale.X);
  ```
  在跨屏全桌面覆盖时，`ScaleOf(this)` 返回的是主屏幕的 DPI 缩放。若用户在 150% 缩放的副屏上拉框截图，尺寸标签显示的像素尺寸计算错误（按主屏缩放计算）。
- **定位**：`CaptureOverlayWindow.xaml:41–49`（四个假手柄）
  选区四角绘制了 `HandleTopLeft` 等 4 个控制手柄，暗示用户可拖拽调节选区大小。但实际上在 `OnMouseLeftButtonUp` 鼠标松开瞬间立即提交截屏并关闭窗口，完全不支持手柄拖拽微调，属于典型的视觉误导（伪交互）。

### 7.3 多窗口主题与 DWM 沉浸式边框热同步断裂（P1）
- `MainWindow` 与 `SettingsWindow` 均订阅了 `ThemeService.ThemeChanged` 并调用 `ApplyWindowChrome`。
- 但 `TranslationPanelWindow`、`QuickSearchWindow`、`FloatingTriggerWindow` **均未订阅 `ThemeService.ThemeChanged`**。当用户切换系统深浅色或在设置中改动主题时，已打开的浮窗的 DWM 沉浸式深色外边框和阴影不会同步更新，残留旧主题边框。

---

## 八、重点专题七：色彩、字号、间距与圆角硬编码清单

### 8.1 字号脱轨与层级混乱
对照 `docs/DESIGN_SYSTEM.md` §2.2 标准排版矩阵（PageTitle 20, SectionTitle 13, RowTitle 13, Body 13, Content Large 14.5, Caption 12, Metadata 11）：
- `MainWindow.xaml:38`：品牌标使用 `FontSize="15"`（直接覆写了 `PageTitle` 的 20 DIP 规格）。
- `TranslationPanelWindow.xaml:81`：品牌标使用 `FontSize="12.5"`（非标字号）。
- `TranslationPanelWindow.xaml:162`：`SourceInputBox` 采用 `FontSize="14.5"`（输入框应为 13–14 DIP，14.5 专属译文大正文）。
- `TranslationPanelWindow.xaml:233`：`EngineBadge` 采用 `FontSize="12"`（Metadata 规范为 11 DIP）。
- `QuickSearchWindow.xaml:43`：`SearchBox` 采用 `FontSize="15"`（非标字号）。

### 8.2 间距网格脱轨（非 4/8 倍数）
对照 `DESIGN_SYSTEM.md` §5.2（微间距 4/6/8，内边距 10/12/14/16）：
- `TranslationPanelWindow.xaml:168`：`Margin="0,5,0,0"`
- `TranslationPanelWindow.xaml:219`：`Margin="0,7,0,7"`
- `TranslationPanelWindow.xaml:230`：`Margin="0,0,0,5"`
- `TranslationPanelWindow.xaml:350`：`Padding="8,5"`
- `QuickSearchWindow.xaml:157`：`Padding="16,7"`

### 8.3 硬编码画刷与阴影
- `FloatingTriggerWindow.xaml:18`：`DropShadowEffect Color="#000000"`
- `FloatingTriggerWindow.xaml:15`：`BorderThickness="1.2"`（非标小数边框）
- `CaptureOverlayWindow.xaml:13–17`：硬编码 `#66FFFFFF`、`#F2FFFFFF`、`#01000000`
- `CaptureOverlayWindow.xaml:61`：`DropShadowEffect Color="#000000"`

---

## 九、UI 审计缺陷条目清单

| ID | 级别 | 文件:行 | 现象 | 违反规格条款 | 最小修复建议 |
|---|---|---|---|---|---|
| **WIN-01** | **P0** | `QuickSearchWindow.xaml:160–170` | 极速查词无服务时结果卡片完全收起，长错误文案塞入单格底栏，与右侧快捷键提示文字严重重叠挤压撞车；且全窗完全没有设置入口引导，用户陷入操作死胡同。 | C17（错误需给修复路径）；DESIGN_SYSTEM.md §3（状态栏弹性列与固定列分离，严禁互相挤压重叠）；V2 §12。 | 底栏 Grid 明确分为双列并给左侧开启 Ellipsis；或者在无可用服务时展示独立错误卡片，内嵌“打开设置”按钮与快捷键（Ctrl+,）。 |
| **WIN-02** | **P0** | `TranslationPanelWindow.xaml.cs:518–526, 601–626, 821`；`TranslationPanelStreamGate.cs:125` | 浮窗翻译中断或报错但保留部分内容（Partial）时，复制按钮被粗暴禁用，用户无法点击复制已生成的半截译文。 | README.md G06 / V2 N04（“明确允许独立、显式‘复制已生成部分’，覆盖旧规则全面禁用 partial 结果动作的限制”）。 | 在 `StreamGate` 中放宽显式点击复制条件（`HasPartialText` 时允许手工复制），并在 Partial 渲染方法中保持 `ResultCopyBtn.IsEnabled = true`。 |
| **WIN-03** | **P0** | `TranslationPanelWindow.xaml:299`；`QuickSearchWindow.xaml:122` | 流式翻译输出文本框误用 `FlatTextBox`（带 IsReadOnly 灰色背景），流式结束切换至 Transparent 的 RichTextBox 瞬间产生剧烈底色闪烁（闪白/闪灰）。 | V2 §10.1, §13（流式期间视觉稳定，不闪白不跳动）；Controls.xaml:805（ResultTextBox 设计初衷）。 | 将 `TranslationTextBox` 与 `ResultStreamBox` 的 Style 替换为专用的 `ResultTextBox`，保持透明底色与无光标闪烁。 |
| **WIN-04** | **P0** | `TranslationPanelWindow.xaml.cs:100, 1481–1492` | 浮窗流式输出导致高度动态增长触发 `SizeChanged` 时，重新运行 `NearAnchor` 翻转计算，导致浮窗在用户阅读过程中突然从光标下方跳跃至上方或屏幕边缘。 | README.md G07（结果不跳动）；V2 §12, §13（不在流式期间移动正文）。 | 首次定位后锁定基准锚点方向（`_anchorLocked = true`），流式期间仅向下或内部滚动，禁止方向翻转跳动。 |
| **WIN-05** | **P1** | `TranslationPanelWindow.xaml:48, 98, 112, 121, 130...` (13处)；`QuickSearchWindow.xaml:54, 95...` (4处) | 浮窗 17 处关键按钮全量实测尺寸均为 30×30 DIP，语言下拉框高 28 DIP，全部跌破 32 DIP 规格下限；且浮窗右上角关闭按钮与设置按钮间距仅 2 DIP，误触风险极高。 | README.md G07（“关键浮窗按钮至少 32 DIP，覆盖旧规则 28 DIP 例外”）；V2 §11.1（关闭与相邻按钮留至少 8 DIP 间隔）。 | 将浮窗全部 IconButton 尺寸恢复为 32×32 DIP，语言下拉框最小高度设为 32 DIP，关闭按钮左边距调整为 `Margin="8,0,0,0"`。 |
| **WIN-06** | **P1** | `TranslationPanelWindow.xaml:99, 115, 124, 133, 183, 190...` | 浮窗所有 IconButton 内部的 Path 均硬编码了 `Fill="{DynamicResource TextSecondaryBrush}"`，导致鼠标悬停（Hover）、按下与焦点时图标无法跟随按钮 Foreground 点亮。 | V2 §10.3（状态合成完整性）；DESIGN_SYSTEM.md §1.1。 | 移除 Path 上的固定 Fill 属性，改由 TemplateBinding Foreground 继承；code-behind 改用 `ClearValue` 恢复。 |
| **WIN-07** | **P1** | `TranslationPanelWindow.xaml:48–75, 264–293` | `PinToggle` 与 `StarToggle` 关键切换控件缺失 `IsPressed` 按下态，`PinToggle` 缺失 `IsEnabled="False"` 禁用态；键盘 Tab 聚焦与选中态并存时无法同时辨识。 | 重点②（四态完整，选中与焦点可同时辨识）；V2 §13（控件状态合同）。 | 补齐 `IsPressed` 触发器，设置 `FocusVisualStyle="{StaticResource FocusRing}"`，区分选中背景与独立焦点外环。 |
| **WIN-08** | **P1** | `TranslationPanelWindow.xaml.cs:568, 620, 808, 812, 819` | 翻译失败时将徽章、文本框正文、说明文字、状态点全屏涂红（DangerBrush），且错误说明区未提供可点击的“前往设置”修复入口。 | V2 §10.2 U05（“错误标题、结果正文、底部详情均红色... 提示正文用正常色，给出下一步”）；C17 方向。 | 正文恢复正常文本色，仅状态点/徽章用 DangerBrush；说明区嵌入可点击的“打开设置”按钮。 |
| **WIN-09** | **P1** | `CaptureOverlayWindow.xaml:41–50`；`CaptureOverlayWindow.xaml.cs:91–116` | 截图覆盖层在选区四角绘制了 4 个 8×8 矩形手柄，视觉暗示可拖动微调，但鼠标松开立刻强制提交并关窗，实为不可交互的“假手柄”。 | V2 §12（“截图/OCR: 可拖控制点仅在真可拖时出现... 用户能判断是否会立即提交”）。 | 移除不可交互的四角手柄，或者实现选区完成后停顿并允许拖动手柄微调的确认模式。 |
| **WIN-10** | **P1** | `QuickSearchWindow.xaml:5, 9`；`App.xaml.cs:1017` | 极速查词使用 `WindowStartupLocation="CenterScreen"`，多屏环境下永远弹在主显示器中央；且 `SizeToContent="Height"` 在高缩放屏幕向下延伸易溢出屏幕底部。 | 重点⑥（窗口 DPI/多显示器对齐）；README.md G07。 | 改用光标所在屏幕工作区居中（`WorkAreaForPixel(CursorPixels)`），并限制 MaxHeight 不超过该屏可用高度。 |
| **WIN-11** | **P1** | `FloatingTriggerWindow.xaml.cs:30–31` | 悬浮触发球直接把物理像素光标坐标赋给 WPF DIP Left/Top，高分屏（150%/200%）严重偏移；且缺少屏幕边界夹逼，不支持键盘操作。 | 重点⑥（物理像素与 DIP 混用）；DESIGN_SYSTEM.md 无障碍规范。 | 通过 `ScaleOf` 进行物理到 DIP 换算，增加屏幕边缘 Margin 夹逼限制；提供键盘激活支持。 |
| **WIN-12** | **P1** | `MainWindow.xaml:18`；`MainWindow.xaml.cs:492–497` | 主窗口 MinWidth=520 时，固定 168 DIP 的侧栏占宽 32.3%，挤压翻译正文区；窗口收窄时侧栏从未折叠，违反 U01 规划。 | V2 §10.2 U01（“主窗口 MinWidth=520，侧栏固定 168 DIP，挤压翻译区。导航随实际可用宽度折叠”）；G07。 | 在客户区宽度 < 720 DIP 时将侧栏折叠为 48 DIP 紧凑图标模式，< 560 DIP 时进一步折叠。 |
| **WIN-13** | **P1** | `WindowPositioner.cs:19–20`；`TranslationPanelWindow.xaml.cs:97` | 窗口定位器 Gap=14 和 Edge=12 为物理像素硬编码，在 200% DPI 屏间距减半（仅 7 DIP）；从属窗口未监听 ThemeChanged 导致 DWM 边框不更新。 | 重点⑥；TR-08（从属窗口未监听 ThemeChanged）；V2 §15 最低检查集 3。 | Gap/Edge 改为按当前屏 DPI 换算的 DIP；从属窗口全部在构造函数中订阅 `ThemeService.ThemeChanged`。 |
| **WIN-14** | **P2** | `QuickSearchWindow.xaml.cs:345–375, 480–495` | 极速查词朗读按钮未监听 `SpeakingStateChanged`，播放中无声波高亮也无停止提示；StarButton 仅变图标颜色，缺乏选中衬底，辨识度弱。 | 重点③（朗读/复制入口的可点性与状态反馈）；DESIGN_SYSTEM.md §1.1。 | 订阅 `SpeakingStateChanged` 联动图标与 ToolTip；StarButton 引入 `AccentSoftBrush` 选中衬底。 |
| **WIN-15** | **P2** | `MainWindow.xaml.cs:336–347`；`MainWindow.xaml:80–91` | 主窗口引擎快速切换菜单使用 `IsEnabled = false` 充当分组标题，导致文字惨遭 0.45 透明度双重淡化不可读；标题栏按钮 Focusable=False 破坏全键盘操作。 | V2 §10.2 U07（双重淡化导致文字消失）；§15 最低检查集 5（整条旅程键盘可完成）。 | 菜单分组标题改用高对比度 Header 模板；标题栏按钮恢复可获得焦点或实现快捷键无障碍桥接。 |
| **WIN-16** | **P2** | `CaptureOverlayWindow.xaml:13–17, 61`；`FloatingTriggerWindow.xaml:18` | 截图覆盖层与悬浮球内联硬编码 `#66FFFFFF`、`#F2FFFFFF`、`DropShadowEffect Color="#000000"` 等画刷与阴影，脱离设计系统 Token 体系。 | 重点⑦（颜色/阴影硬编码）；V2 §10.3；DESIGN_SYSTEM.md。 | 将覆盖层专用颜色与阴影收归至 `ThemeService` 统一管理的 Token。 |
| **WIN-17** | **P2** | `TranslationPanelWindow.xaml:81, 162, 168...`；`QuickSearchWindow.xaml:43` | 存在多处脱离设计系统的非标字号（12.5、15）、非标间距（5、7）与非标圆角（`CornerRadius="4"`、`BorderThickness="1.2"`）。 | DESIGN_SYSTEM.md §2.2（排版矩阵）、§5.1（圆角系统）、§5.2（间距网格）。 | 严格按 4/8 网格收拢间距（5/7 归 6/8），字号归并到 11/12/13/14.5 阶梯，小控件圆角统一为 6。 |
| **WIN-18** | **P2** | `Themes/Controls.xaml:618–638`；`MainWindow.xaml:47–55` | 主窗口侧栏 `NavButton`（单选）完全缺失 `IsPressed` 触发器，而底部的 `NavActionButton`（设置按钮）具备按下态，侧栏操作反馈体验不一致。 | 重点②（四态完整性）；V2 §13。 | 在 `NavButton` 的 ControlTemplate 中补充 `IsPressed` 触发器，设定 `SurfacePressedBrush`。 |
| **WIN-19** | **P2** | `TranslationPanelWindow.xaml:48, 51`；`TranslationPanelWindow.xaml.cs:1136` | `PinToggle` 的 ToolTip 与无障碍名称在已固定和未固定状态下恒为静态的 `"固定浮窗"`，不会翻转为 `"取消固定"`，状态表达不明确。 | 重点③（图标语义与可点性）；V2 §13。 | 联动 `IsChecked` 动态更新 ToolTip（固定为“取消固定”，未固定为“固定浮窗”）及无障碍状态描述。 |

---

## 十、Top 5 核心修复优先级排序

基于对**主流程影响程度**、**用户直接可感知性**与**违反规格严重度**的综合评估，建议开发阶段按以下优先级推进修复：

### 1. 🥇 WIN-01 (P0) —— 极速查词无服务时底栏文字严重撞车重叠，且缺少设置修复路径
- **上榜理由**：用户在未配置引擎时的第一次查词是关键新手路径。当前 XAML 布局缺陷导致红字报错与快捷键提示在同一格中严重重合叠字，且完全没有引导前往设置的出口，用户直接卡死。
- **实施要点**：底栏 Grid 规范分列，文字开启省略号；空态/错误态弹出卡片并附带一键“前往配置服务”入口。

### 2. 🥈 WIN-02 (P0) —— 翻译中断或保留部分译文（Partial）时复制按钮被禁用变灰
- **上榜理由**：直接违反 README.md G06 与 V2 N04 规格核心裁决。长文本或弱网下生成中断时，用户急需提取已出文字，按钮却被完全锁死，严重阻碍实际使用。
- **实施要点**：在 `TranslationPanelStreamGate` 中放行非 busy 状态下的手动复制，解禁 Partial 界面中的 `ResultCopyBtn`。

### 3. 🥉 WIN-03 (P0) —— 流式翻译与 Markdown 最终渲染交接时的强烈背景闪烁与闪底
- **上榜理由**：每次翻译生成必定触发的严重视觉缺陷。流式时使用带有深灰色只读背景的 `FlatTextBox`，生成完成后瞬间变成透明底的 `RichTextBox`，产生明显的闪白/闪灰，严重破坏“流式稳定”承诺。
- **实施要点**：将 `TranslationTextBox` 与 `ResultStreamBox` 统一切换至专门为此设计的 `ResultTextBox` 样式，消除背景突变。

### 4. 🏅 WIN-04 (P0) —— 浮窗流式翻译输出高度递增触发重新定位导致整窗跳跃瞬移
- **上榜理由**：破坏核心阅读体验。流式文本行数增加引发 `SizeChanged`，重新运行 `NearAnchor` 导致浮窗从光标下方突然翻转飞到光标上方或屏幕边缘，用户视线断裂。
- **实施要点**：在面板初次定位后锁定展开方向与基准位置，流式期间禁止重新计算位置翻转。

### 5. 🎖️ WIN-05 (P1) —— 浮窗 17 处关键按钮全线不足 32 DIP 且关闭与设置按钮间距过窄
- **上榜理由**：直接违反 G07 强约束（“关键浮窗按钮至少 32 DIP，覆盖旧规则 28 DIP 例外”）。全窗 13 处按钮均为 30×30 DIP，且关闭与设置仅相隔 2 DIP，日常高频产生误点关闭。
- **实施要点**：全量升级按钮至 32×32 DIP，拉大关闭按钮间距至 8 DIP，提升容错率。
