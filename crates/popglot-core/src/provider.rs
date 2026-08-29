//! Real, resource-bounded model provider protocols and HTTP transport.

use base64::Engine as _;
use futures_util::StreamExt as _;
use popglot_domain::{LanguagePair, ProviderSettings, ProviderType, is_local_base_url};
use reqwest::header::{HeaderMap, HeaderName, HeaderValue, RETRY_AFTER};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::fmt;
use std::time::{Duration, Instant};
use tokio_util::sync::CancellationToken;

pub const MAX_IMAGE_BYTES: usize = 8 * 1024 * 1024;
pub const MAX_REQUEST_BYTES: usize = 12 * 1024 * 1024;
pub const MAX_RESPONSE_BYTES: usize = 4 * 1024 * 1024;
const MAX_MODEL_OUTPUT_TOKENS: u32 = 1_200;
const MAX_EXTRA_HEADERS: usize = 16;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TranslationInput {
    Text { source: String },
    Vision { image: ImageInput },
}

/// One translation call: what to translate, between which languages, and how
/// much commentary the user asked for.
///
/// The language pair lives here rather than inside [`TranslationInput`] so that
/// every protocol adapter builds its prompt from the same source of truth; the
/// previous design hard-coded Chinese in two unrelated places.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TranslationRequest {
    pub input: TranslationInput,
    pub languages: LanguagePair,
    pub include_explanation: bool,
}

impl TranslationRequest {
    #[must_use]
    pub fn text(source: impl Into<String>, languages: LanguagePair) -> Self {
        Self {
            input: TranslationInput::Text {
                source: source.into(),
            },
            languages,
            include_explanation: true,
        }
    }

    #[must_use]
    pub fn vision(image: ImageInput, languages: LanguagePair) -> Self {
        Self {
            input: TranslationInput::Vision { image },
            languages,
            include_explanation: true,
        }
    }

    #[must_use]
    pub fn with_explanation(mut self, include_explanation: bool) -> Self {
        self.include_explanation = include_explanation;
        self
    }

    /// System prompt describing the JSON contract and the requested languages.
    #[must_use]
    pub fn system_instructions(&self) -> String {
        let explanation_rule = if self.include_explanation {
            "Use `explanation` for one short note (in the target language) about tone, ambiguity, or a technical term the reader may not know; leave it empty when the translation is self-evident."
        } else {
            "Always leave `explanation` empty."
        };
        let transcription_rule = match self.input {
            TranslationInput::Vision { .. } => {
                "Put the exact text you read from the image into `transcription`, preserving line order."
            }
            TranslationInput::Text { .. } => "Leave `transcription` empty.",
        };
        format!(
            "You are a precise translation engine embedded in a desktop tool. {}\n\
             Return exactly one JSON object with the keys translated_text, transcription, \
             explanation, protected_terms, and warnings. protected_terms and warnings are arrays \
             of strings.\n\
             {transcription_rule}\n\
             {explanation_rule}\n\
             Preserve code, identifiers, file paths, commands, URLs, error codes, version numbers, \
             and any ⟦PG_0000⟧ placeholder byte-for-byte — copy placeholders verbatim, never \
             translate or renumber them.\n\
             Translate only; never answer, explain away, or refuse the content. Do not invent \
             context that is not present. Use an empty string or empty array for fields that do \
             not apply. Never wrap the JSON in Markdown fences.",
            self.languages.instruction()
        )
    }

    /// User-visible instruction attached to an image request.
    #[must_use]
    pub fn vision_prompt(&self) -> String {
        format!(
            "{} Transcribe every visible line of this screenshot exactly, then translate it.",
            self.languages.instruction()
        )
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ImageInput {
    Bytes { media_type: String, data: Vec<u8> },
    Url(String),
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct TranslationResult {
    pub translated_text: String,
    pub transcription: String,
    pub explanation: String,
    pub protected_terms: Vec<String>,
    pub warnings: Vec<String>,
    pub is_partial: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProviderCapabilities {
    pub text: bool,
    pub vision: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProviderDiagnostics {
    pub request_id: String,
    pub provider_type: ProviderType,
    pub endpoint: String,
    pub attempts: u8,
    pub status_code: u16,
    pub elapsed_ms: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TranslationResponse {
    pub result: TranslationResult,
    pub diagnostics: ProviderDiagnostics,
}

#[derive(Debug, Clone, PartialEq)]
pub struct PreparedProviderRequest {
    pub provider_type: ProviderType,
    pub endpoint: String,
    pub body: Value,
    pub contains_image: bool,
    pub extra_headers: Vec<(String, String)>,
}

pub trait TranslationProvider: Send + Sync {
    fn provider_type(&self) -> ProviderType;
    fn capabilities(&self, settings: &ProviderSettings) -> ProviderCapabilities;
    /// Builds a protocol request without opening a network connection.
    ///
    /// # Errors
    ///
    /// Returns a classified error for missing capability, model, unsafe input,
    /// unsupported image representation, or size limit violations.
    fn prepare(
        &self,
        settings: &ProviderSettings,
        request: &TranslationRequest,
    ) -> Result<PreparedProviderRequest, ProviderError>;
    /// Parses a protocol response into the common result DTO.
    ///
    /// # Errors
    ///
    /// Returns a classified error for invalid JSON, empty output, safety
    /// blocking, or a model response that violates the structured contract.
    fn parse(&self, response: &[u8]) -> Result<TranslationResult, ProviderError>;
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ProviderErrorKind {
    Configuration,
    NetworkDisabled,
    MissingCredential,
    UnsupportedInput,
    RequestTooLarge,
    Authentication,
    RateLimited,
    Timeout,
    Cancelled,
    Transport,
    Server,
    InvalidResponse,
    SafetyBlocked,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProviderError {
    pub kind: ProviderErrorKind,
    pub message: String,
    pub status_code: Option<u16>,
    pub retryable: bool,
}

impl ProviderError {
    pub fn new(kind: ProviderErrorKind, message: impl Into<String>) -> Self {
        Self {
            kind,
            message: message.into(),
            status_code: None,
            retryable: false,
        }
    }

    fn with_status(mut self, status_code: u16) -> Self {
        self.status_code = Some(status_code);
        self
    }

    fn retryable(mut self) -> Self {
        self.retryable = true;
        self
    }
}

impl fmt::Display for ProviderError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{}", self.message)
    }
}

impl std::error::Error for ProviderError {}

#[derive(Debug, Clone)]
pub struct TransportLimits {
    pub connect_timeout: Duration,
    pub total_timeout: Duration,
    pub max_response_bytes: usize,
    pub max_retries: u8,
    pub retry_delay: Duration,
    pub accept_invalid_certs: bool,
}

impl Default for TransportLimits {
    fn default() -> Self {
        Self {
            connect_timeout: Duration::from_secs(5),
            total_timeout: Duration::from_secs(45),
            max_response_bytes: MAX_RESPONSE_BYTES,
            max_retries: 1,
            retry_delay: Duration::from_millis(250),
            accept_invalid_certs: false,
        }
    }
}

#[derive(Debug, Clone)]
pub struct ProviderClient {
    client: reqwest::Client,
    limits: TransportLimits,
}

impl ProviderClient {
    /// Creates a reusable client with a bounded connection timeout and no cookies.
    ///
    /// # Errors
    ///
    /// Returns [`ProviderError`] if the TLS/HTTP client cannot be constructed.
    pub fn new(limits: TransportLimits) -> Result<Self, ProviderError> {
        let mut builder = reqwest::Client::builder()
            .connect_timeout(limits.connect_timeout)
            .user_agent(concat!("PopGlot/", env!("CARGO_PKG_VERSION")));
        if limits.accept_invalid_certs {
            // Opt-in for private relays reached by bare IP or self-signed TLS;
            // the toggle is explicit user consent in provider settings.
            builder = builder.danger_accept_invalid_certs(true);
        }
        let client = builder.build().map_err(|error| {
            ProviderError::new(
                ProviderErrorKind::Configuration,
                format!("无法建立 HTTP 客户端：{error}"),
            )
        })?;
        Ok(Self { client, limits })
    }

    /// Executes one text or vision translation with cancellation and bounded retry.
    ///
    /// # Errors
    ///
    /// Returns a classified [`ProviderError`] for configuration, privacy, HTTP,
    /// timeout, cancellation, size, or response parsing failures.
    pub async fn execute(
        &self,
        provider: &dyn TranslationProvider,
        settings: &ProviderSettings,
        api_key: &str,
        request_id: &str,
        request: &TranslationRequest,
        cancellation: &CancellationToken,
    ) -> Result<TranslationResponse, ProviderError> {
        validate_execution(settings, api_key, request_id)?;
        validate_input_capability(settings, &request.input)?;
        let prepared = provider.prepare(settings, request)?;
        let body = serde_json::to_vec(&prepared.body).map_err(|error| {
            ProviderError::new(
                ProviderErrorKind::Configuration,
                format!("无法序列化 Provider 请求：{error}"),
            )
        })?;
        if body.len() > MAX_REQUEST_BYTES {
            return Err(ProviderError::new(
                ProviderErrorKind::RequestTooLarge,
                format!(
                    "请求正文超过 {} MiB 上限。",
                    MAX_REQUEST_BYTES / 1024 / 1024
                ),
            ));
        }
        let url = build_url(&settings.api_base_url, &prepared.endpoint)?;
        let headers = build_headers(settings, &prepared)?;
        let started = Instant::now();

        let operation = self.execute_with_retry(
            provider,
            api_key,
            request_id,
            &prepared,
            url,
            headers,
            body,
            cancellation,
            started,
        );

        tokio::select! {
            () = cancellation.cancelled() => Err(cancelled_error()),
            result = tokio::time::timeout(self.limits.total_timeout, operation) => {
                result.unwrap_or_else(|_| Err(ProviderError::new(
                    ProviderErrorKind::Timeout,
                    format!("Provider 请求超过 {} 秒总超时。", self.limits.total_timeout.as_secs()),
                )))
            }
        }
    }

    #[allow(clippy::too_many_arguments)]
    async fn execute_with_retry(
        &self,
        provider: &dyn TranslationProvider,
        api_key: &str,
        request_id: &str,
        prepared: &PreparedProviderRequest,
        url: reqwest::Url,
        headers: HeaderMap,
        body: Vec<u8>,
        cancellation: &CancellationToken,
        started: Instant,
    ) -> Result<TranslationResponse, ProviderError> {
        let mut attempt = 0_u8;
        loop {
            attempt = attempt.saturating_add(1);
            tracing::info!(
                request_id,
                provider = ?prepared.provider_type,
                endpoint = prepared.endpoint,
                attempt,
                contains_image = prepared.contains_image,
                "provider request started"
            );
            let mut builder = self
                .client
                .post(url.clone())
                .headers(headers.clone())
                .header(reqwest::header::CONTENT_TYPE, "application/json")
                .body(body.clone());
            builder = match prepared.provider_type {
                ProviderType::AnthropicMessages => builder.header("x-api-key", api_key),
                ProviderType::GeminiGenerateContent => builder.header("x-goog-api-key", api_key),
                ProviderType::OpenAiCompatible | ProviderType::OpenAiResponses => {
                    builder.bearer_auth(api_key)
                }
            };

            let response = tokio::select! {
                () = cancellation.cancelled() => return Err(cancelled_error()),
                response = builder.send() => response,
            };
            match response {
                Ok(response) => {
                    let status = response.status();
                    if status.is_success() {
                        let bytes =
                            read_bounded(response, self.limits.max_response_bytes, cancellation)
                                .await?;
                        let result = provider.parse(&bytes)?;
                        return Ok(TranslationResponse {
                            result,
                            diagnostics: ProviderDiagnostics {
                                request_id: request_id.to_owned(),
                                provider_type: prepared.provider_type,
                                endpoint: prepared.endpoint.clone(),
                                attempts: attempt,
                                status_code: status.as_u16(),
                                elapsed_ms: elapsed_millis(started),
                            },
                        });
                    }

                    let retry_after = retry_after(&response);
                    let status_code = status.as_u16();
                    let error_bytes = read_bounded(response, 64 * 1024, cancellation).await?;
                    let error = classify_http_error(status_code, &error_bytes);
                    if attempt <= self.limits.max_retries && error.retryable {
                        self.wait_before_retry(retry_after, cancellation).await?;
                        continue;
                    }
                    return Err(error);
                }
                Err(error) => {
                    let retryable = error.is_connect() || error.is_timeout();
                    if attempt <= self.limits.max_retries && retryable {
                        self.wait_before_retry(None, cancellation).await?;
                        continue;
                    }
                    return Err(ProviderError {
                        kind: if error.is_timeout() {
                            ProviderErrorKind::Timeout
                        } else {
                            ProviderErrorKind::Transport
                        },
                        message: if error.is_timeout() {
                            "Provider 请求超时。".to_owned()
                        } else {
                            "无法连接模型提供商，请检查网络与 Base URL。".to_owned()
                        },
                        status_code: None,
                        retryable,
                    });
                }
            }
        }
    }

    async fn wait_before_retry(
        &self,
        retry_after: Option<Duration>,
        cancellation: &CancellationToken,
    ) -> Result<(), ProviderError> {
        let delay = retry_after
            .unwrap_or(self.limits.retry_delay)
            .min(Duration::from_secs(2));
        tokio::select! {
            () = cancellation.cancelled() => Err(cancelled_error()),
            () = tokio::time::sleep(delay) => Ok(()),
        }
    }
}

#[must_use]
pub fn provider_for(provider_type: ProviderType) -> Box<dyn TranslationProvider> {
    match provider_type {
        ProviderType::OpenAiCompatible => Box::new(OpenAiChatProvider),
        ProviderType::OpenAiResponses => Box::new(OpenAiResponsesProvider),
        ProviderType::AnthropicMessages => Box::new(AnthropicMessagesProvider),
        ProviderType::GeminiGenerateContent => Box::new(GeminiGenerateContentProvider),
    }
}

/// Validates non-secret endpoint and header settings without opening a network connection.
///
/// # Errors
///
/// Returns [`ProviderError`] when a URL, endpoint, capability, Anthropic version,
/// or custom header would be unsafe or unusable.
pub fn validate_provider_settings(settings: &ProviderSettings) -> Result<(), ProviderError> {
    if !settings.supports_text && !settings.supports_vision {
        return Err(ProviderError::new(
            ProviderErrorKind::Configuration,
            "至少需要启用文本或视觉能力之一。",
        ));
    }
    for endpoint in [&settings.text_endpoint, &settings.vision_endpoint] {
        let resolved = if settings.provider_type == ProviderType::GeminiGenerateContent {
            endpoint.replace("{model}", "model")
        } else {
            endpoint.clone()
        };
        build_url(&settings.api_base_url, &resolved)?;
    }
    let mut headers = extra_headers(settings);
    if settings.provider_type == ProviderType::AnthropicMessages {
        headers.push((
            "anthropic-version".to_owned(),
            settings.anthropic_version.clone(),
        ));
    }
    let prepared = PreparedProviderRequest {
        provider_type: settings.provider_type,
        endpoint: settings.text_endpoint.clone(),
        body: Value::Null,
        contains_image: false,
        extra_headers: headers,
    };
    build_headers(settings, &prepared)?;
    Ok(())
}

struct OpenAiChatProvider;
struct OpenAiResponsesProvider;
struct AnthropicMessagesProvider;
struct GeminiGenerateContentProvider;

impl TranslationProvider for OpenAiChatProvider {
    fn provider_type(&self) -> ProviderType {
        ProviderType::OpenAiCompatible
    }

    fn capabilities(&self, settings: &ProviderSettings) -> ProviderCapabilities {
        configured_capabilities(settings)
    }

    fn prepare(
        &self,
        settings: &ProviderSettings,
        request: &TranslationRequest,
    ) -> Result<PreparedProviderRequest, ProviderError> {
        let (model, endpoint, user_content, contains_image) = match &request.input {
            TranslationInput::Text { source } => (
                require_model(&settings.text_model, "文本")?,
                &settings.text_endpoint,
                Value::String(source.clone()),
                false,
            ),
            TranslationInput::Vision { image } => (
                require_model(&settings.vision_model, "视觉")?,
                &settings.vision_endpoint,
                json!([
                    {"type": "text", "text": request.vision_prompt()},
                    {"type": "image_url", "image_url": {"url": image_data_url(image)?}},
                ]),
                true,
            ),
        };
        Ok(PreparedProviderRequest {
            provider_type: self.provider_type(),
            endpoint: endpoint.clone(),
            contains_image,
            extra_headers: extra_headers(settings),
            body: json!({
                "model": model,
                "stream": false,
                "temperature": 0.1,
                "max_tokens": MAX_MODEL_OUTPUT_TOKENS,
                "messages": [
                    {"role": "system", "content": request.system_instructions()},
                    {"role": "user", "content": user_content},
                ],
            }),
        })
    }

    fn parse(&self, response: &[u8]) -> Result<TranslationResult, ProviderError> {
        let value = parse_json(response)?;
        let content = value
            .pointer("/choices/0/message/content")
            .and_then(Value::as_str)
            .ok_or_else(|| {
                invalid_response("Chat Completions 响应缺少 choices[0].message.content。")
            })?;
        parse_translation_json(content)
    }
}

impl TranslationProvider for OpenAiResponsesProvider {
    fn provider_type(&self) -> ProviderType {
        ProviderType::OpenAiResponses
    }

    fn capabilities(&self, settings: &ProviderSettings) -> ProviderCapabilities {
        configured_capabilities(settings)
    }

    fn prepare(
        &self,
        settings: &ProviderSettings,
        request: &TranslationRequest,
    ) -> Result<PreparedProviderRequest, ProviderError> {
        let (model, endpoint, content, contains_image) = match &request.input {
            TranslationInput::Text { source } => (
                require_model(&settings.text_model, "文本")?,
                &settings.text_endpoint,
                json!([{"type": "input_text", "text": source}]),
                false,
            ),
            TranslationInput::Vision { image } => (
                require_model(&settings.vision_model, "视觉")?,
                &settings.vision_endpoint,
                json!([
                    {"type": "input_text", "text": request.vision_prompt()},
                    {"type": "input_image", "image_url": image_data_url(image)?, "detail": "auto"},
                ]),
                true,
            ),
        };
        Ok(PreparedProviderRequest {
            provider_type: self.provider_type(),
            endpoint: endpoint.clone(),
            contains_image,
            extra_headers: extra_headers(settings),
            body: json!({
                "model": model,
                "store": false,
                "max_output_tokens": MAX_MODEL_OUTPUT_TOKENS,
                "instructions": request.system_instructions(),
                "input": [{"role": "user", "content": content}],
            }),
        })
    }

    fn parse(&self, response: &[u8]) -> Result<TranslationResult, ProviderError> {
        let value = parse_json(response)?;
        if let Some(text) = value.get("output_text").and_then(Value::as_str) {
            return parse_translation_json(text);
        }
        let text = value
            .get("output")
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
            .flat_map(|item| {
                item.get("content")
                    .and_then(Value::as_array)
                    .into_iter()
                    .flatten()
            })
            .find_map(|content| {
                (content.get("type").and_then(Value::as_str) == Some("output_text"))
                    .then(|| content.get("text").and_then(Value::as_str))
                    .flatten()
            })
            .ok_or_else(|| invalid_response("Responses API 响应缺少 output_text 内容。"))?;
        parse_translation_json(text)
    }
}

impl TranslationProvider for AnthropicMessagesProvider {
    fn provider_type(&self) -> ProviderType {
        ProviderType::AnthropicMessages
    }

    fn capabilities(&self, settings: &ProviderSettings) -> ProviderCapabilities {
        configured_capabilities(settings)
    }

    fn prepare(
        &self,
        settings: &ProviderSettings,
        request: &TranslationRequest,
    ) -> Result<PreparedProviderRequest, ProviderError> {
        let (model, endpoint, content, contains_image) = match &request.input {
            TranslationInput::Text { source } => (
                require_model(&settings.text_model, "文本")?,
                &settings.text_endpoint,
                json!([{"type": "text", "text": source}]),
                false,
            ),
            TranslationInput::Vision { image } => {
                let image_block = match image {
                    ImageInput::Bytes { media_type, data } => {
                        validate_image(media_type, data.len())?;
                        json!({
                            "type": "image",
                            "source": {
                                "type": "base64",
                                "media_type": media_type,
                                "data": base64::engine::general_purpose::STANDARD.encode(data),
                            }
                        })
                    }
                    ImageInput::Url(url) => {
                        validate_image_url(url)?;
                        json!({"type": "image", "source": {"type": "url", "url": url}})
                    }
                };
                (
                    require_model(&settings.vision_model, "视觉")?,
                    &settings.vision_endpoint,
                    json!([image_block, {"type": "text", "text": request.vision_prompt()}]),
                    true,
                )
            }
        };
        let mut headers = extra_headers(settings);
        headers.push((
            "anthropic-version".to_owned(),
            settings.anthropic_version.clone(),
        ));
        Ok(PreparedProviderRequest {
            provider_type: self.provider_type(),
            endpoint: endpoint.clone(),
            contains_image,
            extra_headers: headers,
            body: json!({
                "model": model,
                "max_tokens": MAX_MODEL_OUTPUT_TOKENS,
                "system": request.system_instructions(),
                "messages": [{"role": "user", "content": content}],
            }),
        })
    }

    fn parse(&self, response: &[u8]) -> Result<TranslationResult, ProviderError> {
        let value = parse_json(response)?;
        let text = value
            .get("content")
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
            .find_map(|content| {
                (content.get("type").and_then(Value::as_str) == Some("text"))
                    .then(|| content.get("text").and_then(Value::as_str))
                    .flatten()
            })
            .ok_or_else(|| invalid_response("Anthropic 响应缺少 text 内容块。"))?;
        parse_translation_json(text)
    }
}

impl TranslationProvider for GeminiGenerateContentProvider {
    fn provider_type(&self) -> ProviderType {
        ProviderType::GeminiGenerateContent
    }

    fn capabilities(&self, settings: &ProviderSettings) -> ProviderCapabilities {
        configured_capabilities(settings)
    }

    fn prepare(
        &self,
        settings: &ProviderSettings,
        request: &TranslationRequest,
    ) -> Result<PreparedProviderRequest, ProviderError> {
        let (model, endpoint_template, parts, contains_image) = match &request.input {
            TranslationInput::Text { source } => (
                require_model(&settings.text_model, "文本")?,
                &settings.text_endpoint,
                json!([{"text": source}]),
                false,
            ),
            TranslationInput::Vision { image } => {
                let ImageInput::Bytes { media_type, data } = image else {
                    return Err(ProviderError::new(
                        ProviderErrorKind::UnsupportedInput,
                        "Gemini 原生适配器仅发送本地 inline_data，不代为下载远程图片。",
                    ));
                };
                validate_image(media_type, data.len())?;
                (
                    require_model(&settings.vision_model, "视觉")?,
                    &settings.vision_endpoint,
                    json!([
                        {
                            "inline_data": {
                                "mime_type": media_type,
                                "data": base64::engine::general_purpose::STANDARD.encode(data),
                            }
                        },
                        {"text": request.vision_prompt()},
                    ]),
                    true,
                )
            }
        };
        validate_model_path_segment(model)?;
        let endpoint = endpoint_template.replace("{model}", model);
        Ok(PreparedProviderRequest {
            provider_type: self.provider_type(),
            endpoint,
            contains_image,
            extra_headers: extra_headers(settings),
            body: json!({
                "system_instruction": {"parts": [{"text": request.system_instructions()}]},
                "contents": [{"role": "user", "parts": parts}],
                "generationConfig": {
                    "temperature": 0.1,
                    "maxOutputTokens": MAX_MODEL_OUTPUT_TOKENS,
                    "responseMimeType": "application/json",
                },
            }),
        })
    }

    fn parse(&self, response: &[u8]) -> Result<TranslationResult, ProviderError> {
        let value = parse_json(response)?;
        if let Some(reason) = value
            .pointer("/promptFeedback/blockReason")
            .and_then(Value::as_str)
        {
            return Err(ProviderError::new(
                ProviderErrorKind::SafetyBlocked,
                format!("Gemini 因安全策略阻止了请求：{reason}"),
            ));
        }
        let candidates = value
            .get("candidates")
            .and_then(Value::as_array)
            .ok_or_else(|| invalid_response("Gemini 响应缺少 candidates。"))?;
        if candidates.is_empty() {
            return Err(invalid_response("Gemini 未返回候选结果。"));
        }
        let candidate = &candidates[0];
        if matches!(
            candidate.get("finishReason").and_then(Value::as_str),
            Some("SAFETY" | "PROHIBITED_CONTENT" | "IMAGE_SAFETY")
        ) {
            return Err(ProviderError::new(
                ProviderErrorKind::SafetyBlocked,
                "Gemini 因安全策略未返回翻译内容。",
            ));
        }
        let text = candidate
            .pointer("/content/parts")
            .and_then(Value::as_array)
            .into_iter()
            .flatten()
            .filter_map(|part| part.get("text").and_then(Value::as_str))
            .collect::<String>();
        if text.trim().is_empty() {
            return Err(invalid_response("Gemini 候选结果没有文本内容。"));
        }
        parse_translation_json(&text)
    }
}

fn configured_capabilities(settings: &ProviderSettings) -> ProviderCapabilities {
    ProviderCapabilities {
        text: settings.supports_text && settings.text_is_configured(),
        vision: settings.supports_vision && settings.vision_is_configured(),
    }
}

fn require_model<'a>(model: &'a str, label: &str) -> Result<&'a str, ProviderError> {
    if model.trim().is_empty() {
        Err(ProviderError::new(
            ProviderErrorKind::Configuration,
            format!("尚未配置{label}模型。"),
        ))
    } else {
        Ok(model.trim())
    }
}

fn validate_model_path_segment(model: &str) -> Result<(), ProviderError> {
    if model.len() > 128
        || !model.chars().all(|character| {
            character.is_ascii_alphanumeric() || matches!(character, '.' | '_' | '-')
        })
    {
        return Err(ProviderError::new(
            ProviderErrorKind::Configuration,
            "Gemini 模型名包含不允许的路径字符。",
        ));
    }
    Ok(())
}

fn image_data_url(image: &ImageInput) -> Result<String, ProviderError> {
    match image {
        ImageInput::Bytes { media_type, data } => {
            validate_image(media_type, data.len())?;
            Ok(format!(
                "data:{media_type};base64,{}",
                base64::engine::general_purpose::STANDARD.encode(data)
            ))
        }
        ImageInput::Url(url) => {
            validate_image_url(url)?;
            Ok(url.clone())
        }
    }
}

fn validate_image(media_type: &str, byte_count: usize) -> Result<(), ProviderError> {
    if !matches!(media_type, "image/png" | "image/jpeg" | "image/webp") {
        return Err(ProviderError::new(
            ProviderErrorKind::UnsupportedInput,
            "仅支持 PNG、JPEG 和 WebP 图片。",
        ));
    }
    if byte_count == 0 || byte_count > MAX_IMAGE_BYTES {
        return Err(ProviderError::new(
            ProviderErrorKind::RequestTooLarge,
            format!(
                "图片必须大于 0 且不超过 {} MiB。",
                MAX_IMAGE_BYTES / 1024 / 1024
            ),
        ));
    }
    Ok(())
}

fn validate_image_url(url: &str) -> Result<(), ProviderError> {
    let parsed = reqwest::Url::parse(url)
        .map_err(|_| ProviderError::new(ProviderErrorKind::UnsupportedInput, "图片 URL 无效。"))?;
    if parsed.scheme() != "https" {
        return Err(ProviderError::new(
            ProviderErrorKind::UnsupportedInput,
            "远程图片 URL 必须使用 HTTPS。",
        ));
    }
    Ok(())
}

fn extra_headers(settings: &ProviderSettings) -> Vec<(String, String)> {
    settings
        .extra_headers
        .iter()
        .map(|(name, value)| (name.clone(), value.clone()))
        .collect()
}

fn validate_execution(
    settings: &ProviderSettings,
    api_key: &str,
    request_id: &str,
) -> Result<(), ProviderError> {
    // Master offline switch: checked first so that no other permission can
    // re-enable outbound traffic while the user believes they are offline.
    if settings.safe_dev_mode {
        return Err(ProviderError::new(
            ProviderErrorKind::NetworkDisabled,
            "安全离线模式已开启；未发送任何模型请求。可在「翻译服务」中关闭它。",
        ));
    }
    if !settings.network_enabled {
        return Err(ProviderError::new(
            ProviderErrorKind::NetworkDisabled,
            "网络访问未启用；未发送任何 Provider 请求。请在设置中勾选「启用大模型网络翻译」。",
        ));
    }
    if api_key.trim().is_empty() && !is_local_base_url(&settings.api_base_url) {
        return Err(ProviderError::new(
            ProviderErrorKind::MissingCredential,
            "未配置 API Key；请先在设置中填入对应服务的 API Key 或使用本地模型。",
        ));
    }
    if request_id.trim().is_empty() || request_id.len() > 128 {
        return Err(ProviderError::new(
            ProviderErrorKind::Configuration,
            "请求 ID 为空或过长。",
        ));
    }
    Ok(())
}

fn validate_input_capability(
    settings: &ProviderSettings,
    input: &TranslationInput,
) -> Result<(), ProviderError> {
    let supported = match input {
        TranslationInput::Text { .. } => settings.supports_text,
        TranslationInput::Vision { .. } => settings.supports_vision,
    };
    if supported {
        Ok(())
    } else {
        Err(ProviderError::new(
            ProviderErrorKind::UnsupportedInput,
            "当前 Provider 配置未启用此输入能力；未发送网络请求。",
        ))
    }
}

fn build_url(base: &str, endpoint: &str) -> Result<reqwest::Url, ProviderError> {
    validate_endpoint(endpoint)?;
    let base_url = reqwest::Url::parse(base.trim())
        .map_err(|_| ProviderError::new(ProviderErrorKind::Configuration, "API Base URL 无效。"))?;
    let local_http = base_url.scheme() == "http"
        && (matches!(base_url.host_str(), Some("localhost" | "127.0.0.1" | "::1"))
            || base_url.host_str().is_some_and(|host| {
                host.starts_with("192.168.") || host.starts_with("10.") || host.starts_with("172.")
            }));
    if base_url.scheme() != "https" && !local_http {
        return Err(ProviderError::new(
            ProviderErrorKind::Configuration,
            "API Base URL 必须使用 HTTPS；本地或局域网服务允许 HTTP。",
        ));
    }
    if !base_url.username().is_empty()
        || base_url.password().is_some()
        || base_url.query().is_some()
        || base_url.fragment().is_some()
    {
        return Err(ProviderError::new(
            ProviderErrorKind::Configuration,
            "API Base URL 不得包含凭据、查询参数或片段。",
        ));
    }
    // A relay Base URL may already carry the version prefix that the endpoint
    // template also contains (base ".../v1beta" + endpoint "/v1beta/models/…");
    // joining verbatim would double it. Treat the endpoint as authoritative in
    // that case instead of concatenating both prefixes.
    let base_path = base_url.path().trim_matches('/');
    let endpoint_path = endpoint.trim_start_matches('/');
    let endpoint_repeats_base =
        !base_path.is_empty() && endpoint_path.starts_with(&format!("{base_path}/"));
    let full_path = if base_path.is_empty() || endpoint_repeats_base {
        format!("/{endpoint_path}")
    } else {
        format!("/{base_path}/{endpoint_path}")
    };
    let host = base_url.host_str().ok_or_else(|| {
        ProviderError::new(ProviderErrorKind::Configuration, "API Base URL 无效。")
    })?;
    let port = base_url
        .port()
        .map_or_else(String::new, |value| format!(":{value}"));
    let joined = format!("{}://{host}{port}{full_path}", base_url.scheme());
    reqwest::Url::parse(&joined).map_err(|_| {
        ProviderError::new(ProviderErrorKind::Configuration, "Provider endpoint 无效。")
    })
}

fn validate_endpoint(endpoint: &str) -> Result<(), ProviderError> {
    if !endpoint.starts_with('/')
        || endpoint.len() > 200
        || endpoint.contains('?')
        || endpoint.contains('#')
        || endpoint.contains("://")
    {
        return Err(ProviderError::new(
            ProviderErrorKind::Configuration,
            "Endpoint 必须是长度不超过 200 的绝对路径，且不能包含查询参数。",
        ));
    }
    Ok(())
}

fn build_headers(
    settings: &ProviderSettings,
    prepared: &PreparedProviderRequest,
) -> Result<HeaderMap, ProviderError> {
    if prepared.extra_headers.len() > MAX_EXTRA_HEADERS {
        return Err(ProviderError::new(
            ProviderErrorKind::Configuration,
            format!("自定义 Header 不能超过 {MAX_EXTRA_HEADERS} 项。"),
        ));
    }
    let mut headers = HeaderMap::new();
    for (name, value) in &prepared.extra_headers {
        let normalized = name.trim().to_ascii_lowercase();
        if matches!(
            normalized.as_str(),
            "authorization"
                | "x-api-key"
                | "x-goog-api-key"
                | "proxy-authorization"
                | "cookie"
                | "set-cookie"
        ) {
            return Err(ProviderError::new(
                ProviderErrorKind::Configuration,
                format!("敏感 Header {name} 必须由凭据存储管理，不能写入普通配置。"),
            ));
        }
        let header_name = HeaderName::from_bytes(name.trim().as_bytes()).map_err(|_| {
            ProviderError::new(ProviderErrorKind::Configuration, "自定义 Header 名称无效。")
        })?;
        let header_value = HeaderValue::from_str(value.trim()).map_err(|_| {
            ProviderError::new(ProviderErrorKind::Configuration, "自定义 Header 值无效。")
        })?;
        headers.insert(header_name, header_value);
    }
    if settings.provider_type == ProviderType::AnthropicMessages
        && settings.anthropic_version.trim().is_empty()
    {
        return Err(ProviderError::new(
            ProviderErrorKind::Configuration,
            "Anthropic Version 不能为空。",
        ));
    }
    Ok(headers)
}

async fn read_bounded(
    response: reqwest::Response,
    limit: usize,
    cancellation: &CancellationToken,
) -> Result<Vec<u8>, ProviderError> {
    if response
        .content_length()
        .is_some_and(|length| length > limit as u64)
    {
        return Err(ProviderError::new(
            ProviderErrorKind::InvalidResponse,
            "Provider 响应超过大小上限。",
        ));
    }
    let mut bytes = Vec::new();
    let mut stream = response.bytes_stream();
    loop {
        let next = tokio::select! {
            () = cancellation.cancelled() => return Err(cancelled_error()),
            next = stream.next() => next,
        };
        let Some(chunk) = next else { break };
        let chunk = chunk.map_err(|_| {
            ProviderError::new(ProviderErrorKind::Transport, "读取 Provider 响应失败。")
        })?;
        if bytes.len().saturating_add(chunk.len()) > limit {
            return Err(ProviderError::new(
                ProviderErrorKind::InvalidResponse,
                "Provider 响应超过大小上限。",
            ));
        }
        bytes.extend_from_slice(&chunk);
    }
    Ok(bytes)
}

fn retry_after(response: &reqwest::Response) -> Option<Duration> {
    response
        .headers()
        .get(RETRY_AFTER)
        .and_then(|value| value.to_str().ok())
        .and_then(|value| value.parse::<u64>().ok())
        .map(Duration::from_secs)
}

fn classify_http_error(status: u16, _response: &[u8]) -> ProviderError {
    // Never surface the upstream body: gateways sometimes echo request data or
    // credentials in error messages. Keep it bounded for connection reuse, then
    // classify only from the HTTP status.
    match status {
        401 | 403 => ProviderError::new(
            ProviderErrorKind::Authentication,
            format!("Provider 鉴权失败（HTTP {status}）。"),
        )
        .with_status(status),
        429 => ProviderError::new(
            ProviderErrorKind::RateLimited,
            "Provider 请求受限（HTTP 429）。",
        )
        .with_status(status)
        .retryable(),
        408 | 500 | 502 | 503 | 504 => ProviderError::new(
            ProviderErrorKind::Server,
            format!("Provider 暂时不可用（HTTP {status}）。"),
        )
        .with_status(status)
        .retryable(),
        _ => ProviderError::new(
            ProviderErrorKind::Transport,
            format!("Provider 拒绝请求（HTTP {status}）。"),
        )
        .with_status(status),
    }
}

fn parse_json(response: &[u8]) -> Result<Value, ProviderError> {
    serde_json::from_slice(response).map_err(|_| invalid_response("Provider 返回了无效 JSON。"))
}

fn parse_translation_json(model_text: &str) -> Result<TranslationResult, ProviderError> {
    let trimmed = model_text.trim();
    let json_text = trimmed
        .strip_prefix("```json")
        .or_else(|| trimmed.strip_prefix("```"))
        .and_then(|without_prefix| without_prefix.strip_suffix("```"))
        .map_or(trimmed, str::trim);

    // 1. Direct JSON parse
    if let Ok(result) = serde_json::from_str::<TranslationResult>(json_text)
        && !result.translated_text.trim().is_empty()
    {
        return Ok(result);
    }

    // 2. Substring JSON parse if enclosed in other text
    if let (Some(start), Some(end)) = (json_text.find('{'), json_text.rfind('}'))
        && start < end
        && let Ok(result) = serde_json::from_str::<TranslationResult>(&json_text[start..=end])
        && !result.translated_text.trim().is_empty()
    {
        return Ok(result);
    }

    // 3. Fallback: treat entire output as translated text if non-empty
    if !trimmed.is_empty() {
        return Ok(TranslationResult {
            translated_text: trimmed.to_owned(),
            transcription: String::new(),
            explanation: String::new(),
            protected_terms: Vec::new(),
            warnings: Vec::new(),
            is_partial: false,
        });
    }

    Err(invalid_response("模型未返回任何翻译内容。"))
}

fn invalid_response(message: impl Into<String>) -> ProviderError {
    ProviderError::new(ProviderErrorKind::InvalidResponse, message)
}

fn cancelled_error() -> ProviderError {
    ProviderError::new(ProviderErrorKind::Cancelled, "Provider 请求已取消。")
}

fn elapsed_millis(started: Instant) -> u64 {
    u64::try_from(started.elapsed().as_millis()).unwrap_or(u64::MAX)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn settings(provider_type: ProviderType) -> ProviderSettings {
        let endpoint = provider_type.default_endpoint().to_owned();
        ProviderSettings {
            provider_type,
            api_base_url: provider_type.default_base_url().to_owned(),
            text_endpoint: endpoint.clone(),
            vision_endpoint: endpoint,
            text_model: "text-model".to_owned(),
            vision_model: "vision-model".to_owned(),
            network_enabled: true,
            safe_dev_mode: false,
            ..ProviderSettings::default()
        }
    }

    fn image() -> ImageInput {
        ImageInput::Bytes {
            media_type: "image/png".to_owned(),
            data: vec![1, 2, 3],
        }
    }

    fn text_request(source: &str) -> TranslationRequest {
        TranslationRequest::text(source, LanguagePair::new("auto", "zh-CN"))
    }

    fn vision_request() -> TranslationRequest {
        TranslationRequest::vision(image(), LanguagePair::new("auto", "zh-CN"))
    }

    #[test]
    fn openai_chat_serializes_text_and_image_content() {
        let provider = OpenAiChatProvider;
        let config = settings(ProviderType::OpenAiCompatible);
        let text = provider
            .prepare(&config, &text_request("hello"))
            .expect("text request");
        let vision = provider
            .prepare(&config, &vision_request())
            .expect("vision request");
        assert_eq!(text.body["messages"][1]["content"], "hello");
        assert_eq!(
            vision.body["messages"][1]["content"][1]["type"],
            "image_url"
        );
        assert!(
            vision.body["messages"][1]["content"][1]["image_url"]["url"]
                .as_str()
                .expect("data url")
                .starts_with("data:image/png;base64,")
        );
    }

    #[test]
    fn requested_target_language_reaches_every_protocol_prompt() {
        let request = TranslationRequest::text("hello", LanguagePair::new("en", "ja"));
        let instructions = request.system_instructions();
        assert!(instructions.contains("English"));
        assert!(instructions.contains("Japanese"));

        let chat = OpenAiChatProvider
            .prepare(&settings(ProviderType::OpenAiCompatible), &request)
            .expect("chat request");
        assert_eq!(chat.body["messages"][0]["content"], instructions);

        let responses = OpenAiResponsesProvider
            .prepare(&settings(ProviderType::OpenAiResponses), &request)
            .expect("responses request");
        assert_eq!(responses.body["instructions"], instructions);

        let anthropic = AnthropicMessagesProvider
            .prepare(&settings(ProviderType::AnthropicMessages), &request)
            .expect("anthropic request");
        assert_eq!(anthropic.body["system"], instructions);

        let gemini = GeminiGenerateContentProvider
            .prepare(&settings(ProviderType::GeminiGenerateContent), &request)
            .expect("gemini request");
        assert_eq!(
            gemini.body["system_instruction"]["parts"][0]["text"],
            instructions
        );
    }

    #[test]
    fn explanation_opt_out_reaches_the_prompt() {
        let request = text_request("hello").with_explanation(false);
        assert!(
            request
                .system_instructions()
                .contains("Always leave `explanation` empty")
        );
    }

    #[test]
    fn openai_responses_uses_input_image_shape() {
        let provider = OpenAiResponsesProvider;
        let config = settings(ProviderType::OpenAiResponses);
        let request = provider
            .prepare(&config, &vision_request())
            .expect("vision request");
        assert_eq!(request.endpoint, "/responses");
        assert_eq!(request.body["input"][0]["content"][0]["type"], "input_text");
        assert_eq!(
            request.body["input"][0]["content"][1]["type"],
            "input_image"
        );
        assert!(request.body["store"].as_bool().is_some_and(|store| !store));
    }

    #[test]
    fn anthropic_uses_native_headers_and_base64_source() {
        let provider = AnthropicMessagesProvider;
        let config = settings(ProviderType::AnthropicMessages);
        let request = provider
            .prepare(&config, &vision_request())
            .expect("vision request");
        assert_eq!(request.endpoint, "/v1/messages");
        assert_eq!(request.body["messages"][0]["content"][0]["type"], "image");
        assert_eq!(
            request.body["messages"][0]["content"][0]["source"]["type"],
            "base64"
        );
        assert!(
            request
                .extra_headers
                .contains(&("anthropic-version".to_owned(), "2023-06-01".to_owned()))
        );
    }

    #[test]
    fn gemini_uses_model_path_and_inline_data() {
        let provider = GeminiGenerateContentProvider;
        let mut config = settings(ProviderType::GeminiGenerateContent);
        config.vision_model = "gemini-test-flash".to_owned();
        let request = provider
            .prepare(&config, &vision_request())
            .expect("vision request");
        assert_eq!(
            request.endpoint,
            "/v1beta/models/gemini-test-flash:generateContent"
        );
        assert_eq!(
            request.body["contents"][0]["parts"][0]["inline_data"]["mime_type"],
            "image/png"
        );
        assert!(
            request.body["contents"][0]["parts"][1]["text"]
                .as_str()
                .expect("vision prompt")
                .contains("Transcribe")
        );
    }

    #[test]
    fn all_protocols_parse_structured_output() {
        let translated = serde_json::to_string(&TranslationResult {
            translated_text: "你好".to_owned(),
            explanation: "问候语".to_owned(),
            ..TranslationResult::default()
        })
        .expect("translation json");
        let chat = serde_json::to_vec(&json!({"choices":[{"message":{"content":translated}}]}))
            .expect("chat json");
        assert_eq!(
            OpenAiChatProvider
                .parse(&chat)
                .expect("chat parse")
                .translated_text,
            "你好"
        );

        let responses = serde_json::to_vec(&json!({
            "output":[{"type":"message","content":[{"type":"output_text","text":translated}]}]
        }))
        .expect("responses json");
        assert_eq!(
            OpenAiResponsesProvider
                .parse(&responses)
                .expect("responses parse")
                .translated_text,
            "你好"
        );

        let anthropic = serde_json::to_vec(&json!({
            "content":[{"type":"text","text":translated}]
        }))
        .expect("anthropic json");
        assert_eq!(
            AnthropicMessagesProvider
                .parse(&anthropic)
                .expect("anthropic parse")
                .translated_text,
            "你好"
        );

        let gemini = serde_json::to_vec(&json!({
            "candidates":[{"content":{"parts":[{"text":translated}]},"finishReason":"STOP"}]
        }))
        .expect("gemini json");
        assert_eq!(
            GeminiGenerateContentProvider
                .parse(&gemini)
                .expect("gemini parse")
                .translated_text,
            "你好"
        );
    }

    #[test]
    fn gemini_safety_block_is_classified() {
        let response = serde_json::to_vec(&json!({
            "promptFeedback": {"blockReason": "SAFETY"}
        }))
        .expect("gemini json");
        let error = GeminiGenerateContentProvider
            .parse(&response)
            .expect_err("must be blocked");
        assert_eq!(error.kind, ProviderErrorKind::SafetyBlocked);
    }

    #[test]
    fn sensitive_headers_are_rejected() {
        let mut config = settings(ProviderType::OpenAiCompatible);
        config
            .extra_headers
            .insert("Authorization".to_owned(), "secret".to_owned());
        let prepared = OpenAiChatProvider
            .prepare(&config, &text_request("hello"))
            .expect("prepare");
        let error = build_headers(&config, &prepared).expect_err("must reject secret header");
        assert_eq!(error.kind, ProviderErrorKind::Configuration);
    }

    #[test]
    fn missing_key_and_disabled_network_fail_before_http() {
        let mut config = settings(ProviderType::OpenAiCompatible);
        config.network_enabled = false;
        assert_eq!(
            validate_execution(&config, "key", "request")
                .expect_err("disabled")
                .kind,
            ProviderErrorKind::NetworkDisabled
        );
        config.network_enabled = true;
        assert_eq!(
            validate_execution(&config, "", "request")
                .expect_err("missing key")
                .kind,
            ProviderErrorKind::MissingCredential
        );
    }

    #[test]
    fn safe_dev_mode_blocks_even_a_fully_configured_provider() {
        let mut config = settings(ProviderType::OpenAiCompatible);
        config.safe_dev_mode = true;
        let error = validate_execution(&config, "key", "request").expect_err("offline gate");
        assert_eq!(error.kind, ProviderErrorKind::NetworkDisabled);
        assert!(error.message.contains("安全离线模式"));
    }

    #[test]
    fn local_runtime_needs_no_key_but_a_public_lookalike_still_does() {
        let mut config = settings(ProviderType::OpenAiCompatible);
        config.api_base_url = "http://localhost:11434/v1".to_owned();
        assert!(validate_execution(&config, "", "request").is_ok());
        // `relay-10.example.com` used to pass the naive substring host check.
        config.api_base_url = "https://relay-10.example.com/v1".to_owned();
        assert_eq!(
            validate_execution(&config, "", "request")
                .expect_err("public host still needs a key")
                .kind,
            ProviderErrorKind::MissingCredential
        );
    }

    #[test]
    fn disabled_capability_fails_before_preparing_request() {
        let mut config = settings(ProviderType::OpenAiCompatible);
        config.supports_vision = false;
        let error = validate_input_capability(&config, &vision_request().input)
            .expect_err("vision capability is disabled");
        assert_eq!(error.kind, ProviderErrorKind::UnsupportedInput);
    }

    #[test]
    fn rate_limit_is_retryable_and_classified() {
        let error = classify_http_error(429, br#"{"error":{"message":"slow down"}}"#);
        assert_eq!(error.kind, ProviderErrorKind::RateLimited);
        assert!(error.retryable);
        assert_eq!(error.status_code, Some(429));
    }

    #[test]
    fn url_join_deduplicates_shared_version_prefix() {
        let url = build_url(
            "https://relay.example/v1beta",
            "/v1beta/models/gemini-x:generateContent",
        )
        .expect("relay url");
        assert_eq!(
            url.as_str(),
            "https://relay.example/v1beta/models/gemini-x:generateContent"
        );
    }

    #[test]
    fn url_join_keeps_distinct_base_path() {
        let url = build_url("https://api.openai.com/v1", "/chat/completions").expect("openai url");
        assert_eq!(url.as_str(), "https://api.openai.com/v1/chat/completions");
    }

    #[test]
    fn url_join_without_base_path_is_plain_append() {
        let url = build_url(
            "https://generativelanguage.googleapis.com",
            "/v1beta/models/gemini-x:generateContent",
        )
        .expect("gemini url");
        assert_eq!(
            url.as_str(),
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-x:generateContent"
        );
    }
}
