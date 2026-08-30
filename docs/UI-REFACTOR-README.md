# PopGlot UI 重构 — 开发指引（历史归档 · 已完成）

> **归档状态**：✅ UI 重构与架构解耦已全部完成（截至 0.1.0 版本），全量 113 项 Windows 逻辑测试通过。
> 完整历史计划见 `docs/UI-REFACTOR-PLAN.md`。

## 快速上下文

PopGlot 是一个 Windows 桌面翻译/OCR 工具（Rust 核心 + C#/.NET 10 WPF 前端）。
当前 UI 需要重构：拆分巨石 MainWindow、重设计翻译弹窗、统一视觉风格。

## 项目结构

```
PopGlot/
├── crates/                        # Rust 核心库
│   ├── popglot-core/              # 翻译引擎、Provider 路由
│   ├── popglot-domain/            # 领域模型
│   └── popglot-ffi/               # C# ↔ Rust FFI 层
├── apps/
│   └── PopGlot.Windows/           # WPF 前端（重构目标）
│       ├── MainWindow.xaml/.cs     # 🔴 需拆分 → Sections/
│       ├── TranslationPanelWindow  # 🟡 需重设计
│       ├── QuickSearchWindow       # 🟡 需打磨
│       ├── CaptureOverlayWindow    # ✅ 保持
│       ├── FloatingTriggerWindow   # ✅ 保持
│       ├── Sections/               # 🆕 拆分出的 UserControls
│       ├── Services/               # 服务层
│       └── Themes/Controls.xaml    # 全局控件样式
├── tests/                         # 逻辑测试
├── scripts/verify.ps1             # 验证脚本（65 tests）
└── docs/UI-REFACTOR-PLAN.md       # 详细重构计划
```

## 当前进度 (截至 2026-08-29 15:30)

- [x] 重构计划制定 + 对标研究
- [x] Task 1a: 7 个 Section UserControl 文件已创建（Sections/ 目录，含 XAML + CS）
- [x] Task 1b: 共享样式迁移到 Controls.xaml
- [x] **Task 1c: MainWindow.xaml/cs 瘦身（159 行 / 494 行，引用 7 个 Section）**
- [x] Task 2: 重设计 TranslationPanelWindow（三段式布局 + 合并换行）
- [x] Task 3: 打磨 QuickSearchWindow（结果卡片化对齐）
- [x] Task 4: 图标去重 + 硬编码清理
- [x] 全量验证：**39 passed, 0 failed**
- [x] **第二轮（产品级 IA 重构）：独立 SettingsWindow、控制中心移除、服务 Master–Detail + 双默认路由、资料库 Master–Detail、工作台布局基线、设计 token（6/10/12px）**——详见 `UI-REFACTOR-PLAN.md` 第十一节
- [x] **第三轮（P0 收口 + 去 AI 化）：凭据目标顺序修复、设置原子保存+回滚、服务编辑器 Dirty 状态、受约束布局/独立滚动/虚拟化、服务页体验补全、设置页去卡片化、LiveRegion/AutomationProperties——详见 `UI-REFACTOR-PLAN.md` 第十二节**
- [x] 全量验证：**43 passed, 0 failed**（39 存量 + 4 新增）

> 🔑 第二轮布局基线见 `docs/UI-REFACTOR-HANDOFF-PROMPT.md`（已实施完成）

## 如何继续

1. 阅读 `docs/UI-REFACTOR-PLAN.md` 了解完整计划
2. 检查 `apps/PopGlot.Windows/Sections/` 已完成的文件
3. 对比 MainWindow.xaml/.cs 看哪些 section 还没拆出
4. 每次修改后运行验证：
   ```
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1
   ```
5. 预期结果：68 passed, 0 failed

## 关键约束

- **不做 MVVM 迁移** — 保持 code-behind
- **不改 x:Name** — 测试依赖
- **颜色全走 DynamicResource** — 参见 ThemeService.cs
- **TFM**: net10.0-windows10.0.19041.0
- **保留 WindowChrome** 标题栏

## 构建命令

```bash
# Rust 构建
cargo build --workspace --release

# .NET 构建
dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release

# 发布可执行文件
dotnet publish apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -r win-x64 --self-contained false -o dist/release
cp target/release/popglot_ffi.dll dist/release/

# 验证
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1
```
