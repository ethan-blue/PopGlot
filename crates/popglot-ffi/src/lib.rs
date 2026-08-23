//! Stable C ABI for replaceable desktop shells.

// Raw pointers and exported symbols are inherent to this narrow FFI boundary.
// Unsafe code remains denied by convention in every other workspace crate.
#![allow(unsafe_code)]
// MSVC link.exe reports the generated import library on stdout; Rust 1.98
// surfaces that informational localized line as `linker_messages`.
#![allow(linker_messages)]

use base64::Engine as _;
use popglot_core::AppCore;
use popglot_domain::ProviderSettings;
use serde::Serialize;
use std::ffi::{CStr, CString, c_char};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::ptr;
use std::sync::{Mutex, OnceLock};
use tokio::runtime::Runtime;
use tokio_util::sync::CancellationToken;

static CORE: OnceLock<Mutex<AppCore>> = OnceLock::new();
static RUNTIME: OnceLock<Runtime> = OnceLock::new();
static ACTIVE_REQUEST: OnceLock<Mutex<Option<CancellationToken>>> = OnceLock::new();

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

unsafe fn read_utf8<'a>(value: *const c_char) -> Result<&'a str, String> {
    if value.is_null() {
        return Err("received a null string pointer".to_owned());
    }
    // SAFETY: The caller contract requires a valid, NUL-terminated UTF-8 string.
    unsafe { CStr::from_ptr(value) }
        .to_str()
        .map_err(|error| format!("invalid UTF-8: {error}"))
}

/// Initializes the process-local core using a host-provided configuration directory.
///
/// # Safety
///
/// `config_directory` must point to a valid NUL-terminated UTF-8 string for the
/// duration of the call. The returned pointer must be released with
/// [`popglot_free_string`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_initialize(config_directory: *const c_char) -> *mut c_char {
    ffi_guard(|| {
        // SAFETY: Forwarding the ABI caller contract to the validated helper.
        let path = unsafe { read_utf8(config_directory) }?;
        let core = AppCore::open(path).map_err(|error| error.to_string())?;
        CORE.set(Mutex::new(core))
            .map_err(|_| "PopGlot Core has already been initialized".to_owned())?;
        Ok(success(env!("CARGO_PKG_VERSION")))
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn popglot_get_settings() -> *mut c_char {
    ffi_guard(|| {
        let core = core_lock()?;
        Ok(success(core.settings()))
    })
}

/// Persists a UTF-8 JSON provider settings object.
///
/// # Safety
///
/// `json` must point to a valid NUL-terminated UTF-8 string for the duration of
/// the call. The returned pointer must be released with [`popglot_free_string`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_save_settings(json: *const c_char) -> *mut c_char {
    ffi_guard(|| {
        // SAFETY: Forwarding the ABI caller contract to the validated helper.
        let json = unsafe { read_utf8(json) }?;
        let settings: ProviderSettings =
            serde_json::from_str(json).map_err(|error| error.to_string())?;
        let mut core = core_lock()?;
        core.save_settings(settings)
            .map_err(|error| error.to_string())?;
        Ok(success("saved"))
    })
}

/// Sends a user-initiated, text-only Provider connection test.
///
/// # Safety
///
/// `api_key` must point to a valid NUL-terminated UTF-8 string for the duration
/// of the call. The returned pointer must be released with [`popglot_free_string`].
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_test_connection(api_key: *const c_char) -> *mut c_char {
    ffi_guard(|| {
        // SAFETY: Forwarding the ABI caller contract to the validated helper.
        let api_key = unsafe { read_utf8(api_key) }?;
        let core = core_lock()?;
        let runtime = provider_runtime()?;
        let cancellation = CancellationToken::new();
        let response = runtime
            .block_on(core.test_connection(api_key, &cancellation))
            .map_err(|error| error.to_string())?;
        Ok(success(response))
    })
}

/// Translates selected UTF-8 text through the active Provider.
///
/// # Safety
///
/// Both pointers must remain valid NUL-terminated UTF-8 strings for the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_translate_text(
    api_key: *const c_char,
    source: *const c_char,
) -> *mut c_char {
    ffi_guard(|| {
        // SAFETY: Forwarding the ABI caller contract to the validated helper.
        let api_key = unsafe { read_utf8(api_key) }?;
        // SAFETY: Forwarding the ABI caller contract to the validated helper.
        let source = unsafe { read_utf8(source) }?;
        let core = core_lock()?;
        let runtime = provider_runtime()?;
        let cancellation = begin_active_request()?;
        let response = runtime
            .block_on(core.translate_text(api_key, source, &cancellation))
            .map_err(|error| error.to_string());
        finish_active_request()?;
        Ok(response.map_or_else(failure, success))
    })
}

/// Translates one base64-encoded screenshot through the active vision Provider.
///
/// # Safety
///
/// All pointers must remain valid NUL-terminated UTF-8 strings for the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_translate_vision(
    api_key: *const c_char,
    media_type: *const c_char,
    image_base64: *const c_char,
) -> *mut c_char {
    ffi_guard(|| {
        // SAFETY: Forwarding the ABI caller contract to the validated helper.
        let api_key = unsafe { read_utf8(api_key) }?;
        // SAFETY: Forwarding the ABI caller contract to the validated helper.
        let media_type = unsafe { read_utf8(media_type) }?;
        // SAFETY: Forwarding the ABI caller contract to the validated helper.
        let image_base64 = unsafe { read_utf8(image_base64) }?;
        if image_base64.len() > 12 * 1024 * 1024 {
            return Err("编码后的截图超过 12 MiB FFI 上限。".to_owned());
        }
        let image = base64::engine::general_purpose::STANDARD
            .decode(image_base64)
            .map_err(|_| "截图不是有效的 base64。".to_owned())?;
        let core = core_lock()?;
        let runtime = provider_runtime()?;
        let cancellation = begin_active_request()?;
        let response = runtime
            .block_on(core.translate_vision(api_key, media_type, image, &cancellation))
            .map_err(|error| error.to_string());
        finish_active_request()?;
        Ok(response.map_or_else(failure, success))
    })
}

/// Cancels the process-wide active translation, if one exists.
#[unsafe(no_mangle)]
pub extern "C" fn popglot_cancel_active_request() -> i32 {
    i32::from(
        ACTIVE_REQUEST
            .get()
            .and_then(|active| active.lock().ok())
            .and_then(|active| active.as_ref().cloned())
            .is_some_and(|cancellation| {
                cancellation.cancel();
                true
            }),
    )
}

/// Releases strings returned by this library.
///
/// # Safety
///
/// `value` must be null or a pointer returned by this library that has not
/// already been released.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn popglot_free_string(value: *mut c_char) {
    if !value.is_null() {
        // SAFETY: The pointer must have been returned by CString::into_raw in this library.
        drop(unsafe { CString::from_raw(value) });
    }
}

fn core_lock() -> Result<std::sync::MutexGuard<'static, AppCore>, String> {
    CORE.get()
        .ok_or_else(|| "PopGlot Core is not initialized".to_owned())?
        .lock()
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

fn begin_active_request() -> Result<CancellationToken, String> {
    let cancellation = CancellationToken::new();
    let active = ACTIVE_REQUEST.get_or_init(|| Mutex::new(None));
    *active
        .lock()
        .map_err(|_| "Provider cancellation state is poisoned".to_owned())? =
        Some(cancellation.clone());
    Ok(cancellation)
}

fn finish_active_request() -> Result<(), String> {
    let active = ACTIVE_REQUEST.get_or_init(|| Mutex::new(None));
    *active
        .lock()
        .map_err(|_| "Provider cancellation state is poisoned".to_owned())? = None;
    Ok(())
}

fn ffi_guard(operation: impl FnOnce() -> Result<*mut c_char, String>) -> *mut c_char {
    match catch_unwind(AssertUnwindSafe(operation)) {
        Ok(Ok(value)) => value,
        Ok(Err(error)) => failure(error),
        Err(_) => failure("PopGlot Core encountered an unexpected panic"),
    }
}
