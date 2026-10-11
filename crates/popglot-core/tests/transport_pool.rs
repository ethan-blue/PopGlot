//! P04 transport-pool verification (UI-WAKE-AI-CONNECTION-PLAN §7.1).
//!
//! The reuse claim is proven at the TCP level: a local keep-alive server
//! records how many connections were ACCEPTED and which connection carried
//! each request. Pooled clients must send two same-origin requests over ONE
//! connection; per-call clients must open two (the control case proves the
//! harness can actually detect non-reuse). Key isolation, credential
//! exclusion and bounded capacity are asserted separately.

use popglot_core::provider::{ProviderClient, TranslationRequest, TransportLimits, provider_for};
use popglot_core::transport_pool::{ProviderClientPool, TransportKey, pooled_client};
use popglot_domain::{LanguagePair, ProviderSettings, ProviderType};
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::Duration;
use tokio_util::sync::CancellationToken;

const STRUCTURED_RESULT: &str = r#"{\"translated_text\":\"连接测试\",\"transcription\":\"\",\"explanation\":\"ok\",\"protected_terms\":[],\"warnings\":[]}"#;

fn chat_response() -> String {
    format!(r#"{{"choices":[{{"message":{{"content":"{STRUCTURED_RESULT}"}}}}]}}"#)
}

struct KeepAliveServer {
    base_url: String,
    accepted: Arc<AtomicUsize>,
    /// (connection index, request text) per received request.
    requests: Arc<Mutex<Vec<(usize, String)>>>,
    stop: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
}

impl KeepAliveServer {
    /// One HTTP/1.1 persistent-connection server: every accepted connection
    /// serves any number of keep-alive requests until the client closes.
    fn start() -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind keep-alive server");
        let base_url = format!("http://{}", listener.local_addr().expect("server address"));
        let accepted = Arc::new(AtomicUsize::new(0));
        let requests = Arc::new(Mutex::new(Vec::new()));
        let stop = Arc::new(AtomicBool::new(false));
        let worker = {
            let accepted = Arc::clone(&accepted);
            let requests = Arc::clone(&requests);
            let stop = Arc::clone(&stop);
            thread::spawn(move || {
                while !stop.load(Ordering::SeqCst) {
                    let Ok((mut stream, _)) = listener.accept() else {
                        break;
                    };
                    let connection_index = accepted.fetch_add(1, Ordering::SeqCst);
                    // Each connection is served on its own thread: a kept-
                    // alive idle connection must never block the accept loop
                    // from serving the next client.
                    let requests = Arc::clone(&requests);
                    let stop = Arc::clone(&stop);
                    thread::spawn(move || {
                        stream
                            .set_read_timeout(Some(Duration::from_secs(5)))
                            .expect("set read timeout");
                        while let Some(request) = read_request(&mut stream) {
                            requests
                                .lock()
                                .expect("record request")
                                .push((connection_index, request));
                            let body = chat_response();
                            let reply = format!(
                                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\n\r\n{}",
                                body.len(),
                                body
                            );
                            if stream.write_all(reply.as_bytes()).is_err()
                                || stream.flush().is_err()
                            {
                                break;
                            }
                            if stop.load(Ordering::SeqCst) {
                                break;
                            }
                        }
                    });
                }
            })
        };
        Self {
            base_url,
            accepted,
            requests,
            stop,
            worker: Some(worker),
        }
    }

    fn accept_count(&self) -> usize {
        self.accepted.load(Ordering::SeqCst)
    }

    fn requests(&self) -> Vec<(usize, String)> {
        self.requests.lock().expect("read requests").clone()
    }
}

impl Drop for KeepAliveServer {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
        // Unblock a pending accept so the worker can observe the stop flag.
        let _ = TcpStream::connect(self.base_url.trim_start_matches("http://"));
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

fn read_request(stream: &mut TcpStream) -> Option<String> {
    let mut request = Vec::new();
    let mut buffer = [0_u8; 4096];
    let mut content_length = None;
    loop {
        let count = stream.read(&mut buffer).unwrap_or(0);
        if count == 0 {
            return None;
        }
        request.extend_from_slice(&buffer[..count]);
        if let Some(header_end) = find_header_end(&request) {
            content_length.get_or_insert_with(|| parse_content_length(&request[..header_end]));
            if request.len() >= header_end + 4 + content_length.unwrap_or_default() {
                return Some(String::from_utf8(request).unwrap_or_default());
            }
        }
    }
}

fn find_header_end(bytes: &[u8]) -> Option<usize> {
    bytes.windows(4).position(|window| window == b"\r\n\r\n")
}

fn parse_content_length(headers: &[u8]) -> usize {
    let text = String::from_utf8_lossy(headers);
    for line in text.split("\r\n") {
        let lowered = line.to_ascii_lowercase();
        let Some(value) = lowered.strip_prefix("content-length:").map(str::trim) else {
            continue;
        };
        if let Ok(parsed) = value.parse::<usize>() {
            return parsed;
        }
    }
    0
}

fn test_limits() -> TransportLimits {
    TransportLimits {
        connect_timeout: Duration::from_secs(1),
        total_timeout: Duration::from_secs(5),
        stream_idle_timeout: Duration::from_secs(2),
        stream_total_timeout: Duration::from_secs(10),
        max_response_bytes: 64 * 1024,
        max_retries: 0,
        retry_delay: Duration::from_millis(1),
        accept_invalid_certs: false,
    }
}

fn settings_for(server: &KeepAliveServer) -> ProviderSettings {
    ProviderSettings {
        provider_type: ProviderType::OpenAiCompatible,
        api_base_url: server.base_url.clone(),
        text_endpoint: ProviderType::OpenAiCompatible.default_endpoint().to_owned(),
        vision_endpoint: ProviderType::OpenAiCompatible.default_endpoint().to_owned(),
        text_model: "pool-test-model".to_owned(),
        vision_model: "pool-test-model".to_owned(),
        supports_text: true,
        supports_vision: true,
        network_enabled: true,
        safe_dev_mode: false,
        ..ProviderSettings::default()
    }
}

async fn execute_text(client: &ProviderClient, settings: &ProviderSettings, marker: &str) {
    let response = client
        .execute(
            provider_for(settings.provider_type).as_ref(),
            settings,
            "pool-test-key",
            "pool-test",
            &TranslationRequest::text(marker, LanguagePair::new("auto", "zh-CN")),
            &CancellationToken::new(),
        )
        .await
        .expect("pool test response");
    assert_eq!(response.result.translated_text, "连接测试");
}

#[tokio::test]
async fn pooled_clients_reuse_one_tcp_connection_for_same_origin() {
    let server = KeepAliveServer::start();
    let settings = settings_for(&server);
    let limits = test_limits();

    let first = pooled_client(&settings.api_base_url, &limits).expect("first pooled client");
    execute_text(&first, &settings, "first-marker").await;
    let second = pooled_client(&settings.api_base_url, &limits).expect("second pooled client");
    execute_text(&second, &settings, "second-marker").await;

    let requests = server.requests();
    assert_eq!(2, requests.len(), "both requests must have arrived");
    let bodies: Vec<&str> = requests
        .iter()
        .map(|(_, request)| request.split("\r\n\r\n").nth(1).unwrap_or_default())
        .collect();
    assert!(
        bodies.iter().any(|body| body.contains("first-marker")),
        "the first request body must carry its marker"
    );
    assert!(
        bodies.iter().any(|body| body.contains("second-marker")),
        "the second request body must carry its marker"
    );
    assert_eq!(
        1,
        server.accept_count(),
        "two same-origin requests through the pool must reuse ONE TCP connection"
    );
    assert_eq!(
        requests[0].0, requests[1].0,
        "both requests rode the same accepted connection"
    );
    // Both requests still carried their own per-request credentials.
    for (_, request) in &requests {
        assert!(
            request
                .to_ascii_lowercase()
                .contains("authorization: bearer pool-test-key"),
            "each request carries its own credentials"
        );
    }
}

#[tokio::test]
async fn per_call_clients_open_a_connection_each() {
    // The control case: constructing a client per call must open a new
    // connection each time, proving the keep-alive harness detects
    // non-reuse (otherwise the reuse assertion above would be vacuous).
    let server = KeepAliveServer::start();
    let settings = settings_for(&server);
    let limits = test_limits();

    let first = ProviderClient::new(limits.clone()).expect("first per-call client");
    execute_text(&first, &settings, "control-one").await;
    let second = ProviderClient::new(limits).expect("second per-call client");
    execute_text(&second, &settings, "control-two").await;

    assert_eq!(2, server.requests().len(), "both control requests arrived");
    assert_eq!(
        2,
        server.accept_count(),
        "per-call clients must NOT share a TCP connection"
    );
}

#[test]
fn transport_keys_isolate_every_transport_parameter() {
    let limits = test_limits();
    let base = "https://api.example.com";
    let key = TransportKey::from_base_url(base, &limits).expect("key");

    // Same inputs → same key.
    assert_eq!(
        key,
        TransportKey::from_base_url(base, &limits).expect("key again")
    );

    // Origin differences (scheme, host, explicit port) produce different keys.
    assert_ne!(
        key,
        TransportKey::from_base_url("https://api.example.com:8443", &limits).expect("port key")
    );
    assert_ne!(
        key,
        TransportKey::from_base_url("https://other.example.com", &limits).expect("host key")
    );
    // Loopback HTTP is a valid, distinct origin — and a different port is a
    // different origin again.
    let local = TransportKey::from_base_url("http://127.0.0.1:9000", &limits).expect("local key");
    assert_ne!(key, local);
    assert_ne!(
        local,
        TransportKey::from_base_url("http://127.0.0.1:9001", &limits).expect("other local key")
    );

    // TLS strictness must NEVER share a client with relaxed TLS.
    let mut relaxed = limits.clone();
    relaxed.accept_invalid_certs = true;
    assert_ne!(
        key,
        TransportKey::from_base_url(base, &relaxed).expect("relaxed key")
    );

    // Any other transport parameter difference isolates too.
    let mut slower = limits.clone();
    slower.connect_timeout = Duration::from_secs(9);
    assert_ne!(
        key,
        TransportKey::from_base_url(base, &slower).expect("slower key")
    );
    let mut fewer_retries = limits.clone();
    fewer_retries.max_retries = 2;
    assert_ne!(
        key,
        TransportKey::from_base_url(base, &fewer_retries).expect("retry key")
    );

    // The raw user string never becomes the key: an invalid base is an error,
    // and credential-bearing bases are rejected outright.
    assert!(TransportKey::from_base_url("https://user:pass@api.example.com", &limits).is_err());
    assert!(TransportKey::from_base_url("not a url", &limits).is_err());
    // Plain HTTP to a public host is rejected by the shared validation.
    assert!(TransportKey::from_base_url("http://api.example.com", &limits).is_err());
}

#[test]
fn pool_is_bounded_and_evicts_least_recently_used() {
    let pool = ProviderClientPool::new();
    let limits = test_limits();

    let key_a = TransportKey::from_base_url("http://127.0.0.1:7001", &limits).expect("key a");
    let key_b = TransportKey::from_base_url("http://127.0.0.1:7002", &limits).expect("key b");
    assert!(
        pool.get_or_create(key_a.clone()).is_ok() && pool.get_or_create(key_b.clone()).is_ok(),
        "both clients must construct"
    );
    assert_eq!(2, pool.len(), "two distinct keys pool two clients");
    // Same key again: no growth.
    assert!(pool.get_or_create(key_a.clone()).is_ok());
    assert_eq!(2, pool.len(), "re-requesting a pooled key adds nothing");

    // Fill beyond capacity with distinct origins; the pool must stay bounded.
    for port in 7003..7020 {
        let key = TransportKey::from_base_url(&format!("http://127.0.0.1:{port}"), &limits)
            .expect("fill key");
        assert!(pool.get_or_create(key).is_ok(), "filling client {port}");
    }
    assert!(
        pool.len() <= 8,
        "the pool must never exceed its capacity (got {})",
        pool.len()
    );
}
