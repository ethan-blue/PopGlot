# PopGlot 2026-09-13 UI 专项审计与修复全景总览（索引与进度台账）

> **编制日期**：2026-09-13
> **编制人**：OpenCode (Teammate / W10)
> **目标**：为产品负责人、评审委员会及后续接力开发者提供 2026-09-13 UI/性能专项审计报告索引、全量发现 ID 映射矩阵、修复波次履约台账以及遗留项清单。

---

## 目录
1. [四份专项审计报告索引与统计](#一四份专项审计报告索引与统计)
2. [发现 ID → 修复波次 → 当前状态全景映射表](#二发现-id--修复波次--当前状态全景映射表)
3. [修复波次执行台账（W1 ~ W11）](#三修复波次执行台账w1--w11)
4. [核心技术突破与架构收益](#四核心技术突破与架构收益)
5. [待接力遗留项清单（Backlog）](#五待接力遗留项清单backlog)

---

## 一、四份专项审计报告索引与统计

本次 UI 专项审计覆盖 PopGlot 全部 15 个 XAML 视图、全部后台 Code-Behind、主题资源字典、多显示器/高分屏 DPI 适配以及 UI 主线程异步化改造。

| 审计专项 | 报告路径 | 重点范围 | 发现总数 | P0 (严重) | P1 (重要) | P2 (细节) | 核心发现摘要 |
|---|---|---|:---:|:---:|:---:|:---:|---|
| **UI 审计 A** | `docs/ui-audit-2026-09-13/settings-services.md` | 设置窗体 + 服务/翻译/通用分区 | **20** | 5 | 8 | 7 | 推荐证据假按钮误触、无凭据出网 401 报错、无服务空态假就绪、首个服务保存校验漏洞、按钮未达 32 DIP |
| **UI 审计 B** | `docs/ui-audit-2026-09-13/windows.md` | 翻译面板 / 极速查词 / 悬浮球 / 截图覆盖 / 主窗 | **19** | 4 | 9 | 6 | 极速查词底栏文字撞车、Partial 译文手动复制被阻断、流式换富文本闪白、流式阅读浮窗跳跃翻转、按钮点击热区不达标 |
| **UI 审计 C** | `docs/ui-audit-2026-09-13/theme-resources.md` | 资料库/数据/快捷键/隐私分区 + 主题资源字典 | **19** | 4 | 7 | 8 | 开关初始状态居左脱靶（U08 根因）、FlowDocument 切主题白底白字、生词本损坏伪装成空列表、12 处控件 Disabled 双重淡化 |
| **UI 审计 D** | `docs/ui-audit-2026-09-13/perf-spec-gap.md` | UI 线程性能 + V2 UI 规格差距清单 | **15** | 3 | 7 | 5 | 收藏生词与写历史同步阻塞 UI 线程（10~80ms）、开关自启同步读写注册表、流式全量重构布局颠簸、V2 断点与尺寸规格落差 |
| **合计** | — | — | **73 项** | **16 项** | **31 项** | **26 项** | — |

---

## 二、发现 ID → 修复波次 → 当前状态全景映射表

> **状态标识说明**：
> - `已修复+lead复核`：已完成代码实现、编译验证、针对性逻辑断言并通过主评审独立核验。
> - `进行中`：当前已派发给执行模型，正在实施或联调。
> - `未排期`：记录在案，纳入后续接力实施 Backlog。

### 1. 审计 A（设置与服务分区）：`SS-01` ~ `SS-20`

| 发现 ID | 级别 | 缺陷内容摘要 | 修复波次 | 当前状态 | 实施归宿文件 |
|---|:---:|---|:---:|:---:|---|
| **SS-01** | P0 | 模型推荐证据徽章（`EvidenceBadge`）假按钮点击误导 | **W2** | **已修复+lead复核** | `Sections/ServicesSection.xaml(.cs)` |
| **SS-02** | P0 | 无 API Key 且非本地服务时「获取模型」违规出网报错 | **W2** | **已修复+lead复核** | `Sections/ServicesSection.xaml(.cs)` |
| **SS-03** | P0 | 「验证连接」违规采用实色主按钮 + 未填 Key 时无前置拦截 | **W2** | **已修复+lead复核** | `Sections/ServicesSection.xaml(.cs)` |
| **SS-04** | P0 | 无可用服务时工作台空态假就绪，诱导翻译报错且无修复路径 | **W2** | **已修复+lead复核** | `Sections/TranslateSection.xaml(.cs)` |
| **SS-05** | P0 | 首个服务未配置模型时直接保存并激活导致默认路由崩溃 | **W2** | **已修复+lead复核** | `Sections/ServicesSection.xaml.cs` |
| **SS-06** | P1 | 已配置引擎列表行缺少 `Cursor="Hand"` 点击暗示 | **W2** | **已修复+lead复核** | `Sections/ServicesSection.xaml` |
| **SS-07** | P1 | 无 API Key 时「清除」按钮保持可用且误报已清除 | **W5** | **已修复+lead复核** | `Sections/ServicesSection.xaml(.cs)` |
| **SS-08** | P1 | 「收藏到生词本」成功后无持久点亮反馈（空心五角星不变） | **W5** | **已修复+lead复核** | `Sections/TranslateSection.xaml(.cs)` |
| **SS-09** | P1 | 翻译工作台 7 个图标按钮硬编码 28×28 DIP，低于 32 DIP 规格 | **W2** | **已修复+lead复核** | `Sections/TranslateSection.xaml` |
| **SS-10** | P1 | 语音朗读播放期间无状态反馈、无停止操作切换 | **W5** | **已修复+lead复核** | `Sections/TranslateSection.xaml(.cs)` |
| **SS-11** | P1 | 开机启动项被禁用时「重新启用」按钮样式退化为裸系统按钮 | **W2** | **已修复+lead复核** | `Sections/GeneralSection.xaml` |
| **SS-12** | P1 | 服务编辑器打开时与窗体底栏同屏出现两套保存条与冲突提示 | **W5** | **已修复+lead复核** | `SettingsWindow.xaml(.cs)` |
| **SS-13** | P1 | 触发草稿守卫条时底部按钮栏未隐藏，上下堆叠两套取消/保存 | **W5** | **已修复+lead复核** | `Sections/ServicesSection.xaml` |
| **SS-14** | P1 | 设置窗体硬编码 820×600，高 DPI 缩放下保存条超出可视区 | **W2** | **已修复+lead复核** | `SettingsWindow.xaml` |
| **SS-15** | P2 | 设置窗口与翻译工作台标题硬编码 FontSize="15" 压制 PageTitle | **W5** | **已修复+lead复核** | `SettingsWindow.xaml`, `TranslateSection.xaml` |
| **SS-16** | P2 | 服务分区字号散落（12/12.5/14 等）脱离设计系统标尺 | **W5** | **已修复+lead复核** | `Sections/ServicesSection.xaml` |
| **SS-17** | P2 | 服务分区边距散落（13,11 / 5,2 等）脱离 4px/8px 网格 | **W5** | **已修复+lead复核** | `Sections/ServicesSection.xaml` |
| **SS-18** | P2 | 工作台外层卡片与流式胶囊硬编码 CornerRadius="4" | **W5** | **已修复+lead复核** | `Sections/TranslateSection.xaml` |
| **SS-19** | P2 | 无已配置服务时「默认路由」面板常驻且显示空下拉 | **W5** | **已修复+lead复核** | `Sections/ServicesSection.xaml` |
| **SS-20** | P2 | 界面中「翻译引擎」与「模型服务」专有名词混杂打架 | **W5** | **已修复+lead复核** | 全局 XAML 用户文案 |

---

### 2. 审计 B（窗口与浮层）：`WIN-01` ~ `WIN-19`

| 发现 ID | 级别 | 缺陷内容摘要 | 修复波次 | 当前状态 | 实施归宿文件 |
|---|:---:|---|:---:|:---:|---|
| **WIN-01** | P0 | 极速查词无服务时底栏文字撞车重叠且无设置引导入口 | **W3** | **已修复+lead复核** | `QuickSearchWindow.xaml(.cs)` |
| **WIN-02** | P0 | 浮窗翻译失败/取消但有内容时复制按钮被禁用（阻断 Partial 复制） | **W3** | **已修复+lead复核** | `Services/TranslationPanelStreamGate.cs` |
| **WIN-03** | P0 | 流式输出 TextBox 误用 FlatTextBox，切换富文本时剧烈闪灰/闪白 | **W3** | **已修复+lead复核** | `TranslationPanelWindow.xaml`, `QuickSearchWindow.xaml` |
| **WIN-04** | P0 | 浮窗流式输出导致高度变化时重新翻转计算，造成窗口跳跃翻转 | **W3** | **已修复+lead复核** | `TranslationPanelWindow.xaml.cs` |
| **WIN-05** | P1 | 浮窗 17 处按钮尺寸全为 30×30 DIP（未达 32），关闭按钮间距仅 2 DIP | **W3** | **已修复+lead复核** | `TranslationPanelWindow.xaml`, `QuickSearchWindow.xaml` |
| **WIN-06** | P1 | 浮窗所有 IconButton 内部 Path 固定 Fill 导致悬停/点亮失效 | **W3** | **已修复+lead复核** | `TranslationPanelWindow.xaml(.cs)` |
| **WIN-07** | P1 | 浮窗 PinToggle 与 StarToggle 缺失按下态/禁用态，焦点环与选中混淆 | **W7** | **已修复+lead复核** | `TranslationPanelWindow.xaml` |
| **WIN-08** | P1 | 翻译失败全屏通红刺眼，且说明区未提供前往设置修复路径 | **W7** | **已修复+lead复核** | `TranslationPanelWindow.xaml.cs` |
| **WIN-09** | P1 | 截图覆盖层在选区四角绘制不可拖拽的假手柄误导用户 | **W3** | **已修复+lead复核** | `CaptureOverlayWindow.xaml(.cs)` |
| **WIN-10** | P1 | 极速查词在多屏下固定居中主屏且高缩放下易溢出屏幕底部 | **W7** | **已修复+lead复核** | `QuickSearchWindow.xaml(.cs)` |
| **WIN-11** | P1 | 悬浮触发球物理像素与 DIP 混用造成高分屏偏移且无屏幕夹逼 | **W7** | **已修复+lead复核** | `FloatingTriggerWindow.xaml.cs` |
| **WIN-12** | P1 | 主窗口宽度收缩至 <720 DIP 时固定侧栏挤压翻译区，未折叠 | **W7** | **已修复+lead复核** | `MainWindow.xaml(.cs)` |
| **WIN-13** | P1 | 窗口定位器 Gap/Edge 物理像素硬编码在 200% DPI 屏间距减半 | **W7** | **已修复+lead复核** | `WindowPositioner.cs` |
| **WIN-14** | P2 | 极速查词朗读按钮无播放中状态指示，StarButton 缺少选中衬底 | **W7** | **已修复+lead复核** | `QuickSearchWindow.xaml.cs` |
| **WIN-15** | P2 | 主窗口引擎切换菜单用 `IsEnabled=false` 充当标题致双重淡化不可读 | **W4** | **已修复+lead复核** | `MainWindow.xaml.cs` |
| **WIN-16** | P2 | 截图覆盖层与悬浮球内联硬编码画刷与阴影未走 Token | **W7** | **已修复+lead复核** | `CaptureOverlayWindow.xaml`, `FloatingTriggerWindow.xaml` |
| **WIN-17** | P2 | 浮窗标题栏按钮仅 24×24 DIP，存在 9/10px 极小字号与非标间距 | **W7** | **已修复+lead复核** | `TranslationPanelWindow.xaml` |
| **WIN-18** | P2 | 主窗口侧栏 `NavButton` 缺失 `IsPressed` 触发器 | **W4** | **已修复+lead复核** | `Themes/Controls.xaml` |
| **WIN-19** | P2 | PinToggle ToolTip 恒为静态“固定浮窗”，未随选中状态切换为“取消固定” | **W12** | **已修复+lead复核** | `TranslationPanelWindow.xaml.cs` |

---

### 3. 审计 C（主题、控件字典与分区）：`TR-01` ~ `TR-19`

| 发现 ID | 级别 | 缺陷内容摘要 | 修复波次 | 当前状态 | 实施归宿文件 |
|---|:---:|---|:---:|:---:|---|
| **TR-01** | P0 | `ToggleSwitch`/`SwitchCheckBox` 初始绑定 Checked=True 圆点居左脱靶 (U08) | **W1/W4** | **已修复+lead复核** | `Themes/Controls.xaml` |
| **TR-02** | P0 | `FlowDocument` 译文生成冻结静态画刷，切主题白底白字不可读 (U06) | **W1** | **已修复+lead复核** | `Services/MarkdownPresenter.cs` |
| **TR-03** | P0 | 生词本加载失败/只读保护时主视区被伪装为“生词本还是空的” (C02/U12) | **W1** | **已修复+lead复核** | `Sections/LibrarySection.xaml(.cs)` |
| **TR-04** | P0 | 全库 12 处控件模板 Disabled 状态同时叠加 Surface.Opacity 导致双重淡化 (U07) | **W1** | **已修复+lead复核** | `Themes/Controls.xaml` |
| **TR-05** | P1 | `App.xaml` 种子资源缺少 `FocusBrush`、`DangerHover*` 等 5 个关键 Token | **W1** | **已修复+lead复核** | `App.xaml` |
| **TR-06** | P1 | `TextBox`/`PasswordBox` 的 Hover 边框仍为默认边框，悬停无视觉反馈 | **W1** | **已修复+lead复核** | `Themes/Controls.xaml` |
| **TR-07** | P1 | `ThemeService` 缺失 `SystemParameters.HighContrast` 高对比模式支持 | **W6** | **已修复+lead复核** | `ThemeService.cs` |
| **TR-08** | P1 | 从属窗口（翻译面板、查词、悬浮球）未监听 `ThemeChanged`，DWM 边框不更新 | **W3/W7** | **已修复+lead复核** | 各从属窗口 Code-Behind |
| **TR-09** | P1 | 代码后台 `(Brush)FindResource` 破坏 `DynamicResource` 动态更新链 | **W4** | **已修复+lead复核** | `MainWindow.xaml.cs`, `PrivacySection.xaml.cs`, `Helpers.cs` |
| **TR-10** | P1 | 资料库分栏为静态 Rectangle 无 GridSplitter，删除条目后失去选中 | **W6** | **已修复+lead复核** | `Sections/LibrarySection.xaml(.cs)` |
| **TR-11** | P1 | 资料库搜索框有文本但 0 匹配时空态提示仍显示“暂无历史/生词本为空” | **W1** | **已修复+lead复核** | `Sections/LibrarySection.xaml.cs` |
| **TR-12** | P2 | 全库存在大量 4/7/8/9 魔法非标圆角 | **W4** | **已修复+lead复核** | `Themes/Controls.xaml`, `LibrarySection.xaml`, `PrivacySection.xaml` |
| **TR-13** | P2 | 资料库与主窗大标题显式覆盖 FontSize="15"，破坏 PageTitle 20 规范 | **W4** | **已修复+lead复核** | `Sections/LibrarySection.xaml`, `MainWindow.xaml` |
| **TR-14** | P2 | `NavButton`、`CheckBox`、`RadioButton`、`Expander` 等缺少 `IsPressed` 下沉反馈 | **W4** | **已修复+lead复核** | `Themes/Controls.xaml` |
| **TR-15** | P2 | `HotkeyRecorder` 录制中鼠标悬停被基类 Button 模板抹除高亮 | **W4** | **已修复+lead复核** | `Themes/Controls.xaml` |
| **TR-16** | P2 | 隐私承诺卡片内子容器与父卡片背景同化完全隐形 | **W4** | **已修复+lead复核** | `Sections/PrivacySection.xaml` |
| **TR-17** | P2 | 隐私分区对结构体 CornerRadius 误用 DynamicResource | **W4** | **已修复+lead复核** | `Sections/PrivacySection.xaml` |
| **TR-18** | P2 | 全库无间距 Token 资源，各页面充斥散落数值（已定义 Spacing 标尺；当前全库零引用，属死资源待处置） | **W6** | **定义已建立（待接入）** | `Themes/Controls.xaml` |
| **TR-19** | P2 | 全库 `DropShadowEffect` 一律硬编码 `#000000` 缺少 Token | **W4/W7** | **已修复+lead复核** | `ThemeService.cs`, `Controls.xaml`, 覆盖窗口 |

---

### 4. 审计 D（UI 性能与 V2 规格差距）：`PERF-*` 与 `GAP-*`

| 发现 ID | 级别 | 缺陷内容摘要 | 修复波次 | 当前状态 | 实施归宿文件 |
|---|:---:|---|:---:|:---:|---|
| **PERF-IO-01** | P0 | 收藏生词时 UI 线程被同步文件写入阻塞（卡顿 10~80ms） | **W8** | **已修复+lead复核** | `Services/VocabularyStore.cs` |
| **PERF-IO-02** | P0 | 历史记录读写在 UI 线程直接执行加锁文件 I/O | **W8** | **已修复+lead复核** | `HistoryStore.cs` |
| **PERF-IO-03** | P0 | 勾选开机启动时 UI 线程同步操作 Windows 注册表挂起 | **W9** | **已修复+lead复核** | `SettingsWindow.xaml.cs`, `StartupRegistration.cs` |
| **PERF-IO-04** | P1 | 切换/重命名服务时同步执行 File.WriteAllText 与 FFI 存盘 | **W9** | **已修复+lead复核** | `ProfileManager.cs`, `ServicesSection.xaml.cs` |
| **PERF-IO-05** | P1 | 任何高频诊断日志上报均在 UI 线程同步执行 AppendAllText | **W9** | **已修复+lead复核** | `DiagnosticsLog.cs` |
| **PERF-IO-06** | P2 | 应用重启与实例交接路径中主线程同步阻塞等待 `WaitForExit` | **W15** | **已修复** | `App.xaml.cs` |
| **PERF-LIST-01**| P1 | 流式翻译期间 FlowDocument 全量重建引发剧烈布局颠簸（60~80ms 节流） | **W7** | **已修复+lead复核** | `TranslationPanelWindow.xaml.cs` |
| **PERF-LIST-02**| P1 | 资料库列表搜索/切换时 ItemsSource 全量重绑引起视觉闪烁 | **W10** | **已修复+lead复核** | `Sections/LibrarySection.xaml(.cs)` |
| **PERF-LIST-03**| P2 | 快捷查词输入击键即时查询缺少防抖引起键盘粘滞 | **W11** | **已修复+lead复核** | `QuickSearchWindow.xaml.cs` |
| **PERF-HOTKEY-01**| P1 | 热键按下到首帧渲染路径需要无副作用优化；构造完整隐藏窗口的预热方案会污染生命周期 | **W15/R1** | **方案退回，稳定性优先** | `App.xaml.cs` |
| **PERF-HOTKEY-02**| P1 | 选词唤起浮窗时星标检测阻塞首帧关键路径 | **W11** | **已修复+lead复核** | `TranslationPanelWindow.xaml.cs` |
| **PERF-HOTKEY-03**| P2 | 浮窗 OnLoaded 中强制 UpdateLayout() 引发布局双重测量开销 | **W11** | **已修复+lead复核** | `TranslationPanelWindow.xaml.cs` |
| **GAP-01** | P1 | 主窗口响应式断点未实现（<720 DIP 折叠侧栏；≥960 双栏模式） | **W7/W12** | **已修复+lead复核** | `MainWindow.xaml(.cs)` |
| **GAP-02** | P2 | 设置页响应式表单收缩为单列标签（在小宽度下） | **W13** | **已修复+lead复核** | `SettingsWindow.xaml(.cs)` |
| **GAP-03** | P0 | 关键浮窗交互按钮尺寸未达 ≥32 DIP 触控标准 | **W3/W7** | **已修复+lead复核** | 浮窗与查词全量 XAML |

---

## 三、修复波次执行台账（W1 ~ W18）

| 波次编号 | 核心职责与任务集合 | 状态 | 关键交付物 | 编译与测试验证 |
|:---:|---|:---:|---|---|
| **W1** | 主题层 P0 (TR-01/02/03/04) + 种子Token/输入框悬停 (TR-05/06) | **已完成** | `Controls.xaml`, `App.xaml`, `MarkdownPresenter.cs`, `LibrarySection.xaml(.cs)` | Release 编译 0 警告 0 错误；消除白底白字与双重淡化 |
| **W2** | 设置与服务 P0 假按钮/无凭据出网/空态引导 (SS-01~06, 09, 11, 14) | **已完成** | `ServicesSection.xaml(.cs)`, `TranslateSection.xaml`, `SettingsWindow.xaml` | 剥离假按钮外形，拦截无凭据网络请求，空态提供前往配置 |
| **W3** | 浮窗 P0 撞车/Partial复制/闪底/跳窗 (WIN-01~06, WIN-09, TR-08) | **已完成** | `TranslationPanelWindow`, `QuickSearchWindow`, `TranslationPanelStreamGate` | 极速查词底栏双列防挤压，Partial 显式复制放行，流式透明底色，锚点锁定 |
| **W4** | 按下态补齐/圆角字号归一/隐私卡片/TR-09动态绑定/TR-01动画复查 | **已完成** | `Controls.xaml`, `App.xaml`, `ThemeService.cs`, `MainWindow.xaml(.cs)` 等 | 补齐 IsPressed，圆角归拢 6/10，SetResourceReference 清除静态赋值 |
| **W5** | 双保存条/收藏持久点亮/朗读状态/虚假清除/空路由/术语统一 | **已完成** | `ServicesSection`, `TranslateSection`, `SettingsWindow`, 全局 XAML | 统一专有名词为“翻译引擎”，消除同屏双保存，星标持久点亮 |
| **W6** | 资料库分栏/删除邻近选中/清空计数/间距Token/高对比基础版 | **已完成** | `LibrarySection.xaml(.cs)`, `DataSection`, `ShortcutsSection`, `ThemeService` | GridSplitter 分栏，删除自动选邻项，清空显示真实条数，高对比 Token 覆盖 |
| **W7** | 浮窗状态补齐/失败页修复路径/多屏DPI/主窗响应式折叠/流式节流 | **已完成** | `TranslationPanelWindow`, `QuickSearchWindow`, `MainWindow`, `FloatingTrigger` | <720 紧凑图标侧栏，流式 60~80ms 节流，DPI 屏幕坐标换算与边界夹逼 |
| **W8** | 词库/历史后台写盘 (PERF-IO-01/02 P0) + 退出 Flush + 测试缝隙 | **已完成** | `VocabularyStore.cs`, `HistoryStore.cs`, `App.xaml.cs`, 测试套件 | 消除 UI 线程文件 I/O，采用单写者后台队列 + Flush 缝隙 |
| **W9** | 注册表/Profile保存异步化 + 诊断日志后台写 (PERF-IO-03/04/05) | **已完成** | `SettingsWindow`, `StartupRegistration`, `ProfileManager`, `DiagnosticsLog` | 注册表与配置保存切换 Task.Run，日志写入切后台 Channel 消费线程 |
| **W10** | 资料库列表虚拟化/差量更新 (PERF-LIST-02) + 审计索引文档编制 | **已完成** | `LibrarySection.xaml(.cs)`, `docs/ui-audit-2026-09-13/README.md` | ListBox 启用 UI 虚拟化与容器复用，ICollectionView 内存过滤消除重绑闪烁 |
| **W11** | 首帧路径瘦身/查词防抖/结果差量更新/禁用态单层淡化 | **已完成** | `TranslationPanelWindow`, `QuickSearchWindow`, `QuickSearchController` | 查词 150ms 防抖，首帧星标异步填入，削减 OnLoaded 双重测量 |
| **W12** | ≥960双栏断点补全 + PinToggle动态提示 (WIN-19) | **已完成** | `TranslationPanelWindow`, `MainWindow`, `TranslateSection` | ≥960 宽屏 1:1.25 双栏，PinToggle 动态状态与无障碍提示 |
| **W13** | 设置窗窄窗单列响应式 (GAP-02) + 间距Token渐进采用 | **已完成** | `SettingsWindow.xaml(.cs)`, `GeneralSection`, `ServicesSection` | 窄窗 <700 DIP 触发 SetCompact 单列收缩，680 DIP 最小窗口通过验证 |
| **W14** | 主窗引擎切换异步化（W9遗留）+ 窗口文件术语统一核查 | **已完成** | `MainWindow.xaml(.cs)` 及各窗口文件 | 切换引擎异步化并加防重入锁，窗口层术语统一为“翻译引擎” |
| **W15** | 重启交接异步化/日志退出 Flush/索引纠错；隐藏窗口预热经真实故障复核后撤回 | **已完成（预热方案撤回，见 PERF-HOTKEY-01）** | `App.xaml.cs`, `docs/ui-audit-2026-09-13/README.md` | RestartApplication 与退出落盘均不阻塞 UI；删除有生命周期副作用的 ApplicationIdle 完整窗口构造，首帧优化需另做无副作用方案 |
| **W16** | 紧急返修：W8写盘失败可见性/并发丢失/收藏假成功 (C02) | **已完成** | `VocabularyStore.cs`, `HistoryStore.cs`, `TranslationPanelWindow.xaml.cs` | 修复写失败吞异常与提前点亮星标缺陷，LogicTests 全量通过 |
| **W17** | 紧急返修：面板复制/自动复制/强关/查词签名四项回归 | **已完成** | `TranslationPanelWindow`, `QuickSearchWindow`, `TranslationPanelStreamGate` | 恢复显式复制畅通与正确的剪贴板测试覆盖，LogicTests 180/0 全绿 |
| **W18** | 紧急返修：免费引擎单发合同验证/V02-V05接线恢复/日志与线程加固 | **已完成** | `OutboundPolicy`, `SettingsWindow`, `ThemeService`, `DiagnosticsLog` | 硬件级原子单发验证、V02 静态接线断言恢复、ThemeService 跨线程调度加固 |

---

## 四、核心技术突破与架构收益

1. **彻底消除“白底白字”与主题切换割裂（TR-02, TR-08, TR-09）**：
   - 抛弃后台代码使用 `(Brush)FindResource(...)` 赋予本地值的反模式，全面推行 `SetResourceReference(...)`。
   - `MarkdownPresenter` 生成的 `FlowDocument` 全面接入动态资源绑定，主题往返切换时文字、代码块、内联徽章瞬间跟随调色盘刷新，零残存旧暗色画刷。
2. **彻底解决开关状态居左脱靶（TR-01 / U08 根因）**：
   - 定位出 WPF `Trigger.EnterActions` 在初始绑定 `IsChecked=True` 时不触发的底层行为特性。
   - 通过在 `IsChecked=True` 触发器内部显式注入 `Knob.Margin="23,0,0,0"` 静态 Setter，保证无论是否播放动画，初始加载时圆点必然精准定位在右侧开启位，彻底终结了“逻辑开启、视觉关闭”的致命歧义。
3. **消除全库禁用态双重淡化（TR-04 / U07）**：
   - 拔除全部控件模板在 `IsEnabled="False"` 时叠加的 `Surface.Opacity=0.45`，使禁用文本对比度从 1.4:1 的不可辨识状态大幅回升至合规水平。
4. **资料库高性能虚拟化与平滑过滤（PERF-LIST-02 / W10）**：
   - `LibraryListBox` 开启 `VirtualizingStackPanel.IsVirtualizing="True"` 与 `VirtualizationMode="Recycling"`，上千条历史记录在滚动时仅复用视口内的十几个容器。
   - 废除 `ItemsSource = null; ItemsSource = newRows;` 的粗暴重绑模式，引入 `ICollectionView.Filter` 谓词就地刷新，击键搜索时 Visual 树零销毁、零重建、零闪烁。
5. **从死胡同到自愈型 UI（SS-04, WIN-01, WIN-08, TR-03）**：
   - 贯彻“错误有修复路径”原则。无可用服务时工作台与极速查词不再陷入报错死胡同，就地展示错误卡片并提供“打开设置配置引擎”主入口；生词本损坏只读时醒目提示原因并内嵌“重试加载”主按钮。

---

## 五、待接力遗留项清单（Backlog）

以下为本轮 18 波次修复后建议纳入长期工程迭代的遗留规划项：

1. **SettingsWindow 响应式单列收缩（GAP-02）**：
   - 当前状态：已由 W13 波次落实（客户区 <700 DIP 触发 SetCompact 单列收缩，680 DIP 最小窗口通过验证）。
   - 历史备注：原标“未排期/后续规划 <560 DIP”现已归档闭环。
2. **间距 Token 全库业务页面全量替换**：
   - 当前状态：TR-18 在 `Controls.xaml` 中建立了 `Spacing4` ~ `Spacing24` 标尺定义，但经对账 G 核实当前全库零引用，属于死资源；原台账“在模板内部就近应用”与代码不符，待 W23/后续波次决策真正接入或撤回。
   - 后续规划：在后续重构中统一决策 Spacing 标尺的去留与接入路径。
