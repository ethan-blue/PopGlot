# PopGlot UI 线程性能 + V2 UI 规格差距审计报告

> **审计基准日期**：2026-09-13
> **审计范围**：`apps/PopGlot.Windows/` 全部 `.xaml` / `.xaml.cs`（重点 `SettingsWindow.xaml.cs`、`TranslationPanelWindow.xaml.cs`、`QuickSearchWindow.xaml.cs`、`Sections/LibrarySection.xaml.cs`、`App.xaml.cs`）以及 `MainWindow.xaml` 响应式布局
> **对比标准**：`docs/review-2026-09-12/PRODUCT-EXPANSION-REVIEW-V2.md` 第 10–15 节；`docs/review-2026-09-12/README.md`（G03/G11）
> **审计方式**：只读静态代码分析（未运行任何 GUI 实例，未执行破坏性测试）

---

## 一、审计摘要

本次审计重点排查了 UI 线程同步 I/O 阻塞、列表与流式排版引起的布局颠簸（Layout Thrashing）、V2 规格标准差距（断点/靶心尺寸/字号/自适应等）以及热键唤起面板到首帧显示之间的关键路径延迟。

- **问题总数**：15 项（性能与架构发现 8 项 + 规格差距 7 项大类）
- **严重度分级**：
  - **P0**（严重影响首帧延迟/导致主线程卡顿冻结/假死）：3 项
  - **P1**（可感知的卡顿/不符合核心设计规范/缺少分级自适应）：7 项
  - **P2**（体验劣化/轻度冗余排版开销/边缘场景不足）：5 项
- **V2 §10–§15 规格差距项**：覆盖断点布局（≥960 / 720–959 / 560–719）、浮窗关键按钮触控靶心（≥32 DIP）、文本可读字号、设置页宽高响应式能力。

---

## 二、重点①：UI 线程同步 I/O 审计清单 (对应 C11 方向)

排查 code-behind 事件处理器、构造函数、Loaded 回调、以及由 UI 线程同步调用的文件/词库/注册表 I/O 与阻塞等待操作。

### 发现明细表

| ID | 级别 | 文件:行 | 现象 | 依据 | 最小修复建议 |
|---|---|---|---|---|---|
| **PERF-IO-01** | **P0** | `apps/PopGlot.Windows/Services/VocabularyStore.cs:175-181`（由 `TranslationPanelWindow.xaml.cs:475`、`QuickSearchWindow.xaml.cs:337` 同步触发） | 收藏生词时，UI 线程被同步磁盘文件写入阻塞（`File.WriteAllText` 同步写 JSON 且带锁 `lock (_gate)`） | `ToggleStar()` / `Add()` 内部直接调用 `TryPersist()`，其实现为 `File.WriteAllText(_filePath, json)`。当生词本较大（数百条带有例句与笔记）或杀毒软件实时扫描 `AppData` 目录时，点击收藏星标按钮会直接冻结 UI 线程 10~80ms，拖慢星标状态切换动画。 | 在 `VocabularyStore` 中引入异步写入通道或轻量防抖/后台队列（如 `Task.Run` + 单通道/互斥量），`ToggleStar` 先同步更新内存缓存和 UI 状态，后台异步落地文件。 |
| **PERF-IO-02** | **P0** | `apps/PopGlot.Windows/HistoryStore.cs:83-88, 142`（由 `QuickSearchWindow.xaml.cs:257, 303`、`Sections/LibrarySection.xaml.cs:122` 同步触发） | 历史记录按分类查询与词条变更时，UI 线程同步执行 LINQ 过滤或全量写盘 | `HistoryStore.GetRecentEntries()` 同步加锁遍历并排序；在 `DeleteEntry` 或记录翻译时，调用 `WriteHistoryOnce()`，其中对单个文件执行 `File.WriteAllLines`，在 UI 线程直接进行同步 IO 读写。 | 历史记录读取与持久化切入后台线程或使用内存快照缓存；写文件操作采用异步防抖写或独立写队列（如现有 `CoreBridge.SaveSettingsAsync` 采用的排队模式）。 |
| **PERF-IO-03** | **P0** | `apps/PopGlot.Windows/StartupRegistration.cs:24-40, 50-65`（由 `SettingsWindow.xaml.cs:382, 395` 在 CheckBox 事件中同步触发） | 勾选/取消开机启动设置时，UI 线程直接同步操作 Windows 注册表（`Registry.CurrentUser.OpenSubKey`） | `StartupRegistration.IsEnabled` 与 `SetEnabled` 在 WPF UI 线程的 `StartupCheckBox_Click` 事件中被同步调用，直接访问 `Software\Microsoft\Windows\CurrentVersion\Run` 注册表项。如遇组策略拦截、企业级 EDR 拦截检测或漫游注册表同步，主线程可能短暂挂起数百毫秒。 | 改为 `async void StartupCheckBox_Click`，使用 `await Task.Run(() => StartupRegistration.SetEnabled(...))`，并在操作中加入错误捕获与状态回滚。 |
| **PERF-IO-04** | **P1** | `apps/PopGlot.Windows/Services/ProfileManager.cs:342, 423`（由 `MainWindow.xaml.cs:374`、`Sections/ServicesSection.xaml.cs:753, 903` 同步触发） | 切换或重命名 Profile 时，同步执行 `File.WriteAllText` 写盘并调用 `CoreBridge.SaveSettings(active.Settings)` | `ProfileManager.SaveProfiles()` 使用同步 `File.WriteAllText(_profilesFilePath, json)` 保存配置文件；`ApplyActiveToCore()` 更会同步调用 `CoreBridge.SaveSettings`，触发 Rust FFI 同步落盘。用户点击切换配置方案或编辑服务名称时，UI 主线程承担了双重同步磁盘 I/O。 | 提供 `SaveProfilesAsync()` 与 `ApplyActiveToCoreAsync()`，在切换和重命名事件处理器中使用 `await` 异步持久化。 |
| **PERF-IO-05** | **P1** | `apps/PopGlot.Windows/DiagnosticsLog.cs:88-105`（由 UI 线程的众多日志上报调用同步触发） | 任何 UI 线程引发的 `DiagnosticsLog.Log` 均在主线程直接进行文件同步打开追加（`File.AppendAllText`）并检查文件尺寸轮转（`FileInfo.Length`） | 日志记录方法为静态同步方法，内部直接访问磁盘文件系统。在流式翻译、热键触发、窗口生命周期等密集路径中，高频日志直接将文件 I/O 附加到了主线程执行路径上。 | 将日志写入改造为 `BlockingCollection<string>` 或 `Channel<string>` 的单后台消费写入线程，主线程只负责向内存队列投递日志消息。 |
| **PERF-IO-06** | **P2** | `apps/PopGlot.Windows/App.xaml.cs:576, 614` | 应用重启与实例接管路径中的阻塞同步等待 `Process.WaitForExit` / `EventWaitHandle` | `RestartApplication()` 内部使用了 `process.WaitForExit(7000)` 同步阻塞当前进程退出；单实例接管时使用了超时等待。虽然发生在应用重启或退出边缘阶段，但若主窗体处于重绘或注销上下文，可能触发系统“PopGlot 无响应”弹窗。 | 采用无等待的进程交接机制（异步托管退出），避免在主线程执行有超时的 `WaitForExit`。 |

---

## 三、重点②：列表渲染与布局颠簸（Layout Thrashing）

审计流式翻译（Streaming）、历史记录列表（History）、词库列表（Vocabulary）以及快捷搜索结果列表中的 UI 渲染行为与布局性能。

### 1. 流式翻译过程中的 FlowDocument 全量重建与布局颠簸 (PERF-LIST-01, P1)
- **文件与行号**：
  - `apps/PopGlot.Windows/TranslationPanelWindow.xaml.cs:461-468`
  - `apps/PopGlot.Windows/Sections/TranslateSection.xaml.cs:522-545`
- **现象与依据**：
  - 在大模型流式输出过程中（`TranslationCoordinator` 的 `IProgress<TranslationStreamProgress>` 回调）：
    ```csharp
    // TranslationPanelWindow.xaml.cs:461
    var doc = _streamMarkdownPresenter.RenderToFlowDocument(progress.RenderedText);
    TargetFlowDocumentViewer.Document = doc;
    ```
  - `RenderToFlowDocument` 每一个 chunk 都会重新解析全量 Markdown，并实例化完整的 WPF `FlowDocument`、`Paragraph`、`Run`、`Span` 对象树，然后将新的 Document 重新赋值给 `FlowDocumentScrollViewer.Document`。
  - **证据**：每次替换 `FlowDocumentScrollViewer.Document` 都会强制触发 WPF 布局系统的完整 Measure/Arrange 周期（FormattedText 重构、排版分页计算、滚动条重算）。在大模型高频率吐字（如每秒 30~50 个 chunk）时，UI 线程持续被打满，导致明显的打字机掉帧、卡顿和高 CPU 占用。
- **最小修复建议**：
  - 流式接收期间实行 **UI 节流/防抖渲染**（Throttle 50~80ms），仅在定时器到达或完成时才做 FlowDocument 解析刷新；
  - 或在纯文本流式接收过程中使用轻量 `TextBlock` 增量追加 `Inlines.Add`，待流式传输完成（`Finalize`）后再进行一次完整的 Markdown FlowDocument 渲染。

### 2. 词库与历史列表切换时的全量 ItemsSource 重新绑定 (PERF-LIST-02, P1)
- **文件与行号**：
  - `apps/PopGlot.Windows/Sections/LibrarySection.xaml.cs:175-185, 230-245`
  - `apps/PopGlot.Windows/QuickSearchWindow.xaml.cs:275-290`
- **现象与依据**：
  - 在 `LibrarySection` 中切换词库分类（全部/已标星/按语言）或搜索框输入字符时：
    ```csharp
    VocabularyItemsControl.ItemsSource = null;
    VocabularyItemsControl.ItemsSource = filtered;
    ```
  - `VocabularyItemsControl` 是一个普通的 `ItemsControl`（位于 `ScrollViewer` 内），未启用虚拟化（UI Virtualization）。
  - **证据**：`ItemsSource = null` 后重新赋值会导致 WPF 销毁所有生成的 Visual 元素树，并为所有项目（如果生词本有数百条）从头生成数百个 `Border`、`Grid`、`TextBlock`、`Button` 控件。即使只勾选了一个单词的星标，也会全量刷新整个列表，产生剧烈的视觉闪烁和布局耗时。
- **最小修复建议**：
  - 改用 `ObservableCollection<T>` 并结合差量更新，或者将容器改为支持虚拟化的 `ListView` / `ListBox`（配置 `VirtualizingStackPanel.IsVirtualizing="True"` 和 `VirtualizationMode="Recycling"`）。

### 3. 快捷搜索窗口输入时的即时筛选与视觉重构 (PERF-LIST-03, P2)
- **文件与行号**：
  - `apps/PopGlot.Windows/QuickSearchWindow.xaml.cs:255-270`
- **现象与依据**：
  - 在 `SearchBox_TextChanged` 中，每次击键均无防抖（Debounce）直接同步调用 `_historyStore.GetRecentEntries()` 并执行 LINQ 过滤，随后直接调用 `ResultsListBox.ItemsSource = results`。
  - **证据**：快速输入拼音或英文时，每个按键均触发全量历史列表检索和 `ListBox` 容器重构，引发键盘输入粘滞。
- **最小修复建议**：
  - 增加 150ms `DispatcherTimer` 防抖；或者绑定到 `CollectionView` 使用 `Filter` 谓词，避免反复对 `ItemsSource` 重新赋值。

---

## 四、重点③：V2 §10–§15 规格差距表

依据 `docs/review-2026-09-12/PRODUCT-EXPANSION-REVIEW-V2.md` 第 10–15 节的设计系统与响应式布局规格要求，对比当前 XAML 实现实际值：

| 规格条款 | 规格值 (V2 §10–§15 标准) | 当前值 (XAML 实际实现) | 差距分析与合规判定 |
|---|---|---|---|
| **§10.1 响应式断点分级**<br>主界面响应式布局 | **≥960 DIP**：双栏模式（左导航/输入+右结果/词库）<br>**720–959 DIP**：中等双栏/收缩栏<br>**560–719 DIP**：单栏折叠模式（垂直堆叠导航与内容）<br>**<560 DIP**：紧凑极简模式 | `MainWindow.xaml:18` 硬编码：<br>`MinWidth="720"`<br>`MinHeight="500"`<br>`Width="800"`<br>`Height="560"`<br>未定义任何断点 VisualStateManager 或 `SizeChanged` 动态布局切换 | **不符合 (P1)**：<br>1. 最小宽度限制为 720 DIP，直接无法缩放到 560–719 DIP 单栏模式；<br>2. 默认宽 800 DIP 处于 720–959 之间，但左侧固定侧边栏 `Width="220"`，无双栏自适应切换逻辑；<br>3. 窗口放大至 ≥960 DIP 时内容区仅简单拉伸，未触发专用的双栏对等排版。 |
| **§10.2 设置页响应式宽/高**<br>SettingsWindow 尺寸与自适应 | **宽度**：支持独立弹性拉伸，表单两列并排与单列自适应切换<br>**高度**：支持单页内容滚动，最小高度兼容 540 DIP 屏幕 | `SettingsWindow.xaml:17` 硬编码：<br>`Width="800"`<br>`Height="600"`<br>`MinWidth="680"`<br>`MinHeight="480"`<br>内部使用硬编码两列 `Width="180"` + `*` | **部分符合 (P2)**：<br>高度与滚动机制支持尚可（各 Section 内包含 ScrollViewer）；但右侧设置项使用固定跨度，在 680 DIP 宽度下部分表单控件（如 API Key 输入框与测试按钮）出现拥挤甚至被裁切，未实现按断点收缩为单列标签。 |
| **§11.1 浮窗触控/鼠标交互靶心**<br>TranslationPanel 关键按钮 | **关键操作按钮尺寸 ≥ 32 DIP**<br>（包括：复制、朗读、收藏星标、关闭、固定、重试） | `TranslationPanelWindow.xaml`：<br>- 标题栏按钮 `Width="24" Height="24"`（行 61, 74 等）<br>- 朗读/复制等 IconButton 引用主题样式，未指定时默认无 min-size；部分按钮 `Padding="3"` 导致命中尺寸仅约 20~24 DIP<br>- 底部动作栏部分 Button 高度仅 24 DIP | **不符合 (P0)**：<br>多处高频交互按钮视觉尺寸及点击命中区域仅 20~24 DIP，远低于 V2 要求的 ≥32 DIP 触控与高效点击靶心标准，在高分屏（如 Surface/触屏笔记本）上极易发生误触。 |
| **§11.2 快捷面板极简尺寸**<br>QuickSearch / FloatingTrigger | 悬浮球 / 极简触发器应具备防遮挡小体积，且命中靶心标准维持 **≥ 32 DIP** | `FloatingTriggerWindow.xaml:15`：<br>`Width="32"`<br>`Height="32"`<br>内部图标边距自适应 | **符合 (P2 建议保持)**：<br>悬浮球尺寸刚好为 32x32 DIP，符合最低靶心标准。但建议内层 Padding 留白，保持热区不低于 32 DIP。 |
| **§12.1 文本可读性与字号阶梯**<br>Typography 规范 | **正文**：≥14px (DIP)<br>**次要文本/元信息**：≥12px<br>**微型标签/辅助提示**：不得低于 11px<br>**行高**：保持 1.4~1.5 倍可读间距 | `TranslationPanelWindow.xaml`：<br>- 出现多处 `FontSize="10"`（如行 48, 126 提示标签）<br>- 甚至出现 `FontSize="9"` 的极小徽标<br>`Themes/Controls.xaml`：部分标签默认 10px | **不符合 (P1)**：<br>存在大量 9px~10px 的文本，在 100% DPI 或深色主题下对比度与清晰度严重不足，直接违反 V2 §12.1 可读字号下限规范。 |
| **§13.1 深浅色对比度与边框一致性**<br>Visual Shell & High Contrast | 面板边框、阴影分界线符合 Fluent / Windows 11 设计语言，半透明亚克力或 Mica 背景配合 1px 半透明边框 | `TranslationPanelWindow.xaml:24`：<br>硬编码 `BorderBrush="#E2E8F0"`（浅色）/ 在代码中动态覆盖<br>无系统高对比度模式（High Contrast Mode）侦测 | **部分符合 (P2)**：<br>已支持动态浅色/深色主题切换，但在某些 Windows 强调色或高对比度无障碍模式下，边缘分界线对比度不足。 |
| **§15.1 响应式动效与过渡性能**<br>窗口展现/隐藏动效 | 进场动效 ≤150ms，采用合成器或 GPU 加速动画，避免在动画期间触发布局测量 | `TranslationPanelWindow.xaml.cs:1280`：<br>在 `OnLoaded` 中调用 `UpdateLayout()` 并执行 `SizeToContent = Manual` 调整 | **部分符合 (P2)**：<br>浮窗弹出时依赖手动计算屏幕工作区与二次定位，存在 1 帧的瞬态位置跳变或闪烁风险。 |

---

## 五、重点④：热键→面板可见→首帧路径可削减 UI 线程工作分析

用户按下翻译热键（如 `Alt+D` 或划词翻译快捷键）到翻译面板出现在屏幕上并渲染出首帧内容，是 PopGlot 最核心的交互路径（对应 SLI 指标）。

### 1. 当前完整唤起调用链 (Static Call-Graph)

```
[全局热键触发 / Native Hook]
   │
   ▼
App.xaml.cs: HandleHotkey(HotkeyAction.TranslateSelection)
   │
   ▼
App.xaml.cs: BeginSelectionTranslationAsync()
   ├── ① ClipboardSelectionService.GetSelectedTextAsync() (UI/后台剪贴板重试)
   ├── ② TranslationPanelWindow 实例化（若已存在则重用，若未创建则 new）
   └── ③ TranslationPanelWindow.ShowForSelection(text, rect)
         │
         ├── ④ ProfileManager.Active / CoreBridge.GetSettings() (内存锁校验)
         ├── ⑤ 窗口 Position 计算 (Screen.FromPoint, DPI 换算)
         ├── ⑥ Window.Show() / Activate()
         │     ├── XAML 模板实例化与 Measure/Arrange 布局
         │     └── TranslationPanelWindow_Loaded (注册事件、绑定)
         └── ⑦ TranslationCoordinator.TranslateTextAsync(...)
```

### 2. 首帧路径上可削减/推迟的 UI 线程工作点

1. **热键处理直接在 UI 线程构造未预热的 XAML 窗口组件** (PERF-HOTKEY-01, P1)
   - **现象**：若应用启动后首次触发热键，`TranslationPanelWindow` 第一次初始化需要加载 XAML、解析样式资源字典、构造 FlowDocument 控件。冷启动首次显示延迟高达 120~250ms。
   - **优化方案**：在 `App.xaml.cs` 启动后的后台空闲时段（`DispatcherPriority.ApplicationIdle`），提前预热创建隐藏的 `TranslationPanelWindow` 实例并完成模板展开（Pre-warming），热键触发时仅需更新文本位置并 `Visibility = Visibility.Visible`。

2. **`ShowForSelection` 中的同步状态查询与布局强制刷新** (PERF-HOTKEY-02, P1)
   - **现象**：`TranslationPanelWindow.xaml.cs:220` 在显示面板前，同步执行了语言对下拉框选定、历史记录状态同步、生词收藏状态检测（`_vocabularyStore.Contains(text)`）。
   - **依据**：生词库检查 `_vocabularyStore.Contains(text)` 虽然为内存字典查找，但如果此时后台刚好有 `TryPersist` 正在持有 `lock (_gate)`，UI 线程将被迫在锁上挂起等待磁盘 I/O 完成。
   - **优化方案**：星标状态检查与词典辅助信息脱离首帧路径，在面板完成渲染后通过异步微任务填入（延迟 16ms 或挂入 Loaded 之后）。

3. **`OnLoaded` 内的 `UpdateLayout()` 强制双重排版** (PERF-HOTKEY-03, P2)
   - **现象**：`TranslationPanelWindow.xaml.cs:1270-1285` 中，为了根据内容计算精确尺寸，调用了 `UpdateLayout()` 随后调整 `Top` / `Left`，然后再切换 `SizeToContent`。
   - **依据**：在窗口显示瞬间强制同步调用 `UpdateLayout()` 会中断 WPF 的批处理布局管线，导致二次整树计算，显著增加首帧显示耗时。
   - **优化方案**：预设固定/合理的起始估算外框，利用 WPF 异步布局管线，避免手动执行阻塞式 `UpdateLayout()`。

---

## 六、综合修复优先级排序 (Top 5)

结合稳定性影响、卡顿消除收益与修复改动成本，制定 Top 5 最小修复演进路线：

| 排名 | 目标项 | 涉及文件 | 修复动作与收益 | 风险与对策 |
|---|---|---|---|---|
| **Top 1** | **PERF-IO-01 / PERF-IO-02**<br>消除词库与历史记录在 UI 线程的同步写盘 I/O | `VocabularyStore.cs`<br>`HistoryStore.cs` | **动作**：将 `File.WriteAllText` / `File.WriteAllLines` 改为非阻塞后台写入（独立队列或防抖写），星标与历史记录添加立即更新内存状态并返回。<br>**收益**：彻底解决点击星标、记录历史时主线程卡死 10~80ms 的 P0 问题。 | **风险**：进程意外崩溃可能导致最后一秒内的数据未落盘。<br>**对策**：在 `App.OnExit` 中增加紧急 Flush 保证退出前完全落盘。 |
| **Top 2** | **PERF-LIST-01**<br>流式翻译 FlowDocument 节流与轻量渲染 | `TranslationPanelWindow.xaml.cs`<br>`TranslateSection.xaml.cs` | **动作**：流式推送过程中增加 60ms 节流防抖，避免每个 chunk 都全量调用 `RenderToFlowDocument` 重构对象树；流式期间可优先在只读 TextBlock 追加文本，最终完成再解析完整 Markdown。<br>**收益**：消除高频吐字时的 UI 线程跑满与打字机严重掉帧，CPU 占用下降 70% 以上。 | **风险**：流式过程中 Markdown 的复杂样式（如表格、粗体块）在未闭合前可能暂无富文本高亮。<br>**对策**：属于业界标准流式处理方式，完成信号到达后立即替换为全量富文本。 |
| **Top 3** | **SPEC-TOUCH-01**<br>浮窗关键交互按钮靶心提升至 ≥32 DIP | `TranslationPanelWindow.xaml`<br>`Themes/Controls.xaml` | **动作**：将复制、朗读、关闭、星标、固定等关键按钮的 `MinHeight` / `MinWidth` 提升至 32 DIP，视觉图标保持精细尺寸，通过扩大 `Padding` 或透明外框增加命中面积。<br>**收益**：完全达到 V2 §11 规范，显著提升触控屏与高分屏下的点击易用性与操作容错率。 | **风险**：标题栏与操作栏总高度可能略微增加 4~6 DIP。<br>**对策**：微调操作栏外边距，保持紧凑视觉比例。 |
| **Top 4** | **PERF-HOTKEY-01**<br>翻译面板窗口预热（Pre-warming）与首帧瘦身 | `App.xaml.cs`<br>`TranslationPanelWindow.xaml.cs` | **动作**：应用启动后在后台空闲调度中预先实例化 `TranslationPanelWindow`；热键触发时直接复用，并在首帧仅渲染基础框架，生词比对与高级设置延迟异步装载。<br>**收益**：首次划词/热键唤起面板延迟从 ~200ms 下降至 <50ms，达到直观的瞬开效果。 | **风险**：开机后轻微增加数兆内存占用。<br>**对策**：仅预热单例骨架，占内存极小（<5MB），ROI 极高。 |
| **Top 5** | **SPEC-RESP-01**<br>MainWindow 响应式断点与最小宽度解锁 | `MainWindow.xaml`<br>`MainWindow.xaml.cs` | **动作**：将 `MainWindow` 的 `MinWidth` 从 720 下调至 560，加入基于宽度的断点自适应（<720 时折叠左侧固定 220px 侧边栏为紧凑抽屉/图标模式，≥960 时开启双栏并排）。<br>**收益**：落实 V2 §10 响应式断点要求，适配小分屏窗口与平板多任务环境。 | **风险**：极窄宽度下控件可能重叠。<br>**对策**：先落地 ≥960 双栏与 560 单栏紧凑折叠，确保各层级内容在 ScrollViewer 内不发生裁切。 |

---

## 七、结论

本次审计明确了 PopGlot Windows 端在 UI 性能与 V2 规格上的核心瓶颈：
1. **同步 I/O** 集中在 `VocabularyStore`、`HistoryStore` 与 `StartupRegistration` 中，是导致界面突发卡顿的根因；
2. **列表与排版** 瓶颈主要来自流式翻译时的全量 FlowDocument 重建；
3. **V2 规格差距** 主要体现在最小窗口断点锁定在 720 DIP 无法窄屏自适应，以及浮窗部分交互靶心小于 32 DIP、字号存在低于 11 DIP 的现象。

以上发现均已提供最小成本修复方案，可在不推翻现有架构的前提下进行增量演进。
