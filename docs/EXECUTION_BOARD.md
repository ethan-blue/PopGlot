# 执行看板（历史归档 · 已完成）

> **归档状态**：✅ 已完成并归档（截至 0.0.2 版本）。P0/P1/P2 重构及修复全部完成，全量 68 项 Windows 逻辑测试持续保持全绿。

第一轮：15 项 P0 全部封闭（见文末历史）。本轮完成全部 P1/P2 骨架项。

## 第二轮完成项

| 项 | 内容 | 验证 |
|---|---|---|
| P1-CI | `.github/workflows/ci.yml` windows-shell job 增加 `dotnet run --project tests/...` Release 逻辑测试（含零出网、草稿隔离、DPI 矩阵） | ci.yml diff |
| P2-视觉矩阵 | 逻辑测试渲染 主窗口+浮窗 × 深浅主题 × 125/150/200% DPI，共 12 张矩阵截图落盘 `artifacts/screenshots/` | 测试通过，20 张产物 |
| P1-启动路径 | 快捷键注册改挂在隐藏轻量窗口；MainWindow 首次使用才构建（`EnsureMainWindow`） | AX 树确认托盘启动不再建主窗 |
| P1-Sleep 清理 | 剪贴板事务全链路异步化：修饰键等待 `Thread.Sleep(10)`→`await Task.Delay(10)`；重试 `Sleep(12n)`→`await Delay(12n)`；主窗口/浮窗复制同样异步 | 构建+测试绿 |
| P1-IA | 主窗口重构为控制中心：快速翻译 / 资料库 / 通用 / 快捷键 / 服务 / OCR 与隐私 / 数据与高级（7 区导航）；生词/历史不再是顶层设置项 | 真机 AX+截图审查 |
| P1-资料库 | 历史+生词统一「资料库」页：各自搜索框、导出（CSV/MD/Anki）与内容扩展名一致、删除/清空带确认、「数据与高级」页补生词清空 | AX 树确认 |
| P1-服务 Profile | 服务列表（名称/协议/模型/本地-在线/默认徽章）+ 添加/编辑/删除/设为默认 + 每 Profile 独立 `PopGlot/provider/{id}` 凭据 + 草稿测试不落盘 + 首次启用从现有核心设置种子化（用户真实 Gemini 配置成为默认 Profile，旧凭据回退可读） | 真机验证：列表显示 gemini-3.7-flash-high 默认 |
| 可访问性 | 11 个图标按钮补 `AutomationProperties.Name`（浮窗 7 + 查词 3 + 主窗口标题栏 3 + 星标） | AX 树读出名称 |
| P2-向导 | 添加服务改为三步向导：选择服务类型（预设+协议）→ 凭据与模型 → 测试并保存；高级字段第 2 步起折叠；退出/取消随时回活跃服务 | 真机走查 |
| P2-死代码 | 移除无人调用的 `CoreBridge.TestConnectionAsync` 与 v1 `TranslateText`/`TranslateVision`/`TestConnection` P/Invoke 声明（Rust 侧 C ABI 导出保留） | 构建绿 |
| P2-可访问性门禁 | 新逻辑测试 `IconControlsExposeAutomationNames`：五个 XAML 中任何 Path 图标按钮缺 `AutomationProperties.Name` 即失败（修出 QuickSearch 关闭、浮窗重试/清空/结果复制朗读等 6 处漏网） | 39/39 测试 |
| P2-Release 基线 | Release 配置复测：Working Set 89.9 MB（≤100 MiB 预算）、浮窗首帧 21 ms（≤150 ms）、取消 0.05 ms（≤100 ms）、逻辑测试 39/39 | 记录于本表 |

## 第二轮验证

- `scripts/verify.ps1` 全绿（cargo fmt / 53 Rust 测试 / clippy 0 警告 / WPF 构建 / 39 逻辑测试）。
- 真机启动审查：7 区导航、服务页 Profile 列表（种子化正确）、资料库、快捷键 4 行全部渲染正确。
- Profile 一致性修复：`product-config.json` 不存在时从 `provider-settings.json` 播种，预设不再掩盖用户真实配置。
- 文档同步：UX_DECISIONS（新 IA + 向导决策）、ARCHITECTURE（迁移收紧 + 启动路径 + 结构）、CONFIGURATION_MIGRATION（product-config.json）、README、PRIVACY。

## 剩余（P2 深水区，超出本轮）

- 供应商添加走分步向导（当前是列表+编辑表单，已达总纲"不展开高级即可完成添加"的验收线）。
- 多屏/DPI 真机回归矩阵（截图矩阵已就绪，缺人工比对记录）。
- Narrator 全旅程录音验证、高对比主题人工走查。
- 性能预算复测（冷启动延迟建窗后的数字需在 Release 记录）。

---

# 第一轮记录（2026-08-28）

基线：`scripts/verify.ps1` 全绿（cargo fmt/test/clippy + WPF 构建 + 逻辑测试）。
收尾复测：Rust workspace 53 测试全绿、clippy 0 警告；WPF 逻辑测试 38/38 通过；应用实际启动并通过 UI 审查。

## P0 最终状态（全部封闭）

| 项 | 结论 | 证据 |
|---|---|---|
| P0-1 QuickSearch 离线不调免费引擎 | ✅ | 走 TranslationCoordinator；回环监听测试 0 连接 |
| P0-2 统一隐私/出网策略 | ✅ | 四个文本入口（划词/截图/手动/查词）全部经 Coordinator；`OfflineModeSendsNothing` 测试证明离线模式下 mock 端点 0 TCP 连接，授权后同一端点可达（sanity） |
| P0-3 QuickSearch Enter 提交 | ✅ | 无 400ms 定时器；底部文案与行为一致（Enter 翻译） |
| P0-4 ShowWindow 未处理异常 | ✅ | `App.HandleHotkey` 补 `ShowWindow → ShowMainWindow` 分支 |
| P0-5 快捷键保存不丢失 | ✅ | 设置页第 4 行 HotkeyRecorder；Save/Load/侧栏/托盘全链路；round-trip + 旧配置回退测试 |
| P0-6 TestConnection 无副作用 | ✅ | `BuildProviderSettingsFromForm` 内存草稿 + `TestConnectionDraft`；`DraftConnectionLeavesSettingsUntouched` 测试证明设置文件字节级不变 |
| P0-7 免费引擎显式同意 | ✅ | `FreeEngineConsent`（Unset/Allowed/Denied）持久化；首次出网弹窗（允许并记住/仅本次/拒绝）；拒绝不回退；设置页可见+可重置；无提示环境 fail closed |
| P0-8 迁移不新增权限 | ✅ | domain 手写 Deserialize：缺失的 `network_enabled`/`allow_image_upload_in_auto` 一律 false；显式 true 保留；Rust 3 个迁移测试 |
| P0-9 request_id 精确取消 | ✅ | FFI A/B 并发取消测试（双向隔离 + 全局取消收尾） |
| P0-10 锁不跨网络 | ✅ | 既有快照执行保持，测试通过 |
| P0-11 原子写入+损坏恢复 | ✅ | save 增加 `sync_all`；损坏文件保留为 `provider-settings.corrupt-*.json` 并经 `popglot_take_startup_notice` → 托盘气泡告知用户 |
| P0-12 浮窗展开携带 Session | ✅ | 展开回调携带原文/语言对/译文进主窗口，不重译、先开主窗再关浮窗 |
| P0-13 导出文案/格式一致 | ✅ | 历史/生词 CSV、Markdown、Anki TSV 按钮全部接入 XAML，扩展名与内容一致 |
| P0-14 自动路由诚实 | ✅ | `auto_local_first` 文案与真实能力一致（既有修复，验证通过） |
| P0-15 Token 保护失败→Partial | ✅ | 浮窗对带警告的结果显示「部分成功」徽章（警告色）+ 状态文案；主窗口同步显示 |

## 去 AI 化/UI 审查中修复

- 极速查词底部错误提示「Enter 复制并关闭」→「Enter 翻译 · Ctrl+R 朗读 · Esc 退出」；默认文案与实际行为一致。
- 语言徽章从静态「自动 → 中文」改为读取真实语言对。
- 星标按钮从装饰性 Sparkles 图标改为语义化 IconStar；删除解释区的无语义 Sparkles 图标。
- 侧栏快捷键提示补第 4 行（打开主窗口）。

## 验收清单对照

- [x] 离线模式下所有入口对 mock HTTP 服务请求数为 0（自动化测试）
- [x] 仅输入不按 Enter 不出网（无任何自动提交定时器，代码审查 + 文案一致）
- [x] 快捷键首次启动/保存/重启/迁移后全部正常（round-trip + v1/v2 迁移测试）
- [x] 测试连接前后设置文件字节不变、凭据不动（自动化测试）
- [x] A/B 重叠请求取消互不影响（FFI 测试）
- [x] 翻译期间读取/保存设置不等待网络（快照架构 + 测试通过）
- [x] 历史 schema 迁移不新增隐私权限（Rust 迁移测试）
- [x] 免费引擎首次出网有明确同意、结果可见来源、拒绝不回退

## 剩余工作（P1/P2，未在本轮范围）

- ProviderProfile 完整向导 UI（服务列表→添加→凭据→测试草稿→保存）；当前 Profile 数据模型与独立 credential target 已就绪（Rust domain + C# ProfileManager），UI 仍是单表单+预设。
- 控制中心五区导航（通用/快捷键/服务/OCR 与隐私/数据与高级）与资料库独立页面。
- 主窗口延迟创建、启动路径瘦身、UI 线程 Sleep 清理（性能预算项）。
- CI 把 Windows Shell 逻辑测试与 FFI 并发测试真正跑起来（当前仅本地 verify.ps1）。
- 视觉回归矩阵（深浅 × DPI × 高对比）与 Narrator 全旅程验证。
