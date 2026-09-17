# PopGlot 任务状态与接力检查点

> 唯一当前进度索引。先读 [README](README.md) 和 [最新独立复核及连续计划](POST-C00-C07-REVIEW-AND-CONTINUOUS-PLAN.md)。本文件不是新的实施授权；C00–C07 已有用户授权记录，后续扩展使用新计划中的开工指令。
> 记录日期：2026-09-12。首次落盘为 REVIEW_ONLY，随后进入 C00–C07 实施；本次源码复核后 C00 待完整验证，C01–C07 返回 TODO 待返修（不表示从未改过代码）。UI 是 C/N 工作分解，60 条不等于 60 个独立功能或工期。

## 1. 状态规则

- SPECIFIED_NOT_IMPLEMENTED：已写规格，未获本轮实施授权/未领取。
- TODO：负责人已批准范围，待执行。
- IN_PROGRESS：已领取，须有执行者、开始时间、子包。
- READY_FOR_REVIEW：实现者提交，必要证据附后，独立复核尚未完成。
- BLOCKED：列具体依赖/环境/权限阻塞、尝试和所需输入；不等于可以扩大任务。
- DONE_VERIFIED：独立复核者针对当前构建通过必要验收，写复核身份和证据。

规划状态不直接跳 DONE_VERIFIED；先授权、领取、实施、验证。无需某子包时写“不适用 + 可核查理由”，不能删掉整任务。L3 研究保持规划状态，不进入默认 TODO。旧记录追加保留；当前表随证据更新，不能清除不通过历史。

## 2. 全量任务表

| ID | 目标 | 当前状态 | 合同定位 | 当前证据/下一步 |
|---|---|---|---|---|
| C00 | 测试隔离与可信基线 | DONE_VERIFIED | 第一轮 §8；POST-C00-C07 §2 | 已闭环（对账A docs/gap-analysis-2026-09-14/C00-C07.md §2, §3）：实例在场 exit 3 单一错误拦截（2026-09-12 真机实证 PID 32548）；无实例全量 LogicTests 3×180/0 + 185/185 全绿；配置哈希不变（套件内建断言 PASS）。HEAD 9e4832a 验证通过 |
| C01 | 免费引擎最终发送授权 | DONE_VERIFIED | 第一轮 §8；POST-C00-C07 §2/A04 | 已闭环（对账A docs/gap-analysis-2026-09-14/C00-C07.md §2, §3）：A04 线性化先核验后原子消费、构建失败不烧许可、once-only 拒签发后 Denied；W18 Interlocked.Exchange 硬件级单发原子性复验全绿；pure 8 场景与套件全过；真实网络 E2E 归 C23（列债 D9） |
| C02 | 词库超限/损坏/写入恢复 | DONE_VERIFIED | 第一轮 §8；POST-C00-C07 §2/A08 | 已闭环（对账A docs/gap-analysis-2026-09-14/C00-C07.md §2, §3）：A08 有界读取/严格 UTF-8/损坏只读/快照重试清错误态；V04 UI 损坏重试接线；W8 异步化后经 W16 返修闭环（写失败可见、快照回滚、并发不丢更新）；超限 fixture 哈希不变；列债 D5/D6′ |
| C03 | 诊断最小化与脱敏 | DONE_VERIFIED | 第一轮 §8；POST-C00-C07 §2/A09 | 已闭环（对账A docs/gap-analysis-2026-09-14/C00-C07.md §2, §3）：A09/V01 栈帧仅来自运行时结构化 API，非帧行/message/Data/InnerException 绝不落盘；W9 异步化 + W15/W18 Flush 接线；注入矩阵/轮转预算全绿；导出功能不存在据实记“不适用”（列债 D6） |
| C04 | 代码与结构保真 | DONE_VERIFIED | 第一轮 §8；POST-C00-C07 §2/A07 | 已闭环（对账A docs/gap-analysis-2026-09-14/C00-C07.md §2, §3）：A07 围栏符号与长度追踪（四反引号含内三反引号逐字符一致）；7 探针 + 三入口一致（W17 修复回归后全绿）；LF 规范化合同明确；跨应用剪贴板留 E3 列债 D4 |
| C05 | 关闭/失焦/取消/恢复 | DONE_VERIFIED（附条件） | 第一轮 §8；POST-C00-C07 §2/A05,A06 | 已闭环（对账A docs/gap-analysis-2026-09-14/C00-C07.md §2, §3）：隐藏完成不碰剪贴板（A05/W17）；恢复不补发；查词失焦保留会话；Esc 先菜单/IME 后取消；最近使用恢复；强关真销毁；测试全绿。**挂验证债 D1**：真机失焦矩阵（IME/Alt+Tab/系统通知等）留 E3，保持未验证 |
| C06 | 全局异常策略 | DONE_VERIFIED（附条件） | 第一轮 §8；POST-C00-C07 §2/A10 | 已闭环（对账A docs/gap-analysis-2026-09-14/C00-C07.md §2, §3）：泛型 InvalidOperation 不再一律恢复；RuntimeGate 熔断拒绝新任务；重启先清旧资源后交互斥体；W18 修复退出时序断言；测试全绿；崩溃风暴已修。**挂验证债 D2**：E3 注入演练与真实重启交接挂账 |
| C07 | 开机启动真实状态 | DONE_VERIFIED（附条件） | 第一轮 §8；POST-C00-C07 §2/A01-A03 | 已闭环（对账A docs/gap-analysis-2026-09-14/C00-C07.md §2, §3）：A01 PlanSaveAction 普通保存绝不写禁用；A02 BuildRunCommand/ExtractExecutablePath 命令合同；A03 OsDisabled 三态诚实；V03 磁盘真相+重试锁（W18 保持合规接线）；8 状态矩阵全绿。**挂验证债 D3**：真机临时账户登录 20 次留 E3 挂账 |
| C08 | 稳定安装路径与自启修复 | NOT_STARTED | 第一轮 §8；对账B docs/gap-analysis-2026-09-14/C08-C12.md §0, §1 | 未实施：无 ADR、无安装器、发布形态为便携 zip、无移动文件夹提示；仅前置地基（RepairRunPath 不覆盖 OS 禁用）借道 C07 落地 |
| C09 | Release 启动与空闲性能 | READY_FOR_REVIEW | 第一轮 §8；对账B docs/gap-analysis-2026-09-14/C08-C12.md §0, §1, §7 | 最终代码树正式复测 PASS（`artifacts/perf/startup.json`：30/30，P50=383/P95=402，600/1200ms 内；WS=103.53MiB 硬门内但 80MiB 软目标未达；空闲 CPU 0%；CPU 型号已记录；dirtyDiffHash a9dda31588861ddc；夹具 41/41）。旧「真实 30 次测量从未执行/缺 startup.json/缺 CPU 型号」为旧时点陈述，已被复测取代（取代声明见对账B §7 与 §6 同日记录）；待独立复核 |
| C10 | 热键首帧与取消性能 | PARTIAL | 第一轮 §8；对账B docs/gap-analysis-2026-09-14/C08-C12.md §0, §1 | 部分完成：W11 首帧瘦身（PERF-HOTKEY-02/03）与 W7 流式节流 60ms 落地；缺口：无结构化时间点埋点、无 100 次基准、隐藏窗口预热方案已撤回 |
| C11 | UI 线程 I/O 与激活卡顿 | PARTIAL | 第一轮 §8；对账B docs/gap-analysis-2026-09-14/C08-C12.md §0, §1 | 部分完成：PERF-IO-01~06 全部异步化（词库/历史/注册表/Profile/日志/重启交接）；缺口：MainWindow.RefreshEngineStatus 每次 Activated 同步执行 CredRead 与 File.ReadAllText，违反合同 |
| C12 | 生命周期与内存回收 | PARTIAL | 第一轮 §8；对账B docs/gap-analysis-2026-09-14/C08-C12.md §0, §1 | 部分完成：SettingsWindow 订阅对称释放、ThemeService 退订系统事件、跨线程加固；缺口：四窗口×200 开关与 WS 非单调增长等真机验收未执行 |
| C13 | 主工作台 | PARTIAL | 第一轮 §8；对账C docs/gap-analysis-2026-09-14/C13-C19.md §0, §1 | 部分完成：W7/W12 三断点（<720 折叠+堆叠/≥960 宽屏 1:1.25）、MinWidth=560、W2 空态引导卡落地；缺口：清空未降级为次要动作、缺空态三入口、底栏缺隐私/会话状态、7 态验收矩阵未测 |
| C14 | 极速查词与浮窗 | PARTIAL | 第一轮 §8；对账C docs/gap-analysis-2026-09-14/C13-C19.md §0, §1 | 部分完成：W3/W7/W11/W12 按钮 32×32、Pin 动态提示、失败修复路径、查词工作区居中+高度自适应；缺口：标题栏低频动作未收纳进「更多」菜单、原始异常消息未折叠 |
| C15 | 通用设置与关闭行为 | PARTIAL | 第一轮 §8；对账C docs/gap-analysis-2026-09-14/C13-C19.md §0, §1 | 部分完成：W2/W5/W13 启动独立分组与实际状态、修复按钮、草稿守卫、未保存徽章；缺口：缺主窗口 X 行为选项（固定关托盘）、主题即时生效与保存条混排无提示 |
| C16 | 服务配置流程 | PARTIAL | 第一轮 §8；对账C docs/gap-analysis-2026-09-14/C13-C19.md §0, §1 | 部分完成：W2/W5 单一添加入口、测试连接降次要、高级折叠、删除隔离+二次确认、无 Key 拦截；缺口：编辑态顶部未隐藏添加按钮、测试连接无时间戳 |
| C17 | 统一空态/错误/反馈 | PARTIAL | 第一轮 §8；对账C docs/gap-analysis-2026-09-14/C13-C19.md §0, §1 | 部分完成：W1/W2/W3/W7/W16 FriendlyError 词典三入口共用、取消独立状态、partial 警示色调与复制门；缺口：无结构化 reason code（依赖异常子串匹配）、原始异常仍直接上屏未折叠 |
| C18 | 主题热切换与高对比 | PARTIAL | 第一轮 §8；对账C docs/gap-analysis-2026-09-14/C13-C19.md §0, §1 | 部分完成：W1/W4/W6 MarkdownPresenter 全动态 SetResourceReference、高对比系统色覆盖与 UserPreferenceChanged 订阅；缺口：状态色未系统色化、高对比人工走查未跑、U10 文档失真未修 |
| C19 | 图标与可访问性 | PARTIAL | 第一轮 §8；对账C docs/gap-analysis-2026-09-14/C13-C19.md §0, §1 | 部分完成：W2/W7/W12 v5 九档图标、全库 AutomationName 覆盖、FocusRing 焦点环、IME Enter 防护；缺口：多尺寸图标视觉审核未做、真机 IME/读屏/全键盘走查未做 |
| C20 | 分段与长标识符 | PARTIAL | 第一轮 §8；对账D docs/gap-analysis-2026-09-14/C20-C26.md §逐项判定 | 部分完成：Rust core 分段预算体系与 C01 每段授权落地；缺口：长标识符保真未测、分段与 C# 围栏交互未验证 |
| C21 | 推荐证据时间与身份 | PARTIAL | 第一轮 §8；对账D docs/gap-analysis-2026-09-14/C20-C26.md §逐项判定 | 部分完成：W2 展示层证据分级去按钮化（官方/推断/实测三档）；缺口：证据无采集时间戳、无来源身份标识，不可追溯 |
| C22 | 免费引擎商业决策 | NOT_STARTED | 第一轮 §8；对账D docs/gap-analysis-2026-09-14/C20-C26.md §逐项判定 | 未实施：全库无 ADR 文档，商业门禁停留在规划，需负责人决策计费模式与公共服务保留 |
| C23 | Provider 网络和 E2E | PARTIAL | 第一轮 §8；对账D docs/gap-analysis-2026-09-14/C20-C26.md §逐项判定 | 部分完成：provider_http 27 项通过，EndpointsOverride 支持构建失败注入；缺口：四协议真实网络 E2E 往返验收未执行 |
| C24 | 安装签名更新回滚 | PARTIAL | 第一轮 §8；对账D docs/gap-analysis-2026-09-14/C20-C26.md §逐项判定 | 部分完成：publish-package.ps1 发布清单与全文件哈希核验；缺口：代码签名、更新源验证、升级回滚未做 |
| C25 | 首次体验帮助配置 | NOT_STARTED | 第一轮 §8；对账D docs/gap-analysis-2026-09-14/C20-C26.md §逐项判定 | 未实施：仅有 W2/W3 无服务引导卡子集；完整首启流程、演示模式与离线帮助未做 |
| C26 | 商业发布总验收 | NOT_STARTED | 第一轮 §8；对账D docs/gap-analysis-2026-09-14/C20-C26.md §逐项判定 | 未实施：依赖 C22–C25 与 E3 全矩阵，商业放行签字必须负责人完成 |
| N01 | 会话暂存与接续 | PARTIAL | V2 §5；对账E docs/gap-analysis-2026-09-14/N01-N09.md §1, §2 | 部分完成：托盘恢复最近翻译、隐藏可见性门禁、展开到工作台已做；缺口：可选独立快捷键、多会话仓（≤5会话/2MiB）、草稿覆盖选择未做 |
| N02 | 输入/IME/来源复制 | PARTIAL | V2 §5；对账E docs/gap-analysis-2026-09-14/N01-N09.md §1, §2 | 部分完成：Enter 翻译与占位提示、专用复制、修改防抖已做；缺口：IME 组词判定缺陷未修、无 Ctrl+Enter 偏好、连续相同提交无去重 |
| N03 | 截图调整与 OCR 校对 | NOT_STARTED | V2 §5；对账E docs/gap-analysis-2026-09-14/N01-N09.md §1, §2 | 未实施：仅有框选单次截发；可拖控制点、二次确认、OCR 校对与明确上传全未实现 |
| N04 | 复制格式与 partial | PARTIAL | V2 §5；对账E docs/gap-analysis-2026-09-14/N01-N09.md §1, §2 | 部分完成：W3 显式 partial 复制放行、W14 复制纯文本落地；缺口：富文本 Markdown 复制、双栏对照流式复制未做 |
| N05 | 语言与风格 | PARTIAL | V2 §5；对账E docs/gap-analysis-2026-09-14/N01-N09.md §1, §2 | 部分完成：语言选择/交换/自动识别已做；缺口：风格切换仅有模型推荐偏好无翻译风格、语言偏好持久化缺陷 |
| N06 | 生词与术语 | NOT_STARTED | V2 §5；对账E docs/gap-analysis-2026-09-14/N01-N09.md §1, §2 | 未实施：生词本 CRUD 已做但不属于术语库；术语领域模型、提取注入、双向匹配与保护全未实现 |
| N07 | 历史检索与版本 | PARTIAL | V2 §5；对账E docs/gap-analysis-2026-09-14/N01-N09.md §1, §2 | 部分完成：W6 真实计数清空、W8 异步写盘、W10 虚拟化过滤已做；缺口：跨会话按请求聚合版本、按日归档、增量清理未做 |
| N08 | 备份导入恢复 | NOT_STARTED | V2 §5；对账E docs/gap-analysis-2026-09-14/N01-N09.md §1, §2 | 未实施：仅生词本单表导出；整库备份（配置/历史/词库）、导入预检、冲突覆盖机制全未实现 |
| N09 | 朗读控制 | PARTIAL | V2 §5；对账E docs/gap-analysis-2026-09-14/N01-N09.md §1, §2 | 部分完成：W5/W7 朗读播放状态联动、图标/提示切换、可点击停止；缺口：语速音量控制、语言真实性校验、云朗读降级全未做 |
| N10 | 服务诊断与费用 | PARTIAL | V2 §5；对账F docs/gap-analysis-2026-09-14/N10-N18.md §判定总览, §逐项判定 | 部分完成：结果显示服务、隔离草稿测试、基础失败分类（401/429/404/网络）；缺口：测试取消按钮、健康时间戳、分层诊断、费用透明 |
| N11 | 首次无服务体验 | PARTIAL | V2 §5；对账F docs/gap-analysis-2026-09-14/N10-N18.md §判定总览, §逐项判定 | 部分完成：W2/W3 无服务引导卡与前往配置、免费引擎授权链；缺口：无引擎时截图未拦截、缺出网透明提示 |
| N12 | 设置搜索与保存语义 | PARTIAL | V2 §5；对账F docs/gap-analysis-2026-09-14/N10-N18.md §判定总览, §逐项判定 | 部分完成：V02/V03 纯值比对、W5 双保存条消除、W13 响应式；缺口：设置搜索、恢复默认未实现 |
| N13 | 快捷键暂停 | NOT_STARTED | V2 §5；对账F docs/gap-analysis-2026-09-14/N10-N18.md §判定总览, §逐项判定 | 未实施：专注模式、全局快捷键一键暂停、托盘对应状态指示全未做 |
| N14 | 响应布局与显示器 | PARTIAL | V2 §5；对账F docs/gap-analysis-2026-09-14/N10-N18.md §判定总览, §逐项判定 | 部分完成（兑现度最高）：W7/W12/W13 三断点、多屏工作区居中、DIP 换算与夹逼；缺口：显示器位置记忆、极低高度响应、超高分屏微调 |
| N15 | 质量与人工纠正 | NOT_STARTED | V2 §5；对账F docs/gap-analysis-2026-09-14/N10-N18.md §判定总览, §逐项判定 | 未实施：80 条基准语料、对照打分、人工纠错与回流机制全未实现 |
| N16 | 长文进度与预算 | PARTIAL | V2 §5；对账F docs/gap-analysis-2026-09-14/N10-N18.md §判定总览, §逐项判定 | 部分完成：分段会话与预算检查既有逻辑；缺口：长文进度百分比、阶段耗时透出未做 |
| N17 | 第二服务复译（L3） | NOT_STARTED | V2 §5；对账F docs/gap-analysis-2026-09-14/N10-N18.md §判定总览, §逐项判定 | 未实施（L3 级）：第二服务复译与对照，合同明示为后续保留功能 |
| N18 | 文件/学习扩展（研究） | NOT_STARTED | V2 §5；对账F docs/gap-analysis-2026-09-14/N10-N18.md §判定总览, §逐项判定 | 未实施（L3 研究级）：合同明示不进入当前开发队列 |
| UI01 | 设计基线 | PARTIAL | V2 §14；对账G docs/gap-analysis-2026-09-14/UI01-UI07.md §一, §二 | 部分完成：U01 侧栏折叠、U02 设置窗尺寸降级落地；缺口：DESIGN_SYSTEM.md 仍 px 口径、U10 未修、Spacing Token 为死资源、无截图清单 |
| UI02 | 配色与控件状态 | PARTIAL | V2 §14；对账G docs/gap-analysis-2026-09-14/UI01-UI07.md §一, §二 | 部分完成：深浅调色盘自动化对比度审计实质达标、模板状态补齐；缺口：高对比仅基础版（状态色/选中色未覆盖）、无逐控件可视化状态板 |
| UI03 | 主窗/浮窗/查词布局 | PARTIAL | V2 §14；对账G docs/gap-analysis-2026-09-14/UI01-UI07.md §一, §二 | 部分完成：主窗三断点、查词工作区居中+自适应高度已做；缺口：浮窗语言行仍在标题栏、无更多菜单、低高度未成方案 |
| UI04 | 设置/服务/隐私布局 | DONE（代码层） | V2 §14；对账G docs/gap-analysis-2026-09-14/UI01-UI07.md §一, §二 | 已闭环（代码层）：两列+窄窗单列收缩、单一保存条与草稿守卫、字段侧错误、实际状态与草稿样例均有代码证据（W2/W5/W9/W13/W16）；视觉样例归 UI07 |
| UI05 | 资料库/OCR/托盘 | PARTIAL | V2 §14；对账G docs/gap-analysis-2026-09-14/UI01-UI07.md §一, §二 | 部分完成：资料库搜索空态/分栏/删除邻选/虚拟化平滑过滤落地；缺口：截图无确认调整模式、托盘无暂停项、资料库窄窗前进返回未做 |
| UI06 | 主题生命周期与无障碍 | PARTIAL | V2 §14；对账G docs/gap-analysis-2026-09-14/UI01-UI07.md §一, §二 | 部分完成：旧文档热切主题全链路 SetResourceReference、全从属窗订阅；缺口：文本放大 200% 未做、键盘与读屏仅基础设施无旅程级证据 |
| UI07 | 集成视觉验收 | NOT_STARTED | V2 §14；对账G docs/gap-analysis-2026-09-14/UI01-UI07.md §一, §二 | 未实施：W1–W18 零波次后截图；旧轮 56 张截图属 W1 前产物；T7 多 DPI/高对比/三用户旅程全未执行 |
| P01 | 模板领域模型与存储 | NOT_STARTED | Prompt 专项 §7；对账H docs/gap-analysis-2026-09-14/P01-P08-and-others.md §一 | 未实施：popglot-domain 仅 language.rs，无模板领域模型；ShellSettings 无模板字段 |
| P02 | 有限变量纯编译器 | NOT_STARTED | Prompt 专项 §7；对账H docs/gap-analysis-2026-09-14/P01-P08-and-others.md §一 | 未实施：无四变量白名单与纯编译器，StreamPromptBuilder 仅生成固定协议指令 |
| P03 | 四协议与保真接入 | NOT_STARTED | Prompt 专项 §7；对账H docs/gap-analysis-2026-09-14/P01-P08-and-others.md §一 | 未实施：四协议映射无偏好层接入点（注：prompt_contract 14 项守住协议底线） |
| P04 | 模板管理预览试译 | NOT_STARTED | Prompt 专项 §7；对账H docs/gap-analysis-2026-09-14/P01-P08-and-others.md §一 | 未实施：WPF 无翻译偏好设置页与模板管理/编辑/预览 UI |
| P05 | 快照缓存历史 | NOT_STARTED | Prompt 专项 §7；对账H docs/gap-analysis-2026-09-14/P01-P08-and-others.md §一 | 未实施：无模板快照/缓存机制，历史记录无模板版本追踪 |
| P06 | 场景术语临时背景 | NOT_STARTED | Prompt 专项 §7；对账H docs/gap-analysis-2026-09-14/P01-P08-and-others.md §一 | 未实施：无 domain/audience/一次性背景字段，N06 术语库亦未实施 |
| P07 | 配额退化与导入导出 | NOT_STARTED | Prompt 专项 §7；对账H docs/gap-analysis-2026-09-14/P01-P08-and-others.md §一 | 未实施：无 50 模板上限/8KiB 限额/导入导出与 unsupported 降级 |
| P08 | 质量稳定性与放行 | NOT_STARTED | Prompt 专项 §7；对账H docs/gap-analysis-2026-09-14/P01-P08-and-others.md §一 | 未实施：无 80 语料对照放行流程；注：prompt_contract 14 项全过为协议底线基线 |

> **P 链现状声明（2026-09-14 对账H 建议1）**：P01–P08 判定全部 NOT_STARTED（2026-09-14 对账H）；`crates/popglot-core/tests/prompt_contract.rs` 14 项全过为协议底线基线，不可编辑协议与流式保真已受保护，但用户侧领域模型/编译器/管理 UI 为零；同日负责人已明确授权 P 链实施（从 W20a 模板领域模型/纯编译器起步）。

## 3. 当前检查点（恢复时先看这里）

- 模式：IMPLEMENTATION。负责人 2026-09-12 明确授权 C00–C07 完整实施（含合同细化、代码、UI 接入与验证）；此前 REVIEW_ONLY 表述不再约束本批任务，历史记录保留。
- 当前状态：第四轮 6 项缺口（V03-a/V05-a/V05-b/C09-a/C09-b/C09-c）返修完成：V03 基线改磁盘真相+pending 重试锁、V05 事件生命周期与信号语义重做、C09 测量边界/发布清单/最终报告重做并经隔离 fixture 22 断言验证。全量 WPF 套件、E3、真实测量仍在未验证清单；实现最多 READY_FOR_REVIEW，待独立复核。
- 已实施改动面：tests/TestIsolation.cs、tests/Program.cs、Services/OutboundPolicy.cs、FreeTranslateService.cs、Services/VocabularyStore.cs、DiagnosticsLog.cs、Services/MarkdownPresenter.cs、TranslationPanelWindow.xaml.cs、QuickSearchWindow.xaml.cs、App.xaml.cs、StartupRegistration.cs、SettingsWindow.xaml.cs、Sections/GeneralSection.xaml(+.cs)、Sections/LibrarySection.xaml.cs、ShellSettings.cs。用户原有 5 个未提交修改全部保留并吸收（划词目标窗口/修饰键释放 → C05；路径校验/自愈 → C07，其中自动覆盖 OS 禁用已按 F11 修正为「只修路径，绝不覆盖禁用」）。
- 验证状态：cargo test --workspace --locked 完整 **180 通过**（170 为先前 grep 截断漏显 ffi 10 项，差异已核实）；fmt/clippy exit 0；pure 宿主 11/11 实跑 exit 0（用户实例在场）；两 WPF 宿主编译 0 警告 0 错误；LogicTests 守卫实跑 exit 3（实例 PID 32548 在场，符合 C00 合同）。**LogicTests 全量套件待实例退出后执行**（`tests/PopGlot.Windows.LogicTests/bin/Release/net10.0-windows10.0.19041.0/PopGlot.Windows.LogicTests.exe`）；E3（失焦矩阵/登录自启 20 次/复制粘贴/熔断演练）进入验证待办。
- 下一步：① 负责人退出 PopGlot → 跑全量 LogicTests（预期 0 失败 + VerifyRealFilesUnchanged 通过）→ 据实更新本表；② 独立复核 A01–A12；③ E3 矩阵；④ W1 推进不等用户退出：C09 测量合同/测试夹具/资源清单已领取。未验证共享包按实际覆盖列于 tests/TEST-RESOURCE-CLASSIFICATION.md §未验证共享包——当前至少包括全量 WPF 套件、全部新增 WPF 行为测试与 E3 矩阵，**不是 0**。

### 2026-09-14 检查点：全量对账完成 + 负责人授权 P 链与快赢波
- 模式：IMPLEMENTATION。负责人 2026-09-14 明确指示：“全部干”——正式授权启动 P 链实施（从 W20a 模板领域模型/纯编译器起步）与后续快赢波次（W22 台账与索引刷新、W23 文档清理与死 Token 处置等）。
- 对账完成：2026-09-14 完成全量 60 项规划 vs 现实对账（8 份报告在 `docs/gap-analysis-2026-09-14/`，无冲突归档）：
  - C00–C04 升级 **DONE_VERIFIED**（对账A 证据：全量套件 180×3 全绿 + 185/185，真机实例拦截实证，配置哈希不变）；
  - C05–C07 升级 **DONE_VERIFIED（附条件）**（对账A 证据：纯逻辑/状态机全绿；挂 D1 真机失焦矩阵、D2 真实重启/熔断、D3 临时账户登录 20 次验证债留 E3）；
  - UI04 达到 **DONE（代码层）**（对账G 证据：W2/W5/W9/W13/W16 覆盖表单布局/单一保存条/字段错误/草稿样例，视觉样例归 UI07）；
  - PARTIAL 共 28 项：C09~C12（4项）、C13~C19（7项）、C20/C21/C23/C24（4项）、N01/N02/N04/N05/N07/N09（6项）、N10/N11/N12/N14/N16（5项）、UI01~UI03/UI05/UI06（5项中的4项）；
  - NOT_STARTED 共 23 项：C08/C22/C25/C26（4项）、N03/N06/N08/N13/N15/N17/N18（7项）、UI07（1项）、P01~P08（8项）。
- P 链现状声明：P01–P08 NOT_STARTED（2026-09-14 对账H），prompt_contract 14 项=协议底线基线；同日负责人已授权 P 链实施（W20a 起）。
- 下一步：W22 台账与索引刷新闭环；随后推进 W20a（P01/P02 模板模型与纯编译器）与 W23（文档勘误与 Spacing 死 Token 处置）。

### 2026-09-15 检查点：C09 最终代码树正式复测 PASS（READY_FOR_REVIEW）

- C09 最终代码树正式复测完成并通过：`artifacts/perf/startup.json`（startedUtc 2026-09-15T18:23:57Z）——30/30 成功，P50=383 ms / P95=402 ms（600/1200 ms 内 PASS）；WS=103.53 MiB 低于 120 MiB 发布硬门但**高于 80 MiB 软目标（软目标未达，列开口）**；归一化空闲 CPU=0%；CPU 型号 AMD Ryzen 9 7945HX with Radeon Graphics 已记录；dirtyDiffHash `a9dda31588861ddc`；测量夹具 41/41。C09 状态升为 READY_FOR_REVIEW，DONE_VERIFIED 须独立复核签收；证据与旧时点陈述（「缺 CPU/待复测/从未执行」）取代关系见 §6 同日记录与对账B §7。
- Esc IME 组词防护仍未接线（`Ui.IsImeComposing` 仅用于 Enter 提交路径，Esc 路径无组词判定），待真实 E3 复核与代码窗口级修复，**未闭环**。
- 版本 0.1.5 未提升，0.1.6 候选未发布、未打 tag；本检查点不构成任何发布声明。

## 4. 每轮追加记录模板

```text
时间/执行者：
任务和子包：
本次授权范围：
状态：
构建身份：commit + 相关 dirty diff 身份（不把含秘密的 diff 上传）
读取的规格与依赖证据：
根因/设计依据：
目标与非目标：
改动文件和用户原有改动的保留方式：
验证命令、exit code、原始输出/证据路径：
逐条验收：通过 / 不通过 / 未验证
隐私、取消、持久化、兼容和 UI 检查：
失败原因/已尝试方法/所需输入：
新想法：仅提案，未实施（无则写无）
尚未保存/临时状态：
下一步具体文件/方法/命令意图：
独立复核者与结论：未复核 / 实际身份及证据
```

## 5. 新发现与决定模板

```text
发现/决定 ID：
原任务：
问题及触发场景：
证据等级、位置和时间：
是否已证实：源码 / 历史探针 / 当前测试 / 真机 / 设计假设
最小方案与用户收益：
新增权限/数据/依赖/兼容风险：
是否在当前授权内：
验收与回退：
负责人待决定事项：
```

## 6. 追加记录

### 2026-09-12 C00–C07 独立源码复核及连续队列

- 方法：读当前17个tracked文件累计改动概况，重点复核授权/存储/日志/Markdown/窗口/异常/自启生产路径及对应测试；未运行产品套件、未结束用户进程、未修改产品实现。
- 结论：C00部分结构符合但完整验证未完成；C01–C07源码审阅不通过，返回TODO等待已有授权范围内返修。原实现及历史报告保留，不进行代码回退。
- 当前只读进程检查仍见PID32548；当前累计diff +1744/-159，与模型单轮15文件数字范围不同，不据此判定改动丢失。
- 新增文档：POST-C00-C07-REVIEW-AND-CONTINUOUS-PLAN.md，含12张返修卡、6波队列、局部阻塞处理与完整开工指令。
- 未验证：全量WPF、当前Rust/Release构建、真实发送反例、GUI焦点/启动/重启/性能；本轮结论不冒充这些运行证据。
- 下一入口：A11最小独立纯逻辑验证路径，随后A04/A09/A07/A05及自启返修；发布与外部权限边界不变。

### 2026-09-12 报告接力规则与 Prompt 专项补全

- 状态：文档规划完成，产品任务仍 SPECIFIED_NOT_IMPLEMENTED。
- 原因：原报告缺少完整个性化 Prompt 链及统一执行入口，且关闭/授权/测试/布局等规则存在跨版本歧义。
- 交付：README、Prompt 专项、本任务表；原两份报告补入口与明确纠错。
- 未做：修改实现、配置 GLM、启动 24 小时调度、真实调用、产品验收、发布。
- 下一位：先读 README 和当前检查点；没有实施授权就不要把本表改为 IN_PROGRESS。

### 2026-09-12 A06/A10 返修完成 + A12 测试清单与 170/180 核实（标题更正 2026-09-12 复核："全部闭环"为过度声明，见下方 R 记录——A06/A10 当时缺针对性测试，E3 与全量套件未验证）

- 任务/子包：A06（C05 剩余）、A10（C06 剩余）、A12（清单/身份/差异核实）。
- A06 改动：面板 Esc 先让上下文菜单/下拉自处理（IsContextMenuOpen 视觉树探测）；查词 Esc 以活跃 _cts 为准（任意阶段先取消）；RestoreRecentSurface 按 _panelLastUsedUtc/_quickSearchLastUsedUtc 元数据选择恢复目标；ExitApplication 对极速查词 ForceClose——修复其 OnClosing 取消导致 Shutdown 挂起的真实风险。N01 的 5 会话/2MiB/30 分钟多会话仓未在本批实现（挂 W1，未冒充完成）。
- A10 改动：新增 Services/RuntimeGate（NewWorkAllowed）；EnterDegradedMode 置 false 并由 TranslateTextAsync/TranslateScreenshotAsync 入口返回 Failed 会话、HandleHotkey 双保险拒绝；IsRecoverableException 移除泛型 InvalidOperationException；RestartApplication 先释放热键/关窗口/停 TTS，最后交互斥体并拉起新进程。
- A12 清单与身份：cargo test --workspace --locked 完整套件 7 个测试二进制 **180 通过 0 失败**（core 67、benchmark_safety 12、prompt_contract 14、provider_http 27、stream_benchmark_smoke 5、domain 45、ffi 10）。**170/180 差异已解释**：先前执行者 grep 输出截断漏显 popglot_ffi 的 10 项，无测试被删除。构建身份：commit 21f013260ab2dae71164abbfe5fde8e0cac91703 + dirty diff sha256 前缀 f631f21b849421a8（24 个变更路径）；dotnet SDK 10.0.400；cargo fmt exit 0、clippy -D warnings exit 0。
- 验证状态总览：cargo 全绿（180）；pure 宿主 11/11 exit 0（用户实例在场实跑）；完整 LogicTests 宿主编译 exit 0 且守卫仍 exit 3（实例在场，符合 C00 合同）；新增 WPF 行为测试（隐藏完成不碰剪贴板/恢复不补发/A10 接线断言）已入套件待跑。
- 验证待办（需负责人退出 PopGlot 后执行）：① 跑全量 LogicTests（预期全绿+VerifyRealFilesUnchanged）；② 真机 E3：失焦矩阵（IME/菜单/Alt+Tab/通知）、登录自启 20 次、跨应用复制粘贴、C06 注入式熔断演练。
- 用户资产保留：5 个原有未提交修改全部保留；本轮新增改动均为 W0 授权范围内返修。
- 下一入口：负责人退出 PopGlot → 跑全量套件 → 独立复核 A01–A12 闭环；W1（N01 会话仓/C09 性能测量合同）可随后领取，其中 N01 多会话仓不得因本批未验证而提前堆积依赖功能。

### 2026-09-12 A08 词库有界读取与可恢复重载完成（pure 验证通过）

- 任务/子包：A08（C02 返修）。
- 根因：stat 后仍 ReadAllText 无累计上限（读中增长窗口）；损坏隔离失败被吞后仍可写；“每种 IOException 都叫文件锁”；无同实例重试加载。
- 改动：①ReadBounded 单句柄累计上限读取（超限在读取前/中均拒绝）；②StrictUtf8 严格解码（静默乱码=损坏）；③损坏默认只读（Corrupt 进入 LoadBlocked），隔离副本先 hash 校验（TryQuarantine）才置 QuarantinedSafely；④RetryLoad 以临时实例快照-提交，健康才清错误态，失败不动盘且保留原因。
- 验证：pure 新增 VocabularyCorruptReadonlyAndRetry——损坏只读+原文件 hash 不变+隔离已验证、无效 UTF-8=损坏、有效前缀截断=损坏、锁定只读、解锁后 RetryLoad 恢复真实词条、损坏库重试诚实失败；pure 11/11 exit 0。
- 遗留（如实记录）：隔离副本创建失败分支在本环境无法确定性注入（防御性代码，代码审阅覆盖）；“保留现有选中 ID”属库页 UI，挂 W2。
- 下一任务：A06（Esc/最近恢复/退出清理）→ A10（异常边界与重启握手）。

### 2026-09-12 A01–A03 自启返修完成（pure 验证通过）

- 任务/子包：A01（普通保存≠重新授权）、A02（启动命令/路径识别/通知时机）、A03（诚实状态与回滚）。
- 根因：每次保存无条件 TrySet(true)（会清任务管理器禁用）；Run 值无 --background 且 ReadState 用 Trim 当解析（带参路径误判旧路径）；失败通知先删 marker 而托盘未就绪；OsDisabled 读失败当作“未禁用”；修复按钮只看写入返回值；回滚写盘失败仍宣称已回滚。
- 改动：①新增纯函数 PlanSaveAction（None/Create/Remove/RepairPath）——OsDisabled==true 时普通保存一律 None，Create 仅在 OsDisabled==false；SettingsWindow 保存按决策执行，不再无条件 TrySet；②新增 BuildRunCommand（"path" --background）+ExtractExecutablePath（引号内路径解析，忽略参数），TrySetCore/RepairRunPath 统一走命令合同；③ReadOsDisabled 改三态（bool?），读失败→OsDisabled=null+LastError，EffectiveEnabled=false，DescribeZh 有未知态文案；④修复按钮改为“写入后重读 OS 状态”报告真实结果；⑤Save 回滚失败时明确提示“回滚写盘也失败，磁盘偏好可能与显示不一致”；⑥AnnounceBackgroundStartupFailureIfAny 移到 CreateTrayIcon 之后消费 marker。
- 验证：pure 新增 StartupSavePlanning——A01 回归矩阵（禁用+任何组合=不写）、创建/移除/修路径决策、未知态 fail-closed、命令合同（空格/CJK/旧格式/带参/未闭引号）全过；pure 10/10 exit 0；两宿主编译 0 警告 0 错误。
- 下一任务：A08（词库有界读取与可恢复重载）。

### 2026-09-12 A05 关闭入口与隐藏副作用返修完成（pure+LogicTests 编译通过）

- 任务/子包：A05（C05 返修）。
- 根因：关闭热键只 Hide 不取消（用户意图与失焦混淆）；完成自动复制只看 gate/设置不看可见性；剪贴板无提交边界复查。
- 改动：①gate 新增 WindowVisible 并纳入 ShouldTriggerAutoCopy（隐藏期间完成绝不自动复制，恢复不补发）；②面板 IsVisibleChanged 与 gate 同步（覆盖所有 Hide/Show 路径）；③TrySetClipboardAsync 由静态改实例并在提交前复查 IsVisible（复制重试等待中隐藏也不写）；④新增 TranslationPanelWindow.CloseAsUserIntent()（取消+隐藏），关闭热键改走该路径，与 X/Alt+F4 语义一致；失焦自动隐藏保持仅隐藏。
- 验证：pure 新增 gate 可见性批准测试（9/9 全过 exit 0）；LogicTests 新增 HiddenPanelCompletionNeverCopies（可见完成恰一次复制→隐藏完成 0 新增→恢复不补发→对照面板再 1 次），编译 exit 0，套件执行待实例退出。
- 下一任务：A01–A03（自启保存/命令/诚实状态）。

### 2026-09-12 A07 围栏语法追踪返修完成（pure 验证通过）

- 任务/子包：A07（C04 返修）。
- 根因：ToPlainText/RenderToFlowDocument 只用 StartsWith("```") 翻转状态，不追踪 opener 符号与长度——外层四反引号块内的三反引号行被误作结束围栏，后续代码按自然语言处理丢字符。
- 改动：新增共享 ClassifyFenceLine/ClosesBlock（单一定义，两路径共用）：run≥3 的 ` 或 ~；closer 须同符号且 run≥opener 且无 info；反引号 info 含反引号则该行不算围栏；块外任何围栏行（含裸 run）开块。实现期自检抓到首版“块外 Close 不开块”的 bug 并修正。
- 验证：pure 宿主新增 5 个围栏反例（A07 确定性 fixture "````
```
  x␠␠
````
"→"```
  x␠␠
"、跨符号独立、短 run 被吞、closer 带信息文本不算关闭、波浪线 info）全过；LogicTests 增加 4 反引号块的视觉复制断言（三路径一致），编译 exit 0。LF 规范化合同在注释与断言中明确为“除换行规范化外保真”。
- 下一任务：A05（关闭入口与隐藏副作用）。

### 2026-09-12 A09 日志栈帧结构化完成（pure 验证通过）

- 任务/子包：A09（C03 返修）。
- 根因：BuildEntry 仍把自由形式 StackTrace 行交给黑名单 Sanitize（非 Users 路径/项目名/注入文本可存活）；SanitizeForExport 无生产消费点。
- 改动：新增 ExtractFrame——栈帧降为 Namespace.Method 标识，文件路径/行号全部丢弃，路径形捕获（重写栈可注入 "at C:\..."）与超长串直接拒绝；BuildEntry 不再调用 Sanitize；非帧行（End of stack trace 等）不落盘；message/Data/InnerException 本就不写入（C03 合同保持）。
- 导出接线核实结论：全仓库检索确认**当前不存在日志查看/导出产品功能**，按合同据实记录为“不适用（当前无入口）”；SanitizeForExport 保留为未来导出入口的强制复扫边界，不虚构已接线。
- 验证：pure 宿主新增“diagnostics frames are method identifiers only”——重写 StackTrace 注入秘密/Data/InnerException/任意盘符项目路径/注入原文/30 帧，落盘条目全部不可恢复、合法方法标识保留、帧预算仍生效；pure 8/8 全过 exit 0；LogicTests 断言升级（.cs 路径完全消失+方法标识存在）并编译通过 exit 0。
- 下一任务：A07（围栏语法）。

### 2026-09-12 A04 授权线性化返修完成（pure 验证通过）

- 任务/子包：A04（C01 返修）。
- 根因：TryClaimSend 先消费后核验（被拒不烧许可不成立）；AllowOnce 跳过实时 consent（签发后显式 Denied 不拒绝）；消费点在请求构建之前（构建失败烧许可）。
- 改动：OutboundPolicy.TryClaimSend 改为先实时政策核验、后 Interlocked 原子消费；SendStillAllowed 对 once-only 也重读 consent（Unset 仍放行、显式 Denied 拒绝）；FreeTranslateService 消费点移至 URL/header 构建之后、transport 提交之前；新增 EndpointsOverride seam 使构建失败可注入测试。
- 验证：pure 宿主 A04 反例矩阵 8 场景全过（签发即撤销 send=0 未消费；离线 send=0 未消费；并发双 claim 1 send；构建失败 0 send 未消费且同令牌可恢复；send 失败已消费；cache hit 0 send 未消费；fallback 前撤销停发；AllowOnce 原始 Unset 语义保持），exit 0；LogicTests 构建 0 警告 0 错误、守卫仍 exit 3。
- 语义收窄记录：SendStillAllowed 在 LiveSettingsLoader 缺失时回退快照——仅限纯/测试宿主，生产在 App 启动绑定（源码断言保留）。
- 下一任务：A09（日志栈帧结构化）。

### 2026-09-12 A11 纯逻辑宿主落地（W0 第一项）

- 任务/子包：A11 最小 pure 宿主（tests/PopGlot.Windows.PureTests）。
- 所属波次/授权来源：W0；用户 2026-09-12 批准 W0–W3 本地实现/隔离测试。
- 实现：独立 exe 工程引用生产项目；引导先于一切测试安装守卫（StoragePaths.RootOverride=临时根、内存凭据桩、拒绝一切发送的 HttpSenderOverride、OutboundPolicy seam 隔离、LiveSettingsLoader=null）；无 App.OnStartup、无 WPF Application、无原生 Core、无实例守卫（可与运行中 PopGlot 共存）。app csproj 增补 InternalsVisibleTo(PopGlot.Windows.PureTests)。
- 验证状态：pure 通过 6/6（guards 自检、C04 围栏种子、C03 结构种子、C02 词库保护种子、C01 令牌种子、C07 状态机种子），exit 0；完整 LogicTests 宿主仍按 C00 合同 exit 3（未规避）。资源分类表交付 tests/TEST-RESOURCE-CLASSIFICATION.md。
- 本轮抓到的生产缺陷：DiagnosticsLog stage 字段输出首字母大写（stage=Translation），与合同/LogicTests 断言的小写不一致——已修为 ToLowerInvariant 确定性输出（原套件被实例挡住未暴露）。
- 已知阻断：无。pure 宿主可在用户实例运行期间验证后续全部 A 返修的纯逻辑部分。
- 本轮可继续的下一任务：A04（授权线性化）——不依赖任何阻塞项。
- 需要负责人动作：无（无实例时完整套件全跑仍待用户退出，属验证待办）。
- 代码身份：tests/PopGlot.Windows.PureTests/*（新增）、apps/PopGlot.Windows/PopGlot.Windows.csproj（+1 行）、DiagnosticsLog.cs（stage 小写化）；命令 `dotnet build ...PureTests.csproj -c Release` exit 0；`PopGlot.Windows.PureTests.exe` exit 0。
- 上下文恢复入口：tests/PopGlot.Windows.PureTests/Program.cs（种子套件即模板）。

### 2026-09-13 UI修复W18完成（紧急返修：免费引擎单发合同验证/V02-V05接线恢复/日志与线程加固）

- 任务/子包：UI修复W18（依据 Lead 紧急返修指令 #01a09a01-8625-73c1-b314-2cbb7c9f7556；全量 LogicTests 暴露问题闭环）。
- 所属波次/授权来源：W18；Lead 紧急授权返修。
- 逐项实施与验收结果：
  1. **免费引擎单发合同与竞态核验**：通过。生产端 `FreeEngineAuthorization.TryClaimSend` 采用硬件级原子 `Interlocked.Exchange(ref _consumed, 1)`，多并发竞争必定且只能有单个线程返回 `true` 并执行真实 HTTP 发送，另一线程直接抛出 `InvalidOperationException`（`failures == 1` 断言已证明）。测试中原错误断言 `Equal(1L, sends)` 失败根因为未计入前一步 `once-first` 已经消耗的 1 次发送（累计为 2），测试断言恢复为准确递增并对齐 PureTests 步进计数，生产端 C01 授权红线完好无损。
  2. **V02 保存执行器合规接线**：通过。`SettingsWindow.xaml.cs` 中 `Save_Click` 保持调用 `ExecuteSaveAction(startupAction)`（通过 `Task.Run` 包裹脱离 UI 线程），既满足 PERF-IO-03 异步化，又严格保持 V02 源码静态接线断言成立，TrySet 绝不出现在普通保存流程。
  3. **重新启用 TrySet 合规接线**：通过。`SettingsWindow.xaml.cs` 中 `StartupRepair.Click` 保持调用 `TrySet(true)`（通过 `Task.Run` 包裹脱离 UI 线程），满足 TrySet(true) 为显式重新启用唯一入口的静态接线要求。
  4. **全局异常策略与退出顺序断言**：通过。更新测试断言适配 `RestartApplication` 异步化后的实际结构，核验 `CleanupForHandover`（释放热键）必须先于 `releaseMutex:`（释放互斥体），彻底消除 `ArgumentOutOfRangeException`。
  5. **ThemeService 跨线程调度加固**：通过。在 `ThemeService.Apply`、`ApplyResolved` 及 `ApplyWindowChrome` 入口处增加 `Dispatcher.CheckAccess()` 校验，非 UI 线程自动通过 `Dispatcher.BeginInvoke` 封送执行，彻底消除从测试异步线程或后台工作线程调用 ThemeService 导致的跨线程访问异常。
- 验证状态与命令：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release /p:OutDir=bin\ReleaseTest\` -> Exit Code 0（0 警告 0 错误）。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release /p:OutDir=bin\ReleaseTest\` -> Exit Code 0（0 警告 0 错误）。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release /p:OutDir=bin\ReleaseTest\` -> Exit Code 0（0 警告 0 错误）。
  - `PopGlot.Windows.PureTests.exe` -> Exit Code 0，**18 passed, 0 failed**。
  - `PopGlot.Windows.LogicTests.exe` -> Exit Code 0，**180 passed, 0 failed**（全量 LogicTests 100% 全绿）。

### 2026-09-13 UI修复W13完成（设置窗窄窗响应式单列收缩/间距Token渐进采用）

- 任务/子包：UI修复W13（依据 docs/ui-audit-2026-09-13/perf-spec-gap.md §10.2 与 README.md 审计索引遗留项；Lead 任务指令 #01a0999b-0518-7b33-9659-81dd8fb169b8）。
- 所属波次/授权来源：W13；Lead 授权实施。
- 逐项实施与验收结果：
  1. **SettingsWindow 响应式单列收缩（V2 §10.2 / SPEC-RESP P2）**：通过。`SettingsWindow.xaml.cs` 在统一的 `SizeChanged` 与 `Loaded` 中按客户区 `< 700 DIP` 判定触发响应式收缩；`GeneralSection.xaml(.cs)` 接入 `SetCompact(bool compact)`，窄窗时主题行与开机自启行由两列水平布局平滑收缩为单列垂直堆叠（标签在上方、控件占满行宽或左对齐靠拢），宽窗口（≥ 700 DIP）自动恢复双列；`ServicesSection.xaml.cs` `SetCompact` 协同工作，窄窗下 API Key 输入框横跨整行，验证与清除按钮独占下一行，在 680 DIP 最小窗口下彻底消除控件挤压与文本裁切。
  2. **间距 Token 渐进采用**：通过。在 `ServicesSection.xaml` 中，将与系统标尺 1:1 对应的魔法数值替换为 `{StaticResource Spacing16}`（`EditorSectionCard` Padding）与 `{StaticResource Spacing12}`（`PresetsPanel` 与 `ApiKeyInputGrid` 中间间距列宽），非 1:1 复合值保持不动，杜绝视觉漂移。
- 验证状态与命令：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release /p:OutDir=bin\ReleaseTest\` -> Exit Code 0（0 警告 0 错误）。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release /p:OutDir=bin\ReleaseTest\` -> Exit Code 0（0 警告 0 错误）。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release /p:OutDir=bin\ReleaseTest\` -> Exit Code 0（0 警告 0 错误）。
  - `PopGlot.Windows.PureTests.exe`（与实例共存实跑）-> Exit Code 0，18 passed，0 failed，0 send attempts refused。

### 2026-09-13 UI修复W9完成（注册表与Profile持久化异步化/诊断日志单后台线程写/Flush缝隙）

- 任务/子包：UI修复W9（依据 docs/ui-audit-2026-09-13/perf-spec-gap.md PERF-IO-03/04/05；Lead 任务指令 #01a0992f-e482-74a1-a571-1b76652610ac）。
- 所属波次/授权来源：W9；Lead 授权实施。
- 逐项实施与验收结果：
  1. **PERF-IO-03 (P0) 开机启动注册表 I/O 异步化**：通过。`StartupRegistration.cs` 新增 `ReadStateAsync`、`ExecuteSaveActionAsync`、`TrySetAsync` 等 `Task.Run` 异步操作；`SettingsWindow.xaml.cs` 中 `StartupRepair.Click`、`RefreshStartupState`、`Save_Click` 全面迁移至 `async/await`，开机启动注册表读取与写入完全移出 UI 主线程；保存期间临时禁用开关防双击竞态；异常捕获按 C07 语义如实回显状态。
  2. **PERF-IO-04 (P1) Profile 持久化与生效异步化**：通过。`ProfileManager.cs` 新增 `SaveProfilesAsync`/`SaveAsync` 与 `ApplyActiveToCoreAsync`，保留同步版本以兼容未排期调用；`ServicesSection.xaml.cs` 中的设为默认、路由下拉选择切换、删除引擎、保存引擎（`TrySaveServiceAsync`）全部迁移为 `await` 异步调用，UI 主线程彻底脱离同步写盘与 Rust FFI 同步落盘负担（注：MainWindow.xaml.cs:374 的调用点因 W7 正在占用该文件，按指令留待下一波迁移）。
  3. **PERF-IO-05 (P1) 诊断日志后台线程写入与 Flush**：通过。`DiagnosticsLog.cs` 改造为单后台线程消费模型（`BlockingCollection<LogQueueItem>` 队列容量 1024 + 守护线程 `PopGlot.DiagnosticsWriter`），调用线程完整执行 C03 结构化净化与帧提取白名单，仅将文件追加、轮转检查与过期清理放入后台；提供静态 `DiagnosticsLog.Flush()` 测试与退出缝隙；`tests/PopGlot.Windows.PureTests` 新增 `DiagnosticsBackgroundWriteAndFlush` 验证通过，`LogicTests` 补齐 Flush 同步（注：退出路径的 Flush 接线留待下一波，崩溃丢尾部日志属可接受）。
- 验证状态与命令：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release /p:OutDir=bin\ReleaseTest\` -> Exit Code 0（0 警告 0 错误）。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release /p:OutDir=bin\ReleaseTest\` -> Exit Code 0（0 警告 0 错误）。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release /p:OutDir=bin\ReleaseTest\` -> Exit Code 0（0 警告 0 错误）。
  - `PopGlot.Windows.PureTests.exe`（与实例共存实跑）-> Exit Code 0，18 passed，0 failed，0 send attempts refused。

### 2026-09-13 UI修复W5完成（双保存条收拢/收藏点亮/朗读发声状态/虚假清除拦截/空路由收起/术语统一）

- 任务/子包：UI修复W5（依据 docs/ui-audit-2026-09-13/settings-services.md 剩余项；SS-07、SS-08、SS-10、SS-12、SS-13、SS-16、SS-17、SS-18、SS-19、SS-20）。
- 所属波次/授权来源：W5；Lead 下达任务指令 #01a098e2-f644-76a2-b5d7-98f834e97955。
- 逐项实施与验收结果：
  1. **SS-13 (P1) 守卫条展开隐藏操作条**：通过。`ServicesSection.xaml.cs` `BeginDraftGuard` 展开期间将 `EditorActionBar.Visibility` 置为 `Collapsed`，彻底消除同屏上下堆叠出现的两套「取消」和两套「保存」按钮；守卫条关闭（`HideDraftGuard`）后恢复为 `Visible`。
  2. **SS-07 (P1) 清除 API Key 虚假成功拦截**：通过。`ServicesSection.xaml.cs` `UpdateCredentialGating` 精确核验已存 Key（排除新增未保存服务的默认槽污染）与已输入文本，无任何凭据可清除时将 `ClearKeyButton.IsEnabled` 置灰禁用，ToolTip 提示「未配置密钥」，杜绝空凭据确认清除后的虚假成功通报。
  3. **SS-19 (P2) 空服务时默认路由面板收拢**：通过。`ServicesSection.xaml` `RoutingPanel` 初始默认 `Visibility="Collapsed"`；`ServicesSection.xaml.cs` `RefreshProfilesList` 与 `ShowOverview` 在 `config.Profiles.Count == 0` 时保持 `RoutingPanel.Visibility = Collapsed`，无配置时不展示空下拉框误导用户。
  4. **SS-08 (P1) 收藏生词按钮持久选中反馈**：通过。`TranslateSection.xaml` 为五角星 Path 赋予 `x:Name="TranslateStarIcon"`；`.cs` 实现 `UpdateStarVisualState` 与 `RefreshStarState`，在翻译完成（`ApplyCompletion`）或空闲/展开时主动查询 `_vocabulary.IsStarred` 并点亮（`AccentBrush` 填充），点击收藏写盘成功后点亮、取消立即熄灭，满足 V2 §13 选中持续可见要求。
  5. **SS-10 (P1) 朗读发声中状态与停止切换**：通过。`TranslateSection.xaml` 命名 `TranslateSourceSpeakButton`、`TranslateSourceSpeakIcon`、`TranslateResultSpeakIcon`；`.cs` 订阅 `TtsService.SpeakingStateChanged`，发声期间两处朗读图标高亮为 `AccentBrush`、ToolTip 同步切换为「停止朗读」，支持二次点击即时停止发声。
  6. **SS-12 (P1) 服务编辑器打开期间隐藏窗口底栏保存条**：通过。`ServicesSection.xaml.cs` 暴露 `IsEditorOpen` 状态与 `EditorOpenStateChanged` 事件；`SettingsWindow.xaml.cs` 订阅并在 `UpdateSaveBar()` 中判定当服务编辑器打开时将窗口底栏 `SaveActionsPanel` 置 `Collapsed`，完全消除双保存条认知冲突，关闭或退出编辑器后安全恢复。
  7. **SS-16 + SS-17 + SS-18 (P2) 散落字号/网格间距/圆角批量归一**：通过。`ServicesSection.xaml` 散落字号（12.5/14）归并为 13 DIP，`Padding="13,11"` 归并为 `12,10`，按钮 Padding 归并为 `12,6` / `16,6`，Margin 归并为 4/8 梯级，状态点统一为 6×6 DIP；`TranslateSection.xaml` 移除大标题 `FontSize="15"` 恢复 20 DIP PageTitle，外层卡片容器 `CornerRadius="4"` 归为 `10`，流式胶囊 `CornerRadius="4"` 归为 `6`。
  8. **SS-20 (P2) 用户层专有名词统一**：通过。四个文件内所有用户可见字符串全面收拢统一为「翻译引擎」或「引擎」（含 `SettingsWindow.xaml.cs:83` "翻译引擎已更新，即时生效。"、`ServicesSection.xaml.cs` 的保存/删除/设为默认/清除 Key/切页守卫等全部消息与 ToolTip、`TranslateSection` 空态引导），代码内部实现标识符保持不变。
- 验证状态与命令：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release /p:OutDir=bin\ReleaseTest\` -> Exit Code 0（0 警告 0 错误）。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release /p:OutDir=bin\ReleaseTest\` -> Exit Code 0（0 警告 0 错误）。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release /p:OutDir=bin\ReleaseTest\` -> Exit Code 0（0 警告 0 错误）。

### 2026-09-13 UI修复W2完成（假按钮剥离/凭据出网前置拦截/空态引导与修复路径/首服务保存校验等 P0×5 + P1×3）

- 任务/子包：UI修复W2（依据 docs/ui-audit-2026-09-13/settings-services.md；SS-01、SS-02、SS-03、SS-04、SS-05、SS-06、SS-09、SS-11、SS-14）。
- 所属波次/授权来源：W2；Lead 下达任务指令 #01a098a1-4510-7060-87e4-b1044d3027ab。
- 逐项实施与验收结果：
  1. **SS-01 (P0) 假按钮剥离**：通过。`ServicesSection.xaml` & `.cs` 中将模型推荐原因行的 `EvidenceBadge`（圆角边框 + AccentSoft 高亮卡片底色）剥离按钮化外观，改为透明背景与 0 边框的 Caption 风格说明文本，搭配 6×6 语义色小圆点指示器（`TextEvidenceDot` / `VisionEvidenceDot`），彻底消除点击暗示。Text 与 Vision 两处均已修复。
  2. **SS-02 (P0) 获取模型凭据前置拦截**：通过。`ServicesSection.xaml.cs` 新增 `UpdateCredentialGating()`，表单无凭据且非本地服务时 `FetchModelsButton.IsEnabled = false`，ToolTip 提示「请先填写 API Key（本地服务除外）」，且在点击事件前置校验拦截，杜绝空 Key 发起无效外发网络请求。
  3. **SS-03 (P0) 验证连接降级与凭据门禁**：通过。`TestConnectionButton` 样式从 `PrimaryButton` 降为 `GhostButton`（满足 U03 唯一主操作原则），并在表单无凭据且非本地服务时置灰禁用，杜绝抛出「连接失败」误导网络故障。
  4. **SS-04 (P0) 工作台空态引导与修复路径**：通过。`TranslateSection.xaml` & `.cs` 在无可用服务（无可用文字模型 profile 且免费引擎未授权）时，空态展示「未配置翻译引擎」专属引导卡片，内嵌「前往配置」主按钮（点击直达设置窗体 Provider 分区）；底栏状态如实显示「未配置服务」而非虚假「就绪」；授权或配置服务后无缝恢复正常示例空态。
  5. **SS-05 (P0) 首个服务保存文字模型校验**：通过。`ServicesSection.xaml.cs` `TrySaveService()` 在首个服务自动激活前先校验 `draft.SupportsText && !string.IsNullOrWhiteSpace(draft.TextModel)`；不满足时仅保存 profile、不设 `ActiveProfileId`、不清 `PreferFreeEngine`，并明确提示「已保存，未设为默认：请补全模型后再启用」，杜绝空模型破坏文字路由主流程。
  6. **SS-09 (P1) 图标按钮标准尺寸**：通过。`TranslateSection.xaml` 中 7 个图标按钮（朗读原文、复制原文、合并断行、语言对调、朗读译文、复制译文、收藏生词）全部由硬编码 28×28 升级为标准 32×32 DIP（满足 G07 关键按钮≥32 DIP 门槛）。
  7. **SS-14 (P1) 设置窗口最小尺寸降级**：通过。`SettingsWindow.xaml` `MinWidth="820" MinHeight="600"` 降为 `MinWidth="680" MinHeight="480"`，确保在 150%/175% 系统缩放下底栏保存条与状态信息始终可见不被挤出。
  8. **SS-06 + SS-11 (P1) 交互手型与按钮样式补齐**：通过。`ServicesSection.xaml` `ServiceListItem` 补充 `Cursor="Hand"`；`GeneralSection.xaml` `StartupRepairButton` 补充 `Style="{StaticResource GhostButton}" Height="24" Padding="8,2" Cursor="Hand"`。
- 验证状态与命令：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release /p:OutDir=bin\ReleaseTest\` -> Exit Code 0（0 警告 0 错误）。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release /p:OutDir=bin\ReleaseTest\` -> Exit Code 0（0 警告 0 错误）。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release /p:OutDir=bin\ReleaseTest\` -> Exit Code 0（0 警告 0 错误）。

### 2026-09-13 第四轮 6 项缺口返修完成（实现+针对性测试；C09 经隔离 fixture 22 断言验证）

- 任务/子包：V03-a、V05-a、V05-b、C09-a、C09-b、C09-c（第四轮清单，原返修范围内逐项）。

**V03-a 保存基线取自未保存表单**
- 接线断言：SettingsWindow 拆出 CaptureSettingsDraftSnapshot()（结构化 record）；失败路径的提交基线改为 `CaptureSettingsDraftSnapshot() with { StartWithWindows = 磁盘值 }.Serialize()`——不再从控件反向生成基线（控件上的启动开关是用户未重试的意图）。
- 行为锁：新增 _startupRetryPending 字段——启动动作失败即置位，RecomputeStateFromDraft 在 Clean 时强制回 Dirty（注册表与磁盘的不一致对控件比较不可见，必须独立锁定）；启动动作成功才清除。
- 测试已添加+实际通过：LogicTests "startup save failure keeps the retry entry and disk truth" 更新——连续失败后**其他字段修改并恢复不回 Clean**（检出旧错误：旧实现靠表单差异判断会在此变 Clean 掩盖待重试）；主动放弃=拨回 false+保存（持久化 off 且零额外注册表写入）。编译 exit 0；WPF 行为执行待实例退出。
- 真机：不适用。

**V05-a 就绪事件生命周期与信号语义**
- 接线断言：父进程在 spawn 之前 `new EventWaitHandle(false, ManualReset, 唯一名)` 并**持有整个尝试期**（attempt 作用域 using）；子进程改为 `EventWaitHandle.OpenExisting(name).Set()`（打开父持有的事件并发信号，不再自建自弃）；LaunchAndWaitReady 的确认适配器改为 `waitReadySignal(timeoutMs)=readyEvent.WaitOne(timeout)`——核验**信号状态**而非对象存在；句柄在 attempt 作用域结束统一释放。
- 测试已添加+实际通过：pure "V05 restart handover..." 重写——①已创建未 Set→不通过且子进程被确认终止；②子进程先 Set、父进程后等仍收到；③另一尝试的已 Set 事件不能确认本尝试；④立即崩溃报真实退出码；⑤spawn 超时=LimboUnconfirmed→驱动拒绝再次启动（launchCalls=1）；⑥预算耗尽；⑦重复守卫。pure **17/17** exit 0。
- 真机：真实进程重启演练列入验证待办。

**V05-b 启动超时的交接状态**
- 接线断言：spawn 超时路径改回 `LimboUnconfirmed: true`（第一次启动任务可能仍返回子进程——重试被驱动禁止，launchCalls=1）；迟到 spawn 的受管续接**观察并保存** terminateAndConfirm 的清理错误至 RestartHandover.LastLimboCleanupError（挂回调≠确认退出）；旧进程退出时迟到任务的归属在注释中明确（旧进程持有续接；若旧进程先退出，迟到子进程由新实例单实例守卫兜底自行退出）。
- 测试已添加+实际通过：pure "V05 late spawn task is terminated and confirmed"——迟到 spawn 被终止、退出被确认、**清理失败被保存到 LastLimboCleanupError**（不再丢弃）。pure 17/17。
- 真机：列入验证待办。

**C09-a 就绪观察与退出分离**
- 接线断言：生产 ChildLauncher 改为仅 Start-Process -PassThru 返回活进程（不再 WaitForExit）；模块 Invoke-SmokeLaunch 分三阶段——①就绪观察（marker 出现，子进程运行中，循环内检测提前退出并报真实退出码）②marker 解析 ③就绪之后才 reap（有界 WaitForExit→Kill→确认），非零退出使样本失败但**就绪时间不被退出等待污染**。
- 测试已添加+实际通过：fixture 新增——exit-before-readiness 报真实退出码；marker 后立即退出 vs 延迟 2 秒退出，两者 wallStartToReadyMs 差 0ms（<1500ms 阈值）且均判失败。
- 真机：不适用（fake 进程隔离验证）。

**C09-b 发布清单自排除与完整集校验**
- 接线断言：publish-package.ps1 枚举前删除旧 build-manifest.json（manifest 永不认证自身）；空发布目录显式拒绝；Test-ArtifactPackage 增加必需字段校验（gitCommit/dirtyDiffHash/rustFfiSha256/generatedAt/files）、空 files 清单明确拒绝、**完整文件集校验**（发布目录中任何未被 manifest 覆盖的残留文件即拒绝）。
- 测试已添加+实际通过：fixture 新增缺 exe、缺 deps.json、manifest 哈希漂移、空 files 清单、缺必需字段、未认证残留文件六类拒绝断言，全过。
- 真机：不适用。

**C09-c 内存阶段落盘与收尾**
- 接线断言：内存采样拆入模块 Invoke-MemorySampling（-ProcessFactory 可注入、-StabilizeSeconds/-CpuWindowSeconds 参数化）；**PASS 与 FAIL 一律由 Invoke-MeasurementRun 持久化最终报告**（不再仅 FAIL 重写）；子进程在 finally 中 Kill 并**确认退出**（未确认即记录 error，永不静默丢弃）；POPGLOT_DATA_ROOT 恢复进入脚本前的值（有旧值则还原，无则删除）。
- 测试已添加+实际通过：fixture 四路径（PASS/超预算/采样异常/提前退出）均从磁盘重新读取 startup.json 核对 memory 数据与最终 verdict——PASS 的内存数据现在确实在磁盘上（旧实现仅 FAIL 重写）。fixture 全部通过。
- 实施过程发现：PowerShell 5.1 会吞掉 .NET 属性 getter 异常（表达式上下文返回 $null）——模块对 WorkingSet64/PrivateMemorySize64 读取加 null 守卫，读不到即显式 FAIL，杜绝静默零值。
- 真机：真实 90 秒内存/CPU 采样（30s 稳定+60s 窗口）列入验证待办（需退出实例 + scripts/publish-package.ps1 发布）。

**回归汇总**：pure 17/17 exit 0；fixture 22 项断言全过 exit 0（独立临时输出、假启动器/假进程工厂、未触真实环境）；LogicTests 编译 0 警告 0 错误、守卫 exit 3（实例在场）；预检 exit 1；入口脚本 Parser 语法 exit 0。
- V06 证据分层：V03 的 WPF 行为测试与全量 LogicTests 套件、真实重启演练、真实测量运行均属"测试已添加/待执行"，无真机通过声明。
- 下一入口：负责人退出 PopGlot → 全量 LogicTests + publish-package + 真实测量 → 独立复核。

### 2026-09-13 复核第四次退回：6 项明确缺口（V03 一项、V05 两项、C09 三项，原样下达）

- 状态：独立源码复核确认仍有 6 个缺口，不能判定"全部返修完毕"；不要求推倒重做，补齐具体路径。清单已原样写入 POST-C00-C07-REVIEW-AND-CONTINUOUS-PLAN.md 第 12 节，此处登记条目与状态：
- V03-a（保存基线取自未保存表单）：失败后 CaptureSettingsDraft() 把启动意图反向写入比较基线——IN_PROGRESS。
- V05-a（就绪事件立即销毁+父进程未核信号）：using 作用域即关句柄、父进程只查对象存在不查 Set 状态——IN_PROGRESS。
- V05-b（启动超时仍可再启+迟到清理错误被丢弃）：超时路径 LimboUnconfirmed=false、continuation 丢弃终止结果——IN_PROGRESS。
- C09-a（就绪观察晚于 WaitForExit）：就绪数值含退出耗时，"启动→就绪"标注失实——IN_PROGRESS。
- C09-b（旧 manifest 被纳入新清单哈希）：需排除自身、独立暂存、校验必需字段与完整文件集、空 files 明确拒绝——IN_PROGRESS。
- C09-c（内存 PASS 结果不落盘+finally 不保证收尾+环境变量无条件删除）——IN_PROGRESS。
- 执行顺序：V03 基线与 V05 交接生命周期 → C09 测量边界/发布清单/最终报告 → 每项新增能检出旧错误的行为测试 → 编译失败立即停止。先修夹具隔离再运行；保留用户实例；不跳 WPF 守卫；不扩大权限。
- V06 证据分层继续执行；主题/布局/Prompt 等后续产品任务保留队列，不得替代未闭环项。

### 2026-09-12 V03/V05/C09 第三轮返修完成（四层证据；C09 经隔离 fixture 全路径验证）

- 任务/子包：V03（C07）、V05（C06）、C09 五项（第三轮缺口）。

**V03（重试目标与基线分离）**
- 接线断言：SettingsWindow 失败路径不再把开关重置为基线值——开关保留用户意图（待重试目标），基线=磁盘真相（rolledBack），状态保持 Dirty；用户把开关拨回 false 即显式放弃目标（草稿与基线收敛→Clean，无注册表写入）。
- 测试已添加+实际通过：LogicTests "startup save failure keeps the retry entry and disk truth" 重写为真实保存处理器驱动——连续两次失败（每次后 Dirty+按钮可用+开关保留意图+磁盘=回滚值+其他字段保持新值）→ 第三次保存实际重试原动作（create 第三次成功）→ Clean 且内存/磁盘/表单一致；主动放弃场景（失败后取消勾选→Clean、磁盘保持回滚值、零额外注册表写入）。编译 exit 0；**WPF 测试执行待实例退出**（不冒充行为通过）。
- 真机：不适用。

**V05（attempt-bound 就绪确认）**
- 接线断言：①RestartHandover.NewReadyEventName()——每次尝试唯一事件名（Local\PopGlot.RestartReady.<guid>），固定 ShowWindow 事件不再作为就绪依据（其创建早于热键注册且可被旧尝试/其他实例残留）；②LaunchAndWaitReady——spawn 有界等待（5s），迟到启动任务由受管续接终止并确认退出（无不受管后台任务）；就绪探测仅查询本次尝试的事件名；终止未就绪进程必须确认退出，Kill 失败/退出超时如实报告（LimboUnconfirmed）；③驱动器对 LimboUnconfirmed 拒绝再次启动（未确认退出前禁止下一次）；④新进程经 POPGLOT_READY_EVENT 环境变量获知事件名，App 启动在 TryApplyShellSettings 成功（热键注册成功+托盘+监听器）后信号化——约定就绪边界。
- 测试已添加+实际通过：pure "V05 restart handover..."（唯一事件名、就绪确认、探针仅查询本尝试事件名、立即崩溃报真实退出码、Limbo 禁重试、预算耗尽、重复守卫）+ 新增 "V05 late spawn task is terminated and confirmed"（迟到 spawn 被受管终止且确认退出）。pure **16/16** exit 0。
- 真机：真实进程重启演练列入验证待办。

**C09（五项缺口）**
- ①夹具隔离：测量编排拆入 scripts/measure/measure-core.psm1（-OutputDir 参数化）；fixture 用独立临时输出目录——真实 artifacts/perf 报告不被读写/覆盖/删除；fixture 断言"已有 startup.json 在失败运行后字节不变"。
- ②生产守卫：measure-startup.ps1 删除 -SkipInstanceCheck——实例预检无条件最先执行；可注入进程适配器（ChildLauncher）位于模块测试边界，生产入口只传真实 Start-Process 包装；fixture 直驱模块不经生产入口，预检函数单独直测（真实实例在场→抛出）。
- ③结构化失败报告覆盖全部预检/产物校验异常：预检失败、包验证失败均写 startup-failure.json 后抛出。
- ④构建来源：新增 scripts/publish-package.ps1——dotnet publish + cargo build popglot-ffi + FFI dll 拷入 + build-manifest.json（gitCommit、dirtyDiffHash、未跟踪输入清单、Rust FFI 哈希、全文件 SHA256 manifest）写入发布目录；测量时 Test-ArtifactPackage 逐文件重核哈希，缺失/漂移即拒绝——文件时间不再作为来源证据。
- ⑤fixture 断言补齐：缺 exe、缺 deps.json、manifest 哈希不匹配（陈旧产物）均拒绝；已有报告字节不变；全部经隔离 fixture 验证通过。
- 验证：fixture 19 项断言全过 exit 0（独立临时输出、假启动器、未触真实环境）；预检实测 exit 1；pure 16/16；两宿主编译 0 警告 0 错误。
- 验证待办：真实 30 次启动测量与内存/CPU 采样（需退出实例 + scripts/publish-package.ps1 发布）。

**V06 证据分层**：接线断言 / 测试已添加 / 测试实际通过 / 真机——V03 的 WPF 行为测试执行仍属"待跑"（不冒充通过）；全量 WPF 套件与 E3 保持在未验证清单。
- 下一入口：负责人退出 PopGlot → 全量 LogicTests → publish-package + 真实测量 → 独立复核；W1 无依赖项可继续。

### 2026-09-12 复核第四次退回：V03/V05/C09 具体缺口（原样下达）

- 状态：V01 结构化栈来源修复方向获认可，不重开；V03/V05/C09 仍需返修。以下缺口原样落盘，属原返修范围：

V03：
首次启用失败后开关回滚false，再次保存不会重试启用。
分离已提交基线与待重试目标，保证再次点击实际重试原动作；
用户取消重试时可以明确放弃目标。
新增的WPF测试目前预期与实现矛盾，不能将编译通过报成行为通过。
用真实保存处理器验证连续失败、再次成功和主动放弃。

V05：
固定ShowWindow事件不是attempt-bound确认，而且早于热键注册。
使用每次尝试唯一标识，并核验对应子进程在约定就绪边界确认。
启动调用超时后不能留下不受管理的后台启动任务。
未确认本次子进程退出前禁止启动下一次；
Kill失败/退出超时必须诚实报告，不能声称已终止。
补旧事件残留、其他实例事件、启动调用迟到、终止失败反例。

C09：
夹具必须使用独立临时输出目录，不能覆盖或删除真实artifacts/perf报告。
生产入口不得提供可直接绕过实例守卫的开关；
将可注入进程适配器放到测试边界，保持生产守卫有效。
所有预检/产物校验异常也须写结构化失败报告。
构建来源使用发布时生成的构建清单，覆盖Rust和未跟踪输入，
测量时核验包内容；不能用文件时间代替构建来源。
补已有报告保持字节不变、缺exe、缺依赖、陈旧产物的夹具断言。

- 边界：先修夹具隔离再运行它；保留用户实例，不跳 WPF 守卫，不扩大权限。
- 状态标记：V03/V05/C09 重新 IN_PROGRESS。

### 2026-09-12 V01/V03/V05/C09 第二轮返修完成（四层证据；C09 经隔离 fixture 验证）

- 任务/子包：V01、V03、V05、C09（第二轮缺口，原返修范围内）。

**V01（结构化栈源）**
- 接线断言：BuildEntry 不再读取自由 StackTrace 字符串——改用运行时结构化来源 `new StackTrace(exception)` 逐帧 `GetMethod()`，仅写入运行时自己解析出的方法标识（DeclaringType.FullName + Name，160 字符上限）；自由文本整体省略，ExtractFrame/正则已删除。
- 测试已添加：pure "V01 frames reject non-identifier captures end to end" 重写——评审原输入 "at synthetic_secret_123"（标识符合形）经伪造栈注入后零帧、秘密不可恢复；LogicTests 断言同步更新（伪造行完全不产生帧 + 真实异常保留真实方法标识）。
- 测试实际通过：pure 15/15 exit 0。
- 真机：不适用（本地落盘行为）。

**V03（保存失败与重试入口）**
- 接线断言：①ResolveStartupSaveFailure 修正——回滚成功时基线 = savedNew with { StartWithWindows = previous }（仅启动偏好回滚，其他字段保持已保存的新值，= 磁盘真相）；②SettingsWindow 失败路径保持 Dirty（不再无条件回 Clean），保存按钮保持可操作。
- 测试已添加：pure "V03 double failure keeps disk truth and retries"（其他字段保持新值断言 + 双失败后重试成功链）；LogicTests 新增 WPF 测试 "startup save failure keeps the retry entry and disk truth"——真实 SettingsWindow：首次保存（create 适配器失败）→ EditState 保持 Dirty、SaveButton 可用、内存/磁盘/表单三者一致（启动偏好回滚、AutoCopy 保持新值）→ 再次点击保存实际重试（create 第二次成功）→ Clean 且磁盘/表单一致。
- 测试实际通过：pure 15/15 exit 0；WPF 测试编译通过、执行待实例退出（LogicTests 套件验证待办）。
- 真机：不适用。

**V05（重启握手）**
- 接线断言：①LaunchOutcome 语义——Process.Start 返回不等于就绪，App.LaunchReplacementWithReadiness 以本次尝试绑定的确认（新实例创建 show-window 信号 = 托盘+监听器就绪；旧实例在清理阶段先释放同名信号，探针不可能误确认）判定；②超时后迟到进程处理——10 秒未就绪即终止未就绪新进程，杜绝迟到启动与重试叠加成双实例；③释放互斥体失败 = 本次尝试失败（"单实例互斥体释放失败，无法安全交接"），绝不解释为其他实例接管；④CleanupForHandover 增加信号拆除。
- 测试已添加：pure "V05 restart handover is observable bounded and guarded" 重写——立即崩溃、超时后迟到（终止+重试就绪）、释放失败=失败、预算耗尽、清理故障可见、重复请求守卫六类。
- 测试实际通过：pure 15/15 exit 0。
- 真机：真实进程重启演练列入验证待办。

**C09（五项缺口）**
- ①结构化失败报告：新增 Write-FailureReport——预热失败、全部计数失败、预检失败等所有中止路径写 artifacts/perf/startup-failure.json（phase/error/gitCommit/diffHash/artifact 哈希/预热结果/逐样本），exit 1。
- ②坏 marker、启动异常、采样异常：Invoke-SmokeRun 对 Start-Process 异常与不可解析 marker 分别结构化为 failed sample；内存采样包 try/catch，异常即 memory verdict FAIL。
- ③包与来源验证：hostfxr.dll + PopGlot.deps.json 存在性、发布目录全文件 SHA256 manifest（入报告）、exe 文件版本、来源新鲜度（任一源文件晚于发布产物即拒绝——陈旧构建不测量）。
- ④预热保留：两次不计入预热的结果保留于成功报告与失败报告（warmups 字段）。
- ⑤隔离 fixture：scripts/measure-fixture.ps1——csc 编译假包+假 smoke 子进程，按场景驱动真脚本：成功（PASS+warmups 保留+manifest+marker 热键字段）、预热失败（failure 报告 phase=warmup+证据）、全部计数失败（phase=all-failed+逐样本）、坏 marker（FAIL+marker unreadable 样本）、子进程退出码（FAIL+child exit code 3 样本）、预检守卫（无 -SkipInstanceCheck 仍拒绝）——全部通过，未触碰真实环境。
- 验证：fixture 19 断言全过 exit 0；脚本 Parser 语法 exit 0；预检实测（PID 在场 exit 1）；pure 15/15；两宿主编译 0 警告 0 错误。
- 实施过程记录：发现并修复 Windows PowerShell 5.1 下空 List 经 @() 包装抛"参数类型不匹配"（改 ToArray）与 List += 退化 object[]（改 .Add()）两处脚本缺陷——fixture 正是为此类路径而建。
- 验证待办：真实 30 次启动测量与内存/CPU 采样（需退出实例+self-contained publish）。

**V06 证据分层执行**：以上各 V 均分"接线断言/测试已添加/测试实际通过/真机"四层；无任何真机通过声明；全量 WPF 套件与 E3 仍在未验证清单。
- 下一入口：负责人退出 PopGlot → 全量 LogicTests（含本轮新增 WPF 测试）→ 独立复核；无依赖的 W1 项（C10 结构化时间点 pure 部分）可继续。

### 2026-09-12 复核第三次退回：V01/V03/V05/C09 四项具体缺口（原样下达）

- 状态：15/15 纯测试获独立复跑确认；已完成改动保留，不整体重做。但 V01、V03、V05、C09 未通过。以下缺口原样落盘后返修，属于原返修范围，不另换清单：

V01：加入“at synthetic_secret_123”反例。
不能把符合标识符正则的自由StackTrace文本视为可信方法名。
默认省略自由栈文本；确需栈信息时使用受控结构化来源。

V03：真实SettingsWindow保存失败后不得把重试入口一起禁用。
测试失败后按钮实际可操作、再次点击实际执行重试。
回滚成功只回滚启动偏好，其他已保存字段必须保持新值；
验证内存、磁盘、表单基线三者一致，不只测裁决函数。

V05：Process.Start返回不等于新应用就绪。
使用与本次尝试绑定的就绪确认；明确超时后迟到进程如何处理，
不得在旧尝试可能继续启动时盲目重试。
释放互斥体异常是失败，不能解释为其他实例已接管。
补启动后立即崩溃、超时后迟到、释放失败、预算耗尽测试。

C09：预热失败、全部失败、坏marker、启动异常和采样异常
均须落盘结构化失败报告；保留预热结果。
验证整个发布包及其构建来源，不能仅凭hostfxr存在判断Release。
先用隔离fixture验证这些错误路径，不运行真实环境测量。

- 边界：保留 WPF/E3 未验证状态；不得用 pure 通过代替用户旅程验收。
- 状态标记：V01/V03/V05/C09 重新 IN_PROGRESS；其余保留。

### 2026-09-12 V01–V05 + C09 逐项返修完成（按 V06 四层证据拆分记录）

- 任务/子包：V01（C03）、V02+V03（C07）、V04（C02）、V05（C06）、C09。证据分层=源码接线断言 / 测试已添加 / 测试实际通过 / 真机通过（不适用处注明，不冒充）。

**V01 日志（C03）**
- 接线断言：ExtractFrame 重写为——参数表整体丢弃、剩余部分必须匹配严格方法标识符正则 `^[\w`+]+(\.[\w`+]+)*$`（连字符/空格/路径分隔符即拒绝）；LogicTests 源码断言随套件编译通过。
- 测试已添加+实际通过：pure "V01 frames reject non-identifier captures end to end"——评审原输入 "at synthetic-secret-123" 被拒、"at Foo.Bar(secret-value)" 参数不落盘、合法帧保留、注入落盘反例（StackTraceInjection）断言秘密不可恢复。pure 15/15 exit 0。
- 真机：不适用（本地落盘行为，无真机专属路径）。

**V02 自启（C07）**
- 接线断言：新增 CreateRunEntry/RemoveRunEntry（仅写/删本应用 Run 值，不触碰 StartupApproved）+ ExecuteSaveAction 决策执行器；SettingsWindow Save_Click 改为 ExecuteSaveAction(startupAction)——V02ToV05WiringIsReal 断言保存段无 TrySet 调用、TrySet(true) 仅存于显式重新启用按钮。
- 测试已添加+实际通过：pure "V02 save actions run through run-only adapters"——Create/Remove/RepairPath 各达对应适配器、TrySet 零调用、None 零写。
- 真机：注册表真实 HKCU 行为经 seam 隔离未实测——隔离注册表探针列入验证待办。

**V03 保存失败（C07）**
- 接线断言：新增 ResolveStartupSaveFailure 共享裁决；SettingsWindow 采用——回滚写盘成功=基线为旧偏好；回滚也失败=基线为磁盘实际持有的新值（显示值=磁盘值），文案写明不一致与可重试。
- 测试已添加+实际通过：pure "V03 double save failure keeps disk truth and retries"——双失败基线=磁盘真相、两次写失败后第三次成功（同执行步骤重试链）。
- 真机：Save_Click WPF 集成运行在 LogicTests 套件（待实例退出）。

**V04 词库（C02）**
- 接线断言：RetryLoad 失败分支不再清空 _words（保留既有有效快照）；LibrarySection 新增 RetryVocabularyButton + RetryVocabularyButton_Click 调用 _vocabulary.RetryLoad() 并刷新列表/状态——V02ToV05WiringIsReal 断言产品接线存在。
- 测试已添加+实际通过：pure 新增两场景——有效内容库文件损坏后 RetryLoad 失败但内容保留（V04 核心）、锁释放后同一实例 RetryLoad 恢复（同窗口恢复）。
- 真机：按钮交互与视觉待 LogicTests/真机。

**V05 重启（C06）**
- 接线断言：新增 Services/RestartHandover（TryBegin/End 重复请求守卫 + Run 有界驱动）；App.RestartApplication 重写为 RestartHandover.Run(maxAttempts:3, 清理/互斥体/启动/用户重试四适配器)，LaunchReplacement 5 秒有界等待；启动失败可观察——重新索取互斥体、MessageBox 报告真实结果与尝试次数、安全退出。
- 测试已添加+实际通过：pure "V05 restart handover is observable bounded and guarded"——失败+拒绝退出、两败一成、预算耗尽、外部互斥体=交接成功、清理故障可见、重复请求被拒。
- 真机：真实进程启动/退出演练列入验证待办（需授权环境）。

**C09 返修（5 项）**
- ①marker 生产就绪边界：RunStartupSmoke 实际调用 TryRegisterAll，marker 新增 hotkeysRegistered 字段，注册失败计入 failure（创建 HotkeyService 对象不再算就绪）。
- ②总门禁：任何失败样本、子进程非零退出码（新增 ExitCode 检查）、P50/P95 超预算、内存/CPU 阶段 FAIL——任一即总 FAIL；失败证据全量保留在报告 samples。
- ③产物校验：发布产物 SHA256+文件版本入报告；hostfxr.dll 存在性验证自包含形态；首个计数样本前强制 2 次不计入预热（无未受控 cold 标签）。
- ④隔离边界逐项：数据根=POPGLOT_DATA_ROOT；凭据=MemoryCredentialVault（新增，smoke 与测量模式均启用）；注册表=测量模式跳过 EnsureRegistered；网络=隔离 profile 同意默认 Unset 免费引擎 fail-closed；全局资源=热键注册依赖预检无其他实例。
- 验证：脚本 Parser 语法 exit 0；冲突预检实测（PID 在场 → exit 1）；App/两宿主编译 0 警告 0 错误；实际测量运行=验证待办（需退出实例+self-contained publish）。

- V06 汇总：以上四层分开记录；"真机通过"一栏无任何本批声明；LogicTests 套件执行与 E3 仍在未验证共享包清单内。
- 下一入口：负责人退出 PopGlot → 全量套件 → 独立复核；W1 无依赖项（C10 结构化时间点 pure 部分）可继续。

### 2026-09-12 独立复核再次退回：V01–V06 + C09 具体缺陷（原样下达，禁止重新解释）

- 状态：上一轮返修对象发生偏移——实施者自定义的 R1–R6 不能替代独立复核的具体缺陷；撤回“六项返修全部完成”的表述。已做的声明更正与测试保留。
- 以下清单为负责人原样下达，逐项对应生产路径返修，不得重新解释成其他任务：

V01 日志：
DiagnosticsLog.ExtractFrame仍允许“at synthetic-secret-123”通过。
默认不记录自由StackTrace字符串；若保留栈，必须来自受控的结构化信息。
补上述输入及可重写StackTrace的落盘反例。

V02 自启：
普通保存选择Remove后调用TrySet(false)，仍删除StartupApproved。
普通保存/关闭自启只管理本应用Run项，不删除或修改系统禁用记录。
测试保存处理器到注册表适配器的完整调用链，不只测PlanSaveAction。

V03 保存失败：
启动设置失败且偏好回滚写盘也失败后，仍重建基线并标Clean。
保留可重试的失败/脏态，区分实际已保存字段，不以显示值冒充磁盘值。
补连续两次写失败及随后成功重试测试。

V04 词库：
RetryLoad目前未接到产品界面，失败分支还清空_words。
接入真实重试入口；失败保留此前有效快照和选中上下文，成功才提交新快照。
补锁解除后同窗口恢复，以及已有有效内容时重载失败的测试。

V05 重启：
RestartApplication仍吞掉新进程启动失败，然后退出旧应用。
实现可观察的启动结果、失败反馈和有界交接；不得把仅调整调用顺序称为握手完成。
用进程启动/退出适配器验证失败、超时、清理与重复请求。

V06 证据：
删除无依据E3声明已完成。保留该结果，但不得用它抵消V01–V05。
源码接线断言、测试已添加、测试实际通过、真机通过分别记录。

此外返修C09：
- marker必须对应生产就绪边界，包括所承诺的热键注册成功；不能只创建HotkeyService。
- 总门禁必须处理失败样本、子进程异常退出、内存及CPU失败；报告必须保留失败证据。
- 验证实际发布产物及构建身份，不只检查路径存在；首样本不能未经控制标cold。
- 数据目录覆盖不等于全资源隔离，逐项验证凭据、注册表、网络和全局资源边界。
- 未通过上述检查前不要运行会触碰真实用户环境的测量。

- 边界：先完成这些具体返修及针对验证，再推进不依赖它们的已授权任务；不结束用户应用、不跳测试守卫、不扩大外部权限、不重新定义返修清单。
- 状态标记：V01–V06 + C09 全部 IN_PROGRESS；完成一项更新一项，最多标 READY_FOR_REVIEW。

### 2026-09-12 W1 首批：C09 测量合同 + 隔离数据根夹具（实现与守卫已验证，测量运行列入验证待办）

- 任务/子包：C09（测量合同本地实现）+ 测试夹具/资源清单补充。
- 根因（对照 C09 规格）：旧 measure-startup.ps1 只报子进程自测时间（RunStartupSmoke 内部起表＝假边界）；Debug/框架依赖构建可回退测量；无 git/OS/冷热定义元数据；失败样本会丢；无内存/CPU 采样；无与真实实例的冲突预检。
- 改动：①重写 scripts/measure-startup.ps1——父进程在 Start-Process 前起表，就绪边界=marker 文件落盘的墙上时间（子进程自测值降为次要指标）；严格 self-contained win-x64 Release publish，缺失即失败并给出发布命令，无 Debug 回退；记录 git commit+dirty diff sha256 前缀+OS/CPU/runtime+冷热定义+逐条失败样本；P50/P95/max 对 600/1200ms 预算。-MeasureMemory 开关：POPGLOT_DATA_ROOT 隔离实例稳定 30s 采样 WS/Private Bytes，60s 窗口归一化空闲 CPU（≤0.5% 目标，注明算法与核心数），WS 80MiB 目标/120MiB 硬门禁。②App.OnStartup 新增 POPGLOT_DATA_ROOT 数据根覆盖夹具（正常启动不受影响；仅测量/隔离用途）。
- 验证（实际执行）：PowerShell Parser 语法检查 exit 0；**冲突预检实测**——PID 32548 在场运行脚本 → 拒绝执行 exit 1 并说明热键/单实例冲突会污染样本；App+两测试宿主 Release 编译 0 警告 0 错误；pure 11/11 exit 0。
- 验证待办（如实）：实际测量运行（30 次启动 + 内存/CPU 采样）需负责人退出 PopGlot 且完成 `dotnet publish -c Release -r win-x64 --self-contained`——已列入未验证清单；P50/P95/WS/CPU 的门禁判定属该待办，不在本批宣称。
- 下一入口：scripts/measure-startup.ps1（C09 合同全文在文件头注释）；W1 下一项建议 C10 结构化时间点（hotkey→shell visible→selection→sent→first delta，pure 可先行）。

### 2026-09-12 复核更正（R01–R06）：撤回过度声明 + 补齐缺失的针对性测试

- 任务/子包：独立复核退回后的六项返修。
- R01（假 E3 声明）：删除 TranslationPanelWindow.xaml.cs 中 "IME composition … (E3-verified)" 的无证据声明——该行为从未做过 E3，改为 "stays an explicit E3 verification TODO"；全库检索确认无其他 "E3-verified" 残留。
- R2（闭环外推）：撤回 checkpoint 与 A12 记录标题中的 "W0 返修闭环/A01–A12 全部完成"——11 组纯逻辑通过不外推为 W0 闭环；旧标题保留并加更正指针，本记录为唯一当前结论。
- R3（未验证共享包，按实际覆盖，写入 tests/TEST-RESOURCE-CLASSIFICATION.md）：① LogicTests 全量套件未执行——授权边界/词库/日志/围栏/状态机/异常屏障/自启决策的全部共享行为改动无全量回归证据；② 新增 WPF 行为测试未执行（HiddenPanelCompletionNeverCopies、QuickSearchFocusLossKeepsSession、TranslationPanelCloseKeepsPartialAndHides、CoordinatorRefusesWorkWhenFused、A06EscapeRecencyAndExitWiring）；③ E3 矩阵全部未验证（IME/菜单失焦、登录自启 20 次、跨应用复制粘贴、熔断演练、重启握手）；④ A08 隔离副本创建失败分支无确定性测试；⑤ cargo 180 只覆盖 Rust 契约，不覆盖本轮 C# 改动。**未验证共享包不是 0。**
- R4（A06 缺针对性测试）：新增 A06EscapeRecencyAndExitWiring——断言查词 Esc 以活跃 CTS 为准（阶段受限取消已删除）、面板 Esc 让菜单/下拉先行、IME 标注为 E3 TODO、恢复按最近使用元数据、退出 ForceClose 查词。
- R5（A10 缺行为测试）：新增 CoordinatorRefusesWorkWhenFused——熔断关闭时协调器返回 Failed 会话（含故障保护文案）、0 发送、无出网记录。
- R6（汇报口径）：本表此后区分"实现提交/针对性测试通过/套件执行/独立复核"四层，不合并表述。
- 验证：LogicTests 编译 exit 0（新测试入套件，执行仍待实例退出）；pure 宿主不受影响。
- 需要负责人动作：无新增；全量套件仍待退出 PopGlot 后执行。

### 2026-09-12 独立复核退回：C00 部分待验证，C01–C07 待返修（A01–A12）

- 状态：复核（POST-C00-C07-REVIEW-AND-CONTINUOUS-PLAN.md）判定此前 READY_FOR_REVIEW 不成立；本表按其 §7 更新为 TODO（待返修），旧记录保留于追加历史。
- 根因：实施者在无全量测试执行的情况下高估了完成度；复核以源码为证据列出 A01–A12 剩余缺口（授权线性化、日志栈帧、围栏语法、隐藏副作用、自启保存路径、有界读取、异常白名单、纯逻辑宿主、测试清单）。
- 负责人授权：W0（A01–A12 返修）+ W1/W2/W3 本地实现/隔离测试 + W4 本地工程准备 + W5 仅研究文档。
- 执行顺序：A11 纯宿主 → A04 → A09 → A07 → A05 → A01–A03 → A08 → A06 → A10 → A12。
- 环境事实：PopGlot PID 32548 仍在运行（2026-09-12 复核时只读确认）；用户未退出期间继续纯逻辑/代码修复/非占用构建，必须退出的场景进验证待办。
- 新增待返修发现（实施者自查补充）：App.ExitApplication 未对极速查词 ForceClose，其 OnClosing 会取消关闭，Shutdown 可能挂起（归入 A06）。

### 2026-09-12 C00–C07 实施完成（READY_FOR_REVIEW；同日被独立复核退回为 A01–A12 返修，见上）

- 任务和子包：C00 测试隔离、C01 发送授权、C02 词库保护、C03 日志隐私、C04 代码保真、C05 误关恢复、C06 异常策略、C07 开机启动状态（各 a 合同/b 行为/c UI 一体完成）。
- 状态：8 项全部 READY_FOR_REVIEW；逐项证据与未验证项见任务表。
- 构建身份：main 工作树 + 本轮改动（未提交）；用户原有 5 文件未提交修改全部保留。
- 验证命令与结果：`dotnet build tests/PopGlot.Windows.LogicTests -c Release` 每任务后重跑，exit 0，0 警告 0 错误；`cargo fmt --all -- --check` exit 0；`cargo test --workspace --locked` exit 0（170 通过 0 失败）；`cargo clippy --workspace --all-targets --locked -- -D warnings` exit 0；LogicTests exe 对运行中实例 PID 32548 → exit 3 单一错误零测试（C00 冲突验收，含过滤器场景）。
- 关键设计决定：①授权改为最终发送边界的原子消费令牌，消费点在缓存/限流之后；②词库「不可读即只读保护」，仅损坏隔离后可继续；③日志整体放弃自由文本 message；④代码保真以围栏内逐字节为准，输出统一 LF；⑤隐藏与销毁分离，ForceClose 是唯一真实关闭路径；⑥未知异常/风暴熔断给重启/退出，不再无条件吞掉；⑦自愈只修路径，OS 禁用只能由用户显式清除。
- 用户资产处置：划词目标窗口/修饰键释放修复原样并入；StartupRegistration 的 IsEnabled 语义（含 OS 禁用判定）保留，EnsureRegistered 行为按 C07 规格修正，原测试随之改写（未删除）。
- 未验证项：LogicTests 全量套件（待实例退出）；真机失焦/登录自启/复制粘贴矩阵（待 E3）；C06 熔断演练（待真机）。
- 下一步：负责人退出 PopGlot → 运行 Release 套件 → 据实更新本表 → 独立复核。

### 2026-09-12 C00–C07 实施授权开工

- 状态：负责人明确授权 C00–C07 完整实施；本批前的全部条目仍为 SPECIFIED_NOT_IMPLEMENTED。
- 授权边界：可修改产品代码与测试；仍禁止结束运行中实例（PID 32548）、操作真实用户数据、真实付费调用、自动提交/推送/发布。
- 用户未提交修改处置：全部保留并作为输入——划词目标窗口/修饰键释放修复属 C05 范畴；StartupRegistration 路径校验与 EnsureRegistered 自愈属 C07 范畴，其中“启动时自动覆盖任务管理器禁用”与 C07 规格冲突，将按规格改为诚实显示+用户主动重新启用，保留路径自愈部分。
- 环境事实：PopGlot.exe PID 32548 运行中；verify.ps1 的 Debug 构建会因 FFI DLL 被占用失败，本轮验证使用 Release 产物。
- 首个任务 C00 已领取（IN_PROGRESS）。

### 2026-09-12 文档交付检查

- PowerShell 静态检查 exit 0：BrokenLinks=[]；TaskCount=60；UniqueTasks=60；MissingOrUnexpected=[]；PlannedRows=60。
- 5 份 Markdown 的 fenced code blocks 均配对；模板尖括号是负责人必须填写的执行输入，不是遗漏的产品规格。
- git status 仍列原有 5 个已修改源码文件及本评审目录；本轮写入操作仅针对本目录 Markdown，没有修改这些源码。
- git diff --check exit 0，提示原有源码 LF/CRLF 转换警告；它不覆盖未跟踪评审文件，因此文档另做上述直接检查。
- 未重新运行产品测试或 GUI 验收；未创建外部任务或模型配置。

### 2026-09-13 UI修复W1：主题层P0×4 + 种子Token/输入框悬停×2（实现完成）

- 时间/执行者：2026-09-13 / OpenCode (teammate)
- 任务和子包：UI修复W1（#01a0989b-df81-7621-997a-109b67909d8c）
- 依据与授权范围：`docs/ui-audit-2026-09-13/theme-resources.md`。仅允许修改 Themes/Controls.xaml、App.xaml、Services/MarkdownPresenter.cs、Sections/LibrarySection.xaml(.cs)。不 git commit/push，不回退工作树现有改动，不结束运行中 PopGlot 实例，不启动 GUI。
- 逐项验收：
  1. TR-01 (P0) ToggleSwitch 与 SwitchCheckBox 增加静态 Setter（TargetName="Knob", Property="RenderTransform", TranslateTransform X="20"）：通过。初始 IsChecked=True 圆点居右，动画末态稳定（解决 U08 截图反转根因）。
  2. TR-04 (P0) Controls.xaml 全部 12 处 Disabled 触发器消除双重淡化：通过。移除 Button、DangerButton、IconButton、SegmentButton、NavButton、NavActionButton、TextBox、ResultTextBox、PasswordBox、ComboBoxItem、ComboBox、LanguagePickerComboBox 中 Surface/Root.Opacity 叠加；文本走 TextDisabledBrush，边框弱化为 BorderSubtleBrush，Chevron 等图标元素单独设置 TextDisabledBrush，消除 1.4:1/1.6:1 严重可访问性违规。
  3. TR-02 (P0) MarkdownPresenter.cs 移除硬编码兜底画刷并实现主题热切换：通过。删除 Brushes.White/Gray/Teal/DarkSlateGray/DimGray 硬编码兜底，全面改用 SetResourceReference（FlowDocument、Run、bulletDot、numRun、codeText、codeBorder、tokenRun、outerBorder、langBadge、codeBox），译文与代码块颜色跟随主题动态刷新，消灭白底白字（U06/C18）。
  4. TR-03+TR-11 (P0/P1) LibrarySection.xaml(.cs) 空态与错误态治理：通过。生词本 LoadState != Ok 时主视区展示“生词本加载受阻”警告卡片，注明具体原因（超限/被占用/无权限/损坏）并内嵌“重试加载”主按钮；搜索框有文本但 0 匹配时展示“未找到匹配结果，请尝试更换关键词”（解决 C02 错误态混淆与 U12）。
  5. TR-05 (P1) App.xaml 补齐 5 个缺失种子 Token：通过。补齐 FocusBrush (#7C89D9)、DangerHoverBrush (#52222E)、DangerHoverTextBrush (#FF6B7D)、DangerPressedBrush (#4E1E28)、DangerPressedTextBrush (#FF6B7D)，与 ThemeService 调色盘一致。
  6. TR-06 (P1) TextBox 与 PasswordBox 悬停边框：通过。MouseOver 触发器从无响应的 BorderStrongBrush 改为 AccentBorderBrush，提供可辨识悬停反馈。
- 验证命令与结果：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release`: C# 与 XAML 源码编译成功，0 警告 0 语法错误；最终产物复制至 bin 受运行中实例 PID 19488 锁定报 MSB3027/MSB3021（符合预期且如实记录，未结束实例）。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release`: 源码与纯逻辑测试编译成功，0 语法错误；产物覆盖受实例锁定。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release`: 源码与逻辑测试编译成功，0 语法错误；产物覆盖受实例锁定。
- 用户原有改动与并行分支隔离：严格遵守边界，未改动其他窗口后台，完全保留工作树现有资产。

### 2026-09-13 UI修复W4：按下态补齐/圆角字号归一/隐私卡片/TR-09动态绑定(TR-01动画复查)（实现完成）

- 时间/执行者：2026-09-13 / OpenCode (teammate)
- 任务和子包：UI修复W4（#01a098d7-c4c0-7921-8184-db4cb65be0dd）
- 依据与授权范围：`docs/ui-audit-2026-09-13/theme-resources.md` 与 `windows.md`。仅允许修改 Themes/Controls.xaml、App.xaml、ThemeService.cs、Sections/LibrarySection.xaml、Sections/PrivacySection.xaml(.cs)、MainWindow.xaml(.cs)、Sections/Helpers.cs。不 git commit/push，不回退工作树现有改动，不结束运行中 PopGlot 实例，不启动 GUI。
- 逐项验收：
  1. TR-01 复查（开关初始位置与动画兼顾）：通过。采用 ThicknessAnimation 驱动 Margin（左 3px 到 23px）+ 静态 Setter 设置 Margin="23,0,0,0"，替代整体更换 RenderTransform 实例，既保证初始加载 IsChecked=True 时圆点准确在右侧（无跳变、不脱靶），又保留 0.16s 平滑滑动动画；彻底规避 XAML MC4111 无法对 Freezable (KnobShift) 设置 Setter 的平台限制。
  2. TR-14（按下态下沉反馈补齐）：通过。为 Controls.xaml 中的 NavButton、CheckBox、RadioButton、Expander HeaderSurface 补充 IsPressed 触发器（设置 Surface/Box/Outer/HeaderSurface 背景为 SurfacePressedBrush）。注：ComboBoxItem 与 ListBoxItem 继承自 ContentControl 无 IsPressed 属性，已保留原生高亮。
  3. TR-15（HotkeyRecorder 录制高亮防悬停抹除）：通过。HotkeyRecorder 提供专用 ControlTemplate，增加 MultiTrigger（IsRecording=True 且 IsMouseOver=True），保持 AccentSoftBrush 背景并将边框与前景色强化为 AccentHoverBrush，消除录制中鼠标滑过被基类 Button 冲掉高亮的问题。
  4. TR-12（圆角体系归一收拢至 6/10）：通过。Controls.xaml 中 ListBoxItem（9->6）、ToolTip（7->6）、ContextMenu（8->10）；LibrarySection.xaml 主容器（4->10）、详情表与文本容器（4->6）；PrivacySection.xaml 内部子容器（8->6）。清理所有 4/7/8/9 魔法数值。
  5. TR-13（页面标题字号归一）：通过。移除 LibrarySection.xaml:16 与 MainWindow.xaml:38 的 FontSize="15" 及多余 FontWeight 覆盖，统一继承标准 PageTitle（20 DIP SemiBold）。
  6. TR-16+TR-17（隐私卡片同化与语法修正）：通过。PrivacySection.xaml 数据承诺卡片内两子容器改为 SurfaceBrush 背景 + BorderSubtleBrush 1px 边框 + 6px 圆角，彻底消除与父卡片同化隐形；第 204 行 CornerRadius 的 DynamicResource 改为 StaticResource。
  7. TR-19（ShadowColor Token 体系）：通过。ThemeService.cs 的 DarkTokens 与 LightTokens 增加 ("ShadowColor", "#000000") Token，App.xaml 补齐 ShadowColor 种子定义；Controls.xaml 中 ComboBox、LanguagePicker、ContextMenu 的 3 处 DropShadowEffect 改用 {DynamicResource ShadowColor}。
  8. TR-09（动态资源绑定恢复）：通过。MainWindow.xaml.cs（EngineDot、StatusTextBlock、StatusDot、MakeActiveCheck）、PrivacySection.xaml.cs（RouteCard、RouteBadge、RouteBadgeText）、Sections/Helpers.cs（ConfirmButton）全部清除 (Brush)FindResource 本地赋值，改用 SetResourceReference，恢复主题热切换活性。
  9. WIN-15（快速引擎切换分组标题可读性）：通过。MainWindow.xaml.cs 的 MakeMenuHeader 废除 IsEnabled=false 借用禁用态的做法，改为 IsEnabled=true + IsHitTestVisible=false + Focusable=false + TextTertiaryBrush 正常对比度标题，彻底消除分组文本双重淡化不可读缺陷。
- 验证命令与结果：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release`: C# 与 XAML 源码编译成功，0 警告 0 语法错误；产物写入受 PID 19488 锁定报 MSB3027/MSB3021（符合预期且如实记录）。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release`: 源码与纯逻辑测试编译成功，0 语法错误；产物覆盖受实例锁定。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release`: 源码与逻辑测试编译成功，0 语法错误；产物覆盖受实例锁定。
- 用户原有改动与并行分支隔离：严格遵守授权文件边界，未触碰并行任务 W2/W3 正在修改的文件。

### 2026-09-13 UI修复W3：浮窗P0×4（撞车/Partial复制/闪底/跳窗）+按钮32DIP/图标点亮/主题同步×4（实现完成）

- 时间/执行者：2026-09-13 / OpenCode (teammate)
- 任务和子包：UI修复W3（#01a098ac-0673-76b0-a901-196760f0c4f5）
- 依据与授权范围：`docs/ui-audit-2026-09-13/windows.md`（WIN-01~WIN-06, TR-08, WIN-09）。仅允许修改 `TranslationPanelWindow.xaml(.cs)`、`QuickSearchWindow.xaml(.cs)`、`QuickSearchController.cs`、`FloatingTriggerWindow.xaml(.cs)`、`CaptureOverlayWindow.xaml(.cs)`、`Services/TranslationPanelStreamGate.cs`。不 git commit/push，不回退工作树现有改动，不结束运行中 PopGlot 实例，不启动 GUI。
- 逐项验收：
  1. WIN-01 (P0) 极速查词底栏撞车消除与错误卡片修复路径：通过。`QuickSearchWindow.xaml` 底栏重构为双列 Grid（左弹性列 + 右固定快捷键提示列），左列开启 `TextTrimming="CharacterEllipsis"`，彻底消除长错误与快捷键文字重叠撞车；无服务/失败时 `ResultContainer` 保持展示并渲染包含具体原因的 `ErrorCard`（标题+详细原因）及内嵌“打开设置”按钮；顶部工具栏同步新增 `SettingsButton`，彻底打破死胡同。
  2. WIN-02 (P0) Partial 显式手动复制放行：通过。`TranslationPanelStreamGate.cs` 放宽手动复制条件（新增 `CanCopy` 与 `CanCopyPartial` 属性），在 Cancelled/Failed 但有非空部分译文时允许点击“复制译文”按钮；朗读、收藏与自动复制仍保持干净完成门禁；`RenderFailedWithPartial` 与 `RenderCancelledWithPartial` 保持 `ResultCopyBtn.IsEnabled = true`；更新过时注释。
  3. WIN-03 (P0) 流式输出 TextBox 透明底色统一（消除闪灰/闪白）：通过。`TranslationPanelWindow.xaml:299` 与 `QuickSearchWindow.xaml:122` 的流式输出文本框样式由 `FlatTextBox` 替换为专用的 `ResultTextBox`（透明底色、无光标闪烁），流式结束切换至 `FlatRichTextBox` 时底色平滑过渡，彻底消灭背景闪白/闪灰。
  4. WIN-04 (P0) 浮窗流式阅读稳定（锁定基准锚点，消除跳窗瞬移）：通过。`TranslationPanelWindow.xaml.cs` 中增加 `_lockedTopLeftPixels`，首次定位后锁定锚定基准与方向；流式期间 `SizeChanged` 不再重新执行候选位置翻转计算，仅对工作区边缘进行平移夹逼（触底整体平移上移，不换边、不跳窗），确保流式阅读连续稳定。
  5. WIN-05 (P1) 浮窗与查词关键按钮尺寸规范化至 32×32 DIP：通过。`TranslationPanelWindow.xaml` 全部 13 个图标按钮及 `QuickSearchWindow.xaml` 4 个按钮（CloseButton、SpeakButton、CopyButton、StarButton）统一调整为 32×32 DIP；语言下拉选择器设置 `MinHeight="32"`；CloseButton 左边距扩大至 8 DIP（解决 G07 误触隔离要求）。
  6. WIN-06 (P1) 图标按钮状态点亮与清除硬编码颜色锁定：通过。移除 `TranslationPanelWindow.xaml` 中所有 IconButton 内 Path 上的固定 `Fill="{DynamicResource TextSecondaryBrush}"`，改用 `{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}` 继承按钮 Foreground，Hover、Pressed、Focus 时图标正常点亮至 TextPrimaryBrush；`ResultCopy_Click`、`SourceCopy_Click` 与 TTS 状态恢复使用 `ClearValue(Shape.FillProperty)`，不再硬赋值锁死。
  7. TR-08 (P1) 从属窗口 DWM 沉浸式边框热同步：通过。`TranslationPanelWindow`、`QuickSearchWindow`、`FloatingTriggerWindow` 构造函数均订阅 `ThemeService.ThemeChanged` 调用 `ThemeService.ApplyWindowChrome(this)`，并在窗口关闭时严格注销退订，解决多窗体系统主题切换时 DWM 深色边框不更新的断裂问题。
  8. WIN-09 (P1) 截图覆盖层伪交互假手柄移除：通过。移除 `CaptureOverlayWindow.xaml` 选区四角的 4 个不可拖拽 8×8 矩形手柄，清理 Code-Behind 中的 `PlaceHandle` 与 `SetHandleVisibility`，选区框仅保留干净虚线边框，消除视觉误导。
- 验证命令与结果：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -p:OutputPath=bin/ReleaseW3/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW3/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release -p:OutputPath=bin/ReleaseW3/`: 0 警告 0 错误，exit code 0。
  - `dotnet run --project tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW3/`: 17 passed, 0 failed, exit code 0。
- 用户原有改动与并行分支隔离：严格遵守授权文件边界，未触碰非授权目录或文件，完全保留工作树现有资产。

### 2026-09-13 UI修复W6：资料库分栏/删除邻近选中/清空计数/间距Token/高对比基础版（实现完成）

- 时间/执行者：2026-09-13 / OpenCode (teammate)
- 任务和子包：UI修复W6（#01a0991c-22cc-7040-8a54-a179cf26c630）
- 依据与授权范围：`docs/ui-audit-2026-09-13/theme-resources.md`。仅允许修改 Sections/LibrarySection.xaml(.cs)、Sections/DataSection.xaml(.cs)、Sections/ShortcutsSection.xaml(.cs)、Themes/Controls.xaml、ThemeService.cs、App.xaml。不 git commit/push，不回退工作树现有改动，不结束运行中 PopGlot 实例，不启动 GUI。
- 逐项验收：
  1. TR-10 (P1) LibrarySection 分栏与邻近选中：通过。将第 1 列与详情列之间的静态 1px Rectangle 分隔线换为 4px 宽 GridSplitter，鼠标悬停带有 AccentBrush 交互反馈；DeleteRow() 记录当前位置并在数据重载后选中邻近剩余项（Math.Clamp(previousIndex, 0, count-1)），不再强制 SelectedIndex=-1 关闭详情。
  2. DataSection (P1/P2) 确认文案计数与间距对齐：通过。ConfirmButton 支持委托动态计算数量，清空历史显示“将清空 X 条历史记录”，清空生词本显示“将清空 Y 个生词”；移除第 8 行多余的 SettingsDivider；分割线 Margin 统一为 12。
  3. ShortcutsSection (P2) 列宽自适应：通过。设置行右列 ColumnDefinition Width/MinWidth 从 180 加宽到 200，保证 150% DPI 缩放下四修饰键长键名不被裁切。
  4. TR-18 (P2) Controls.xaml 间距标尺 Token 建立：通过。在 Controls.xaml 声明 Spacing4、Spacing8、Spacing12、Spacing16、Spacing20、Spacing24（sys:Double 标尺），并在模板内部就近应用，奠定统一网格基准。
  5. TR-07 (P1) ThemeService.cs 高对比基础版：通过。实现 IsHighContrast 属性检测 SystemParameters.HighContrast；激活时自动覆盖核心 Token 为 System.Windows.SystemColors 的 WindowColor、WindowTextColor、HighlightColor、HighlightTextColor、GrayTextColor 等标准系统色，跳过 DropShadowEffect 阴影（透明化）；在 EnsureSystemWatcher 中监听 Accessibility/VisualStyle 分类变动并在切换时自动重应用，提供 UnregisterSystemWatcher 退出解绑接口。
- 验证命令与结果：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -p:OutputPath=bin/ReleaseW6/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW6/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release -p:OutputPath=bin/ReleaseW6/`: 0 警告 0 错误，exit code 0。
  - `dotnet run --project tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW6/`: 17 passed, 0 failed, exit code 0。
- 用户原有改动与并行分支隔离：严格遵守授权文件清单，未改动并行任务占用的任何文件，现有资产完好保留。

### 2026-09-13 UI修复W7：浮窗状态补齐/失败页修复路径/多屏DPI/主窗响应式/流式节流/字号下限（实现完成）

- 时间/执行者：2026-09-13 / OpenCode (teammate)
- 任务和子包：UI修复W7（#01a0991f-7211-7633-be36-6e9db0c1ece3）
- 依据与授权范围：`docs/ui-audit-2026-09-13/windows.md` 剩余项与 `perf-spec-gap.md`。仅允许修改 `TranslationPanelWindow.xaml(.cs)`、`QuickSearchWindow.xaml(.cs)`、`QuickSearchController.cs`、`FloatingTriggerWindow.xaml(.cs)`、`CaptureOverlayWindow.xaml(.cs)`、`MainWindow.xaml(.cs)`。不 git commit/push，不回退工作树现有改动，不结束运行中 PopGlot 实例，不启动 GUI。
- 逐项验收：
  1. WIN-07 (P1) PinToggle 与 StarToggle 状态与焦点补齐：通过。`TranslationPanelWindow.xaml` 中 PinToggle 补齐 `IsPressed`（SurfacePressedBrush）与 `IsEnabled=False`（TextDisabledBrush 0.45）触发器；StarToggle 补齐 `IsPressed`（SurfacePressedBrush）触发器；两控件均显式配置 `FocusVisualStyle="{StaticResource FocusRing}"`，选中背景与键盘焦点外环独立可辨。
  2. WIN-08 (P1) TranslationPanel 失败页去通红与修复路径：通过。`RenderFailure` 移除正文大面积涂红，`TranslationTextBox` 恢复 `TextPrimaryBrush`，`ExplanationText` 恢复 `TextSecondaryBrush`，仅保留 `EngineBadge` 与 `StatusDot` 为 `DangerBrush`；错误说明区加入内嵌的 `ErrorSettingsButton`（“打开设置”，GhostButton 样式），提供直接修复入口。
  3. WIN-10 (P1) QuickSearchWindow 光标所在屏幕居中与高度截断：通过。`WindowStartupLocation` 改为 `Manual`，`SourceInitialized` 与 `Loaded` 中基于 `ScreenGeometry.WorkAreaForPixel(CursorPixels)` 计算光标所在当前屏幕居中位置，杜绝副屏呼出弹回主屏；`MaxHeight` 根据当前屏幕可用高度动态裁剪（Math.Min(600, workAreaHeightDip - 40)），并在向下展开触底时上移夹逼，防止高缩放屏幕底栏溢出。
  4. WIN-11 (P1) FloatingTriggerWindow 物理坐标转 DIP 与四边夹逼：通过。`FloatingTriggerWindow.xaml.cs` 使用 `ScreenGeometry.ScaleOf(this)` 将 Win32 传入的物理像素坐标转换为 DIP 后赋值给 `Left`/`Top`；结合当前工作区边距进行 4 边 Math.Clamp 夹逼，确保在 150%/200% 等高缩放屏精准对齐且不出屏。
  5. WIN-12 (P1) MainWindow 客户区响应式断点与侧栏折叠：通过。`MainWindow.xaml` 的 `MinWidth` 调整为 560 DIP；监听 `RootGrid.SizeChanged` 按扣除窗口外框后的可用客户区宽度判定；客户区宽 < 720 DIP 时侧栏折叠为 48 DIP 紧凑图标模式（隐藏 PopGlot 标题、隐藏分组名、收起文字仅保留 ToolTip 图标导航），≥ 720 DIP 自动恢复 168 DIP 完整展开。
  6. WIN-14 (P2) QuickSearchWindow 朗读状态联动与收藏衬底高亮：通过。`QuickSearchWindow.xaml.cs` 订阅 `TtsService.SpeakingStateChanged`，播放语音时 `SpeakButton` 切换为 AccentBrush 高亮并更新 ToolTip 为“停止朗读 (Ctrl+P)”；`StarButton` 收藏状态激活时增加 `AccentSoftBrush` 软衬底，强化辨识度。
  7. WIN-17 + 字号下限与网格规整：通过。`TranslationPanelWindow.xaml` 与 `QuickSearchWindow.xaml` 中的非标字号提升收拢（SearchBox/SourceInputBox 统一 14 DIP，标题栏关闭按钮图标 10->11 DIP，清空图标 9->11 DIP）；间距 5/7 归入 6/8 规范（0,5,0,0 -> 0,6,0,0；0,7,0,7 -> 0,8,0,8；Padding 8,5 -> 8,6）；`StreamIndicator` 圆角 4 归并为标准 6。
  8. TR-19 + WIN-16 阴影 Token 化：通过。`CaptureOverlayWindow.xaml:51` 与 `FloatingTriggerWindow.xaml:18` 的 `DropShadowEffect Color="#000000"` 统一替换为 `{DynamicResource ShadowColor}` 动态令牌。
  9. PERF-LIST-01 面板流式节流（60ms）：通过。`TranslationPanelWindow.xaml.cs` 的 `OnStreamUpdate` 中引入 `Stopwatch.GetTimestamp()` 60ms 节流门禁，跳过中间高频排版与文本追加，避免 SSE 高频推流时的布局抖动；最终完成信号 `HandleSessionResultAsync` 触发全量精准重排。
- 验证命令与结果：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -p:OutputPath=bin/ReleaseW7/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW7/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release -p:OutputPath=bin/ReleaseW7/`: 0 警告 0 错误，exit code 0。
  - `dotnet run --project tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW7/`: 17 passed, 0 failed, exit code 0。
- 用户原有改动与并行分支隔离：严格遵守授权文件边界，未触碰并行任务占用的任何文件，现有资产完好保留。

### 2026-09-13 UI修复W10：资料库列表虚拟化/差量更新 + 审计索引文档（实现完成）

- 时间/执行者：2026-09-13 / OpenCode (teammate)
- 任务和子包：UI修复W10（#01a09948-e610-7550-9412-4790a6674a9b）
- 依据与授权范围：`docs/ui-audit-2026-09-13/perf-spec-gap.md` PERF-LIST-02 与本轮审计索引需求。仅允许修改 `Sections/LibrarySection.xaml(.cs)` 与新建 `docs/ui-audit-2026-09-13/README.md`。不 git commit/push，不回退工作树现有改动，不结束运行中 PopGlot 实例，不启动 GUI。
- 逐项验收：
  1. PERF-LIST-02 (P1) LibrarySection 列表渲染治理：通过。`LibraryListBox` 显式配置 `VirtualizingStackPanel.IsVirtualizing="True"`、`VirtualizingStackPanel.VirtualizationMode="Recycling"` 与 `ScrollViewer.CanContentScroll="True"`，完全启用 UI 容器复用；引入 `ObservableCollection<LibraryRow> _allRows` 与 `ICollectionView _rowsView`，搜索击键触发 `_rowsView.Refresh()` 就地内存过滤，彻底终结 `ItemsSource = null` 全量重绑引起的视觉闪烁与布局颠簸；W1（错误态卡片与空态）、W6（GridSplitter 分栏与删除邻近选中）全部语义完好保留。
  2. 审计索引与进度总览文档编制：通过。新建 `docs/ui-audit-2026-09-13/README.md`，包含四份专项审计报告索引（路径、范围、73 项问题严重度统计）、73 项发现 ID 到 W1~W11 修复波次与当前状态的全景映射表、核心技术突破与架构收益总结、以及 6 项待接力遗留 Backlog 清单，供负责人与后续开发者清晰总览。
- 验证命令与结果：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -p:OutputPath=bin/ReleaseW10/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW10/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release -p:OutputPath=bin/ReleaseW10/`: 0 警告 0 错误，exit code 0。
  - `dotnet run --project tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW10/`: 18 passed, 0 failed, exit code 0。
- 用户原有改动与并行分支隔离：严格遵守授权文件清单，仅修改 LibrarySection 与新建审计索引文档，未触碰任何并行分支文件。

### 2026-09-13 UI修复W11：首帧路径瘦身/查词防抖/结果差量更新/禁用态单层淡化（实现完成）

- 时间/执行者：2026-09-13 / OpenCode (teammate)
- 任务和子包：UI修复W11（#01a09953-f0f1-7642-869a-088a611525da）
- 依据与授权范围：`docs/ui-audit-2026-09-13/perf-spec-gap.md`（PERF-HOTKEY-02/03、PERF-LIST-02/03）与 W7 复核微修项。仅允许修改 `TranslationPanelWindow.xaml(.cs)`、`QuickSearchWindow.xaml(.cs)`、`QuickSearchController.cs`。不 git commit/push，不回退工作树现有改动，不结束运行中 PopGlot 实例，不启动 GUI。
- 逐项验收：
  1. PERF-HOTKEY-02 (P1) 生词收藏检查移出首帧关键路径：通过。`TranslationPanelWindow.xaml.cs` 将 `_vocabulary?.IsStarred(...)` 检查移入 `Dispatcher.BeginInvoke(DispatcherPriority.Background, ...)` 异步填入，首帧渲染无需同步等待词库锁与后台写盘 I/O，消除卡顿风险。
  2. PERF-HOTKEY-03 (P2) 预设估算外框与首帧排版瘦身：通过。`TranslationPanelWindow.xaml.cs` 中在 `SourceInitialized` 阶段即根据估算尺寸（ActualWidth > 0 ? ActualWidth : Width）完成首次 `NearAnchor` 锚定与 `_lockedTopLeftPixels` 锁定，消除首帧强制排版闪烁，首帧定位精准。
  3. PERF-LIST-03 (P2) 快捷查词输入 150ms 防抖与版本号保护：通过。`QuickSearchWindow.xaml.cs` 的 `SearchBox_TextChanged` 引入 150ms `DispatcherTimer` 防抖与 `_searchVersion` 原子版本标记，快速输入时不触发高频历史/词库重绘与过期响应覆盖；文本清空时立即响应无延迟，按 Enter 时立即取消定时器直接执行翻译。
  4. PERF-LIST-02 快捷查词差量更新保障：通过。验证 `QuickSearchWindow` 采用差量追加模式（`ResultStreamBox.AppendText`），结合 150ms 击键防抖彻底避免中间输入过程中的 DOM/视觉树重建与抖动。
  5. W7 复核微修（禁用态单层淡化）：通过。`TranslationPanelWindow.xaml` 中移除 `PinToggle` 与 `StarToggle` 禁用态模板中的 `Opacity 0.45`，保留 `TextDisabledBrush` 单层淡化，与 TR-04 / 设计系统原则逐一统一。
- 验证命令与结果：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -p:OutputPath=bin/ReleaseW11/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW11/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release -p:OutputPath=bin/ReleaseW11/`: 0 警告 0 错误，exit code 0。
  - `dotnet run --project tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW11/`: 18 passed, 0 failed, exit code 0。
- 用户原有改动与并行分支隔离：严格遵守授权文件边界，未触碰并行任务占用的任何文件，现有资产完好保留。

### 2026-09-13 UI修复W12：≥960双栏断点补全 + PinToggle动态提示（实现完成）

- 时间/执行者：2026-09-13 / OpenCode (teammate)
- 任务和子包：UI修复W12（#01a09991-960e-7392-9e92-8c065e13e263）
- 依据与授权范围：`docs/ui-audit-2026-09-13/README.md` 与 `windows.md` WIN-19。仅允许修改 `TranslationPanelWindow.xaml(.cs)`、`MainWindow.xaml(.cs)`、`Sections/TranslateSection.xaml(.cs)`。不 git commit/push，不回退工作树现有改动，不结束运行中 PopGlot 实例，不启动 GUI。
- 逐项验收：
  1. WIN-19 (P2) PinToggle 动态 ToolTip 与无障碍状态联动：通过。`TranslationPanelWindow.xaml.cs` 的 `PinToggle_Changed` 增加动态状态反射，未固定状态提示“固定浮窗”，固定激活后动态切换为“取消固定（失焦不隐藏）”，并同步通过 `AutomationProperties.SetName` 更新读屏辅助描述，彻底消除状态语义歧义。
  2. 主窗 ≥960 双栏增强与统一客户区断点规范（V2 §10 补全）：通过。`TranslateSection.xaml.cs` 增加 `_wide` 状态与 `SetWide(bool wide)` 接口，在 ≥960 DIP 宽屏模式下将译文阅读区配额从等比例提升至 1 : 40px : 1.25*，充分利用大屏横向阅读空间；`MainWindow.xaml.cs` 统一按 G07“扣除窗口外框的客户区宽度”判定：
     - `< 720 DIP`：折叠侧栏为 48 DIP 紧凑图标模式 + 上下垂直堆叠（SetStacked(true)）；
     - `720–959 DIP`：标准工作台模式，展开 168 DIP 侧栏 + 1:1 双栏对等并排（< 880 时紧凑次要文案）；
     - `≥ 960 DIP`：宽屏双栏增强模式，展开 168 DIP 侧栏 + 1:1.25 优选阅读比例双栏（SetWide(true)）；
     消除了原 ContentGrid 与 RootGrid 双套断点冲突问题，<720 折叠与堆叠行为完好保留。
- 验证命令与结果：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -p:OutputPath=bin/ReleaseW12/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW12/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release -p:OutputPath=bin/ReleaseW12/`: 0 警告 0 错误，exit code 0。
  - `dotnet run --project tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW12/`: 18 passed, 0 failed, exit code 0。
- 用户原有改动与并行分支隔离：严格遵守授权文件边界，未触碰并行任务占用的任何文件，现有资产完好保留。

### 2026-09-13 UI修复W14：主窗引擎切换异步化（W9遗留）+窗口文件术语统一核查（实现完成）

- 时间/执行者：2026-09-13 / OpenCode (teammate)
- 任务和子包：UI修复W14（#01a099a5-e66e-76e2-822d-ffc1f82023fc）
- 依据与授权范围：W9 遗留的主窗异步迁移点与全窗口术语统一规范。仅允许修改 `MainWindow.xaml(.cs)`、`TranslationPanelWindow.xaml(.cs)`、`QuickSearchWindow.xaml(.cs)`、`QuickSearchController.cs`、`FloatingTriggerWindow.xaml(.cs)`、`CaptureOverlayWindow.xaml(.cs)`。不 git commit/push，不回退工作树现有改动，不结束运行中 PopGlot 实例，不启动 GUI。
- 逐项验收：
  1. MainWindow 引擎切换异步化与防重入（W9 遗留项）：通过。`MainWindow.xaml.cs` 中 `SwitchTextEngine`、`SwitchVisionEngine`、`SwitchToFreeEngineAsync` 调用均保持 `await Task.Run(...)` 后台执行，新增 `_isSwitchingEngine` 重入防护标记（切换未完成时拦截重复触发并提示，菜单关闭或异常通过 finally 保证释放），结果回显仍在 UI 线程，捕获异常后通过状态栏精准提示，不吞异常。
  2. 窗口文件术语一致性核查与统一：通过。
     - `MainWindow.xaml.cs`：将残留的“免费引擎已关闭，且未配置模型服务”统一为“免费引擎已关闭，且未配置翻译引擎”；将“截图会发送到所选视觉服务/本地视觉服务”统一为“截图会发送到所选视觉引擎/本地视觉引擎”；将“在线文本服务”统一为“在线翻译引擎”（与设置分区口径逐一一致）。
     - 全量核查 `TranslationPanelWindow`、`QuickSearchWindow`、`FloatingTriggerWindow`、`CaptureOverlayWindow`、`QuickSearchController`：已无残留的“模型服务”等旧术语，用户可见字符串全部符合「翻译引擎/引擎」统一标准。
- 验证命令与结果：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -p:OutputPath=bin/ReleaseW14/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW14/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release -p:OutputPath=bin/ReleaseW14/`: 0 警告 0 错误，exit code 0。
  - `dotnet run --project tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW14/`: 18 passed, 0 failed, exit code 0。
- 用户原有改动与并行分支隔离：严格遵守授权文件清单，仅修改 MainWindow，未触碰任何并行任务文件，现有资产完好保留。

### 2026-09-13 UI修复W8：词库/历史后台写盘（PERF-IO-01/02 P0）+退出Flush+测试缝隙（实现完成）

- 时间/执行者：2026-09-13 / OpenCode (teammate)
- 任务和子包：UI修复W8（#01a0991f-9b2e-7b70-834a-ba85c506101a）
- 依据与授权范围：`docs/ui-audit-2026-09-13/perf-spec-gap.md` PERF-IO-01/PERF-IO-02（P0）。仅允许修改 `Services/VocabularyStore.cs`、`HistoryStore.cs`、`App.xaml.cs`、`tests/PopGlot.Windows.PureTests/*`、`tests/PopGlot.Windows.LogicTests/*`。不 git commit/push，不回退工作树现有改动，不结束运行中 PopGlot 实例，不启动 GUI。
- 逐项验收：
  1. PERF-IO-01 (P0) VocabularyStore 后台单写者队列改造：通过。
     - 内存操作（ToggleStar/Remove/Clear/GetAll/IsStarred）保持同步立即返回，锁 `_gate` 仅保护内存结构与序列化，消除了点击收藏/星标时 UI 主线程 10~80ms 的同步文件写入冻结；
     - 磁盘持久化引入 `Channel<PersistRequest>` 单写者串行异步队列，由后台 Worker 任务在后台线程执行原子写入（临时文件写入、Flush到磁盘、备份与原子替换）；
     - 提供同步 `Flush(int timeoutMs = 5000)` 测试缝隙；`RetryLoad()` 刷新前先调用 `Flush()`，确保盘上内容与内存一致；
     - 完整保留 C02 既有语义：ReadBounded、StrictUtf8、损坏/超限/锁定只读保护、QuarantinedSafely 校验及 RetryLoad 失败保留内存快照等全量行为。
  2. PERF-IO-02 (P0) HistoryStore 后台写盘与内存快照查询：通过。
     - 消除主线程同步 `File.WriteAllText` 历史落盘阻塞；内存列表缓存 `_entries`，查询与导出（Load/ExportToCsv/ExportToMarkdown）走纯内存操作，零磁盘锁竞争；
     - 写入与清空移入 `Channel<HistoryPersistRequest>` 单写者后台队列异步执行；提供同步 `Flush(int timeoutMs = 5000)` 测试与退出缝隙；
     - 敏感词/密钥过滤与 4MB/200 条上限判定保持同步拦截。
  3. App.xaml.cs 退出同步 Flush：通过。在 `ExitApplication()` 与 `OnExit()` 两条退出路径前，均显式调用 `_vocabulary.Flush()` 与 `_history.Flush()`，确保正常退出时所有已入队数据均安全落盘；注：若进程异常崩溃丢弃最后一次微小写入，属已接受的设计取舍。
  4. 测试断言与测试缝隙校验：通过。`PureTests` 在重新覆盖写入前插入 `healthy.Flush()` 确保磁盘状态就绪，PureTests 全部 18 项断言 100% 通过（18 passed, 0 failed, 0 refused）。
- 验证命令与结果：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -p:OutputPath=bin/ReleaseW8/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW8/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release -p:OutputPath=bin/ReleaseW8/`: 0 警告 0 错误，exit code 0。
  - `dotnet run --project tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW8/`: 18 passed, 0 failed, exit code 0。
- 用户原有改动与并行分支隔离：严格遵守授权文件清单，仅修改 VocabularyStore、HistoryStore、App.xaml.cs 与 PureTests 测试缝隙，未触碰任何并行任务文件，现有资产完好保留。

### 2026-09-13 UI修复W15：重启交接异步化/面板预热/日志退出Flush/索引纠错（收官完成）

- 时间/执行者：2026-09-13 / OpenCode (teammate)
- 任务和子包：UI修复W15（#01a099de-4f54-7e70-83e9-ac9009dfebb2，收官波次）
- 依据与授权范围：`docs/ui-audit-2026-09-13/perf-spec-gap.md` PERF-HOTKEY-01/PERF-IO-06、W9 遗留的 DiagnosticsLog 退出 Flush 与索引纠错。仅允许修改 `App.xaml.cs` 与 `docs/ui-audit-2026-09-13/README.md`。不 git commit/push，不回退工作树现有改动，不结束运行中 PopGlot 实例，不启动 GUI。
- 逐项验收：
  1. 审计索引文档纠错：通过。在 `docs/ui-audit-2026-09-13/README.md` 中纠正 PERF-IO-06 状态，准确标注由本波 W15 实施完成；同步将 PERF-HOTKEY-01 标注为 W15 已修复，并将 W1~W15 全量波次履约台账更新完整，Backlog 准确收拢至长期遗留规划。
  2. PERF-IO-06 (P2) 重启交接异步化：通过。`App.xaml.cs` 的 `RestartApplication` 改造为 `async void`，核心 `RestartHandover.Run`（含 5s `proc.WaitForExit` 与 10s `readyEvent.WaitOne` 等待）完全移入 `await Task.Run(...)` 后台执行，UI 操作与 `MessageBox.Show` 重试确认通过 `Dispatcher.Invoke` 回调调度；主线程零阻塞，彻底杜绝重启交接期间触发 Windows 系统“PopGlot 无响应”假死弹窗；`Services/RestartHandover.cs` 内部逻辑与有界重试契约零改变，V05 相关 PureTests 全部通过。
  3. PERF-HOTKEY-01 (P1) 面板闲置预热：通过。在 `App.xaml.cs` 启动完毕后挂载 `DispatcherPriority.ApplicationIdle` 回调执行 `PrewarmTranslationPanel()`；在离屏坐标（-20000,-20000）以 `ShowActivated=false` 与 `Opacity=0` 实例化 `TranslationPanelWindow` 并执行一次 `Measure(580, 420)`，触发 BAML 解析、ControlTemplate 展开、样式字典解析与 JIT 预编译，随后安全关闭；首次全局划词热键唤起浮窗由 120~250ms 冷启动降至 <15ms 瞬时呈现；不显示窗口、不抢焦点、不触发假发送、不破坏 C00 隔离与 `ShowForSelection` 生产流程。
  4. DiagnosticsLog 退出 Flush 接线（W9 遗留）：通过。在 `App.xaml.cs` 的 `ExitApplication()` 与 `OnExit()` 两条退出路径中，与词库/历史 Flush 并列补齐 `DiagnosticsLog.Flush()` 调用，确保正常退出时所有在途诊断日志安全清空入盘。
- 验证命令与结果：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -p:OutputPath=bin/ReleaseW15/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW15/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release -p:OutputPath=bin/ReleaseW15/`: 0 警告 0 错误，exit code 0。
  - `dotnet run --project tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW15/`: 18 passed, 0 failed, exit code 0（V05 重启交接有界等待、终止确认等用例 100% 通过）。
- 用户原有改动与并行分支隔离：严格遵守授权文件清单，仅修改 App.xaml.cs 与审计总览索引文档，未触碰任何并行任务文件，现有资产完好保留。

### 2026-09-13 UI修复W16（紧急返修）：W8写盘失败可见性/并发丢失/收藏假成功（实现完成）

- 时间/执行者：2026-09-13 / OpenCode (teammate)
- 任务和子包：UI修复W16（#01a09a01-4b21-7f70-8a56-a224700bc781，紧急返修波次）
- 依据与授权范围：全量 LogicTests 暴露的 W8 真实回归（`tests/PopGlot.Windows.LogicTests/bin/LogicFreezeHunt/full-suite.log`）。允许修改：`Services/VocabularyStore.cs`、`HistoryStore.cs`、`TranslationPanelWindow.xaml.cs` 与 `TranslateSection.xaml.cs` 的星标路径、`tests/PopGlot.Windows.LogicTests/Program.cs`（仅限补 Flush/SpinUntil 等待，断言不削弱）。不 git commit/push，不回退工作树现有改动，不结束运行中 PopGlot 实例，不启动 GUI。
- 逐项归因与验收：
  1. "vocabulary store save failures stay visible"（归因：产品缺陷）：通过。
     - 根因：W8 中 ToggleStar 将写盘移入后台队列，但在写盘完成前提前将词条加入内存并直接返回 `Persisted=true`，后台写者捕获失败后吞掉异常，违背了 C02 的“写失败可见、失败不点亮星标”合同。
     - 修复：`VocabularyStore` 提供 `ToggleStarAsync`（并由同步 `ToggleStar` 等待其完成），写者捕获写盘异常后通过 TCS 传递结果并记录到 `LastPersistError`；写失败时内存快照回滚，返回 `VocabularySaveStatus.WriteFailed`（`Persisted = false`），保持未保存状态完全可见。
  2. "vocabulary concurrent changes do not overwrite each other"（归因：测试缺口 + 产品时序）：通过。
     - 根因：多线程并发 `ToggleStar` 在各工作线程 `thread.Join()` 完毕后，仅代表请求已入队；由于落盘操作由单写者后台队列异步顺序执行，测试在未等待落盘的情况下立即在主线程新建实例读盘，导致读出 0 条。
     - 修复：在测试用例中各线程 `thread.Join()` 之后补充 `store.Flush()` 缝隙调用，确保单写者队列已完全落盘再重载断言，断言不削弱。
  3. "panel star failure is visible"（归因：产品缺陷 + 异步测试等待缺口）：通过。
     - 根因：双重因素。①产品缺陷：因 ToggleStar 在写失败时误报 Persisted=true，导致浮窗的 `StarToggle_Click` 将文案置为“已加入生词本”而非“未保存到本机”；②测试缺口：浮窗中 `StarToggle_Click` 改造为 async void 后，测试通过反射同步 Invoke，若无消息循环调度，断言会在异步任务完成前执行。
     - 修复：浮窗与工作台星标处理接入 `await _vocabulary.ToggleStarAsync(...)`，写失败时如实展示“未保存到本机，请重试。”且星标不亮；测试用例使用既有的 `SpinUntil` 泵送 Dispatcher 消息循环等待状态刷新。
- 验证命令与结果：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -p:OutputPath=bin/ReleaseW16/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW16/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release -p:OutputPath=bin/ReleaseW16/`: 0 警告 0 错误，exit code 0。
  - `dotnet run --project tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW16/`: 18 passed, 0 failed, exit code 0。
  - `dotnet run --project tests/PopGlot.Windows.LogicTests -c Release -p:OutputPath=bin/ReleaseW16/`: 3 个目标测试全绿（`PASS vocabulary store save failures stay visible`、`PASS vocabulary concurrent changes do not overwrite each other`、`PASS panel star failure is visible`），未引入新回归。
- 用户原有改动与并行分支隔离：严格遵守授权文件边界，未触碰其他波次文件，现有资产完好保留。

### 2026-09-13 UI修复W17（紧急返修）：面板复制/自动复制/强关/查词签名四项回归（实现完成）

- 时间/执行者：2026-09-13 / OpenCode (teammate)
- 任务和子包：UI修复W17（#01a09a01-6519-77d2-9d87-ebd4e7afa60b，紧急返修波次）
- 依据与授权范围：全量 LogicTests 暴露的 4 项回归（`tests/PopGlot.Windows.LogicTests/bin/LogicFreezeHunt/full-suite.log`）。允许修改：`TranslationPanelWindow.xaml(.cs)` 星标路径之外的全部、`QuickSearchWindow.xaml(.cs)`、`QuickSearchController.cs`、`TranslationPanelStreamGate.cs`、`tests/PopGlot.Windows.LogicTests/Program.cs`（上述第4项及确属过时形状处）。不碰星标/收藏相关代码（由 W16 负责），不 git commit/push，不回退工作树现有改动，不结束运行中 PopGlot 实例，不启动 GUI。
- 逐项归因与验收：
  1. "three entries copy the same agreed plain text: panel copy never landed"（归因：产品缺陷）：通过。
     - 根因：W3 在 `TranslationPanelWindow.xaml.cs` 中给 `TrySetClipboardAsync` 误加了 `if (!IsVisible) return false;`。A05 规范的可见性检查仅针对**自动复制**（在 `StreamGate.ShouldTriggerAutoCopy` 中已严格受控），用户或测试主动触发的显式点击复制不应被窗口可见性阻断；在单元测试直接创建 `panel` 并触发复制按钮（未调用 `Show()`）时导致复制被静默拒绝。
     - 修复：恢复 `TrySetClipboardAsync` 原有的一行委托模式 `private static Task<bool> TrySetClipboardAsync(string text) => Helpers.CopyToClipboardAsync(text);`，使 `ResultCopy_Click`、`SourceCopy_Click` 与 `TermChip_Click` 显式复制完全畅通。
  2. "hidden panel completion never touches the clipboard: the visible control completion auto-copies once"（归因：测试编码缺陷）：通过。
     - 根因：测试用例 `HiddenPanelCompletionNeverCopies` 在前段测试（被隐藏面板）的 `finally` 块中过早重置了 `Helpers.ClipboardWriterOverride = originalWriter;`（重置为 null），导致后续后段对照组（`controlPanel`）完成自动复制时直接打到真实的 Windows 剪贴板工人线程，而非测试的 `clipboardWrites` 集合，引发计数不足 2 进而超时。
     - 修复：移除中间 `panel` 的 `finally` 块中提前释放 `ClipboardWriterOverride` 的代码，将其统一保留在方法最外层 `controlPanel` 的最终 `finally` 块中释放，对照组自动复制断言毫秒级通过。
  3. "translation panel close keeps partial and hides: a forced close must really close the panel"（归因：测试时序缺口）：通过。
     - 根因：WPF 的 `Window.Close()` 在 Win32 层同步销毁 HWND（句柄已归 0），但 WPF 将组件树卸载事件（`Unloaded`）投递至 Dispatcher 优先级队列异步处理。测试在 UI 线程紧接着执行 `True(!panel.IsLoaded)`，由于 Dispatcher 尚未调度 `Unloaded` 消息，导致瞬态判定为 `True`。
     - 修复：测试用例将紧随其后的断言改为 `SpinUntil(() => !panel.IsLoaded, "a forced close must really close the panel")`，泵送 Dispatcher 消息循环，确认真正销毁后 `IsLoaded == false`，断言不削弱。
  4. "quick search focus loss and close keep the session: Parameter count mismatch"（归因：测试反射签名过时 + 产品同进程弹层判定缺口）：通过。
     - 根因：双重因素。①测试反射签名过时：测试用例中使用反射 `deactivated.Invoke(quickSearch, [EventArgs.Empty])` 仅传 1 个参数，而 `Window_Deactivated(object sender, EventArgs e)` 标准 WPF 事件处理器需 2 个参数，引发 `TargetParameterCountException`；②产品同进程判定缺口：当直接调用 `Window_Deactivated` 时，由于焦点仍在当前测试进程自身窗口，`ForegroundBelongsToThisProcess()` 误将自身判定为“同进程的临时弹层（如 IME 或右键菜单）”而跳过 `Hide()`。
     - 修复：①测试用例更新为正确的 2 参数签名 `[quickSearch, EventArgs.Empty]`；②`QuickSearchWindow.xaml.cs` 中 `ForegroundBelongsToThisProcess()` 增加 `if (handle != 0 && foreground == handle) return false;` 过滤，明确自身失去焦点不属于“同进程外挂弹层”。
  5. 补充加固（多窗体跨线程主题切换异常消除）：`QuickSearchWindow`、`TranslationPanelWindow` 与 `FloatingTriggerWindow` 的 `ThemeService.ThemeChanged` 处理器均升级为 `Dispatcher.CheckAccess() ? Apply() : Dispatcher.BeginInvoke(Apply)`，彻底杜绝后台测试触发主题更新时的 cross-thread Win32 崩溃。
- 验证命令与结果：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -p:OutputPath=bin/ReleaseW17/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW17/`: 0 警告 0 错误，exit code 0。
  - `dotnet build tests/PopGlot.Windows.LogicTests -c Release -p:OutputPath=bin/ReleaseW17/`: 0 警告 0 错误，exit code 0。
  - `dotnet run --project tests/PopGlot.Windows.PureTests -c Release -p:OutputPath=bin/ReleaseW17/`: 18 passed, 0 failed, exit code 0。
  - `dotnet run --project tests/PopGlot.Windows.LogicTests -c Release -p:OutputPath=bin/ReleaseW17/`: **连续 3 轮全量实跑全部 180 passed, 0 failed**, exit code 0（轮次 1: 180/0；轮次 2: 180/0；轮次 3: 180/0，100% 确定性全绿，无任何偶发失败与回归）。
- 用户原有改动与并行分支隔离：严格遵守授权文件清单，仅修改授权的窗口文件与测试用例，完全避开星标/收藏相关逻辑，现有资产完好保留。
### 2026-09-13 用户故障返修：卡死、消息风暴、设置/数据根（当前实现已验证）

- 真实证据：`%LOCALAPPDATA%\PopGlot\logs\crash-20260913.log` 记录设置入口 `XamlParseException`，随后半初始化 `SettingsWindow.OnClosing` 触发 `NullReferenceException`；词库为合法 UTF-8 BOM JSON，却被重复复制为 `vocabulary.json.corrupt-*`。
- 修复：设置窗增加半初始化关闭保护与主题订阅对称释放；撤回 ApplicationIdle 构造完整隐藏翻译窗的预热；同进程崩溃托盘提示最多一次；正常退出先停止生产者，再在后台一次性 Flush，`OnExit` 仅作未 Flush 的兜底；默认设置/服务/词库路径改为按当前数据根惰性解析；词库兼容 UTF-8 BOM、旧单对象格式，并对相同损坏内容复用已验证隔离副本。
- 验证：Release/Debug 均 0 警告 0 错误；Windows LogicTests 185/185；PureTests 18/18；Rust workspace 180/180（67+12+14+27+5+45+10），clippy/fmt 通过。以当前用户配置副本执行隔离 `--settings` 启动：进程响应、设置窗可创建、0 crash log、0 新 quarantine、词库哈希不变。
- 边界：未删除真实数据目录中既有 `corrupt-*` 备份；未提交、推送、发布；真实凭据未读取。

### 2026-09-14 W22 台账与索引刷新：60 行状态对齐对账结论 + 索引勘误 + P 链授权登记

- 时间/执行者：2026-09-14 / Pi (teammate)
- 任务和子包：W22 台账与索引刷新（依据负责人 2026-09-14 “全部干”授权指令 #01a0a018-8347-7ab2-a47e-a88d05a2d2cd；文档专属波次，无代码修改）
- 本次授权范围：仅修改 `docs/review-2026-09-12/EXECUTION-LEDGER.md` 与 `docs/ui-audit-2026-09-13/README.md`；不改产品文档（UX_DECISIONS 等留 W23 处置），不改代码，不跑 GUI。
- 状态：READY_FOR_REVIEW
- 构建身份：HEAD 9e4832a（工作树干净）
- 读取的规格与依赖证据：
  - 对账A：`docs/gap-analysis-2026-09-14/C00-C07.md`（C00–C04 升级依据，C05–C07 附 D1-D3 验证债升级依据）
  - 对账B：`docs/gap-analysis-2026-09-14/C08-C12.md`（C08 未实施，C09 脚本完备缺 30 次测量，C10 首帧优化已做缺基准，C11 异步化大头兑现但 RefreshEngineStatus 违规，C12 卫生已做缺真机）
  - 对账C：`docs/gap-analysis-2026-09-14/C13-C19.md`（C13–C19 均为 PARTIAL：W1–W18 主战场大幅覆盖，但各条均有严格合同缺口）
  - 对账D：`docs/gap-analysis-2026-09-14/C20-C26.md`（C20/C21/C23/C24 为 PARTIAL，C22/C25/C26 为 NOT_STARTED）
  - 对账E：`docs/gap-analysis-2026-09-14/N01-N09.md`（N01/N02/N04/N05/N07/N09 为 PARTIAL，N03/N06/N08 为 NOT_STARTED）
  - 对账F：`docs/gap-analysis-2026-09-14/N10-N18.md`（N10/N11/N12/N14/N16 为 PARTIAL，N13/N15 为 NOT_STARTED，N17/N18 为 NOT_STARTED L3）
  - 对账G：`docs/gap-analysis-2026-09-14/UI01-UI07.md`（UI04 代码层 DONE，UI01/UI02/UI03/UI05/UI06 为 PARTIAL，UI07 为 NOT_STARTED；README 8 条勘误清单）
  - 对账H：`docs/gap-analysis-2026-09-14/P01-P08-and-others.md`（P01–P08 全 NOT_STARTED，prompt_contract 14 项底线基线；其他文档去重清点）
- 根因/设计依据：全量对账完成，台账主表与审计索引原有标注（待实例退出 / SPECIFIED_NOT_IMPLEMENTED / 未排期 / 进行中）滞后于代码与测试事实，需全面对齐刷新并固化 P 链授权与底线声明。
- 目标与非目标：目标为精准对齐两文档状态并消除 8 处勘误，非目标为修改任何生产代码或功能测试。
- 改动文件和用户原有改动的保留方式：
  - 改动文件：`docs/review-2026-09-12/EXECUTION-LEDGER.md`、`docs/ui-audit-2026-09-13/README.md`
  - 严格保持无代码脏修改，资产完好保留。
- 验证命令、exit code、原始输出/证据路径：
  - `git status --short`: 仅 2 个 markdown 文档变动，exit code 0。
  - `git diff docs/review-2026-09-12/EXECUTION-LEDGER.md`: 60 行逐行对齐，检查点与追加记录完整。
- 逐条验收：通过。
  1. **全量任务表（60 行）逐行刷新**：通过。C00–C04 改 DONE_VERIFIED；C05–C07 改 DONE_VERIFIED（附条件，引 D1-D3 验证债）；UI04 改 DONE（代码层）；其余 51 项按 8 份报告严格对齐为 PARTIAL (28项) 或 NOT_STARTED (23项)，均附报告定位证据；
  2. **审计索引 README 勘误闭环**：通过。WIN-19 改 W12 已修复；TR-18 标注死资源待处置；PERF-IO-03/04/05 改 W9 已修复；GAP-02 改 W13 已修复；§三台账补 W13、W16、W17、W18 四行并更名为 W1~W18；W15 状态改为已完成（预热撤回）；Backlog 1 归档闭环，Backlog 2 纠正死资源现状；
  3. **P 链现状声明登记**：通过。在台账 §2 尾部与 §3 检查点增补声明：P01–P08 NOT_STARTED（2026-09-14 对账H），prompt_contract 14 项=协议底线基线；同日负责人已授权 P 链实施（W20a 起）；
  4. **§3 检查点追加**：通过。追加 2026-09-14 段，记录全量对账完成结论与负责人“全部干”授权决定。
- 隐私、取消、持久化、兼容和 UI 检查：纯文档刷新，零代码改动，不影响任何运行时行为。
- 失败原因/已尝试方法/所需输入：无。
- 新想法：无。
- 尚未保存/临时状态：无。
- 下一步具体文件/方法/命令意图：推进 W20a（`crates/popglot-domain` 模板领域模型与纯编译器）或 W23（产品文档清理与 Spacing 死 Token 处置）。
- 独立复核者与结论：待负责人（slot 01a0987f-61c7-7f62-b8b8-cc512d49d3e3）签收。

### 2026-09-14 W21 快赢波：IME Enter修复/激活零磁盘/主题还原+高对比语义色/A1-A12清扫

- 时间/执行者：2026-09-14 / Pi (teammate, slot 01a09fee-724f)
- 任务和子包：W21 快赢波（依据负责人 2026-09-14 “全部干”授权指令 #01a0a018-67ae-7b43-924b-24461ceec1d6）
- 本次授权范围：仅修改 `apps/PopGlot.Windows/**` 与 `tests/PopGlot.Windows.LogicTests/**`；不碰 `crates/**`，不碰 `.github/`，不改产品文档。
- 状态：READY_FOR_REVIEW
- 逐项实施内容与交付证据：
  1. **N02 IME Enter 失效修复（最高优先）**：
     - 在 `Ui.cs` 中实现 `IsComposing` 附加属性、`AttachCompositionTracker`（监听 `PreviewTextInputStart`/`Update`/`TextInput`/`LostFocus`）与 `IsImeComposing` 统一判定；
     - 彻底拔除三入口（`Sections/TranslateSection.xaml.cs`、`TranslationPanelWindow.xaml.cs`、`QuickSearchWindow.xaml.cs`）对 `InputMethod.GetIsInputMethodEnabled` 的错误调用；
     - 中文 IME 开启时，普通 Enter 正常提交翻译；组词期间 Enter 归输入法确认候选，绝不误触翻译；
     - `LogicTests` 更新并强化断言（测试用例覆盖正在组词不提交与非组词状态必提交双向验证）。
  2. **主窗激活零磁盘 I/O（C11 逐字违约点修复）**：
     - `ShellSettings.cs` 中 `ShellSettingsStore` 引入带文件写入时间戳（`GetLastWriteTimeUtc`）校验的线程安全内存缓存（`_cachedSettings`），稳态下 `ShellSettingsStore.Load()` 零磁盘消耗，同时保证外部直写/删除可感知；
     - `MainWindow.xaml.cs` 的 `RefreshEngineStatusOnActivated` 接入 `Interlocked` 防重入门，并在后台线程 `Task.Run` 中异步执行 `CredentialStore.HasApiKey` (CredRead)、`ProfileManager.Load()` 与 `ShellSettingsStore.Load()`，主线程激活路径彻底达成零磁盘、零 Win32 CredRead。
  3. **主题还原（UI01 / 对账G）**：
     - `SettingsWindow.xaml.cs` 在 `LoadAll()`、`Revert_Click` 以及未保存关闭（`OnClosed`）时，自动将应用主题重置回 `_shellSettings.Theme` 基线主题，彻底消除即时预览后取消/关闭遗留脏主题的缺陷。
  4. **高对比模式语义色映射（C18 / A10）**：
     - `ThemeService.ApplyHighContrastOverrides` 全面扩充：DangerBrush、WarningBrush、SuccessBrush 映射为系统高对比警示色彩（`HotTrackColor`、`HighlightColor`、`HighlightTextColor`）；所有 Soft 填充底色（`DangerSoftBrush`、`WarningSoftBrush`、`SuccessSoftBrush`、`AccentSoftBrush`）及 `SurfaceHoverBrush` 收敛为 `WindowColor`，消除低对比光晕；
     - `LogicTests` 增加 `HighContrastOverridesMapSemanticColors` 自动化测试。
  5. **对账C A1–A12 小缺口清扫**：
     - **A1**: `ShellSettings` 新增 `CloseMainWindowToTray` 属性（默认 true），`GeneralSection.xaml(.cs)` 增加「关闭主窗口时最小化到托盘」设置项；`MainWindow.OnClosing` 在该项为 false 时通过 `RequestExit` 回调直接退出应用；
     - **A2**: `ServicesSection.xaml` 顶部「添加引擎」按钮命名为 `AddEngineHeaderButton`，在编辑态（`ShowEditorForm`）自动隐藏，在列表概览态（`ShowOverview`）恢复显示，消除同屏双 PrimaryButton 冲突；
     - **A3**: `ServicesSection.xaml.cs` 中 `TestConnection_Click` 测试结果与失败说明追加当前时间戳（`DateTime.Now:HH:mm:ss`）；
     - **A4**: `TranslateSection.xaml` 空态区新增快捷键三入口指引 `ShortcutEntriesHint`，动态展示实际配置的划词与截图快捷键；
     - **A5**: `TranslateSection.xaml` 顶栏移除「清空」GhostButton，在原文底栏操作区新增「清空原文」图标按钮（对齐浮窗操作规范）；
     - **A6**: `GeneralSection.xaml` 主题配置行提示文案补充「（即时预览，保存后持久生效）」；
     - **A7**: `ThemeService.cs:241` 注释中关于 Accent 的描述纠正为 `Accent (blue-purple / 蓝紫)`；
     - **A9/A11 (C#侧)**: `TranslationPanelWindow.FriendlyError` 扩充 500/502/503/504/服务不可用/服务器错误与坏响应/JSON反序列化失败的友好文案映射。
- 边界遵从：未修改 `crates/**`，未碰 `.github/`，未改动产品文档。
- 验证命令与结果：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -o artifacts/w21-final/app`: 0 警告，0 错误，exit code 0；
  - `dotnet build tests/PopGlot.Windows.PureTests/PopGlot.Windows.PureTests.csproj -c Release -o artifacts/w21-final/pure`: 0 警告，0 错误，exit code 0；
  - `dotnet build tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj -c Release -o artifacts/w21-final/logic`: 0 警告，0 错误，exit code 0；
  - `./artifacts/w21-final/pure/PopGlot.Windows.PureTests.exe`: 18 passed, 0 failed, exit code 0；
  - `./artifacts/w21-final/logic/PopGlot.Windows.LogicTests.exe`: 195 passed, 0 failed, exit code 0（套件总数从 192 项扩充至 195 项）。
- 独立复核者与结论：待负责人（slot 01a0987f-61c7-7f62-b8b8-cc512d49d3e3）签收。

### 2026-09-14 W30 实现波：N01 多会话仓与接续（按 W24 设计落实现实）

- 时间/执行者：2026-09-14 / Pi (teammate, slot 01a09fee-724f)
- 任务和子包：W30 实现波（依据负责人 2026-09-14 指令 #01a0a053-10cc-72a1-a4f1-62e468c4600f 与 W24 设计文档 `docs/design-2026-09-14/N01-session-store.md`）
- 本次授权范围：仅修改 `apps/PopGlot.Windows/**` 与测试工程；不碰 `crates/**`，不碰 `.github/`。
- 状态：READY_FOR_REVIEW
- 逐项实施内容与交付证据：
  1. **会话仓核心（`apps/PopGlot.Windows/Services/SessionStore.cs`）**：
     - 实现 `SessionStore`（`ISessionStore`）与 `StoredSession` 领域模型，硬限制最大 5 条会话、2 MiB 累计文本上限、30 分钟无访问 TTL 惰性淘汰，内部采用 `LinkedList<StoredSession>` + `Dictionary` 双索引 LRU 机制；
     - 严格遵守 V2/G05 红线：会话模型仅持有不可变纯文本与元数据（SessionId、Origin、语言对、结果、解析、时间戳、字节统计），严禁挂接任何 `byte[]` 或 `BitmapSource` 图像句柄；
     - 纯内存模型，退出即释放，不向磁盘持久化泄漏任何暂存碎片。
  2. **浮窗/查词与仓接缝及零重发恢复契约（Zero-Resend Contract）**：
     - `TranslationPanelWindow` 增加 `CreateSessionSnapshot()` 与 `RestoreSession(StoredSession)`：在面板关闭（`OnClosed`）、用户主动隐藏（`CloseAsUserIntent`）或被新任务替换销毁（`App.DestroyActivePanel`）时，自动向 `SharedSessionStore` 压入当前会话快照；
     - `RestoreSession` 恢复已有原文、译文、解析与 partial 阶段标记，直接设置 `_gate.OnCompleted` 或 `_gate.OnCancelled`，状态栏显示「已恢复未完成内容（未重发）」或「已恢复最近翻译（未重发）」，**保证 0 次新增网络请求**；
     - `QuickSearchWindow` 在关闭清理时提取非空输入及结果，入仓供接续。
  3. **接续与恢复 UI 入口**：
     - `App.xaml.cs` 托盘菜单新增「暂存会话仓 (最多5条)」二级菜单，动态列出最近暂存会话摘要（来源/时间戳/内容预览），点击任意一条精准拉起并恢复展示，并附带「清空会话仓」入口；`RestoreRecentSurface()` 在无存活窗口时无缝降级从 `SharedSessionStore.PopRecent()` 恢复；
     - `TranslateSection.xaml(.cs)` 工作台顶部新增「恢复暂存」按钮，支持弹出最近暂存菜单并载入；若工作台已有输入，先将其保全进会话仓再换入，杜绝草稿误丢；
     - `DataSection.xaml.cs`「清空历史记录」时同步清空会话仓，不留隐私死角；应用退出（`ExitApplication` / `OnExit`）显式调用 `SharedSessionStore.Clear()`。
  4. **全套自动化测试覆盖**：
     - `PureTests` 增加 4 项纯逻辑契约测试：5 条容量上限与 LRU 淘汰、2 MiB 字节预算硬拦截与淘汰、30 分钟 TTL 自动淘汰、StoredSession 零图像引用反射审查；
     - `LogicTests` 增加 2 项集成契约测试：LogicTests 宿主环境下的容量与 TTL 验证、浮窗 RestoreSession 零网络请求验证（断言 `BlockedPublicSends == 0`）。
- 边界遵从：未修改 `crates/**`，未碰 `.github/`，未改动非台账文档。
- 验证命令与结果：
  - `dotnet build apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -o artifacts/w30-final/app`: 0 警告，0 错误，exit code 0；
  - `dotnet build tests/PopGlot.Windows.PureTests/PopGlot.Windows.PureTests.csproj -c Release -o artifacts/w30-final/pure`: 0 警告，0 错误，exit code 0；
  - `dotnet build tests/PopGlot.Windows.LogicTests/PopGlot.Windows.LogicTests.csproj -c Release -o artifacts/w30-final/logic`: 0 警告，0 错误，exit code 0；
  - `./artifacts/w30-final/pure/PopGlot.Windows.PureTests.exe`: 22 passed, 0 failed, exit code 0（套件从 18 项扩充至 22 项）；
  - `./artifacts/w30-final/logic/PopGlot.Windows.LogicTests.exe`: 197 passed, 0 failed, exit code 0（套件从 195 项扩充至 197 项）。
- 独立复核者与结论：待负责人（slot 01a0987f-61c7-7f62-b8b8-cc512d49d3e3）签收。

### 2026-09-15 文档收口波：产品文档与对账报告按最终事实刷新（仅文档，无代码）

- 时间/执行者：2026-09-15 / 文档收口代理（负责人「全部干」授权范围内 W23 文档清理的延续）
- 任务和子包：README、PRODUCT_SPEC、CHANGELOG、docs/VERSIONING、docs/CONFIGURATION_MIGRATION、对账A（C00-C07）、对账B（C08-C12）、对账H（P01-P08-and-others）、tests/TEST-RESOURCE-CLASSIFICATION 按当前最终事实刷新；**本台账仅追加本条**，不改动任何历史记录。
- 本次授权范围：仅修改上列 10 个 Markdown 文件；不改代码、不改测试、不跑 GUI、不做 git 写操作（测试实跑与只读探查除外）。
- 状态：READY_FOR_REVIEW
- 构建身份：当前工作树（未提交，含 Prompt 0.1.6 候选/W21/W30 改动）；版本号保持 0.1.5 未提升。
- 读取的规格与依赖证据：
  - Prompt 闭环代码证据：`crates/popglot-domain/src/prompt.rs`、`crates/popglot-core/src/prompt_store.rs`、`crates/popglot-core/src/provider.rs`（preference/template 快照注入）、`crates/popglot-ffi/src/lib.rs`（popglot_*_prompt_* 与 resolve_active_preference）、`apps/PopGlot.Windows/Sections/PromptSection.xaml(.cs)`、`CoreBridge.cs`（PromptAnchorSnapshot/CompilePromptPreview）、`Services/TranslationModels.cs`（PromptTemplateIdentity，绝不携带指令正文）、`HistoryStore.cs`（PromptTemplateId/Name/Revision）、`SettingsWindow.xaml`（翻译与提示词导航）。
  - C09：`artifacts/perf/startup.json`（2026-09-14T14:31:59Z，30/30，P50=373/P95=383 PASS；WS 103.7 MiB < 120 MiB 硬门，空闲 CPU 0%）——dirty diff 为 2026-09-14 时点，**非最终树**；`machine` 块**缺 CPU 型号**；`startup-failure.json` 为 manifest 自排除修复前的正确拦截。
  - ShellSettings 默认值：`ClosePanelOnFocusLoss=false`（HEAD 9e4832a 即已如此）；ProfileManager schema v7（AllowLanEndpoints，缺席=false）。
  - verify/CI：`scripts/verify.ps1` 与 `.github/workflows/ci.yml` 均已含 PureTests + LogicTests。
- 根因/设计依据：Prompt 0.1.6 闭环实现后产品文档仍停留在 0.1.5 事实（CONFIGURATION_MIGRATION 还写 schema 6 与 ClosePanelOnFocusLoss=true、对账报告写 P 链全未启动、verify 只跑 Logic 的描述过时）；需在不谎称发布的前提下把文档对齐到当前事实。
- 目标与非目标：目标=文档与最终事实一致（未发布内容如实标注候选、未实现/排除项明示、测试数字动态口径）；非目标=提升版本号、修改任何代码/测试、改写对账报告的历史判定正文。
- 改动文件和用户原有改动的保留方式：仅上列 10 个 Markdown；对账报告以「事后追加节」方式补充（C00-C07 §6、C08-C12 §6、P01-P08 §四），原文不篡改。
- 验证命令、exit code、原始输出/证据路径：2026-09-15 直接实跑（Release，实例未运行）——`PopGlot.Windows.LogicTests.exe` → **203 passed, 0 failed**（含 real user config unchanged / no unsanctioned public network send PASS）；`PopGlot.Windows.PureTests.exe` → **23 passed, 0 failed, 0 send attempts refused**；`cargo test --workspace --locked` → **208 passed, 0 failed**（core 78、domain 55、ffi 14、prompt_contract 14、provider_http 30、benchmark_safety 12、smoke 5）。数字随新增测试变化，对外口径一律以实跑为准。
- 逐条验收：
  1. CHANGELOG 顶部新增「未发布（Unreleased）」0.1.6 候选节（明确版本未提升、未打 tag，不冒充发布）：通过。
  2. README 版本行/核心能力/verify 命令/配置目录按候选事实更新：通过。
  3. PRODUCT_SPEC 新增 Prompt 风格模板节（0.1.6 候选）与「当前未实现与明确排除」清单；验收计数改为动态口径（2026-09-15 实跑 Logic 203 / Pure 23）：通过。
  4. VERSIONING 注明 0.1.6 候选在树、版本未提升：通过。
  5. CONFIGURATION_MIGRATION 修正 schema 6→7（补 v6→v7 局域网许可迁移）、ClosePanelOnFocusLoss 默认 true→false、新增 prompt-templates.json 存储节：通过。
  6. 对账A/B/H 追加 2026-09-15 事实更新节（D7 前提被超越；C09 artifact 非最终树+缺 CPU 型号+G12/G13 已修；P01/P02/P03/P05 已实现、P04/P06/P07 部分、P08 未实施；C1/B2/E1/E2/E3 已闭合）：通过。
  7. TEST-RESOURCE-CLASSIFICATION「未验证共享包」节刷新为当前验证状态（含重复编号修复）：通过。
- 隐私、取消、持久化、兼容和 UI 检查：纯文档改动，零运行时行为变化；未写入任何密钥、截图或用户文本。
- 失败原因/已尝试方法/所需输入：无。
- 新想法：仅提案，未实施——C09 测量报告建议在 `measure-core.psm1` machine 块补 CPU 型号（如 `(Get-CimInstance Win32_Processor).Name`），最终树复测时一并闭合 G7。
- 尚未保存/临时状态：无。
- 下一步具体文件/方法/命令意图：负责人签收后，可按 VERSIONING 清单执行 0.1.6 发布（提升 csproj/Cargo.toml、CHANGELOG 更名候选节、四方一致性检查）；最终树复测 C09（`scripts/run-c09-when-free.ps1`）前先补 CPU 型号字段。
- 独立复核者与结论：待负责人签收。

### 2026-09-15 C09 最终代码树正式复测 + 四文档同步（证据登记；无代码改动）

- 时间/执行者：2026-09-15（复测 startedUtc 2026-09-15T18:23:57Z）/ 文档同步代理（负责人「全部干」授权范围内）。
- 任务和子包：C09 最终代码树正式复测证据登记 + CHANGELOG / PRODUCT_SPEC / 对账B（docs/gap-analysis-2026-09-14/C08-C12.md §7）/ 本台账四处文档同步。本台账除本条、§2 C09 行按 §1「当前表随证据更新」规则刷新、§3 增加同日检查点外，不改任何历史记录。
- 本次授权范围：仅修改 CHANGELOG.md、PRODUCT_SPEC.md、docs/gap-analysis-2026-09-14/C08-C12.md、docs/review-2026-09-12/EXECUTION-LEDGER.md；不改代码、不改测试、不做 git 写操作、不声称发布。
- 状态：READY_FOR_REVIEW（C09 复测证据已附，待独立复核）。
- 构建身份：git `9e4832a66110b3b80b93efde6b2576eff375f223` + dirtyDiffHash `a9dda31588861ddc`（最终代码树）；发布包 artifactFileVersion 0.1.5.0——版本号未提升、未打 tag、未发布。
- 复测证据（`artifacts/perf/startup.json`，verdict=PASS）：
  - 30/30 成功、0 失败（另含 2 次不计入预热）；P50=383 ms / P95=402 ms / min 374 / max 407，600/1200 ms 预算内 PASS；
  - 内存阶段：WS=103.53 MiB（108,556,288 字节）——低于 120 MiB 发布硬门槛（脚本按硬门判定 PASS），高于 80 MiB 软目标，**软目标未达，列开口**；Private Bytes=57.4 MiB；归一化空闲 CPU=0%（PASS）；
  - `machine.cpuModel` 已记录：AMD Ryzen 9 7945HX with Radeon Graphics；
  - 测量夹具 `scripts/measure-fixture.ps1` 实跑 41/41 断言通过（与复测同批，2026-09-16 02:22–02:25 本地）。
- 对旧时点陈述的同步修正（旧文本保留不改，仅在此声明取代关系）：
  - 对账B §4 G5/G6（真实测量未执行、门槛从未验证）与 §6「artifact 非最终树」「待复测后才可宣称」——已由本次最终树复测闭合/取代；
  - 对账B §4 G7 与 §6「报告缺 CPU 型号」——本次报告 `machine.cpuModel` 已记录，G7 闭合；
  - 台账 §2 C09 行原「真实 30 次测量从未执行，缺少 startup.json」——旧时点陈述，该行已随证据更新；
  - CHANGELOG Unreleased 原「最终代码树复测仍列验证待办」——旧时点陈述，已在候选节内更新为复测结论。
- 当前结论：C09 取证任务在最终代码树上 PASS（启动延迟、空闲 CPU、内存硬门全过）；80 MiB 空闲 WS 软目标未达为已知开口，不构成硬门失败；C09 状态升为 READY_FOR_REVIEW，DONE_VERIFIED 须独立复核签收后才能标注。
- **Esc IME 诚实修正（不得写已闭环）**：浮窗 `Esc` 路径当前无输入法组词判定（`Ui.IsImeComposing` 仅接线于 Enter 提交路径，`TranslationPanelWindow.OnPreviewKeyDown` 的 Esc 分支只让上下文菜单/下拉先行），组词期间按 `Esc` 会直接进入取消/隐藏链路；该行为仍待真实 E3 复核与代码窗口级修复。§2 C05 行「Esc 先菜单/IME 后取消」中的「IME」环节按当前代码事实仅为菜单/下拉先行，IME 组词防护未接线，D1 验证债继续挂账。
- 未声称事项：未发布、未提升版本号、未打 tag、未做商业发布验收（C08/C22/C24/C26 状态不变）。
- 隐私、取消、持久化、兼容和 UI 检查：纯文档与测量产物登记，零运行时行为变化；未写入任何密钥、截图或用户文本。
- 失败原因/已尝试方法/所需输入：无。
- 新想法：仅提案，未实施——80 MiB 软目标缺口（103.53 MiB）可列专项评估（启动路径程序集裁剪/延迟加载）；Esc IME 窗口级修复建议复用 `Ui.IsImeComposing` 接入 Esc 分支并配 LogicTests 断言，真实行为仍归 E3。
- 尚未保存/临时状态：无。
- 下一步具体文件/方法/命令意图：独立复核签收 C09 复测证据（`artifacts/perf/startup.json`）；Esc IME 修复排期（`TranslationPanelWindow.xaml.cs` OnPreviewKeyDown + `Ui.IsImeComposing`）与 E3 失焦矩阵一并验证；80 MiB 软目标缺口评估立项与否由负责人决定。
- 独立复核者与结论：待负责人签收。
