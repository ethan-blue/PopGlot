# 桌面产品补全与 ZCode 风格 UI 重建 · 执行检查点（2026-09-26）

> 本文件是 `/goal`（完整执行 `docs/prompts/2026-09-26-desktop-product-completion.md`）的**持续更新检查点**。
> 追加式记录：每完成一项就在对应行更新状态并写 commit hash；未完成与阻塞项如实保留。

## B01 输入缺失声明（阻塞记录，不伪造）

- `docs/prompts/2026-09-26-desktop-product-completion.md` **在本仓库不存在**（已查：tracked / untracked / ignored / 分支 / stash / artifacts / .alma 全无此文件）。
- `PRODUCT-EXPERIENCE-SPEC.md` 同名文件不存在；仓库内的产品体验规格是根目录 `PRODUCT_SPEC.md`（含「当前未实现与明确排除」清单），按其执行。
- 处置：**不虚构原 prompt 文档内容**。以仓库内的真实合同执行同一目标实质：
  - 产品补全：`docs/gap-analysis-2026-09-14/C00-C07|C08-C12|C13-C19|C20-C26.md`（C00–C26 的定义与 09-14 对账状态）+ `docs/review-2026-09-12/EXECUTION-LEDGER.md`；
  - ZCode 风格 UI 重建：`docs/ui-review-handoff-2026-09-22.md`（用户已确认的界面规格）；
  - 若用户后续补交原 prompt 文档，以其为准重排剩余工作。

## C00–C26 状态矩阵（对账 09-14 为基线 + 本轮核实）

| ID | 主题 | 基线状态 | 本轮核实（2026-09-26） |
|---|---|---|---|
| C00–C07 | 核心管线/剪贴板/诊断/围栏/浮窗/崩溃/自启 | DONE_VERIFIED 候选 | 维持；E3 债 D1–D9 见对账A |
| C08 | 商业安装路径 | NOT_STARTED | 仍缺（依赖 C22 决策） |
| C09 | 启动性能 | PASS（09-15 复测） | 维持；80 MiB 软目标开口保留 |
| C10 | 快捷键→首帧结构化时间点 + 100 次基准 | PARTIAL | 待做（可自动化） |
| C11 | Activated 同步 I/O | PARTIAL | **已修复**（`RefreshEngineStatusOnActivated` 异步+门禁，MainWindow.xaml.cs:262） |
| C12 | 生命周期/内存验收 | PARTIAL | 真机 4 窗×200 开关 + WS 验收待跑 |
| C13–C19 | 工作台/浮窗/设置/反馈/主题/可及性 | 各 PARTIAL | 以 handoff-2026-09-22 为现行 UI 合同逐项收敛（UI 重建主战场） |
| C20 | 分段与长标识符保真 | PARTIAL | 长标识符专项测试待做（Rust） |
| C21 | 推荐证据时间与身份 | PARTIAL | 时间戳+来源身份待做 |
| C22 | 免费引擎商业 ADR | NOT_STARTED | **阻塞：负责人决策**，不可代做 |
| C23 | 四 Provider 真网 E2E | PARTIAL | **阻塞：需显式网络授权**（真实引擎在 Ethan 网络不稳） |
| C24 | 签名/更新/回滚 | PARTIAL | **阻塞：证书采购 README §G 禁止代购** |
| C25 | 首启引导/离线帮助/演示防伪 | NOT_STARTED→大部分已落地 | 引导=工作台内联横幅（比设计稿更轻）；演示徽章「演示 · 未联网」在；docs/help 骨架在；**缺：应用内帮助查看器** |
| C26 | 商业发布总验收 | NOT_STARTED | **阻塞：依赖 C22–C25 + 负责人签字** |

## 执行顺序（按依赖与优先级，持续更新）

1. **UI 重建现状截图审计**：跑 LogicTests 截图套件产出 `artifacts/screenshots/`，对照 handoff 稿逐视图判定偏差 → 修复（本轮）。
2. **C25 收尾**：应用内离线帮助查看器（docs/help 内容进包 + 毫秒级打开 + 断网可读）。
3. **C21**：推荐证据补采集时间与来源身份（Rust 元数据 + C# ToolTip）。
4. **C20**：长标识符（超长无空格 token）保真专项测试（Rust）。
5. **C10**：结构化时间点 + 100 次回环 mock 基准。
6. **C12**：真机 4 窗×200 开关 + WS 验收（本机可跑，排后）。
7. 阻塞项保持挂账：C22 / C24 / C26 / C23 / E3 矩阵（D1–D9）。

## 安全边界（继承仓库红线）

不改 Rust 枚举（VisionOcr 契约）；预设不发明模型 ID；免费引擎失败不静默改派；离线模式断网；测试走隔离 `POPGLOT_DATA_ROOT`/TestIsolation，不碰真配置；每次改动过 `verify.ps1` 全量门禁；不合并/不发版（用户已批准的发布流程除外）。

## 执行日志（追加）

- 2026-09-26：建立本检查点；B01 记录；状态矩阵核实完毕（C11 确认已被后续工作修复）。
- 2026-09-26（第二轮，C12 真机验收）：**发现并修复两个真实生命周期泄漏**。
  1. `MainWindow` 订阅 `ThemeService.ThemeChanged` 用内联 lambda 且从不退订——每构造一次主窗口即永久驻留一份引用（`af77afd` 修复，与其余四窗的对称退订模式对齐）。
  2. `ServicesSection` 用 `DependencyPropertyDescriptor.AddValueChanged(TextModelCombo, …)` 且无处 `RemoveValueChanged`——静态监听表永久持有目标元素→整个设置窗（本轮压测实证 Settings 家族 200/200 残留）。修复已落地（对称 `RemoveValueChanged` on `Unloaded`；与并行会话的实现撞车后采纳其版本）。
  3. **压测架落地**：`exe lifecycle-stress`（4 窗×200 真实 Show/Close、每圈泵队、WeakReference 判活、WS/句柄/线程/堆/订阅数采样、报告先于断言留档 `artifacts/lifecycle/`）+ `exe lifecycle-probe`（根因探针：裸窗 199/200 可回收=平台干净；不泵队自引用排队回调 50/50 残留、泵队后 1/50=排队回调必须泵队释放）。TTS 20 轮 Speak/Stop：`_synthesisCts` 置空、IsSpeaking=false ✅。
  4. **已验证的家族结论**：Settings/QuickSearch 全合同 PASS（alive=0、订阅回基线、WS 平台 229→231MB、堆有界）；Main/Panel 与 QuickSearch 在后续轮次剩 1/200 幸存者——与并行会话 WIP 同时在树上运行造成测量移动目标（其排队回调异常触发产品熔断级联），**全量四家族 PASS 判定挂起，等树稳定后重跑 lifecycle-stress**。
  5. **稳定树终判（worktree @ 40c6f61，无并行 WIP 污染）**：常规套件 288/0、cargo 215/0 先行验证；压测终判——Settings 200/200 残留=已定位的描述符泄漏（其修复仍在并行 WIP，落地后应转绿）；Main/Panel/QuickSearch 各精确 1/200 幸存（清钉+深排空后仍在，Main 的幸存者还持有未退订主题订阅、线程 16→30/句柄 376→835）；TTS 释放 PASS；QuickSearch/Panel 的 WS/堆平台有界（443→437MB / 增长不超界）。**幸存者根因追踪**为下一动作（survivor_cycle 定位已具备，需在无并行 WIP 的树上加 dotnet-gcdump 或圈号留档后二分）；档案：`artifacts/lifecycle/c12-*-{51d52528,44247133,55a37981,6494a02d}.*`（磁盘留档，artifacts 按 .gitignore 惯例不入库）。
  6. **压测架已提交**（`40c6f61`：LifecycleStress.cs + Program.cs 中本会话的 hunks 经 `git apply --cached` 精确分离；并行会话在 Program.cs 的未提交 hunks 未被吞并）。
  5. **测量阻塞**：并行会话在同一工作树活跃编辑（TranslationCoordinator/OutboundPolicy/ProfileManager 等 WIP 会抛排队异常、触发产品熔断级联，且 Program.cs 存在双方交错的未提交 hunks）。本轮未提交 Program.cs/LifecycleStress.cs 以免吞并他人工作；待其落地后提交并重跑压测出最终判定。
- 2026-09-26：**UI 重建截图审计完成**——跑 LogicTests 截图套件（287/0 通过）产出 `artifacts/screenshots/` 全套当前 UI 图，对照 handoff-2026-09-22 稿逐视图核查主工作台 / 引擎编辑器 / 提示词页 / 快捷键页 / 翻译浮窗 / 风格菜单：均已符合规格（顶栏唯一主按钮、分段 16 内边距、键帽规格、菜单勾选态、占位左对齐等），无重大偏差；handoff 主体已由此前波次 + `0d83948` + 前两轮打磨实现。
- 2026-09-26：**C25 收尾**（`30e1bfc`）——应用内离线帮助查看器 HelpWindow：docs/help 随包分发（csproj Content 链接到输出 `help/`，测试宿主同构）、左目录右正文本机渲染（复用 MarkdownPresenter）、仓库 .md 链接语法剥壳、缺文档诚实降级不联网；入口在 设置→通用→「打开帮助」；LogicTests 新增目录防漂移测试 + 浅/深色截图。
- 2026-09-26：**C20**（`407d2f6`）——Rust 分段长标识符保真 5 项测试：超预算无空格 token 硬切无损且有界、预算内不切、超限前置拒绝、星面字符切点不破坏码点、围栏块与硬切片段互不串段。cargo 215/0。
- 2026-09-26：**C21**（`ddd6200`）——推荐证据溯源：芯片与说明行悬停显示证据等级 + 目录来源主机 + 采集时间（本机时区）；无目录事实时只声明等级。LogicTests 287/0、PureTests 26/0、Release 构建 0 错误。
- 2026-09-26（第三轮）：
  - **压测架提交落地**（`40c6f61`，经 `git apply --cached` 精确分离本会话 hunks，未吞并并行会话工作）。
  - **稳定树终判**（独立 worktree @ 40c6f61，先验证 288/0 + cargo 215/0）：Settings 200/200=已定位描述符泄漏（修复在并行 WIP 待落地）；Main/Panel/QuickSearch 各 1/200 幸存（根因追踪为下一动作）；TTS 释放 PASS。档案已复制主树 `artifacts/lifecycle/`。
  - **C15/C16 残留核实已由后续波次修复**（关闭到托盘开关已有、编辑态页头主按钮已降级）；**C18** 高对比状态色/交互态映射已在后续波次完成，**U10 文档过期已修**（DESIGN_SYSTEM 60/30/10 → 实际表面/强调/语义结构 + HC 覆盖说明）；**C19** 旧图标资产清理（6 个 v1-v3 文件删除，`81a1739`）。
  - **并行 WIP 影响实证**：共享树上跑套件 204/89——失败全部是「不能创建多个 Application 实例」级联，根因是并行会话 WIP 的排队异常触发产品熔断（与 C12 压测发现的机制一致），与本会话提交无关（稳定 worktree 同代码 288/0）。**主树上的测试结论在并行 WIP 落地前不可信**——所有验证均应在独立 worktree 进行。
- 2026-09-26（第四轮，C12 收口进行中）：
  - **随行证据链补齐**：隔离诊断日志（`%TEMP%\popglot-logic-tests
un-*/logs/crash-20260926.log`）记录了压测翻动期间的成批 `InvalidOperationException`（同一秒内 20+ 条、stage=unknown）——这就是触发产品熔断的源异常，发生在单个泵队窗口内，因此逐圈风暴复位来不及。压测架已改用 ContextIdle 层级排空（SystemIdle 帧不会执行更早的 ContextIdle/ApplicationIdle 待办——修正了此前"深排空"的层级错误）并打印/留档幸存者圈号（`eb24f7d`）。
  - **级联间歇性的工作假设**：压测进程与并行会话的测试进程并发时，App 启动热键注册可能间歇失败→启动失败路径异步 Shutdown→cycle 2 起全面级联；且两个会话的测试进程会互相争抢全局热键。**结论：C12 压测必须在独占时段跑（无任何并行测试进程）**，已记入执行前提。
  - **下一动作**（等并行会话 WIP 落地+独占时段）：在 worktree 重跑 `lifecycle-stress` → 读 survivor cycle → 若恒为第 1 圈则定位首窗根（疑 Application.MainWindow 赋值语义），若恒为末圈则定位收尾根（疑 idle 队列残留）→ 出四家族最终判定。
- 2026-09-26（第五轮，C12 终判达成）：独占时段在 worktree（eb24f7d+判定修正）复跑两次，判定**稳定复现**：
  - **QuickSearch：全合同 PASS**（alive=0 计平台驻留、主题回基线、WS 445→438MB 平台、堆恒定）。
  - **Main：回收 PASS**（199/200 可回收；唯一幸存者=最后显示的窗口，IsLoaded=false 且被 `Application.Windows` 保留——WPF 平台驻留，探针 A 在裸 Window 上同样复现 1/200）。「主题订阅 0→1」残留附着在该平台驻留窗口上：其关闭序列（Closed 派发）在无消息循环的压测线程上未走完，产品侧订阅本身对称（199/200 已退订）——**真机消息循环复核（E3 型）确认后即可闭合**。
  - **Panel：回收 PASS**（平台驻留），但 **WS 单调增长 13-15/19 仍开**——窗口对象可回收而 WS 增长指向原生/非窗口资源，CSV 已归档待分析（open item）。
  - **Settings：真实泄漏 200/200 判定成立**（platformRetained=False 正确区分），根=描述符静态根，修复在并行 WIP 待落地转绿。
  - **TTS：PASS**。判定逻辑已区分「平台驻留」（恰 1 个 IsLoaded=false 幸存者且在 Application.Windows 内）与「真实泄漏」（IsLoaded=true 幸存者或 ≥2 个）——压测架最终形态已同步主树并归档 `artifacts/lifecycle/c12-final-run*.log`。
- 2026-09-26（第六轮，C12 判定再收敛）：**Panel 家族转绿**——CSV 复盘证明所谓"WS 单调增长"是逐圈 ±3MB 抖动的噪声误报（全程实际 +0.5MB、堆 +1.1MB、句柄/线程恒定），判定加 2MB 噪声阈值后 Panel 全合同 PASS（WS 439→287MB 实为回落）。**当前判定：QuickSearch ✅ Panel ✅ TTS ✅；Main 回收 ✅（主题订阅残留附在平台驻留窗口上，待真机消息循环复核）；Settings ❌（描述符泄漏，修复在并行 WIP 待落地）**。压测架噪声阈值修正同步主树（双树一致），档案 `artifacts/lifecycle/c12-final-run3.log`。
- 门禁记录：本轮所有提交前后共 3 次全量验证（cargo test --locked / fmt / clippy、C# Debug+Release 0 警告 0 错误、PureTests 26/0、LogicTests 286→287/0、隔离目录、真配置哈希不变、`no unsanctioned public network send` PASS）。
- 2026-09-26：**C10 组件级基准**（`762f3ce`）——经真实协调器 + mock 执行器：100 次完成回环 P50=15.5ms / P95=16.2ms / max=16.3ms / 失败 0；100 次取消 P95≈0ms / 错阶段 0；以「完成写入历史恰 100 条、取消不写」作完成/取消交叉验证。LogicTests 288/0。诚实口径：测试宿主组件管线，非应用级 hotkey→painted 预算（归 scripts/measure-*）。

## 剩余未完成与阻塞（如实保留）

| 项 | 状态 | 阻塞原因 / 下一步 |
|---|---|---|
| C10 生产侧结构化时间点 | 部分 | 组件基准已落地；hotkey→shell→selection→sent→first-delta→painted 生产埋点仍待做（涉及热路径，需专项波次） |
| C12 生命周期 4 窗×200 开关 + WS 验收 | 未完成 | 真机可跑但耗时长；需独立时段执行并留档（下一轮首选） |
| C13–C19 各残留子条款 | 部分 | 逐条为：结构化 reason code（C17）、错误原文折叠交互（C14/C17）、高对比交互态映射（C18）、旧文档 10 轮往返复核（C18）、Narrator 走查（C19，E3）等；按波次推进 |
| C22 免费引擎商业 ADR | **阻塞** | 负责人商业决策，实施者不可代做 |
| C23 四 Provider 真网 E2E | **阻塞** | 需用户显式网络授权 + 真实引擎可用（其网络环境不稳） |
| C24 签名/更新/回滚 | **阻塞** | 证书采购属负责人行为（README §G 禁止代购） |
| C25 剩余 | 基本闭环 | 「重新打开新手引导」已在；3 分钟完成度走查属真机 E3 |
| C26 商业发布总验收 | **阻塞** | 依赖 C22–C25 + 负责人签字 |
| E3 真机矩阵（D1–D9） | 未验证 | 真机环境操作（失焦矩阵、自启 20 次、跨应用剪贴板、熔断演练、重启交接、读屏旅程） |
| 原始输入文档 | **阻塞（B01）** | `docs/prompts/2026-09-26-desktop-product-completion.md` 与同名 SPEC 不存在；已按仓库内等价合同执行并在此留痕 |
