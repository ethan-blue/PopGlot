# PopGlot 实施方案设计复查结果

日期：2026-10-11。复查范围：同目录 UI-WAKE-AI-CONNECTION-PLAN.md 与 EXECUTOR-PROMPT.md 的可执行性、当前源码依据、依赖顺序、账号接口与验收边界。此报告是本轮设计自查，不是未来实施后的独立产品验收。

结论：方案可作为实施交接；P00→P04 可先推进。Codex/OMP 适配需达到各自版本、最低权限、出网与上游支持门槛；Claude 订阅复用当前不放行。应用代码和真实产品行为本轮未改、未验收。

| 复查项 | 结果 | 说明 |
| --- | --- | --- |
| 当前基线与工作区范围 | 通过 | HEAD 96a7fd6e5ee19a57704f9120cfd3f69879259374；本轮仅新增本目录文档 |
| 蓝框源码依据 | 通过 | 核对了隐式 TextBox、FlatTextBox、ResultTextBox 与 FlatRichTextBox 的实际触发器 |
| 鼠标与键盘焦点区分 | 通过，设计层 | 不再把 IsKeyboardFocusWithin 解释成键盘来源；选用原生 FocusVisualStyle，保留真机验证 |
| 唤醒归因 | 通过，设计层 | 强制工作集压缩、构造、取词与历史列表刷新分别量测，没有预先宣称根因 |
| 历史读盘表述 | 通过 | HistoryStore.Load 实际复制内存列表；首次构造读盘与每次 UI 重建分开 |
| HTTP 生命周期定位 | 通过 | 已核对 CreateTextStream→StreamTextDraft→CoreBridge→FFI 的每次 new；连接复用收益待测 |
| 时间指标 | 通过，设计层 | 取词原始成本、首 buffer/UI delta、渲染回调、实际 DWM 呈现分开；未知新增字段用 null |
| API 与 CLI 接入模型 | 通过，设计层 | 后端与 HTTP 协议分开；C#/Rust 配置、路由、能力、历史和诊断兼容都有任务 |
| 工具与凭据边界 | 通过，设计层 | 不读 Token；认证归工具；只关闭自有进程；没有把 readOnly/never 当作无工具保证 |
| Codex 与 OMP 版本/API | 未验证，已列门槛 | 官方接口可作为设计来源，实际安装版本/schema/有效隔离仍由实施者验证 |
| Claude 订阅凭据用于产品 | 不通过 | 官方当前限制该路由，方案保留 Anthropic API 入口 |
| 各步骤文件、依赖、验收、失败回退 | 通过 | P00→P10 及回归矩阵、命令、独立复查与报告要求齐全 |
| 文档格式/路径 | 通过 | 代码围栏成对；当前完整文件路径核对；measure-wake 与新接口明确为待新增 |
| 真实 GUI、旧机器、AI 账号、联网性能、Release/升级 | 未验证 | 没有以源代码或历史报告代替这些证据 |

复查中已修正普通档案流式入口的名称为 `TranslationCoordinator.CreateTextStream`；明确现有 HTTP 诊断结构需要为 CLI 扩展，不能填假 HTTP 状态；去掉一处不必要的英文表述。

本轮未运行构建、WPF 回归或真实请求：交付范围是设计文件。文档检查不证明代码片段已编译、建议性能预算已达成或账号路由已工作。

实施完成后按任务书第 13.3 节对实际代码/样本重新复查，另产出 INDEPENDENT-REVIEW.md。本报告不代替该报告。
