//! Stable boundary for the staged `genai` migration.
//!
//! `PopGlot` still owns transport policy, request caps, cancellation, retry and
//! privacy checks. This module makes protocol selection explicit so later
//! migrations do not let a model name silently choose another wire protocol.

use crate::provider::{
    ImageInput, PreparedProviderRequest, ProviderError, ProviderErrorKind, StreamPrompt,
    TranslationInput, TranslationRequest, extra_headers, gemini_stream_endpoint,
    glm_thinking_config, output_token_limit, require_model, validate_image, validate_image_url,
    validate_model_path_segment,
};
use base64::Engine as _;
use genai::chat::{
    Binary, ChatMessage, ChatOptions, ChatRequest, ChatResponseFormat, ContentPart, ReasoningEffort,
};
use genai::resolver::{AuthData, Endpoint};
use genai::{Client, adapter::AdapterKind};
use genai::{ModelIden, ServiceTarget};
use popglot_domain::ProviderSettings;
use popglot_domain::ProviderType;
use serde_json::Value;

/// Serialize through the SDK without giving it secrets or transport ownership.
/// The synthetic endpoint is never contacted; only the payload is returned.
pub(crate) fn prepare(
    kind: ProviderType,
    settings: &ProviderSettings,
    request: &TranslationRequest,
    prompt: Option<&StreamPrompt>,
) -> Result<PreparedProviderRequest, ProviderError> {
    let vision = matches!(request.input, TranslationInput::Vision { .. });
    let model = require_model(
        if vision {
            &settings.vision_model
        } else {
            &settings.text_model
        },
        "模型",
    )?;
    let mut endpoint = if vision {
        settings.vision_endpoint.clone()
    } else {
        settings.text_endpoint.clone()
    };
    if kind == ProviderType::GeminiGenerateContent {
        validate_model_path_segment(model)?;
        endpoint = endpoint.replace("{model}", model);
        if prompt.is_some() {
            endpoint = gemini_stream_endpoint(&endpoint)?;
        }
    }
    let system = prompt.map_or_else(
        || request.system_instructions(),
        |p| p.system_instructions.clone(),
    );
    let text = prompt.map_or_else(
        || match &request.input {
            TranslationInput::Text { source } => source.clone(),
            TranslationInput::Vision { .. } => request.vision_prompt(),
        },
        |p| p.user_payload.clone(),
    );
    let user = user_message(kind, &request.input, text)?;
    let mut options = ChatOptions::default().with_max_tokens(output_token_limit(request));
    // Explicit value suppresses SDK suffix inference; remove the resulting wire
    // option below so arbitrary gateway IDs never acquire reasoning settings.
    options.reasoning_effort = Some(ReasoningEffort::None);
    if matches!(
        kind,
        ProviderType::OpenAiCompatible | ProviderType::GeminiGenerateContent
    ) {
        options.temperature = Some(0.1);
    }
    if kind == ProviderType::GeminiGenerateContent && prompt.is_none() {
        options.response_format = Some(ChatResponseFormat::JsonMode);
    }
    let target = ServiceTarget {
        model: ModelIden::new(adapter_kind(kind), model),
        endpoint: Endpoint::from_static("https://sdk-payload.invalid/v1/"),
        auth: AuthData::from_single("payload-only"),
    };
    let mut body = genai::adapter::prepare_payload(
        target,
        ChatRequest::new(vec![user]).with_system(system),
        &options,
        prompt.is_some(),
    )
    .map_err(|_| {
        ProviderError::new(
            ProviderErrorKind::Configuration,
            "无法生成请求，请检查模型配置。",
        )
    })?;
    // Names are opaque gateway identifiers, not SDK namespaces.
    if kind != ProviderType::GeminiGenerateContent {
        body["model"] = Value::String(model.to_owned());
    }
    if let Some(object) = body.as_object_mut() {
        object.remove("reasoning_effort");
        object.remove("reasoning");
        object.remove("thinking");
    }
    if let Some(config) = body
        .get_mut("generationConfig")
        .and_then(Value::as_object_mut)
    {
        config.remove("thinkingConfig");
    }
    if kind == ProviderType::OpenAiCompatible
        && let Some(thinking) = glm_thinking_config(model)
    {
        body["thinking"] = thinking;
    }
    let mut headers = extra_headers(settings);
    if kind == ProviderType::AnthropicMessages {
        headers.push((
            "anthropic-version".to_owned(),
            settings.anthropic_version.clone(),
        ));
    }
    Ok(PreparedProviderRequest {
        provider_type: kind,
        endpoint,
        contains_image: vision,
        extra_headers: headers,
        body,
    })
}

fn user_message(
    kind: ProviderType,
    input: &TranslationInput,
    text: String,
) -> Result<ChatMessage, ProviderError> {
    Ok(match input {
        TranslationInput::Text { .. } => ChatMessage::user(text),
        TranslationInput::Vision { image } => {
            let binary = match image {
                ImageInput::Bytes { media_type, data } => {
                    validate_image(media_type, data.len())?;
                    Binary::from_base64(
                        media_type,
                        base64::engine::general_purpose::STANDARD.encode(data),
                        None,
                    )
                }
                ImageInput::Url(url) => {
                    if kind == ProviderType::GeminiGenerateContent {
                        return Err(ProviderError::new(
                            ProviderErrorKind::UnsupportedInput,
                            "此协议需要本地图片。",
                        ));
                    }
                    validate_image_url(url)?;
                    Binary::from_url("image/png", url, None)
                }
            };
            let image = ContentPart::Binary(binary);
            let text = ContentPart::Text(text);
            let parts = if matches!(
                kind,
                ProviderType::OpenAiCompatible | ProviderType::OpenAiResponses
            ) {
                vec![text, image]
            } else {
                vec![image, text]
            };
            ChatMessage::user(parts)
        }
    })
}

/// The reviewed SDK version. Keep this in sync with the exact Cargo pin.
pub const GENAI_VERSION: &str = "0.6.5";

/// Maps `PopGlot`'s user-selected protocol to exactly one SDK adapter.
///
/// This deliberately does not infer a provider from the model name. Custom
/// gateways frequently expose arbitrary IDs and must keep the protocol the
/// user selected in settings.
#[must_use]
pub const fn adapter_kind(provider_type: ProviderType) -> AdapterKind {
    match provider_type {
        ProviderType::OpenAiCompatible => AdapterKind::OpenAI,
        ProviderType::OpenAiResponses => AdapterKind::OpenAIResp,
        ProviderType::AnthropicMessages => AdapterKind::Anthropic,
        ProviderType::GeminiGenerateContent => AdapterKind::Gemini,
    }
}

/// Builds an adapter-bound SDK client over a caller-owned HTTP client.
///
/// The caller remains responsible for applying `PopGlot`'s redirect, TLS and
/// timeout policy to `http_client`. Binding the adapter prevents `genai` from
/// guessing a protocol from a model suffix or namespace.
#[must_use]
pub fn build_bound_client(provider_type: ProviderType, http_client: reqwest13::Client) -> Client {
    Client::builder()
        .with_reqwest(http_client)
        .with_adapter_kind(adapter_kind(provider_type))
        .build()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn production_requests_preserve_gateway_ids_and_do_not_infer_reasoning() {
        for kind in [
            ProviderType::OpenAiCompatible,
            ProviderType::OpenAiResponses,
            ProviderType::AnthropicMessages,
            ProviderType::GeminiGenerateContent,
        ] {
            for model in ["custom-model-high", "vendor::custom-model-low"] {
                let settings = ProviderSettings {
                    provider_type: kind,
                    text_model: model.to_owned(),
                    text_endpoint: if kind == ProviderType::GeminiGenerateContent {
                        "/v1beta/models/{model}:generateContent".to_owned()
                    } else {
                        "/gateway/custom".to_owned()
                    },
                    ..ProviderSettings::default()
                };
                let request = TranslationRequest::text(
                    "  code_name\n",
                    popglot_domain::LanguagePair::new("en", "zh-CN"),
                );
                let prompt = crate::provider::StreamPromptBuilder::new(
                    &request,
                    "sdk-test-delimiter-20260926",
                )
                .build()
                .unwrap();
                for stream in [None, Some(&prompt)] {
                    let prepared = crate::provider::provider_for(kind);
                    let prepared = if let Some(prompt) = stream {
                        prepared.prepare_stream(&settings, &request, prompt)
                    } else {
                        prepared.prepare(&settings, &request)
                    };
                    if kind == ProviderType::GeminiGenerateContent && model.contains(':') {
                        assert_eq!(prepared.unwrap_err().kind, ProviderErrorKind::Configuration);
                        continue;
                    }
                    let prepared = prepared.unwrap();
                    assert!(prepared.body.get("reasoning").is_none());
                    assert!(prepared.body.get("reasoning_effort").is_none());
                    assert!(prepared.body.get("thinking").is_none());
                    assert!(
                        prepared.body["generationConfig"]
                            .get("thinkingConfig")
                            .is_none()
                    );
                    if kind == ProviderType::GeminiGenerateContent {
                        assert!(prepared.endpoint.contains(model));
                    } else {
                        assert_eq!(prepared.body["model"], model);
                        assert_eq!(prepared.endpoint, "/gateway/custom");
                    }
                    if kind == ProviderType::OpenAiResponses {
                        assert_eq!(prepared.body["store"], false);
                    }
                }
            }
        }
    }

    #[test]
    fn every_product_protocol_has_an_explicit_sdk_adapter() {
        assert_eq!(
            adapter_kind(ProviderType::OpenAiCompatible),
            AdapterKind::OpenAI
        );
        assert_eq!(
            adapter_kind(ProviderType::OpenAiResponses),
            AdapterKind::OpenAIResp
        );
        assert_eq!(
            adapter_kind(ProviderType::AnthropicMessages),
            AdapterKind::Anthropic
        );
        assert_eq!(
            adapter_kind(ProviderType::GeminiGenerateContent),
            AdapterKind::Gemini
        );
    }

    #[test]
    fn bound_clients_build_with_popglot_owned_transport() {
        let http_client = reqwest13::Client::builder()
            .redirect(reqwest13::redirect::Policy::none())
            .build()
            .expect("test HTTP client should build");

        for provider_type in [
            ProviderType::OpenAiCompatible,
            ProviderType::OpenAiResponses,
            ProviderType::AnthropicMessages,
            ProviderType::GeminiGenerateContent,
        ] {
            let _client = build_bound_client(provider_type, http_client.clone());
        }
    }
}
