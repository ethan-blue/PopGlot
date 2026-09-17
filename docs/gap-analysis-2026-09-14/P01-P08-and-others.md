# 对账H：P01–P08 + 其他规划文档未兑现承诺清点

> **编制日期**：2026-09-14
> **编制人**：Pi（Teammate / 对账H）
> **任务**：对账 P01–P08（Prompt 个性化全家桶）"规划 vs 现实"；并清点其他规划文档（REFACTOR_MASTER_PLAN / UI-REFACTOR-PLAN / EXECUTION_BOARD / PRODUCT_SPEC / UX_DECISIONS）中仍未兑现且未被台账 C/N/UI/P 体系覆盖的承诺（去重）。
> **方法**：只读分析。证据基线：Git HEAD `9e4832a`（工作树干净）。合同：`docs/review-2026-09-12/PROMPT-PERSONALIZATION-AND-GLM-HANDOFF.md` §7（任务卡）+ §2（现状基线）+ §4（编译合同）+ §8（最小切片）。
> **交叉引用**：视觉/无碍验证缺口见对账G（`UI01-UI07.md`）；性能测量现状见对账B；N06/N15/N17 见对账E/F；C16 见对账C。

---

## 一、P01–P08 总判定表

| 任务 | 内容 | 判定 | 证据 |
|---|---|:---:|---|
| **P01** | 模板领域模型、版本与安全存储 | **NOT_STARTED** | `crates/popglot-domain/src/` 仅 language.rs，无模板模型；`ShellSettings.cs:215-230` 无任何模板/风格字段；全库无 schemaVersion/id/revision 模板记录 |
| **P02** | 有限变量与纯编译器 | **NOT_STARTED** | `provider.rs` 无 `{{source_language}}` 等四变量白名单与编译器；`StreamPromptBuilder`（:80-183）仅生成固定协议指令，无 preference 输入 |
| **P03** | 四协议和输出保真接入 | **NOT_STARTED** | `provider.rs:1344+` 四协议映射只消费 system_instructions/stream payload，无偏好层接入点 |
| **P04** | 模板管理、选择、预览与可取消试译 | **NOT_STARTED** | WPF 全库 grep "风格/模板/偏好/template/preference" 零业务命中（ServicesSection 的"推荐偏好"是模型推荐 Speed/Balanced/Quality 单选，非 Prompt 风格）；设置窗无"翻译偏好"子页（SettingsWindow.xaml 导航仅 翻译引擎/通用/快捷键/隐私与数据，:45-61）；主工作台无风格选择器 |
| **P05** | 请求快照、缓存和历史可追溯 | **NOT_STARTED** | 模板不存在故无 templateId/revision 快照；历史记录无模板版本标注链路 |
| **P06** | 场景、匹配术语与一次性背景 | **NOT_STARTED** | 无 domain/audience 字段、无一次性背景输入；N06 术语库本体亦未实施（见对账E），仅协议侧有 glossary 合同占位（prompt_contract.rs:493） |
| **P07** | 配额、错误、导入导出和退化 | **NOT_STARTED** | 无 50 上限/8KiB 限额/导入导出/不支持个性化的"unsupported"声明链路 |
| **P08** | 质量、稳定性与功能放行 | **NOT_STARTED** | 无 80 条非隐私语料 + 每风格 10 条对照的质量放行流程（依赖 P01–P07 与 N15，均未启动） |

**统计：NOT_STARTED 8 / 8。** 与任务预期一致：2026-09-13 的 W1–W18 全部为 UI/性能波次，未触碰 Prompt 链（WPF/Rust 两侧 grep 均零命中）。

### 1.1 现有能力基线（P 链可复用的地基，≠ 功能已存在）

`crates/popglot-core/tests/prompt_contract.rs`（14 项，2026-09-12 台账全过）守住的正是 P02/P03 合同中的"**不可编辑协议底线**"（§4.1 层次 1–2）：

| 类别 | 测试（行号） | 对应 P 合同的意义 |
|---|---|---|
| A | source_text_never_enters_system_instructions (:244) | P02"源文同符号原样进入数据、不内插系统指令"的底线已机器验证 |
| A | text_first_and_version_invariants (:299) | 编译策略版本不变量存在（P05 快照可挂接的锚点） |
| A | delimiter_validation_rules (:334) | "delimiter 由每次请求生成、用户不能控制"的底线已验证 |
| A | vision_vs_text_dispatch (:384) / explanation_toggle (:447) | 请求结构选择层（§4.1 层 3）行为已冻结 |
| A | token_protection_and_structural_rules (:477) | 保护 token 高于一切偏好（§4.1 层 4 优先级的地基） |
| A | glossary_protocol_contract (:493) | 术语"由结构字段携带"的协议位已预留（P06 接入点） |
| B | synthetic_stream_chunking_and_hard_gate_scoring (:670) 等 7 项 | 输出保真/完整性硬门（P03"输出失败按 Partial/Failed 规则"的执行机构） |

**结论**：协议安全底线（源文隔离、delimiter、token 保护、流式完整性）已有测试护栏；P01–P08 需要的**用户侧全部能力（存储/编译/接入/管理/快照/场景/配额/质量）为零**。合同 §2 的警告仍然成立且需在台账固化："现有内置 Prompt 不等于已有完整自定义功能"——避免后续轮次把固定协议指令误认作模板功能。

---

## 二、其他规划文档未兑现承诺清单（去重后）

> 去重规则：已被 C/N/UI/P 体系覆盖的只给交叉引用不展开；"已完成"误传为未做的予以澄清。来源文档中 EXECUTION_BOARD.md 与 UI-REFACTOR-PLAN.md 均为"历史归档·已完成"状态，只清点其"剩余/深水区"小节。

### A. 视觉与无障碍验证（交叉引用 对账G，不重复展开）

| # | 承诺 | 来源 | 现状 |
|---|---|---|---|
| A1 | 多屏/DPI 真机回归矩阵 + 人工比对记录 | EXECUTION_BOARD 剩余；REFACTOR_MASTER_PLAN P2"视觉回归矩阵：浅/深/高对比 × 100–200% × 中英文" | **未做**。W1–W18 零波次后截图；旧轮 T7 自认未验证（详见对账G UI07） |
| A2 | Narrator 全旅程录音验证、高对比人工走查 | EXECUTION_BOARD 剩余；REFACTOR_MASTER_PLAN P1.7 | **未做**。仅有 AutomationProperties 基础设施（详见对账G UI06） |
| A3 | 文本放大 200% 单独验证 | V2 §15.2（PRODUCT_SPEC 未列，属 UI 验收体系） | **未做**，且字号设置功能本体未实施（对账G UI06） |

### B. 性能与预算一致性（交叉引用 对账B）

| # | 承诺 | 来源 | 现状 |
|---|---|---|---|
| B1 | 性能预算复测：冷启动延迟建窗后的 Release 数字记录 | EXECUTION_BOARD 剩余 | **未做**（scripts/measure 体系与 fixture 已就绪，真实复测记录待对账B 核实） |
| B2 | 性能预算"只有一个真相来源" | REFACTOR_MASTER_PLAN P0.8（针对默认值）+ §7 预算表 | **冲突未解**：PRODUCT_SPEC §性能预算（冷启动 P95 ≤1.2s、工作集 80/120 MiB）与 REFACTOR_MASTER_PLAN §7（≤1.5s、≤100 MiB）两套数字并存且不一致，无声明谁为准 |

### C. CI 与工程防线（未被 C/N/UI/P 覆盖的新发现）

| # | 承诺 | 来源 | 现状 |
|---|---|---|---|
| C1 | CI 实际运行 Windows Shell 测试 + FFI 并发 | REFACTOR_MASTER_PLAN P1.9；EXECUTION_BOARD 第一轮剩余 | **大部分已兑现**：`.github/workflows/ci.yml` rust-core 三平台跑 `cargo test --workspace`（含 FFI）+ clippy + fmt；windows-shell 跑 Release 构建 + LogicTests（注释明示含零出网旅程/草稿隔离/热键往返/DPI 截图矩阵）。**缺口：PureTests（18 项，含 C03 净化注入矩阵）未接入 CI**，`scripts/verify.ps1` 同样只跑 LogicTests——净化红线目前只靠本地手跑守护 |
| C2 | WPF 启动/主旅程 smoke 入 CI | REFACTOR_MASTER_PLAN P1.9 后半句 | **部分**：LogicTests 含设置窗构造/关闭等，但独立"启动冒烟"用例未见（归并为 C1 一并补齐即可） |

### D. 产品功能承诺（未被 C/N/UI/P 覆盖）

| # | 承诺 | 来源 | 现状 |
|---|---|---|---|
| D1 | 供应商添加分步向导（列表→添加→凭据→测试草稿→保存） | REFACTOR_MASTER_PLAN P1.4；EXECUTION_BOARD 剩余 | **未做（增强级）**。现形态为列表+编辑表单+预设（Master-Detail），已达"不展开高级即可完成添加"验收线；SS 审计后表单流程可用。向导属 P2 增强，无对应缺陷 |
| D2 | 截图四角可拖拽控制点 | UX_DECISIONS §3"四角出现控制点" | **承诺与现实相反**：代码从无可拖拽手柄；W3/WIN-09 反而移除了"不可拖拽的假手柄"（当时被审计为误导缺陷）。即：要么实现真可拖手柄，要么更新 UX_DECISIONS 文字——现状是文档承诺了一不存在的东西（另见对账G UI05 确认模式缺口） |
| D3 | 跨应用 hover 取词 / 双击 Ctrl 增强取词 / UIA TextPattern 取词 | PRODUCT_SPEC P1；UX_DECISIONS §1（列为"可作增强项"） | **未做**（P1 尾部，无排期） |
| D4 | 常用语言语法增强、普通软件布局翻译 | PRODUCT_SPEC P1 | **未做**（远期） |
| D5 | 连续区域翻译、离线本地大模型、IDE/浏览器集成、macOS/Linux 移植 | PRODUCT_SPEC P2 | **未做**（远期 backlog，合理搁置） |
| D6 | 会话级测试状态不持久化（重启回「未测试」） | UI-REFACTOR-PLAN 已知偏差 | **仍在**（小项，非缺陷级：ServicesSection TestStatusPanel 为会话态） |

### E. 文档一致性勘误（未被覆盖的新发现，小额）

| # | 问题 | 证据 |
|---|---|---|
| E1 | UX_DECISIONS §6 强调色写"冷靛蓝 Light `#5B5BD6` / Dark `#8B8FF7`"，与 ThemeService 现值不符（Light Accent `#5563B8`、Dark Accent `#7C89D9`，ThemeService.cs:263,312）——旧调色盘残留 | 两值均不在当前 Token 表中 |
| E2 | PRODUCT_SPEC §验收"全量 113 项 Windows 逻辑测试"已过时（2026-09-13 全量实跑 192 项） | RESULT-2026-09-13-user-ui-repair.md:51 |
| E3 | UX_DECISIONS §7 流式/终态"字号 15px 行高 22px"与现实现 14.5 DIP（Content Large 阶梯）有轻微漂移；连同 §3 四角控制点（D2），该文档多处需与代码对账 | QuickSearchWindow.xaml ResultStreamBox FontSize=14.5 等 |

### F. 已兑现澄清（防止误列为未做——归档文档中的"剩余"已被后续轮次封闭）

| 项 | 归档文档说法 | 现实 |
|---|---|---|
| 控制中心五区导航 + 资料库独立页面 | EXECUTION_BOARD 第一轮"剩余" | **已兑现**：SettingsWindow 四专区导航（:45-61）+ 主窗资料库视图 |
| 模型下拉为静态列表、未接"获取模型列表"API | UI-REFACTOR-PLAN 已知偏差 | **已兑现**：`Services/ModelCatalogService.cs:40 FetchAsync`（W2 波次 SS-02 亦佐证"获取模型"按钮存在） |
| 主窗口延迟创建 / UI 线程 Sleep 清理 | EXECUTION_BOARD 第一轮"剩余" | **已兑现**：`App.xaml.cs:706 EnsureMainWindow`（"Builds the main window on first use"）+ W8/W9/W15 异步化波次 |
| PureTests/FFI 测试"仅本地 verify.ps1" | EXECUTION_BOARD 第一轮原文 | **已过时的一半**：CI 已跑 LogicTests+cargo workspace；剩余缺口仅为 PureTests 未入 CI（见 C1） |

---

## 三、建议下一步（3 条）

1. **在台账固化 P 链现状声明**：EXECUTION-LEDGER 增补一行"P01–P08 NOT_STARTED（2026-09-14 对账H），prompt_contract 14 项 = 协议底线基线"，并引用合同 §2 警告——防止任何后续轮次把内置固定 Prompt 误当作"模板功能已存在"而跳过 P01 存储/编译合同直接做 UI。
2. **文档一致性小额清理（半天内可完成）**：合并两套性能预算表为单一真相来源（建议 PRODUCT_SPEC 为准并修订 REFACTOR_MASTER_PLAN §7 或加"以 PRODUCT_SPEC 为准"注记）；修 UX_DECISIONS 调色盘值与四角控制点描述、PRODUCT_SPEC 测试计数。文档失真已在两轮对账（G/H）中反复造成额外核查成本。
3. **把 PureTests 接入 ci.yml**（一行 `dotnet run --project tests/PopGlot.Windows.PureTests`）：C03 净化注入矩阵是"绝不落盘"红线目前唯一的机器护栏，不应只存在于本地手跑；顺带满足总纲 P1.9 的完整性。

---

## 四、2026-09-15 后续事实更新（P 链已开工；上文 §一–§三为 2026-09-14 时点判定）

> 本节为事后追加。负责人 2026-09-14 授权 P 链实施后，**Prompt 0.1.6 闭环已在当前工作树实现**——版本号尚未提升、未发布（版本仍为 0.1.5，CHANGELOG 以「未发布（Unreleased）」记录）。

### 4.1 P01–P08 当前判定

| 任务 | 当前判定 | 代码证据 |
|---|---|---|
| **P01** | **已实现** | `crates/popglot-domain/src/prompt.rs`（PROMPT_SCHEMA_VERSION=1、revision + 最近 5 次修订、内置 faithful/natural/formal）+ `crates/popglot-core/src/prompt_store.rs`（独立 `prompt-templates.json`、≤4 MiB、≤50 自定义、临时文件+flush+原子改名、`.bak`/corrupt 备份）+ FFI `popglot_list/get_active/set_active/save/delete_prompt_template` |
| **P02** | **已实现** | 四变量白名单纯编译器 `compile_prompt`/`compile_instruction`（≤8 KiB 正文、≤12 KiB 编译结果、未知占位符立即失败、无递归）；FFI `popglot_compile_prompt` 与 C# `CoreBridge.CompilePromptPreview` 本地预览零网络 |
| **P03** | **已实现（共用链路）** | 激活模板在请求发起时解析（faithful=零改动），按「仅语气与用词、永不覆盖代码保护与格式」规则注入 system instructions 与流式 payload；四大协议共用该路径；编译失败降级为无偏好（`provider.rs` preference/template_id/template_revision + FFI `resolve_active_preference`） |
| **P04** | **部分实现** | 设置窗「翻译与提示词」`Sections/PromptSection.xaml(.cs)`（列表/新建/编辑/删除/设默认/复制内置/本地编译预览/草稿守卫）+ 主窗与极速查词风格快切；**缺：真模型可取消试译** |
| **P05** | **已实现（身份层）** | 请求发起即快照 `PromptTemplateIdentity`（id/名称/修订号，绝不携带指令正文）；历史记录 `PromptTemplateId/Name/Revision`；恢复零网络本地标注 |
| **P06** | **部分实现** | 模板自带 domain/audience 静态默认值参与编译；**缺：请求级一次性背景**；术语库仍归 N06 未实施 |
| **P07** | **部分实现** | 配额（50/8 KiB/12 KiB/4 MiB）、结构化错误、编译失败降级已实现；**缺：导入导出** |
| **P08** | 未实施 | 80 条语料质量放行流程仍无 |

统计：P01/P02/P03/P05 已实现，P04/P06/P07 部分实现，P08 未实施；未实现/排除项已在 PRODUCT_SPEC「当前未实现与明确排除」固化，不得对外宣称超出上表的能力。

### 4.2 §二清单的后续闭合

- **C1 缺口已闭合**：`scripts/verify.ps1` 现依次运行 PureTests 与 LogicTests；`.github/workflows/ci.yml` windows-shell job 亦新增 PureTests 步骤——净化红线不再只靠本地手跑。
- **B2/E1/E2/E3 已由文档勘误波次修复**：`REFACTOR_MASTER_PLAN.md` §7 加注「以 PRODUCT_SPEC 为准」单一真相来源；`UX_DECISIONS.md` 调色盘值、四角控制点描述、字号行高描述均与当前代码对齐；PRODUCT_SPEC 验收计数改为动态口径（截至 2026-09-15 直接实跑 LogicTests 203 项 / PureTests 23 项全绿，随新增测试变化，以实跑为准）。
- §二 A 组（视觉/无障碍真机验证）与 D 组远期项维持 2026-09-14 判定不变。
