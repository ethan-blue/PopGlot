# PopGlot English Promotion Copy Pack

> 文件角色：PopGlot 推广团队的「英文内容」产出物。
> 生成日期：2026-10-29。产出人：copywriter-en。
> 用途：对外发布前逐字核对后直接复制粘贴。不要改版本号、文件名、快捷键。
> 中文说明性文字中的 `> TODO:` 标记是每节的「缺失素材」清单，发布前必须清零或明确接受降级。

---

## 0. 统一事实表与发布前检查

### 0.1 可以写、必须写对的事实（逐字使用）

```
Product      PopGlot - a Windows desktop translator for selected text and screenshots
Repository   https://github.com/ethan-blue/PopGlot
Licence      MIT
Stack        WPF (.NET 10) shell + Rust core (popglot-domain / popglot-core / popglot-ffi),
             separated by a C ABI; the shell can be replaced without rewriting the translation core
Version      0.1.35, released 2026-10-29 (latest)
Downloads    https://github.com/ethan-blue/PopGlot/releases
             PopGlot-v0.1.35-win-x64-setup.exe   ~59 MB, Inno Setup installer
             PopGlot-v0.1.35-win-x64.zip         ~88 MB, portable
Both assets bundle .NET (no separate runtime install) and each ships a .sha256
```

Cited below, 直接写：

- Installer behaviour: detects the previous install location on upgrade; installs per-user without admin by default; uninstalling keeps your config and history.
- Portable: unzip and run `PopGlot.exe`.
- Hotkeys (default): `Ctrl+Alt+W` select-text, `Ctrl+Alt+Space` screenshot, `Ctrl+Alt+Q` quick lookup, `Ctrl+Alt+O` workbench, `Ctrl+Alt+X` close panel.
- Engines: OpenAI-compatible, OpenAI Responses, Anthropic, Gemini, plus local endpoints such as Ollama and LM Studio.
- Free route: with no engine configured, Google public translate is used by default; MyMemory can be selected when Google is unavailable. Only the single selected snippet is sent.
- Privacy: screenshots are OCR'd locally by default; sending the image to a vision model requires explicit permission. Safe offline mode blocks every outbound request including the free route, while local model endpoints still work. Keys live in Windows Credential Manager. Logs never record source text, screenshots or keys. The clipboard is restored by sequence number, so a later copy is not overwritten.
- Workbench: source on the left, translation on the right, notes below the translation; "key points" is a separate view that does not erase the translation.
- History: max 200 entries / 90 days / 4 MiB; partial results are not stored.

### 0.2 必须主动写出来的三条（不要藏）

凡是对募集用户的英文渠道，正文里都要原样出现：

1. **Unsigned installer.** Current installer is unsigned, so SmartScreen will warn on the first run. Say this yourself, before anyone else does, and always give the way to verify.统一用这句：

   > The installer is currently unsigned, so Windows SmartScreen will warn before the first run. Every asset ships a `.sha256`: open the release page, download that file, and compare the values yourself - `Get-FileHash PopGlot-v0.1.35-win-x64-setup.exe` in PowerShell, or `certutil -hashfile PopGlot-v0.1.35-win-x64-setup.exe SHA256` in cmd.

   Do not claim a code-signing certificate we do not have.
2. **Bring your own key.** The interesting engines need your own API key (OpenAI-compatible, OpenAI Responses, Anthropic, Gemini) or a local endpoint (Ollama, LM Studio). The default no-configuration route is Google public translate, i.e. public machine translation - convenient, not special. MyMemory can be selected when Google is unavailable.
3. **No traction claims.** Do not say "users", "downloads", "rated", "trusted by N". We have none of those numbers to cite. See 0.4.

### 0.3 绝对不能写

- 用户数、下载量、评分、好评率、性能对比倍数 —— 没有可用数据，一律不写。
- `best` / `#1` / `No.1` / `the only` / `ultimate` / `revolutionary` / `game-changing` / `world's fastest` 等无依据措辞。
- "free forever" / "100% private" / "zero telemetry" —— 过度承诺；离线模式之外的行为要说清楚。
- Emoji。本文件所有对外文案均不含 emoji。
- 任何你没有真机验证过的行为（例如某个游戏全屏下是否可用、某个 PDF 阅读器里划词是否成功）。

### 0.4 现状基线（内部用，禁止写进文案）

以下数字于 2026-10-29 通过 GitHub REST API 实际查询得到，用途是「判断是否够资格进某个列表」，**不要写进任何对外文案**：

| 项 | 实测值 | 用途 |
| --- | --- | --- |
| `ethan-blue/PopGlot` stargazers | 2 | 判断 awesome 列表门槛 |
| 仓库创建时间 | 2026-08-23 | 判断「项目年龄」门槛（目前 66 天） |
| 最后一次 push | 2026-10-29 | 判断活跃度门槛 |
| License | MIT | 满足所有列表的 OSI 许可要求 |
| 默认分支 | `main` | PR 目标 |
| 全部 release 资产累计下载次数 | 130（30 个 release 合计） | 判断 awesome-rust 的 downloads 门槛 |
| `docs/images/` 现有截图 | panel-dark.png, settings-dark.png, workbench-dark.png, workbench-light.png | 静态图，无 GIF |

### 0.5 每篇发布前的通用检查

- [ ] 版本号写作 `0.1.35`，文件名写作 `PopGlot-v0.1.35-win-x64-setup.exe` / `PopGlot-v0.1.35-win-x64.zip`。
- [ ] 快捷键写作 `Ctrl+Alt+W / Space / Q / O / X`，不含 Mac 的 Command。
- [ ] 正文里出现 0.2 的三条。
- [ ] 没有出现 0.3 的任一词汇。
- [ ] 声明了「我是作者」（Reddit 尤其重要，写做 "I'm the author - AMA / happy to answer anything technical"）。
- [ ] 同一天没有把同一段文案投到多个 subreddit（这是 Reddit 判定 spam 的头号信号）。
- [ ] 原文手写，不要过一遍 LLM 润色。r/dotnet、r/rust、HN 明确排斥 AI 生成的帖子。

---

### 0.6 竞品格局与定位纪律（2026-10-29，由 team-lead 提供，数据查自 GitHub REST API）

**可陈述的事实**

- pot-app 组织下的仓库在 GitHub 上均已 archived；主仓库 `pot-desktop` 最后提交日期为 2026-07-04。同品类最知名的开源项目已停更。
- 同品类里仍然活跃的开源竞品包括 STranslate（8,442 star，MIT，同为 WPF）与 eSearch（7,371 star）。**它们同样支持 Ollama / LM Studio 本地端点。**

**因此「支持本地模型」不是差异化，是入场券。不要在英文文案里把它写成卖点。**

差异化只能落在这五条上，且每条都有对应的实现：

| 差异化 | 可写的表述 |
| --- | --- |
| 截图默认本机 OCR | Screenshots are OCR'd on your machine by default |
| 图片外发是独立开关 | Sending the bitmap to a vision model requires an explicit opt-in, every time |
| 离线模式连免费通道一起断 | Safe offline mode blocks every outbound request, including the no-configuration fallback, while local endpoints keep working |
| 剪贴板按序号还原 | The clipboard is restored by sequence number, so a newer copy is never overwritten |
| 密钥进凭据管理器 | Keys are stored in Windows Credential Manager; logs never record source text, screenshots or keys |

**涉及竞品时的红线**

- 只写可验证的事实：「the official pot-app repositories are archived; pot-desktop's last commit is 2026-07-04」。
- **禁止**写 "Pot is dead" / "the best alternative" / "better than X" / "the only one that ..."。Reddit 对踩竞品与自我吹捧高度敏感，会被 downvote 甚至删帖。
- 提到竞品时，顺带承认它们的存在与优点（例如 STranslate 与 eSearch 都活跃且都支持本地模型）。这反而增加可信度，也避免别人在评论区替你补充。

---

## Task A. 社区文案

### A.1 Reddit

#### A.1.0 站点级规则（对所有 sub 都适用）

Reddit 的内容政策把「重复、不受欢迎、未被请求的推广」（content policy 下的 spam 定义）列为违规，社区层规则更严格且永远优先。社区通行做法是 90/10：约九成内容是不含自身推广的真实参与，一成可以是自己的东西。违规后果从删帖、subreddit 封禁到全站 shadowban（你的帖子对所有人不可见且不通知你）。

两条硬红线：

- 不用小号互相点赞，不请托来刷票 —— 这是全站级的账号风险。
- 不同时在多个 sub 发同一段文案。本文件给的七个版本是「按 sub 改写入口」，不是群发模板，且发布时间要拉开。

#### A.1.1 r/Windows11

**① 规则与自我推广红线**

规则标题是 "Do not advertise a 3rd party software without permission"。原文：如果你要推广一个 app 或网站，**必须先发 modmail 申请许可**，并且要在 modmail 和正文里都给出足够多的相关信息。该 sub 明确 "This is not a marketplace subreddit, selling anything is prohibited"。

其它相关规则：

- 禁止短链接 / blogspam / 推广联盟链接。发完整桌面端链接。
- 标题必须与内容相关，禁止含糊、煽动、误导。
- 禁止 repost 与低质量帖子；一切能用搜索或搜索引擎查到的内容会被酌情删除。
- Meme 仅限周一（UTC）。**任何时候都禁止技术求助帖**，请去 r/WindowsHelp 或 r/TechSupport。
- 需要正确选择 flair。

**结论：必须先 modmail，获批后再发。不要在获批前发帖。**

Modmail 草稿：

> Hi mods,
>
> I'd like to request permission to share a Windows 11 app I built, per your rule on advertising third-party software.
>
> PopGlot is an open source (MIT) translator for Windows desktop. It translates selected text and screenshots from any window. v0.1.35 was released today: https://github.com/ethan-blue/PopGlot/releases
>
> Source and full technical detail: https://github.com/ethan-blue/PopGlot
>
> Relevant to Windows 11 specifically: it installs per-user without an admin prompt by default, the installer detects the previous install location on upgrade, and uninstalling keeps the user's config and history. Both the installer (~59 MB) and the portable zip (~88 MB) bundle .NET, so no separate runtime install is needed. Each asset ships a .sha256. Screenshots are OCR'd on the machine by default.
>
> Known caveat I will state in the post: the installer is currently unsigned, so SmartScreen may warn on the first run. The strongest engine quality needs the user's own API key (OpenAI-compatible, OpenAI Responses, Anthropic, Gemini) or a local endpoint such as Ollama or LM Studio. With nothing configured it falls back to Google's public translate, which is ordinary machine translation.
>
> I'm the author and I'll be in the thread answering anything. Happy to adjust the post or drop it if you'd rather I didn't post.

**② 标题候选**

1. `PopGlot 0.1.35 - an open source (MIT) translator for selected text and screenshots on Windows 11`
2. `I built a Windows 11 translator that OCRs your screenshots locally instead of uploading them`
3. `A hotkey translator for Windows 11 that installs per-user, no admin prompt`

**③ 正文（可直接复制，获批后使用）**

```
Mods gave me permission to share this - thanks. I'm the author.

PopGlot translates whatever you have selected, in any window, and it also translates screenshots. v0.1.35 is out today.

Repo and full source: https://github.com/ethan-blue/PopGlot
Downloads: https://github.com/ethan-blue/PopGlot/releases

Why it exists: I wanted translation to be a hotkey, not a browser tab. Select anything, hit a key, read it in place. Screenshots go through local OCR first, so the picture does not become somebody else's problem unless you say so.

Windows-specific bits worth knowing:
- Installs per-user with no admin prompt by default. Both the installer (~59 MB, Inno Setup) and the portable zip (~88 MB) bundle .NET, so there is no separate runtime to install. Each asset ships a .sha256.
- On upgrade the installer detects where the previous version lives. Uninstalling keeps your config and history, which is how I'd want a translator to behave.
- Portable option: unzip and run PopGlot.exe. Nothing written to Program Files.

Default hotkeys (rebindable):
- Ctrl+Alt+W - translate selected text
- Ctrl+Alt+Space - screenshot translation
- Ctrl+Alt+Q - quick lookup
- Ctrl+Alt+O - open the workbench
- Ctrl+Alt+X - close the panel

Under the hood it is a WPF (.NET 10) shell over a Rust core (popglot-domain, popglot-core, popglot-ffi) separated by a C ABI. The translation logic is in the Rust core, so the shell can be replaced without rewriting it. MIT licensed.

Being straight with you about the parts that are not polished:
- The installer is currently unsigned, so Windows SmartScreen will warn before the first run. Every asset ships a `.sha256`: open the release page, download that file, and compare it yourself with `Get-FileHash PopGlot-v0.1.35-win-x64-setup.exe` in PowerShell (or `certutil -hashfile PopGlot-v0.1.35-win-x64-setup.exe SHA256` in cmd). I'd rather say this than have someone find out after downloading.
- Better results need your own key: OpenAI-compatible endpoints, OpenAI Responses, Anthropic, Gemini, or a local model through Ollama or LM Studio. Keys go into Windows Credential Manager. If you configure nothing, it falls back to Google's public translate, with MyMemory available when Google is unreachable. That fallback is ordinary machine translation - it is a convenience default, not the interesting part.
- Screenshots are OCR'd on your machine by default. Sending the image to a vision model takes an explicit opt-in. Safe offline mode blocks every outbound request, including that fallback, while a local Ollama or LM Studio endpoint keeps working. Logs never record source text, screenshots or keys.
- The clipboard is restored by sequence number, so if you copy something else while the panel is open, the app does not clobber your newer copy. That was fiddly to get right.
- If you want to actually read rather than just skim, the workbench puts source on the left and translation on the right with notes below. "Key points" is its own view and switching to it does not erase the translation.
- History is capped at 200 entries, 90 days and 4 MiB. Partial results are not stored.

Screenshots: docs/images/ in the repo (workbench and popup panel, light and dark).

I have no launch numbers to quote you - no user counts, no ratings. What I have is a tool I use all day. Tell me where it falls apart and I'll fix it.
```

**④ 发布时机与风险**

- 时机：周二到周四的北美工作时段。发之前至少在该 sub 正常评论一到两周，别用新号直接丢链接。
- 风险 1（最高）：未获 modmail 许可就发 → 删帖 + 可能被封。这一条是硬性的。
- 风险 2：标题带版本号且没有版本号以外的信息 → 可能被判为低质量或版本流水账。
- 风险 3：该 sub 对第三方软件天然警惕，任何看起来像 marketing 的话术会被质疑。保持上面的写法（缺点写在前）。
- Flair：版主给什么用什么；如果不确定，按列表选择而不自己假设。

> TODO: 缺 Windows 11 真机上的 demo GIF / 短视频；缺 SmartScreen 警告实际截屏（如果反而要放,能大幅降低信任成本）；缺官网 landing page（目前只有 GitHub releases）。

---

#### A.1.2 r/software

**① 规则与自我推广红线**

规则原文（Spam/Self-Promotion）：「Don't promote your own software unless its open source. Permanent ban. The one exception is if you share on Wednesdays, use the appropriate flair and provide something of value to the community rather than just the software.」

拆开就是四个条件，缺一不可：

1. 必须开源 —— PopGlot 是 MIT，**满足**。
2. 必须在周三发。
3. 必须用恰当的 flair（列表里有 Releases 一类的概括性 flair）。
4. 必须给出超出软件本身的价值，而不是只丢一个软件。

其它规则：

- Releases 规则：应当是新程序或重大更新，不要把这里当版本号流水账；开源「不是硬性要求但强烈建议」；不得有付费墙，可以有捐赠链接。
- 还有一个**不公开的 karma 门槛**，AutoModerator 会直接删掉低于门槛的提交。规则里明说门槛是秘密且不会太高，如果确实需要发可以留言让版主人工复核。
- 站点还有一个 "Weekly Discovery Thread"，允许自推但要求相关、有价值、透明，且不允许 AI 生成内容倾倒。

**② 标题候选**

1. `Three engineering decisions behind PopGlot 0.1.35: clipboard restoration by sequence number, local-first screenshot OCR, and a C ABI between WPF and Rust`
2. `How I split a Windows translator into a Rust core and a WPF shell (MIT, v0.1.35 released today)`
3. `[Release] PopGlot 0.1.35 - open source Windows translator for selected text and screenshots`

**③ 正文（可直接复制，周三发）**

```
Wednesday release post. MIT licensed, no paywall, no accounts. I'm the author.

Repo: https://github.com/ethan-blue/PopGlot
v0.1.35 downloads: https://github.com/ethan-blue/PopGlot/releases

PopGlot translates text you select anywhere on Windows, and translates screenshots. Rather than list features, here are the three problems that took actual work, since those are the parts that might be interesting here.

1. Restoring the clipboard without stealing newer copies

The obvious implementation of select-text translation is: copy the selection to the clipboard, read it, put the old clipboard contents back. The bug in that is ordering. If the user copies something else in the ~200ms the panel is open, the naive "restore" overwrites their newer copy. Users experience this as the tool eating their clipboard, which is worse than the tool doing nothing.

What I settled on: capture the clipboard's current sequence number before touching it, and only write back if the sequence number is still unchanged when I'm done. If it moved, someone else owns the clipboard now and I leave it alone. It is a small CAS loop and it removes an entire class of "this app ate my copy" reports.

2. Local OCR first, vision model only on explicit opt-in

For screenshots there are two paths with very different privacy properties: OCR the bitmap on this machine and send the extracted text, or send the bitmap to a vision model. The second gives better results on badly rendered text but means the pixels leave the machine. So local OCR is the default and the vision path requires an explicit choice per run. There is also a safe offline mode that blocks every outbound request - including the free fallback - while leaving a local Ollama or LM Studio endpoint reachable. That combination is what makes "offline" actually mean something rather than a marketing word.

3. Rust core behind a C ABI, WPF shell in front

The translation logic lives in Rust crates (popglot-domain, popglot-core, popglot-ffi) and is exposed over a C ABI. The WPF (.NET 10) shell is just a consumer of that ABI. The point is not language taste, it is that I can rewrite or replace the entire UI without touching the translation core, and I can test the core without spinning up WPF. The tradeoff is real though: every boundary crossing needs hand-written marshalling, and adding an interop bug manifests as a crash rather than a compile error.

Practical details:
- Two downloads: ~59 MB Inno Setup installer and ~88 MB portable zip. Both bundle .NET, so there is no separate runtime install. Each ships a .sha256.
- Installer defaults to per-user, no admin prompt. On upgrade it detects the previous install location. Uninstalling keeps your config and history.
- Default hotkeys: Ctrl+Alt+W select-text, Ctrl+Alt+Space screenshot, Ctrl+Alt+Q quick lookup, Ctrl+Alt+O workbench, Ctrl+Alt+X close panel.
- Engines: bring your own key for OpenAI-compatible endpoints, OpenAI Responses, Anthropic or Gemini; or point it at a local Ollama / LM Studio endpoint. Keys live in Windows Credential Manager.
- If you configure nothing: Google public translate by default, MyMemory selectable when Google is unreachable. Only the single selected snippet is sent, never the whole screen or document. This is ordinary public machine translation - it is there so the app works out of the box, not so anybody gets excited about it.
- Logs never record source text, screenshots or keys. History caps at 200 entries / 90 days / 4 MiB, and partial results are not stored.

Rough edges I'd rather name myself:
- The installer is currently unsigned, so Windows SmartScreen will warn before the first run. Every asset ships a `.sha256` - compare it with `Get-FileHash PopGlot-v0.1.35-win-x64-setup.exe` in PowerShell. I am not going to pretend the warning does not exist.
- Confidential or sensitive text should go to a local endpoint or offline mode, not the free fallback. I say this in the README too, and it is the one piece of advice I would want somebody to repeat back to me.

I have no usage numbers to quote. If it breaks on your machine, that's the most useful thing you can tell me.
```

**④ 发布时机与风险**

- 时机：**周三**（规则硬性要求）。周二/周四无效会被删。
- 风险 1：AutoModerator 的隐藏 karma 门槛可能直接吞帖。如果 30 分钟后没出现且没收到删除通知，发 modmail 请求人工复核（规则明确欢迎这么做）。
- 风险 2：如果把上面的技术内容删掉只留下载链接，就是规则里说的 "just the software" → 删帖。
- 风险 3：标题不要写成 `[Release] PopGlot 0.1.35` 这种纯版本号形式，容易被判为版本流水账。标题 1 或 2 更安全。
- 备选低风险通道：先投 "Weekly Discovery Thread"，该 thread 明确说 self-promotion 只要相关、透明、有价值就可以。

> TODO: 缺 demo GIF；缺本地 OCR 与视觉模型两条路径的对比截图；缺一条可以在评论里给出/issues 上的真实修复记录（增加可信度）。

---

#### A.1.3 r/opensource

**① 规则与自我推广红线 —— 当前不符合条件，请勿发帖**

我在 2026-10-29 通过公开报道（TechRepublic 转载 / The Register 原始报道的二次来源）核实到该 sub 近期改了准入规则。**我没有直接读到 Reddit 页面，Reddit 会拦截自动化读取规则页，所以正式发帖前再核一次 sidebar。** 报道一致提到的准入标准：

- 要推广的项目需满足：至少 1 年 actively maintained、3 个或更多贡献者、已有用户社区、OSI 批准的许可证。
- 发帖账号：至少 1 年且有大量 Reddit 贡献记录。
- 使用 AI、用 AI 构建、或者面向 AI 的项目不被接受（版主自己也承认这条没有客观判定方法，执行会有选择性）。
- 不收常见课程作业项目，不接受招募维护者/贡献者的帖子。
- 被删后去 modmail 争论 → 永久封禁 + mute。

对照 PopGlot：仓库创建于 2026-08-23（约 2 个月）、单人、会调用 LLM 接口做翻译。三项核心条件都不满足。

**结论：不发帖。** 报道里提到因为这次改版已经有人另起了 community（如被提及的 r/TrueOpenSource、r/OpenSourceAIAllowed），但这两个我没有独立核实，要用请先在 Reddit 上确认它真实存在且规则允许。

**② 标题候选（仅在项目满足条件时使用，现在不要用）**

1. `PopGlot - a MIT-licensed Windows translator with local-first screenshot OCR`
2. `PopGlot: Rust translation core behind a C ABI, replaceable WPF shell in front`
3. `PopGlot - translate selected text and screenshots on Windows without handing the image to a server`

**③ 正文（留作条件满足后的存档，现在不要发）**

```
PopGlot is a Windows desktop translator for selected text and screenshots, MIT licensed.

Source: https://github.com/ethan-blue/PopGlot
Releases: https://github.com/ethan-blue/PopGlot/releases

The design goal was a translation tool that does not require changing how you work: a hotkey, a panel, done. The privacy-relevant design choices:

- Screenshots are OCR'd on the machine by default. Sending the bitmap to a vision model requires an explicit opt-in; it is never the default.
- Safe offline mode blocks every outbound request, including the no-configuration fallback, while leaving local Ollama and LM Studio endpoints working.
- Only the single snippet you selected is sent, not the surrounding document or the whole screen.
- API keys are stored in Windows Credential Manager, never in a config file next to your data. Logs never record source text, screenshots or keys.
- History is bounded: 200 entries, 90 days, 4 MiB. Partial results are not stored.

Architecture: the translation logic is Rust crates (popglot-domain, popglot-core, popglot-ffi) exposed over a C ABI; the UI is a WPF (.NET 10) shell that consumes that ABI. The shell is replaceable without touching the core, which was deliberate - it means a different UI or a different platform shell is a project someone can start without asking me to rewrite anything.

Engines are pluggable: OpenAI-compatible endpoints, OpenAI Responses, Anthropic, Gemini, or a local model server. With nothing configured, Google public translate is used by default with MyMemory available when Google is unreachable. That default is ordinary machine translation.

Limitations, stated up front: the installer is unsigned, so SmartScreen warns; strongest output needs your own API key; the free route is a third-party service and should not be used for confidential text.

v0.1.35 (2026-10-29) ships a ~59 MB installer and a ~88 MB portable zip, both bundling .NET, both with a .sha256. Default hotkeys: Ctrl+Alt+W selected text, Ctrl+Alt+Space screenshot, Ctrl+Alt+Q quick lookup, Ctrl+Alt+O workbench, Ctrl+Alt+X close panel.
```

**④ 发布时机与风险**

- 时机：不满足条件前，不发。
- 风险 1（决定性）：现在发帖几乎必被删，且去问为什么 = 永久封禁 + mute。
- 风险 2：即使将来满足了维护年限和贡献者数量，"use AI" 这条仍可能被援引，因为该项目的核心功能之一是调用 LLM。不要抱侥幸心理。
- 替代路径：把精力放在 A.1.6 / A.1.7 以及 Task B 的 awesome 列表上，那些地方开源本身就是加分项。

> TODO: 正式发帖前需人工再核一次 r/opensource 当前 sidebar 规则；需要确认是否真的存在允许 AI 项目的替代 community。

---

#### A.1.4 r/translator

**① 规则与自我推广红线 —— 强烈建议不发**

规则逐条核对：

- `[#R3] No Spam / Advertising for Services or Goods`：任何试图宣传你有个人利益的东西的帖子，「will be removed on sight」。此外不许宣传商品、不许招募翻译服务。
- `[#T1] No Joke / Fake / Machine Translations`：明确包含 Google / Bing 的机器生成译文，反复违反可能封禁。
- `[#R4] No Non-Translation Requests`：除了 Meta / Community 帖子之外，所有帖子必须是一个翻译请求。

一个「替 ruined workflow 做宣传」的帖子会同时撞上 R3 和 R4。而且这个社区的基础假设是志愿者人力翻译，机器翻译是最被反感的东西。

**结论：不要在这里发 launch。** 如果你真的想在这个社区露面，唯一合规的方式是：先去回答别人的翻译请求、积累真实贡献记录，然后在个人资料里挂上 PopGlot 链接，不主动推销。

如果你觉得一定要尝试，唯一可能有开口的形式是先 modmail 问能不能发一个 Meta / Community 帖：

> Hi mods,
>
> I built an open source Windows translator (MIT): https://github.com/ethan-blue/PopGlot
>
> I know rule R3 removes anything advertising something the poster has an interest in, and rule R4 restricts posts to translation requests outside meta/community threads, so I want to ask before posting anything.
>
> The only angle I think is genuinely relevant to this sub is the opposite of a pitch: the app does local OCR on screenshots by default and never sends the bitmap anywhere without an explicit opt-in, and it has a hard offline mode that blocks every outbound request. If that is of interest as a Meta post I'd write it up without a download link and let people decide for themselves. If it is not appropriate, that is fine and I won't post.
>
> Thanks.

**② 标题候选（仅在版主明确同意 Meta 帖后使用）**

1. `[META] A local-first OCR setting that keeps screenshots off third-party servers - looking for how you handle this`
2. `[META] Which text should never be pasted into a free machine-translation box - a question for the volunteers`
3. `[META] Building a translation tool that refuses to send images unless you opt in - feedback from people who translate for a living`

**③ 正文（仅在获批后使用，且不带下载链接）**

```
Mods approved this as a meta post - no download link below, by request.

I wrote a Windows translator, and while building it I kept running into questions I don't think I can answer correctly myself. I'd like yours.

The one that matters most: when someone hands you a screenshot of text rather than the text itself, what do you do with it? My tool OCRs the image on the user's machine by default and only offers sending the bitmap to a vision model as an explicit, per-use choice. I chose that default because it seemed obvious, but I am not the person whose livelihood depends on the text staying private, so "obvious" may be doing too much work in that sentence.

Second question: I set a hard offline mode that blocks every outbound request, including the no-configuration fallback to public machine translation, while still letting a locally hosted model answer. Is that the right shape, or does "offline" as a mode give users false confidence because anything you type is still on a machine that may be synced somewhere else?

Third: how much should a tool warn before the user pastes something they shouldn't? Right now I warn once about the free fallback being a third-party service, and I say it in the README and the post, but I have no intuition for whether that is enough, too much, or the wrong register entirely.

For context on my assumptions so you can dismantle them: I assumed machine translation output should never be presented as final, only as a first pass that a human reviews; I assumed partial translations should not be stored at all; and I assumed history should be bounded and easy to wipe (currently 200 entries / 90 days / 4 MiB). Tell me where those are wrong. This sub is full of people who do this properly and I have been guessing.
```

**④ 发布时机与风险**

- 时机：只有在拿到明确的 modmail 许可后。
- 风险 1（决定性）：未获批发帖 = 秒删，反复 = 封禁。
- 风险 2：即使获批，语气必须是「求教」而不是「介绍产品」。上面的正文刻意不给下载链接，就是为了不触发 R3。
- 风险 3：不要把得意的功能写成功劳簿。这个社区对「自动化替代译者」高度敏感。
- 更聪明的做法：完全放弃这个 sub。它的成员是译者，不是桌面工具买家，即便 tolerated，转化也不会好。

> TODO: 需要先建立真实贡献记录（回答若干翻译请求）才谈得上后续互动；缺版主许可；缺「给译者的工作流」真实调研（上面三个问题如果换成引用真实译者说法会强很多）。

---

#### A.1.5 r/rust

**① 规则与自我推广红线**

公开规则（六条）：遵守 Rust 行为准则；必须与 Rust 相关（不相关的用 text post 说明关系）；批判要有建设性；不要反复炒旧话题；禁止低质量内容；以及关键的一条 —— **code dump（单纯丢仓库链接）不再受欢迎，除非特别有新闻价值**（例如突破性应用或大公司项目），新库公告需要满足至少 4 个月的开发周期且已实际用于验证其设计。报告还提到 **AI 生成的提交可能被版主酌情删除**。

社区配套的合规途径有两条：

- 每周的 "What's Everyone Working on this Week?" 汇总帖 —— 规则原文的建议就是「只是想分享的话，去这里发 top-level comment」。
- "Arts-and-crafts posts are permitted on weekends"（展示 / 随手作品类的帖子限周末发）。

对照 PopGlot：仓库 2026-08-23 创建，约 2 个月，不到 4 个月；单代码不是库而是应用。所以 **不要走公告路线**。

**② 标题候选**

1. `PopGlot - a Windows translator whose UI shell talks to its Rust core over a hand-written C ABI`
2. `Putting a Rust translation core behind a C ABI so I can throw away the WPF shell without touching it`
3. `Weekend project: a Rust translation core (popglot-domain / -core / -ffi) with three different front-end strategies`

**③ 正文（可直接复制，周末发，或改短后投每周汇总帖）**

```
Weekend post. I'm the author of this and I wrote this text by hand.

I have a Windows translator called PopGlot. The interesting part for this sub is not that it translates - it is the boundary between the two halves.

Source: https://github.com/ethan-blue/PopGlot
Releases: https://github.com/ethan-blue/PopGlot/releases

Shape: three Rust crates - popglot-domain (the concepts and rules), popglot-core (the actual translation orchestration, engine adapters, history boundaries), popglot-ffi (the flat C ABI). Above that sits a WPF (.NET 10) shell that knows nothing about translation; it only knows how to call the C ABI and render what comes back.

Why bother, given that "rewrite it all in Rust" was also on the table:

- Windows UI is not a thing I wanted to write in Rust for this app. Global hotkeys, an overlay panel that has to behave over fullscreen applications, Credential Manager access, tray semantics - all of it is dramatically less code in WPF, and all of it is churn I expect to redo several times as I learn what users actually need.
- The translation logic is the part I want to be able to reason about and unit test without spinning up a window. Keeping it behind a pure C ABI means it has a test surface that does not involve a message pump.
- It also means someone who wants a different UI - not WPF, or not Windows - can start from the core and never ask me for anything.

The cost, honestly: every value crossing the boundary is hand-written marshalling. A changed signature that the compiler would normally catch becomes a crash at runtime or, worse, a truncated string that looks fine in one language and not another. Strings going out get an explicit length and a caller-provided buffer, ownership is documented per function rather than implied, and there are unit tests over the FFI surface specifically because the type system is not there to help. If I were doing this again I would probably look harder at generating the bindings rather than writing them, and I am not going to pretend we considered cbindgen properly early enough.

The rest of the app, briefly, since it explains constraints the core has to respect:
- Screenshots are OCR'd locally by default; sending a bitmap to a vision model needs an explicit opt-in.
- Safe offline mode blocks every outbound request including the no-key fallback, while a local Ollama or LM Studio endpoint still answers. So the core has to treat "the network is off" as a supported steady state, not an error path.
- Engines are adapters: OpenAI-compatible, OpenAI Responses, Anthropic, Gemini, plus local endpoints. Adding one means writing another adapter behind the same trait, which is the only real payoff of the layering so far.
- History is bounded (200 entries / 90 days / 4 MiB) and partial results are never persisted, which turned out to matter for the streaming paths.

v0.1.35 (2026-10-29). MIT. ~59 MB installer and ~88 MB portable zip, both bundling .NET.

I'd genuinely like feedback on the FFI boundary specifically - whether the three-crate split is earning its keep, and where a Rust person would have drawn the line differently.
```

**④ 发布时机与风险**

- 时机：**周末**（UTC 周一前的周末窗口，规则允许 arts-and-crafts），或者作为 top-level comment 投到每周的 "What's Everyone Working on this Week?" 汇总帖 —— 后者风险最低。
- 风险 1（最高）：把它包装成 "Announcing PopGlot 0.1.35" 会被当作 code dump 删掉。入口必须是「C ABI 边界的取舍」这个技术议题。
- 风险 2：不要用 AI 润色这段文字。该 sub 明确说了疑似 AI 生成的内容可被删除，而且这篇文章正因为是 Rust 视角才站得住。
- 风险 3：项目约 2 个月，未满 4 个月的「新库」门槛。这不是新库的公告，而是一个已经在用的应用的架构讨论，正文不要宣称 "new library"。
- 风险 4：正文里如果出现 "blazingly fast" 一类的 Rust 社区梗会更自然，但不要为了气氛牺牲诚实。

> TODO: 缺 demo GIF；缺 FFI 边界的一小张结构图（比文字更能留住人）；缺实际基准数据（所以文案里完全不提性能 —— 保持现状，不要临时补数据）。

---

#### A.1.6 r/dotnet

**① 规则与自我推广红线**

该 sub 就自我推广规则做过投票并因此修改了规则（规则变更帖里说明了来龙去脉）。新规则原文要求自我推广帖同时满足：

1. **必须在新西兰时间周六（GMT+12）发布**；
2. **必须使用新的 "Promotion" flair**；
3. **不能由 AI 撰写**（"Put some effort into it if you want other people to check it out"）；
4. **仅限于 major 或 minor 版本** (例如 "v1.3") —— 明文是为了防止用碎版本号刷存在感。

不满足以上任一条的推广帖一律删除。

对照 PopGlot：目前是 `0.1.35`，按 semver 是 patch 级。这直接落在第 4 条的射程内。

**这里有一个真实的风险，我不打算绕过去。** 两条可选路：

- 路线 A（推荐）：**等到下一个 minor 版本（0.2.0）再发**，并且周六 NZT 发、挂 Promotion flair、手写。这样四条全满足。
- 路线 B：本周六照发，但把帖子的落脚点放在 WPF/.NET 10 侧的技术方案上，而不是 "0.1.35 is out"。仍然可能被判为 patch 版本推广而删。要发就接受这个概率。

两条路线共用下面这段正文。

**② 标题候选**

1. `PopGlot - a .NET 10 WPF translator over a Rust core behind a C ABI (MIT)`
2. `What I learned building an always-on-top WPF panel that has to work over fullscreen apps`
3. `PopGlot: treating the WPF shell as a replaceable front end for a translation engine`

**③ 正文（可直接复制）**

```
Saturday promo post per sub rules, Promotion flair, written by me. I'm the author.

PopGlot is a Windows translator for selected text and screenshots. The .NET angle: the UI is a WPF app on .NET 10, and it is deliberately only one possible consumer of the translation core, which lives in Rust behind a C ABI.

Source: https://github.com/ethan-blue/PopGlot
Version discussed here: 0.1.35 - https://github.com/ethan-blue/PopGlot/releases

Why WPF in 2026 rather than anything newer: the entire hard part of this app is Win32 surface area, not XAML. A global hotkey that survives playing nicely with every other app that wants Ctrl+Alt+something; a panel that has to appear above fullscreen content without stealing focus; Credential Manager access so API keys are not sitting in a JSON file next to user data; tray lifetime and per-user install with no admin prompt. Every one of those is dramatically less code in WPF than in any UI framework I'd otherwise pick, and I expect to rewrite the UI at least twice as I learn what people actually want. Keeping the shell swappable means that rewrite never touches the translation logic.

Things that bit me that I did not expect:

- Clipboard ownership. The naive "save clipboard, read selection, restore clipboard" flow silently discards whatever the user copied during the ~200ms the panel was open. Users experience that as the app eating their clipboard. The fix that stuck was to capture the clipboard sequence number before touching it and only write back when the sequence number is unchanged; if it moved, leave it alone. It is a compare-and-swap and it removes a whole class of bug reports.
- Focus stealing. An overlay panel that grabs focus makes the underlying selection disappear. Getting the panel to present without activating took more iterations than the rest of the UI combined.
- Per-user install semantics. The Inno Setup installer defaults to per-user with no UAC prompt, detects the previous install location on upgrade, and leaves config and history behind on uninstall. Users upgrading across those two modes was the source of several confusing reports early on.
- Bundling .NET. Both the ~59 MB installer and the ~88 MB portable zip bundle the runtime so there is no separate install step. That is a real size penalty and I think it is worth it; a translator that fails because the user has the wrong runtime patch is worse than 60 MB.

Being straight about the state of it:
- The installer is currently unsigned, so Windows SmartScreen will warn before the first run. Every asset ships a `.sha256`; verify it with `Get-FileHash PopGlot-v0.1.35-win-x64-setup.exe` in PowerShell.
- The better engines need your own key: OpenAI-compatible, OpenAI Responses, Anthropic, Gemini, or a local Ollama / LM Studio endpoint. Configure nothing and you get Google's public translate, with MyMemory selectable when that is unreachable - ordinary machine translation, there so the app works out of the box.
- Screenshots OCR locally by default; vision models require explicit opt-in. Safe offline mode blocks every outbound request but still lets a local endpoint answer.
- MIT. No telemetry claims beyond the above, no usage numbers to quote.

Default hotkeys: Ctrl+Alt+W select-text, Ctrl+Alt+Space screenshot, Ctrl+Alt+Q quick lookup, Ctrl+Alt+O workbench, Ctrl+Alt+X close panel.

Happy to talk about the Win32 bits, the interop boundary, or any part of this that is wrong.
```

**④ 发布时机与风险**

- 时机：**周六 NZT（GMT+12）**。这条是硬性的，北京时间周六早些时候通常落在 NZ 周六内，但发布前自己换算一次。
- 风险 1（最高）：`0.1.35` 是 patch 版本号，规则要求 major/minor。优先等到 0.2.0。
- 风险 2：必须挂 "Promotion" flair，漏了就删。
- 风险 3：**不要过 LLM 润色**。这条规则写得非常直白。
- 风险 4：正文里的 ".NET angle" 必须是真的。上面这篇之所以可能被容忍，是因为它讨论的是 Win32/WPF 的实际问题，而不是功能清单。

> TODO: 缺 demo GIF；缺 SmartScreen 截屏；等 0.2.0 再发可以解决 patch 版本问题。

---

#### A.1.7 r/SideProject

**① 规则与自我推广红线**

这是少数明确欢迎自推的社区。规则精神：**posts must show the project itself, not ads** —— 必须有真实产物：能跑的东西、真实截图、可用链接，不能是等待列表或邮箱墙。

会被删的：

- 纯营销文案 / "check out our new…" 没有实质内容。
- 只在需要宣传时才出现、只发自己帖子而不回应别人的账号 —— 这里的社交契约是互惠：发一次自己的东西，就要认真回应若干别人的项目。
- 只在淡入淡出产己帖的账号 —— 这里的社交契约是互惠：发一次自己的东西，就要认真回应若干别人的项目。
- 转售/白标/联盟/返利链接，以及刷票与小号。

对 PopGlot 有利的是：我们产品价格是零、开源、可以直接下载运行，天然满足「show the product」。

不利的是：**没有 demo GIF，只有四张静态 PNG**。所以要在正文里坦白说明「截图是静态的」，并把仓库 `docs/images/` 里的截图直接摆出来。

**② 标题候选**

1. `I got tired of tab-switching to translate, so I wrote a hotkey translator for Windows (MIT)`
2. `Built a Windows translator where screenshots are OCR'd on your machine by default - v0.1.35`
3. `Side project update: PopGlot, a selected-text and screenshot translator that finally stops stealing my clipboard`

**③ 正文（可直接复制）**

```
I'm the author. This is the honest version, including the parts that are not done.

The problem was small and constant. I read a lot of material that is not in my first language, and translating meant: select, copy, alt-tab, paste into a browser box, read, alt-tab back, lose my place. Every one of those steps is trivial and the sum of them drove me mad. So I wrote a translator whose whole pitch is "select text, press a key, read it where you are."

What it does now:

- Select text in any window, Ctrl+Alt+W, get a panel with the translation. Ctrl+Alt+Q for a quick lookup, Ctrl+Alt+O for the fuller workbench, Ctrl+Alt+X to dismiss.
- Ctrl+Alt+Space takes a screenshot and translates what is in it. This is the feature people assume exists and usually does not.
- The workbench keeps source on the left and translation on the right, with your own notes below the translation. There is a separate "key points" view - importantly it does not wipe your translation when you switch to it, which is a bug every other tool I tried had in some form.

Three decisions I'd defend:

1. Screenshots are OCR'd on your machine by default. Sending the picture to a vision model gives better results on ugly text, but it means your pixels go somewhere. So local OCR is the default and the vision path takes an explicit yes each time. There is also a hard offline mode that blocks every outbound request - including the free fallback - while still letting a local Ollama or LM Studio model answer.
2. The clipboard is restored by sequence number. This sounds like trivia and it is not. Every naive version of this app will overwrite a newer thing you copied while the panel was open. Mine checks whether the clipboard sequence number moved before writing back, and if it did, it backs off.
3. History is capped and mean about what it stores: 200 entries, 90 days, 4 MiB, and partial results are never saved. A translation tool left running for months should not quietly become an archive of everything you read.

What it costs you and what it does not do:

- It's free and MIT: https://github.com/ethan-blue/PopGlot. No account, no paid tier, no upsell. I have no usage numbers, no ratings - this is a thing I built and use.
- To get genuinely good output you bring your own key (OpenAI-compatible, OpenAI Responses, Anthropic, Gemini) or point it at a local model server. If you configure nothing it falls back to Google's public translate, and MyMemory can be selected when Google is unreachable. That fallback is ordinary public machine translation. It is there so the app works out of the box, not because it's special.
- The installer is unsigned. SmartScreen will warn the first time you run it. I am not pretending otherwise; every download ships a .sha256 so you can check it yourself.
- Confidential text should go to a local endpoint or offline mode, not the free fallback. The README says this too.

v0.1.35, out today: https://github.com/ethan-blue/PopGlot/releases
~59 MB installer (per-user, no admin prompt, detects your old install location, keeps your config and history if you uninstall) or ~88 MB portable zip (unzip, run PopGlot.exe). Both bundle .NET.

Screenshots of the workbench and the popup panel are in docs/images/ in the repo. They are static - I haven't cut a demo video yet, which is on me; a tool that lives entirely in hotkey muscle memory does not photograph well.

What I actually want from you: tell me which window, which app, or which kind of text it breaks on. Those are the reports I can act on, and they're worth more to me than upvotes. I'll answer everything in this thread.
```

**④ 发布时机与风险**

- 时机：周六到周日的上午到下午是这里公认的高活跃时段；工作日晚上次之。留出整天回评论 —— 发了就跑的作者和 spam 无法区分。
- 风险 1：只有静态截图、没有 demo GIF，在这个很看重「看到它真的在动」的社区会被低估。至少要在正文里坦白（上面的写法已包含）。
- 风险 2：不要把帖子写成新闻稿。原文必须是手写、第一人称、有具体细节。
- 风险 3：发完之后 24 小时内回复每一条评论。这是这里唯一的算法。
- 风险 4：同一天不要把这个帖子复制到其它 sub。这是全站 spam 判定的头号信号。

> TODO: **最缺 demo GIF 的就是这里**（10-20 秒、真实屏幕、展示三次呼出 + 一次截图翻译 + 剪贴板未被覆盖）；缺一条 README 的 one-line hero 图；缺 landing page（不强制，因为规则禁止仅仅给 landing page）。

---

### A.2 Hacker News (Show HN)

**① 标题规范与正文**

HN 的规则很短：Show HN 是给「你自己做的、别人能立刻试的东西」。按 Show HN 的通行实践：要求作者本人在场并能参与讨论，禁止发布 AI 编辑过的文本，且「quickly-generated one-offs」不受欢迎。社区约定：**链接指向那个可以直接试的东西**（产品 / live demo / repository），并且**由作者手写、不用 AI 编辑**；不要索要点赞，不要删帖重发。

标题规范：以 `Show HN:` 开头，然后写「它是什么」，不要写「它有多厉害」。禁止全大写、感叹号、标题党形容词。**实操上标题长度建议 ≤ 80 字符**。

**② 标题候选（≤80 字符，纯 ASCII）**

1. `Show HN: PopGlot - Windows translator for selected text and screenshots` (70 chars)
2. `Show HN: PopGlot - screenshot OCR stays local, Rust core behind a C ABI` (73 chars)
3. `Show HN: PopGlot - a Windows translator that will not steal your clipboard` (71 chars)

建议用标题 1。标题 3 的「不会抢你剪贴板」吸引点击但容易被质疑标题党；如果评论区第一条解释好了可以用。

**③ 提交内容（URL + Text 字段）**

URL 字段：`https://github.com/ethan-blue/PopGlot`

Text 字段（Show HN 允许且建议在 text 字段写背景）：

```
I read a lot of material that is not in my first language, and translating meant copy, alt-tab, paste into a browser box, alt-tab back, lose my place. So I built a Windows translator that is a hotkey instead of a destination: select text anywhere and press Ctrl+Alt+W, or press Ctrl+Alt+Space and translate a region of the screen.

Three of the decisions were actually hard and are the only reason I'm posting here rather than writing a README bullet list.

Clipboard ownership. Every naive implementation does save-clipboard / read-selection / restore-clipboard. If the user copies something during the few hundred milliseconds the panel is open, that "restore" silently destroys their newer copy. PopGlot records the clipboard sequence number before it touches anything and only writes back if the sequence number has not moved; otherwise it leaves the clipboard to whoever owns it now. It is a compare-and-swap, and it converts what users experience as "this app ate my clipboard" into "this app did nothing weird".

Screenshot privacy is two different products wearing one name. OCR the bitmap on this machine and translate the extracted text, or send the bitmap to a vision model and get better results on badly rendered text. The second one means your pixels leave. So local OCR is the default and the vision path needs an explicit opt-in every time. There is also a strict offline mode that blocks all outbound requests including the no-configuration fallback, while a locally hosted model still answers - which is why the core has to treat "network is off" as a supported state rather than an error path.

A C ABI between language halves. The translation logic is Rust (popglot-domain, popglot-core, popglot-ffi) behind a flat C ABI; the UI is a WPF .NET 10 shell consuming that ABI. I did this because I expect to rewrite the UI several times - global hotkeys, an overlay that must appear over fullscreen applications, Credential Manager access - and none of that churn should touch translation logic I want to unit test without a message pump. The cost is real: every value crossing that boundary is hand-written marshalling, and a signature change the compiler would normally catch becomes a runtime crash or a truncated string. Whether that trade is worth it at this size is the thing I most want told to me.

Engines are adapters: OpenAI-compatible, OpenAI Responses, Anthropic, Gemini, or a local Ollama / LM Studio endpoint, keys in Windows Credential Manager. If you configure nothing you get Google's public translate, with MyMemory selectable when Google is unreachable. That fallback is ordinary machine translation - it exists so the app works out of the box, not as a selling point. Only the single selected snippet is ever sent, never the whole document. Logs never record source text, screenshots or keys. History is bounded: 200 entries, 90 days, 4 MiB, and partial results are never persisted.

Things that are not good yet: the installer is unsigned so SmartScreen warns on first run (every asset ships a .sha256); there is no demo video, only static screenshots in docs/images/; and I have no users or numbers to quote you.

MIT. v0.1.35: https://github.com/ethan-blue/PopGlot/releases (~59 MB installer bundling .NET, ~88 MB portable zip).
```

**第一条评论（务必由作者本人发，真正測試后才发）：**

```
Author here. Two things I'd especially like wrong answers to:

1. The three-crate Rust split behind a hand-written C ABI. At this project size, is that boundary earning its keep, or should I have written the whole thing in Rust and accepted a worse Windows UI? I can defend it for testing surface, and I cannot defend it for marshalling cost.

2. The clipboard compare-and-swap. Is comparing the sequence number actually sufficient on Windows, or are there races I have not hit yet because I have only ever had one machine to test on?

Happy to answer anything about the interop boundary, the OCR path, or why the workbench keeps "key points" in a separate view from the translation.
```

**④ 发布时机与风险**

- 时机：工作日早上（美东上午）通常比周末更容易被 Publisher covered。不要在集合首页已经有一整天热度的时候发。
- 风险 1（最高）：**不要让我/任何 LLM 润色这段**。HN 的 Show HN 实践明确排斥 AI 编辑过的文本，而这个群体对「什么时候在写 await 代码」极其敏感。提交前自己读一遍。
- 风险 2：不要索要点赞，不要让朋友在评论里刷「great work」。被发现就是 permanent reputation penalty。
- 风险 3：不要因为没有热度就删帖重发。HN FAQ 允许在一年内未获得显著关注的情况下少量重发，且有重大改版时也可以重发一次。
- 风险 4：会把识别和说明清楚写在第一屏（上面已写），否则评论区第一条一定是这个。
- 风险 5：准备好被问 "why not just use X"。不要把回答写成与竞品的横向对比；不知道就说不知道。
- Show HN 的中位数批 pitcher 数据：满分是没有保证的，不要对此做出任何销量或流量预测。

> TODO: 缺 demo GIF（HN 整站不支持插图，但可以在 repo README 首屏放 GIF，让 Show HN 落地页一眼可见）；缺 landing page；缺「首次安装 / 首次运行」体验的整体验证说明。

---

### A.3 Product Hunt

**① 字段与限制**

按 Product Hunt 官方提交的字段说明：

- **URL** —— 直链到产品主页面，可以是 GitHub repo。**禁止短链接和带追踪参数的链接**（bit.ly、UTM 一律拒绝）。同一产品在 6 个月内再次提交另有规则。
- **Name** —— 只写产品名，不加描述、不加 emoji（除非 emoji 本身属于名字）。例：`PopGlot`。不能写 "Best translator app"。
- **Tagline** —— 上限 **60 字符**。简短说明产品做什么，不要夸张。官方给的正例是描述一个具体结果，反例是 "The most ameozing app in the app store"。
- **Description** —— 上限 **500 字符**（社区普遍流传 260 字符是「展示效果更好」的区间，不是硬限制）。
- **Launch tags** —— 最多 3 个。
- **Thumbnail** —— 必须有，方形，建议 240x240；图片资产建议 1270x760，一般准备 4 张以上。
- **First comment（Maker comment）** —— 字数没有硬性限制，但官方与社区的一致做法是 200-300 词：为什么做、和别人有什么差别、一个具体的提问。
- 上线时间：PH 的一天按太平洋时间午夜重置，常见的做法是排到 **PST 12:01am**，最长可以提前 1 个月排期。

**② Tagline（三个候选，均 ≤60 字符）**

1. `Translate selected text and screenshots on Windows` (49 chars)
2. `Windows screen translator with local OCR and your own key` (57 chars)
3. `Select it, screenshot it, translate it - without leaving the window` (61 chars → **超 1 字符，改用 1 或 2**)

确认可用：1 和 2。**推荐 1**，因为它一句说完「做什么 + 在哪个平台」。

**③ Description（≤500 字符，可直接粘贴）**

```
PopGlot translates what you select anywhere on Windows, and translates screenshots too. Select text and press Ctrl+Alt+W; press Ctrl+Alt+Space to translate a region of the screen. Screenshots are OCR'd on your machine by default - sending the picture to a vision model needs your explicit yes, every time. Bring your own key for OpenAI-compatible, OpenAI Responses, Anthropic or Gemini endpoints, or point it at a local Ollama or LM Studio model; with nothing configured it falls back to Google's public translate. Keys live in Windows Credential Manager and logs never record source text, screenshots or keys. Open source under MIT.
```

字符数：约 700 → **超了**。下面是压缩到 500 以内的版本，请直接用这一版：

```
PopGlot translates what you select anywhere on Windows, and translates screenshots too. Select text and press Ctrl+Alt+W; press Ctrl+Alt+Space to translate a region of the screen. Screenshots are OCR'd locally by default - sending the image to a vision model needs an explicit yes each time. Bring your own key for OpenAI-compatible, Anthropic or Gemini endpoints, or a local Ollama or LM Studio model. With nothing configured it uses Google's public translate. Open source, MIT.
```

（约 497 字符，含空格。上线前请在表单里实际数一遍。）

**④ First comment（Maker comment，可直接粘贴）**

```
This started because translating was costing me four steps I resented: select, copy, alt-tab to a browser, paste. Every step was trivial and the sum of them made me avoid reading things in languages I am learning. So I built the translator I actually wanted: select text anywhere, press Ctrl+Alt+W, read the result where you are. No app to switch to.

The part I care most about is screenshots. Ctrl+Alt+Space grabs a region and translates it, and by default the OCR happens on your machine. Sending the bitmap to a vision model produces better results on badly rendered text, but it also means your pixels leave; so that path requires an explicit yes every single time and is never the default. There is also a strict offline mode that blocks every outbound request - including the free fallback - while still letting a locally hosted model answer, because "offline" should be a real state, not a marketing word.

The other decision worth explaining: the clipboard. Every naive version of this kind of tool does save-clipboard, read-selection, restore-clipboard, and every naive version will silently destroy something you copied during the few hundred milliseconds the panel was open. PopGlot records the clipboard sequence number first and only writes back if it has not moved. Small detail, and the difference between a tool people trust and a tool people uninstall.

Honest caveats, because you should know before downloading: to get genuinely good output you bring your own API key, or point it at Ollama or LM Studio. Configure nothing and it falls back to Google's public translate, which is ordinary machine translation. The installer is currently unsigned, so SmartScreen will warn on the first run - every download ships a .sha256 so you can verify it yourself. And I have no usage numbers or ratings to wave at you; this is a thing I built for myself and use every day.

MIT licensed, no accounts, no paid tier. ~59 MB installer or ~88 MB portable zip, both bundling .NET. The workbench keeps source on the left, translation on the right, your notes below, and puts "key points" in a separate view so switching to it does not wipe your translation.

One ask, concretely: tell me which app, window type, or kind of text breaks on you. Those reports are the most useful thing you can give me and I read all of them.
```

**⑤ 上线前需要准备的素材清单**

必须有的：

- [ ] Tagline ≤ 60 字符（上面给了，表单里再数一遍）
- [ ] Description ≤ 500 字符（上面给了）
- [ ] **Thumbnail 240x240**（目前没有统一的品牌 logo → 见下）
- [ ] 至少 4 张 1270x760 的产品图
- [ ] Maker 头像 + maker 资料
- [ ] 产品 URL（用 `https://github.com/ethan-blue/PopGlot`，**不得**带 UTM 或短链）
- [ ] 最多 3 个 launch tags
- [ ] First comment（上面给了）
- [ ] 上线当天整天在线回评论（PH 的排名机制奖励作者活跃）。

建议有的：

- [ ] 30-60 秒 demo 视频（YouTube 链接）
- [ ] 静态的产品图之外，至少一张「before / after」对比图
- [ ] 上线前 2 周开始在 PH 上正常互动（点赞、评论别人的产品），新号直接 launch 效果很差
- [ ] 通知列表（PH 支持上线前收集 notify me 邮箱）

**上线前请不要做的：**

- 不要索要点赞。这是明确违规且会被社区看见。让人去「试用并留下真实评论」。
- 不要在 launching 当天同时把同一段文案铺到 Reddit。先把 PH 的 24 小时撑住。

> TODO: **PH 缺的东西最多**：品牌 logo / 240x240 缩略图；1270x760 产品图（现有四张 PNG 尺寸未知，需要重裁或补图）；demo 视频；统一的 landing page；maker 的 PH 账号历史互动。**我建议 PH 排在 demo GIF 和 landing page 补齐之后再上线**，否则 launch 页会显得很空。

---

### A.4 目录站（AlternativeTo / LibHunt / OSS Software / Slant 类）

**总原则：** 目录站的价值是长期搜索流量，不是 launch 流量。提交的先后顺序应该是「先把不需要付费、最契合的做掉」，再考虑付费加速。

#### A.4.1 AlternativeTo

**① 提交要求（核实自官方 FAQ）**

- 账号需要**先完成邮箱验证**才能提交新应用；投票、评论、编辑已有应用则在注册后立即可用。
- 提交入口：右上角用户图标 → **"Suggest new application"**。
- 需要填：Platforms、License、Descriptions、Tags 等。
- 提交后进入 review backlog。**新应用通常要等「几个月」**，且队列不是「慢」而是无上界。表单最后提供 **$5 一次性「提前审核」**（约 1-2 个工作日）。
- 已成为该应用开发者的人可以向官方申请 developer access，之后编辑自己这页会即时生效。
- **「alternatives」是这里的全部游戏**：提交完他们就提醒你，没有 alternatives 的应用在搜索里几乎不可见。所以要主动去竞品页面 propose 自己。
- 可选字段：可以填一次性购买价格与订阅价格（官方称填了更容易被找到）。
- 他们的 decline list 明确拒绝：目录站、在线工具集合、AI 工具清单站、评测/对比站、教程指南、LLM wrapper、单一用途的转换器/生成器/计算器、个人站与博客。**PopGlot 不在任何一条上。**

**② 认可程度：推荐** —— 这是所有目录站里最契合的一个，因为「找 X 的替代品」正是划词翻译工具的典型搜索场景，且免费。

**③ 填写文案**

Short description（一句话）：

```
A Windows translator for selected text and screenshots, with local OCR by default.
```

Full description：

```
PopGlot translates text you select in any window, and translates screenshots. Hotkeys are Ctrl+Alt+W for selected text, Ctrl+Alt+Space for a screen region, Ctrl+Alt+Q for a quick lookup, Ctrl+Alt+O for the workbench and Ctrl+Alt+X to dismiss the panel.

Screenshots are OCR'd on your machine by default. Sending the picture to a vision model gives better results on badly rendered text but requires an explicit opt-in each time; there is also a strict offline mode that blocks every outbound request while leaving locally hosted models working.

Bring your own key for OpenAI-compatible, OpenAI Responses, Anthropic or Gemini endpoints, or point PopGlot at a local Ollama or LM Studio model. With nothing configured it falls back to Google's public translate, with MyMemory selectable when Google is unreachable. Only the selected snippet is sent, never the whole document. Keys are stored in Windows Credential Manager and logs never record source text, screenshots or keys.

The workbench shows source on the left and the translation on the right with your notes below. History is capped at 200 entries, 90 days and 4 MiB, and partial results are never stored.

Open source under MIT. Ships as a ~59 MB installer (per-user, no admin prompt, detects the previous install location on upgrade, keeps your config and history on uninstall) and a ~88 MB portable zip. Both bundle .NET and both ship a .sha256. Note that the installer is currently unsigned, so Windows SmartScreen may warn on the first run.
```

字段：Platforms = Windows；License = Open Source (MIT)；Price = Free。

Tags：`translation`, `translator`, `machine-translation`, `screenshot`, `ocr`, `clipboard`, `windows`, `productivity`, `hotkey`, `open-source`

Alternatives 候选（主动去这些页面 propose 自己，也在表单里挑几个填上）：Crow Translate、Copy Translator、QTranslate、Mate Translate、Argos Translate、GoldenDict 系 —— **在这里不要写任何「比 X 好」的比较**，只建立「同类替代」关系。

**④ 步骤**

1. 注册 AlternativeTo 账号并完成邮箱验证。
2. 右上角用户图标 → "Suggest new application"。
3. 按上面内容填 name / short description / full description / platforms / license / tags / official URL。
4. 提交后进 waitlist；如果想加速，在表单末尾或 My submissions 里用 $5 优先审核。
5. 提交完成后立刻去几个竞品页面 → "Contribute to this page" → "Suggest Alternatives" → 搜索 PopGlot 并关联。
6. 之后申请 developer access，将来改文案可以即时生效。

> TODO: 缺 128x128 以上的方形 icon（必备）；缺带说明的产品截图。

#### A.4.2 LibHunt

**① 承认要求（核实自 LibHunt 页面）**

LibHunt 没有单独的 `/submit` 页面 —— 我在 2026-10-29 访问 `libhunt.com/submit` 得到 404。实际入口是它页面上的 **"add it to LibHunt"（`https://www.libhunt.com/site/project_submit`）**，形式为「把自己建议为某个已有项目的 alternative」。也就是说：

- 必须定位到一个已有项目（例如 Crow Translate 或 Copy Translator）作为锚点。
- 它的索引偏向 GitHub 上的项目，收录基础是 SPDX 许可证 + GitHub 仓库地址。

**② 认可程度：可以做，优先级中低** —— 免费、流程短，但流量期望值低。顺手做。

**③ 填写文案**

Description（LibHunt 偏好一句话）：

```
PopGlot is a MIT-licensed Windows translator for selected text and screenshots. Screenshots are OCR'd on the machine by default and sending the image to a vision model requires an explicit opt-in. Engines are pluggable: OpenAI-compatible, OpenAI Responses, Anthropic, Gemini, or a local Ollama / LM Studio endpoint.
```

**④ 步骤**

1. 打开 `https://www.libhunt.com/site/project_submit`。
2. 在 "related project" 里填 `https://github.com/crow-translate/crow-translate`（或 Copy Translator）。
3. 在 suggestion 里粘上面的描述 + PopGlot 仓库地址。
4. 等待人工索引。

> TODO: 需要先确认 LibHunt 的索引是否会 hook 到「Windows 桌面应用」（它过去偏向 dev library）；缺方形 logo。

#### A.4.3 OSS Software (osssoftware.org)

**① 承认要求（核实自其官网首页）**

- 入口：首页 "Showcase Your OSS Project" / "Submit it here"。
- **免费队列：官方自己写「大约三个月内加入」，并且发出的是 nofollow 链接。**
- **付费：$49，立刻发布且是永久 dofollow 链接。**

**② 认可程度：谨慎 / 建议先免费。** 这是一个把 nofollow 升级成 dofollow 的付费漏斗。我不建议你为它付 $49 —— 理由：

- 链接价值未经任何第三方验证，$49 换一个 nofollow→dofollow 的升级，性价比存疑。
- 这个站的第一价值主张是曝光而非 SEO，而我们还没有任何能衡量曝光的落地页。

如果一定要做，**走免费队列**，并且把它当作「等三个月但零成本」的占位。

**③ 填写文案**

复用 A.4.1 的 short description 即可：

```
A MIT-licensed Windows translator for selected text and screenshots, with local OCR by default and pluggable engines (OpenAI-compatible, Anthropic, Gemini, or local Ollama / LM Studio).
```

**④ 步骤**

1. 打开 `https://osssoftware.org/`，点 "Submit it here"。
2. 填名称、描述、GitHub 链接、License（MIT）。
3. 免费队列等待约三个月；不要选 $49。

> TODO: 缺 logo；缺 landing page（如果把 GitHub 作为唯一落地页，付费获得的曝光几乎无法转化；这也是不建议付费的理由之一）。

#### A.4.4 Slant (slant.co) 与同类

**① 提交要求 —— 我没有核实到官方原文的字段说明。** 我在 2026-10-29 检索到的都是第三方目录聚合站对 slant.co 的描述（它们声称免费、dofollow、DR 70），** slant.co 自己的提交条款我没有读到**。按「不编造」原则，这里不给出具体字段，只给行动建议。

**② 建议**

- 提交前手动打开 slant.co，找到实际的 submit / add product 入口并读完自己的条款。如果当前的入口要求付费或有壁垒，先跳过。
- Slant 的核心是社区 pros/cons 对比。**最诚实的用法是：不要自己去写自己的 pros**（这就是 astroturfing，会被识破并损害项目）。让真实用户在愿意的时候自然补充。所以这一项的价值取决于有没有用户，而我们目前没有。
- 结论：**这周不要做 Slant。** 它排在 AlternativeTo、LibHunt 之后，且排在 Task B 的 awesome 列表之后。

> TODO: 需要人工核实 slant.co 的官方提交入口与条款；需要真实用户在 Slant 上留下对比评价（目前为零）。

---

## Task B. awesome 列表清单

### B.0 核实方法与免责

以下每个仓库我都做了实际核实（2026-10-29）：

- 通过 GitHub REST API 确认仓库存在、star 数、最后 push 时间、许可证、默认分支。
- 拉取并阅读 `CONTRIBUTING.md`（或 README 中的 Contributing 段）确认条目的格式要求。

**我没有发起任何 PR**（无授权）。下面给的条目原文和命令都可以直接复制，但请在发出前与 README 里实际的缩进对一遍。

另外一个大前提：**没有任何一个列表会因为 inclusion 给你带来显著流量。** 这些是长期 SEO 和可信度资产，不是 launch 通道。这也是我们诚实评估「性价比」的基准。

### B.1 总览表

| 仓库全名 | URL | star | 最后 push | 有无数字门槛 | 建议 |
| --- | --- | --- | --- | --- | --- |
| **mbiesiad/awesome-translations** | https://github.com/mbiesiad/awesome-translations | 193 | 2026-01-18 | 无 | **现在就提交** |
| **quozd/awesome-dotnet** | https://github.com/quozd/awesome-dotnet | 21,691 | 2026-03-26 | 无（但有「活跃维护/文档/测试」标准） | **现在提交**，做好等待准备 |
| Carlos487/awesome-wpf | https://github.com/Carlos487/awesome-wpf | 2,177 | 2026-10-10 | 无 CONTRIBUTING | **低优先级，可选** |
| rust-unofficial/awesome-rust | https://github.com/rust-unofficial/awesome-rust | 60,184 | 2026-10-29 | **star > 50 或 downloads > 2000** | **现在不提交** |
| 0PandaDEV/awesome-windows | https://github.com/0PandaDEV/awesome-windows | 3,092 | 2026-10-28 | **star ≥ 50 + 项目年龄/活跃度** | **现在不提交** |
| awesome-selfhosted/awesome-selfhosted | https://github.com/awesome-selfhosted/awesome-selfhosted | 331.7k | 活跃 | 门槛：必须可自托管 | **不合适，不提交** |
| ~~awesome-opensource-windows~~ | — | — | — | — | **不存在 / 无法核实，跳过** |

### B.2 mbiesiad/awesome-translations —— 建议现在提交

**核实结果**

- 仓库存在，193 star，最后 push 2026-01-18，CC0-1.0，默认分支 `main`。
- CONTRIBUTING.md 内容非常宽松：没有 star 门槛，没有严格的字数限制。明确要求：fork + PR，**在 `develop` 分支上工作**；关于排序的说法是「Sort alphabetically（ie. relevant sources in a specific sections）」—— 即同一小节内按字母序排。
- README 的实际条目格式是：`- [Name](url) - Description.`
- 现有相关分类：`## Apps > ### Automated translation`，目前有 RTranslator、Crow Translate、Copy Translator、LibreTranslate。**这是最接近 PopGlot 的位置，而且 Crow Translate 和 Copy Translator 已经在这里了 —— 说明这个分类对「Windows 桌面划词翻译」完全对口。**

**建议理由**：这是我们唯一一个当下完全符合条件、且分类精准对口的 awesome 列表。它没有热度门槛，PR 被 review 的概率最高。**这是 Task B 里唯一建议今天就做的事。**

**条目原文（可直接粘进 PR）**

插入位置：`## Apps` → `### Automated translation` 小节。**按该小节现有字母序，PopGlot 排在 Copy Translator 之后、LibreTranslate 之前**（现有顺序已经是 R / C / C / L 并不严格按字母序，因此你也可以直接追加到小节末尾，并在 PR 里说明）。稳妥做法是追加到小节最后一行。

```markdown
- [PopGlot](https://github.com/ethan-blue/PopGlot) - A translator for Windows that translates selected text and screenshots, OCRs screenshots locally by default, and supports OpenAI-compatible, OpenAI Responses, Anthropic, Gemini and local Ollama or LM Studio endpoints.
```

**提交步骤**

```bash
# 1. fork（网页点点即可），然后本地 clone
git clone https://github.com/<your-username>/awesome-translations.git
cd awesome-translations

# 2. 切到 develop（CONTRIBUTING 明确要求）
git checkout develop

# 3. 改 README.md 的 ### Automated translation 小节，追加上面那一行

# 4. 提交 + PR，目标分支必须是 develop，不是 main
git add README.md
git commit -m "Add PopGlot to Apps / Automated translation"
git push origin develop
# 在 GitHub 网页上开 PR: base = mbiesiad/awesome-translations : develop
```

PR 正文建议：

```
Adding PopGlot under Apps > Automated translation.

PopGlot is a MIT-licensed Windows translator for selected text and screenshots. Relevant points for this section: screenshots are OCR'd on the machine by default with the vision-model path behind an explicit per-use opt-in, engines are pluggable (OpenAI-compatible, OpenAI Responses, Anthropic, Gemini, plus local Ollama / LM Studio), and there is a strict offline mode.

I am the author of the project.
```

> TODO: 该列表涉及翻译领域，如果你之后能把 PopGlot 接上某个真正 offline-capable 的翻译模型（而不是依赖 Google fallback），这个条目的说服力会强很多。

### B.3 quozd/awesome-dotnet —— 建议现在提交，做好等待准备

**核实结果**

- 仓库存在，21,691 star，CC0-1.0，默认分支 `master`。**最后 push 2026-03-26（约 7 个月前）** —— 该仓库仍被广泛引用，但合并速度可能很慢。这是要如实告知的期望值。
- CONTRIBUTING.md 的明确要求：
  - 格式：`[LIBRARY](LINK) - DESCRIPTION`
  - 链接指向 package 或 project 的名字
  - 描述要 concise、clear、simple
  - **一个 PR 只加一个链接**；PR 描述里要带上被加项目的链接并说明加它的理由
  - Quality standard：对社区普遍有用、actively maintained（就算只是 acknowledged 也算）、stable、documented、tests
  - 商业项目另有 `**[$]**` / `**[Proprietary]**` 等标签要求 —— PopGlot 是 MIT 且免费，不需要任何标签
- 分类候选：`## Desktop`（目前只有一条 Sucrose Wallpaper Engine，很像 PopGlot 的性质：桌面工具而非库）、`## Tools`（偏重实际工具如 ShareX / Open Live Writer）、`## Internationalization`（偏 resx/i18n 库，不完全对口）、`## WPF`（偏 UI 控件库，不对口）。

**建议理由**：21.7k star 的列表本身是高价值资产；又没有 star 门槛。**推荐放 `## Desktop`**，因为 PopGlot 是一个终端用户桌面程序，和那里唯一一条现有条目同性质；`## Tools` 是次选。

风险提示：它的 quality standard 要求 "documented" 和 "tests"。我们有 README 和测试，但我没有核实二者覆盖率，所以 PR 里不要吹这两点；如果被 maintainer 问起再据实回答。

**条目原文（可直接粘进 PR）**

`## Desktop` 小节（该小节目前只有一条 Sucrose，追加即可）：

```markdown
* [PopGlot](https://github.com/ethan-blue/PopGlot) - Translator for Windows that translates the selected text and screenshots, with local OCR for screenshots and a Rust translation core behind a C ABI that the WPF .NET 10 shell consumes. MIT licensed.
```

如果想同时准备 `## Tools` 的版本（**不要在同一封 PR 里发两条**，规则明确），备用：

```markdown
* [PopGlot](https://github.com/ethan-blue/PopGlot) - Hotkey translator for Windows: select text or a screen region, read the result in place. MIT licensed, pluggable engines including local models.
```

**提交步骤**

```bash
git clone https://github.com/<your-username>/awesome-dotnet.git
cd awesome-dotnet
git checkout -b add-popglot

# 编辑 README.md，在 ## Desktop 小节里追加上面的那一行
# 注意格式是 `* [...](...) - ...`，和 mbiesiad 那个列表的 `- [...]` 不一样

git add README.md
git commit -m "Add PopGlot to Desktop"
git push origin add-popglot
# 开 PR: base = quozd/awesome-dotnet : master
# PR 描述里必须写上被加项目的链接，并说明为什么加它（CONTRIBUTING 明文要求）
```

PR 正文建议：

```
Link added: https://github.com/ethan-blue/PopGlot

Adding PopGlot under Desktop.

Why it belongs in this list: it is an end-user Windows desktop application rather than a library, and the part that is interesting from a .NET perspective is the Win32 surface area - global hotkeys that coexist with other apps, an overlay panel that must appear above fullscreen content, and Windows Credential Manager storage for API keys. The shell is a WPF .NET 10 app consuming a Rust translation core over a C ABI, so none of the UI churn affects the translation logic.

MIT licensed, no paywall, no accounts.

I am the author of the project.
```

> TODO: 确认 README 的文档完整度足以支撑列表的 "documented" 标准；如果 maintainer 希望描述里带上 `.NET 10` 关键词，可以在精简后再补。

### B.4 Carlos487/awesome-wpf —— 可选，低优先级

**核实结果**

- 存在（2,177 star，CC0-1.0，默认分支 `master`，最后 push 2026-10-10）。这是同名仓库里 star 最高且维护最勤的。
- **仓库里没有 CONTRIBUTING.md**（根目录只有 `LICENSE`、`Program.cs`、`README.md`）。没有成文的规则，也就是说 PR 的结果高度不确定。
- README 的定位是「WPF resources, libraries and UI controls」，不是终端应用清单。它有一个 `## Sample Apps` 小节，但目前只有两条：一个教学用的 trading app 和一个 MVVM 最小示例。**从性质上看，PopGlot 这类成品应用放进这里会显得错类。**

**建议理由（为什么只给「可选」）**：

- 加分项：PopGlot 确实是一个 WPF on .NET 10 的真实应用，作为「WPF 能做出什么样的东西」是有参考价值的。
- 减分项：仓库没有成文规则 + 列表定位是 resources 而非 products → PR 大概率被搁置或拒绝。
- 结论：**如果你想做，就自己量力。它不在「必须做」的队列里。** 优先级排在 awesome-translations 和 awesome-dotnet 之后。

**条目原文（如果决定做）**

`## Sample Apps` 小节末尾追加：

```markdown
- [PopGlot](https://github.com/ethan-blue/PopGlot) - An open source Windows translator built as a WPF (.NET 10) shell over a Rust translation core behind a C ABI; useful as a reference for a real, shipped WPF application rather than a sample.
```

> TODO: 这个列表的定位与成品应用不完全一致；发出前最好先在 issue 里问一下 maintainer 是否接受成品应用进 Sample Apps。

### B.5 rust-unofficial/awesome-rust —— 现在不要提交

**核实结果**

- 存在且高度活跃：60,184 star，最后 push 2026-10-29，CC0-1.0，默认分支 `main`。
- CONTRIBUTING.md 的门槛写得很直白，原文：

  > Accepted: `(stars > 50 | downloads > 2000)`
  > Template: `[ACCOUNT/REPO](https://github.com/ACCOUNT/REPO) [[CRATE](https://crates.io/crates/CRATE)] - DESCRIPTION`
  > Sort: alphabetical

  并且解释得很清楚：「The maintainers of this repo are not responsible for making your project popular」。

**为什么现在不提交**：实测 star = 2，全部 release 资产累计下载 = 130。两个条件都不满足（2000 downloads 那条显然也不满足）。现在提交一定被拒，而且第一次被拒的记录对以后再提没有好处。

**达成的条件**：star 过 50。

**条目原文（留作将来提交，不要现在发）**

目标小节：`## Applications > ### Utilities`（该小节全是终端工具和小型应用，PopGlot 的性质对口）；次选 `## Applications > ### Text processing`。两者差别是 Utilities 偏「工具」，Text processing 偏「处理文本的 CLI/库」。建议 Utilities。

PopGlot 的 crates 没有发布到 crates.io，所以按 CONTRIBUTING 的说明要**去掉 `[[CRATE](...)]` 部分**。

```markdown
* [ethan-blue/PopGlot](https://github.com/ethan-blue/PopGlot) - Windows translator for selected text and screenshots, with the translation logic in Rust (popglot-domain, popglot-core, popglot-ffi) behind a C ABI that a WPF .NET 10 shell consumes. [![Build Status](https://github.com/ethan-blue/PopGlot/actions/workflows/ci.yml/badge.svg)](https://github.com/ethan-blue/PopGlot/actions/workflows/ci.yml)
```

> ⚠ 上面的 badge 行我**没有核实** workflow 文件名是否叫 `ci.yml`。PR 前必须去 `.github/workflows/` 里确认真实文件名，或者直接删掉 badge（CONTRIBUTING 说 badge 是「if you have a CI build」，不是强制）。

**将来提交步骤**

```bash
git clone https://github.com/<your-username>/awesome-rust.git
cd awesome-rust
git checkout -b add-popglot
# 在 ## Applications > ### Utilities 里按字母序插入上面的行
git add README.md
git commit -m "Add PopGlot to Applications / Utilities"
git push origin add-popglot
# PR: base = rust-unofficial/awesome-rust : main
# PR 描述里要明确写出 star 数已 > 50（CONTRIBUTING 要求把 popularity metric 写在 PR 里）
```

> TODO: star 数未达 50（当前 2）；如果 crates 未来发布到 crates.io，需要补 `[[CRATE](...)]` 部分；CI badge 的 workflow 文件名需要核实。

### B.6 0PandaDEV/awesome-windows —— 现在不要提交，且即使达标也有额外风险

**核实结果**

- 存在且活跃：3,092 star，最后 push 2026-10-28，CC0-1.0，默认分支 `main`。
- README 顶部的 Submission criteria（2026-10-28 刚加入，commit `a05d2dc`）：
  > A project must have at least 50 GitHub stars and either be at least a month old (30+ days since the first commit) or have had a commit within the last 30 days. Projects that don't meet this are not accepted.
- CONTRIBUTING.md 的明确要求：
  - `* [List Name](link)` 格式，使用 title-casing (AP style)
  - 在所在分类里**按字母序**插
  - 一个 PR 只加一条建议
  - 检查拼写和语法，去掉行尾空格
- 一字不差地抄在这里的 CAUTION：
  > Vibecoded slop and tools that don't fall in the category of "awesome" are not welcomed on this list and PR's will be rejected. This is NOT a list for your new AI tool or whisper transcribe wrapper.

**为什么现在不提交**

1. 硬门槛：star ≥ 50，实测 2。现在提必被拒。
2. **即使将来 star 达标，也有第二个风险**：这条 CAUTION 明确点名排斥「new AI tool」和「whisper transcribe wrapper」。PopGlot 的核心体验之一是调用 LLM 做翻译，虽然它不是 wrapper（有真实的 Rust 引擎层和离线 OCR 路径），但 maintainer 第一眼可能会这么归类。**这一点必须诚实面对，不要指望靠措辞糊过去。** 真要做到，唯一的方法是让 PR 的第一句就让 maintainer 看到本地 OCR 默认、offline mode、以及 MIT 开源 —— 也就是下面这条描述做的事。

**达成的条件**：star ≥ 50。另一半条件已经满足（创建于 2026-08-23，已超过 30 天；最后一次 push 是 2026-10-29，在 30 天内）。

**分类选择**：该列表没有 "Translation" 分类。**推荐 `Productivity`**（PopGlot 对用户的价值是阅读效率）；次选 `Screen Capture Screenshot`（因为截图翻译确实是它的一个主入口）。`Local AI` 分类也可以，但容易强化「AI tool」的归类，反而不建议。

**条目原文（留作将来提交，不要现在发）**

```markdown
* [PopGlot](https://github.com/ethan-blue/PopGlot) - Translates selected text and screenshots on Windows, OCRs screenshots on your machine by default and supports local Ollama or LM Studio endpoints with no network required. Open source, MIT.
```

> 格式核对：AP style title-casing → 描述句首字母大写、句末句号。列表现有条目如 `Velocmd - A lightning fast, native system launcher. Search files, run commands, and navigate your system seamlessly.` 就是这种写法。

**将来提交步骤**

```bash
git clone https://github.com/<your-username>/awesome-windows.git
cd awesome-windows
git checkout -b add-popglot
# 在 ## Productivity 里按字母序插入上面的行
git add README.md
git commit -m "Add PopGlot to Productivity"
git push origin add-popglot
# PR: base = 0PandaDEV/awesome-windows : main
```

> TODO: star 数未达 50（当前 2）；该列表对 AI 类工具的立场需要 PR 文案特别小心；需要先确认那时 `## Productivity` 分类还存在。

### B.7 awesome-selfhosted —— 不合适，不提交

**核实结果**

- 仓库存在且巨大（331.7k star），但它的范围写在 README 第一句：
  > This is a list of Free Software **network services** and **web applications** which can be hosted on your own server(s).

**为什么不提交**：PopGlot 是一台机器上运行的 Windows 桌面应用，不是网络服务也不是 web 应用，用户无法「把它部署到自己的服务器」。它不符合该列表的定义，提交了也是浪费 maintainer 时间。

即便它的生态热度很高 —— 这也是个说服力很强的诱惑 —— 也不要提交。**这一点不要变通。**

**如果将来想沾边**：只有当 PopGlot 长出了「局域网/自托管的翻译服务后端」形态时才成立。现在没有。

### B.8 awesome-opensource-windows —— 无法核实，跳过

我按这个名字去找了：

- `https://github.com/Awesome-Windows/Awesome` → **404**（仓库不存在、已私有化、或已改名）。
- GitHub 仓库名搜索 `awesome opensource windows` → 无相关结果。

最可能的解释是：这个名字指向的就是现在的 `0PandaDEV/awesome-windows`（第三方索引站点给出的同名条目，README 内容与它是同一份）。

**结论：跳过。** 如果你想复查，`0PandaDEV/awesome-windows` 的规则已经在 B.6 里给了，等 star 达标后直接按 B.6 走。

### B.9 关于你还提到的其它列表

我额外检索了一轮，没有找到第 7 个「相关且我能核实到 CONTRIBUTING 要求」的列表。建议暂就此止步 —— 把精力集中在两个「现在能提交」的列表上，比再去找六个边缘清单更有价值。如果发现新的线索，先自己去核实「是否真的存在、是否还在维护、CONTRIBUTING 写了什么」，再决定。

---

## Task B 速查表（今天就能做的两件事）

| 顺序 | 动作 | 目标 | 预计结果 |
| --- | --- | --- | --- |
| 1 | 发 PR | `mbiesiad/awesome-translations` → `develop` 分支，`### Automated translation` | 今天就能做，最可能被接受 |
| 2 | 发 PR | `quozd/awesome-dotnet` → `master`，`## Desktop` | 今天能做，但 mergers 可能要等 |

---

## 附录 A. 通用免责声明模板（Reddit / 评论区用）

凡是需要「声明我是作者」的场合，用这一句，不要自己发挥：

```
I'm the author of PopGlot (MIT, open source). Happy to answer anything technical, including the parts that are wrong.
```

不要写：

- "I hope you like it" / "Please upvote" / "Check it out" —— 这些都是 spam 信号。
- 任何形式的「如果你们喜欢我就……」—— Reddit 对这个尤其敏感。

## 附录 B. 缺失素材总清单

按「卡住最多渠道」排序：

| 缺失 | 卡住的渠道 | 说明 |
| --- | --- | --- |
| **demo GIF / 短视频** | r/SideProject（最严重）、r/software、r/Windows11、HN、PH | 一个完全由肌肉记忆驱动的工具，静态图讲不清楚。10-20 秒：三次呼出 + 一次截图翻译 + 剪贴板未被覆盖。 |
| **品牌 logo / 方形 icon（≥128x128，240x240 更佳）** | PH thumbnail、AlternativeTo | AlternativeTo 明写需要 128x128 以上的方形 icon。 |
| **1270x760 产品图 ≥4 张** | PH 画廊 | 现有四张 PNG 尺寸未知，需重裁或补图。 |
| **landing page** | PH（间接）、HN（间接） | 不是硬条件；但因为还没有用户，投资 landing page 的性价比目前不高。 |
| **SmartScreen 警告的实测截屏** | r/Windows11、r/software | 主动放出这条反而增信，比让人自己撞上好。 |
| **CI badge 的真实 workflow 文件名** | awesome-rust（将来） | 未核实，PR 前必须查。 |
| **star ≥ 50** | awesome-rust、awesome-windows | 当前 2。这两个列表现在都不可用。 |
| ** slant.co 官方提交条款** | Slant | 未核实完成，本周不做。 |

## 附录 C. 我对发布顺序的建议

1. 先补齐 demo GIF（这是所有渠道里最贵的瓶颈）。
2. 同时发 Task B 的两个 PR（awesome-translations、awesome-dotnet）—— 它们不依赖任何素材，今天就能发出去，且不会被「源码/应当有 star」这种门槛卡住。
3. 然后在 **r/SideProject** 首发 Reddit —— 它是唯一「欢迎自推」且与我们的产品形态贴合的社区，风险最低，反馈最真。
4. 拿到第一条真机反馈与修复后，再按 A.1.2 的周三窗口发 r/software，然后 A.1.6 的周六 NZT 发 r/dotnet（或等 0.2.0），再 A.1.1 的 modmail → r/Windows11。
5. 最后才轮到 HN 与 r/rust —— 这两个对「AI 味道 / 快速拼凑」最敏感，要在正文里有真实的技术故事时才去。
6. PH 排在 logo + 4 张图 + demo 视频都齐了之后。
7. AlternativeTo 可以和第 2 步并行做，因为它审得慢（几个月），越早越好。

不要做的事（本文件反复强调）：

- 不同时群发同一段文案到多个 sub。
- 不在任何一个多渠道使用一个 AI 润色过的版本 —— r/dotnet、r/rust、HN 都是明文排斥。
- 不在任何渠道描述用户数、下载量、评分。
- 不购买任何「付费立即上线」的目录站套餐（尤其是 OSS Software 的 $49）。
