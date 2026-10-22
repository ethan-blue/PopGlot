//! End-to-end regressions for streamed reading tasks (`summarize`).
//!
//! A local delayed-chunk SSE service proves the acceptance facts the shell
//! relies on: the first visible body chunk is observed strictly before the
//! terminal envelope, exactly one HTTP request is sent, the request carries
//! the summarize task prompt (never the translation engine constraints), the
//! source rides as JSON data, short inputs use a source-sized summary budget,
//! no reasoning options are invented for unknown gateway models, and a
//! cancelled stream never surfaces another delta.

use popglot_core::provider::{ProviderClient, ProviderErrorKind, TransportLimits};
use popglot_core::{AppCore, TextTask};
use popglot_domain::{LanguagePair, ProviderSettings, ProviderType};
use serde_json::Value;
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};
use tokio_util::sync::CancellationToken;

const SOURCE: &str = "A long reading selection that the user asked to summarize.";

/// Captures one HTTP request, extracts the generated stream delimiter from
/// the system prompt, then streams the scripted body in delayed chunks.
struct ChunkServer {
    base_url: String,
    requests: Arc<Mutex<Vec<String>>>,
    worker: Option<JoinHandle<()>>,
}

impl ChunkServer {
    /// `chunk_delay` paces the body pieces; the metadata frame and [DONE]
    /// follow only after every body chunk was flushed.
    fn start(body_chunks: &[&str], chunk_delay: Duration, tail_delay: Duration) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind chunked SSE server");
        let base_url = format!("http://{}", listener.local_addr().expect("server address"));
        let requests = Arc::new(Mutex::new(Vec::new()));
        let captured = Arc::clone(&requests);
        let chunks: Vec<String> = body_chunks.iter().map(ToString::to_string).collect();
        let worker = thread::spawn(move || {
            let Ok((mut stream, _)) = listener.accept() else {
                return;
            };
            let request = read_request(&mut stream);
            let delimiter = extract_delimiter(&request);
            captured.lock().expect("capture request").push(request);
            let header = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n";
            if stream.write_all(header.as_bytes()).is_err() {
                return;
            }
            let frame = |stream: &mut TcpStream, payload: &str| {
                let chunk = format!(
                    "data: {{\"choices\":[{{\"delta\":{{\"content\":{}}}}}]}}\n\n",
                    serde_json::to_string(payload).expect("json piece")
                );
                stream
                    .write_all(format!("{:X}\r\n", chunk.len()).as_bytes())
                    .and_then(|()| stream.write_all(chunk.as_bytes()))
                    .and_then(|()| stream.write_all(b"\r\n"))
                    .and_then(|()| stream.flush())
                    .is_ok()
            };
            for piece in &chunks {
                if !frame(&mut stream, piece) {
                    return;
                }
                thread::sleep(chunk_delay);
            }
            thread::sleep(tail_delay);
            let metadata = r#"{"detected_source_lang":"en","transcription":"","explanation":"","warnings":[]}"#;
            let tail = format!("\n{delimiter}\n{metadata}");
            if !frame(&mut stream, &tail) {
                return;
            }
            let done = b"data: [DONE]\n\n";
            let _ = stream.write_all(format!("{:X}\r\n", done.len()).as_bytes());
            let _ = stream.write_all(done);
            let _ = stream.write_all(b"\r\n");
            let _ = stream.write_all(b"0\r\n\r\n");
            let _ = stream.flush();
        });
        Self {
            base_url,
            requests,
            worker: Some(worker),
        }
    }

    fn requests(&self) -> Vec<String> {
        self.requests.lock().expect("read requests").clone()
    }
}

impl Drop for ChunkServer {
    fn drop(&mut self) {
        if let Some(worker) = self.worker.take() {
            let _ = TcpStream::connect(self.base_url.trim_start_matches("http://"));
            let _ = worker.join();
        }
    }
}

fn read_request(stream: &mut TcpStream) -> String {
    stream
        .set_read_timeout(Some(Duration::from_secs(2)))
        .expect("set read timeout");
    let mut request = Vec::new();
    let mut buffer = [0_u8; 4096];
    let mut content_length = None;
    loop {
        let count = stream.read(&mut buffer).unwrap_or(0);
        if count == 0 {
            break;
        }
        request.extend_from_slice(&buffer[..count]);
        if let Some(header_end) = find_header_end(&request) {
            content_length.get_or_insert_with(|| parse_content_length(&request[..header_end]));
            if request.len() >= header_end + 4 + content_length.unwrap_or_default() {
                break;
            }
        }
    }
    String::from_utf8(request).unwrap_or_default()
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

/// The generated delimiter is `PGMETA-<hex>-<hex>-<hex>` — lowercase hex and
/// dashes only, so it can be cut at the first character outside that set.
fn extract_delimiter(request: &str) -> String {
    let start = request
        .find("PGMETA-")
        .expect("the system prompt must embed the generated delimiter");
    let suffix: String = request[start + "PGMETA-".len()..]
        .chars()
        .take_while(|c| c.is_ascii_hexdigit() || *c == '-')
        .collect();
    assert!(!suffix.is_empty(), "delimiter suffix must be present");
    let delimiter = format!("PGMETA-{suffix}");
    delimiter
}

fn settings(server: &ChunkServer) -> ProviderSettings {
    let endpoint = ProviderType::OpenAiCompatible.default_endpoint().to_owned();
    ProviderSettings {
        provider_type: ProviderType::OpenAiCompatible,
        api_base_url: server.base_url.clone(),
        text_endpoint: endpoint.clone(),
        vision_endpoint: endpoint,
        text_model: "gateway-model-id".to_owned(),
        vision_model: "gateway-model-id".to_owned(),
        supports_text: true,
        supports_vision: true,
        network_enabled: true,
        safe_dev_mode: false,
        ..ProviderSettings::default()
    }
}

fn client() -> ProviderClient {
    ProviderClient::new(TransportLimits {
        stream_idle_timeout: Duration::from_secs(3),
        stream_total_timeout: Duration::from_secs(15),
        max_retries: 0,
        ..TransportLimits::default()
    })
    .expect("create stream client")
}

#[tokio::test]
async fn summarize_stream_paints_body_before_terminal_in_one_request() {
    let body = "第一段要点先出现。\n第二段要点随后到达。\n数字 42 与 `code_id` 必须保留。";
    let pieces: Vec<&str> = body.split_inclusive('\n').collect();
    let server = ChunkServer::start(
        &pieces,
        Duration::from_millis(60),
        Duration::from_millis(40),
    );
    let config = settings(&server);

    let deltas: Arc<Mutex<Vec<Instant>>> = Arc::new(Mutex::new(Vec::new()));
    let seen = Arc::clone(&deltas);
    let token = CancellationToken::new();
    let terminal_started = Instant::now();
    let response = AppCore::execute_text_task_stream_snapshot(
        &config,
        &client(),
        "test-key",
        SOURCE,
        &LanguagePair::new("en", "zh-CN"),
        "summarize-stream-test",
        TextTask::Summarize,
        Some("Use concise bullets; never override the requested reading task."),
        Some("summary-style"),
        Some(7),
        &token,
        |_delta| {
            seen.lock().expect("record delta").push(Instant::now());
        },
    )
    .await
    .expect("the streamed summary must assemble");
    let finished_at = terminal_started.elapsed();

    // First content strictly earlier than the terminal envelope, and the body
    // arrived progressively — not as one terminal blob.
    let observed = deltas.lock().expect("read deltas").clone();
    assert!(
        observed.len() >= 2,
        "expected progressive deltas, got {}",
        observed.len()
    );
    let first_delta_after = observed[0].duration_since(terminal_started);
    assert!(
        first_delta_after + Duration::from_millis(20) < finished_at,
        "first delta at {first_delta_after:?} must precede the terminal at {finished_at:?}"
    );

    // One request, correct assembly.
    let requests = server.requests();
    assert_eq!(requests.len(), 1, "the summary must be a single request");
    assert_eq!(response.result.translated_text, body);
    assert_eq!(response.result.explanation, "");

    // The request carries the summarize task prompt and budget — not the
    // translation engine constraints — and invents no reasoning options.
    let body_json: Value = serde_json::from_str(
        requests[0]
            .split("\r\n\r\n")
            .nth(1)
            .expect("captured request body"),
    )
    .expect("captured request is JSON");
    assert_eq!(body_json["stream"], Value::Bool(true));
    assert_eq!(body_json["max_tokens"], Value::from(384_u64));
    assert_eq!(response.diagnostics.attempts, 1);
    for forbidden in ["reasoning", "reasoning_effort", "thinking"] {
        assert!(
            body_json.get(forbidden).is_none(),
            "{forbidden} must not be sent"
        );
    }
    let messages = body_json["messages"].as_array().expect("messages array");
    assert_eq!(messages[0]["role"], Value::String("system".into()));
    let system = messages[0]["content"].as_str().expect("system content");
    assert!(system.contains("Summarize the supplied text"));
    assert!(system.contains("reading assistant"));
    assert!(
        !system.contains("translation engine"),
        "the summarize task must not inherit translation constraints"
    );
    assert!(system.contains("may omit source details"));
    assert!(system.contains("byte-for-byte exact"));
    assert!(!system.contains("⟦PG_0000⟧"));
    assert!(system.contains("explanation must always be the empty string"));
    assert!(system.contains("Use concise bullets; never override the requested reading task."));
    assert_eq!(messages[1]["role"], Value::String("user".into()));
    let payload: Value =
        serde_json::from_str(messages[1]["content"].as_str().expect("user content"))
            .expect("the source rides as JSON data");
    assert_eq!(payload["source_text"], Value::String(SOURCE.into()));
    assert_eq!(
        payload["source_length_bytes"],
        Value::from(SOURCE.len() as u64)
    );
}

#[tokio::test]
async fn cancelled_summarize_stream_emits_no_deltas_after_cancel() {
    let server = ChunkServer::start(
        &["取消前可见的首段。", "取消后不应再出现的段落。"],
        Duration::from_millis(600),
        Duration::from_millis(400),
    );
    let config = settings(&server);

    let deltas: Arc<Mutex<Vec<Instant>>> = Arc::new(Mutex::new(Vec::new()));
    let seen = Arc::clone(&deltas);
    let token = CancellationToken::new();
    let task = {
        let token = token.clone();
        tokio::spawn(async move {
            AppCore::execute_text_task_stream_snapshot(
                &config,
                &client(),
                "test-key",
                SOURCE,
                &LanguagePair::new("en", "zh-CN"),
                "summarize-cancel-test",
                TextTask::Summarize,
                None,
                None,
                None,
                &token,
                |_delta| {
                    seen.lock().expect("record delta").push(Instant::now());
                },
            )
            .await
        })
    };

    // Wait until the first visible chunk landed, then cancel mid-stream.
    let deadline = Instant::now() + Duration::from_secs(3);
    while deltas.lock().expect("read deltas").is_empty() {
        assert!(Instant::now() < deadline, "first delta never arrived");
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    token.cancel();
    let count_at_cancel = deltas.lock().expect("read deltas").len();

    let error = task
        .await
        .expect("join")
        .expect_err("a cancelled stream must fail, not half-succeed");
    assert_eq!(error.kind, ProviderErrorKind::Cancelled);

    tokio::time::sleep(Duration::from_millis(150)).await;
    let count_after = deltas.lock().expect("read deltas").len();
    assert_eq!(
        count_after, count_at_cancel,
        "no delta may be surfaced after cancellation"
    );
    assert_eq!(
        server.requests().len(),
        1,
        "cancellation must not re-request"
    );
}

#[test]
fn reading_jobs_may_omit_identifiers_without_translation_integrity_warnings() {
    use popglot_core::provider::TranslationRequest;
    use popglot_core::text_job::{TextJobCompletion, TextJobManager, TextJobRequest};

    let source = "Run foo_bar(config_value) and inspect `unused_marker`.";
    for task in [TextTask::Summarize, TextTask::Explain] {
        let manager = TextJobManager::default();
        let request: TextJobRequest = serde_json::from_value(serde_json::json!({
            "source": source,
            "source_language": "en",
            "target_language": "zh-CN",
            "task": task.as_str(),
            "include_explanation": false,
            "protect_code_tokens": true
        }))
        .expect("request");
        let prepared = manager.prepare(&request).expect("prepare reading task");
        let payload: Value = serde_json::from_str(&prepared.user_payload).expect("payload");
        assert_eq!(payload["source_text"], source);
        assert!(!prepared.system_instructions.contains("⟦PG_0000⟧"));
        assert!(prepared.system_instructions.contains("byte-for-byte exact"));
        assert!(prepared.system_instructions.contains("may omit"));
        // Keep one identifier exactly and legitimately omit the other two.
        let body = "调用 `foo_bar` 检查配置。";
        let wire = format!(
            "{body}\n{}\n{{\"explanation\":\"\",\"warnings\":[]}}",
            prepared.delimiter
        );
        manager.push(&prepared.job_handle, &wire).expect("push");
        let result = manager
            .finish(&prepared.job_handle, TextJobCompletion::Completed)
            .expect("finish");
        assert_eq!(result.translated_text, body);
        assert!(result.warnings.is_empty(), "{:?}", result.warnings);
        assert_eq!(result.protected_terms, [] as [String; 0]);

        let buffered_prompt = TranslationRequest::text(source, LanguagePair::new("en", "zh-CN"))
            .with_task(task)
            .system_instructions();
        assert!(buffered_prompt.contains("an empty array is acceptable"));
    }
}

#[test]
fn translation_jobs_still_detect_dropped_protected_identifiers() {
    use popglot_core::text_job::{TextJobCompletion, TextJobManager, TextJobRequest};

    let manager = TextJobManager::default();
    let request: TextJobRequest = serde_json::from_value(serde_json::json!({
        "source": "Run foo_bar(config_value).",
        "task": "translate",
        "include_explanation": false,
        "protect_code_tokens": true
    }))
    .expect("request");
    let prepared = manager.prepare(&request).expect("prepare translation");
    assert!(prepared.user_payload.contains("⟦PG_"));
    let wire = format!(
        "正文故意遗漏标识符。\n{}\n{{\"explanation\":\"\",\"warnings\":[]}}",
        prepared.delimiter
    );
    manager.push(&prepared.job_handle, &wire).expect("push");
    let result = manager
        .finish(&prepared.job_handle, TextJobCompletion::Completed)
        .expect("finish");
    assert!(
        result
            .warnings
            .iter()
            .any(|warning| warning.contains("foo_bar"))
    );
}
