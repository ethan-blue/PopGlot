# PopGlot 整改执行记录

基线提交：`feb77d5`（release: prepare PopGlot 0.1.3）
当前检查日期：2026-09-05
工作区原有修改：仅未跟踪目录 `docs/review-2026-09-05/`（评审文档本身）；无其他未提交代码修改。
环境：.NET SDK 10.0.400；Rust 1.98.0 / Cargo 1.98.0；Windows 10.0.26200 x64；PopGlot.exe 开工时未运行。

## 任务状态总表

| ID | 状态 | 根因/变更简述 | 证据 | 未验证/阻塞 |
|---|---|---|---|---|
| T00 | Verified | 隔离测试与可信基线：StoragePaths 注入、内存凭据替身、Demo 夹具、HTTP 护栏、哈希守卫 | 见下文 T00 记录 | 多显示器/真机 DPI 环境未涉及 |
| T01 | Verified | 授权上下文 FreeEngineAuthorization 进入发送边界；自动健康探测移除；12 组合发送计数矩阵 | 见下文 T01 记录 | 撤销许可不中断已在途请求（见记录限制） |
| T02 | Verified | MarkdownPresenter 结构优先解析；三入口复制统一 formatter；OCR 原文不回写 Pangu；面板并入共享剪贴板入口 | 见下文 T02 记录 | 真机 Narrator/触控未涉及 |
| T03 | Verified | IsCleanCompletion 唯一资格契约；Rust is_partial 接入 C# DTO；Partial 不写历史/不自动复制；重复 final 幂等；代码块按钮继承资格 | 见下文 T03 记录 | 动态代码块在真实流式会话中的端到端未驱动（以渲染器级测试覆盖） |
| T04 | Verified | 恰好一次 token 恢复（Rust domain/core）+ 命名空间避撞 + 免费引擎共享保护链（FFI）+ 响应 4MiB 累计上限/HTML 拒绝/缓存严格有界 | 见下文 T04 记录 | 真实免费引擎线路的遮蔽回环未做线上验证（按规则禁止公网） |
| T05 | Verified | Loopback/PrivateNetwork/Internet 三分类（Rust+C# 同夹具含 FFI 对拍）；allow_lan_endpoints 独立许可（v7 迁移不扩大）；validate_execution 分类门禁；仅同源重定向；路由说明诚实化 | 见下文 T05 记录 | 真实 LAN 设备/真机多显示器未涉及 |
| T06 | Verified | 云端语音独立许可（隐私页入口+Microsoft 目的地说明）；languageTag 优先选声；合成取消+CancellationToken；EndOfMessage 分片组装；5000字/8MiB 限额；双前缀临时清理；设置保存不再丢 CloudSpeechEnabled/CloseHintShown | 见下文 T06 记录 | 真机扬声器播放、Narrator 播报未测 |
| T07 | Verified | 词库结构化保存结果+落盘后才提交内存；限额（8000字/10000条/32MiB）明确拒绝；星标身份=词面+语言对（大小写保留）；历史损坏隔离备份+UI告知；CSV公式前缀防护；Anki HTML转义；并发丢失更新缺陷修复 | 见下文 T07 记录 | 32MiB 文件上限分支未单测（在其余限额下实际不可达）；无写权限/磁盘满未单独模拟（与路径为目录同走 IOException 分支） |
| T08 | Verified | DiagnosticsLog 统一诊断出口：Authorization/Bearer/密钥形态/hex块/URL query 脱敏+长度界（消息400字/栈24行）；托盘摘要脱敏；1MiB/文件轮转+10MiB 总量+7天保留；崩溃兜底取消在途请求 | 见下文 T08 记录 | 真实崩溃样本未采集（用合成异常验证）；“恢复可用状态”以取消在途请求+既有 Handled 兜底实现，无独立断言 |
| T09 | Verified | F10 根因修复：隐式 TextBlock 样式不再设 Foreground（样式 setter 掩蔽按钮前景），改窗口级 TextElement.Foreground 环境继承；新增 FocusBrush（焦点环 2DIP）；DangerButton hover/pressed 主题化配色（暗色弃白字亮红底）；审计扩展+视觉树反例+10 轮主题切换 | 见下文 T09 记录 | 高对比度（SystemParameters.HighContrast）映射未实现；既有元素原地热切换留待 T11 真机 |
| T10 | Verified | DPI 生产器重写：显式逻辑 DIP×缩放=像素，位图尺寸自洽断言；36 张矩阵（6 页面×3 缩放×2 主题）+ 画布填充断言（≥98% 不透明）+ 合成破图自检；写入重试+OnLoad 解码不持句柄 | 见下文 T10 记录 | 错误状态截图 fixture 未建（T11 旅程）；多显示器/WindowChrome 真机未验证 |
| T11 | Verified | 剩余子项完成：<720 DIP 上下堆叠（源≥160 DIP 在上/译文在下/中轴隐藏）；长模型ID/长错误/错误状态截图 fixtures；流式阅读滚动保持断言；空态引导随布局换向。另修复测试宿主缺 DispatcherSynchronizationContext 的环境差异 | 见下文 T11 记录 | IME 真机组合行为、Narrator、真实显示器 200%/混合 DPI 未验证。**实例退出后全量连续回归已补跑：156 passed / 0 failed，exit 0（2026-09-05 19:20，Release）** |
| T12 | Verified | 长输入会话规划：Rust 域纯函数 plan_translation_segments（段落/行/句/词边界原子切分、围栏代码原子、超限拒绝）+ FFI popglot_plan_segments + C# 分段编排（≤8段、顺序合并、会话级 10 分钟 deadline/共享取消、片段 Partial 可见、拒绝发送前失败）；短文本保持单请求自适应预算 | 见下文 T12 记录 | 真实供应商长文质量/费用未测（规则禁止）；视觉直译路径不经过分段（视觉请求本身）；provider 响应 4MiB 累计界限依赖既有流缓冲硬限 |
| T13 | Verified | 截图路由单一策略来源：Rust domain 持有决策表（select_route 重写为任务书表：VisionDirect 阻断不降级/VisionOcr 双前提/Auto 诚实本地优先/LAN 许可/未知分类保守拒绝）；删除伪能力字段（looks_like_code/complex_layout/image_quality/ocr_confidence 常量假输入）；C# ResolveRoute 改为「采集事实→FFI 决策→映射管道」；MayUploadImage 语义统一为「图片离开设备」（loopback=false 但管道照跑）；FFI parity 测试锁两语一致 | 见下文 T13 记录 | 真实 LAN 设备连通；CoreBridge.TranslateScreenshotAsync 死路径（Rust 决策的旧消费者）未删除——标注待 T17 架构整理处理 |
| T14 | Verified | 模型推荐证据链补齐：合成当前模型恒 Unknown（不虚构 Vision）；NormalizeEndpoint 保留 path 大小写/剥离默认端口/query 指纹化（不存明文 secret）；样本数值有效性（NaN/负数/无限拒绝）+7 天有效期（可注入时钟）；多样本 TTFT/速度中位数聚合，n<5 标注「试测」；推荐理由不再称「实测响应快」 | 见下文 T14 记录 | UI 徽章路径（有数据才显示「本机实测」）由既有 ModelRecommendationUiTests 覆盖；真实 benchmark 仓库仍不存在（无数据可导入，属诚实空态） |
| T15 | Verified | 性能仪表诚实化：删除假 TrayAvailable 指标（Start→Stop 立停）；伪指标更名（CoreInitialize/WindowConstruct/WindowConstructArrange/CancelNoopOverhead/测试宿主 WS 明示非应用空闲）；新增 App `--smoke-startup` 显式测量模式（隔离数据目录+真实启动全路径+marker 文件）与 scripts/measure-startup.ps1（N 次启动 P50/P95 预算判定）；**真实测量 30 次启动 P50=181ms P95=200ms（预算 600/1200ms 内 PASS）**，数据在 artifacts/perf/startup.json | 见下文 T15 记录 | 热键→首帧、真实空闲工作集/空闲 CPU、1080p OCR 仍未测（需真机交互环境，见记录） |
| T16 | Verified | 流式复杂度注释诚实化：O(1)/non-blocking/zero-allocation 改为真实契约（append O(k)/drain O(pending)/锁有界非无锁/按节奏 drain）；未测到热点前不动实现（按规则保留） | 注释 diff + 163 全绿（行为不变） | profiler 采样数据未采集 |
| T17 | Verified（第一切片） | 死路径删除：CoreBridge.TranslateScreenshotAsync 双重载+TranslateViaLocalOcrAsync+ScreenshotTranslation+popglot_translate_vision_v2 P/Invoke（均无调用方，生产走 Coordinator/ResolveRoute）；ServicesSection 抽离纯规则到 ServiceDraftCoordinator（Validate/BuildDraft/ComputeRecommendations/ParseHeaders 无 WPF 无 I/O），视图变薄适配器，credential 顺序守卫保留在视图 | 见下文 T17 记录 | ServicesSection 其余 2000 行（测试连接/模型目录获取/列表渲染）未拆——后续切片；Controls.xaml 拆分未动 |
| T18 | Verified | README 版本/测试数/四模式/VisionDirect 语义/schema v7；DESIGN_SYSTEM token 表对齐 ThemeService 并标注旧配色历史；看板 113 项标注历史时点；SPEC/PRIVACY 补 VisionOcr 与阻断语义 | 见下文 T18 记录 | TRANSLATION_BENCHMARK/UX_DECISIONS 未逐字复核 |
| T19 | Todo | 独立整体验收（建议负责人另开审查上下文执行 README 独立验收提示词；实现侧自验已尽：T00–T18 全部 Verified 或标注） | 实现侧证据见各任务记录 | 交互式验收与真机项待独立执行 |

状态取值：Todo / InProgress / ImplementedNeedsVerification / Verified / Blocked / NotApplicableWithEvidence。

## 每轮记录

### 任务编号：T00（InProgress）

**核实**（当前代码验证评审结论）：

- `tests/PopGlot.Windows.LogicTests/Program.cs` `RenderScreenshotsAndMeasureBaseline`：直接 `new HistoryStore()` / `new VocabularyStore()`（默认落到 `%LOCALAPPDATA%\PopGlot` 真实用户数据）；调用 `CoreBridge.Initialize()`（读取真实 LOCALAPPDATA 配置目录）；`CreateServiceEditorPreview` 使用真实厂商模板 `ProviderProfile.CreateGemini()`。证实评审"截图使用虚构配置失败"的发现。
- `RenderAndSaveAtDpi`：`logicalWidth = width / dpiScale` 而位图 `width * dpiScale`，证实 F11 测试生产器错误。
- `ProfileManager` 有 `ConfigPathOverride` 测试缝；`ShellSettingsStore.Load/Save` 有路径参数；`HistoryStore`/`VocabularyStore` 构造器有路径参数——注入点已存在但截图路径未使用。
- `CredentialStore` 全部为静态方法直接打 Windows Credential Manager，无替身缝；`ProfileManager` 多处直接调用 `CredentialStore.HasApiKey`。
- `OutboundPolicy` 已有 `SettingsLoader/SettingsSaver` 测试缝。
- `MainWindow.RefreshEngineStatus` 在 consent=Unset 时 `UsesFreeEngine` 为 true 并触发 `UpdateFreeEngineHealthAsync` → `FreeTranslateService.GetHealthAsync` → `TranslateAsync("ping N")` 真实出网（F01 证实，T01 修复）。
- 开工时 `tasklist` 确认 PopGlot.exe 未运行，Debug 构建无文件锁阻塞（评审期间曾锁定）。

**基线命令与真实结果**（修改任何代码前，2026-09-05）：

| 命令 | 结果 |
|---|---|
| `cargo fmt --check` | 通过（exit 0） |
| `cargo test --workspace --locked` | 150 passed / 0 failed（exit 0） |
| `cargo clippy --workspace --all-targets --locked` | 干净（exit 0） |
| `dotnet build tests/PopGlot.Windows.LogicTests/... -c Release` | 0 警告 0 错误 |
| `PopGlot.Windows.LogicTests.exe`（Release） | 121 passed / 0 failed |
| `tasklist` PopGlot.exe | 未运行，Debug 构建无文件锁阻塞 |

基线运行截图段仍读取真实用户数据（当时未隔离）——对应截图已在隔离后重新生成，旧含真实服务信息的截图不再向上传（本仓库 artifacts/ 未跟踪，不入库）。

**实现**：

- 新增 `apps/PopGlot.Windows/StoragePaths.cs`：显式可注入存储根（`RootOverride`），生产默认 null 时路径与旧版逐字节一致；`ShellSettingsStore`/`HistoryStore`/`VocabularyStore`/`ProfileManager`/`CoreBridge.Initialize`/`App.LogCrashToFile` 全部改走 StoragePaths。
- 新增 `tests/PopGlot.Windows.LogicTests/TestIsolation.cs`：进程级隔离引导——临时根、Demo Text Service/demo-text-model 固定配置、合成历史/词库夹具、`InMemoryCredentialVault`、HTTP 护栏（公网拒发并计数）、真实配置文件 SHA256 快照与 `VerifyRealFilesUnchanged`。
- `CredentialStore` 增加 `OverrideVault` 替身缝；`FreeTranslateService` 增加 `HttpSenderOverride` 发送缝（T01 也使用）；`CoreBridge.Initialize(string?)` 支持指定目录。
- `Program.cs`：Main 首行 `TestIsolation.Initialize()`；新增测试 "test isolation is active"、"real user config unchanged by the run"、"no unsanctioned public network send was attempted"；`RenderScreenshotsAndMeasureBaseline` 改用隔离存储与 Demo 档案。

**验证**：

- 旧错误被捕获：隔离首跑 `render screenshots and measure performance baseline: rendering the windows attempted a public-network request: expected <0>, got <1>`——即 F01 自动探测在窗口构建期真实出网，被护栏捕获并计数。
- 终验：`PopGlot.Windows.LogicTests.exe`（Release）→ **125 passed / 0 failed，exit 0**（含隔离断言与哈希守卫）。
- 截图目检（打开 PNG 确认，非仅存在性）：`main_window_dark.png` 页脚显示"内置免费引擎 · 未检测"、无真实用户数据；`settings_dark.png` 仅 Demo Text Service / demo-text-model；`service_editor_dark.png` 仅 Demo 档案。

**兼容性与迁移**：生产默认行为不变（RootOverride/OverrideVault/HttpSenderOverride 生产为 null）；`popglot_initialize` 原生侧幂等，重复 Initialize 安全。

**未验证条件**：EdgeTts WebSocket 尚无发送缝（T06 处理）；隔离哈希守卫假设运行期间真实 PopGlot 应用未并行运行。

**下一项**：T02，第一处代码入口 `Services/MarkdownPresenter.ToPlainText/AppendFormattedSpans`。

### 任务编号：T01（Verified）

**原问题**（当前代码复核，基线 feb77d5）：`MainWindow.RefreshEngineStatus` 在 consent=Unset 时 `UsesFreeEngine` 为 true → 自动 `UpdateFreeEngineHealthAsync` → `FreeTranslateService.GetHealthAsync` → `ProbeCoreAsync` → `TranslateAsync("ping N")` 直接静态 HttpClient 出网，全程不经过 `OutboundPolicy.AllowsFreeEngine`；`EngineHealthButton_Click` 强制探测同样无门禁。

**实现**：

- `OutboundPolicy`：新增 `FreeEngineAuthorization(Settings, IsOnceOnly)` 授权上下文；`AllowsFreeEngine` 三参数重载只在 Allowed/AllowOnce 分支签发授权；保留两参数兼容重载。AllowOnce 明确 `IsOnceOnly: true`，不作为后续探测许可。
- `FreeTranslateService`：`TranslateAsync` 增加 `authorization` 参数并在发送前 `EnsureOutboundAuthorized`（null 或快照离线→拒绝发送）；`GetHealthAsync(force, authorization)`——force 只刷新缓存，不替代授权；`ProbeCoreAsync` 同样携带授权；新增 `HasHealthResult` 供 UI 区分"未检测"与"不可用"。
- `ITranslationExecutor.TranslateFreeAsync` / `DefaultTranslationExecutor` / `TranslationCoordinator` 三条免费分支 / `CoreBridge.TranslateRecognizedTextAsync`：全部改为携带授权上下文调用。
- `MainWindow`：`RefreshEngineStatus` 移除自动探测，页脚显示"内置免费引擎 · 未检测"；`UpdateFreeEngineHealthAsync` 先过 `OutboundPolicy.AllowsFreeEngine` 门禁，未通过时 `PaintFreeEngineNotProbed`（"免费引擎未检测"+原因 tooltip）；切换菜单未探测时显示"未检测"。

**验证**（新增测试 `FreeEngineAuthorizationMatrixAtSendBoundary`，直接调用生产 Coordinator 与生产健康服务，断言发送计数而非返回值）：

- Consent{Unset,Denied,Allowed} × SafeDevMode{on,off} × Network{on,off} 共 12 组合：仅 Allowed+off+on 允许 1 次普通发送 + 1 次强制探测，其余全部 0 发送；Allowed 组合断言译文到达 mock（`mock-translation`，防"全断网虚假通过"）；未授权调用 `GetHealthAsync(force:true, null)` 断言 0 发送且不报成功。
- Unset 被拒后断言持久层仍为 Unset（未写成 Denied）。
- 隔离层回归守卫：截图段 `BlockedPublicSends == 0`（窗口构建/激活/渲染零出网）。
- 命令与结果：`PopGlot.Windows.LogicTests.exe`（Release）→ **125 passed / 0 failed，exit 0**。
- UI 证据：`artifacts/screenshots/main_window_dark.png` 页脚"内置免费引擎 · 未检测"。

**兼容性**：`AllowsFreeEngine` 两参数重载保留；授权在发送时校验快照，离线总开关优先级不变；授权失败的免费翻译会话仍以 Failed+可操作建议呈现。

**未验证**：撤销许可时不中断已在途请求（单发 GET、12s 总超时；撤销对下一次发送生效）——已记录为限制，未伪造"已取消"。

**下一项**：T03，第一处代码入口 `Services/TranslationModels.TranslationSession.IsSuccess`、`TranslationCoordinator.ApplyFinalResponse/WriteHistoryOnce`、各窗口结果动作门禁。

### 任务编号：T02（Verified）

**原问题**（当前代码复核）：`MarkdownPresenter.ToPlainText` 先用正则剥强调标记后去反引号，不区分代码与自然语言——`foo_bar_baz`→`foobarbaz`、`__init__`→`init`；`AppendFormattedSpans` 先对整行 FormatPangu 再分段，行内代码 `C:\用户data\file.txt` 展示成 `C:\用户 data\file.txt`。主窗直接复制原始文本、浮窗/查词复制损坏文本，不一致；面板 OCR 原文被 Pangu 回写；面板复制绕过共享剪贴板入口。

**旧错误如何被测试捕获**（对旧代码复现）：

- `FAIL markdown plain text...: Expected <foo_bar_baz>, got <foobarbaz>`
- `FAIL markdown visual...: the path code span must keep every character, got: ... C:\用户 data\file.txt ...`

**实现**：

- `MarkdownPresenter.ToPlainText`：结构优先——围栏块逐字保留；`TransformNaturalSegments` 先按行内 code span 切分，仅自然语言段做标题/列表/强调剥离；强调剥离仅对内容含空白或 CJK/全角字符的 run 生效（`**普通粗体**`→普通粗体；`__init__`/`foo_bar_baz`/`a*b*c`/`**GDP**` 保留）；占位符不再被静默删除。
- `AppendFormattedSpans`：先分段后排版；自然段逐段 Pangu，代码/占位符逐字渲染；接缝处按原 Pangu 字符类规则补空格（`使用\`ls\`命令`→`使用 ls 命令`，代码内不进空格）。
- 三入口统一：`TranslateSection.CopyResultToClipboardAsync`/`TranslateResultSpeak_Click`、`LibrarySection.CardCopy_Click` 改走共享 `MarkdownPresenter.ToPlainText`；面板 OCR 原文（`RecognizeOcrAsync`）不再回写 Pangu；`TranslationPanelWindow.TrySetClipboardAsync` 并入 `Helpers.CopyToClipboardAsync` 共享入口（新增 `ClipboardWriterOverride` 测试缝）。

**验证**：

- 新增测试：`markdown plain text preserves technical identifiers and code`（18 项断言覆盖任务书全部输入表）、`markdown visual rendering separates code from natural language`（FlowDocument 实际 inline 树断言代码逐字、路径无空格、粗体渲染）、`three entries copy the same agreed plain text`（同一模拟终态：查词生产状态机+生产复制句柄、浮窗真实按钮 RaiseEvent、工作台真实按钮点击，经内存剪贴板断言三者一致）。
- 命令与结果：`PopGlot.Windows.LogicTests.exe`（Release）→ **128 passed / 0 failed，exit 0**（3 个新测试 + 全部既有测试）。
- UI 证据：`artifacts/screenshots/translation_panel_dark.png`、`quick_search_dark.png` 目检正常，视觉无回归。

**兼容性**：复制/朗读/收藏输出对纯中文和普通英文文本与旧行为一致（强调剥离条件更保守）；`FormatPangu` 直接调用方行为不变。

**未验证**：WPF 之外的应用粘贴后的目标应用行为（如 Excel 公式解释）属 T07 导出策略。

**下一项**：T03，第一处代码入口 `Services/TranslationModels.cs` 的 `IsSuccess`、`TranslationCoordinator.ApplyFinalResponse/WriteHistoryOnce`、`TranslationPanelStreamGate.ShouldTriggerAutoCopy`。

### 任务编号：T03（Verified）

**原问题**（当前代码复核）：`TranslationSession.IsSuccess => Completed or Partial` 使 Partial 判为成功；`WriteHistoryOnce` 以其写历史；面板 `HandleSessionResultAsync` 把 Partial 会话送进 `_gate.OnCompleted` → 自动复制部分译文（"部分成功 · 已自动复制译文"）；C# `TranslationResult` DTO 丢失 Rust `is_partial` 字段，完成判定只看 warnings.Count；代码块动态复制按钮无条件可点。

**实现**：

- `TranslationModels`：删除 `IsSuccess`，新增 `IsCleanCompletion`（Completed + 无 warnings + 非空译文）与 `HistoryCommitted` 一次性落盘守卫。
- `CoreBridge.TranslationResult`：新增 `IsPartial` 字段（snake_case 反序列化绑定 Rust `is_partial`）。
- `TranslationCoordinator.ApplyFinalResponse`：`warnings>0 ∨ IsPartial ∨ 空译文` → Partial；`WriteHistoryOnce` 以 `IsCleanCompletion + HistoryCommitted` 门禁，重复 final 只写一次。
- `TranslationPanelWindow.HandleSessionResultAsync`：Partial → `_gate.OnFailed` → FailedWithPartial（文本保留可见、结果动作禁用、自动复制 0 次）；删除不可达的"部分成功 · 已自动复制"分支。
- `MarkdownPresenter.RenderToFlowDocument` 新增 `resultActionsEnabled` 参数：Partial/失败时动态代码块复制按钮禁用并提示"内容不完整，复制已禁用"；查词（`_state.CanCopy`）、浮窗（`session.IsCleanCompletion`）、主窗（`state.AreResultActionsEnabled`）三个调用点传入资格。

**旧错误如何被测试捕获**：新测试对旧行为断言（Partial 入历史、面板 Partial 自动复制、is_partial 丢失）在实现前即失败；实现后全部转绿。

**验证**（`PopGlot.Windows.LogicTests.exe` Release → **132 passed / 0 failed，exit 0**）：

- `partial final never persists or triggers side effects`：warnings final→Partial+历史 0；is_partial=true→Partial+历史 0；空译文→不资格+历史 0；干净 final→历史 1，同一 final 经 `ApplyFinalResponse` 重复投递两次仍为 1。
- `rust is_partial flag survives into the csharp dto`：`{"is_partial":true}` 反序列化后 `Result.IsPartial==True`。
- `panel routes partial session to blocked actions and no auto copy`：生产 `HandleSessionResultAsync` 驱动 Partial 会话 → gate=FailedWithPartial、`ShouldTriggerAutoCopy=false`、剪贴板写入 0。
- `code block copy button obeys eligibility`：`resultActionsEnabled:false` 时代码块按钮 IsEnabled=false 且带说明 tooltip；true 时可点。
- 既有 coordinator error/cancellation/success 历史测试全部保持通过。

**兼容性**：干净 Completed 的行为不变；历史条目格式不变；Rust 侧未改动（is_partial 本就序列化）。

**未验证**：真实供应商返回 is_partial 的线上样本（以契约测试代替）；Narrator 对禁用代码块按钮的播报。

**下一项**：T04（依赖 T02/T03 已满足），第一处代码入口 `crates/popglot-domain/src/lib.rs` `protect_tokens/restore_tokens`、`crates/popglot-core/src/streaming.rs` `StreamingTokenRestorer`。

### 任务编号：T04（Verified）

**原问题**：`restore_tokens` 用 `contains/replace` 全量替换且只报缺失——占位符重复出现被静默全部替换、未知索引不报；`StreamingTokenRestorer` 用 `matched: bool` 无法检测重复；固定 `PG_0000` 命名与原文撞车；免费引擎完全不做代码保护（F05）且响应体无累计上限、缓存仅按条数近似淘汰；缓存命中直接返回旧耗时伪装本次测量。

**实现**：

- `popglot-domain`：`restore_tokens` 改为对译文扫描全部占位符拼法（`⟦PG_n⟧/[PG_n]/[[PG_n]]/{PG_n}/<PG_n>/裸`），按四位索引映射 token——缺失→`dropped_terms`；重复→`duplicated_terms`（首处还原、其余保留可见）；未知索引→`unknown_placeholders`；`RestoredText` 增加后两个字段（serde default 向后兼容）。`protect_tokens` 在原文已含 `PG_\d{4}` 字面量时改用 `PGZ/PGQ/PGV` 命名空间，杜绝用户文本撞车。`protected_token_variants(placeholder)` 从占位符本体派生拼法（去掉 index 参数）。
- `popglot-core`：`StreamingTokenRestorer.matched:bool → match_counts:usize`；`finish()` 输出 dropped/duplicated/unknown；`unknown_placeholders_in` 扫描流式输出中未签发的索引。core 的流式与非流式恢复统一走 `apply_restoration`（三类不完整各自追加 warning 并置 `is_partial=true`）。
- `popglot-ffi`：新增纯函数导出 `popglot_protect_tokens` / `popglot_restore_tokens`（含 `# Safety` 文档），供免费线路共用同一套 Rust 正则。
- C# `CoreBridge`：`ProtectTokens/RestoreTokens` 包装 + `ProtectedTokenDto/ProtectedTextDto/RestoredTextDto`（snake_case 绑定）。
- `TranslationCoordinator.TranslateFreeWithTokenProtectionAsync`：三条免费分支（划词/截图回退/OCR 识别文）统一 protect→translate→restore，破坏占位符→warnings+IsPartial→（T03 资格）Partial 无副作用。
- `FreeTranslateService`：响应体按 64KiB 分块读取并累计上限 4MiB（无 Content-Length 也生效，声明超限直接拒）；`text/html` 明确报错；缓存改为单锁 LRU（≤256 条且 ≤16MiB）；缓存命中 `ElapsedMs=0`（不复用旧耗时）。

**验证**：

- Rust：`cargo fmt --check` 通过；`cargo clippy --workspace --all-targets --locked` 0 警告；`cargo test --workspace --locked` **158 passed / 0 failed**（新增 domain 6 例：同词两次双占位、重复/缺失/未知报告、原文撞车避让、兼容拼法恰好一次；streaming 2 例：流式重复/未知）。既有 `every_token_variant_splits_at_every_unicode_boundary`（Unicode 跨块）保持通过。
- C#：`PopGlot.Windows.LogicTests.exe`（Release）→ **134 passed / 0 failed，exit 0**。新增：`free engine runs the shared token protection chain`（mock 引擎收到 ⟦PG_0000⟧ 遮蔽文本、还原后标识符逐字保留、干净结果历史 1 条；占位符被破坏→Partial+warning 点名+历史 0）；`free engine transport boundary rejects oversize html and fakes`（无 Content-Length 超 4MiB 拒绝、声明超限拒绝、HTML 明确报错、缓存命中 sends==1 且 ElapsedMs==0）。
- 日志不含 GET query：`FreeTranslateService` 无任何 URL 日志输出（代码核查）。

**兼容性**：默认命名空间 `PG_0000` 行为与 prompt 示例不变；`RestoredText` 新字段 serde default，旧 JSON 可解析；FFI 只新增导出不改既有 ABI。

**未验证**：真实免费引擎（Google）对遮蔽文本的回环质量——按规则本轮禁止真实公网调用；PGZ 命名空间下模型的实际服从度未做线上验证。

**下一项**：T05（依赖 T01 已满足），第一处代码入口 `crates/popglot-core/src/lib.rs is_local 相关`、`apps/PopGlot.Windows/CoreBridge.cs ProviderSettings.IsLocalBaseUrl`、`ProfileManager.ResolveRoute`。

### 任务编号：T05（Verified）

**原问题**：Rust `is_local_base_url` 与 C# 同判把 10/8、172.16/12、192.168/16 视为"本地"——安全模式下允许私网地址、图片发往另一台 LAN 设备却显示"不离开本机"；reqwest 默认跟随跨源重定向（携带 query 原文）；免费引擎 HttpClient 默认跟随重定向；无独立局域网许可概念。

**实现**：

- Rust `popglot-domain`：新增 `EndpointClass {Loopback, PrivateNetwork, Internet}` 与 `classify_endpoint`（loopback= localhost/*.localhost/127/8/::1；private= RFC1918 + IPv6 ULA fc00::/7；未知/非法输入保守归 Internet，含非法端口拒绝）。`is_local_base_url` 保留为"非 Internet"语义（HTTP 明文许可沿用）。
- Rust `ProviderSettings/VisionProviderSettings/ProviderProfile` 新增 `allow_lan_endpoints`（serde default false；denied_when_missing 阴影结构同样 Option→false，沉默不扩权）。`validate_execution` 改为分类门禁：仅 Loopback 可离线运行；PrivateNetwork 需显式允许 LAN；仅 Internet 需要 Key。
- Rust `ProviderClient`：重定向策略改为同源最多 3 跳（`same_origin_redirect_policy`），跨源 302 直接报错。
- FFI 新增 `popglot_classify_endpoint` 纯导出（对拍用）。
- C# `ProviderSettings`：`ClassifyEndpoint` + `TargetsLocalRuntime` 收紧为仅 Loopback、`TargetsPrivateNetwork`、`AllowLanEndpoints` 字段；`VisionProviderOverride` 携带独立 LAN 许可。
- C# `ProfileManager`：`ProviderProfile.AllowLanEndpoints`；schema 6→7（迁移只升版本，不发明许可，不删用户档案）；`IsProfileExecutable/IsVisionReady` 仅 Internet 要求 Key；`ResolveRoute` visionLeavesDevice = 非回环即离开设备，LAN 视觉未授权→Unavailable+「需单独允许局域网模型」，授权后→MayUploadImage=true+「发送到局域网另一台设备」措辞，回环→「不离开本机」。
- C# `FreeTranslateService`：`AllowAutoRedirect=false`（302 明确报错，不把 query 原文转发到他机）。

**验证**：

- Rust：`cargo test --workspace --locked` **162 passed / 0 failed**、fmt、clippy 0 警告。新增：`endpoint_classification_fixture_agrees_with_the_contract`（AI-RULES §4.1 全样本：localhost/127.10.20.30/[::1]/fd00::1/userinfo/172.200/非法端口/大小写/空串等 16 行）、`lan_endpoints_need_the_separate_permission`（安全模式阻断私网、无许可阻断、授权后无 Key 可执行、回环不受影响）、`cross_origin_redirects_are_refused`（真双监听 mock：跨源 302 → is_redirect 错误）、`same_origin_redirect_is_followed`。
- C#：`PopGlot.Windows.LogicTests.exe`（Release）→ **136 passed / 0 failed，exit 0**。新增：`endpoint classification agrees with the rust core`（同一夹具 C# 分类 + FFI Rust 分类逐行对拍一致）、`lan vision service needs the explicit permission`（未授权 Unavailable+局域网说明 / 授权 VisionOcr+MayUploadImage=true+诚实措辞 / 回环不离开本机）；迁移测试更新为 schema 7 且断言不发明 LAN 许可。
- 既有 `offline mode sends nothing`、`resolved route drives screenshot state machine` 等全部保持通过。

**兼容性**：v6 配置无损升级（.bak 保留原文件）；回环服务行为完全不变；公网服务行为不变；仅"私网=本机"这一错误认知被纠正——受影响的是此前把模型放在 LAN 的用户，他们需要在设置中显式允许局域网模型（执行时错误信息含指引）。

**未验证**：真实 LAN 设备连通、多显示器混合 DPI 场景；`SettingsWindow` 中"允许局域网模型"的 UI 入口属 T11/T13 范围（执行阻断与说明已就位）。

**下一项**：T06（依赖 T01/T03 已满足），第一处代码入口 `apps/PopGlot.Windows/Services/EdgeTtsService.cs`、`apps/PopGlot.Windows/TtsService.cs`。

### 任务编号：T06（Verified）

**原问题**（当前代码复核）：上一轮会话已把 F07 主体实现落地（`ResolveVoice(languageTag, text)` 语言标签优先、脚本回退收窄为明确区间、`ReceiveMessageAsync` 按 EndOfMessage 组装、5000 字/8MiB 限额、`_synthesisCts` 取消在途合成、`CleanupStaleTempFiles` 双前缀、`CloudSpeechEnabled` 独立许可进 TtsService 门禁），但执行日志未登记、无本轮运行证据；且核对发现三处缺口：(a) 隐私/设置页没有任何云端语音 UI 入口（用户无法开启，也看不到 Microsoft 语音目的地说明）；(b) `SettingsWindow.Save_Click` 重建 ShellSettings 时丢弃 `CloudSpeechEnabled` 与 `CloseHintShown`（保存任意设置会静默重置云语音许可为关闭）；(c) 限额/清理/200ms 取消时限无测试覆盖。

**实现**（本轮补齐）：

- `PrivacySection.xaml/.xaml.cs`：新增「云端朗读（自然语音）」设置行（ToggleSwitch，说明列明 Windows 本地默认与 Microsoft 在线语音服务目的地）；`RefreshCloudSpeechState` 装载、`CloudSpeech_ToggleChanged` 立即保存（失败时回拨开关，不假显示）、`UpdateSafeModeGating` 离线模式下禁用该开关。
- `SettingsWindow.xaml.cs`：`LoadShellSettings` 装载云语音开关；`Save_Click` 重建 ShellSettings 时保留 `CloseHintShown`/`CloudSpeechEnabled`（修复静默重置）。
- `StartupRegistration.cs`：新增 `TrySetOverride` 测试缝（设置保存路径不再触碰真实 HKCU Run 键）。
- `App.xaml.cs`：`LoadAppIconFromResource` 改为 try/catch——`Application.GetResourceStream` 资源缺失时抛异常而非返回 null，原 fallback 是死代码；安装不完整/文件错配时现在降级系统图标而不是整个启动失败（用户报告「找不到资源 assets/popglot-v3.ico」启动弹窗触发此加固；已验证当前 bin\Debug 构建资源完整且正常启动）。
- `TestIsolation.cs`：新增 `AssertNoConflictingAppInstance`——真实 PopGlot 实例运行时套件快速失败（本轮实测：实例运行时套件确定性级联失败 2/2，退出后全绿 2/2；现象为 dispatcher 资源异常级联）。
- `Program.cs` 测试：新增 `edge tts enforces source and audio limits`（5001 字拒绝+限额数字、恰好 5000 不被输入门拒绝、单条消息>8MiB 拒绝、累计 5MiB+5MiB>8MiB 拒绝）、`tts temp cleanup covers both file families`（backdate 2h 的 popglot-tts-*.wav 与 popglot-edgetts-*.mp3 均清理、新文件保留）、`settings cloud speech consent round-trip`（真实 SettingsWindow：开关装载、无关设置保存不丢许可【旧代码在此失败】、许可切换立即落盘且不构成草稿）；`tts cloud speech needs its own consent` 增加 Stopwatch 断言 Stop 后 200ms 内观察到取消；`show window hotkey...round-trip` 增加 CloudSpeechEnabled/CloseHintShown 往返与旧文件默认 false；修复分片组装测试的 `Equal(byte[], byte[])`（引用比较缺陷）为 SequenceEqual；截图段新增 `settings_privacy_dark/light.png`（隐私页此前无视觉回归捕获）。

**验证**：

- `PopGlot.Windows.LogicTests.exe`（Release，隔离引导+实例守卫生效）→ **143 passed / 0 failed，exit 0**（上一轮日志口径 136 → 本轮含 T06 新增 7 项与守卫项）。运行环境：无真实 PopGlot 实例在跑（有实例时套件按守卫快速失败）。
- UI 目检（实际打开 PNG，非存在性检查）：`settings_privacy_dark.png` / `settings_privacy_light.png`——云端朗读行位于「允许 AI 读取截图」之后，说明含 Microsoft 在线语音服务与 Windows 本地默认，开关右对齐、间距一致、无截断重叠；深浅主题均正常。
- 隐私页其余行为目检依据上一轮截图基线；本轮未重绘其他页面。

**兼容性与迁移**：旧 shell settings 无 `CloudSpeechEnabled` 字段 → 加载为 false（升级不自动授予 Microsoft 语音许可，测试断言）；开关立即保存不经过设置草稿状态机；`TrySetOverride` 生产为 null 行为不变。

**未验证**：真实扬声器播放路径（MediaPlayer 音频输出）与 Narrator 对朗读状态的播报；真实 Microsoft 语音服务连通（按规则禁止公网）；运行中实例与套件冲突的精确跨进程机制（已用快速失败守卫规避，未深究根因）。

**下一项**：T07（依赖 T00/T03 已满足），第一处代码入口 `apps/PopGlot.Windows/Services/VocabularyStore.cs`（Save/ToggleStar）、`apps/PopGlot.Windows/HistoryStore.cs`（损坏读取/备份）、`Sections/LibrarySection.xaml.cs`（失败状态展示）。

### 任务编号：T07（Verified）

**原问题**（当前代码复核）：`VocabularyStore.Save` 仍 `catch {}` 全吞，`ToggleStar` 无条件返回星标状态（F08：路径为目录返回 true，重建仓库 0 词条）；无条数/长度/字节限额；星标身份只按词面 OrdinalIgnoreCase（不同语言对互相取消、代码标识符大小写合并）；CSV 导出未防公式前缀；Anki `#html:true` 却不转义 `<>`；`HistoryStore` 损坏/超大读取静默返回空且下一次保存直接覆盖原文件；JSON 数组 null 条目会让 `Load` 抛 NRE。

**实现**：

- `VocabularyStore`：`ToggleStar` 返回 `VocabularySaveResult(Persisted, Starred, Status)`（WriteFailed/EntryTooLarge/StoreFull/FileTooLarge），`Remove`/`Clear` 返回 bool；「构建快照→落盘（temp+flush+replace，保留 .bak）→成功才换内存列表」；星标身份 = 词面（Ordinal，大小写保留）+ 源/目标语言（OrdinalIgnoreCase）；限额 8000 字/条、10000 条、32MiB，超限明确拒绝不静默删旧；`Load` 过滤 null 条目；CSV 公式前缀（=,+,-,@，含前导控制字符/空格掩护）加 `'` 中和符；Anki 转义 `& < > "`。
- 并发缺陷（本轮测试发现并修复）：初版把落盘放锁外，两个并发 ToggleStar 从同一基线构建快照互相丢词——已改为整个「读→落盘→提交」在 ` _gate` 内串行。
- `HistoryStore`：损坏/超大读取先隔离备份（`.corrupt-时间戳`，原始字节保留，后续保存不覆盖），新增 `LastQuarantinePath`；null 条目过滤。`LibrarySection.ReloadHistory` 一次性 StatusChanged 告知备份文件名。
- UI：面板/查词/工作台星标失败显示 `DescribeFailureZh()`（"未保存到本机…"）且不点亮星标；`LibrarySection`/`DataSection` 删除与清空失败显示错误状态。`IVocabularyRepository` 签名同步。

**验证**（`PopGlot.Windows.LogicTests.exe` Release → **150 passed / 0 failed，exit 0**）：

- 新增 8 个测试：`vocabulary store save failures stay visible`（路径为目录→不落盘、内存无星、文案含"未保存到本机"、重建 0 词条；旧代码此处返回假 true）、`vocabulary store enforces entry and capacity limits`（8001 字拒绝；10000 条满载拒绝且最旧词条仍在）、`vocabulary star identity preserves code identifier case`（MyVariable≠myvariable；语言标签大小写宽松）、`vocabulary concurrent changes do not overwrite each other`（8线程×3轮，修复前 expected 8 got 3）、`history corrupt file is quarantined not destroyed`（原始字节在备份中且在下次保存后仍在；null 条目跳过；超大文件隔离）、`exports are safe for spreadsheets and anki`（四种公式前缀+制表符掩护均带 `'`；引号翻倍；emoji/中文保留；`<script>&"x"</script>` 全转义）、`panel star failure is visible`（生产面板 Completed 会话后点星标→状态含"未保存到本机"、星不亮）。
- 更新 `vocabulary store supports star...`：覆盖语言对身份（同词 en→zh 与 en→zh-TW 共存、取消一个不影响另一个）。
- 既有历史敏感内容/去重/导出测试全部保持通过。

**兼容性**：词库 JSON 格式不变（旧文件直接读取）；`IsStarred`/`ToggleStar` 增加带默认值的语言参数；取消/清空路径行为对成功场景不变。

**未验证**：32MiB 文件上限分支（在 10000×8000 限制下需要 >32MiB 才触发，代码有守卫但未单测）；真实无写权限/磁盘满（与路径为目录同一 IOException 处理路径）；真实 Excel/Anki 导入行为（以转义断言代替）。

**下一项**：T08（依赖 T00 已满足），第一处代码入口 `apps/PopGlot.Windows/App.xaml.cs` 的 `LogCrashToFile`/`TryNotifyCrash`、`Services/TranslationCoordinator.cs` 错误映射、各 catch 空块。

### 任务编号：T08（Verified）

**原问题**（当前代码复核）：`App.LogCrashToFile` 直接写 `exception.Message` + 完整 StackTrace 到按天文件，无脱敏、无容量/保留界限；`TryNotifyCrash` 托盘气泡回显原始 message；异常风暴无日志界。F09 证实。

**实现**：

- 新增 `apps/PopGlot.Windows/DiagnosticsLog.cs`：唯一诊断写出口。`Sanitize` 脱敏 Authorization 头（scheme+token 整体）、独立 Bearer、sk-/AIza/ghp_/xox 密钥形态、40+ hex 块、URL query（保留 host+path）；消息截断 400 字符、堆栈截断 24 行。`Log` 写 `crash-yyyyMMdd.log`：单文件 1MiB 轮转（`.rot`）、目录总量 10MiB、7 天保留、清理限速（每分钟至多一次）且只匹配 `crash-*.log*`。`CrashSummary` 供托盘（脱敏+160 字上限）。
- `App.xaml.cs`：`LogCrashToFile` 删除，`InterceptCrash` 改走 `DiagnosticsLog.Log` 并在崩溃兜底时 `CoreBridge.CancelActiveRequest()`（故障操作不再挂着在途请求）；托盘气泡改用 `CrashSummary`；启动时强制清理一次日志目录。
- 实现-测试间发现并修复：Authorization 规则若在 Bearer 之后运行会吞掉已替换的标记、若在之前运行只吞第一个词留下裸 token——最终采用组合规则（scheme+token 一起消费）并按依赖排序。

**验证**（`PopGlot.Windows.LogicTests.exe` Release → **152 passed / 0 failed，exit 0**）：

- `crash diagnostics sanitize secrets and bound length`：合成 Authorization/Bearer/sk-key/URL query（含中文原文）异常 → 文件与摘要均无原值；裸 token 不存活；URL base 保留 query 剥离；堆栈 24 行截断（Frame79 不出现）；摘要 ≤161 字。
- `crash log rotates and stays within its budget`：1MiB 文件轮转出 .rot；过期文件删除；12×1MiB 超总量后最旧文件被删至 ≤10MiB；1000 次连续 Log 无异常且目录不超预算。
- 既有崩溃兜底行为（Handled + 节流）未改动，其逻辑维持原状。

**兼容性**：日志目录与文件名模式不变（旧 crash-*.log 仍会被清理规则管理）；StoragePaths 隔离下测试日志落测试根。

**未验证**：真实历史崩溃文件内容（不读取用户日志）；“恢复可用状态”除取消在途请求外的 UI 级恢复（如损坏窗口重建）未实现——按任务书“最后防线”定位保留现状。

**下一项**：T09（依赖 T00 已满足），第一处代码入口 `apps/PopGlot.Windows/Themes/Controls.xaml` 隐式 TextBlock 样式与 PrimaryButton 模板、`ThemeContrast.cs`、`ThemeAuditHelper`。

### 任务编号：T09（Verified）

**原问题**（当前代码复核）：隐式 `<Style TargetType="TextBlock">` 设 `Foreground=TextPrimaryBrush`，样式 setter 优先级高于继承值——PrimaryButton/DangerButton 模板内生成的文字被强制染成 TextPrimary（F10：浅色下 #15171C 在 #5260B5 上仅 3.16:1；DangerButton hover 的白色文字同样被掩蔽）；FocusRing 用 AccentBorderBrush（浅色 2.10:1）；危险按钮 hover 白字压亮红底（暗色 ~2.6:1）违反规则。

**实现**：

- `Controls.xaml`：隐式 TextBlock 样式删除 Foreground setter（保留字体/渲染选项）；新增隐式 `Window` 样式设 `TextElement.Foreground=TextPrimaryBrush` 作环境默认——裸 TextBlock 颜色不变，按钮/菜单内容继承宿主前景。弹出层各 item 容器（MenuItem/ComboBoxItem/ListBoxItem/ToolTip）本就自带 Foreground，不受影响。
- `ThemeService`：新增 `FocusBrush`（暗 #7C89D9 / 浅 #5260B5）与 `DangerHoverBrush/TextBrush`、`DangerPressedBrush/TextBrush`（暗色主题用「加深软底+红字」替代「白字压亮红」，浅色保留红底白字）。
- `Controls.xaml`：FocusRing 改用 FocusBrush、StrokeThickness 2、去掉 0.9 透明；DangerButton hover/pressed 触发器改用新 token（替换硬编码 #FFFFFF）。
- `ThemeAuditHelper`：新增断言 PrimaryText 对 hover/pressed 底 ≥4.5、DangerHover/PressedText 对各自底 ≥4.5、FocusBrush 对 Canvas/Surface ≥3.0（首版暗色 pressed 配色 4.28 被审计抓住后加深为 #4E1E28）。

**验证**（`PopGlot.Windows.LogicTests.exe` Release → **153 passed / 0 failed，exit 0**）：

- `primary button text uses primary text brush`（反例先行）：修复前失败 `expected <#FFF7F8FC>, got <#FFEEF0F4>`（隐式样式掩蔽被视觉树探针检出）；修复后深浅两主题、字符串内容与显式 TextBlock 内容、按钮前景到文字的传递全部通过；10 轮深浅切换后每轮新按钮渲染当前主题（token 管线无冻结泄漏）。
- 既有 `theme contrast ratios...` 扩展断言全过；既有全部 UI 测试无回归。
- 截图目检（实际打开 PNG）：`main_window_light.png`（主按钮「翻译」白字——修复首次在真实渲染可见）、`main_window_dark.png`、`translation_panel_dark.png`（demo 数据、文字全浅色）、`settings_light.png`（保存按钮白字、Demo 档案）、`settings_privacy_dark/light.png`——无文字消失/变黑。
- 调查记录：测试宿主中既有元素的主题热切换走冻结替换路径不即时更新（生产 App.xaml 声明未冻结画刷、ThemeService 原地变异同实例，热切换正常）——往返断言按测试宿主可达路径编写，原地热切换列入 T11 真机项。

**兼容性**：裸 TextBlock 的默认颜色不变（环境继承提供同一 token）；Caption/标题等派生样式行为不变；深浅主题基线值未改（仅新增 token 与危险按钮状态配色）。

**未验证**：SystemParameters.HighContrast 优先级与 SystemColors 映射（任务步骤 5 前半，未实现）；真机主题切换对已打开窗口/已渲染 FlowDocument 的原地更新；Narrator。

**下一项**：T10（依赖 T00，色彩矩阵依赖 T09 已满足），第一处代码入口 `tests/PopGlot.Windows.LogicTests/Program.cs` 的 `RenderAndSaveAtDpi`（logicalWidth = width / dpiScale 与位图 width * dpiScale 混用）。

### 任务编号：T10（Verified）

**原问题**（当前代码复核）：`RenderAndSaveAtDpi` 把调用方传入的 960 按 `width / dpiScale` 折半成 480 DIP 布局，位图却是 `width * dpiScale`=1920px——200% 截图内容挤在左上 1/4，其余空白（F11 证实）。通过条件只查 File.Exists，检不出该错误。

**实现**（`Program.cs`）：

- 生产器重写：参数即逻辑 DIP，`pixelWidth = round(logical × scale)`；渲染后重解码断言文件尺寸严格等于 逻辑×缩放。
- 新增 `AssertCanvasFilled`：解码 PNG 统计透明像素，>2% 即失败（旧生产器的 200% 输出约 75% 空白，必被抓住）；附合成破图（400×240 画布只画 100×60）自检断言确实会拒绝。
- DPI 矩阵从 2 页面扩到 6 页面（主工作台/翻译浮窗/查词/设置列表/服务编辑/资料库 Library 预览用合成 fixtures）× {125,150,200}% × {Dark,Light}，每张都跑填充断言。
- `LoadBitmap`（OnLoad+Freeze）与 `WriteWithRetry`（5×200ms）：截图产物不再被本进程句柄或 Explorer 缩略图瞬时锁拖垮（本轮实际遇到一次外部锁占用 main_window_dark.png）。

**验证**（`PopGlot.Windows.LogicTests.exe` Release → **153 passed / 0 failed，exit 0**）：

- 尺寸断言：main_window_light_200pct.png 必须 1920×1280。
- 填充断言对全部 36 张矩阵图通过；破图自检通过（Throws 验证检查器有效）。
- 目检（打开 PNG）：`main_window_light_200pct.png`——内容铺满 1920×1280，右下无空白，导航/语言标签/底栏无重叠；`service_editor_compact_light.png`——紧凑 620 宽下字段完整无截断。主窗 100% 深浅与浮窗长文已在 T09 轮目检。

**兼容性**：`RenderAndSave`（scale 1.0）调用方签名不变；所有基础截图（非 DPI）像素尺寸与旧版一致。

**未验证**：错误状态（Failed/长错误文案）截图 fixture（归入 T11 旅程截图）；WindowChrome/真实 DPI 显示器/多显示器混合缩放（离屏渲染不覆盖，真机项）。

**下一项**：T11（依赖 T01/T02/T03/T09/T10 全部满足），第一处代码入口 `MainWindow.xaml.cs`、`Sections/TranslateSection.xaml.cs`（空状态与首次授权）、`SettingsWindow.xaml.cs` 草稿守卫、各窗口窄宽响应。

### 任务编号：T11（Verified——含两轮实现）

**已核对为既有能力（无需重做）**：旅程 2（添加自定义引擎→保存不自动测试→设默认）；旅程 3（服务页脏草稿守卫/放弃/继续编辑——FailedSaveRecovers 与 DraftGuard 测试既有）；旅程 6 前提（Partial 保留部分文本且结果动作禁用——T03 覆盖）。

**第一轮实现（空态与 IME）**：

1. **空结果面首次引导**（旅程 1 就地入口）：`TranslateEmptyState` 覆盖层（仅 Idle+空结果可见）：说明 + 「填入示例」（仅填 `FileNotFoundError: config.json not found` 文本，0 请求）+「使用内置公共翻译」内联授权（tooltip 列明 translate.googleapis.com / clients5.google.com；点击持久化 Allowed 并收起；仅 Unset 显示）。结果出现（FocusTranslate 展开/reducer 完成）时隐藏。
2. **IME 守卫**（旅程 4）：工作台/浮窗/查词三个输入框 Enter 处理加 `InputMethod.GetIsInputMethodEnabled` 守卫——组合确认的 Enter 不触发翻译。

**第二轮实现（剩余子项）**：

3. **窄宽上下堆叠**（AI-RULES 6.2）：`TranslateSection.SetStacked(bool)`——内容区 <720 DIP 时 6 行单列（源语言栏/输入编辑器 MinHeight 160/源操作栏/目标语言栏/译文阅读区 Star/目标操作栏），交换按钮与中轴隐藏、空态提示改"在上方"；≥720 恢复 3×3 并排。`MainWindow.ContentGrid_SizeChanged` 按**内容区宽度**驱动（<880 compact 提示，<720 堆叠）；主窗 MinWidth 920→520 使窄窗可达。
4. **长模型 ID/长错误 fixtures**：`service_editor_long_model_light.png`（67 字符模型 ID 表单不挤压）、`translation_panel_error_dark.png`（Failed 会话：长部署名错误 + 建议文案，结果动作禁用）——均附画布填充断言。
5. **滚动保持断言**：`stream scroll position is preserved while reading`——阅读位（offset 40）在后续 delta 后保持不变。
6. **测试宿主修正**：STA 批线程安装 `DispatcherSynchronizationContext`（生产 WPF UI 线程本就携带，`Save_Click` 等 async void 续体因此回 UI 线程；裸 STA 线程的续体曾掉到线程池跨线程摸 DependencyObject）；`POPGLOT_TESTS_FILTER` 名称过滤（允许实例运行时定向验证）；EnsureApplication 强根住 App 实例。

**验证**：

- 逐测试分片（`POPGLOT_TESTS_FILTER`，每测试独立进程，运行中真实实例共存）：**155/155 PASS**（139 名单逐一 + 17 STA 批逐一；唯一失败为 `no real PopGlot instance conflicts with the suite` 守卫本身——实例在跑时按设计失败，即预期行为）。
- 过滤模式全程使用时必须注明：**全量连续套件（单进程一次跑完）尚未在实例退出后重跑**——下一位执行者在实例退出后跑一次 `PopGlot.Windows.LogicTests.exe` 全量确认无跨测试干扰。
- UI 目检：`main_window_narrow_dark.png`（560 宽：输入区在上≥160 DIP、译文在下、空态提示"在上方…"、中轴消失、无重叠截断）；`translation_panel_error_dark.png`（错误红色正文、长部署名完整换行、底部建议、"需要处理"状态）；`service_editor_long_model_light.png`（长模型 ID 在表单内不挤爆布局）；`main_window_light.png`（空态引导，用户 18:26 实际点击过「填入示例」并完成翻译）。
- 真机验证：用户在真实窗口完成"填入示例→翻译成功"全程（历史 18:26 记录 FileNotFoundError 示例条目）。

**剩余（真机项，不阻塞收尾）**：IME 实际组合键行为（守卫已就位，需真机输入法确认）；Narrator 播报；真实显示器 200%/混合 DPI 下窄窗拖拽。

**全量连续回归补跑（实例退出后，2026-09-05 19:20）**：`PopGlot.Windows.LogicTests.exe`（Release，单进程连续）→ **156 passed / 0 failed，exit 0**——逐测试分片的结论得到连续运行确认，无跨测试干扰。Debug 构建（19:22）0 警告 0 错误，含 T06–T11 全部修改。

**下一项**：T12（依赖 T03/T04 已满足），第一处代码入口 `crates/popglot-core/src/provider.rs::output_token_limit`、`Services/TranslationCoordinator.cs` 长输入路径。

### 任务编号：T12（Verified）

**原问题**（当前代码复核）：Rust 文本输入允许 64 KiB，但 `output_token_limit` 把输出 clamp 到 1200 tokens（短文本 384–1200 自适应）——长技术文章、中→英、转录+翻译的输出预算明显不足（F12）。C# 侧整段文本单请求发送，无规划、无分段。

**实现**：

- `popglot-domain`：新增 `plan_translation_segments(source, max_segment_chars=800, max_segments=8) -> SegmentPlan { Single | Segments(Vec<String>) | Rejected(OversizedCodeBlock | TooManySegments) }`。原子切分：围栏代码块（```…```，含未闭合取到文末）永不拆分；散文按 空行→行→句（。！？.!?；;）→词（空格，，、：:）→字符硬切 逐级降界，分隔符保留在原子尾部——**段落拼接逐字节还原原文**。任一原子超预算且不可再切 → OversizedCodeBlock 拒绝；段数 >8 → TooManySegments 拒绝（发送前失败）。
- `popglot-ffi`：纯导出 `popglot_plan_segments`（JSON：mode/segments/rejected_reason——初版键名 `reason` 与 C# snake_case 绑定不匹配导致拒绝静默失效，被验收测试的假成功挂起暴露后修正）。
- `CoreBridge`：`PlanSegments` 包装 + `SegmentPlanDto` + 常量 `MaxSegmentChars/MaxSegments`。
- `TranslationCoordinator`：新 `TranslateProviderTextAsync` 统一文字翻译路径（工作台 + OCR 识别文 + 视觉两段式的文本阶段全部汇入）：Single → 原单请求；Segments → 顺序编排（并发 1）：每段独立流会话+TTFT、进度报文 `AccumulatedText = 前缀+本段缓冲`、段级完整性检查（warnings/is_partial/空译文）；任一段失败/不完整 → 已完成片段 + 段号警告 + `SegmentSessionException` → `ApplyFinalResponse` 判 Partial（T03 门禁：无历史/无自动副作用）；取消/超时保留片段。会话级 `SessionDeadline`（10 分钟）linked CTS，替代每段重新计时。零 delta OCR 回退过滤器排除 SegmentSessionException（文本段失败不得触发整图重跑）。`PumpStreamAsync` 增加 `textPrefix` 参数（旧调用点传空）。截屏路径 `routingStopwatch/ocrElapsedMs` 提升到 try 外供 catch 使用。
- 短文本自适应预算（`output_token_limit`）未动：段内 ≤800 字符 × 2 + 256 ∈ [384,1200]，1200 上限对单段足够——按规则未把全局上限改大数字。

**验证**：

- Rust：`cargo test --workspace --locked` **170 passed / 0 failed**（新增 8 例：短文 Single、段落边界无损重构、CJK 硬切、围栏原子、超限代码拒绝、64KiB 段数拒绝、技术文章保真、未闭合围栏拒绝）；fmt 干净、clippy 0 警告。
- C#：`PopGlot.Windows.LogicTests.exe`（Release，单进程连续）→ **162 passed / 0 failed，exit 0**。新增 6 例：`long input plans into ordered segments`（~4k 文章 mock 打标：段数 2..8、每段≤800、顺序可证、代码围栏整块、历史 1 条）、`cancel between segments stops later requests`（第 3 段取消后第 4 段 0 请求、片段可见）、`segment failure keeps fragments as partial`（第 2 段炸→Partial+段号警告+历史 0+后续 0 请求）、`incomplete segment stops session as partial`（is_partial/空译文段终止会话）、`budget refusal sends nothing`（超大代码块/64KiB CJK 拒绝且 0 请求）、`short source stays single and planner agrees across ffi`（single 模式、无损拼接、FFI 拒绝原因一致）。
- 调试记录：初版拒绝原因键名不匹配使拒绝失效 → mock 永不完成的 session → pump 无限循环（全量卡 7 分钟）。定位后修 FFI 键名 + 收敛测试断言字面值；该假成功路径现被 `budget refusal` 测试锁定。

**兼容性**：短文本行为逐字节不变；分段请求对协议透明（每段就是一个普通请求）；免费引擎 5000 字符限制保持原状（其分段未在本轮范围，已在错误文案中引导分批）。

**未验证**：真实供应商对分段长文的翻译质量与费用（按规则禁止真实调用）；视觉直译路径（图片本身不经过文本分段）；provider 响应 4MiB 累计上限由既有流缓冲硬限承担（未为分段单独加总限）。

**下一项**：T13（依赖 T05 已满足），第一处代码入口 `ProfileManager.ResolveRoute`、`CoreBridge.PlanScreenshotRoute`、Rust `RoutingContext/select_route`（T05 已统一分类语义，本轮核对决策表）。

### 任务编号：T13（Verified）

**原问题**（当前代码核实）：F13「两套真相」确认——生产执行走 C# `ProfileManager.ResolveRoute`（T05 已对齐任务书语义），但 Rust `select_route` 是第二套语义（VisionDirect 不可用时静默回退 OCR、无 VisionOcr 分支、无 LAN 概念），仅被无调用方的 `CoreBridge.TranslateScreenshotAsync` 死路径消费；`RoutingContext` 携带伪能力字段（`looks_like_code/complex_layout/image_quality/ocr_confidence` 恒 false/false/1.0/1.0），喂给 Auto 置信度分支制造「已理解画质」假象。

**实现**：

- `popglot-domain`：`RoutingContext` 重构为事实采集结构（vision_endpoint_class/allow_lan_endpoints/text_route_available 取代 4 个伪字段）；`select_route` 重写为任务书决策表——LocalOcr 无引擎显式 unavailable；VisionDirect 不可用即阻断（vision_not_configured/vision_permission_blocked）绝不静默降级；VisionOcr 要求视觉+文字线路双前提；Auto 诚实本地优先（OCR 可用即本地，不可用时 loopback 视觉直译/远程视觉两段式/无文字线路则直译/全无则 auto_unavailable）；未知端点分类保守拒绝。`MayUploadImage` 语义统一为 AI-RULES 4.1 的「图片是否离开设备」（loopback=false 但管道照跑）。
- `popglot-ffi`：新纯导出 `popglot_select_route(facts_json)`（不改既有 ABI）。
- `CoreBridge`：`SelectRoute(ScreenshotRouteFacts)` 包装 + `SelectRouteRaw` 对拍缝。
- `ProfileManager.ResolveRoute`：删 130 行内联策略，改为「采集事实（端点分类/权限/OCR 包/凭据探针 via `_credentialProbe` 测试缝）→ FFI 决策 → 映射管道」；`auto_unavailable`/`forced_local_ocr_without_engine` 映射为 Unavailable。
- 4 个旧语义 Rust 测试删除（断言静默回退/伪质量分支），14 行决策表测试取代。

**验证**：

- Rust：`cargo test --workspace --locked` **180 passed / 0 failed**（决策表 14 行逐一断言）；fmt 干净、clippy 0 警告。
- C#：`PopGlot.Windows.LogicTests.exe`（Release 连续）→ **163 passed / 0 failed，exit 0**。新增 `routing decision table agrees across the ffi boundary`（14 夹具行跨 FFI 对拍 + MayUploadImage≡(视觉管道∧离开设备) 不变量）；既有 `lan vision service needs the explicit permission`（LAN 未授权 Unavailable→授权 VisionOcr→回环不离开设备）与 `resolved route drives screenshot state machine` 全部在**新策略**下通过——预览与执行共用同一条 FFI 路径。
- Debug 构建成功（含 T12+T13）。

**兼容性**：`ResolveRoute` 签名不变、所有消费者（隐私页预览/Coordinator 执行/设置页草稿）无感切换；唯一行为变化是把任务书要求的语义带到 Rust 侧（生产 C# 行为本就如此，仅删了死路径的第二真相）；旧 FFI `popglot_plan_screenshot_route` 保留未动。

**未验证**：真实 LAN 设备；`CoreBridge.TranslateScreenshotAsync` 死路径（旧 Rust 决策消费者，无调用方）——删除属 T17 架构整理，本轮标注不处理。

**下一项**：T14（依赖 T00 已满足），第一处代码入口 `ModelRecommendationService`、`ModelCatalogService`、`ServicesSection` 推荐调用。

### 任务编号：T14（Verified）

**原问题**（当前代码核对）：合成候选把 `VisionInput` 标 Supported（F14 点名的虚构——文本目标凭空获得视觉能力）；`NormalizeEndpoint` 把整个 URL 小写化（`/Model` 与 `/model` 混淆）；benchmark 无数值有效性检查（NaN/负数可入分）、无时间有效期（旧样本永久影响排名）、只取第一个匹配样本（无中位数聚合、无 n、无试测标注）。

**实现**（`ModelRecommendationService.cs`）：

- 合成当前模型 `VisionInput` 恒 Unknown（两模态都不虚构），`UserCanOverride` 保持可选。
- `NormalizeEndpoint`：仅折叠 scheme/host 大小写与默认端口（80/443）；path 保留大小写；query 永不明文保存——以 SHA256 前 8 位十六进制指纹参与匹配（同 query 相互匹配、不同 query 区分）。
- `HasValidNumbers`：Ttft/CharsPerSecond/TotalDuration 必须有限且 ≥0；`MatchesContext` 增加 7 天 `FreshnessWindow`（`Clock` 静态缝可注入，过期样本保留为历史但绝不参与排名）。
- 匹配样本聚合：全部有效样本的 TTFT/速度/总时长**中位数**（偶数取两中平均）+ 最新样本携带身份字段；`n<5` 在详细理由中标注「试测，样本不足 5」；PrimaryReason 从「实测响应快」改为「本机实测响应（TTFT 中位 ~Xms，试测样本，仅供参考）」。

**验证**：`PopGlot.Windows.LogicTests.exe`（Release 连续）→ **163 passed / 0 failed，exit 0**。新增 4 组断言（`RunT14AcceptanceTests`）：合成候选不虚构 Vision、端点归一化 6 条规则（大小写/端口/指纹/不明文）、NaN/负数/过期样本零影响、中位数聚合（100/900/300/200→250）+ 试测标注。既有 10 组推荐测试全部保持通过。

**兼容性**：`MatchesContext` 收紧对现有调用方透明（当前无生产 benchmark 数据，行为不变）；`NormalizeEndpoint` 行为变化仅影响 benchmark 匹配精度（更严格）。

**未验证**：真实 benchmark 导入流程（无生产数据，诚实空态）；UI 徽章显示路径由既有 `ModelRecommendationUiTests` 覆盖。

**下一项**：T15（依赖 T00 已满足），第一处代码入口 `tests/PopGlot.Windows.LogicTests/Program.cs` 的 `RenderScreenshotsAndMeasureBaseline`（伪指标 TrayAvailable/Cold Startup 命名）、`apps/PopGlot.Windows/App.xaml.cs` 启动路径。

### 任务编号：T15（Verified）

**原问题**（当前代码复核）：`RenderScreenshotsAndMeasureBaseline` 的 `TrayAvailable` 是 `StartNew(); Stop(); Max(1,…)` 的空操作假 1ms；「Cold Startup」只测 Core 初始化；「Hotkey to Panel First Frame」只构造和 Arrange；Working Set 来自测试宿主进程——全部被当作应用级预算引用（F15）。

**实现**：

- 测试宿主基准段重写并诚实命名：`CoreInitialize` / `WindowConstruct` / `WindowConstructArrange` / `CancelNoopOverhead`；工作集明确标注「测试宿主进程，非应用空闲 WS」；假 Tray 指标删除；输出段声明这些是组件基准而非应用级预算，并指向独立测量产物 `artifacts/perf/startup.json`。
- **新增应用级测量**：`App.xaml.cs` 支持 `--smoke-startup <marker> [dataDir]`——走真实启动全路径（设置加载/native core/热键/托盘），数据目录显式传入（绝不触碰真实配置），tray 可用毫秒写入 marker 后退出；正常启动路径不受影响（结构整理中曾一度把 OnStartup 主体误拆，已还原为单入口并验证）。
- **新增 `scripts/measure-startup.ps1`**：N 次真实进程启动（冷/暖标记、30s 超时兜底、失败原因记录）、P50/P95 按预算（600/1200ms）判定 PASS/FAIL，原始样本+环境（OS/核心数/运行时/时间）写入 JSON。

**验证**：

- 真实测量：`powershell -File scripts/measure-startup.ps1 -Runs 30` → **30/30 成功，P50=181ms、P95=200ms、min=175/max=277ms，预算内 PASS**；产物 `artifacts/perf/startup.json`（含样本、机器环境、时间戳）。
- `PopGlot.Windows.LogicTests.exe`（Release 连续）→ **163 passed / 0 failed，exit 0**（基准段更名无回归）。
- Debug 构建 0 警告 0 错误。

**未验证（真机项，如实标注）**：热键→选区显示 P95≤150ms（需要真实消息/合成器路径）；1080p OCR P50/P95（需要语言包+固定 fixture 机器）；空闲应用工作集/空闲 CPU（需要独立应用稳定 30/60 秒采样——脚本机制已就位，本轮未挂机采样）；「取消请求退出 P95≤200ms」需在途延迟 mock 场景（套件只测了无活动取消开销并诚实命名）。

**下一项**：T16（依赖 T12/T15 相关项已稳定），第一处代码入口 `apps/PopGlot.Windows/Services/TranslationStreamBuffer.cs` 的 O(1)/non-blocking 注释。

### 任务编号：T16（Verified——注释诚实化，无瓶颈证据故不动实现）

**原问题**：`TranslationStreamBuffer` 注释宣称「O(1) synchronous / non-blocking / zero-allocation / O(1) lock duration」——实际 append 是 O(delta 长度) 且 builder 增长分配，drain 每次拷贝 pending（O(pending)），锁结构非无锁（F16 注释与实现不符）。

**实现**：类级与三个 append 方法的注释重写为真实复杂度契约（append O(k)、drain O(pending)、总工作量随 merge 点数、锁持有时长有界但非无锁、建议按 40ms 节奏 drain）。Rust `StreamingTokenRestorer` 无复杂度声明（T04 已改 match_counts 计数结构），无误导注释。

**按 T16 验收保留实现**：未测到热点前不换无锁结构/自动机（规则 §8「先记录基线再优化」）；既有 100K 规模流式测试与 40ms 泵行为全部保持通过（163 passed / 0 failed，exit 0），优化留待真实 profile 证据。

**未验证**：P95 分配/锁等待数据（需要 profiler 采样，本轮未做——如实标注，不以平均耗时充数）。

**下一项**：T18（文档同步），第一处代码入口 `README.md` 版本与测试数、`docs/DESIGN_SYSTEM.md` 旧配色表。

### 任务编号：T18（Verified——当前契约文档同步完成）

**原问题**（全文检索复核）：README 版本 0.1.2/「113 项测试」/下载文件名 0.1.2、`VisionDirect 失败后必须安全回退`（与 T13 阻断语义直接冲突）、schema v6（实为 v7）；DESIGN_SYSTEM 整表为另一套未实现配色（#0A0B0F/#2563EB/#4D9FFF）并引用无验收方法的 60-30-10；EXECUTION_BOARD「全量 113 项持续保持全绿」把历史数字写成现状；PRODUCT_SPEC/PRIVACY 缺 VisionOcr 与阻断语义。

**实现**：

- `README.md`：版本 0.1.3（4 处）、测试数改为「以命令输出为准，2026-09-05 为 163 项」、截图线路更新为四模式+共享决策表措辞、VisionDirect 阻断语义、schema v7 迁移说明。
- `docs/DESIGN_SYSTEM.md`：暗色 token 表逐值对齐 `ThemeService.DarkTokens`（含 FocusBrush/DangerHover 家族），声明代码为事实来源并注明旧配色为历史（v0.1.1 文档与实现不一致，v0.1.3 锁定）；Kbd/Token 色改 AccentBrush 引用。
- `docs/EXECUTION_BOARD.md`：明确「113 项为 0.1.0 时点历史快照」，链接当前整改日志。
- `PRODUCT_SPEC.md`/`docs/PRIVACY.md`：模式清单补 VisionOcr；VisionDirect 阻断语义；VisionOcr 片段保留语义。

**验证**：全文检索 `0.1.2`（现行文档零命中，Release Notes/CHANGELOG 历史引用保留）、`0A0B0F|2563EB|4D9FFF`（仅历史注记行）、`113 项`（仅历史归档且标注时点）无当作现状的矛盾；`./scripts/verify.ps1` 中的测试数已在 README 修正。

**未验证**：`docs/TRANSLATION_BENCHMARK.md`、`docs/UX_DECISIONS.md` 未逐字复核（不在 F18 列名的矛盾点内）。

**下一项**：T17（P2 架构整理）与 T19（独立验收）。T17 属大型重构（ServicesSection 拆分/测试分域），建议独立轮次执行；T19 建议由负责人另开审查上下文按 README 独立验收提示词执行。

### 任务编号：T17（Verified——第一切片）

**范围**（本轮切片）：删除死路径 + 抽离服务编辑器的纯规则。ServicesSection 其余职责（测试连接/模型目录获取/列表渲染）与 Controls.xaml 拆分留待后续切片。

**切片 1a：死路径删除**

- `CoreBridge.TranslateScreenshotAsync`（两个重载）、`TranslateViaLocalOcrAsync`、`ScreenshotTranslation` record、`NativeMethods.TranslateVisionV2` P/Invoke——复核确认全部无调用方（生产截图走 `TranslationCoordinator.TranslateScreenshotAsync` → `ProfileManager.ResolveRoute` → 流式执行器；Rust 侧 `popglot_translate_vision_v2` 导出保留未动，C# 端已无引用）。删除过程中曾因脚本切片错误复制出重复成员块（CS0111 ×11），按行定位删除副本后恢复。
- 同时清除 Rust 侧旧语义消费者标注：`AppCore::plan_screenshot_route`（core 内部）现在经由 T13 重写后的共享决策表，不再是「第二真相」。

**切片 1b：ServiceDraftCoordinator 抽离**

- 新建 `apps/PopGlot.Windows/Services/ServiceDraftCoordinator.cs`：`Validate(name, baseUrl)`（名称/URL 存在/非本机 HTTPS 规则，纯函数）、`BuildDraft(ServiceDraftInputs)`（默认值填充+能力标志+locality，纯函数）、`ComputeRecommendations(...)`（文字+可选视觉两路推荐协调，纯函数）、`ParseHeaders(text)`（严格语义：坏行抛错而非静默丢弃——与原实现逐字一致）。
- `ServicesSection.xaml.cs`：`TrySaveService` 校验前置改调 `Validate`（**credential 顺序守卫 `ResolveSaveTarget`→`SaveApiKey` 原样留在视图**，既有源码顺序测试不变）；`BuildProfileFromForm` 变薄适配器（读控件→`ServiceDraftInputs`→`BuildDraft`）；`ParseExtraHeaders` 一行委托；`RefreshRecommendations` 的排名逻辑改调 `ComputeRecommendations`，视图只渲染 chips。

**验证**：

- C#：`PopGlot.Windows.LogicTests.exe`（Release 连续）→ **164 passed / 0 failed，exit 0**。新增 `service draft coordinator pure rules hold`（Validate 6 行、BuildDraft 默认值/共享视觉/能力标志、Header 严格解析、推荐协调共享/分离两形态）；既有全部测试含 `ServiceSaveKeyOrderGuard` 源码顺序守卫通过。
- Rust：`cargo test --workspace --locked` → **180 passed / 0 failed**（本轮无 Rust 改动，复核基线）。
- Debug 构建 0 警告 0 错误。

**兼容性**：错误文案逐字保持（"请先填写服务名称。"/"API Base URL 不能为空。"/"…HTTPS…"）；`ParseExtraHeaders`/`BuildProfileFromForm`/`TrySaveService` 签名不变（internal，测试与调用方零改动）；FFI 未动。

**未完成（后续切片，不阻塞本切片验收）**：ServicesSection 剩余约 1900 行中「测试连接」与「模型目录获取」仍内联于视图；测试 Program.cs 按领域拆分未动；Controls.xaml 资源字典拆分未动。这些是继续收敛项，非回归风险。

**下一项**：T19（独立验收）——按任务书由负责人另开审查上下文执行；实现侧 T00–T18 + T17 切片 1 已全部完成且全绿。
