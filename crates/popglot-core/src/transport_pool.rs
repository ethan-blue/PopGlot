//! P04: bounded reuse of HTTP transport clients for the draft execution
//! paths (profile text/vision streaming, draft snapshots, connection tests).
//!
//! The FFI draft entries used to construct a fresh [`ProviderClient`] per
//! call, paying TLS handshake + TCP setup on every translation. This pool
//! keys clients by EVERY parameter that affects transport behaviour
//! (normalized origin, timeouts, size caps, retry policy, TLS strictness and
//! a transport revision) and hands out cheap clones — a `reqwest::Client` is
//! an `Arc` handle over one connection pool.
//!
//! Security invariants (UI-WAKE-AI-CONNECTION-PLAN §7.1):
//! - The pool NEVER stores API keys, `Authorization` or `ExtraHeaders`;
//!   credentials are attached per request by the existing execute paths.
//! - A key change in ANY transport parameter (including
//!   `accept_invalid_certs`) produces a different key: strict TLS can never
//!   ride a client built for relaxed TLS.
//! - The lock guards map lookup/insert/eviction only; client construction
//!   happens outside the lock and the lock is never held across an await or
//!   an HTTP send.

use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

use crate::provider::{ProviderClient, ProviderError, TransportLimits, transport_origin};

/// Maximum simultaneously pooled clients. Distinct origins × transport
/// configurations beyond this evict the least recently used entry.
const MAX_ENTRIES: usize = 8;

/// A pooled entry that has not been used for this long is swept on the next
/// pool operation.
const IDLE_EVICTION: Duration = Duration::from_secs(600);

/// Bumped whenever the client-construction behaviour itself changes (e.g. a
/// new default policy), so old pool entries can never satisfy a new runtime.
const TRANSPORT_REVISION: u64 = 1;

/// The complete identity of a pooled client: the normalized transport origin
/// plus every [`TransportLimits`] field that shapes the underlying
/// `reqwest::Client`, plus the construction revision.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct TransportKey {
    origin: String,
    connect_timeout: Duration,
    total_timeout: Duration,
    stream_idle_timeout: Duration,
    stream_total_timeout: Duration,
    max_response_bytes: usize,
    max_retries: u8,
    retry_delay: Duration,
    accept_invalid_certs: bool,
    transport_revision: u64,
}

impl TransportKey {
    /// Derives the key from a Base URL and the transport limits. The origin
    /// goes through the SAME validation as request URL construction — an
    /// invalid base is a configuration error, never a silently mismatched
    /// cache entry.
    ///
    /// # Errors
    ///
    /// Returns a configuration error when the Base URL fails validation.
    pub fn from_base_url(base: &str, limits: &TransportLimits) -> Result<Self, ProviderError> {
        Ok(Self {
            origin: transport_origin(base)?,
            connect_timeout: limits.connect_timeout,
            total_timeout: limits.total_timeout,
            stream_idle_timeout: limits.stream_idle_timeout,
            stream_total_timeout: limits.stream_total_timeout,
            max_response_bytes: limits.max_response_bytes,
            max_retries: limits.max_retries,
            retry_delay: limits.retry_delay,
            accept_invalid_certs: limits.accept_invalid_certs,
            transport_revision: TRANSPORT_REVISION,
        })
    }

    fn limits(&self) -> TransportLimits {
        TransportLimits {
            connect_timeout: self.connect_timeout,
            total_timeout: self.total_timeout,
            stream_idle_timeout: self.stream_idle_timeout,
            stream_total_timeout: self.stream_total_timeout,
            max_response_bytes: self.max_response_bytes,
            max_retries: self.max_retries,
            retry_delay: self.retry_delay,
            accept_invalid_certs: self.accept_invalid_certs,
        }
    }
}

struct PooledClient {
    client: ProviderClient,
    last_used: Instant,
}

/// A bounded pool of transport clients. Clone-out semantics: callers receive
/// their own `ProviderClient` handle sharing the pooled `reqwest::Client`.
pub struct ProviderClientPool {
    entries: Mutex<HashMap<TransportKey, PooledClient>>,
}

impl ProviderClientPool {
    #[must_use]
    pub fn new() -> Self {
        Self {
            entries: Mutex::new(HashMap::new()),
        }
    }

    /// The process-wide pool used by the FFI draft execution paths.
    #[must_use]
    pub fn global() -> &'static Self {
        static POOL: OnceLock<ProviderClientPool> = OnceLock::new();
        POOL.get_or_init(ProviderClientPool::new)
    }

    /// Returns a client for the key, creating (outside the lock) and pooling
    /// one when absent. Pooled clients are reused; a full pool evicts the
    /// least recently used entry; idle entries are swept opportunistically.
    ///
    /// # Errors
    ///
    /// Returns a configuration error when the client cannot be constructed.
    pub fn get_or_create(&self, key: TransportKey) -> Result<ProviderClient, ProviderError> {
        let now = Instant::now();
        {
            let mut entries = self.lock();
            if let Some(entry) = entries.get_mut(&key) {
                entry.last_used = now;
                return Ok(entry.client.clone());
            }
        }

        // Construction happens OUTSIDE the lock: a TLS setup stall must not
        // block lookups for unrelated origins.
        let client = ProviderClient::new(key.limits())?;

        let mut entries = self.lock();
        // Another thread may have inserted the same key while we built.
        if let Some(entry) = entries.get_mut(&key) {
            entry.last_used = now;
            return Ok(entry.client.clone());
        }
        entries.retain(|_, entry| now.duration_since(entry.last_used) < IDLE_EVICTION);
        while entries.len() >= MAX_ENTRIES {
            let evict = entries
                .iter()
                .min_by_key(|(_, entry)| entry.last_used)
                .map(|(key, _)| key.clone());
            match evict {
                Some(oldest) => {
                    entries.remove(&oldest);
                }
                None => break,
            }
        }
        entries.insert(
            key,
            PooledClient {
                client: client.clone(),
                last_used: now,
            },
        );
        Ok(client)
    }

    /// Number of currently pooled entries (diagnostics/tests only).
    pub fn len(&self) -> usize {
        self.lock().len()
    }

    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, HashMap<TransportKey, PooledClient>> {
        // A poisoned lock means another thread panicked mid-map-op; the map
        // itself is still structurally valid, so recovering is safer than
        // poisoning every future translation.
        self.entries
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
    }
}

impl Default for ProviderClientPool {
    fn default() -> Self {
        Self::new()
    }
}

/// Convenience wrapper mirroring the old `ProviderClient::new(limits)` calls:
/// derives the pool key from the settings' Base URL and limits, then returns
/// a pooled client. Credentials are NOT part of the key or the pooled client.
///
/// # Errors
///
/// Returns a configuration error when the Base URL is invalid or the client
/// cannot be constructed.
pub fn pooled_client(
    base_url: &str,
    limits: &TransportLimits,
) -> Result<ProviderClient, ProviderError> {
    let key = TransportKey::from_base_url(base_url, limits)?;
    ProviderClientPool::global().get_or_create(key)
}
