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
use popglot_domain::{LanguagePair, ProviderSettings};
use serde::Serialize;
use std::collections::HashMap;
use std::ffi::{CStr, CString, c_char};
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
        let ticket = begin_request(None)?;
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
        finish_request(&ticket.id);
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
        let ticket = begin_request(custom_id)?;
        let response = runtime
            .block_on(AppCore::test_connection_draft(
                &draft_settings,
                api_key,
                Some(&ticket.id),
                &ticket.token,
            ))
            .map_err(|error| error.to_string());
        finish_request(&ticket.id);
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
    ffi_guard(|| {
        let api_key = unsafe { read_utf8(api_key) }?;
        let source = unsafe { read_utf8(source) }?;
        let source_lang = unsafe { read_optional_utf8(source_lang) }?;
        let target_lang = unsafe { read_optional_utf8(target_lang) }?;
        let custom_id = unsafe { read_optional_utf8(request_id) }?;

        let (settings, client) = {
            let core = core_read()?;
            (core.settings().clone(), core.provider_client().clone())
        };
        let languages = resolve_languages(&settings, source_lang, target_lang);
        let runtime = provider_runtime()?;
        let ticket = begin_request(custom_id)?;
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
        finish_request(&ticket.id);
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
        let ticket = begin_request(custom_id)?;
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
        finish_request(&ticket.id);
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
        let ticket = begin_request(custom_id)?;
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
        finish_request(&ticket.id);
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
        let ticket = begin_request(custom_id)?;
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
        finish_request(&ticket.id);
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
        if let Ok(mut map) = requests.lock()
            && let Some(token) = map.remove(id)
        {
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
        if let Ok(mut map) = requests.lock() {
            let mut cancelled_any = false;
            for (_, token) in map.drain() {
                token.cancel();
                cancelled_any = true;
            }
            return i32::from(cancelled_any);
        }
        0
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

fn begin_request(custom_id: Option<&str>) -> Result<RequestTicket, String> {
    let id = custom_id.map_or_else(
        || format!("req-{}", REQUEST_TICKET.fetch_add(1, Ordering::Relaxed)),
        ToOwned::to_owned,
    );
    let token = CancellationToken::new();
    let requests = ACTIVE_REQUESTS.get_or_init(|| Mutex::new(HashMap::new()));
    requests
        .lock()
        .map_err(|_| "Provider cancellation map is poisoned".to_owned())?
        .insert(id.clone(), token.clone());
    Ok(RequestTicket { id, token })
}

fn finish_request(id: &str) {
    let requests = ACTIVE_REQUESTS.get_or_init(|| Mutex::new(HashMap::new()));
    if let Ok(mut map) = requests.lock() {
        map.remove(id);
    }
}

fn ffi_guard(operation: impl FnOnce() -> Result<*mut c_char, String>) -> *mut c_char {
    match catch_unwind(AssertUnwindSafe(operation)) {
        Ok(Ok(value)) => value,
        Ok(Err(error)) => failure(error),
        Err(_) => failure("PopGlot Core encountered an unexpected panic"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// One serial test: the cancellation registry is process-global, so the
    /// global-cancel scenario must run after the per-id scenarios instead of
    /// in a parallel test that would cancel their tickets.
    #[test]
    fn request_cancellation_is_precise_and_isolated() {
        // Cancelling A leaves concurrent B running.
        let a = begin_request(Some("test-req-a")).expect("begin A");
        let b = begin_request(Some("test-req-b")).expect("begin B");

        let a_id = CString::new("test-req-a").unwrap();
        assert_eq!(unsafe { popglot_cancel_request(a_id.as_ptr()) }, 1);

        assert!(a.token.is_cancelled(), "A must be cancelled");
        assert!(
            !b.token.is_cancelled(),
            "B must be unaffected by cancelling A"
        );

        // Finishing A must not clean up B's registration either.
        finish_request(&a.id);
        assert!(!b.token.is_cancelled());

        let b_id = CString::new("test-req-b").unwrap();
        assert_eq!(unsafe { popglot_cancel_request(b_id.as_ptr()) }, 1);
        assert!(b.token.is_cancelled());

        // The reverse order is equally isolated.
        let a = begin_request(Some("test-rev-a")).expect("begin A");
        let b = begin_request(Some("test-rev-b")).expect("begin B");

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
        let ticket = begin_request(None).expect("begin anonymous request");
        assert_eq!(popglot_cancel_active_request(), 1);
        assert!(ticket.token.is_cancelled());
        // Second global cancel finds nothing left to cancel.
        assert_eq!(popglot_cancel_active_request(), 0);
    }
}
