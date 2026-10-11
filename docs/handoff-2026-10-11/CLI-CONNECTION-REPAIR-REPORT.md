# 本机 AI 工具连接修复验收记录

日期：2026-10-11。修复版：PopGlot 0.1.16。工作区基线：main / 96a7fd6e5ee19a57704f9120cfd3f69879259374，包含原有未提交变更；本次没有丢弃或提交这些变更。

## 修复结果

截图里的页面原来只做入口检测，两个 Codex npm 启动脚本重复显示，适配器仍全部关闭，无法创建可执行连接。此次实现并验收了真实连接链路：检测 → 连接/刷新账号 → 模型选择 → 测试翻译 → 保存/保存并使用 → 主翻译/要点整理/OCR 识别后文字翻译 → 断开。

支持本机验证过的 **Codex CLI 0.150.1** 和 **oh-my-pi 18.2.11**。版本不匹配明确拒绝，不绕过检查。Claude Code 订阅登录没有在此版本接入；OMP 的 anthropic/claude-code 路由不出现在可选列表中。既有 API 引擎继续走原调用链。

本次证据优先于旧 IMPLEMENTATION-REPORT.md 和 INDEPENDENT-REVIEW.md 中“CLI 未放行/当前版本不执行”的历史描述。旧文档保留用于追踪，不能作为本次实现状态。

## 具体代码与执行顺序

| 顺序 | 文件（相对仓库根目录） | 已执行内容 |
| --- | --- | --- |
| 1 | apps/PopGlot.Windows/Services/AiConnections/CliDiscovery.cs、CliLauncher.cs | 将已知 npm shim 解析到原生 codex.exe；同一原生入口去重；确认精确版本；不使用 shell 拼接命令。 |
| 2 | Services/AiConnections/JsonLineTransport.cs、CliRuntimeHost.cs | 异步串行 stdin、并行 stderr 排空；逐帧 UTF-8 验证、输出/队列上限；请求响应匹配与事件订阅并存；进程退出使等待结束；只回收自己启动的进程树及工作目录。 |
| 3 | Services/AiConnections/CodexAppServerBackend.cs | 实际 initialize/account/read/model/list/thread/start/turn/start；每次使用新的临时线程，空 environments、空动态工具、关闭 shell/MCP/扩展等能力；消费对应 thread/turn 的文本增量和终态。 |
| 4 | Services/AiConnections/OmpRpcBackend.cs | 实际 get_state/get_available_models/set_model/prompt；固定无工具/无扩展参数及配置覆盖；模型切换前后确认 dumpTools 为空；等待最终 agent_end，不把 prompt 接收确认误当成功。 |
| 5 | Services/AiConnections/CliTranslationExecutor.cs、CoreBridge.cs | 复用 Rust Prepare/Push/Finish/Free 文本作业和生产提示词；保护代码 token、解析元数据；取消/失败先终止进程并等待回调退出，再释放 FFI 作业；记录本机准备与首文字时间。 |
| 6 | Services/ProfileManager.cs、ProfileVerification.cs、RouteCapabilityService.cs | 保存/切换 CLI 配置不要求 URL/API Key，不从 Windows 凭据库取 API Key；CLI 无视觉能力；禁用连接并取消运行中及已排队请求。 |
| 7 | Services/TranslationCoordinator.cs、TranslationModels.cs、SummaryLifecycleCoordinator.cs、MainWindow.xaml.cs | 每次请求捕获配置快照；主文字翻译、要点整理、OCR 后文字和风格能力查询消费相同 CLI 路由；CLI 不伪造 HTTP 200；完成后历史只提交一次。 |
| 8 | Sections/ServicesSection.xaml、ServicesSection.CliConnections.cs、ServicesSection.xaml.cs | 实际异步连接、测试、保存及断开按钮；模型正常显示名称；选择项使用柔和背景，移除突兀的蓝色框；保留键盘焦点提示；导航/取消后晚到结果不覆盖新状态。 |
| 9 | PopGlot.Windows.csproj、tests/PopGlot.Windows.CliTests | 版本升至 0.1.16；独立隔离测试宿主，覆盖协议、FFI、主路由、真实工具和实际 WPF 操作。 |
| 10 | artifacts/cli-repair/win-x64 | 重新构建最终 Release 包，执行真实托盘/热键启动 smoke 和主窗口首开检查。 |

表内 Services/Sections 路径均位于 apps/PopGlot.Windows/ 下。

## 通过、拒绝和未验证

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| 最终 CLI 回归 + 两个真实提供方 + WPF 页面 | **通过：14 项，0 失败** | artifacts/cli-repair/cli-final-tests.log |
| Release 应用及 Release 原生核心的协议/路由/WPF 回归 | **通过：12 项，0 失败** | artifacts/cli-repair/cli-release-tests.log；测试宿主按构建配置复制相应原生 DLL。 |
| 真实 Codex 测试句 | **通过**，返回“你好，世界” | Codex 0.150.1；3 个模型；总计 4767ms；请求准备 272ms；提交至首文字 3995ms。 |
| 真实 OMP 测试句 | **通过**，返回“你好，世界” | OMP 18.2.11；47 个可选模型；glm-4.7 / zhipu-coding-plan；总计 4398ms；请求准备 557ms；提交至首文字 3203ms。 |
| 协议子进程和旧配置回归 | **通过：8 项，0 失败** | artifacts/cli-repair/logic-tests.log。最后一项是“不支持版本的 CLI 截图路由拒绝”，不代表所有 CLI 截图文字翻译被禁用。 |
| 纯逻辑回归 | **通过：47 项，0 失败** | artifacts/cli-repair/pure-tests.log |
| Rust 文本作业与连接池 | **通过：14 项，0 失败** | artifacts/cli-repair/rust-text-job-tests.log |
| WPF 布局/按钮 | **通过**，800×600 下无重叠或按钮缺失；模型名称正常 | artifacts/cli-repair/connection-panel.png；截图为实际 WPF 控件、隔离模拟工具，真实工具调用另列。 |
| 最终 Release 发布 | **通过** | artifacts/cli-repair/publish.log；命令退出码 0。 |
| 最终包托盘/热键启动 | **通过**，629ms，热键注册 true，failure=null | artifacts/cli-repair/startup-smoke.json；0.1.16.0。此时间从 OnStartup 内计时，不是完整冷启动时间。 |
| 最终包主窗口首开 | **通过**，构造 243ms，Show 至交互就绪 159ms | artifacts/cli-repair/first-open.json；仅一个样本，OS 文件缓存可能已热。 |
| 隐藏 1s 的热唤醒 | **通过**，两组各 10 次，0 失败 | artifacts/cli-repair/wake-trim.json、wake-no-trim.json。 |
| 离线/安全模式/版本不符 | **按设计拒绝**，不能提交模型请求 | 协议测试；离线检查确认 0 次进程启动和 0 次 prompt。 |
| OMP 工具请求、延迟错误、子进程崩溃 | **按设计失败**，不冒充完成 | CLI/子进程回归。 |
| 取消、已保存连接断开、排队请求断开 | **通过** | CLI 回归，进程及临时工作目录回收。 |
| 旧机器、长时间休眠、全部 DPI/高对比度、完整截图选区/OCR 识别/剪贴板动作 | **未验证** | 本次 OCR 覆盖的是已识别文字后的真实翻译路由，不是整条取图和识别流程。 |
| 签名安装包/正式发布兼容性 | **未验证** | 此交付为本机已启动验证的 Windows x64 Release 文件夹。 |

## 性能判断

同机隐藏 1s 的短样本：工作集压缩开启时交互唤醒 P50/P95 = 10.07/11.74ms，隐藏后平均驻留 17.55MiB；关闭时 P50/P95 = 8.42/31.97ms，平均驻留 146.95MiB。它不能证明旧机器上的结论，当前默认保持原有压缩策略，没有以增加约八倍驻留内存换取未证明的收益。

真实模型测试中，本机版本/协议初始化/请求准备约 0.27–0.56s；提交到首段文字约 3.2–4.0s。当前机器上，多秒等待主要出现在模型请求阶段，不能归因于蓝框或主窗口唤醒。本次新增 RequestPreparationMs / FirstProtocolDeltaMs，可继续按同一分段复测；远端网络、服务排队和模型计算的占比仍不能从客户端计时分离。没有同机同模型原 API 路由对照，因此不声称账号复用一定更快。

CLI 每个翻译作业使用自己拥有的进程/会话；OMP 固定版本没有已验证的系统提示词更新接口，保持每作业启动以防跨请求上下文污染。没有为缩短测量数字而复用未知会话。

## 账号和权限边界

检测只找程序入口；连接使用工具的正式协议读取状态和模型。没有扫描、复制、迁移登录 token 或读取登录凭据文件。测试句和翻译内容会交给所选工具配置的提供方，并消耗其额度；本机 CLI 不代表离线模型。提供方自己的网络、代理和登录设置由工具管理。

固定启动参数关闭工具、扩展、MCP、文件/执行能力，并在协议出现工具请求时停止连接；OMP 在提交前确认工具列表为空。网络总开关和安全模式在启动/提交前以及请求期间检查。这不是整个子进程网络的操作系统防火墙证明，也不是通用沙箱审计；不宣称复用账号天然更安全。

错误输出排空后丢弃，协议错误不把原始工具正文、账号信息或配置对象放进 UI/日志。计时只含阶段、毫秒、工具版本和所选模型，不含用户原文。

## 复测命令

在仓库根目录 PowerShell 执行：

```powershell
dotnet run --project tests/PopGlot.Windows.CliTests/PopGlot.Windows.CliTests.csproj -- --ui
# 以下两次真实 Hello world 请求使用已登录工具的额度：
dotnet run --project tests/PopGlot.Windows.CliTests/PopGlot.Windows.CliTests.csproj -- --live
dotnet run --project tests/PopGlot.Windows.PureTests/PopGlot.Windows.PureTests.csproj
& "$env:USERPROFILE\.cargo\bin\cargo.exe" test --locked -p popglot-core --test text_job --test transport_pool
dotnet publish apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -r win-x64 --self-contained false -o artifacts/cli-repair/win-x64
```

测试使用临时数据目录，不覆盖个人引擎或历史；WPF 测试加载资源和控件，不执行生产 App 启动/托盘/全局热键。产品启动/唤醒测量必须确认没有用户 PopGlot 进程后运行，不能为清场杀用户进程。

## 使用、失败处理和回退

1. 从托盘退出仍在运行的旧版本，启动 **artifacts/cli-repair/win-x64/PopGlot.exe**。确认版本 0.1.16；旧 artifacts/release/win-x64 文件夹未被替换，旧快捷方式可能仍指向旧版。
2. 设置 → 翻译引擎 → 连接本机工具 → 选择 Codex CLI 或 oh-my-pi → 连接/刷新账号。
3. 选择模型 → 测试翻译 → 保存并使用。主翻译、要点整理和 OCR 后文字使用该连接。
4. 未登录：在该工具本身登录后重新连接；模型不可用/额度耗尽：换可用模型或恢复已有 API 引擎；版本不符：更新适配器/工具，不能手工伪造 ExpectedVersion。
5. 断开只禁用 PopGlot 连接并取消它自己的请求，不登出工具账号。需要回退时在引擎列表切换已有 API 引擎；勿为回退删除账号、历史或整个配置目录。

发布包依赖 .NET 10 Windows Desktop Runtime；当前机器已具备并通过启动验证。迁移到未安装该运行时的机器时应安装运行时或生成自包含包。

最终 PopGlot.dll SHA256：D8F4DE0D8E8BFB69DFCAF517FE76D3FA9FC16FF171B8642D14669A654E4E3B44。
