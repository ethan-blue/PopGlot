# PopGlot 翻译流式基准评测规范

本文档定义 PopGlot 的流式性能评测标准、Prompt Fixtures 夹具规范、离线基准测试工具与在线评测安全约束。

---

## 1. Prompt Fixtures 夹具体系

Prompt Fixtures 位于 `tests/fixtures/prompts/` 目录下，用于端到端验证翻译流式解析、Token 保护与边界容错能力：

| 夹具文件 | 场景分类 | 核心验证目标 |
| --- | --- | --- |
| `01_prose.json` | 纯自然语言散文 | 基础流式吞吐、多语言表达流畅度 |
| `02_tech_error_stack.json` | 英文报错与调用栈 | 异常名、行号、模块名保护与原因解释分层 |
| `03_code_comments_mixed.json` | 代码与英文注释混排 | 代码逐字符保留，仅翻译注释与文档字符串 |
| `04_markdown_rich_structure.json` | 复杂 Markdown 结构 | 标题、列表、代码块与表格在流式下的结构保全 |
| `05_paths_urls_cli.json` | 路径、URL 与命令参数 | 绝对/相对路径、Query 参数、CLI Flag 遮蔽与还原 |
| `06_glossary_protocol.json` | 专业术语与协议词汇 | 术语一致性与领域特定词汇保留 |
| `07_prompt_injection.json` | 提示词注入防御 | 抵抗输入中包含的越狱与覆盖系统指令尝试 |
| `08_token_protection.json` | 极限 Token 保护 | 高密度占位符还原与单次严格重试机制 |
| `09_delimiter_collision.json` | 分隔符冲突防御 | 用户输入中包含伪造分隔符时的抗冲突能力 |
| `10_vision_transcription.json` | 视觉转录与直译 | 模拟 OCR/图片结构化转录文本流式解析 |
| `11_bad_metadata_corrupt_json.json` | 损坏元数据 JSON | 流尾 Trailer 为无效 JSON 时的正文保全降级 |
| `12_missing_metadata_no_delimiter.json` | 缺失分隔符 | 模型未输出分隔符时的正文保全与流截断 |
| `13_incomplete_metadata_cutoff.json` | 元数据早闭截断 | 网络早闭导致 Trailer 不完整时的优雅保全 |

评测时支持通过 `--subset` 选择评测子集：
- `minimal`：仅运行轻量代表性用例，适合快速健康探测；
- `code-mixed`：运行代码与技术报错混合用例；
- `all`：运行全量 13 项基准夹具。

---

## 2. 离线流式基准测试（`stream_benchmark`）

离线基准测试工具位于 `crates/popglot-core/src/bin/stream_benchmark.rs`，基于本地回环（Loopback TCP/HTTP）与内存 Mock 模拟真实的 SSE 流式传输。

### 运行命令

```powershell
# 1. 运行默认离线基准评测（标准 SSE 流回环模拟）
cargo run -p popglot-core --bin stream_benchmark --

# 2. 指定 UTF-8 跨 Chunk 拆分场景、Anthropic 协议与延迟注入
cargo run -p popglot-core --bin stream_benchmark -- --scenario split-utf8 --provider anthropic --iterations 20 --ttft-ms 25 --chunk-interval-ms 5

# 3. 运行全量场景并验证延迟容忍度门限（40ms）
cargo run -p popglot-core --bin stream_benchmark -- --scenario all --validate --tolerance-ms 40

# 4. 输出 JSON 格式度量报告
cargo run -p popglot-core --bin stream_benchmark -- --scenario all --json
```

### 评测场景（Scenarios）
- `default`：标准平滑 SSE 流式传输；
- `split-utf8`：故意在多字节 UTF-8 字符（如中文、Emoji）中间切断 TCP Chunk，检验解码器拼接能力；
- `delay-jitter`：模拟高网络抖动与延迟峰值；
- `burst`：突发流量大 Chunk 推送；
- `all`：遍历全量测试场景。

---

## 3. 指标定义（Metric Definitions）

| 指标 | 名称 | 单位 | 定义与评估意义 |
| --- | --- | --- | --- |
| **TTFT** | Time to First Token | 毫秒 (ms) | 发起请求到接收到首个有效 Text Delta 的耗时。衡量启动响应敏捷度。 |
| **Chunk Interval** | 增量平均间隔 | 毫秒 (ms) | 连续两个流式 Chunk 之间的平均时间间隔。 |
| **Jitter** | 增量间隔抖动 | 毫秒 (ms) | Chunk 到达间隔的标准差。值越小代表流式输出越平滑稳定。 |
| **Throughput** | 流式吞吐率 | 字符/秒 (chars/s) | 接收到的有效译文正文字符总数除以流式传输阶段耗时。 |
| **UTF-8 Integrity** | UTF-8 边界完整性 | 百分比 (%) | 跨 Chunk 截断的多字节字符被无损还原的比率（基线要求 100%）。 |
| **Trailer Recovery** | 元数据恢复率 | 百分比 (%) | 流尾随机 Trailer 分隔符被准确识别并成功解析为 JSON 的比率。 |
| **E2E Latency** | 端到端总耗时 | 毫秒 (ms) | 发起请求到收到完整终态 Envelope 并完成释放的总耗时。 |

---

## 4. 离线基准实测基线数据（2026-08-30）

以下为 Windows 本机 Loopback 环境下，主控实际运行 `stream_benchmark` 获得的权威基线数据摘要：

### 测试环境与配置参数
- **评测日期**：2026-08-30
- **运行环境**：Windows 本机 Loopback（127.0.0.1 TCP / HTTP Mock）
- **测试轮次**：10 次迭代 + 2 次 Warmup 预热
- **注入延迟**：注入 TTFT 30 ms，Chunk 发送间隔 5 ms
- **文本样本**：203 字符多语言测试样本（包含中文、英文、代码块与 Emoji）
- **随机种子与协议**：Seed 42，Prompt v1 (`STREAM_PROMPT_VERSION = 1`)
- **总体判定**：`overall_passed: true`

### 全场景度量数据表

| 测试场景 (Scenario) | TTFT p50 (ms) | TTFT p95 (ms) | Total Latency p50 (ms) | Total Latency p95 (ms) | 解析吞吐 (chars/s) | 验证结论与状态 |
| --- | --- | --- | --- | --- | --- | --- |
| `realistic` | 31.46 | 31.88 | 84.08 | 85.63 | 3840.33 | 通过（平滑 SSE 流与 Trailer 元数据解析） |
| `split_utf8` | 32.77 | 33.19 | 101.13 | 102.21 | 2981.35 | 通过（多字节 UTF-8 跨 Chunk 截断无损还原） |
| `jitter` | 31.21 | 31.56 | 82.15 | 83.00 | 3996.06 | 通过（抗网络抖动与增量波动） |
| `missing_trailer` | 31.31 | 31.52 | 78.45 | 79.54 | 4292.66 | 通过（缺失 Trailer 时正文保全降级，触发预期 Warning） |
| `corrupted_sse` | - | - | - | - | 0.00 | 通过（Success 0, Parse Errors 10，预期故障被正确拦截） |
| `direct assembler` | 30.10 | 30.51 | 81.99 | 82.72 | 3917.41 | 通过（内存纯汇编解析，无网络栈开销） |

> **关键澄清与免责说明**：
> 1. **管道基线性质**：本基线为本地 Loopback 回环网络、固定延迟注入与本地解析/缓冲管道基线，**不代表任何公网云端大模型**的实际网络延迟、并发排队或服务可用性；
> 2. **吞吐率口径 (chars/s)**：表中的 `chars/s` 为 Mock 文本在本地流式传输与解析管道中的字符输送吞吐，**绝非大模型生成的推理速度 (tokens/s)**。

---

## 5. 本地 Mock 限定说明（免责声明）

- **环境限制**：`stream_benchmark` 与自动化测试套件均运行在本地回环网络或内存 Mock 环境中；
- **评估边界**：离线基准测试测得的极低 TTFT（< 10 ms）与高吞吐率，**仅代表 PopGlot 本地流式架构（Rust SSE 解析、FFI Callback 桥接、C# Coordinator 缓冲管道）的管道吞吐与边界处理能力**；
- **非云端承诺**：本地测试数据**绝不代表真实云端大模型（如 OpenAI、Anthropic、Gemini）在公网环境下的实际网络延迟、网关排队耗时或服务可用性**。

---

## 6. 在线基准评测安全与脱敏（`live_provider_bench`）

`live_provider_bench` 用于在真实外部大模型上评测流式表现。由于涉及公网请求与 API 费用，该工具实施了严格的安全门禁：

### 安全门禁机制

1. **默认离线 Dry-Run 模式**：
   - 若未显式提供安全确认开关，工具强制以 Dry-Run 模式运行，仅输出请求参数预览，**不发出任何网络请求，并以 exit code 2 退出**。
2. **双重显式确认开关**：
   - 必须在命令行同时传入 `--live` 与 `--i-understand-cost` 两个开关，且环境变量配置正确时，才允许发起真实网络连接。
3. **命令行严禁传递 API Key**：
   - 命令行参数显式拒绝 `--api-key`，防止密钥泄露在系统进程列表（Task Manager / `ps`）或 Shell 历史记录中；
   - 密钥仅支持通过环境变量临时注入：
     - `POPGLOT_BENCHMARK_API_KEY`（通用评测密钥）
     - 或 Provider 专属变量：`OPENAI_API_KEY`、`ANTHROPIC_API_KEY`、`GEMINI_API_KEY`。
4. **输入字符硬上限**：
   - 评测请求强制施加最大输入字符上限（`--max-chars`，默认 2,000 字符），防止意外构造超长 Prompt 产生高昂账单。
5. **输出严格脱敏**：
   - 控制台摘要与 JSON 输出已做深度脱敏，绝不记录 API Key、鉴权请求头、私有网络地址或未脱敏的用户原文。

### 在线评测运行示例

```powershell
# 1. 安全 Dry-Run 模式（默认行为，退出码为 2）
cargo run --example live_provider_bench -- --subset minimal
# 输出：[DRY-RUN] Safety flags missing. Exiting with exit code 2.

# 2. 真实在线评测（需双重确认开关 + 环境变量密钥）
$env:POPGLOT_BENCHMARK_API_KEY = "sk-xxxxxxxxxxxxxxxxxxxxxxxx"
cargo run --example live_provider_bench -- --live --i-understand-cost --subset minimal --json
```
