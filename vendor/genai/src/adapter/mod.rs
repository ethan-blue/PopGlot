//! The Adapter layer allows adapting client requests/responses to various AI providers.
//! Currently, it employs a static dispatch pattern with the `Adapter` trait and `AdapterDispatcher` implementation.
//! Adapter implementations are organized by adapter type under the `adapters` submodule.
//!
//! Notes:
//! - All `Adapter` trait methods take the `AdapterKind` as an argument, and for now, the `Adapter` trait functions
//!   are all static (i.e., no `&self`). This reduces state management and ensures that all states are passed as arguments.
//! - Only `AdapterKind` from `AdapterConfig` is publicly exported.

// region:    --- Modules

mod adapter_kind;
mod adapter_types;
mod adapters;
mod dispatcher;
mod dispatcher_macros;

// -- Flatten (private, crate, public)
use adapters::*;

pub(crate) use adapter_types::*;
pub(crate) use dispatcher::*;

pub use adapter_kind::*;

/// Build a protocol payload without performing I/O. PopGlot's only SDK patch.
/// Transport, credentials and endpoint selection remain with the caller.
pub fn prepare_payload(
    target: crate::ServiceTarget,
    request: crate::chat::ChatRequest,
    options: &crate::chat::ChatOptions,
    stream: bool,
) -> crate::Result<serde_json::Value> {
    let service = if stream { ServiceType::ChatStream } else { ServiceType::Chat };
    AdapterDispatcher::to_web_request_data(
        target, service, request,
        crate::chat::ChatOptionsSet::default().with_chat_options(Some(options)),
    ).map(|request| request.payload)
}

// -- Crate modules
pub(crate) mod inter_stream;

// endregion: --- Modules
