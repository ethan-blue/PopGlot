# PopGlot 蓝框交互 唤醒性能与本机 AI 工具接入实施方案

日期：2026-10-11。接收者：后续实施者与独立复查者。

先改善鼠标操作时的大区域蓝框，测清并优化唤醒与现有 API 链路，再增加由本机工具管理账号的可选连接。账号复用的主要目标是减少配置步骤；速度、稳定性和安全收益必须分别验证。用户已确认目标工具为 Codex、Claude Code、oh-my-pi。

本文件是设计与执行交接。本轮没有修改应用代码，没有测量旧机器，没有验证真实账号或请求。所有性能目标均为建议验收目标，不是已达到的数字。

## 1 当前基线与事实

- 仓库：`D:\Projects\GitProjects\PopGlot`，分支 `main`，HEAD `96a7fd6e5ee19a57704f9120cfd3f69879259374`。
- 审查开始时工作区干净。仓库、父目录及仓库内检索未发现适用的 `AGENTS.md`；实施开始时必须重新查。
- 本轮进程查询未发现 PopGlot 进程。这只描述查询时刻；执行测试前必须重新检查，不能据此终止后来启动的用户实例。
- PATH 可解析 `codex.ps1`、`claude.ps1` 和 `omp.exe`。这证明命令入口存在，不证明版本兼容、安装可信、已登录、模型可用或可用于产品。
- `docs/UI-UX-整改结果报告-2026-09-29.md` 是历史实施报告。以下结论重新查过当前源代码；历史通过标签不代替本轮验收。

| 当前发现 | 源码入口 | 状态及含义 |
| --- | --- | --- |
| `FlatTextBox` 继承普通输入模板，常驻 1 DIP 描边；悬停设置 AccentBorderBrush，焦点设置 AccentBrush | `apps/PopGlot.Windows/Themes/Controls.xaml`：隐式 TextBox 模板、FlatTextBox | 通过：代码确认存在；与用户描述吻合，实际区域仍需复现 |
| `ResultTextBox`、`FlatRichTextBox` 使用 IsKeyboardFocusWithin 画 FocusBrush | 同上 | 通过：代码确认。该属性对鼠标点击获得焦点也成立，并非“只在键盘导航时出现” |
| 主窗隐藏、浮窗隐藏会主动压缩工作集 | `App.CloseActivePanel`、`App.TrimWorkingSet`、`MainWindow.OnClosing` | 通过：代码确认；造成慢唤醒的因果关系未验证 |
| 主窗口第一次使用才创建，ShowMainWindow 在 Show 前刷新历史 | `App.EnsureMainWindow`、`ShowMainWindow`、`LibrarySection.ReloadHistory` | 通过：代码确认。HistoryStore.Load 返回内存副本，不能误报为每次重新读历史文件；列表重建和过滤成本需测量 |
| 划词先读取选区，再创建和显示浮窗；预读超时为 2 秒 | `App.BeginSelectionTranslationAsync`、`ReadSelectionPrereadAsync` | 通过：代码确认；不同目标应用取词延迟未测 |
| App 字段初始化会创建 HistoryStore、VocabularyStore | `App.xaml.cs`、两类构造函数 | 通过：代码确认；托盘启动计时可能遗漏 OnStartup 之前的工作 |
| 档案流式翻译选择 StreamTextDraft，FFI 每次创建 ProviderClient | `TranslationCoordinator.CreateTextStream`、`CoreBridge.TranslateTextDraftStream`、`popglot_translate_text_draft_stream_v2` | 通过：代码确认生命周期；握手/连接复用收益未测 |
| 现有 C10 有 FirstDeltaMs、SelectionReadMs、PaintedLagMs，泵约 40 ms 一次 | `TranslationModels.cs`、`TranslationCoordinator.PumpStreamAsync`、`TranslationPanelWindow.xaml.cs` | 通过：代码确认；没有完整热键到可交互首帧指标 |
| PaintedLagMs 在终态处理函数尾部停止 Stopwatch | `TranslationPanelWindow.HandleSessionResultAsync` | 通过：代码确认；不能代表画面已由 DWM 呈现 |
| genai 只构造请求，HTTP、安全策略和流式解析仍由 PopGlot 保留 | `docs/architecture/ADR-0030-genai-staged-migration.md`、`provider_sdk.rs`、`provider.rs` | 通过：代码确认；协议构造是否为瓶颈未测 |
| 旧机器表现、真实 UI、真实提供方、CLI、安装包与升级 | — | 未验证 |

微软说明 `SetProcessWorkingSetSize(-1,-1)` 会移出尽可能多的工作集页；后续访问可能产生缺页。这里把它列为需测量的候选原因，不认定已经证明根因。[Microsoft 文档](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-setprocessworkingsetsize)

## 2 执行顺序

每步交付必须包含：改动文件、根因或设计理由、验证命令、观察结果、失败路径、通过/不通过/未验证、下一步。出现失败要修复或保留明确阻塞，不允许把“没有测”填成通过。

| 步骤 | 优先级 | 依赖 | 要做的事 | 放行条件 |
| --- | --- | --- | --- | --- |
| P00 | P0 | 无 | 固定基线、复现蓝框、建立性能与证据目录 | 原始输入与产物身份可追溯 |
| P01 | P1 | P00 | 调整大文本区域的 hover 与 focus | 鼠标无蓝框；键盘焦点仍可见 |
| P02 | P0 | P00 | 补齐启动、唤醒、取词、网络和呈现时间线 | 能归因本地/传输/模型/渲染 |
| P03 | P1 | P02 | 优化热唤醒、历史刷新、选区反馈 | 同机 A/B 有收益且生命周期不退步 |
| P04 | P1 | P02 | 复用现有 HTTP 客户端，排查协议和流式成本 | 回环证明复用、安全回归通过 |
| P05 | P0 | P00、P02 | 定义 CLI 执行模型、配置与出网边界 | API 旧配置兼容，离线阻断子进程出网 |
| P06 | P1 | P05 | 准备统一文本协议与进程通信底座 | 不重复提示词/保护逻辑；可回放、可取消 |
| P07 | P1 | P06 | 接入 Codex app-server | 匹配版本、授权范围、有效工具限制验证 |
| P08 | P2 | P06 | 接入 OMP RPC；明确 Claude 支持边界 | 按上游提供方逐项放行 |
| P09 | P1 | P05、P07，P08 按结果 | 设置页连接流程与错误闭环 | 能连接、测试、选用、断开、恢复 |
| P10 | P0 | 所有实际交付项 | 本地回归、旧机器 A/B、独立复查、交接 | 各层证据分开；未放行适配器保持禁用 |

先交付 P00→P04，再交付 P05→P10；不让 CLI 接入阻塞已经可以交付的交互与性能修复。P02 可与 P01 顺序穿插，但性能修复必须有 P02 的前后对照。这里的步骤编号用于实施验收，不是当前完成状态。

## 3 P00 基线与复现

1. 查规则、`git status --short`、`git diff --stat`、HEAD 和当前进程，保护已有改动。需要分支时使用 `codex/ui-wake-ai-connections` 或符合当时规则的名称；不自动提交、推送、合并、发布。
2. 逐一复现：工作台原文、译文、要点，浮窗原文/译文，查词输入/结果，资料库详情，以及设置中的普通表单。记录“只移入”“点击”“Tab 进入”“Shift+Tab 退出”“程序自动聚焦”是哪种触发。
3. 保存截图与操作说明到 `artifacts/ui-wake-ai-2026-10-11/baseline/`，使用当次构建；历史截图只作参考。
4. 记录机器 CPU 型号、内存容量、存储类型、电源模式、Windows 版本、DPI、主题、程序版本与构建 SHA256。不要记录用户名、序列号、账号邮件或私人历史内容。
5. 性能至少区分：进程启动至托盘；第一次打开工作台；第二次打开；隐藏 1/30/300 秒后打开；划词；查词；截图取字；截图翻译；恢复最近结果。启动、唤醒、AI 等待不能混在同一列。

失败处理：无法接触旧机器时完成本地基线和测量工具，旧机器栏保持未验证；无法复现指定蓝框时报告实际测过区域，继续按当前源码已确认的范围修复，不猜截图中的其他区域。

## 4 P01 让鼠标操作保持安静

### 4.1 目标行为

| 控件 | 悬停 | 鼠标点击 | 键盘导航 | 禁用/错误 |
| --- | --- | --- | --- | --- |
| 大原文编辑区、查词内嵌输入 | I-beam，无大区域蓝色描边 | 光标和文字选区反馈 | 清晰且不挤压内容的 1 DIP 焦点提示 | 保留现有可读性和错误状态 |
| 只读译文、要点、富文本阅读区 | 不描蓝框，保持选择/链接能力 | 可选择复制，不出现整块蓝框 | 使用轻量焦点提示，Tab 可识别当前区域 | 不用光标假装可编辑 |
| 普通小表单、按钮、开关、选项 | 保留适度反馈 | 保留按下/选中语义 | 保留现有 FocusRing | 保留错误、选中与禁用语义 |

不要把所有 Accent 状态全局删除：选中的引擎、单选项、错误状态和键盘定位有各自含义。文本仍用 I-beam；按钮按当前系统用 Hand/正常按下反馈，不把所有区域改成手形。

### 4.2 具体修改方式

修改 `Themes/Controls.xaml` 与 `Ui.cs`，为大文本表面添加局部附加属性。下面为可实现的结构；名称按项目风格统一，避免对普通 FormTextBox 产生连带改动。

```csharp
// Ui.cs。默认 false，既有表单模板保持原行为。
public static readonly DependencyProperty QuietTextSurfaceProperty =
    DependencyProperty.RegisterAttached(
        "QuietTextSurface", typeof(bool), typeof(Ui), new PropertyMetadata(false));
public static bool GetQuietTextSurface(DependencyObject element) =>
    (bool)element.GetValue(QuietTextSurfaceProperty);
public static void SetQuietTextSurface(DependencyObject element, bool value) =>
    element.SetValue(QuietTextSurfaceProperty, value);
```

隐式 TextBox 模板当前直接写 `Surface.BorderBrush`，因此仅在 FlatTextBox 上写一个 BorderBrush Setter 不能覆盖模板触发器。把原来的 hover/focus Trigger 改为以下条件形式；平面样式设置 QuietTextSurface=true。

```xml
<MultiTrigger>
    <MultiTrigger.Conditions>
        <Condition Property="local:Ui.QuietTextSurface" Value="False" />
        <Condition Property="IsMouseOver" Value="True" />
    </MultiTrigger.Conditions>
    <Setter TargetName="Surface" Property="BorderBrush"
            Value="{DynamicResource AccentBorderBrush}" />
</MultiTrigger>
<MultiTrigger>
    <MultiTrigger.Conditions>
        <Condition Property="local:Ui.QuietTextSurface" Value="False" />
        <Condition Property="IsKeyboardFocusWithin" Value="True" />
    </MultiTrigger.Conditions>
    <Setter TargetName="Surface" Property="BorderBrush"
            Value="{DynamicResource AccentBrush}" />
</MultiTrigger>
```

给三种平面样式 `FlatTextBox`、`ResultTextBox`、`FlatRichTextBox` 设置统一的轻量 `QuietSurfaceFocus`。Result/FlatRich 模板中删除会无差别响应鼠标点击的 IsKeyboardFocusWithin 描边触发器；FlatTextBox 被上述附加属性条件隔离。

```xml
<Style x:Key="QuietSurfaceFocus" TargetType="Control">
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate>
                <Border BorderBrush="{DynamicResource FocusBrush}"
                        BorderThickness="1" CornerRadius="6"
                        IsHitTestVisible="False" SnapsToDevicePixels="True" />
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
<!-- 三个平面样式中的 setter；QuietTextSurface 主要用于 FlatTextBox 的继承模板 -->
<Setter Property="local:Ui.QuietTextSurface" Value="True" />
<Setter Property="FocusVisualStyle" Value="{StaticResource QuietSurfaceFocus}" />
```

保留预留描边厚度，避免布局尺寸变化。若内侧遮到文本或高 DPI 下被裁剪，改为不影响布局的外侧 Adorner，并验证宿主有 AdornerLayer。不要额外注册全局键鼠 hook 来实现这件事。必要的自动聚焦仍保留 caret；读屏能获取实际焦点。高对比模式下核对 FocusBrush 的有效值，必要时使用系统高对比颜色。

WPF 的 FocusVisualStyle 通常只在键盘发起的焦点导航时绘制，鼠标/程序聚焦会退出该模式；模板 IsKeyboardFocusWithin 的含义不同。优先采用框架机制，真实键盘与自动聚焦行为必须验收。[Microsoft 文档](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/styling-for-focus-in-controls-and-focusvisualstyle)

### 4.3 文件与验证

- 实际调用面：`Sections/TranslateSection.xaml`、`TranslationPanelWindow.xaml`、`QuickSearchWindow.xaml`、`Sections/LibrarySection.xaml`、`HelpWindow.xaml`。只改用到目标样式的区域；不改字号、整套色板或信息架构。
- 检查 `tests/PopGlot.Windows.LogicTests/Program.cs` 中已有焦点测试，更新“只要聚焦就整块描边”的旧断言。新增/调整实际鼠标输入、真实 Tab 导航下的行为测试，不以强制 SetValue(IsKeyboardFocused) 代替输入源测试。
- 真机检查深/浅主题、100/125/150/200% DPI、高对比、键盘、IME、链接点击、复制、文字选择与滚动。普通表单、开关和单选项不得失去焦点/选中反馈。

失败回退：只回退 P01 引入的局部样式变更，保留其他原有 UI 修复。若原生焦点 Adorner 在某窗口不可见，先修 Adorner 宿主；仍不可行再设计局部输入来源跟踪，不能直接把焦点提示清空。

## 5 P02 建立能归因的时间线

### 5.1 时间点与定义

新增 `Services/InteractionTrace.cs`，使用同一 C# 进程的 Stopwatch 单调时钟。每次交互生成随机 traceId，贯穿 App、窗口、Coordinator 与 Bridge；Rust 只返回自己的阶段耗时，不与 C# 的原始 ticks 直接相减。

| 时间点 | 采集位置 | 定义 |
| --- | --- | --- |
| HotkeyReceived / TrayActionReceived | HotkeyService 回调到 App 的入口 | 开始计时，不从请求发送才开始 |
| DispatcherEntered | App UI 调度入口 | 从输入到 UI 队列开始执行 |
| SelectionStarted / SelectionEnded | App.ReadSelectionPrereadAsync 前后 | 包含真实前台应用的取词；传入 preloaded adapter 后的计时不能替代它 |
| WindowConstructionStarted / Ended | EnsureMainWindow、CreatePanel、ShowQuickSearch | 分别记录首次和重用 |
| ShowRequested / Loaded | 调用 Show 与 Loaded | 仅表示逻辑生命周期，不代表首帧呈现 |
| FirstRenderCallback | Show/内容更新后的单次 CompositionTarget.Rendering | UI 渲染机会近似值，不冒充 DWM 实际呈现 |
| InteractiveReady | 首屏布局完成、必需输入与事件可用 | 不等待历史列表、模型目录或网络健康检查 |
| PolicyChecked / RoutingEnded / PromptPrepared | Coordinator、Core 的请求准备 | 本地配置/路由/协议构造成本 |
| TransportSubmitted / ResponseHeaders / FirstWireEvent | Rust HTTP sender / SSE 流 | 发送到响应头/首事件；包含网络和服务器等待 |
| FirstVisibleDeltaBuffered | TranslationStreamBuffer | 已经可用于译文展示的首段，不把 reasoning/usage 当译文 |
| FirstVisibleDeltaApplied / FirstDeltaRenderCallback | UI dispatcher 合并更新后 | UI 接收和渲染机会，区别于字节抵达 |
| Completed / CancelRequested / CancelObserved | 全链路 | 完成和取消传播耗时 |
| CliSpawn / Ready / AccountChecked / TurnSubmitted | CLI adapter | 子进程与推理分别计时 |

`SelectionReadMs` 原字段继续兼容，但生产预读耗时应从 App 传给会话。保留 C10 展示兼容；新增诊断字段使用 nullable，未知为 null，而不是用 0 混淆“未测”与“极快”。FirstDeltaMs 当前含约 40 ms 轮询延迟，要另记 buffer 的首 delta 时间点。

```csharp
// 新诊断结构示例；不替代现有 TranslationSessionTiming 的公开兼容契约。
internal enum TracePoint { HotkeyReceived, DispatcherEntered, SelectionStarted,
    SelectionEnded, ShowRequested, FirstRenderCallback, InteractiveReady,
    TransportSubmitted, FirstVisibleDeltaBuffered, FirstVisibleDeltaApplied,
    FirstDeltaRenderCallback, Completed, CancelRequested, CancelObserved }

internal sealed class InteractionTrace
{
    private readonly long _origin = System.Diagnostics.Stopwatch.GetTimestamp();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TracePoint, long> _points = new();
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public void Mark(TracePoint point) => _points.TryAdd(point,
        System.Diagnostics.Stopwatch.GetTimestamp());
    public double? Milliseconds(TracePoint point) =>
        _points.TryGetValue(point, out var tick)
            ? System.Diagnostics.Stopwatch.GetElapsedTime(_origin, tick).TotalMilliseconds
            : null;
}
```

记录结构化、限量、本地诊断，默认不开启详细性能日志；调试模式显式开启。只存阶段名、耗时、匿名引擎类型、次数、字节数和终态。不要保存原文、译文、截图库、HTTP 头、账户、URL 查询串、完整 CLI JSON 或 stderr。导出摘要前做脱敏。日志刷盘不能在热键/UI 主线程关键路径。

### 5.2 实验与归因

建立 `scripts/measure-wake.ps1` 和对应可隔离的 app harness；复制现有 measure-core 的实例预检查、包身份与结构化失败报告机制。只控制测量程序自己创建的实例，不触碰用户进程/剪贴板/真实凭据。通过实例内调度指定动作，不在任意前台软件合成快捷键。真实跨应用取词另做人工测试。

每场景先做 3 次未计入样本的预热，热唤醒至少 30 个计入样本。首用场景逐进程独立测；称为“进程首次启动”，不要把 OS 文件缓存已经热了的重复启动叫磁盘冷启动。不通过清系统缓存、关闭防护等方式美化数字。

输出每个样本、p50/p95、失败率、样本量、构建身份；失败/超时单独统计且计入失败率，不能丢掉后只报告快样本。记录 WorkingSet、PrivateBytes、GC/分配、CPU、缺页差值；硬缺页与文件/磁盘原因须用 ETW/WPR/PerfView 等工具确认，不把总缺页计数当作硬缺页。

- 网络被禁止、fake executor 立即返回时仍慢：优先本地 UI/工作集/存储/取词。
- 输入到首屏很快，submit→首响应慢：可确定等待发生在提供方链路，但仅此不能区分 DNS/TLS、网络 RTT、服务器排队、模型推理；补 transport trace 和服务端证据。
- 首事件很快、首译文慢：检查 reasoning、元数据、流式拆分、过滤/保护恢复及客户端缓冲；不能把模型思考归因到 UI。
- 首译文 buffer 很快，应用/渲染很慢：检查 Dispatcher 队列、Markdown 重绘与轮询。
- API 和 CLI 不同模型/服务域名：只能比较最终用户体验，不能称为“登录协议更快”的因果证明。

本机回环夹具分别注入：响应头延迟、首译文延迟、碎片 SSE、快速结束、401、429、断流、永不完成。使用 fake CLI 注入 stdout 分片和交错、stderr 堵塞、崩溃。避免默认调用收费模型。

### 5.3 建议目标

以下为待 P00 硬件基线确认的产品目标；实施者不得降低目标来填通过，应记录差距与原因。

| 场景 | 主机目标 p95 | 旧机器目标 p95 | 限定 |
| --- | --- | --- | --- |
| 热唤醒到可交互首屏 | ≤150 ms | ≤300 ms | 无在线请求，隐藏 1/30/300 秒分别报告 |
| 进程首次打开工作台 | ≤500 ms | ≤1000 ms | 不含 OS 真冷磁盘条件，单独报告 |
| 划词获取前的等待反馈 | ≤150 ms | ≤300 ms | 不抢焦点，不能妨碍复制 |
| 译文进入 UI 至下一次渲染机会 | ≤100 ms | ≤200 ms | 使用已定义近似点；实际呈现另用 ETW |
| 取消后 UI 停止接受旧结果 | ≤100 ms | ≤200 ms | 不等于远端停止计费 |

真实模型 TTFT 不写统一硬保证；与原配置、同模型/同任务测量做对照，报告限流与错误。既有内存预算必须一起复查：放弃强制工作集压缩后数字可能升高，不能继续引用压缩后的低读数，也不能为了低读数重新引入卡顿。

## 6 P03 修复本地唤醒路径

### 6.1 工作集压缩 A/B

修改 `App.CloseActivePanel`、`MainWindow.OnClosing`：实验版本停止每次 Hide 后调用 TrimWorkingSet。隐藏/取消/保存快照逻辑不变。普通生产交互最终默认不主动压缩工作集；实验开关仅供 A/B，不增加面向用户的复杂设置。

连续开关与隐藏 1/30/300 秒做对照，同时记录内存、缺页与唤醒。强制 GC、VirtualLock、忙轮询、后台周期重绘都不是替代方案。需要减内存时释放真实闲置资源、解除事件订阅、限制图像缓存；不要只改变任务管理器数字。

失败回退：回退本步的压缩策略实验；保留时间线。若确认不是瓶颈，报告“不支持该假设”并继续其他线索。最终选择综合内存/响应数据，不能仅凭直觉。

### 6.2 主窗口先显示 首屏后刷新

`App.ShowMainWindow` 当前在 Show 前 `ReloadHistory`。把非关键历史刷新从首次帧前移出；重用窗口时按数据版本检查刷新，而不是每次重建相同列表。

```csharp
// App.ShowMainWindow 的结构示例；沿用现有最大化、聚焦和首屏收敛行为。
var mainWindow = EnsureMainWindow();
WindowPositioner.ConvergeFirstShow(mainWindow);
mainWindow.Show();
if (mainWindow.WindowState == WindowState.Minimized)
    mainWindow.WindowState = WindowState.Normal;
mainWindow.Activate();
mainWindow.Focus();
mainWindow.RequestLibraryRefreshAfterFirstFrame(); // 新接口，需实施，不是现有方法
```

新增刷新代数/取消机制：记录请求代数；窗口已关闭或后来的刷新已替换时丢弃旧结果。历史/生词筛选在后台计算纯数据快照；WPF 对象只在 UI 线程创建/应用。列表是可见页时显示上次内容与轻量加载提示，失败不清空已有内容。

在 `HistoryStore`、VocabularyStore 的**实际内存数据变更成功**处递增 Revision；包括添加、删除、清空、重试加载和撤销。Library 以 Revision 判断是否需要刷新，不能只靠 Count（相同条数也可能变了）。初次加载、损坏隔离提示、删除/导出/撤销即时性必须保持。先测过滤/重建成本，必要时虚拟化，别把 UI 控件扔进 Task.Run。

审查 MainWindow 构造、TranslateSection.Initialize、状态刷新与 ProfileManager.Load 的耗时。只有证据证明影响首帧才延迟；语言选择、核心交互、事件绑定仍是首屏必需项。字段构造读历史与生词需要在启动 trace 中体现，是否异步加载必须另证明启动/退出/错误恢复行为，不做一次性重写。

### 6.3 划词先反馈 仍保持正确前台

不减少现有取词上限来假装更快。先测真实 pre-read。若它是等待主因，允许在启动读取后显示不激活的轻量反馈壳；目标 HWND 在显示前固定，继续针对原目标取词，保持复制序列号与 finally 恢复契约。

```text
捕获目标 HWND 与 request generation
→ 启动唯一一次取词
→ 若尚未完成，在预算内显示 ShowActivated=false 的等待壳
→ 成功：更新同一壳并开始翻译
→ 无选区/超时：按现有规则落到查词并给出提示
→ 取消/被新请求替换：丢弃旧 UI 回调，仍观察取词任务并完成剪贴板恢复
```

不要为先显示壳而调用 Activate/Focus；不要再合成第二次 Ctrl+C。需要改变预读任务取消所有权时，统一由 App 请求 generation/CTS 管理，并证明快连续两次热键不会交叉复原剪贴板或污染结果。相关回归位于 `ClipboardSelectionService.cs`、LogicTests 中现有 preread/剪贴板测试。截图取字、恢复最近结果与展开到主窗应继续零重发。

窗口池/预热是后续选项：只有构造占比显著才采用。在 Dispatcher 空闲时限量创建 UI，不能在工作线程创建 WPF 控件，不能让隐藏预热触发网络、OCR、取词、历史写入或抢焦点。每次 session 必须重新取消旧请求、推进 epoch、清图片与终态；未通过 200 次生命周期压力验收时不启用复用池。

## 7 P04 优化现有 API 传输与协议

### 7.1 客户端复用

现有 `ProviderClient` 已是 Clone 包装的 reqwest::Client，但档案 FFI draft-stream 入口每次 new。保留不可变配置快照和单请求凭据，把客户端生命周期提高到可限量复用的池。

修改：`crates/popglot-core/src/provider.rs`、新增 `transport_pool.rs`、`lib.rs`、`crates/popglot-ffi/src/lib.rs`。覆盖实际档案文本、分段、图片、连接测试路径；分别说明哪些路径仍刻意单用。不能只修未被普通档案选中的全局默认 client。

```rust
// 设计结构，不是当前项目已有类型。字段必须包含所有影响 transport 行为的参数。
struct TransportKey {
    origin: String,                 // 规范化 scheme + host + port
    connect_timeout: std::time::Duration,
    total_timeout: std::time::Duration,
    stream_idle_timeout: std::time::Duration,
    stream_total_timeout: std::time::Duration,
    max_response_bytes: usize,
    max_retries: u8,
    retry_delay: std::time::Duration,
    accept_invalid_certs: bool,
    transport_revision: u64,
}
// ProviderClientPool::get_or_create(key, limits) -> Result<ProviderClient, ProviderError>
// 最多 8 个条目，闲置 10 分钟淘汰；以上是初始设计参数，测量后可调整。
// 持锁只查找/插入/clone；建 client 在锁外；锁不跨 await/HTTP。
```

使用现有 URL 验证与归一化工具；不通过用户字符串拼装缓存键。池不包含 API key、Authorization、ExtraHeaders；这些仍在每次构造 request 时添加。禁止 client 默认认证头。复用不复用某次出网许可：所有每次发送的策略、LAN 许可、重定向、TLS、尺寸、取消与重试检查原样执行。TLS/传输参数变化要命中不同键或失效；严格 TLS 不可复用宽松 TLS 的 client。

参考改动点：`popglot_translate_text_draft_stream_v2` 中 `ProviderClient::new(AppCore::limits_for(&settings))` 改为经过验证的池获取；返回 clone 后传给原 execute_*_snapshot 函数。不要保存草稿配置，不要写回用户档案，不要通过修改全局当前引擎实现复用。

验证方式：本机 HTTP keep-alive 服务记录两次同 origin 请求是否复用同一 TCP 连接；同 client 实例指针不是充分证据。检查 A/B 两个 Authorization 每次正确，端点隔离、严格/宽松 TLS 隔离、配置切换、限流重试与取消仍工作。若回环服务主动关闭连接，客户端重新连接属正常，不能伪造复用率。吞吐与 TTFT 收益以实测为准。

### 7.2 协议与渲染排查

先在 `provider_sdk.rs`、`provider.rs` 记录保护/Prompt 编译、genai 请求体构造、序列化、send、SSE 解析与恢复各阶段；重用现有固定 prompt 与流式 fixture。模型名单/连接测试不得在每次翻译前在线刷新。

核对实际请求形状、任务、模型、stream、include_explanation、响应上限、reasoning/thinking 是否与设置相符。不要擅自切换模型、减少完整性要求、删除术语保护、改语言或改权限来提升速度。建议“译文先到，说明后到”；若增加“只译文”选项，必须单独保存用户选择并验证，不默认吞掉已有说明。

若随机 delimiter 出现在提示词早段影响缓存，先确认真实 cache usage 与模板布局；不能为缓存命中改成固定 delimiter，不能削弱注入/元数据分隔。SDK 迁移保持 ADR-0030，不升级依赖或替换整个传输栈来碰运气。

流式 UI 已有约 40 ms 泵与节流。检查 `TranslationStreamBuffer` 累积副本成本和浮窗/工作台 Markdown 全量重绘；短文本与 1/8/32 KiB 分别测。若重绘占比大，流式期间用增量纯文本，终态再渲染 Markdown，保持末尾排空、空白保真、滚动/选区与链接语义。每步以采样证据判断，不能把降低刷新频率当作无代价优化。

失败处理：回滚本步客户端池或渲染优化的独立提交/补丁，保留原协议与测试；不自动回退另一供应商、协议、账号或模型，不重发已经出现译文的请求。

## 8 P05 本机账号接入的产品与配置决策

### 8.1 支持边界

| 接入 | 决策 | 账号由谁管理 | 本轮放行状态 |
| --- | --- | --- | --- |
| 现有 API/本机模型 | 继续支持，并先修链路 | PopGlot 原有 credential vault | 现有路径；本轮未实测 |
| Codex CLI app-server | 第一接入候选，用户显式连接 | Codex 自己的认证存储与刷新 | 协议设计，真实接入未验证 |
| OMP RPC | 第二候选、逐提供方放行 | OMP 自己的 auth storage | 协议设计，真实接入与上游授权未验证 |
| Claude Code 订阅账号 | 不作为面向用户产品的通用后端 | 不读取/转交订阅 Token | 按当前官方限制不放行 |
| Claude API | 保留现有 Anthropic API 接入 | 用户 API key，原有 vault | 现有路径；本轮未实测 |

Codex 文档提供 account/read、托管登录与 app-server 集成。优先由 Codex 管理认证；按所支持安装版本验证有效配置和协议。[Codex app-server](https://learn.chatgpt.com/docs/app-server)；CLI 的本地缓存与刷新由其管理，PopGlot 不读取其 Token 文件。[Codex 认证](https://learn.chatgpt.com/docs/auth)

OMP 项目提供 stdio RPC，工具与扩展可以通过 CLI 参数禁用；这说明技术可接入，不能据此证明每个上游提供方都授权账号复用。[OMP RPC](https://github.com/can1357/oh-my-pi/blob/main/docs/rpc.md)、[OMP CLI](https://github.com/can1357/oh-my-pi/blob/main/docs/cli-reference.md)、[OMP Providers](https://github.com/can1357/oh-my-pi/blob/main/docs/providers.md)

Anthropic 当前官方说明不允许第三方产品为用户接入 Claude.ai 登录或通过 Free/Pro/Max 凭据路由请求；应使用 API key 或支持的云提供方。本产品不复制订阅 Token，也不以包装 Claude Code 的方式绕过此限制。若未来官方支持变化，另立适配决策后验证。[Claude Code 官方说明](https://code.claude.com/docs/en/legal-and-compliance)

更完整的原生“登录 ChatGPT”可另评估 OpenAI 的注册接入要求、权限与会话生命周期；它与“调用已有 Codex 登录”是两种实现，不能抄公共 OAuth client ID 冒充自家客户端。本次不要求实施原生 OAuth。[Sign in with ChatGPT](https://developers.openai.com/siwc/token-sharing-open-source/codex-app-server)

### 8.2 发现与连接

“检测本机工具”按钮只检查 PATH 和明确候选安装目录的可执行入口、文件元数据/离线版本信息。不要扫描浏览器 Cookie、账号库、auth.json、agent.db、Windows 凭据条目或其他应用会话。未连接时只显示“已检测到工具，登录状态待检查”，不猜已登录。

用户选择该工具并点“连接”后，才启动它的独立子进程并调用支持的非推理状态接口。启动/检查可能触发工具的网络行为，设置页在首次连接前说明“工具会按其配置连接服务，翻译内容将发送到所选提供方”；网络关闭时不得启动此流程。启动来源与版本需要确认，不能执行当前目录伪装的同名脚本。

Codex 使用 account/read 的 refreshToken=false 查询；不自动刷新、不遍历历史。模型目录只是目录，不等于权限或真实可用。状态区分：未发现、未连接、启动中、需登录、状态已识别、待请求验证、可用、过期、限流、版本不兼容、配置不满足隔离要求。只有用户主动测试成功或真实完成一次请求后才把“请求可用”标为已验证。

未登录时让用户通过工具自己的登录流程完成，返回后再检查；PopGlot 不索取密码/Token。断开仅断开 PopGlot 的连接并关闭它创建的子进程，不调用全局 logout、不删除/改写其他工具凭据。

### 8.3 配置模型

不要向 Rust ProviderType 硬塞 Codex/OMP：ProviderType 是 HTTP 协议，执行后端是另一个维度。

建议新增执行 discriminator（序列化用稳定字符串，不用数字）：

```csharp
internal enum EngineExecutionKind { NativeApi, CodexAppServer, OmpRpc }
internal sealed record CliConnectionSettings(
    string ExecutablePath, string ExpectedVersion, string ModelId,
    string? UpstreamProviderId, bool Enabled = false);
// ProviderProfile 新增 ExecutionKind，默认 NativeApi；CliConnection 可空。
// 本机子进程不等于本机推理：Codex/OMP 远程模型必须 IsLocal=false。
```

同步修改 C# `Services/ProfileManager.cs` 的 clone、加载/保存、验证/路由/指纹和 `CoreBridge.cs` DTO；Rust `crates/popglot-domain/src/lib.rs` 的 ProviderProfile、产品配置校验/迁移；Core/FFI 相关转换；设置页编辑、快速切换、任务能力检查、历史执行器标识。API 旧配置缺字段时默认 NativeApi，旧字段不被重置；未知后端保持不支持状态，不能偷偷回退到免费引擎。

CLI 路由不要求 API key，也不假造 HTTP endpoint；不经过 `TextIsConfigured` 的模型+API 地址逻辑判定。ResolveTextProviderCapability、ProbeNextTextRoute、summary 能力检查、视觉/文字路由都必须消费新的**类型化能力**，不能从中文标签、文件存在或 IsLocal 推断。

初版 CLI 只支持文字与本机 OCR 后的文字。截图图像不经 CLI 发送，SupportsVision=false；summary 需经过任务协议测试才启用。翻译风格/自定义规则在同一会话起点冻结，历史保留实际 backend/model/template revision；新枚举加入 TranslationTextExecutor、必要的 PipelineKind/文案，不能伪装成原 OpenAI HTTP 服务。诊断里的 HTTP status/endpoint 未知应为空，不用 200 或假 URL 表示成功。当前 ProviderDiagnostics 带 HTTP 协议身份，实施时应增加类型化 BackendDiagnostics，或明确区分 HTTP/CLI 的可空诊断 DTO，并同步 JSON 兼容与 UI 格式化，不能仅在 CLI 侧填假值凑现有构造函数。

### 8.4 出网与子进程控制

新建 `Services/ExternalRuntimePolicy.cs` 与实际 RuntimeHost 管理类；复用核心网络、安全离线和敏感输入策略，增加“当前所选连接允许执行”的类型化检查。不要把免费的 AllowOnce token 泛化为 CLI 权限。

必须先检查再启动/连接，再在每个 prompt 提交前重新检查。已暖进程存在时也必须重查。安全离线/关闭网络/断开生效后，停止新请求、推进 epoch、取消正在执行的 turn；必要时终止 PopGlot 创建的子进程树。绝不按进程名杀用户 Codex/OMP。

**重要限制：**PopGlot 的 HTTP sender 门禁控制不了 CLI 内部请求。启动阶段可能加载扩展、MCP、遥测、更新、缓存预热或模型目录；不能用“提交前检查了一个 bool”声称子进程零出网。离线态禁止启动；连接态使用有效配置关闭与翻译无关的功能；验证网络轨迹。若要求运行中撤权后严格零新出网，必须提供可验证的 OS/进程网络边界，否则显示“已停止本应用继续发送，已提交请求可能仍在提供方运行”，不得作更强承诺。

不能把跨进程的“取消成功”视为服务端已停止计费。不能默认由代理/转发隐藏真实提供方，不能把 CLI 自动代理配置说成沿用 PopGlot 的 no_proxy 行为。连接详情说明提供方与可能的不同网络配置；无法限制未知目的地或无法确认最低权限时，该适配器不放行。

## 9 P06 统一文本协议与进程通信

### 9.1 保留一套翻译语义

新通道必须通过 TranslationCoordinator 与现有 TranslationStreamSession/TranslationStreamBuffer 汇合；保留 RuntimeGate、sessionId/epoch fence、Partial、不保存不完整结果、复制/收藏/朗读门槛和零重发恢复。

不要在 C# 另写一套“请翻译这个文本”提示词。重用 Rust `TranslationRequest`、`StreamPromptBuilder`、`protect_tokens/restore_tokens`、随机 delimiter 和 `TextFirstAssembler`。现有 Rust 代码在 `crates/popglot-core/src/provider.rs`、`streaming.rs` 与 `popglot-domain/src/lib.rs`。

建议将 HTTP 执行器中的无网络准备/恢复步骤抽为 `PreparedTextJob` 与 `TextJobAssembler`，供 HTTP 与外部执行器共用。给 FFI 增加明确命名、版本化的纯文本会话入口，C# Bridge 包装：

```text
popglot_text_job_prepare_v1(request_json) -> envelope
  返回不含凭据的 jobHandle、systemInstructions、userPayload、promptVersion
popglot_text_job_push_v1(jobHandle, utf8_delta) -> visible_delta envelope
popglot_text_job_finish_v1(jobHandle, completion_state) -> result envelope
popglot_text_job_free_v1(jobHandle)
```

这些是拟新增接口，不是已有 API；遵循现有返回内存分配/释放约定。jobHandle 用不可预测 ID、注册表有数量/字节上限，所有错误/取消/finally 释放且 free 幂等；拒绝已结束/未知 handle。锁只处理局部 assembler，不跨异步调用。request JSON 包含 task、语言、说明选项与冻结 preference，不包含 API key；source 作为独立 JSON 数据传入，不按 shell/指令插值。已在 fixture 中保护的 token 不得再次重复保护。

抽取前后 HTTP 请求与最终输出应匹配现有 fixture；delimiter 跨 chunk、UTF-8、空白、metadata 缺失/损坏、正文中类似 delimiter、路径/命令/代码与注入样本全部验收。不要把 CLI 最终 JSON 输出直接当 TranslationResponse：先恢复可见正文与元数据，再构造正确结果和 Partial 状态。

### 9.2 类型化执行器

为路由到达的文本通道增加统一请求对象，避免把 CLI 塞进只有 draftSettings+apiKey 的旧签名：

```csharp
internal sealed record TextExecutionRequest(
    string SessionId, long Epoch, string Source, string SourceLanguage,
    string TargetLanguage, PromptAnchorSnapshot? Preference, long MaxResponseBytes);
internal interface ITextBackend
{
    TranslationStreamSession Start(
        ResolvedTextEngine engine, TextExecutionRequest request,
        CancellationToken cancellationToken);
}
// ResolvedTextEngine 为拟新增的 NativeApi/Codex/Omp 判别联合。
// NativeApi 实现包住现有 ITranslationExecutor，不改现有 HTTP 语义。
```

修改 Coordinator 的 text route、分段路径以及截图 OCR 后文本入口，统一选择 backend；前置策略检查和 style snapshot 放在公共层。大文本分段继续共用锚点、总输出上限，后段失败只保留 Partial、不写历史。测试 fake executor 更新并保留旧 HTTP 适配器回归。

### 9.3 子进程底座

新增 `Services/AiConnections/`：`CliDiscovery.cs`、`CliRuntimeHost.cs`、`JsonLineTransport.cs`、`CodexAppServerBackend.cs`、`OmpRpcBackend.cs`、`ConnectionState.cs`。名称是建议，实际文件与证据索引必须同步。

```csharp
// 用已验证的真实可执行入口；若安装只有 .cmd/.ps1，解析并验证它指向的
// node.exe + CLI entrypoint，分别传 ArgumentList，不靠拼接 cmd /c 或 powershell -Command。
var start = new System.Diagnostics.ProcessStartInfo(verifiedExecutable)
{
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    WorkingDirectory = isolatedEmptyWorkDirectory
};
foreach (var argument in verifiedArguments) start.ArgumentList.Add(argument);
// 进程仅在许可有效的显式连接/首次已选通道请求后创建；必须保存所有权。
```

两个 read loop 同时消费 stdout/stderr，避免 stderr 堵塞导致假超时。stdout 按 UTF-8 增量解码与换行组帧，设置最大帧/累计 bytes、有限 Channel 队列；达到上限显式失败并取消，不静默丢字符。写入由单队列串行化；每条请求有 id→TCS，RunContinuationsAsynchronously；断线完成全部 pending，清理 finally。

系统参数只用固定白名单；用户只选路径、模型、提供方，不接受任意额外 shell 参数。原文、规则、图片或 Token 不进入 command line/env/log。工具自己的认证环境仅在确需兼容时按范围继承；不要清掉导致登录失效后复制 Token 补救，也不要自动继承不相关 agent/MCP/调试配置。

保留单个已选 backend 的暖进程可以减少启动成本；初始闲置退出为 5 分钟，须分别测父进程+子进程 PrivateBytes/CPU。复用进程不复用上下文：每次翻译建立新会话；不读取/恢复用户历史对话，不让上一段不同请求进入下一段。关闭网络、断开、应用退出时释放自己的进程；崩溃可恢复连接，但不自动重发已提交翻译。

安全边界：翻译原文可能包含恶意命令。必须在有效配置层关闭工具/文件读取/执行/插件/MCP/额外提示词和后台任务；readOnly 不是禁读文件、拒绝工具批准也不是禁用无需批准的工具。空工作目录和“不要调用工具”的 Prompt 都不是安全证明。无法验证禁用能力时禁用该 backend，保留已改善的 API 路径。

## 10 P07 Codex app-server 适配

执行者先对支持的安装版本获取离线 `--version`、`app-server --help` 与 `generate-json-schema`（仅确认该版本为离线操作后运行），固定 schema 与版本/文件哈希。根据本地 schema 实现，不能照网上示例猜字段兼容。

协议实现：initialize→initialized→账号查询→所选模型→独立 thread→turn→delta→terminal。账号状态检查、模型目录、生成请求均不并行污染 UI；方法名、错误码、序列匹配按 schema。

最小握手示意：

```json
{"id":1,"method":"initialize","params":{"clientInfo":{"name":"popglot","title":"PopGlot","version":"当前产品版本"}}}
{"method":"initialized"}
{"id":2,"method":"account/read","params":{"refreshToken":false}}
```

请求 id=1 成功后才发送 initialized，再查询账号。thread/start/turn/start 的具体字段由该安装版本 schema 生成并测试；不在这里把固定“最新模型”写死。使用实际 threadId/turnId 关联流事件；只消费 agent message 的译文内容，不显示 reasoning、tool output 或启动日志。

映射到共享 TextFirstAssembler；turn/completed 的成功终态才能完成，失败/中断即 Failed/Partial/Cancelled。取消用对应 turn 的 interrupt，并按 session/epoch 拒绝晚事件；断流或处理失败后不自动重发。

托管 auth 由 Codex 处理。不得使用 chatgptAuthTokens 路径读取其他应用 Token，不调用用户全局 logout，不改其全局 config。需要会话不留痕时验证该版本是否支持真正 ephemeral；不支持则不得声称“不会保存会话”，把存储影响提示给用户或禁止该连接。PopGlot 只清理自己能可靠归属的会话，不遍历/删除外部历史。

**实施门槛：**按安装版可用配置显式禁用 shell、文件读取、MCP、skills/plugins、额外规则、后台能力与非必需网络请求，验收有效值与实际事件/文件/网络副作用。不能把 dynamicTools=[] 当成禁用内置工具；不能把 approvalPolicy=never 当作“所有工具被拒绝”。必要时采用独立配置覆盖或本机权限边界，但不得破坏工具认证。若现有版本无法实现所需翻译隔离，P07 标不放行并在 UI 解释，API 修复继续交付。

离线/fake 验证：乱序回复、未知 id、快速 terminal、错误先于 delta、碎片 JSON、两次快速取消/提交、进程中途退出、版本字段缺失与 tool-call 注入。必须证明任何工具事件不会交由 PopGlot 执行；但事件抵达后的拒绝不能证明内置工具之前没执行，需要有效配置与副作用负例一起验证。

真实验证由用户在本机主动连接和测试；仅发送固定无敏感文本的一次最小翻译，明确会消耗所选账户额度。保存脱敏结果状态、模型与耗时，不保存账号或完整流。重复 A/B 基准请求应单独获准，不因“一次测试”默许几十次调用。

## 11 P08 OMP 适配与 Claude 边界

OMP 是自己的 JSONL RPC，不能把 Codex JSON-RPC 解析器照搬。版本固定后对照该版本 wire schema/源码；验证 ready、prompt acknowledgement、delta、终态和取消。发送类型如 prompt/abort 等必须按实际 schema，不能靠中文文本解析结果。

启动参数候选为 `--mode rpc --no-ui --no-session --no-tools --no-lsp --no-pty --no-extensions --no-skills --no-rules`；这是需本地 help 验证的候选，旧版不支持要明确报版本不兼容。还须显式关闭 MCP、额外上下文/自定义工具、缓存预热/自动副调用，不能认为 --no-tools 禁掉了所有扩展或 MCP。不透传 `-e`，不在真实仓库目录启动。

prompt acknowledgement 不代表成功；按实际版本的完成/settled 语义结束，错误不能因普通 agent_end 被判 Completed。新请求独立 session，无 session 文件时也验证全局日志/诊断是否留文本。RPC 与 SDK 都可接入，但初版选进程隔离的 RPC，避免在 WPF 内引入 Node/Bun 运行时与额外认证存储。

OMP 的多 provider 登录只能在用户选定 provider 后检查；若该版本没有安全的非敏感登录状态接口，显示“状态待请求验证”，不能读 agent.db 猜。上游 OAuth 是否允许第三方产品路由必须分别确认，支持目录不是授权证明。Anthropic 订阅路由按 P05 限制禁用；不会因为由 OMP 发请求就变成可放行。

Claude Code 官方支持程序化调用，但本产品场景的订阅凭据限制另受官方规则约束，因此本步只交付明确的 Unsupported 状态与“配置 Anthropic API”入口。个人研究验证不混入产品可用列表；无明确官方变化不新增订阅后端。[Claude Code 程序化接口](https://code.claude.com/docs/en/headless)

## 12 P09 连接设置与完整用户流程

在 `Sections/ServicesSection.xaml(.cs)` 增加两个入口：“使用 API”和“连接本机工具”。沿用现有独立编辑页、返回/保存/草稿机制，避免一打开就扫描账号、启动 agent 或自动登录。

初次流程：检测工具→选择 Codex/OMP→查看提供方与数据提示→连接→需要时在工具内登录→刷新状态→选择模型→主动测试→保存→选择为文字引擎。文本“已连接”与“测试成功”分开；连接变更/模型变更后测试指纹失效。不要让第一个被检测到的工具自动替换现有引擎。

| 状态 | UI 行为与恢复 |
| --- | --- |
| 未安装/入口失效 | 选择已安装工具路径或继续使用 API；不自动下载安装 |
| 未登录/过期 | 引导在对应工具完成登录，保留设置和当前译文，提供重新检查 |
| 启动/检查中 | 小范围 loading，可取消，不锁住整个设置窗口 |
| 无权使用模型/限流 | 显示准确类别，保留可读结果；仅用户主动重试/换模型 |
| 版本不兼容/最低权限未满足 | 此连接不可用，展示简短原因；继续 API |
| 推理中断/CLI 崩溃 | 保留 Partial、给重新连接与手动重试；不写完成历史 |
| 网络关闭/安全离线 | 所有远程 CLI 禁用；不假装是本机模型 |
| 用户断开 | 仅关闭 PopGlot 的连接，不退出工具原账号 |
| 切换引擎 | 已开始会话保持冻结引擎/规则；新请求用新引擎 |
| 取消/恢复/展开 | 取消后旧事件无效；恢复/展开使用已有结果，不自动重发 |

默认设置页显示“工具、模型、状态”，详细可展开显示路径、版本、上游提供方、测试时间与诊断 ID；原始 Token、内部 protocol 字段、毫秒级性能拆分不进入普通用户流程。使用现有 13 DIP 左右正文、6/10 DIP 圆角与紧凑间距，不重设计整个软件。

隐私页同步说明本机工具只是请求入口，实际模型可能远程；内容仅发送到用户选择的通道，存在各工具自己的日志/代理/额度规则。连接、测试、断开、离线、过期都做键盘和读屏检查。

## 13 P10 验证 独立复查与交付

### 13.1 回归矩阵

| 层 | 必须证明的行为 | 证据 |
| --- | --- | --- |
| 纯逻辑/配置 | API 旧配置、未知 backend、clone/fingerprint、版本和 capability 路由 | PureTests + Rust 单测 |
| 协议与传输 | 客户端真实连接复用、每次凭据、TLS/重定向/LAN/尺寸门禁、文本协议保真 | 回环 HTTP + Rust/FFI fixture |
| 进程与事件 | 不执行输入命令、工具隔离、有限队列、崩溃、取消/晚事件、重连不重发 | fake CLI、文件/网络负例、子进程资源证据 |
| WPF | 鼠标/键盘焦点、IME、首屏、复制/读屏、关闭/恢复/切换 | LogicTests + 当前真机操作/截图 |
| 性能 | 热/首用/长闲置、取词、TTFT/绘制拆分；父+子内存 CPU | 同机同条件 A/B 原始样本与 p95 |
| 真实账号 | 授权的提供方/模型确实完成固定文本请求、限额错误正确 | 用户主动执行的脱敏最小请求 |
| 包与升级 | 旧配置迁移、安装入口/CLI 版本变化、运行时缺失可恢复 | 当前 Release 包 SHA256 与实际升级 |

覆盖工作台、划词、查词、截图 OCR 文本、要点、资料库、设置、隐私、托盘恢复。CLI 视觉能力初版不放行，需要图像时维持原视觉 API/本机 OCR 路由与权限；无文字模型不能静默发送截图到 CLI。

断言离线态 fake CLI spawnCount=0、promptCount=0；暖进程存在时撤权不再提交；取消/切换后旧 epoch 的 delta 不进新结果；结果可见后重连不重发；首屏不调用模型目录/健康探测；图像从不进入 CLI stdin/args/temp；原文中 `$(...)`、反引号、shell 路径只能作为数据且无副作用。

压力验收连续开关/请求/取消 200 次，跟踪事件订阅、handles、进程数量、PrivateBytes、挂起请求和 FFI job registry，允许稳定缓存，但不能单调增长。应用退出等待原有持久化队列、释放自有子进程，不能终止用户 CLI 或遗失历史。

真实提供方对照：同任务、语言、prompt 语义、输出要求、相同可选模型、网络条件与配额；API 与 CLI 的冷/暖进程分开、顺序随机化、失败也计入。无法同模型/同接入域名时标注混杂变量，仅报告体验比较。权限、内容隔离、成本和 p95 分别判断，不用某个快样本决定默认引擎。

### 13.2 执行命令

以下由实施者在确认隔离与进程状态后执行，本轮没有执行这些构建/产品测试。

```powershell
Set-Location D:\Projects\GitProjects\PopGlot
git status --short
git diff --stat
Get-Process PopGlot -ErrorAction SilentlyContinue
dotnet run --project tests/PopGlot.Windows.PureTests/PopGlot.Windows.PureTests.csproj --configuration Debug
cargo test --workspace --locked
git diff --check
```

完整门禁：`powershell -NoProfile -File scripts/verify.ps1`。其中 LogicTests 对运行中的 PopGlot 有隔离保护；冲突时保留用户进程，运行允许的纯测试，记录受阻部分并等待用户自行关闭，不能删除 guard 或强杀进程。

包验证：先检查 `scripts/publish-package.ps1` 的当前输出/覆盖与构建行为，创建隔离测量包后使用 `scripts/measure-startup.ps1 -Runs 30 -MeasureMemory`。该脚本测启动到托盘，不能代替唤醒和真实首帧；新增 measure-wake 后报告两类数据。命令不可被当作自动联网模型 benchmark 的授权。

### 13.3 独立复查清单

实施者完成后冻结 HEAD/dirty patch 与产物哈希，复查者重新读实际 diff、实际日志和原始样本，不照抄实施报告。重点寻找反例：

1. 所有鼠标移入/点击都不会为整块读写区域画蓝框，真正 Tab 导航仍有定位；普通表单/选中/错误状态未被误删。
2. 唤醒数字起点确实是热键/托盘入口，第一次构造与长闲置均测；首帧是定义明确的指标，未知时间点没有填 0 伪装通过。
3. 工作集策略与 HTTP 复用确实影响生产路径；HistoryStore.Load 未被误报为每次读盘；没有把低 working set 当作内存泄漏修复。
4. 任何新增 HTTP/client 缓存都没有缓存凭据、许可或宽松 TLS；每次请求重查原安全边界。
5. CLI 不绕开网络、敏感输入、图像与外部执行许可；实际 tool/config 隔离有证据，readOnly/never 等字段没有被误解。
6. Codex/OMP 认证由工具管理；没有扫描/复制 Token，不退出用户原账号；Claude 订阅路由没有绕道放行。
7. 源文本是独立数据，新 backend 共用保护、Prompt、分段和元数据契约；history executor/status 真实，无假 200/假本地标签。
8. 进程暖而会话不串、重连不重发、取消晚事件不污染，恢复/展开零重发。
9. 明确未测旧机器、真实账号、特定 provider、Windows 隔离/真实呈现/升级的范围；mock 与绿色 build 没有冒充这些证明。

输出 `IMPLEMENTATION-REPORT.md` 与 `INDEPENDENT-REVIEW.md`，存于本目录，每项列通过/不通过/未验证、证据路径和剩余动作。没有新任务授权时不启动其他聊天代实施或复查；用户指定实施者后交出本文件和 EXECUTOR-PROMPT。后续独立复查应针对实施后的实际代码，不能以本轮设计复查替代。

### 13.4 失败与回退

每个步骤用独立、可审查 patch/提交组织；是否提交遵循用户授权。回退仅撤销本步，不 reset --hard、不删无关文件、不改第三方配置。P01/P03/P04 可分别回退；P05 迁移失败时保留原配置副本并报告，不把 unknown backend 写成 NativeApi 后静默发送。

Codex/OMP 如无法满足版本/最低权限/出网/上游授权要求，适配器保持 disabled/unsupported，完整交付已验证的 UI 与 API 优化；这属于明确未放行，不等于三种 CLI 接入已全部完成。已提交请求失败后由用户主动重试，不切另一账号、协议、模型或免费线路。外部服务和旧机器验收受阻时继续可完成工作，最终保留需要用户条件的具体项目。

## 14 最终交付要求

提交给用户：改动与原因、可复现前后对照、当前 UI 截图、分阶段性能原始样本/摘要、fake/real provider 分离结果、配置迁移和撤权负例、安装包身份（若实际构建）、独立复查结论、剩余缺口。

产品默认选择以可靠性、权限与体验为依据：API 优化后可继续是默认，CLI 是可选连接；只有实测支持且所有边界放行时再讨论改默认。不得承诺“账号复用天然更快、更安全、额度无限”，不得宣称本机 CLI 等于离线模型。
