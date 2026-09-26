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
