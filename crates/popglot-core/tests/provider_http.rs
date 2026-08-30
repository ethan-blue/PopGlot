use popglot_core::provider::{
    ImageInput, ProviderClient, ProviderErrorKind, TranslationRequest, TransportLimits,
    provider_for,
};
use popglot_domain::{LanguagePair, ProviderSettings, ProviderType};
use serde_json::Value;
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::Duration;
use tokio_util::sync::CancellationToken;

const STRUCTURED_RESULT: &str = r#"{\"translated_text\":\"连接测试\",\"transcription\":\"\",\"explanation\":\"ok\",\"protected_terms\":[],\"warnings\":[]}"#;

#[derive(Clone)]
struct MockResponse {
    status: u16,
    body: String,
    delay: Duration,
}

impl MockResponse {
    fn ok(body: impl Into<String>) -> Self {
        Self {
            status: 200,
            body: body.into(),
            delay: Duration::ZERO,
        }
    }
}

struct MockServer {
    base_url: String,
    requests: Arc<Mutex<Vec<String>>>,
    worker: Option<JoinHandle<()>>,
}

impl MockServer {
    fn start(responses: Vec<MockResponse>) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind local mock server");
        let base_url = format!("http://{}", listener.local_addr().expect("mock address"));
        let requests = Arc::new(Mutex::new(Vec::new()));
        let captured = Arc::clone(&requests);
        let worker = thread::spawn(move || {
            for response in responses {
                let Ok((mut stream, _)) = listener.accept() else {
                    break;
                };
                let request = read_request(&mut stream);
                captured.lock().expect("capture request").push(request);
                if !response.delay.is_zero() {
                    thread::sleep(response.delay);
                }
                let reason = if response.status == 200 {
                    "OK"
                } else {
                    "Error"
                };
                let reply = format!(
                    "HTTP/1.1 {} {}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
                    response.status,
                    reason,
                    response.body.len(),
                    response.body
                );
                let _ = stream.write_all(reply.as_bytes());
                let _ = stream.flush();
            }
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

impl Drop for MockServer {
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
        .expect("set mock read timeout");
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

fn settings(provider_type: ProviderType, server: &MockServer) -> ProviderSettings {
    let endpoint = provider_type.default_endpoint().to_owned();
    ProviderSettings {
        provider_type,
        api_base_url: server.base_url.clone(),
        text_endpoint: endpoint.clone(),
        vision_endpoint: endpoint,
        text_model: "test-text-model".to_owned(),
        vision_model: "test-vision-model".to_owned(),
        supports_text: true,
        supports_vision: true,
        network_enabled: true,
        safe_dev_mode: false,
        ..ProviderSettings::default()
    }
}

fn client() -> ProviderClient {
    ProviderClient::new(TransportLimits {
        connect_timeout: Duration::from_secs(1),
        total_timeout: Duration::from_secs(2),
        max_response_bytes: 64 * 1024,
        max_retries: 0,
        retry_delay: Duration::from_millis(1),
        accept_invalid_certs: false,
    })
    .expect("create provider client")
}

fn chat_response() -> String {
    format!(r#"{{"choices":[{{"message":{{"content":"{STRUCTURED_RESULT}"}}}}]}}"#)
}

fn anthropic_response() -> String {
    format!(r#"{{"content":[{{"type":"text","text":"{STRUCTURED_RESULT}"}}]}}"#)
}

fn responses_api_response() -> String {
    format!(r#"{{"output_text":"{STRUCTURED_RESULT}"}}"#)
}

fn gemini_response() -> String {
    format!(r#"{{"candidates":[{{"content":{{"parts":[{{"text":"{STRUCTURED_RESULT}"}}]}}}}]}}"#)
}

#[tokio::test]
async fn openai_chat_sends_bearer_auth_and_image_url_content() {
    let server = MockServer::start(vec![MockResponse::ok(chat_response())]);
    let config = settings(ProviderType::OpenAiCompatible, &server);
    let response = client()
        .execute(
            provider_for(config.provider_type).as_ref(),
            &config,
            "local-test-key",
            "chat-mock",
            &TranslationRequest::vision(
                ImageInput::Bytes {
                    media_type: "image/png".to_owned(),
                    data: vec![1, 2, 3],
                },
                LanguagePair::new("auto", "zh-CN"),
            ),
            &CancellationToken::new(),
        )
        .await
        .expect("chat mock response");

    assert_eq!(response.result.translated_text, "连接测试");
    let request = server.requests().join("\n");
    assert!(
        request
            .to_ascii_lowercase()
            .contains("authorization: bearer local-test-key")
    );
    let body: Value = serde_json::from_str(request.split("\r\n\r\n").nth(1).expect("request body"))
        .expect("chat JSON body");
    assert_eq!(body["model"], "test-vision-model");
    assert_eq!(body["messages"][1]["content"][1]["type"], "image_url");
    assert!(
        body["messages"][1]["content"][1]["image_url"]["url"]
            .as_str()
            .expect("data URL")
            .starts_with("data:image/png;base64,")
    );
}

#[tokio::test]
async fn openai_responses_sends_bearer_auth_and_input_image_content() {
    let server = MockServer::start(vec![MockResponse::ok(responses_api_response())]);
    let config = settings(ProviderType::OpenAiResponses, &server);
    client()
        .execute(
            provider_for(config.provider_type).as_ref(),
            &config,
            "responses-local-key",
            "responses-mock",
            &TranslationRequest::vision(
                ImageInput::Url("https://example.invalid/image.png".to_owned()),
                LanguagePair::new("auto", "zh-CN"),
            ),
            &CancellationToken::new(),
        )
        .await
        .expect("Responses mock response");

    let request = server.requests().join("\n");
    assert!(
        request
            .to_ascii_lowercase()
            .contains("authorization: bearer responses-local-key")
    );
    let body: Value = serde_json::from_str(request.split("\r\n\r\n").nth(1).expect("request body"))
        .expect("Responses JSON body");
    assert_eq!(body["input"][0]["content"][1]["type"], "input_image");
    assert_eq!(
        body["input"][0]["content"][1]["image_url"],
        "https://example.invalid/image.png"
    );
    assert_eq!(body["store"], false);
}

#[tokio::test]
async fn anthropic_sends_native_auth_version_and_image_source() {
    let server = MockServer::start(vec![MockResponse::ok(anthropic_response())]);
    let config = settings(ProviderType::AnthropicMessages, &server);
    client()
        .execute(
            provider_for(config.provider_type).as_ref(),
            &config,
            "anthropic-local-key",
            "anthropic-mock",
            &TranslationRequest::vision(
                ImageInput::Bytes {
                    media_type: "image/jpeg".to_owned(),
                    data: vec![1, 2, 3],
                },
                LanguagePair::new("auto", "zh-CN"),
            ),
            &CancellationToken::new(),
        )
        .await
        .expect("Anthropic mock response");

    let request = server.requests().join("\n");
    let lowercase = request.to_ascii_lowercase();
    assert!(lowercase.contains("x-api-key: anthropic-local-key"));
    assert!(lowercase.contains("anthropic-version: 2023-06-01"));
    let body: Value = serde_json::from_str(request.split("\r\n\r\n").nth(1).expect("request body"))
        .expect("Anthropic JSON body");
    assert_eq!(body["messages"][0]["content"][0]["type"], "image");
    assert_eq!(
        body["messages"][0]["content"][0]["source"]["type"],
        "base64"
    );
}

#[tokio::test]
async fn gemini_sends_native_key_and_inline_data() {
    let server = MockServer::start(vec![MockResponse::ok(gemini_response())]);
    let config = settings(ProviderType::GeminiGenerateContent, &server);
    client()
        .execute(
            provider_for(config.provider_type).as_ref(),
            &config,
            "gemini-local-key",
            "gemini-mock",
            &TranslationRequest::vision(
                ImageInput::Bytes {
                    media_type: "image/webp".to_owned(),
                    data: vec![1, 2, 3],
                },
                LanguagePair::new("auto", "zh-CN"),
            ),
            &CancellationToken::new(),
        )
        .await
        .expect("Gemini mock response");

    let request = server.requests().join("\n");
    assert!(
        request
            .to_ascii_lowercase()
            .contains("x-goog-api-key: gemini-local-key")
    );
    assert!(request.starts_with("POST /v1beta/models/test-vision-model:generateContent"));
    let body: Value = serde_json::from_str(request.split("\r\n\r\n").nth(1).expect("request body"))
        .expect("Gemini JSON body");
    let inline = &body["contents"][0]["parts"][0]["inline_data"];
    assert_eq!(inline["mime_type"], "image/webp");
    assert_eq!(inline["data"], "AQID");
}

#[tokio::test]
async fn transient_server_error_retries_once_then_succeeds() {
    let server = MockServer::start(vec![
        MockResponse {
            status: 503,
            body: r#"{"error":{"message":"temporary"}}"#.to_owned(),
            delay: Duration::ZERO,
        },
        MockResponse::ok(chat_response()),
    ]);
    let config = settings(ProviderType::OpenAiCompatible, &server);
    let retrying_client = ProviderClient::new(TransportLimits {
        max_retries: 1,
        retry_delay: Duration::from_millis(1),
        ..TransportLimits::default()
    })
    .expect("create retry client");
    let response = retrying_client
        .execute(
            provider_for(config.provider_type).as_ref(),
            &config,
            "local-test-key",
            "retry-mock",
            &TranslationRequest::text("test", LanguagePair::new("auto", "zh-CN")),
            &CancellationToken::new(),
        )
        .await
        .expect("retry succeeds");

    assert_eq!(response.diagnostics.attempts, 2);
    assert_eq!(server.requests().len(), 2);
}

#[tokio::test]
async fn authentication_error_is_classified_without_retry() {
    let server = MockServer::start(vec![MockResponse {
        status: 401,
        body: r#"{"error":{"message":"bad key"}}"#.to_owned(),
        delay: Duration::ZERO,
    }]);
    let config = settings(ProviderType::OpenAiCompatible, &server);
    let error = client()
        .execute(
            provider_for(config.provider_type).as_ref(),
            &config,
            "local-test-key",
            "auth-mock",
            &TranslationRequest::text("test", LanguagePair::new("auto", "zh-CN")),
            &CancellationToken::new(),
        )
        .await
        .expect_err("401 must fail");

    assert_eq!(error.kind, ProviderErrorKind::Authentication);
    assert!(!error.retryable);
    assert_eq!(server.requests().len(), 1);
}

#[tokio::test]
async fn oversized_response_is_rejected_without_unbounded_buffering() {
    let server = MockServer::start(vec![MockResponse::ok("x".repeat(256))]);
    let config = settings(ProviderType::OpenAiCompatible, &server);
    let bounded_client = ProviderClient::new(TransportLimits {
        max_response_bytes: 32,
        max_retries: 0,
        ..TransportLimits::default()
    })
    .expect("create bounded client");
    let error = bounded_client
        .execute(
            provider_for(config.provider_type).as_ref(),
            &config,
            "local-test-key",
            "oversized-mock",
            &TranslationRequest::text("test", LanguagePair::new("auto", "zh-CN")),
            &CancellationToken::new(),
        )
        .await
        .expect_err("oversized response must fail");

    assert_eq!(error.kind, ProviderErrorKind::InvalidResponse);
}

#[tokio::test]
async fn total_timeout_and_cancellation_are_distinct() {
    let timeout_server = MockServer::start(vec![MockResponse {
        status: 200,
        body: chat_response(),
        delay: Duration::from_millis(120),
    }]);
    let timeout_config = settings(ProviderType::OpenAiCompatible, &timeout_server);
    let timeout_client = ProviderClient::new(TransportLimits {
        total_timeout: Duration::from_millis(25),
        max_retries: 0,
        ..TransportLimits::default()
    })
    .expect("create timeout client");
    let timeout_error = timeout_client
        .execute(
            provider_for(timeout_config.provider_type).as_ref(),
            &timeout_config,
            "local-test-key",
            "timeout-mock",
            &TranslationRequest::text("test", LanguagePair::new("auto", "zh-CN")),
            &CancellationToken::new(),
        )
        .await
        .expect_err("request must time out");
    assert_eq!(timeout_error.kind, ProviderErrorKind::Timeout);

    let cancel_server = MockServer::start(vec![MockResponse {
        status: 200,
        body: chat_response(),
        delay: Duration::from_millis(120),
    }]);
    let cancel_config = settings(ProviderType::OpenAiCompatible, &cancel_server);
    let cancellation = CancellationToken::new();
    let cancel_after_start = cancellation.clone();
    tokio::spawn(async move {
        tokio::time::sleep(Duration::from_millis(20)).await;
        cancel_after_start.cancel();
    });
    let cancel_error = client()
        .execute(
            provider_for(cancel_config.provider_type).as_ref(),
            &cancel_config,
            "local-test-key",
            "cancel-mock",
            &TranslationRequest::text("test", LanguagePair::new("auto", "zh-CN")),
            &cancellation,
        )
        .await
        .expect_err("request must be cancelled");
    assert_eq!(cancel_error.kind, ProviderErrorKind::Cancelled);
}

#[allow(dead_code)]
enum SseFlow {
    Complete {
        content_type: &'static str,
        frames: Vec<(Vec<u8>, Duration)>,
    },
    Abrupt {
        content_type: &'static str,
        frames: Vec<(Vec<u8>, Duration)>,
    },
    ImmediateDisconnect,
    RawHttp {
        status: u16,
        body: String,
        delay: Duration,
    },
}

struct SseServer {
    base_url: String,
    requests: Arc<Mutex<Vec<String>>>,
    worker: Option<JoinHandle<()>>,
}

impl SseServer {
    fn start(frames: Vec<(Vec<u8>, Duration)>) -> Self {
        Self::start_with_content_type("text/event-stream; charset=utf-8", frames)
    }

    fn start_with_content_type(
        content_type: &'static str,
        frames: Vec<(Vec<u8>, Duration)>,
    ) -> Self {
        Self::start_multi(vec![SseFlow::Complete {
            content_type,
            frames,
        }])
    }

    fn start_abrupt(frames: Vec<(Vec<u8>, Duration)>) -> Self {
        Self::start_multi(vec![SseFlow::Abrupt {
            content_type: "text/event-stream; charset=utf-8",
            frames,
        }])
    }

    fn start_multi(flows: Vec<SseFlow>) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind SSE server");
        let base_url = format!("http://{}", listener.local_addr().expect("SSE address"));
        let requests = Arc::new(Mutex::new(Vec::new()));
        let captured = Arc::clone(&requests);
        let worker = thread::spawn(move || {
            for flow in flows {
                let Ok((mut stream, _)) = listener.accept() else {
                    break;
                };
                let request = read_request(&mut stream);
                captured.lock().expect("capture request").push(request);
                match flow {
                    SseFlow::Complete {
                        content_type,
                        frames,
                    } => {
                        let header = format!(
                            "HTTP/1.1 200 OK\r\nContent-Type: {content_type}\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
                        );
                        if stream.write_all(header.as_bytes()).is_err() {
                            continue;
                        }
                        for (frame, delay) in frames {
                            if !delay.is_zero() {
                                thread::sleep(delay);
                            }
                            let chunk_header = format!("{:X}\r\n", frame.len());
                            if stream.write_all(chunk_header.as_bytes()).is_err() {
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
                    SseFlow::Abrupt {
                        content_type,
                        frames,
                    } => {
                        let header = format!(
                            "HTTP/1.1 200 OK\r\nContent-Type: {content_type}\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
                        );
                        if stream.write_all(header.as_bytes()).is_err() {
                            continue;
                        }
                        for (frame, delay) in frames {
                            if !delay.is_zero() {
                                thread::sleep(delay);
                            }
                            let chunk_header = format!("{:X}\r\n", frame.len());
                            if stream.write_all(chunk_header.as_bytes()).is_err() {
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
                        // Intentionally drop stream without sending the terminating chunk `0\r\n\r\n`
                    }
                    SseFlow::ImmediateDisconnect => {
                        drop(stream);
                    }
                    SseFlow::RawHttp {
                        status,
                        body,
                        delay,
                    } => {
                        if !delay.is_zero() {
                            thread::sleep(delay);
                        }
                        let reason = if status == 200 { "OK" } else { "Error" };
                        let reply = format!(
                            "HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                            body.len()
                        );
                        let _ = stream.write_all(reply.as_bytes());
                        let _ = stream.flush();
                    }
                }
            }
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

impl Drop for SseServer {
    fn drop(&mut self) {
        if let Some(worker) = self.worker.take() {
            let _ = TcpStream::connect(self.base_url.trim_start_matches("http://"));
            let _ = worker.join();
        }
    }
}

fn sse_settings(provider_type: ProviderType, server: &SseServer) -> ProviderSettings {
    let endpoint = provider_type.default_endpoint().to_owned();
    ProviderSettings {
        provider_type,
        api_base_url: server.base_url.clone(),
        text_endpoint: endpoint.clone(),
        vision_endpoint: endpoint,
        text_model: "stream-model".to_owned(),
        vision_model: "stream-model".to_owned(),
        supports_text: true,
        supports_vision: true,
        network_enabled: true,
        safe_dev_mode: false,
        ..ProviderSettings::default()
    }
}

#[tokio::test]
async fn openai_streaming_protocols_assemble_text_before_final() {
    for provider_type in [
        ProviderType::OpenAiCompatible,
        ProviderType::OpenAiResponses,
    ] {
        let delimiter = "PGMETA_stream_test_0123456789";
        let payload = format!("你好\n{delimiter}\n{{\"explanation\":\"ok\"}}");
        let frames = match provider_type {
            ProviderType::OpenAiCompatible => vec![
                (
                    format!(
                        "data: {{\"choices\":[{{\"delta\":{{\"content\":{}}}}}]}}\n\n",
                        serde_json::to_string(&payload).expect("json string")
                    )
                    .into_bytes(),
                    Duration::ZERO,
                ),
                (b"data: [DONE]\n\n".to_vec(), Duration::from_millis(20)),
            ],
            ProviderType::OpenAiResponses => vec![
                (
                    format!(
                        "event: response.output_text.delta\ndata: {{\"type\":\"response.output_text.delta\",\"delta\":{}}}\n\n",
                        serde_json::to_string(&payload).expect("json string")
                    )
                    .into_bytes(),
                    Duration::ZERO,
                ),
                (
                    b"event: response.completed\ndata: {\"type\":\"response.completed\"}\n\n"
                        .to_vec(),
                    Duration::from_millis(20),
                ),
            ],
            _ => unreachable!(),
        };
        let server = SseServer::start(frames);
        let config = sse_settings(provider_type, &server);
        let mut deltas = Vec::new();
        let response = client()
            .execute_stream(
                provider_for(provider_type).as_ref(),
                &config,
                "stream-key",
                "stream-test",
                &TranslationRequest::text("hello", LanguagePair::new("auto", "zh-CN")),
                Some(delimiter),
                &CancellationToken::new(),
                |delta| deltas.push(delta.to_owned()),
            )
            .await
            .expect("stream response");
        assert_eq!(deltas.concat(), "你好");
        assert_eq!(response.result.translated_text, deltas.concat());
        assert_eq!(response.result.explanation, "ok");
    }
}

#[tokio::test]
async fn openai_json_stream_fallback_parses_text_first_outer_response_once() {
    let delimiter = "PGMETA_json_fallback_012345";
    let payload =
        format!("译文\n{delimiter}\n{{\"explanation\":\"说明\",\"warnings\":[\"模型警告\"]}}");
    for provider_type in [
        ProviderType::OpenAiCompatible,
        ProviderType::OpenAiResponses,
    ] {
        let body = match provider_type {
            ProviderType::OpenAiCompatible => serde_json::json!({
                "choices": [{"message": {"content": payload}}],
            })
            .to_string(),
            ProviderType::OpenAiResponses => serde_json::json!({
                "output": [{"content": [{"type": "output_text", "text": payload}]}],
            })
            .to_string(),
            _ => unreachable!(),
        };
        let server = SseServer::start_multi(vec![SseFlow::RawHttp {
            status: 200,
            body,
            delay: Duration::ZERO,
        }]);
        let config = sse_settings(provider_type, &server);
        let mut deltas = Vec::new();
        let response = client()
            .execute_stream(
                provider_for(provider_type).as_ref(),
                &config,
                "stream-key",
                "json-fallback",
                &TranslationRequest::text("hello", LanguagePair::new("auto", "zh-CN")),
                Some(delimiter),
                &CancellationToken::new(),
                |delta| deltas.push(delta.to_owned()),
            )
            .await
            .expect("JSON fallback response");
        assert_eq!(deltas, vec!["译文"]);
        assert_eq!(response.result.translated_text, "译文");
        assert_eq!(response.result.explanation, "说明");
        assert_eq!(
            response.result.warnings,
            vec!["模型警告", "Provider 未返回 SSE，已回退为非流式响应。"]
        );
        assert!(!response.result.translated_text.contains(delimiter));
        assert_eq!(server.requests().len(), 1);
    }
}

#[tokio::test]
async fn openai_json_stream_fallback_preserves_body_without_or_with_bad_trailer() {
    let delimiter = "PGMETA_json_bad_trailer_01234";
    for payload in [
        "正文".to_owned(),
        format!("正文\n{delimiter}\n{{not-json}}"),
    ] {
        let body = serde_json::json!({
            "choices": [{"message": {"content": payload}}],
        })
        .to_string();
        let server = SseServer::start_multi(vec![SseFlow::RawHttp {
            status: 200,
            body,
            delay: Duration::ZERO,
        }]);
        let config = sse_settings(ProviderType::OpenAiCompatible, &server);
        let response = client()
            .execute_stream(
                provider_for(config.provider_type).as_ref(),
                &config,
                "stream-key",
                "json-fallback-bad-trailer",
                &TranslationRequest::text("hello", LanguagePair::new("auto", "zh-CN")),
                Some(delimiter),
                &CancellationToken::new(),
                |_| {},
            )
            .await
            .expect("JSON fallback response");
        assert_eq!(response.result.translated_text, "正文");
        assert!(!response.result.translated_text.contains(delimiter));
        assert!(response.result.explanation.is_empty());
        assert!(
            response
                .result
                .warnings
                .iter()
                .any(|warning| warning.contains("metadata"))
        );
        assert_eq!(server.requests().len(), 1);
    }
}

async fn collect_stream(
    provider_type: ProviderType,
    server: &SseServer,
    delimiter: &str,
) -> (Vec<String>, popglot_core::provider::TranslationResponse) {
    let config = sse_settings(provider_type, server);
    let mut deltas = Vec::new();
    let response = client()
        .execute_stream(
            provider_for(provider_type).as_ref(),
            &config,
            "stream-key",
            "stream-case",
            &TranslationRequest::text("hello", LanguagePair::new("auto", "zh-CN")),
            Some(delimiter),
            &CancellationToken::new(),
            |delta| deltas.push(delta.to_owned()),
        )
        .await
        .expect("stream response");
    (deltas, response)
}

#[tokio::test]
async fn anthropic_and_gemini_sse_streams_assemble_text_and_metadata() {
    let delimiter = "PGMETA_native_stream_0123456789";
    let payload = format!("你好\n{delimiter}\n{{\"explanation\":\"ok\"}}");
    for (provider_type, frames) in [
        (
            ProviderType::AnthropicMessages,
            vec![
                (b": ping\n\n".to_vec(), Duration::ZERO),
                (
                    format!(
                        "event: content_block_delta\ndata: {{\"type\":\"content_block_delta\",\"delta\":{{\"type\":\"text_delta\",\"text\":{}}}}}\n\n",
                        serde_json::to_string(&payload).expect("JSON string")
                    )
                    .into_bytes(),
                    Duration::ZERO,
                ),
                (
                    b"event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n".to_vec(),
                    Duration::ZERO,
                ),
            ],
        ),
        (
            ProviderType::GeminiGenerateContent,
            vec![(
                format!(
                    "data: {{\"candidates\":[{{\"content\":{{\"parts\":[{{\"text\":{}}}]}},\"finishReason\":\"STOP\"}}]}}\n\n",
                    serde_json::to_string(&payload).expect("JSON string")
                )
                .into_bytes(),
                Duration::ZERO,
            )],
        ),
    ] {
        let server = SseServer::start(frames);
        let (deltas, response) = collect_stream(provider_type, &server, delimiter).await;
        assert_eq!(deltas.concat(), "你好");
        assert_eq!(response.result.translated_text, deltas.concat());
        assert_eq!(response.result.explanation, "ok");
        assert!(!response.result.is_partial);
    }
}

#[tokio::test]
async fn gemini_chunked_stream_with_finish_reason_stop_completes_normally() {
    let delimiter = "PGMETA_gemini_stop_123456789";
    let frames = vec![
        (
            "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"你好，\"}]}}]}\n\n"
                .as_bytes()
                .to_vec(),
            Duration::ZERO,
        ),
        (
            format!(
                "data: {{\"candidates\":[{{\"content\":{{\"parts\":[{{\"text\":\"世界！\\n{delimiter}\\n{{\\\"explanation\\\":\\\"ok\\\"}}\"}}]}},\"finishReason\":\"STOP\"}}]}}\n\n"
            )
            .into_bytes(),
            Duration::ZERO,
        ),
    ];
    let server = SseServer::start(frames);
    let (deltas, response) =
        collect_stream(ProviderType::GeminiGenerateContent, &server, delimiter).await;
    assert_eq!(deltas, vec!["你好，", "世界！"]);
    assert_eq!(response.result.translated_text, "你好，世界！");
    assert_eq!(response.result.explanation, "ok");
    assert!(!response.result.is_partial);
    assert!(
        !response
            .result
            .warnings
            .iter()
            .any(|w| w.contains("正常结束；译文可能不完整"))
    );
}

#[tokio::test]
async fn gemini_chunked_stream_without_stop_on_clean_eof_returns_partial_and_warning() {
    let delimiter = "PGMETA_gemini_nostop_12345678";
    let frames = vec![
        (
            "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"未完成的\"}]}}]}\n\n"
                .as_bytes()
                .to_vec(),
            Duration::ZERO,
        ),
        (
            "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"译文\"}]}}]}\n\n"
                .as_bytes()
                .to_vec(),
            Duration::ZERO,
        ),
    ];
    let server = SseServer::start(frames);
    let (deltas, response) =
        collect_stream(ProviderType::GeminiGenerateContent, &server, delimiter).await;
    assert_eq!(deltas, vec!["未完成的", "译文"]);
    assert_eq!(response.result.translated_text, "未完成的译文");
    assert!(response.result.is_partial);
    assert!(
        response
            .result
            .warnings
            .iter()
            .any(|w| w.contains("SSE 流在协议完成事件前正常结束；译文可能不完整。"))
    );
}

#[tokio::test]
async fn chat_in_stream_error_fails_immediately_without_completed() {
    let chat_server = SseServer::start(vec![
        (
            "data: {\"choices\":[{\"delta\":{\"content\":\"部分译文\"}}]}\n\n"
                .as_bytes()
                .to_vec(),
            Duration::ZERO,
        ),
        (
            "data: {\"error\":{\"message\":\"chat completion model overloaded\",\"type\":\"server_error\"}}\n\n"
                .as_bytes()
                .to_vec(),
            Duration::ZERO,
        ),
    ]);
    let chat_config = sse_settings(ProviderType::OpenAiCompatible, &chat_server);
    let mut chat_deltas = Vec::new();
    let chat_error = client()
        .execute_stream(
            provider_for(chat_config.provider_type).as_ref(),
            &chat_config,
            "stream-key",
            "chat-stream-error",
            &TranslationRequest::text("hello", LanguagePair::new("auto", "zh-CN")),
            Some("PGMETA_chat_error_0123456789"),
            &CancellationToken::new(),
            |delta| chat_deltas.push(delta.to_owned()),
        )
        .await
        .expect_err("chat in-stream error must immediately fail without returning success");
    assert_eq!(chat_error.kind, ProviderErrorKind::InvalidResponse);
    assert!(
        chat_error
            .message
            .contains("chat completion model overloaded")
    );
    assert_eq!(chat_deltas, vec!["部分译文"]);
}

#[tokio::test]
async fn responses_in_stream_error_fails_immediately_without_completed() {
    let responses_server = SseServer::start(vec![
        (
            "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"响应部分\"}\n\n"
                .as_bytes()
                .to_vec(),
            Duration::ZERO,
        ),
        (
            "event: response.failed\ndata: {\"type\":\"response.failed\",\"error\":{\"message\":\"responses quota exhausted\"}}\n\n"
                .as_bytes()
                .to_vec(),
            Duration::ZERO,
        ),
    ]);
    let responses_config = sse_settings(ProviderType::OpenAiResponses, &responses_server);
    let mut responses_deltas = Vec::new();
    let responses_error = client()
        .execute_stream(
            provider_for(responses_config.provider_type).as_ref(),
            &responses_config,
            "stream-key",
            "responses-stream-error",
            &TranslationRequest::text("hello", LanguagePair::new("auto", "zh-CN")),
            Some("PGMETA_responses_error_012345"),
            &CancellationToken::new(),
            |delta| responses_deltas.push(delta.to_owned()),
        )
        .await
        .expect_err("responses in-stream error must immediately fail without returning success");
    assert_eq!(responses_error.kind, ProviderErrorKind::InvalidResponse);
    assert!(
        responses_error
            .message
            .contains("responses quota exhausted")
    );
    assert_eq!(responses_deltas, vec!["响应部分"]);
}

#[tokio::test]
async fn anthropic_in_stream_error_fails_immediately_without_completed() {
    let anthropic_server = SseServer::start(vec![
        (
            "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Claude译文\"}}\n\n"
                .as_bytes()
                .to_vec(),
            Duration::ZERO,
        ),
        (
            "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"anthropic service overloaded\"}}\n\n"
                .as_bytes()
                .to_vec(),
            Duration::ZERO,
        ),
    ]);
    let anthropic_config = sse_settings(ProviderType::AnthropicMessages, &anthropic_server);
    let mut anthropic_deltas = Vec::new();
    let anthropic_error = client()
        .execute_stream(
            provider_for(anthropic_config.provider_type).as_ref(),
            &anthropic_config,
            "stream-key",
            "anthropic-stream-error",
            &TranslationRequest::text("hello", LanguagePair::new("auto", "zh-CN")),
            Some("PGMETA_anthropic_error_012345"),
            &CancellationToken::new(),
            |delta| anthropic_deltas.push(delta.to_owned()),
        )
        .await
        .expect_err("anthropic in-stream error must fail");
    assert_eq!(anthropic_error.kind, ProviderErrorKind::InvalidResponse);
    assert!(
        anthropic_error
            .message
            .contains("anthropic service overloaded")
    );
    assert_eq!(anthropic_deltas, vec!["Claude译文"]);
}

#[tokio::test]
async fn disconnect_after_first_delta_does_not_retry_or_fallback() {
    let server = SseServer::start_abrupt(vec![(
        "data: {\"choices\":[{\"delta\":{\"content\":\"正文\"}}]}\n\n"
            .as_bytes()
            .to_vec(),
        Duration::ZERO,
    )]);
    let config = sse_settings(ProviderType::OpenAiCompatible, &server);
    let retrying = ProviderClient::new(TransportLimits {
        max_retries: 2,
        retry_delay: Duration::from_millis(1),
        ..TransportLimits::default()
    })
    .expect("retrying client");
    let mut deltas = Vec::new();
    let error = retrying
        .execute_stream(
            provider_for(config.provider_type).as_ref(),
            &config,
            "stream-key",
            "no-retry-after-delta",
            &TranslationRequest::text("hello", LanguagePair::new("auto", "zh-CN")),
            Some("PGMETA_disconnect_case_012345"),
            &CancellationToken::new(),
            |delta| deltas.push(delta.to_owned()),
        )
        .await
        .expect_err("abrupt stream must fail after its first delta");
    assert_eq!(deltas, vec!["正文"]);
    assert_eq!(deltas.concat(), "正文");
    assert_eq!(error.kind, ProviderErrorKind::Transport);
    assert_eq!(server.requests().len(), 1);
}

#[tokio::test]
async fn zero_delta_transport_failure_retries_and_succeeds() {
    let delimiter = "PGMETA_zero_delta_retry_012345";
    let payload = format!("重试成功\n{delimiter}\n{{\"explanation\":\"ok\"}}");
    let server = SseServer::start_multi(vec![
        // Attempt 1: Server accepts connection, but abruptly closes without sending any delta/frame
        SseFlow::Abrupt {
            content_type: "text/event-stream; charset=utf-8",
            frames: vec![],
        },
        // Attempt 2: Server sends valid SSE frames and finishes chunked encoding
        SseFlow::Complete {
            content_type: "text/event-stream; charset=utf-8",
            frames: vec![
                (
                    format!(
                        "data: {{\"choices\":[{{\"delta\":{{\"content\":{}}}}}]}}\n\n",
                        serde_json::to_string(&payload).expect("json string")
                    )
                    .into_bytes(),
                    Duration::ZERO,
                ),
                (b"data: [DONE]\n\n".to_vec(), Duration::ZERO),
            ],
        },
    ]);
    let config = sse_settings(ProviderType::OpenAiCompatible, &server);
    let retrying_client = ProviderClient::new(TransportLimits {
        max_retries: 1,
        retry_delay: Duration::from_millis(1),
        ..TransportLimits::default()
    })
    .expect("create retry client");
    let mut deltas = Vec::new();
    let response = retrying_client
        .execute_stream(
            provider_for(config.provider_type).as_ref(),
            &config,
            "stream-key",
            "zero-delta-retry",
            &TranslationRequest::text("hello", LanguagePair::new("auto", "zh-CN")),
            Some(delimiter),
            &CancellationToken::new(),
            |delta| deltas.push(delta.to_owned()),
        )
        .await
        .expect("retry after zero-delta failure must succeed");

    assert_eq!(response.diagnostics.attempts, 2);
    assert_eq!(server.requests().len(), 2);
    assert_eq!(deltas.concat(), "重试成功");
    assert_eq!(response.result.translated_text, "重试成功");
    assert_eq!(response.result.explanation, "ok");
    assert!(!response.result.is_partial);
}

#[tokio::test]
async fn cancellation_during_slow_ttft_and_mid_stream_returns_cancelled_without_subsequent_deltas()
{
    // 1. Cancellation during slow first frame (slow TTFT)
    let slow_ttft_server = SseServer::start(vec![(
        "data: {\"choices\":[{\"delta\":{\"content\":\"延迟译文\"}}]}\n\n"
            .as_bytes()
            .to_vec(),
        Duration::from_millis(200),
    )]);
    let slow_config = sse_settings(ProviderType::OpenAiCompatible, &slow_ttft_server);
    let cancellation_slow = CancellationToken::new();
    let cancel_slow_clone = cancellation_slow.clone();
    tokio::spawn(async move {
        tokio::time::sleep(Duration::from_millis(20)).await;
        cancel_slow_clone.cancel();
    });
    let mut slow_deltas = Vec::new();
    let slow_error = client()
        .execute_stream(
            provider_for(slow_config.provider_type).as_ref(),
            &slow_config,
            "stream-key",
            "slow-ttft-cancel",
            &TranslationRequest::text("hello", LanguagePair::new("auto", "zh-CN")),
            Some("PGMETA_cancel_ttft_012345"),
            &cancellation_slow,
            |delta| slow_deltas.push(delta.to_owned()),
        )
        .await
        .expect_err("slow TTFT cancellation must return Cancelled error");
    assert_eq!(slow_error.kind, ProviderErrorKind::Cancelled);
    assert!(slow_deltas.is_empty());

    // 2. Cancellation mid-stream: callback triggers cancellation upon receiving first delta
    let mid_stream_server = SseServer::start(vec![
        (
            "data: {\"choices\":[{\"delta\":{\"content\":\"第一段\"}}]}\n\n"
                .as_bytes()
                .to_vec(),
            Duration::ZERO,
        ),
        (
            "data: {\"choices\":[{\"delta\":{\"content\":\"第二段\"}}]}\n\n"
                .as_bytes()
                .to_vec(),
            Duration::from_millis(200),
        ),
        (
            "data: {\"choices\":[{\"delta\":{\"content\":\"第三段\"}}]}\n\n"
                .as_bytes()
                .to_vec(),
            Duration::ZERO,
        ),
        (b"data: [DONE]\n\n".to_vec(), Duration::ZERO),
    ]);
    let mid_config = sse_settings(ProviderType::OpenAiCompatible, &mid_stream_server);
    let cancellation_mid = CancellationToken::new();
    let cancel_mid_trigger = cancellation_mid.clone();
    let mut mid_deltas = Vec::new();
    let mid_error = client()
        .execute_stream(
            provider_for(mid_config.provider_type).as_ref(),
            &mid_config,
            "stream-key",
            "mid-stream-cancel",
            &TranslationRequest::text("hello", LanguagePair::new("auto", "zh-CN")),
            Some("PGMETA_cancel_mid_0123456"),
            &cancellation_mid,
            |delta| {
                mid_deltas.push(delta.to_owned());
                cancel_mid_trigger.cancel();
            },
        )
        .await
        .expect_err("mid-stream cancellation must return Cancelled error");
    assert_eq!(mid_error.kind, ProviderErrorKind::Cancelled);
    assert_eq!(mid_deltas, vec!["第一段"]);
}

#[tokio::test]
async fn clean_eof_without_completion_event_yields_partial_warning() {
    // 1. OpenAI Compatible (Chat) normal chunked EOF without [DONE] or finish_reason
    let chat_server = SseServer::start(vec![(
        "data: {\"choices\":[{\"delta\":{\"content\":\"未完正文\"}}]}\n\n"
            .as_bytes()
            .to_vec(),
        Duration::ZERO,
    )]);
    let (chat_deltas, chat_response) = collect_stream(
        ProviderType::OpenAiCompatible,
        &chat_server,
        "PGMETA_eof_chat_0123456789",
    )
    .await;
    assert_eq!(chat_deltas.concat(), "未完正文");
    assert_eq!(chat_response.result.translated_text, "未完正文");
    assert!(chat_response.result.is_partial);
    assert!(
        chat_response
            .result
            .warnings
            .iter()
            .any(|w| w.contains("SSE 流在协议完成事件前正常结束；译文可能不完整。"))
    );

    // 2. OpenAI Responses normal chunked EOF without response.completed
    let responses_server = SseServer::start(vec![(
        "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"响应未完\"}\n\n"
            .as_bytes()
            .to_vec(),
        Duration::ZERO,
    )]);
    let (responses_deltas, responses_response) = collect_stream(
        ProviderType::OpenAiResponses,
        &responses_server,
        "PGMETA_eof_responses_01234",
    )
    .await;
    assert_eq!(responses_deltas.concat(), "响应未完");
    assert_eq!(responses_response.result.translated_text, "响应未完");
    assert!(responses_response.result.is_partial);
    assert!(
        responses_response
            .result
            .warnings
            .iter()
            .any(|w| w.contains("SSE 流在协议完成事件前正常结束；译文可能不完整。"))
    );

    // 3. Anthropic normal chunked EOF without message_stop
    let anthropic_server = SseServer::start(vec![(
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Claude未完\"}}\n\n"
            .as_bytes()
            .to_vec(),
        Duration::ZERO,
    )]);
    let (anthropic_deltas, anthropic_response) = collect_stream(
        ProviderType::AnthropicMessages,
        &anthropic_server,
        "PGMETA_eof_anthropic_01234",
    )
    .await;
    assert_eq!(anthropic_deltas.concat(), "Claude未完");
    assert_eq!(anthropic_response.result.translated_text, "Claude未完");
    assert!(anthropic_response.result.is_partial);
    assert!(
        anthropic_response
            .result
            .warnings
            .iter()
            .any(|w| w.contains("SSE 流在协议完成事件前正常结束；译文可能不完整。"))
    );
}

#[tokio::test]
async fn chat_sse_handles_utf8_split_multiple_events_and_heartbeat() {
    let delimiter = "PGMETA_utf8_split_0123456789";
    let payload = format!("你好\n{delimiter}\n{{\"warnings\":[\"mock warning\"]}}");
    let wire = format!(
        "data: {{\"choices\":[{{\"delta\":{{\"content\":{}}}}}]}}\n\n",
        serde_json::to_string(&payload).expect("JSON string")
    )
    .into_bytes();

    // Split in the middle of UTF-8 character '你' (3 bytes: [0xE4, 0xBD, 0xA0])
    let split = wire
        .windows("你".len())
        .position(|bytes| bytes == "你".as_bytes())
        .expect("UTF-8 text")
        + 1;

    // Chunk 1: wire up to the middle of '你'
    let chunk1 = wire[..split].to_vec();

    // Chunk 2: rest of wire including heartbeat and [DONE]
    let mut chunk2 = Vec::from(&wire[split..]);
    chunk2.extend_from_slice(b": heartbeat\n\ndata: [DONE]\n\n");

    let server = SseServer::start(vec![(chunk1, Duration::ZERO), (chunk2, Duration::ZERO)]);
    let (deltas, response) =
        collect_stream(ProviderType::OpenAiCompatible, &server, delimiter).await;
    assert_eq!(deltas.concat(), "你好");
    assert_eq!(response.result.translated_text, deltas.concat());
    assert_eq!(response.result.warnings, vec!["mock warning"]);
    assert!(!response.result.is_partial);
}

#[tokio::test]
async fn missing_or_malformed_trailer_preserves_visible_body_with_warning() {
    for payload in ["正文", "正文\nPGMETA_trailer_case_0123456789\n{not-json}"] {
        let server = SseServer::start(vec![
            (
                format!(
                    "data: {{\"choices\":[{{\"delta\":{{\"content\":{}}}}}]}}\n\n",
                    serde_json::to_string(payload).expect("JSON string")
                )
                .into_bytes(),
                Duration::ZERO,
            ),
            (b"data: [DONE]\n\n".to_vec(), Duration::ZERO),
        ]);
        let (deltas, response) = collect_stream(
            ProviderType::OpenAiCompatible,
            &server,
            "PGMETA_trailer_case_0123456789",
        )
        .await;
        assert_eq!(deltas.concat(), "正文");
        assert_eq!(response.result.translated_text, deltas.concat());
        assert!(!response.result.warnings.is_empty());
    }
}

#[tokio::test]
async fn gemini_stream_endpoint_query_merges_alt_sse_and_keeps_existing_query() {
    let delimiter = "PGMETA_gemini_query_01234567";
    let payload = format!("双语\n{delimiter}\n{{\"explanation\":\"说明\"}}");
    let server = SseServer::start(vec![(
        format!(
            "data: {{\"candidates\":[{{\"content\":{{\"parts\":[{{\"text\":{}}}]}},\"finishReason\":\"STOP\"}}]}}\n\n",
            serde_json::to_string(&payload).expect("JSON string")
        )
        .into_bytes(),
        Duration::ZERO,
    )]);
    let mut config = sse_settings(ProviderType::GeminiGenerateContent, &server);
    config.text_endpoint =
        "/v1beta/models/{model}:generateContent?key=custom-key&alt=json".to_owned();

    let mut deltas = Vec::new();
    let response = client()
        .execute_stream(
            provider_for(config.provider_type).as_ref(),
            &config,
            "gemini-key",
            "gemini-query-test",
            &TranslationRequest::text("hello", LanguagePair::new("auto", "zh-CN")),
            Some(delimiter),
            &CancellationToken::new(),
            |delta| deltas.push(delta.to_owned()),
        )
        .await
        .expect("stream response");

    assert_eq!(deltas.concat(), "双语");
    assert_eq!(response.result.translated_text, "双语");
    assert_eq!(response.result.explanation, "说明");
    let request = server.requests().join("\n");
    assert!(
        request.contains(
            "POST /v1beta/models/stream-model:streamGenerateContent?key=custom-key&alt=sse"
        ),
        "request was: {request}"
    );
}

#[tokio::test]
async fn gemini_json_stream_fallback_parses_text_first_outer_response_without_delimiter_leak() {
    let delimiter = "PGMETA_gemini_fallback_0123";
    let payload = format!(
        "降级译文\n{delimiter}\n{{\"explanation\":\"降级说明\",\"warnings\":[\"降级警告\"]}}"
    );
    let body = serde_json::json!({
        "candidates": [{
            "content": {
                "parts": [{"text": payload}]
            },
            "finishReason": "STOP"
        }]
    })
    .to_string();

    let server = SseServer::start_multi(vec![SseFlow::RawHttp {
        status: 200,
        body,
        delay: Duration::ZERO,
    }]);
    let config = sse_settings(ProviderType::GeminiGenerateContent, &server);
    let mut deltas = Vec::new();
    let response = client()
        .execute_stream(
            provider_for(config.provider_type).as_ref(),
            &config,
            "gemini-key",
            "gemini-fallback",
            &TranslationRequest::text("hello", LanguagePair::new("auto", "zh-CN")),
            Some(delimiter),
            &CancellationToken::new(),
            |delta| deltas.push(delta.to_owned()),
        )
        .await
        .expect("fallback response");

    assert_eq!(deltas, vec!["降级译文"]);
    assert_eq!(response.result.translated_text, "降级译文");
    assert_eq!(response.result.explanation, "降级说明");
    assert_eq!(
        response.result.warnings,
        vec!["降级警告", "Provider 未返回 SSE，已回退为非流式响应。"]
    );
    assert!(!response.result.translated_text.contains(delimiter));
}

#[tokio::test]
async fn gemini_stream_safety_block_returns_safety_blocked_error() {
    // 1. promptFeedback safety block
    let prompt_block_server = SseServer::start(vec![(
        "data: {\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}\n\n"
            .as_bytes()
            .to_vec(),
        Duration::ZERO,
    )]);
    let prompt_config = sse_settings(ProviderType::GeminiGenerateContent, &prompt_block_server);
    let err1 = client()
        .execute_stream(
            provider_for(prompt_config.provider_type).as_ref(),
            &prompt_config,
            "gemini-key",
            "prompt-block-test",
            &TranslationRequest::text("hello", LanguagePair::new("auto", "zh-CN")),
            Some("PGMETA_safety_block_012345"),
            &CancellationToken::new(),
            |_| {},
        )
        .await
        .expect_err("promptFeedback block must fail");
    assert_eq!(err1.kind, ProviderErrorKind::SafetyBlocked);

    // 2. candidate finishReason safety block
    let candidate_block_server = SseServer::start(vec![(
        "data: {\"candidates\":[{\"finishReason\":\"SAFETY\"}]}\n\n"
            .as_bytes()
            .to_vec(),
        Duration::ZERO,
    )]);
    let candidate_config =
        sse_settings(ProviderType::GeminiGenerateContent, &candidate_block_server);
    let err2 = client()
        .execute_stream(
            provider_for(candidate_config.provider_type).as_ref(),
            &candidate_config,
            "gemini-key",
            "candidate-block-test",
            &TranslationRequest::text("hello", LanguagePair::new("auto", "zh-CN")),
            Some("PGMETA_safety_block_012345"),
            &CancellationToken::new(),
            |_| {},
        )
        .await
        .expect_err("candidate finishReason block must fail");
    assert_eq!(err2.kind, ProviderErrorKind::SafetyBlocked);
}

#[tokio::test]
async fn gemini_illegal_endpoint_fails_with_configuration_error() {
    let server = SseServer::start_multi(vec![]);
    let mut config = sse_settings(ProviderType::GeminiGenerateContent, &server);
    config.text_endpoint = "/v1beta/models/{model}:unknownMethod".to_owned();

    let err = client()
        .execute_stream(
            provider_for(config.provider_type).as_ref(),
            &config,
            "gemini-key",
            "illegal-endpoint-test",
            &TranslationRequest::text("hello", LanguagePair::new("auto", "zh-CN")),
            Some("PGMETA_illegal_endpoint_0123"),
            &CancellationToken::new(),
            |_| {},
        )
        .await
        .expect_err("illegal endpoint must fail with configuration error");
    assert_eq!(err.kind, ProviderErrorKind::Configuration);
    assert_eq!(server.requests().len(), 0);
}
