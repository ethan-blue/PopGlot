# PopGlot 整改任务书：T00—T19

本任务书与 AI-RULES.md 一起执行。每项都包含文件入口、具体步骤和验收。相对路径均以仓库根为起点；旧行号仅参考 REVIEW.md，实施前通过方法名确认。不得只生成伪代码或将验收项抄成“已完成”。

优先级：P0 = 隐私/文本保真/不完整内容错误持久化；P1 = 核心可靠性或可用性；P2 = 后续优化。风险类型与依赖优先于编号。任务已在当前代码解决时，补证据即可，禁止为体现工作量重新实现。

## T00 / P1 / 首先执行：隔离测试与建立可信基线

**入口**：`CoreBridge.Initialize`、`ShellSettingsStore`、`ProfileManager`、`CredentialStore`、`TranslationCoordinator.Instance`、`tests/PopGlot.Windows.LogicTests/Program.cs` 的 EnsureApplication/RenderScreenshotsAndMeasureBaseline、`scripts/verify.ps1`。

**步骤**：

1. 记录 git 状态、提交、版本、可用SDK。新建 EXECUTION-LOG.md，登记本文件全部编号，不预先标完成。
2. 列出各 store 默认路径和读取凭据入口；加入显式可注入运行环境，生产默认行为保持兼容。测试不得依赖环境变量“看起来换了目录”。
3. 测试环境启动前断言 StoragePaths 全部在独立测试根下，CredentialVault 是替身，HTTP/WebSocket 默认拒绝公网。默认测试启动时若缺隔离配置，直接失败。
4. 截图 fixture 使用固定假服务和空/短/长合成历史；不要启动真实 App.OnStartup，不注册全局真实热键，不发健康请求。
5. 先跑 Rust checks 与 Windows Release 测试；记录前置失败；Debug 被运行中exe占用不关用户进程。

**验收**：测试即使在配置了真实服务的开发机运行，截图也只含 `Demo Text Service / demo-text-model`；测试前后真实配置文件哈希不变（不输出内容）；凭据操作全部记在内存替身；网络请求仅本地mock。已生成的含真实服务信息截图不得上传；在隔离修好后重新生成。

**交付**：隔离实现/断言、基线命令与真实结果、执行记录。后续所有任务依赖此项的可靠测试环境。

## T01 / P0 / 依赖 T00：封闭全部免费引擎出网入口

**入口**：`MainWindow.RefreshEngineStatus/UsesFreeEngine/UpdateFreeEngineHealthAsync/EngineHealthButton_Click`、`FreeTranslateService.TranslateAsync/GetHealthAsync/ProbeCoreAsync`、`OutboundPolicy`。

**步骤**：

1. 追踪自动刷新、点击状态、强制健康、普通翻译、截图OCR免费回退五条链路。
2. 把授权判断放到公共请求边界，让调用者必须携带已核实的网络/目的地上下文；不允许绕过的直接静态 HTTP API。架构可先用小接口，不引入完整DI框架。
3. 默认取消窗口激活时自动健康请求，显示“未检测”；仅用户点击才探测；点击也必须先通过免费授权和网络门禁。
4. force 只意味着忽略健康缓存，不意味着忽略授权。已开始的请求在撤销许可后按任务策略取消。
5. Unset 保持未作选择，不因为默认拒绝而写成 Denied。明确 AllowOnce 的作用域，不作为以后探测许可。

**验收矩阵**：Consent取 Unset/Denied/Allowed，SafeMode取开/关，Network取开/关；所有组合只在 Allowed+Safe关闭+Network开启时允许请求。针对普通入口和强制探测都断言发送次数。Allowed sanity case 必须真的到达mock，避免测试因网络全断而虚假通过。构造/激活窗口即使 Allowed 也不自动请求。

**必须防回归**：测试直接调用生产健康服务，而不只验证 UsesFreeEngine 返回值。

## T02 / P0 / 依赖 T00：统一保真展示、复制、朗读与收藏文本

**入口**：`Services/MarkdownPresenter.cs`、`QuickSearchWindow.xaml.cs`、`TranslationPanelWindow.xaml.cs`、`Sections/TranslateSection.xaml.cs` 的复制/朗读/收藏、`LibrarySection.xaml.cs` 的载回与复制。

**步骤**：

1. 在当前生产 formatter 上添加反例，先确认 `foo_bar_baz` 损坏能被检出。
2. 建立原始正文、视觉文档、复制普通译文、复制Markdown、复制代码、语音文本的明确转换路径；复用同一个解析结果或共同文本服务。
3. 先识别 fenced code/inline code/受保护技术span，再处理自然语言强调与间距。无法明确解析的内容按原文保留，禁止猜测删除符号。
4. 自然语言视觉Pangu仅作用于文本节点，代码与链接目标不参与。若使用Markdown库，只接入解析器，不接受HTML执行、远程图片自动获取等额外行为。
5. 不同入口用同一 formatter，禁止主窗复制原始Markdown而浮窗复制另一套损坏文本却声称一致。可给用户“复制译文”“复制Markdown”的明确选择，默认行为在三入口一致。

**必须测试的输入与预期**：

| 输入/上下文 | 预期 |
|---|---|
| 技术正文 `foo_bar_baz` | 字符完整，不成为 foobarbaz |
| 行内代码包裹的 foo_bar_baz | 复制去容器语法后仍是 foo_bar_baz |
| 技术正文 `__init__`、`snake_case_name`、`a*b*c` | 技术字符保留；不能作为强调标记误删 |
| 行内代码 `C:\用户data\file.txt` | 路径内无新增空格，展示与复制都正确 |
| fenced code，含缩进/空行/Tab | 代码复制保留内容，外围围栏按复制模式处理 |
| “调用 `getUserName()` 获取名字” | 自然语言可排版，代码严格不变 |
| **普通粗体**、嵌套列表、URL、Markdown链接 | 普通译文仍可读；Markdown复制保持原结构 |
| 未闭合围栏、反引号不配对、类似占位符的普通文本 | 不崩溃、不静默删内容 |

**验收**：从相同模拟终态出发，三入口点击复制得到同一约定文本；自动复制也走相同保真逻辑。用内存剪贴板适配器验证，补至少一次WPF组件动作路径验证。

## T03 / P0 / 依赖 T00：唯一终态资格与 Partial 无副作用

**入口**：`TranslationModels.TranslationSession.IsSuccess`、`TranslationCoordinator.ApplyFinalResponse/WriteHistoryOnce`、C# TranslationResult DTO、Rust result.is_partial、三个窗口/reducer/gate。

**步骤**：

1. 定义非空Completed且完整性通过才可使用的资格属性；明确区别“请求返回了结果”和“结果可持久化”。
2. 核对Rust序列化→C#反序列化是否保留 is_partial/finish reason/完整性错误；没有字段时补向后兼容DTO与版本约定。
3. 历史、自动复制、朗读、收藏、代码块复制按钮均从同一资格出发。程序动态生成的代码块按钮也要遵守资格，不能只禁用外层工具栏。
4. 副作用按session执行至多一次；重复final/回调不能再次写盘。清空/关闭/新epoch后的final不得触发旧会话动作。

**验收**：mock final正文非空且warnings包含“缺失token”，阶段Partial，history count=0/copy count=0/TTS count=0/star count=0；is_partial=true但warnings空也不成功；Completed空正文也不成功。网络中断/取消/校验失败/正常完成全覆盖。正常非空完整final重复投递两次，历史和auto-copy最多一次。

**交付**：不是仅把 IsSuccess 表达式改名；需要生产Coordinator到存储spy的集成断言和UI动态代码块路径断言。

## T04 / P1 / 依赖 T02、T03：Token 严格恢复与免费传输边界

**入口**：`popglot-domain::protect_tokens/restore_tokens/protected_token_variants`、`StreamingTokenRestorer`、Coordinator免费分支、FreeTranslateService。

**步骤**：

1. 以原文中的每次出现为独立token，恢复时计数并检查恰好一次；定义兼容写法的解析边界，避免裸PG片段误吞普通文本。
2. 占位符命名避开原文已有字符串。可使用每请求namespace+序号；如更换格式，更新prompt/fixture/版本，支持当前请求内可验证的格式即可，不需永久容忍所有坏格式。
3. 所有文本路线进入共同保护→翻译→恢复/校验；可以通过小型FFI纯函数共享Rust域逻辑，不能在C#复制另一套不同regex。
4. 免费引擎使用可注入HTTP，限制响应累计4MiB、总请求deadline、限流冷却和缓存上限；第二目标仍须同一授权，显示实际使用来源。
5. HTTP/JSON格式异常返回可理解错误；429不连续轰炸；缓存命中标识为缓存，不复用旧request ID/耗时伪装本次测量。缓存≤256条，同时限制总字节并线程安全。

**验收**：重复token、缺失token、未知token、同词多次出现、原文含PG_0000、变体跨chunk、Unicode跨chunk；流式/非流式结果与资格一致。免费mock回显遮蔽文本可还原，mock破坏一个占位符则Partial且无副作用。Content-Length缺失而正文超限仍拒绝；HTML/畸形JSON/429/超时/取消可控。日志不含GET原文query。

## T05 / P1 / 依赖 T01：本机/局域网/公网一致分类与权限迁移

**入口**：Rust `is_local_base_url/validate_execution`、C# `ProviderSettings.IsLocalBaseUrl`、`ProfileManager.ResolveRoute`、图片上传标记、隐私页/文档、HTTP redirect行为。

**步骤**：

1. 实现 AI-RULES.md 四分类。新增字段独立表达局域网许可，缺失默认false，不从“有私网地址”推导用户许可。
2. 旧SafeDevMode/NetworkEnabled语义迁移写成版本化规则；保存旧URL/模型/Key关联，不能删用户LAN档案。被新仅本机策略阻止时说明并给“允许局域网模型”入口，不自动放开。
3. 图片发往另一台LAN设备时 `ImageLeftDevice=true`；UI写“局域网服务”，不能写“图片不离开本机”。
4. 统一重定向限制，默认不跨origin；每跳验证策略。未知域名不因为名字含localhost/10就视为本机。

**验收样本**：localhost、127.0.0.1、127.10.20.30、[::1]、192.168.1.20、172.16.0.4、172.200.1.1、10.0.0.5、[fd00::1]、localhost.example.com、带userinfo URL、非法端口、混合大小写、回环302到另一mock origin。网络关闭/仅本机/LAN许可组合使用相同fixture让Rust和C#得出相同结论。不要发真实公网请求做负例。

## T06 / P1 / 依赖 T01、T03：TTS 正确语言、许可、取消和分片

**入口**：`TtsService`、`Services/EdgeTtsService`、ShellSettings与隐私/通用页。

**步骤**：

1. 默认使用Windows本地语音；云端语音可选且明确目的地。保存设置迁移不默认新增允许。
2. Speak接收已知languageTag和CancellationToken；当前语言来自原文/目标语言选择，auto未知才走脚本后备。法语/德语不能用单个>=字符条件判断；无法判断用合理默认并允许用户选择。
3. TTS拥有当前CTS，Stop/下一次Speak/窗口关闭取消当前合成并停止播放；generation继续防旧回调。
4. WebSocket按EndOfMessage组装；单消息头/UTF8文本可能跨Receive，二字节头不完整时等待而非丢弃；二进制拼接后再解析头。
5. 限制文字输入5000字符、音频总量8MiB和总deadline；收到异常close且未完成turn时不得把截断音频当成功。
6. 临时音频生命周期统一，清理两种前缀；最好集中到应用私有临时目录。播放、取消、失败、崩溃残留均覆盖。

**验收**：fr-FR的“bonjour été”用法语；en-US的“Hello 😀”用英语；日文含汉字不因第一个汉字就选中文（有ja标签优先）；offline/云端语音未许可时WebSocket发送0。mock synth等3秒，中途Stop后200ms内看到取消，不播放；音频按1字节/不规则片组装结果逐字节相同；畸形头/超限/中断失败且无残留文件。

## T07 / P1 / 依赖 T00、T03：收藏/历史保存失败可见且不丢数据

**入口**：VocabularyStore、HistoryStore、IHistoryRepository/IVocabularyRepository、LibrarySection及各星标动作。

**步骤**：

1. Save返回结构化成功/失败；临时文件写入、flush、替换成功后再发布内存快照；失败UI保持旧星标或显示待保存状态。
2. 按规则增加词库长度/条数/字节限制；已有超限文件不要直接覆盖或静默丢弃，给导出/恢复路径。
3. 历史损坏保留备份并告知；读取超大/非法结构/空数组中的null条目都安全处理。实际保留期与备份清理规则明确。
4. 收藏身份含词面和语言对；同一词不同目标语言不互相取消；代码标识符大小写默认保留。
5. CSV安全导出默认保护公式前缀 =,+,-,@（包括前导控制字符），如需原样模式明确标注；Anki HTML字符/Tab/换行正确转义，Markdown导出保留可读结构。

**验收**：存储路径是目录、无写权限、替换阶段失败、模拟磁盘满：操作返回失败、UI不假成功、重载保留之前词条；成功保存重启后存在；损坏文件有备份；并发两个变更不互相覆盖；不同语言对两条都在；导出包含引号、逗号、换行、中文、emoji、公式开头和HTML字符，按目标格式读取后正确。

**注意**：使用失败注入/临时路径，不更改真实磁盘权限或清空用户数据。

## T08 / P1 / 依赖 T00：统一有界诊断与异常恢复

**入口**：App.LogCrashToFile/TryNotifyCrash、Coordinator错误映射、ProviderDiagnostics、各catch空块。

**步骤**：

1. 新建小型诊断出口：错误代码、阶段、requestId、受限堆栈、允许的计时；移除UI/日志直接输出任意exception.Message的默认路径。
2. 若必须保留异常摘要，先脱敏token/key/header/query/原文，截断并确保二次异常不崩溃。
3. 日志1MiB轮转、总10MiB、7天保留；清理只匹配已核实的应用日志目录。
4. 全局兜底只作为最后防线，操作失败后取消活动请求、恢复可用状态；不能每帧吞同一UI异常无限循环。
5. 面向用户错误提供“原因+下一步”，例如“连接超时。检查地址后重试”，不显示Rust/SSE/FFI内部栈。

**验收**：构造含合成Key、Authorization、q原文的异常，日志/托盘摘要无原值；超过长度仍受限；1000次错误不无限增长或弹窗风暴；当前session正确Failed/Partial，不继续自动保存。无需读取真实crash日志证明脱敏。

## T09 / P1 / 依赖 T00：修复真实控件配色与焦点

**入口**：Controls.xaml的隐式TextBlock/PrimaryButton/DangerButton/GhostButton/FocusRing；ThemeService；ThemeAuditHelper；动态Markdown渲染资源。

**步骤**：

1. 添加生产控件视觉树反例：Light PrimaryButton.Content="翻译"时内部TextBlock当前是#15171C，测试必须检出。
2. 修正样式作用域/模板文字前景；字符串内容和显式TextBlock内容都必须遵循按钮语义。不要通过把所有文字都改成白色解决浅色主按钮。
3. 保留规则中的品牌方向；新增FocusBrush并用于焦点环，区别低对比选中软边框。
4. 审计normal/hover/pressed/focus/disabled；特别检查危险按钮hover文字。禁用态按例外处理但保留足够可辨识性。
5. 增加SystemParameters.HighContrast优先级与系统颜色映射；主题切换更新已经生成的FlowDocument，不让旧冻结brush留在结果里。
6. token表由代码生成或有一致性检查，更新DESIGN_SYSTEM不继续写旧蓝色。

**验收**：主按钮实际文本/实际底色normal/hover/pressed均≥4.5:1；焦点识别元素对实际相邻背景≥3:1；截图不再黑字主按钮；设置/浮窗/菜单/Markdown来回切深浅主题10次，无文字消失。查询有效值不能仅查Button.Foreground。高对比真机不可用时写未验证，不能因token测试通过就宣称完成真机验收。

## T10 / P1 / 依赖 T00，完成色彩矩阵依赖 T09：重建有效DPI视觉验证

**入口**：Program.RenderAndSaveAtDpi、截图fixture/输出、CI artifact步骤。

**步骤**：

1. 把参数改为明确logicalWidthDip/logicalHeightDip/scale，位图像素=逻辑×缩放；另外提供固定物理视口测试，不混用。
2. 960×640 DIP，scale=2时生成1920×1280，内容应铺满正确画布；禁止用裁掉空白的方法掩盖错误布局。
3. 覆盖6类页面：主工作台、翻译浮窗、查词、设置列表、服务编辑、资料库；每类至少空/正常长内容/错误，主题×缩放基础矩阵自动生成。
4. 增加边界/重叠/内容占比和有效颜色断言；约定动态状态区可掩码，正文/主要按钮不可掩码。
5. 验证输出是虚构数据后再加入CI上传；失败保留产物与diff，不上传用户现有数据。

**验收**：原200%留白错误被检测；移除故意注入的Grid宽度错误前测试会失败；正常输出尺寸自洽。人工至少查看主窗深浅100%、主窗200%、浮窗长文、服务紧凑和错误状态，记录观察。多显示器位置/WindowChrome另列真机检查，不能用离屏图代替。

## T11 / P1 / 依赖 T01、T02、T03、T09、T10：完成可理解的核心旅程

**入口**：MainWindow、TranslateSection、TranslationPanelWindow、QuickSearchWindow、SettingsWindow、ServicesSection、PrivacySection、LibrarySection。

**步骤**：按AI-RULES第6/7节落实：就地首次授权、单主操作、窄宽响应、原文/译文对称、浮窗更多菜单、默认vs保存区别、统一文案。

**明确验收旅程**：

1. 干净配置启动→打开主窗→输入示例→翻译→内联授权→允许→mock完整结果→复制；原文不丢，授权前0请求，完成后复制正确。
2. 同路径拒绝→保留输入→添加自定义引擎→保存（无请求）→设默认→mock翻译。不要强制先测试连接。
3. 服务页修改模型→改回原值变Clean；修改后离开→内联保存/放弃/继续编辑；测试草稿不改持久配置或Key。
4. 中文IME候选确认回车不发翻译；Enter提交/Shift+Enter换行与提示一致。
5. 长模型ID、长错误、720 DIP以下内容区、200%实际缩放：主要按钮不被挤走，语言标签不重叠，不通过缩小正文解决。
6. mock流式中用户向上滚动→后续delta不抢滚动；完成切Markdown后当前位置合理；取消保留部分文本且不开放结果动作。
7. Tab可走完输入/语言/翻译/结果/设置，所有图标有名称和tooltip。缺少Narrator环境时保留待验证记录。

**禁止**：借本任务重画logo、重命名所有内部类、添加新的主导航或无关统计卡片。UI做一轮有证据的改进后锁定，不重复换主题。

## T12 / P1 / 依赖 T03、T04：输入大小与输出预算匹配

**入口**：`provider.rs::output_token_limit/MAX_MODEL_OUTPUT_TOKENS`、各协议finish reason、Core MAX_SOURCE_BYTES、Coordinator、输入计数与错误提示。

**默认实施方案**：

1. 短文本保留自适应低输出预算，不能所有请求都预留巨额tokens。
2. 对长输入增加规划阶段，以段落/句子/代码块边界切分，每段目标不超过800个Unicode标量值；不可拆分的代码块/token超限则明确要求缩短，不硬切标识符。
3. 单段输出预算按原文和目标语言保守估算，上限4096（Provider有显式更低上限时遵循）；估算不足时安全截断状态，不自动重复收费请求。
4. 最多8段、并发1，合并结果维持顺序；整个会话继承一个总deadline和取消令牌，不每段重新延长。响应总量仍有4MiB界限，超过预定任务范围在发送前拒绝。
5. 每段token映射/完整性单独检查，整会话只有全部完成才Completed；任意一段失败→已完成片段可见为Partial，无历史/自动副作用。
6. 若现有协议不适合这套默认参数，可提出有测试证据的修订并记录；不能只把1200改成大数字就结束。

**验收**：4000字符长技术文章、中文→英文、代码围栏、超长单段、64KiB边界、解释开启、视觉转录加翻译。使用mock模拟输出length/max_tokens/异常结束，所有协议均不假成功；第3段取消后第4段0请求；输出顺序/代码保真正确；实际供应商质量和费用未测时明确标注。

## T13 / P1 / 依赖 T05：统一截图路由契约，删除不真实能力声明

**入口**：ProfileManager.ResolveRoute、CoreBridge.PlanScreenshotRoute、Rust RoutingContext/select_route、TranslationCoordinator screenshot路径、WindowsOcrService、隐私线路预览。

**目标决策表**：

| 请求方式 | 本机OCR可用 | 视觉可用且许可 | 行为 |
|---|---|---|---|
| LocalOcr | 是 | 任意 | 本机识别→当前文字路线；不上传图 |
| LocalOcr | 否 | 任意 | 明确缺OCR；不擅自上传 |
| VisionDirect | 任意 | 是 | 所选视觉直译 |
| VisionDirect | 任意 | 否 | 明确阻断，提供用户主动选择本机识别；不静默降级 |
| VisionOcr | 任意 | 是且文字路线可执行 | 视觉识别→文字翻译 |
| VisionOcr | 任意 | 缺任一段 | 明确缺失配置/许可 |
| Auto | 是 | 任意 | 本机优先 |
| Auto | 否 | 是 | 可执行文字路线存在且远程视觉时走识别+文字；否则可用视觉直译 |
| Auto | 否 | 否 | 明确不可用 |

**步骤**：将此表做成版本化纯决策输入/输出。优先Rust domain持有策略，Shell只采集平台事实并传入；若需分阶段过渡，用共享JSON契约用例验证两端直到删掉重复实现。输出reason_code、pipeline、目的地、imageLeavesDevice、是否可执行，设置预览和运行共用。

不宣称自动检测画质/复杂度；没有实际OCR置信度就记Unknown。不要用常量1.0伪装识别质量。Vision请求已有可见delta后失败不自动重发；零delta Auto是否回退需现有可用OCR事实及取消/权限检查。

**验收**：模式×OCR×文字可用×视觉可用×网络×图片许可全部表驱动；预览/执行/FFI一致；区分图像仅本机/LAN/公网。明确VisionDirect与旧规格的行为取舍并同步文档。

## T14 / P2 / 依赖 T00：模型推荐证据诚实并可追溯

**入口**：ModelRecommendationService、ModelCatalogService、ServicesSection推荐调用、TRANSLATION_BENCHMARK文档。

**步骤**：

1. 没有目录声明时Text/Vision能力均Unknown；当前配置可保留但不推导另一模态Supported。
2. endpoint归一化只标准化scheme/host/默认port；保留大小写敏感path及影响目标的query语义，不能保存secret query明文。建议使用已剥离秘密的上下文标识/指纹。
3. benchmark有效性包含相同模型/协议/端点/提示词版本/机器、Success、有限非负数字、时间有效期。默认7天；过期数据显示为历史，不影响当前推荐。
4. 同一上下文使用最新一组样本的中位数，显示n；n<5只显示试测，不宣传稳定排行。
5. 当前无生产benchmark仓库时，先诚实隐藏“本机实测”能力声明；可以实现由用户主动导入schema校验后的离线结果，不自动运行付费测试。
6. 推荐理由用“目录声明/名称推测/本机样本/未知”，健康测试不能阻断保存或用户覆盖选择。

**验收**：无目录当前文字模型不虚构Vision；/Model与/model上下文不同；不同query目标不混淆；旧样本、NaN、负数、错误样本不污染得分；未知模型可手动选择；刷新推荐不改变用户偏好和已选模型。不得硬编码某个最新商用模型为最佳。

## T15 / P1 / 依赖 T00：真实性能采集与预算报告

**入口**：Program.RenderScreenshotsAndMeasureBaseline、App启动与托盘、热键/截图/OCR/流式路径、Rust stream benchmark、scripts。

**步骤**：

1. 删除或更名伪指标：托盘空计时不能保留TrayAvailable名；Core初始化改CoreInitialize；构造窗口改WindowConstructArrange；无活动取消只叫CancelNoopOverhead。
2. 建独立Release应用测试模式，显式隔离数据/凭据，不向真实已运行实例发ShowWindow信号、不占真实热键；性能模式和真机正常模式区分。
3. 按AI-RULES的时间点、样本量和预算输出JSON/CSV；记录机器/版本/原始样本，无正文或图片。
4. 真实热键/窗口首帧不可从隔离模式等效测到时标待真机，不用构造耗时代填。
5. 将现有stream benchmark结果解释为协议处理基准，并与UI首帧测量分别展示。

**验收**：30次独立启动样本、可测试交互100次、空闲稳定采样；P50/P95计算可复算；同一指标不混Debug/Release、测试宿主/应用；未达到预算真实标失败并建立优化项，不删除慢样本或提高阈值。环境不能支持真实测量时交付脚本与明确未测项，不能把T15写成性能已达标。

## T16 / P2 / 依赖 T12、T15：有证据的流式与资源优化

**入口**：TranslationStreamBuffer/PumpStreamAsync、三个UI的ApplyState/Text更新、StreamingTokenRestorer、MarkdownPresenter、窗口/Theme事件生命周期。

**步骤**：

1. 基线覆盖1/100/1000个技术token，1字/32字/4KiB chunk，1K/10K/100K输出规模（大规模用合成mock，尊重生产输出限额）。采集CPU、分配、锁等待、UI泵延迟。
2. 若累计全文复制/重设Text是热点，优先改增量追加、减少累计snapshot次数，终态只校准一次；保持用户滚动位置和selection。
3. 若token多变体扫描是热点，使用按前缀定位/索引等有界算法；不要无证据引入复杂自动机或无锁队列。
4. 审查静态事件订阅和关闭资源；主窗口长生命周期不直接判泄漏，但反复创建的测试/设置/浮窗必须可回收。
5. 修正O(1)/non-blocking注释，用真实复杂度描述。

**验收**：原有流式尾字零丢失、UTF8、epoch、取消、partial门禁全部仍通过；提供同机前后相同数据集结果。不得只报告平均总耗时，要显示P95与内存/分配；未测到瓶颈时保留正确实现，仅修误导注释并记录无需优化的证据。

## T17 / P2 / 依赖 T01—T16中相关功能已稳定：按真实职责整理架构

**入口**：ServicesSection.xaml.cs、TranslationCoordinator、ProfileManager、CoreBridge、Controls.xaml、5002行的测试Program。

**步骤**：

1. 画出并记录最终调用链：View/Reducer→Coordinator→Routing/Policy→Executor→FFI/Core→Provider；Store/Clipboard/OCR/TTS走可注入平台接口。
2. 从ServicesSection抽离纯草稿校验、model catalog/recommendation协调、save操作；窗口仍负责事件接线和呈现。不要把业务藏进万能ViewModel或ServiceLocator。
3. Coordinator保留流程编排，权限/路由/终态资格各一个来源；不将相同逻辑复制到各窗口。
4. 测试按Clipboard、Policy、Session、Provider、Storage、Theme、UI拆分，保留入口可运行。删除脆弱源码Contains测试前，用实际行为测试替代。
5. Controls按Typography/Buttons/Inputs/Menus等资源字典拆分仅在依赖顺序和主题行为有验证时做；不能改引用导致隐式样式失效。
6. FFI调用仍保证所有native字符串释放、GCHandle生命周期、callback不越界/不向native抛异常、request取消隔离。没有业务需要不改ABI。

**验收**：全量必要检查通过；没有新重复路由/权限；UI行为截图不变（除已验收修复）；文件拆分是责任变化而非partial搬家。产出简洁架构说明和资源所有权表，不引入未来平台用不到的框架。

## T18 / P2 / 依赖相应实现：文档、迁移与发布元数据同步

**入口**：README、PRODUCT_SPEC、ARCHITECTURE、DESIGN_SYSTEM、PRIVACY、CONFIGURATION_MIGRATION、VERSIONING、历史执行看板、CI/release。

**步骤**：

1. 更新当前能力、四种截图方式、权限、路由、语音默认、保真限制、数据保存和性能定义。
2. 旧计划加历史归档说明并链接当前契约；删除“全部完成”与现存待办混合的歧义，不抹掉历史事实。
3. 测试数量标日期/commit或改为“以本次命令输出为准”；版本从代码文件核对，不把本次评审等同版本发布。
4. 新schema迁移加入缺字段/损坏/旧许可/旧Key槽/保存失败用例；缺失权限默认拒绝，保留可恢复备份。
5. 发布脚本维持Rust native DLL、self-contained包、SHA256；不执行发布。第三方依赖若新增，记录许可证和版本选择依据。

**验收**：全文检索不再存在当作现状的0.1.2/旧配色/113tests/伪托盘1ms/自动画质识别/纯本机却放LAN等矛盾；历史引用可以保留但必须标历史。说明里的命令确实存在，不能写无法运行的示例。未实际测量的质量/性能用明确待测描述。

## T19 / P1 / 依赖 T01—T18：独立整体验收与交付

**执行者**：建议由项目负责人另开一个AI审查上下文执行README中的独立验收提示词。当前实现AI仍需先完成自验；不能用“等待另一个AI”跳过已授权可做的测试。

**必查**：

1. 整理全部任务状态及证据，复核最初反例确实修复；不能凭新的测试数替代缺陷结论。
2. 跑fmt/workspace tests/clippy/WPF Release/Windows逻辑测试；记录真实命令与退出码。对有环境限制的Debug说明，不强制终止用户进程。
3. 核对T01授权发送计数、T02代码复制、T03带warnings final历史计数、T07磁盘失败、T09内部文字实际颜色。
4. 查看固定fixture截图；确认CI可安全上传；按支持矩阵注明真机尚未覆盖项。
5. 准备但不自动发布便携包的隔离smoke：空配置启动、无OCR、Key不存在、native DLL缺失、旧schema升级、设置保存失败、退出清理。Windows干净账户/VM不足时写明未验证。
6. 最终差异回看：没有真实用户数据、密钥、私人截图入仓；没有无关功能；没有扩大权限；没有删除失败测试或放宽门槛掩盖问题。

**最终结论使用以下等级**：

- 可继续内部使用：主要闭环已验证，仍列明确限制。
- 可候选发布：阻断缺陷为零，规定自动/真机验收完成，文档与迁移齐全。
- 不建议发布：仍有未授权外发、代码损坏、不完整落盘、数据保存假成功等问题。

不使用“100%完美”“永久安全”“全部平台均正常”。未验证与失败分开列，分别写下一步，而不是合成一个好看的通过百分比。

## 执行日志模板

```markdown
# PopGlot 整改执行记录
基线提交：
当前检查日期：
工作区原有修改：

| ID | 状态 | 根因/变更简述 | 证据 | 未验证/阻塞 |
|---|---|---|---|---|
| T00 | Todo | | | |

## 每轮记录
任务编号：
修改文件与方法：
旧错误如何被测试捕获：
命令、退出码、真实数量：
截图/基准/用例路径：
兼容性与迁移：
未验证条件：
下一项与第一处代码入口：
```

创建日志时填写T00—T19全部行。任务完成状态只能由实际证据推进；本任务书下发不代表任何整改已经完成。
