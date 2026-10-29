# README 改动记录（推广用途）

> 目标：让从 GitHub 首页进来的陌生人 15 秒内决定要不要下载。
> 原则：不编造数据、不删除既有章节、不动版本锚点、保留中英双语结构。

## 一、逐条改了什么

### 1. 标题下方加一句话定位（中文 + English）

- **改前**：标题下直接是一句平铺的功能描述「Windows 划词和截图翻译。选中报错、注释、命令或文档……」，没说清给谁。
- **改后**：两段 blockquote —— 中文一句「给在 Windows 上读英文技术内容的程序员：……不用切窗口、不用手动复制」，English 一句同义。
- **为什么**：陌生人第一个判断是「这东西是不是给我的」而不是「它有什么功能」。写清受众（读英文技术内容的程序员）+ 场景（报错/注释/命令/文档）+ 结果（译文和一句解释出现在光标旁，不用切窗口）。不用「最」字。

### 2. 「下载」从文档下半部分移到功能之前

- **改前**：下载区在第 86 行之后，位于「能做什么」「界面」「第一次使用」「数据放在哪里」之后。
- **改后**：紧接定位句之后就是 `## 下载`，顺序为 徽章 → 定位 → 下载 → 能做什么 → 差异化 → 界面 → FAQ → 第一次使用 → 数据 → 版本 → 构建 → 参与 → 许可。
- **为什么**：愿意下载的人不需要读完功能表；犹豫的人也不会滚到最后才看到按钮。

### 3. 两个按钮式下载链接

- 新增两枚 `style=for-the-badge` 的 shields.io 徽章作为按钮（安装包 / 便携 zip），指向 `v0.1.35` 的两个 Release 附件；下面再补一行英文纯文本链接，兼顾 Markdown 渲染受限的场景（邮件、RSS、部分镜像）。
- **为什么不保留原来的两个 bullet**：bullet 在 GitHub 首页渲染只是一行普通蓝色链接，视觉权重不够；徽章式按钮在「首屏 15 秒」里是唯一能跟 Star/Fork 行抢注意力的元素。
- 原来 bullet 里的两个 URL 与文件名被完整保留在按钮链接中，没有丢锚点。

### 4. 体积说明（真实数据）

- 我在初稿里因为本地没有 0.1.35 的产物，只写了「约 85 MB 量级」这种保守估算。同僚随后补上了**实测值**，我用 GitHub API 核对无误：

  | 附件 | 字节数 | 折算 |
  | --- | --- | --- |
  | `PopGlot-v0.1.35-win-x64-setup.exe` | 59,483,995 | ≈ 57 MB |
  | `PopGlot-v0.1.35-win-x64.zip` | 87,833,887 | ≈ 84 MB |

  数据源：`GET https://api.github.com/repos/ethan-blue/PopGlot/releases/tags/v0.1.35` 的 asset `size` 字段。
- README 现在的体积块引用标注为「0.1.35 实测」，并解释了为什么这么大（自带 .NET 10 运行时），安装包比 zip 小是因为 Inno Setup 压缩率更高。
- **注意**：这是会随版本变化的数据，每次发新版都必须重新拉一次实际附件大小，不要沿用。

### 5. 新增「和别的工具有什么不同」

三条差异化，每条都能在仓库现有文档里找到出处，**不含任何对比数据或竞品点名**：

| 条目 | 依据出处 |
| --- | --- |
| 截图默认不出本机，本地 OCR + 单独授权 + 离线总开关（Shell/Core 双侧拦截） | `docs/PRIVACY.md`「何时可能联网」「0.1.35 起默认允许」；`PRODUCT_SPEC.md`「隐私与回退」「模式与设置」 |
| 译文与「要点」分离，切要点不清掉译文 | `README.md` 原「工作台」行；`PRODUCT_SPEC.md`「P0 用户流程」第 6 条结果分层 |
| 代码标识符占位保护 + 返回后逐字符校验 + 校验失败安全失败 | `PRODUCT_SPEC.md`「程序员内容保护」 |

再加一段明确标为「**还比不上的地方（诚实说明）**」（English: Where it falls short）：只有 Windows、安装包未签名有 SmartScreen、没有官网、没有账号与云同步、免费公共线路无 SLA、要好效果得自备 Key。全部来自 `PRODUCT_SPEC.md`「当前未实现与明确排除」和 `docs/DISTRIBUTION_MATURITY.md` 的分级状态。

### 6. 截图区预留 Demo 动图位

- 在 `panel-dark.png` 下方插入一段 HTML 注释，内含：目标路径 `docs/images/demo.gif`、建议内容（15 秒一次完整划词，可选第 4 步开工作台）、生成方式、脱敏提醒，以及**录好后直接解除注释就能用的两行 Markdown**（图片 + 图注）。
- 生成步骤里接上了 `./scripts/start-demo-instance.ps1 -Reset`（隔离演示实例，数据重定向到 `%TEMP%\PopGlot-Demo`，碰不到日常实例），并写明它的边界：只隔离 PopGlot 自己的数据目录，**不隔离 Windows 凭据管理器和外部账号**，所以还要自己关掉聊天、邮箱、通知预览。分镜指向 `docs/VIDEO_DEMO.md`。
- **为什么不写成可见占位**：文件 `docs/images/demo.gif` 当前不存在，若写成正常 Markdown 图片，GitHub 页面会渲染成裂图，直接伤害转化。整段用 HTML 注释包裹，页面上看不到任何痕迹，但维护者打开 README 源码立刻能拿到脚本大纲。
- 原先位于文档开头的「深色/浅色工作台」对照表移到了界面区，让截图集中在一个区块。

### 7. 新增「常见问题」7 条

要付费吗 / 需要 API Key 吗 / 数据去了哪 / SmartScreen 怎么办 / 便携与安装包装什么区别 / 快捷键冲突怎么办 / 英文合并版第 7 条（free + privacy + key 三问合一）。

- 内容全部来自既有事实：MIT、Credential Manager、`%LOCALAPPDATA%\PopGlot`、日志不记原文截图密钥、`history.json` 200 条/90 天/4 MiB、免费线路 Google → MyMemory 回退、安装包未签名、0.1.2 起自带 .NET、快捷键可重录且冲突时退回旧组合。
- SmartScreen 那条直接给了处置动作（更多信息 → 仍要运行），并回指上面的 SHA-256 校验命令。
- **为什么放在「界面」之后、「第一次使用」之前**：常见问题承担「打消最后一丝顾虑」的职责，位置在下载和差异化之后最有效；放在「第一次使用」后面会变成售后说明而不是成交前的释疑。

### 8. 过时文案修正（划出为独立条目）

**背景**：0.1.35 改了免费线路的授权模型，README 有一处没跟上，按 team-lead 要求单列。

**改前 —— 「第一次使用」第 3 步：**

> 若还没有自己的引擎，到「设置 → 隐私与数据」允许免费线路。未允许时不会发出请求。

**为什么错：** 照 paste 出的假象用户会去设置页找一个已经不存在的开关。核对代码后的实际行为：

| 事实 | 代码出处 |
| --- | --- |
| 0.1.35 起默认允许：只有 `FreeEngineConsent.Denied` 才拦下 | `apps/PopGlot.Windows/Sections/TranslateSection.xaml.cs:1410`、`1460` |
| 隐私页不再有开关按钮，只剩一行状态文本 | `Sections/PrivacySection.xaml:125-142`（`FreeEngineConsentRow` + `FreeEngineConsentState`），`PrivacySection.xaml.cs:217-225` |
| 关闭免费线路的唯一入口是重开向导 | `PrivacySection.xaml:142`「默认开启。要关闭，请在『设置 → 通用 → 重新显示引导』的隐私一页取消勾选。」；`GeneralSection.xaml:126` 的「重新显示引导」按钮；`FirstRunWindow.xaml.cs:12` |
| Google / MyMemory 的切换在浮窗与查词窗的线路菜单里，不在设置页 | `TranslationPanelWindow.xaml.cs:1904`、`QuickSearchWindow.xaml.cs:886`（`BuildTextRouteMenu`） |

**改后（中英两份都改）：**

- 中文第 3 步：「内置免费线路 **0.1.35 起默认开启**，不再拦你一层『先去设置页允许』的动作，默认走 Google 公共翻译。Google 不可用时，在浮窗或查词窗的线路菜单里改选 MyMemory。」
- 中文第 4 步（新增）：关闭走「设置 → 通用 → 重新显示引导」，并明说「设置 → 隐私与数据页现在只陈述当前状态，那里没有开关按钮」——**明确告诉用户别去找**，比删掉不提更有用。
- 原第 4、5 步顺延为第 5、6 步。
- 英文段重写，去掉了 "Allow the free engine under Settings → Privacy before the first request"，改为说明默认开启、线路菜单位置、以及从 General → Redisplay Guide 关闭。

**全文残留扫描结果**：对 `未允许` / `允许免费线路` / `允许公共翻译` / `需先授权` / `恢复询问` / `允许 / 拒绝` 六个词做了 grep，**README 内均已清零**。其余提到的行为（安全离线模式阻止一切外发、 openssl 密钥进 Credential Manager、日志不记原文截图密钥、卸载保留数据）都有 `docs/PRIVACY.md` 与 `PRODUCT_SPEC.md` 支撑，保留不动。

### 9. 「从源码构建」补演示实例

按建议加了一小节：`./scripts/start-demo-instance.ps1 -Reset`，中英各一句，说明数据重定向到 `%TEMP%\PopGlot-Demo`、碰不到日常实例，并写清隔离的边界（不隔离凭据管理器与外部账号）。放在这一节而不是「第一次使用」，因为它对普通用户无意义、对录 Demo 的人和 contributor 才有意义。

### 10. 末尾「参与」段

轻量引导：Star（让下一个人更容易找到它）、Issues（提醒先脱敏，提到内置的 `SecretRedactor`）、提 Feature 前先看 `PRODUCT_SPEC.md`「当前未实现与明确排除」，避免把明确边界当成 bug 重复提。没有乞讨式措辞。

## 二、没有做的事

- 没有跑构建、测试或 install/build 任何脚本。
- 没有改任何 `.cs` / `.rs` / `.csproj` / `Cargo.toml`。
- 没有改 `CHANGELOG.md`。
- 没有创建或修改任何真实图片；`docs/images/demo.gif` 仍然不存在（有意保留为占位）。
- 没有改动 `.workbuddy` 目录。
- 没有新增或删除 README 里的既有章节：「数据放在哪里」「版本怎么编号」「从源码构建」「许可」「第一次使用」「界面」「能做什么」全部保留。
- 保留了中英双语结构：中文段在前，English 段紧随；新增的下载、差异化、FAQ、参与四节都同时写了英文版。

## 三、版本锚点一致性自查

最后一次复查（修完过时文案之后）：README 全文 `0.1.35` 共 **23 处出现**，分布在 **13 行**，逐处核对一致：

| 位置 | 内容 |
| --- | --- |
| 徽章（第 4 行） | `badge/version-0.1.35-5B67B5` → CHANGELOG.md |
| 安装包按钮（18） | `releases/download/v0.1.35/PopGlot-v0.1.35-win-x64-setup.exe` |
| 便携包按钮（19） | `releases/download/v0.1.35/PopGlot-v0.1.35-win-x64.zip` |
| 英文下载行（21） | 同上两个 URL，行内出现 3 次版本号 |
| 体积说明中/英（23、24） | 各行 1 处，标注为 0.1.35 实测 |
| 形态表格（28、29） | `PopGlot-v0.1.35-win-x64-setup.exe`、`PopGlot-v0.1.35-win-x64.zip` |
| 0.1.35 发布说明段（31） | 段首加粗版本号 |
| SHA-256 校验命令（43、44） | 两行 PowerShell，均用 `PopGlot-v0.1.35-win-x64-setup.exe` |
| 「第一次使用」第 3 步（154） | 「内置免费线路 0.1.35 起默认开启」 |
| 「第一次使用」英文段（159） | "since 0.1.35 it is on by default" |

另有 `0.1.1` 与 `0.1.2` 各 1 处，是「包里有没有 .NET」那张历史表格的行标签（`0.1.2 及以后` / `0.1.1 及以前`），**属于历史事实陈述，不是版本锚点**，不动。全文没有混入任何其他版本号。

`.github/workflows/release.yml` 的版本校验只检查 csproj / Cargo.toml / Cargo.lock / CHANGELOG.md 四处，**不检查 README**，因此本次改动不会影响发布流程；README 侧的版本号仍保持与它们一致。发新版时按惯例全局替换 README 里的 `0.1.35` 即可。

## 四、诚实性与渲染最终自检

| 检查项 | 结论 |
| --- | --- |
| 编造的数字？ | 无。全文唯一的三处数字来源是：两个包体积（GitHub Release 附件接口实测）、history.json 的 200 条/90 天/4 MiB（`PRODUCT_SPEC.md`）。其余全是行为描述。 |
| 用户数 / 下载量 / 评分？ | 全文 0 处。 |
| 「最强 / 第一 / 唯一 / best / #1」？ | 0 处。仅有的含「第一」的文本是「第一次使用」「`Esc` 第一次取消当前请求」，属于叙述性用法。 |
| 竞争对比数据？ | 无。「和别的工具有什么不同」三条全部是自身设计事实，没有点名任何竞品。 |
| demo.gif 是否会造成裂图？ | 不会。三处 `docs/images/demo.gif` 字符串全部落在 `<!--` (第 86 行) 与 `-->` (第 113 行) 之间，页面上零渲染痕迹。 |
| 内部 TODO 是否漏在 README 上？ | 没有。README 面向陌生人，凡是没确定的东西都不写在那里，只记进本文件「留待确认」一节。 |
| 中英双语结构？ | 保持。中文段在前，English 段紧随；下载、差异化、FAQ、第一次使用、演示实例、参与六处新增内容都补了英文。 |

## 五、docs/help 过时文案修正（第二轮追加）

同一处 0.1.35 行为变更的连坐：应用内帮助文档也在指导用户找那个已经删掉的开关。

### 5.1 先查影响面（改前必做）

| 检查项 | 结论 |
| --- | --- |
| 生产代码是否按路径读取这些 md？ | 是，但**只按文件名登记**。`apps/PopGlot.Windows/HelpWindow.xaml.cs:22` 的文章目录里有 `("隐私与数据安全", "privacy-and-security.md")`，渲染时走 `MarkdownPresenter.RenderToFlowDocument`。改文件内容不影响解析，增删改article条目才会触发 `WaveRegressionTests` 里 `Articles.Length == 6` 的断言。本次只改内容，条目数不变。 |
| 是否有测试硬编码文档文本？ | **没有**。`tests/` 里对 md 内容的断言只有一处：`WaveRegressionTests.cs:3571` 检查 `index.md` 不漏 `](xxx.md)` 语法；rendering 断言只覆盖 index.md 与 troubleshooting.md 的「非空渲染」。两份文件本次都改动了内容但没动链接语法，保持非空。 |
| 是否有「读源码/文档路径后 grep 内容」的测试？ | 有这个先例：`tests/PopGlot.Windows.PureTests/Program.cs` 的 `DistributionMaturityAndPackagingInvariants()`（2295 行起）会读 `docs/DISTRIBUTION_MATURITY.md`、`docs/VERSIONING.md`、`scripts/publish-package.ps1`、`scripts/write-release-notes.ps1`、`.github/workflows/release.yml` 并按关键词断言。**目标清单里没有 docs/help 下的任何文件**，所以改帮助文档不会被这类断言命中。本次也未触碰它断言的那五个文件。 |
| 打包方式是否被连带影响？ | `tests/PopGlot.Windows.LogicTests.csproj:24` 把 `docs\help\**\*.md` 作为 Content 拷入测试宿主；`PopGlot.Windows.csproj` 同理把 docs/help 进主输出。文件数量与文件名不变，拷贝规则不变。 |
| 是否跑构建/测试？ | **没有跑**，按团队约定留给门禁。上述结论全部来自静态 grep。 |

### 5.2 改了什么

**`docs/help/privacy-and-security.md`**

- 改前（第 24–29 行）：在「设置 → 隐私与数据」管理四个开关，其中「公共翻译：可允许、拒绝或恢复询问。『仅本次允许』只授权一次实际发送，不会变成永久许可。」
- 改后：保留真实存在的四个开关（安全离线模式、允许上传截图、允许连接云端翻译服务、云端朗读与崩溃诊断），把公共翻译单独拎出来写成一大段：「0.1.35 起默认开启，页面上的这一行只显示当前状态，不再有『允许 / 拒绝 / 恢复询问』的按钮」，并写清改线路（浮窗/查词窗底部菜单）与关闭路径（设置 → 通用 → 重新显示引导）。
- 第 12 行的表格行加上「（默认开启）」，避免表格与正文互相打架。
- 代码出处：`Sections/PrivacySection.xaml:125-142`（只剩 `FreeEngineConsentState` 状态文本）、`PrivacySection.xaml.cs:214-225`（状态三种：默认/已关闭）、`PrivacySection.xaml.cs:223-225`、`TranslationPanelWindow.xaml.cs:1904` 与 `QuickSearchWindow.xaml.cs:886`（线路菜单在浮窗与查词窗）。

**`docs/help/getting-started.md`**

- 第 9 行原写「公共翻译**需要先授权**」——直接和 0.1.35 冲突。改为「免配置、默认开启，不需要先去授权」，并补上 MyMemory 切换位置与关闭路径。
- 第 8 行「可选择公共翻译」改为「直接用公共翻译」（原来暗示还要做一次选择动作）。
- 第 49 行常用设置清单里的「公共翻译**授权**」→「公共翻译**状态**」，并注明开关在「通用 → 重新显示引导」的向导里。

**`docs/help/troubleshooting.md`**

- 第 5 行「或在『隐私与数据』允许公共翻译」——同類残留，改为「或直接用默认开启的公共翻译」。

本批次**没有英文段**：三份文档都是纯中文，不存在中英不对称问题。

### 5.3 docs/help 全目录扫描结论

两轮 grep：

- 第一轮（团队给的关键词）：`允许免费线路`、`允许公共翻译`、`恢复询问`、`需先授权`、`可允许、拒绝`、`允许 / 拒绝` —— 修复后**全部清零**，唯一残留命中是 privacy-and-security.md 第 31 行自己写的「不再有『允许 / 拒绝 / 恢复询问』的按钮」，属于说明性文字，不是引导用户去点。
- 第二轮（放宽到 `公共翻译` / `授权`）：剩余 12 处已逐条看过，全部合规——不是新写的正确说明，就是仍然成立的授权场景（ChatGPT 浏览器授权、OMP 授权回调、崩溃诊断单独授权、`windows-shell.json` 里的授权字段）。

**附带发现（未改，不在本次范围）**：`docs/help/privacy-and-security.md` 第 33 行写凭据目标为 `PopGlot/provider/<引擎 id>`，与当前代码一致（历史上有过 `PopGlot/Profile/{ProfileId}` 的写法，`docs/product-review-2026-09-26/IMPLEMENTATION-REPORT.md:113` 记录过这处偏差），现已修好，无需处理。

## 六、留待确认的事项

1. **体积**：0.1.35 的体积已填为实测值（见上文），但这是逐版本数据——发新版时需重新取一次附件大小。
2. **用户数据目录口径**：README 写 `%LOCALAPPDATA%\PopGlot`，`docs/DISTRIBUTION_MATURITY.md` 写 `%APPDATA%\PopGlot\`。两者在多数 Windows 配置下等价（都是用户漫游/本地 AppData 分支），但**不是同一个结论**。本次未擅自改 README，建议由产品侧确认后统一。
3. **Demo GIF**：需要有人实际录一段并落盘到 `docs/images/demo.gif`，再按注释说明解除注释。用 `./scripts/start-demo-instance.ps1 -Reset` 起隔离实例，分镜见 `docs/VIDEO_DEMO.md`。
4. ~~docs/help 也有同类过期说明~~ **已修（第二轮）**：`privacy-and-security.md`、`getting-started.md`、`troubleshooting.md` 三份共 5 处 0.1.35 残留已按真实行为改写，详见上一节。
