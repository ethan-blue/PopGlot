//! Stable C ABI for replaceable desktop shells.

// Raw pointers and exported symbols are inherent to this narrow FFI boundary.
// Unsafe code remains denied by convention in every other workspace crate.
#![allow(unsafe_code)]
// MSVC link.exe reports the generated import library on stdout; Rust 1.98
// surfaces that informational localized line as `linker_messages`.
#![allow(linker_messages)]

use base64::Engine as _;
use popglot_core::AppCore;
use popglot_core::provider::ProviderClient;
use popglot_domain::{
    BUILTIN_FAITHFUL_ID, LanguagePair, PromptTemplate, PromptVariables, ProviderSettings,
    compile_prompt,
};
use serde::Serialize;
use std::collections::HashMap;
use std::ffi::{CStr, CString, c_char, c_void};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::ptr;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock, RwLock, RwLockReadGuard, RwLockWriteGuard};
use tokio::runtime::Runtime;
use tokio_util::sync::CancellationToken;

/// Core is behind an `RwLock`. Snapshot cloning takes microseconds so lock
/// guards are NEVER held across asynchronous network calls.
static CORE: OnceLock<RwLock<AppCore>> = OnceLock::new();
static RUNTIME: OnceLock<Runtime> = OnceLock::new();
static ACTIVE_REQUESTS: OnceLock<Mutex<HashMap<String, CancellationToken>>> = OnceLock::new();
static REQUEST_TICKET: AtomicU64 = AtomicU64::new(1);

/// Version 1 stack-scoped stream callback. `payload` is borrowed UTF-8 and is
/// valid only for the duration of the call; the callback must copy it before
/// returning. Return zero to continue or any non-zero value to abort locally.
/// The callback is synchronous, so it must be O(1) and non-blocking.
pub type PopglotStreamCallbackV1 = unsafe extern "C" fn(
    user_data: *mut c_void,
    event_type: i32,
    payload: *const c_char,
    byte_len: usize,
) -> i32;

/// The only event emitted by v1. Final, reset, and error state is conveyed by
/// the returned translation envelope; callers clear presentation on start.
pub const POPGLOT_STREAM_EVENT_TEXT_DELTA_V1: i32 = 1;

fn emit_text_delta(
    callback: Option<PopglotStreamCallbackV1>,
    user_data: *mut c_void,
    delta: &str,
    cancellation: &CancellationToken,
) {
    if cancellation.is_cancelled() || delta.is_empty() {
        return;
    }
    let Some(callback) = callback else { return };
    // The pointer targets `delta` and is never retained by Rust. A callback
    // panic or non-zero return becomes local cancellation.
    cancel_on_callback_abort(cancellation, || unsafe {
        callback(
            user_data,
            POPGLOT_STREAM_EVENT_TEXT_DELTA_V1,
            delta.as_ptr().cast::<c_char>(),
            delta.len(),
        )
    });
}

fn cancel_on_callback_abort(cancellation: &CancellationToken, callback: impl FnOnce() -> i32) {
    if !matches!(catch_unwind(AssertUnwindSafe(callback)), Ok(0)) {
        cancellation.cancel();
    }
}

#[derive(Serialize)]
struct Envelope<T: Serialize> {
    ok: bool,
    data: Option<T>,
    error: Option<String>,
}

fn success<T: Serialize>(data: T) -> *mut c_char {
    to_c_string(&Envelope {
        ok: true,
        data: Some(data),
        error: None,
    })
}

fn failure(message: impl Into<String>) -> *mut c_char {
    to_c_string(&Envelope::<()> {
        ok: false,
        data: None,
        error: Some(message.into()),
    })
}

fn to_c_string<T: Serialize>(value: &T) -> *mut c_char {
    let json = serde_json::to_string(value).unwrap_or_else(|error| {
        format!(r#"{{"ok":false,"data":null,"error":"serialization failed: {error}"}}"#)
    });
    CString::new(json).map_or(ptr::null_mut(), CString::into_raw)
}

/// # Safety
///
/// `value` must be a valid, null-terminated C string pointer or null.
unsafe fn read_utf8<'a>(value: *const c_char) -> Result<&'a str, String> {
    if value.is_null() {
        return Err("received a null string pointer".to_owned());
    }
    // SAFETY: Caller guarantees `value` is valid and null-terminated.
    unsafe { CStr::from_ptr(value) }
        .to_str()
        .map_err(|error| format!("invalid UTF-8: {error}"))
}

/// Reads an optional string argument, treating null as None.
///
/// # Safety
///
/// `value` must be null or point to a valid null-terminated C string.
unsafe fn read_optional_utf8<'a>(value: *const c_char) -> Result<Option<&'a str>, String> {
    if value.is_null() {
        return Ok(None);
    }
    // SAFETY: Caller guarantees `value` is valid and null-terminated.
    unsafe { read_utf8(value) }.map(Some)
}

/// Initializes the process-local core using a host-provided configuration directory.
///
/// # Safety
///
/// `config_directory` must be a valid null-terminated UTF-8 string pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_initialize(config_directory: *const c_char) -> *mut c_char {
    ffi_guard(|| {
        let path = unsafe { read_utf8(config_directory) }?;
        if CORE.get().is_some() {
            return Ok(success(env!("CARGO_PKG_VERSION")));
        }
        let core = AppCore::open(path).map_err(|error| error.to_string())?;
        CORE.set(RwLock::new(core))
            .map_err(|_| "PopGlot Core has already been initialized".to_owned())?;
        Ok(success(env!("CARGO_PKG_VERSION")))
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn popglot_get_settings() -> *mut c_char {
    ffi_guard(|| {
        let settings = {
            let core = core_read()?;
            core.settings().clone()
        };
        Ok(success(settings))
    })
}

/// Returns and clears the pending startup notice (empty string when none).
///
/// The shell shows it once so a corrupted-settings reset is never silent.
#[unsafe(no_mangle)]
pub extern "C" fn popglot_take_startup_notice() -> *mut c_char {
    ffi_guard(|| {
        let notice = {
            let mut core = core_write()?;
            core.take_startup_notice()
        };
        Ok(success(notice.unwrap_or_default()))
    })
}

/// Persists a UTF-8 JSON provider settings object atomically.
///
/// # Safety
///
/// `json` must be a valid null-terminated UTF-8 string pointer containing serialized JSON settings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_save_settings(json: *const c_char) -> *mut c_char {
    ffi_guard(|| {
        let json = unsafe { read_utf8(json) }?;
        let settings: ProviderSettings =
            serde_json::from_str(json).map_err(|error| error.to_string())?;
        let mut core = core_write()?;
        core.save_settings(settings)
            .map_err(|error| error.to_string())?;
        Ok(success("saved"))
    })
}

fn resolve_active_preference(
    core: &AppCore,
    source_lang: Option<&str>,
    target_lang: Option<&str>,
) -> Option<popglot_domain::CompiledPrompt> {
    let active = core.prompt_store().active_template();
    if active.id == BUILTIN_FAITHFUL_ID {
        return None;
    }

    let languages = resolve_languages(core.settings(), source_lang, target_lang);
    let vars = PromptVariables {
        source_language: languages.source,
        target_language: languages.target,
        domain: None,
        audience: None,
    };
    compile_prompt(&active, &vars).ok()
}

/// Lists all prompt templates (built-ins followed by custom templates).
#[unsafe(no_mangle)]
pub extern "C" fn popglot_list_prompt_templates() -> *mut c_char {
    ffi_guard(|| {
        let templates = {
            let core = core_read()?;
            core.prompt_store().list_templates()
        };
        Ok(success(templates))
    })
}

/// Returns the active prompt template (defaults to faithful).
#[unsafe(no_mangle)]
pub extern "C" fn popglot_get_active_prompt_template() -> *mut c_char {
    ffi_guard(|| {
        let active = {
            let core = core_read()?;
            core.prompt_store().active_template()
        };
        Ok(success(active))
    })
}

/// Sets the active prompt template by ID (or null/empty to reset to faithful).
///
/// # Safety
///
/// `id` must be null or a valid null-terminated UTF-8 string pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_set_active_prompt_template(id: *const c_char) -> *mut c_char {
    ffi_guard(|| {
        let id = unsafe { read_optional_utf8(id) }?;
        let mut core = core_write()?;
        core.prompt_store_mut()
            .set_active_template_id(id)
            .map_err(|error| error.to_string())?;
        Ok(success("active template updated"))
    })
}

/// Persists or updates a custom prompt template.
///
/// # Safety
///
/// `template_json` must be a valid null-terminated UTF-8 string pointer containing a serialized `PromptTemplate`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_save_prompt_template(template_json: *const c_char) -> *mut c_char {
    ffi_guard(|| {
        let json = unsafe { read_utf8(template_json) }?;
        let template: PromptTemplate =
            serde_json::from_str(json).map_err(|error| error.to_string())?;
        let mut core = core_write()?;
        let saved = core
            .prompt_store_mut()
            .save_custom_template(template)
            .map_err(|error| error.to_string())?;
        Ok(success(saved))
    })
}

/// Deletes a custom prompt template by ID.
///
/// # Safety
///
/// `id` must be a valid null-terminated UTF-8 string pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_delete_prompt_template(id: *const c_char) -> *mut c_char {
    ffi_guard(|| {
        let id = unsafe { read_utf8(id) }?;
        let mut core = core_write()?;
        core.prompt_store_mut()
            .delete_custom_template(id)
            .map_err(|error| error.to_string())?;
        Ok(success("deleted"))
    })
}

/// Compiles a prompt template with provided variables (pure function).
///
/// # Safety
///
/// Both pointers must be valid null-terminated UTF-8 string pointers.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_compile_prompt(
    template_json: *const c_char,
    variables_json: *const c_char,
) -> *mut c_char {
    ffi_guard(|| {
        let t_json = unsafe { read_utf8(template_json) }?;
        let v_json = unsafe { read_utf8(variables_json) }?;
        let template: PromptTemplate =
            serde_json::from_str(t_json).map_err(|error| error.to_string())?;
        let vars: PromptVariables =
            serde_json::from_str(v_json).map_err(|error| error.to_string())?;
        let compiled = compile_prompt(&template, &vars).map_err(|error| error.to_string())?;
        Ok(success(compiled))
    })
}

/// Reports which screenshot pipeline the current settings would choose.
#[unsafe(no_mangle)]
pub extern "C" fn popglot_plan_screenshot_route(
    local_ocr_available: i32,
    credential_present: i32,
) -> *mut c_char {
    ffi_guard(|| {
        let decision = {
            let core = core_read()?;
            core.plan_screenshot_route(local_ocr_available != 0, credential_present != 0)
        };
        Ok(success(decision))
    })
}

/// Pure helper: the screenshot routing DECISION TABLE. The shell collects
/// every observable fact, the domain owns the decision — one strategy for
/// the settings preview and the actual capture, in either language.
///
/// # Safety
///
/// `facts_json` must be a valid NUL-terminated UTF-8 pointer, or null (which
/// fails with an error envelope instead of dereferencing).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_select_route(facts_json: *const c_char) -> *mut c_char {
    ffi_guard(|| {
        let facts = unsafe { read_utf8(facts_json) }?;
        let context: popglot_domain::RoutingContext =
            serde_json::from_str(facts).map_err(|error| error.to_string())?;
        Ok(success(popglot_domain::select_route(&context)))
    })
}

/// Sends a user-initiated, text-only Provider connection test using saved settings.
///
/// # Safety
///
/// `api_key` must be a valid null-terminated UTF-8 string pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_test_connection(api_key: *const c_char) -> *mut c_char {
    ffi_guard(|| {
        let api_key = unsafe { read_utf8(api_key) }?;
        let (settings, client) = {
            let core = core_read()?;
            (core.settings().clone(), core.provider_client().clone())
        };
        let runtime = provider_runtime()?;
        let ticket = begin_request(None);
        let request_id = ticket.id.clone();
        let response = runtime
            .block_on(async {
                let provider = popglot_core::provider::provider_for(settings.provider_type);
                let request = popglot_core::provider::TranslationRequest::text(
                    "Connection test",
                    LanguagePair::new("en", &settings.target_language),
                )
                .with_explanation(false);
                client
                    .execute(
                        provider.as_ref(),
                        &settings,
                        api_key,
                        &request_id,
                        &request,
                        &ticket.token,
                    )
                    .await
            })
            .map_err(|error| error.to_string());
        Ok(response.map_or_else(failure, success))
    })
}

/// Tests connection against an in-memory draft settings object without saving it
/// to disk or altering the active configuration.
///
/// # Safety
///
/// `draft_json` and `api_key` must be valid null-terminated UTF-8 string pointers.
/// `request_id` can be null or a valid null-terminated UTF-8 string.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_test_connection_draft(
    draft_json: *const c_char,
    api_key: *const c_char,
    request_id: *const c_char,
) -> *mut c_char {
    ffi_guard(|| {
        let draft_json = unsafe { read_utf8(draft_json) }?;
        let api_key = unsafe { read_utf8(api_key) }?;
        let custom_id = unsafe { read_optional_utf8(request_id) }?;
        let draft_settings: ProviderSettings =
            serde_json::from_str(draft_json).map_err(|error| error.to_string())?;

        let runtime = provider_runtime()?;
        let ticket = begin_request(custom_id);
        let response = runtime
            .block_on(AppCore::test_connection_draft(
                &draft_settings,
                api_key,
                Some(&ticket.id),
                &ticket.token,
            ))
            .map_err(|error| error.to_string());
        Ok(response.map_or_else(failure, success))
    })
}

/// Translates selected UTF-8 text through the active Provider without holding a core lock.
///
/// # Safety
///
/// `api_key` and `source` must be valid null-terminated UTF-8 string pointers.
/// `source_lang` and `target_lang` can be null or valid null-terminated UTF-8 strings.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_translate_text(
    api_key: *const c_char,
    source: *const c_char,
    source_lang: *const c_char,
    target_lang: *const c_char,
) -> *mut c_char {
    unsafe { popglot_translate_text_v2(api_key, source, source_lang, target_lang, ptr::null()) }
}

/// Translates selected UTF-8 text with an optional caller-specified `request_id`.
///
/// # Safety
///
/// Pointers must be valid null-terminated UTF-8 strings or null where optional.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_translate_text_v2(
    api_key: *const c_char,
    source: *const c_char,
    source_lang: *const c_char,
    target_lang: *const c_char,
    request_id: *const c_char,
) -> *mut c_char {
    unsafe {
        popglot_translate_text_v3(
            api_key,
            source,
            source_lang,
            target_lang,
            request_id,
            ptr::null(),
            ptr::null(),
            0,
        )
    }
}

/// Translates selected UTF-8 text with explicit preference and template snapshot anchors.
///
/// # Safety
///
/// Pointers must be valid null-terminated UTF-8 strings or null where optional.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_translate_text_v3(
    api_key: *const c_char,
    source: *const c_char,
    source_lang: *const c_char,
    target_lang: *const c_char,
    request_id: *const c_char,
    preference: *const c_char,
    template_id: *const c_char,
    template_revision: u64,
) -> *mut c_char {
    ffi_guard(|| {
        let api_key = unsafe { read_utf8(api_key) }?;
        let source = unsafe { read_utf8(source) }?;
        let source_lang = unsafe { read_optional_utf8(source_lang) }?;
        let target_lang = unsafe { read_optional_utf8(target_lang) }?;
        let custom_id = unsafe { read_optional_utf8(request_id) }?;
        let explicit_pref = unsafe { read_optional_utf8(preference) }?;
        let explicit_tid = unsafe { read_optional_utf8(template_id) }?;

        let (settings, client, resolved_pref) = {
            let core = core_read()?;
            let pref = if explicit_pref.is_none() {
                resolve_active_preference(&core, source_lang, target_lang)
            } else {
                None
            };
            (
                core.settings().clone(),
                core.provider_client().clone(),
                pref,
            )
        };

        let (pref_str, tid_str, rev_val) = if let Some(pref) = explicit_pref {
            (Some(pref), explicit_tid, Some(template_revision))
        } else if let Some(cp) = &resolved_pref {
            (
                Some(cp.compiled_text.as_str()),
                Some(cp.template_id.as_str()),
                Some(cp.revision),
            )
        } else {
            (None, None, None)
        };

        let languages = resolve_languages(&settings, source_lang, target_lang);
        let runtime = provider_runtime()?;
        let ticket = begin_request(custom_id);
        let response = runtime
            .block_on(AppCore::execute_translate_text_snapshot_with_preference(
                &settings,
                &client,
                api_key,
                source,
                &languages,
                &ticket.id,
                pref_str,
                tid_str,
                rev_val,
                &ticket.token,
            ))
            .map_err(|error| error.to_string());
        Ok(response.map_or_else(failure, success))
    })
}

/// Runs a summary or quick-explanation task through the active text provider.
///
/// # Safety
/// All pointers must be valid NUL-terminated UTF-8 strings; language pointers may be null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_text_task_v1(
    api_key: *const c_char,
    source: *const c_char,
    source_lang: *const c_char,
    target_lang: *const c_char,
    task: *const c_char,
    request_id: *const c_char,
) -> *mut c_char {
    ffi_guard(|| {
        let api_key = unsafe { read_utf8(api_key) }?;
        let source = unsafe { read_utf8(source) }?;
        let source_lang = unsafe { read_optional_utf8(source_lang) }?;
        let target_lang = unsafe { read_optional_utf8(target_lang) }?;
        let task = popglot_core::provider::TextTask::parse(unsafe { read_utf8(task) }?)
            .map_err(|error| error.to_string())?;
        let custom_id = unsafe { read_optional_utf8(request_id) }?;
        let (settings, client) = {
            let core = core_read()?;
            (core.settings().clone(), core.provider_client().clone())
        };
        let languages = resolve_languages(&settings, source_lang, target_lang);
        let runtime = provider_runtime()?;
        let ticket = begin_request(custom_id);
        let response = runtime
            .block_on(AppCore::execute_text_task_snapshot(
                &settings,
                &client,
                api_key,
                source,
                &languages,
                &ticket.id,
                task,
                &ticket.token,
            ))
            .map_err(|error| error.to_string());
        Ok(response.map_or_else(failure, success))
    })
}

/// Runs a text task through a complete, non-persisted provider snapshot.
///
/// # Safety
/// All pointers must be valid NUL-terminated UTF-8 strings; language pointers may be null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_text_task_draft_v1(
    settings_json: *const c_char,
    api_key: *const c_char,
    source: *const c_char,
    source_lang: *const c_char,
    target_lang: *const c_char,
    task: *const c_char,
    request_id: *const c_char,
) -> *mut c_char {
    ffi_guard(|| {
        let settings_json = unsafe { read_utf8(settings_json) }?;
        let api_key = unsafe { read_utf8(api_key) }?;
        let source = unsafe { read_utf8(source) }?;
        let source_lang = unsafe { read_optional_utf8(source_lang) }?;
        let target_lang = unsafe { read_optional_utf8(target_lang) }?;
        let task = popglot_core::provider::TextTask::parse(unsafe { read_utf8(task) }?)
            .map_err(|error| error.to_string())?;
        let custom_id = unsafe { read_optional_utf8(request_id) }?;
        let settings = serde_json::from_str::<ProviderSettings>(settings_json)
            .map_err(|error| format!("文字任务设置无效：{error}"))?;
        let client = ProviderClient::new(AppCore::limits_for(&settings))
            .map_err(|error| error.to_string())?;
        let languages = resolve_languages(&settings, source_lang, target_lang);
        let runtime = provider_runtime()?;
        let ticket = begin_request(custom_id);
        let response = runtime
            .block_on(AppCore::execute_text_task_snapshot(
                &settings,
                &client,
                api_key,
                source,
                &languages,
                &ticket.id,
                task,
                &ticket.token,
            ))
            .map_err(|error| error.to_string());
        Ok(response.map_or_else(failure, success))
    })
}

/// Translates text through a complete, non-persisted provider snapshot.
/// The snapshot and credential are supplied together so OCR output cannot
/// accidentally use a stale global provider configuration.
///
/// # Safety
///
/// Pointers must be valid null-terminated UTF-8 strings or null where optional.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_translate_text_draft_v1(
    settings_json: *const c_char,
    api_key: *const c_char,
    source: *const c_char,
    source_lang: *const c_char,
    target_lang: *const c_char,
    request_id: *const c_char,
) -> *mut c_char {
    ffi_guard(|| {
        let settings_json = unsafe { read_utf8(settings_json) }?;
        let api_key = unsafe { read_utf8(api_key) }?;
        let source = unsafe { read_utf8(source) }?;
        let source_lang = unsafe { read_optional_utf8(source_lang) }?;
        let target_lang = unsafe { read_optional_utf8(target_lang) }?;
        let custom_id = unsafe { read_optional_utf8(request_id) }?;
        let settings = serde_json::from_str::<popglot_domain::ProviderSettings>(settings_json)
            .map_err(|error| format!("文字草稿设置无效：{error}"))?;
        let client = ProviderClient::new(AppCore::limits_for(&settings))
            .map_err(|error| error.to_string())?;
        let languages = resolve_languages(&settings, source_lang, target_lang);
        let runtime = provider_runtime()?;
        let ticket = begin_request(custom_id);
        let response = runtime
            .block_on(AppCore::execute_translate_text_snapshot(
                &settings,
                &client,
                api_key,
                source,
                &languages,
                &ticket.id,
                &ticket.token,
            ))
            .map_err(|error| error.to_string());
        Ok(response.map_or_else(failure, success))
    })
}

/// Translates one base64-encoded screenshot through the active vision Provider.
///
/// # Safety
///
/// Pointers must be valid null-terminated UTF-8 strings or null where optional.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_translate_vision(
    api_key: *const c_char,
    media_type: *const c_char,
    image_base64: *const c_char,
    source_lang: *const c_char,
    target_lang: *const c_char,
) -> *mut c_char {
    unsafe {
        popglot_translate_vision_v2(
            api_key,
            media_type,
            image_base64,
            source_lang,
            target_lang,
            ptr::null(),
        )
    }
}

/// Translates one base64-encoded screenshot with an optional caller-specified `request_id`.
///
/// # Safety
///
/// Pointers must be valid null-terminated UTF-8 strings or null where optional.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_translate_vision_v2(
    api_key: *const c_char,
    media_type: *const c_char,
    image_base64: *const c_char,
    source_lang: *const c_char,
    target_lang: *const c_char,
    request_id: *const c_char,
) -> *mut c_char {
    ffi_guard(|| {
        let api_key = unsafe { read_utf8(api_key) }?;
        let media_type = unsafe { read_utf8(media_type) }?;
        let image_base64 = unsafe { read_utf8(image_base64) }?;
        let source_lang = unsafe { read_optional_utf8(source_lang) }?;
        let target_lang = unsafe { read_optional_utf8(target_lang) }?;
        let custom_id = unsafe { read_optional_utf8(request_id) }?;

        if image_base64.len() > 12 * 1024 * 1024 {
            return Err("编码后的截图超过 12 MiB FFI 上限。".to_owned());
        }
        let image = base64::engine::general_purpose::STANDARD
            .decode(image_base64)
            .map_err(|_| "截图不是有效的 base64。".to_owned())?;

        let (settings, client) = {
            let core = core_read()?;
            (core.settings().clone(), core.provider_client().clone())
        };
        let languages = resolve_languages(&settings, source_lang, target_lang);
        let runtime = provider_runtime()?;
        let ticket = begin_request(custom_id);
        let response = runtime
            .block_on(AppCore::execute_translate_vision_snapshot(
                &settings,
                &client,
                api_key,
                "",
                media_type,
                image,
                &languages,
                &ticket.id,
                &ticket.token,
            ))
            .map_err(|error| error.to_string());
        Ok(response.map_or_else(failure, success))
    })
}

/// Translates one screenshot through a draft settings snapshot with a
/// dedicated vision provider: the shell passes the text key, the vision
/// key, and (optionally) a full settings JSON that is used without being
/// persisted. When `settings_json` is empty the stored settings apply.
///
/// # Safety
///
/// Pointers must be valid null-terminated UTF-8 strings or null where optional.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_translate_vision_v3(
    api_key: *const c_char,
    vision_api_key: *const c_char,
    settings_json: *const c_char,
    media_type: *const c_char,
    image_base64: *const c_char,
    source_lang: *const c_char,
    target_lang: *const c_char,
    request_id: *const c_char,
) -> *mut c_char {
    ffi_guard(|| {
        let api_key = unsafe { read_utf8(api_key) }?;
        let vision_api_key = unsafe { read_optional_utf8(vision_api_key) }?.unwrap_or_default();
        let settings_json = unsafe { read_optional_utf8(settings_json) }?.unwrap_or_default();
        let media_type = unsafe { read_utf8(media_type) }?;
        let image_base64 = unsafe { read_utf8(image_base64) }?;
        let source_lang = unsafe { read_optional_utf8(source_lang) }?;
        let target_lang = unsafe { read_optional_utf8(target_lang) }?;
        let custom_id = unsafe { read_optional_utf8(request_id) }?;

        if image_base64.len() > 12 * 1024 * 1024 {
            return Err("编码后的截图超过 12 MiB FFI 上限。".to_owned());
        }
        let image = base64::engine::general_purpose::STANDARD
            .decode(image_base64)
            .map_err(|_| "截图不是有效的 base64。".to_owned())?;

        let settings = if settings_json.trim().is_empty() {
            let core = core_read()?;
            core.settings().clone()
        } else {
            serde_json::from_str::<popglot_domain::ProviderSettings>(settings_json)
                .map_err(|error| format!("视觉草稿设置无效：{error}"))?
        };
        // A draft may target a completely different provider and TLS policy;
        // the global Core client is therefore never safe to reuse here.
        let client = ProviderClient::new(AppCore::limits_for(&settings))
            .map_err(|error| error.to_string())?;
        let languages = resolve_languages(&settings, source_lang, target_lang);
        let runtime = provider_runtime()?;
        let ticket = begin_request(custom_id);
        let response = runtime
            .block_on(AppCore::execute_translate_vision_snapshot(
                &settings,
                &client,
                api_key,
                vision_api_key,
                media_type,
                image,
                &languages,
                &ticket.id,
                &ticket.token,
            ))
            .map_err(|error| error.to_string());
        Ok(response.map_or_else(failure, success))
    })
}

/// Streams active settings text translation via a stack-scoped v1 callback.
/// The callback is invoked only before return, receives borrowed UTF-8 bytes,
/// and runs synchronously on the provider task: copy/enqueue only, never block.
///
/// # Safety
///
/// All non-null string pointers must reference valid null-terminated UTF-8 for
/// the duration of this call. If supplied, `callback` must be valid and must
/// not retain the borrowed payload pointer or unwind across the FFI boundary.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_translate_text_stream_v1(
    api_key: *const c_char,
    source: *const c_char,
    source_lang: *const c_char,
    target_lang: *const c_char,
    request_id: *const c_char,
    callback: Option<PopglotStreamCallbackV1>,
    user_data: *mut c_void,
) -> *mut c_char {
    unsafe {
        popglot_translate_text_stream_v2(
            api_key,
            source,
            source_lang,
            target_lang,
            request_id,
            ptr::null(),
            ptr::null(),
            0,
            callback,
            user_data,
        )
    }
}

/// Streams active settings text translation with explicit preference and template snapshot anchors.
///
/// # Safety
///
/// All non-null string pointers must reference valid null-terminated UTF-8 for
/// the duration of this call. If supplied, `callback` must be valid and must
/// not retain the borrowed payload pointer or unwind across the FFI boundary.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_translate_text_stream_v2(
    api_key: *const c_char,
    source: *const c_char,
    source_lang: *const c_char,
    target_lang: *const c_char,
    request_id: *const c_char,
    preference: *const c_char,
    template_id: *const c_char,
    template_revision: u64,
    callback: Option<PopglotStreamCallbackV1>,
    user_data: *mut c_void,
) -> *mut c_char {
    ffi_guard(|| {
        let api_key = unsafe { read_utf8(api_key) }?;
        let source = unsafe { read_utf8(source) }?;
        let source_lang = unsafe { read_optional_utf8(source_lang) }?;
        let target_lang = unsafe { read_optional_utf8(target_lang) }?;
        let custom_id = unsafe { read_optional_utf8(request_id) }?;
        let explicit_pref = unsafe { read_optional_utf8(preference) }?;
        let explicit_tid = unsafe { read_optional_utf8(template_id) }?;

        let (settings, client, resolved_pref) = {
            let core = core_read()?;
            let pref = if explicit_pref.is_none() {
                resolve_active_preference(&core, source_lang, target_lang)
            } else {
                None
            };
            (
                core.settings().clone(),
                core.provider_client().clone(),
                pref,
            )
        };

        let (pref_str, tid_str, rev_val) = if let Some(pref) = explicit_pref {
            (Some(pref), explicit_tid, Some(template_revision))
        } else if let Some(cp) = &resolved_pref {
            (
                Some(cp.compiled_text.as_str()),
                Some(cp.template_id.as_str()),
                Some(cp.revision),
            )
        } else {
            (None, None, None)
        };

        let languages = resolve_languages(&settings, source_lang, target_lang);
        let runtime = provider_runtime()?;
        let ticket = begin_request(custom_id);
        let response = runtime
            .block_on(
                AppCore::execute_translate_text_stream_snapshot_with_preference(
                    &settings,
                    &client,
                    api_key,
                    source,
                    &languages,
                    &ticket.id,
                    pref_str,
                    tid_str,
                    rev_val,
                    &ticket.token,
                    |delta| emit_text_delta(callback, user_data, delta, &ticket.token),
                ),
            )
            .map_err(|error| error.to_string());
        Ok(response.map_or_else(failure, success))
    })
}

/// Streams text translation through an unpersisted settings draft.
///
/// # Safety
///
/// All non-null string pointers must reference valid null-terminated UTF-8 for
/// the duration of this call. If supplied, `callback` must be valid and must
/// not retain the borrowed payload pointer or unwind across the FFI boundary.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_translate_text_draft_stream_v1(
    settings_json: *const c_char,
    api_key: *const c_char,
    source: *const c_char,
    source_lang: *const c_char,
    target_lang: *const c_char,
    request_id: *const c_char,
    callback: Option<PopglotStreamCallbackV1>,
    user_data: *mut c_void,
) -> *mut c_char {
    unsafe {
        popglot_translate_text_draft_stream_v2(
            settings_json,
            api_key,
            source,
            source_lang,
            target_lang,
            request_id,
            ptr::null(),
            ptr::null(),
            0,
            callback,
            user_data,
        )
    }
}

/// Streams text translation through an unpersisted settings draft with explicit preference.
///
/// # Safety
///
/// All non-null string pointers must reference valid null-terminated UTF-8 for
/// the duration of this call. If supplied, `callback` must be valid and must
/// not retain the borrowed payload pointer or unwind across the FFI boundary.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_translate_text_draft_stream_v2(
    settings_json: *const c_char,
    api_key: *const c_char,
    source: *const c_char,
    source_lang: *const c_char,
    target_lang: *const c_char,
    request_id: *const c_char,
    preference: *const c_char,
    template_id: *const c_char,
    template_revision: u64,
    callback: Option<PopglotStreamCallbackV1>,
    user_data: *mut c_void,
) -> *mut c_char {
    ffi_guard(|| {
        let settings_json = unsafe { read_utf8(settings_json) }?;
        let api_key = unsafe { read_utf8(api_key) }?;
        let source = unsafe { read_utf8(source) }?;
        let source_lang = unsafe { read_optional_utf8(source_lang) }?;
        let target_lang = unsafe { read_optional_utf8(target_lang) }?;
        let custom_id = unsafe { read_optional_utf8(request_id) }?;
        let explicit_pref = unsafe { read_optional_utf8(preference) }?;
        let explicit_tid = unsafe { read_optional_utf8(template_id) }?;

        let settings: ProviderSettings = serde_json::from_str(settings_json)
            .map_err(|error| format!("文字草稿设置无效：{error}"))?;
        let client = ProviderClient::new(AppCore::limits_for(&settings))
            .map_err(|error| error.to_string())?;

        let resolved_pref = if explicit_pref.is_none() {
            core_read()
                .ok()
                .and_then(|core| resolve_active_preference(&core, source_lang, target_lang))
        } else {
            None
        };

        let (pref_str, tid_str, rev_val) = if let Some(pref) = explicit_pref {
            (Some(pref), explicit_tid, Some(template_revision))
        } else if let Some(cp) = &resolved_pref {
            (
                Some(cp.compiled_text.as_str()),
                Some(cp.template_id.as_str()),
                Some(cp.revision),
            )
        } else {
            (None, None, None)
        };

        let languages = resolve_languages(&settings, source_lang, target_lang);
        let runtime = provider_runtime()?;
        let ticket = begin_request(custom_id);
        let response = runtime
            .block_on(
                AppCore::execute_translate_text_stream_snapshot_with_preference(
                    &settings,
                    &client,
                    api_key,
                    source,
                    &languages,
                    &ticket.id,
                    pref_str,
                    tid_str,
                    rev_val,
                    &ticket.token,
                    |delta| emit_text_delta(callback, user_data, delta, &ticket.token),
                ),
            )
            .map_err(|error| error.to_string());
        Ok(response.map_or_else(failure, success))
    })
}

/// Streams a screenshot through unpersisted (or empty, active) vision settings.
///
/// # Safety
///
/// All non-null string pointers must reference valid null-terminated UTF-8 for
/// the duration of this call. If supplied, `callback` must be valid and must
/// not retain the borrowed payload pointer or unwind across the FFI boundary.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_translate_vision_draft_stream_v1(
    api_key: *const c_char,
    vision_api_key: *const c_char,
    settings_json: *const c_char,
    media_type: *const c_char,
    image_base64: *const c_char,
    source_lang: *const c_char,
    target_lang: *const c_char,
    request_id: *const c_char,
    callback: Option<PopglotStreamCallbackV1>,
    user_data: *mut c_void,
) -> *mut c_char {
    ffi_guard(|| {
        let api_key = unsafe { read_utf8(api_key) }?;
        let vision_api_key = unsafe { read_optional_utf8(vision_api_key) }?.unwrap_or_default();
        let settings_json = unsafe { read_optional_utf8(settings_json) }?.unwrap_or_default();
        let media_type = unsafe { read_utf8(media_type) }?;
        let image_base64 = unsafe { read_utf8(image_base64) }?;
        let source_lang = unsafe { read_optional_utf8(source_lang) }?;
        let target_lang = unsafe { read_optional_utf8(target_lang) }?;
        let custom_id = unsafe { read_optional_utf8(request_id) }?;
        if image_base64.len() > 12 * 1024 * 1024 {
            return Err("编码后的截图超过 12 MiB FFI 上限。".to_owned());
        }
        let image = base64::engine::general_purpose::STANDARD
            .decode(image_base64)
            .map_err(|_| "截图不是有效的 base64。".to_owned())?;
        let settings = if settings_json.trim().is_empty() {
            core_read()?.settings().clone()
        } else {
            serde_json::from_str::<ProviderSettings>(settings_json)
                .map_err(|error| format!("视觉草稿设置无效：{error}"))?
        };
        let client = ProviderClient::new(AppCore::limits_for(&settings))
            .map_err(|error| error.to_string())?;
        let languages = resolve_languages(&settings, source_lang, target_lang);
        let runtime = provider_runtime()?;
        let ticket = begin_request(custom_id);
        let response = runtime
            .block_on(AppCore::execute_translate_vision_stream_snapshot(
                &settings,
                &client,
                api_key,
                vision_api_key,
                media_type,
                image,
                &languages,
                &ticket.id,
                &ticket.token,
                |delta| emit_text_delta(callback, user_data, delta, &ticket.token),
            ))
            .map_err(|error| error.to_string());
        Ok(response.map_or_else(failure, success))
    })
}

/// Cancels an exact in-flight request by its `request_id`.
///
/// # Safety
///
/// `request_id` must be a valid null-terminated UTF-8 string pointer or null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_cancel_request(request_id: *const c_char) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Ok(id) = (unsafe { read_utf8(request_id) }) else {
            return 0;
        };
        let requests = ACTIVE_REQUESTS.get_or_init(|| Mutex::new(HashMap::new()));
        let mut map = match requests.lock() {
            Ok(guard) => guard,
            Err(poisoned) => poisoned.into_inner(),
        };
        if let Some(token) = map.remove(id) {
            token.cancel();
            return 1;
        }
        0
    }))
    .unwrap_or(0)
}

/// Cancels all active requests in the process.
#[unsafe(no_mangle)]
pub extern "C" fn popglot_cancel_active_request() -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let requests = ACTIVE_REQUESTS.get_or_init(|| Mutex::new(HashMap::new()));
        let mut map = match requests.lock() {
            Ok(guard) => guard,
            Err(poisoned) => poisoned.into_inner(),
        };
        let mut cancelled_any = false;
        for (_, token) in map.drain() {
            token.cancel();
            cancelled_any = true;
        }
        i32::from(cancelled_any)
    }))
    .unwrap_or(0)
}

/// Releases strings returned by this library.
///
/// # Safety
///
/// `value` must be a pointer previously allocated and returned by this library, or null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_free_string(value: *mut c_char) {
    if !value.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| {
            drop(unsafe { CString::from_raw(value) });
        }));
    }
}

fn resolve_languages(
    settings: &ProviderSettings,
    source_lang: Option<&str>,
    target_lang: Option<&str>,
) -> LanguagePair {
    let stored = settings.language_pair();
    LanguagePair::new(
        source_lang
            .filter(|value| !value.trim().is_empty())
            .unwrap_or(&stored.source),
        target_lang
            .filter(|value| !value.trim().is_empty())
            .unwrap_or(&stored.target),
    )
}

fn core_read() -> Result<RwLockReadGuard<'static, AppCore>, String> {
    CORE.get()
        .ok_or_else(|| "PopGlot Core is not initialized".to_owned())?
        .read()
        .map_err(|_| "PopGlot Core lock is poisoned".to_owned())
}

fn core_write() -> Result<RwLockWriteGuard<'static, AppCore>, String> {
    CORE.get()
        .ok_or_else(|| "PopGlot Core is not initialized".to_owned())?
        .write()
        .map_err(|_| "PopGlot Core lock is poisoned".to_owned())
}

fn provider_runtime() -> Result<&'static Runtime, String> {
    if let Some(runtime) = RUNTIME.get() {
        return Ok(runtime);
    }
    let runtime =
        Runtime::new().map_err(|error| format!("无法启动异步 Provider Runtime：{error}"))?;
    let _ = RUNTIME.set(runtime);
    RUNTIME
        .get()
        .ok_or_else(|| "异步 Provider Runtime 初始化失败".to_owned())
}

struct RequestTicket {
    id: String,
    token: CancellationToken,
}

impl Drop for RequestTicket {
    fn drop(&mut self) {
        let requests = ACTIVE_REQUESTS.get_or_init(|| Mutex::new(HashMap::new()));
        let mut map = match requests.lock() {
            Ok(guard) => guard,
            Err(poisoned) => poisoned.into_inner(),
        };
        map.remove(&self.id);
    }
}

fn begin_request(custom_id: Option<&str>) -> RequestTicket {
    let base = custom_id.map_or_else(
        || format!("req-{}", REQUEST_TICKET.fetch_add(1, Ordering::Relaxed)),
        ToOwned::to_owned,
    );
    let token = CancellationToken::new();
    let requests = ACTIVE_REQUESTS.get_or_init(|| Mutex::new(HashMap::new()));
    let mut map = match requests.lock() {
        Ok(guard) => guard,
        Err(poisoned) => poisoned.into_inner(),
    };
    let mut id = base.clone();
    while map.contains_key(&id) {
        // A live registration must never be evicted by a repeated id: the
        // old ticket's drop would then remove the new request's entry and
        // leave an in-flight call uncancellable. Disambiguate with the
        // monotonic ticket counter instead.
        id = format!("{base}#{}", REQUEST_TICKET.fetch_add(1, Ordering::Relaxed));
    }
    map.insert(id.clone(), token.clone());
    RequestTicket { id, token }
}

fn ffi_guard(operation: impl FnOnce() -> Result<*mut c_char, String>) -> *mut c_char {
    match catch_unwind(AssertUnwindSafe(operation)) {
        Ok(Ok(value)) => value,
        Ok(Err(error)) => failure(error),
        Err(_) => failure("PopGlot Core encountered an unexpected panic"),
    }
}

/// Pure helper: masks technical tokens in `text`. Shared by every text route
/// (the configured providers run it inside the core; the built-in free engine
/// calls it from the shell through this export) so both languages execute one
/// regex set, never a divergent copy.
///
/// # Safety
///
/// `text` must be a valid NUL-terminated UTF-8 pointer, or null (which fails
/// with an error envelope instead of dereferencing).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_protect_tokens(text: *const c_char) -> *mut c_char {
    ffi_guard(|| {
        let source = unsafe { read_utf8(text) }?;
        let protected = popglot_domain::protect_tokens(source);
        Ok(success(protected))
    })
}

/// Pure helper: restores placeholders with exactly-once semantics. `tokens`
/// is the JSON array the shell received from `popglot_protect_tokens`.
///
/// # Safety
///
/// `translated` and `tokens_json` must be valid NUL-terminated UTF-8
/// pointers, or null (which fails with an error envelope).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_restore_tokens(
    translated: *const c_char,
    tokens_json: *const c_char,
) -> *mut c_char {
    ffi_guard(|| {
        let translated = unsafe { read_utf8(translated) }?;
        let tokens_json = unsafe { read_utf8(tokens_json) }?;
        let tokens: Vec<popglot_domain::ProtectedToken> =
            serde_json::from_str(tokens_json).map_err(|error| error.to_string())?;
        Ok(success(popglot_domain::restore_tokens(translated, &tokens)))
    })
}

/// Pure helper: classifies an endpoint host so the shell and the core reach
/// the same conclusion from the same fixture list.
///
/// # Safety
///
/// `text` must be a valid NUL-terminated UTF-8 pointer, or null (which fails
/// with an error envelope instead of dereferencing).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_classify_endpoint(text: *const c_char) -> *mut c_char {
    ffi_guard(|| {
        let candidate = unsafe { read_utf8(text) }?;
        let class = match popglot_domain::classify_endpoint(candidate) {
            popglot_domain::EndpointClass::Loopback => "loopback",
            popglot_domain::EndpointClass::PrivateNetwork => "private",
            popglot_domain::EndpointClass::Internet => "internet",
        };
        Ok(success(serde_json::json!({ "class": class })))
    })
}

/// Pure helper: plans how a long source is translated within one session's
/// request budget — ordered segments whose concatenation reproduces the
/// source, or an explicit rejection reason sent before anything goes out.
///
/// # Safety
///
/// `text` must be a valid NUL-terminated UTF-8 pointer, or null (which fails
/// with an error envelope instead of dereferencing).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_plan_segments(
    text: *const c_char,
    max_segment_chars: u32,
    max_segments: u32,
) -> *mut c_char {
    ffi_guard(|| {
        let source = unsafe { read_utf8(text) }?;
        let plan = popglot_domain::plan_translation_segments(
            source,
            max_segment_chars as usize,
            max_segments as usize,
        );
        let payload = match plan {
            popglot_domain::SegmentPlan::Single => serde_json::json!({ "mode": "single" }),
            popglot_domain::SegmentPlan::Segments(segments) => serde_json::json!({
                "mode": "segments",
                "segments": segments,
            }),
            popglot_domain::SegmentPlan::Rejected(reason) => serde_json::json!({
                "mode": "rejected",
                "rejected_reason": reason,
            }),
        };
        Ok(success(payload))
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Read, Write};
    use std::net::TcpListener;
    use std::thread;

    static TEST_SERIAL_LOCK: Mutex<()> = Mutex::new(());

    fn lock_test_serial() -> std::sync::MutexGuard<'static, ()> {
        match TEST_SERIAL_LOCK.lock() {
            Ok(guard) => guard,
            Err(poisoned) => poisoned.into_inner(),
        }
    }

    unsafe extern "C" fn collect_delta(
        user_data: *mut c_void,
        event_type: i32,
        payload: *const c_char,
        byte_len: usize,
    ) -> i32 {
        assert_eq!(event_type, POPGLOT_STREAM_EVENT_TEXT_DELTA_V1);
        // SAFETY: test callback receives the FFI-provided borrowed UTF-8 range.
        let text = unsafe { std::slice::from_raw_parts(payload.cast::<u8>(), byte_len) };
        // SAFETY: caller supplied a valid collector for this stack-scoped call.
        unsafe { &mut *user_data.cast::<Vec<String>>() }
            .push(std::str::from_utf8(text).expect("valid UTF-8").to_owned());
        0
    }

    unsafe extern "C" fn abort_delta(_: *mut c_void, _: i32, _: *const c_char, _: usize) -> i32 {
        7
    }

    #[test]
    fn stream_callback_copies_utf8_bytes_with_exact_length() {
        let cancellation = CancellationToken::new();
        let mut collected: Vec<String> = Vec::new();
        emit_text_delta(
            Some(collect_delta),
            (&raw mut collected).cast::<c_void>(),
            "中文 delta ✓",
            &cancellation,
        );
        assert_eq!(collected, ["中文 delta ✓"]);
        assert!(!cancellation.is_cancelled());
    }

    #[test]
    fn stream_callback_abort_stops_the_request_and_null_callback_is_noop() {
        let cancellation = CancellationToken::new();
        emit_text_delta(Some(abort_delta), ptr::null_mut(), "first", &cancellation);
        assert!(cancellation.is_cancelled());
        // A cancelled request never calls a later callback.
        emit_text_delta(None, ptr::null_mut(), "later", &cancellation);
    }

    #[test]
    fn stream_callback_panic_is_isolated_as_local_cancellation() {
        let cancellation = CancellationToken::new();
        cancel_on_callback_abort(&cancellation, || -> i32 { panic!("test callback panic") });
        assert!(cancellation.is_cancelled());
    }

    #[test]
    fn stream_with_empty_callback_returns_final_envelope_from_local_mock() {
        let _lock = lock_test_serial();

        let listener = TcpListener::bind("127.0.0.1:0").expect("bind mock provider");
        let base_url = format!("http://{}", listener.local_addr().expect("mock address"));
        let worker = thread::spawn(move || {
            let (mut stream, _) = listener.accept().expect("accept provider request");
            let mut request = [0_u8; 8192];
            let _ = stream.read(&mut request).expect("read provider request");
            let body = r#"{"choices":[{"message":{"content":"最终译文"}}]}"#;
            let response = format!(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                body.len()
            );
            stream
                .write_all(response.as_bytes())
                .expect("write mock response");
        });

        let settings = ProviderSettings {
            api_base_url: base_url,
            text_model: "mock-stream-model".to_owned(),
            ..ProviderSettings::default()
        };
        let settings_json = CString::new(serde_json::to_string(&settings).expect("settings JSON"))
            .expect("settings CString");
        let api_key = CString::new("test-key").expect("key CString");
        let source = CString::new("hello").expect("source CString");
        let request_id = CString::new("ffi-empty-callback").expect("id CString");
        let result = unsafe {
            popglot_translate_text_draft_stream_v1(
                settings_json.as_ptr(),
                api_key.as_ptr(),
                source.as_ptr(),
                ptr::null(),
                ptr::null(),
                request_id.as_ptr(),
                None,
                ptr::null_mut(),
            )
        };
        assert!(!result.is_null());
        // SAFETY: the FFI function returns one owned, nul-terminated envelope.
        let envelope = unsafe { CStr::from_ptr(result) }
            .to_str()
            .expect("UTF-8 envelope")
            .to_owned();
        unsafe { popglot_free_string(result) };
        worker.join().expect("mock worker");
        assert!(envelope.contains("\"ok\":true"), "{envelope}");
        assert!(envelope.contains("最终译文"), "{envelope}");
    }

    #[test]
    fn stream_text_draft_yields_deltas_and_final_envelope() {
        let _lock = lock_test_serial();

        let listener = TcpListener::bind("127.0.0.1:0").expect("bind mock provider");
        let base_url = format!("http://{}", listener.local_addr().expect("mock address"));
        let worker = thread::spawn(move || {
            let (mut stream, _) = listener.accept().expect("accept provider request");
            let mut request = [0_u8; 8192];
            let _ = stream.read(&mut request).expect("read provider request");
            let body = "data: {\"choices\":[{\"delta\":{\"content\":\"流式\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"翻译\"}}]}\n\ndata: [DONE]\n\n";
            let response = format!(
                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n{body}"
            );
            stream
                .write_all(response.as_bytes())
                .expect("write mock response");
        });

        let settings = ProviderSettings {
            api_base_url: base_url,
            text_model: "mock-stream-model".to_owned(),
            ..ProviderSettings::default()
        };
        let settings_json = CString::new(serde_json::to_string(&settings).expect("settings JSON"))
            .expect("settings CString");
        let api_key = CString::new("test-key").expect("key CString");
        let source = CString::new("hello").expect("source CString");
        let request_id = CString::new("ffi-stream-deltas").expect("id CString");
        let mut collected: Vec<String> = Vec::new();
        let result = unsafe {
            popglot_translate_text_draft_stream_v1(
                settings_json.as_ptr(),
                api_key.as_ptr(),
                source.as_ptr(),
                ptr::null(),
                ptr::null(),
                request_id.as_ptr(),
                Some(collect_delta),
                (&raw mut collected).cast::<c_void>(),
            )
        };
        assert!(!result.is_null());
        let envelope = unsafe { CStr::from_ptr(result) }
            .to_str()
            .expect("UTF-8 envelope")
            .to_owned();
        unsafe { popglot_free_string(result) };
        worker.join().expect("mock worker");
        assert_eq!(collected, ["流式", "翻译"]);
        assert!(envelope.contains("\"ok\":true"), "{envelope}");
        assert!(envelope.contains("流式翻译"), "{envelope}");
    }

    #[test]
    fn request_ticket_raii_drop_cleans_active_requests() {
        let _lock = lock_test_serial();
        let ticket = begin_request(Some("test-raii-drop"));
        {
            let requests = ACTIVE_REQUESTS.get_or_init(|| Mutex::new(HashMap::new()));
            let map = match requests.lock() {
                Ok(guard) => guard,
                Err(poisoned) => poisoned.into_inner(),
            };
            assert!(map.contains_key("test-raii-drop"));
        }
        drop(ticket);
        {
            let requests = ACTIVE_REQUESTS.get_or_init(|| Mutex::new(HashMap::new()));
            let map = match requests.lock() {
                Ok(guard) => guard,
                Err(poisoned) => poisoned.into_inner(),
            };
            assert!(!map.contains_key("test-raii-drop"));
        }
    }

    #[test]
    fn request_ticket_cleans_up_on_panic_unwind() {
        let _lock = lock_test_serial();
        let unwind_result = catch_unwind(AssertUnwindSafe(|| {
            let _ticket = begin_request(Some("test-panic-unwind"));
            panic!("intentional panic during provider execution");
        }));
        assert!(unwind_result.is_err());

        let requests = ACTIVE_REQUESTS.get_or_init(|| Mutex::new(HashMap::new()));
        let map = match requests.lock() {
            Ok(guard) => guard,
            Err(poisoned) => poisoned.into_inner(),
        };
        assert!(!map.contains_key("test-panic-unwind"));
    }

    #[test]
    fn request_ticket_drop_does_not_panic_on_poisoned_lock() {
        let _lock = lock_test_serial();
        // Poison the ACTIVE_REQUESTS lock in a separate thread.
        let _ = std::thread::spawn(|| {
            let requests = ACTIVE_REQUESTS.get_or_init(|| Mutex::new(HashMap::new()));
            let _guard = requests.lock().expect("lock to poison");
            panic!("intentional panic to poison lock");
        })
        .join();

        // Creating and dropping a ticket with a poisoned mutex must succeed without panicking.
        let ticket = begin_request(Some("test-poison-recovery"));
        drop(ticket);

        let requests = ACTIVE_REQUESTS.get_or_init(|| Mutex::new(HashMap::new()));
        let map = match requests.lock() {
            Ok(guard) => guard,
            Err(poisoned) => poisoned.into_inner(),
        };
        assert!(!map.contains_key("test-poison-recovery"));
    }

    #[test]
    fn request_ticket_cancel_and_drop_is_idempotent() {
        let _lock = lock_test_serial();
        // 1. Cancel then drop
        let ticket_a = begin_request(Some("test-cancel-then-drop"));
        let id_a = CString::new("test-cancel-then-drop").unwrap();
        assert_eq!(unsafe { popglot_cancel_request(id_a.as_ptr()) }, 1);
        assert!(ticket_a.token.is_cancelled());
        // Dropping already-removed ticket is safe and idempotent
        drop(ticket_a);
        assert_eq!(unsafe { popglot_cancel_request(id_a.as_ptr()) }, 0);

        // 2. Drop then cancel
        let ticket_b = begin_request(Some("test-drop-then-cancel"));
        drop(ticket_b);
        let id_b = CString::new("test-drop-then-cancel").unwrap();
        assert_eq!(unsafe { popglot_cancel_request(id_b.as_ptr()) }, 0);
    }

    #[test]
    fn request_cancellation_is_precise_and_isolated() {
        let _lock = lock_test_serial();
        // Cancelling A leaves concurrent B running.
        let a = begin_request(Some("test-req-a"));
        let b = begin_request(Some("test-req-b"));

        let a_id = CString::new("test-req-a").unwrap();
        assert_eq!(unsafe { popglot_cancel_request(a_id.as_ptr()) }, 1);

        assert!(a.token.is_cancelled(), "A must be cancelled");
        assert!(
            !b.token.is_cancelled(),
            "B must be unaffected by cancelling A"
        );

        // Dropping A must not clean up B's registration either.
        drop(a);
        assert!(!b.token.is_cancelled());

        let b_id = CString::new("test-req-b").unwrap();
        assert_eq!(unsafe { popglot_cancel_request(b_id.as_ptr()) }, 1);
        assert!(b.token.is_cancelled());

        // The reverse order is equally isolated.
        let a = begin_request(Some("test-rev-a"));
        let b = begin_request(Some("test-rev-b"));

        let b_id = CString::new("test-rev-b").unwrap();
        assert_eq!(unsafe { popglot_cancel_request(b_id.as_ptr()) }, 1);
        assert!(b.token.is_cancelled());
        assert!(!a.token.is_cancelled(), "A must survive B's cancellation");

        // Unknown ids cancel nothing.
        let ghost = CString::new("test-req-ghost").unwrap();
        assert_eq!(unsafe { popglot_cancel_request(ghost.as_ptr()) }, 0);

        // Null pointer cancels nothing and does not panic.
        assert_eq!(unsafe { popglot_cancel_request(ptr::null()) }, 0);
        // Null pointer free does not panic.
        unsafe { popglot_free_string(ptr::null_mut()) };

        // The global cancel clears whatever remains.
        let ticket = begin_request(None);
        assert_eq!(popglot_cancel_active_request(), 1);
        assert!(ticket.token.is_cancelled());
        // Second global cancel finds nothing left to cancel.
        assert_eq!(popglot_cancel_active_request(), 0);
    }

    // ==================== Active preference injection ====================
    // The v3 translate export must keep the wire request byte-identical to the
    // legacy default route whenever the active template resolves to nothing
    // (faithful, or a custom template that was disabled), and must inject the
    // compiled active-template preference together with its template id and
    // revision anchors only when an enabled custom template is active. Every
    // request below terminates on a local loopback mock; nothing leaves the
    // machine.

    struct MockProvider {
        base_url: String,
        captured: thread::JoinHandle<Vec<u8>>,
    }

    impl MockProvider {
        // Binds a loopback server that answers exactly one Provider request and
        // returns the captured raw request bytes through `request_bytes`.
        fn start(reply_content: &str) -> Self {
            let listener = TcpListener::bind("127.0.0.1:0").expect("bind mock provider");
            let base_url = format!("http://{}", listener.local_addr().expect("mock address"));
            let body = format!(r#"{{"choices":[{{"message":{{"content":"{reply_content}"}}}}]}}"#);
            let captured = thread::spawn(move || {
                let (mut stream, _) = listener.accept().expect("accept provider request");
                stream
                    .set_read_timeout(Some(std::time::Duration::from_secs(30)))
                    .expect("set mock read timeout");
                let raw = read_full_http_request(&mut stream);
                let response = format!(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                    body.len()
                );
                stream
                    .write_all(response.as_bytes())
                    .expect("write mock response");
                raw
            });
            Self { base_url, captured }
        }

        fn request_bytes(self) -> Vec<u8> {
            self.captured.join().expect("mock provider worker")
        }
    }

    fn http_header_end(raw: &[u8]) -> Option<usize> {
        raw.windows(4).position(|window| window == b"\r\n\r\n")
    }

    fn read_full_http_request(stream: &mut std::net::TcpStream) -> Vec<u8> {
        let mut raw = Vec::new();
        let mut chunk = [0_u8; 4096];
        loop {
            if let Some(header_end) = http_header_end(&raw)
                && raw.len() >= header_end + 4 + http_content_length(&raw, header_end)
            {
                return raw;
            }
            let read = stream.read(&mut chunk).expect("read provider request");
            assert!(
                read > 0,
                "mock provider connection closed before the full request arrived"
            );
            raw.extend_from_slice(&chunk[..read]);
        }
    }

    fn http_content_length(raw: &[u8], header_end: usize) -> usize {
        let headers = String::from_utf8_lossy(&raw[..header_end]).to_ascii_lowercase();
        headers
            .lines()
            .find_map(|line| line.strip_prefix("content-length:"))
            .and_then(|value| value.trim().parse::<usize>().ok())
            .unwrap_or(0)
    }

    fn http_request_body(raw: &[u8]) -> String {
        let header_end = http_header_end(raw).expect("HTTP header terminator");
        String::from_utf8_lossy(&raw[header_end + 4..]).into_owned()
    }

    fn http_request_line(raw: &[u8]) -> String {
        let line_end = raw
            .iter()
            .position(|byte| *byte == b'\n')
            .expect("HTTP request line");
        String::from_utf8_lossy(&raw[..line_end])
            .trim_end()
            .to_owned()
    }

    fn cstring(value: &str) -> CString {
        CString::new(value).expect("CString without interior NUL")
    }

    // Converts one owned FFI envelope into a String and releases it.
    fn take_envelope(value: *mut c_char) -> String {
        assert!(!value.is_null(), "FFI returned a null envelope");
        // SAFETY: the FFI function returned one owned, nul-terminated envelope.
        let text = unsafe { CStr::from_ptr(value) }
            .to_str()
            .expect("UTF-8 envelope")
            .to_owned();
        // SAFETY: the pointer was produced by this library and not yet freed.
        unsafe { popglot_free_string(value) };
        text
    }

    // Initializes the process-global core exactly once per test binary, against
    // a private scratch config directory. Callers hold the serial test lock.
    fn initialize_core_for_tests() {
        if CORE.get().is_some() {
            return;
        }
        let suffix = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        let directory = std::env::temp_dir().join(format!("popglot-ffi-core-tests-{suffix}"));
        std::fs::create_dir_all(&directory).expect("create test config directory");
        let path = cstring(directory.to_str().expect("UTF-8 temp path"));
        // SAFETY: `path` is a valid nul-terminated UTF-8 pointer for this call.
        let envelope = unsafe { popglot_initialize(path.as_ptr()) };
        let text = take_envelope(envelope);
        assert!(text.contains("\"ok\":true"), "{text}");
    }

    fn save_core_settings(base_url: &str, source_language: &str, target_language: &str) {
        let settings = ProviderSettings {
            api_base_url: base_url.to_owned(),
            text_model: "mock-model".to_owned(),
            source_language: source_language.to_owned(),
            target_language: target_language.to_owned(),
            ..ProviderSettings::default()
        };
        let json = cstring(&serde_json::to_string(&settings).expect("settings JSON"));
        // SAFETY: `json` is a valid nul-terminated UTF-8 pointer for this call.
        let envelope = unsafe { popglot_save_settings(json.as_ptr()) };
        let text = take_envelope(envelope);
        assert!(text.contains("\"ok\":true"), "{text}");
    }

    fn save_custom_template(id: &str, instruction: &str, enabled: bool) {
        let template = PromptTemplate {
            id: id.to_owned(),
            schema_version: popglot_domain::PROMPT_SCHEMA_VERSION,
            name: format!("FFI 测试模板 {id}"),
            description: "FFI active preference 测试模板".to_owned(),
            instruction: instruction.to_owned(),
            domain: String::new(),
            audience: String::new(),
            enabled,
            is_built_in: false,
            revision: 0,
            created_at: 0,
            updated_at: 0,
            past_revisions: Vec::new(),
        };
        let json = cstring(&serde_json::to_string(&template).expect("template JSON"));
        // SAFETY: `json` is a valid nul-terminated UTF-8 pointer for this call.
        let envelope = unsafe { popglot_save_prompt_template(json.as_ptr()) };
        let text = take_envelope(envelope);
        assert!(text.contains("\"ok\":true"), "{text}");
    }

    fn activate_template(id: &str) {
        let id = cstring(id);
        // SAFETY: `id` is a valid nul-terminated UTF-8 pointer for this call.
        let envelope = unsafe { popglot_set_active_prompt_template(id.as_ptr()) };
        let text = take_envelope(envelope);
        assert!(text.contains("\"ok\":true"), "{text}");
    }

    fn reset_active_template_to_faithful() {
        // SAFETY: null is documented to reset the active template to faithful.
        let envelope = unsafe { popglot_set_active_prompt_template(ptr::null()) };
        let text = take_envelope(envelope);
        assert!(text.contains("\"ok\":true"), "{text}");
    }

    #[allow(clippy::too_many_arguments)]
    fn translate_text_v3(
        api_key: &CStr,
        source: &CStr,
        source_lang: Option<&CStr>,
        target_lang: Option<&CStr>,
        request_id: &CStr,
        preference: Option<&CStr>,
        template_id: Option<&CStr>,
        template_revision: u64,
    ) -> String {
        // SAFETY: every pointer is null or a valid nul-terminated UTF-8 string
        // that outlives the call.
        let envelope = unsafe {
            popglot_translate_text_v3(
                api_key.as_ptr(),
                source.as_ptr(),
                source_lang.map_or(ptr::null(), CStr::as_ptr),
                target_lang.map_or(ptr::null(), CStr::as_ptr),
                request_id.as_ptr(),
                preference.map_or(ptr::null(), CStr::as_ptr),
                template_id.map_or(ptr::null(), CStr::as_ptr),
                template_revision,
            )
        };
        take_envelope(envelope)
    }

    #[test]
    fn faithful_active_template_sends_legacy_request_bytes_without_preference() {
        let _lock = lock_test_serial();
        initialize_core_for_tests();
        reset_active_template_to_faithful();

        let active_mock = MockProvider::start("最终译文");
        save_core_settings(
            &active_mock.base_url,
            popglot_domain::AUTO_LANGUAGE,
            "zh-CN",
        );

        let api_key = cstring("test-key");
        let source = cstring("hello");
        let request_id = cstring("ffi-faithful-v3");
        let active = translate_text_v3(&api_key, &source, None, None, &request_id, None, None, 0);
        assert!(active.contains("\"ok\":true"), "{active}");

        // The legacy default route: a draft snapshot without any preference.
        let legacy_mock = MockProvider::start("最终译文");
        let legacy_settings = ProviderSettings {
            api_base_url: legacy_mock.base_url.clone(),
            text_model: "mock-model".to_owned(),
            ..ProviderSettings::default()
        };
        let settings_json = cstring(&serde_json::to_string(&legacy_settings).expect("settings"));
        let legacy_request_id = cstring("ffi-faithful-legacy");
        // SAFETY: every argument is a valid nul-terminated UTF-8 pointer.
        let legacy = take_envelope(unsafe {
            popglot_translate_text_draft_v1(
                settings_json.as_ptr(),
                api_key.as_ptr(),
                source.as_ptr(),
                ptr::null(),
                ptr::null(),
                legacy_request_id.as_ptr(),
            )
        });
        assert!(legacy.contains("\"ok\":true"), "{legacy}");

        let active_raw = active_mock.request_bytes();
        let legacy_raw = legacy_mock.request_bytes();
        assert_eq!(
            http_request_line(&active_raw),
            "POST /chat/completions HTTP/1.1"
        );
        let active_body = http_request_body(&active_raw);
        let legacy_body = http_request_body(&legacy_raw);
        // The system instructions ride inside messages[0] of the body, so byte
        // equality proves the faithful route is unchanged from the old default.
        assert_eq!(
            active_body, legacy_body,
            "faithful active template must not alter the legacy default request"
        );
        assert!(
            active_body.contains("\"model\":\"mock-model\""),
            "{active_body}"
        );
        assert!(
            active_body.contains("precise translation engine"),
            "{active_body}"
        );
        assert!(
            !active_body.contains("User style preference"),
            "faithful route must not inject a preference: {active_body}"
        );
    }

    #[test]
    fn blank_language_arguments_fall_back_to_saved_settings() {
        let _lock = lock_test_serial();
        initialize_core_for_tests();
        reset_active_template_to_faithful();

        let mock = MockProvider::start("最终译文");
        save_core_settings(&mock.base_url, "fr", "ja");

        let blank = cstring("");
        let api_key = cstring("test-key");
        let source = cstring("hello");
        let request_id = cstring("ffi-blank-language");
        let response = translate_text_v3(
            &api_key,
            &source,
            Some(&blank),
            Some(&blank),
            &request_id,
            None,
            None,
            0,
        );
        assert!(response.contains("\"ok\":true"), "{response}");

        let body = http_request_body(&mock.request_bytes());
        assert!(
            body.contains("Translate the content from French into Japanese."),
            "blank languages must fall back to the saved pair: {body}"
        );
        assert!(
            !body.contains("Detect the source language automatically"),
            "the stored source language must win over auto-detect: {body}"
        );

        // Same conclusion at the unit level for whitespace-only arguments.
        let stored = {
            let core = core_read().expect("core");
            core.settings().clone()
        };
        let languages = resolve_languages(&stored, Some("   "), Some(" "));
        assert_eq!(
            (languages.source.as_str(), languages.target.as_str()),
            ("fr", "ja")
        );
    }

    #[test]
    fn disabled_custom_template_never_injects_a_preference() {
        let _lock = lock_test_serial();
        initialize_core_for_tests();
        reset_active_template_to_faithful();

        save_custom_template(
            "ffi-disabled-style",
            "Always answer like a polite pirate in {{target_language}}.",
            true,
        );
        activate_template("ffi-disabled-style");
        {
            let core = core_read().expect("core");
            assert_eq!(
                core.prompt_store().active_template().id,
                "ffi-disabled-style"
            );
        }

        // Disable it. Content stays identical, so the stored id and revision
        // survive while runtime resolution must fall back to faithful.
        save_custom_template(
            "ffi-disabled-style",
            "Always answer like a polite pirate in {{target_language}}.",
            false,
        );
        let active_text = take_envelope(popglot_get_active_prompt_template());
        assert!(active_text.contains("\"id\":\"faithful\""), "{active_text}");
        let resolved = {
            let core = core_read().expect("core");
            resolve_active_preference(&core, None, None)
        };
        assert!(
            resolved.is_none(),
            "a disabled custom template must resolve to no preference"
        );

        let mock = MockProvider::start("最终译文");
        save_core_settings(&mock.base_url, popglot_domain::AUTO_LANGUAGE, "zh-CN");
        let api_key = cstring("test-key");
        let source = cstring("hello");
        let request_id = cstring("ffi-disabled-style");
        let response = translate_text_v3(&api_key, &source, None, None, &request_id, None, None, 0);
        assert!(response.contains("\"ok\":true"), "{response}");

        let body = http_request_body(&mock.request_bytes());
        assert!(
            !body.contains("User style preference"),
            "disabled template must not inject a preference: {body}"
        );
        assert!(!body.contains("pirate"), "{body}");
    }

    #[test]
    fn enabled_custom_template_injects_compiled_preference_with_template_anchors() {
        let _lock = lock_test_serial();
        initialize_core_for_tests();
        reset_active_template_to_faithful();

        let mock = MockProvider::start("最终译文");
        save_core_settings(&mock.base_url, popglot_domain::AUTO_LANGUAGE, "ja");

        save_custom_template(
            "ffi-pirate-style",
            "Always answer like a polite pirate in {{target_language}}.",
            true,
        );
        activate_template("ffi-pirate-style");

        // The FFI forwards exactly this CompiledPrompt (id, revision, compiled
        // text) as the preference anchors; the anchors themselves are request
        // metadata and intentionally never serialized onto the wire.
        let compiled = {
            let core = core_read().expect("core");
            resolve_active_preference(&core, None, None).expect("enabled template compiles")
        };
        assert_eq!(compiled.template_id, "ffi-pirate-style");
        assert_eq!(compiled.revision, 1);
        // Templates receive the stored tag verbatim ("ja"); the English name is
        // only used by the system-instruction language summary.
        assert!(
            compiled.compiled_text.ends_with("in ja."),
            "compiled text must use the settings fallback language: {}",
            compiled.compiled_text
        );

        let api_key = cstring("test-key");
        let source = cstring("hello");
        let request_id = cstring("ffi-pirate-style");
        let response = translate_text_v3(&api_key, &source, None, None, &request_id, None, None, 0);
        assert!(response.contains("\"ok\":true"), "{response}");

        let body = http_request_body(&mock.request_bytes());
        assert!(
            body.contains("User style preference"),
            "enabled custom template must inject a preference: {body}"
        );
        assert!(body.contains("polite pirate in ja."), "{body}");
    }
}
