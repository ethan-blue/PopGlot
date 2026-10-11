# PopGlot 后续实施者执行提示词

把下面全文发给实际实施者；不需要重新讨论方案才能开始 P00。

```text
你负责实施 D:\Projects\GitProjects\PopGlot 的蓝框交互、唤醒性能和可选本机 AI 工具连接。

完整任务书：docs/handoff-2026-10-11/UI-WAKE-AI-CONNECTION-PLAN.md。
请先完整阅读任务书，然后检查当前 AGENTS.md/仓库规则、分支、HEAD、git status/diff、进程、架构契约和测试。任务书基线为 96a7fd6e5ee19a57704f9120cfd3f69879259374，当前若有变化必须按实际代码重定位。保留已有改动，不停止用户进程，不自动提交/推送/合并/发布。

按 P00→P10 的依赖执行，先交付 P00→P04，再做 P05→P10。不要把所有问题归因到 AI，不以账号登录替代性能测量。每步按“复现/测量→根因→局部代码修复→负例/回归→证据”完成，更新本目录 IMPLEMENTATION-REPORT.md。

P01 去掉大文本区域鼠标 hover/click 的蓝框，保留真正键盘导航焦点、IME、选择和读屏。不要全局删除选中/错误/按钮状态。采用 WPF 原生 FocusVisualStyle 区分输入来源，普通表单保持原契约。

P02 必须从热键/托盘入口记录首用、热唤醒、取词、发送、首事件、首可见译文与渲染机会。未知为 null，CompositionTarget.Rendering 不是实际 DWM 呈现。P03 对 Hide 后强制工作集压缩做同机 A/B；历史刷新移出首帧且按 Revision 收敛；划词反馈不抢原目标焦点、不重复 Ctrl+C、取消后仍恢复剪贴板。

P04 确认实际生产档案走 draft-stream，修复 FFI 每次创建 ProviderClient 的生命周期。客户端池有上限，锁不跨网络；认证头逐请求，TLS、目标、限额/重试/撤权检查仍逐请求。回环服务证明真实 TCP 复用，先测后改 genai/流式/渲染，不削弱文本保真和保护协议。

P05/P06 将 HTTP 协议与执行 backend 分开，C#/Rust 配置与路由同步迁移，API 旧配置缺字段默认 NativeApi。所有入口经 Coordinator，复用 Rust 的 Prompt/保护/随机分隔符/解析，不在 C# 写第二套提示词。CLI 不假造 API key、HTTP 200、endpoint 或本机推理标签。

P07 Codex 使用安装版本匹配的 app-server schema，认证由 Codex 管理。P08 OMP 使用该版本 RPC 与经过验证的无工具/扩展/MCP配置；上游账号授权逐提供方核实。Claude Code 订阅凭据不作为产品通用后端，保留 Anthropic API 接入。不要扫描或复制浏览器 Cookie、auth.json、agent.db 或其他凭据；不退出原 CLI 账号、不编辑其全局配置。

子进程必须验证实际可执行入口，用 ArgumentList/stdio，不插值 shell；不在真实仓库运行，不把原文放 args/env/log。stdout/stderr 并行消费、有限队列、epoch fence、精确取消、进程所有权、重连不重发、暖进程不同请求不串上下文。最低工具隔离/出网边界无法验证的 adapter 必须禁用，不能用 readOnly/approvalPolicy=never/Prompt 保证“无工具/无出网”。离线状态不启动远程 CLI，暖进程撤权不再提交。只清理 PopGlot 自己创建的子进程。

P09 完整实现检测→连接→登录引导→状态检查→模型→主动测试→保存→选择→断开/过期/限流/取消恢复。仅检测到命令不宣称已登录或请求可用。初版 CLI 仅文字/本机 OCR 后文字，不上传截图。不要自动更换用户当前引擎。

P10 执行适当 PureTests、Rust、LogicTests、完整 verify 和隔离 Release 测量，保留 guard；旧机器和真实 GUI/账号/安装升级各有独立状态。真实服务测试由用户主动在本机执行，额度/联网性能重复测试需要单独明确授权；本任务没有默认许可读取真实凭据或调用收费服务。

每步报告修改文件、代码设计、验证命令和结果、失败恢复、未验证项。通过/不通过/未验证分开；缺外部条件时继续独立可做工作，不把受阻步骤标完成。完成后冻结代码与产物身份，让独立复查者重新读 diff/原始样本并执行任务书第 13.3 节，输出 INDEPENDENT-REVIEW.md。不要用本轮设计复查代替实施验收。

最终交付实际代码/patch、实施报告、性能样本/摘要、当前截图、配置与安全负例、CLI 支持矩阵、独立复查结果和明确剩余项。未经用户授权不对外发送、不发布、不创建其他聊天执行此任务。
```
