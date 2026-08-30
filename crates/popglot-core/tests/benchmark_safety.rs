//! Comprehensive safety tests for the live provider benchmark engine.
//!
//! Verifies:
//! - Dual safety flag gating (`--live` AND `--i-understand-cost`)
//! - Absolute protection against network executor invocation when gates fail
//! - Settings gates (`SafeDevMode` and `NetworkEnabled`)
//! - Fail-closed behavior on missing API keys
//! - Deterministic endpoint fingerprinting without leaking internal URLs
//! - Zero leakage of prompts, source text, response body, headers, or keys in reports
//! - Error sanitization for URLs, Bearer tokens, and API key patterns
//! - Strict fixture input character cap enforcement
//! - Safe handling of invalid configurations

#![allow(clippy::too_many_lines)]

use popglot_core::STREAM_PROMPT_VERSION;
use popglot_core::benchmark::{
    BenchmarkSafetyError, BenchmarkSubset, LiveBenchmarkConfig, LiveBenchmarkSafetyFlags,
    MockBenchmarkExecutor, compute_endpoint_fingerprint, generate_dry_run_report,
    load_benchmark_fixtures, resolve_benchmark_api_key_with_lookup, run_live_benchmark,
    sanitize_error_string,
};
use popglot_domain::{ProviderSettings, ProviderType};
use std::sync::Arc;
use std::sync::atomic::Ordering;

// ---------------------------------------------------------------------------
// 1. Dual Safety Flag Matrix & Zero Network Invocations
// ---------------------------------------------------------------------------

#[tokio::test]
async fn test_safety_flag_matrix_blocks_executor_unless_both_present() {
    let test_cases = [
        // (live, i_understand_cost, should_permit)
        (false, false, false),
        (true, false, false),
        (false, true, false),
        (true, true, true),
    ];

    for (live, cost, should_permit) in test_cases {
        let mock_executor = MockBenchmarkExecutor::default();
        let config = LiveBenchmarkConfig {
            settings: ProviderSettings {
                provider_type: ProviderType::OpenAiCompatible,
                network_enabled: true,
                safe_dev_mode: false,
                ..ProviderSettings::default()
            },
            model_override: Some("test-model".to_owned()),
            safety_flags: LiveBenchmarkSafetyFlags {
                live,
                i_understand_cost: cost,
            },
            subset: BenchmarkSubset::Minimal,
            max_input_chars: 1000,
            env_key_override: Some("mock-secret-key-12345".to_owned()),
            ..LiveBenchmarkConfig::default()
        };

        let result = run_live_benchmark(&config, &mock_executor).await;

        if should_permit {
            assert!(
                result.is_ok(),
                "Dual flags (live=true, cost=true) must allow execution, got: {result:?}"
            );
            assert!(
                mock_executor.call_count.load(Ordering::SeqCst) > 0,
                "Executor should have been called when both flags are present"
            );
        } else {
            assert!(
                matches!(result, Err(BenchmarkSafetyError::MissingSafetyFlags { .. })),
                "Expected MissingSafetyFlags for (live={live}, cost={cost}), got: {result:?}"
            );
            assert_eq!(
                mock_executor.call_count.load(Ordering::SeqCst),
                0,
                "Executor MUST NOT be invoked when flags are missing (live={live}, cost={cost})"
            );
        }
    }
}

// ---------------------------------------------------------------------------
// 2. Settings Overrides (SafeDevMode & NetworkDisabled Block Network)
// ---------------------------------------------------------------------------

#[tokio::test]
async fn test_safe_dev_mode_blocks_even_with_dual_flags() {
    let mock_executor = MockBenchmarkExecutor::default();
    let config = LiveBenchmarkConfig {
        settings: ProviderSettings {
            provider_type: ProviderType::OpenAiCompatible,
            network_enabled: true,
            safe_dev_mode: true, // Safe mode ON
            ..ProviderSettings::default()
        },
        safety_flags: LiveBenchmarkSafetyFlags {
            live: true,
            i_understand_cost: true,
        },
        subset: BenchmarkSubset::Minimal,
        max_input_chars: 1000,
        env_key_override: Some("mock-key".to_owned()),
        ..LiveBenchmarkConfig::default()
    };

    let result = run_live_benchmark(&config, &mock_executor).await;
    assert_eq!(result, Err(BenchmarkSafetyError::SafeDevModeBlocked));
    assert_eq!(
        mock_executor.call_count.load(Ordering::SeqCst),
        0,
        "Mock executor must not be called when SafeDevMode is active"
    );
}

#[tokio::test]
async fn test_network_disabled_blocks_even_with_dual_flags() {
    let mock_executor = MockBenchmarkExecutor::default();
    let config = LiveBenchmarkConfig {
        settings: ProviderSettings {
            provider_type: ProviderType::OpenAiCompatible,
            network_enabled: false, // Network disabled
            safe_dev_mode: false,
            ..ProviderSettings::default()
        },
        safety_flags: LiveBenchmarkSafetyFlags {
            live: true,
            i_understand_cost: true,
        },
        subset: BenchmarkSubset::Minimal,
        max_input_chars: 1000,
        env_key_override: Some("mock-key".to_owned()),
        ..LiveBenchmarkConfig::default()
    };

    let result = run_live_benchmark(&config, &mock_executor).await;
    assert_eq!(result, Err(BenchmarkSafetyError::NetworkDisabledBlocked));
    assert_eq!(
        mock_executor.call_count.load(Ordering::SeqCst),
        0,
        "Mock executor must not be called when network_enabled is false"
    );
}

// ---------------------------------------------------------------------------
// 3. API Key Resolution & Missing Key Handling
// ---------------------------------------------------------------------------

#[test]
fn test_api_key_resolution_priority() {
    // 1. Generic benchmark key takes precedence
    let key1 =
        resolve_benchmark_api_key_with_lookup(ProviderType::OpenAiCompatible, |name| match name {
            "POPGLOT_BENCHMARK_API_KEY" => Some("general-bench-key".to_owned()),
            "OPENAI_API_KEY" => Some("openai-specific-key".to_owned()),
            _ => None,
        });
    assert_eq!(key1.unwrap(), "general-bench-key");

    // 2. Falls back to provider-specific key
    let key2 =
        resolve_benchmark_api_key_with_lookup(ProviderType::AnthropicMessages, |name| match name {
            "ANTHROPIC_API_KEY" => Some("claude-secret-key".to_owned()),
            _ => None,
        });
    assert_eq!(key2.unwrap(), "claude-secret-key");

    // 3. Fails closed when no key found
    let err = resolve_benchmark_api_key_with_lookup(ProviderType::GeminiGenerateContent, |_| None);
    assert!(matches!(
        err,
        Err(BenchmarkSafetyError::MissingApiKey { .. })
    ));
}

#[tokio::test]
async fn test_missing_api_key_fails_closed_without_executor_invocation() {
    let mock_executor = MockBenchmarkExecutor::default();
    let config = LiveBenchmarkConfig {
        settings: ProviderSettings {
            provider_type: ProviderType::OpenAiCompatible,
            network_enabled: true,
            safe_dev_mode: false,
            ..ProviderSettings::default()
        },
        safety_flags: LiveBenchmarkSafetyFlags {
            live: true,
            i_understand_cost: true,
        },
        subset: BenchmarkSubset::Minimal,
        max_input_chars: 1000,
        env_key_lookup: Some(Arc::new(|_| None)), // Simulates missing environment keys cleanly
        ..LiveBenchmarkConfig::default()
    };

    let result = run_live_benchmark(&config, &mock_executor).await;

    assert!(matches!(
        result,
        Err(BenchmarkSafetyError::MissingApiKey { .. })
    ));
    assert_eq!(
        mock_executor.call_count.load(Ordering::SeqCst),
        0,
        "Mock executor must not be called when API key is absent"
    );
}

// ---------------------------------------------------------------------------
// 4. Endpoint Hash Determinism & Zero Internal URL Leakage
// ---------------------------------------------------------------------------

#[test]
fn test_endpoint_fingerprint_deterministic_and_hides_raw_url() {
    let base1 = "https://internal-llm-gateway.corp.example.com:8443/v1";
    let ep1 = "/chat/completions";
    let hash1_a = compute_endpoint_fingerprint(base1, ep1);
    let hash1_b = compute_endpoint_fingerprint(base1, ep1);

    assert_eq!(hash1_a, hash1_b, "Hash must be strictly deterministic");
    assert!(hash1_a.starts_with("sha256:"));
    assert_eq!(hash1_a.len(), 7 + 64);

    // Verify raw internal host/path are NOT present in fingerprint
    assert!(!hash1_a.contains("internal-llm-gateway"));
    assert!(!hash1_a.contains("corp.example.com"));
    assert!(!hash1_a.contains("8443"));

    let base2 = "https://api.openai.com/v1";
    let ep2 = "/chat/completions";
    let hash2 = compute_endpoint_fingerprint(base2, ep2);

    assert_ne!(
        hash1_a, hash2,
        "Different endpoints must produce different hashes"
    );
}

// ---------------------------------------------------------------------------
// 5. Error Sanitization (Zero Secret / Bearer / Query Leaks)
// ---------------------------------------------------------------------------

#[test]
fn test_error_sanitization_removes_sensitive_patterns() {
    let raw_error = "HTTP 401: Unauthorized at https://api.openai.com/v1/chat?key=secret_query_val_12345&foo=bar with Bearer sk-live-1234567890abcdef1234567890 and Gemini AIzaSyFakeGeminiKey9876543210. Body: {\"api_key\": \"secret_body_token\"}";
    let sanitized = sanitize_error_string(raw_error);

    // Verify secrets are redacted
    assert!(!sanitized.contains("secret_query_val_12345"));
    assert!(!sanitized.contains("sk-live-1234567890abcdef1234567890"));
    assert!(!sanitized.contains("AIzaSyFakeGeminiKey9876543210"));
    assert!(!sanitized.contains("secret_body_token"));

    // Verify structure is preserved
    assert!(sanitized.contains("HTTP 401: Unauthorized"));
    assert!(sanitized.contains("Bearer [REDACTED]"));
    assert!(sanitized.contains("?[QUERY_REDACTED]"));
}

// ---------------------------------------------------------------------------
// 6. Zero Leaks in Live & Dry-Run Benchmark Reports
// ---------------------------------------------------------------------------

#[tokio::test]
async fn test_report_contains_zero_prompts_bodies_headers_or_keys() {
    let mock_executor = MockBenchmarkExecutor {
        simulated_deltas: vec!["Translated text chunk 1".to_owned(), " chunk 2".to_owned()],
        ..MockBenchmarkExecutor::default()
    };

    let fake_secret = "sk-fake-secret-key-1234567890";
    let config = LiveBenchmarkConfig {
        settings: ProviderSettings {
            provider_type: ProviderType::OpenAiCompatible,
            network_enabled: true,
            safe_dev_mode: false,
            api_base_url: "https://confidential-cluster.internal:9000".to_owned(),
            text_model: "gpt-4o-mini".to_owned(),
            ..ProviderSettings::default()
        },
        safety_flags: LiveBenchmarkSafetyFlags {
            live: true,
            i_understand_cost: true,
        },
        subset: BenchmarkSubset::Minimal,
        max_input_chars: 2000,
        custom_text: Some("Confidential internal prompt source text with PII 12345".to_owned()),
        env_key_override: Some(fake_secret.to_owned()),
        ..LiveBenchmarkConfig::default()
    };

    let report = run_live_benchmark(&config, &mock_executor)
        .await
        .expect("benchmark execution");

    let report_json = serde_json::to_string_pretty(&report).expect("serialize report");

    // Negative assertions: Report JSON MUST NOT contain sensitive payloads
    assert!(!report_json.contains(fake_secret), "Report leaked API key!");
    assert!(
        !report_json.contains("Confidential internal prompt"),
        "Report leaked prompt source text!"
    );
    assert!(
        !report_json.contains("Translated text chunk"),
        "Report leaked model translation response body!"
    );
    assert!(
        !report_json.contains("confidential-cluster.internal"),
        "Report leaked raw internal base URL!"
    );

    // Positive assertions: Report contains proper metrics and fingerprints
    assert_eq!(report.prompt_version, STREAM_PROMPT_VERSION);
    assert!(report.endpoint_fingerprint.starts_with("sha256:"));
    assert_eq!(report.model, "gpt-4o-mini");
    assert_eq!(report.items.len(), 1);
    assert_eq!(report.items[0].status, "success");
    assert!(report.items[0].input_chars > 0);
    assert!(report.items[0].output_chars > 0);
}

// ---------------------------------------------------------------------------
// 7. Input Character Cap Hard Enforcement
// ---------------------------------------------------------------------------

#[test]
fn test_fixture_character_cap_enforced() {
    let max_chars = 30;
    let fixtures = load_benchmark_fixtures(BenchmarkSubset::All, max_chars);

    assert!(!fixtures.is_empty());
    let total_chars: usize = fixtures.iter().map(|f| f.source_text.chars().count()).sum();
    assert!(
        total_chars <= max_chars,
        "Total characters {total_chars} exceeded cap {max_chars}"
    );
}

// ---------------------------------------------------------------------------
// 8. Dry-Run Report Generation Functionality
// ---------------------------------------------------------------------------

#[test]
fn test_dry_run_report_generation() {
    let config = LiveBenchmarkConfig {
        settings: ProviderSettings {
            provider_type: ProviderType::OpenAiCompatible,
            text_model: "test-model".to_owned(),
            ..ProviderSettings::default()
        },
        safety_flags: LiveBenchmarkSafetyFlags::default(), // Offline default
        subset: BenchmarkSubset::Minimal,
        max_input_chars: 1000,
        ..LiveBenchmarkConfig::default()
    };

    let dry_report = generate_dry_run_report(&config);
    assert!(dry_report.dry_run);
    assert!(dry_report.safety_verified);
    assert_eq!(dry_report.total_output_chars, 0);
    assert!(dry_report.total_input_chars > 0);
    assert!(!dry_report.items.is_empty());
    assert_eq!(dry_report.items[0].status, "dry_run_blocked");
}

// ---------------------------------------------------------------------------
// 9. Subsets Loading & Parsing
// ---------------------------------------------------------------------------

#[test]
fn test_benchmark_subset_parsing_and_selection() {
    assert_eq!(
        BenchmarkSubset::parse("minimal"),
        Some(BenchmarkSubset::Minimal)
    );
    assert_eq!(
        BenchmarkSubset::parse("code-mixed"),
        Some(BenchmarkSubset::CodeMixed)
    );
    assert_eq!(BenchmarkSubset::parse("all"), Some(BenchmarkSubset::All));
    assert_eq!(BenchmarkSubset::parse("unknown"), None);

    let min_fixtures = load_benchmark_fixtures(BenchmarkSubset::Minimal, 2000);
    assert_eq!(min_fixtures.len(), 1);

    let code_fixtures = load_benchmark_fixtures(BenchmarkSubset::CodeMixed, 5000);
    assert!(!code_fixtures.is_empty());

    let all_fixtures = load_benchmark_fixtures(BenchmarkSubset::All, 10000);
    assert!(all_fixtures.len() >= min_fixtures.len());
}

// ---------------------------------------------------------------------------
// 10. Real ProviderClient Stream Execution on Local Loopback Mock
// ---------------------------------------------------------------------------

#[tokio::test]
async fn test_real_provider_client_executor_with_local_mock() {
    use popglot_core::benchmark::RealProviderClientExecutor;
    use popglot_core::provider::{ProviderClient, TransportLimits};
    use std::io::{Read, Write};
    use std::net::TcpListener;
    use std::sync::atomic::AtomicBool;
    use std::thread;
    use std::time::Duration;

    let listener = TcpListener::bind("127.0.0.1:0").expect("bind local test server");
    let base_url = format!("http://{}", listener.local_addr().expect("addr"));
    let shutdown = Arc::new(AtomicBool::new(false));
    let shutdown_worker = Arc::clone(&shutdown);

    let delimiter = "PGMETA_test_delimiter_123456";

    let worker = thread::spawn(move || {
        while !shutdown_worker.load(Ordering::SeqCst) {
            let Ok((mut stream, _)) = listener.accept() else {
                break;
            };
            if shutdown_worker.load(Ordering::SeqCst) {
                break;
            }
            let _ = stream.set_read_timeout(Some(Duration::from_millis(500)));
            let _ = stream.set_write_timeout(Some(Duration::from_secs(2)));

            // Consume incoming HTTP request
            let mut req_buf = Vec::new();
            let mut chunk = [0_u8; 4096];
            let mut content_length = 0_usize;
            while let Ok(n) = stream.read(&mut chunk) {
                if n == 0 {
                    break;
                }
                req_buf.extend_from_slice(&chunk[..n]);
                if let Some(pos) = req_buf.windows(4).position(|w| w == b"\r\n\r\n") {
                    if let Ok(header_str) = std::str::from_utf8(&req_buf[..pos]) {
                        for line in header_str.lines() {
                            if let Some(val) =
                                line.to_ascii_lowercase().strip_prefix("content-length:")
                            {
                                content_length = val.trim().parse().unwrap_or(0);
                            }
                        }
                    }
                    if req_buf.len() >= pos + 4 + content_length {
                        break;
                    }
                }
            }

            let header = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n";
            if stream.write_all(header.as_bytes()).is_err() {
                continue;
            }
            let _ = stream.flush();

            let frames = vec![
                b"data: {\"choices\":[{\"delta\":{\"content\":\"Benchmarked \"}}]}\n\n".to_vec(),
                b"data: {\"choices\":[{\"delta\":{\"content\":\"stream chunk\"}}]}\n\n".to_vec(),
                format!("data: {{\"choices\":[{{\"delta\":{{\"content\":\"\\n{delimiter}\\n{{\\\"explanation\\\":\\\"ok\\\",\\\"warnings\\\":[]}}\"}}}}]}}\n\n").into_bytes(),
                b"data: [DONE]\n\n".to_vec(),
            ];

            for frame in frames {
                let chunk_hdr = format!("{:X}\r\n", frame.len());
                if stream.write_all(chunk_hdr.as_bytes()).is_err() {
                    break;
                }
                if stream.write_all(&frame).is_err() {
                    break;
                }
                if stream.write_all(b"\r\n").is_err() {
                    break;
                }
                let _ = stream.flush();
            }
            let _ = stream.write_all(b"0\r\n\r\n");
            let _ = stream.flush();
        }
    });

    let client = ProviderClient::new(TransportLimits {
        connect_timeout: Duration::from_secs(2),
        total_timeout: Duration::from_secs(5),
        max_response_bytes: 1024 * 1024,
        max_retries: 0,
        retry_delay: Duration::from_millis(1),
        accept_invalid_certs: false,
    })
    .expect("create client");

    let executor = RealProviderClientExecutor::new(client);

    let config = LiveBenchmarkConfig {
        settings: ProviderSettings {
            provider_type: ProviderType::OpenAiCompatible,
            api_base_url: base_url.clone(),
            text_endpoint: ProviderType::OpenAiCompatible.default_endpoint().to_owned(),
            vision_endpoint: ProviderType::OpenAiCompatible.default_endpoint().to_owned(),
            text_model: "mock-model".to_owned(),
            vision_model: "mock-model".to_owned(),
            supports_text: true,
            supports_vision: true,
            network_enabled: true,
            safe_dev_mode: false,
            ..ProviderSettings::default()
        },
        safety_flags: LiveBenchmarkSafetyFlags {
            live: true,
            i_understand_cost: true,
        },
        subset: BenchmarkSubset::Minimal,
        max_input_chars: 1000,
        env_key_override: Some("mock-key-for-loopback".to_owned()),
        ..LiveBenchmarkConfig::default()
    };

    let report = run_live_benchmark(&config, &executor)
        .await
        .expect("run live benchmark on loopback mock");

    assert_eq!(report.items.len(), 1);
    assert_eq!(report.items[0].status, "success");
    assert!(report.items[0].output_chars > 0);
    assert!(report.avg_ttft_ms.is_some());

    shutdown.store(true, Ordering::SeqCst);
    let _ = std::net::TcpStream::connect(base_url.trim_start_matches("http://"));
    let _ = worker.join();
}
