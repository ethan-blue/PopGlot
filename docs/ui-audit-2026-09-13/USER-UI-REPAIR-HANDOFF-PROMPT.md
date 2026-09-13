# PopGlot 用户原始 UI 要求返工：执行 Prompt

> 使用方式：把“Prompt 正文”完整交给负责实现的 AI。不要只摘取任务列表。
> 本文针对当前未提交工作树，不代表任务已经完成。

## Prompt 正文

```text
你负责修复 PopGlot 当前工作树中“文档声称已完成、真实界面却没有按用户要求完成”的问题。

仓库：D:\Projects\GitProjects\PopGlot
技术栈：.NET 10 WPF 前端 + Rust Core/FFI
当前分支：main，HEAD 仍为 21f0132（v0.1.4）

这不是继续写审计文档的任务。最终交付必须是可运行的产品代码、针对真实状态的自动测试、实际 WPF 页面证据和一个身份明确的最新构建产物。不得用“XAML 里出现过按钮”“编译通过”“测试数量很多”代替用户路径验收。

## 一、开始前必须确认的事实

1. 先运行 `git status --short --branch` 和相关 `git diff`，保留工作树中所有现有修改。当前存在大量未提交修改和未跟踪文件，它们属于用户，不得覆盖、回退、清理或重写。
2. 禁止执行 `git reset --hard`、`git checkout --`、`git clean`，禁止删除现有 `corrupt-*` 数据备份。
3. 不提交、不 push、不创建发布标签，除非用户之后明确要求。
4. 不结束用户当前运行的 PopGlot。构建到独立输出目录，UI 验证使用临时 `POPGLOT_DATA_ROOT` 和内存凭据库，不读取真实 API Key，不调用付费或真实第三方服务。
5. 不相信以下文档中的“已完成/lead 复核”标签；只把它们当问题线索：
   - `docs/ui-audit-2026-09-13/README.md`
   - `docs/ui-audit-2026-09-13/settings-services.md`
   - `docs/ui-audit-2026-09-13/windows.md`
   - `docs/review-2026-09-12/EXECUTION-LEDGER.md`
6. 本轮调查开始时用户运行的是 `artifacts/ui-preview-2026-09-13/PopGlot.exe`（18:03 的旧 DLL）；随后实际进程已切换为普通 Release 目录中 19:27 的 DLL（SHA-256 `A2F3A53D5ECAEB7B3977B98F1525E207C69C2738AFDC1395D396E65B9932C722`）。这证明旧产物曾造成显示落后，但本 Prompt 所列两项在当前源码/最新进程中仍然存在，不能再把它们归因于旧进程。验收时必须重新读取进程路径，不能沿用这里的快照。

## 二、用户要求，不得改写含义

### R1：主页面必须明确显示“尚未配置自己的引擎”，并提供真正可点击的配置入口

当前问题：

- `Sections/TranslateSection.xaml` 虽然已经添加 `GoToSettingsButton`，但 `TranslateSection.xaml.cs::HasUsableService()` 把“内置公共翻译已获授权”也当成“用户已经配置翻译引擎”。
- 用户真实状态是 `product-config.json` 中 0 个 Profiles，同时 `FreeEngineConsent=Allowed`。现有代码因此隐藏 `UnconfiguredGuidePanel`，用户看不到配置入口。
- 现有判断只检查 `SupportsText + TextModel`，没有检查 Base URL、云端凭据等实际可执行条件；不完整的 Profile 也会错误隐藏修复入口。应复用或抽取 `ProfileManager` 的统一 readiness 判定，禁止出现两套“是否可用”的规则。
- 主窗口重新激活时目前只刷新底部引擎摘要；从非模态设置窗口保存或返回后，主工作台空态可能继续显示旧状态。必须补齐显式刷新路径。
- 文档把“假按钮”主要归因到服务编辑页的 `EvidenceBadge`，修错了对象；用户说的是主工作台没有清楚、真实、可点击的添加引擎入口。

目标行为：

1. 分开两个概念，禁止再用一个 `usable` 布尔值混在一起：
   - `hasConfiguredUserEngine`：是否存在完整、可执行的用户引擎。
   - `hasFallbackRoute`：是否允许使用内置公共翻译。
2. 只要用户引擎为 0，主工作台必须显示一个简洁的引导区域；即使内置公共翻译可用，也不能隐藏。
3. 免费引擎已允许时，文案应如实表达，例如：
   - 标题：`当前使用内置公共翻译`
   - 说明：`添加自己的翻译引擎，可使用指定模型和服务商。`
   - 主动作：`添加翻译引擎`
4. 免费引擎未允许且没有用户引擎时，文案应为：
   - 标题：`尚未配置翻译引擎`
   - 说明：`添加翻译引擎后即可开始使用。`
   - 主动作：`添加翻译引擎`
5. 这个动作必须是 WPF `Button`，视觉上符合 PrimaryButton，拥有 Hand 光标、可见的 Hover/Pressed/Focus/Disabled 状态和 `AutomationProperties.Name`。
6. 点击必须真实打开 `SettingsWindow` 并直接定位“翻译引擎”页/新增引擎流程；不能只是更新状态文字，也不能要求用户再找一次“添加引擎”。
7. 主页面不能同时显示互相冲突的“尚未配置”和“就绪/等待输入”。免费线路可用时可以翻译，但仍要明确“尚未配置自己的引擎”。
8. 不得重新引入首次使用弹窗、自动出网、自动探测或隐式授权。
9. 设置保存、删除或补全引擎后，返回/激活主窗口必须立即重算空态，无需重启、重新导航或开始一次翻译。

主要文件：

- `apps/PopGlot.Windows/Sections/TranslateSection.xaml`
- `apps/PopGlot.Windows/Sections/TranslateSection.xaml.cs`
- `apps/PopGlot.Windows/MainWindow.xaml.cs`
- `apps/PopGlot.Windows/SettingsWindow.xaml.cs`

### R2：路由切换只保留一个用户入口，删除重复和啰嗦的路由区域

当前存在三套入口：

1. 主窗口底部 `EngineHealthButton` 快速切换菜单。
2. 设置 → 翻译引擎页上方 `RoutingPanel`，包含 `DefaultTextCombo` 和 `DefaultVisionCombo`。
3. 单个引擎编辑页内 `SetDefaultButton`（设为文字默认）。

用户已经明确要求：既然路由切换放到主窗口底部，其余重复路由操作就删除，不要继续在设置页上方展示“默认路由/即时生效/文字/图片”等啰嗦区域。

目标行为：

1. 主窗口底部快速切换器是唯一的路由修改入口。
2. 从 `ServicesSection.xaml` 删除整个 `RoutingPanel`，不是设置 `Collapsed`，也不是仅在 0 Profiles 时隐藏。
3. 删除 `DefaultTextCombo`、`DefaultVisionCombo`、`VisionIncompatHint` 以及只为它们存在的布局、刷新和 SelectionChanged 代码。
4. 删除编辑页中的 `SetDefaultButton` 和相应点击处理器，避免第二个隐蔽的路由切换入口。
5. 引擎列表可以保留只读的“当前”状态徽章，但它只能展示状态，不能承担切换动作，也不能长得像可点击按钮。
6. 新建第一个完整可执行的文字引擎时可以继续按现有合同自动成为初始默认值；后续引擎保存只负责保存，不暗中改路由。后续切换统一从主窗口底部进行。
7. 删除设置页重复入口时，不能破坏 `ActiveProfileId`、`VisionProfileId`、`PreferFreeEngine` 的持久化和运行时应用逻辑；这些领域字段继续保留，只是 UI 写入口收敛为一个。
8. 主窗口底部摘要只显示用户做决定所需的最少信息：状态点、当前文字引擎名称、展开箭头。不要常驻拼接“未检测”“截图会发送到……”“本地 OCR”等长句。
9. 详细健康状态、图片线路和隐私说明可以放在展开菜单、Tooltip 或对应设置页的只读说明中，但不能占据主窗口常驻状态栏。
10. 快速切换菜单必须清楚区分“文字引擎”和“图片引擎”，当前项只出现一次，并保证切换防重入、保存失败可见、成功后即时刷新。
11. 更新现有反向测试：`tests/PopGlot.Windows.LogicTests/Program.cs` 当前约 5692～5701 行明确要求 `DefaultTextCombo`、`DefaultVisionCombo`、`RoutingPanel` 存在，它锁定了与用户要求相反的设计。必须改成运行时负断言，而不是简单删除测试。
12. 当前 `EngineHealthButton_Click` 打开菜单后会自动调用 `UpdateFreeEngineHealthAsync(force: true)` 并产生网络探测。打开路由菜单只能读取本地状态，不能隐式联网；免费引擎检测必须改成菜单内独立、明确的“重新检测”动作，并受既有出网授权门禁约束。

主要文件：

- `apps/PopGlot.Windows/MainWindow.xaml`
- `apps/PopGlot.Windows/MainWindow.xaml.cs`
- `apps/PopGlot.Windows/Sections/ServicesSection.xaml`
- `apps/PopGlot.Windows/Sections/ServicesSection.xaml.cs`
- `apps/PopGlot.Windows/Services/ProfileManager.cs`

### R3：把此前大量 MD 的声明重新映射到真实产品，不再扩写空泛文档

当前两个审计目录共有约 11 个 Markdown 文件、4,144 行，但主窗口 XAML 的可见差异只有少量响应式/字号修改。必须承认“文档很多”和“用户可见改动完成”不是一回事。

执行要求：

1. 从用户可见结果出发，建立一张紧凑的需求矩阵；每项只能是 `通过 / 不通过 / 未验证`。
2. 至少重新核查：主页面空态、添加引擎入口、服务列表、引擎编辑、唯一路由入口、主窗口状态栏、保存/取消、错误恢复路径、窗口窄宽布局、主题切换、键盘焦点与无障碍名称。
3. 文档中原有“已完成”不能继承。只有本轮真实测试证据覆盖到的项目才能改成“通过”。
4. 不要再创建大篇幅产品规划、角色扮演评审或重复执行日志。优先改代码与测试；最终只写一份短结果表。
5. 如果发现用户历史要求在文档中被错误转义或遗漏，按当前用户明确表述优先，不得以旧文档反驳用户。
6. 特别核对文档内部冲突：UI 总览批量标记“已修复+lead复核”，但总台账仍把 UI07 集成视觉验收记为 `SPECIFIED_NOT_IMPLEMENTED`。在得到真实 GUI 证据前，不能沿用前者。
7. 实现者不得给自己的工作标记“独立复核”“lead 复核”或 `DONE_VERIFIED`；实现者最多提交 `READY_FOR_REVIEW`，最终复核必须来自另一个明确身份。

## 三、依赖顺序

严格按以下顺序执行，前一步未通过不能宣布后一步完成：

1. 保存基线证据：git 状态、相关 diff、当前进程路径、目标构建目录。
2. 写失败用例：先证明 `0 profiles + FreeEngineConsent.Allowed` 下当前入口被隐藏，并证明当前源码存在多套路由 UI。
3. 修复 R1 的状态模型与真实点击导航。
4. 修复 R2，删除设置页和编辑器重复路由入口，清理孤儿处理器。
5. 精简主窗口底部摘要与切换菜单，但不改变网络/隐私合同。
6. 运行聚焦测试。
7. 运行 Windows 全量逻辑测试、纯逻辑测试和 Rust 门禁。
8. 构建到全新的独立 Release 输出目录。
9. 使用隔离数据根进行真实 WPF 页面验收，收集控件可见性、可点击性、窗口标题、进程响应和 0 crash log 证据。
10. 最后才允许更新一份结果文档；失败项必须保持“不通过/未验证”。

## 四、必须新增或更新的自动测试

测试不得只搜索源码字符串，能实例化 WPF 控件的项目必须验证真实控件状态和事件结果。

### T1：免费引擎已允许，但仍未配置用户引擎

隔离配置：

- `Profiles=[]`
- `ActiveProfileId=""`
- `PreferFreeEngine=true`
- `FreeEngineConsent=Allowed`

断言：

- 引导区域可见。
- 文案明确当前使用内置公共翻译，同时提示添加自己的引擎。
- `GoToSettingsButton` 可见、启用、命中测试开启。
- 点击一次只触发一次打开设置回调，并落在翻译引擎/新增流程。
- 翻译功能是否可用与“添加自己的引擎”入口是否可见是两个独立断言。

### T2：完全无可用线路

隔离配置：0 Profiles，免费引擎为 Unset 或 Denied。

断言：显示“尚未配置翻译引擎”和真实按钮；状态栏不得显示“就绪”；不产生网络请求。

### T3：已有完整用户引擎

使用内存凭据和假 Provider，不读取系统凭据。

断言：未配置引导不再抢占结果区；底部切换器显示当前引擎的简短名称；菜单当前项唯一。

补充反例：只有一个不完整云端 Profile（缺模型、Base URL 或必需凭据）时，不能伪装成完整可用；页面必须保留“继续配置/完成配置”的真实修复入口。

### T4：路由入口唯一性

断言：

- `ServicesSection` 视觉树中不存在 `RoutingPanel`、`DefaultTextCombo`、`DefaultVisionCombo`、`SetDefaultButton`。
- 设置页没有任何可以改变默认文字/图片路由的控件。
- 主窗口底部菜单是唯一可改变 `ActiveProfileId`、`VisionProfileId` 或 `PreferFreeEngine` 的用户操作入口。
- 仅打开快速切换菜单不得改变配置，也不得发出任何网络请求；免费引擎检测必须由用户明确点击独立动作。
- 保存第二个引擎不会静默切换当前路由。
- 保存第一个完整文字引擎仍能得到一个有效初始路由。

### T5：交互和布局

至少验证 100%、150%、175% 缩放或等效 DIP 尺寸：

- CTA 不被裁切、不与示例按钮重叠。
- 底部引擎摘要有合理最大宽度和省略号，不把左侧状态挤没。
- Tab 能聚焦 CTA 和底部切换器；Enter/Space 可触发。
- Hover、Pressed、Focus、Disabled、当前项状态肉眼可区分。
- Light、Dark、High Contrast 下文字可读。
- 对关键状态保留前后截图，并注明窗口尺寸、DPI、主题、夹具状态、EXE 路径与 DLL SHA-256；设计稿或源码截图不算运行证据。

### T6：旧问题回归

- 设置窗口构造和关闭无 `XamlParseException`、无半初始化 `NullReferenceException`。
- 打开/关闭设置不产生消息风暴。
- 不恢复 ApplicationIdle 构造完整隐藏翻译窗的预热方案。
- 不把配置、历史或词库写回真实用户目录。

## 五、验证命令

根据仓库当前项目名称执行；任何一项失败都必须如实记录，不能跳过后宣布完成。

```powershell
git status --short --branch
dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -p:OutputPath=bin/UserUiRepair/
dotnet run --project tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/UserUiRepair/
dotnet run --project tests/PopGlot.Windows.LogicTests -c Release -p:OutputPath=bin/UserUiRepair/
cargo fmt --all -- --check
cargo test --workspace --locked
cargo clippy --workspace --all-targets --locked -- -D warnings
```

如果运行中的 PopGlot 锁定默认输出，不要结束它；继续使用独立 `OutputPath`。PowerShell 调用外部命令后必须检查退出码。

当前 LogicTests 和真实 `PopGlot.exe` 受单实例边界约束。若用户实例仍在运行，测试按设计返回环境错误码 3 或真实外部 GUI 无法启动时：不得杀掉用户进程、不得绕过生产单实例保护后假装通过。先完成不冲突的构建与静态检查，把外部 GUI 和受保护全量测试标为“未验证”，等用户明确关闭实例后继续。若新增测试专用宿主或实例 ID，它必须只在显式测试进程中生效，不能削弱普通生产启动的单实例合同。

真实 WPF 隔离启动必须：

- 创建新的临时数据目录；
- 设置 `POPGLOT_DATA_ROOT` 指向该目录；
- 使用内存凭据；
- 禁止真实发送；
- 启动本轮新构建而不是 `artifacts/ui-preview-2026-09-13`；
- 记录实际进程 `ExecutablePath`、`PopGlot.dll` SHA-256 与构建时间；
- 不得只核验 `PopGlot.exe`：framework-dependent apphost 在不同构建中可能完全相同，产品代码身份必须以 `PopGlot.dll` 或完整构建 manifest 为准；
- 检查进程 Responding、目标控件状态和隔离日志目录中 0 个新 crash。

## 六、禁止事项

1. 禁止只改 MD 或只更新完成状态。
2. 禁止用 `Visibility=Collapsed` 冒充删除重复路由。
3. 禁止把免费公共翻译等同于“已配置自己的引擎”。
4. 禁止新增首次使用弹窗、自动联网、自动模型探测或隐式授权。
5. 禁止读取、打印、复制或保存真实 API Key、Cookie、令牌。
6. 禁止真实调用付费 Provider；使用 fake sender、loopback 或内存替身。
7. 禁止为了让测试通过而削弱断言、仅做字符串测试或删除失败用例。
8. 禁止停止当前用户进程、覆盖旧预览目录或直接替换用户正在运行的文件。
9. 禁止顺手重构无关模块；仅处理本 Prompt 范围和由此产生的必要编译修复。
10. 禁止声称“发布完成”；签名、安装、快捷方式切换和用户真实配置验收没有证据时必须标记未验证。

## 七、最终交付格式

最终回复先给结论，再给证据。必须包含：

1. 修改文件清单及每个文件承担的行为变化。
2. R1、R2、R3 与 T1～T6 的 `通过 / 不通过 / 未验证` 表。
3. 每条测试命令、退出码、通过/失败数量。
4. 实际启动的 EXE 路径、DLL 时间和 SHA-256，证明不是旧预览。
5. 隔离 WPF 验收的配置状态、可见控件、点击结果和 crash 日志数量。
6. 关键状态的运行时截图与控件树摘要；点击前后必须来自同一个已标识构建。
7. 剩余风险与明确禁止发布的条件。
8. `git status --short`，证明没有回退或覆盖用户原有改动。

只有以下条件同时满足，才能说本任务“完成”：

- 0 Profiles + FreeEngineConsent.Allowed 的真实 WPF 页面仍显示可点击的“添加翻译引擎”；
- 点击后直接进入新增引擎流程；
- 设置页重复 RoutingPanel 和编辑器设默认入口从代码与视觉树中删除；
- 主窗口底部是唯一的路由修改入口；
- 最新独立构建通过聚焦测试和全量门禁；
- 隔离 GUI 无崩溃、无消息风暴；
- 没有把旧预览或旧进程当成最新验收对象。
```
