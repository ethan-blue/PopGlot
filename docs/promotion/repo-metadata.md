# GitHub 仓库元数据建议

> 用途：把 README 之外的「被发现、被信任」通道也补上。
> 口径：所有描述都以仓库既有事实为限，不含用户数、下载量、评分或性能对比数字。
> 操作入口：仓库页 → 右上角齿轮（Settings）→ General 的 About 区块，或直接点首页 About 旁的齿轮图标。

---

## 1. Description / About（已落地，勿重复改）

**现状（已由 team-lead 用 gh 写入，2026-10-29）**：

```
A Windows translator for selected text and screenshots. Select and translate instantly, screenshot OCR stays on your machine by default, bring your own OpenAI/Anthropic/Gemini key or a local Ollama model, and cut every outbound request with safe offline mode. WPF + Rust, MIT.
```

290 字符，在 350 上限内。它已经覆盖了三件陌生人在搜索结果里最想确认的事：**是什么**（selected text + screenshots translator）、**数据是否安全**（OCR stays on your machine / cut every outbound request）、**要不要自带账号**（bring your own key or a local Ollama model）。无需再改。

**维护时的硬性约束**（改这行前先过一遍）：

- 不能出现版本号（`v0.1.35` 之类会立刻过期）。
- 不能出现 `best` / `fastest` / `#1` / `official` 这类无法验证的说法。
- 每次产品能力边界变化时（例如真的出了 macOS 版、真的签了名）回头同步，否则 Description 会先于 README 变成假信息。
- 注意：README 的定位句已改成「给读英文技术内容的程序员」，Description 这边偏通用受众。两者不必逐字对齐——搜索结果要宽，README 要准——但**不能互斥**，比如 Description 不能暗示 macOS 可用。

---

## 2. Homepage

**当前状态：没有官网，GitHub 上的 Homepage 字段为空（API 读回 `null`）。**

**建议：保持留空，不要填。**

理由：

- 仓库 URL 本身已经显示在 About 区块旁边，填自己的仓库地址是重复且无信息量的。
- Homepage 一旦填了，很多人会默认那是「产品官网」点进去，跳到 Releases 或 README 会让人觉得项目没做但站点先做了。
- GitHub 的话题排名和搜索不会因为 Homepage 为空而扣分。

**不建议**把 Homepage 指向 `https://github.com/ethan-blue/PopGlot/releases` —— 发布页会随版本变化，语义上也不该占「官网」这个位置。

**后续填什么**（当其中之一落地时再回来填）：

1. 一个静态落地页（哪怕是 `https://ethan-blue.github.io/PopGlot/`），上面只有一个 15–30 秒 Demo GIF、两个下载按钮、一段 privacy 摘要 —— 这是最推荐的第一步，因为它同时给 Homepage 一个真实可填的 URL。GIF 素材可以先照 README 里那段 HTML 注释的分镜录。
2. 如果做了文档站，填文档站根地址，而不是某个具体页面。

填之前先在 `[docs/PRIVACY.md](./../PRIVACY.md)` 口径下确认：落地页不能声称的能力比 README 更多，也不能出现统计外链（分析脚本）却没在隐私说明里写清。

---

## 3. Topics（已落地，现状 13 个）

**现状（已由 team-lead 用 gh 写入，2026-10-29）**，按 GitHub 返回的顺序：

`csharp`、`desktop-app`、`dotnet`、`llm`、`ocr`、`privacy`、`rust`、`screenshot`、`translate`、`translation`、`translator`、`windows`、`wpf`

这组已经把我原本建议的 10 个全部覆盖，并多带了好用的 `screenshot`、`dotnet`、`csharp`、`translation`、`privacy`。**不需要再增删。**

**后续维护约束**：

- **绝对不要加 `macos` / `linux` / `cross-platform`** —— 当前只有 Windows 版，加了等于骗关注，点进来的人立刻发现并留下负第一印象。等真的移植完成再说。
- 满了 20 个也不要继续塞；现在 13 个已经是上限舒适区的边缘，再加低频词（`productivity`、`developer-tools`）只会稀释。
- 支持哪些引擎（`openai` / `anthropic` / `gemini` / `ollama`）不占 topic 位，写在 README 里就够了。有人为了能被按生态搜索到而加进去，代价是标签栏变成厂商列表。
- 每次加之前用 `https://github.com/topics/<topic名>` 确认存在：不存在的 topic 会显示成灰色普通文本，不能点击，等于白占一格。

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
