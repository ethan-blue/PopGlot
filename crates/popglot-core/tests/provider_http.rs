use popglot_core::provider::{
    ImageInput, ProviderClient, ProviderErrorKind, TranslationInput, TransportLimits, provider_for,
};
use popglot_domain::{ProviderSettings, ProviderType};
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
                let (mut stream, _) = listener.accept().expect("accept local mock request");
                let request = read_request(&mut stream);
                captured.lock().expect("capture request").push(request);
                thread::sleep(response.delay);
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
            worker.join().expect("join local mock server");
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
        let count = stream.read(&mut buffer).expect("read mock request");
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
    String::from_utf8(request).expect("mock request is UTF-8")
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
            &TranslationInput::Vision {
                prompt: "translate".to_owned(),
                image: ImageInput::Bytes {
                    media_type: "image/png".to_owned(),
                    data: vec![1, 2, 3],
                },
            },
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
            &TranslationInput::Vision {
                prompt: "translate".to_owned(),
                image: ImageInput::Url("https://example.invalid/image.png".to_owned()),
            },
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
            &TranslationInput::Vision {
                prompt: "translate".to_owned(),
                image: ImageInput::Bytes {
                    media_type: "image/jpeg".to_owned(),
                    data: vec![1, 2, 3],
                },
            },
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
            &TranslationInput::Vision {
                prompt: "translate".to_owned(),
                image: ImageInput::Bytes {
                    media_type: "image/webp".to_owned(),
                    data: vec![1, 2, 3],
                },
            },
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
            &TranslationInput::Text {
                source: "test".to_owned(),
            },
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
            &TranslationInput::Text {
                source: "test".to_owned(),
            },
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
            &TranslationInput::Text {
                source: "test".to_owned(),
            },
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
            &TranslationInput::Text {
                source: "test".to_owned(),
            },
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
            &TranslationInput::Text {
                source: "test".to_owned(),
            },
            &cancellation,
        )
        .await
        .expect_err("request must be cancelled");
    assert_eq!(cancel_error.kind, ProviderErrorKind::Cancelled);
}
