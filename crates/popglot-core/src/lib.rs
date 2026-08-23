//! Cross-platform `PopGlot` application core.
//!
//! The host shell supplies a configuration directory and platform services.
//! This crate never discovers Windows folders, invokes Win32, or performs a
//! network request implicitly.

pub mod provider;

use popglot_domain::{
    ProviderSettings, RoutingContext, RoutingDecision, TranslationMode, protect_tokens,
    select_route,
};
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use tokio_util::sync::CancellationToken;

use provider::{
    ProviderClient, ProviderError, TranslationInput, TranslationResponse, TransportLimits,
    provider_for, validate_provider_settings,
};

const SETTINGS_FILE: &str = "provider-settings.json";
static REQUEST_SEQUENCE: AtomicU64 = AtomicU64::new(1);

#[derive(Debug)]
pub struct AppCore {
    settings_path: PathBuf,
    settings: ProviderSettings,
    provider_client: ProviderClient,
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
            provider_client: ProviderClient::new(TransportLimits::default())?,
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
        validate_provider_settings(&settings)?;
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
                } else if !self.settings.network_enabled {
                    "当前未启用模型网络请求。"
                } else {
                    "仅用户主动触发的连接测试可发送最小文本；预览本身不会发送网络请求。"
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

    /// Sends a minimal text-only connection test after every privacy gate passes.
    ///
    /// # Errors
    ///
    /// Returns [`ProviderError`] for disabled networking, missing credentials,
    /// protocol, timeout, cancellation, HTTP, or response parsing errors.
    pub async fn test_connection(
        &self,
        api_key: &str,
        cancellation: &CancellationToken,
    ) -> Result<TranslationResponse, ProviderError> {
        let provider = provider_for(self.settings.provider_type);
        let request_id = format!(
            "connection-test-{}",
            REQUEST_SEQUENCE.fetch_add(1, Ordering::Relaxed)
        );
        self.provider_client
            .execute(
                provider.as_ref(),
                &self.settings,
                api_key,
                &request_id,
                &TranslationInput::Text {
                    source: "Translate the phrase 'Connection test' into Chinese.".to_owned(),
                },
                cancellation,
            )
            .await
    }
}

fn validate_settings(settings: &ProviderSettings) -> Result<(), CoreError> {
    let base = settings.api_base_url.trim();
    if !(base.starts_with("https://")
        || base.starts_with("http://localhost")
        || base.starts_with("http://127.0.0.1"))
    {
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

#[derive(Debug, thiserror::Error)]
pub enum CoreError {
    #[error("I/O error: {0}")]
    Io(#[from] std::io::Error),
    #[error("settings JSON error: {0}")]
    Json(#[from] serde_json::Error),
    #[error("invalid settings: {0}")]
    InvalidSettings(String),
    #[error("provider error: {0}")]
    Provider(#[from] ProviderError),
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

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
