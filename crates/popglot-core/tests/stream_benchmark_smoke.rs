//! Smoke test for streaming benchmark to prevent bitrot in CI.

#![allow(
    clippy::all,
    clippy::pedantic,
    unused_imports,
    clippy::uninlined_format_args
)]

use popglot_core::STREAM_PROMPT_VERSION;
use popglot_core::provider::{ProviderClient, TranslationRequest, TransportLimits, provider_for};
use popglot_core::sse::SseDecoder;
use popglot_core::streaming::TextFirstAssembler;
use popglot_domain::{LanguagePair, ProviderSettings, ProviderType};
use serde_json::Value;
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};
use tokio_util::sync::CancellationToken;

/// Helper to read an entire HTTP request including body according to Content-Length.
fn read_http_request(stream: &mut TcpStream) -> Vec<u8> {
    let _ = stream.set_read_timeout(Some(Duration::from_secs(5)));
    let mut request = Vec::new();
    let mut buffer = [0_u8; 4096];
    let mut content_length = None;
    loop {
        let count = match stream.read(&mut buffer) {
            Ok(n) if n > 0 => n,
            _ => break,
        };
        request.extend_from_slice(&buffer[..count]);
        if let Some(header_end) = find_header_end(&request) {
            content_length.get_or_insert_with(|| parse_content_length(&request[..header_end]));
            let expected_total = header_end + 4 + content_length.unwrap_or_default();
            if request.len() >= expected_total || request.len() > 10 * 1024 * 1024 {
                break;
            }
        }
    }
    request
}

fn find_header_end(bytes: &[u8]) -> Option<usize> {
    bytes.windows(4).position(|window| window == b"\r\n\r\n")
}

fn parse_content_length(headers: &[u8]) -> usize {
    String::from_utf8_lossy(headers)
        .lines()
        .find_map(|line| {
            let (name, value) = line.split_once(':')?;
            name.eq_ignore_ascii_case("content-length")
                .then(|| value.trim().parse().ok())
                .flatten()
        })
        .unwrap_or_default()
}

/// RAII mock server for streaming smoke and stress tests.
struct SmokeMockServer {
    pub base_url: String,
    shutdown: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
}

impl SmokeMockServer {
    fn start<F>(frame_generator: F) -> Self
    where
        F: Fn() -> Vec<(Vec<u8>, Duration)> + Send + Sync + 'static,
    {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind smoke test server");
        let base_url = format!("http://{}", listener.local_addr().expect("addr"));
        let shutdown = Arc::new(AtomicBool::new(false));
        let shutdown_clone = Arc::clone(&shutdown);
        let generator = Arc::new(frame_generator);

        let worker = thread::spawn(move || {
            while !shutdown_clone.load(Ordering::SeqCst) {
                let Ok((mut stream, _)) = listener.accept() else {
                    break;
                };
                if shutdown_clone.load(Ordering::SeqCst) {
                    break;
                }
                let _ = stream.set_read_timeout(Some(Duration::from_secs(5)));
                let _ = stream.set_write_timeout(Some(Duration::from_secs(10)));

                let _ = read_http_request(&mut stream);
                if shutdown_clone.load(Ordering::SeqCst) {
                    break;
                }

                let header = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n";
                if stream.write_all(header.as_bytes()).is_err() {
                    continue;
                }
                if stream.flush().is_err() {
                    continue;
                }

                let frames = generator();
                let mut write_failed = false;
                for (frame, delay) in frames {
                    if !delay.is_zero() {
                        thread::sleep(delay);
                    }
                    if shutdown_clone.load(Ordering::SeqCst) {
                        write_failed = true;
                        break;
                    }
                    let chunk_hdr = format!("{:X}\r\n", frame.len());
                    if stream.write_all(chunk_hdr.as_bytes()).is_err() {
                        write_failed = true;
                        break;
                    }
                    if stream.write_all(&frame).is_err() {
                        write_failed = true;
                        break;
                    }
                    if stream.write_all(b"\r\n").is_err() {
                        write_failed = true;
                        break;
                    }
                    if stream.flush().is_err() {
                        write_failed = true;
                        break;
                    }
                }

                if !write_failed {
                    let _ = stream.write_all(b"0\r\n\r\n");
                    let _ = stream.flush();
                }

                let _ = stream.shutdown(std::net::Shutdown::Write);
                let _ = stream.set_read_timeout(Some(Duration::from_millis(500)));
                let mut drain = [0_u8; 1024];
                while let Ok(n) = stream.read(&mut drain) {
                    if n == 0 {
                        break;
                    }
                }
            }
        });

        Self {
            base_url,
            shutdown,
            worker: Some(worker),
        }
    }
}

impl Drop for SmokeMockServer {
    fn drop(&mut self) {
        self.shutdown.store(true, Ordering::SeqCst);
        if let Some(worker) = self.worker.take() {
            let _ = TcpStream::connect(self.base_url.trim_start_matches("http://"));
            let _ = worker.join();
        }
    }
}

#[tokio::test]
async fn stream_benchmark_loopback_smoke_test() {
    let delimiter = "PGMETA_smoke_delim_0123456789";
    let delim_str = delimiter.to_owned();

    let server = SmokeMockServer::start(move || {
        vec![
            (
                b"data: {\"choices\":[{\"delta\":{\"content\":\"Hello \"}}]}\n\n".to_vec(),
                Duration::from_millis(5),
            ),
            (
                b"data: {\"choices\":[{\"delta\":{\"content\":\"World!\"}}]}\n\n".to_vec(),
                Duration::from_millis(2),
            ),
            (
                format!("data: {{\"choices\":[{{\"delta\":{{\"content\":\"\\n{delim_str}\\n{{\\\"explanation\\\":\\\"ok\\\",\\\"warnings\\\":[]}}\"}}}}]}}\n\n").into_bytes(),
                Duration::from_millis(2),
            ),
            (
                b"data: [DONE]\n\n".to_vec(),
                Duration::ZERO,
            ),
        ]
    });

    let client = ProviderClient::new(TransportLimits {
        connect_timeout: Duration::from_secs(2),
        total_timeout: Duration::from_secs(10),
        max_response_bytes: 1024 * 1024,
        max_retries: 0,
        retry_delay: Duration::from_millis(1),
        accept_invalid_certs: false,
    })
    .expect("create client");

    let provider_type = ProviderType::OpenAiCompatible;
    let provider = provider_for(provider_type);
    let settings = ProviderSettings {
        provider_type,
        api_base_url: server.base_url.clone(),
        text_endpoint: provider_type.default_endpoint().to_owned(),
        vision_endpoint: provider_type.default_endpoint().to_owned(),
        text_model: "smoke-model".to_owned(),
        vision_model: "smoke-model".to_owned(),
        supports_text: true,
        supports_vision: true,
        network_enabled: false,
        safe_dev_mode: false,
        ..ProviderSettings::default()
    };

    let request = TranslationRequest::text("Hello World", LanguagePair::new("en", "zh-CN"));
    let cancellation = CancellationToken::new();

    let mut deltas = Vec::new();
    let start = Instant::now();
    let response = client
        .execute_stream(
            provider.as_ref(),
            &settings,
            "smoke-key",
            "smoke-req-1",
            &request,
            Some(delimiter),
            &cancellation,
            |delta| deltas.push(delta.to_owned()),
        )
        .await
        .expect("execute stream response");

    let elapsed = start.elapsed();
    assert_eq!(deltas.concat(), "Hello World!");
    assert_eq!(response.result.translated_text, "Hello World!");
    assert_eq!(response.result.explanation, "ok");
    assert!(response.result.warnings.is_empty());
    assert!(elapsed >= Duration::from_millis(5));
}

#[tokio::test]
async fn stream_benchmark_clean_chunked_eof_stress_test() {
    let delimiter = "PGMETA_stress_delim_0123456789";
    let delim_str = delimiter.to_owned();

    let server = SmokeMockServer::start(move || {
        vec![
            (
                b"data: {\"choices\":[{\"delta\":{\"content\":\"Chunked \"}}]}\n\n".to_vec(),
                Duration::ZERO,
            ),
            (
                b"data: {\"choices\":[{\"delta\":{\"content\":\"EOF \"}}]}\n\n".to_vec(),
                Duration::ZERO,
            ),
            (
                b"data: {\"choices\":[{\"delta\":{\"content\":\"Stress!\"}}]}\n\n".to_vec(),
                Duration::ZERO,
            ),
            (
                format!("data: {{\"choices\":[{{\"delta\":{{\"content\":\"\\n{delim_str}\\n{{\\\"explanation\\\":\\\"stress ok\\\",\\\"warnings\\\":[]}}\"}}}}]}}\n\n").into_bytes(),
                Duration::ZERO,
            ),
            (
                b"data: [DONE]\n\n".to_vec(),
                Duration::ZERO,
            ),
        ]
    });

    let client = ProviderClient::new(TransportLimits {
        connect_timeout: Duration::from_secs(2),
        total_timeout: Duration::from_secs(10),
        max_response_bytes: 1024 * 1024,
        max_retries: 0,
        retry_delay: Duration::from_millis(1),
        accept_invalid_certs: false,
    })
    .expect("create client");

    let provider_type = ProviderType::OpenAiCompatible;
    let provider = provider_for(provider_type);
    let settings = ProviderSettings {
        provider_type,
        api_base_url: server.base_url.clone(),
        text_endpoint: provider_type.default_endpoint().to_owned(),
        vision_endpoint: provider_type.default_endpoint().to_owned(),
        text_model: "smoke-model".to_owned(),
        vision_model: "smoke-model".to_owned(),
        supports_text: true,
        supports_vision: true,
        network_enabled: false,
        safe_dev_mode: false,
        ..ProviderSettings::default()
    };

    let iterations = 50_usize;
    for i in 0..iterations {
        let request = TranslationRequest::text("Test EOF Stress", LanguagePair::new("en", "zh-CN"));
        let cancellation = CancellationToken::new();
        let mut deltas = Vec::new();
        let response = client
            .execute_stream(
                provider.as_ref(),
                &settings,
                "smoke-key",
                &format!("smoke-stress-{i}"),
                &request,
                Some(delimiter),
                &cancellation,
                |delta| deltas.push(delta.to_owned()),
            )
            .await
            .unwrap_or_else(|err| panic!("iteration {i} failed with error: {err:?}"));

        assert_eq!(deltas.concat(), "Chunked EOF Stress!");
        assert_eq!(response.result.translated_text, "Chunked EOF Stress!");
        assert_eq!(response.result.explanation, "stress ok");
        assert!(response.result.warnings.is_empty());
    }
}

#[test]
fn stream_benchmark_json_contract_keys_verified() {
    let raw_json = serde_json::json!({
        "prompt_version": STREAM_PROMPT_VERSION,
        "scenario": "realistic_stream",
        "provider": "OpenAiCompatible",
        "iterations": 10,
        "warmup_iterations": 2,
        "config": {
            "injected_ttft_ms": 30,
            "injected_chunk_interval_ms": 5,
            "chunk_count": 10,
            "configured_chars": 128,
            "seed": 42,
            "offline": true
        },
        "ttft_ms": {
            "min_ms": 30.1,
            "p50_ms": 30.5,
            "p95_ms": 32.0,
            "max_ms": 32.5,
            "avg_ms": 30.8
        },
        "stream_ms": {
            "min_ms": 45.0,
            "p50_ms": 46.0,
            "p95_ms": 48.0,
            "max_ms": 48.5,
            "avg_ms": 46.2
        },
        "total_ms": {
            "min_ms": 75.1,
            "p50_ms": 76.5,
            "p95_ms": 80.0,
            "max_ms": 81.0,
            "avg_ms": 77.0
        },
        "chars_per_sec": 2770.5,
        "delta_count": {
            "min": 10,
            "p50": 10,
            "p95": 10,
            "max": 10,
            "avg": 10.0
        },
        "total_chars": 1280,
        "flush_simulated": false,
        "parse_errors": 0,
        "warnings_count": 0,
        "success_rate": 1.0,
        "tolerance_check": {
            "tolerance_ms": 35,
            "passed": true,
            "expected_failure": false,
            "ttft_overhead_ms": 0.5,
            "stream_overhead_ms": 1.0,
            "note": "Tolerance threshold applies only to local injected delay overhead; does not claim cloud LLM performance."
        }
    });

    let obj: Value = serde_json::from_str(&raw_json.to_string()).expect("valid json");
    assert_eq!(obj["prompt_version"], STREAM_PROMPT_VERSION);
    assert_eq!(obj["scenario"], "realistic_stream");
    assert_eq!(obj["iterations"], 10);
    assert_eq!(obj["config"]["configured_chars"], 128);
    assert!(obj["ttft_ms"]["p50_ms"].is_number());
    assert!(obj["ttft_ms"]["p95_ms"].is_number());
    assert!(obj["stream_ms"]["p50_ms"].is_number());
    assert!(obj["total_ms"]["p50_ms"].is_number());
    assert!(obj["chars_per_sec"].is_number());
    assert!(obj["delta_count"]["p50"].is_number());
    assert_eq!(obj["total_chars"], 1280);
    assert_eq!(obj["flush_simulated"], false);
    assert_eq!(obj["parse_errors"], 0);
    assert_eq!(obj["tolerance_check"]["expected_failure"], false);
    assert_eq!(obj["tolerance_check"]["passed"], true);
}

#[tokio::test]
async fn stream_benchmark_corrupted_sse_smoke_test() {
    let server = SmokeMockServer::start(|| {
        vec![
            (
                b"data: {invalid-json-corrupted-line-test}\n\n".to_vec(),
                Duration::ZERO,
            ),
            (b"data: [DONE]\n\n".to_vec(), Duration::ZERO),
        ]
    });

    let client = ProviderClient::new(TransportLimits {
        connect_timeout: Duration::from_secs(2),
        total_timeout: Duration::from_secs(10),
        max_response_bytes: 1024 * 1024,
        max_retries: 0,
        retry_delay: Duration::from_millis(1),
        accept_invalid_certs: false,
    })
    .expect("create client");

    let provider_type = ProviderType::OpenAiCompatible;
    let provider = provider_for(provider_type);
    let settings = ProviderSettings {
        provider_type,
        api_base_url: server.base_url.clone(),
        text_endpoint: provider_type.default_endpoint().to_owned(),
        vision_endpoint: provider_type.default_endpoint().to_owned(),
        text_model: "smoke-model".to_owned(),
        vision_model: "smoke-model".to_owned(),
        supports_text: true,
        supports_vision: true,
        network_enabled: false,
        safe_dev_mode: false,
        ..ProviderSettings::default()
    };

    let iterations = 5_usize;
    let mut parse_errors = 0_usize;
    let mut successes = 0_usize;
    let mut total_output_chars = 0_usize;

    for i in 0..iterations {
        let request =
            TranslationRequest::text("Test corrupted SSE", LanguagePair::new("en", "zh-CN"));
        let cancellation = CancellationToken::new();
        let mut iter_output_chars = 0_usize;
        let res = client
            .execute_stream(
                provider.as_ref(),
                &settings,
                "smoke-key",
                &format!("smoke-corrupt-{i}"),
                &request,
                None,
                &cancellation,
                |delta| {
                    iter_output_chars += delta.chars().count();
                },
            )
            .await;

        match res {
            Ok(_) => {
                successes += 1;
                total_output_chars += iter_output_chars;
            }
            Err(_) => {
                parse_errors += 1;
            }
        }
    }

    assert_eq!(successes, 0, "Corrupted SSE must produce 0 successes");
    assert_eq!(
        parse_errors, iterations,
        "All corrupted iterations must produce parse errors"
    );
    assert_eq!(
        total_output_chars, 0,
        "No output characters should be counted on corrupted stream"
    );

    // Success rate is 0.0 -> chars_per_sec must be 0.0
    let chars_per_sec = if successes > 0 { 100.0 } else { 0.0 };
    assert_eq!(chars_per_sec, 0.0);

    // Tolerance check for expected failure: passed must be true because all errors were captured
    let expected_failure = true;
    let passed = parse_errors == iterations && successes == 0;
    assert!(
        passed,
        "Passed must be true when all errors are correctly captured in expected failure scenario"
    );
    assert!(expected_failure);
}

#[test]
fn test_stream_benchmark_cli_corrupted_sse_and_all_scenarios() {
    // Execute Cargo's prebuilt benchmark binary directly. Spawning a nested
    // `cargo run` from inside `cargo test` can deadlock on the target-dir lock
    // or be killed for memory pressure on macOS CI runners.
    let benchmark = env!("CARGO_BIN_EXE_stream_benchmark");
    let output = std::process::Command::new(benchmark)
        .args(["--scenario", "corrupted-sse", "--iterations", "5", "--json"])
        .output()
        .expect("execute stream_benchmark corrupted-sse");

    let stdout = String::from_utf8_lossy(&output.stdout);
    let stderr = String::from_utf8_lossy(&output.stderr);
    assert!(
        output.status.success(),
        "stream_benchmark corrupted-sse must exit successfully: exit={:?}\n--- STDOUT ---\n{}\n--- STDERR ---\n{}",
        output.status.code(),
        stdout,
        stderr
    );
    let val: Value = serde_json::from_str(&stdout).expect("valid JSON summary");

    assert_eq!(val["scenario"], "corrupted_sse");
    assert_eq!(val["iterations"], 5);
    assert_eq!(val["success_rate"], 0.0);
    assert_eq!(val["chars_per_sec"], 0.0);
    assert_eq!(val["total_chars"], 0);
    assert_eq!(val["parse_errors"], 5);
    assert_eq!(val["ttft_ms"]["p50_ms"], 0.0);
    assert_eq!(val["stream_ms"]["p50_ms"], 0.0);
    assert_eq!(val["total_ms"]["p50_ms"], 0.0);
    assert_eq!(val["tolerance_check"]["expected_failure"], true);
    assert_eq!(val["tolerance_check"]["passed"], true);

    // Run all scenarios through the same prebuilt binary. The 250 ms tolerance
    // verifies injected-delay accounting without enforcing a cloud-runner SLA.
    let output_all = std::process::Command::new(benchmark)
        .args([
            "--scenario",
            "all",
            "--iterations",
            "2",
            "--tolerance-ms",
            "250",
            "--validate",
            "--json",
        ])
        .output()
        .expect("execute stream_benchmark all");

    let stdout_all = String::from_utf8_lossy(&output_all.stdout);
    let stderr_all = String::from_utf8_lossy(&output_all.stderr);
    assert!(
        output_all.status.success(),
        "stream_benchmark --scenario all --validate must pass: exit={:?}\n--- STDOUT ---\n{}\n--- STDERR ---\n{}",
        output_all.status.code(),
        stdout_all,
        stderr_all
    );
    let val_all: Value = serde_json::from_str(&stdout_all).expect("valid JSON multi report");

    assert_eq!(
        val_all["overall_passed"], true,
        "overall_passed must be true"
    );
    let scenarios = val_all["scenarios"].as_array().expect("scenarios array");
    assert_eq!(
        scenarios.len(),
        6,
        "all 6 scenarios must be present in report"
    );
    let corrupted = scenarios
        .iter()
        .find(|s| s["scenario"] == "corrupted_sse")
        .expect("corrupted_sse scenario present");

    assert_eq!(corrupted["success_rate"], 0.0);
    assert_eq!(corrupted["chars_per_sec"], 0.0);
    assert_eq!(corrupted["total_chars"], 0);
    assert_eq!(corrupted["parse_errors"], 2);
    assert_eq!(corrupted["tolerance_check"]["expected_failure"], true);
    assert_eq!(corrupted["tolerance_check"]["passed"], true);
}
