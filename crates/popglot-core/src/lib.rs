//! Cross-platform `PopGlot` application core.
//!
//! The host shell supplies a configuration directory and platform services.
//! This crate never discovers Windows folders, invokes Win32, or performs a
//! network request implicitly.

use popglot_domain::{
    ProviderSettings, RoutingContext, RoutingDecision, TranslationMode, protect_tokens,
    select_route,
};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::fs;
use std::path::{Path, PathBuf};

const SETTINGS_FILE: &str = "provider-settings.json";

#[derive(Debug)]
pub struct AppCore {
    settings_path: PathBuf,
    settings: ProviderSettings,
}

impl AppCore {
    /// Opens an application core rooted at a shell-provided configuration directory.
    ///
    /// # Errors
    ///
    /// Returns [`CoreError`] when the directory cannot be created or existing
    /// settings cannot be read and decoded.
    pub fn open(config_directory: impl AsRef<Path>) -> Result<Self, CoreError> {
        let directory = config_directory.as_ref();
        fs::create_dir_all(directory)?;
        let settings_path = directory.join(SETTINGS_FILE);
        let settings = if settings_path.exists() {
            let json = fs::read_to_string(&settings_path)?;
            serde_json::from_str(&json)?
        } else {
            ProviderSettings::default()
        };
        Ok(Self {
            settings_path,
            settings,
        })
    }

    #[must_use]
    pub fn settings(&self) -> &ProviderSettings {
        &self.settings
    }

    /// Validates and persists non-secret provider settings.
    ///
    /// # Errors
    ///
    /// Returns [`CoreError`] for invalid endpoints or an unsuccessful file write.
    pub fn save_settings(&mut self, settings: ProviderSettings) -> Result<(), CoreError> {
        validate_settings(&settings)?;
        let json = serde_json::to_string_pretty(&settings)?;
        fs::write(&self.settings_path, json)?;
        self.settings = settings;
        Ok(())
    }

    /// Produces a deterministic vertical-slice result without network or image upload.
    #[must_use]
    pub fn preview(&self, request: &PreviewRequest) -> PreviewResult {
        let context = RoutingContext {
            requested_mode: request.mode,
            vision_configured: self.settings.vision_is_configured(),
            image_upload_allowed: self.settings.allow_image_upload_in_auto,
            looks_like_code: request.looks_like_code,
            complex_layout: request.complex_layout,
            image_quality: request.image_quality,
            ocr_confidence: request.ocr_confidence,
        };
        let decision = select_route(&context);
        let protected = protect_tokens(&request.sample_text);
        let requires_configuration = !self.settings.text_is_configured()
            || (decision.selected_mode == TranslationMode::VisionDirect
                && !self.settings.vision_is_configured());

        PreviewResult {
            decision,
            title: "PopGlot 开发模式预览".to_owned(),
            translated_text: if requires_configuration {
                "尚未配置可用模型。截图未上传，请在设置中填写 API Base URL、文本模型与视觉模型。"
                    .to_owned()
            } else {
                "安全开发模式已完成路由演示；真实 OCR、视觉请求和翻译尚未发送。".to_owned()
            },
            explanation: format!(
                "检测并保护了 {} 个代码或技术元素。{}",
                protected.tokens.len(),
                if self.settings.safe_dev_mode {
                    "当前 Safe Dev Mode 禁止任何外部 API 请求。"
                } else {
                    "网络传输仍未在此初始切片中启用。"
                }
            ),
            protected_terms: protected
                .tokens
                .into_iter()
                .map(|token| token.original)
                .collect(),
            requires_configuration,
            network_request_sent: false,
        }
    }
}

fn validate_settings(settings: &ProviderSettings) -> Result<(), CoreError> {
    let base = settings.api_base_url.trim();
    if !(base.starts_with("https://") || base.starts_with("http://localhost")) {
        return Err(CoreError::InvalidSettings(
            "API Base URL 必须使用 HTTPS；本地开发仅允许 http://localhost。".to_owned(),
        ));
    }
    Ok(())
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PreviewRequest {
    pub mode: TranslationMode,
    pub sample_text: String,
    pub looks_like_code: bool,
    pub complex_layout: bool,
    pub image_quality: f32,
    pub ocr_confidence: f32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PreviewResult {
    pub decision: RoutingDecision,
    pub title: String,
    pub translated_text: String,
    pub explanation: String,
    pub protected_terms: Vec<String>,
    pub requires_configuration: bool,
    pub network_request_sent: bool,
}

/// Transport-neutral representation of an OpenAI-compatible request.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PreparedProviderRequest {
    pub api_path: String,
    pub body: Value,
    pub contains_image: bool,
}

pub trait TranslationProvider {
    fn prepare_text_request(&self, source: &str) -> PreparedProviderRequest;
    fn prepare_vision_request(&self, prompt: &str, image_data_url: &str)
    -> PreparedProviderRequest;
}

/// Builds compatible request envelopes only; sending is delegated to an audited transport.
#[derive(Debug, Clone)]
pub struct OpenAiCompatibleProvider {
    pub text_model: String,
    pub vision_model: String,
}

impl TranslationProvider for OpenAiCompatibleProvider {
    fn prepare_text_request(&self, source: &str) -> PreparedProviderRequest {
        PreparedProviderRequest {
            api_path: "/chat/completions".to_owned(),
            contains_image: false,
            body: json!({
                "model": self.text_model,
                "stream": true,
                "messages": [{
                    "role": "user",
                    "content": source,
                }],
            }),
        }
    }

    fn prepare_vision_request(
        &self,
        prompt: &str,
        image_data_url: &str,
    ) -> PreparedProviderRequest {
        PreparedProviderRequest {
            api_path: "/chat/completions".to_owned(),
            contains_image: true,
            body: json!({
                "model": self.vision_model,
                "stream": true,
                "messages": [{
                    "role": "user",
                    "content": [
                        {"type": "text", "text": prompt},
                        {"type": "image_url", "image_url": {"url": image_data_url}},
                    ],
                }],
            }),
        }
    }
}

#[derive(Debug, thiserror::Error)]
pub enum CoreError {
    #[error("I/O error: {0}")]
    Io(#[from] std::io::Error),
    #[error("settings JSON error: {0}")]
    Json(#[from] serde_json::Error),
    #[error("invalid settings: {0}")]
    InvalidSettings(String),
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    #[test]
    fn provider_builds_distinct_text_and_vision_envelopes() {
        let provider = OpenAiCompatibleProvider {
            text_model: "text-model".to_owned(),
            vision_model: "vision-model".to_owned(),
        };
        let text = provider.prepare_text_request("hello");
        let vision = provider.prepare_vision_request("translate", "data:image/png;base64,AAAA");
        assert!(!text.contains_image);
        assert!(vision.contains_image);
        assert_eq!(vision.body["model"], "vision-model");
    }

    #[test]
    fn settings_round_trip_without_secret_value() {
        let suffix = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let directory = std::env::temp_dir().join(format!("popglot-core-test-{suffix}"));
        let mut core = AppCore::open(&directory).expect("open core");
        let settings = ProviderSettings {
            text_model: "demo-text".to_owned(),
            api_key_configured: true,
            ..ProviderSettings::default()
        };
        core.save_settings(settings.clone()).expect("save settings");
        let reopened = AppCore::open(&directory).expect("reopen core");
        assert_eq!(reopened.settings(), &settings);
        let persisted = fs::read_to_string(directory.join(SETTINGS_FILE)).expect("read file");
        assert!(!persisted.contains("api_key\":"));
        fs::remove_dir_all(directory).expect("remove isolated test directory");
    }

    #[test]
    fn preview_never_sends_network_request() {
        let suffix = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let directory = std::env::temp_dir().join(format!("popglot-preview-test-{suffix}"));
        let core = AppCore::open(&directory).expect("open core");
        let result = core.preview(&PreviewRequest {
            mode: TranslationMode::Auto,
            sample_text: "NullReferenceException in getUserProfile".to_owned(),
            looks_like_code: true,
            complex_layout: false,
            image_quality: 0.9,
            ocr_confidence: 0.9,
        });
        assert!(!result.network_request_sent);
        assert!(
            result
                .protected_terms
                .contains(&"getUserProfile".to_owned())
        );
        fs::remove_dir_all(directory).expect("remove isolated test directory");
    }
}
