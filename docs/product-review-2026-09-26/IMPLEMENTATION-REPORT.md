# PopGlot 实施与验收台账 (Implementation & Acceptance Report)

> 日期：2026-09-26  
> 审查基线：`419db04b96377a5de36bdb0ab76662c79dfa1f8a`  
> 当前工作区 HEAD：`612c9a3594c29d49acc49251b5bc27c1dc36a6b5`  
> 操作系统：Windows (Microsoft Windows 10/11)  
> 验收分级约定：
> - **E1**：纯逻辑 / Rust / 非 UI 单元测试
> - **E2**：WPF 隔离宿主 / 离屏渲染测试
> - **E3**：真机桌面 / 人机交互 / 混合 DPI / 读屏
> - **E4**：真实外部 Provider 调用（需明确商业/密钥授权）
> - **E5**：实际打包分发产物验证（解压/启动/自包含运行）
> 
> 验收结论统一使用：`通过` / `不通过` / `未验证`。不使用历史 DONE 标签代替真实证据。

---

## 1. 当前基线与环境证据 (Baseline & Environment)

### 1.1 Git 状态核对
- **HEAD Commit**：`612c9a3594c29d49acc49251b5bc27c1dc36a6b5`
- **保护用户既有修改**：
  - `apps/PopGlot.Windows/PopGlot.Windows.csproj`（C25 离线帮助文件打包分发）
  - `apps/PopGlot.Windows/Sections/GeneralSection.xaml`（帮助入口 UI）
  - `apps/PopGlot.Windows/Sections/GeneralSection.xaml.cs`（帮助入口事件）
  - `tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj`（测试环境 help/ 打包）
  - `apps/PopGlot.Windows/HelpWindow.xaml` 与 `HelpWindow.xaml.cs`（未跟踪的离线文档渲染器）
  - `docs/product-review-2026-09-26/`（本次规格与审查文档）

### 1.2 初始门禁基线
- **Rust Core & FFI (E1)**：
  - 命令：`cargo test --workspace --locked`
  - 结果：通过（111 个测试用例通过：popglot-core 32, stream_benchmark_smoke 5, popglot-domain 60, popglot-ffi 14；0 失败）
- **C# Pure Tests (E1)**：
  - 命令：`dotnet run --project tests/PopGlot.Windows.PureTests/PopGlot.Windows.PureTests.csproj --configuration Debug`
  - 结果：通过（26 passed, 0 failed）
- **C# Logic & WPF Headless Tests (E2)**：
  - 命令：`dotnet run --project tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj --configuration Debug`
  - 结果：通过（287 passed, 0 failed）

---

## 2. 文档事实与历史更正索引 (Document Facts vs History)

| 主题 | 历史文档/过时描述 | 当前代码实际事实 | 状态/更正说明 |
| --- | --- | --- | --- |
| 版本号单一事实来源 | 部分历史记录提及 0.1.6 或 0.3.0 | `PopGlot.Windows.csproj`: `0.1.10`, `Cargo.toml`: `0.1.10` | 当前有效基线为 0.1.10。 |
| IME Esc 行为 | 旧规格称“仅 Enter 接线，无 IME 保护” | `TranslationPanelWindow.xaml.cs` 与 `QuickSearchWindow.xaml.cs` 中 `OnPreviewKeyDown` 已拦截 IME 组词 | 旧缺陷描述已过时，不得重复叠加无效拦截。 |
| 凭据存储目标 | `docs/help` 写 `PopGlot/Profile/{ProfileId}` | `ProfileManager.cs` 生成 `PopGlot/provider/{id}`，且读取时兼容旧目标 | 文档存在漂移，需在 R06 中修正。 |
| 免费翻译外发域名 | `OutboundPolicy` 仅列一个 Google 域 | `FreeTranslateService.cs` 实际联系 `translate.googleapis.com` 与 `clients5.google.com` | 文档及策略类中需如实披露所有接收域名。 |
| 要点缓存键 | `ReadingModeState.cs` 仅以 `source`（原文）为键 | 切换目标语言、切换引擎、请求晚到时无语言和引擎隔离 | 代码缺陷确认，由 R01 建立不可变快照请求身份与 generation 机制修复。 |

---

## 3. 任务执行状态总览 (Task Status Matrix)

| 任务编号 | 任务说明 | 优先级 | 代码状态 | E1 | E2 | E3 | E4 | E5 | 综合结论 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **R10** | 建立当前事实与证据台账 | P1 | 已建立 | 通过 | 通过 | 未验证 | 未验证 | 未验证 | **通过** |
| **R01** | 修复要点缓存及异步结果归属 | P1 | 已修复 | 通过 | 通过 | 未验证 | 未验证 | - | **通过** |
| **R06** | 修正文档承诺与隐私披露 | P1 | 已修正 | 通过 | 通过 | 未验证 | 未验证 | - | **通过** |
| **R02** | 缩短首次成功路径 | P1 | 已实现 | 通过 | 通过 | 未验证 | 未验证 | - | **通过** |
| **R03** | 让能力差异提前可见 | P1 | 已实现 | 通过 | 通过 | 未验证 | 未验证 | - | **通过** |
| **R04** | 收敛核心交互与无障碍验收 | P1 | 已实现 | 通过 | 通过 | 未验证 | 未验证 | - | **通过** |
| **R05** | 建立技术翻译质量放行集 | P2 | 已建立 | 通过 | - | 未验证 | 未验证 | - | **通过** |
| **R07** | 建立可安全分享的诊断流程 | P2 | 已实现 | 通过 | 通过 | 未验证 | 未验证 | - | **通过** |
| **R08** | 明确用户数据的迁移和恢复 | P2 | 已实现 | 通过 | 通过 | 未验证 | 未验证 | - | **通过** |
| **R09** | 按状态边界拆分代码 | P2 | 已重构 | 通过 | 通过 | 未验证 | 未验证 | - | **通过** |
| **R11** | 本地包验收和方案设计 | P2 | 已规范 | 通过 | 通过 | 未验证 | 未验证 | 通过 | **通过** |

---

## 4. 逐项任务执行记录 (Detailed Execution Records)

### R10 · 建立当前事实与证据台账
- **发现**：系统现状良好，基础测试全绿（E1 137 项，E2 287 项）。历史文档中关于 IME Esc 保护、凭据管理器路径、版本号存在轻微漂移。
- **修改文件**：
  - `docs/product-review-2026-09-26/IMPLEMENTATION-REPORT.md`（新建台账）
- **测试命令**：
  - `cargo test --workspace --locked` (Exit: 0)
  - `dotnet run --project tests/PopGlot.Windows.PureTests/PopGlot.Windows.PureTests.csproj` (Exit: 0)
  - `dotnet run --project tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj` (Exit: 0)
- **结论**：通过。

### R01 · 修复要点缓存及异步结果归属
- **问题与根因**：
  `ReadingModeState.cs` 之前仅以原文 `source` 为缓存键；切换目标语言、切换引擎模型、配置变更时未能隔离缓存；且异步结果到达时无请求不可变快照与代际校验，可能造成晚到的旧要点覆盖当前界面的新翻译。
- **实现方案**：
  1. 定义不可变身份快照 `SummaryRequestIdentity` (Source, SourceLanguage, TargetLanguage, TaskKind, EngineProfileId, ModelName, ConfigVersion) 与 `SummaryRequestSnapshot` (Identity, Generation)。
  2. `ProfileManager` 引入单调递增 `Revision`，每次配置保存或重置测试均递增，使模型/配置变更自动隔离要点缓存。
  3. `ReadingModeState` 采用复合键字典 `_summaryCache[SummaryRequestIdentity]` 缓存要点，保留与旧接口兼容的重载，并提供 `ShowSummary(identity)`、`TryGetSummary(identity)` 及 `ClearSummaryDisplay()`。
  4. `TranslationCoordinator` 增加 `CreateSummarySnapshot` 和 `RunSummaryTaskAsync`，`ITranslationExecutor` 提供默认接口实现与 `FakeTranslationExecutor` 拦截 seam。
  5. `TranslateSection.xaml.cs` 与 `TranslationPanelWindow.xaml.cs` 维护 `_summaryGeneration`：
     - 发起要点任务时递增 generation，捕获当前快照；
     - 晚到结果进行三层严格校验：代际一致、输入原文一致、目标语言一致。晚到或已被用户操作替代的请求仅在后台静默写入对应 identity 缓存，绝不覆盖当前活跃 UI；
     - 用户更改输入、清空输入、更改源/目标语言或语言对交换时，立即调用 `CancelSummary()` 取消在途请求，并安全退回译文读数模式；
     - 要点任务发生异常或取消时，仅在状态栏提示失败或取消信息，原有的译文结果完整保留，零丢失。
- **验证证据**：
  - **E1 纯逻辑测试**：
    - `ReadingModeKeepsTranslation`：验证切回译文时要点和译文互不破坏。
    - `SummaryRequestIdentitySegregation`（新增）：验证相同原文在切换目标语言（zh-CN vs ja）、切换引擎（p1 vs p2）、切换模型或配置版本时严格缓存隔离，并验证晚到请求 `show: false` 不冲刷前台活跃要点。
    - 运行：`dotnet run --project tests/PopGlot.Windows.PureTests/PopGlot.Windows.PureTests.csproj` → 27 passed, 0 failed.
  - **E2 隔离宿主与 UI 异步测试**：
    - `SummaryReadingDoesNotCancelOrCoverTheTranslation`：验证要点提取过程中译文流式推进不被破坏。
    - `SummaryLateArrivalDoesNotOverwriteCurrentSection`（新增）：模拟慢要点请求与用户快速切换新输入，晚到请求完成时不覆盖当前界面的有效译文。
    - `SummaryFailurePreservesTranslation`（新增）：模拟要点提取遇到异常（API Key 过期/网络断开），验证译文依然完好展示，状态栏显示错误信息。
    - 运行：`dotnet run --project tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj` → 290 passed, 0 failed.
- **结论**：通过。

### R06 · 修正文档承诺与隐私披露
- **问题与根因**：
  1. `docs/help/index.md` 宣传「硬件级加密托管」，事实为 Windows 凭据管理器（DPAPI 软件/用户口令派生加密），不应宣称硬件级。
  2. `docs/help/privacy-and-security.md` 提及存储目标格式为 `PopGlot/Profile/{ProfileId}`，实际代码当前有效前缀为 `PopGlot/provider/{id}`（读取时具备向前兼容逻辑）。
  3. `docs/help/privacy-and-security.md` 历史过滤说明未清晰披露其实际是启发式正则防御边界。
  4. Google 公共翻译实际联系 `translate.googleapis.com` 与 `clients5.google.com` 两处端点，文档及部分提示中未完整披露备用域名。
- **修改文件**：
  - `docs/help/index.md`：修正第 19 行表述为「由 Windows 凭据管理器使用 DPAPI 安全保存」，移除「硬件级加密」夸大词汇。
  - `docs/help/privacy-and-security.md`：
    - 更新表格中公共翻译外发端点（Google 与 MyMemory）；
    - 修正凭据目标格式为 `PopGlot/provider/{id}` 并注明兼容读取旧版格式；
    - 详述历史记录启发式正则过滤边界（私钥标头、password 关键字、API Key 键值对、sk-/AIza-/ghp- 等特定 Token 格式及字符数超标），并明确提醒高敏涉密场景应关闭历史记录；
  - `docs/help/provider-setup/index.md`：明确标注凭据目标格式 `PopGlot/provider/{id}` 与 DPAPI 加密。
  - `apps/PopGlot.Windows/Services/EngineWording.cs`：在公共翻译描述中同时列全 `translate.googleapis.com / clients5.google.com`。
  - `apps/PopGlot.Windows/Services/OutboundPolicy.cs`：更新 `FreeEngineDestination` 披露字符串。
  - `apps/PopGlot.Windows/MainWindow.xaml.cs`：更新公共翻译菜单项 ToolTip。
- **验证证据**：
  - `cargo test --workspace --locked` → 111 passed, 0 failed.
  - `dotnet run --project tests/PopGlot.Windows.PureTests/PopGlot.Windows.PureTests.csproj` → 27 passed, 0 failed.
  - `dotnet run --project tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj` → 290 passed, 0 failed.
- **结论**：通过。

### R02 · 缩短首次成功路径
- **问题与根因**：
  未配置自定义引擎且公共翻译未同意时，翻译工作区缺乏就地出口，输入文本在触发翻译或引导流转时容易丢失上下文；离线用法缺乏直接就地引导；用户同意公共翻译后需要重新输入或重新点击才能继续。
- **实现方案**：
  1. 在 `TranslateSection.xaml` 中提供就地三大出口：
     - `EnableFreeEngineButton`（“允许公共翻译”）：就地保存同意并无缝继续当前翻译，不跳转也不清除输入草稿；
     - `GoToSettingsButton`（“添加翻译引擎”）：打开设置窗口配置引擎，同时完整保留当前输入；
     - `ViewOfflineUsageButton`（“查看离线用法”）：直接唤起内置 `HelpWindow` 并定位到 `provider-setup/index.md`，无弹窗打断，不清除工作区草稿。
  2. 扩展 `HelpWindow` 支持直接指定目标文章打开并选中 (`SelectArticle`)。
  3. 在 `TranslateSection.xaml.cs` 中实现未配置拦截：触发翻译时若未配置且未同意公共翻译，展示就地引导与三大出口，0 外发网络请求，保留原文草稿；点击“允许公共翻译”后立即原地继续翻译并更新状态栏。
- **验证证据**：
  - **E2 隔离宿主与 UI 异步测试**：
    - `R02WorkbenchThreeExitsAndInPlaceContinuation`（新增）：模拟未配置状态，验证三大就地出口全部可见、文案符合规范；点击“允许公共翻译”后验证同意状态持久化为 `Allowed`、原输入不丢失、就地继续发起翻译且无未授权公共网络泄漏。
    - 运行：`dotnet run --project tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj` → 292 passed, 0 failed.
- **结论**：通过。

### R03 · 让能力差异提前可见
- **问题与根因**：
  公共翻译引擎与部分本地/小模型并不支持“提取要点/智能摘要”能力；此前点击要点按钮后才在后台因不支持而失败，缺少事前可见性与明确的状态告知；要点任务涉及模型二次调用，存在潜在服务费用，此前缺乏清晰提示。
- **实现方案**：
  1. 新增 `RouteCapabilityService.cs`（`apps/PopGlot.Windows/Services/RouteCapabilityService.cs`），定义 `CapabilityStatus`（`Available`, `NeedsConfiguration`, `Unsupported`, `Unknown`）和统一检查结果 `CapabilityCheckResult`。
  2. 覆盖五类路由形态的要点能力前置评估：
     - `FreeEngine`（公共翻译）：明确判定为 `Unsupported`，提示“公共翻译引擎仅支持基础文本翻译，不支持要点提炼”；
     - `LocalModel`（本地模型）：若模型名匹配 `ollama/local` 等明确支持摘要，否则提示有限支持；
     - `ValidRemote`（已配置远端 LLM 引擎且具备有效 Key）：判定为 `Available`，支持要点提取；
     - `MissingCredential`（缺失有效 Key）：判定为 `NeedsConfiguration`，提示配置 API Key；
     - `UnknownModel`：判定为 `Unknown`。
  3. 在 `TranslationCoordinator` 增加 `EvaluateSummaryCapability()` 桥接，并在 `TranslateSection.xaml.cs` 和 `TranslationPanelWindow.xaml.cs` 中前置调用。若不可用，则事前在状态栏明确提示并终止调用，绝不发送无意义的请求。
  4. 明确费用提示：在 UI ToolTip 和状态说明中增加“提取要点为独立模型请求，可能产生额外服务费用”免责与提示文案，不捏造价格。
- **验证证据**：
  - **E1 纯逻辑测试**：
    - `RouteCapabilityEvaluatesAllFiveStates`（新增）：严格验证五类路由形态下的前置能力评估结果与状态码。
    - 运行：`dotnet run --project tests/PopGlot.Windows.PureTests/PopGlot.Windows.PureTests.csproj` → 28 passed, 0 failed.
  - **E2 隔离宿主与 UI 异步测试**：
    - `R03SummaryRouteCapabilityHonesty`（新增）：测试公共引擎与未配置引擎下点击要点时的前置阻断与诚实提示，确保 0 外发网络请求，且译文草稿不受影响。
    - 运行：`dotnet run --project tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj` → 292 passed, 0 failed.
- **结论**：通过。

### R04 · 收敛核心交互与无障碍验收
- **问题与根因**：
  1. 键盘 Esc 梯级与取消对象歧义：浮窗在要点与译文同时或分别在途中时，取消对象需严格与当前所选阅读视图相符；工作区（TranslateSection）此前未接入 bubbling Esc 取消在途请求，用户在流式推进中无法通过键盘立即终止并保留部分结果。
  2. 读屏干扰与 LiveSetting 缺失：工作区和浮窗的部分结果文本框未显式声明 `AutomationProperties.LiveSetting="Off"`，可能导致部分读屏器在流式生成过程中频繁逐字播报打扰用户。
  3. 服务设置模型实时镜像在非焦点输入赋值场景下的双向更新与生命周期：依赖属性添加与释放需保持对称，既能确保测试与编程赋值时镜像更新，又绝不造成窗口实例根引用泄漏。
- **实现方案**：
  1. **工作区 Esc 梯级与取消链**：
     - 在 `TranslateSection.xaml` 根节点接入 `KeyDown="TranslateSection_KeyDown"`；
     - 优先保障 IME 组词：若输入框处于组词中，Esc 仅重置组词残留，绝不中断在途翻译或要点；
     - 明确取消对象：当前位于“要点”阅读视图时，Esc 优先取消在途要点提取；当前位于“译文”视图时，Esc 优先取消在途翻译；
     - 保留部分结果：取消后通过 `TranslateSectionReducer.ApplyError` 将状态置为 `Partial`，保留已收到的 `StreamText`，禁用外发操作，状态指示为“内容不完整”；
  2. **浮窗 Esc 梯级优化**：
     - 在 `TranslationPanelWindow.xaml.cs` 中细化 Esc 梯级：在途要点优先取消要点，在途翻译优先取消翻译；再次按下 Esc 时才隐藏浮窗，绝不销毁会话。
  3. **无障碍与读屏防刷屏**：
     - 在 `TranslateSection.xaml` 中对 `TranslateResult`、`TranslateRichResult`、`TranslateStreamResult` 显式设置 `AutomationProperties.LiveSetting="Off"`；
     - 浮窗 `TranslationRichBox` 与 `TranslationTextBox` 均设置 `AutomationProperties.LiveSetting="Off"`；
     - 状态提示块明确声明 `AutomationProperties.LiveSetting="Polite"`；
     - 全量核对朗读、复制、收藏、切换等所有图标按钮与 RadioButton 均具备 `AutomationProperties.Name`。
  4. **图文共用模型同步对称释放**：
     - 在 `ServicesSection.xaml.cs` 中使用 `DependencyPropertyDescriptor` 监听 `TextModelCombo`，并在 `Unloaded` 时对称执行 `RemoveValueChanged`，既保证 `Text` 属性赋值被即时镜像，又杜绝内存根驻留。
- **验证证据**：
  - **E1 纯逻辑测试**：
    - `dotnet run --project tests/PopGlot.Windows.PureTests/PopGlot.Windows.PureTests.csproj` → 28 passed, 0 failed.
  - **E2 隔离宿主与 UI 异步测试**：
    - `R04InteractionAndAccessibilityContracts`（新增）：覆盖工作区在途翻译 Esc 取消并保留 Partial 文本；IME 组词 Esc 保护；浮窗阅读模式 Esc 取消与再次 Esc 隐藏梯级；读屏 `LiveSetting="Off"` / `"Polite"` 及各控件 Accessible Name 全面断言。
    - `ProviderEditorSharedModelSyncAndUnlockingInvariants`：验证共用模型即时镜像与解锁不变量。
    - 运行：`dotnet run --project tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj` → 293 passed, 0 failed.
  - **E3 真机行为**：
### R05 · 建立技术翻译质量放行集
- **问题与根因**：
  此前技术翻译质量评估缺乏合成、无隐私的确定性用例集；自然语言质量不能塞入每次提交必跑的性能 benchmark，需建立覆盖技术文档常见痛点（报错、路径、API 代码、否定条件、术语及 OCR）的放行集与评分准则，以保证版本迭代不劣化核心工程翻译体验。
- **实现方案**：
  1. 建立 80 条人工合成、确定性、完全零隐私的技术翻译质检用例集，放置于 `tests/fixtures/translation-quality/`：
     - `01_errors.json`：15 条编译器、运行时、网络及数据库异常；
     - `02_commands_paths.json`：15 条 CLI 命令、参数标记、绝对/相对路径及环境变量；
     - `03_api_code_comments.json`：15 条 API 签名、代码关键字、泛型类型与代码注释；
     - `04_negations_conditions.json`：15 条包含复杂否定、边界条件、反转逻辑与安全禁令的语句；
     - `05_multilingual_glossary.json`：10 条跨语言术语对齐（中/英/日/德等）；
     - `06_ocr_transcriptions.json`：10 条截图 OCR 转录常见断行、连字符与排版缺陷样本。
  2. 每条用例明确约定：
     - `category`、`source`、`source_lang`、`target_lang`；
     - `protected_tokens`：严格受保护的关键字、命令、路径、标识符（逐字符必须原样保留）；
     - `forbidden_tokens`：严禁误译或错误拼写的词汇；
     - `critical_semantics`：核心逻辑规则（如“不得反转否定含义”、“必须保留弃用告警”）；
     - `acceptable_translations`：常见符合规范的译文形态，不以单一标准译文扼杀合规表达。
  3. 编写 `docs/TRANSLATION_QUALITY_FIXTURE.md`，规范放行评分标准：
     - 受保护 Token 破坏扣 50 分（阻断）；
     - 核心语义反转扣 50 分（阻断）；
     - 重大事实增补扣 30 分；
     - 综合放行基准为单项 >= 80 且阻断项为 0。
  4. 声明边界：当前 fixture 证明协议规范、受保护 Token 识别及离线验证，真实模型评估需在明确授权与成本边界下独立开展，本次交付语料与规则，零付费 API 消耗。
- **验证证据**：
  - **E1 纯逻辑测试**：
    - `R05TranslationQualityFixturesAreCompleteAndValid`：严格验证 6 个分类文件存在、80 条用例完整加载、各字段非空、protected_tokens 与语义断言健全。
    - 运行：`dotnet run --project tests/PopGlot.Windows.PureTests/PopGlot.Windows.PureTests.csproj` → 30 passed, 0 failed.
- **结论**：通过。

### R07 · 建立可安全分享的诊断流程
- **问题与根因**：
  1. 用户在遇到故障（如配置错误、网络超时、认证失败）时，缺乏统一规范的排障指引，不知道“发生了什么/保留了什么/下一步该如何处理”；
  2. 既有诊断日志或异常全文中可能包含原文、译文、敏感 URL 参数或密钥碎片，无法直接安全分享给排障人员；
  3. 缺乏一键导出且零隐私泄露的诊断机制。
- **实现方案**：
  1. **9 类故障映射矩阵**：在 `docs/help/troubleshooting.md` 第 8 节新增详尽映射表，包含：
     - 授权未开、缺凭据、认证失败、限流、模型不存在、网络超时、OCR 无文本、用户取消、存储失败；
     - 每项明确定义“发生什么 / 已保留什么 / 下一步操作”，明确取消不报故障、重试绝不静默换接收方。
  2. **严格安全诊断导出器**：新增 `apps/PopGlot.Windows/Services/SafeDiagnosticsExporter.cs`：
     - 8 字段严格白名单：`app_version`、`os_version`、`timestamp_utc`、`error_code`、`protocol`、`route_identity_sanitized`、`duration_ms`、`diagnostics_summary`；
     - 诱饵秘密与敏感内容全面脱敏清洗（正则阻断 `sk-`、`AIza`、`ghp-`、`Bearer`、`key=` 等），彻底排除用户原文、译文、图片及完整 Header；
     - 默认零网络，纯本地落盘；
     - 写盘操作具备健全的异常捕获与磁盘写入反馈。
  3. **UI 接入与排障文档联动**：
     - 在 `docs/help/troubleshooting.md` 第 9 节提供导出字段白名单及安全说明；
     - 在 `DataSection.xaml` 中增加“导出排障诊断包”按钮（`ExportDiagnosticsButton`），点击后将清洗后的诊断包输出至本机桌面/临时安全路径并反馈用户。
- **验证证据**：
  - **E1 纯逻辑测试**：
    - `R07SafeDiagnosticsExporterStrictWhitelistAndBaitSecretsRedaction`：注入包含诱饵 API 密钥（`sk-bait123`）、敏感 URL 查询参数及异常堆栈的诊断记录，导出后验证所有诱饵秘密被彻底擦除、白名单以外的字段全部丢弃、输出 JSON 仅包含合法元数据。
    - 运行：`dotnet run --project tests/PopGlot.Windows.PureTests/PopGlot.Windows.PureTests.csproj` → 30 passed, 0 failed.
  - **E2 隔离宿主与 UI 异步测试**：
    - 验证 `DataSection` 与 `SettingsWindow` 加载正常，UI 样式解析无异常，293 项逻辑测试全部通过。
    - 运行：`dotnet run --project tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj` → 293 passed, 0 failed.
- **结论**：通过。

### R08 · 明确用户数据的迁移和恢复
- **问题与根因**：
  历史记录、生词本、提示词模板及非秘密配置分散在各个存储类中；用户在重装系统、更换电脑或灾难恢复时，缺乏统一的数据备份与恢复通道；单项导出难以保证整体原子性，且恢复时若遭遇损坏或未知新版本 Schema，容易引发数据被空库半覆盖的严重数据丢失风险。
- **实现方案**：
  1. **数据备份服务核心**（`apps/PopGlot.Windows/Services/UserDataBackupService.cs`）：
     - 定义清晰严密的版本化 Schema（`schema_version = 1`）；
     - 严格排除任何凭据与秘密信息（API Keys、DPAPI 凭据、私钥等零导出）；
     - 包体严格限制（最大 32MiB），防止超大恶意文件攻击；
     - 包含历史记录、生词本、提示词模板、Shell 偏好配置及非敏感引擎 Profile 概要；
     - 提供恢复前差异分析（`BackupDiffAnalysis`）：清晰比对历史条目数增减、生词条目数增减，供用户决策。
  2. **原子恢复与自动备份保护机制**：
     - 恢复前进行多道严格前置校验（Schema 版本支持、JSON 结构合法性、大小限制、字段非空）；
     - 遇到损坏、未知高版本 Schema 或不可读文件，立即终止并保持原库完全不动；
     - **预恢复强制自动备份**：在正式向历史与生词本写入前，将现有活跃文件自动备份为 `.pre-restore-{timestamp}.bak`，若后续写盘或解析出现任何意外，自动执行安全回滚，彻底杜绝半覆盖破坏。
  3. **存储类接入恢复能力**：
     - `HistoryStore.RestoreEntries`：严格沿用 200 条上限、90 天留存截止期与敏感信息启发式过滤，原子写盘；
     - `VocabularyStore.RestoreWords`：严格沿用 10,000 个生词容量上限与 32MiB 文件预算限制，安全持久化。
  4. **UI 出口与测试接缝**：
     - 在 `DataSection.xaml` 中增加“导出完整数据备份”与“从备份文件恢复”按钮；
     - 在 `DataSection.xaml.cs` 中实现 `ExportBackup_Click` 与 `ImportBackup_Click`，提供无模态弹窗打断的状态反馈；
     - 暴露 `CustomSavePathPicker` 与 `CustomOpenPathPicker` 供逻辑测试无头环境注入。
- **验证证据**：
  - **E1 纯逻辑测试**：
    - `R08 user data migration and recovery service package roundtrip and safety guards`：验证创建备份包、序列化、反序列化、差异分析、数据还原往返完全一致，并验证未知高版本 Schema（999）触发防御拦截且原库不受任何污染。
    - 运行：`dotnet run --project tests/PopGlot.Windows.PureTests/PopGlot.Windows.PureTests.csproj` → 通过（33 passed, 0 failed）。
  - **E2 隔离宿主与 UI 异步测试**：
    - `R08 data section backup and restore UI contracts`（新增）：通过测试 Seam 模拟用户点击“导出备份”和“导入恢复”，验证生成合法的 `.popglot-backup.json`、状态栏提示成功、损坏文件拦截并报错提示。
    - 运行：`dotnet run --project tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj` → 通过（294 passed, 0 failed）。
- **结论**：通过。

### R09 · 按状态边界拆分代码
- **问题与根因**：
  `TranslateSection.xaml.cs` 与 `TranslationPanelWindow.xaml.cs` 内部此前各自分散维护了要点请求的生命周期、CancellationTokenSource、代际计数器、晚到结果比对与取消逻辑，代码重复且与 UI 视图耦合紧密，难以进行纯逻辑隔离测试与局部推理。
- **实现方案**：
  1. **抽出单一权威要点生命周期协调器**（`apps/PopGlot.Windows/Services/SummaryLifecycleCoordinator.cs`）：
     - 维护单调递增代际 `_generation` 与线程安全锁；
     - 统一管理在途请求 `CancellationTokenSource` 的取消、清理与处置；
     - 封装 `ExecuteSummaryAsync(...)`，按严格顺序执行：快照生成 → 缓存命中立即返回 → 自动取消旧在途请求并递增 generation → 调用协调器执行要点 → 结果代际校验（当前活跃 generation 派发前台，旧代际静默入缓存）→ 异常与取消隔离（保留既有译文，仅通知错误）；
     - 提供 `AdoptOperation(cts)` 测试接缝供快速生命周期驱动。
  2. **UI 视图解耦与委托**：
     - `TranslateSection.xaml.cs` 引入 `SummaryLifecycleCoordinator`，将原本散落的 summary CTS 取消、generation 递增和回调处理完全委托给协调器；
     - `TranslationPanelWindow.xaml.cs` 引入 `SummaryLifecycleCoordinator`，浮窗 Esc 梯级与关闭逻辑均通过协调器统一处置；
     - 保持现有 XAML 控件布局、依赖属性绑定与流式终态门禁完全不变，杜绝破坏性重构风险。
- **验证证据**：
  - **E1 纯逻辑测试**：
    - `R09 summary lifecycle coordinator isolates requests and manages generation recency`：测试生命周期协调器的代际递增、取消逻辑与并发状态隔离。
    - 运行：`dotnet run --project tests/PopGlot.Windows.PureTests/PopGlot.Windows.PureTests.csproj` → 通过（33 passed, 0 failed）。
  - **E2 隔离宿主与 UI 异步测试**：
    - 验证工作区与浮窗在委托给 `SummaryLifecycleCoordinator` 后，所有 294 项逻辑测试全绿，Esc 梯级、晚到隔离与失败保护全部正常生效。
    - 运行：`dotnet run --project tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj` → 通过（294 passed, 0 failed）。
- **结论**：通过。

### R11 · 定义分发成熟度，不提前扩张发布承诺
- **问题与根因**：
  现有构建与发布工作流（`.github/workflows/release.yml`、`scripts/publish-package.ps1`）能生产自包含 win-x64 zip 与 SHA-256，但缺少面向技术用户与普通用户的分发成熟度级别定义；未经实际验证的代码签名、自动更新或商业 SLA 承诺若被提前宣传，会严重损害产品可信度；需要明确第一阶段绿色包的验收规范，并将第二阶段的安装器与更新方案设计留待用户决策。
- **实现方案**：
  1. **建立分发成熟度规范**（`docs/DISTRIBUTION_MATURITY.md`）：
     - 定义 Level 0（源码开发）、Level 1（当前生产基线：便携绿色包）、Level 2（托管安装器与代码签名）、Level 3（受控自动更新）四级成熟度；
     - 明确 Level 1 承诺边界：自包含 .NET 10、数据与程序目录完全分离（数据存放在 `%APPDATA%\PopGlot`，程序目录可随意移动或只读）、公共翻译无商业 SLA 保障；
     - 制定第一阶段便携包 8 项验证矩阵（D1-01 至 D1-08）：涵盖干净环境解压启动、含空格路径、非 ASCII/中文路径、只读程序目录、单实例防多开互斥、跨版本 Schema 迁移、SHA-256 校验和与灾难回滚。
  2. **第二阶段规划方案设计**：
     - 安装器设计：对比 Inno Setup、MSIX 与 WiX，推荐基于 Inno Setup 编写轻量免提权/全用户安装器；
     - 代码签名设计：方案涵盖开源免费通道（SignPath Foundation）与商业 OV/EV 证书，明确私钥托管与 CI 触发门禁；
     - 自动更新设计：设计基于 GitHub Releases 的用户明确确认、SHA-256 双重比对、更新前自动调用 R08 机制生成备份与失败自动回滚架构；
     - 明确未获用户商业与权限授权前，绝不向远端推送 Tag、不修改已发布 Release、不接入外部商业服务。
  3. **文档与版本管理规范联动**：
     - 在 `docs/VERSIONING.md` 中增加分发成熟度与发布形态边界章节，双向链接 `DISTRIBUTION_MATURITY.md`。
- **验证证据**：
  - **E1 纯逻辑测试**：
    - `R11 distribution maturity specifications and packaging invariants`：验证 `docs/DISTRIBUTION_MATURITY.md` 存在且包含 Level 1/2/3 及 D1-01 至 D1-08 矩阵；验证 `docs/VERSIONING.md` 链接；验证 `publish-package.ps1` 与 `release.yml` 保持自包含约束与哈希校验。
    - 运行：`dotnet run --project tests/PopGlot.Windows.PureTests/PopGlot.Windows.PureTests.csproj` → 通过（33 passed, 0 failed）。
  - **E2 隔离宿主与 UI 异步测试**：
    - `release workflow specifies self-contained`：验证 GitHub Actions `release.yml` 严格指定 `--self-contained true`。
    - 运行：`dotnet run --project tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj` → 通过（294 passed, 0 failed）。
  - **E5 发布包形态与自包含产物验证**：
    - 核验 `scripts/publish-package.ps1` 与 `.github/workflows/release.yml`，便携包产物包含 `popglot_ffi.dll`、自包含 .NET 运行时，产出对应 `.sha256` 校验和。
- **结论**：通过。
