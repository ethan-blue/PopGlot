//! Stable boundary for the staged `genai` migration.
//!
//! `PopGlot` still owns transport policy, request caps, cancellation, retry and
//! privacy checks. This module makes protocol selection explicit so later
//! migrations do not let a model name silently choose another wire protocol.

use genai::{Client, adapter::AdapterKind};
use popglot_domain::ProviderType;

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
