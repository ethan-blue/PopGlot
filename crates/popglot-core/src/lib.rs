//! Cross-platform `PopGlot` application core.
//!
//! The host shell supplies a configuration directory and platform services.
//! This crate never discovers Windows folders, invokes Win32, or performs a
//! network request implicitly.

pub mod provider;

use popglot_domain::{ProviderSettings, TranslationMode, protect_tokens, restore_tokens};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use tokio_util::sync::CancellationToken;

use provider::{
    ImageInput, ProviderClient, ProviderError, ProviderErrorKind, TranslationInput,
    TranslationResponse, TransportLimits, provider_for, validate_provider_settings,
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

    /// Translates user-selected text through the configured Provider.
    ///
    /// # Errors
    ///
    /// Returns a classified Provider error for invalid text, privacy gates,
    /// cancellation, transport failures, or invalid model output.
    pub async fn translate_text(
        &self,
        api_key: &str,
        source: &str,
        cancellation: &CancellationToken,
    ) -> Result<TranslationResponse, ProviderError> {
        let source = source.trim();
        if source.is_empty() || source.len() > 64 * 1024 {
            return Err(ProviderError {
                kind: ProviderErrorKind::Configuration,
                message: "选中文本必须大于 0 且不超过 64 KiB。".to_owned(),
                status_code: None,
                retryable: false,
            });
        }
        let protected = protect_tokens(source);
        let mut response = self
            .execute_translation(
                api_key,
                TranslationInput::Text {
                    source: protected.sanitized_text,
                },
                cancellation,
            )
            .await?;
        if !protected.tokens.is_empty() {
            response.result.translated_text = restore_tokens(
                &response.result.translated_text,
                &protected.tokens,
            )
            .map_err(|_| ProviderError {
                kind: ProviderErrorKind::InvalidResponse,
                message: "模型修改或遗漏了受保护的代码元素；为避免展示错误标识符，本次结果已拒绝。"
                    .to_owned(),
                status_code: None,
                retryable: false,
            })?;
            response.result.protected_terms = protected
                .tokens
                .into_iter()
                .map(|token| token.original)
                .collect();
        }
        Ok(response)
    }

    /// Translates one captured screenshot through the configured vision Provider.
    ///
    /// # Errors
    ///
    /// Returns a classified Provider error for privacy gates, unsupported modes,
    /// image limits, cancellation, transport failures, or invalid model output.
    pub async fn translate_vision(
        &self,
        api_key: &str,
        media_type: &str,
        image: Vec<u8>,
        cancellation: &CancellationToken,
    ) -> Result<TranslationResponse, ProviderError> {
        if self.settings.mode == TranslationMode::LocalOcr {
            return Err(ProviderError {
                kind: ProviderErrorKind::UnsupportedInput,
                message: "当前选择了本地 OCR，但本机 OCR 适配器尚未安装。请切换视觉直译，或等待本地 OCR 模块。".to_owned(),
                status_code: None,
                retryable: false,
            });
        }
        if !self.settings.allow_image_upload_in_auto {
            return Err(ProviderError {
                kind: ProviderErrorKind::NetworkDisabled,
                message: "截图上传未获授权，且本地 OCR 适配器尚未安装；未发送图片。".to_owned(),
                status_code: None,
                retryable: false,
            });
        }
        self.execute_translation(
            api_key,
            TranslationInput::Vision {
                prompt: "Accurately transcribe and translate this screenshot into Chinese. Preserve every code token, identifier, command, path, URL, version, and error code exactly.".to_owned(),
                image: ImageInput::Bytes {
                    media_type: media_type.to_owned(),
                    data: image,
                },
            },
            cancellation,
        )
        .await
    }

    async fn execute_translation(
        &self,
        api_key: &str,
        input: TranslationInput,
        cancellation: &CancellationToken,
    ) -> Result<TranslationResponse, ProviderError> {
        let provider = provider_for(self.settings.provider_type);
        let request_id = format!(
            "translation-{}",
            REQUEST_SEQUENCE.fetch_add(1, Ordering::Relaxed)
        );
        self.provider_client
            .execute(
                provider.as_ref(),
                &self.settings,
                api_key,
                &request_id,
                &input,
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

    #[tokio::test]
    async fn selected_text_respects_validation_and_network_gate() {
        let suffix = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let directory = std::env::temp_dir().join(format!("popglot-selection-test-{suffix}"));
        let core = AppCore::open(&directory).expect("open core");
        let cancellation = CancellationToken::new();
        let empty = core
            .translate_text("key", "   ", &cancellation)
            .await
            .expect_err("empty selection must fail");
        assert_eq!(empty.kind, ProviderErrorKind::Configuration);
        let disabled = core
            .translate_text("key", "selected text", &cancellation)
            .await
            .expect_err("default network gate must fail");
        assert_eq!(disabled.kind, ProviderErrorKind::NetworkDisabled);
        fs::remove_dir_all(directory).expect("remove isolated test directory");
    }

    #[tokio::test]
    async fn screenshot_route_never_bypasses_local_or_upload_permission() {
        let suffix = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let directory = std::env::temp_dir().join(format!("popglot-vision-test-{suffix}"));
        let mut core = AppCore::open(&directory).expect("open core");
        let cancellation = CancellationToken::new();
        core.settings.mode = TranslationMode::LocalOcr;
        let local = core
            .translate_vision("key", "image/png", vec![1], &cancellation)
            .await
            .expect_err("local mode must not upload");
        assert_eq!(local.kind, ProviderErrorKind::UnsupportedInput);
        core.settings.mode = TranslationMode::VisionDirect;
        let unapproved = core
            .translate_vision("key", "image/png", vec![1], &cancellation)
            .await
            .expect_err("unapproved screenshot must not upload");
        assert_eq!(unapproved.kind, ProviderErrorKind::NetworkDisabled);
        fs::remove_dir_all(directory).expect("remove isolated test directory");
    }
}
