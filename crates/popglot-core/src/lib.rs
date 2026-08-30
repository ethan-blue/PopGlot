//! Cross-platform `PopGlot` application core.
//!
//! The host shell supplies a configuration directory and platform services.
//! This crate never discovers Windows folders, invokes Win32, or performs a
//! network request implicitly.

pub mod provider;

use popglot_domain::{
    LanguagePair, ProviderSettings, RoutingContext, RoutingDecision, TranslationMode,
    protect_tokens, restore_tokens, select_route,
};
use std::fs::{self, File};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use tokio_util::sync::CancellationToken;

use provider::{
    ImageInput, ProviderClient, ProviderError, ProviderErrorKind, TranslationRequest,
    TranslationResponse, TransportLimits, provider_for, validate_provider_settings,
};

const SETTINGS_FILE: &str = "provider-settings.json";
const MAX_SOURCE_BYTES: usize = 64 * 1024;
static REQUEST_SEQUENCE: AtomicU64 = AtomicU64::new(1);

#[derive(Debug)]
pub struct AppCore {
    settings_path: PathBuf,
    settings: ProviderSettings,
    provider_client: ProviderClient,
    startup_notice: Option<String>,
}

impl AppCore {
    /// Opens an application core rooted at a shell-provided configuration directory.
    ///
    /// # Errors
    ///
    /// Returns [`CoreError`] when the directory cannot be created or existing
    /// settings cannot be read. Corrupt JSON files are backed up safely with a
    /// timestamp suffix so user data is never lost.
    pub fn open(config_directory: impl AsRef<Path>) -> Result<Self, CoreError> {
        let directory = config_directory.as_ref();
        fs::create_dir_all(directory)?;
        let settings_path = directory.join(SETTINGS_FILE);
        let mut startup_notice = None;
        let settings = if settings_path.exists() {
            let json = fs::read_to_string(&settings_path)?;
            match serde_json::from_str::<ProviderSettings>(&json) {
                Ok(loaded) => loaded,
                Err(err) => {
                    let timestamp = std::time::SystemTime::now()
                        .duration_since(std::time::UNIX_EPOCH)
                        .map_or(0, |d| d.as_secs());
                    let corrupt_name = format!("provider-settings.corrupt-{timestamp}.json");
                    let corrupt_path = directory.join(&corrupt_name);
                    let _ = fs::rename(&settings_path, &corrupt_path);
                    tracing::warn!(error = %err, "Corrupted provider settings backed up to {:?}", corrupt_path);
                    // The shell surfaces this so a silent-looking reset is never
                    // mistaken for the user's own configuration.
                    startup_notice = Some(format!(
                        "provider-settings.json 无法解析，已重置为默认设置；原文件保留为 {corrupt_name}，可从中恢复你的配置。"
                    ));
                    ProviderSettings::default()
                }
            }
        } else {
            ProviderSettings::default()
        };
        let provider_client = ProviderClient::new(Self::limits_for(&settings))?;
        Ok(Self {
            settings_path,
            settings,
            provider_client,
            startup_notice,
        })
    }

    #[must_use]
    pub fn limits_for(settings: &ProviderSettings) -> TransportLimits {
        TransportLimits {
            accept_invalid_certs: settings.allow_insecure_tls,
            ..TransportLimits::default()
        }
    }

    #[must_use]
    pub fn settings(&self) -> &ProviderSettings {
        &self.settings
    }

    #[must_use]
    pub fn provider_client(&self) -> &ProviderClient {
        &self.provider_client
    }

    /// Returns and clears the one-shot startup notice (e.g. corrupted settings
    /// were backed up), so the shell can tell the user what happened.
    pub fn take_startup_notice(&mut self) -> Option<String> {
        self.startup_notice.take()
    }

    /// Validates and atomically persists non-secret provider settings.
    ///
    /// Writes to a temporary file, flushes to disk, backs up the previous version,
    /// and performs an atomic replace so process interruption never corrupts the configuration.
    ///
    /// # Errors
    ///
    /// Returns [`CoreError`] for invalid endpoints or an unsuccessful file write.
    pub fn save_settings(&mut self, settings: ProviderSettings) -> Result<(), CoreError> {
        validate_settings(&settings)?;
        validate_provider_settings(&settings)?;
        let json = serde_json::to_string_pretty(&settings)?;

        let temp_path = self.settings_path.with_extension("tmp");
        let bak_path = self.settings_path.with_extension("bak");

        {
            // Flush to disk before the rename: a crash between write and
            // replace must never leave a truncated temp file as the "new" copy.
            let mut file = File::create(&temp_path)?;
            file.write_all(json.as_bytes())?;
            file.sync_all()?;
        }
        if self.settings_path.exists() {
            let _ = fs::copy(&self.settings_path, &bak_path);
        }
        fs::rename(&temp_path, &self.settings_path)?;

        if settings.allow_insecure_tls != self.settings.allow_insecure_tls {
            self.provider_client = ProviderClient::new(Self::limits_for(&settings))?;
        }
        self.settings = settings;
        Ok(())
    }

    /// Explains which screenshot pipeline the current settings would choose.
    ///
    /// The shell asks before capturing so that routing lives in one place
    /// instead of being re-derived by each platform front-end.
    #[must_use]
    pub fn plan_screenshot_route(
        &self,
        local_ocr_available: bool,
        credential_present: bool,
    ) -> RoutingDecision {
        select_route(&RoutingContext::from_settings(
            &self.settings,
            local_ocr_available,
            credential_present,
        ))
    }

    /// Sends a minimal text-only connection test using active settings.
    ///
    /// # Errors
    ///
    /// Returns [`ProviderError`] for transport or configuration failures.
    pub async fn test_connection(
        &self,
        api_key: &str,
        cancellation: &CancellationToken,
    ) -> Result<TranslationResponse, ProviderError> {
        let request_id = format!(
            "connection-test-{}",
            REQUEST_SEQUENCE.fetch_add(1, Ordering::Relaxed)
        );
        Self::execute_test_connection_snapshot(
            &self.settings,
            &self.provider_client,
            api_key,
            &request_id,
            cancellation,
        )
        .await
    }

    /// Tests connection using an in-memory draft configuration.
    ///
    /// Does not mutate the active core settings, change the stored credential,
    /// or write to disk.
    ///
    /// # Errors
    ///
    /// Returns [`ProviderError`] for invalid draft settings or transport failures.
    pub async fn test_connection_draft(
        draft_settings: &ProviderSettings,
        api_key: &str,
        request_id: Option<&str>,
        cancellation: &CancellationToken,
    ) -> Result<TranslationResponse, ProviderError> {
        validate_settings(draft_settings)
            .map_err(|e| ProviderError::new(ProviderErrorKind::Configuration, e.to_string()))?;
        validate_provider_settings(draft_settings)?;
        let client = ProviderClient::new(Self::limits_for(draft_settings))?;
        let generated_id = format!(
            "connection-draft-{}",
            REQUEST_SEQUENCE.fetch_add(1, Ordering::Relaxed)
        );
        let req_id = request_id.unwrap_or(&generated_id);
        Self::execute_test_connection_snapshot(
            draft_settings,
            &client,
            api_key,
            req_id,
            cancellation,
        )
        .await
    }

    async fn execute_test_connection_snapshot(
        settings: &ProviderSettings,
        client: &ProviderClient,
        api_key: &str,
        request_id: &str,
        cancellation: &CancellationToken,
    ) -> Result<TranslationResponse, ProviderError> {
        let provider = provider_for(settings.provider_type);
        let request = TranslationRequest::text(
            "Connection test",
            LanguagePair::new("en", &settings.target_language),
        )
        .with_explanation(false);
        client
            .execute(
                provider.as_ref(),
                settings,
                api_key,
                request_id,
                &request,
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
        languages: &LanguagePair,
        cancellation: &CancellationToken,
    ) -> Result<TranslationResponse, ProviderError> {
        let request_id = format!(
            "translation-{}",
            REQUEST_SEQUENCE.fetch_add(1, Ordering::Relaxed)
        );
        Self::execute_translate_text_snapshot(
            &self.settings,
            &self.provider_client,
            api_key,
            source,
            languages,
            &request_id,
            cancellation,
        )
        .await
    }

    /// Pure lock-free text translation snapshot execution.
    ///
    /// # Errors
    ///
    /// Returns [`ProviderError`] for transport or parsing failures.
    #[allow(clippy::too_many_arguments)]
    pub async fn execute_translate_text_snapshot(
        settings: &ProviderSettings,
        client: &ProviderClient,
        api_key: &str,
        source: &str,
        languages: &LanguagePair,
        request_id: &str,
        cancellation: &CancellationToken,
    ) -> Result<TranslationResponse, ProviderError> {
        let source = source.trim();
        if source.is_empty() || source.len() > MAX_SOURCE_BYTES {
            return Err(ProviderError::new(
                ProviderErrorKind::Configuration,
                format!(
                    "选中文本必须大于 0 且不超过 {} KiB。",
                    MAX_SOURCE_BYTES / 1024
                ),
            ));
        }

        if !settings.protect_code_tokens {
            let provider = provider_for(settings.provider_type);
            let request = TranslationRequest::text(source, languages.clone())
                .with_explanation(settings.include_explanation);
            return client
                .execute(
                    provider.as_ref(),
                    settings,
                    api_key,
                    request_id,
                    &request,
                    cancellation,
                )
                .await;
        }

        let protected = protect_tokens(source);
        let provider = provider_for(settings.provider_type);
        let request = TranslationRequest::text(protected.sanitized_text, languages.clone())
            .with_explanation(settings.include_explanation);
        let mut response = client
            .execute(
                provider.as_ref(),
                settings,
                api_key,
                request_id,
                &request,
                cancellation,
            )
            .await?;

        if !protected.tokens.is_empty() {
            let restored = restore_tokens(&response.result.translated_text, &protected.tokens);
            response.result.translated_text = restored.text;
            response.result.protected_terms = protected
                .tokens
                .iter()
                .map(|token| token.original.clone())
                .collect();
            if !restored.dropped_terms.is_empty() {
                response.result.is_partial = true;
                response.result.warnings.push(format!(
                    "模型未在译文中保留这些代码元素：{}",
                    restored.dropped_terms.join("、")
                ));
            }
        }
        Ok(response)
    }

    /// Translates one captured screenshot through the configured vision Provider.
    ///
    /// # Errors
    ///
    /// Returns [`ProviderError`] for transport or parsing failures.
    pub async fn translate_vision(
        &self,
        api_key: &str,
        media_type: &str,
        image: Vec<u8>,
        languages: &LanguagePair,
        cancellation: &CancellationToken,
    ) -> Result<TranslationResponse, ProviderError> {
        let request_id = format!(
            "translation-{}",
            REQUEST_SEQUENCE.fetch_add(1, Ordering::Relaxed)
        );
        Self::execute_translate_vision_snapshot(
            &self.settings,
            &self.provider_client,
            api_key,
            // No separate vision key on the legacy in-core path: without a
            // dedicated vision provider the text key authenticates both.
            "",
            media_type,
            image,
            languages,
            &request_id,
            cancellation,
        )
        .await
    }

    /// Pure lock-free vision translation snapshot execution.
    ///
    /// # Errors
    ///
    /// Returns [`ProviderError`] for transport or parsing failures.
    #[allow(clippy::too_many_arguments)]
    pub async fn execute_translate_vision_snapshot(
        settings: &ProviderSettings,
        client: &ProviderClient,
        api_key: &str,
        vision_api_key: &str,
        media_type: &str,
        image: Vec<u8>,
        languages: &LanguagePair,
        request_id: &str,
        cancellation: &CancellationToken,
    ) -> Result<TranslationResponse, ProviderError> {
        if settings.mode == TranslationMode::LocalOcr {
            return Err(ProviderError::new(
                ProviderErrorKind::UnsupportedInput,
                "当前模式为本地 OCR，截图不应上传；请由外壳使用本地 OCR 后翻译文字。",
            ));
        }
        // The upload consent protects images leaving the device. A loopback
        // vision provider still receives the image, but it remains local and
        // must work in Network Off / Safe Mode.
        if !settings.allow_image_upload_in_auto && !settings.targets_local_runtime() {
            return Err(ProviderError::new(
                ProviderErrorKind::NetworkDisabled,
                "隐私设置未授权上传截图；未发送图片。可在设置中勾选“允许截图上传”，或使用本地 OCR 模式。",
            ));
        }

        // A dedicated vision provider replaces the text provider's connection
        // details wholesale - base URL, protocol, endpoint, model, headers -
        // and is authenticated with its own key, never the text key.
        let (effective_settings, effective_key) =
            resolve_vision_runtime(settings, api_key, vision_api_key);

        let provider = provider_for(effective_settings.provider_type);
        let request = TranslationRequest::vision(
            ImageInput::Bytes {
                media_type: media_type.to_owned(),
                data: image,
            },
            languages.clone(),
        )
        .with_explanation(effective_settings.include_explanation);
        client
            .execute(
                provider.as_ref(),
                &effective_settings,
                &effective_key,
                request_id,
                &request,
                cancellation,
            )
            .await
    }
}

/// Resolves provider details and authentication together. A dedicated vision
/// route never inherits the text credential: an empty vision credential must
/// fail closed in provider validation instead of leaking the text key to a
/// different host.
fn resolve_vision_runtime(
    settings: &ProviderSettings,
    text_api_key: &str,
    vision_api_key: &str,
) -> (ProviderSettings, String) {
    if let Some(vision) = &settings.vision_provider {
        (
            settings.with_vision_provider(vision),
            vision_api_key.to_owned(),
        )
    } else {
        (settings.clone(), text_api_key.to_owned())
    }
}

fn validate_settings(settings: &ProviderSettings) -> Result<(), CoreError> {
    let base = settings.api_base_url.trim();
    if base.is_empty() {
        return Err(CoreError::InvalidSettings(
            "API Base URL 不能为空。".to_owned(),
        ));
    }
    if !(base.starts_with("https://")
        || (base.starts_with("http://") && popglot_domain::is_local_base_url(base)))
    {
        return Err(CoreError::InvalidSettings(
            "API Base URL 必须使用 HTTPS；仅本机或局域网服务 (如 Ollama/LM Studio) 允许 HTTP。"
                .to_owned(),
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
    use std::collections::BTreeMap;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn scratch_directory(label: &str) -> PathBuf {
        let suffix = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        std::env::temp_dir().join(format!("popglot-{label}-{suffix}"))
    }

    #[test]
    fn dedicated_vision_route_never_inherits_text_credential() {
        let settings = ProviderSettings {
            vision_provider: Some(popglot_domain::VisionProviderSettings {
                provider_type: popglot_domain::ProviderType::GeminiGenerateContent,
                api_base_url: "https://vision.example".to_owned(),
                vision_endpoint: "/v1beta/models/{model}:generateContent".to_owned(),
                vision_model: "configured-vision".to_owned(),
                extra_headers: BTreeMap::default(),
                anthropic_version: String::new(),
                allow_insecure_tls: false,
            }),
            ..ProviderSettings::default()
        };

        let (vision_settings, key) = resolve_vision_runtime(&settings, "text-secret", "");
        assert_eq!(vision_settings.api_base_url, "https://vision.example");
        assert!(key.is_empty(), "missing vision key must fail closed");

        let (_, independent_key) =
            resolve_vision_runtime(&settings, "text-secret", "vision-secret");
        assert_eq!(independent_key, "vision-secret");
    }

    fn pair() -> LanguagePair {
        LanguagePair::new("auto", "zh-CN")
    }

    #[test]
    fn settings_round_trip_without_secret_value() {
        let directory = scratch_directory("core-test");
        let mut core = AppCore::open(&directory).expect("open core");
        let settings = ProviderSettings {
            text_model: "demo-text".to_owned(),
            api_key_configured: true,
            target_language: "ja".to_owned(),
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
    fn corrupt_settings_fall_back_to_defaults_and_backup_corrupt_file() {
        let directory = scratch_directory("corrupt-test");
        fs::create_dir_all(&directory).expect("create directory");
        fs::write(directory.join(SETTINGS_FILE), "{ not json").expect("write corrupt settings");
        let mut core = AppCore::open(&directory).expect("core must still open");
        assert_eq!(core.settings(), &ProviderSettings::default());

        // The shell is told what happened instead of the reset being silent.
        let notice = core.take_startup_notice().expect("startup notice");
        assert!(notice.contains("provider-settings.corrupt-"), "{notice}");
        assert!(core.take_startup_notice().is_none(), "notice is one-shot");

        // Corrupt file must be preserved with a backup name
        let entries = fs::read_dir(&directory)
            .expect("read dir")
            .filter_map(std::result::Result::ok)
            .map(|e| e.file_name().to_string_lossy().to_string())
            .collect::<Vec<_>>();
        assert!(
            entries
                .iter()
                .any(|name| name.starts_with("provider-settings.corrupt-"))
        );

        fs::remove_dir_all(directory).expect("remove isolated test directory");
    }

    #[test]
    fn healthy_settings_produce_no_startup_notice() {
        let directory = scratch_directory("notice-test");
        let mut core = AppCore::open(&directory).expect("open core");
        assert!(core.take_startup_notice().is_none());
        fs::remove_dir_all(directory).expect("remove isolated test directory");
    }

    #[test]
    fn http_is_rejected_for_public_hosts_and_allowed_for_loopback() {
        let directory = scratch_directory("url-test");
        let mut core = AppCore::open(&directory).expect("open core");
        let public_http = ProviderSettings {
            api_base_url: "http://api.example.com/v1".to_owned(),
            ..ProviderSettings::default()
        };
        assert!(core.save_settings(public_http).is_err());
        let loopback = ProviderSettings {
            api_base_url: "http://127.0.0.1:11434/v1".to_owned(),
            ..ProviderSettings::default()
        };
        assert!(core.save_settings(loopback).is_ok());
        fs::remove_dir_all(directory).expect("remove isolated test directory");
    }

    #[tokio::test]
    async fn selected_text_respects_validation_and_network_gate() {
        let directory = scratch_directory("selection-test");
        let mut core = AppCore::open(&directory).expect("open core");
        let cancellation = CancellationToken::new();
        let empty = core
            .translate_text("key", "   ", &pair(), &cancellation)
            .await
            .expect_err("empty selection must fail");
        assert_eq!(empty.kind, ProviderErrorKind::Configuration);
        core.settings.network_enabled = false;
        let disabled = core
            .translate_text("key", "selected text", &pair(), &cancellation)
            .await
            .expect_err("disabled network gate must fail");
        assert_eq!(disabled.kind, ProviderErrorKind::NetworkDisabled);
        fs::remove_dir_all(directory).expect("remove isolated test directory");
    }

    #[tokio::test]
    async fn screenshot_route_never_bypasses_local_or_upload_permission() {
        let directory = scratch_directory("vision-test");
        let mut core = AppCore::open(&directory).expect("open core");
        let cancellation = CancellationToken::new();
        core.settings.mode = TranslationMode::LocalOcr;
        let local = core
            .translate_vision("key", "image/png", vec![1], &pair(), &cancellation)
            .await
            .expect_err("local mode must not upload");
        assert_eq!(local.kind, ProviderErrorKind::UnsupportedInput);
        core.settings.mode = TranslationMode::VisionDirect;
        core.settings.allow_image_upload_in_auto = false;
        let unapproved = core
            .translate_vision("key", "image/png", vec![1], &pair(), &cancellation)
            .await
            .expect_err("unapproved screenshot must not upload");
        assert_eq!(unapproved.kind, ProviderErrorKind::NetworkDisabled);
        fs::remove_dir_all(directory).expect("remove isolated test directory");
    }

    #[test]
    fn screenshot_route_plan_reflects_credentials_and_permissions() {
        let directory = scratch_directory("route-test");
        let mut core = AppCore::open(&directory).expect("open core");
        core.settings.mode = TranslationMode::Auto;
        // Fresh installs declare no model; this test exercises the route
        // matrix with a vision-capable provider configured.
        core.settings.supports_vision = true;
        core.settings.vision_model = "vision-test-model".to_owned();
        core.settings.allow_image_upload_in_auto = true;

        // No credential yet: the vision model is unreachable, so local OCR wins.
        let without_key = core.plan_screenshot_route(true, false);
        assert_eq!(without_key.selected_mode, TranslationMode::LocalOcr);
        assert!(!without_key.may_upload_image);

        // Credential present and upload permitted: vision becomes reachable and
        // simple text still prefers local OCR.
        let with_key = core.plan_screenshot_route(true, true);
        assert_eq!(with_key.reason_code, "auto_local_first");

        // No OCR language pack: vision is the only route that can produce text.
        let no_ocr = core.plan_screenshot_route(false, true);
        assert_eq!(no_ocr.selected_mode, TranslationMode::VisionDirect);

        // The offline switch outranks everything.
        core.settings.safe_dev_mode = true;
        let offline = core.plan_screenshot_route(true, true);
        assert!(!offline.may_upload_image);

        fs::remove_dir_all(directory).expect("remove isolated test directory");
    }
}
