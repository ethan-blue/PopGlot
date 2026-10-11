# 独立复查报告（PopGlot UI-WAKE-AI-CONNECTION-PLAN §13.3）

复查者：只读 code-reviewer 子代理（独立于实施者），2026-10-11 完成静态复查 + 工件核对。
基线：HEAD 96a7fd6 + 未提交工作区。复查方式：重读实际 diff 与未跟踪新文件、核对 artifacts/perf 原始样本；不照抄实施报告。

## 九项结论

| # | 项 | 结论 | 关键证据 |
| --- | --- | --- | --- |
| 1 | 鼠标蓝框/键盘焦点 | **通过** | Controls.xaml:901-918 MultiTrigger 条件化；三平面样式 QuietTextSurface+QuietSurfaceFocus（:1012/:1037/:1100）；旧触发器已删；表单/按钮/开关/单选未动；U02 用真实 PostMessage(VK_TAB)+SendInput |
| 2 | 时间线 | **通过**（Rust 传输阶段未接线，报告已承认） | InteractionTrace.cs:109-112 未知=null；热键/托盘/二次实例起点；JSONL 后台有界默认关；诊断无原文/URL/凭据 |
| 3 | 工作集/历史刷新 | **通过** | Show 前刷新已移除；HistoryStore 8 处/VocabularyStore 14 处赋值全部 bump Revision；trim 默认一致开启 |
| 4 | HTTP 复用 | **通过** | TransportKey 覆盖 ProviderClient::new 全部动态参数；不存凭据；锁不跨 await；5 FFI 入口+test_connection_draft 走池；回环 accept=1/2 对照（transport_pool.rs:199-264） |
| 5 | 执行模型 | **部分不通过 → 复查修复后关闭** | 主文本路径诚实失败不落免费引擎；但截图 OCR→文本阶段（TranslationCoordinator.cs:1296-1324、VisionOcr 二段、回退分支、CoreBridge.TranslateScreenshotViaOcrAsync）缺类型化门——CLI/Unknown 档案可被当 HTTP 档案发送【中危 #1】 |
| 6 | 文本协议/子进程 | **通过**（含低危 #3） | text_job 逐字段复用生产 prompt/保护/装配器；句柄生命周期测试齐备；JsonLineTransport 上限/排空/Dispose 实证；CliRuntimeHost 每次先查 policy |
| 7 | Codex/OMP | **通过（按未放行验收）** | AdapterEnabledFor 恒 false；唯一 spawn 点前置拒绝；spawnCount=0/promptCount=0 负例；无 Claude 绕道 |
| 8 | 连接面板 | **通过** | 打开零扫描；检测仅文件事实；徽标"待检查"；不换引擎 |
| 9 | 运行抽查/工件 | **工件核对通过**；运行抽查由实施者执行（复查者沙箱禁执行），见下"修复后回归" | wake-*.json 样本数 30/30/10/10、0 失败，p50/p95 与报告逐一吻合 |

## 发现的问题与处置

| # | 严重度 | 问题 | 处置 |
| --- | --- | --- | --- |
| 1 | 中 | 截图 OCR 文本阶段缺类型化执行门；CoreBridge.TranslateScreenshotViaOcrAsync 死旁路 | **已修复**：TranslateScreenshotAsync 路由后统一设门（OCR/网络前失败）；TranslateTextAsync 加 CLI"已放行但无分派"守卫；死旁路删除；新增抓老 bug 回归测试（LogicTests "coordinator screenshot with cli text engine fails before any work"：CLI/Unknown 档案 → Failed、OCR=0、HTTP/free 零触发） |
| 2 | 低 | capability CLI 已放行分支无 backend 分派（被硬禁用掩盖） | **已修复**：主文本路径显式守卫（复用 #1 修复）；启用适配器时不会再走 HTTP |
| 3 | 低 | text_job push-after-finish 静默、finish 不出注册表（32 任务后 prepare 永久失败） | **已修复**：finish 即移出注册表（free 变幂等空操作），已完成句柄的 push/finish 得到"未知句柄"错误 |
| 4 | 低 | Codex ResolveInterpreter 死代码返回错误进程 | **已修复**：简化为诚实的 .ps1→codex.js 解析 |
| 5 | 低 | CliDiscovery docstring 声称读版本实际不读 | **已修复**：docstring 如实声明不读版本 |

**报告与代码不符处**：仅注释级（#4/#5，已修）。一处证据缺口：报告 §4"d1 不压缩"列的原始样本被最终包复测覆盖（报告 §11.1 已披露），该数字不能从现存工件复现；d30/d300 配对完整。

## 修复后回归（实施者执行，2026-10-11）

- `cargo test --workspace --locked`：**238 passed / 0 failed**；clippy 0 警告；fmt clean。
- LogicTests exe：**318 passed / 0 failed**（含新增 #1 回归测试）。
- PureTests exe：**47 passed / 0 failed**。
- 复查修复后重建包：commit 96a7fd6 + dirty diff `9c6eb22673dcf05b`（481 文件）。

## 结论

九项中 8 项通过；P05 的中危缺口（#1）经修复+回归测试后关闭。未验证范围（真实账号、旧机器、真机 DPI/IME、Rust 传输阶段数值、真实提供方吞吐）与实施报告 §12 一致，无 mock 冒充。
