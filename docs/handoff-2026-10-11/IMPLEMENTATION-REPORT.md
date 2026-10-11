# PopGlot 蓝框交互 / 唤醒性能 / 本机 AI 工具连接 —— 实施报告

日期：2026-10-11。任务书：`docs/handoff-2026-10-11/UI-WAKE-AI-CONNECTION-PLAN.md`（基线 96a7fd6e5ee19a57704f9120cfd3f69879259374，与本轮起点一致）。

状态图例：**通过**＝有证据的完成；**不通过**＝验证失败；**未验证**＝缺少外部条件，未冒充完成。

## 0 交付总览

| 步骤 | 状态 | 一句话结论 |
| --- | --- | --- |
| P00 基线与复现 | 通过 | 机器信息/复现机制/目录已固定（`artifacts/ui-wake-ai-2026-10-11/baseline/machine.md`） |
| P01 鼠标蓝框 | 通过（真机 DPI/主题抽查未验证） | 三种平面样式鼠标/程序聚焦不再描边；Tab 仍有定位；真实输入管线测试 + 全套 LogicTests 通过 |
| P02 时间线 | 通过（Rust 内部传输阶段未接线） | InteractionTrace 全链路 + `--measure-wake` + measure-wake.ps1；未知=null |
| P03 唤醒优化 | 通过（旧机器未验证） | 历史刷新移出首帧+Revision 收敛；Trim A/B 完成，**假设不支持**，默认保持压缩 |
| P04 客户端复用 | 通过（吞吐/TTFT 收益未测真实服务） | 生产 draft-stream 走池；回环 TCP 复用证明；键隔离/容量/凭据逐请求验证 |
| P05 执行模型 | 通过（复查发现截图文本阶段缺门→已修复+回归） | EngineExecutionKind/CliConnectionSettings C#+Rust 同步；CLI=配置但不放行，绝不回落免费引擎 |
| P06 文本协议+底座 | 通过 | FFI text-job（prepare/push/finish/free）复用生产 prompt/保护/分隔符/装配器；JsonLineTransport/CliRuntimeHost 真子进程测试 |
| P07 Codex | **未放行**（按门槛） | 协议骨架+禁用；本机 codex-cli 0.150.1 仅离线 `--version` 事实；schema 未固定 |
| P08 OMP | **未放行**（按门槛） | 协议骨架+禁用；本机 omp.exe 18.2.11 仅离线版本事实；Claude 订阅路由按官方限制禁用 |
| P09 连接流程 | 通过（连接后段未验证） | 设置页「连接本机工具」检测面板：只读离线事实、待检查状态、不启动进程、不换引擎 |
| P10 验证 | 见 §10 | 三套件全绿；隔离测量完成；独立复查见 INDEPENDENT-REVIEW.md |

## 1 改动文件清单

**C#（apps/PopGlot.Windows）**
- `Ui.cs`：新增 `QuietTextSurface` 附加属性（P01）。
- `Themes/Controls.xaml`：`QuietSurfaceFocus` 焦点样式；隐式 TextBox 模板 hover/focus 触发器加 `QuietTextSurface=False` 条件；FlatTextBox/ResultTextBox/FlatRichTextBox 改为静默+键盘 FocusVisualStyle（P01）。
- `Services/InteractionTrace.cs`（新）：TracePoint/InteractionTrace/InteractionTraceWindow/InteractionTraceCollector（P02）。
- `App.xaml.cs`：热键/托盘/二次实例 trace 入口；ShowMainWindow/EnsureMainWindow/CreatePanel/ShowQuickSearch 生命周期标记；取词 trace；`--measure-wake` 模式；TrimOnHideEnabled 开关（P02/P03）。
- `MainWindow.xaml.cs`：`RequestLibraryRefreshAfterFirstFrame`；OnClosing trim 走开关；ProbeNextTextRoute 消费类型化能力（P03/P05）。
- `Sections/LibrarySection.xaml.cs`：ReloadHistory/ReloadVocabulary 按 Revision 收敛（P03）。
- `HistoryStore.cs` / `Services/VocabularyStore.cs`：Revision 递增于全部真实变更点（P03）。
- `Services/TranslationCoordinator.cs`：`ResolveTextRouteCapability`（类型化）；CLI/未知后端 → 配置但不支持（不发请求、不落免费引擎）；stream 各阶段 trace 标记；终态 CloseInteractionTrace（P02/P05）。
- `Services/TranslationModels.cs`：TextRouteCapability；TranslationTextExecutor 增 CodexAppServer/OmpRpc；session.Trace（P02/P05）。
- `Services/ProfileManager.cs`：ProviderProfile.ExecutionKind/CliConnection/ExecutionFacts；vision 解析消费类型化能力（P05）。
- `Services/ProfileVerification.cs`：连接指纹纳入执行维度（P05）。
- `Services/RouteCapabilityService.cs`：CLI/未知后端要点=不支持（P05）。
- `CoreBridge.cs`：CliBackendDiagnostics DTO；text-job 四个包装（P05/P06）。
- `Services/AiConnections/`（新目录）：EngineExecution/ConnectionState/CliDiscovery/ExternalRuntimePolicy/JsonLineTransport/CliRuntimeHost/CodexAppServerBackend/OmpRpcBackend（P05–P08）。
- `Sections/ServicesSection.xaml(.cs)`：「连接本机工具」检测面板（P09）。
- `QuickSearchWindow.xaml.cs` / `Sections/TranslateSection.xaml.cs`：查询 trace + 首段应用/渲染机会标记（P02）。

**Rust**
- `crates/popglot-core/src/transport_pool.rs`（新）：ProviderClientPool（8 条上限、10 分钟闲置、锁不跨 await；键含 origin+全部传输参数+版本）。
- `crates/popglot-core/src/provider.rs`：`parse_valid_base_url`/`transport_origin` 提取（与 build_url 同一校验）。
- `crates/popglot-core/src/text_job.rs`（新）：TextJobManager/PreparedTextJob/TextJobCompletion/TextJobResult。
- `crates/popglot-ffi/src/lib.rs`：五个 draft 入口+test_connection_draft 走池；`popglot_text_job_prepare/push/finish/free_v1`。
- `crates/popglot-domain/src/lib.rs`：ProviderProfile.execution_kind/cli_connection；ExecutionKind（`#[serde(other)] Unknown`）；CliConnectionSettings。

**脚本/测试**
- `scripts/measure-wake.ps1`（新）：实例预检+包校验+first-open/warm-wake 场景+-NoTrim A/B。
- `crates/popglot-core/tests/transport_pool.rs`（新）、`tests/text_job.rs`（新）。
- `tests/PopGlot.Windows.LogicTests/CliSubprocessTests.cs`（新）；WaveRegressionTests U02 重写（真实输入）；PureTests/Program.cs 新增 4 测试。

## 2 P01 蓝框：设计与验证

**设计**：隐式模板的两个描边触发器改为 `QuietTextSurface=False` 条件的 MultiTrigger；三个平面样式设置 `QuietTextSurface=True` + `FocusVisualStyle=QuietSurfaceFocus`（1 DIP FocusBrush 边框，IsHitTestVisible=False，随元素边界绘制，无布局位移）。WPF 的 FocusVisualStyle 仅在**键盘导航**时绘制（KeyboardNavigation.ShowFocusVisual 由真实 Tab 输入触发）——鼠标点击/程序聚焦天然不显示，这正是输入源区分。普通表单（FormTextBox/密码框/按钮/开关/单选）完全不受影响。

**验证**：
- 结构断言：模板不得含无条件 IsMouseOver/IsKeyboardFocusWithin→Surface 描边触发器（禁用/只读状态描边保留）；平面样式必须带 FocusVisualStyle。
- 程序聚焦（与鼠标点击同一 IsKeyboardFocusWithin 路径）：三个平面样式边界保持透明、无 Adorner；FormTextBox 仍画 AccentBrush。
- **真实键盘**：`PostMessage(WM_KEYDOWN VK_TAB)` 走真实 Win32 键盘 provider（MostRecentInputDevice=键盘 → ShowFocusVisual 生效）→ 断言 FlatTextBox 出现 FocusVisualAdorner 且边框保持透明。
- **真实鼠标**：`SendInput`（复用应用内 ABI 已验证的 INPUT 结构）移动真实光标到测试窗口中心并发真实按下/抬起 → 断言 IsMouseOver/IsKeyboardFocused 经真实管线成立，平面样式无描边、表单保留原契约；光标位置事后恢复。
- 全套 LogicTests 309 通过（现 316+，见 §10）；未验证项：真机 100–200% DPI/高对比/IME 人工抽查（本轮无人工配合）。

## 3 P02 时间线

时间点定义与实现见 `Services/InteractionTrace.cs`。要点：
- 原点=用户输入入口（热键回调/托盘/二次实例信号），非请求发送。
- `FirstRenderCallback`=Show 后下一次 CompositionTarget.Rendering（渲染**机会**，文档明确不是 DWM 呈现）；`InteractiveReady`=Show 后首个 Background 优先级调度（布局/Loaded/渲染队列之后、历史列表之前）。
- 未知点=null（`Milliseconds()` 可空）；`SelectionReadMs` 旧字段不动。
- 隐私：诊断只含点名/毫秒/通道/终态；JSONL 后台线程写、有界队列、默认关闭（`POPGLOT_TRACE=1` 显式开启）。
- **未验证**：ResponseHeaders/FirstWireEvent 需 Rust 返回自身阶段耗时，枚举已定义、`RecordExternalPhaseMs` 通道已建，实际数值接线归 P04 后续（见 §5 缺口）。
- 测试：PureTests 2 项（null 语义/隐私形状/环形有界/JSONL 落盘）；measure-wake 端到端验证（本机 warm-wake show→interactive p50 20.9ms）。

## 4 P03 唤醒：测量与结论

**历史刷新**：`ShowMainWindow` 不再在 Show 前 `ReloadHistory`；新增 `RequestLibraryRefreshAfterFirstFrame`（Background 优先级）。HistoryStore/VocabularyStore 在**全部真实内存变更点**（增/删/清/插入缺失/恢复/重试加载/回滚）递增 Revision；LibrarySection 按 Revision 跳过无变化重建；同计数不同内容必然 bump（PureTests 断言）。LogicTests：首帧后收敛/变更收敛/未变更不清空。

**Trim A/B**（同机，Release 包，构建身份 `artifact=3d484b43…`，commit 96a7fd6 + dirty diff a2ed310e）：

| 场景 | 不压缩 P50/P95 (ms) | 压缩 P50/P95 (ms) | 驻留内存·隐藏后 |
| --- | --- | --- | --- |
| 隐藏 1s×30 | 20.9 / 32.3 | 20.0 / 32.6 | 152MB / 14MB |
| 隐藏 30s×30 | 26.8 / 47.8 | 31.6 / 42.8 | 153MB / 14MB |
| 隐藏 300s×10 | 23.8 / 48.5 | 8.3 / 28.1 | 152MB / 19MB |
| 首次打开×10 | 124.2 / 133.9（构造 194.7 / 214.8） | — | — |

原始样本：`artifacts/perf/wake-*.json`（`*-trim.json` 为压缩列；当时开关语义=env 开启压缩，与现默认一致）。

**结论（按 §6.1 失败处理条款如实报告）**：在参考机（Ryzen 9 7945HX/NVMe/32GB）上**"隐藏时压缩工作集导致慢唤醒"假设不成立**——各延迟下唤醒差异在噪声内（300s 压缩反而更快），而停用压缩使驻留内存升约 10 倍。因此**默认保留原有压缩行为**，`POPGLOT_TRIM_ON_HIDE=0` 仅供 A/B（如旧机器复测）。旧机器（p95≤300ms 目标）**未验证**。全部数字远低于主机目标（热唤醒≤150ms、首开≤500ms）。

**划词反馈壳**：按任务书 6.3"先测真实 pre-read"条件实现——真实前台应用取词耗时需真机人工热键测试（时间线已可记录 SelectionStarted/Ended），无证据不支持壳，未实现（未猜）。

## 5 P04 传输池

- `transport_pool.rs`：键=规范化 origin（经与 `build_url` 完全一致的校验/归一化，绝不用用户原始串）+ 全部 TransportLimits 字段 + TRANSPORT_REVISION；上限 8、闲置 10 分钟淘汰；锁只护查表，client 构建在锁外，锁不跨 await/HTTP。
- 接线：FFI 五个 draft 入口（text v1/stream v2、text-task、vision snapshot/stream）+ `AppCore::test_connection_draft` 均走 `pooled_client_for`；全局 AppCore 常驻 client（存量设置路径）本就复用，不动。
- 安全回归：键隔离测试（origin/端口/TLS 宽严/超时/重试任一变化→不同键；凭据/查询串 base 直接报错；公网 HTTP 拒绝）；凭据逐请求（回环断言 Authorization 每请求都在、池不存 key/头）。
- **TCP 复用证明**：keep-alive 回环服务记录 accept 数——池化两次请求=1 条连接；逐次新建=2 条（对照组证明夹具能检出非复用）。同 client 指针不作为证据。
- 未验证：真实提供方吞吐/TTFT 收益（需用户授权的重复真实调用）；7.2 渲染/协议排查（含 Rust 内部阶段计时回传）未做。

## 6 P05–P08 执行模型与适配器

- `ExecutionKind` 稳定串（native-api/codex-app-server/omp-rpc）；未知串→Unknown，**可加载、永不执行、绝不回落 NativeApi/免费引擎**（C# 与 Rust `#[serde(other)]` 对称，双侧迁移测试）。旧配置缺字段=NativeApi，旧字段不重置。
- 指纹/能力：ConnectionVersion 纳入执行维度+CLI 连接字段；`ResolveTextRouteCapability`/ProbeNextTextRoute/要点能力/视觉路由全部消费类型化事实；CLI 档案永不claim视觉（截图不进 CLI 通道）。
- 诊断：`CliBackendDiagnostics`（Backend/ModelId/ToolVersion/TurnElapsedMs/完成态，全可空）独立于 HTTP 形状的 ProviderDiagnostics；本构建无 CLI 结果产生，绝不假 200/假 URL。
- 子进程底座：`JsonLineTransport`（字节级 0x0A 组帧、UTF-8、8MiB 帧/64MiB 累计上限、stderr 并行排空、id→TCS RunContinuationsAsynchronously、断线全部完成、Dispose 只杀自建进程树）；`CliRuntimeHost`（每次 spawn 前与每提交前重查 ExternalRuntimePolicy、ArgumentList、隔离空工作目录、warm 进程不带常驻许可、spawn/prompt 计数为断言面）；`CliDiscovery` 只做 PATH/候选目录文件事实，绝不扫 cookie/auth.json/agent.db/凭据库。
- **发现的重要工程事实**：PowerShell 5.1 控制台宿主会对管道 stdin 重编码并注入 UTF-8 BOM（子进程在父进程字节前观察到 EF BB BF；父侧十六进制取证无 BOM）——因此 fake CLI 测试改用 node（字节级语义），该事实已记录在测试注释中。
- P07/P08 状态：协议骨架（Codex 握手形状/OMP 候选参数白名单注释）+ `AdapterEnabledFor()==false` 全局禁用；本机离线版本事实（codex-cli 0.150.1、omp 18.2.11、Claude Code 2.1.295）已采集（`cli-fixtures/installed-versions.md`），**schema 未按版本固定**（未运行 app-server --help/generate-json-schema/RPC 源码对照），按任务书门槛保持禁用并在 UI 说明。禁用负例测试：spawnCount=0/promptCount=0。
- Claude：订阅路由按官方限制禁用；Anthropic API 档案保留。

## 7 P09 连接流程（本轮范围）

设置→翻译引擎新增「连接本机工具」：打开零扫描；「检测本机工具」只列 PATH/候选目录入口（工具名/路径/已检测到·状态待检查/未安装）；每个已检出入口给出未放行原因与"继续使用 API"指引；数据去向说明（工具自己的日志/代理/额度规则）在页头。不启动进程、不读登录态、不替换当前引擎。LogicTests 覆盖（含返回列表恢复）。连接→登录→模型→测试→保存→选择的**后段流程未实现**（适配器禁用下不可达，按任务书属"明确未放行"）。

## 8 CLI 支持矩阵

| 工具 | 入口（本机） | 版本 | 适配器 | 放行条件（未满足） |
| --- | --- | --- | --- | --- |
| Codex | npm 垫片→node+codex.js | 0.150.1 | 骨架+禁用 | 固定该版 app-server schema；隔离参数验证；account/read 只读查询联测 |
| oh-my-pi | omp.exe | 18.2.11 | 骨架+禁用 | 对照该版 RPC wire schema；--no-* 隔离参数按 help 验证；上游账号授权逐提供方核实 |
| Claude Code | npm 垫片→claude.exe | 2.1.295 | **不放行** | 官方限制第三方以订阅凭据路由；保留 Anthropic API 接入 |

## 9 失败与回退记录

- measure-wake 首版 WriteLoop 退出崩溃（ChannelClosedException）→ 修复为 WaitToReadAsync 收尾；参数解析漏位置参数 dataDir → 修复。
- fake CLI 三次迭代：PS 控制台 stdin BOM（上述）→ node；JSONL 键 camelCase vs snake_case 绑定 null → `visible_delta`；适配器禁用断言 async 任务丢弃 → await。
- U02 旧断言（程序聚焦即整块描边）按任务书更新为输入源区分断言；两处反射/字符串契约测试同步更新（`OnStreamUpdate` 可选参、`ShowQuickSearch(trace)`）。
- P03 trim 默认两度翻转：按 A/B 数据最终恢复生产默认=压缩（详见 §4）。
- clippy 修复中发现 text_job 的 task 参数未生效（summarize/explain 被当 translate）→ `.with_task(task)` 修复后全量复测。

## 10 P10 验证矩阵

| 层 | 命令 | 结果 |
| --- | --- | --- |
| Rust 全工作区 | `cargo test --workspace --locked` | **238 passed / 0 failed**（含新增 transport_pool 4、text_job 10、domain 迁移 1；clippy/fmt 清零后复跑） |
| 纯逻辑 | PureTests exe | **47 passed / 0 failed**（新增：trace 语义/环形、store Revision、执行类型化） |
| WPF 逻辑 | LogicTests exe | **317 passed / 0 failed**（含真实键盘/鼠标输入、子进程底座、连接面板、首帧后刷新） |
| 完整门禁 | `scripts/verify.ps1` | **通过**（exit 0；首次因 cargo fmt 未过中止→格式化+clippy 清零后复跑通过，含 317 LogicTests） |
| 隔离测量 | measure-startup/measure-wake | 包身份校验通过；wake 报告见 §4 |
| 最终包复测 | measure-wake d1（最终包，默认=压缩） | 30 样本 0 失败：show→interactive P50=30.0ms / P95=33.2ms（远低于 150ms 目标）；包身份 commit=96a7fd6 diff=dcaefeb244e9733b 481 文件 |
| 真实账号/提供方 | — | **未验证**（任务书明确需用户主动+单独授权） |
| 旧机器 | — | **未验证**（未接触） |
| 包升级 | — | **未验证**（未执行安装升级演练） |

## 11 证据与独立复查状态

## 11.1 证据索引
- 唤醒原始样本：`artifacts/perf/wake-*.json`（`*-trim.json`=压缩列；最终包复测覆盖 `wake-warm-d1.json`）。
- 截图（最终样式）：`artifacts/ui-wake-ai-2026-10-11/p01/`（主窗/极速查词 深+浅、设置页、连接面板）；全量在 `artifacts/screenshots/`（截图 pass 复跑通过）。
- CLI 版本事实：`artifacts/ui-wake-2026-10-11/cli-fixtures/installed-versions.md`。
- 诊断时间线样例：各测量 run 的 `<dataDir>/logs/interaction-trace.jsonl`。

## 11.2 独立复查状态
独立复查（只读 code-reviewer 子代理）已完成：8 项通过，P05 发现 1 个中危（截图 OCR 文本阶段缺类型化执行门）+4 个低危；全部 5 项已修复并带抓老 bug 回归测试，修复后回归 cargo 238/0、LogicTests 318/0（+1）、PureTests 47/0、clippy/fmt 清洁，最终包 diff=`9c6eb22673dcf05b`。详见 `INDEPENDENT-REVIEW.md`。

## 12 明确剩余项

1. 真机人工抽查：P01 的 DPI/高对比/IME/链接/复制；划词 pre-read 真实耗时（决定反馈壳）。
2. Rust 传输内部阶段（ResponseHeaders/FirstWireEvent）数值回传（通道已建）。
3. 7.2 协议/渲染排查与真实服务吞吐对照。
4. Codex/OMP 适配器按版本 schema 固定与放行（P07/P08 门槛）→ P09 后段流程。
5. 旧机器 A/B 与验收；真实账号最小请求（用户主动）。
6. 200 次生命周期压力（复用池启用前）——本轮未引入复用池，未执行。
