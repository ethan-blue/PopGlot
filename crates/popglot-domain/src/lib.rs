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
    /// 视觉模型仅负责识别截图文字，译文由文本模型生成。
    VisionOcr,
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
    /// Independent, explicit permission for services hosted on another LAN
    /// device (RFC1918 / IPv6 ULA). A private-range URL never grants this by
    /// itself: content sent there has already left the machine.
    #[serde(default)]
    pub allow_lan_endpoints: bool,
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
    /// Independent vision provider. When present, screenshot requests use
    /// this provider's base URL, protocol, endpoint, model and headers — with
    /// the vision API key supplied per call by the shell — instead of
    /// reusing the text provider's connection details.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub vision_provider: Option<VisionProviderSettings>,
}

/// Complete connection details for a dedicated vision provider. The API key
/// deliberately lives outside this struct: it is passed per request and is
/// never persisted inside settings.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct VisionProviderSettings {
    pub provider_type: ProviderType,
    pub api_base_url: String,
    pub vision_endpoint: String,
    pub vision_model: String,
    #[serde(default)]
    pub extra_headers: BTreeMap<String, String>,
    #[serde(default)]
    pub anthropic_version: String,
    #[serde(default)]
    pub allow_insecure_tls: bool,
    /// Explicit permission for a vision service on another LAN device.
    #[serde(default)]
    pub allow_lan_endpoints: bool,
}

impl Default for ProviderSettings {
    fn default() -> Self {
        Self {
            schema_version: 3,
            provider_type: ProviderType::OpenAiCompatible,
            api_base_url: "https://api.openai.com/v1".to_owned(),
            text_endpoint: "/chat/completions".to_owned(),
            vision_endpoint: "/chat/completions".to_owned(),
            // Fresh installs declare no models: models are fetched from the
            // provider's catalog or typed by the user, never invented here.
            text_model: String::new(),
            vision_model: String::new(),
            extra_headers: BTreeMap::new(),
            anthropic_version: "2023-06-01".to_owned(),
            supports_text: true,
            supports_vision: false,
            network_enabled: true,
            mode: TranslationMode::Auto,
            // Screenshots never leave the machine unless the user opts in.
            allow_image_upload_in_auto: false,
            safe_dev_mode: false,
            // A LAN service on another device is never trusted by default.
            allow_lan_endpoints: false,
            allow_insecure_tls: false,
            api_key_configured: false,
            source_language: AUTO_LANGUAGE.to_owned(),
            target_language: "zh-CN".to_owned(),
            include_explanation: true,
            protect_code_tokens: true,
            vision_provider: None,
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
            allow_lan_endpoints: Option<bool>,
            allow_insecure_tls: bool,
            api_key_configured: bool,
            source_language: String,
            target_language: String,
            include_explanation: bool,
            protect_code_tokens: bool,
            #[serde(default)]
            vision_provider: Option<VisionProviderSettings>,
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
                    allow_lan_endpoints: None,
                    allow_insecure_tls: defaults.allow_insecure_tls,
                    api_key_configured: defaults.api_key_configured,
                    source_language: defaults.source_language.clone(),
                    target_language: defaults.target_language.clone(),
                    include_explanation: defaults.include_explanation,
                    protect_code_tokens: defaults.protect_code_tokens,
                    vision_provider: None,
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
            allow_lan_endpoints: shadow.allow_lan_endpoints.unwrap_or(false),
            allow_insecure_tls: shadow.allow_insecure_tls,
            api_key_configured: shadow.api_key_configured,
            source_language: shadow.source_language,
            target_language: shadow.target_language,
            include_explanation: shadow.include_explanation,
            protect_code_tokens: shadow.protect_code_tokens,
            vision_provider: shadow.vision_provider,
        })
    }

    #[must_use]
    pub fn vision_is_configured(&self) -> bool {
        self.supports_vision && !self.vision_model.trim().is_empty()
    }

    /// Applies a dedicated vision provider on top of these settings: the
    /// returned snapshot routes vision requests through the override's base
    /// URL, endpoint, model, headers and protocol. Text routing is untouched.
    #[must_use]
    pub fn with_vision_provider(&self, vision: &VisionProviderSettings) -> Self {
        Self {
            provider_type: vision.provider_type,
            api_base_url: vision.api_base_url.clone(),
            vision_endpoint: vision.vision_endpoint.clone(),
            vision_model: vision.vision_model.clone(),
            extra_headers: vision.extra_headers.clone(),
            anthropic_version: if vision.anthropic_version.trim().is_empty() {
                self.anthropic_version.clone()
            } else {
                vision.anthropic_version.clone()
            },
            allow_insecure_tls: vision.allow_insecure_tls,
            // The vision route carries its own LAN permission, independent of
            // the text route's.
            allow_lan_endpoints: vision.allow_lan_endpoints,
            supports_vision: true,
            supports_text: false,
            vision_provider: None,
            ..self.clone()
        }
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
    /// Explicit permission for a service on another LAN device. Never derived
    /// from the URL alone; older configs deserialize as false.
    #[serde(default)]
    pub allow_lan_endpoints: bool,
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
            text_model: String::new(),
            vision_model: String::new(),
            extra_headers: BTreeMap::new(),
            anthropic_version: "2023-06-01".to_owned(),
            supports_text: true,
            supports_vision: true,
            allow_insecure_tls: false,
            credential_target: "PopGlot/provider/openai-default".to_owned(),
            is_local: false,
            allow_lan_endpoints: false,
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
            text_model: String::new(),
            vision_model: String::new(),
            extra_headers: BTreeMap::new(),
            anthropic_version: "2023-06-01".to_owned(),
            supports_text: true,
            supports_vision: false,
            allow_insecure_tls: false,
            credential_target: "PopGlot/provider/deepseek".to_owned(),
            is_local: false,
            allow_lan_endpoints: false,
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
            text_model: String::new(),
            vision_model: String::new(),
            extra_headers: BTreeMap::new(),
            anthropic_version: "2023-06-01".to_owned(),
            supports_text: true,
            supports_vision: true,
            allow_insecure_tls: false,
            credential_target: "PopGlot/provider/ollama-local".to_owned(),
            is_local: true,
            allow_lan_endpoints: false,
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
            text_model: String::new(),
            vision_model: String::new(),
            extra_headers: BTreeMap::new(),
            anthropic_version: "2023-06-01".to_owned(),
            supports_text: true,
            supports_vision: true,
            allow_insecure_tls: false,
            credential_target: "PopGlot/provider/gemini".to_owned(),
            is_local: false,
            allow_lan_endpoints: false,
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
            text_model: String::new(),
            vision_model: String::new(),
            extra_headers: BTreeMap::new(),
            anthropic_version: "2023-06-01".to_owned(),
            supports_text: true,
            supports_vision: true,
            allow_insecure_tls: false,
            credential_target: "PopGlot/provider/claude".to_owned(),
            is_local: false,
            allow_lan_endpoints: false,
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
            allow_lan_endpoints: self.allow_lan_endpoints,
            allow_insecure_tls: self.allow_insecure_tls || policy.allow_insecure_tls,
            api_key_configured: false,
            source_language: prefs.source_language.clone(),
            target_language: prefs.target_language.clone(),
            include_explanation: prefs.include_explanation,
            protect_code_tokens: prefs.protect_code_tokens,
            vision_provider: None,
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

/// Where an endpoint's host actually lives. The three classes carry
/// different permissions: loopback stays on the machine, a private-range
/// address is another device on the LAN, anything else is the internet.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum EndpointClass {
    Loopback,
    PrivateNetwork,
    Internet,
}

/// Classifies an endpoint's host. Unknown or malformed input is classified
/// conservatively as [`EndpointClass::Internet`].
#[must_use]
pub fn classify_endpoint(base_url: &str) -> EndpointClass {
    let trimmed = base_url.trim();
    // `Url::parse` needs a scheme; accept a bare `host:port` too.
    let candidate = if trimmed.contains("://") {
        trimmed.to_owned()
    } else {
        format!("http://{trimmed}")
    };
    let Ok(host) = url_host(&candidate) else {
        return EndpointClass::Internet;
    };
    classify_host(&host)
}

/// Whether a Base URL addresses the machine itself or an RFC1918/ULA private
/// network — the cases that may skip the credential gate and use plain HTTP.
///
/// Substring matching (the previous approach) misclassified public hosts such
/// as `relay-10.example.com` as local and silently skipped the credential gate.
#[must_use]
pub fn is_local_base_url(base_url: &str) -> bool {
    !matches!(classify_endpoint(base_url), EndpointClass::Internet)
}

fn url_host(candidate: &str) -> Result<String, ()> {
    // Minimal host extraction that does not pull a URL crate into the domain
    // layer: strip scheme, credentials, port, and path. A malformed port
    // makes the whole URL unclassifiable, so it errors into the conservative
    // Internet class.
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
    let (host, port) =
        if let Some(end) = authority.strip_prefix('[').and_then(|rest| rest.find(']')) {
            // IPv6 literal: `[::1]:11434`
            let rest = &authority[end + 1..];
            let port = rest.strip_prefix(':').unwrap_or_default();
            (&authority[1..=end], port)
        } else {
            match authority.split_once(':') {
                Some((host, port)) => (host, port),
                None => (authority, ""),
            }
        };
    if host.is_empty() || (!port.is_empty() && port.parse::<u16>().is_err()) {
        return Err(());
    }
    Ok(host.to_ascii_lowercase())
}

fn classify_host(host: &str) -> EndpointClass {
    if matches!(host, "localhost" | "::1") || host.ends_with(".localhost") {
        return EndpointClass::Loopback;
    }
    if let Ok(address) = host.parse::<std::net::Ipv4Addr>() {
        let octets = address.octets();
        return match (octets[0], octets[1]) {
            (127, _) => EndpointClass::Loopback,
            (10, _) | (192, 168) => EndpointClass::PrivateNetwork,
            (172, second) if (16..=31).contains(&second) => EndpointClass::PrivateNetwork,
            _ => EndpointClass::Internet,
        };
    }
    if let Ok(address) = host.parse::<std::net::Ipv6Addr>() {
        let segments = address.segments();
        if segments[0] == 1
            && segments[1] == 0
            && segments[2] == 0
            && segments[3] == 0
            && segments[4] == 0
            && segments[5] == 0
            && segments[6] == 0
            && segments[7] == 1
        {
            return EndpointClass::Loopback; // ::1
        }
        // Unique local addresses: fc00::/7 (fc/fd first byte).
        if segments[0] & 0xFE00 == 0xFC00 {
            return EndpointClass::PrivateNetwork;
        }
        return EndpointClass::Internet;
    }
    EndpointClass::Internet
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

/// Observable inputs to screenshot routing. The shell collects every fact
/// before capture; the domain owns the decision. No opaque model-side or
/// pseudo-quality input exists: fields the shell cannot actually observe
/// were removed rather than fed constants.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
// Independent user permissions / platform observations, each answering a
// different question — collapsing them into an enum would hide real
// combinations the decision table must distinguish.
#[allow(clippy::struct_excessive_bools)]
pub struct RoutingContext {
    pub requested_mode: TranslationMode,
    /// A vision profile exists with a named model and is reachable
    /// (network on / safe mode off / key where required).
    pub vision_configured: bool,
    /// The vision endpoint class as the shell classified it
    /// ("loopback" | "private" | "internet"); "unknown" is refused like
    /// "internet" but reported distinctly in the reason.
    pub vision_endpoint_class: String,
    /// The user's separate allow-upload-to-vision permission.
    pub image_upload_allowed: bool,
    /// The user's separate allow-LAN-endpoints permission.
    pub allow_lan_endpoints: bool,
    /// Windows OCR language packs are installed.
    pub local_ocr_available: bool,
    /// A text route exists to carry the translation phase of `VisionOcr`.
    pub text_route_available: bool,
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
            vision_endpoint_class: "internet".to_owned(),
            image_upload_allowed: settings.allow_image_upload_in_auto,
            allow_lan_endpoints: settings.allow_lan_endpoints,
            local_ocr_available,
            text_route_available: true,
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
        // Explicit LocalOcr: local recognition when the engine exists; a
        // refusal to upload otherwise — never a silent picture upload.
        TranslationMode::LocalOcr => {
            if context.local_ocr_available {
                local_decision(
                    "forced_local_ocr",
                    "已按设置使用本地 OCR；截图不会上传给视觉模型。",
                )
            } else {
                RoutingDecision {
                    selected_mode: TranslationMode::LocalOcr,
                    reason_code: "forced_local_ocr_without_engine".to_owned(),
                    explanation_zh: "已指定本地 OCR，但系统没有可用的 OCR 语言包。".to_owned(),
                    may_upload_image: false,
                }
            }
        }
        TranslationMode::VisionDirect => select_vision_direct(context),
        TranslationMode::VisionOcr => select_vision_ocr(context),
        TranslationMode::Auto => select_auto_route(context),
    }
}

/// Vision usability as a fact triple: (usable, `leaves_device`, block reason).
/// An unavailable-but-requested vision route BLOCKS with a precise reason —
/// explicit user choices never silently degrade to another pipeline.
fn vision_admission(context: &RoutingContext) -> Result<(bool, bool), String> {
    if !context.vision_configured {
        return Err("未配置可用的图片服务，请先选择图片模型。".to_owned());
    }
    let class = context.vision_endpoint_class.as_str();
    let leaves_device = class != "loopback";
    if !context.image_upload_allowed && leaves_device {
        return Err("所选图片服务为远程服务，但当前未允许截图离开设备。".to_owned());
    }
    if class == "private" && !context.allow_lan_endpoints {
        return Err(
            "该图片服务位于局域网的另一台设备；需在设置中单独允许局域网模型，并允许截图上传。"
                .to_owned(),
        );
    }
    if class != "loopback" && class != "private" && class != "internet" {
        return Err("图片服务地址无法分类，已按远程地址处理并被当前设置阻止。".to_owned());
    }
    Ok((true, leaves_device))
}

/// `VisionDirect`: an explicit user choice. Either the selected vision profile
/// is executable, or the operation is blocked — never silently OCR.
fn select_vision_direct(context: &RoutingContext) -> RoutingDecision {
    match vision_admission(context) {
        Ok((true, leaves_device)) => RoutingDecision {
            selected_mode: TranslationMode::VisionDirect,
            reason_code: "forced_vision".to_owned(),
            explanation_zh: if !leaves_device {
                "本地视觉服务直接识别并翻译截图，图片不离开本机。".to_owned()
            } else if context.vision_endpoint_class == "private" {
                "已授权局域网视觉模型直接识别并翻译截图（截图将发送到局域网另一台设备）。"
                    .to_owned()
            } else {
                "已按设置使用所选视觉模型直接识别并翻译截图。".to_owned()
            },
            // The flag answers AI-RULES 4.1's question: does the image leave
            // the device? A loopback pipeline still runs, nothing is uploaded.
            may_upload_image: leaves_device,
        },
        Ok((false, _)) => unreachable!("vision_admission never reports usable-but-blocked"),
        Err(reason) => RoutingDecision {
            selected_mode: TranslationMode::VisionDirect,
            reason_code: if context.vision_configured {
                "vision_permission_blocked".to_owned()
            } else {
                "vision_not_configured".to_owned()
            },
            explanation_zh: reason,
            may_upload_image: false,
        },
    }
}

/// `VisionOcr`: the vision model only transcribes; a text route must exist to
/// carry the translation. Missing either half blocks with a precise reason.
fn select_vision_ocr(context: &RoutingContext) -> RoutingDecision {
    if !context.text_route_available {
        return RoutingDecision {
            selected_mode: TranslationMode::VisionOcr,
            reason_code: "vision_ocr_no_text_route".to_owned(),
            explanation_zh: "视觉识别 + 文本翻译需要同时配置图片模型与文字模型。".to_owned(),
            may_upload_image: false,
        };
    }
    match vision_admission(context) {
        Ok((true, leaves_device)) => RoutingDecision {
            selected_mode: TranslationMode::VisionOcr,
            reason_code: "vision_ocr_two_stage".to_owned(),
            explanation_zh: if !leaves_device {
                "本地视觉服务识别截图文字，译文由文本模型流式生成，图片不离开本机。".to_owned()
            } else if context.vision_endpoint_class == "private" {
                "已授权局域网视觉服务识别截图文字（截图将发送到局域网另一台设备），译文由文本模型流式生成。".to_owned()
            } else {
                "已授权视觉模型识别截图文字（截图将上传），译文由文本模型流式生成。".to_owned()
            },
            may_upload_image: leaves_device,
        },
        Ok((false, _)) => unreachable!("vision_admission never reports usable-but-blocked"),
        Err(reason) => RoutingDecision {
            selected_mode: TranslationMode::VisionOcr,
            reason_code: if context.vision_configured {
                "vision_permission_blocked".to_owned()
            } else {
                "vision_not_configured".to_owned()
            },
            explanation_zh: reason,
            may_upload_image: false,
        },
    }
}

/// Auto stays honestly LOCAL-FIRST: local OCR when available, otherwise a
/// usable vision route. No pseudo-quality signal participates.
fn select_auto_route(context: &RoutingContext) -> RoutingDecision {
    if context.local_ocr_available {
        return local_decision(
            "auto_local_first",
            "自动模式优先使用本地 OCR；截图不会上传，识别出的文字进入统一文字翻译线路。",
        );
    }
    match vision_admission(context) {
        Ok((true, leaves_device)) if !leaves_device => RoutingDecision {
            selected_mode: TranslationMode::VisionDirect,
            reason_code: "auto_local_visual".to_owned(),
            explanation_zh: "本地 OCR 不可用；使用本地视觉服务，图片不离开本机。".to_owned(),
            may_upload_image: false,
        },
        Ok((true, leaves_device)) => {
            if context.text_route_available {
                RoutingDecision {
                    selected_mode: TranslationMode::VisionOcr,
                    reason_code: "auto_remote_vision_two_stage".to_owned(),
                    explanation_zh: if context.vision_endpoint_class == "private" {
                        "本地 OCR 不可用；已授权局域网视觉模型识别截图（截图将发送到局域网另一台设备），译文由文本模型生成。".to_owned()
                    } else {
                        "本地 OCR 不可用；已授权视觉模型识别截图（截图将上传），译文由文本模型生成。".to_owned()
                    },
                    may_upload_image: true,
                }
            } else {
                RoutingDecision {
                    selected_mode: TranslationMode::VisionDirect,
                    reason_code: "auto_remote_vision_direct".to_owned(),
                    explanation_zh: "本地 OCR 不可用；已授权视觉模型直接识别并翻译截图。"
                        .to_owned(),
                    may_upload_image: leaves_device,
                }
            }
        }
        Ok((false, _)) => unreachable!("vision_admission never reports usable-but-blocked"),
        Err(reason) => RoutingDecision {
            selected_mode: TranslationMode::LocalOcr,
            reason_code: "auto_unavailable".to_owned(),
            explanation_zh: format!("本地 OCR 不可用，且没有可用的视觉线路：{reason}"),
            may_upload_image: false,
        },
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

/// Restored translation plus exactly-once integrity findings: placeholders
/// the model dropped, spelled more than once, or unknown indexes that never
/// belonged to this request. Any non-empty finding makes the result
/// incomplete.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RestoredText {
    pub text: String,
    pub dropped_terms: Vec<String>,
    #[serde(default)]
    pub duplicated_terms: Vec<String>,
    #[serde(default)]
    pub unknown_placeholders: Vec<String>,
}

/// Placeholder namespaces. The first is the stable default; the others are
/// only used when the source text already contains `PG_\d{4}`-shaped
/// literals, so a user's own `PG_0000` can never collide with a mask.
const TOKEN_NAMESPACES: [&str; 4] = ["PG", "PGZ", "PGQ", "PGV"];

/// Any placeholder spelling for the given namespace inside the source text.
fn contains_placeholder_namespace(input: &str, namespace: &str) -> bool {
    let marker = format!("{namespace}_");
    let mut cursor = 0;
    while let Some(position) = input[cursor..].find(&marker) {
        let start = cursor + position + marker.len();
        let digits = input
            .get(start..start + 4)
            .is_some_and(|slice| slice.bytes().all(|byte| byte.is_ascii_digit()));
        if digits {
            return true;
        }
        cursor = start;
        while cursor < input.len() && !input.is_char_boundary(cursor) {
            cursor += 1;
        }
        if cursor >= input.len() {
            break;
        }
    }
    false
}

/// Every placeholder-shaped spelling, longest alternative first so a bare
/// `PG_0000` inside `[[PG_0000]]` is never matched separately.
fn placeholder_occurrence_regex() -> &'static Regex {
    static PATTERN: LazyLock<Regex> = LazyLock::new(|| {
        Regex::new(
            r"⟦\s*PG[ZQV]?_\d{4}\s*⟧|\[\[\s*PG[ZQV]?_\d{4}\s*\]\]|\[\s*PG[ZQV]?_\d{4}\s*\]|\{\s*PG[ZQV]?_\d{4}\s*\}|<\s*PG[ZQV]?_\d{4}\s*>|\bPG[ZQV]?_\d{4}\b",
        )
        .expect("placeholder occurrence regex must compile")
    });
    &PATTERN
}

fn parse_placeholder_index(spelling: &str) -> Option<usize> {
    let digits: String = spelling.chars().filter(char::is_ascii_digit).collect();
    if digits.len() == 4 {
        digits.parse().ok()
    } else {
        None
    }
}

/// Placeholder-shaped spellings in `text` whose index was never issued for
/// this request (index ≥ `issued_count`), kept visible and reported.
#[must_use]
pub fn unknown_placeholders_in(text: &str, issued_count: usize) -> Vec<String> {
    placeholder_occurrence_regex()
        .find_iter(text)
        .filter_map(|found| {
            parse_placeholder_index(found.as_str())
                .and_then(|index| (index >= issued_count).then(|| found.as_str().trim().to_owned()))
        })
        .collect()
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

    // If the source already contains placeholder-shaped literals, mask with a
    // namespace the text does not use, so restoration can tell the model's
    // echo of a mask apart from the user's own "PG_0000".
    let namespace = TOKEN_NAMESPACES
        .iter()
        .find(|candidate| !contains_placeholder_namespace(input, candidate))
        .unwrap_or(&TOKEN_NAMESPACES[0]);

    let mut tokens = Vec::new();
    let mut sanitized_text = String::with_capacity(input.len());
    let mut cursor = 0;
    for (start, end) in ranges {
        if start < cursor {
            continue;
        }
        sanitized_text.push_str(&input[cursor..start]);
        let placeholder = format!("⟦{namespace}_{:04}⟧", tokens.len());
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

/// Restores protected tokens with exactly-once semantics.
///
/// Every placeholder must appear exactly once, in any accepted variant.
/// Missing placeholders are reported in `dropped_terms`, placeholders the
/// model echoed twice or more in `duplicated_terms` (the first occurrence is
/// restored, the rest stay visible as evidence), and placeholder indexes this
/// request never issued in `unknown_placeholders`. All three findings make
/// the result incomplete.
#[must_use]
pub fn restore_tokens(translated: &str, tokens: &[ProtectedToken]) -> RestoredText {
    let mut counts = vec![0usize; tokens.len()];
    let mut unknown: Vec<String> = Vec::new();

    let mut matches: Vec<(usize, usize, Option<usize>)> = placeholder_occurrence_regex()
        .find_iter(translated)
        .map(|found| {
            let index = parse_placeholder_index(found.as_str());
            (found.start(), found.end(), index)
        })
        .collect();

    let mut restored = String::with_capacity(translated.len());
    let mut cursor = 0;
    for (start, end, index) in matches.drain(..) {
        restored.push_str(&translated[cursor..start]);
        match index {
            Some(token_index) if token_index < tokens.len() => {
                counts[token_index] += 1;
                if counts[token_index] == 1 {
                    restored.push_str(&tokens[token_index].original);
                } else {
                    // Keep the extra occurrence visible instead of guessing
                    // which one the model meant.
                    restored.push_str(&translated[start..end]);
                }
            }
            _ => {
                if !unknown.contains(&translated[start..end].to_owned()) {
                    unknown.push(translated[start..end].to_owned());
                }
                restored.push_str(&translated[start..end]);
            }
        }
        cursor = end;
    }
    restored.push_str(&translated[cursor..]);

    RestoredText {
        dropped_terms: tokens
            .iter()
            .enumerate()
            .filter(|(index, _)| counts[*index] == 0)
            .map(|(_, token)| token.original.clone())
            .collect(),
        duplicated_terms: tokens
            .iter()
            .enumerate()
            .filter(|(index, _)| counts[*index] > 1)
            .map(|(_, token)| token.original.clone())
            .collect(),
        unknown_placeholders: unknown,
        text: restored,
    }
}

/// Returns the placeholder spellings accepted by [`restore_tokens`].
#[must_use]
pub fn protected_token_variants(placeholder: &str) -> Vec<String> {
    let inner = placeholder
        .trim_start_matches('⟦')
        .trim_end_matches('⟧')
        .trim();
    vec![
        placeholder.to_owned(),
        format!("[{inner}]"),
        format!("[[{inner}]]"),
        format!("{{{inner}}}"),
        format!("<{inner}>"),
        inner.to_owned(),
    ]
}

// ==================== Long-input session planning ====================

/// Per-segment source budget (Unicode scalar values). Sized so one segment's
/// adaptive output budget stays well inside a single request window.
pub const MAX_SEGMENT_CHARS: usize = 800;

/// A session translates at most this many segments; beyond that the request
/// is refused BEFORE anything is sent instead of silently truncating.
pub const MAX_SEGMENTS: usize = 8;

/// Why a source cannot be translated as one budgeted session.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SegmentRejectReason {
    /// A single fenced code block exceeds the per-segment budget; cutting it
    /// would corrupt the code, so the user must shorten it themselves.
    OversizedCodeBlock,
    /// Segmentation would need more than [`MAX_SEGMENTS`] requests; the
    /// session refuses up front rather than half-translating.
    TooManySegments,
}

/// How a source will be translated within the session budget.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SegmentPlan {
    /// Short enough for a single request — the historical behavior, with the
    /// adaptive output budget unchanged.
    Single,
    /// Ordered segments; concatenating their translations in order yields the
    /// full translation. Concatenating the segments themselves yields the
    /// original source byte-for-byte.
    Segments(Vec<String>),
    /// Refused up front: nothing may be sent.
    Rejected(SegmentRejectReason),
}

/// An atomic piece of the source: either a fenced code block (never split)
/// or a prose chunk already reduced to at most `max_chars`.
struct SourceAtom {
    text: String,
    is_code: bool,
}

/// Splits `source` into atoms at blank-line paragraph boundaries, keeping each
/// separator attached to the following atom so concatenation is lossless.
/// Fenced code blocks are atomic. Prose atoms longer than `max_chars` are
/// further split at line, sentence, and finally word boundaries.
fn source_atoms(source: &str, max_chars: usize) -> Result<Vec<SourceAtom>, SegmentRejectReason> {
    let mut atoms: Vec<SourceAtom> = Vec::new();
    let mut rest = source;

    while !rest.is_empty() {
        // A fenced code block opens here: capture through its closing fence
        // (or the end of input) as one atomic piece.
        if let Some(open_end) = fence_end(rest) {
            let close = match rest[open_end..].find("\n```") {
                Some(position) => {
                    // Include the closing fence line itself.
                    let mut end = open_end + position + 4;
                    if rest[end..].starts_with('\r') {
                        end += 1;
                    }
                    if rest[end..].starts_with('\n') {
                        end += 1;
                    }
                    end
                }
                None => rest.len(),
            };
            atoms.push(SourceAtom {
                text: rest[..close].to_owned(),
                is_code: true,
            });
            rest = &rest[close..];
            continue;
        }

        // Prose: take everything up to the next fence or end of input.
        let prose_end = find_next_fence(rest).unwrap_or(rest.len());
        let prose = &rest[..prose_end];
        split_prose(prose, max_chars, &mut atoms);
        rest = &rest[prose_end..];
    }

    // An oversized atomic code block can never fit a segment.
    if atoms
        .iter()
        .any(|atom| atom.is_code && atom.text.chars().count() > max_chars)
    {
        return Err(SegmentRejectReason::OversizedCodeBlock);
    }
    Ok(atoms)
}

/// Position where a code fence opens at the start of a line, relative to
/// `text`, plus the length of the opening fence line.
fn find_next_fence(text: &str) -> Option<usize> {
    let mut cursor = 0;
    while let Some(position) = text[cursor..].find("```") {
        let absolute = cursor + position;
        let at_line_start = absolute == 0 || text.as_bytes()[absolute - 1] == b'\n';
        if at_line_start {
            return Some(absolute);
        }
        cursor = absolute + 3;
    }
    None
}

/// If `text` starts with an opening code fence, returns the offset just after
/// the fence line's newline.
fn fence_end(text: &str) -> Option<usize> {
    if !text.starts_with("```") {
        return None;
    }
    let line_end = text.find('\n')?;
    let mut after = line_end + 1;
    if text[after..].starts_with('\r') {
        after += 1;
    }
    Some(after)
}

/// Splits prose into atoms of at most `max_chars` at the best available
/// boundary, descending through paragraph → line → sentence → space → hard
/// character cut. Hard cuts never split a surrogate pair (Rust `char`
/// iteration cannot, by construction).
fn split_prose(prose: &str, max_chars: usize, atoms: &mut Vec<SourceAtom>) {
    for paragraph in split_keep_separator(prose, "\n\n") {
        if paragraph.chars().count() <= max_chars {
            if !paragraph.is_empty() {
                atoms.push(SourceAtom {
                    text: paragraph,
                    is_code: false,
                });
            }
            continue;
        }
        for line in split_keep_separator(&paragraph, "\n") {
            if line.chars().count() <= max_chars {
                if !line.is_empty() {
                    atoms.push(SourceAtom {
                        text: line,
                        is_code: false,
                    });
                }
                continue;
            }
            for sentence in
                split_keep_separator_any(&line, &["。", "！", "？", ".", "!", "?", "；", ";"])
            {
                if sentence.chars().count() <= max_chars {
                    if !sentence.is_empty() {
                        atoms.push(SourceAtom {
                            text: sentence,
                            is_code: false,
                        });
                    }
                    continue;
                }
                for word in split_keep_separator_any(&sentence, &[" ", "，", ",", "、", "：", ":"])
                {
                    if word.chars().count() <= max_chars {
                        if !word.is_empty() {
                            atoms.push(SourceAtom {
                                text: word,
                                is_code: false,
                            });
                        }
                        continue;
                    }
                    // Last resort for boundary-less prose (CJK runs): hard
                    // cut at a char boundary.
                    for chunk in hard_chunks(&word, max_chars) {
                        atoms.push(SourceAtom {
                            text: chunk,
                            is_code: false,
                        });
                    }
                }
            }
        }
    }
}

/// Splits on a literal separator, keeping the separator attached to the END
/// of the preceding piece, so concatenation reproduces the input exactly.
fn split_keep_separator(text: &str, separator: &str) -> Vec<String> {
    if separator.is_empty() || !text.contains(separator) {
        return if text.is_empty() {
            Vec::new()
        } else {
            vec![text.to_owned()]
        };
    }
    let mut pieces: Vec<String> = Vec::new();
    let mut rest = text;
    while let Some(position) = rest.find(separator) {
        let end = position + separator.len();
        pieces.push(rest[..end].to_owned());
        rest = &rest[end..];
    }
    if !rest.is_empty() {
        pieces.push(rest.to_owned());
    }
    pieces
}

fn split_keep_separator_any(text: &str, separators: &[&str]) -> Vec<String> {
    let mut pieces = vec![text.to_owned()];
    for separator in separators {
        let mut next: Vec<String> = Vec::new();
        for piece in pieces {
            next.extend(split_keep_separator(&piece, separator));
        }
        pieces = next;
    }
    pieces
}

fn hard_chunks(text: &str, max_chars: usize) -> Vec<String> {
    text.chars()
        .collect::<Vec<_>>()
        .chunks(max_chars)
        .map(|chunk| chunk.iter().collect())
        .collect()
}

/// Plans how `source` is translated within one session's request budget.
///
/// Concatenating the returned segments reproduces the source exactly; the
/// shell translates them in order and concatenates the translations.
#[must_use]
pub fn plan_translation_segments(
    source: &str,
    max_segment_chars: usize,
    max_segments: usize,
) -> SegmentPlan {
    if source.chars().count() <= max_segment_chars {
        return SegmentPlan::Single;
    }

    let Ok(atoms) = source_atoms(source, max_segment_chars) else {
        return SegmentPlan::Rejected(SegmentRejectReason::OversizedCodeBlock);
    };

    // Greedy packing: append atoms while the budget holds, otherwise open a
    // new segment. Empty atoms carry no content and no separators.
    let mut segments: Vec<String> = Vec::new();
    let mut current = String::new();
    let mut current_chars = 0usize;
    for atom in &atoms {
        let atom_chars = atom.text.chars().count();
        if current_chars + atom_chars > max_segment_chars && !current.is_empty() {
            segments.push(std::mem::take(&mut current));
            current_chars = 0;
        }
        current.push_str(&atom.text);
        current_chars += atom_chars;
    }
    if !current.is_empty() {
        segments.push(current);
    }

    if segments.len() > max_segments {
        return SegmentPlan::Rejected(SegmentRejectReason::TooManySegments);
    }
    SegmentPlan::Segments(segments)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn context(mode: TranslationMode) -> RoutingContext {
        RoutingContext {
            requested_mode: mode,
            vision_configured: true,
            vision_endpoint_class: "internet".to_owned(),
            image_upload_allowed: true,
            allow_lan_endpoints: false,
            local_ocr_available: true,
            text_route_available: true,
        }
    }

    // ==================== Routing decision table (T13) ====================
    // The AI-RULES/TASKS decision table, exercised row by row through the
    // SAME `select_route` entry the preview and the execution share.

    #[test]
    fn forced_local_never_uploads() {
        let decision = select_route(&context(TranslationMode::LocalOcr));
        assert_eq!(decision.selected_mode, TranslationMode::LocalOcr);
        assert_eq!(decision.reason_code, "forced_local_ocr");
        assert!(!decision.may_upload_image);
    }

    #[test]
    fn forced_local_without_engine_blocks_instead_of_uploading() {
        let mut input = context(TranslationMode::LocalOcr);
        input.local_ocr_available = false;
        let decision = select_route(&input);
        assert!(!decision.may_upload_image);
        assert_eq!(decision.reason_code, "forced_local_ocr_without_engine");
    }

    #[test]
    fn vision_direct_runs_when_admitted() {
        let decision = select_route(&context(TranslationMode::VisionDirect));
        assert_eq!(decision.selected_mode, TranslationMode::VisionDirect);
        assert_eq!(decision.reason_code, "forced_vision");
        assert!(decision.may_upload_image);
    }

    #[test]
    fn vision_direct_blocks_without_configured_vision() {
        let mut input = context(TranslationMode::VisionDirect);
        input.vision_configured = false;
        let decision = select_route(&input);
        assert_eq!(decision.selected_mode, TranslationMode::VisionDirect);
        assert_eq!(decision.reason_code, "vision_not_configured");
        assert!(!decision.may_upload_image);
        // The explicit choice BLOCKS; it must not silently become OCR.
        assert_ne!(decision.selected_mode, TranslationMode::LocalOcr);
    }

    #[test]
    fn vision_direct_blocks_when_upload_not_allowed() {
        let mut input = context(TranslationMode::VisionDirect);
        input.image_upload_allowed = false;
        let decision = select_route(&input);
        assert_eq!(decision.reason_code, "vision_permission_blocked");
        assert!(!decision.may_upload_image);
    }

    #[test]
    fn vision_direct_needs_lan_permission_for_private_endpoint() {
        let mut input = context(TranslationMode::VisionDirect);
        input.vision_endpoint_class = "private".to_owned();
        input.allow_lan_endpoints = false;
        let decision = select_route(&input);
        assert_eq!(decision.reason_code, "vision_permission_blocked");
        assert!(decision.explanation_zh.contains("局域网"));

        input.allow_lan_endpoints = true;
        let admitted = select_route(&input);
        assert_eq!(admitted.selected_mode, TranslationMode::VisionDirect);
        assert!(admitted.may_upload_image);
        assert!(admitted.explanation_zh.contains("局域网"));
    }

    #[test]
    fn loopback_vision_does_not_leave_the_device() {
        let mut input = context(TranslationMode::VisionDirect);
        input.vision_endpoint_class = "loopback".to_owned();
        input.image_upload_allowed = false; // remote-only permission, irrelevant locally
        let decision = select_route(&input);
        assert_eq!(decision.selected_mode, TranslationMode::VisionDirect);
        assert!(
            !decision.may_upload_image,
            "MayUploadImage answers 'does the image leave the device?' — loopback never does"
        );
        assert!(decision.explanation_zh.contains("不离开本机"));
    }

    #[test]
    fn vision_ocr_needs_both_vision_and_a_text_route() {
        let decision = select_route(&context(TranslationMode::VisionOcr));
        assert_eq!(decision.selected_mode, TranslationMode::VisionOcr);
        assert_eq!(decision.reason_code, "vision_ocr_two_stage");

        let mut no_text = context(TranslationMode::VisionOcr);
        no_text.text_route_available = false;
        let blocked = select_route(&no_text);
        assert_eq!(blocked.reason_code, "vision_ocr_no_text_route");
        assert!(!blocked.may_upload_image);
    }

    #[test]
    fn auto_prefers_local_ocr() {
        let decision = select_route(&context(TranslationMode::Auto));
        assert_eq!(decision.selected_mode, TranslationMode::LocalOcr);
        assert_eq!(decision.reason_code, "auto_local_first");
        assert!(!decision.may_upload_image);
    }

    #[test]
    fn auto_without_local_ocr_prefers_two_stage_for_remote_vision() {
        let mut input = context(TranslationMode::Auto);
        input.local_ocr_available = false;
        let decision = select_route(&input);
        assert_eq!(decision.selected_mode, TranslationMode::VisionOcr);
        assert_eq!(decision.reason_code, "auto_remote_vision_two_stage");
        assert!(decision.may_upload_image);
    }

    #[test]
    fn auto_without_local_ocr_uses_local_visual_directly() {
        let mut input = context(TranslationMode::Auto);
        input.local_ocr_available = false;
        input.vision_endpoint_class = "loopback".to_owned();
        let decision = select_route(&input);
        assert_eq!(decision.selected_mode, TranslationMode::VisionDirect);
        assert_eq!(decision.reason_code, "auto_local_visual");
    }

    #[test]
    fn auto_without_local_ocr_and_without_text_route_goes_direct() {
        let mut input = context(TranslationMode::Auto);
        input.local_ocr_available = false;
        input.text_route_available = false;
        let decision = select_route(&input);
        assert_eq!(decision.selected_mode, TranslationMode::VisionDirect);
        assert_eq!(decision.reason_code, "auto_remote_vision_direct");
    }

    #[test]
    fn auto_is_explicit_when_neither_path_exists() {
        let mut input = context(TranslationMode::Auto);
        input.local_ocr_available = false;
        input.vision_configured = false;
        let decision = select_route(&input);
        assert_eq!(decision.reason_code, "auto_unavailable");
        assert!(!decision.may_upload_image);
    }

    #[test]
    fn unknown_endpoint_class_is_refused_conservatively() {
        let mut input = context(TranslationMode::VisionDirect);
        input.vision_endpoint_class = "somewhere-else".to_owned();
        let decision = select_route(&input);
        assert_eq!(decision.reason_code, "vision_permission_blocked");
        assert!(!decision.may_upload_image);
    }

    #[test]
    fn same_word_twice_gets_two_occurrence_tokens() {
        let source = "let v = foo_bar + foo_bar;";
        let protected = protect_tokens(source);
        assert_eq!(
            protected.tokens.len(),
            2,
            "each occurrence is its own token"
        );
        assert_ne!(
            protected.tokens[0].placeholder,
            protected.tokens[1].placeholder
        );

        let echo = format!(
            "检查 {} 和 {} 两次",
            protected.tokens[0].placeholder, protected.tokens[1].placeholder
        );
        let restored = restore_tokens(&echo, &protected.tokens);
        assert_eq!(restored.text, "检查 foo_bar 和 foo_bar 两次");
        assert!(restored.dropped_terms.is_empty());
        assert!(restored.duplicated_terms.is_empty());
        assert!(restored.unknown_placeholders.is_empty());
    }

    #[test]
    fn duplicated_placeholder_is_reported_and_kept_visible() {
        let protected = protect_tokens("let v = foo_bar;");
        assert_eq!(protected.tokens.len(), 1);
        let restored = restore_tokens("结果 PG_0000 加 PG_0000", &protected.tokens);
        assert_eq!(restored.duplicated_terms, vec!["foo_bar"]);
        assert_eq!(restored.text, "结果 foo_bar 加 PG_0000");
    }

    #[test]
    fn dropped_placeholder_is_reported() {
        let protected = protect_tokens("let v = foo_bar;");
        let restored = restore_tokens("译文里占位符没了", &protected.tokens);
        assert_eq!(restored.dropped_terms, vec!["foo_bar"]);
    }

    #[test]
    fn unknown_placeholder_index_is_reported_and_kept() {
        let protected = protect_tokens("let v = foo_bar;");
        let restored = restore_tokens("含未知 [[PG_0007]] 标记", &protected.tokens);
        assert_eq!(restored.unknown_placeholders, vec!["[[PG_0007]]"]);
        assert!(restored.text.contains("[[PG_0007]]"));
    }

    #[test]
    fn source_placeholder_literal_does_not_collide() {
        let source = "PG_0000 是用户写的字面量，调用 foo_bar 试试";
        let protected = protect_tokens(source);
        assert!(
            protected
                .tokens
                .iter()
                .all(|t| !t.placeholder.contains("⟦PG_")),
            "the default namespace must be avoided when the source contains PG_0000"
        );
        // The user's literal must survive untouched after restoration.
        let restored = restore_tokens(&protected.sanitized_text, &protected.tokens);
        assert!(restored.text.contains("PG_0000 是用户写的字面量"));
        assert!(restored.dropped_terms.is_empty());
        assert!(restored.unknown_placeholders.is_empty());
    }

    #[test]
    fn compat_spellings_restore_exactly_once() {
        let protected = protect_tokens("let v = foo_bar;");
        let placeholder_inner = protected.tokens[0]
            .placeholder
            .trim_start_matches('⟦')
            .trim_end_matches('⟧');
        for spelling in [
            protected.tokens[0].placeholder.clone(),
            format!("[{placeholder_inner}]"),
            format!("[[{placeholder_inner}]]"),
            format!("{{{placeholder_inner}}}"),
            format!("<{placeholder_inner}>"),
            placeholder_inner.to_owned(),
        ] {
            let restored = restore_tokens(&format!("值是 {spelling} 结束"), &protected.tokens);
            assert_eq!(restored.text, "值是 foo_bar 结束", "spelling: {spelling}");
            assert!(restored.dropped_terms.is_empty() && restored.duplicated_terms.is_empty());
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
        assert!(ds_settings.text_model.is_empty());
        assert_eq!(ds_settings.api_base_url, "https://api.deepseek.com/v1");
        assert!(!ds_settings.supports_vision);

        // Switch to ollama profile (local)
        config.active_profile_id = "ollama-local".to_owned();
        let ollama_settings = config.to_provider_settings();
        assert!(ollama_settings.targets_local_runtime());
        assert!(ollama_settings.text_model.is_empty());
    }

    #[test]
    fn forced_local_never_uploads_original_guard() {
        // Kept from the earlier contract: a forced LocalOcr on a machine
        // without OCR packs must never turn into an upload.
        let mut input = context(TranslationMode::LocalOcr);
        input.local_ocr_available = false;
        input.image_upload_allowed = false;
        let decision = select_route(&input);
        assert!(!decision.may_upload_image);
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

    /// The shared classification fixture from AI-RULES section 4.1: both the
    /// Rust domain and the C# shell must reach the same conclusion per row.
    #[test]
    fn endpoint_classification_fixture_agrees_with_the_contract() {
        use EndpointClass::{Internet, Loopback, PrivateNetwork};
        let cases: &[(&str, EndpointClass)] = &[
            ("http://localhost:11434", Loopback),
            ("http://127.0.0.1:8080/v1", Loopback),
            ("http://127.10.20.30/v1", Loopback),
            ("http://[::1]:11434", Loopback),
            ("https://localhost.example.com", Internet),
            ("https://api.openai.com/v1", Internet),
            ("http://192.168.1.20:8080", PrivateNetwork),
            ("http://172.16.0.4:8000/v1", PrivateNetwork),
            ("http://172.200.1.1/v1", Internet),
            ("http://10.0.0.5/v1", PrivateNetwork),
            ("http://[fd00::1]:11434", PrivateNetwork),
            ("http://user:secret@10.0.0.5:11434/v1", PrivateNetwork),
            ("http://LOCALHOST:11434", Loopback),
            ("http://10.0.0.5:notaport/", Internet),
            ("not a url at all", Internet),
            ("", Internet),
        ];
        for (url, expected) in cases {
            assert_eq!(&classify_endpoint(url), expected, "fixture: {url}");
        }
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

    // ==================== plan_translation_segments ====================

    #[test]
    fn short_source_plans_single() {
        assert_eq!(
            plan_translation_segments("hello world", MAX_SEGMENT_CHARS, MAX_SEGMENTS),
            SegmentPlan::Single
        );
        // Exactly at the budget is still one request.
        let exact = "a".repeat(MAX_SEGMENT_CHARS);
        assert_eq!(
            plan_translation_segments(&exact, MAX_SEGMENT_CHARS, MAX_SEGMENTS),
            SegmentPlan::Single
        );
    }

    #[test]
    fn long_prose_segments_on_paragraph_boundaries_and_reconstructs_exactly() {
        let paragraph = "This is a technical paragraph about foo_bar_baz and config.json loading. ";
        let source = format!("{}\n\n{}", paragraph.repeat(10), paragraph.repeat(10));
        let plan = plan_translation_segments(&source, MAX_SEGMENT_CHARS, MAX_SEGMENTS);
        let SegmentPlan::Segments(segments) = plan else {
            panic!("expected segments, got {plan:?}");
        };
        assert!(segments.len() >= 2 && segments.len() <= MAX_SEGMENTS);
        for segment in &segments {
            assert!(segment.chars().count() <= MAX_SEGMENT_CHARS);
        }
        // Lossless: concatenation reproduces the source byte-for-byte.
        assert_eq!(segments.concat(), source);
    }

    #[test]
    fn cjk_prose_hard_splits_at_char_boundaries_without_loss() {
        let source = "这是一段没有空格也没有标点的很长中文内容。".repeat(40);
        let plan = plan_translation_segments(&source, MAX_SEGMENT_CHARS, MAX_SEGMENTS);
        let SegmentPlan::Segments(segments) = plan else {
            panic!("expected segments, got {plan:?}");
        };
        for segment in &segments {
            assert!(segment.chars().count() <= MAX_SEGMENT_CHARS);
        }
        assert_eq!(segments.concat(), source, "reconstruction must be lossless");
    }

    #[test]
    fn fenced_code_block_stays_atomic_inside_a_segment() {
        let code = "```rust\nfn main() {\n    println!(\"hello\");\n}\n```\n";
        let prose = "Explains the code above in plain language. ".repeat(25);
        let source = format!("{code}{prose}");
        let plan = plan_translation_segments(&source, MAX_SEGMENT_CHARS, MAX_SEGMENTS);
        let SegmentPlan::Segments(segments) = plan else {
            panic!("expected segments, got {plan:?}");
        };
        assert_eq!(segments.concat(), source);
        // The complete fenced block must appear intact inside ONE segment.
        assert!(
            segments
                .iter()
                .any(|segment| segment.contains("```rust\nfn main()")),
            "the code block must not be split across segments"
        );
    }

    #[test]
    fn oversized_code_block_rejects_before_any_send() {
        let big_code = format!("```text\n{}\n```\n", "x".repeat(2000));
        let plan = plan_translation_segments(&big_code, MAX_SEGMENT_CHARS, MAX_SEGMENTS);
        assert_eq!(
            plan,
            SegmentPlan::Rejected(SegmentRejectReason::OversizedCodeBlock)
        );
    }

    #[test]
    fn unmixed_cjk_mass_rejects_on_segment_count() {
        // ~64KiB of CJK would need ~80 segments: refuse up front.
        let source = "翻".repeat(64 * 1024);
        let plan = plan_translation_segments(&source, MAX_SEGMENT_CHARS, MAX_SEGMENTS);
        assert_eq!(
            plan,
            SegmentPlan::Rejected(SegmentRejectReason::TooManySegments)
        );
    }

    #[test]
    fn long_technical_article_plans_and_preserves_code_and_identifiers() {
        let article = format!(
            "{}\n\n```bash\nset -euo pipefail\ncargo test --workspace\n```\n\n{}",
            "The build failed with a FileNotFoundError for config.json. ".repeat(15),
            "Retry with the correct path foo_bar_baz and verify the layout. ".repeat(15)
        );
        assert!(article.chars().count() > MAX_SEGMENT_CHARS);
        let plan = plan_translation_segments(&article, MAX_SEGMENT_CHARS, MAX_SEGMENTS);
        let SegmentPlan::Segments(segments) = plan else {
            panic!("expected segments, got {plan:?}");
        };
        assert!(segments.len() <= MAX_SEGMENTS);
        assert_eq!(segments.concat(), article, "lossless reconstruction");
        assert!(
            segments
                .iter()
                .any(|segment| segment.contains("cargo test --workspace")),
            "the fenced commands must stay intact"
        );
    }

    #[test]
    fn unclosed_fence_takes_rest_of_input_atomically() {
        let source = format!("```python\nprint('hi')\n{}", "more code\n".repeat(200));
        let plan = plan_translation_segments(&source, MAX_SEGMENT_CHARS, MAX_SEGMENTS);
        // The unterminated block is atomic and oversized → explicit reject.
        assert_eq!(
            plan,
            SegmentPlan::Rejected(SegmentRejectReason::OversizedCodeBlock)
        );
    }
}
