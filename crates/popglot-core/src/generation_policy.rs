//! Generation policy: the single place that turns the user's
//! `reasoning_level` plus the resolved output budget into each protocol's
//! native wire controls.
//!
//! ## Integration point (for the main thread)
//!
//! Call [`apply_generation_policy`] in `provider_sdk::prepare` AFTER the SDK
//! body has been built and every SDK-generated reasoning control has been
//! stripped (`reasoning_effort`, `reasoning`, `thinking`,
//! `generationConfig.thinkingConfig`, `output_config`, and all output-cap
//! fields — this module strips them again itself, so the call is idempotent).
//! It replaces both the interim `apply_reasoning_controls` helper and the
//! interim "raise the ceiling above the thinking budget" logic in
//! `provider_sdk.rs`: under this policy the thinking budget always lives
//! INSIDE the resolved total output budget (thinking <= total / 3), never on
//! top of it.
//!
//! `adaptive_output` must be the value already computed by
//! `crate::provider::output_token_limit(request, settings)`. The policy
//! defensively re-clamps it to the explicit `settings.output_tokens` ceiling
//! and to the hard product cap of 65 536 tokens.
//!
//! ## Capability model (conservative, known families only)
//!
//! - **`OpenAI` chat/Responses**: `gpt-5*` (non-chat variants) and the
//!   `o1`/`o3`/`o4` series (minus `o1-mini`/`o1-preview`) take
//!   `reasoning_effort` / `reasoning.effort`, require
//!   `max_completion_tokens` / `max_output_tokens`, and only accept the
//!   default temperature. Regular GPT-4/GPT-3.5 models and unknown gateway
//!   IDs never receive a reasoning control; an explicit level is a
//!   `Configuration` error instead of a silently ignored field.
//! - **GLM (OpenAI-compatible only)**: keeps the historical contract —
//!   switchable models (`glm-4.5` … `glm-5.2`, via
//!   [`crate::provider::glm_thinking_config`]) send
//!   `thinking: {"type": "enabled"|"disabled"}` and never `reasoning_effort`.
//!   `Auto` keeps the legacy default `disabled`.
//! - **Anthropic**: `claude` 4.7+/5+ reject manual budgets — they get
//!   adaptive output with `output_config.effort`. Claude 3.7 and 4.0–4.6 get
//!   a manual `thinking.budget_tokens` validated against the total budget.
//!   Older/non-thinking Claude models send nothing.
//! - **Gemini**: 3.x uses `thinkingLevel` (Flash additionally supports medium; `off` is
//!   rejected clearly — Pro has no medium/off and Flash cannot truly
//!   disable thinking, so we never fake an "off"). 2.5 uses
//!   `thinkingBudget` (Pro cannot be off; Flash disables with 0). Older or
//!   unrecognized Gemini models get no thinking control at all.
//!
//! No level is ever silently compressed or downgraded: an unsupported
//! combination is a `Configuration` error with an actionable message.

use crate::provider::{ProviderError, ProviderErrorKind, glm_thinking_config};
use popglot_domain::{ProviderSettings, ProviderType, ReasoningLevel};
use serde_json::{Value, json};

/// Hard product-wide cap on the total output budget (visible translation +
/// thinking) sent on the wire, regardless of what any adaptive computation
/// produced.
const MAX_TOTAL_OUTPUT_TOKENS: u32 = 65_536;

/// Anthropic's minimum extended-thinking budget; every manual budget this
/// module emits is at least this large.
const MIN_ANTHROPIC_THINKING_BUDGET: u32 = 1_024;

/// Manual thinking budgets for the level names, shared by every protocol
/// that meters thoughts in tokens (Anthropic manual, Gemini 2.5).
const THINKING_BUDGET_LOW: u32 = 1_024;
const THINKING_BUDGET_MEDIUM: u32 = 4_096;
const THINKING_BUDGET_HIGH: u32 = 8_192;

/// Thinking must leave at least this fraction of the total budget for the
/// visible translation: `budget * THINKING_BUDGET_SHARE <= total`.
const THINKING_BUDGET_SHARE: u32 = 3;

/// Applies the generation policy to one prepared request body.
///
/// * `settings` — the effective provider settings for this request (text or
///   dedicated vision provider); `provider_type` selects the wire protocol,
///   `reasoning_level` the requested thinking depth, and `output_tokens` the
///   explicit user ceiling (`0` = adaptive).
/// * `model` — the opaque gateway model ID the body was built for.
/// * `adaptive_output` — the current total output budget as computed by
///   `crate::provider::output_token_limit(request, settings)`; defensively
///   clamped here to `min(explicit, 65_536)`.
/// * `body` — the SDK-generated JSON body with reasoning controls already
///   stripped (stripping is repeated here, so double application is safe).
///
/// # Errors
///
/// Returns [`ProviderError`] with kind [`ProviderErrorKind::Configuration`]
/// when the requested reasoning level is not genuinely supported by the
/// model/protocol combination, or when a manual thinking budget cannot fit
/// inside the total output budget with room for the visible translation.
/// The body may already be partially modified in that case; callers abort
/// the request on error.
pub(crate) fn apply_generation_policy(
    settings: &ProviderSettings,
    model: &str,
    adaptive_output: u32,
    body: &mut Value,
) -> Result<(), ProviderError> {
    if !body.is_object() {
        return Err(configuration_error(
            "请求体不是 JSON 对象，无法应用生成策略。",
        ));
    }
    let total = resolve_total_output_budget(settings, adaptive_output);
    strip_stale_generation_controls(body);
    match settings.provider_type {
        ProviderType::OpenAiCompatible => {
            apply_openai_chat(model, settings.reasoning_level, total, body)
        }
        ProviderType::OpenAiResponses => {
            apply_openai_responses(model, settings.reasoning_level, total, body)
        }
        ProviderType::AnthropicMessages => {
            apply_anthropic(model, settings.reasoning_level, total, body)
        }
        ProviderType::GeminiGenerateContent => {
            apply_gemini(model, settings.reasoning_level, total, body)
        }
    }?;
    Ok(())
}

/// Clamps the adaptive budget to the explicit user ceiling and the hard
/// product cap. The explicit ceiling from `resolve_output_tokens` is already
/// domain-clamped; re-clamping here is defensive so a future change in the
/// adaptive computation can never exceed what the user configured.
fn resolve_total_output_budget(settings: &ProviderSettings, adaptive_output: u32) -> u32 {
    let mut total = adaptive_output.min(MAX_TOTAL_OUTPUT_TOKENS);
    let explicit = popglot_domain::resolve_output_tokens(settings.output_tokens);
    if explicit != 0 {
        total = total.min(explicit);
    }
    total.max(1)
}

/// Removes every generation-related control so exactly one canonical set is
/// written afterwards, no matter what the SDK (or an earlier policy pass)
/// left behind. This makes the policy idempotent and resolves conflicting
/// output-cap fields (`max_tokens` vs `max_completion_tokens` vs
/// `max_output_tokens` vs `generationConfig.maxOutputTokens`) by rebuilding
/// the correct one from scratch.
fn strip_stale_generation_controls(body: &mut Value) {
    if let Some(object) = body.as_object_mut() {
        for key in [
            "reasoning_effort",
            "reasoning",
            "thinking",
            "output_config",
            "max_tokens",
            "max_completion_tokens",
            "max_output_tokens",
        ] {
            object.remove(key);
        }
    }
    if let Some(config) = body
        .get_mut("generationConfig")
        .and_then(Value::as_object_mut)
    {
        config.remove("thinkingConfig");
    }
}

fn configuration_error(message: impl Into<String>) -> ProviderError {
    ProviderError::new(ProviderErrorKind::Configuration, message)
}

fn effort_str(level: ReasoningLevel) -> &'static str {
    match level {
        ReasoningLevel::Low => "low",
        ReasoningLevel::Medium => "medium",
        ReasoningLevel::High => "high",
        ReasoningLevel::Auto | ReasoningLevel::Off => "auto",
    }
}

/// Manual token budget for a named level. `Auto`/`Off` never carry a budget.
const fn manual_budget(level: ReasoningLevel) -> u32 {
    match level {
        ReasoningLevel::Low => THINKING_BUDGET_LOW,
        ReasoningLevel::Medium => THINKING_BUDGET_MEDIUM,
        ReasoningLevel::High => THINKING_BUDGET_HIGH,
        ReasoningLevel::Auto | ReasoningLevel::Off => 0,
    }
}

/// Validates that a manual thinking budget fits inside the total output
/// budget while leaving the visible translation at least two thirds of it.
/// Never compresses the budget or the ceiling silently — the user must raise
/// the output limit or lower the level.
fn ensure_budget_fits(budget: u32, total: u32, model: &str) -> Result<(), ProviderError> {
    debug_assert!(budget >= MIN_ANTHROPIC_THINKING_BUDGET);
    if budget.saturating_mul(THINKING_BUDGET_SHARE) > total {
        return Err(configuration_error(format!(
            "模型 {model} 的思考预算 {budget} 超过总输出预算 {total} 的三分之一：思考必须为可见译文留出足够空间。\
             请将输出上限提高到至少 {}，或降低思考级别；不会自动压缩思考或译文预算。",
            budget * THINKING_BUDGET_SHARE
        )));
    }
    Ok(())
}

/// Sets the only output-cap field the chat-completions protocol should
/// carry. Reasoning models (o-series, gpt-5+) reject `max_tokens` outright,
/// so they get `max_completion_tokens`; everything else keeps the widely
/// compatible `max_tokens`. The stale counterpart was already stripped.
fn set_openai_chat_cap(body: &mut Value, total: u32, reasoning_model: bool) {
    if let Some(object) = body.as_object_mut() {
        let key = if reasoning_model {
            "max_completion_tokens"
        } else {
            "max_tokens"
        };
        object.insert(key.to_owned(), json!(u64::from(total)));
    }
}

/// Known `OpenAI` model families. Conservative: an unrecognized ID is
/// `Unknown` and never receives a reasoning control.
enum OpenAiFamily {
    /// gpt-5+ (non-chat variants) and o1/o3/o4 (minus o1-mini/o1-preview):
    /// accept effort controls, reject `max_tokens` and non-default
    /// temperature.
    ReasoningEffort,
    ReasoningNoEffort,
    /// Chat-tuned gpt-5 variants (e.g. `gpt-5-chat-latest`): ordinary model.
    ChatVariant,
    /// GPT-4/GPT-3.5 generation: no reasoning control.
    Ordinary,
    /// Anything else — custom gateways included.
    Unknown,
}

fn openai_family(model: &str) -> OpenAiFamily {
    let lower = model.to_ascii_lowercase();
    let gpt_major = lower
        .strip_prefix("gpt-")
        .and_then(|rest| rest.split(['.', '-']).next())
        .and_then(|major| major.parse::<u32>().ok());
    if gpt_major.is_some_and(|major| major >= 5) {
        return if lower.contains("chat") {
            OpenAiFamily::ChatVariant
        } else {
            OpenAiFamily::ReasoningEffort
        };
    }
    if lower.starts_with("o1-mini") || lower.starts_with("o1-preview") {
        return OpenAiFamily::ReasoningNoEffort;
    }
    if is_o_series(&lower) {
        return OpenAiFamily::ReasoningEffort;
    }
    if lower.starts_with("gpt-4") || lower.starts_with("gpt-3") {
        return OpenAiFamily::Ordinary;
    }
    OpenAiFamily::Unknown
}

/// `o1`/`o3`/`o4` prefixes accept `reasoning_effort`, except the documented
/// non-reasoning `o1-mini`/`o1-preview`.
fn is_o_series(lower: &str) -> bool {
    let mut segments = lower.split('-');
    let head = segments.next().unwrap_or("");
    if !matches!(head, "o1" | "o3" | "o4") {
        return false;
    }
    !matches!(segments.next(), Some("mini" | "preview") if head == "o1")
}

fn openai_supports_none(model: &str) -> bool {
    let lower = model.to_ascii_lowercase();
    let Some(version) = lower.strip_prefix("gpt-").and_then(|v| v.split('-').next()) else {
        return false;
    };
    let mut parts = version.split('.');
    let major = parts
        .next()
        .and_then(|v| v.parse::<u32>().ok())
        .unwrap_or(0);
    let minor = parts
        .next()
        .and_then(|v| v.parse::<u32>().ok())
        .unwrap_or(0);
    major > 5 || major == 5 && minor >= 1
}

/// `OpenAI` chat-completions protocol (also the protocol most third-party
/// gateways speak).
fn apply_openai_chat(
    model: &str,
    level: ReasoningLevel,
    total: u32,
    body: &mut Value,
) -> Result<(), ProviderError> {
    use ReasoningLevel::{Auto, High, Low, Medium, Off};
    // GLM keeps its historical contract before any OpenAI-family detection:
    // switchable GLM IDs take `thinking`, never `reasoning_effort`, and
    // `Auto` keeps the legacy default (`disabled`) rather than sending
    // nothing.
    if glm_thinking_config(model).is_some() {
        body["thinking"] = match level {
            Auto | Off => json!({"type": "disabled"}),
            Low | Medium | High => json!({"type": "enabled"}),
        };
        set_openai_chat_cap(body, total, false);
        return Ok(());
    }
    match openai_family(model) {
        OpenAiFamily::ReasoningEffort => {
            match level {
                Auto => {}
                Off if openai_supports_none(model) => {
                    body["reasoning_effort"] = json!("none");
                }
                Off => {
                    return Err(configuration_error(format!(
                        "模型 {model} 始终进行推理，不支持关闭思考；请改用 Auto 或 low/medium/high。"
                    )));
                }
                Low | Medium | High => {
                    if let Some(object) = body.as_object_mut() {
                        object.insert(
                            "reasoning_effort".to_owned(),
                            Value::String(effort_str(level).to_owned()),
                        );
                    }
                }
            }
            // Reasoning models only accept the default temperature and the
            // newer completion-cap field.
            if let Some(object) = body.as_object_mut() {
                object.remove("temperature");
            }
            set_openai_chat_cap(body, total, true);
        }
        OpenAiFamily::ReasoningNoEffort => {
            if !matches!(level, Auto) {
                return Err(configuration_error(
                    "这个旧版推理模型不支持设置思考程度，请使用自动。",
                ));
            }
            if let Some(object) = body.as_object_mut() {
                object.remove("temperature");
            }
            set_openai_chat_cap(body, total, true);
        }
        OpenAiFamily::ChatVariant | OpenAiFamily::Ordinary | OpenAiFamily::Unknown => {
            if matches!(level, Low | Medium | High)
                || matches!(level, Off) && matches!(openai_family(model), OpenAiFamily::Unknown)
            {
                return Err(configuration_error(format!(
                    "模型 {model} 不支持思考控制：普通 GPT-4/聊天变体/未知网关不会发送 reasoning_effort，\
                     强塞会被网关拒绝或静默忽略。请改用 Auto，或换用支持推理控制的模型。"
                )));
            }
            // Auto/Off on an ordinary model: no thinking exists to control,
            // so sending nothing is the honest payload (never a fake off).
            set_openai_chat_cap(body, total, false);
        }
    }
    Ok(())
}

/// `OpenAI` Responses protocol: effort nests under `reasoning`, and the output
/// cap field is `max_output_tokens`.
fn apply_openai_responses(
    model: &str,
    level: ReasoningLevel,
    total: u32,
    body: &mut Value,
) -> Result<(), ProviderError> {
    use ReasoningLevel::{Auto, High, Low, Medium, Off};
    match openai_family(model) {
        OpenAiFamily::ReasoningEffort => {
            match level {
                Auto => {}
                Off if openai_supports_none(model) => {
                    body["reasoning"] = json!({"effort":"none"});
                }
                Off => {
                    return Err(configuration_error(format!(
                        "模型 {model} 始终进行推理，不支持关闭思考；请改用 Auto 或 low/medium/high。"
                    )));
                }
                Low | Medium | High => {
                    if let Some(object) = body.as_object_mut() {
                        object.insert("reasoning".to_owned(), json!({"effort": effort_str(level)}));
                    }
                }
            }
            if let Some(object) = body.as_object_mut() {
                object.remove("temperature");
                object.insert("max_output_tokens".to_owned(), json!(u64::from(total)));
            }
        }
        OpenAiFamily::ReasoningNoEffort => {
            if !matches!(level, Auto) {
                return Err(configuration_error(
                    "这个旧版推理模型不支持设置思考程度，请使用自动。",
                ));
            }
            if let Some(object) = body.as_object_mut() {
                object.remove("temperature");
            }
            body["max_output_tokens"] = json!(total);
        }
        OpenAiFamily::ChatVariant | OpenAiFamily::Ordinary | OpenAiFamily::Unknown => {
            if matches!(level, Low | Medium | High)
                || matches!(level, Off) && matches!(openai_family(model), OpenAiFamily::Unknown)
            {
                return Err(configuration_error(format!(
                    "模型 {model} 不支持思考控制：普通 GPT-4/聊天变体/未知网关不会发送 reasoning 参数。\
                     请改用 Auto，或换用支持推理控制的模型。"
                )));
            }
            if let Some(object) = body.as_object_mut() {
                object.insert("max_output_tokens".to_owned(), json!(u64::from(total)));
            }
        }
    }
    Ok(())
}

/// Known Claude families.
enum ClaudeFamily {
    /// 4.7+ and 5+: reject manual `thinking.budget_tokens`; take adaptive
    /// output with `output_config.effort`.
    AdaptiveEffort,
    /// 3.7 and 4.0–4.6: manual `thinking.budget_tokens`.
    ManualBudget,
    /// Pre-3.7 Claude: no thinking support.
    NonThinking,
    /// A `claude-*` ID whose version cannot be parsed conservatively.
    Unrecognized,
}

/// Parses `(major, minor)` from Claude ID shapes: `claude-3-7-sonnet-…`,
/// `claude-sonnet-4-5-…`, `claude-opus-4-1`, `claude-sonnet-5-…`,
/// `claude-4.5`. Date-stamp segments (>= 100) are ignored.
fn claude_version(model: &str) -> Option<(u32, u32)> {
    let lower = model.to_ascii_lowercase();
    let rest = lower.strip_prefix("claude-")?;
    let mut major: Option<u32> = None;
    for token in rest.split('-') {
        if let Some((left, right)) = token.split_once('.') {
            if let (Ok(left), Ok(right)) = (left.parse::<u32>(), right.parse::<u32>()) {
                return Some((left, right));
            }
        } else if let Ok(value) = token.parse::<u32>() {
            if value >= 100 {
                // Date stamps like 20250219 are not version segments.
                continue;
            }
            if major.is_none() {
                major = Some(value);
            } else {
                return Some((major.unwrap_or(0), value));
            }
        }
    }
    major.map(|major| (major, 0))
}

fn claude_family(model: &str) -> ClaudeFamily {
    let Some((major, minor)) = claude_version(model) else {
        return ClaudeFamily::Unrecognized;
    };
    if major >= 5 || (major == 4 && minor >= 7) {
        ClaudeFamily::AdaptiveEffort
    } else if major == 4 || (major == 3 && minor >= 7) {
        ClaudeFamily::ManualBudget
    } else {
        ClaudeFamily::NonThinking
    }
}

/// Anthropic Messages protocol. `max_tokens` is required and caps thinking +
/// visible answer together, so the manual budget is validated against the
/// total instead of raising the ceiling above it.
fn apply_anthropic(
    model: &str,
    level: ReasoningLevel,
    total: u32,
    body: &mut Value,
) -> Result<(), ProviderError> {
    use ReasoningLevel::{Auto, High, Low, Medium, Off};
    match claude_family(model) {
        ClaudeFamily::AdaptiveEffort => match level {
            Off => {
                body["thinking"] = json!({"type":"disabled"});
            }
            Auto => {
                // Thinking is opt-in on Anthropic: omitting every control
                // genuinely leaves it disabled; `Auto` additionally keeps
                // each gateway's own default behavior.
            }
            Low | Medium | High => {
                // 4.7+/5+ reject manual budgets; adaptive output with an
                // effort is their native depth control.
                if let Some(object) = body.as_object_mut() {
                    object.insert(
                        "output_config".to_owned(),
                        json!({"effort": effort_str(level)}),
                    );
                    object.insert("thinking".to_owned(), json!({"type": "adaptive"}));
                    // Extended-thinking requests cannot pin a temperature.
                    object.remove("temperature");
                }
            }
        },
        ClaudeFamily::ManualBudget => match level {
            Auto => {}
            Off => {
                body["thinking"] = json!({"type":"disabled"});
            }
            Low | Medium | High => {
                let budget = manual_budget(level);
                ensure_budget_fits(budget, total, model)?;
                if let Some(object) = body.as_object_mut() {
                    object.insert(
                        "thinking".to_owned(),
                        json!({"type": "enabled", "budget_tokens": budget}),
                    );
                    // Anthropic rejects temperature != 1 with thinking on.
                    object.remove("temperature");
                }
            }
        },
        ClaudeFamily::NonThinking | ClaudeFamily::Unrecognized => {
            if matches!(level, Low | Medium | High)
                || matches!(level, Off)
                    && matches!(claude_family(model), ClaudeFamily::Unrecognized)
            {
                return Err(configuration_error(format!(
                    "模型 {model} 不支持思考控制（旧版或无法识别版本的 Claude 不发送 thinking 字段）。\
                     请改用 Auto，或换用 claude-3.7/4.x 及以上的模型。"
                )));
            }
        }
    }
    if let Some(object) = body.as_object_mut() {
        object.insert("max_tokens".to_owned(), json!(u64::from(total)));
    }
    Ok(())
}

/// Known Gemini families.
enum GeminiFamily {
    /// 3.x: qualitative `thinkingLevel` (low/high). `medium` does not exist
    /// and there is no true off (Flash supports medium but cannot fully disable; Pro has no
    /// off level), so those requests fail clearly instead of faking it.
    V3 { pro: bool },
    /// 2.5: token-metered `thinkingBudget`; Pro cannot be off, Flash can
    /// (budget 0). `maxOutputTokens` includes the thinking budget.
    V25 { pro: bool },
    /// 1.x / 2.0 and earlier: no thinking support.
    Legacy,
    /// Unrecognized `gemini-*` IDs (e.g. `gemini-flash-latest`, exp builds).
    Unrecognized,
}

fn gemini_family(model: &str) -> GeminiFamily {
    let lower = model.to_ascii_lowercase();
    let Some(rest) = lower.strip_prefix("gemini-") else {
        return GeminiFamily::Unrecognized;
    };
    // Anything without "flash" is treated with Pro's stricter rules — the
    // conservative direction for medium/off restrictions.
    let pro = !rest.contains("flash");
    let first = rest.split('-').next().unwrap_or("");
    let version = if let Some((left, right)) = first.split_once('.') {
        match (left.parse::<u32>(), right.parse::<u32>()) {
            (Ok(left), Ok(right)) => Some((left, right)),
            _ => None,
        }
    } else {
        first.parse::<u32>().ok().map(|major| (major, 0))
    };
    match version {
        Some((major, _)) if major >= 3 => GeminiFamily::V3 { pro },
        Some((2, minor)) if minor >= 5 => GeminiFamily::V25 { pro },
        Some((1 | 2, _)) => GeminiFamily::Legacy,
        _ => GeminiFamily::Unrecognized,
    }
}

/// Gemini generateContent protocol. `generationConfig.maxOutputTokens`
/// covers thinking + visible answer, so a manual budget must fit inside the
/// total (never added on top of it).
fn apply_gemini(
    model: &str,
    level: ReasoningLevel,
    total: u32,
    body: &mut Value,
) -> Result<(), ProviderError> {
    use ReasoningLevel::{Auto, High, Low, Medium, Off};
    match gemini_family(model) {
        GeminiFamily::V3 { pro } => {
            if let Some(config) = body
                .get_mut("generationConfig")
                .and_then(Value::as_object_mut)
            {
                config.remove("temperature");
            }
            match level {
                Auto => {}
                Off => {
                    return Err(configuration_error(
                        "Gemini 3 系列无法真正关闭思考（Pro 没有 off 级别，Flash 也不能完全关闭），\
                     不能假装已关闭；请改用 low/high 或 Auto。",
                    ));
                }
                Medium if !pro => {
                    set_gemini_thinking_control(body, json!({"thinkingLevel": "medium"}));
                }
                Medium => {
                    let tier = if pro {
                        "（gemini-3-pro 仅支持 low/high）"
                    } else {
                        ""
                    };
                    return Err(configuration_error(format!(
                        "Gemini 3 不支持 medium 思考级别{tier}：thinkingLevel 仅接受 low/high。"
                    )));
                }
                Low | High => {
                    set_gemini_thinking_control(
                        body,
                        json!({"thinkingLevel": if matches!(level, Low) { "low" } else { "high" }}),
                    );
                }
            }
        }
        GeminiFamily::V25 { pro } => match level {
            Auto => {}
            Off if !pro => {
                // 2.5 Flash (incl. flash-lite) genuinely disables with 0.
                set_gemini_thinking_control(body, json!({"thinkingBudget": 0}));
            }
            Off => {
                return Err(configuration_error(format!(
                    "模型 {model} 无法关闭思考：gemini-2.5-pro 的 thinkingBudget 不支持 0。\
                     请改用 Auto 或降低思考级别。"
                )));
            }
            Low | Medium | High => {
                let budget = manual_budget(level);
                ensure_budget_fits(budget, total, model)?;
                set_gemini_thinking_control(body, json!({"thinkingBudget": budget}));
            }
        },
        GeminiFamily::Legacy | GeminiFamily::Unrecognized => {
            if matches!(level, Low | Medium | High)
                || matches!(level, Off)
                    && matches!(gemini_family(model), GeminiFamily::Unrecognized)
            {
                return Err(configuration_error(format!(
                    "模型 {model} 不支持思考控制（旧版或未知 Gemini 不发送 thinkingConfig）。\
                     请改用 Auto，或换用 gemini-2.5 及以上的模型。"
                )));
            }
            // Off on a model with no thinking: vacuously true, send nothing.
        }
    }
    if let Some(config) = gemini_generation_config(body) {
        config.insert("maxOutputTokens".to_owned(), json!(u64::from(total)));
    }
    Ok(())
}

fn set_gemini_thinking_control(body: &mut Value, control: Value) {
    if let Some(config) = gemini_generation_config(body) {
        config.insert("thinkingConfig".to_owned(), control);
    }
}

/// Returns the `generationConfig` object, creating it when missing. The body
/// was validated as a JSON object by the entry point.
fn gemini_generation_config(body: &mut Value) -> Option<&mut serde_json::Map<String, Value>> {
    if !body.get("generationConfig").is_some_and(Value::is_object) {
        body.as_object_mut()?.insert(
            "generationConfig".to_owned(),
            Value::Object(serde_json::Map::new()),
        );
    }
    body.get_mut("generationConfig")?.as_object_mut()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn settings(
        kind: ProviderType,
        model: &str,
        level: ReasoningLevel,
        output_tokens: u32,
    ) -> ProviderSettings {
        ProviderSettings {
            provider_type: kind,
            text_model: model.to_owned(),
            text_endpoint: "/chat/completions".to_owned(),
            reasoning_level: level,
            output_tokens,
            ..ProviderSettings::default()
        }
    }

    fn openai_body() -> Value {
        json!({
            "model": "m",
            "messages": [{"role": "user", "content": "hi"}],
            "max_tokens": 527,
            "temperature": 0.1,
        })
    }

    fn responses_body() -> Value {
        json!({
            "model": "m",
            "input": [{"role": "user", "content": "hi"}],
            "max_output_tokens": 527,
        })
    }

    fn anthropic_body() -> Value {
        json!({
            "model": "m",
            "messages": [{"role": "user", "content": "hi"}],
            "max_tokens": 527,
        })
    }

    fn gemini_body() -> Value {
        json!({
            "contents": [{"role": "user", "parts": [{"text": "hi"}]}],
            "generationConfig": {"maxOutputTokens": 527, "temperature": 0.1},
        })
    }

    #[test]
    fn total_budget_respects_explicit_cap_and_65536_on_every_protocol() {
        // (a) An explicit user ceiling caps even a bogus adaptive value.
        let explicit = 12_000u32;
        let mut body = openai_body();
        apply_generation_policy(
            &settings(
                ProviderType::OpenAiCompatible,
                "gpt-4o",
                ReasoningLevel::Auto,
                explicit,
            ),
            "gpt-4o",
            99_999,
            &mut body,
        )
        .unwrap();
        assert_eq!(body["max_tokens"].as_u64(), Some(12_000));

        let mut body = anthropic_body();
        apply_generation_policy(
            &settings(
                ProviderType::AnthropicMessages,
                "claude-3-7-sonnet",
                ReasoningLevel::Auto,
                explicit,
            ),
            "claude-3-7-sonnet",
            99_999,
            &mut body,
        )
        .unwrap();
        assert_eq!(body["max_tokens"].as_u64(), Some(12_000));

        let mut body = responses_body();
        apply_generation_policy(
            &settings(
                ProviderType::OpenAiResponses,
                "gpt-4o",
                ReasoningLevel::Auto,
                explicit,
            ),
            "gpt-4o",
            99_999,
            &mut body,
        )
        .unwrap();
        assert_eq!(body["max_output_tokens"].as_u64(), Some(12_000));

        let mut body = gemini_body();
        apply_generation_policy(
            &settings(
                ProviderType::GeminiGenerateContent,
                "gemini-2.5-pro",
                ReasoningLevel::Auto,
                explicit,
            ),
            "gemini-2.5-pro",
            99_999,
            &mut body,
        )
        .unwrap();
        assert_eq!(
            body["generationConfig"]["maxOutputTokens"].as_u64(),
            Some(12_000)
        );

        // (b) Without an explicit ceiling the hard product cap is 65 536.
        let mut body = openai_body();
        apply_generation_policy(
            &settings(
                ProviderType::OpenAiCompatible,
                "gpt-4o",
                ReasoningLevel::Auto,
                0,
            ),
            "gpt-4o",
            70_000,
            &mut body,
        )
        .unwrap();
        assert_eq!(body["max_tokens"].as_u64(), Some(65_536));
    }

    #[test]
    fn openai_reasoning_family_switches_cap_field_and_drops_temperature() {
        for model in ["o3", "o4-mini", "gpt-5", "gpt-5.1", "gpt-5-mini", "o1-pro"] {
            let mut body = openai_body();
            apply_generation_policy(
                &settings(
                    ProviderType::OpenAiCompatible,
                    model,
                    ReasoningLevel::High,
                    0,
                ),
                model,
                2_000,
                &mut body,
            )
            .unwrap();
            assert_eq!(body["reasoning_effort"], "high", "{model}");
            assert_eq!(
                body["max_completion_tokens"].as_u64(),
                Some(2_000),
                "{model}"
            );
            assert!(body.get("max_tokens").is_none(), "{model}");
            assert!(body.get("temperature").is_none(), "{model}");
        }
        // Auto on a reasoning model still gets the protocol-correct cap and
        // temperature handling, but no effort field.
        let mut body = openai_body();
        apply_generation_policy(
            &settings(
                ProviderType::OpenAiCompatible,
                "o3",
                ReasoningLevel::Auto,
                0,
            ),
            "o3",
            2_000,
            &mut body,
        )
        .unwrap();
        assert!(body.get("reasoning_effort").is_none());
        assert_eq!(body["max_completion_tokens"].as_u64(), Some(2_000));
        assert!(body.get("temperature").is_none());

        // Ordinary models keep max_tokens + temperature and never get the
        // completion field.
        let mut body = openai_body();
        apply_generation_policy(
            &settings(
                ProviderType::OpenAiCompatible,
                "gpt-4o",
                ReasoningLevel::Auto,
                0,
            ),
            "gpt-4o",
            2_000,
            &mut body,
        )
        .unwrap();
        assert_eq!(body["max_tokens"].as_u64(), Some(2_000));
        assert!(body.get("max_completion_tokens").is_none());
        assert_eq!(body["temperature"], 0.1);
    }

    #[test]
    fn responses_protocol_uses_max_output_tokens_with_nested_effort() {
        let mut body = responses_body();
        apply_generation_policy(
            &settings(
                ProviderType::OpenAiResponses,
                "gpt-5",
                ReasoningLevel::Medium,
                0,
            ),
            "gpt-5",
            2_000,
            &mut body,
        )
        .unwrap();
        assert_eq!(body["reasoning"]["effort"], "medium");
        assert_eq!(body["max_output_tokens"].as_u64(), Some(2_000));
        assert!(body.get("max_tokens").is_none());
        assert!(body.get("max_completion_tokens").is_none());
        // A stale conflicting cap from a gateway template is removed.
        let mut body = responses_body();
        body["max_tokens"] = json!(99);
        apply_generation_policy(
            &settings(
                ProviderType::OpenAiResponses,
                "gpt-4o",
                ReasoningLevel::Auto,
                0,
            ),
            "gpt-4o",
            2_000,
            &mut body,
        )
        .unwrap();
        assert!(body.get("max_tokens").is_none());
        assert_eq!(body["max_output_tokens"].as_u64(), Some(2_000));
    }

    #[test]
    fn anthropic_manual_budget_is_validated_against_total_budget() {
        // Low fits at exactly budget * 3.
        let mut body = anthropic_body();
        apply_generation_policy(
            &settings(
                ProviderType::AnthropicMessages,
                "claude-3-7-sonnet-20250219",
                ReasoningLevel::Low,
                0,
            ),
            "claude-3-7-sonnet-20250219",
            3_072,
            &mut body,
        )
        .unwrap();
        assert_eq!(body["thinking"]["type"], "enabled");
        assert_eq!(body["thinking"]["budget_tokens"].as_u64(), Some(1_024));
        assert_eq!(body["max_tokens"].as_u64(), Some(3_072));
        assert!(body.get("temperature").is_none());

        // Medium/High on the 4.0–4.6 manual family.
        let mut body = anthropic_body();
        apply_generation_policy(
            &settings(
                ProviderType::AnthropicMessages,
                "claude-opus-4-5",
                ReasoningLevel::Medium,
                0,
            ),
            "claude-opus-4-5",
            12_288,
            &mut body,
        )
        .unwrap();
        assert_eq!(body["thinking"]["budget_tokens"].as_u64(), Some(4_096));
        let mut body = anthropic_body();
        apply_generation_policy(
            &settings(
                ProviderType::AnthropicMessages,
                "claude-sonnet-4-6",
                ReasoningLevel::High,
                0,
            ),
            "claude-sonnet-4-6",
            24_576,
            &mut body,
        )
        .unwrap();
        assert_eq!(body["thinking"]["budget_tokens"].as_u64(), Some(8_192));

        // High cannot squeeze into a 20 000 budget: error, never compressed.
        let mut body = anthropic_body();
        let error = apply_generation_policy(
            &settings(
                ProviderType::AnthropicMessages,
                "claude-3-7-sonnet",
                ReasoningLevel::High,
                0,
            ),
            "claude-3-7-sonnet",
            20_000,
            &mut body,
        )
        .unwrap_err();
        assert_eq!(error.kind, ProviderErrorKind::Configuration);
        assert!(error.message.contains("三分之一"), "{}", error.message);

        // Off/Auto send nothing (thinking is opt-in on Anthropic).
        let mut body = anthropic_body();
        body["temperature"] = json!(0.7);
        apply_generation_policy(
            &settings(
                ProviderType::AnthropicMessages,
                "claude-3-7-sonnet",
                ReasoningLevel::Off,
                0,
            ),
            "claude-3-7-sonnet",
            2_000,
            &mut body,
        )
        .unwrap();
        assert_eq!(body["thinking"]["type"], "disabled");
        assert_eq!(body["temperature"], 0.7, "Off must not touch temperature");
    }

    #[test]
    fn modern_claude_uses_adaptive_effort_and_never_manual_budget() {
        for model in [
            "claude-opus-4-7",
            "claude-sonnet-5",
            "claude-opus-5-20251101",
        ] {
            let mut body = anthropic_body();
            body["thinking"] = json!({"type": "enabled", "budget_tokens": 8_192});
            apply_generation_policy(
                &settings(
                    ProviderType::AnthropicMessages,
                    model,
                    ReasoningLevel::High,
                    0,
                ),
                model,
                9_000,
                &mut body,
            )
            .unwrap();
            // The stale manual control is stripped and replaced by adaptive
            // effort; 4.7+/5+ reject manual budgets outright.
            assert_eq!(body["thinking"]["type"], "adaptive", "{model}");
            assert!(body["thinking"].get("budget_tokens").is_none());
            assert!(body["output_config"].get("type").is_none());
            assert_eq!(body["output_config"]["effort"], "high", "{model}");
            assert_eq!(body["max_tokens"].as_u64(), Some(9_000), "{model}");
        }
        let mut body = anthropic_body();
        apply_generation_policy(
            &settings(
                ProviderType::AnthropicMessages,
                "claude-sonnet-5",
                ReasoningLevel::Low,
                0,
            ),
            "claude-sonnet-5",
            9_000,
            &mut body,
        )
        .unwrap();
        assert_eq!(body["output_config"]["effort"], "low");
    }

    #[test]
    #[allow(clippy::too_many_lines)] // One protocol matrix; assertions share the same body contract.
    fn gemini3_uses_thinking_level_and_rejects_medium_and_off() {
        let mut body = gemini_body();
        apply_generation_policy(
            &settings(
                ProviderType::GeminiGenerateContent,
                "gemini-3-pro",
                ReasoningLevel::Low,
                0,
            ),
            "gemini-3-pro",
            4_000,
            &mut body,
        )
        .unwrap();
        assert_eq!(
            body["generationConfig"]["thinkingConfig"]["thinkingLevel"],
            "low"
        );
        assert!(
            body["generationConfig"]["thinkingConfig"]
                .get("thinkingBudget")
                .is_none(),
            "Gemini 3 must not carry a token budget"
        );
        assert_eq!(
            body["generationConfig"]["maxOutputTokens"].as_u64(),
            Some(4_000)
        );

        let mut body = gemini_body();
        apply_generation_policy(
            &settings(
                ProviderType::GeminiGenerateContent,
                "gemini-3-flash",
                ReasoningLevel::High,
                0,
            ),
            "gemini-3-flash",
            4_000,
            &mut body,
        )
        .unwrap();
        assert_eq!(
            body["generationConfig"]["thinkingConfig"]["thinkingLevel"],
            "high"
        );

        let mut flash_medium = gemini_body();
        apply_generation_policy(
            &settings(
                ProviderType::GeminiGenerateContent,
                "gemini-3-flash",
                ReasoningLevel::Medium,
                0,
            ),
            "gemini-3-flash",
            4000,
            &mut flash_medium,
        )
        .unwrap();
        assert_eq!(
            flash_medium["generationConfig"]["thinkingConfig"]["thinkingLevel"],
            "medium"
        );

        // Pro does not support medium; Flash supports its native medium.
        let mut body = gemini_body();
        let error = apply_generation_policy(
            &settings(
                ProviderType::GeminiGenerateContent,
                "gemini-3-pro",
                ReasoningLevel::Medium,
                0,
            ),
            "gemini-3-pro",
            30_000,
            &mut body,
        )
        .unwrap_err();
        assert_eq!(error.kind, ProviderErrorKind::Configuration);
        assert!(error.message.contains("medium"), "{}", error.message);

        // Off: 3 Pro has no off level and 3 Flash cannot truly disable —
        // never fake an off.
        for model in ["gemini-3-pro", "gemini-3-flash"] {
            let mut body = gemini_body();
            let error = apply_generation_policy(
                &settings(
                    ProviderType::GeminiGenerateContent,
                    model,
                    ReasoningLevel::Off,
                    0,
                ),
                model,
                4_000,
                &mut body,
            )
            .unwrap_err();
            assert_eq!(error.kind, ProviderErrorKind::Configuration);
            assert!(error.message.contains("关闭"), "{}", error.message);
        }

        // Auto keeps the gateway default: no thinkingConfig at all.
        let mut body = gemini_body();
        apply_generation_policy(
            &settings(
                ProviderType::GeminiGenerateContent,
                "gemini-3-pro",
                ReasoningLevel::Auto,
                0,
            ),
            "gemini-3-pro",
            4_000,
            &mut body,
        )
        .unwrap();
        assert!(body["generationConfig"].get("thinkingConfig").is_none());
    }

    #[test]
    fn gemini25_budgets_live_inside_the_total_and_off_follows_tier() {
        let mut body = gemini_body();
        apply_generation_policy(
            &settings(
                ProviderType::GeminiGenerateContent,
                "gemini-2.5-pro",
                ReasoningLevel::High,
                0,
            ),
            "gemini-2.5-pro",
            24_576,
            &mut body,
        )
        .unwrap();
        assert_eq!(
            body["generationConfig"]["thinkingConfig"]["thinkingBudget"].as_u64(),
            Some(8_192)
        );
        // The budget is inside the total, never added on top of it.
        assert_eq!(
            body["generationConfig"]["maxOutputTokens"].as_u64(),
            Some(24_576)
        );

        // Medium needs at least 12 288 total: 12 000 must fail loudly.
        let mut body = gemini_body();
        let error = apply_generation_policy(
            &settings(
                ProviderType::GeminiGenerateContent,
                "gemini-2.5-flash",
                ReasoningLevel::Medium,
                0,
            ),
            "gemini-2.5-flash",
            12_000,
            &mut body,
        )
        .unwrap_err();
        assert!(error.message.contains("三分之一"), "{}", error.message);

        // 2.5 Flash disables with budget 0; 2.5 Pro cannot.
        let mut body = gemini_body();
        apply_generation_policy(
            &settings(
                ProviderType::GeminiGenerateContent,
                "gemini-2.5-flash",
                ReasoningLevel::Off,
                0,
            ),
            "gemini-2.5-flash",
            4_000,
            &mut body,
        )
        .unwrap();
        assert_eq!(
            body["generationConfig"]["thinkingConfig"]["thinkingBudget"].as_u64(),
            Some(0)
        );
        let mut body = gemini_body();
        let error = apply_generation_policy(
            &settings(
                ProviderType::GeminiGenerateContent,
                "gemini-2.5-pro",
                ReasoningLevel::Off,
                0,
            ),
            "gemini-2.5-pro",
            4_000,
            &mut body,
        )
        .unwrap_err();
        assert!(error.message.contains("关闭"), "{}", error.message);
    }

    #[test]
    fn legacy_and_unknown_models_reject_explicit_thinking_clearly() {
        // Ordinary / chat-variant / unknown OpenAI IDs never get effort.
        for (kind, model) in [
            (ProviderType::OpenAiCompatible, "gpt-4o"),
            (ProviderType::OpenAiCompatible, "gpt-4-turbo"),
            (ProviderType::OpenAiCompatible, "gpt-5-chat-latest"),
            (ProviderType::OpenAiCompatible, "o1-mini"),
            (ProviderType::OpenAiCompatible, "custom-gateway-id"),
            (ProviderType::OpenAiResponses, "gpt-4o"),
        ] {
            let mut body = if matches!(kind, ProviderType::OpenAiResponses) {
                responses_body()
            } else {
                openai_body()
            };
            let error = apply_generation_policy(
                &settings(kind, model, ReasoningLevel::Medium, 0),
                model,
                9_000,
                &mut body,
            )
            .unwrap_err();
            assert_eq!(error.kind, ProviderErrorKind::Configuration, "{model}");
            assert!(
                body.get("reasoning_effort").is_none() && body.get("reasoning").is_none(),
                "{model}"
            );
        }
        // Off on ordinary models is honest: no thinking exists to disable.
        let mut body = openai_body();
        apply_generation_policy(
            &settings(
                ProviderType::OpenAiCompatible,
                "gpt-4o",
                ReasoningLevel::Off,
                0,
            ),
            "gpt-4o",
            2_000,
            &mut body,
        )
        .unwrap();
        assert!(body.get("reasoning_effort").is_none() && body.get("thinking").is_none());

        // Old/unrecognized Claude and Gemini models: explicit levels error.
        for (kind, model) in [
            (
                ProviderType::AnthropicMessages,
                "claude-3-5-sonnet-20241022",
            ),
            (ProviderType::AnthropicMessages, "claude-3-opus"),
            (ProviderType::AnthropicMessages, "claude-instant"),
            (ProviderType::GeminiGenerateContent, "gemini-2.0-flash"),
            (ProviderType::GeminiGenerateContent, "gemini-1.5-pro"),
            (ProviderType::GeminiGenerateContent, "gemini-flash-latest"),
        ] {
            let mut body = if matches!(kind, ProviderType::AnthropicMessages) {
                anthropic_body()
            } else {
                gemini_body()
            };
            let error = apply_generation_policy(
                &settings(kind, model, ReasoningLevel::Low, 0),
                model,
                9_000,
                &mut body,
            )
            .unwrap_err();
            assert_eq!(error.kind, ProviderErrorKind::Configuration, "{model}");
        }
        // Off on a legacy non-thinking Gemini sends nothing (vacuously off).
        let mut body = gemini_body();
        apply_generation_policy(
            &settings(
                ProviderType::GeminiGenerateContent,
                "gemini-2.0-flash",
                ReasoningLevel::Off,
                0,
            ),
            "gemini-2.0-flash",
            2_000,
            &mut body,
        )
        .unwrap();
        assert!(body["generationConfig"].get("thinkingConfig").is_none());
    }

    #[test]
    fn glm_legacy_contract_is_preserved() {
        // Auto keeps the historical default: thinking disabled, never an
        // effort string, and the ordinary max_tokens cap field.
        let mut body = openai_body();
        apply_generation_policy(
            &settings(
                ProviderType::OpenAiCompatible,
                "glm-4.6",
                ReasoningLevel::Auto,
                0,
            ),
            "glm-4.6",
            2_000,
            &mut body,
        )
        .unwrap();
        assert_eq!(body["thinking"]["type"], "disabled");
        assert!(body.get("reasoning_effort").is_none());
        assert_eq!(body["max_tokens"].as_u64(), Some(2_000));

        let mut body = openai_body();
        apply_generation_policy(
            &settings(
                ProviderType::OpenAiCompatible,
                "glm-4.6",
                ReasoningLevel::Off,
                0,
            ),
            "glm-4.6",
            2_000,
            &mut body,
        )
        .unwrap();
        assert_eq!(body["thinking"]["type"], "disabled");

        let mut body = openai_body();
        apply_generation_policy(
            &settings(
                ProviderType::OpenAiCompatible,
                "glm-4.6",
                ReasoningLevel::High,
                0,
            ),
            "glm-4.6",
            2_000,
            &mut body,
        )
        .unwrap();
        assert_eq!(body["thinking"]["type"], "enabled");
        assert!(body.get("reasoning_effort").is_none());

        // GLM-5.3 is not switchable (it requires thinking) and must not get
        // a control invented for it.
        let mut body = openai_body();
        let error = apply_generation_policy(
            &settings(
                ProviderType::OpenAiCompatible,
                "glm-5.3",
                ReasoningLevel::High,
                0,
            ),
            "glm-5.3",
            2_000,
            &mut body,
        )
        .unwrap_err();
        assert_eq!(error.kind, ProviderErrorKind::Configuration);
        assert!(body.get("thinking").is_none() && body.get("reasoning_effort").is_none());
    }

    #[test]
    fn stale_generation_controls_are_stripped_idempotently() {
        let mut body = json!({
            "model": "claude-3-7-sonnet",
            "messages": [],
            "max_tokens": 99,
            "max_completion_tokens": 123,
            "reasoning_effort": "high",
            "thinking": {"type": "enabled", "budget_tokens": 65_536},
            "output_config": {"type": "adaptive"},
            "temperature": 0.3,
        });
        apply_generation_policy(
            &settings(
                ProviderType::AnthropicMessages,
                "claude-3-7-sonnet",
                ReasoningLevel::Auto,
                0,
            ),
            "claude-3-7-sonnet",
            2_000,
            &mut body,
        )
        .unwrap();
        assert!(body.get("max_completion_tokens").is_none());
        assert!(body.get("reasoning_effort").is_none());
        assert!(body.get("thinking").is_none());
        assert!(body.get("output_config").is_none());
        assert_eq!(body["max_tokens"].as_u64(), Some(2_000));
        // A second application is a no-op beyond the same result.
        apply_generation_policy(
            &settings(
                ProviderType::AnthropicMessages,
                "claude-3-7-sonnet",
                ReasoningLevel::Auto,
                0,
            ),
            "claude-3-7-sonnet",
            2_000,
            &mut body,
        )
        .unwrap();
        assert_eq!(body["max_tokens"].as_u64(), Some(2_000));
        assert!(body.get("thinking").is_none());
    }

    #[test]
    fn modern_off_legacy_caps_and_gemini_temperature_follow_real_capabilities() {
        let mut body = openai_body();
        apply_generation_policy(
            &settings(
                ProviderType::OpenAiCompatible,
                "gpt-5.2",
                ReasoningLevel::Off,
                4096,
            ),
            "gpt-5.2",
            4096,
            &mut body,
        )
        .unwrap();
        assert_eq!(body["reasoning_effort"], "none");
        let mut legacy = openai_body();
        apply_generation_policy(
            &settings(
                ProviderType::OpenAiCompatible,
                "o1-mini",
                ReasoningLevel::Auto,
                4096,
            ),
            "o1-mini",
            4096,
            &mut legacy,
        )
        .unwrap();
        assert_eq!(legacy["max_completion_tokens"], 4096);
        assert!(legacy.get("temperature").is_none());
        let mut gemini = gemini_body();
        gemini["generationConfig"]["temperature"] = json!(0.1);
        apply_generation_policy(
            &settings(
                ProviderType::GeminiGenerateContent,
                "gemini-3.5-flash-lite",
                ReasoningLevel::Auto,
                4096,
            ),
            "gemini-3.5-flash-lite",
            4096,
            &mut gemini,
        )
        .unwrap();
        assert!(gemini["generationConfig"].get("temperature").is_none());
    }

    #[test]
    fn manual_budgets_always_meet_the_anthropic_minimum() {
        assert!(manual_budget(ReasoningLevel::Low) >= MIN_ANTHROPIC_THINKING_BUDGET);
        assert!(manual_budget(ReasoningLevel::Medium) >= MIN_ANTHROPIC_THINKING_BUDGET);
        assert!(manual_budget(ReasoningLevel::High) >= MIN_ANTHROPIC_THINKING_BUDGET);
    }
}
