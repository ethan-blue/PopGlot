# 测试资源依赖分类（A11 交付）

> 2026-09-12。本表把现有与计划的测试按实际资源依赖分入四档，是"哪条验证可以在什么环境跑"的唯一索引。
> 规则：pure 宿主（tests/PopGlot.Windows.PureTests）永不触碰 WPF Application、原生 Core、凭据库、注册表、剪贴板、热键或真实网络；
> 任何需要其中之一的测试必须挂在对应宿主，不得为迁就 pure 宿主删减断言。

| 档位 | 宿主 | 允许的资源 | 典型测试组 |
|---|---|---|---|
| pure | PureTests exe（无实例守卫，可与运行中 PopGlot 共存） | 临时目录、内存凭据桩、拒绝一切发送的 guard、seam 注入 | OutboundPolicy 令牌/策略、MarkdownPresenter.ToPlainText、DiagnosticsLog 结构与轮转、VocabularyStore 全行为、StartupRegistration 状态机（seam）、ShellSettings 序列化、FreeTranslateService URL/解析/缓存（假 sender）、TranslationPanelStreamGate/TranslationStreamBuffer/QuickSearchState 纯状态机 |
| isolated-WPF | LogicTests exe（完整守卫：无真实实例、隔离存储、内存凭据、守护 sender） | 单个 STA Application/Dispatcher、离屏渲染、WPF 控件树 | RenderToFlowDocument 视觉、面板/查词窗口生命周期、设置窗口草稿机、主题/字幕按钮、RunStaBatch 全组 |
| native-loopback | LogicTests exe + CoreBridge.Initialize(隔离配置目录) + popglot_ffi.dll | 原生 Rust core（隔离数据目录）、回环 mock HTTP | CoreBridge 路由决策表、endpoint 分类跨语言一致性、ModelCatalogService、ProfileManager、Provider 回环协议 |
| E3 | 真机/真实实例/临时账户 | 真实焦点/IME/注册表/登录/多显示器 | 失焦矩阵（IME/菜单/Alt+Tab/通知）、登录自启 20 次、跨应用复制粘贴、Release 性能采样、打包主旅程 |

## 分配决定

- C01/A04 授权反例、C04/A07 围栏反例、C02/A08 词库反例、C03/A09 日志反例、C07/A01–A03 自启反例：**pure**（生产函数 + seam），同一反例在 LogicTests 的副本保留为完整宿主回归。
- C05/A05/A06 的窗口行为部分：**isolated-WPF**；其自动复制批准逻辑若抽入 gate/纯函数则补 pure 版本。
- C00 全量套件与 12 个 WPF Application 生命周期失败调查：**isolated-WPF**（宿主 STA/Application 所有权修复属 A11 后续子包）。
- C23 协议回环、CoreBridge 契约：**native-loopback**。
- 一切"真实焦点/真实登录/真实付费"场景：**E3**，必须逐项获得授权与环境，不得以 fixture 冒充。

## 未验证共享包（按实际覆盖，2026-09-12 复核更正；不是 0）

1. **全量 WPF 套件（LogicTests）未执行**：授权边界、词库读取/保护、日志 allowlist、围栏语法、面板/查词状态机、异常屏障、自启决策等共享行为改动，目前只有针对性测试与编译证据，没有全量回归。
2. **新增 WPF 行为测试未执行**：HiddenPanelCompletionNeverCopies、QuickSearchFocusLossKeepsSession、TranslationPanelCloseKeepsPartialAndHides、CoordinatorRefusesWorkWhenFused、A06EscapeRecencyAndExitWiring。
3. **E3 矩阵全部未验证**：IME/菜单失焦、登录自启 20 次、跨应用复制粘贴、异常熔断演练、重启握手。
4. **A08 隔离副本创建失败分支**无确定性测试（防御性代码，代码审阅覆盖）。
5. **cargo 180 只覆盖 Rust 契约**，不覆盖本轮 C# 共享合同。
6. **C09 测量运行未执行**：30 次启动 P50/P95 与内存/空闲 CPU 采样需退出真实实例并完成 self-contained publish 后运行 scripts/measure-startup.ps1；当前只有脚本、冲突预检与 POPGLOT_DATA_ROOT 夹具的实现证据。
6. pure 宿主 11 组仅覆盖纯逻辑切片；不得外推为 WPF/native-loopback/E3 通过。

## 宿主身份

- PureTests：`tests/PopGlot.Windows.PureTests/bin/Release/net10.0-windows10.0.19041.0/PopGlot.Windows.PureTests.exe`，exit 0=全过。
- 完整宿主：保持既有 C00 合同（实例在场 → exit 3），不加 skip、不改进程名。
