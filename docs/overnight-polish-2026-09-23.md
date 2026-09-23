# PopGlot 夜间全量打磨任务清单（2026-09-23）

> **目的**：供 AI agent 在 23:00–09:00 自动执行。每完成一条任务就在对应节末尾写上 `✅ DONE` 和 commit hash。跳过任一条必须写 `⏭ SKIPPED` 和原因。
>
> **安全红线**：每条改动必须能 `dotnet build -c Release` 通过 + `cargo test` 180 全绿 + PureTests 全绿。不满足则立即 revert 该条。不合并到 main，所有工作在 `polish/overnight-2026-09-23` 分支进行。

---

## 0. 执行环境

```
工作目录：D:\Projects\GitProjects\PopGlot
分支：从 main 创建 polish/overnight-2026-09-23
构建验证：
  dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release
  cargo test --workspace (180 tests)
  dotnet build tests/PopGlot.PureTests/PopGlot.PureTests.csproj -c Release && tests/PopGlot.PureTests/bin/Release/net10.0-windows10.0.19041.0/PopGlot.PureTests.exe
```

---

## 第一部分：架构与协议改动

### T01 — 将内部请求协议换成 LiteLLM 代理兼容

**背景**：项目当前实现了四套原生协议适配器（OpenAI Chat、OpenAI Responses、Anthropic Messages、Gemini generateContent）。用户希望改为统一走 [LiteLLM](https://github.com/BerriAI/litellm) 代理，即所有请求走 OpenAI-compatible `/chat/completions` 格式，由 LiteLLM 代理层转换为各家协议。

**目标**：不删除现有四套协议实现（保留直连能力），而是新增 LiteLLM 代理预设，让用户可以选择通过 LiteLLM 代理统一接入。

**涉及文件**：

| 文件 | 改动 |
|---|---|
| `apps/PopGlot.Windows/Sections/ServicesSection.xaml` | 在预设列表中新增 LiteLLM 预设按钮 |
| `apps/PopGlot.Windows/Sections/ServicesSection.xaml.cs` | 在 `Preset_Click` 的 switch 中新增 `"litellm"` 分支 |
| `apps/PopGlot.Windows/Services/ModelCatalogService.cs` | 确认 OpenAI-compatible catalog adapter 已能解析 LiteLLM 的 `/models` 响应（LiteLLM 返回标准 OpenAI 格式，应已兼容） |

**具体步骤**：

1. **ServicesSection.xaml** — 在 `PresetColumnRight` 的 Ollama 按钮之后新增：
   ```xml
   <Button Style="{StaticResource ProviderPresetButton}" Tag="litellm"
           AutomationProperties.Name="LiteLLM Proxy 预设" Click="Preset_Click">
       <StackPanel>
           <TextBlock Text="LiteLLM Proxy" FontWeight="SemiBold"/>
           <TextBlock Text="统一代理 · OpenAI 兼容" Style="{StaticResource Caption}" Margin="0,3,0,0"/>
       </StackPanel>
   </Button>
   ```

2. **ServicesSection.xaml.cs Preset_Click switch** — 新增 case：
   ```csharp
   "litellm" => (ProviderType.OpenAiCompatible, "http://localhost:4000",
       "/chat/completions", "已填入 LiteLLM Proxy 预设，确认地址后点「获取模型」。"),
   ```

3. **ServicesSection.xaml.cs 名称映射** — 在 `ServiceNameTextBox.Text is ...` 的 `or` 链中加入 `"LiteLLM Proxy"`，在 switch 中加 `"litellm" => "LiteLLM Proxy"`。

4. **验证**：构建通过，启动后在「添加引擎 → 选择服务商」页面能看到 LiteLLM 预设卡片，点击后自动填入 `http://localhost:4000` 和 `/chat/completions`，协议选择 OpenAI 兼容。

**不做的事**：不修改 Rust 侧 `ProviderType` 枚举、不新增协议适配器、不改 `provider.rs` 中任何逻辑。LiteLLM 走的就是 OpenAI 兼容协议。

---

## 第二部分：设置页自定义引擎表单布局问题

### T02 — 自定义引擎编辑器输入框左侧大空白

**现象**：用户反馈"输入自定义引擎的文字或链接时，左边有很大的空位"。

**根因分析**：
- 编辑器表单使用 `IdentityFieldsGrid` 三列布局：`<ColumnDefinition Width="*"/>` + `Width="16"` gutter + `Width="*"/>`。
- 当「接口协议」(`CustomProtocolGroup`) 为 `Collapsed` 时（预设云服务商），`ServiceNamePanel` 通过 `Grid.SetColumnSpan(ServiceNamePanel, 3)` 占满三列 — 无问题。
- 但当 `CustomProtocolGroup` 可见时（自定义引擎），`ServiceNamePanel` 占第一列（50% 宽度），`CustomProtocolGroup` 占第三列。`ServiceNamePanel` 只有一个 TextBox 和一个标签，视觉上在左半部分留白正常。
- **真正的空白问题在 `BaseUrlPanel`**：它作为独立一行（`Margin="0,0,0,14"`），但没有 `Grid.SetColumnSpan`，仅占第一列 — 因此 BaseUrl 输入框只有容器宽度的 50%，右半完全空白。

**涉及文件**：

| 文件 | 改动 |
|---|---|
| `apps/PopGlot.Windows/Sections/ServicesSection.xaml` | `BaseUrlPanel` 确认布局 |
| `apps/PopGlot.Windows/Sections/ServicesSection.xaml.cs` | `ApplyEditorLayout` 中为 `BaseUrlPanel` 补充 `ColumnSpan=3` |

**具体步骤**：

1. 在 `ApplyEditorLayout()` 方法中，在 `BaseUrlPanel.Margin = ...` 行之后加入：
   ```csharp
   Grid.SetColumnSpan(BaseUrlPanel, 3);
   ```
   **注意**：`BaseUrlPanel` 不在 `IdentityFieldsGrid` 内部，它是 `ConnectionCard` 的直属子元素。确认它的父 Grid 列定义。如果 `BaseUrlPanel` 是 `StackPanel` 的子元素而非 `Grid`，则它自然全宽 — 问题可能在别处。

2. **二次排查**：用 `grep` 确认 `BaseUrlPanel` 的父容器。如果它是 `StackPanel` 子元素（根据 XAML 第 296 行 `ConnectionCard` 内 `<StackPanel>`），则 `BaseUrlPanel` 已经全宽。那么用户反映的"左侧大空白"可能是：
   - **Padding 过大**：`BaseUrlTextBox` 使用 `EditorTextField` 样式，其父样式 `FormTextBox` 的 `Padding` 是 `12,0` — 仅 12px 左内边距，不算大。
   - **Placeholder 文字对齐**：占位文字 `https://api.openai.com/v1 或 https://your-proxy.com/v1` 显示正常。
   - **ComboBox 可编辑模式**：`TextModelCombo` 和 `VisionModelCombo` 使用 `IsEditable="True"` 的 ComboBox，其内部 `PART_EditableTextBox` 有 `Margin="10,0,34,0"` — **左边距 10px + Padding 2px**，ComboBox 本身无左 Padding — 所以文字从左边 12px 开始。但如果 ComboBox 的 `ContentPresenter`（非编辑模式下的 `Selection`）有 `Margin="12,0,34,0"` — 也是 12px。

3. **ServiceNamePanel 单列 vs 全宽**：当 `CustomProtocolGroup` 可见且非 compact 时，`ServiceNamePanel` 只占第一列（`Grid.SetColumnSpan(ServiceNamePanel, hasProtocol && !compact ? 1 : 3)`）。这是正确的 — 名称和协议并排。但如果窗口很宽（比如 960px），每列约 370px，名称输入框内的文字从 12px 开始 — 视觉上是否显得空？**不是空白 bug**，是双列布局的正常表现。

4. **最终判断与修复**：如果用户反映的是"输入框内的文字离左边框太远"，需要检查 `FormTextBox` 的 `Padding` 和 `ComboBox` 模板的 `PART_EditableTextBox.Margin`。如果是 "整个表单区域太窄，周围留白太多"，需要检查 `ConfigFormPanel` 的 `MaxWidth="760"`。

   **建议修复**：将 `ConfigFormPanel` 的 `MaxWidth` 从 760 改为 `900` 或删除 MaxWidth，让表单充分利用宽屏空间。同时确保 `EditorScroll` 的 `Padding="0,0,8,24"` 不会造成视觉偏移。

**涉及文件（最终）**：

```
apps/PopGlot.Windows/Sections/ServicesSection.xaml 第 280 行:
  <StackPanel x:Name="ConfigFormPanel" MaxWidth="760" ...>
改为:
  <StackPanel x:Name="ConfigFormPanel" MaxWidth="900" ...>
```

### T03 — 占位提示文案优化

**现象**：用户反馈"控制提示有没有很不好"。

**分析**：自定义引擎的输入框占位文案（placeholder）可以更明确地引导用户。

**改动**：

| 控件 | 当前占位 | 建议占位 |
|---|---|---|
| `ServiceNameTextBox` | `例如：公司 Gemini` | `例如：公司内部代理` |
| `BaseUrlTextBox` | `https://api.openai.com/v1 或 https://your-proxy.com/v1` | `例如：http://localhost:4000 或 https://your-proxy.com/v1` |
| `ApiKeyPasswordBox` | `留空保持现有密钥` | 不变（语义明确） |
| `ExtraHeadersTextBox` | `例如：X-Organization: team-a` | 不变 |

**涉及文件**：`apps/PopGlot.Windows/Sections/ServicesSection.xaml` 第 303、316 行。

---

## 第三部分：翻译结果区交互问题

### T04 — 翻译面板（TranslationPanelWindow）术语芯片可点击但工作台（TranslateSection）术语芯片不可点击

**现象**：用户反馈"正文翻译提出的文字或者是那样的字眼我们应该可以进行点击目前是点击不了的"。

**根因分析**：

1. **翻译浮窗 (TranslationPanelWindow)**：术语芯片使用 `TokenChipButton` 样式（`Button`），绑定了 `Click="TermChip_Click"` — 点击复制术语到剪贴板。**这个是可以点击的**，行为正确。

2. **工作台 (TranslateSection)**：术语列表 `TranslateTermsList`（第 516 行）使用 `ItemsControl`，但其 `DataTemplate` 内是一个 **`Border`**（非 Button）：
   ```xml
   <Border Background="{DynamicResource SurfacePressedBrush}"
           BorderBrush="{DynamicResource BorderSubtleBrush}" BorderThickness="1"
           CornerRadius="8" Padding="8,3" Margin="0,0,6,5">
       <TextBlock Text="{Binding}" Style="{StaticResource Metadata}"
                  Foreground="{DynamicResource TextSecondaryBrush}" />
   </Border>
   ```
   **Border 不是交互元素，没有 Click 事件** — 这就是用户说的"点击不了"。

**修复方案**：将 `TranslateSection.xaml` 中的术语 Border 改为 Button，仿照 `TranslationPanelWindow.xaml` 的做法。

**涉及文件**：

| 文件 | 改动 |
|---|---|
| `apps/PopGlot.Windows/Sections/TranslateSection.xaml` 第 520-529 行 | 将 Border 改为 Button，使用 `TokenChipButton` 样式 |
| `apps/PopGlot.Windows/Sections/TranslateSection.xaml.cs` | 新增 `TranslateTermChip_Click` 事件处理，复制点击的术语到剪贴板 |

**具体步骤**：

1. **TranslateSection.xaml** — 将第 522-527 行的 `<Border>...<TextBlock/></Border>` 替换为：
   ```xml
   <Button Style="{StaticResource TokenChipButton}"
           Content="{Binding}" Margin="0,2,4,2"
           Click="TranslateTermChip_Click" />
   ```

2. **TranslateSection.xaml.cs** — 新增事件处理方法（参照 TranslationPanelWindow 的 TermChip_Click）：
   ```csharp
   private async void TranslateTermChip_Click(object sender, RoutedEventArgs e)
   {
       if ((sender as Button)?.Content is string term && !string.IsNullOrWhiteSpace(term))
       {
           try
           {
               Clipboard.SetText(term);
               TranslateStatus.Text = $"已复制术语：{term}";
           }
           catch
           {
               TranslateStatus.Text = "复制失败，剪贴板被占用";
           }
       }
   }
   ```

3. **验证**：构建通过；在工作台翻译一段含术语保护的文本后，结果区下方的术语芯片可以点击，点击后显示"已复制术语：xxx"。

### T05 — 确认翻译结果正文是否可选择/复制

**现象**：用户提到"提出的文字或者是那样的字眼应该可以进行点击"。除了术语芯片，还需确认翻译结果的正文文本是否可选中复制。

**分析**：
- `TranslateResult` 使用 `ResultTextBox` 样式，设置了 `IsReadOnly="True"` — 文本可选中可复制 ✅
- `TranslateRichResult` 使用 `FlatRichTextBox`，设置了 `IsReadOnly="True" IsDocumentEnabled="True"` — 文本可选中可复制 ✅
- `TranslationPanelWindow` 的 `TranslationTextBox` 同理 ✅

**结论**：正文选中复制功能正常，无需改动。问题只在术语芯片（T04 已修复）。

---

## 第四部分：全局视觉细节打磨

以下任务按优先级排列，每条独立可提交。

### T06 — 翻译浮窗源文本输入框占位文案补全

**文件**：`apps/PopGlot.Windows/TranslationPanelWindow.xaml` 第 179 行

**现状**：`local:Ui.Placeholder="输入文本，Enter 翻译"`
**建议**：`local:Ui.Placeholder="输入文本，Enter 翻译，Shift+Enter 换行"`

理由：代码中 `SourceInputBox_KeyDown` 已处理 Shift+Enter 换行，但占位文案未告知用户。运行时代码（第 665 行）确实设置了完整提示，但初始 XAML 缺少。

### T07 — 极速查词窗口搜索框占位文案对齐

**文件**：`apps/PopGlot.Windows/QuickSearchWindow.xaml` 第 48 行

**现状**：`local:Ui.Placeholder="搜索单词、句子或报错信息…"`
**无需改动**：文案清楚，与产品定位匹配 ✅

### T08 — 设置页翻译引擎列表空状态文案

**文件**：`apps/PopGlot.Windows/Sections/ServicesSection.xaml` 第 150 行

**现状**：`未配置翻译引擎。`
**建议**：改为 `尚未添加翻译引擎，点击右上角「添加引擎」开始。`

理由：给用户明确的下一步指引。

### T09 — 工作台空状态引导文案

**文件**：`apps/PopGlot.Windows/Sections/TranslateSection.xaml` 第 487 行

**现状**：`划词 Ctrl+Alt+W · 截图 Ctrl+Alt+Space`
**无需改动**：简洁明了，已告知两个核心入口快捷键 ✅

### T10 — 设置页编辑器滚动底部留白

**文件**：`apps/PopGlot.Windows/Sections/ServicesSection.xaml` 第 218 行

**现状**：`Padding="0,0,8,24"` — 底部 24px 留白
**无需改动**：已在代码中确认底栏不会遮挡最后一个字段 ✅

### T11 — ComboBox 可编辑模式左侧 Margin 统一

**文件**：`apps/PopGlot.Windows/Themes/Controls.xaml` 第 1185 行

**现状**：`PART_EditableTextBox` 的 `Margin="10,0,34,0"` — 左 10px
相比 `FormTextBox` 的 `Padding="12,0"` — 左 12px

**建议**：将 ComboBox 模板中 `PART_EditableTextBox` 的 Margin 改为 `Margin="12,0,34,0"`，与 TextBox 的 12px 左对齐一致。

**同步改动**：第 1190 行 `Placeholder` 的 `Margin="12,0,34,0"` 已经是 12 — 不需要动。第 1178 行 `ContentPresenter Selection` 的 `Margin="12,0,34,0"` 也已是 12。仅 PART_EditableTextBox 是 10。

### T12 — 翻译面板 Explanation 区域的「打开设置」按钮在成功时隐藏

**文件**：`apps/PopGlot.Windows/TranslationPanelWindow.xaml.cs`

**现状**：`ErrorSettingsButton.Visibility = Visibility.Visible` 只在错误态设置，但在成功完成渲染时（`HandleSessionResultAsync`）没有显式设置 `Collapsed`。

**排查**：查看第 1111 行 `ShowIfPresent(ExplanationText, session.Explanation, ...)` — 当 Explanation 不为空时显示 ExplanationBox。此时 `ErrorSettingsButton` 的状态取决于上一次渲染是否是错误态。

**建议**：在成功渲染路径中（约第 1106 行 `SetResultActionsEnabled(true)` 之后），加入：
```csharp
ErrorSettingsButton.Visibility = Visibility.Collapsed;
```

### T13 — 工作台翻译完成后补充说明区「打开设置」按钮状态

**文件**：`apps/PopGlot.Windows/Sections/TranslateSection.xaml.cs`

同理检查工作台的对应控件。搜索 `TranslateExplanationBox` 和其内部的按钮状态管理。

---

## 第五部分：代码健壮性

### T14 — TermChip_Click 剪贴板异常处理

**文件**：`apps/PopGlot.Windows/TranslationPanelWindow.xaml.cs` 第 1409 行

**现状**：使用 `TrySetClipboardAsync` — 已有异常处理 ✅ 无需改动。

### T15 — ModelCatalogService 对 LiteLLM /models 响应的兼容性

**文件**：`apps/PopGlot.Windows/Services/ModelCatalogService.cs`

**分析**：LiteLLM 的 `/models` 端点返回标准 OpenAI 格式：`{"data": [{"id": "model-name", ...}]}`。当前 `OpenAiCatalogAdapter.Parse` 解析 `data[].id` — **已兼容** ✅

### T16 — ProviderType 枚举序列化兼容

**分析**：`ProviderType.OpenAiCompatible` 已是 `#[default]`。LiteLLM 预设使用此值，无新枚举值，序列化/反序列化无影响 ✅

---

## 第六部分：文档与记录

### T17 — 更新 CHANGELOG.md

在当前 Unreleased 区段添加：
```
### 新增
- 翻译引擎预设新增 LiteLLM Proxy（统一代理接入）
- 工作台翻译结果的术语芯片支持点击复制

### 修复
- 自定义引擎表单在宽屏下内容区域过窄的问题
- ComboBox 可编辑模式文字左侧对齐与 TextBox 不一致
```

### T18 — 更新 docs/DESIGN_SYSTEM.md

如果 T01 新增了 LiteLLM 预设卡片，在 DESIGN_SYSTEM.md 的预设列表说明中补充。

---

## 执行顺序建议

```
优先级排序（每完成一条提交一次）：

1. T04 — 术语芯片不可点击（用户直接可感知的 bug）
2. T01 — LiteLLM 预设（用户明确要求的功能）
3. T02 — 表单布局空白（用户反馈的视觉问题）
4. T03 — 占位文案优化
5. T11 — ComboBox 左对齐
6. T06 — 浮窗占位文案补全
7. T08 — 空状态文案引导
8. T12 — ErrorSettingsButton 状态
9. T17 — CHANGELOG
10. 其余标记 ✅ 的条目跳过
```

---

## 附录 A：文件清单速查

| 文件路径 | 涉及任务 |
|---|---|
| `apps/PopGlot.Windows/Sections/ServicesSection.xaml` | T01, T02, T03, T08 |
| `apps/PopGlot.Windows/Sections/ServicesSection.xaml.cs` | T01, T02 |
| `apps/PopGlot.Windows/Sections/TranslateSection.xaml` | T04 |
| `apps/PopGlot.Windows/Sections/TranslateSection.xaml.cs` | T04 |
| `apps/PopGlot.Windows/TranslationPanelWindow.xaml` | T06 |
| `apps/PopGlot.Windows/TranslationPanelWindow.xaml.cs` | T12, T14 |
| `apps/PopGlot.Windows/Themes/Controls.xaml` | T11 |
| `apps/PopGlot.Windows/Services/ModelCatalogService.cs` | T15 |
| `crates/popglot-core/src/provider.rs` | 无改动（LiteLLM 走 OpenAI 兼容） |
| `crates/popglot-domain/src/lib.rs` | 无改动（不新增 ProviderType 枚举） |
| `CHANGELOG.md` | T17 |

## 附录 B：验证检查表

每条任务完成后执行：

- [ ] `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release` — 编译通过
- [ ] `cargo test --workspace` — 180 tests passed
- [ ] `dotnet build tests/PopGlot.PureTests/PopGlot.PureTests.csproj -c Release` — 编译通过
- [ ] PureTests 执行全绿
- [ ] git diff 确认只改了预期文件
- [ ] git commit 到 `polish/overnight-2026-09-23`

## 附录 C：不做的事

1. **不删除现有四套协议适配器** — 保留直连能力，LiteLLM 是新增预设
2. **不修改 Rust 侧任何代码** — LiteLLM 走 OpenAI 兼容，Rust 无需变化
3. **不修改主题 token** — 颜色和圆角沿用现有设计系统
4. **不合并到 main** — 等用户审核通过后再合并
5. **不修改已归档的 UI-REFACTOR 文档** — 那些是历史快照
6. **不发版本** — 打磨分支不触发 release 流程

---

# 执行报告（2026-09-23 夜间自动执行）

分支 `polish/overnight-2026-09-23`，共 10 个 commit。**每条改动提交前均验证：C# Release 编译 0 警告 0 错误；cargo test --workspace 210 passed / 0 failed；PureTests 26 passed / 0 failed（exit 0）。**

| 任务 | 状态 | Commit | 说明 |
|---|---|---|---|
| T04 | ✅ DONE | `11d02c1` | 工作台术语芯片 Border→Button（TokenChipButton 样式），新增 `TranslateTermChip_Click`，走 `Helpers.CopyToClipboardAsync`（与浮窗同一剪贴板通道），状态栏反馈成功/失败 |
| T01 | ✅ DONE | `a923ba1` | 新增 LiteLLM 预设卡片（`http://localhost:4000` + `/chat/completions`，OpenAI 兼容）。**执行中修正**：预设与自定义引擎一样显示「接口协议」「请求地址」并聚焦地址框（LiteLLM 代理常远程部署，地址是核心字段；且其代理层支持多家原生格式透传，协议下拉有意义），与 Ollama 隐藏地址的做法不同 |
| T02 | ✅ DONE | `e046de5` | `ConfigFormPanel` MaxWidth 760→900，宽窗两侧空白收窄 |
| T03 | ✅ DONE | `9c79918` | 名称占位「例如：公司内部代理」；地址占位加入 `http://localhost:4000` 示例 |
| T11 | ⚠️ DONE(修正) | `71b9090` | **文档前提有误**：`PART_EditableTextBox` 实际文字起点 = Margin 10 + Padding 2 = 12 DIP，与 Selection presenter（12）、TextBox（Padding 12）本已对齐。按文档改成 12 反而错位到 14。仅提交注释澄清 10+2=12 的对齐约束，数值未动 |
| T06 | ✅ DONE | `6318648` | 浮窗初始占位补「Shift+Enter 换行」（与第 665 行运行时提示一致） |
| T08 | ✅ DONE | `27088e0` | 空态文案改为给出「添加引擎」指引 |
| T12 | ✅ DONE(修正) | `1f6cb33` | **文档定位有误**：成功路径 `RenderFinalSuccessAsync` 第 1090 行已有 `Collapsed`。真实缺口在要点渲染 `PaintPanelSummary` — 此前失败残留的「打开设置」按钮会跟到要点说明旁。已在该路径补 `Collapsed` |
| T17 | ✅ DONE | `7efd0b2` | CHANGELOG 顶部新增「未发布」段（未发版，随下版本发布） |
| T05 | ✅ 已核 | — | 正文 `IsReadOnly` 可选中可复制，浮窗/工作台一致，无需改动 |
| T07 | ✅ 已核 | — | 极速查词占位无需改动 |
| T09 | ✅ 已核 | — | 工作台空态引导无需改动 |
| T10 | ✅ 已核 | — | 编辑器滚动底部留白 24 已正确 |
| T13 | ✅ 已核 | — | 工作台说明区无「打开设置」按钮（`OpenSettings_Click` 仅程序化调用），不存在残留问题 |
| T14 | ✅ 已核 | — | 浮窗 `TermChip_Click` 已有 `TrySetClipboardAsync` 异常处理 |
| T15 | ✅ 已核 | — | LiteLLM `/models` 返回标准 OpenAI 格式，现有 adapter 已兼容 |
| T16 | ✅ 已核 | — | 未新增枚举值，序列化无影响 |
| T18 | ⏭ N/A | — | `docs/DESIGN_SYSTEM.md` 无预设清单章节，无需补充 |

## 与文档的差异说明

1. **T01 超出文档范围**：文档只要求加预设按钮和 switch 分支；执行中发现沿用 Ollama 的可见性逻辑会隐藏代理地址字段，远程部署的 LiteLLM 用户将无法填地址。故 litellm 分支复用自定义引擎的「显示协议+地址」路径。这是行为差异点，用户审核时请重点看这一条。
2. **T11/T12 前提修正**见上表，改动以实际代码事实为准。
3. **任务文档勘误**：附录 A/B 中 PureTests 项目名应为 `PopGlot.Windows.PureTests.csproj`（文档写的 `PopGlot.PureTests.csproj` 不存在）。
4. **测试口径**：cargo 全套 11 个 suite，210 passed / 0 failed；PureTests 26/26。为诚实计数，未采用截断输出推算。

## 待用户审核

- [ ] T01 的 LiteLLM 预设显示地址字段的行为是否符合预期
- [ ] T02 表单 900 上限在最小窗宽 680 下的观感（compact 逻辑未动，理论上不受影响）
- [ ] 合并 `polish/overnight-2026-09-23` → main
