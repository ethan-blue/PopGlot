//! Offline, reproducible streaming performance and delay tolerance benchmark tool.
//!
//! Measures end-to-end streaming assembly, SSE decoding, TTFT, chunk intervals,
//! UTF-8 boundary resilience, and trailer recovery over local loopback / in-memory.
//!
//! # Usage
//!
//! ```sh
//! # Run default benchmark (realistic SSE stream over loopback HTTP)
//! cargo run -p popglot-core --bin stream_benchmark --
//!
//! # Run with specific scenario, provider, iterations and delay parameters
//! cargo run -p popglot-core --bin stream_benchmark -- --scenario split-utf8 --provider anthropic --iterations 20 --ttft-ms 25 --chunk-interval-ms 5
//!
//! # Run all scenarios and validate delay tolerances
//! cargo run -p popglot-core --bin stream_benchmark -- --scenario all --validate --tolerance-ms 40
//! ```

#![allow(
    clippy::cast_precision_loss,
    clippy::cast_possible_truncation,
    clippy::cast_sign_loss,
    clippy::too_many_lines,
    clippy::missing_panics_doc
)]

use popglot_core::STREAM_PROMPT_VERSION;
use popglot_core::provider::{
    ProviderClient, ProviderStreamEvent, TranslationRequest, TransportLimits, provider_for,
};
use popglot_core::sse::SseDecoder;
use popglot_core::streaming::TextFirstAssembler;
use popglot_domain::{LanguagePair, ProviderSettings, ProviderType};
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};
use tokio_util::sync::CancellationToken;

const DEFAULT_BENCHMARK_TEXT: &str = "PopGlot 是一个跨平台划词翻译工具。\nSupports streaming SSE tokens: 🚀 high throughput, multi-byte UTF-8 (如「你好世界」，Emojis 🎉), and zero-loss delimiter assembly.\n```rust\nfn stream() -> Result<()> { Ok(()) }\n```\n快速、精准、原生体验！";

const FIXED_DELIMITER: &str = "PGMETA_bench_delimiter_0123456789";

/// Deterministic, zero-dependency pseudo-random number generator for reproducible jitter and slicing.
#[derive(Debug, Clone)]
pub struct SimpleRng {
    state: u64,
}

impl SimpleRng {
    #[must_use]
    pub fn new(seed: u64) -> Self {
        Self {
            state: if seed == 0 {
                0x853c_49e6_748f_ea9b
            } else {
                seed
            },
        }
    }

    pub fn next_u64(&mut self) -> u64 {
        self.state ^= self.state << 13;
        self.state ^= self.state >> 7;
        self.state ^= self.state << 17;
        self.state
    }

    pub fn gen_range(&mut self, min: u64, max: u64) -> u64 {
        if min >= max {
            return min;
        }
        min + (self.next_u64() % (max - min + 1))
    }
}

/// Supported benchmark scenarios.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum BenchmarkScenario {
    /// Realistic LLM streaming response with initial TTFT, steady chunk intervals, and JSON metadata trailer.
    RealisticStream,
    /// SSE frames deliberately cut across multi-byte UTF-8 boundaries to verify decoder and assembler resilience.
    SplitUtf8,
    /// Chunks delivered with deterministic inter-chunk delay jitter using a fixed random seed.
    Jitter,
    /// Stream terminates without delimiter or JSON trailer; tests flush-on-EOF fallback and warning generation.
    MissingTrailer,
    /// Stream contains corrupted SSE chunks; verifies error handling and error count recording.
    CorruptedSse,
    /// Direct in-memory parsing without loopback TCP sockets; measures raw assembler / decoder CPU throughput.
    DirectAssembler,
}

impl BenchmarkScenario {
    #[must_use]
    pub fn parse(s: &str) -> Option<Self> {
        match s.to_ascii_lowercase().replace('_', "-").as_str() {
            "realistic" | "realistic-stream" | "default" => Some(Self::RealisticStream),
            "split-utf8" | "split" => Some(Self::SplitUtf8),
            "jitter" => Some(Self::Jitter),
            "missing-trailer" | "missing" | "no-trailer" => Some(Self::MissingTrailer),
            "corrupted-sse" | "corrupted" | "bad-sse" => Some(Self::CorruptedSse),
            "direct-assembler" | "direct" | "in-memory" => Some(Self::DirectAssembler),
            _ => None,
        }
    }

    #[must_use]
    pub fn as_str(self) -> &'static str {
        match self {
            Self::RealisticStream => "realistic_stream",
            Self::SplitUtf8 => "split_utf8",
            Self::Jitter => "jitter",
            Self::MissingTrailer => "missing_trailer",
            Self::CorruptedSse => "corrupted_sse",
            Self::DirectAssembler => "direct_assembler",
        }
    }

    #[must_use]
    pub fn all() -> &'static [Self] {
        &[
            Self::RealisticStream,
            Self::SplitUtf8,
            Self::Jitter,
            Self::MissingTrailer,
            Self::CorruptedSse,
            Self::DirectAssembler,
        ]
    }
}

/// Configuration options for the streaming benchmark.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BenchmarkConfig {
    pub scenario: BenchmarkScenario,
    pub provider_type: ProviderType,
    pub iterations: usize,
    pub warmup_iterations: usize,
    pub injected_ttft_ms: u64,
    pub injected_chunk_interval_ms: u64,
    pub chunk_count: usize,
    pub seed: u64,
    pub custom_text: Option<String>,
    pub tolerance_ms: u64,
    pub validate: bool,
}

impl Default for BenchmarkConfig {
    fn default() -> Self {
        Self {
            scenario: BenchmarkScenario::RealisticStream,
            provider_type: ProviderType::OpenAiCompatible,
            iterations: 10,
            warmup_iterations: 2,
            injected_ttft_ms: 30,
            injected_chunk_interval_ms: 5,
            chunk_count: 10,
            seed: 42,
            custom_text: None,
            tolerance_ms: 35,
            validate: false,
        }
    }
}

/// Injected benchmark parameters summary in JSON.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SummaryConfig {
    pub injected_ttft_ms: u64,
    pub injected_chunk_interval_ms: u64,
    pub chunk_count: usize,
    #[serde(alias = "total_chars")]
    pub configured_chars: usize,
    pub seed: u64,
    pub offline: bool,
}

/// Percentile and statistical distribution of timings in milliseconds.
/// All fields are 0.0 when there are no valid/successful samples.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TimingStats {
    pub min_ms: f64,
    pub p50_ms: f64,
    pub p95_ms: f64,
    pub max_ms: f64,
    pub avg_ms: f64,
}

/// Percentile and statistical distribution of counts.
/// All fields are 0 when there are no valid/successful samples.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CountStats {
    pub min: usize,
    pub p50: usize,
    pub p95: usize,
    pub max: usize,
    pub avg: f64,
}

/// Delay tolerance validation against local injected timings.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ToleranceCheck {
    pub tolerance_ms: u64,
    pub passed: bool,
    #[serde(default)]
    pub expected_failure: bool,
    pub ttft_overhead_ms: f64,
    pub stream_overhead_ms: f64,
    pub note: String,
}

/// Complete benchmark JSON summary.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BenchmarkSummary {
    pub prompt_version: String,
    pub scenario: String,
    pub provider: String,
    pub iterations: usize,
    pub warmup_iterations: usize,
    pub config: SummaryConfig,
    pub ttft_ms: TimingStats,
    pub stream_ms: TimingStats,
    pub total_ms: TimingStats,
    pub chars_per_sec: f64,
    pub delta_count: CountStats,
    pub total_chars: usize,
    pub flush_simulated: bool,
    pub parse_errors: usize,
    pub warnings_count: usize,
    pub success_rate: f64,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub tolerance_check: Option<ToleranceCheck>,
}

/// Multi-scenario benchmark report container.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MultiBenchmarkReport {
    pub prompt_version: String,
    pub scenarios: Vec<BenchmarkSummary>,
    pub overall_passed: bool,
}

/// Split UTF-8 text into `count` roughly equal string chunks along char boundaries.
#[must_use]
pub fn split_text_into_chunks(text: &str, count: usize) -> Vec<String> {
    if text.is_empty() || count == 0 {
        return vec![text.to_owned()];
    }
    let chars: Vec<char> = text.chars().collect();
    if chars.len() <= count {
        return chars.into_iter().map(|c| c.to_string()).collect();
    }
    let chunk_size = chars.len().div_ceil(count);
    chars
        .chunks(chunk_size)
        .map(|chunk| chunk.iter().collect::<String>())
        .collect()
}

/// Constructs provider-specific SSE frames for the specified benchmark scenario.
#[must_use]
pub fn build_mock_frames(
    provider_type: ProviderType,
    text: &str,
    delimiter: &str,
    config: &BenchmarkConfig,
    rng: &mut SimpleRng,
) -> Vec<(Vec<u8>, Duration)> {
    let chunks = split_text_into_chunks(text, config.chunk_count);
    let ttft_delay = Duration::from_millis(config.injected_ttft_ms);
    let base_interval = config.injected_chunk_interval_ms;

    let mut frames = Vec::new();

    let compute_interval = |idx: usize, rng: &mut SimpleRng| -> Duration {
        if idx == 0 {
            ttft_delay
        } else if config.scenario == BenchmarkScenario::Jitter && base_interval > 0 {
            let max_jitter = (base_interval / 2).max(1);
            let jitter = rng.gen_range(0, max_jitter);
            let delay_ms = if rng.next_u64().is_multiple_of(2) {
                base_interval.saturating_add(jitter)
            } else {
                base_interval.saturating_sub(jitter)
            };
            Duration::from_millis(delay_ms)
        } else {
            Duration::from_millis(base_interval)
        }
    };

    match provider_type {
        ProviderType::OpenAiCompatible => {
            for (idx, chunk) in chunks.iter().enumerate() {
                let delay = compute_interval(idx, rng);
                let payload = json!({
                    "choices": [{
                        "delta": {
                            "content": chunk
                        }
                    }]
                });
                frames.push((format!("data: {payload}\n\n").into_bytes(), delay));
            }

            if config.scenario != BenchmarkScenario::MissingTrailer {
                let trailer_text = format!(
                    "\n{delimiter}\n{{\"explanation\":\"benchmark trailer\",\"warnings\":[]}}"
                );
                let payload = json!({
                    "choices": [{
                        "delta": {
                            "content": trailer_text
                        }
                    }]
                });
                let delay = Duration::from_millis(base_interval);
                frames.push((format!("data: {payload}\n\n").into_bytes(), delay));
            }

            if config.scenario == BenchmarkScenario::CorruptedSse {
                frames.push((
                    b"data: {invalid-json-sse-line-for-bench}\n\n".to_vec(),
                    Duration::from_millis(base_interval),
                ));
            }

            frames.push((b"data: [DONE]\n\n".to_vec(), Duration::ZERO));
        }

        ProviderType::AnthropicMessages => {
            frames.push((
                b"event: message_start\ndata: {\"type\":\"message_start\"}\n\n".to_vec(),
                ttft_delay,
            ));
            frames.push((
                b"event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n".to_vec(),
                Duration::ZERO,
            ));

            for (idx, chunk) in chunks.iter().enumerate() {
                let delay = if idx == 0 {
                    Duration::ZERO
                } else {
                    compute_interval(idx, rng)
                };
                let payload = json!({
                    "type": "content_block_delta",
                    "index": 0,
                    "delta": {
                        "type": "text_delta",
                        "text": chunk
                    }
                });
                frames.push((
                    format!("event: content_block_delta\ndata: {payload}\n\n").into_bytes(),
                    delay,
                ));
            }

            if config.scenario != BenchmarkScenario::MissingTrailer {
                let trailer_text = format!(
                    "\n{delimiter}\n{{\"explanation\":\"benchmark trailer\",\"warnings\":[]}}"
                );
                let payload = json!({
                    "type": "content_block_delta",
                    "index": 0,
                    "delta": {
                        "type": "text_delta",
                        "text": trailer_text
                    }
                });
                let delay = Duration::from_millis(base_interval);
                frames.push((
                    format!("event: content_block_delta\ndata: {payload}\n\n").into_bytes(),
                    delay,
                ));
            }

            if config.scenario == BenchmarkScenario::CorruptedSse {
                frames.push((
                    b"event: content_block_delta\ndata: {corrupted-anthropic}\n\n".to_vec(),
                    Duration::from_millis(base_interval),
                ));
            }

            frames.push((
                b"event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n".to_vec(),
                Duration::ZERO,
            ));
        }

        ProviderType::OpenAiResponses => {
            for (idx, chunk) in chunks.iter().enumerate() {
                let delay = compute_interval(idx, rng);
                let payload = json!({
                    "type": "response.output_text.delta",
                    "delta": chunk
                });
                frames.push((
                    format!("event: response.output_text.delta\ndata: {payload}\n\n").into_bytes(),
                    delay,
                ));
            }

            if config.scenario != BenchmarkScenario::MissingTrailer {
                let trailer_text = format!(
                    "\n{delimiter}\n{{\"explanation\":\"benchmark trailer\",\"warnings\":[]}}"
                );
                let payload = json!({
                    "type": "response.output_text.delta",
                    "delta": trailer_text
                });
                let delay = Duration::from_millis(base_interval);
                frames.push((
                    format!("event: response.output_text.delta\ndata: {payload}\n\n").into_bytes(),
                    delay,
                ));
            }

            if config.scenario == BenchmarkScenario::CorruptedSse {
                frames.push((
                    b"event: response.output_text.delta\ndata: {corrupted-responses}\n\n".to_vec(),
                    Duration::from_millis(base_interval),
                ));
            }

            frames.push((
                b"event: response.completed\ndata: {\"type\":\"response.completed\"}\n\n".to_vec(),
                Duration::ZERO,
            ));
        }

        ProviderType::GeminiGenerateContent => {
            for (idx, chunk) in chunks.iter().enumerate() {
                let delay = compute_interval(idx, rng);
                let payload = json!({
                    "candidates": [{
                        "content": {
                            "parts": [{
                                "text": chunk
                            }]
                        }
                    }]
                });
                frames.push((format!("data: {payload}\n\n").into_bytes(), delay));
            }

            if config.scenario != BenchmarkScenario::MissingTrailer {
                let trailer_text = format!(
                    "\n{delimiter}\n{{\"explanation\":\"benchmark trailer\",\"warnings\":[]}}"
                );
                let payload = json!({
                    "candidates": [{
                        "content": {
                            "parts": [{
                                "text": trailer_text
                            }]
                        }
                    }]
                });
                let delay = Duration::from_millis(base_interval);
                frames.push((format!("data: {payload}\n\n").into_bytes(), delay));
            }

            if config.scenario == BenchmarkScenario::CorruptedSse {
                frames.push((
                    b"data: {corrupted-gemini}\n\n".to_vec(),
                    Duration::from_millis(base_interval),
                ));
            }

            frames.push((
                b"data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"\"}]},\"finishReason\":\"STOP\"}]}\n\n"
                    .to_vec(),
                Duration::ZERO,
            ));
        }
    }

    if config.scenario == BenchmarkScenario::SplitUtf8 {
        // Slice byte stream into small 1-3 byte fragments across UTF-8 code points
        let mut sliced_frames = Vec::new();
        for (frame_bytes, delay) in frames {
            if frame_bytes.is_empty() {
                continue;
            }
            let mut offset = 0;
            let mut first_slice = true;
            while offset < frame_bytes.len() {
                let slice_len = (rng.gen_range(1, 3) as usize).min(frame_bytes.len() - offset);
                let slice = frame_bytes[offset..offset + slice_len].to_vec();
                offset += slice_len;
                let slice_delay = if first_slice {
                    first_slice = false;
                    delay
                } else {
                    Duration::ZERO
                };
                sliced_frames.push((slice, slice_delay));
            }
        }
        return sliced_frames;
    }

    frames
}

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

/// Loopback HTTP mock server for offline streaming benchmark execution.
pub struct LoopbackMockServer {
    pub base_url: String,
    shutdown: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
}

impl LoopbackMockServer {
    /// Starts a loopback server generating frames via closure for each incoming HTTP request.
    pub fn start<F>(frame_generator: F) -> Self
    where
        F: Fn() -> Vec<(Vec<u8>, Duration)> + Send + Sync + 'static,
    {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind loopback mock server");
        let base_url = format!("http://{}", listener.local_addr().expect("mock address"));
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

                // Read incoming HTTP request completely
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

impl Drop for LoopbackMockServer {
    fn drop(&mut self) {
        self.shutdown.store(true, Ordering::SeqCst);
        if let Some(worker) = self.worker.take() {
            let _ = TcpStream::connect(self.base_url.trim_start_matches("http://"));
            let _ = worker.join();
        }
    }
}

fn round_2(val: f64) -> f64 {
    (val * 100.0).round() / 100.0
}

fn percentile(sorted: &[f64], p: f64) -> f64 {
    if sorted.is_empty() {
        return 0.0;
    }
    if sorted.len() == 1 {
        return sorted[0];
    }
    let rank = (p / 100.0) * ((sorted.len() - 1) as f64);
    let lower = rank.floor() as usize;
    let upper = rank.ceil() as usize;
    if lower == upper {
        sorted[lower]
    } else {
        let weight = rank - (lower as f64);
        sorted[lower] * (1.0 - weight) + sorted[upper] * weight
    }
}

fn calculate_timing_stats(mut samples: Vec<f64>) -> TimingStats {
    if samples.is_empty() {
        return TimingStats {
            min_ms: 0.0,
            p50_ms: 0.0,
            p95_ms: 0.0,
            max_ms: 0.0,
            avg_ms: 0.0,
        };
    }
    samples.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
    let n = samples.len();
    let min_ms = samples[0];
    let max_ms = samples[n - 1];
    let avg_ms = samples.iter().sum::<f64>() / (n as f64);
    let p50_ms = percentile(&samples, 50.0);
    let p95_ms = percentile(&samples, 95.0);
    TimingStats {
        min_ms: round_2(min_ms),
        p50_ms: round_2(p50_ms),
        p95_ms: round_2(p95_ms),
        max_ms: round_2(max_ms),
        avg_ms: round_2(avg_ms),
    }
}

fn calculate_count_stats(mut samples: Vec<usize>) -> CountStats {
    if samples.is_empty() {
        return CountStats {
            min: 0,
            p50: 0,
            p95: 0,
            max: 0,
            avg: 0.0,
        };
    }
    samples.sort_unstable();
    let n = samples.len();
    let min = samples[0];
    let max = samples[n - 1];
    let avg = samples.iter().sum::<usize>() as f64 / (n as f64);
    let p50_idx = ((n as f64 * 0.50).ceil() as usize)
        .saturating_sub(1)
        .min(n - 1);
    let p95_idx = ((n as f64 * 0.95).ceil() as usize)
        .saturating_sub(1)
        .min(n - 1);
    CountStats {
        min,
        p50: samples[p50_idx],
        p95: samples[p95_idx],
        max,
        avg: round_2(avg),
    }
}

/// Runs a single benchmark scenario and collects statistical summaries.
pub async fn run_scenario(config: &BenchmarkConfig) -> BenchmarkSummary {
    let benchmark_text = config
        .custom_text
        .as_deref()
        .unwrap_or(DEFAULT_BENCHMARK_TEXT);
    let delimiter = FIXED_DELIMITER.to_owned();
    let configured_chars = benchmark_text.chars().count();

    if config.scenario == BenchmarkScenario::DirectAssembler {
        return run_direct_assembler_scenario(config, benchmark_text, &delimiter);
    }

    let config_clone = config.clone();
    let text_clone = benchmark_text.to_owned();
    let delim_clone = delimiter.clone();

    let server = LoopbackMockServer::start(move || {
        let mut rng = SimpleRng::new(config_clone.seed);
        build_mock_frames(
            config_clone.provider_type,
            &text_clone,
            &delim_clone,
            &config_clone,
            &mut rng,
        )
    });

    let client = ProviderClient::new(TransportLimits {
        connect_timeout: Duration::from_secs(2),
        total_timeout: Duration::from_secs(30),
        max_response_bytes: 4 * 1024 * 1024,
        max_retries: 0,
        retry_delay: Duration::from_millis(1),
        accept_invalid_certs: false,
    })
    .expect("create benchmark provider client");

    let provider = provider_for(config.provider_type);
    let settings = ProviderSettings {
        provider_type: config.provider_type,
        api_base_url: server.base_url.clone(),
        text_endpoint: config.provider_type.default_endpoint().to_owned(),
        vision_endpoint: config.provider_type.default_endpoint().to_owned(),
        text_model: "mock-bench-model".to_owned(),
        vision_model: "mock-bench-model".to_owned(),
        supports_text: true,
        supports_vision: true,
        network_enabled: false,
        safe_dev_mode: false,
        ..ProviderSettings::default()
    };

    let request = TranslationRequest::text(benchmark_text, LanguagePair::new("auto", "zh-CN"));
    let cancellation = CancellationToken::new();

    // Warmup iterations
    for warmup_idx in 0..config.warmup_iterations {
        let request_id = format!("bench-warmup-{}", warmup_idx + 1);
        let _ = client
            .execute_stream(
                provider.as_ref(),
                &settings,
                "bench-local-key",
                &request_id,
                &request,
                Some(&delimiter),
                &cancellation,
                |_| {},
            )
            .await;
    }

    // Measured iterations
    let mut ttft_samples = Vec::with_capacity(config.iterations);
    let mut stream_samples = Vec::with_capacity(config.iterations);
    let mut total_samples = Vec::with_capacity(config.iterations);
    let mut delta_count_samples = Vec::with_capacity(config.iterations);
    let mut total_output_chars = 0_usize;
    let mut parse_errors = 0_usize;
    let mut warnings_count = 0_usize;
    let mut flush_simulated = false;
    let mut successes = 0_usize;

    for i in 0..config.iterations {
        let request_id = format!("bench-iter-{}", i + 1);
        let mut first_delta_time: Option<Duration> = None;
        let mut iter_deltas = 0_usize;
        let mut iter_output_chars = 0_usize;

        let start_instant = Instant::now();
        let res = client
            .execute_stream(
                provider.as_ref(),
                &settings,
                "bench-local-key",
                &request_id,
                &request,
                Some(&delimiter),
                &cancellation,
                |delta| {
                    if !delta.is_empty() {
                        if first_delta_time.is_none() {
                            first_delta_time = Some(start_instant.elapsed());
                        }
                        iter_deltas += 1;
                        iter_output_chars += delta.chars().count();
                    }
                },
            )
            .await;

        let total_elapsed = start_instant.elapsed();

        match res {
            Ok(resp) => {
                successes += 1;
                total_output_chars += iter_output_chars;
                let ttft = first_delta_time.unwrap_or(total_elapsed);
                let stream_time = total_elapsed.saturating_sub(ttft);
                ttft_samples.push(ttft.as_secs_f64() * 1000.0);
                stream_samples.push(stream_time.as_secs_f64() * 1000.0);
                total_samples.push(total_elapsed.as_secs_f64() * 1000.0);
                delta_count_samples.push(iter_deltas);

                if !resp.result.warnings.is_empty() {
                    warnings_count += resp.result.warnings.len();
                    if resp
                        .result
                        .warnings
                        .iter()
                        .any(|w| w.contains("trailer") || w.contains("回退"))
                    {
                        flush_simulated = true;
                    }
                }
            }
            Err(_) => {
                parse_errors += 1;
            }
        }
    }

    let ttft_stats = calculate_timing_stats(ttft_samples);
    let stream_stats = calculate_timing_stats(stream_samples);
    let total_stats = calculate_timing_stats(total_samples);
    let delta_stats = calculate_count_stats(delta_count_samples);

    let chars_per_sec = if successes > 0 && stream_stats.avg_ms > 0.0 && total_output_chars > 0 {
        let avg_chars = (total_output_chars as f64) / (successes as f64);
        let stream_secs = stream_stats.avg_ms / 1000.0;
        round_2(avg_chars / stream_secs)
    } else {
        0.0
    };

    let success_rate = if config.iterations > 0 {
        round_2((successes as f64) / (config.iterations as f64))
    } else {
        0.0
    };

    let is_corrupted = config.scenario == BenchmarkScenario::CorruptedSse;
    let expected_failure = is_corrupted;

    // Calculate delay tolerance checks against local injected parameters
    let expected_ttft = config.injected_ttft_ms as f64;
    let expected_stream =
        (config.chunk_count.saturating_sub(1) as u64 * config.injected_chunk_interval_ms) as f64;
    let ttft_overhead = round_2((ttft_stats.p50_ms - expected_ttft).max(0.0));
    let stream_overhead = round_2((stream_stats.p50_ms - expected_stream).max(0.0));

    let (passed, note) = if is_corrupted {
        let all_failed_as_expected = parse_errors == config.iterations && successes == 0;
        (
            all_failed_as_expected,
            if all_failed_as_expected {
                "Expected failure scenario: all corrupted SSE iterations were correctly intercepted as parse errors without fake output throughput."
                    .to_owned()
            } else {
                format!(
                    "Expected failure scenario: expected {} parse errors, got {} (successes={}).",
                    config.iterations, parse_errors, successes
                )
            },
        )
    } else {
        let tolerance = config.tolerance_ms as f64;
        let is_ok = success_rate >= 0.99
            && (ttft_overhead <= tolerance)
            && (stream_overhead <= tolerance * 2.5);
        (
            is_ok,
            "Tolerance threshold applies only to local injected delay overhead; does not claim cloud LLM performance."
                .to_owned(),
        )
    };

    let tolerance_check = Some(ToleranceCheck {
        tolerance_ms: config.tolerance_ms,
        passed,
        expected_failure,
        ttft_overhead_ms: ttft_overhead,
        stream_overhead_ms: stream_overhead,
        note,
    });

    BenchmarkSummary {
        prompt_version: STREAM_PROMPT_VERSION.to_owned(),
        scenario: config.scenario.as_str().to_owned(),
        provider: format!("{:?}", config.provider_type),
        iterations: config.iterations,
        warmup_iterations: config.warmup_iterations,
        config: SummaryConfig {
            injected_ttft_ms: config.injected_ttft_ms,
            injected_chunk_interval_ms: config.injected_chunk_interval_ms,
            chunk_count: config.chunk_count,
            configured_chars,
            seed: config.seed,
            offline: true,
        },
        ttft_ms: ttft_stats,
        stream_ms: stream_stats,
        total_ms: total_stats,
        chars_per_sec,
        delta_count: delta_stats,
        total_chars: total_output_chars,
        flush_simulated,
        parse_errors,
        warnings_count,
        success_rate,
        tolerance_check,
    }
}

/// Runs direct in-memory parser and assembler scenario.
fn run_direct_assembler_scenario(
    config: &BenchmarkConfig,
    text: &str,
    delimiter: &str,
) -> BenchmarkSummary {
    let mut rng = SimpleRng::new(config.seed);
    let frames = build_mock_frames(config.provider_type, text, delimiter, config, &mut rng);
    let configured_chars = text.chars().count();
    let provider = provider_for(config.provider_type);

    let mut ttft_samples = Vec::with_capacity(config.iterations);
    let mut stream_samples = Vec::with_capacity(config.iterations);
    let mut total_samples = Vec::with_capacity(config.iterations);
    let mut delta_count_samples = Vec::with_capacity(config.iterations);
    let mut total_output_chars = 0_usize;
    let mut parse_errors = 0_usize;
    let warnings_count = 0_usize;
    let mut successes = 0_usize;

    for _ in 0..config.iterations {
        let mut decoder = SseDecoder::default();
        let mut assembler = TextFirstAssembler::new(delimiter.to_owned());
        let mut first_delta_time: Option<Duration> = None;
        let mut iter_deltas = 0_usize;
        let mut iter_output_chars = 0_usize;
        let start_instant = Instant::now();
        let mut has_error = false;

        for (frame, delay) in &frames {
            if !delay.is_zero() {
                thread::sleep(*delay);
            }
            match decoder.push(frame) {
                Ok(events) => {
                    for event in events {
                        match provider.parse_stream_event(&event.event, &event.data) {
                            Ok(Some(
                                ProviderStreamEvent::TextDelta(delta)
                                | ProviderStreamEvent::TextDeltaCompleted(delta),
                            )) => {
                                let visible = assembler.push(&delta);
                                if !visible.is_empty() {
                                    if first_delta_time.is_none() {
                                        first_delta_time = Some(start_instant.elapsed());
                                    }
                                    iter_deltas += 1;
                                    iter_output_chars += visible.chars().count();
                                }
                            }
                            Ok(Some(ProviderStreamEvent::ProviderError(_))) | Err(_) => {
                                has_error = true;
                            }
                            Ok(_) => {}
                        }
                    }
                }
                Err(_) => {
                    has_error = true;
                }
            }
        }

        let _ = assembler.finish();
        let total_elapsed = start_instant.elapsed();

        if has_error {
            parse_errors += 1;
        } else {
            successes += 1;
            total_output_chars += iter_output_chars;
            let ttft = first_delta_time.unwrap_or(total_elapsed);
            let stream_time = total_elapsed.saturating_sub(ttft);
            ttft_samples.push(ttft.as_secs_f64() * 1000.0);
            stream_samples.push(stream_time.as_secs_f64() * 1000.0);
            total_samples.push(total_elapsed.as_secs_f64() * 1000.0);
            delta_count_samples.push(iter_deltas);
        }
    }

    let ttft_stats = calculate_timing_stats(ttft_samples);
    let stream_stats = calculate_timing_stats(stream_samples);
    let total_stats = calculate_timing_stats(total_samples);
    let delta_stats = calculate_count_stats(delta_count_samples);

    let chars_per_sec = if successes > 0 && stream_stats.avg_ms > 0.0 && total_output_chars > 0 {
        let avg_chars = (total_output_chars as f64) / (successes as f64);
        let stream_secs = stream_stats.avg_ms / 1000.0;
        round_2(avg_chars / stream_secs)
    } else {
        0.0
    };

    let success_rate = if config.iterations > 0 {
        round_2((successes as f64) / (config.iterations as f64))
    } else {
        0.0
    };

    BenchmarkSummary {
        prompt_version: STREAM_PROMPT_VERSION.to_owned(),
        scenario: config.scenario.as_str().to_owned(),
        provider: format!("{:?}", config.provider_type),
        iterations: config.iterations,
        warmup_iterations: 0,
        config: SummaryConfig {
            injected_ttft_ms: config.injected_ttft_ms,
            injected_chunk_interval_ms: config.injected_chunk_interval_ms,
            chunk_count: config.chunk_count,
            configured_chars,
            seed: config.seed,
            offline: true,
        },
        ttft_ms: ttft_stats,
        stream_ms: stream_stats,
        total_ms: total_stats,
        chars_per_sec,
        delta_count: delta_stats,
        total_chars: total_output_chars,
        flush_simulated: false,
        parse_errors,
        warnings_count,
        success_rate,
        tolerance_check: Some(ToleranceCheck {
            tolerance_ms: config.tolerance_ms,
            passed: success_rate >= 0.99,
            expected_failure: false,
            ttft_overhead_ms: 0.0,
            stream_overhead_ms: 0.0,
            note: "In-memory direct assembler pipeline without TCP overhead.".to_owned(),
        }),
    }
}

fn print_help() {
    println!(
        r"PopGlot Streaming Benchmark Tool (Default Offline & Reproducible)

USAGE:
    cargo run -p popglot-core --bin stream_benchmark -- [OPTIONS]

OPTIONS:
    --scenario <NAME>           Scenario: realistic, split-utf8, jitter, missing-trailer, corrupted-sse, direct-assembler, all (default: realistic)
    --provider <TYPE>           Provider: openai, anthropic, gemini, openai-responses (default: openai)
    --iterations <N>            Number of benchmark iterations (default: 10)
    --warmup <N>                Warmup runs before measurement (default: 2)
    --ttft-ms <MS>              Injected TTFT delay in ms (default: 30)
    --chunk-interval-ms <MS>    Injected inter-chunk delay in ms (default: 5)
    --chunk-count <N>           Number of streaming chunks (default: 10)
    --seed <U64>                Random seed for reproducible jitter/slicing (default: 42)
    --tolerance-ms <MS>         Delay tolerance threshold for local loopback overhead (default: 35)
    --validate                  Validate results against delay tolerance thresholds
    --json                      Output strict JSON only
    -h, --help                  Print this help message
"
    );
}

fn parse_cli_args() -> (BenchmarkConfig, bool, bool) {
    let args: Vec<String> = std::env::args().collect();
    let mut config = BenchmarkConfig::default();
    let mut run_all = false;
    let mut json_only = false;

    let mut i = 1;
    while i < args.len() {
        match args[i].as_str() {
            "-h" | "--help" => {
                print_help();
                std::process::exit(0);
            }
            "--json" => {
                json_only = true;
            }
            "--validate" => {
                config.validate = true;
            }
            "--scenario" => {
                i += 1;
                if i < args.len() {
                    if args[i].eq_ignore_ascii_case("all") {
                        run_all = true;
                    } else if let Some(scenario) = BenchmarkScenario::parse(&args[i]) {
                        config.scenario = scenario;
                    } else {
                        eprintln!("Unknown scenario: {}", args[i]);
                        std::process::exit(1);
                    }
                }
            }
            "--provider" => {
                i += 1;
                if i < args.len() {
                    config.provider_type = match args[i].to_ascii_lowercase().as_str() {
                        "openai" | "openai-compatible" => ProviderType::OpenAiCompatible,
                        "anthropic" | "anthropic-messages" => ProviderType::AnthropicMessages,
                        "gemini" | "gemini-generate-content" => ProviderType::GeminiGenerateContent,
                        "openai-responses" | "responses" => ProviderType::OpenAiResponses,
                        other => {
                            eprintln!("Unknown provider: {other}");
                            std::process::exit(1);
                        }
                    };
                }
            }
            "--iterations" => {
                i += 1;
                if i < args.len() {
                    config.iterations = args[i].parse().unwrap_or(config.iterations);
                }
            }
            "--warmup" => {
                i += 1;
                if i < args.len() {
                    config.warmup_iterations = args[i].parse().unwrap_or(config.warmup_iterations);
                }
            }
            "--ttft-ms" => {
                i += 1;
                if i < args.len() {
                    config.injected_ttft_ms = args[i].parse().unwrap_or(config.injected_ttft_ms);
                }
            }
            "--chunk-interval-ms" => {
                i += 1;
                if i < args.len() {
                    config.injected_chunk_interval_ms =
                        args[i].parse().unwrap_or(config.injected_chunk_interval_ms);
                }
            }
            "--chunk-count" => {
                i += 1;
                if i < args.len() {
                    config.chunk_count = args[i].parse().unwrap_or(config.chunk_count);
                }
            }
            "--seed" => {
                i += 1;
                if i < args.len() {
                    config.seed = args[i].parse().unwrap_or(config.seed);
                }
            }
            "--tolerance-ms" => {
                i += 1;
                if i < args.len() {
                    config.tolerance_ms = args[i].parse().unwrap_or(config.tolerance_ms);
                }
            }
            "--text" => {
                i += 1;
                if i < args.len() {
                    config.custom_text = Some(args[i].clone());
                }
            }
            unknown => {
                eprintln!("Unknown option: {unknown}");
                print_help();
                std::process::exit(1);
            }
        }
        i += 1;
    }

    (config, run_all, json_only)
}

#[tokio::main]
async fn main() {
    let (config, run_all, _json_only) = parse_cli_args();

    if run_all {
        let mut summaries = Vec::new();
        let mut overall_passed = true;
        for scenario in BenchmarkScenario::all() {
            let mut scenario_config = config.clone();
            scenario_config.scenario = *scenario;
            let summary = run_scenario(&scenario_config).await;
            if let Some(ref check) = summary.tolerance_check
                && !check.passed
            {
                overall_passed = false;
            }
            summaries.push(summary);
        }
        let report = MultiBenchmarkReport {
            prompt_version: STREAM_PROMPT_VERSION.to_owned(),
            scenarios: summaries,
            overall_passed,
        };
        let output = serde_json::to_string_pretty(&report).expect("serialize benchmark report");
        println!("{output}");
        if config.validate && !overall_passed {
            std::process::exit(1);
        }
    } else {
        let summary = run_scenario(&config).await;
        let output = serde_json::to_string_pretty(&summary).expect("serialize benchmark summary");
        println!("{output}");
        if config.validate
            && let Some(ref check) = summary.tolerance_check
            && !check.passed
        {
            std::process::exit(1);
        }
    }
}
