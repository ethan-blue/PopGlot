# P04 提示词模板管理页设计方案（设置·翻译偏好）

> **设计日期**：2026-09-14  
> **责任角色**：Pi（W20b 设计先行 / 前端 UI 设计）  
> **依据合同**：`docs/review-2026-09-12/PROMPT-PERSONALIZATION-AND-GLM-HANDOFF.md` §3、§4、§7 P04；关联 N12（设置保存语义）、UI04（设置服务隐私布局）  
> **依赖说明**：底层 Rust core 侧模板领域模型、四变量编译器与 FFI 接口由并行任务 W20a（`crates/popglot-core`）承接；本设计定义 WPF 展现层架构、交互状态机与 XAML 规格，供 W20b 直接实施。

---

## 1. 目标与非目标

### 1.1 目标
1. **不懂 Prompt 的用户也能用**：提供开箱即用的内置三风格卡片（忠实翻译、自然表达、商务邮件/正式书面），默认零配置直接使用。
2. **轻入口、深编辑**：在设置窗体中新增「翻译偏好」独立分区，提供标准左右双栏（宽屏）与前进后退（窄屏）的管理界面；杜绝在普通界面堆叠复杂 AI 调试控制台。
3. **白名单变量物理限制**：UI 变量插入辅助条在物理交互上只提供合同规定的 4 个白名单变量（`{{source_language}}`、`{{target_language}}`、`{{domain}}`、`{{audience}}`），输入期即时拦截未知变量与语法越界。
4. **安全编译零网络预览**：提供编译后 `system_instructions` 的只读预览，清晰标记变量占位符与不可编辑的系统保护规则；点击预览与切换模板绝对零网络请求（Zero-Network）。
5. **可取消且授权透明的试译**：试译是次要操作，明确标注目标引擎与可能计费；试译在途可随时取消，试译结果不污染正式历史。
6. **内置原件受保护**：内置三风格支持复制为个人模板，但原件不可修改、不可删除（`isBuiltIn == true` 强防护）。

### 1.2 非目标
1. **不开放原始系统协议覆盖**：不允许用户修改底层不可编辑的翻译协议、分隔符（Delimiter）或输出保护规则。
2. **不引入聊天机器人调试台**：不提供多轮对话历史回放或模型微调参数（Temperature/Top_p）控制。
3. **试译不触发自动复制/TTS**：试译仅用于验证模板效果，绝不触发外部副作用。

---

## 2. 设置窗口导航位次与系统集成

### 2.1 导航位次设计
依据合同 §3.1「管理入口位于设置的'翻译服务'邻近子页'翻译偏好'，不是新增顶级'AI 工作台'」，在 `SettingsWindow.xaml` 左侧导航栏中插入 `NavTemplates`：

```xml
<!-- SettingsWindow.xaml: Navigation items -->
<StackPanel Grid.Row="1" Margin="0,10,0,0">
    <TextBlock Text="偏好设置" Style="{StaticResource Metadata}"
               Foreground="{DynamicResource TextTertiaryBrush}"
               FontSize="11" Margin="10,0,0,8" />
    <RadioButton x:Name="NavProvider" Style="{StaticResource NavButton}"
                 GroupName="SettingsNav" Content="翻译引擎"
                 local:Ui.Icon="{StaticResource IconProvider}"
                 Checked="SubNav_Checked" Tag="Provider" />
    <!-- 新增：紧邻翻译引擎下方 -->
    <RadioButton x:Name="NavTemplates" Style="{StaticResource NavButton}"
                 GroupName="SettingsNav" Content="翻译偏好"
                 local:Ui.Icon="{StaticResource IconSparkles}"
                 Checked="SubNav_Checked" Tag="Templates" />
    <RadioButton x:Name="NavGeneral" Style="{StaticResource NavButton}"
                 GroupName="SettingsNav" Content="通用"
                 local:Ui.Icon="{StaticResource IconSettings}"
                 Checked="SubNav_Checked" Tag="General" />
    <RadioButton x:Name="NavShortcuts" Style="{StaticResource NavButton}"
                 GroupName="SettingsNav" Content="快捷键"
                 local:Ui.Icon="{StaticResource IconKeyboard}"
                 Checked="SubNav_Checked" Tag="Shortcuts" />
    <RadioButton x:Name="NavPrivacy" Style="{StaticResource NavButton}"
                 GroupName="SettingsNav" Content="隐私与数据"
                 local:Ui.Icon="{StaticResource IconCapture}"
                 Checked="SubNav_Checked" Tag="Privacy" />
</StackPanel>
```

### 2.2 窗体宿主与生命周期集成
1. **宿主注入**：在 `SettingsWindow.xaml` 的 `Grid.Row="1"` 内容区添加 `<sections:TemplatesSection x:Name="TemplatesSection" Visibility="Collapsed" Margin="28,14,32,16" />`。
2. **底栏保存条协同（引用 W5 SS-12 规则）**：
   - 类似 `ServicesSection` 的编辑器模式，当用户在 `TemplatesSection` 处于模板深度编辑状态时，宿主底栏的保存条隐藏，由 `TemplatesSection` 自身的独立保存条接管，杜绝同屏双保存条；
   - 离开分区时触发未保存草稿守卫：若 `TemplatesSection.IsDirty == true`，弹出统一守卫对话框（「保存修改」、「放弃修改」、「留在页面」）。

---

## 3. 页面布局与线框图（ASCII Wireframe）

### 3.1 宽屏标准布局（Client Width ≥ 720 DIP）
采用「左侧模板目录 + 右侧编辑/预览区」经典 Master-Detail 分栏模式：

```text
+---------------------------------------------------------------------------------------------------------+
| 设置 · 翻译偏好                                                                      [—] [口] [X] |
+-------------------+-------------------------------------------------------------------------------------+
| 偏好设置          | 翻译偏好模板                                           [+ 新建模板] [导入] [导出]   |
|   翻译引擎        +-------------------------------------------------------------------------------------+
| > 翻译偏好 (✨)   | [内置风格] (只读，可复制)                                                           |
|   通用            | +-------------------+ +-------------------+ +-------------------+                   |
|   快捷键          | | ✨ 忠实翻译       | | 💬 自然表达       | | 💼 商务邮件       |                   |
|   隐私与数据      | | 原文直译/保护代码 | | 口语润色/流畅地道 | | 正式严谨/礼貌得体 |                   |
|                   | +-------------------+ +-------------------+ +-------------------+                   |
|                   |                                                                                     |
|                   | [自定义模板] (3/50)                     | 编辑模板: 技术文档精修        [复制] [删除]   |
|                   | +-------------------------------------+ | 模板名称 *                                |
|                   | | * 技术文档精修 (选定中)             | | [ 技术文档精修                        ] |
|                   | |   专业术语一致，保留代码与参数      | | 适用领域 {{domain}} (可选)              |
|                   | +-------------------------------------+ | [ 计算机软件 / 云原生架构              ] |
|                   | |   学术论文阅读                      | | 目标受众 {{audience}} (可选)            |
|                   | |   学术规范，句式紧凑                | | [ 研发工程师 / 架构师                 ] |
|                   | +-------------------------------------+ | 简短说明 (可选)                           |
|                   | |   社交媒体简报                      | | [ 用于 API 文档与技术规范翻译         ] |
|                   | |   短句精炼，吸引眼球                | | 偏好提示词正文 * (UTF-8: 320/8192 B)    |
|                   | +-------------------------------------+ | +---------------------------------------+ |
|                   |                                         | | 适用领域：{{domain}}。面向{{audience}}| |
|                   |                                         | | 严格保持专业术语一致；代码、命令、    | |
|                   |                                         | | 路径、URL 必须逐字符保留。            | |
|                   |                                         | +---------------------------------------+ |
|                   |                                         | 变量插入: [{{source_language}}]           |
|                   |                                         |           [{{target_language}}]           |
|                   |                                         |           [{{domain}}] [{{audience}}]     |
|                   |                                         | ----------------------------------------- |
|                   |                                         | v 安全编译预览 (零网络纯本地计算)         |
|                   |                                         | +---------------------------------------+ |
|                   |                                         | | [系统级安全与协议层 (只读)]           | |
|                   |                                         | | [用户偏好层: 适用领域: 计算机软件...] | |
|                   |                                         | +---------------------------------------+ |
|                   |                                         | > 可取消试译 (当前引擎: DeepSeek-V3)      |
|                   |                                         | ----------------------------------------- |
|                   |                                         | [未保存草稿]              [放弃] [保存模板] |
+-------------------+-----------------------------------------+-------------------------------------------+
| (o) 就绪                                                                                                |
+---------------------------------------------------------------------------------------------------------+
```

### 3.2 窄屏折叠模式（Client Width < 720 DIP，遵循 W7/W13 响应式规范）
当窗口宽度缩窄时，自动折叠为单列栈式布局：
1. 默认展示模板列表视图（内置卡片 + 自定义列表 + 顶部新建/导入操作栏）；
2. 点击任一模板或「新建模板」后，列表滑出，编辑器平滑切入，顶部显示 `[< 返回列表]` 导航按钮；
3. 编辑器内部所有双列表单项自动折叠为纵向单列（标签在上，输入框占满行宽）。

---

## 4. 编辑器组件规范与字段约束

### 4.1 字段规格表

| 字段名称 | XAML 控件类型 | 约束规则 | 超限与校验反馈 |
|---|---|---|---|
| **模板名称** (`name`) | `TextBox` (Style=`EditorTextField`) | 必填；1~64 Unicode 标量值；不可全空格 | 红色即时报错：「请输入模板名称（最多 64 字）」；保存按钮禁用 |
| **适用领域** (`domain`) | `TextBox` (Style=`EditorTextField`) | 可选；0~256 标量值且 ≤ 1KiB UTF-8 | 计数指示器；超限截断并提示 |
| **目标受众** (`audience`) | `TextBox` (Style=`EditorTextField`) | 可选；0~256 标量值且 ≤ 1KiB UTF-8 | 计数指示器；超限截断并提示 |
| **简短说明** (`description`)| `TextBox` (Style=`EditorTextField`) | 可选；0~256 标量值 | 计数指示器；超过 256 字符即时告警 |
| **偏好正文** (`instruction`)| `TextBox` (Style=`EditorTextAreaField`) | 必填；AcceptsReturn=True；≤ 8KiB (8192 字节) UTF-8；Enter 仅换行 | 右下角字节计数 `当前/8192 B`；超限标红并阻止保存 |

### 4.2 白名单变量插入辅助条（物理级限制）
为杜绝用户误写无效变量或尝试注入系统级参数，在偏好正文下方提供专用辅助工具条：

```xml
<StackPanel Orientation="Horizontal" Margin="0,6,0,0">
    <TextBlock Text="插入变量：" Style="{StaticResource Caption}" VerticalAlignment="Center" Margin="0,0,6,0" />
    <Button Content="{{source_language}}" Style="{StaticResource TokenChipButton}" Margin="0,0,6,0"
            ToolTip="源语言名称（自动识别未定时输出'自动识别'）" Click="InsertVariable_Click" Tag="{{source_language}}" />
    <Button Content="{{target_language}}" Style="{StaticResource TokenChipButton}" Margin="0,0,6,0"
            ToolTip="目标语言名称（根据实际翻译目标动态填入）" Click="InsertVariable_Click" Tag="{{target_language}}" />
    <Button Content="{{domain}}" Style="{StaticResource TokenChipButton}" Margin="0,0,6,0"
            ToolTip="适用领域（引用上方领域字段填入内容）" Click="InsertVariable_Click" Tag="{{domain}}" />
    <Button Content="{{audience}}" Style="{StaticResource TokenChipButton}" Margin="0,0,6,0"
            ToolTip="目标受众（引用上方受众字段填入内容）" Click="InsertVariable_Click" Tag="{{audience}}" />
</StackPanel>
```

**交互与解析规则**：
1. **光标处插入**：点击变量芯片按钮，向 `InstructionTextBox` 当前光标位置插入对应文本，并将焦点归还文本框；
2. **非法变量实时检测**：监听 `TextChanged` 事件，使用纯本地正则匹配所有形如 `(?<!\\)\{\{([^}]+)\}\}` 的插值；若存在不在白名单中的变量（如 `{{source_text}}`、`{{api_key}}` 等），正文下方即时渲染 Danger 提示：「存在未知变量 '{{...}}'。仅支持 4 个标准变量，保存已阻止。」同时禁用保存按钮；
3. **转义支持**：允许使用 `\{{` 表达字面含义的 `{{`。

---

## 5. 本地安全编译预览（Zero-Network）

### 5.1 预览区结构
预览区默认折叠为一个 `Expander`，展开时由本地 Rust core 编译器即时输出预览结果，**严禁触发任何网络出网**：

```text
+-----------------------------------------------------------------------------------+
| v 编译预览 (本地纯计算 · 零网络请求 · 变量以示例值展示)                            |
+-----------------------------------------------------------------------------------+
| [分层 1: 系统核心翻译协议 (不可修改)]                                             |
| You are a professional translator. Strictly output translation only.             |
| Preserve all protected tokens ([P_0], [P_1]), code blocks, and markdown syntax.   |
|                                                                                   |
| [分层 2: 用户个性化偏好层 (已安全注入)]                                           |
| <user_preference>                                                                 |
| 适用领域：计算机软件。面向：研发工程师。                                          |
| 严格保持专业术语一致；代码、命令、路径、URL 必须逐字符保留。                      |
| </user_preference>                                                                |
+-----------------------------------------------------------------------------------+
```

### 5.2 安全与隐私边界
1. **无源文混入**：预览仅展示针对系统指令（System Instructions）的编译产物，待翻译源文本决不内插至 System Prompt；
2. **占位符规则**：预览时，`{{source_language}}` 默认以「英语 (示例)」展示，`{{target_language}}` 以「简体中文 (示例)」展示；
3. **Delimiter 防护**：预览区展示固定的 `<user_preference>` 结构标签，不泄漏请求级别的动态随机 Delimiter 令牌。

---

## 6. 可取消试译交互设计

### 6.1 交互流程与透明度保障
试译是验证提示词效果的次要操作，不可作为模板保存的前提条件。

```text
[折叠面板] > 效果试译 (验证当前偏好)
  +---------------------------------------------------------------------------------+
  | 试译说明：将向当前默认引擎 [DeepSeek-V3] 发送一段技术报错示例文本及此偏好。    |
  | 费用透明：本次调用将通过服务商真实接口进行，可能产生少许 Token 计费。          |
  | 示例文本：[ TypeError: Cannot read properties of undefined (reading 'map')    ] |
  |                                                                                 |
  | [开始试译] (GhostButton)                                                         |
  +---------------------------------------------------------------------------------+
```

### 6.2 试译中与试译后状态机
1. **点击「开始试译」**：
   - 检查是否有可用且已验证的翻译引擎（若无，就地提示「未配置可用引擎，请先前往【翻译引擎】完成配置」，提供跳转超链接，不禁用保存）；
   - 按钮状态切换为「取消试译」（前景色切换为 DangerBrush），右侧展示环形进度条或 `正在向 DeepSeek-V3 发送试译请求…`；
   - 内部创建独立的 `CancellationTokenSource`，超时设定为 15 秒；
2. **点击「取消试译」**：
   - 触发 CTS 取消，中止后台流式传输，状态恢复就绪，提示「试译已取消」；
3. **试译成功**：
   - 下方展开结果展示框（卡片背景 `SurfaceMutedBrush`，边框 `BorderSubtleBrush`）；
   - 顶部显著徽章：`[试译验证结果 · 针对草稿版本]`；
   - 底部副文本：`* 试译结果不计入资料库历史记录，不触发自动复制与语音朗读`；
4. **试译后用户再次修改正文**：
   - 监听文本变更，试译结果框顶部徽章即时变为 Warning 色：「[结果基于修改前的旧版本，请重新试译]」，杜绝误将旧验证套用于新文本。

---

## 7. 内置三风格防护机制

### 7.1 内置项特征
系统出厂预置 3 个基线模板：
1. `built-in-faithful`（忠实翻译）：强调原文真实还原、否定句与条件句严密性；
2. `built-in-natural`（日常自然表达）：强调中文母语习惯润色、地道流畅；
3. `built-in-formal`（商务与技术）：强调术语一致性、专业严谨、规范礼貌。

### 7.2 强保护规则（不可篡改、不可删除）
- `isBuiltIn == true` 时：
  - 顶部显示锁定提示条：「系统内置基线模板（只读保护）」；
  - 所有输入文本框置为只读模式（`IsReadOnly="True"`，底色保持 `SurfaceMutedBrush`）；
  - 隐藏「保存」与「删除」按钮；
  - 突出显示「复制为个人模板」主按钮（`Style="{StaticResource PrimaryButton}"`）；
  - 点击后以该内置模板内容为基础新建一份自定义模板草稿，名称自动后缀「(副本)」，用户可自由编辑并保存。

---

## 8. 控件清单与 Design Token 映射表

| UI 组件/区域 | XAML 控件 | 样式/资源引用 | 语义与对比度规范 |
|---|---|---|---|
| 分区主标题 | `TextBlock` | `Style="{StaticResource PageTitle}"` | 字号 20px，SemiBold，Foreground=`TextPrimaryBrush` (≥4.5:1) |
| 模板列表项容器 | `ListBoxItem` | `Style="{StaticResource ServiceListItem}"` | 悬停 `SurfaceHoverBrush`，选中 `AccentBorderBrush` + `SurfaceMutedBrush` |
| 内置风格卡片 | `Border` | `CornerRadius="{DynamicResource CardRadius}"` (10px) | 背景 `SurfaceBrush`，边框 `BorderSubtleBrush` |
| 变量插入芯片 | `Button` | `Style="{StaticResource TokenChipButton}"` | 悬停高亮，Padding 8,4，圆角 6px，触控热区达标 |
| 多行偏好文本框 | `TextBox` | `Style="{StaticResource EditorTextAreaField}"` | 背景 `InputBrush`，边框悬停 `BorderStrongBrush`，字体 `UiFontFamily` |
| 试译结果卡片 | `Border` | 背景 `SurfaceMutedBrush`，圆角 6px | 内部文本 `ResultTextBox` 只读渲染 |
| 保存底栏守卫条 | `Border` | 背景 `WarningSoftBrush`，边框 `WarningBrush` | 提示未保存草稿状态，与 `ServicesSection` 守卫保持完全一致 |
| 主要保存操作 | `Button` | `Style="{StaticResource PrimaryButton}"` | 背景 `PrimaryBrush`，文字 `PrimaryTextBrush` (≥4.5:1) |
| 次要操作/取消 | `Button` | `Style="{StaticResource GhostButton}"` | 透明底，悬停 `SurfaceHoverBrush` |
| 危险操作/删除 | `Button` | `Style="{StaticResource DangerGhostButton}"` | 文字 `DangerBrush`，悬停 `DangerHoverBrush` |

---

## 9. 交互状态机（State Machine）

```text
               +---------------------------------------------+
               |                  Loading                    |
               +---------------------------------------------+
                                      |
                                      v
               +---------------------------------------------+
               |          Clean (已保存 / 基线一致)          | <---------------+
               +---------------------------------------------+                 |
                 |                         |                                   |
       用户编辑文本 / 变量插入          选择其他模板                           |
                 |                         |                                   |
                 v                         v                                   |
   +---------------------------+   +-------------------------------+           |
   | Dirty (草稿未保存)        |   | 切换至选定模板                |           |
   +---------------------------+   +-------------------------------+           |
     |            |                                                            |
 编译失败     点击保存                                                         |
     |            |                                                            |
     v            v                                                            |
+---------+  +--------------------------+                                      |
| Invalid |  | Saving (异步持久化中)    |                                      |
| (阻止)  |  +--------------------------+                                      |
+---------+   | 成功                         | 失败                            |
              v                              v                                 |
         +---------+           +--------------------------+                    |
         |  Clean  |           | SaveError (弹窗保留草稿) | -------------------+
         +---------+           +--------------------------+ (用户修正后重试)
```

---

## 10. 验收清单（Definition of Done）

1. [ ] **导航与页面归属**：设置窗口侧栏在「翻译引擎」下方精准呈现「翻译偏好」（✨ 图标），点击平滑切换至模板管理视图。
2. [ ] **内置模板只读防护**：进入内置忠实/自然/商务模板时，正文只读，不可删除，提供「复制为个人模板」操作且复制后可正常编辑。
3. [ ] **四变量白名单硬拦截**：
   - 仅提供且允许 `{{source_language}}`、`{{target_language}}`、`{{domain}}`、`{{audience}}` 四个标准变量；
   - 手工输入未知变量（如 `{{foo}}`）时，即时显式报错并锁定保存操作。
4. [ ] **Enter 键换行防误触**：正文编辑框按下 `Enter` 仅产生换行符，绝不触发出网提交或保存。
5. [ ] **零网络安全预览**：展开「编译预览」时，即时在本地完成模板拼装并渲染，抓包确认无任何网络请求产生。
6. [ ] **可取消试译透明度**：
   - 试译时明确显示发送目的地与计费预警；
   - 试译请求在途可随时点击「取消」立即中断连接；
   - 试译结果绝不写入 `HistoryStore`，不改变生词本与 TTS 状态；
   - 试译后修改正文，旧结果立即标为「旧草稿过期结果」。
7. [ ] **草稿守卫一致性**：存在未保存修改时切换设置页或关闭设置窗，弹出统一保存/放弃守卫，杜绝草稿静默丢失。
8. [ ] **全主题对比度与高分屏**：Dark/Light 主题往返切换及 125%/150%/200% DPI 下，字号、行距、边框均符合 WCAG AA 标尺。
