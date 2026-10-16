# PopGlot 第一轮代码审查

## 修复进展（当前工作区）

下文保留初次审查的发现和当时的验证边界。随后已修复：

- 免费 HTTP 使用 ResponseHeadersRead，4 MiB 上限在读取过程中生效；补无长度和虚假长度响应测试。
- OMP 官方 Bun 包入口解析、默认 Bun/BUN_INSTALL 搜索和相对 PATH 排除；不执行 cmd/ps1 shim。
- 当前 OMP 按 id 等待 prompt_result 并检查 completed/aborted/error；对源码已核对的 v18.2.11 保留 terminal agent_end 兼容（该旧版没有成功 prompt_result/status 契约）。未知版本不冒认成功。
- 工作台/浮窗同步保存后的全局语言；请求中不改标签，下次请求读取最新语言。字段级队列更新避免语言、策略表单保存/回滚互相覆盖；语言保存异常会提示。
- 切语言同时清掉隐藏的旧译文、说明和要点显示，防止切换阅读模式后旧内容复现。
- 中文/英语目标的保守质量诊断：明显错误语言保留为 Partial 并显示原因，阻止干净终态副作用；没有自动收费重试。
- OCR 等比限制单边尺寸、WinRT 取消传递、显式缺语言包时停止错误语言回退、中文变体匹配。
- MyMemory 对假名/韩文作有限识别，对无法判断的混合/其他脚本要求显式源语言；帮助说明真实能力与免费线路长度预算。

验证结果：Rust workspace 238 项、PureTests 52 项、LogicTests 318 项、CliTests（含 `--ui`）25 项全部通过，共 633 项；`git diff --check` 通过。WPF 回归覆盖既有窗口双向语言同步和隐藏旧读物清理，CLI 回归覆盖旧版/新版 OMP 终态、错误语言不入历史、策略保存/回滚保留语言设置。没有调用真实模型；全缩放/全屏幕人工 UI 验收及真实 OCR 准确率评测仍待进行。未加入第三方 OCR 引擎，也未擅自把默认目标语言改为自动双向。

## 范围与证据边界（初次审查）

检查当前源码、翻译语言链路、Windows OCR、OMP 发现/RPC、设置同步和 HTTP 资源边界。没有修改业务代码，没有调用真实模型，没有读取用户凭据，没有开展实际 UI 截图验收。

执行结果：

- `cargo test --workspace --locked`：238 项测试通过。
- `dotnet run --project tests/PopGlot.Windows.PureTests`：47 项通过。
- `dotnet run --project tests/PopGlot.Windows.CliTests`：21 项通过（默认隔离模拟模式，不是实机 OMP 验收）。

测试通过证明现有测试中的契约成立，不代表实际语言质量、各种 OMP 安装布局或 UI 在所有缩放比例下正确。以下区分“代码确认”和“待运行复现”。

## 1. P1：免费翻译响应上限在默认 HTTP 缓冲之后才生效

**代码确认。** `apps/PopGlot.Windows/FreeTranslateService.cs:290` 使用默认 `HttpClient.SendAsync(request, cancellationToken)`，即 ResponseContentRead。HttpClient 会先缓冲完整响应，才返回给后面的 Content-Length 检查和 `ReadCappedAsync`。因此后面的 4 MiB 检查不能限制发送阶段已经发生的响应缓冲；解压后的异常大响应同样有资源消耗风险。

修复：改为 `HttpCompletionOption.ResponseHeadersRead`，再以有界流读取；加入无 Content-Length、错误 Content-Length、压缩大响应的测试。属于资源耗尽风险，当前没有实际攻击或利用证据。

## 2. P1：OMP“扫描到入口”和“入口可解析”相互矛盾

**代码确认。**

- `Services/AiConnections/CliDiscovery.cs:22` 声称扫描 `omp.exe / omp.cmd / omp.ps1`。
- 同文件的 `Discover` 对每个候选调用 `CliLauncher.Resolve`，失败就跳过。
- `Services/AiConnections/CliLauncher.cs:12` 的 Resolve 只直接接受 `.exe`；只有 Codex 有 npm 包布局解析，OMP 没有对应分支。

结果：只有 cmd/ps1 启动器的 OMP 安装被静默过滤，用户看到的是“没检测到”，而不是“检测到了，但安装方式暂不支持”。搜索目录也不覆盖 Bun 默认全局入口目录（除非已在当前进程 PATH 中）。

开源对照：`can1357/oh-my-pi` README 同时提供 Bun 全局安装和原生二进制安装，并非所有用户都有同一种入口。本机确实存在 `%LOCALAPPDATA%/omp/omp.exe`，所以**不能断言这台机器上的扫描必然失败**。

修复：建立明确的安装布局解析，受控解析 Bun/包入口，不通过 shell 执行任意 shim；扫描结果应保留 UnsupportedEntry 原因和手动选择入口操作。补齐原生、Bun、路径含空格、旧 PATH、重复入口测试。

## 3. P1：OMP 终态契约落后于上游当前协议

**代码差异确认，具体版本影响待实机验收。**

`Services/AiConnections/OmpRpcBackend.cs` 在 `agent_end` 时直接返回成功；对 `prompt_result` 只检查 `agentInvoked == false`，没有以请求 id 关联并检查其 `status: completed / aborted / error`。

对照上游 commit `714a63689bb4551e59dd27efba2e894d31aec86e`：

- `packages/coding-agent/src/modes/rpc/rpc-types.ts` 的 RpcPromptResultFrame 明确为每个已接收 prompt 的终态，携带 id、status、error、sessionSettled。
- 同文件说明 terminal agent_end 仅表示一个 run 已 yield，与整个 prompt 工作结算不同。

现有假服务测试偏重 `agent_end` 和迟到的 response error，没有覆盖该完整终态契约。当前不能据此声称所有已安装版本都出错。

修复：按验证过的版本/能力协商终态；新协议等待匹配 id 的 prompt_result，处理 aborted/error，再决定完成；旧协议明确记录兼容策略。测试 agent_end 后 prompt_result:error、多轮、空结果及 EOF。

上游链接：
- https://github.com/can1357/oh-my-pi
- https://github.com/can1357/oh-my-pi/blob/714a63689bb4551e59dd27efba2e894d31aec86e/packages/coding-agent/src/modes/rpc/rpc-types.ts

## 4. P1：目标语言只有提示词约束，没有结果质量门禁

**代码确认；是否解释用户某次异常需要失败样例。**

`crates/popglot-domain/src/language.rs` 正确生成目标语言指令；`crates/popglot-core/src/provider.rs` 把指令放进提示词。但文本任务的完成流程主要验证执行终态、正文和占位符，没有判断译文是否仍是原文、是否明显用了错误语言。`text_job.rs` 的完成结果也没有保存语言对以供这类检查。

因此模型返回流畅英文，即便目标是中文，也可能通过格式校验。流式正常结束不等于翻译正确。

还应区分另一种情况：默认是 `auto → zh-CN`。输入本来就是中文时保持中文并不是执行失败；项目没有“遇到中文自动转英文”的自动双向规则。若需要双向翻译，应作为独立、可见的设置，不能擅自改变显式目标语言。

修复：加入保守的质量诊断（原文回显、明显语言不匹配），排除代码、URL、纯数字、专名、极短文本及合法同语言输入；提供明确提示/用户重试，不把有歧义的启发式当成绝对拒绝，也不对已展示的流自动重复收费。覆盖纯英文到中文、中文到英文、混合代码、日文汉字、简繁转换。

## 5. P1：全局语言设置与各窗口控件会分叉

**静态状态链路确认；建议补跨窗口回归。**

- 工作台 `Sections/TranslateSection.xaml.cs:339` 初始化时载入语言；实际翻译使用控件选项。
- 同文件 `:1251` 的 RefreshAfterSettingsChanged 只更新风格和服务能力，不重新同步语言控件。
- 浮窗构造函数读取保存语言，自己的选择变化又写回全局设置。
- `QuickSearchWindow.xaml.cs:583` 翻译时直接读取全局语言设置。

由此，工作台保持旧选项时，浮窗可以把全局目标语言改成另一种，查词再使用新值。同一应用不同入口的目标语言不一致，容易表现成“有时不翻中文”。

另外 `PersistLanguagePair` 使用 `_ = CoreBridge.SaveSettingsAsync(settings with {...})`：它保存的是排队前读取的完整快照，串行写入不等于字段级合并；并发更新其他设置时存在旧快照覆盖风险。未等待的 Task 异常也不受调用点同步 try/catch 完整保护。

修复：先定义语言是全局还是会话级；全局策略需要版本化设置通知及窗口同步，会话策略需要清晰显示当前语言。持久化改为队列内读取最新设置的字段更新，并观察失败。

## 6. P2：OCR 自动识别并非自动选择文字语言

**代码确认。** `WindowsOcrService.ResolveEngine` 对 auto 使用用户配置语言或第一个已安装语言，并不检测图片的文字脚本。显式语言缺包时也会退到其他语言，用户未收到明确警告。

`TryCreateForTag` 的最后回退只按 primary subtag 匹配，zh-CN/zh-TW 可能落到不合适的中文变体。`RecognizeTextAsync` 未检查 `OcrEngine.MaxImageDimension`；截图允许的总像素数与系统 OCR 的单边限制不是一回事，宽长图仍可能失败。

`TranslationCoordinator.cs:132` 接口收到了 CancellationToken，却没有传给实际 OCR 操作，取消不能终止正在执行的识别阶段。

修复：显式语言缺包时提示；建立脚本优先和混合文本策略；检查/缩放单边尺寸，保留布局信息；接入可取消 WinRT 操作。若增加 PaddleOCR/RapidOCR 等开源后端，应先评估包体、启动时间、模型许可证和离线分发，不建议在没有样本评测时直接换引擎。

## 7. P2：MyMemory 自动源语言实际上只在中英文之间猜

**代码确认。** `FreeTranslateService.cs:518` 的 ResolveMyMemorySource 仅比较基本汉字和 ASCII 字母计数，最终只返回 zh-CN 或 en。UI 却提供日、韩、法、德等语言。

结果：自动模式下日文汉字可能误判中文，韩文/西里尔字母等通常判为英文。属于能力边界未在交互中表达，不应称为真正多语言自动检测。

修复：MyMemory 在不可靠自动识别时要求用户选源语言，或引入经过评测的离线检测；至少明确提示当前推断并允许修正。

## 8. P2：免费线路实际长度预算与“5000 字符”提示不一致

**代码确认。** 前置字符上限为 5000，但 Google 两条 GET 线路还受到 2000 字符编码 URL 的本地预算限制。中文 UTF-8 经百分号编码后膨胀明显，实际允许的原文可能远短于 5000 字符。MyMemory 则是 500 字节。

当前已会报长内容错误，不是静默发送；不足在于用户对容量的预期不稳定。建议输入前按当前引擎真实字节/URL预算提示剩余额度；如要分块，需保留段落、代码块和统一终态，不能简单按字符截断。

## UI 审查结论与待验收项

已确认的交互问题是第 5 项的跨窗口语言状态分叉。没有实际运行截图，本轮不把配色、遮挡或 DPI 布局问题冒充已复现缺陷。

浮窗 XAML 存在 460×380 最小尺寸、原文最多 86 DIP、说明/警告最多 80 DIP 等硬约束，应该用真实窗口验收而不是仅审查常量。建议矩阵：

- 100% / 150% / 200% 缩放，以及混合 DPI 双屏、负坐标副屏。
- 最小窗口、长引擎名、长错误、长警告、多段正文与大代码块。
- 中文 IME 的 Enter/Esc、流式取消、连续划词、切引擎、窗口关闭后迟到回调。
- 工作台、浮窗、查词之间的语言同步与历史重开。
- 受限工作区下底部按钮/状态栏是否仍可见，键盘焦点和屏幕阅读器可达性。

## 建议整改顺序

1. 修复 HTTP 真正的有界读取；统一跨窗口语言状态和字段级设置更新。
2. 完善 OMP 安装发现与最新终态协议的兼容测试。
3. 增加目标语言/回显诊断和用户可控重试。
4. 修复 OCR 尺寸、语言回退、取消及 MyMemory 能力提示。
5. 跑真实 UI 矩阵与经授权的真实引擎质量样例，再决定外观重构和 OCR 后端替换。

若要定位用户反馈的“有时不翻中文”，最有价值的补充是：原文、期望译文语言、实际结果、使用入口及引擎名称（不要提供密钥）。
