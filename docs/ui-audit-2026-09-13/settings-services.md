# PopGlot UI 专项审计报告：设置窗体 + 服务/翻译/通用分区

> 审计日期：2026-09-13
> 审计人：OpenCode (Teammate)
> 审计范围：`apps/PopGlot.Windows/SettingsWindow.xaml(.cs)`、`Sections/ServicesSection.xaml(.cs)`、`Sections/TranslateSection.xaml(.cs)`、`Sections/GeneralSection.xaml(.cs)`
> 审计基线：`docs/review-2026-09-12/README.md`（重点 G03/G07/G08/G09）、`docs/review-2026-09-12/PRODUCT-EXPANSION-REVIEW-V2.md` 第 10–15 节、`docs/DESIGN_SYSTEM.md` (v0.1.3+)、`docs/UI-REVIEW-2026-09-03.md`
> 审计原则：只读审查，基于当前工作树真实代码，逐一定位用户点名缺陷与规格落差。

---

## 目录
1. [审计概述与统计](#一审计概述与统计)
2. [重点专题一：未添加模型时的空态、假按钮与凭据验证专项定位](#二重点专题一未添加模型时的空态假按钮与凭据验证专项定位)
3. [重点专题二：可交互元素的点击暗示与操作反馈完整性](#三重点专题二可交互元素的点击暗示与操作反馈完整性)
4. [重点专题三：设置保存语义与 G08 规范一致性审查](#四重点专题三设置保存语义与-g08-规范一致性审查)
5. [重点专题四：设计令牌（Token）与硬编码/排版网格差距](#五重点专题四设计令牌token与硬编码排版网格差距)
6. [重点专题五：控件对齐、响应式断点与专有名词一致性](#六重点专题五控件对齐响应式断点与专有名词一致性)
7. [UI 审计缺陷条目清单（ID / 级别 / 文件:行 / 现象 / 规格条款 / 最小修复建议）](#七ui-审计缺陷条目清单)
8. [Top 5 核心修复优先级排序](#八top-5-核心修复优先级排序)

---

## 一、审计概述与统计

本次审计针对 PopGlot Windows 桌面端的核心设置窗口（`SettingsWindow`）、服务与模型管理分区（`ServicesSection`）、翻译工作台主分区（`TranslateSection`）以及通用偏好分区（`GeneralSection`）进行了全面深度的只读源码审计。

### 发现统计
- **缺陷总数**：20 项
- **P0 严重级别**：5 项（严重误导用户/假按钮无响应/空态诱导报错/首存服务逻辑破坏/主次按钮混淆）
- **P1 次级重要**：8 项（点击反馈缺失/双重保存冲突/守卫按钮堆叠/交互暗示不足/小屏断点溢出）
- **P2 一致性/细节**：7 项（网格脱轨/字号散落/圆角不一/专有名词打架/无用面板常驻）

---

## 二、重点专题一：未添加模型时的空态、假按钮与凭据验证专项定位

用户重点反映：“**未添加模型时，有看起来像点击按钮但实际不是的元素**”。本节进行逐一源码命中与机理剖析：

### 2.1 命中一：“假按钮”根因 —— 模型推荐证据徽章（`EvidenceBadge`）
- **定位代码**：`apps/PopGlot.Windows/Sections/ServicesSection.xaml:421–428, 438–445`；`ServicesSection.xaml.cs:1366–1373, 1440–1452`
- **视觉现象**：
  在「文字模型」与「图片模型」输入框下方，存在推荐原因行。左侧是一个带有圆角边框的徽章（`TextEvidenceBadge` / `VisionEvidenceBadge`），文字呈现为「官方声明」、「系列推断」、「本机实测」或「未声明」。
  其 XAML 样式为：
  ```xaml
  <Style x:Key="EvidenceBadge" TargetType="Border">
      <Setter Property="CornerRadius" Value="4" />
      <Setter Property="Padding" Value="5,2" />
      <Setter Property="Margin" Value="0,0,6,0" />
      <Setter Property="BorderThickness" Value="1" />
      <Setter Property="VerticalAlignment" Value="Center" />
  </Style>
  ```
  在 C# 逻辑中，当为官方声明时，赋予 `AccentSoftBrush` 背景、`AccentBorderBrush` 边框、`AccentBrush` 紫蓝色粗体文字。
- **为何用户认为它是按钮**：
  1. **与真按钮高度雷同**：紧贴其上方的模型推荐选项是一个个真正的按钮（`ModelChipButton`，圆角 6，内边距 8,4，浅色背景带边框）。`EvidenceBadge` 与之处于同一视觉区域，长相均为药丸形胶囊卡片。
  2. **视觉层级反客为主**：`EvidenceBadge` 采用了品牌强调色 `AccentSoftBrush` + `AccentBorderBrush`，而上方的真实可点击芯片 `ModelChipButton` 仅使用了中性的 `SurfaceRaisedBrush`。导致**不可点击的徽章比真正可点击的按钮还要醒目、更具操作暗示**！
  3. **用户心理预期**：用户会误以为这是一个可以点击切换偏好类型、过滤模型、或者点击查看官方技术文档引用的交互 Tag。
- **实际行为**：它是一个完全静态的 `Border`，没有 `Cursor="Hand"`，没有 Hover 高亮，没有 Click 处理器。用户点击时如同死机，毫无任何响应。

### 2.2 命中二：“假按钮”次因 —— 预设卡片内的药丸徽章（`StatusPill`）与默认徽章
- **定位代码**：`ServicesSection.xaml:291–294`（自建服务卡片内的「自建或第三方服务」）；`ServicesSection.xaml:228–231`（服务列表条目内的「文字默认」）。
- **视觉现象**：
  在「自定义兼容服务」预设大卡片内，嵌套了一个带有 `Style="{StaticResource StatusPill}"`、背景为 `AccentBrush` 实色的药丸胶囊。该药丸外观如同独立小按钮，用户容易尝试单独点击该胶囊而非卡片整体。

### 2.3 核心疑问定性：获取模型 / 验证连接 / 试译在无凭据时的真实状态

| 动作按钮 | 文件与行号 | 无凭据时状态 | 点击后实际行为与反馈 | 规范要求与定性 |
| :--- | :--- | :--- | :--- | :--- |
| **获取模型**<br>(`FetchModelsButton`) | `ServicesSection.xaml:389`<br>`ServicesSection.xaml.cs:1135` | **未禁用、未隐藏**<br>（`IsEnabled=true`） | 没有任何前置拦截，直接带空 Key 发起真实 HTTP 网络请求至供应商（OpenAI/Gemini/DeepSeek 等），等待至多 15 秒超时或 401 鉴权拒绝，在下方展示红字报错：`密钥无效或没有权限（HTTP 401），无法读取模型列表。`若 Base URL 为空则抛出格式异常。 | **严重违规 (P0)**：违反 V2 §13 与 README G09。缺少凭据时必须置灰禁用并附 ToolTip，或在点击时直接阻断网络出网、光标聚焦 API Key 输入框引导用户输入。 |
| **验证连接**<br>(`TestConnectionButton`) | `ServicesSection.xaml:369`<br>`ServicesSection.xaml.cs:1563` | **未禁用、未隐藏**<br>**违规使用实色主按钮**<br>（`Style="{StaticResource PrimaryButton}"`） | 按钮不仅完全可点，而且使用 PrimaryButton 强势抢占全屏视觉焦点。点击后在本地抛出异常，下方亮起红点（`DangerBrush`）并显示红色错误：`连接失败 / 请先填写 API Key（不会被保存），或改用本地模型地址。（设置未被修改）` | **严重违规 (P0)**：违反 V2 §10.2 U03（验证连接降为次要，突出保存）及调色盘合同。将“未输入凭据”的表单校验包装成“连接失败”，给用户造成严重的网络/服务不可用恐慌。 |
| **试译按钮**<br>(`TranslateButton` / 填入示例) | `TranslateSection.xaml:28, 390`<br>`TranslateSection.xaml.cs:495` | **代码中未实现独立「试译」按钮**<br>工作台「翻译」按钮**未禁用** | 用户在无模型时若想“试译”，会点击空态中的「填入示例」并点「翻译」。由于无可用模型且未允许免费引擎，请求直接受阻并报错：`未允许出网翻译。可在设置中配置自己的模型服务。`但界面上**没有任何可点击跳转至设置页的修复入口**。 | **严重违规 (P0)**：违反 README G09（无服务不伪装就绪）与 V2 §10.1（错误有修复路径）。状态栏谎称“等待输入/就绪”，用户操作后直接撞车报错且无引导。 |

---

## 三、重点专题二：可交互元素的点击暗示与操作反馈完整性

### 3.1 鼠标指针（Cursor）缺失
1. **服务列表项 (`ServiceListItem`)**：
   - 样式定义在 `ServicesSection.xaml:13–48`，未设置 `Cursor="Hand"`。
   - 用户鼠标移入卡片虽然有 `SurfaceHoverBrush` 背景淡入，但鼠标指针依然保持文本或箭头光标，用户无法一眼看出整行卡片均支持点击切换查看/编辑。
2. **通用分区「重新启用」按钮 (`StartupRepairButton`)**：
   - `GeneralSection.xaml:47` 遗漏了 `Style` 属性，光标保持默认箭头，点击暗示不足。

### 3.2 点击后状态持久化反馈缺失
1. **生词本收藏按钮 (`TranslateStarButton`)**：
   - `TranslateSection.xaml:453–461`；`TranslateSection.xaml.cs:789–810`
   - 用户点击收藏后，虽然执行了 `_vocabulary.ToggleStar` 且在底栏文字输出「已加入生词本」，但按钮内部图标（五角星 Path）**没有任何持久的高亮或实心填充改变**！
   - 违反 V2 §13 核心条款：“**收藏/固定：图标轮廓与填充或文字共同反馈，选中持续可见，写盘成功后收藏才点亮**”。用户移开视线后，完全无法判断该内容是否已被收藏。
2. **语音朗读播放状态 (`TranslateSourceSpeakButton` / `TranslateResultSpeakButton`)**：
   - `TranslateSection.xaml.cs:775–783`
   - TTS 正在发声时，按钮图标保持静态扬声器，Tooltip 保持“朗读”，没有任何正在播放的声波脉冲反馈或停止按钮切换。

### 3.3 破坏性/清理操作的双步确认与虚假操作
1. **清空无内容的 API Key (`ClearKeyButton`)**：
   - 按钮接入了 `ConfirmButton`（两步确认），但当当前服务从未保存过 Key 且输入框为空时，按钮依然可用。
   - 用户点击后变为“确认清除？”，再次点击后向底栏通报“该服务的 API Key 已清除”，形成虚假成功反馈。

---

## 四、重点专题三：设置保存语义与 G08 规范一致性审查

### 4.1 窗体级与分区级“双重保存体系”割裂
PopGlot 当前在 `SettingsWindow` 采用了双层脱节的保存体系：
1. **窗体底栏保存条 (`SettingsWindow.xaml:112–148`)**：
   - 托管 `GeneralSection`、`ShortcutsSection`、`DataSection` 以及全局网络/模式策略。
   - 状态由快照比对（`_settingsBaseline`）严格驱动（`Clean` / `Dirty` / `Saving`），只有存在实际改动时才展示保存条，支持放弃修改回滚。
2. **服务分区内部独立保存条 (`ServicesSection.xaml:530–543`)**：
   - 托管单一模型的编辑，拥有独立的 `SaveServiceButton`、`CancelEditButton`、`DeleteServiceButton` 以及 `EditorDirtyBadge`。

**冲突场景**：
- 用户在「通用」修改了深色主题（未保存，底栏浮起「未保存的修改」和「保存」）。
- 用户切换到「翻译引擎」新建或编辑一个服务（内部出现「保存服务」）。
- 此时屏幕底部和编辑卡片底部**同时出现两个「未保存的修改」徽章和两个「保存」按钮**！用户完全无法分辨点击哪一个保存按钮会持久化哪一部分数据。

### 4.2 首个服务无模型强制生效破坏主流程 (严重逻辑漏洞)
- `ServicesSection.xaml.cs:1948–1970`：
  ```csharp
  var isFirstService = config.Profiles.Count == 0;
  // ...
  if (isFirstService || wasActive)
  {
      config.ActiveProfileId = draft.Id;
      config.PreferFreeEngine = false;
  }
  ```
- **问题**：`TrySaveService()` **完全没有校验 TextModel 是否为空**。
- 若用户添加第一个服务时未填模型直接点保存，该配置被直接写入 `config.ActiveProfileId`（成为默认文字服务）。
- 然而在 `RefreshDefaultCombos()` 中，默认文字服务下拉框只加载 `profile.SupportsText` 为 true 的服务；由于模型为空，`SupportsText = false`，导致默认服务下拉框中**该服务被过滤掉，选中项直接变为空**！
- 此时回到主工作台发起翻译，系统由于默认引擎缺少模型而直接崩溃报错。这严重破坏了 G08 条款中“默认服务需经过完整前置检查”的核心原则。

### 4.3 守卫警告条与操作条垂直堆叠双胞胎
- `ServicesSection.xaml:515–543`：
  当编辑器处于脏状态且用户点击切页时，`DraftGuardBar` 展开（显示：取消、放弃修改、保存并继续）；但其下方的 `EditorActionBar` 依然处于 Visible 状态（显示：取消、保存）。
  界面同一列同时出现**两个「取消」按钮**和**两个「保存」按钮**，交互层级产生严重干扰。

---

## 五、重点专题四：设计令牌（Token）与硬编码/排版网格差距

### 5.1 颜色 Token 引用现状
经全文扫描，4 个文件内的画刷与颜色**全部正确采用了 DynamicResource / StaticResource**，未发现裸十六进制 HEX（`#RRGGBB`）硬编码，主题适配底座良好。

### 5.2 字号阶梯（Typography Hierarchy）散落与断层

| 出现位置 | 当前代码 | 规范标准 (DESIGN_SYSTEM §2.2) | 缺陷分析 |
| :--- | :--- | :--- | :--- |
| `SettingsWindow.xaml:37` | `PageTitle FontSize="15"` | `PageTitle`: **20 DIP**, SemiBold | 大标题被局部硬编码强制缩小至 15 DIP，失去视线锚点 |
| `TranslateSection.xaml:16`| `PageTitle FontSize="15"` | `PageTitle`: **20 DIP**, SemiBold | 工作台大标题同样被局部缩小至 15 DIP |
| `ServicesSection.xaml:148`| `FontSize="12.5"` | `SectionTitle`: 13 DIP / `Caption`: 12 DIP | 12.5 DIP 属于 Kbd/Token 专用刻度，用在标题上脱轨 |
| `ServicesSection.xaml:198`| `FontSize="14"` | `SectionTitle`: 13 DIP / `H2`: 13 DIP | 14 DIP 非系统字号刻度 |
| `ServicesSection.xaml:289`| `FontSize="14"` | `SectionTitle`: 13 DIP / `RowTitle`: 13 DIP | 14 DIP 非系统字号刻度 |
| `ServicesSection.xaml:304`| `FontSize="12"` | `SectionTitle`: 13 DIP | 分组小标题字号偏小，弱于 RowTitle |
| `ServicesSection.xaml:521`| `FontSize="12.5"` | `Caption`: 12 DIP / `Body`: 13 DIP | 草稿守卫文字刻度脱轨 |

### 5.3 间距网格（Spacing Grid）脱离 4px/8px 标尺
`ServicesSection.xaml` 存在大量随意的奇数与魔法数值内边距：
- `Padding="13,11"`（预设卡片、列表行容器）—— 13 和 11 均为奇数；
- `Padding="11,0"`（输入框、密码框内边距）；
- `Padding="5,2"`（推荐证据徽章）；
- `Padding="17,7"`、`Padding="11,7"`、`Padding="13,7"`、`Padding="14,6"`、`Padding="11,5"`（各操作按钮）；
- `Margin="0,17,0,16"`、`Margin="7,0,0,0"`、`Margin="16,3,0,0"`（分割线与状态指示点）；
- `Width="7" Height="7" CornerRadius="3.5"`（状态指示点 StatusDot，半像素圆角）。

这些数值脱离了 `DESIGN_SYSTEM.md` §5.2 的 `4 / 8 / 12 / 16 / 20 / 24 / 28 / 32` 标准梯级。

---

## 六、重点专题五：控件对齐、响应式断点与专有名词一致性

### 6.1 桌面最小尺寸与高 DPI 适配风险
- **`SettingsWindow.xaml:6`**：硬编码 `MinWidth="820" MinHeight="600"`。
- **风险**：
  在主流笔记本（1080p 分辨率）开启 150% 推荐缩放时，实际屏幕客户区高度通常仅约 `720 DIP`（扣除 Windows 任务栏后约 `680 DIP`）；若开启 175% 缩放，可用高度不足 `600 DIP`。
  设置窗口固定最小高度 600 DIP，导致在小屏幕或高缩放环境下，**底部的状态栏与「保存」按钮直接被推到屏幕可视范围之外**，用户根本无法完成保存操作！
  直接违反 `PRODUCT-EXPANSION-REVIEW-V2.md` §10.2 条款 **U02**（设置窗口必须做低高度规则与紧凑响应模式）。

### 6.2 触控与点击热区不达标
- **`TranslateSection.xaml` 中的 7 个功能图标按钮**：
  朗读、复制、合并断行、对调语言、朗读译文、复制译文、收藏生词，全部硬编码 `Width="28" Height="28"`。
  违反 V2 §11.1 与 README G07 的强制门槛：“**关键按钮至少 32 DIP，覆盖旧规则 28 DIP 例外**”。

### 6.3 概念与专有名词混乱冲突
同一业务对象在 4 个文件中交替使用两个不同词汇：
- **“翻译引擎” vs “模型服务”**：
  - 左侧导航按钮：`Content="翻译引擎"`
  - 服务页面顶栏：`TextBlock Text="翻译引擎"`、`Button "添加引擎"`
  - 列表空态：`TextBlock "未配置翻译引擎"`、`Button "添加第一个引擎"`
  - 编辑器返回：`Button "← 返回引擎列表"`
  - 编辑器底部：`Button "删除引擎"`
  - 代码状态消息：`"模型服务已更新，即时生效。"`（`SettingsWindow.xaml.cs:83`）
  - 保存按钮文案：`"保存服务"`（`ServicesSection.xaml.cs:886`）
  - 清理 Key 消息：`"该服务的 API Key 已清除"`（`ServicesSection.xaml.cs:1708`）
  - 新增拦截消息：`"新增服务前请先处理当前草稿。"`（`ServicesSection.xaml.cs:1722`）
  - 代码内部日志：`"返回服务列表前请先处理当前修改。"`（UI 上按钮写的是“返回引擎列表”）

这种在同一界面无规律跳跃的文案，给新用户造成了“引擎和模型服务到底是不是一个东西”的认知混乱。

---

## 七、UI 审计缺陷条目清单

| 缺陷 ID | 优先级 | 文件:行号 | 缺陷现象描述 | 违反规格条款 | 最小修复建议 |
| :--- | :---: | :--- | :--- | :--- | :--- |
| **SS-01** | **P0** | `ServicesSection.xaml`: 421–428, 438–445; `ServicesSection.xaml.cs`: 1366–1373, 1440–1452 | 模型推荐证据徽章（`EvidenceBadge`）采用 4px 圆角、独立边框和高亮软底色，与上方模型芯片按钮极其相似。实际为静态 Border，用户点击没有任何反馈（用户点名问题根因）。 | V2 §13 (控件状态合同：伪装可交互元素误导点击)；用户点名缺陷 | 移除类似按钮的独立外边框与有色卡片衬底，改为 `TextBlock Style="{StaticResource Caption}"` 前置实心小圆点或普通括号文本，彻底消除按钮暗示。 |
| **SS-02** | **P0** | `ServicesSection.xaml`: 389–390; `ServicesSection.xaml.cs`: 1135–1172 | 未填写 API Key 时，「获取模型」按钮保持启用。点击后直接向外发送空 Key 的真实网络请求，等待超时或 401 报错并打出红字「密钥无效或没有权限」，误导用户以为服务商不可用。 | V2 §13 (禁用态可辨识)；README G09 (获取模型是显式动作，应前置核查) | 当表单无凭据且非本地服务时，将 `FetchModelsButton` 设为 `IsEnabled="False"`，ToolTip 提示「请先填写 API Key」；或点击时拦截并直接聚焦密码框。 |
| **SS-03** | **P0** | `ServicesSection.xaml`: 369; `ServicesSection.xaml.cs`: 1563–1605 | 「验证连接」违规采用实色主按钮（`PrimaryButton`）抢占视觉焦点；在未填 Key 时不禁用，点击后抛错报红「连接失败」，将本地表单未填凭据伪装成真实连接故障。 | V2 §10.2 U03；V2 §10.3 调色盘合同 (Primary 唯一性)；README G07 | 将按钮样式降为 `GhostButton`；在未填 Key 且非本地服务时置灰禁用，或点击时给出输入引导而不是亮红灯报错。 |
| **SS-04** | **P0** | `TranslateSection.xaml`: 368–405; `TranslateSection.xaml.cs`: 495–565, 643–648 | 无任何可用服务且公共翻译未授权时，主工作台空态仍提示输入和回车翻译，底栏谎称「就绪」，用户填入示例后翻译必然撞车报错，且报错无任何前往设置的修复入口。 | README G09 (无服务不伪装可离线)；V2 §10.1 (错误有修复路径)；V2 §12 (空态围绕下一动作) | 当系统无可用翻译线路时，在空态展示醒目的「未配置翻译引擎」引导卡片，提供「前往配置引擎」按钮直达设置页；底栏状态标明「未配置服务」。 |
| **SS-05** | **P0** | `ServicesSection.xaml.cs`: 1902–1970 | 新建首个服务未填模型即可直接保存，并被自动设为默认；由于没有文字模型，该服务在默认路由下拉框中被过滤隐藏，主工作台翻译直接崩溃，与界面的「设为文字默认」校验严重矛盾。 | README G08 (设置与默认值；默认服务需经过完整前置检查)；体验缺陷 | 在 `TrySaveService` 中引入完整可用性校验，若首个服务缺少模型或有效凭据，只存草稿不自动赋予默认，并明确提示补全。 |
| **SS-06** | **P1** | `ServicesSection.xaml`: 13–48 | 已配置引擎列表条目（`ServiceListItem`）未设置 `Cursor="Hand"`，鼠标悬停时保持默认光标，缺乏整行卡片可交互点击打开编辑器的暗示。 | V2 §13 (导航/列表交互规范)；DESIGN_SYSTEM §5 | 在 `ServiceListItem` 样式中增加 `<Setter Property="Cursor" Value="Hand" />`。 |
| **SS-07** | **P1** | `ServicesSection.xaml`: 368; `ServicesSection.xaml.cs`: 95, 1694–1714 | 在 API Key 输入框为空且存储中无 Key 时，「清除」按钮保持可用，用户确认后报告「API Key 已清除」的虚假成功提示。 | V2 §13 (禁用态；反馈真实性)；体验缺陷 | 检查若当前密码框与存储均无凭据，将 `ClearKeyButton` 置为禁用态（`IsEnabled="False"`）。 |
| **SS-08** | **P1** | `TranslateSection.xaml`: 453–461; `TranslateSection.xaml.cs`: 789–810 | 「收藏到生词本」按钮在收藏成功后没有任何持久的状态指示，图标始终为空心五角星，用户失焦后完全无法获知当前内容是否已被收藏。 | V2 §13 (收藏/固定：图标轮廓与填充共同反馈，选中持续可见，写盘成功后点亮) | 收藏成功后将五角星切换为实心或填充 `AccentBrush` 高亮；在翻译完成时联动查询生词本并更新按钮状态。 |
| **SS-09** | **P1** | `TranslateSection.xaml`: 157, 171, 185, 227, 438, 446, 454 | 主翻译工作台 7 个操作图标按钮（朗读、复制、合并断行、对调、生词）硬编码 `Width="28" Height="28"`，低于桌面交互最小 32 DIP 门槛。 | README G07 (关键按钮至少 32 DIP)；V2 §11.1 (图标按钮至少 32×32 DIP) | 移除行内 `28` 硬编码，统一采用全局 `IconButton` 标准尺寸（32×32 DIP）。 |
| **SS-10** | **P1** | `TranslateSection.xaml`: 157–170, 437–444; `TranslateSection.xaml.cs`: 775–783 | 语音朗读在播放过程中，按钮无「播放中」动效或停止图标切换，Tooltip 仍为「朗读」，缺乏操作中的状态反馈。 | V2 §13 (控件状态合同：操作中状态反馈) | 监听 `TtsService` 播放状态，发声期间将图标切换为停止方块或动态波纹，Tooltip 更新为「停止朗读」。 |
| **SS-11** | **P1** | `GeneralSection.xaml`: 47–52 | 开机启动项被禁用时展示的「重新启用」按钮（`StartupRepairButton`）缺少 Style 声明，样式退化为原生裸 Button，且缺少 Hand 光标。 | DESIGN_SYSTEM §5；体验缺陷 | 为其指定 `Style="{StaticResource GhostButton}" Height="24" Padding="8,2"` 并居中对齐。 |
| **SS-12** | **P1** | `SettingsWindow.xaml`: 112–148; `ServicesSection.xaml`: 530–543 | 设置窗口底栏与服务编辑器各自拥有一套独立的保存条与「未保存的修改」提示。在跨分区编辑时同屏出现两套保存条，造成认知冲突。 | README G08 (显式保存体系一致性)；V2 §12 (设置导航与服务编辑) | 当服务编辑器处于打开状态时，收拢或隐藏窗体底栏的保存按钮；或通过全局状态机统一纳管草稿生命周期。 |
| **SS-13** | **P1** | `ServicesSection.xaml`: 515–543 | 当触发草稿守卫条（`DraftGuardBar`）时，底部的 `EditorActionBar` 依然展示，导致同屏上下堆叠出现两个「取消」按钮和两个保存按钮。 | V2 §10.2 U03；体验缺陷 | 在 `DraftGuardBar` 激活展示期间，将下方的 `EditorActionBar.Visibility` 设为 `Collapsed`，settle 后恢复。 |
| **SS-14** | **P1** | `SettingsWindow.xaml`: 6 | 设置窗体硬编码 `MinWidth="820" MinHeight="600"`。在 1080p 开启 150%/175% 系统缩放时，窗口超出可用屏幕高度，底部保存按钮被挤出可视区。 | V2 §10.2 U02 (设置窗口做低高度规则与紧凑模式)；README G07 | 将窗口 `MinWidth` 降为 680，`MinHeight` 降为 480，内部内容开启纵向自适应滚动。 |
| **SS-15** | **P2** | `SettingsWindow.xaml`: 37; `TranslateSection.xaml`: 16 | 设置窗口与翻译工作台的大标题硬编码 `FontSize="15"`，强行压制全局 `PageTitle` (20 DIP) 标准，破坏排版视觉层级。 | DESIGN_SYSTEM §2.2 (PageTitle 20px)；UI-REVIEW P2 | 移除局部 `FontSize="15"` 声明，恢复规范的 20 DIP 半粗标题标准。 |
| **SS-16** | **P2** | `ServicesSection.xaml`: 148, 198, 289, 304, 521 | 服务分区内部充斥 `12`、`12.5`、`14` 等散落在设计系统标尺之外的字号数值。 | DESIGN_SYSTEM §2.2；V2 §11.1 (排版规范) | 统一归并到系统标尺（SectionTitle 13 DIP / RowTitle 13 DIP / Caption 12 DIP / PageTitle 20 DIP）。 |
| **SS-17** | **P2** | `ServicesSection.xaml`: 8, 58, 68, 114, 208, 241, 267, 369, 481, 532, 539, 540 | 服务分区大量使用 `13,11`、`11,0`、`5,2`、`17,7` 等奇数与非标准边距数值，脱离 4px/8px 网格。 | DESIGN_SYSTEM §5.2 (间距网格基准 4px/8px) | 统一按照 4/8/12/16/20/24 梯度重整 Padding 与 Margin。 |
| **SS-18** | **P2** | `TranslateSection.xaml`: 44, 293 | 工作台外层卡片容器与流式胶囊硬编码 `CornerRadius="4"`，圆角生硬，脱离容器圆角标准。 | DESIGN_SYSTEM §5.1 (容器卡片 10, 胶囊 6) | 外层卡片改为 `CornerRadius="10"`（或引用 CardRadius），流式胶囊改为 `CornerRadius="6"`。 |
| **SS-19** | **P2** | `ServicesSection.xaml`: 138–178 | 未配置任何服务时，顶部的「默认路由」面板仍然常驻显示且下拉框无内容，占用空间且误导用户。 | V2 §10.2 U12 (空态围绕下一动作)；V2 §12 | 在 `config.Profiles.Count == 0` 时将 `RoutingPanel.Visibility` 设为 `Collapsed`。 |
| **SS-20** | **P2** | 全局 XAML / CS | 「翻译引擎」与「模型服务」概念混杂交替（标题写作“引擎”，提示写作“服务”，按钮写作“引擎”，消息写作“服务”）。 | DESIGN_SYSTEM §4；体验缺陷 | 统一专业术语，界面层全部收拢统一为「翻译服务」（或统一为「翻译引擎」）。 |

---

## 八、Top 5 核心修复优先级排序

依据对用户正常体验、主流程可用性以及操作困惑度的影响，整理出建议最高优先排期的 Top 5 修复项：

```
+-----------------------------------------------------------------------------------------------+
|                                    TOP 5 核心修复路线                                         |
+-----------------------------------------------------------------------------------------------+
| 1. [SS-01 / P0] 修复模型推荐证据徽章（EvidenceBadge）伪装为可点击按钮的问题 (用户点名根因)       |
|    - 影响范围：服务编辑面板中的「官方声明/系列推断/本机实测」徽章。                           |
|    - 修复动作：剥离独立有色外边框与卡片底色，改为带弱指示点的静态 Caption 解释文本，消除按钮暗示。 |
+-----------------------------------------------------------------------------------------------+
| 2. [SS-02 & SS-03 / P0] 纠正「验证连接」与「获取模型」在无凭据时的禁用态与按钮层级竞争 (U03) |
|    - 影响范围：服务编辑面板凭据输入区与模型拉取区。                                           |
|    - 修复动作：「验证连接」从 Primary 降为 Ghost 按钮；无 Key 时置灰禁用并附 ToolTip 提示输入。  |
+-----------------------------------------------------------------------------------------------+
| 3. [SS-04 / P0] 修复工作台无服务时空态虚假就绪与引导缺失缺陷 (G09 / U12)                      |
|    - 影响范围：主翻译工作台空态、填入示例与翻译触发流程。                                     |
|    - 修复动作：未配置服务时展示专属引导卡片与「前往配置引擎」跳转按钮；底栏状态标为未配置。   |
+-----------------------------------------------------------------------------------------------+
| 4. [SS-05 / P0] 修复首个服务未填模型保存即被强行设为文字默认的严重逻辑漏洞 (G08)              |
|    - 影响范围：新建服务保存逻辑与 Core 默认路由绑定。                                         |
|    - 修复动作：在 TrySaveService 中接入完整模型/凭据就绪性校验，未就绪的服务不自动激活。      |
+-----------------------------------------------------------------------------------------------+
| 5. [SS-14 / P1] 解决设置窗口 MinWidth/MinHeight 过大导致高 DPI 屏幕底栏保存按钮被挤出问题 (U02)|
|    - 影响范围：设置窗体整体框架与高缩放/小屏幕笔记本电脑。                                   |
|    - 修复动作：最小尺寸降为 680×480 DIP，保障 150% 缩放下底栏保存条与状态栏安全可见。        |
+-----------------------------------------------------------------------------------------------+
```
