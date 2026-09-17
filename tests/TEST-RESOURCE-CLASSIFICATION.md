# 测试资源依赖分类（A11 交付）

> 2026-09-12。本表把现有与计划的测试按实际资源依赖分入四档，是"哪条验证可以在什么环境跑"的唯一索引。
> 规则：pure 宿主（tests/PopGlot.Windows.PureTests）永不触碰 WPF Application、原生 Core、凭据库、注册表、剪贴板、热键或真实网络；
> 任何需要其中之一的测试必须挂在对应宿主，不得为迁就 pure 宿主删减断言。

| 档位 | 宿主 | 允许的资源 | 典型测试组 |
|---|---|---|---|
| pure | PureTests exe（无实例守卫，可与运行中 PopGlot 共存） | 临时目录、内存凭据桩、拒绝一切发送的 guard、seam 注入 | OutboundPolicy 令牌/策略、MarkdownPresenter.ToPlainText、DiagnosticsLog 结构与轮转、VocabularyStore 全行为、StartupRegistration 状态机（seam）、ShellSettings 序列化、FreeTranslateService URL/解析/缓存（假 sender）、TranslationPanelStreamGate/TranslationStreamBuffer/QuickSearchState 纯状态机、Prompt 存储/纯编译器 envelope 契约、SessionStore 容量/LRU/TTL |
| isolated-WPF | LogicTests exe（完整守卫：无真实实例、隔离存储、内存凭据、守护 sender） | 单个 STA Application/Dispatcher、离屏渲染、WPF 控件树 | RenderToFlowDocument 视觉、面板/查词窗口生命周期、设置窗口草稿机、主题/字幕按钮、RunStaBatch 全组 |
| native-loopback | LogicTests exe + CoreBridge.Initialize(隔离配置目录) + popglot_ffi.dll | 原生 Rust core（隔离数据目录）、回环 mock HTTP | CoreBridge 路由决策表、endpoint 分类跨语言一致性、ModelCatalogService、ProfileManager、Provider 回环协议 |
| E3 | 真机/真实实例/临时账户 | 真实焦点/IME/注册表/登录/多显示器 | 失焦矩阵（IME/菜单/Alt+Tab/通知）、登录自启 20 次、跨应用复制粘贴、Release 性能采样、打包主旅程 |

## 分配决定

- C01/A04 授权反例、C04/A07 围栏反例、C02/A08 词库反例、C03/A09 日志反例、C07/A01–A03 自启反例：**pure**（生产函数 + seam），同一反例在 LogicTests 的副本保留为完整宿主回归。
- C05/A05/A06 的窗口行为部分：**isolated-WPF**；其自动复制批准逻辑若抽入 gate/纯函数则补 pure 版本。
- C00 全量套件与 12 个 WPF Application 生命周期失败调查：**isolated-WPF**（宿主 STA/Application 所有权修复属 A11 后续子包）。
- C23 协议回环、CoreBridge 契约：**native-loopback**。
- 一切"真实焦点/真实登录/真实付费"场景：**E3**，必须逐项获得授权与环境，不得以 fixture 冒充。

## 验证状态（2026-09-15 实跑刷新；开口项照实列出，不是 0）

> 口径：测试数字随新增用例持续变化，本文不维护固定值，以 `scripts/verify.ps1`（现含
> PureTests + LogicTests）与 `cargo test --workspace` 实跑为准。截至 2026-09-15 直接实跑：
> LogicTests 203/203、PureTests 23/23、Rust workspace 208/208 全绿。

1. **全量套件已执行**：LogicTests 与 PureTests 自 2026-09-13 起多轮全量实跑全绿（180 → 185 → 192 → 195 → 197 → 203，Pure 11 → 23；含 `real user config unchanged by the run` 与 `no unsanctioned public network send was attempted` PASS）。
2. **E3 矩阵全部未验证**：IME/菜单失焦、登录自启 20 次、跨应用复制粘贴、异常熔断演练、重启握手。
3. **A08 隔离副本创建失败分支**无确定性测试（防御性代码，代码审阅覆盖）。
4. **Rust 套件只覆盖 Rust 契约**（当前 208 项：core 78、domain 55、ffi 14、prompt_contract 14、provider_http 30、benchmark_safety 12、smoke 5），不替代 C# 共享合同的 Pure/Logic 覆盖；反之亦然。
5. **C09 真实测量**：首次 30 次启动测量已于 2026-09-14 完成（P50=373ms / P95=383ms、内存/空闲 CPU 门禁 PASS），但发布包取自 2026-09-14 时点树、非最终代码树，且报告缺 CPU 型号——最终树复测仍开口。
6. pure 宿主仅覆盖纯逻辑切片；不得外推为 WPF/native-loopback/E3 通过。

## 宿主身份

- PureTests：`tests/PopGlot.Windows.PureTests/bin/Release/net10.0-windows10.0.19041.0/PopGlot.Windows.PureTests.exe`，exit 0=全过。
- 完整宿主：保持既有 C00 合同（实例在场 → exit 3），不加 skip、不改进程名。
