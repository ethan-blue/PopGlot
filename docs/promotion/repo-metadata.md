# GitHub 仓库元数据建议

> 用途：把 README 之外的「被发现、被信任」通道也补上。
> 口径：所有描述都以仓库既有事实为限，不含用户数、下载量、评分或性能对比数字。
> 操作入口：仓库页 → 右上角齿轮（Settings）→ General 的 About 区块，或直接点首页 About 旁的齿轮图标。

---

## 1. Description / About（≤ 350 字符，英文）

GitHub 的 Description 会显示在搜索结果、Topic 页、以及 `github.com/topics/xxx` 列表里，比 README 更先被搜索引擎抓到。**写受众 + 场景 + 结果，不堆关键词。**

### 推荐（223 字符，含空格）

```
Windows translator for developers: select any English error, comment, command or doc line, press one hotkey, and read the translation next to your cursor. Local OCR by default, works offline. MIT.
```

### 备选 A —— 强调隐私（247 字符）

```
A Windows translator for people who read English technical text. Select it, press a hotkey, read the translation in place. Screenshots stay local unless you allow otherwise; an offline switch blocks every outbound request. MIT.
```

### 备选 B —— 强调引擎自由（262 字符）

```
Windows translation hotkeys for reading English technical material. Bring your own key: OpenAI-compatible, OpenAI Responses, Anthropic, Gemini, Ollama or LM Studio. No key needed to start. Screen OCR stays local. MIT.
```

选一条即可；推荐首选，因为它包含了陌生人在搜索结果里最想确认的三件事：**给谁的**（developers）、**怎么用**（select + hotkey）、**数据是否安全**（local / offline 收尾）。

不要用：句号结尾省略、`WIP`、`v0.1.35` 之类的版本号（会立刻过期）、任何形式的「best / fastest / #1」。

---

## 2. Homepage

**当前状态：没有官网。**

**建议：暂时留空，不要填。**

理由：

- 仓库 URL 本身已经显示在 About 区块旁边，填自己的仓库地址是重复且无信息量的。
- Homepage 一旦填了，很多人会默认那是「产品官网」点进去，跳到 Releases 或 README 会让人觉得项目没做但站点先做了。
- GitHub 的话题排名和搜索不会因为 Homepage 为空而扣分。

**不建议**把 Homepage 指向 `https://github.com/ethan-blue/PopGlot/releases` —— 发布页会随版本变化，语义上也不该占「官网」这个位置。

**后续填什么**（当其中之一落地时再回来填）：

1. 一个静态落地页（哪怕是 `https://ethan-blue.github.io/PopGlot/`），上面只有一个 30 秒 Demo GIF、两个下载按钮、一段 privacy 摘要 —— 这是最推荐的第一步，因为它同时给 Homeowner 一个真实可填的 URL。
2. 如果做了文档站，填文档站根地址，而不是某个具体页面。

填之前先在 `[docs/PRIVACY.md](./../PRIVACY.md)` 口径下确认：落地页不能声称的能力比 README 更多，也不能出现统计外链（分析脚本）却没在隐私说明里写清。

---

## 3. Topics（建议 10 个）

GitHub 单个仓库最多 20 个 topic，但**塞满 20 个会让标签栏像关键词堆砌**。控制在 8–10 个，选择的原则是：搜索时会用到的 + 别人过滤时用得到的。

**置信度说明**：标 High 的是我判断 GitHub Topics 里长期存在的高频 topic；标 Medium/Low 的请在填之前用 `https://github.com/topics/<topic名>` 打开确认是否存在——不存在的 topic 填进去会显示成灰色普通文本，不会生成可点击的话题页，等于白占一格。

| # | Topic | 为什么 | GitHub 是否已存在 |
| --- | --- | --- | --- |
| 1 | `windows` | 唯一的门槛条件，必须让人在结果列表里一眼看到 | High |
| 2 | `translator` | 与 `translation`（偏「翻译这项技术/译文本身」）相比，`translator` 更贴近「工具」语义，两类都应该占 | High |
| 3 | `translate` | 覆盖动词型搜索词 | High |
| 4 | `ocr` | 截图翻译的核心能力，也是区分于纯文本翻译工具的词 | High |
| 5 | `wpf` | 技术栈筛选；找 .NET 桌面项目的人会用它过滤 | High |
| 6 | `rust` | 技术栈筛选；架构上是 Rust 内核 + WPF 外壳，对 contributor 有吸引力 | High |
| 7 | `dotnet` | 技术栈筛选（`.net` 作为 topic 名不合法，`csharp` 可替代） | High |
| 8 | `llm` | 表明支持接入大模型；比 `ai` 更精准、噪音更少 | High |
| 9 | `desktop-app` | 与 cli / web 工具区分，`desktop-application` 也是同类候选 | Medium（若不存在用 `desktop` 兜底，置信 High） |
| 10 | `privacy` | 隐私是这个项目真实的卖点之一（本地 OCR、离线总开关） | High |

**候补（当前不填，留着轮换）**：

- `productivity`、`developer-tools`（置信 Medium，偏泛，容易淹没）
- `openai`、`anthropic`、`gemini`、`ollama`（支持哪些引擎不该占 topic 位，写在 README 里就够了；等人搜索特定生态时再说）
- `clipboard`、`hotkey`（贴切但太小众，搜索量撑不起）
- `python`、`go` 之类与本项目无关的词绝不要填

**为什么不填 `macos` / `linux`**：当前只有 Windows 版，填了等于骗关注，点进来的人会立刻发现并留下负第一印象。`PRODUCT_SPEC.md` 里 macOS/Linux 属于 P2 移植计划，做完再补。

---

## 4. 是否开 Discussions

**建议：开，但只开两个分区。**

理由：这是一个面向单用户场景的工具，很多提问其实不是 bug，而是「我这个环境能不能用」——比如「某 IDE 里 Ctrl+Alt+W 被占用了怎么办」「公司网络下 Ollama 怎么配」。这类问答塞进 Issues 会让 issue 列表迅速变成杂物间，压掉真正的 bug 报告。

配置建议：

| 分区 | 用途 | 是否建议 |
| --- | --- | --- |
| **Q&A** | 使用问题、环境适配、引擎配置 | 开 |
| **Ideas** | 功能想法，走「讨论 → 确认有价值 → 转 Issue」的路径 | 开 |
| Show and tell | 等到有外部用户量再说 | 暂不开 |
| Announcements | 只有一个维护者，CHANGELOG + Releases 已经覆盖 | 暂不开 |
| Polls / General | 噪音大 | 不开 |

配套动作：在 Issues 的新建页面上把 Discussions 写出来（GitHub 支持给 `.github/ISSUE_TEMPLATE/config.yml` 加 `contact_links`，见下节），让「我不是 bug，我只是有个问题」的人有出口。

---

## 5. Issue 模板要点

### `config.yml` —— 把不是 bug 的人拦出去

```yaml
blank_issues_enabled: false
contact_links:
  - name: 使用问题与交流 / Questions and usage help
    url: https://github.com/ethan-blue/PopGlot/discussions/categories/q-a
    about: 配置、快捷键冲突、引擎接不通这类问题，先来这里问。
  - name: 隐私与数据处理边界 / Privacy boundary
    url: https://github.com/ethan-blue/PopGlot/blob/main/docs/help/privacy-and-security.md
    about: 想知道什么会外发、什么不会，先读这份说明。
  - name: 已知边界与明确不做的事 / Known exclusions
    url: https://github.com/ethan-blue/PopGlot/blob/main/PRODUCT_SPEC.md
    about: PRODUCT_SPEC.md 里「当前未实现与明确排除」一节列出的不是 bug。
```

### Bug 报告模板要点

必填字段按「能不能复现」来设计，不要一上来让用户写长篇：

1. **PopGlot 版本**（只读的下拉或文本框，提示在哪看：托盘右键 → 关于）。版本号定位必须靠前，很多 issue 是已修复的老问题。
2. **Windows 版本与架构**（10 / 11，x64）。混合 DPI、多显示器是已知敏感区。
3. **安装形态**：安装包 / 便携 zip —— 两者的升级路径和数据位置不同。
4. **触发路径**：划词 / 截图 / 工作台 / 查词 —— 四个入口走不同的状态机。
5. **当前线路**：内置免费线路 / 具体的第三方引擎类型（` OpenAI 兼容 / Responses / Anthropic / Gemini / Ollama / LM Studio`）。不要让用户填 endpoint 完整地址，也不要让填任何形式的 key。
6. **是否开了安全离线模式** —— 很多「翻译没反应」其实是开了离线模式。
7. **复现步骤与期望/实际**（按 Markdown 模板预填格式，给的两行示例要是中文）。
8. **日志与诊断**：明确写出「日志不记录原文、截图与密钥」（这是 README 与 PRIVACY.md 的既有口径），并提示用户可以导出诊断，但导出前自己过一眼。`SecretRedactor` 会掩码用户自己的真实密钥，但**不会**掩码从别处复制进来的第三方密钥——这句话要写在模板里，别指望用户猜到。
9. **截图 / 录屏**：欢迎附截图，加一行提醒——贴之前把 API Key、主机地址、客户代码糊掉。

### Feature Request 模板要点

设计目标是过滤掉已经被明确排除的项，而不是劝退：

1. **前置确认**（一个 checkbox）：「我已读过 PRODUCT_SPEC.md 的『当前未实现与明确排除』一节，确认这不是已列入 P1/P2 或明确排除的项」。这一条能过滤掉 macOS 移植、术语库、hover 取词、IDE 集成等重复提议。
2. **要解决的真实场景**：要求写出「我在做什么的时候，卡在哪一步」——而不是「希望支持 XX 模型」。前者能讨论，后者只能排队。
3. **你现在怎么绕过**：有绕法的一般是真需求。
4. **是否涉及网络外发**：新增能力如果要把数据送出去（新引擎、云端同步等），必须写清送什么、送到哪，因为这跟整个产品的隐私承诺直接冲突，属于要单独过决策的功能而非常规排期。
5. **愿意参与实现吗** —— MIT 项目，把 contributor 的分岔点前置。
6. 明确写出「**维护者可能直接关闭不符合边界的请求，不另行讨论**」，比事后关闭更体面。

### 模板里统一要注意的

- 中英双语，跟 README 口径一致；标题行可以是双语一行。
- 不要在模板里承诺响应时间（「我们会在 24 小时内回复」这种话单人项目写不得）。
- 每个模板顶部都留一行指回 README 的「常见问题」，因为 FAQ 已经回答了免费线路、SmartScreen、快捷键冲突这三类最高频问题。
