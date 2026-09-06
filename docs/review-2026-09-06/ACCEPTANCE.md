# PopGlot 独立验收与 UI 产品复审

验收日期：2026-09-06。结论：**不建议发布**。隐私授权生命周期、复制保真、取消后持久化、保存失败可见性仍有阻断缺陷，不能由测试数量或平均评分抵消。

本轮没有修改产品实现或既有测试。新增本报告和 `artifacts/acceptance-2026-09-06/` 独立探针；运行既有测试重新生成了截图。没有提交、推送、发布、真实供应商调用或使用私人输入。探针使用生产 Release DLL、生产 App 资源初始化（不运行 OnStartup）、临时存储和内存 HTTP；无真实剪贴板写入。

## 范围、日期与实际运行

读取了 `docs/review-2026-09-05/` 的 README、REVIEW、AI-RULES、TASKS、EXECUTION-LOG，并核查当前实现、相关 diff、测试、CI、性能脚本和产物。基线/HEAD 都是 `feb77d5`；当前有 45 个已跟踪文件的未提交变更，另有新增实现文件。没有找到此前独立的 6 号评审目录；6 号工作仍写在 5 号目录下，执行记录头部日期也未更新。此次覆盖的是**截至本次验收的整个工作树，包括 6 号修改**，并非只审 5 号提交。无逐轮提交可用，不能仅靠文件时间精确归属每个 hunk 的作者和日期。

| 本轮命令 | 结果与边界 |
|---|---|
| `cargo fmt --check` | 无差异输出；同一顺序命令组随后继续 test/clippy。未单独捕获 fmt 退出码，不虚构独立进程退出码。 |
| `cargo test --workspace --locked` | 180 passed / 0 failed（67+12+14+27+5+45+10）；顺序命令组最终 exit 0。 |
| `cargo clippy --workspace --all-targets --locked` | 完成、无警告输出，exit 0。 |
| `dotnet build tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj -c Release` | exit 0，0 警告、0 错误；包括生产 WPF Release 与 native Release 构建。 |
| `tests/PopGlot.Windows.LogicTests/bin/Release/net10.0-windows10.0.19041.0/PopGlot.Windows.LogicTests.exe` | exit 0，164 passed / 0 failed；真实配置哈希守卫与免费引擎发送守卫通过。运行前无 PopGlot 进程。 |
| `dotnet run --project artifacts/acceptance-2026-09-06/Probe.csproj -c Release` | 独立探针完成；结果见 `artifacts/acceptance-2026-09-06/probe-output.txt`。探针是观察器，退出成功不代表断言全部通过；输出中的反例是验收失败证据。首次探针编译因 ShellSettings 构造参数错误失败，修正为 Default 后重跑成功。 |

已有性能 JSON 的时间是 `2026-09-06T00:21:51Z`，含 30 个独立启动样本。未重跑已发现计时边界错误的性能脚本，也未拿本轮 190.8 MiB 的**测试宿主**工作集判定应用空闲内存。

## T00—T19 判定

“通过”指任务要求的范围已有足够证据；“不通过”指至少一个明确断言失败/明确要求未实现；“未验证”指不能用已有证据完成整项验收。局部修复有效会单列，不把整项强行涂绿。

| 项目 | 判定 | 有效证据与未过部分 |
|---|---|---|
| T00 隔离与基线 | **未验证** | 本轮隔离启动、内存凭据、真实配置哈希、免费发送守卫均通过，截图见 Demo fixture。但 TestIsolation 默认只拦 FreeTranslateService，未在所有 HTTP/WebSocket/native 最终边界安装统一拒公网策略；真实文件快照在 Core 初始化后才取。全边界隔离契约未证明。 |
| T01 免费引擎授权 | **不通过** | 12 组合与强制健康生产路径通过，构窗不自动探测；但撤销后旧凭证健康发送=1，AllowOnce 翻译后还能健康发送，第一目标失败并撤销后第二目标仍发送。见 F01。 |
| T02 文本保真 | **不通过** | 两种 foo_bar_baz、__init__、中文行内路径已通过；三入口真实复制测试有效。代码块首行缩进、Tab、尾空格和空行仍被 Trim 删去，未闭合围栏也被吞。见 F02。 |
| T03 Partial/终态副作用 | **不通过** | warnings final、is_partial、空终态、代码复制禁用、历史至多一次既有测试通过；但取消发生在完整 final 返回前时仍 Completed+history=1。见 F03。不能仅凭 UI epoch 过滤证明持久化被过滤。 |
| T04 token 与免费边界 | **不通过** | Rust 重复/缺失/未知 occurrence、跨 chunk、FFI 免费保护的既有测试通过；生产 HttpClient 仍先整包缓冲再执行 4 MiB 读取限额，且两目标各用超时而非整个操作的总 deadline。见 F04。 |
| T05 本机/LAN 分类与迁移 | **不通过** | C#/Rust 分类对拍、LAN 拒绝、跨源 Rust redirect 测试通过；LAN 许可没有 UI 入口，编辑档案重建时不保留 AllowLanEndpoints；模型目录入口未执行 LAN 权限且默认跟随 redirect。见 F05。 |
| T06 TTS | **不通过** | 法语标签、emoji 英文、逐片组装、Stop 取消、独立云许可既有测试通过；合成链没有总 deadline，畸形二进制头直接跳过，后续 turn.end 可能把剩余音频当完成；云失败转本地时丢 languageTag。见 F06。真实音频输出/长挂起未实测。 |
| T07 存储与导出 | **不通过** | 目录路径收藏失败可见、同词语言对、同实例并发、损坏备份既有测试通过；历史失败被 Coordinator 忽略，CSV 公式保护破坏字段结构，历史 CSV 无公式保护，词库读取缺 32 MiB 前置限额。见 F07。真实磁盘满/flush/替换注入未覆盖。 |
| T08 诊断与恢复 | **不通过** | 已有常见 bearer/key/query 脱敏与轮转测试通过；普通原文与非特定形状 api_key 仍写日志，任意异常 Message 仍进入 UI，Coordinator 继续用 Contains 分类。见 F08。 |
| T09 实际颜色与焦点 | **不通过** | 实际主按钮正常态内部文字已修复；生产 App 资源下旧 FlowDocument 切主题仍保留旧色，高对比系统映射未实现；hover/pressed 实际视觉树没有完整验收。见 F09。 |
| T10 DPI 与视觉证据 | **不通过** | 960×640 DIP、200%=1920×1280 正确，旧透明留白反例可检出；但检查器只统计 alpha=0，不能检测不透明空白、遮挡和文字截断；缺六页面×空/长/错完整状态矩阵和生产 Grid 宽度 mutation。见 V01。 |
| T11 核心用户旅程 | **不通过** | 窄窗上下布局、草稿守卫、部分动作路径通过；IME enabled 误当 composition，空态只有单击直接持久授权而没有任务要求的披露/允许并开始/拒绝旅程；浮窗语言截断、多主按钮仍在。见 F10、V02—V04。 |
| T12 分段与预算 | **不通过** | ~4000 字技术文、段序、第三段取消、拒绝超大围栏/64 KiB 既有测试通过；超长标识符会被硬切，整会话没有输出累计上限，1200 token 上限未解决视觉输出与扩写预算。见 F11。4000 字实际长译文 UI 尚未验证。 |
| T13 路由契约 | **不通过** | Rust 单决策函数与 14 行 FFI 对拍是有效改进；Shell facts 把 NetworkEnabled=false/SafeDevMode=true 下所有视觉都判不可达，连 loopback 视觉也被挡；TextRouteAvailable 仅看档案存在。见 F12。要求的完整组合矩阵缺失。 |
| T14 推荐证据 | **不通过** | Unknown、path 大小写、query 指纹、NaN/负数/旧样本与中位数测试通过；未来一年样本被接受，协议身份缺失且聚合全部七天有效样本而非最新批次。见 F13。 |
| T15 性能证据 | **不通过** | 确有独立 Release 应用 30 条历史样本；计时从 RunStartupSmoke 内才开始，漏进程/.NET/WPF 启动，脚本还会回退 Debug。不能认定启动预算通过。空闲 WS/CPU、100 次交互、真实首帧未测。见 F14。 |
| T16 流式优化 | **未验证** | 复杂度注释修正合理，保留实现也合理；没有要求的 token/chunk/输出规模采样、P95/分配/锁等待以及同机对照，不能把“未采样”解释成“没有热点”。 |
| T17 架构 | **不通过** | 死路径清理、ServiceDraftCoordinator 纯函数抽离有效；执行记录自己限定“第一切片”，目录/保存协调、测试分域、资源所有权表未交齐，且发送策略仍多源。不因文件较长本身判错。 |
| T18 文档与迁移 | **不通过** | README/部分模式已更新；PRODUCT_SPEC:152、ARCHITECTURE:163 仍将 113 项当现状，隐私 UI 仍写“切断一切网络请求”；执行记录声称这些零命中，与实际不符。见 F15。迁移失败/旧 Key 槽矩阵未完整重验。 |
| T19 整体验收 | **不通过** | 本报告完成独立复审，但 T01—T03 与 T07 尚有阻断缺陷，便携包干净账户/native DLL 缺失/无 OCR/退出清理的完整 smoke 未运行，不能候选发布。 |

没有给出平均分；没有整项通过不等于所有已修复子项都无效。

## 阻断与重要反例：最小复现及应补断言

### F01 / P0：许可是可复用快照，没有约束下一次发送

证据：`FreeTranslateService.cs:88,128,140,222,384`，`OutboundPolicy.cs` 的 `FreeEngineAuthorization`。`EnsureOutboundAuthorized` 只看签发时 settings 的网络开关；不读当前 consent、不消费 IsOnceOnly，未在 endpoint 循环内重新验证；健康使用 CancellationToken.None。

探针从生产 OutboundPolicy 获取 Allowed 授权，再把 loader 改 Denied，调用生产 GetHealthAsync(force:true, auth)，mock **发送 1 次**。AllowOnce 用于普通翻译后继续健康探测，总 **2 次**。更贴近用户撤销的反例：第一 endpoint mock 返回 503 时撤销，生产循环仍访问第二 endpoint，总 **2 次**。

应补：签发→撤销→健康/备用目标的发送计数为 0；一次性授权只能绑定那个会话，不得用于未来健康；撤销在途后观察取消，并禁止后续目标。旧 12 组合测试每次拒绝都传 null，因此无法检出授权过期/复用。

### F02 / P0：代码块仍非逐字复制

证据：`MarkdownPresenter.cs:138` 对总输出 Trim；`:212,312` 对代码按钮内容 TrimEnd。生产探针：

```text
输入：```python\n    foo_bar_baz()\n\n```
实际普通复制：foo_bar_baz()
输入：```\n\t__init__()  \n\n```
实际普通复制：__init__()
```

未闭合围栏也直接被识别并删除。标识符本身修复并不等于缩进保真；Python 等场景首行缩进有意义。应补：仅含代码的结果，首/末代码行、Tab、空行逐字符比较；代码按钮经内存剪贴板点击，明确只移除容器围栏及其约定分隔换行，不能 Trim 内容。三入口均要相同；未闭合容器按保守契约保留。

### F03 / P0：取消后迟到完整 final 仍落盘

证据：`TranslationCoordinator.TranslateTextAsync` await 后无取消复查，`:1115 ApplyFinalResponse` 不使用传入 epoch 防止提交，直接调用 WriteHistoryOnce。

独立探针注入只替代传输的 ITranslationExecutor：在 TranslateFreeAsync 返回完整结果**前** cancel 当前 token，然后返回完整响应。生产 Coordinator 返回 **Completed**，真实临时 HistoryStore 的 **Count=1**。这是取消与成功响应交错的确定性测试，不声称已在真实供应商上发生。

应补：不可依赖 executor 一定抛取消异常；在接受终态、写历史及自动动作前重新检查会话仍有效。用完成屏障安排 cancel/close/new epoch 与 final，断言 history/copy/TTS/star=0。现有 warnings/Partial 测试确实调用生产方法，但没有这一交错。

### F04 / P1：免费响应上限位于 HttpClient 隐式缓冲之后

证据：`FreeTranslateService.cs:140` 使用 `HttpClient.SendAsync(request, cancellationToken)` 默认完成模式，返回时响应已缓冲；之后的 ReadCappedAsync 无法约束之前分配。现有 HttpSenderOverride 直接返回 StringContent，绕过了生产 HttpClient 的缓冲行为。

应补：通过可配置的实际 transport/local server 发无 Content-Length、超 4 MiB/慢流，观察读取量与中断时点；最后发送采用 headers-first 再累计限制。整个免费操作需一个 deadline，不能首目标 12 秒失败后又给第二目标 12 秒。429 当前 continue 下一目标，应明确并测单操作与冷却期发送次数。缓存 ElapsedMs=0 有改进，但缺显式 cache 标记，RequestId 仍恒 free-web。

### F05 / P1：LAN 许可和模型目录路径没有闭环

证据：全仓搜索 AllowLanEndpoints，存在 settings/profile/FFI，却没有设置控件；`ServiceDraftInputs/BuildDraft` 没有此字段，`ServicesSection.TrySaveService` 用新 draft 替换旧档案，已授权 LAN 编辑保存后回到 false。独立分类单测不会发现这个往返。

`ModelCatalogService.cs:46-67` 只检查两个全局开关，没有 LAN 许可，SocketsHttpHandler 未禁重定向。相反，全局离线又一律挡 loopback 模型列表。这不符合“同目的地统一策略”。

应补：通过服务页开关授予 LAN→保存→改名称→重载许可仍在；未许可 LAN 获取模型 sends=0；loopback 在仅本机可列模型；双 loopback mock origin 302 不得跟随。这里没有发真实 LAN/公网验证泄漏，目录 redirect 为代码确定的默认行为风险。

### F06 / P1：语音挂起与坏帧验收缺口

证据：`EdgeTtsService.SynthesizeToMp3FileAsync` 接收循环没有 total deadline；二进制 payload<2 或声明 header 超出 payload 时直接跳过，非结构化失败。`TtsService.Speak` 云语音失败 fallback 调用 `SynthesizeLocalToFileAsync(text)`，丢弃已知 languageTag，而通常本地分支有传入标签。

应补：mock 永远不发 turn.end→到总 deadline 必须退出；有效音频+非法头+turn.end 必须失败且不留下文件；云失败→本地 synth 的 fr-FR 标签不丢。5000 字/8 MiB、分片重组和 Stop 200ms 的已有测试属于局部通过，不证明这些场景。磁盘写音频部分失败后的临时文件清理也未闭环。

### F07 / P1：历史失败假提交，导出格式坏，词库读无限制

1. `TranslationCoordinator.cs:1185` 在 TryAdd 前设 HistoryCommitted=true，随后忽略返回值。探针将 HistoryStore 路径设为目录：返回 **HistoryCommitted=true、Completed、Error=null**。应补 production Coordinator+失败 repository 的集成断言：不能报告已保存，要显示未保存并允许恢复；翻译完整状态和存储状态可以分开，不必把完整译文伪装成翻译失败。
2. `VocabularyStore.cs:265` 返回 `'`+已带双引号字段。输入 `=SUM(1,2)`，生产导出成为 `,'"=SUM(1,2)",`；用 .NET TextFieldParser 真解析，**表头 9 列，记录 10 列**。应在字段内容内添加 neutralizer 再 CSV quoting，并真实 round-trip 检查逗号/引号/换行/中文/emoji/控制前缀。当前测试注释写 Parse，实际只是 Contains("'\"")，恰好锁定了错误字节。
3. `HistoryStore.cs:222` 只 quote，无公式前缀防护，探针原样导出 `=SUM(1,2)`。两种 CSV 应共享契约。
4. `VocabularyStore.cs:299` File.ReadAllText 前不查大小。探针用 33,554,434 字节的合法数组（空白填充）加载后添加词条，返回 Persisted=true；并非拒绝/提示超限。其 .bak 仍保留原文件，不把这个探针夸大成“旧词条已丢失”，但它证明读取上限与不可静默重写超限文件的契约未实现。

补充负例：独立探针锁住已有 history 文件，本次 TryAdd 返回 Failed、重载仍保留 old；**没有复现这一锁定条件下的旧历史丢失**。不要把风险写成既成数据损失。真实磁盘满、flush 失败、替换失败的可控注入仍应补；不能用“路径是目录”代替所有阶段。

### F08 / P1：白名单诊断仍未落实

生产 BuildEntry 对 `Exception("request source: synthetic-private-source; api_key=synthetic-secret-123")` 完整保留合成原文和合成 secret。`DiagnosticsLog` 靠常见形状 regex，无法兑现“日志不含任意原文”。`TranslationCoordinator.ClassifyException` 仍原样使用 Message，并用 Key/network/中文 Contains 决定状态；截图错误同时出现“模型网络目前未启用”和“连接超时”也暴露分类与呈现混乱。

应补：生产诊断默认只允许 code/stage/requestId/受限 stack/计时；合成不带 key 特征的正文必须不出现。包含这些数据的异常需驱动 UI/托盘，验证不原样显示。不将本探针推断为真实用户密钥已经泄漏。

### F09 / P1：生产主题切换留下冻结的旧文字画刷

生产探针 `new App(); InitializeComponent()`，不执行 Startup；Apply(Dark)→RenderToFlowDocument→Apply(Light)。实际：画刷 IsFrozen=true；已有 Run 前景仍 **#FFEEF0F4**，当前 TextPrimaryBrush 已 **#FF15171C**。三个文本窗口没有 ThemeChanged 重建文档路径，ThemeService 在画刷冻结时创建新实例，旧 Run 的局部值仍握住旧实例。

执行记录声称生产 App 画刷未冻结、原地更新正常，探针不支持该说法。已有十轮测试每次**新建按钮**，不能检出旧文档引用。应补：真实 App 资源、同一个窗口/文档往返十轮，检查普通文字/代码/链接前景及实际相邻底色；再检查实际 hover/pressed/focus。HighContrast 优先级及 SystemColors 映射仍未实现，不能以不可用真机环境掩盖未实现代码。

### F10 / P1：IME 防误提交变成广泛禁用 Enter

`TranslateSection.xaml.cs:487`、`QuickSearchWindow.xaml.cs:82`、`TranslationPanelWindow.xaml.cs:1156` 以 `GetIsInputMethodEnabled` 为条件直接 return。独立生产资源环境中新建 TextBox 的该值即 **true**，没有 composition。它表示控件允许输入法，不表示当前正在确认候选。

应补：正常输入英文/中文提交后的非 composition Enter→一次翻译；候选确认 Enter→0 请求；Shift+Enter→换行。通过真实控件键盘动作或可测 composition 状态完成，不能只判断源代码有 InputMethod。另：当前首次授权按钮直接 Save(Allowed)，不等于任务书要求的披露、拒绝、允许并开始完整旅程。

### F11 / P1：分段能重拼原文，不等于逐段保真

生产 FFI PlanSegments 输入 `new string('a',799)+"_foo_bar_baz"`，返回两个段：第一段以 `_` 结束，第二段 `foo_bar_baz`，未拒绝。`popglot-domain/src/lib.rs:1433` 在无自然语言边界时无条件 hard_chunks。技术 token 在保护前被拆了，后续“各段都恢复正确”也无法保障整个标识符没被当普通文字翻译。

应补：超预算不可拆标识符/行内 code/path 原子拒绝，CJK 自然文本仍可切；还要测总会话 >4 MiB 累积，不能依赖每段 4 MiB。`provider.rs:20,266` 仍为 1200 token，视觉直译无分段，长中文→英文+解释要覆盖四协议 length/max_tokens 后的 Partial。既有长输入 test 是 mock 段标记，不证明 4000 字最终译文在 UI 上的可读性。

### F12 / P1：共享决策表的输入事实仍会错

`ProfileManager.cs:ResolveRoute` 的 visionReachable 恒为 NetworkEnabled && !SafeDevMode，没有 loopback 例外；因此仅本机模式下本机视觉模型也被当未配置。`TextRouteAvailable = providers.Text is not null` 未表达文字路线当时是否允许执行。纯 Rust/FFI 对拍用的是人工 facts，不能覆盖 Shell 采集事实错误。

应补：从生产 ProfileManager 输入回环视觉档案、无 OCR、仅本机模式，VisionDirect 应可执行且 ImageLeftDevice=false；LAN/公网被阻断。再以 blocked text route 检查 VisionOcr 预览不得显示可执行。完整组合需包括 settings→facts→FFI→执行，而非只对拍手写 facts。

### F13 / P2：推荐证据仍接受不可能的日期

生产 ModelBenchmarkMetric.MatchesContext 对未来一年 Timestamp 返回 **true**；只有 `now - timestamp > 7 days` 检查，没有 future guard。Context/Metric 没有协议身份；聚合逻辑收集全部仍有效日期记录，没有最新一组 run/batch 的区别。

应补：未来时间拒绝/定义小范围时钟偏差；不同协议不混用；最新一批 n 的中位数可复算。当前无生产 benchmark 仓库，不夸大为已有用户排序受到影响，但任务的证据契约没完成。

### F14 / P1：6 号的启动 PASS 仍测错边界

`App.xaml.cs:80` 在 RunStartupSmoke 才 StartNew，入口已在 OnStartup 后，未覆盖进程创建、CLR/WPF 与资源初始化；`:CreateTrayIcon` 返回也未证明 Shell 托盘首帧已可见/可交互。脚本若 Release 不存在会自动回退 Debug；JSON 的 dotnet=4.0.30319.42000 是 PowerShell 宿主运行时，非 .NET 10 应用运行时。首条标 cold 未控制 OS 缓存，不足以称冷启动。

30 条历史样本确有数据，P50=181/P95=200 仅可描述该内部阶段，**撤销应用完整启动预算 PASS 的结论**。脚本按成功样本计算并可能在部分启动失败时仍 PASS，也缺完整提交/diff/构建元数据。

应补：父进程启动前计时至子进程明确 readiness marker；严格 Release 并记录应用 runtime、构建 hash、全样本失败情况；独立应用稳定 30 秒后的工作集、60 秒 CPU、100 次真实计时边界交互单列。无需为了修报告提高阈值或删慢样本。

### F15 / P2：文档验收的“零命中”不成立

本轮 `rg` 找到 PRODUCT_SPEC:152 和 ARCHITECTURE:163 的现状 113 项测试；PrivacySection.xaml 的“切断一切网络请求”“纯离线”仍存在。EXECUTION-LOG 头部还是 2026-09-05，表中 Verified 与正文未实现项冲突；T17 使用“Verified（第一切片）”并不能作为整项结论。应补范围明确的现行文档校验，保留历史事实但标时间，不重写全部旧计划。

## UI 产品经理视角复审

结论：**当前 UI 不通过产品验收**。这不仅是主色偏好；阅读层级、操作主次、短标签可读性、状态一致性、品牌资产细节都未完成。本轮只评审，不换色、不重画图标。用户此次已明确提出重新审视主题和图标，旧任务书“禁止重画 logo”的限制不应被拿来拒绝后续设计讨论。

### V01：截图能出图，仍不构成验收矩阵

人工打开本轮生成的主窗深/浅 100%、主窗浅 200%、主窗窄深色、设置浅色、紧凑服务编辑浅色、隐私浅色、浮窗深色空态、浮窗错误态，以及品牌源资产。服务/资料库夹具来自固定 Demo config 和合成记录；没有在这些图中看到私人内容。

200% 图是 1920×1280，内容铺满，像素/DIP 这部分通过。测试覆盖的是 6 页面×3 缩放×2 主题，**并不是再乘空/正常长/错误三状态**；所查看的基础浮窗是空态，不是日志所谓“长文”。4000 字长译文浮窗、查词长文、每页错误态没有完成矩阵。

检查器只统计完全透明像素，背景不透明时即使正文宽度错误、文字被剪掉仍可过。`settings_privacy_light.png` 显示“保存历史”紫色已开但滑块仍左侧；Controls.xaml 使用 160ms Storyboard，渲染器 Measure/Arrange 后立刻截图，未等待动画。这证明取样时刻不足，**真实开关稳定态是否错误仍未验证**。应加入静止状态与动画完成后的状态，控件边界/重叠/文本可见范围断言，再看图。

### V02：主色没有承担稳定的操作语义

紧凑服务编辑同一屏“添加引擎”“验证连接”“保存修改”都是实心紫色，用户没有一个清晰主动作；文字/图片默认选择器、推荐 chip、toggle 又各自有不同强调方式。先让每个区域只有一个提交主按钮，连接验证和获取模型降为次级动作，默认选择显示即时生效。主题色不是通过反复改紫色色值就能解决。

建议后续建立一套中性表面+一个品牌强调色+独立状态色的候选，在**同一批真实 WPF 页面**上比较。当前浅色细边框/灰说明与大量空白组合显得表单化，深色多重近黑框叠加显得沉重；精简阅读区框线与嵌套表面、提高文字层级，比铺更多卡片有效。具体新 hex 本轮未设计或验收。

### V03：开关、选择与按钮缺少统一层级

隐私页长句与右侧小开关分离，开启/关闭状态主要靠颜色和滑块，截图又把状态拍错。应确保稳定布局明确的 on/off 位置、相同 hit area、键盘焦点、禁用说明；不把 Switch、CheckBox、选项 chip 全画成相同紫色药丸。

语言选择应使用可读短名/详情 tooltip，预留最低文字宽度；浮窗 420 DIP 截图显示“自动检”“简体中”，这不是美术意见，是可读性缺陷。使用紧凑短标签、将低频项收进更多菜单，禁止通过缩小正文解决。

### V04：布局与错误呈现不够成熟

主窗侧栏仅两项，却在窄窗继续占 168 DIP；560 宽截图中约三成宽度用于导航，核心阅读区域受挤压。窄屏可折叠为有 tooltip/名称的导航轨，桌面宽度保留文字导航。当前“自动识别源语言/自动检测”重复说明仍占语言栏。

浮窗顶栏塞固定、logo、应用名、两个语言、交换、展开、设置、关闭；原文栏又有多个图标，低频项没有按任务约定收入更多。错误截图中红色标题、红色错误正文、底部长红字同时出现，且“网络未启用”和“超时”指向不同原因；应保留一个明确失败主句和一个下一步，把诊断细节收拢。

服务编辑顶部“添加引擎”在编辑已有服务时仍竞争注意力；在 620 宽/720 高 fixture 中关键模型字段需要滚动才到，当前截图本身不足以证明完成配置旅程。不要把“无重叠”当作“好用”。

### V05：应用图标需要单独设计验收

已打开 `Assets/popglot-app-avatar-v3.png`：紫色方底、白/高饱和青色两块图形，上下各有一条黑横线。图中的黑线是 PNG 内容，不是 UI 渲染边框。`popglot-mark-selected-source.png` 有复杂边缘、光晕和残留细节。当前标题栏小图标仍呈方框式、细节拥挤，与克制的正文界面不一致。

后续建议用干净几何结构定义可辨认的“翻译/对照”核心符号，再统一描边/实心图标体系，明确品牌色与状态色区别；不要直接把大幅来源图缩小当托盘图标。验收至少看 16/20/24/32/48/256 px、浅/深托盘背景、任务栏与高 DPI。两条黑线是否设计意图可由负责人确认，但现在的资产不应未经检查直接沿用。这里没有生成替代 logo，也没有宣称哪种新风格已获用户认可。

## 测试是否真正检出旧错

| 测试族 | 本次判断 |
|---|---|
| 免费授权矩阵 | 生产 OutboundPolicy/Coordinator/健康服务+发送计数，有 Allowed sanity；能检出原先 Unset/null 无门禁，不覆盖旧凭证复用/撤销。 |
| 文本复制 | 调用生产 formatter，三窗口真实动作+内存剪贴板，能检出旧 foo_bar_baz 正则损坏；没有首末代码空白反例。 |
| Partial | 生产 ApplyFinalResponse/Coordinator+历史 spy、面板 gate 和动态代码按钮有效；未覆盖取消成功竞态、全 UI 自动 TTS/star 组合。 |
| SSE/token | 本地 HTTP mock 驱动生产 Rust provider/restorer；截断 EOF、UTF8、重复/缺失等结果有行为断言，本轮通过。四协议所有长度停止组合不能仅由这些测试名推导。 |
| 存储 | 目录失败、重载与并发真实生产存储有效；CSV 测试并未真正 parse，反而断言错误 `'"` 字节；磁盘各失败阶段缺注入。 |
| 颜色/截图 | 主按钮正常态读实际内部 TextBlock 是有效改进；十轮主题每次创建新按钮，放过旧文档冻结；透明像素检查放过不透明布局错误。 |
| 架构/旅程 | 仍有大量 ReadAllText/Contains 类型源码检查；不能证明 IME 组合状态、焦点、权限运行链。 |

本轮未把旧实现重新注入整个仓库运行完整 mutation suite，因此不声称“所有原始缺陷的 mutation 都已失败”。独立反例已明确证明现有全绿套件仍不能检出 F01/F02/F03/F07/F09/F10/F11/F13；后续应先把这些最小反例转成失败测试，再修改实现。这样无需重写 T00—T19 全部任务书。

## 真机与交付边界

未验证：混合 DPI 多显示器、真实 WindowChrome/焦点/跨应用快捷键、中文输入法完整候选流程、Narrator、高对比系统切换、真实扬声器、OCR 语言包、供应商语义质量与费用、干净 Windows 账户便携包 smoke。没有把离屏图片当真机证据。

后续顺序：先关闭 F01/F02/F03/F07 阻断，再处理 F05/F08/F09/F10/F11 的核心可靠性与视觉行为；UI 设计用上述页面和控件反例做一轮具体方案并验证，不能再以“截图生成成功/测试全绿/Verified 标签”结束验收。
