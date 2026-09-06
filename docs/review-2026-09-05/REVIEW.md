# PopGlot 全面评审

日期：2026-09-05；基线：`feb77d5`；技术栈：Windows WPF / .NET 10、Rust domain/core/ffi；版本文件为 0.1.3。

## 1. 总判断

项目有值得继续投入的基础：定位明确、入口齐全、有流式分层、剪贴板事务、独立凭据槽、取消隔离、受限响应读取和大量离线测试。现阶段最缺的不是更多功能，而是“所有入口遵守同一契约”和“测试结果真正对应用户体验”。

建议定位为：**阅读英文技术内容时，随叫随到、保留代码原样、清楚说明数据去向的 Windows 翻译工具。** 主窗口服务长文本，浮窗服务即时阅读；设置服务配置。不要把普通用户主流程扩张成模型排行榜、聊天工作台或多平台框架展示。

不建议整体重写，不建议换掉 WPF，不建议为了高级感重新设计整套品牌色。先封闭隐私、文本保真、Partial 副作用和数据保存问题，再修 UI 实际样式，最后用真实性能数据推动重构。

## 2. 证据范围与边界

已检查：产品/架构/设计/隐私/历史计划文档；主要窗口及 XAML；设置、服务档案、翻译协调器、Markdown、TTS、OCR、存储；Rust token、流式、Provider、FFI 的关键实现；测试入口和 CI/release 流程。

实际运行：

| 检查 | 本次结果 | 能证明什么 |
|---|---|---|
| `scripts/verify.ps1` 中的 Rust fmt/test/clippy | fmt 通过；150 个 Rust 测试通过；clippy 通过 | 当前离线契约与静态质量检查通过 |
| 默认 Debug WPF 构建 | 被运行中的 PopGlot.exe 锁定；MSB3027/MSB3021 | 环境占用，不能算代码编译失败，也不能称完整 verify 通过 |
| `dotnet run --project tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj --configuration Release` | 成功，121 passed / 0 failed | Release 构建及现有 Windows 逻辑/渲染测试通过 |
| 独立 .NET 10 组件探针，反射调用本次 Release DLL | 复现文本、语音、收藏失败和按钮前景问题 | 下文列出的具体生产方法行为 |
| 本次测试生成截图 | 实际查看 7 张：主窗口深浅、设置浅色、紧凑服务编辑浅色、翻译浮窗深色、查词浅色、主窗口 200% 浅色 | 这些离屏场景的视觉结果；不是完整真机体验验收 |

未进行：真实供应商付费调用、真实翻译质量盲评、抓包证明所有路径零出网、管理员窗口和跨应用完整流程、多物理显示器混合 DPI、Narrator 全流程、独立应用多次冷启动/空闲测量。

特别发现：尝试给测试子进程设置临时 LOCALAPPDATA，并未使截图使用虚构配置；截图仍显示现有服务信息。代码使用 Windows Known Folder，并且窗口创建时读取全局配置。**不得声称测试已经隔离；不得把这些截图直接上传到公开 CI 或文档。** 本次没有打印密钥、主动调用真实翻译服务或修改产品源码。后续必须先做 T00。

证据标识：A = 本次生产组件直接复现；B = 源码调用链可确定；C = 风险存在但尚需特定环境/输入复现；D = 产品/设计判断。以下路径相对于仓库根，行号仅指本次基线，后续以方法名查找。

## 3. 必须优先修复的缺陷

### F01 / P0 / B：健康探测绕过免费引擎授权

- 入口：`MainWindow.xaml.cs:137` 的 `UsesFreeEngine` 使用 `consent != Denied`，因此 Unset 也满足条件。
- `RefreshEngineStatus` 可自动进入 `UpdateFreeEngineHealthAsync`；后者在 159 行直接调用 `FreeTranslateService.GetHealthAsync`。
- `FreeTranslateService.cs:256` 的探测最终直接调用 `TranslateAsync("ping ...")`，未经过 `OutboundPolicy.AllowsFreeEngine`。
- 影响：首次尚未授权也可能发生真实健康请求，违背“未授权不发任何请求”的声明。这里可确定的是探测文本，不应夸大成已经泄漏用户翻译原文。
- 修复：HTTP/WebSocket 最后发送层必须具备可检查的授权上下文；自动健康检查与手动强制检查均不能绕过。不能只改一个 UI 判断。
- 验收：Consent=Unset/Denied 时，构造窗口、激活窗口、强制探测均 0 个发送；Allowed 才可探测；离线/网络关闭覆盖 Allowed。

### F02 / P0 / A：复制处理破坏技术标识符

- `Services/MarkdownPresenter.cs:36` 的 `ToPlainText` 先用正则去斜体/粗体，后去行内代码反引号，未区分代码与自然语言。
- Release 组件实测：`foo_bar_baz → foobarbaz`；行内代码包裹后也一样；`__init__ → init`。
- 浮窗和查词的复制、朗读、收藏调用该方法。Rust 之前保护正确也不能阻止最后这次破坏。
- 另一个复现：`FormatPangu` 把行内代码中的 `C:\用户data\file.txt` 改成 `C:\用户 data\file.txt`。`AppendFormattedSpans` 先对整行加空格，再识别代码，造成展示失真。
- 修复：原始结果、复制文本、朗读文本、视觉排版分离；解析结构后仅处理自然语言 span；代码文本绝不自动空格或去下划线。不能靠新增几个例外正则掩盖根因。

### F03 / P0 / A+B：Partial 仍可进入历史

- `Services/TranslationModels.cs:102`：`IsSuccess` 包含 Completed **和 Partial**。组件探针确认 `Partial.IsSuccess=True`。
- `TranslationCoordinator.ApplyFinalResponse` 将 warnings 非空结果置为 Partial。
- `WriteHistoryOnce:925` 以 `session.IsSuccess` 判断保存，因此校验警告 Partial 可以写入历史。现有“取消/错误不写历史”测试没有覆盖这个成功传输但完整性不合格的分支。
- 修复：新增明确 `IsCleanCompletion/CanPersistResult` 契约，只有非空、校验通过的 Completed 可自动持久化和执行结果动作。不要仅凭名字含 Success 判断安全。

### F04 / P1 / B：Token 只检查出现过，没有落实“恰好一次”

- `crates/popglot-core/src/streaming.rs` 的 `StreamingTokenRestorer` 用 `matched: bool`；重复命中依旧 true。
- `crates/popglot-domain/src/lib.rs:954` 的非流式恢复用 `replace` 替换全部命中，只收集完全未命中的 token。
- 产品规格承诺每个占位符只出现一次，但重复、未知占位符、不同兼容写法混用的契约不完整。
- 固定 `PG_0000` 命名也需要原文碰撞测试；随机 trailer 不等于 token 本身随机。
- 修复：按原文 occurrence 编号计数、检测重复/缺失/未知、避开原文命名碰撞。必须同时覆盖流式与非流式，并接入 F03 门禁。

### F05 / P1 / B+C：免费引擎没有执行代码保护，外发说明不完整

- Coordinator 免费分支把 `trimmed`/OCR 文本直接传给 `_executor.TranslateFreeAsync`，没有 Rust 保护/恢复链。
- 免费实现有 `translate.googleapis.com` 与 `clients5.google.com` 两个目标；授权常量和主要文档只突出前者。
- 采用含原文 `q` 的 GET URL。代码 token 遮蔽并不等于完整隐私脱敏，更不能把整个待翻译文本称为“已脱敏”。
- 免费 HTTP 路径 `SendAsync` 默认缓冲、随后 `ReadAsStringAsync`，未落实 Rust Provider 同等级的响应字节限制。缓存仅按条数近似控制，并发淘汰不构成严格容量上限。
- 修复：所有在线文本路线共享保真契约、完整列出目的地、对响应/缓存/时限设边界、协议失败可见；不要在免费失败后偷偷换供应商。

### F06 / P1 / B：纯离线、本地、局域网三个概念混为一谈

- Rust `is_local_base_url` 与 C# 本地判定把 10/8、172.16/12、192.168/16 视作本地；Provider 在安全模式允许这些地址。
- UI 同时出现“只使用本机”“切断一切网络请求”等承诺。局域网地址可能在另一台机器，内容已离开设备。
- 截图的 `ImageLeftDevice` 也受同一分类影响，不能只改说明不改分类。
- 修复建议：明确 OnDevice / Loopback / PrivateNetwork / Internet；“仅本机”默认只允许系统本地能力及回环服务。局域网模型另行明确允许，显示其设备外传输性质。T05 定义兼容迁移，不擅自把现有许可扩大。
- 另需测试重定向：Rust Client 构造未见显式 redirect 策略，必须验证初始回环/私网地址重定向公网时的边界；本次未发真实绕过请求，不判定已发生泄漏。

### F07 / P1 / A+B：朗读有真实语言误判和生命周期缺口

- `Services/EdgeTtsService.cs`：`ch is >= 'ä' or 'ö'...` 使用了范围条件；很多大于 ä 的字符都会进入德语分支。
- 组件实测：`bonjour été` 和 `Hello 😀` 均选 `de-DE-KatjaNeural`。
- `TtsService` 只以全局网络许可选择云端语音，没有面向 Microsoft 语音目的地的独立选择；翻译服务许可不应让用户意外把文本交给另一个服务。
- `Stop()` 改 generation/停止播放，却没有取消正在合成的请求；旧请求继续消耗网络，直到完成后被代际检查抛弃。
- WebSocket 接收没有按 `EndOfMessage` 组装消息；对每次 Receive 都重新读二字节头，有碎片音频丢失风险。该特定故障尚需本地碎片消息复现。
- 崩溃清理只匹配 `popglot-tts-*.*`，漏掉 `popglot-edgetts-*.mp3`；音频保存失败也需要清理路径。

### F08 / P1 / A+B：收藏显示成功，实际未保存

- `Services/VocabularyStore.cs:219` 的 `Save` 吞掉全部异常；`ToggleStar` 先更新内存，再无条件返回星标状态。
- 组件实测：将存储路径指向一个目录，`ToggleStar` 返回 true；重新创建仓库后词条数 0。
- 资料库是“用户主动积累的数据”，可靠性应高于临时译文。收藏失败必须可见且可重试，不得假亮星标。
- 同时没有文件读取大小、条目数和单条长度边界；默认按词面去重，不含语言对，可能在多语言收藏时互相取消。

### F09 / P1 / B：崩溃诊断绕过脱敏与容量契约

- `App.xaml.cs:156` 的 `LogCrashToFile` 直接记录 `exception.Message` 和堆栈；托盘也直接回显异常 message。
- 文件按天生成，但没有日志总容量/保留天数限制。全局异常 handler 一律 Handled 还可能掩盖已经损坏的 UI/操作状态。
- 风险：异常若携带请求 URL、路径或用户内容，会落到原本承诺不记录正文的诊断文件。未声称本次日志已发现密钥。
- 修复：结构化错误码、消息脱敏、长度和保留界限；当前操作安全失败；非恢复错误不要无限继续运行。

## 4. UI、视觉与可访问性评审

### 4.1 色彩方向值得保留，但规则执行失效

当前实际深色：Canvas `#101216`、Surface `#181B22`、Accent `#7C89D9`、Primary `#5562B3`；浅色：Canvas `#F6F7F9`、Surface `#FFFFFF`、Accent `#5563B8`、Primary `#5260B5`。这是中性底色配克制蓝紫强调，符合高频阅读工具。建议保留，不要每一轮 AI 随意改成纯黑、荧光紫或大渐变。

但 `docs/DESIGN_SYSTEM.md` 仍记录另一套 `#0A0B0F / #2563EB / #4D9FFF`，还强调没有验收方法的“60-30-10”。设计文档与实际实现并非同一套。需要可生成、可检查的 token 表，设计事实统一来源。

### F10 / P1 / A：实际主按钮没有使用白色文字

- `Themes/Controls.xaml:76` 隐式 TextBlock 样式设置 `TextPrimaryBrush`；PrimaryButton 自身虽设 PrimaryTextBrush，ContentPresenter 内生成的文字仍被该样式覆盖。
- 实测浅色 Button.Foreground=`#FFFFFFFF`，内部 TextBlock.Foreground=`#FF15171C`。
- `#15171C` 在 `#5260B5` 上对比度约 **3.16:1**，用于当前小号按钮文字不足 4.5:1。测试仅验 token 配对，没验最终视觉子树。
- 这是样式继承错误，不是调亮品牌底色就能合理解决。要覆盖字符串按钮内容和显式 TextBlock 内容两类。
- 浅色焦点边框 `#AAB1D9` 对白底约 **2.10:1**；需要检查实际焦点呈现并增加独立强对比 FocusBrush。

阈值依据：普通文字至少 4.5:1，重要识别边界和状态图形按 3:1 检查；装饰分隔线和真正禁用控件不能混同为全部强边框。此处将 WCAG 用作桌面可访问性工程基准，不代表通过几个比值就取得完整合规结论。[W3C 文字对比](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html)、[W3C 非文字对比](https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast.html)。

### F11 / P1 / A+B：DPI 截图生产器本身不正确

- `tests/.../Program.cs:2585` 把逻辑宽度设为 `width / scale`，位图却设为 `width * scale`。
- 本次 200% 主窗口截图内容只占左上部分，右下有大片空白；布局被压缩，语言标签/底部控件发生重叠。
- 这能证明离屏测试生产器错误，不能直接推论真实 Windows 200% 缩放必然如此。要先修测试，再判断真实布局。
- 当前通过条件主要为 File.Exists；不能检出遮挡、黑字按钮、空白填充或私人配置污染。其价值是“产物生成”，不是“自动视觉回归通过”。

### 4.2 页面与交互意见（D，目标设计见规则）

| 页面 | 应保留 | 具体改进 |
|---|---|---|
| 主工作台 | 左右对照、同一阅读面、底部状态 | 空状态给一条虚构示例；未授权提供就地入口；窄内容区转上下对照；隐藏重复的“自动识别源语言”说明；长引擎名不挤占状态 |
| 翻译浮窗 | 随手调用、固定、展开、流式增量 | 420 DIP 宽下语言名已有截断；把低频设置/合并/清空收进更多菜单；正文与控件分层；只在内容需要时扩高 |
| 查词窗口 | 输入优先、Enter 提交、轻量 | 当前空状态占很大空间；空/短词收紧高度，长文再展开；它与翻译浮窗共享动作，不再维护第三套业务规则 |
| 设置列表 | 配置与工作台分离，服务列表可扫描 | 名称统一用“翻译引擎”；清楚区分保存与默认切换；有脏草稿离开给内联保存/放弃/继续编辑 |
| 服务编辑 | 标准表单、模型目录辅助、自定义路径 | 首次配置只展示供应商/地址、Key、文字模型；高级项折叠；测试连接不是保存前置；标题位置不要同时争抢添加、设默认、保存等主操作 |
| 隐私页 | 将授权与数据行为明确展示 | 免费首次授权藏在“高级设置”不合理；云端语音没有独立目的地；本机/局域网分类必须诚实 |
| 资料库 | 历史与收藏合并入口 | 区分自动记录与主动收藏；保存失败状态、语言对去重、清空/导出真实格式与恢复能力优先于再加标签系统 |

正文是产品主角。页标题建议 18 DIP、设置标签 13 DIP、正文 15 DIP、说明 12 DIP、极次要元数据 11 DIP；不要用 10.5 DIP 作为主要操作文案。尺寸应写 DIP，不把 WPF 值称为固定物理像素。

截图里的颜色和层级已有基础，主要视觉缺点是小字密度、过多浮窗图标、空态没有引导、窄空间没有优先级，以及真实样式和声明不一致。先修这些，比全盘重新画界面更有价值。

## 5. 算法、模型质量与性能

### F12 / P1 / B：长输入与固定输出上限不匹配

Rust 文本输入允许 64 KiB；`provider.rs:264` 的 `output_token_limit` 最终 clamp 到 1200 tokens，视觉同样封顶 1200。长技术段落、中文转英文、转录加翻译再加解释很容易超出可用输出预算。并非所有输入一定截断，但能力范围和预算明显不匹配。

目标：短文本保持低延迟；长文本用有界分段/预算或在发出请求前诚实限制；所有协议长度停止都进入不完整终态，禁止假成功。不能简单把全局上限改成很大的常量。

### F13 / P1 / B：自动路由存在两套不同真相

Rust domain 有 OCR 置信度/布局复杂度分支，但 `RoutingContext::from_settings` 默认值是 false/false/1.0/1.0；Windows OCR 返回纯文本，未给该链路提供实际置信度。Windows 的 `ProfileManager.ResolveRoute` 有独立规则：本地 OCR 可用时 Auto 优先本地；显式 VisionDirect 不可用时阻断；Rust 则可回退。

这不是说 Auto 完全不能工作，而是不能把它描述成已经理解截图画质和复杂布局的智能分类器；README 的“Rust 统一裁决”和强制视觉失败必回退也与当前路径不一致。

建议先以当前 Windows 用户行为为基础制定一份纯路由契约与共享 JSON 用例，所有预览/执行/FFI 使用一致判定。保留 Auto 的“本地优先”名称；真正加入置信度必须先有可测信号和质量评估。

### F14 / P2 / B：模型推荐结构比证据链走得更远

- 能力 Unknown、目录事实、命名启发式分级是好设计；未知模型可保留并由用户覆盖也合理。
- 推荐函数可接受本机 benchmark，但搜索 Windows 调用点未发现实测数据仓库/上下文注入，不能把这项可选接口宣传成已闭环的用户能力。
- `MatchesContext` 把整个 endpoint 路径小写、忽略 query；路径可能区分大小写。时间戳存在却未用于过期判断，首个匹配样本并非可靠统计。
- 当前无文本推荐候选合成时可能给 VisionInput=Supported，违背未知能力不虚构原则；应针对当前文字模型但无目录记录的场景补断言。
- 优化次序：先纠正证据与文案，再做用户主动运行的合成数据实测；不要硬编码当下所谓最佳模型和价格。

### F15 / P1 / B：性能仪表不测量所声称的对象

- `RenderScreenshotsAndMeasureBaseline:2481` 的 TrayAvailable 计时器 Start 后立即 Stop，再强制至少 1ms，没有托盘初始化动作。
- “Cold Startup” 只测 CoreBridge 初始化，不含进程启动、资源加载、托盘；“Hotkey to first frame”只构造和 Arrange，没有实际热键/合成器首帧；取消在无活动请求时测一次。
- 本次测试宿主 Working Set 为 117.0 MB。该数字不能证明应用空闲达标/超标，不是独立应用的测量。
- 修复：先更正指标命名；独立 Release 进程记录真实时间边界、样本数、P50/P95、内存与 CPU；不得用空操作结果填性能表。

### F16 / P2 / B+C：流式算法的复杂度与注释不一致

- `TranslationStreamBuffer` 在锁内追加两份 StringBuilder，复制随块长增长；O(1)、non-blocking、zero-allocation 注释不成立。
- Coordinator 每个 40ms 泵会取累计全文；三个 UI 层用 TextBox.Text 重设全文。长输出可能出现重复分配、重新布局。
- Rust token restorer 每次寻找 token 变体，代价随 token 数增长。无需先上复杂自动机，但应对 1/100/1000 token、1字/小块/大块 chunk 做规模测试。
- 40ms UI 泵与“客户端首字额外开销≤20ms”需要分开定义：核心处理和首帧上屏不能混为一个 TTFT。
- 先 Profile 再优化，不应只因见到锁就换无锁结构、只因文件长就改语言。

### 翻译质量评测建议（D）

建立 60 条固定合成样本，按散文、报错栈、命令/路径、代码注释、中英混排、Markdown、短词歧义和 OCR 易混字覆盖。Token 完整率是硬门槛；翻译含义/术语/遗漏/无依据建议分别评分。流式 benchmark 只能评传输，不能证明翻译准确率。

真实模型评测需负责人明确预算、供应商和授权。没有这一步时，可以完成离线协议和质量评估框架，但必须写“真实质量未评测”。

## 6. 架构、数据、测试与发布

### 架构意见

保留三层 Rust 分工及 C ABI；Windows 平台能力留在 Shell。已有 request_id / epoch / cancellation、FFI 字符串释放、快照执行值得保留。

真正的问题是业务策略散落：路由在两种语言、权限在多个服务、完成定义在会话和各窗口、复制策略在不同入口、全局静态依赖让测试读真实状态。先抽出明确能力边界，而非先建抽象框架。

`ServicesSection.xaml.cs` 2136 行，测试 Program.cs 5002 行，Controls.xaml 约 114KB。行数仅表示审查成本，不自动构成缺陷。应按职责拆出草稿校验、模型目录、保存协调、状态 reducer 和按领域测试；禁止只切成 partial 文件、增加同义转发层来伪装解耦。

### F17 / P1 / B：测试既有实质覆盖，也有容易误导的通过项

优点：Rust mock HTTP、SSE 切块、剪贴板新值优先、取消隔离、凭据草稿无保存、Windows state reducer 已有实质用例。

问题：不少测试用源文件 Contains/正则断言实现片段，有些只验证辅助模型；真实按钮树、健康请求入口、带 warnings 的 final、错误磁盘路径遗漏。测试命名“headless”不代表不读取机器状态。优先增加缺陷反例，然后按领域拆分，不以测试数量作为质量目标。

### 数据与导出

历史有限额是优点；但损坏历史读取返回空，后续写入可能覆盖原文件，需保留可恢复备份并给可见状态。历史过期筛选不等于物理删除；备份和生词语音文件也要纳入清理说明。

CSV 双引号转义已做，但公式前缀、Anki HTML 及换行/Tab 应有明确安全导出策略和兼容模式。不要以“文本格式导出”隐含保证任何目标应用里都不会执行公式。

### 发布意见

CI 已包含三平台 Rust、Windows Release 逻辑测试；release 有版本匹配、便携包、SHA256，值得保留。还需全新隔离 Windows 账户的解压运行/升级回归、配置迁移失败恢复、native DLL 缺失可见错误、OCR 未安装指引。

不应在当前评审直接改变发布渠道或自动签名/发布；签名证书、更新分发属于后续明确配置任务。无需为了本轮引入遥测、账户、云同步或自动更新服务。

### F18 / P2 / B：文档持续漂移

README 仍称当前 0.1.2、113 项测试，代码已 0.1.3，本次 121 项；旧看板同文既写全部完成又保留同名未完成项；旧设计与真实配色不同；当前已有 VisionOcr，规格仍侧重三模式。

建立“当前契约”和“历史归档”分界。版本由版本文件/构建生成，测试数附日期和命令，性能声明附硬件/样本/提交；不再维护多个相互矛盾的“最终版总纲”。

## 7. 整改顺序与产品决策

| 阶段 | 任务 | 出关标准 |
|---|---|---|
| A：证据与信任 | T00—T05 | 测试隔离；授权不可旁路；代码保真；不完整结果不持久化；网络边界明确 |
| B：可靠功能 | T06—T08 | 语音取消/分片正确；收藏与历史失败可见；日志有界脱敏 |
| C：界面可用 | T09—T11 | 实际按钮及焦点可见；DPI 测试正确；首次使用可理解；主要页面适应窄窗口 |
| D：质量与效率 | T12—T17 | 输入/输出预算匹配；路由一致；推荐证据诚实；真实性能；有依据的优化；职责分离 |
| E：发布验收 | T18—T19 | 最新文档与代码一致；整体验收无隐私/保真/数据丢失阻断项 |

关键取舍：默认本地朗读；免费联网显式同意；Auto 诚实本地优先；已有 Delta 不透明重试；不完整结果保留可见但不得自动流转；主题保留现有方向；先可验证闭环，后新功能。

这份评审不是穷尽全部潜在漏洞的证明。上述明确缺陷足以指导下一轮高价值整改，未覆盖的真机行为必须保留“待验证”，不能被漂亮的完成报告替代。

## 8. 技术核对来源

- WPF 属性来源存在优先级差异，不能只看 Button.Foreground 推断内部文字有效值：[Microsoft 依赖属性优先级](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/properties/dependency-property-value-precedence)。
- WebSocket 接收是否完成一个消息需检查 EndOfMessage：[Microsoft EndOfMessage](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.websocketreceiveresult.endofmessage?view=net-10.0)。
- 对比度阈值见第 4 节 W3C 原始资料；其他项目事实来自本次本地代码与组件探针，不来自网络产品宣传。

## 9. 本次组件探针原始摘要

探针在临时 .NET 10 Windows/WPF 控制台项目中加载本次 Release 的 PopGlot.dll，通过反射调用生产方法；未启动 App.OnStartup，未调用 CoreBridge.Initialize。按钮实验只加载 Controls.xaml 并应用 Light 主题，再 Measure/Arrange 后遍历视觉子树。收藏失败使用临时目录本身作为“文件路径”，不操作用户词库。

```text
MarkdownPresenter.ToPlainText("foo_bar_baz") => foobarbaz
MarkdownPresenter.ToPlainText("`foo_bar_baz`") => foobarbaz
MarkdownPresenter.ToPlainText("__init__") => init
MarkdownPresenter.FormatPangu("`C:\用户data\file.txt`") => `C:\用户 data\file.txt`
EdgeTtsService.ResolveDefaultVoice("bonjour été") => de-DE-KatjaNeural
EdgeTtsService.ResolveDefaultVoice("Hello 😀") => de-DE-KatjaNeural
EdgeTtsService.ResolveDefaultVoice("こんにちは") => ja-JP-NanamiNeural
TranslationSession(Stage=Partial).IsSuccess => True
VocabularyStore(路径为目录).ToggleStar(...) => True
重新创建同一路径的VocabularyStore.GetAll().Count => 0
ThemeContrast.Ratio("#15171C", "#5260B5") => 3.1583982202165415
ThemeContrast.Ratio("#AAB1D9", "#FFFFFF") => 2.0999965228735045
Light PrimaryButton.Foreground => #FFFFFFFF
Light PrimaryButton内部TextBlock.Foreground => #FF15171C
```

这些结果只证明对应方法和条件，不能替代整个应用旅程。后续AI应将反例加入正式隔离测试；不要把临时探针截图或本机路径当成发布产物。
