# PopGlot 商业产品全量评审与执行总纲

> **最终统一入口**：[README](README.md)。冲突裁决、授权边界和任务状态以该入口及 [EXECUTION-LEDGER](EXECUTION-LEDGER.md) 为准；自定义翻译 Prompt 与 GLM5.3flash 接力见 [专项合同](PROMPT-PERSONALIZATION-AND-GLM-HANDOFF.md)。本文件保留第一轮证据与 C 任务，不是自动开工指令。

> **第二轮复审入口**：[功能优化、新增能力与交互规格补全](PRODUCT-EXPANSION-REVIEW-V2.md)。用户已明确：本轮只评审、补全文档，不执行实现。本文的任务指令只供未来收到实施授权后使用。第二轮文档的纠错、产品决策和优先级覆盖本文冲突表述；第一轮测试数字是历史快照，本轮未重新运行测试。

> 评审日期：2026-09-12
> 评审对象：当前 `main` 工作树（含未提交修改）
> 当前版本：0.1.4
> 结论：**不建议作为完整商业产品发布；可继续作为内部预览版迭代。**
> 本文用途：交给能力较弱、上下文较短的实现模型逐项执行。本文是任务合同，不是灵感清单。

## 0. 一句话产品目标

PopGlot 应成为一个“按下快捷键就可靠得到结果、不会误关、不会泄露或损坏用户内容、安装后长期不用维护”的 Windows 翻译工具，而不是一个需要用户理解 Provider、Endpoint、路由和托盘机制的开发者面板。

## 1. 本次评审范围与边界

本次覆盖：

- 安装、首次启动、开机启动、单实例、托盘、主窗口关闭与真正退出。
- 划词、截图、手动翻译、极速查词四个入口及其取消、重试、失焦和恢复。
- 翻译保真、隐私授权、日志、历史、生词、导入导出、错误恢复。
- 主工作台、浮窗、极速查词、设置、服务编辑器的布局、层级、状态和文案。
- 启动、空闲、交互、流式渲染和大数据量下的卡顿风险。
- Release、CI、便携包、签名、更新、卸载、文档和商业交付能力。

本次没有：

- 修改产品实现、用户配置、凭据、历史或词库。
- 关闭当前正在运行的 PopGlot 进程。
- 重启 Windows 或执行真实登录启动测试。
- 调用真实收费模型、上传用户文本/截图、发布、打标签或推送。
- 引入遥测、账户、云同步或本地大模型运行时。

## 2. 证据与当前基线

### 2.1 已执行证据

| 检查 | 结果 | 可以证明什么 | 不能证明什么 |
|---|---|---|---|
| Rust workspace 测试 | 180 passed | Core、Provider、SSE、FFI 的既有自动化契约通过 | 真实 GUI、真实供应商、Windows 登录启动 |
| WPF Release build | 0 warning / 0 error | 当前工作树可生成 Release | 安装包可用、运行时不卡顿 |
| `verify.ps1` | 失败 | 检出了 Debug FFI DLL 被真实 PopGlot 占用 | 不能据此判定产品逻辑失败 |
| Release Windows 逻辑套件 | 155 passed / 13 failed | 大部分纯逻辑回归通过；隔离守卫发现 PID 32548 | 另有 12 项 WPF `Application` 生命周期失败；它们与真实实例冲突的因果关系尚未隔离 |
| 独立 Release DLL 探针 | 完成 | 复现了授权、日志、词库、主题、代码保真等生产方法反例 | 没有运行 `App.OnStartup`，不等于完整 GUI E2E |
| 2026-09-09 截图资产 | 人工查看 | 可评估静态层级、密度、文案、空态和错误态 | 不能证明点击、焦点、动画结束态和真实性能 |
| 当前进程只读采样 | Debug WS 118,849,536 bytes；Private 113,262,592 bytes | 当前真实进程接近 120 MiB 硬门槛 | 不是 Release、不是稳定 30 秒后的严格空闲样本 |

### 2.2 当前机器上的开机启动事实

- `%LOCALAPPDATA%\PopGlot\windows-shell.json` 中 `StartWithWindows=true`。
- HKCU `Run\PopGlot` 指向仓库 `bin\Debug` 下的 `PopGlot.exe`。
- 本次只读查询未取得同名 `StartupApproved\Run` 值。
- 因此“用户配置和 Run 项存在”已确认；“下一次登录确实不会启动”仍是**用户报告、未做重启复现**。
- 即使当前机器可启动，注册到 Debug/便携目录本身也不满足商业产品的稳定安装路径要求。

### 2.3 证据等级

- E3：真实打包 GUI / 真机 / 独立进程端到端证据，最强。
- E2：生产 Release DLL、隔离注册表/文件系统、回环服务探针。
- E1：单元测试、源码检查、离屏渲染截图。
- E0：注释、任务日志、“已完成”标签、主观推断。

任何任务不得用 E0 取代 E2/E3。UI 截图存在不等于交互合格；测试全绿不等于发布就绪。

## 3. 总体结论

### 3.1 值得保留

- 本地优先、显式网络权限、Token 保护、流式状态机和多 Provider 架构方向正确。
- Release 可构建，Rust 基础测试覆盖较广；取消后历史写入和伪提交状态已经修复。
- 主窗口已形成翻译/资料库/设置的基本信息架构，深浅主题色彩方向可以继续使用。
- 便携包自包含 .NET，适合作为测试渠道。

### 3.2 当前发布阻断项

以下任意一项未关闭，都不得给出“商业版可发布”：

1. 免费引擎授权是可复用快照，撤销和“仅本次”不能在最终发送边界生效。
2. 词库读取前缺少大小限制，异常加载后的写入保护需要补齐。第一轮超大 fixture 是合法空数组加空白，其重写证明读入上限失效，不能证明已有词条丢失；含真实词条的损坏/超限保护仍待验证。
3. 诊断日志按黑名单脱敏，仍可记录原文和非典型密钥内容。
4. 代码块复制丢失缩进、尾空格和空行，破坏程序员内容。
5. 开机启动依赖可移动便携路径，且当前“自愈”会尝试覆盖用户在任务管理器里的禁用选择。
6. 极速查词失焦无条件销毁，普通浮窗默认失焦关闭，构成高概率误触和结果丢失。
7. 没有签名安装器、稳定更新/回滚和全新账户验收链。
8. 真实 Release 性能、开机登录、混合 DPI、IME、Narrator 和真实打包主旅程未通过 E3。

## 4. 问题、风险与产品判断清单（证据范围见第二轮复审）

| ID | 严重度 | 状态 | 问题 | 当前证据 |
|---|---|---|---|---|
| F01 | P0 隐私 | 不通过 | 撤销免费引擎同意后，旧 authorization 仍可发送健康请求 | Release 探针 sends=1 |
| F02 | P0 隐私 | 不通过 | `AllowOnce` 可被翻译和健康检查重复使用 | Release 探针 sends=2 |
| F03 | P0 隐私 | 不通过 | 端点回退期间撤销授权仍继续下一端点 | Release 探针 sends=2 |
| F04 | P1 数据边界 | 上限不通过；词条丢失未验证 | 超大合法空数组可被读入并重写，缺读取前上限；不能从该样本推断已有词条丢失 | 33,554,434 bytes 变为 291 bytes；fixture 为 `[` + 空白 + `]` |
| F05 | P0 隐私 | 不通过 | 日志保留模拟原文和 `api_key=synthetic-secret-123` | `DiagnosticsLog.BuildEntry` 生产探针 |
| F06 | P0 保真 | 不通过 | fenced/unclosed code 的缩进、尾空格、空行被 `Trim`/`TrimEnd` 丢弃 | `MarkdownPresenter.ToPlainText` 探针 |
| F07 | P1 交互 | 不通过 | 极速查词窗口任何失焦都 `Close()`，无宽限、无恢复 | `QuickSearchWindow.Window_Deactivated` |
| F08 | P1 交互 | 不通过 | 普通浮窗默认 `ClosePanelOnFocusLoss=true`，固定状态弱、误触成本高 | 默认配置与 `OnDeactivated` |
| F09 | P1 主题 | 不通过 | 已渲染 FlowDocument 在深浅主题切换后保留旧冻结画刷 | 旧色 `#FFEEF0F4`，新 token `#FF15171C` |
| F10 | P1 启动 | 不通过 | 自启注册到便携/Debug 绝对路径，移动或升级后在再次手动启动前无法自愈 | 当前 HKCU Run + `EnsureRegistered` 调用时机 |
| F11 | P1 用户权利 | 不通过 | 代码直接改写/删除 `StartupApproved`，可能覆盖用户在任务管理器中的明确禁用 | `StartupRegistration.TrySetCore` |
| F12 | P1 测试 | 不通过 | 实例冲突守卫失败后继续运行；另有 12 个 Application 生命周期失败，两者因果关系尚未隔离 | 第一轮 155/13 输出 |
| F13 | P1 性能 | 未验证 | 当前 Debug WS 约 113.3 MiB，接近硬门槛；缺 Release 稳定采样和交互 P95 | 进程只读采样 |
| F14 | P1 产品 | 不通过 | 主工作台空态大面积留白、弱信息层级、顶部与底部状态重复 | `main_window_*` 截图 |
| F15 | P1 产品 | 不通过 | 服务编辑时“添加/设默认/验证/保存”多主动作竞争，用户不知道下一步 | `service_editor_*` 截图 |
| F16 | P1 错误体验 | 不通过 | 错误浮窗重复红色“未完成/错误正文/长技术错误”，首要行动不清 | `translation_panel_error_dark.png` |
| F17 | P2 推荐可信度 | 不通过 | 未来时间的 benchmark 样本仍匹配当前上下文 | Release 探针 future accepted=true |
| F18 | P1 保真 | 不通过 | 超长代码式 identifier 在 799 字符附近被硬切 | Release 探针分成 `..._` 与 `foo_bar_baz` |
| F19 | P1 商业依赖 | 待核实 | 公共翻译端点和浏览器 UA 已见于代码；仓库未提供商业授权及稳定性保证证据，不能仅凭 UA 判定违规或规避意图 | `FreeTranslateService.Endpoints` 与 User-Agent |
| F20 | P2 性能 | 风险 | 主窗 Activated 同步读 Core/Profile/Credential，历史/词库同步文件 I/O 可能造成间歇卡顿 | 源码调用链；尚缺 ETW/Profiler |
| F21 | P2 可靠性 | 风险 | 全局异常屏障将所有 UI 异常标记 Handled，可能让程序带病运行 | `App.DispatcherUnhandledException` |

### 已回归通过但仍需保留的防回归项

- 取消在 final 前发生时会话为 Cancelled，历史 0 条。
- 历史写失败时 `HistoryCommitted=false`。
- 生词 CSV 和历史 CSV 当前可被标准 CSV parser 正确解析并防公式注入。
- 当前 Run 键路径带双引号，包含空格时的基础格式正确。

## 5. 目标体验与产品规则

### 5.1 用户心智

- 主窗口右上角 X：默认隐藏到托盘，首次提供说明，之后不反复弹通知；设置和托盘持续提供恢复入口。退出行为详见第二轮窗口事件表。
- 默认从托盘或设置的明确入口真正退出；若用户配置主窗 X 为退出，同样进入统一退出守卫，处理在途任务和未保存草稿。
- 极速查词和翻译浮窗属于临时工作面，不应因为一次系统级失焦立即销毁内容。
- “固定”表示保持可见；“自动隐藏”表示可恢复的隐藏，不等于销毁会话。
- 开机启动页必须显示“期望状态、Windows 实际状态、当前启动路径、修复动作”，不能只显示一个乐观开关。

### 5.2 视觉方向

- 保留紧凑、原生、工具型桌面工作台；不要改成大量圆角卡片的通用 AI 控制台。
- 每个区域只允许一个主按钮。危险操作与主操作必须空间分离。
- 图标只用于高频且行业语义稳定的动作；低频动作使用文字或“更多”菜单。
- 正文、次要信息、禁用态、焦点态、错误态必须靠文本/形状共同表达，不只靠颜色。
- 420–720 DIP 使用紧凑侧栏或抽屉；不允许固定宽侧栏吞噬核心阅读区。

### 5.3 错误与恢复

每个错误界面只展示：

1. 一句用户语言的失败标题。
2. 一句“有没有发送、有没有保存、结果是否完整”的事实。
3. 一个主要修复动作和至多一个次要动作。
4. 折叠的技术详情，可复制但不得含秘密或原文。

## 6. 轻量模型执行规则（强制）

### 6.1 状态机

任务状态只能是：

- `TODO`：无人处理。
- `IN_PROGRESS`：正在实现，必须写执行者和开始时间。
- `READY_FOR_REVIEW`：实现者完成并附证据，不能自称通过。
- `BLOCKED`：有具体阻塞，写明已尝试方案和所需输入。
- `DONE_VERIFIED`：独立复核者按本文验收通过。

实现模型不得把自己的任务直接标成 `DONE_VERIFIED`。

### 6.2 每次领取任务前

1. 读本文对应任务的全部内容和直接依赖。
2. 执行 `git status --short`，不得覆盖不属于自己的修改。
3. 读相关 diff；工作树里的现有修改都视为用户资产。
4. 建立能检出旧错误的测试或探针，再改实现。
5. 只处理一个任务 ID；发现旁支问题写入“新发现”，不要顺手大改。

### 6.3 禁止事项

- 禁止自动 commit、push、tag、release、上传文件或调用真实付费 Provider。
- 禁止修改真实用户 `%LOCALAPPDATA%\PopGlot`、凭据管理器、真实历史/词库。
- 禁止在用户真实账户上测试开机项；使用临时 Windows 测试账户、VM 或可恢复的隔离适配器。
- 禁止通过放宽阈值、删慢样本、更新截图基线来“修复”失败。
- 禁止把 Debug、测试宿主、组件构造耗时写成 Release 应用指标。
- 禁止新增遥测、账号、云同步、自动上传和本地模型运行时。
- 禁止吞掉异常后返回成功；失败必须可见、可恢复、可诊断。
- 禁止复制旧报告的“已完成”结论；必须重跑当前生产路径。

### 6.4 每个任务的交付格式

在本文末尾执行记录追加：

```text
任务：Cxx
状态：READY_FOR_REVIEW / BLOCKED
根因：
改动文件：
新增或修改的测试：
执行命令与 exit code：
关键原始输出：
真机步骤与结果：
未验证项：
工作树中原有修改如何保留：
下一位从哪个文件/方法开始：
```

### 6.5 统一完成定义

一个任务只有同时满足以下条件才可独立验收：

- 旧反例先红后绿；至少包含一个失败/取消/竞争/边界用例。
- `cargo fmt/test/clippy`、WPF Release build、Windows Release 逻辑套件通过。
- 涉及 UI 时检查稳定动画结束态的深浅主题、窄宽、125/150/200% DPI 截图。
- 涉及真实 Windows 行为时完成 E3；环境不具备就保持“未验证”。
- 文案、隐私文档、架构和迁移说明同步。
- 没有新增真实出网、凭据明文、用户数据修改或未授权副作用。

## 7. 24 小时执行顺序

### 阶段 A：先阻止泄露、丢数据和误关（C00–C06）

此阶段未全部 `DONE_VERIFIED`，后续不得做发布、视觉大改或性能宣称。

### 阶段 B：启动、生命周期和卡顿（C07–C12）

先让产品每天可靠存在，再优化视觉。启动/关闭状态机必须先于安装器和 UI 文案。

### 阶段 C：工作台与设置重构（C13–C19）

每次只改一个窗口或一个共享控件族，保留可回退的小 diff。

### 阶段 D：翻译可信度与商业交付（C20–C26）

最后完成 Provider 合规、安装更新、全新账户和真实打包验收。

## 8. 可执行任务包

### C00 测试隔离与可信基线（P0）

**目标**：真实 PopGlot 运行时，测试要在首个守卫后立即停止，不能产生级联假失败。

**入口**：`TestIsolation.AssertNoConflictingAppInstance`、测试 `Program.Main`、`verify.ps1`。

**实现要求**：

- 把环境前置条件放在任何 `Application`、Core、热键、剪贴板初始化之前。
- 冲突时整个套件 exit 非零且只报告一个明确错误。
- 提供 `POPGLOT_TESTS_FILTER` 时，也不能绕过会触碰真实全局资源的守卫。
- 纯函数测试若要支持并行运行，应拆成另一个不加载 WPF App 的测试进程，而不是在失败后继续。

**验收**：有真实实例时 1 个环境失败、0 个后续测试；无实例时全量单进程连续通过；前后真实配置哈希不变。

### C01 免费引擎最终发送授权（P0）

**目标**：授权是一次可消费能力，并在每次 HTTP send 前重新确认全局策略。

**入口**：`OutboundPolicy.FreeEngineAuthorization`、`FreeTranslateService.TranslateAsync`、健康检查、端点 fallback。

**实现要求**：

- `AllowOnce` 使用原子消费；第一次真实 send 以后永久失效。
- cache hit 不消费 send 权限，但不得借 cache authorization 发起健康检查。
- 永久允许也要在每个端点 send 前重读当前 consent、offline、network 状态。
- 撤销时取消或阻止排队中的健康探测和下一 fallback endpoint。
- authorization 不得长期保存在 UI/单例字段中。

**验收反例**：F01/F02/F03 均 sends=0/1/1（按场景定义），并有并发双消费测试确保只允许一个真实 send。

### C02 词库超限、损坏与写入恢复（P0）

**目标**：不能因“读不了旧文件”而把旧数据覆盖为空或一条新记录。

**入口**：`VocabularyStore.Load`、`ToggleStar`、`TryPersist`。

**实现要求**：

- 读取前检查文件大小；超限进入只读错误态，不反序列化、不覆盖。
- 损坏、被锁、无权限、磁盘满分别记录状态；保留原文件和 `.bak/.corrupt-*`。
- UI 展示“词库未加载，新收藏未保存”，而不是点亮星标。
- 提供导出原文件/重试/打开数据目录，不做静默截断。

**验收**：33,554,434-byte fixture 操作后原文件字节和 hash 不变；收藏返回明确失败；正常并发收藏仍不丢更新。

### C03 诊断最小化与脱敏（P0）

**目标**：日志默认不记录用户原文、译文、请求体、URL query、Header、密钥或截图路径。

**入口**：`DiagnosticsLog`、全局异常屏障、所有拼接 exception message 的位置。

**实现要求**：

- 从黑名单正则改为结构化 allowlist：错误码、异常类型、受控阶段、随机会话 ID/必要长度，不写任意 message 或原文 hash。
- UI 可显示受控错误文案；技术详情也只能来自结构化安全字段。
- 如果保留第三方 exception，仅记录类型和内部 correlation id。
- 日志查看/导出前再次扫描敏感模式。

**验收**：包含 `source:`、`api_key=`、自定义 8–200 字符秘密、Bearer、URL query、Windows 用户路径的矩阵全部不可恢复原值。

### C04 代码与结构保真（P0）

**目标**：复制结果时，代码内容逐字符保留；自然语言 Markdown 只去展示标记。

**入口**：`MarkdownPresenter.ToPlainText`、代码块复制、三入口复制、TTS、收藏。

**实现要求**：

- fenced 和 unclosed fence 内保留 tab、空格、空行、最终换行；只去 fence 本身。
- 不对整个输出调用 `Trim()`，不对代码行调用 `TrimEnd()`。
- 复制和 TTS 分离：TTS 可有可读规范化，剪贴板必须保真。
- 定义 CRLF/LF 输出合同并统一三个入口。

**验收**：本轮 7 个 probe 输入逐字符断言；混合 prose+code、空代码块、四反引号、语言标签、尾换行均覆盖。

### C05 关闭、失焦、取消与恢复状态机（P0/P1）

**目标**：消灭“点一下别处结果没了”和“Esc 到底取消还是关闭”的不确定性。

**入口**：`QuickSearchWindow.Window_Deactivated`、`TranslationPanelWindow.OnDeactivated/OnPreviewKeyDown`、`MainWindow.OnClosing`。

**产品决定**：

- 极速查词：失焦规则、暂存时限和恢复入口按第二轮 N01；取消“隐藏 5 秒”这条含义不明确的规定。
- 翻译浮窗：默认不因失焦销毁；可配置“失焦自动隐藏”，隐藏可恢复。固定状态持久到当前会话。
- 请求中第一次 Esc=取消并保留 partial；第二次 Esc=隐藏；显式 X=隐藏。真正销毁在新会话覆盖或退出时。
- 主窗 X=隐藏到托盘；托盘退出=真正退出。

**验收**：失焦到输入法、上下文菜单、系统通知、同进程下拉框、其他应用、Alt+Tab 的矩阵；在途/完成/失败/partial 均验证内容可恢复且不产生副作用。

### C06 全局异常策略（P1）

**目标**：不可恢复 UI 异常不能被无条件吞掉后继续运行。

**入口**：`App.DispatcherUnhandledException`、`TaskScheduler.UnobservedTaskException`。

**实现要求**：

- 只对已分类、可恢复的预期异常设置 `Handled=true`。
- 未知异常写安全 crash envelope，停止新任务，给出重启/退出入口。
- 同类异常风暴熔断；禁止继续注册热键或写历史。

**验收**：注入可恢复/不可恢复异常，验证一次通知、无通知风暴、退出不卡死、日志无敏感内容。

### C07 开机启动状态模型（P0/P1）

**目标**：设置页显示真实有效状态，并尊重用户在 Windows 中的禁用。

**入口**：`StartupRegistration`、`SettingsWindow.LoadValues/Save_Click`、`App.OnStartup`。

**实现要求**：

- 建立 `DesiredEnabled / RunEntryPresent / PathMatches / OsDisabled / EffectiveEnabled / LastError` 状态对象。
- 不直接改写或删除 `StartupApproved`；用户在任务管理器禁用后，应用显示“Windows 已禁用”，由用户主动点“重新启用”。
- 保存设置失败时开关回滚并显示原因；不能“设置已保存”与实际失败同时让人误解。
- `settings.StartWithWindows || IsEnabled()` 改为诚实显示期望与实际的组合状态，而不是把任一 true 显示为成功。
- 后台启动使用明确 `--background`，不抢焦点、不弹主窗；失败写可见的下次启动诊断。

**验收**：缺 Run、旧路径、用户禁用、无权限、文件移动、升级换目录、重复启动、崩溃恢复 8 种状态自动化；真机临时账户登录 20 次 20/20 到托盘可用。

### C08 商业安装路径与自启修复（P1）

**目标**：自启指向稳定安装位置，升级后继续有效。

**产品决定**：保留 zip 作为 portable 渠道；商业默认渠道选择签名 MSIX/Store 或签名安装器，两者二选一并写 ADR。

**实现要求**：

- 安装器负责安装目录、开始菜单、卸载、升级和启动注册迁移。
- portable 模式明确提示“移动文件夹会影响开机启动”，启动时只修正 Run 路径，不覆盖 OS 禁用。
- 发布包内每个 PE/native DLL 一致版本、架构和签名策略。
- 升级失败可回滚，数据目录与二进制目录分离。

**验收**：全新安装、覆盖升级、降级拒绝/回滚、移动 portable、卸载保留/删除数据选择、标准用户权限矩阵。

### C09 真实启动与空闲性能证据（P1）

**目标**：修正当前只从 `RunStartupSmoke` 内部开始计时的假边界。

**入口**：`measure-startup.ps1`、readiness marker、Release publish。

**实现要求**：

- 父进程在 `Start-Process` 前开始计时；子进程在托盘和热键真实可交互后写 marker。
- 严格使用 self-contained Release publish；不存在就失败，不回退 Debug。
- 记录 git hash、dirty diff hash、OS、CPU、应用 runtime、冷/暖定义和所有失败样本。
- 30 次启动；稳定 30 秒后 WS/Private Bytes；60 秒 CPU；不能只记录均值。

**门槛**：启动 P50≤600ms、P95≤1200ms；空闲 WS 目标≤80MiB、硬门槛≤120MiB；空闲 CPU 接近 0 且无 1 秒轮询。

### C10 快捷键到首帧与取消性能（P1）

**目标**：定位用户感知的“卡一下”发生在复制、窗口构造、路由、FFI、网络还是渲染。

**实现要求**：

- 在不记录文本内容的前提下打结构化时间点：hotkey、panel shell visible、selection acquired、request sent、first delta received、first delta painted、final painted。
- 100 次回环 mock，输出 P50/P95/max 和失败率。
- 请求中取消 100 次，测 UI 反馈和任务真正退出两个指标。

**门槛**：快捷键到壳 P95≤150ms；首 delta 到绘制 P95≤50ms；取消 UI≤100ms、任务退出建议 P95≤200ms。

### C11 UI 线程 I/O 与激活卡顿（P1）

**入口**：`MainWindow.RefreshEngineStatus`、`ProfileManager.Load`、`CredentialStore.HasApiKey`、History/Vocabulary load/save、主题切换。

**实现要求**：

- Activated 路径不做同步磁盘、注册表、Credential Manager 或网络调用。
- 配置使用版本化内存快照；磁盘变更由后台加载后一次 Dispatcher 提交。
- 历史/词库分页或虚拟化；大 JSON 不在 UI 线程全量 parse/render。
- 用 ETW/PerfView 或 dotnet-trace 提供 200ms 以上卡顿调用栈，未测到瓶颈不要盲目重写。

**验收**：10k 词库、200 历史、4MiB 合法文件、慢磁盘模拟；UI thread 最长任务<50ms，输入/拖动不冻结。

### C12 生命周期、缓存与内存回收（P2）

**目标**：反复打开/关闭/隐藏窗口不增长事件订阅、Timer、FlowDocument 和图像。

**验收**：四窗口各开关 200 次，GC 后对象数回到稳定平台；WS 不随次数单调增长；主题事件、DispatcherTimer、TTS、CancellationToken 全部释放。

### C13 主翻译工作台重构（P1 UI）

**目标**：空态也清楚告诉用户下一步，结果态优先阅读而不是显示控件。

**实现要求**：

- 顶部只保留语言对、交换、主“翻译”；清空降为输入区次要动作。
- 去掉重复“原文/译文/状态”噪声；底部状态只显示真实来源、隐私和会话状态。
- 空态提供三个入口：粘贴、划词快捷键、截图快捷键；不放大面积无意义空白。
- 窄宽上下分栏时侧栏折叠，源/译文最小高度和滚动独立。

**验收矩阵**：空、短、长、代码、流式、partial、错误 × 深浅 × 560/720/960/1440 DIP。

### C14 极速查词与翻译浮窗（P1 UI）

**目标**：更像可靠系统工具，少像临时 demo。

**实现要求**：

- 极速查词 idle 高度紧凑；先核对截图生成器是否强制尺寸，再以真实窗口验证并调整生产布局。
- 翻译浮窗标题栏最多保留固定、语言、更多、关闭；低频动作进“更多”。
- 固定/自动隐藏用文字 tooltip、明确选中形状和状态提示。
- 错误态应用第 5.3 节统一结构；技术错误折叠。
- 主按钮和可点击区≥32 DIP，关键触控目标建议≥40 DIP。

### C15 设置：通用、启动与关闭行为（P1 UI）

**目标**：用户不需要猜开关是否真的生效。

**实现要求**：

- 将“启动与后台运行”独立分组，展示实际状态和修复入口。
- 将“点击浮窗外部时自动隐藏”默认改为安全值，并解释隐藏后如何恢复。
- 增加“主窗口 X 行为”选择；不增加第二个含义相同的开关。
- 所有设置采用保存后才生效或即时生效二选一；同页不能混合而不提示。
- sticky 保存条显示“未保存修改”，关闭草稿时给保存/放弃/取消三个明确动作。

### C16 服务配置流程（P1 UI）

**目标**：普通用户完成一个服务只需名称、Key、模型；高级用户再展开协议字段。

**实现要求**：

- 列表态：一个“添加服务”主动作。
- 编辑态：一个“保存修改”主动作；验证连接为次要，设默认在保存成功后提供。
- 删除与保存分居左右并二次确认；不能在编辑态顶部继续显示同等级“添加引擎”。
- API Base/Endpoint/Header 放高级设置；预设说明数据会去哪里。
- 测试连接显示“本次测试结果+时间”，不伪装永久健康。

### C17 统一错误、空态、加载与成功反馈（P1 UI）

**目标**：同一个错误在四入口具有相同事实和恢复动作。

**实现要求**：建立有限状态词典和 reason code，不直接把 exception message 作为 UI 文案；覆盖未配置、未授权、离线、超时、429、5xx、坏响应、partial、取消、保存失败。

### C18 主题、高对比与已有文档刷新（P1 UI）

**目标**：主题切换后已有文本、代码、链接、按钮立即更新。

**入口**：`ThemeService`、`MarkdownPresenter`、三个 RichTextBox/FlowDocument 持有者。

**实现要求**：不要把动态主题画刷冻结为文档局部值；可绑定动态资源，或 ThemeChanged 时重建现有文档但保持滚动/选择。实现 `SystemParameters.HighContrast` 优先级和系统颜色映射。

**验收**：同一窗口深→浅→深 10 轮，旧文档颜色都等于当前 token；高对比人工走查；禁止每轮 new window 掩盖旧引用。

### C19 图标、品牌与可访问性（P2 UI）

**目标**：小尺寸清晰、语义一致、键盘与读屏可完成主旅程。

**实现要求**：

- 审核 16/20/24/32/48/256px 图标在浅/深/高 DPI 背景。
- 应用头像、托盘、窗口 icon 使用同一母形；不要缩放复杂插画冒充小图标。
- 所有图标按钮具 AutomationName、tooltip、focus ring；tab 顺序与视觉顺序一致。
- 真机 IME composition 的 Enter 不提交翻译；Narrator 读出状态变化但不逐 token 播报。

### C20 分段与长标识符保真（P1）

**目标**：URL、路径、identifier、代码块和 placeholder 永不跨段硬切。

**实现要求**：语义原子超过单段上限时明确拒绝或走专门大段策略，不在 `_`、`::`、路径分隔符中间切。保持总请求/输出预算。

**验收**：本轮 799+identifier 反例不再切；长 URL、GUID、stack trace、minified JSON、CJK 长文均覆盖。

### C21 推荐证据时间与协议身份（P2）

**目标**：未来、过期、其他协议/端点/模型的 benchmark 不能影响推荐。

**实现要求**：拒绝 `sampleTime > now + clockSkew`；匹配加入 Provider protocol、endpoint fingerprint、model、machine、app benchmark schema；UI 标注样本日期和“仅本机试测”。

### C22 免费引擎商业合规与稳定性决策（P0 商业）

**目标**：商业产品不得把未认证、无 SLA、可能变化的公共网页接口当作默认稳定服务。

**要求**：

- 由产品负责人做 ADR：移除内置免费引擎、改用有正式条款/API 的供应商、或明确降级为实验功能。
- 禁止伪装浏览器 User-Agent 规避服务边界。
- 文档写明数据接收方、地区、条款、限额、失败策略和成本责任。
- 在 ADR 完成前，商业渠道默认不启用该引擎。

### C23 Provider、网络与真实 E2E（P1）

**目标**：四协议在回环 mock 和至少一个授权测试账户中通过真实流式主旅程。

**验收**：重定向、TLS、代理、IPv6、断网、DNS、429 Retry-After、总 deadline、取消、坏 UTF-8、超限、零 delta fallback；真实调用必须由用户另行明确授权并使用非生产测试 Key。

### C24 安装、签名、更新和回滚（P1 商业）

**目标**：用户下载后可相信发布者、升级不丢设置、卸载行为明确。

**实现要求**：

- 选择 Store/MSIX 或签名 installer；zip 保留为 advanced portable。
- CI 生成 SBOM、SHA256、版本清单；签名发生在受保护流水线。
- 更新提示可延期，下载失败不影响翻译；不静默自更新。
- 设置 schema 向前迁移、失败回滚和备份矩阵。

**参考**：Microsoft Learn 的 Windows distribution path、code signing 和 StartupTask 文档；不要由轻量模型自行购买证书或创建发布账户。

### C25 首次体验、帮助与可恢复配置（P2 产品）

**目标**：首次打开 3 分钟内完成第一次翻译。

**流程**：欢迎→隐私说明→选择“使用自己的服务/稍后配置”→显示快捷键→本地示例翻译。不得默认上传，也不得要求先理解协议名。

**要求**：内置诊断页显示版本、运行模式、启动状态、热键冲突、OCR 语言包、数据目录和“复制安全诊断”；一键重置不得删除历史/词库，删除数据另走明确确认。

### C26 商业发布总验收（Release Gate）

**自动门禁**：

- Rust fmt/test/clippy；WPF Release build；Windows suite 0 fail。
- 生产 DLL 独立反例探针全绿。
- self-contained publish 后从空目录启动，不依赖仓库文件。
- 包内版本、hash、SBOM、签名、native DLL 完整。

**真机矩阵**：

- Windows 10 22H2、Windows 11 当前受支持版本；标准用户。
- 100/125/150/200% DPI；单屏、双屏、负坐标、混合 DPI。
- 中文/英文系统，中文/日文 IME，高对比、键盘-only、Narrator。
- 全新安装、升级、开机登录、睡眠恢复、Explorer 重启、网络切换。
- 四入口 × 成功/超时/取消/partial/错误；每条记录视频或步骤证据。

**硬规则**：P0 不能按平均分抵消；任何隐私、数据丢失、代码保真、无法启动/退出项失败即阻断发布。

## 9. 推荐任务依赖

```text
C00 ─┬─> C01 ─> C22 ─> C23
     ├─> C02
     ├─> C03 ─> C06
     └─> C04 ─> C20

C05 ─> C07 ─> C08 ─> C24 ─> C26
  └──> C14

C09 ─> C10 ─> C11 ─> C12 ─> C26

C13 ─> C14 ─> C17 ─┐
C15 ─> C16 ────────┼─> C19 ─> C26
C18 ───────────────┘

C21 ─> C23
C25 ─> C26
```

## 10. 未来获得实施授权后的首批任务建议

| 顺序 | 任务 | 原因 | 建议单次工作上限 |
|---|---|---|---|
| 1 | C00 | 没有可信门禁，后续结果都会混入假失败 | 2 小时 |
| 2 | C01 | 当前有真实出网授权反例 | 4 小时 |
| 3 | C02 | 已复现读取上限失效；真实词条损坏/丢失保护仍需验证 | 3 小时 |
| 4 | C03 | 当前日志可保留用户内容 | 3 小时 |
| 5 | C04 | 翻译工具不能损坏代码 | 3 小时 |
| 6 | C05 | 直接对应用户报告的误触关闭 | 4 小时 |
| 7 | C07 | 直接对应用户报告的开机不启动 | 4 小时 |

不要让一个轻量模型同时领取 C01、C02、C05；它们的状态机和验收边界不同。

## 11. 执行记录（只追加，不删除历史）

### 2026-09-12 主评审

- 状态：评审完成，等待实现。
- 工作树：`main`，存在用户未提交修改：`App.xaml.cs`、`ClipboardSelectionService.cs`、`StartupRegistration.cs`、`TranslationPanelWindow.xaml.cs`、Windows logic `Program.cs`。
- 当前未提交启动修复必须作为 C07 输入评审，不能直接覆盖。
- 真实 Windows GUI 自动控制接口本会话未暴露 PopGlot 窗口，因此鼠标级旅程保持未验证。
- 下一位：从 C00 或 C01 开始；先重跑本文列出的原始反例。

## 12. 外部依据

- Microsoft Learn: [Choose a distribution path for your Windows app](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/choose-distribution-path)
- Microsoft Learn: [Code signing options for Windows app developers](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)
- Microsoft Learn: [Startup apps](https://learn.microsoft.com/en-us/windows/win32/w8cookbook/startup-apps)
- Microsoft Learn: [Integrate your desktop app with Windows using packaging extensions](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-extensions)
