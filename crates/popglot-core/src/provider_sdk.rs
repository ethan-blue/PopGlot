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
use genai::adapter::AdapterKind;
use genai::chat::{
    Binary, ChatMessage, ChatOptions, ChatRequest, ChatResponseFormat, ContentPart, ReasoningEffort,
};
use genai::resolver::{AuthData, Endpoint};
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
    // A supplied SDK value prevents suffix inference. Zero is not Option::None
    // and does not prescribe gateway behavior: all generated reasoning controls
    // are removed below before the payload reaches PopGlot's transport.
    options.reasoning_effort = Some(ReasoningEffort::Zero);
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
        // Cache policy is not a product setting. New SDK defaults for native
        // OpenAI model names must not add options to arbitrary gateways.
        object.remove("prompt_cache_options");
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
pub const GENAI_VERSION: &str = "0.7.1";

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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn production_requests_preserve_gateway_ids_without_unconfigured_sdk_options() {
        for kind in [
            ProviderType::OpenAiCompatible,
            ProviderType::OpenAiResponses,
            ProviderType::AnthropicMessages,
            ProviderType::GeminiGenerateContent,
        ] {
            let native_models: &[&str] = match kind {
                ProviderType::OpenAiCompatible | ProviderType::OpenAiResponses => {
                    &["gpt-5.6-high", "gpt-6-astra", "vendor::gpt-6-astra-zero"]
                }
                ProviderType::AnthropicMessages => {
                    &["claude-sonnet-5", "claude-haiku-5", "claude-opus-4-8-zero"]
                }
                ProviderType::GeminiGenerateContent => &["gemini-3-pro-zero", "gemini-4-argon"],
            };
            for model in ["custom-model-high", "vendor::custom-model-low"]
                .into_iter()
                .chain(native_models.iter().copied())
            {
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
                    assert!(prepared.body.get("prompt_cache_options").is_none());
                    assert!(prepared.body["output_config"].get("effort").is_none());
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
    fn output_budget_uses_each_protocols_native_wire_field() {
        let request = TranslationRequest::text(
            "x".repeat(800),
            popglot_domain::LanguagePair::new("en", "zh-CN"),
        );
        assert_eq!(output_token_limit(&request), 1_200);

        for (kind, model, pointer) in [
            (
                ProviderType::OpenAiCompatible,
                "custom-model",
                "/max_tokens",
            ),
            (
                ProviderType::OpenAiCompatible,
                "gpt-5.6-high",
                "/max_completion_tokens",
            ),
            (
                ProviderType::OpenAiCompatible,
                "gpt-6-astra",
                "/max_completion_tokens",
            ),
            (
                ProviderType::OpenAiResponses,
                "gpt-6-astra",
                "/max_output_tokens",
            ),
            (
                ProviderType::AnthropicMessages,
                "claude-opus-4-8",
                "/max_tokens",
            ),
            (
                ProviderType::GeminiGenerateContent,
                "gemini-3-pro-zero",
                "/generationConfig/maxOutputTokens",
            ),
        ] {
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
            let prepared = crate::provider::provider_for(kind)
                .prepare(&settings, &request)
                .unwrap();
            assert_eq!(
                prepared.body.pointer(pointer).and_then(Value::as_u64),
                Some(1_200)
            );
            if pointer == "/max_completion_tokens" {
                assert!(prepared.body.get("max_tokens").is_none());
            }
        }
    }

    #[test]
    fn summaries_size_short_outputs_without_reducing_long_summary_ceiling() {
        for (source, expected) in [("short".to_owned(), 384), ("x".repeat(800), 700)] {
            let request =
                TranslationRequest::text(source, popglot_domain::LanguagePair::new("en", "zh-CN"))
                    .with_task(crate::provider::TextTask::Summarize)
                    .with_explanation(false);
            assert_eq!(output_token_limit(&request), expected);
            for (kind, pointer) in [
                (ProviderType::OpenAiCompatible, "/max_tokens"),
                (ProviderType::OpenAiResponses, "/max_output_tokens"),
                (ProviderType::AnthropicMessages, "/max_tokens"),
                (
                    ProviderType::GeminiGenerateContent,
                    "/generationConfig/maxOutputTokens",
                ),
            ] {
                let settings = ProviderSettings {
                    provider_type: kind,
                    text_model: "opaque-gateway-model".to_owned(),
                    text_endpoint: if kind == ProviderType::GeminiGenerateContent {
                        "/v1beta/models/{model}:generateContent".to_owned()
                    } else {
                        "/custom".to_owned()
                    },
                    ..ProviderSettings::default()
                };
                let prepared = crate::provider::provider_for(kind)
                    .prepare(&settings, &request)
                    .expect("prepare");
                assert_eq!(
                    prepared.body.pointer(pointer).and_then(Value::as_u64),
                    Some(u64::from(expected))
                );
            }
        }
    }

    #[test]
    fn text_and_vision_requests_use_their_own_model_and_endpoint() {
        let settings = ProviderSettings {
            provider_type: ProviderType::OpenAiCompatible,
            text_model: "text-model".to_owned(),
            vision_model: "vision-model".to_owned(),
            text_endpoint: "/text-route".to_owned(),
            vision_endpoint: "/vision-route".to_owned(),
            ..ProviderSettings::default()
        };
        let text_request =
            TranslationRequest::text("hello", popglot_domain::LanguagePair::new("en", "zh-CN"));
        let vision_request = TranslationRequest::vision(
            ImageInput::Bytes {
                media_type: "image/png".to_owned(),
                data: vec![1, 2, 3],
            },
            popglot_domain::LanguagePair::new("auto", "zh-CN"),
        );

        let text = prepare(
            ProviderType::OpenAiCompatible,
            &settings,
            &text_request,
            None,
        )
        .unwrap();
        let vision = prepare(
            ProviderType::OpenAiCompatible,
            &settings,
            &vision_request,
            None,
        )
        .unwrap();
        assert_eq!(text.body["model"], "text-model");
        assert_eq!(text.endpoint, "/text-route");
        assert!(!text.contains_image);
        assert_eq!(vision.body["model"], "vision-model");
        assert_eq!(vision.endpoint, "/vision-route");
        assert!(vision.contains_image);
    }

    #[test]
    fn maximum_image_still_fits_the_request_body_budget() {
        for kind in [
            ProviderType::OpenAiCompatible,
            ProviderType::OpenAiResponses,
            ProviderType::AnthropicMessages,
            ProviderType::GeminiGenerateContent,
        ] {
            let settings = ProviderSettings {
                provider_type: kind,
                vision_model: "vision-model".to_owned(),
                vision_endpoint: if kind == ProviderType::GeminiGenerateContent {
                    "/v1beta/models/{model}:generateContent".to_owned()
                } else {
                    "/gateway/vision".to_owned()
                },
                ..ProviderSettings::default()
            };
            let request = TranslationRequest::vision(
                ImageInput::Bytes {
                    media_type: "image/png".to_owned(),
                    data: vec![0; crate::provider::MAX_IMAGE_BYTES],
                },
                popglot_domain::LanguagePair::new("auto", "zh-CN"),
            );
            let prepared = prepare(kind, &settings, &request, None).unwrap();
            let serialized = serde_json::to_vec(&prepared.body).unwrap();
            assert!(
                serialized.len() <= crate::provider::MAX_REQUEST_BYTES,
                "{kind:?} encoded {} bytes for the maximum image",
                serialized.len()
            );
        }
    }
}
