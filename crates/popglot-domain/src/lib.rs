//! Platform-neutral domain types and decisions for `PopGlot`.
//!
//! This crate deliberately contains no WPF, Win32, platform path, credential,
//! capture, or tray dependencies. Every shell communicates through these DTOs.

use regex::Regex;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::sync::LazyLock;

pub mod language;

pub use language::{AUTO_LANGUAGE, LanguagePair, language_english_name, normalize_language_tag};

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
///
/// Deserialization is hand-written so a *missing* `network_enabled` or
/// `allow_image_upload_in_auto` can be treated as `false` while every other
/// missing field keeps its default. A blanket `#[serde(default)]` would grant
/// migrated v1/v2 configurations network and image-upload rights the user
/// never explicitly gave.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
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
    /// Master offline switch. When enabled, no outbound model request is made
    /// regardless of every other permission.
    pub safe_dev_mode: bool,
    /// Opt-in for private relays reached by bare IP or self-signed TLS.
    pub allow_insecure_tls: bool,
    pub api_key_configured: bool,
    /// Last language pair chosen by the user, restored on the next launch.
    pub source_language: String,
    pub target_language: String,
    /// Ask the model for a short usage note alongside the translation.
    pub include_explanation: bool,
    /// Keep code identifiers byte-for-byte by masking them before translation.
    pub protect_code_tokens: bool,
}

impl Default for ProviderSettings {
    fn default() -> Self {
        Self {
            schema_version: 3,
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
            source_language: AUTO_LANGUAGE.to_owned(),
            target_language: "zh-CN".to_owned(),
            include_explanation: true,
            protect_code_tokens: true,
        }
    }
}

impl<'de> Deserialize<'de> for ProviderSettings {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        Self::denied_when_missing(deserializer)
    }
}

impl ProviderSettings {
    /// Deserializes settings, denying network and image-upload rights that a
    /// legacy file never explicitly granted.
    ///
    /// Everything a schema predates (endpoints, models, language pair, …)
    /// still falls back to its normal default; only the two outbound
    /// permissions are tightened, because silence must not widen what may
    /// leave the machine.
    ///
    /// # Errors
    ///
    /// Returns whatever deserialization error the underlying data carries.
    pub fn denied_when_missing<'de, D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        #[derive(Deserialize)]
        #[serde(default)]
        // Mirror of `ProviderSettings` with the two outbound permissions kept
        // optional so "absent" is distinguishable from "explicitly true".
        #[allow(clippy::struct_excessive_bools)]
        struct Shadow {
            schema_version: u32,
            provider_type: ProviderType,
            api_base_url: String,
            text_endpoint: String,
            vision_endpoint: String,
            text_model: String,
            vision_model: String,
            extra_headers: BTreeMap<String, String>,
            anthropic_version: String,
            supports_text: bool,
            supports_vision: bool,
            network_enabled: Option<bool>,
            mode: TranslationMode,
            allow_image_upload_in_auto: Option<bool>,
            safe_dev_mode: bool,
            allow_insecure_tls: bool,
            api_key_configured: bool,
            source_language: String,
            target_language: String,
            include_explanation: bool,
            protect_code_tokens: bool,
        }

        impl Default for Shadow {
            fn default() -> Self {
                let defaults = ProviderSettings::default();
                Self {
                    schema_version: defaults.schema_version,
                    provider_type: defaults.provider_type,
                    api_base_url: defaults.api_base_url.clone(),
                    text_endpoint: defaults.text_endpoint.clone(),
                    vision_endpoint: defaults.vision_endpoint.clone(),
                    text_model: defaults.text_model.clone(),
                    vision_model: defaults.vision_model.clone(),
                    extra_headers: BTreeMap::new(),
                    anthropic_version: defaults.anthropic_version.clone(),
                    supports_text: defaults.supports_text,
                    supports_vision: defaults.supports_vision,
                    network_enabled: None,
                    mode: defaults.mode,
                    allow_image_upload_in_auto: None,
                    safe_dev_mode: defaults.safe_dev_mode,
                    allow_insecure_tls: defaults.allow_insecure_tls,
                    api_key_configured: defaults.api_key_configured,
                    source_language: defaults.source_language.clone(),
                    target_language: defaults.target_language.clone(),
                    include_explanation: defaults.include_explanation,
                    protect_code_tokens: defaults.protect_code_tokens,
                }
            }
        }

        let shadow = Shadow::deserialize(deserializer)?;
        Ok(Self {
            schema_version: shadow.schema_version,
            provider_type: shadow.provider_type,
            api_base_url: shadow.api_base_url,
            text_endpoint: shadow.text_endpoint,
            vision_endpoint: shadow.vision_endpoint,
            text_model: shadow.text_model,
            vision_model: shadow.vision_model,
            extra_headers: shadow.extra_headers,
            anthropic_version: shadow.anthropic_version,
            supports_text: shadow.supports_text,
            supports_vision: shadow.supports_vision,
            network_enabled: shadow.network_enabled.unwrap_or(false),
            mode: shadow.mode,
            allow_image_upload_in_auto: shadow.allow_image_upload_in_auto.unwrap_or(false),
            safe_dev_mode: shadow.safe_dev_mode,
            allow_insecure_tls: shadow.allow_insecure_tls,
            api_key_configured: shadow.api_key_configured,
            source_language: shadow.source_language,
            target_language: shadow.target_language,
            include_explanation: shadow.include_explanation,
            protect_code_tokens: shadow.protect_code_tokens,
        })
    }

    #[must_use]
    pub fn vision_is_configured(&self) -> bool {
        self.supports_vision && !self.vision_model.trim().is_empty()
    }

    #[must_use]
    pub fn text_is_configured(&self) -> bool {
        self.supports_text && !self.text_model.trim().is_empty()
    }

    /// The language pair to use when the caller does not override it.
    #[must_use]
    pub fn language_pair(&self) -> LanguagePair {
        LanguagePair::new(&self.source_language, &self.target_language)
    }

    /// True when the Base URL points at loopback or an RFC1918 private range.
    ///
    /// Such a deployment (Ollama, LM Studio, vLLM) legitimately needs no key,
    /// so this is checked with real host parsing rather than substring tests.
    #[must_use]
    pub fn targets_local_runtime(&self) -> bool {
        is_local_base_url(&self.api_base_url)
    }
}

/// Individual Provider Profile with unique stable ID and credential routing.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
#[allow(clippy::struct_excessive_bools)]
pub struct ProviderProfile {
    pub id: String,
    pub name: String,
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
    pub allow_insecure_tls: bool,
    pub credential_target: String,
    pub is_local: bool,
}

impl Default for ProviderProfile {
    fn default() -> Self {
        Self::openai_default()
    }
}

impl ProviderProfile {
    #[must_use]
    pub fn openai_default() -> Self {
        Self {
            id: "openai-default".to_owned(),
            name: "OpenAI".to_owned(),
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
            allow_insecure_tls: false,
            credential_target: "PopGlot/provider/openai-default".to_owned(),
            is_local: false,
        }
    }

    #[must_use]
    pub fn deepseek() -> Self {
        Self {
            id: "deepseek".to_owned(),
            name: "DeepSeek".to_owned(),
            provider_type: ProviderType::OpenAiCompatible,
            api_base_url: "https://api.deepseek.com/v1".to_owned(),
            text_endpoint: "/chat/completions".to_owned(),
            vision_endpoint: "/chat/completions".to_owned(),
            text_model: "deepseek-chat".to_owned(),
            vision_model: String::new(),
            extra_headers: BTreeMap::new(),
            anthropic_version: "2023-06-01".to_owned(),
            supports_text: true,
            supports_vision: false,
            allow_insecure_tls: false,
            credential_target: "PopGlot/provider/deepseek".to_owned(),
            is_local: false,
        }
    }

    #[must_use]
    pub fn ollama() -> Self {
        Self {
            id: "ollama-local".to_owned(),
            name: "Ollama (本地)".to_owned(),
            provider_type: ProviderType::OpenAiCompatible,
            api_base_url: "http://localhost:11434/v1".to_owned(),
            text_endpoint: "/chat/completions".to_owned(),
            vision_endpoint: "/chat/completions".to_owned(),
            text_model: "qwen2.5:7b".to_owned(),
            vision_model: "llava:7b".to_owned(),
            extra_headers: BTreeMap::new(),
            anthropic_version: "2023-06-01".to_owned(),
            supports_text: true,
            supports_vision: true,
            allow_insecure_tls: false,
            credential_target: "PopGlot/provider/ollama-local".to_owned(),
            is_local: true,
        }
    }

    #[must_use]
    pub fn gemini() -> Self {
        Self {
            id: "gemini".to_owned(),
            name: "Google Gemini".to_owned(),
            provider_type: ProviderType::GeminiGenerateContent,
            api_base_url: "https://generativelanguage.googleapis.com".to_owned(),
            text_endpoint: "/v1beta/models/{model}:generateContent".to_owned(),
            vision_endpoint: "/v1beta/models/{model}:generateContent".to_owned(),
            text_model: "gemini-2.0-flash".to_owned(),
            vision_model: "gemini-2.0-flash".to_owned(),
            extra_headers: BTreeMap::new(),
            anthropic_version: "2023-06-01".to_owned(),
            supports_text: true,
            supports_vision: true,
            allow_insecure_tls: false,
            credential_target: "PopGlot/provider/gemini".to_owned(),
            is_local: false,
        }
    }

    #[must_use]
    pub fn claude() -> Self {
        Self {
            id: "claude".to_owned(),
            name: "Anthropic Claude".to_owned(),
            provider_type: ProviderType::AnthropicMessages,
            api_base_url: "https://api.anthropic.com".to_owned(),
            text_endpoint: "/v1/messages".to_owned(),
            vision_endpoint: "/v1/messages".to_owned(),
            text_model: "claude-3-5-sonnet-latest".to_owned(),
            vision_model: "claude-3-5-sonnet-latest".to_owned(),
            extra_headers: BTreeMap::new(),
            anthropic_version: "2023-06-01".to_owned(),
            supports_text: true,
            supports_vision: true,
            allow_insecure_tls: false,
            credential_target: "PopGlot/provider/claude".to_owned(),
            is_local: false,
        }
    }

    #[must_use]
    pub fn targets_local_runtime(&self) -> bool {
        self.is_local || is_local_base_url(&self.api_base_url)
    }

    #[must_use]
    pub fn to_provider_settings(
        &self,
        policy: &OutboundPolicy,
        prefs: &TranslationPreferences,
    ) -> ProviderSettings {
        ProviderSettings {
            schema_version: 3,
            provider_type: self.provider_type,
            api_base_url: self.api_base_url.clone(),
            text_endpoint: self.text_endpoint.clone(),
            vision_endpoint: self.vision_endpoint.clone(),
            text_model: self.text_model.clone(),
            vision_model: self.vision_model.clone(),
            extra_headers: self.extra_headers.clone(),
            anthropic_version: self.anthropic_version.clone(),
            supports_text: self.supports_text,
            supports_vision: self.supports_vision,
            network_enabled: policy.network_enabled,
            mode: prefs.mode,
            allow_image_upload_in_auto: policy.allow_image_upload_in_auto,
            safe_dev_mode: policy.safe_dev_mode,
            allow_insecure_tls: self.allow_insecure_tls || policy.allow_insecure_tls,
            api_key_configured: false,
            source_language: prefs.source_language.clone(),
            target_language: prefs.target_language.clone(),
            include_explanation: prefs.include_explanation,
            protect_code_tokens: prefs.protect_code_tokens,
        }
    }
}

/// Outbound network security and privacy policy.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
#[allow(clippy::struct_excessive_bools)]
pub struct OutboundPolicy {
    pub safe_dev_mode: bool,
    pub network_enabled: bool,
    pub allow_image_upload_in_auto: bool,
    pub allow_insecure_tls: bool,
}

impl Default for OutboundPolicy {
    fn default() -> Self {
        Self {
            safe_dev_mode: false,
            network_enabled: true,
            allow_image_upload_in_auto: true,
            allow_insecure_tls: false,
        }
    }
}

/// Translation preference options.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct TranslationPreferences {
    pub mode: TranslationMode,
    pub source_language: String,
    pub target_language: String,
    pub include_explanation: bool,
    pub protect_code_tokens: bool,
}

impl Default for TranslationPreferences {
    fn default() -> Self {
        Self {
            mode: TranslationMode::Auto,
            source_language: AUTO_LANGUAGE.to_owned(),
            target_language: "zh-CN".to_owned(),
            include_explanation: true,
            protect_code_tokens: true,
        }
    }
}

/// Consolidated multi-profile product configuration.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct CoreProductConfig {
    pub schema_version: u32,
    pub active_profile_id: String,
    pub profiles: Vec<ProviderProfile>,
    pub outbound_policy: OutboundPolicy,
    pub preferences: TranslationPreferences,
}

impl Default for CoreProductConfig {
    fn default() -> Self {
        Self {
            schema_version: 4,
            active_profile_id: "openai-default".to_owned(),
            profiles: vec![
                ProviderProfile::openai_default(),
                ProviderProfile::deepseek(),
                ProviderProfile::ollama(),
                ProviderProfile::gemini(),
                ProviderProfile::claude(),
            ],
            outbound_policy: OutboundPolicy::default(),
            preferences: TranslationPreferences::default(),
        }
    }
}

impl CoreProductConfig {
    #[must_use]
    #[allow(clippy::missing_panics_doc)]
    pub fn active_profile(&self) -> &ProviderProfile {
        self.profiles
            .iter()
            .find(|p| p.id == self.active_profile_id)
            .unwrap_or_else(|| self.profiles.first().expect("at least one profile"))
    }

    #[must_use]
    #[allow(clippy::missing_panics_doc)]
    pub fn active_profile_mut(&mut self) -> &mut ProviderProfile {
        let id = self.active_profile_id.clone();
        if let Some(pos) = self.profiles.iter().position(|p| p.id == id) {
            &mut self.profiles[pos]
        } else {
            &mut self.profiles[0]
        }
    }

    #[must_use]
    pub fn to_provider_settings(&self) -> ProviderSettings {
        self.active_profile()
            .to_provider_settings(&self.outbound_policy, &self.preferences)
    }

    pub fn update_active_from_settings(&mut self, settings: &ProviderSettings) {
        self.outbound_policy.safe_dev_mode = settings.safe_dev_mode;
        self.outbound_policy.network_enabled = settings.network_enabled;
        self.outbound_policy.allow_image_upload_in_auto = settings.allow_image_upload_in_auto;
        self.outbound_policy.allow_insecure_tls = settings.allow_insecure_tls;

        self.preferences.mode = settings.mode;
        self.preferences
            .source_language
            .clone_from(&settings.source_language);
        self.preferences
            .target_language
            .clone_from(&settings.target_language);
        self.preferences.include_explanation = settings.include_explanation;
        self.preferences.protect_code_tokens = settings.protect_code_tokens;

        let active = self.active_profile_mut();
        active.provider_type = settings.provider_type;
        active.api_base_url.clone_from(&settings.api_base_url);
        active.text_endpoint.clone_from(&settings.text_endpoint);
        active.vision_endpoint.clone_from(&settings.vision_endpoint);
        active.text_model.clone_from(&settings.text_model);
        active.vision_model.clone_from(&settings.vision_model);
        active.extra_headers.clone_from(&settings.extra_headers);
        active
            .anthropic_version
            .clone_from(&settings.anthropic_version);
        active.supports_text = settings.supports_text;
        active.supports_vision = settings.supports_vision;
        active.allow_insecure_tls = settings.allow_insecure_tls;
        active.is_local = is_local_base_url(&settings.api_base_url);
    }
}

/// Whether a Base URL addresses loopback or an RFC1918 private network.
///
/// Substring matching (the previous approach) misclassified public hosts such
/// as `relay-10.example.com` as local and silently skipped the credential gate.
#[must_use]
pub fn is_local_base_url(base_url: &str) -> bool {
    let trimmed = base_url.trim();
    // `Url::parse` needs a scheme; accept a bare `host:port` too.
    let candidate = if trimmed.contains("://") {
        trimmed.to_owned()
    } else {
        format!("http://{trimmed}")
    };
    let Ok(parsed) = url_host(&candidate) else {
        return false;
    };
    is_local_host(&parsed)
}

fn url_host(candidate: &str) -> Result<String, ()> {
    // Minimal host extraction that does not pull a URL crate into the domain
    // layer: strip scheme, credentials, port, and path.
    let after_scheme = candidate
        .split_once("://")
        .map_or(candidate, |(_, rest)| rest);
    let authority = after_scheme
        .split(['/', '?', '#'])
        .next()
        .unwrap_or_default();
    let authority = authority
        .rsplit_once('@')
        .map_or(authority, |(_, host)| host);
    let host = if let Some(end) = authority.strip_prefix('[').and_then(|rest| rest.find(']')) {
        // IPv6 literal: `[::1]:11434`
        &authority[1..=end]
    } else {
        authority.split(':').next().unwrap_or_default()
    };
    if host.is_empty() {
        Err(())
    } else {
        Ok(host.to_ascii_lowercase())
    }
}

fn is_local_host(host: &str) -> bool {
    if matches!(host, "localhost" | "::1" | "[::1]") || host.ends_with(".localhost") {
        return true;
    }
    let octets: Vec<u8> = host
        .split('.')
        .filter_map(|part| part.parse::<u8>().ok())
        .collect();
    if octets.len() != 4 || host.split('.').count() != 4 {
        return false;
    }
    match (octets[0], octets[1]) {
        (127 | 10, _) | (192, 168) => true,
        (172, second) => (16..=31).contains(&second),
        _ => false,
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
    pub local_ocr_available: bool,
    pub looks_like_code: bool,
    pub complex_layout: bool,
    pub image_quality: f32,
    pub ocr_confidence: f32,
}

impl RoutingContext {
    /// Builds the routing inputs the shell can answer before any OCR has run.
    #[must_use]
    pub fn from_settings(
        settings: &ProviderSettings,
        local_ocr_available: bool,
        credential_present: bool,
    ) -> Self {
        let can_reach_model = settings.network_enabled
            && !settings.safe_dev_mode
            && (credential_present || settings.targets_local_runtime());
        Self {
            requested_mode: settings.mode,
            vision_configured: settings.vision_is_configured() && can_reach_model,
            image_upload_allowed: settings.allow_image_upload_in_auto,
            local_ocr_available,
            looks_like_code: false,
            complex_layout: false,
            image_quality: 1.0,
            ocr_confidence: 1.0,
        }
    }
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
        TranslationMode::LocalOcr => local_or_blocked(
            context,
            "forced_local_ocr",
            "已按设置使用本地 OCR；截图不会上传给视觉模型。",
        ),
        TranslationMode::VisionDirect => {
            if !context.vision_configured {
                local_or_blocked(
                    context,
                    "vision_not_configured",
                    "视觉模型不可用，已安全回退到本地 OCR 与文本模型。",
                )
            } else if !context.image_upload_allowed {
                local_or_blocked(
                    context,
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
        return local_or_blocked(
            context,
            "auto_no_vision_model",
            "未配置可用的视觉模型，自动模式使用本地 OCR 与文本模型。",
        );
    }
    if !context.image_upload_allowed {
        return local_or_blocked(
            context,
            "auto_upload_disabled",
            "自动模式未获准上传截图，使用本地 OCR 与文本模型。",
        );
    }
    // Without a local OCR engine there is nothing to fall back to, so a
    // permitted vision model is the only route that can produce a result.
    if !context.local_ocr_available {
        return vision_decision(
            "auto_no_local_ocr",
            "系统没有可用的 OCR 语言包，改用视觉模型识别并翻译。",
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
        "auto_local_first",
        "本地优先自动：默认优先使用本地 OCR 识别与翻译，无需上传截图。",
    )
}

/// Local OCR is the safe answer, but only when an OCR engine actually exists.
fn local_or_blocked(context: &RoutingContext, code: &str, explanation: &str) -> RoutingDecision {
    if context.local_ocr_available {
        local_decision(code, explanation)
    } else {
        RoutingDecision {
            selected_mode: TranslationMode::LocalOcr,
            reason_code: format!("{code}_without_ocr"),
            explanation_zh:
                "系统没有安装 Windows OCR 语言包，且当前不允许上传截图；请安装语言包或在设置中开启截图上传。"
                    .to_owned(),
            may_upload_image: false,
        }
    }
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

/// Restored translation plus the placeholders the model failed to echo back.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RestoredText {
    pub text: String,
    pub dropped_terms: Vec<String>,
}

/// Tokens that are unambiguously machine syntax in any context.
static STRONG_PATTERN: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
        r#"(?x)
        https?://[^\s<>\"']+
        | (?:[A-Za-z]:\\|\./|/)[A-Za-z0-9_./\\-]*[A-Za-z0-9_.-]
        | --?[A-Za-z][A-Za-z0-9_-]*
        | \$[A-Za-z_][A-Za-z0-9_]*
        | [A-Z][A-Za-z0-9]+(?:Exception|Error)
        | [A-Za-z_][A-Za-z0-9_]*(?:(?:::|->)[A-Za-z_][A-Za-z0-9_]*)+
        | [A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+
        | [A-Z]{2,}[A-Z0-9_]*\d+
        "#,
    )
    .expect("strong protected-token regex must compile")
});

/// Identifier shapes that are only worth masking inside technical text.
/// In ordinary prose these also match product names such as `JavaScript`,
/// and masking those measurably degrades translation quality.
static WEAK_PATTERN: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
        r"(?x)
        [a-z]+(?:[A-Z][A-Za-z0-9]*)+
        | [A-Za-z]+_[A-Za-z0-9_]+
        ",
    )
    .expect("weak protected-token regex must compile")
});

/// Punctuation density that marks a snippet as source code rather than prose.
static CODE_MARKERS: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"[{};=<>()\[\]`]|\bfn\b|\bvar\b|\blet\b|\bconst\b|\bdef\b|\bclass\b")
        .expect("code marker regex must compile")
});

#[must_use]
pub fn protect_tokens(input: &str) -> ProtectedText {
    // Both passes scan the *original* text and their ranges are merged before a
    // single substitution. Substituting the strong pass first would let the
    // weak pass match the `PG_0000` inside a freshly written placeholder and
    // nest placeholders inside each other, corrupting every later restore.
    let strong: Vec<(usize, usize)> = STRONG_PATTERN
        .find_iter(input)
        .map(|found| (found.start(), found.end()))
        .collect();

    // Weak identifiers only earn a placeholder when the surrounding text is
    // already recognisable as code or carries a strong technical token.
    let mut ranges = strong.clone();
    if !strong.is_empty() || CODE_MARKERS.is_match(input) {
        for found in WEAK_PATTERN.find_iter(input) {
            let (start, end) = (found.start(), found.end());
            let overlaps_strong = strong
                .iter()
                .any(|(other_start, other_end)| start < *other_end && *other_start < end);
            if !overlaps_strong {
                ranges.push((start, end));
            }
        }
    }
    ranges.sort_unstable();

    let mut tokens = Vec::new();
    let mut sanitized_text = String::with_capacity(input.len());
    let mut cursor = 0;
    for (start, end) in ranges {
        if start < cursor {
            continue;
        }
        sanitized_text.push_str(&input[cursor..start]);
        let placeholder = format!("⟦PG_{:04}⟧", tokens.len());
        sanitized_text.push_str(&placeholder);
        tokens.push(ProtectedToken {
            placeholder,
            original: input[start..end].to_owned(),
        });
        cursor = end;
    }
    sanitized_text.push_str(&input[cursor..]);

    ProtectedText {
        sanitized_text,
        tokens,
    }
}

/// Restores protected tokens, tolerating the placeholder normalisations that
/// models routinely apply (ASCII brackets, stripped brackets, added spaces).
///
/// Terms the model dropped entirely are reported instead of silently lost, so
/// the shell can warn that an identifier is missing from the translation.
#[must_use]
pub fn restore_tokens(translated: &str, tokens: &[ProtectedToken]) -> RestoredText {
    let mut restored = translated.to_owned();
    let mut dropped = Vec::new();

    for (index, token) in tokens.iter().enumerate() {
        let variants = placeholder_variants(&token.placeholder, index);
        let matched = variants.iter().find(|variant| restored.contains(*variant));
        match matched {
            Some(variant) => restored = restored.replace(variant, &token.original),
            None => dropped.push(token.original.clone()),
        }
    }

    RestoredText {
        text: restored,
        dropped_terms: dropped,
    }
}

fn placeholder_variants(placeholder: &str, index: usize) -> Vec<String> {
    let ascii = placeholder.replace('⟦', "[").replace('⟧', "]");
    let bare = placeholder.replace(['⟦', '⟧'], "");
    vec![
        placeholder.to_owned(),
        ascii,
        format!("[[PG_{index:04}]]"),
        format!("{{PG_{index:04}}}"),
        format!("<PG_{index:04}>"),
        bare,
    ]
}

#[cfg(test)]
mod tests {
    use super::*;

    fn context(mode: TranslationMode) -> RoutingContext {
        RoutingContext {
            requested_mode: mode,
            vision_configured: true,
            image_upload_allowed: true,
            local_ocr_available: true,
            looks_like_code: false,
            complex_layout: false,
            image_quality: 0.9,
            ocr_confidence: 0.9,
        }
    }

    #[test]
    fn multi_profile_product_config_conversions() {
        let mut config = CoreProductConfig::default();
        assert_eq!(config.active_profile_id, "openai-default");
        assert_eq!(config.profiles.len(), 5);

        let settings = config.to_provider_settings();
        assert_eq!(settings.provider_type, ProviderType::OpenAiCompatible);
        assert_eq!(settings.api_base_url, "https://api.openai.com/v1");

        // Switch to deepseek profile
        config.active_profile_id = "deepseek".to_owned();
        let ds_settings = config.to_provider_settings();
        assert_eq!(ds_settings.text_model, "deepseek-chat");
        assert_eq!(ds_settings.api_base_url, "https://api.deepseek.com/v1");
        assert!(!ds_settings.supports_vision);

        // Switch to ollama profile (local)
        config.active_profile_id = "ollama-local".to_owned();
        let ollama_settings = config.to_provider_settings();
        assert!(ollama_settings.targets_local_runtime());
        assert_eq!(ollama_settings.text_model, "qwen2.5:7b");
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
    fn auto_uses_vision_when_no_ocr_language_pack_exists() {
        let mut input = context(TranslationMode::Auto);
        input.local_ocr_available = false;
        let decision = select_route(&input);
        assert_eq!(decision.selected_mode, TranslationMode::VisionDirect);
        assert_eq!(decision.reason_code, "auto_no_local_ocr");
    }

    #[test]
    fn blocked_route_is_explicit_when_neither_path_is_available() {
        let mut input = context(TranslationMode::LocalOcr);
        input.local_ocr_available = false;
        let decision = select_route(&input);
        assert!(!decision.may_upload_image);
        assert!(decision.reason_code.ends_with("_without_ocr"));
    }

    #[test]
    fn protected_tokens_round_trip_exactly() {
        let original = "NullReferenceException in getUserProfile at C:\\src\\User.cs --verbose";
        let protected = protect_tokens(original);
        assert!(protected.tokens.len() >= 3);
        let translated = format!("中文解释：{}", protected.sanitized_text);
        let restored = restore_tokens(&translated, &protected.tokens);
        assert!(restored.dropped_terms.is_empty());
        assert!(restored.text.contains("NullReferenceException"));
        assert!(restored.text.contains("getUserProfile"));
        assert!(restored.text.contains("C:\\src\\User.cs"));
        assert!(restored.text.contains("--verbose"));
    }

    #[test]
    fn plain_prose_is_not_masked() {
        let protected = protect_tokens("I am learning JavaScript and it is fun");
        assert!(protected.tokens.is_empty());
        assert_eq!(
            protected.sanitized_text,
            "I am learning JavaScript and it is fun"
        );
    }

    #[test]
    fn identifiers_inside_code_are_masked() {
        let protected = protect_tokens("const userName = getUserName();");
        assert!(
            protected
                .tokens
                .iter()
                .any(|token| token.original == "userName")
        );
    }

    #[test]
    fn dropped_placeholder_is_reported_not_lost() {
        let protected = protect_tokens("open C:\\src\\User.cs now");
        let restored = restore_tokens("打开配置文件", &protected.tokens);
        assert_eq!(restored.dropped_terms, vec!["C:\\src\\User.cs".to_owned()]);
    }

    #[test]
    fn ascii_normalized_placeholder_still_restores() {
        let protected = protect_tokens("check --verbose flag");
        let translated = protected.sanitized_text.replace('⟦', "[").replace('⟧', "]");
        let restored = restore_tokens(&translated, &protected.tokens);
        assert!(restored.text.contains("--verbose"));
        assert!(restored.dropped_terms.is_empty());
    }

    #[test]
    fn private_hosts_are_local_and_public_lookalikes_are_not() {
        assert!(is_local_base_url("http://localhost:11434/v1"));
        assert!(is_local_base_url("http://127.0.0.1:1234/v1"));
        assert!(is_local_base_url("http://192.168.1.20:8080"));
        assert!(is_local_base_url("http://172.16.0.4:8000/v1"));
        assert!(is_local_base_url("http://10.0.0.5/v1"));
        assert!(!is_local_base_url("https://relay-10.example.com/v1"));
        assert!(!is_local_base_url("https://api.openai.com/v1"));
        assert!(!is_local_base_url("https://172.200.1.1/v1"));
    }

    #[test]
    fn legacy_config_without_permission_fields_stays_offline() {
        // A v1/v2 file knows nothing about `network_enabled` or
        // `allow_image_upload_in_auto`; migration must not invent consent.
        let legacy = r#"{
            "schema_version": 2,
            "provider_type": "OpenAiCompatible",
            "api_base_url": "https://api.openai.com/v1",
            "text_model": "gpt-4o-mini",
            "mode": "Auto"
        }"#;
        let settings: ProviderSettings = serde_json::from_str(legacy).expect("parse legacy file");
        assert!(
            !settings.network_enabled,
            "absent network permission must stay off"
        );
        assert!(
            !settings.allow_image_upload_in_auto,
            "absent image-upload permission must stay off"
        );
        // Non-permission fields still migrate to their defaults.
        assert!(settings.protect_code_tokens);
        assert!(settings.supports_text);
    }

    #[test]
    fn explicitly_saved_permissions_survive_migration() {
        let saved = r#"{
            "schema_version": 3,
            "network_enabled": true,
            "allow_image_upload_in_auto": true,
            "safe_dev_mode": false
        }"#;
        let settings: ProviderSettings = serde_json::from_str(saved).expect("parse saved file");
        assert!(settings.network_enabled);
        assert!(settings.allow_image_upload_in_auto);

        // An explicit opt-out must of course stay off.
        let opted_out: ProviderSettings =
            serde_json::from_str("{\"network_enabled\": false}").expect("parse opted-out file");
        assert!(!opted_out.network_enabled);
    }

    #[test]
    fn default_settings_round_trip_through_json() {
        let json = serde_json::to_string(&ProviderSettings::default()).expect("serialize");
        let parsed: ProviderSettings = serde_json::from_str(&json).expect("parse own output");
        assert_eq!(parsed, ProviderSettings::default());
    }
}
