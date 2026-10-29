# PopGlot

[![Windows](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-0078D4)](https://github.com/ethan-blue/PopGlot/releases)
[![Version](https://img.shields.io/badge/version-0.1.35-5B67B5)](CHANGELOG.md)
[![License](https://img.shields.io/badge/license-MIT-3D8B6E)](LICENSE)
[![CI](https://img.shields.io/github/actions/workflow/status/ethan-blue/PopGlot/ci.yml?branch=main&label=CI)](https://github.com/ethan-blue/PopGlot/actions)

> 给在 Windows 上读英文技术内容的程序员：报错、注释、命令、文档里的句子，选中、按一次快捷键，译文和一句解释就出现在光标旁边，不用切窗口、不用手动复制。

> For programmers reading English technical copy on Windows: select an error message, comment, command or doc line, press one hotkey, and the translation plus a short note appear next to the cursor. No window switching, no manual copy-paste.

中文说明在上，English follows each section.

## 下载

两个包都自带 .NET 10 运行时，不需要你先装 .NET。Windows 10 / 11 x64。

[![下载安装包 PopGlot 0.1.35](https://img.shields.io/badge/%E4%B8%8B%E8%BD%BD%E5%AE%89%E8%A3%85%E5%8C%85-0.1.35-2E7D32?style=for-the-badge)](https://github.com/ethan-blue/PopGlot/releases/download/v0.1.35/PopGlot-v0.1.35-win-x64-setup.exe)
[![下载便携包 PopGlot 0.1.35](https://img.shields.io/badge/%E4%B8%8B%E8%BD%BD%E4%BE%BF%E6%90%BA%E5%8C%85-0.1.35-1F6FEB?style=for-the-badge)](https://github.com/ethan-blue/PopGlot/releases/download/v0.1.35/PopGlot-v0.1.35-win-x64.zip)

Download PopGlot 0.1.35 — [installer](https://github.com/ethan-blue/PopGlot/releases/download/v0.1.35/PopGlot-v0.1.35-win-x64-setup.exe) or [portable zip](https://github.com/ethan-blue/PopGlot/releases/download/v0.1.35/PopGlot-v0.1.35-win-x64.zip). Both bundle the .NET 10 runtime. Windows 10/11 x64.

> 体积（0.1.35 实测）：安装包 59,483,995 字节（约 57 MB），便携 zip 87,833,887 字节（约 84 MB）。两个包都自带 .NET 10 运行时，所以比不带运行时的程序大。
> Size (measured for 0.1.35): installer 59,483,995 bytes (~57 MB), portable zip 87,833,887 bytes (~84 MB). Both bundle the .NET 10 runtime, which is why they are not small.

| 形态 | 文件名 | 适合谁 |
| --- | --- | --- |
| 安装包（推荐） | `PopGlot-v0.1.35-win-x64-setup.exe` | 大多数桌面用户。向导安装、开始菜单快捷方式、标准卸载入口；下次升级直接运行新版安装包即可原地覆盖。 |
| 便携 zip | `PopGlot-v0.1.35-win-x64.zip` | 免安装、U 盘随身、只读目录运行。解压后运行 `PopGlot.exe`。 |

**0.1.35** 开始提供 Windows 安装包：覆盖升级会自动探测上一次的安装位置，默认按当前用户安装、无需管理员，卸载只删程序文件而保留你的配置与历史。同时新增首次配置向导（隐私选择一次做完）、应用内更新提示（只提示，不静默下载），内置免费引擎改为默认允许——不再需要钻进设置页授权；另外加固了密钥与端点的防泄露。发布状态与附件以 [Releases](https://github.com/ethan-blue/PopGlot/releases) 为准。

每一版的发布页都用同一个样子：标题是 `PopGlot 版本号`，页上直接写这一版改了什么，并附上安装包与 zip（各自带 `.sha256`）。不用再点进另一份说明。

| 版本 | 包里有没有 .NET |
| --- | --- |
| 0.1.2 及以后 | 有。安装包装完即用，便携包解压后运行 `PopGlot.exe`。 |
| 0.1.1 及以前 | 没有。要先安装 .NET 10 Desktop Runtime（x64）。 |

文件名始终是 `PopGlot-v版本号-win-x64-setup.exe` 与 `PopGlot-v版本号-win-x64.zip`。校验：

```powershell
(Get-FileHash -Path .\PopGlot-v0.1.35-win-x64-setup.exe -Algorithm SHA256).Hash.ToLower()
Get-Content .\PopGlot-v0.1.35-win-x64-setup.exe.sha256
```

> 安装包目前未签名，首次运行可能看到 SmartScreen 提示；每个包的 SHA-256 与发布页附件同名文件一一对应。SmartScreen 怎么处理见下面「常见问题」。

全部发布页在 [Releases](https://github.com/ethan-blue/PopGlot/releases)。

## 能做什么

| | |
| --- | --- |
| 划词 | 任意程序里选中文字，`Ctrl+Alt+W`。原剪贴板会按序列号恢复，不会盖掉你后来复制的内容。 |
| 截图 | `Ctrl+Alt+Space` 框选。默认识别在本机完成。只有你允许图片离开本机时，才会把截图交给视觉模型。 |
| 工作台 | `Ctrl+Alt+O`。左边原文，右边译文。译文下面是说明；「要点」是另一种阅读，不会清掉译文。 |
| 查词 | `Ctrl+Alt+Q`。输入后按 Enter 翻译。 |
| 引擎 | OpenAI 兼容、OpenAI Responses、Anthropic、Gemini，以及 Ollama、LM Studio 这类本机地址。 |
| 免费线路 | 未配置自己的引擎时，默认可用 Google 公共翻译。Google 不可用时，可改选 MyMemory。一次只发给你选中的那一条。 |
| 外观 | 设置 → 通用。跟随系统、浅色、深色，点选后立即预览。 |
| 离线 | 「安全离线模式」打开后，包括免费线路在内的外发请求都不会发出。本机模型地址仍可用。 |

Select text anywhere and press `Ctrl+Alt+W`. Frame a screenshot with `Ctrl+Alt+Space`. The main window (`Ctrl+Alt+O`) keeps the translation and a separate summary, so one does not erase the other. Engines cover OpenAI-compatible, OpenAI Responses, Anthropic, Gemini, plus local endpoints like Ollama and LM Studio. With no engine configured, Google public translate is used; MyMemory is the fallback. Keys stay in Windows Credential Manager, one entry per engine.

## 和别的工具有什么不同

1. **截图默认不出本机。** 默认走本机 Windows OCR，图片只在你单独授权后才离开；「安全离线模式」是总开关，打开后连内置免费线路在内的所有外发请求都被拦截——Shell 与 Core 两侧都设了拦截，因为免费引擎是 Shell 侧的调用。换句话说，想彻底断网照样能用，本机模型地址不受影响。
2. **译文和「要点」是两件事。** 工作台左边原文、右边译文，译文下方附一层说明（做的事、可能的原因、下一步）。「要点」是独立视图，切过去不会清掉已经出来的译文。
3. **代码里的标识符不会被翻译。** 本地 OCR 路线在发出请求之前，先把变量、类名、函数名、命名空间、路径、URL、命令参数、环境变量、异常、错误码、代码块替换成占位符；返回后逐个校验「只出现一次」，再逐字符还原。校验不过就安全失败，不给你一份看起来通顺但标识符被改过的译文。

What sets it apart: screenshots stay local by default and every outbound request dies behind one offline master switch; translation and summary are separate views that do not overwrite each other; protected identifiers in code survive translation or the result is discarded.

**还比不上的地方（诚实说明）：** 目前只有 Windows 版，macOS / Linux 在移植计划里但还没做；安装包未签名，首次运行可能被 SmartScreen 拦一次；没有官网，所有内容都以 GitHub 仓库和 Releases 为准；没有账号与云端同步，历史只在本机；未配自己的引擎时用的公共翻译线路没有 SLA，遇到限流或上游变动要自备 Key；想要更好的翻译质量和截图视觉理解，得自己准备 API Key 或本机模型。

**Where it falls short:** Windows only — macOS/Linux porting is planned, not built. The installer is unsigned, so SmartScreen may warn on first run. There is no website; the repo and its Releases are the source of truth. No accounts and no cloud sync; history is local only. The free public route has no SLA, so bring your own key for production use, and any real quality jump requires your own API key or a local model.

## 界面

划词浮窗贴在光标旁。结果先出译文，说明附在下面。

The selection panel sits next to the cursor. The translation stays; the note sits under it.

![划词浮窗](docs/images/panel-dark.png)

<!--
  ============================================================
  Demo GIF 占位位（文件目前不存在，先不要取消注释，否则 GitHub 页面会渲染成裂图）
  Pending: record a 15-second selection-translation GIF.

  目标路径 Target path: docs/images/demo.gif

  建议内容 Content: 15 秒，一次完整划词 ——
    1) 在任意窗口选中一段英文报错或注释
    2) 按 Ctrl+Alt+W
    3) 浮窗在光标旁流式吐出译文，下方跟一句说明
    可选第 4 秒：按 Ctrl+Alt+O 打开工作台，展示左原文右译文

  怎么生成 How to record:
    1) 起一个干净实例（配置、历史、提示词全部重定向到 %TEMP%\PopGlot-Demo，与日常实例隔离）：
         ./scripts/start-demo-instance.ps1 -Reset
       注意：它只隔离 PopGlot 自己的数据目录，不隔离 Windows 凭据管理器和外部账号——
       录之前自己关掉聊天窗口、邮箱、通知预览和带个人路径的终端。
    2) Windows 推荐 ScreenToGif（开源）：「录像机」框选区域 → 录制 → 编辑器删多余帧 → 另存为 GIF。
       参数建议：12–15 fps，128 色，裁剪掉空白桌面区域，目标 < 3 MiB（GitHub README 渲染友好）。
    3) 画面里不要出现真实的 API Key、私有地址或客户代码。完整分镜见 docs/VIDEO_DEMO.md。

  录好并放到 docs/images/demo.gif 之后，把下面两行解除注释即可（它们现在的形态就是最终形态）：

  ![PopGlot 划词演示](docs/images/demo.gif)
  _选中英文 → Ctrl+Alt+W → 译文出现在光标旁。Select text, press Ctrl+Alt+W, read it there._
  ============================================================
-->

| 深色工作台 | 浅色工作台 |
| --- | --- |
| ![深色工作台](docs/images/workbench-dark.png) | ![浅色工作台](docs/images/workbench-light.png) |

图中的 “Demo Text Service” 是测试数据，不是内置服务。The “Demo Text Service” label in the pictures is test data, not a built-in provider.

主题在「设置 → 通用」，可选跟随系统、浅色或深色；点击立即预览，保存后保留。下图是隔离示例数据的引擎页实际窗口渲染。

Appearance is under Settings → General: system, light, or dark. The picture below is the engine page, not the theme page.

![翻译引擎](docs/images/settings-dark.png)

## 常见问题

**要付费吗？**
不用。PopGlot 本体 MIT 许可、免费使用，源码公开。如果你自己接了 OpenAI / Anthropic / Gemini 这类付费引擎，那一小笔模型费用由你的服务商收。

**需要 API Key 吗？**
不配也能用：默认走 Google 公共翻译，不可用时可以改选 MyMemory。想要更好的质量、更稳的可用性，或要让截图走视觉模型，就得自备 Key（或本机 Ollama / LM Studio）。Key 存在 Windows 凭据管理器里。

**我的数据去了哪里？**
配置和历史在 `%LOCALAPPDATA%\PopGlot`，不上传。Key 在 Windows 凭据管理器。日志不记原文、截图和密钥。截图默认本机 OCR；图片外发需要单独授权。想彻底断网就开「安全离线模式」。细的边界见 [docs/help/privacy-and-security.md](docs/help/privacy-and-security.md)。

**提示 SmartScreen「Windows 已保护你的电脑」怎么办？**
安装包还没做代码签名。确认文件来自本仓库的 Releases 后，点「更多信息」→「仍要运行」。不放心就先用上面的 PowerShell 命令比对 SHA-256，和发布页附件里的 `.sha256` 逐字符一致再运行。

**便携包和安装包到底差什么？**
内容一样，都自带 .NET 10 运行时。安装包多一个向导、开始菜单快捷方式和标准卸载入口，升级时直接跑新版安装包原地覆盖。便携包解压即用、可以放 U 盘或只读目录，适合不想写注册表的人。卸载安装包只删程序文件，你的配置和历史留着。

**快捷键和别的软件冲突怎么办？**
设置 → 快捷键，可以逐条重录。和系统已占用的组合冲突时，会退回保存前的那一组而不是静默失效。默认五组是划词 `Ctrl+Alt+W`、截图 `Ctrl+Alt+Space`、查词 `Ctrl+Alt+Q`、工作台 `Ctrl+Alt+O`、关浮窗 `Ctrl+Alt+X`。

**Is it free? Is my data safe? Do I need a key?**
Free and MIT-licensed. Model fees are between you and your provider, if you plug one in. A key is optional — Google public translate runs out of the box, MyMemory is the fallback — but better quality and screenshot vision need yours. Config and history live in `%LOCALAPPDATA%\PopGlot`; keys sit in Windows Credential Manager; logs carry no source text, screenshots or secrets. The installer is unsigned, so expect one SmartScreen prompt. See [docs/help/privacy-and-security.md](docs/help/privacy-and-security.md).

## 第一次使用

1. 装完（或解压后）运行 `PopGlot.exe`。窗口不自动弹出，图标在任务栏通知区域。
2. 选中一段英文，按 `Ctrl+Alt+W`。第一次会走一遍配置向导，把云端翻译、免费线路、历史、开机自启、诊断上报这五项一次选完（都标了推荐值），之后随时可以在「设置 → 通用 → 重新显示引导」重来。
3. 没有自己的引擎也能直接用：内置免费线路 **0.1.35 起默认开启**，不再拦你一层「先去设置页允许」的动作，默认走 Google 公共翻译。Google 不可用时，在浮窗或查词窗的线路菜单里改选 MyMemory。
4. 想彻底关掉免费线路：「设置 → 通用 → 重新显示引导」，在向导的隐私一页取消勾选。设置 → 隐私与数据页现在只陈述当前状态，那里没有开关按钮。
5. 要换模型：设置 → 引擎 → 添加引擎。填地址和密钥，测试连接，再在底部切换器中选用。
6. 想彻底不外发：打开「安全离线模式」。此时本机 Ollama / LM Studio 仍然可用。

Run `PopGlot.exe`. It stays in the tray and walks you through the five privacy choices once. With no engine configured, the free route answers right away — since 0.1.35 it is on by default, with no separate consent step in Settings → Privacy. Pick MyMemory instead of Google from the route menu in the panel or the quick-search window, or turn the free route off via Settings → General → Redisplay Guide. Add your own engine for better results.

默认快捷键：

| 动作 | 快捷键 |
| --- | --- |
| 划词翻译 | `Ctrl+Alt+W` |
| 截图翻译 | `Ctrl+Alt+Space` |
| 关闭浮窗 | `Ctrl+Alt+X` |
| 打开主窗口 | `Ctrl+Alt+O` |
| 查词 | `Ctrl+Alt+Q` |

`Esc` 第一次取消当前请求，再按一次关闭浮窗。快捷键可以在设置里重录；和系统已占用的组合冲突时，会退回保存前的组合。

## 数据放在哪里

配置在 `%LOCALAPPDATA%\PopGlot`。

| 文件 | 内容 |
| --- | --- |
| `provider-settings.json` | 当前文字和图片线路，schema v3。不含密钥。 |
| `product-config.json` | 已添加的引擎列表，schema v7。 |
| `prompt-templates.json` | 翻译风格。只在本机。 |
| `windows-shell.json` | 快捷键、主题、免费线路授权。 |
| `history.json` | 历史。最多 200 条、90 天、4 MiB。半成品不写入。 |

密钥在 Windows 凭据管理器，名称是 `PopGlot/provider/<引擎 id>`。日志不记原文、截图和密钥。

更细的边界见 [隐私说明](docs/help/privacy-and-security.md)。应用内的其余说明在 [docs/help](docs/help/index.md)。

## 版本怎么编号

`主版本.次版本.修订号`。已打 tag 的编号不重用、不跳号。工作区还没提交时，下一版就是最近 tag 加 1；同一轮里继续修改，仍然写在这一个编号上。

规则和一条说明该怎么写，见 [docs/VERSIONING.md](docs/VERSIONING.md)。已发布的变化见 [CHANGELOG.md](CHANGELOG.md)。

## 从源码构建

Windows 11 x64，Rust stable（`x86_64-pc-windows-msvc`），Visual Studio Build Tools 2022，.NET 10 SDK，PowerShell 7。

```powershell
./scripts/verify.ps1
./scripts/run.ps1
```

`verify.ps1` 会跑 Rust 格式检查、测试、Clippy、WPF 构建，以及 PureTests、LogicTests 和 CLI/WPF 连接场景（`CliTests --ui`）。全部使用隔离配置与模拟服务，不会调用真实模型。WPF 工程会自己编译 `popglot-ffi`，并把 `popglot_ffi.dll` 拷到输出目录。

要录演示或截图，用隔离实例，配置与历史都重定向到 `%TEMP%\PopGlot-Demo`，碰不到你日常那个实例的数据：

```powershell
./scripts/start-demo-instance.ps1 -Reset
```

For a clean recording instance, use `./scripts/start-demo-instance.ps1 -Reset` — config, history and prompts are redirected to `%TEMP%\PopGlot-Demo`, leaving your daily instance untouched. It isolates PopGlot's own data directory only: Credential Manager and external accounts are still shared, so close chat, mail and notification previews before you record.

离线延迟测量：

```powershell
cargo run -p popglot-core --bin stream_benchmark -- --scenario all --validate --tolerance-ms 40
```

在线测量默认不发出请求。要打到真实服务，必须同时加上 `--live` 和 `--i-understand-cost`，密钥放在环境变量 `POPGLOT_BENCHMARK_API_KEY`，不要写进命令行。说明见 [docs/TRANSLATION_BENCHMARK.md](docs/TRANSLATION_BENCHMARK.md)。

代码分成四块：WPF 外壳在 `apps/PopGlot.Windows`，领域类型在 `crates/popglot-domain`，翻译和路由在 `crates/popglot-core`，C ABI 在 `crates/popglot-ffi`。外壳可以换，翻译内核不跟着重写。结构见 [ARCHITECTURE.md](ARCHITECTURE.md)，范围见 [PRODUCT_SPEC.md](PRODUCT_SPEC.md)。

## 参与

用着顺手就给个 Star，让下一个人更容易找到它。遇到问题请开 Issue——复制粘贴前注意脱敏（应用内置的 `SecretRedactor` 已经把你的真实密钥从 Issue 正文里掩码掉了，但别把别人的贴上来）。想法需要在星标之前先看一遍 [PRODUCT_SPEC.md](PRODUCT_SPEC.md) 的「当前未实现与明确排除」那一节：列在那里的是明确的边界，不是漏做的 bug。已经在这里排队的 macOS 移植、术语库、hover 取词，就不必重复提了。

Star it if it helps, file issues when it does not. Before proposing a feature, skim the "Current limitations and exclusions" section of [PRODUCT_SPEC.md](PRODUCT_SPEC.md) — items listed there are deliberate scope boundaries, not oversights. Redact anything sensitive before pasting; the built-in `SecretRedactor` masks your own keys in issue bodies, but not third-party ones.

## 许可

[MIT](LICENSE)。
