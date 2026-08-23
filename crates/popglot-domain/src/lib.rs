//! Platform-neutral domain types and decisions for `PopGlot`.
//!
//! This crate deliberately contains no WPF, Win32, platform path, credential,
//! capture, or tray dependencies. Every shell communicates through these DTOs.

use regex::Regex;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::sync::LazyLock;

/// User-selected translation pipeline.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub enum TranslationMode {
    #[default]
    Auto,
    LocalOcr,
    VisionDirect,
}

/// Persisted non-secret provider settings.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
// These are independent user permissions/capabilities, not mutually exclusive states.
#[allow(clippy::struct_excessive_bools)]
pub struct ProviderSettings {
    pub schema_version: u32,
    pub provider_type: ProviderType,
    pub api_base_url: String,
    pub text_endpoint: String,
    pub vision_endpoint: String,
    pub text_model: String,
    pub vision_model: String,
    pub extra_headers: BTreeMap<String, String>,
    pub anthropic_version: String,
    pub supports_text: bool,
    pub supports_vision: bool,
    pub network_enabled: bool,
    pub mode: TranslationMode,
    pub allow_image_upload_in_auto: bool,
    pub safe_dev_mode: bool,
    /// Opt-in for private relays reached by bare IP or self-signed TLS.
    pub allow_insecure_tls: bool,
    pub api_key_configured: bool,
}

impl Default for ProviderSettings {
    fn default() -> Self {
        Self {
            schema_version: 2,
            provider_type: ProviderType::OpenAiCompatible,
            api_base_url: "https://api.openai.com/v1".to_owned(),
            text_endpoint: "/chat/completions".to_owned(),
            vision_endpoint: "/chat/completions".to_owned(),
            text_model: "gpt-4o-mini".to_owned(),
            vision_model: "gpt-4o-mini".to_owned(),
            extra_headers: BTreeMap::new(),
            anthropic_version: "2023-06-01".to_owned(),
            supports_text: true,
            supports_vision: true,
            network_enabled: true,
            mode: TranslationMode::Auto,
            allow_image_upload_in_auto: true,
            safe_dev_mode: false,
            allow_insecure_tls: false,
            api_key_configured: false,
        }
    }
}

impl ProviderSettings {
    #[must_use]
    pub fn vision_is_configured(&self) -> bool {
        self.supports_vision && !self.vision_model.trim().is_empty()
    }

    #[must_use]
    pub fn text_is_configured(&self) -> bool {
        self.supports_text && !self.text_model.trim().is_empty()
    }
}

/// Wire protocol used by the active model provider.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub enum ProviderType {
    #[default]
    OpenAiCompatible,
    OpenAiResponses,
    AnthropicMessages,
    GeminiGenerateContent,
}

impl ProviderType {
    #[must_use]
    pub fn default_base_url(self) -> &'static str {
        match self {
            Self::OpenAiCompatible | Self::OpenAiResponses => "https://api.openai.com/v1",
            Self::AnthropicMessages => "https://api.anthropic.com",
            Self::GeminiGenerateContent => "https://generativelanguage.googleapis.com",
        }
    }

    #[must_use]
    pub fn default_endpoint(self) -> &'static str {
        match self {
            Self::OpenAiCompatible => "/chat/completions",
            Self::OpenAiResponses => "/responses",
            Self::AnthropicMessages => "/v1/messages",
            Self::GeminiGenerateContent => "/v1beta/models/{model}:generateContent",
        }
    }
}

/// Observable inputs to automatic routing. No opaque model-side decision is used.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
// These flags are independent observations from capture/OCR, not mutually
// exclusive states. Named booleans keep the routing contract direct.
#[allow(clippy::struct_excessive_bools)]
pub struct RoutingContext {
    pub requested_mode: TranslationMode,
    pub vision_configured: bool,
    pub image_upload_allowed: bool,
    pub looks_like_code: bool,
    pub complex_layout: bool,
    pub image_quality: f32,
    pub ocr_confidence: f32,
}

/// Route selected for a translation request.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RoutingDecision {
    pub selected_mode: TranslationMode,
    pub reason_code: String,
    pub explanation_zh: String,
    pub may_upload_image: bool,
}

#[must_use]
pub fn select_route(context: &RoutingContext) -> RoutingDecision {
    match context.requested_mode {
        TranslationMode::LocalOcr => local_decision(
            "forced_local_ocr",
            "已按设置使用本地 OCR；截图不会上传给视觉模型。",
        ),
        TranslationMode::VisionDirect => {
            if !context.vision_configured {
                local_decision(
                    "vision_not_configured",
                    "视觉模型尚未配置，已安全回退到本地 OCR 与文本模型。",
                )
            } else if !context.image_upload_allowed {
                local_decision(
                    "image_upload_not_allowed",
                    "当前隐私设置不允许上传截图，已安全回退到本地 OCR。",
                )
            } else {
                vision_decision("forced_vision", "已按设置使用视觉模型直接识别并翻译截图。")
            }
        }
        TranslationMode::Auto => select_auto_route(context),
    }
}

fn select_auto_route(context: &RoutingContext) -> RoutingDecision {
    if !context.vision_configured {
        return local_decision(
            "auto_no_vision_model",
            "未配置视觉模型，自动模式使用本地 OCR 与文本模型。",
        );
    }
    if !context.image_upload_allowed {
        return local_decision(
            "auto_upload_disabled",
            "自动模式未获准上传截图，使用本地 OCR 与文本模型。",
        );
    }
    if context.looks_like_code && context.ocr_confidence >= 0.55 {
        return local_decision(
            "auto_code_exactness",
            "检测到代码且 OCR 置信度可用，为保证标识符准确而使用本地 OCR。",
        );
    }
    if context.complex_layout || context.image_quality < 0.45 || context.ocr_confidence < 0.55 {
        return vision_decision(
            "auto_visual_complexity",
            "检测到复杂布局或较低 OCR 置信度，使用视觉模型理解并翻译。",
        );
    }
    local_decision(
        "auto_simple_text",
        "画面文字清晰且布局简单，使用更快、更易校验的本地 OCR。",
    )
}

fn local_decision(code: &str, explanation: &str) -> RoutingDecision {
    RoutingDecision {
        selected_mode: TranslationMode::LocalOcr,
        reason_code: code.to_owned(),
        explanation_zh: explanation.to_owned(),
        may_upload_image: false,
    }
}

fn vision_decision(code: &str, explanation: &str) -> RoutingDecision {
    RoutingDecision {
        selected_mode: TranslationMode::VisionDirect,
        reason_code: code.to_owned(),
        explanation_zh: explanation.to_owned(),
        may_upload_image: true,
    }
}

/// A source token that must survive translation byte-for-byte.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProtectedToken {
    pub placeholder: String,
    pub original: String,
}

/// Deterministic protected-token output for the OCR + text route.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProtectedText {
    pub sanitized_text: String,
    pub tokens: Vec<ProtectedToken>,
}

static PROTECTED_PATTERN: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
        r#"(?x)
        https?://[^\s<>\"']+
        | (?:[A-Za-z]:\\|/)[A-Za-z0-9_./\\-]+
        | --?[A-Za-z][A-Za-z0-9_-]*
        | \$[A-Za-z_][A-Za-z0-9_]*
        | [A-Z][A-Za-z0-9]+(?:Exception|Error)
        | [A-Za-z_][A-Za-z0-9_]*(?:(?:::|\.)[A-Za-z_][A-Za-z0-9_]*)+
        | [A-Za-z]+(?:[A-Z][A-Za-z0-9]*)+
        | [A-Za-z]+_[A-Za-z0-9_]+
        | [A-Z]{2,}[A-Z0-9_]*\d+
        "#,
    )
    .expect("protected-token regex must compile")
});

#[must_use]
pub fn protect_tokens(input: &str) -> ProtectedText {
    let mut tokens = Vec::new();
    let sanitized_text = PROTECTED_PATTERN
        .replace_all(input, |captures: &regex::Captures<'_>| {
            let placeholder = format!("⟦PG_{:04}⟧", tokens.len());
            tokens.push(ProtectedToken {
                placeholder: placeholder.clone(),
                original: captures[0].to_owned(),
            });
            placeholder
        })
        .into_owned();

    ProtectedText {
        sanitized_text,
        tokens,
    }
}

/// Restores protected tokens. If a placeholder was omitted or slightly modified by the model,
/// does best-effort restoration without throwing a fatal error.
pub fn restore_tokens(translated: &str, tokens: &[ProtectedToken]) -> Result<String, TokenError> {
    let mut restored = translated.to_owned();
    for token in tokens {
        if restored.contains(&token.placeholder) {
            restored = restored.replace(&token.placeholder, &token.original);
        } else {
            // Also try ascii brackets [PG_0000] in case the LLM normalized unicode brackets
            let ascii_placeholder = token.placeholder.replace('⟦', "[").replace('⟧', "]");
            if restored.contains(&ascii_placeholder) {
                restored = restored.replace(&ascii_placeholder, &token.original);
            }
        }
    }
    Ok(restored)
}

#[derive(Debug, thiserror::Error, PartialEq, Eq)]
pub enum TokenError {
    #[error("placeholder {placeholder} error")]
    PlaceholderCount { placeholder: String, count: usize },
}

#[cfg(test)]
mod tests {
    use super::*;

    fn context(mode: TranslationMode) -> RoutingContext {
        RoutingContext {
            requested_mode: mode,
            vision_configured: true,
            image_upload_allowed: true,
            looks_like_code: false,
            complex_layout: false,
            image_quality: 0.9,
            ocr_confidence: 0.9,
        }
    }

    #[test]
    fn forced_local_never_uploads() {
        let decision = select_route(&context(TranslationMode::LocalOcr));
        assert_eq!(decision.selected_mode, TranslationMode::LocalOcr);
        assert!(!decision.may_upload_image);
    }

    #[test]
    fn auto_prefers_local_for_code_exactness() {
        let mut input = context(TranslationMode::Auto);
        input.looks_like_code = true;
        let decision = select_route(&input);
        assert_eq!(decision.reason_code, "auto_code_exactness");
    }

    #[test]
    fn auto_uses_vision_for_complex_layout() {
        let mut input = context(TranslationMode::Auto);
        input.complex_layout = true;
        let decision = select_route(&input);
        assert_eq!(decision.selected_mode, TranslationMode::VisionDirect);
        assert!(decision.may_upload_image);
    }

    #[test]
    fn protected_tokens_round_trip_exactly() {
        let original = "NullReferenceException in getUserProfile at C:\\src\\User.cs --verbose";
        let protected = protect_tokens(original);
        assert!(protected.tokens.len() >= 3);
        let translated = format!("中文解释：{}", protected.sanitized_text);
        let restored = restore_tokens(&translated, &protected.tokens).expect("restore should work");
        assert!(restored.contains("NullReferenceException"));
        assert!(restored.contains("getUserProfile"));
        assert!(restored.contains("C:\\src\\User.cs"));
        assert!(restored.contains("--verbose"));
    }

    #[test]
    fn missing_placeholder_handles_gracefully() {
        let protected = protect_tokens("open getUserProfile now");
        let result = restore_tokens("打开配置文件", &protected.tokens);
        assert!(result.is_ok());
    }
}
