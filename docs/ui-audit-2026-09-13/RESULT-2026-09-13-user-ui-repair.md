# 用户界面修复结果表（2026-09-13）

状态：READY_FOR_REVIEW（实现者自检，无独立复核 / 无 lead 复核 / 无 DONE_VERIFIED）

## 代码修改（本轮新增行为）

| 文件 | 行为变化 |
|---|---|
| `Services/ProfileManager.cs` | 新增统一就绪判断 `IsTextEngineExecutable` / `HasConfiguredUserEngine`；`IsProfileExecutable` 的密钥检查改用与执行链一致的 `HasResolvableApiKey`（profile 专属 target → 活动 profile 的 legacy target 回退） |
| `Sections/TranslateSection.xaml(.cs)` | 空态拆分为两个独立概念：有完整用户引擎才隐藏 CTA；允许免费引擎时显示「当前使用内置公共翻译」，否则「尚未配置翻译引擎」；按钮为真实 `Button`（PrimaryButton、Hand、AutomationProperties.Name=添加翻译引擎）；新增 `OpenAddEngine_Click`（仅导航）与 `RefreshAfterSettingsChanged` |
| `Sections/ServicesSection.xaml(.cs)` | 彻底删除 RoutingPanel、DefaultTextCombo、DefaultVisionCombo、VisionIncompatHint、RefreshDefaultCombos、两个 SelectionChanged、SetDefaultButton、SetDefault_Click、ProviderComboOption、_suppressComboEvents（删除，非 Collapsed）；新增 `BeginAddEngineFlow` 直接入口 |
| `MainWindow.xaml.cs` | 底部常驻摘要只剩 状态点+当前引擎简短名+箭头；删除打开菜单时的 `UpdateFreeEngineHealthAsync(force:true)` 自动探测（探测仅保留菜单内「重新检测免费引擎」）；`BuildEngineSwitchMenu` 抽出（构建只读本地状态）；激活时刷新空态与状态行；新增 `RefreshShellStatusForConfig` 消除「尚未配置/就绪」冲突 |
| `SettingsWindow.xaml.cs` | 新增 `ShowProviderAddFlow()`：打开即选中「翻译引擎」并进入新增流程 |
| `App.xaml.cs` | `ShowSettings(startAddEngineFlow)` 装配主窗口 CTA 直达新增流程 |
| `tests/.../LogicTests/Program.cs` | RoutingPanel/Combo 断言翻转为负断言；新增 T1–T6 六个 UI 测试（RunStaBatch） |

### 2026-09-13 当前复核纠正

- 原结果把「CTA 可见」误当成「CTA 一定可点击」。实际空列表 `ListBox` 与空态 CTA 位于同一 Grid 单元格，后声明的透明列表覆盖并截获了按钮命中测试。
- 当前修复在零 Profile 时折叠 `ProfilesListBox`，并为 `ProfilesEmptyText` 设置明确层级；新增 WPF 命中测试 `EmptyServiceListLeavesAddButtonClickable`。完整 LogicTests 已在用户实例退出后重跑，当前 192/192 通过，命中测试确认指针到达按钮且点击进入新增流程。
- 应用图标已切换为 v5 扁平双气泡：针对 16–20px 小尺寸缩小白色面积、扩大紫色负空间和气泡间隔；纯色、透明圆角、无渐变和阴影，主窗口、设置窗口、托盘与程序集图标统一使用同一套资源。

## 需求结论

| 项 | 结论 | 证据 |
|---|---|---|
| REQ-UI-01 主页面添加引擎入口 | 通过 | 已修复透明空列表遮挡；完整 LogicTests 的真实 WPF 命中测试确认按钮可命中并进入新增流程 |
| REQ-UI-02 路由唯一入口 | 通过 | 负断言 + 真实设置窗口视觉树无路由控件；底部为唯一切换入口；打开菜单显示「未检测」且不改配置、0 网络请求 |
| REQ-UI-03 文档纠正 | 通过（本文档即唯一结果表） | 旧「已完成/lead 复核」标签未继承 |

## 测试结论

| 测试 | 结论 |
|---|---|
| T1 0 Profiles + Allowed | 通过（旧 GUI 可见证据 + 当前 WPF 命中/点击回归） |
| T2 完全无线路 | 通过（LogicTests + GUI main_unconfigured.png，状态行无「就绪」） |
| T3 不完整引擎 ×3 | 通过（LogicTests + GUI main_incomplete_profile_cta.png） |
| T4 完整引擎 | 通过（LogicTests；GUI 侧保存 DeepSeek 后列表「文字默认」徽章只读、底部显示 DeepSeek） |
| T5 路由入口唯一 | 通过（负断言 + 逻辑树唯一切换按钮 + 菜单每段当前项唯一 + 打开菜单 0 网络请求/不改配置） |
| T6 点击导航 | 通过（LogicTests + 真实点击 GUI：设置窗口落在服务商目录新增流程） |
| T7 布局/多 DPI/主题矩阵 | 未验证（本轮未跑 150%/175%/High Contrast 截图矩阵） |
| T8 旧问题回归 | 通过（LogicTests 全套 192 含设置窗构造/关闭、隔离「真实配置未变」「0 次公网发送」断言） |

## 命令与退出码

| 命令 | 退出码 | 结果 |
|---|---|---|
| `dotnet build … -p:OutputPath=bin/UserUiRepair/` | 0 | 0 警告 0 错误 |
| `dotnet run --project tests/PopGlot.Windows.PureTests` | 0 | 18 通过 / 0 失败 |
| `dotnet run --project tests/PopGlot.Windows.LogicTests` | 0 | 192 通过 / 0 失败（含新增 CTA 命中回归） |
| `cargo fmt --all -- --check` | 0 | 通过 |
| `cargo test --workspace --locked` | 0 | 11 个套件全部 ok（67+12+14+27+5+45+10=180），0 失败 |
| `cargo clippy --workspace --all-targets --locked -- -D warnings` | 0 | 通过 |

## 验收构建身份（实际运行的 EXE）

- Git HEAD：21f0132（未 commit，按要求不提交）
- EXE：`D:\Projects\GitProjects\PopGlot\apps\PopGlot.Windows\bin\UserUiRepair\PopGlot.exe`
- PopGlot.dll：SHA-256 `CE3AF279B793F3A7E867730FD9E40B44111E5DD1EE763A9DE8C4D6C15B85FBD9`（2026-09-13 21:03:48）
- popglot_ffi.dll：SHA-256 `C3D21D7C83AF4E2C05E50CD77178D7CDFA05879EDA422B629E981EA1CF197887`（Rust 未改动）
- 验收进程 PID 31268/41080，Responding=True，实际 Path 指向上述目录（非旧 ui-preview/非 Release 默认目录）

### 当前跟进构建（CTA 命中修复 + v5 图标）

- EXE：`D:\Projects\GitProjects\PopGlot\apps\PopGlot.Windows\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\PopGlot.exe`
- PopGlot.dll SHA-256：`66EDEEA3A293073050C9B7E05F66E7779A724E677A41A21DD7580D5E51D214E7`
- Release 构建：exit 0，0 警告，0 错误；PureTests：18/18 通过，0 次发送；LogicTests：192/192 通过；Rust：180/180 通过。

## 隔离配置与 GUI 证据

- 数据根：`POPGLOT_DATA_ROOT=D:\tmp\popglot-acceptance-20260913`（product-config.json / windows-shell.json / provider-settings.json 均在其中）
- 夹具演进：全新安装（Unset）→ 允许免费引擎（Allowed）→ 保存 DeepSeek（假 Key 流程，本机实际未写入凭据，Profile 保持"未配置密钥"——恰为 T3 真实态）
- 截图（均 1120×760，Dark，100% 缩放，DLL 见上）：`main_unconfigured.png`、`main_fallback_allowed.png`、`settings_addflow_final.png`、`main_incomplete_profile_cta.png`
- 控件树摘要：CTA 为真实 Button（添加翻译引擎），主窗口恰 1 个「翻译引擎快速切换」，设置引擎页视觉树 0 个路由控件
- Crash：隔离数据根 0 个 crash 文件；Event Log 1026 近 2 小时 0 条
- 无隐式联网：打开快速切换菜单后免费引擎保持「未检测」；测试隔离层报告本次运行 0 次公网发送尝试

## 剩余风险 / 未验证

1. T7（150%/175% 缩放、High Contrast、窄窗口矩阵）未验证。
2. 签名、安装器、快捷方式、真实用户配置迁移：未验证；0.1.5 仅按产品负责人明确授权发布未签名便携 zip，不把这些能力写入发布承诺。
3. 本机存在历史遗留凭据条目 `PopGlot/OpenAICompatibleApiKey`（用户真实数据，未读取内容、未修改）；新增的 `HasResolvableApiKey` 使 legacy 回退同时作用于就绪判断与执行链，行为一致性依赖 `ResolveCredentialTargetFor` 现有合同。
4. 隔离验收期间真实点击未真正进入 PasswordBox（假 Key 未落盘），T4 的"完整引擎"GUI 态由列表徽章与底部摘要佐证，输入路径本身由 LogicTests 的内存凭据夹具覆盖。
5. 更大范围的执行台账并非全部实施：`review-2026-09-12/EXECUTION-LEDGER.md` 仍有 52 行 `SPECIFIED_NOT_IMPLEMENTED`；UI 总览也同时存在 W9「进行中」、GAP-02「未排期」以及 WIN-19 状态互相矛盾，不能用文档篇幅推导产品已完成。

## 发布边界

T7、独立复核、签名和真实用户配置升级仍未验证，因此不得发布或宣称签名安装器、自动更新或商业总验收；产品负责人已明确授权的 0.1.5 未签名便携 zip 可在全量自动门禁通过后发布。
