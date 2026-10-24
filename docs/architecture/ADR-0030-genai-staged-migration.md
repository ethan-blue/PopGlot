# ADR 0030: 分阶段迁移到 genai 协议适配层

- 状态：四种协议的生产请求构造已接入；固定稳定版 0.7.1，本地协议回放通过；真实网关未验收
- 日期：2026-09-26；升级审查：2026-10-24
- 固定版本：`genai = 0.7.1`（精确 pin，禁用默认 features）

## 结论

使用 `genai` 构造 OpenAI Chat Completions、OpenAI Responses、Anthropic Messages 和 Gemini generateContent 的普通/流式、文字/图片请求体。`TranslationProvider::prepare/prepare_stream` 调用 `provider_sdk::prepare`，不再维护四套重复的协议拼装。传输、认证和保真响应解析继续由 PopGlot 负责，不将 SDK 高层客户端引入生产出网路径。

0.7.1 是此次固定采用的稳定版本，不是旧决策中的 0.7 beta。上游仍未公开脱离传输的 adapter 调用，因此保留最小 vendor 快照，只在 `src/adapter/mod.rs` 添加同步 `prepare_payload` 导出，不修改上游协议实现。来源与差分见 `vendor/genai/POPGLOT-PATCH.md`。

端点和认证来自用户配置，不采用 SDK 生成的地址或认证信息。协议由用户选择明确绑定，模型名不能触发协议切换；非 Gemini 请求恢复原始模型 ID，Gemini 将经路径校验的原始 ID 代入端点。SDK 收到的仅是不会访问的合成地址和占位认证。

## 0.6.5 → 0.7.1 请求边界审查

本次比较两个固定 crates.io 归档中的请求实现，而不是由上游 changelog 推断产品行为。重点为 `openai/adapter_shared.rs`、`openai/cache_policy.rs`、`openai_resp/adapter_impl.rs`、`anthropic/adapter_shared.rs`、`anthropic/ant_reasoning.rs` 和 `gemini/adapter_impl.rs`：

| 产品协议 | 上游请求变化 | PopGlot 的实际影响与约束 |
| --- | --- | --- |
| OpenAI Chat Completions | GPT-6 的预算字段改用 `max_completion_tokens`；GPT-5.6 及以后模型新增默认 `prompt_cache_options`；新增 schema/cache/tool 等构造规则 | 保留原生预算字段；用户选 Chat 就仍发 Chat，不由 GPT-6 名称改选 Responses。产品未配置缓存，删除 SDK 自动注入的 `prompt_cache_options`，避免给同名自定义网关增加参数。未使用 schema、工具或多轮 reasoning 重放。 |
| OpenAI Responses | 新增端点受限的 prompt-cache 策略；`Zero` 仍映射为 wire `none`；schema 与工具序列化调整 | 继续使用 `max_output_tokens`、`store: false` 和现有 instructions/input 结构。合成 SDK 端点不是官方 OpenAI 端点，不启用其端点缓存策略；边界亦统一删除未配置的 cache/reasoning 字段。未启用工具、schema 或 reasoning 捕获。 |
| Anthropic Messages | 按模型能力解析/规范化名称与默认预算；`Zero` 不再进入 adaptive thinking，对部分默认思考模型会生成 `thinking: disabled`；新增请求级 cache 和 `extra_body`，更新 schema 与思考块重放 | 恢复用户原始模型 ID，显式传入 PopGlot 预算，因此默认最大预算变化不影响产品上限。删除 SDK 生成的 thinking；`Zero` 不会产生 `output_config.effort`。产品不设置 cache、extra body、schema 或思考块重放。配置的 `anthropic-version` 仍由 PopGlot 发送。 |
| Gemini generateContent | `Zero` 改为预算 0；schema 原样使用 JSON Schema 字段；新增 seed/extra body，调整工具与 thought signature 配对 | 删除 `generationConfig.thinkingConfig`，不向未知网关发送预算 0。预算仍使用 `maxOutputTokens`，保留现有文字/图片 parts 与 systemInstruction。产品未设置 seed、extra body、JsonSpec 或工具，因此这些新增分支不进入当前请求路径。 |

`ReasoningEffort::Zero` 与 Rust 的 `Option::None` 不等价：前者作为显式 SDK 输入阻止按名称后缀推断，后者可能触发推断。生成后删除 `reasoning_effort`、`reasoning`、`thinking` 和 `generationConfig.thinkingConfig`。这只能保证 PopGlot 不额外发送这些控制字段，**不保证服务端关闭思考、不消耗思考 token，也不证明任何网关支持某个 effort 值**。GLM 既有的显式 `thinking` 产品策略保持不变。

0.7 的 `ClientBuilder::build()` 改为返回 `Result`。旧 `build_bound_client` 没有生产调用，只有一个构造测试；已连同该测试和直接 `reqwest13` 别名依赖删除，不能用未经证明的 `expect("cannot fail")` 掩盖 API 变化。生产仍只有 PopGlot 的 HTTP 客户端，`ProviderClient::new` 将构造错误映射为 `ProviderError` 并通过 `?` 传递。SDK 所需的 reqwest 0.13 仍是传递依赖，但不承担生产传输或 TLS。

SDK 流式解析、usage、签名重放和整段读取方面的上游修复没有进入 PopGlot 的响应路径，因此不能列为此次产品已获得或已验证的能力。

## 保留在 PopGlot 的边界

- 出网许可、私网目标许可和密钥生命周期
- 同源跳转限制、TLS 选择、连接与总超时
- `no_proxy`，禁止系统代理静默改写用户明确配置的服务地址
- 请求体和响应体大小上限
- 取消、有限重试和可重试错误分类
- Markdown/代码保护、翻译元数据协议及流式重组
- 日志脱敏和面向用户的错误文案
- 不因请求失败静默切换协议、SDK、模型或付费服务

## 本次证据与验收边界

2026-10-24 重新取得不可变归档 `https://static.crates.io/crates/genai/genai-0.7.1.crate`，SHA-256 为 `f031d61ad96cd018f8413af948613b9ec94cc0eff1dee670461aa490e933522c`。本地与归档的 **156 个源文件**路径一致，按 LF 规范化比较后只有 `prepare_payload` 导出插入。分发保留全部源码、库 manifest、README、双许可证和 provenance；删去未分发的开发目录、开发 targets/dependencies 及无效 manifest 引用。更新后的 verifier 将对归档哈希、完整文件集合、精确导出差分、最小 manifest、README 和许可证逐项核对。

源码审查扩充了模型 ID、预算与参数边界行为测试；其后的集成验收已实际运行，不再把新增测试视作通过记录。

客户端本地证据：

- 更新后的 `scripts/verify-genai-vendor.ps1` 对上述原始归档核验通过：156 个源码、README、双许可证、最小 manifest 和唯一同步导出差分。
- Release 模式 Rust workspace **248 项通过**，`cargo fmt --all --check` 与 `cargo clippy --workspace --all-targets --locked --release -- -D warnings` 通过。真实 loopback HTTP 覆盖四协议的认证、普通/流式文字与图片、取消、重试、跳转、大小限制和原始空白。
- 实际执行 `stream_benchmark --scenario all --iterations 3 --warmup 1 --json`，四种协议共 24 个场景报告均满足判定；损坏 SSE 保持预期失败，未抬高容差或把错误帧算作成功。
- Windows 隔离宿主实际显示并渲染 56 个双主题窗口场景；Pure 58、Logic 342、CLI（含 UI）29 项通过，真实用户配置未改动，未尝试未授权公网发送。公开发布包启动与 CI 结果另记发布页。

真实服务边界：

- 未读取真实密钥、登录凭据或调用付费模型。本地回放只证明客户端生成和处理给定协议的行为，**不证明提供商对任意模型、端点或字段的实际接受情况，也不代表模型质量验收**。
- 真实四种协议的服务或用户网关仍需有明确授权的人工验收；当前没有此类证据，不声称已完成。客户端本地门禁不能代替该兼容性结论。
- 回滚使用上一版已验证的应用构建；不在失败时自动重发到另一协议或产生另一笔付费请求。

## 不采用

- 不跟随 beta/latest 自动更新，也不在后台下载并替换运行时代码。
- 不把模型列表、namespace 或模型名当作协议选择依据。
- 不用 SDK 客户端的默认代理、认证解析或无界响应读取替代 PopGlot 的安全策略。
