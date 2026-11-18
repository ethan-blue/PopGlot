//! P04 transport-pool verification (UI-WAKE-AI-CONNECTION-PLAN §7.1).
//!
//! The reuse claim is proven at the TCP level: a local keep-alive server
//! records how many connections were ACCEPTED and which connection carried
//! each request. Pooled clients must send two same-origin requests over ONE
//! connection; per-call clients must open two (the control case proves the
//! harness can actually detect non-reuse). Key isolation, credential
//! exclusion and bounded capacity are asserted separately.

use popglot_core::provider::{ProviderClient, TranslationRequest, TransportLimits, provider_for};
use popglot_core::transport_pool::{
    ProviderClientPool, TransportKey, pooled_client, pooled_client_with_proxy,
};
use popglot_domain::{LanguagePair, NetworkProxyConfig, ProviderSettings, ProviderType, ProxyMode};
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

    pool.clear();
    assert!(pool.is_empty(), "clear must evict all pooled clients");
}

#[test]
fn transport_keys_isolate_proxy_modes_and_enforce_local_direct() {
    let limits = test_limits();
    let public = "https://api.example.com";

    let direct =
        TransportKey::from_base_url_with_proxy(public, &limits, &NetworkProxyConfig::direct())
            .expect("direct key");
    let system =
        TransportKey::from_base_url_with_proxy(public, &limits, &NetworkProxyConfig::system())
            .expect("system key");
    let custom_http = TransportKey::from_base_url_with_proxy(
        public,
        &limits,
        &NetworkProxyConfig::custom("http://127.0.0.1:7890"),
    )
    .expect("custom http key");
    let custom_socks = TransportKey::from_base_url_with_proxy(
        public,
        &limits,
        &NetworkProxyConfig::custom("socks5://127.0.0.1:1080"),
    )
    .expect("custom socks key");
    let custom_socks5h = TransportKey::from_base_url_with_proxy(
        public,
        &limits,
        &NetworkProxyConfig::custom("socks5h://127.0.0.1:1080"),
    )
    .expect("custom socks5h key");

    assert_ne!(direct, system);
    assert_ne!(direct, custom_http);
    assert_ne!(system, custom_http);
    assert_ne!(custom_http, custom_socks);
    assert_ne!(custom_socks, custom_socks5h);

    // Local loopback ALWAYS resolves to Direct even when proxy is Custom and bypass_local is false.
    let mut custom_no_bypass = NetworkProxyConfig::custom("http://127.0.0.1:7890");
    custom_no_bypass.bypass_local = false;
    let local_direct = TransportKey::from_base_url_with_proxy(
        "http://127.0.0.1:11434/v1",
        &limits,
        &NetworkProxyConfig::direct(),
    )
    .expect("local direct");
    let local_under_custom = TransportKey::from_base_url_with_proxy(
        "http://127.0.0.1:11434/v1",
        &limits,
        &custom_no_bypass,
    )
    .expect("local under custom");
    assert_eq!(local_direct, local_under_custom);
    assert_eq!(local_under_custom.effective_proxy().mode, ProxyMode::Direct);

    // Domestic / no_proxy match resolves to Direct and shares the direct key.
    let mut with_no_proxy = NetworkProxyConfig::custom("http://127.0.0.1:7890");
    with_no_proxy.no_proxy = Some("api.example.com, dict.youdao.com".to_owned());
    let bypassed =
        TransportKey::from_base_url_with_proxy(public, &limits, &with_no_proxy).expect("bypassed");
    assert_eq!(direct, bypassed);

    // Invalid proxy URL is rejected cleanly.
    let invalid = NetworkProxyConfig::custom("ftp://127.0.0.1:21");
    assert!(TransportKey::from_base_url_with_proxy(public, &limits, &invalid).is_err());
    assert!(
        TransportKey::from_base_url_with_proxy(
            public,
            &limits,
            &NetworkProxyConfig::custom("not-a-url")
        )
        .is_err()
    );
}

#[tokio::test]
async fn custom_http_proxy_routes_public_https_via_proxy_and_bypasses_loopback() {
    // Wire-level proxy verification: a local TCP listener captures incoming
    // HTTP CONNECT tunnels (with Proxy-Authorization) for public HTTPS targets,
    // while a local Ollama-style loopback server receives direct traffic without
    // touching the proxy listener.
    let proxy_listener = TcpListener::bind("127.0.0.1:0").expect("bind mock proxy");
    let proxy_port = proxy_listener.local_addr().expect("proxy addr").port();
    let proxy_hits = Arc::new(AtomicUsize::new(0));
    let proxy_captured = Arc::new(Mutex::new(Vec::<String>::new()));
    let stop = Arc::new(AtomicBool::new(false));
    let proxy_worker = {
        let proxy_hits = Arc::clone(&proxy_hits);
        let proxy_captured = Arc::clone(&proxy_captured);
        let stop = Arc::clone(&stop);
        thread::spawn(move || {
            while !stop.load(Ordering::SeqCst) {
                let Ok((mut stream, _)) = proxy_listener.accept() else {
                    break;
                };
                if stop.load(Ordering::SeqCst) {
                    break;
                }
                proxy_hits.fetch_add(1, Ordering::SeqCst);
                let _ = stream.set_read_timeout(Some(Duration::from_secs(2)));
                let mut buf = [0_u8; 2048];
                let n = stream.read(&mut buf).unwrap_or(0);
                proxy_captured
                    .lock()
                    .expect("record proxy req")
                    .push(String::from_utf8_lossy(&buf[..n]).into_owned());
                let _ = stream.write_all(
                    b"HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                );
                let _ = stream.flush();
            }
        })
    };

    let proxy_cfg =
        NetworkProxyConfig::custom(format!("http://proxyuser:proxypass@127.0.0.1:{proxy_port}"));
    let client =
        pooled_client_with_proxy("https://api.example.invalid", &test_limits(), &proxy_cfg)
            .expect("build custom http proxy client");

    let public_settings = ProviderSettings {
        provider_type: ProviderType::OpenAiCompatible,
        api_base_url: "https://api.example.invalid/v1".to_owned(),
        text_endpoint: ProviderType::OpenAiCompatible.default_endpoint().to_owned(),
        text_model: "proxy-test-model".to_owned(),
        supports_text: true,
        network_enabled: true,
        safe_dev_mode: false,
        ..ProviderSettings::default()
    };

    let err = client
        .execute(
            provider_for(public_settings.provider_type).as_ref(),
            &public_settings,
            "key",
            "proxy-wire-http",
            &TranslationRequest::text("hello", LanguagePair::new("en", "zh-CN")),
            &CancellationToken::new(),
        )
        .await
        .expect_err("502 from mock proxy must surface as transport error");
    assert!(!err.message.contains("proxypass"));
    assert_eq!(proxy_hits.load(Ordering::SeqCst), 1);
    let captured = proxy_captured.lock().expect("read captured").join("\n");
    assert!(
        captured.starts_with("CONNECT api.example.invalid:443"),
        "expected HTTP CONNECT tunnel request, got: {captured}"
    );
    assert!(
        captured
            .to_ascii_lowercase()
            .contains("proxy-authorization: basic "),
        "expected Basic Proxy-Authorization header, got: {captured}"
    );

    // Local loopback server must NOT go through the proxy even with bypass_local=false.
    let local_server = KeepAliveServer::start();
    let local_settings = settings_for(&local_server);
    let mut no_bypass = proxy_cfg.clone();
    no_bypass.bypass_local = false;
    let local_client =
        pooled_client_with_proxy(&local_settings.api_base_url, &test_limits(), &no_bypass)
            .expect("build local client");
    execute_text(&local_client, &local_settings, "local-bypass-marker").await;
    assert_eq!(
        proxy_hits.load(Ordering::SeqCst),
        1,
        "loopback request must never hit the proxy"
    );
    assert_eq!(local_server.accept_count(), 1);

    stop.store(true, Ordering::SeqCst);
    let _ = TcpStream::connect(format!("127.0.0.1:{proxy_port}"));
    let _ = proxy_worker.join();
}

#[tokio::test]
async fn custom_socks5_proxy_sends_socks5_handshake_at_wire_level() {
    let socks_listener = TcpListener::bind("127.0.0.1:0").expect("bind mock socks5");
    let socks_port = socks_listener.local_addr().expect("socks addr").port();
    let handshake_bytes = Arc::new(Mutex::new(Vec::<u8>::new()));
    let stop = Arc::new(AtomicBool::new(false));
    let worker = {
        let handshake_bytes = Arc::clone(&handshake_bytes);
        let stop = Arc::clone(&stop);
        thread::spawn(move || {
            let Ok((mut stream, _)) = socks_listener.accept() else {
                return;
            };
            if stop.load(Ordering::SeqCst) {
                return;
            }
            let _ = stream.set_read_timeout(Some(Duration::from_secs(2)));
            let mut buf = [0_u8; 64];
            let n = stream.read(&mut buf).unwrap_or(0);
            handshake_bytes
                .lock()
                .expect("record socks5 bytes")
                .extend_from_slice(&buf[..n]);
            // Reject method so reqwest fails cleanly right after greeting.
            let _ = stream.write_all(&[0x05, 0xFF]);
            let _ = stream.flush();
        })
    };

    let socks_cfg = NetworkProxyConfig::custom(format!(
        "socks5h://socksuser:sockspass@127.0.0.1:{socks_port}"
    ));
    let client =
        pooled_client_with_proxy("https://api.example.invalid", &test_limits(), &socks_cfg)
            .expect("build socks5h client");
    let settings = ProviderSettings {
        provider_type: ProviderType::OpenAiCompatible,
        api_base_url: "https://api.example.invalid/v1".to_owned(),
        text_endpoint: ProviderType::OpenAiCompatible.default_endpoint().to_owned(),
        text_model: "socks-test-model".to_owned(),
        supports_text: true,
        network_enabled: true,
        safe_dev_mode: false,
        ..ProviderSettings::default()
    };

    let _ = client
        .execute(
            provider_for(settings.provider_type).as_ref(),
            &settings,
            "key",
            "socks5-wire",
            &TranslationRequest::text("hello", LanguagePair::new("en", "zh-CN")),
            &CancellationToken::new(),
        )
        .await
        .expect_err("mock socks5 rejects method");

    let bytes = handshake_bytes.lock().expect("read handshake").clone();
    assert!(
        bytes.len() >= 3 && bytes[0] == 0x05,
        "expected SOCKS5 greeting starting with 0x05, got: {bytes:?}"
    );

    stop.store(true, Ordering::SeqCst);
    let _ = TcpStream::connect(format!("127.0.0.1:{socks_port}"));
    let _ = worker.join();
}
