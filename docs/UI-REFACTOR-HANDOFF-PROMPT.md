# PopGlot UI 重构 — 交接 Prompt（历史归档 · 已完成）

> **归档状态**：✅ 本交接文档涉及的任务已全部实施完毕（截至 0.1.0 版本），全量 113 项逻辑测试通过。

---

## Prompt 正文（复制以下全部内容）

```
你正在继续一个 WPF 桌面翻译工具 PopGlot 的 UI 重构任务。项目目录：D:/Projects/GitProjects/PopGlot

## 背景
PopGlot 是 Rust 核心 + C#/.NET 10 WPF 前端的翻译/OCR 工具。我们正在重构 UI：
1. 把巨石 MainWindow（1092 行 XAML + 1791 行 CS）拆分成 7 个 UserControl
2. 重设计翻译弹窗（TranslationPanelWindow）对标 Pot/Bob/DeepL
3. 打磨 QuickSearchWindow
4. 清理硬编码和重复图标

## 当前进度
详细计划见 `docs/UI-REFACTOR-PLAN.md`，快速指引见 `docs/UI-REFACTOR-README.md`。

### 已完成
- ✅ 7 个 Section UserControl 文件已创建在 `apps/PopGlot.Windows/Sections/`
  - TranslateSection, LibrarySection, GeneralSection, ShortcutsSection, ServicesSection, PrivacySection, DataSection
  - 每个都有 .xaml 和 .xaml.cs
  - 还有一个 Helpers.cs 共享辅助类
- ✅ 共享样式（RowTitle, RowHint, SettingsRow）已迁移到 Controls.xaml

### ⚠️ 未完成（最高优先级）
- ❌ **MainWindow.xaml 还没有被改造** — 7 个 Section 虽然创建了，但 MainWindow 还是原样（1092 行），没有引用它们
- ❌ **MainWindow.xaml.cs 还没有被改造** — 事件处理器虽然已复制到 Section，但原文件还没删除对应代码

### 你需要做的（按顺序）

#### Step 1: 先全面理解现状
1. 读 `docs/UI-REFACTOR-PLAN.md` — 完整重构计划（包含 theme token 表、事件处理器归属表、文件依赖图）
2. 读 `apps/PopGlot.Windows/MainWindow.xaml` — 理解现有 7 个 Section 的 XAML 边界
3. 读 `apps/PopGlot.Windows/MainWindow.xaml.cs` — 理解哪些事件处理器属于哪个 Section
4. 读所有 `apps/PopGlot.Windows/Sections/*.xaml` 和 `*.cs` — 确认已创建的内容是否正确

#### Step 2: 改造 MainWindow.xaml
1. 添加命名空间: `xmlns:sections="clr-namespace:PopGlot.Windows.Sections"`
2. 把 `<StackPanel x:Name="TranslateSection">...</StackPanel>` 整块替换为 `<sections:TranslateSection x:Name="TranslateSection" />`
3. 对其余 6 个 Section 做同样处理（LibrarySection, GeneralSection, ShortcutsSection, ProviderSection, CaptureSection, DataSection）
4. 注意：原 XAML 中 Section 的 x:Name 是 `TranslateSection`、`LibrarySection` 等 — 新 UserControl 的 x:Name 要保持一致
5. 保留：侧边栏、导航 RadioButton、标题栏 WindowChrome、底部 Save/Revert 栏
6. 删除 Window.Resources 中只被 Section 用到的图标 Geometry（已移到 Controls.xaml 或 Section 自带）

#### Step 3: 改造 MainWindow.xaml.cs
1. 删除已经迁移到各 Section 的事件处理器（参考 `docs/UI-REFACTOR-PLAN.md` 第八节的归属表）
2. 保留：构造函数、Nav_Checked、Save_Click、Revert_Click、MinimizeButton_Click、MaximizeButton_Click、CloseButton_Click、LoadSettings、ApplySettings
3. 在构造函数中初始化各 Section（传入它们需要的服务实例）
4. 连接跨 Section 事件（如 Library 的 LoadToTranslate 触发导航到 Translate）

#### Step 4: 确保编译和集成
1. 检查每个 Section 的命名空间是 `PopGlot.Windows.Sections`
2. 确保 Section 构造函数签名与 MainWindow 传参匹配
3. .csproj 使用隐式包含，新文件自动加入

#### Step 5: 验证
运行：
```
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1
```
必须 **39 passed, 0 failed**。

如果测试失败，重点检查：
- x:Name 是否保持一致
- 事件处理器是否正确绑定
- 服务实例是否正确传入
- DynamicResource 在 UserControl 中是否能正确解析

#### Step 6: 翻译弹窗重设计（如果 Step 5 通过）
参考 `docs/UI-REFACTOR-PLAN.md` 第三节 Task 2，重设计 `TranslationPanelWindow.xaml`。
核心改动：源文本区 → 语言选择栏 → 结果卡片三段式布局。

#### Step 7: QuickSearchWindow 打磨（如果 Step 6 通过）
统一与翻译弹窗的视觉风格。

#### Step 8: 清理
- 图标去重（Controls.xaml 为唯一来源）
- 硬编码颜色 → theme token
- 更新 docs/UI-REFACTOR-PLAN.md 进度日志

## 关键约束
- 保持 code-behind，不做 MVVM
- 不改 x:Name — 测试依赖
- 颜色全走 DynamicResource（token 列表见 ThemeService.cs）
- TFM: net10.0-windows10.0.19041.0
- 每改一步都可以跑 verify.ps1 验证

## 构建命令
```bash
# 编译
cargo build --workspace --release
dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release

# 验证
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1

# 发布
dotnet publish apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -r win-x64 --self-contained false -o dist/release
cp target/release/popglot_ffi.dll dist/release/
```
```

---

## 使用说明

1. 复制上面 ``` 之间的全部内容
2. 粘贴给新的 AI（Claude / GPT / Cursor / Windsurf 等）
3. 新 AI 会先读文档理解上下文，然后从 Step 2 开始继续
4. 关键文档路径：
   - `docs/UI-REFACTOR-PLAN.md` — 16KB 完整计划
   - `docs/UI-REFACTOR-README.md` — 精简版
   - `apps/PopGlot.Windows/Sections/` — 已创建的 7 个 UserControl
