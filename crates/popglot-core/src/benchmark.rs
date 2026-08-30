//! Live provider benchmark engine with safety controls and zero secret leaks.
//!
//! # Safety Guarantees
//!
//! 1. **Default Offline / Zero Network**: Network calls are strictly blocked unless
//!    both `--live` and `--i-understand-cost` flags are explicitly present.
//! 2. **Settings Enforced**: Even with dual flags, if `settings.safe_dev_mode` is enabled
//!    or `settings.network_enabled` is false, live calls are refused.
//! 3. **No CLI API Key**: API keys cannot be passed via CLI arguments. Keys are only
//!    resolved from `POPGLOT_BENCHMARK_API_KEY` or provider-specific environment variables.
//! 4. **Zero Secret / Body Leakage**: The recorded report and JSON summary contain ONLY
//!    endpoint fingerprints (SHA-256), provider, model, prompt version, machine arch,
//!    timing metrics (TTFT, total, deltas), character counts, and sanitized errors.
//!    Prompts, source texts, response bodies, headers, and keys are NEVER serialized.
//! 5. **Error Sanitization**: All error messages are sanitized to strip query parameters,
//!    Bearer tokens, and API key patterns.
//! 6. **Bounded Character Limits**: Fixture inputs are constrained by a configurable character cap.

use crate::provider::{
    ProviderClient, ProviderError, ProviderErrorKind, STREAM_PROMPT_VERSION, TranslationRequest,
    generate_stream_delimiter, provider_for,
};
use popglot_domain::{LanguagePair, ProviderSettings, ProviderType};
use serde::{Deserialize, Serialize};
use std::fmt::Write as _;
use std::future::Future;
use std::path::PathBuf;
use std::pin::Pin;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use tokio_util::sync::CancellationToken;

/// Default character limit for total benchmark input text.
pub const DEFAULT_MAX_BENCHMARK_INPUT_CHARS: usize = 2_000;

/// Default prompt fixture for minimal subset.
pub const FALLBACK_MINIMAL_FIXTURE_ID: &str = "prose_autumn";
pub const FALLBACK_MINIMAL_FIXTURE_TEXT: &str = "The autumn wind blows gently across the golden fields, carrying the quiet scent of fallen leaves and the distant promise of winter.";

// ---------------------------------------------------------------------------
// Pure SHA-256 implementation (0-dependency, deterministic)
// ---------------------------------------------------------------------------

#[allow(
    clippy::many_single_char_names,
    clippy::too_many_lines,
    clippy::chunks_exact_to_as_chunks
)]
fn sha256_digest(data: &[u8]) -> [u8; 32] {
    const K: [u32; 64] = [
        0x428a_2f98,
        0x7137_4491,
        0xb5c0_fbcf,
        0xe9b5_dba5,
        0x3956_c25b,
        0x59f1_11f1,
        0x923f_82a4,
        0xab1c_5ed5,
        0xd807_aa98,
        0x1283_5b01,
        0x2431_85be,
        0x550c_7dc3,
        0x72be_5d74,
        0x80de_b1fe,
        0x9bdc_06a7,
        0xc19b_f174,
        0xe49b_69c1,
        0xefbe_4786,
        0x0fc1_9dc6,
        0x240c_a1cc,
        0x2de9_2c6f,
        0x4a74_84aa,
        0x5cb0_a9dc,
        0x76f9_88da,
        0x983e_5152,
        0xa831_c66d,
        0xb003_27c8,
        0xbf59_7fc7,
        0xc6e0_0bf3,
        0xd5a7_9147,
        0x06ca_6351,
        0x1429_2967,
        0x27b7_0a85,
        0x2e1b_2138,
        0x4d2c_6dfc,
        0x5338_0d13,
        0x650a_7354,
        0x766a_0abb,
        0x81c2_c92e,
        0x9272_2c85,
        0xa2bf_e8a1,
        0xa81a_664b,
        0xc24b_8b70,
        0xc76c_51a3,
        0xd192_e819,
        0xd699_0624,
        0xf40e_3585,
        0x106a_a070,
        0x19a4_c116,
        0x1e37_6c08,
        0x2748_774c,
        0x34b0_bcb5,
        0x391c_0cb3,
        0x4ed8_aa4a,
        0x5b9c_ca4f,
        0x682e_6ff3,
        0x748f_82ee,
        0x78a5_636f,
        0x84c8_7814,
        0x8cc7_0208,
        0x90be_fffa,
        0xa450_6ceb,
        0xbef9_a3f7,
        0xc671_78f2,
    ];

    let mut h0: u32 = 0x6a09_e667;
    let mut h1: u32 = 0xbb67_ae85;
    let mut h2: u32 = 0x3c6e_f372;
    let mut h3: u32 = 0xa54f_f53a;
    let mut h4: u32 = 0x510e_527f;
    let mut h5: u32 = 0x9b05_688c;
    let mut h6: u32 = 0x1f83_d9ab;
    let mut h7: u32 = 0x5be0_cd19;

    let bit_len = (data.len() as u64) * 8;
    let mut msg = Vec::from(data);
    msg.push(0x80);
    while (msg.len() % 64) != 56 {
        msg.push(0x00);
    }
    msg.extend_from_slice(&bit_len.to_be_bytes());

    for chunk in msg.chunks_exact(64) {
        let mut w = [0_u32; 64];
        for (i, w_elem) in w.iter_mut().take(16).enumerate() {
            let offset = i * 4;
            *w_elem = u32::from_be_bytes([
                chunk[offset],
                chunk[offset + 1],
                chunk[offset + 2],
                chunk[offset + 3],
            ]);
        }
        for i in 16..64 {
            let s0 = w[i - 15].rotate_right(7) ^ w[i - 15].rotate_right(18) ^ (w[i - 15] >> 3);
            let s1 = w[i - 2].rotate_right(17) ^ w[i - 2].rotate_right(19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16]
                .wrapping_add(s0)
                .wrapping_add(w[i - 7])
                .wrapping_add(s1);
        }

        let mut a = h0;
        let mut b = h1;
        let mut c = h2;
        let mut d = h3;
        let mut e = h4;
        let mut f = h5;
        let mut g = h6;
        let mut h = h7;

        for i in 0..64 {
            let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
            let ch = (e & f) ^ ((!e) & g);
            let temp1 = h
                .wrapping_add(s1)
                .wrapping_add(ch)
                .wrapping_add(K[i])
                .wrapping_add(w[i]);
            let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
            let maj = (a & b) ^ (a & c) ^ (b & c);
            let temp2 = s0.wrapping_add(maj);

            h = g;
            g = f;
            f = e;
            e = d.wrapping_add(temp1);
            d = c;
            c = b;
            b = a;
            a = temp1.wrapping_add(temp2);
        }

        h0 = h0.wrapping_add(a);
        h1 = h1.wrapping_add(b);
        h2 = h2.wrapping_add(c);
        h3 = h3.wrapping_add(d);
        h4 = h4.wrapping_add(e);
        h5 = h5.wrapping_add(f);
        h6 = h6.wrapping_add(g);
        h7 = h7.wrapping_add(h);
    }

    let mut out = [0_u8; 32];
    out[0..4].copy_from_slice(&h0.to_be_bytes());
    out[4..8].copy_from_slice(&h1.to_be_bytes());
    out[8..12].copy_from_slice(&h2.to_be_bytes());
    out[12..16].copy_from_slice(&h3.to_be_bytes());
    out[16..20].copy_from_slice(&h4.to_be_bytes());
    out[20..24].copy_from_slice(&h5.to_be_bytes());
    out[24..28].copy_from_slice(&h6.to_be_bytes());
    out[28..32].copy_from_slice(&h7.to_be_bytes());
    out
}

/// Generates a deterministic SHA-256 fingerprint from base URL and endpoint.
///
/// Hides raw internal IPs, private hostnames, and sensitive routing paths.
#[must_use]
pub fn compute_endpoint_fingerprint(base_url: &str, endpoint: &str) -> String {
    let normalized = format!(
        "{}/{}",
        base_url.trim_end_matches('/'),
        endpoint.trim_start_matches('/')
    );
    let digest = sha256_digest(normalized.as_bytes());
    let mut hex = String::with_capacity(64);
    for byte in digest {
        let _ = write!(hex, "{byte:02x}");
    }
    format!("sha256:{hex}")
}

// ---------------------------------------------------------------------------
// Error and String Sanitization
// ---------------------------------------------------------------------------

/// Sanitizes error strings to remove URL query strings, Bearer tokens,
/// API keys, and sensitive authorization patterns.
#[must_use]
pub fn sanitize_error_string(input: &str) -> String {
    let mut result = input.to_owned();

    // 1. Redact URL queries (?key=..., ?token=..., etc.)
    if let Ok(re_url_query) = regex::Regex::new(r"\?[a-zA-Z0-9_&=%\-\.\+~]+") {
        result = re_url_query
            .replace_all(&result, "?[QUERY_REDACTED]")
            .to_string();
    }

    // 2. Redact Bearer tokens
    if let Ok(re_bearer) = regex::Regex::new(r"(?i)bearer\s+[a-zA-Z0-9_\-\.]{6,}") {
        result = re_bearer
            .replace_all(&result, "Bearer [REDACTED]")
            .to_string();
    }

    // 3. Redact common API key patterns (OpenAI sk-..., Google AIza..., GitHub ghp_...)
    if let Ok(re_sk) = regex::Regex::new(r"sk-[a-zA-Z0-9_\-]{8,}") {
        result = re_sk.replace_all(&result, "[KEY_REDACTED]").to_string();
    }
    if let Ok(re_aiza) = regex::Regex::new(r"AIza[a-zA-Z0-9_\-]{10,}") {
        result = re_aiza.replace_all(&result, "[KEY_REDACTED]").to_string();
    }
    if let Ok(re_ghp) = regex::Regex::new(r"ghp_[a-zA-Z0-9]{10,}") {
        result = re_ghp.replace_all(&result, "[KEY_REDACTED]").to_string();
    }

    // 4. Redact JSON fields containing api_key / key / secret
    if let Ok(re_json_key) =
        regex::Regex::new(r#""(api_key|apiKey|key|secret|token|password)":\s*"[^"]*""#)
    {
        result = re_json_key
            .replace_all(&result, r#""$1": "[REDACTED]""#)
            .to_string();
    }

    // 5. Redact HTTP basic auth credentials in URLs: http://user:pass@host
    if let Ok(re_userinfo) = regex::Regex::new(r"https?://([^/@:]+):([^/@]+)@") {
        result = re_userinfo
            .replace_all(&result, "http://[REDACTED]@")
            .to_string();
    }

    result
}

// ---------------------------------------------------------------------------
// Safety Flags & Error Types
// ---------------------------------------------------------------------------

/// Required dual-flags to permit any live network execution.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct LiveBenchmarkSafetyFlags {
    /// Explicit flag `--live`
    pub live: bool,
    /// Explicit flag `--i-understand-cost`
    pub i_understand_cost: bool,
}

impl LiveBenchmarkSafetyFlags {
    #[must_use]
    pub fn is_live_permitted(self) -> bool {
        self.live && self.i_understand_cost
    }
}

/// Benchmark safety validation and execution errors.
#[derive(Debug, Clone, PartialEq, Eq, thiserror::Error)]
pub enum BenchmarkSafetyError {
    #[error(
        "安全拒绝：必须同时提供 `--live` 和 `--i-understand-cost` 才能发起真实网络基准测试（live={live}, i_understand_cost={i_understand_cost}）。已回退到 Dry-Run 模式。"
    )]
    MissingSafetyFlags { live: bool, i_understand_cost: bool },

    #[error("安全拒绝：当前设置启用了 SafeDevMode（开发安全隔离），禁止任何真实网络请求。")]
    SafeDevModeBlocked,

    #[error("安全拒绝：当前设置 network_enabled=false（已禁网），禁止任何真实网络请求。")]
    NetworkDisabledBlocked,

    #[error(
        "缺少认证密钥：未在环境变量中找到有效 API Key（已检查：{}）。注意：CLI 禁止通过参数传入密钥。",
        .env_vars_checked.join(", ")
    )]
    MissingApiKey { env_vars_checked: Vec<String> },

    #[error(
        "安全违规：禁止通过 `--api-key` 命令行参数传递密钥！请设置环境变量 POPGLOT_BENCHMARK_API_KEY 或 Provider 专用变量。"
    )]
    ForbiddenApiKeyArgument,

    #[error("设置配置无效：{0}")]
    InvalidSettings(String),

    #[error("总输入字符数超过安全上限：{total} > {cap}")]
    TotalCharsExceeded { total: usize, cap: usize },

    #[error("Provider 执行失败：{0}")]
    ExecutionFailed(String),
}

// ---------------------------------------------------------------------------
// API Key Resolution (Env-Only, Cross-Platform)
// ---------------------------------------------------------------------------

/// Resolves the benchmark API key from environment variables.
///
/// Priority:
/// 1. `POPGLOT_BENCHMARK_API_KEY`
/// 2. Provider-specific env var:
///    - `OpenAiCompatible` / `OpenAiResponses`: `OPENAI_API_KEY`
///    - `AnthropicMessages`: `ANTHROPIC_API_KEY`
///    - `GeminiGenerateContent`: `GEMINI_API_KEY`
///
/// # Errors
///
/// Returns [`BenchmarkSafetyError::MissingApiKey`] if no key was found.
pub fn resolve_benchmark_api_key(
    provider_type: ProviderType,
) -> Result<String, BenchmarkSafetyError> {
    resolve_benchmark_api_key_with_lookup(provider_type, |name| std::env::var(name).ok())
}

/// Resolves benchmark API key using a custom lookup closure (for deterministic testing).
///
/// # Errors
///
/// Returns [`BenchmarkSafetyError::MissingApiKey`] when none of the checked environment
/// variable names match or return non-empty content.
pub fn resolve_benchmark_api_key_with_lookup(
    provider_type: ProviderType,
    lookup: impl Fn(&str) -> Option<String>,
) -> Result<String, BenchmarkSafetyError> {
    let mut checked = Vec::new();

    // 1. Generic benchmark key
    checked.push("POPGLOT_BENCHMARK_API_KEY".to_owned());
    if let Some(val) = lookup("POPGLOT_BENCHMARK_API_KEY") {
        let trimmed = val.trim();
        if !trimmed.is_empty() {
            return Ok(trimmed.to_owned());
        }
    }

    // 2. Provider-specific key
    let specific_var = match provider_type {
        ProviderType::OpenAiCompatible | ProviderType::OpenAiResponses => "OPENAI_API_KEY",
        ProviderType::AnthropicMessages => "ANTHROPIC_API_KEY",
        ProviderType::GeminiGenerateContent => "GEMINI_API_KEY",
    };
    checked.push(specific_var.to_owned());
    if let Some(val) = lookup(specific_var) {
        let trimmed = val.trim();
        if !trimmed.is_empty() {
            return Ok(trimmed.to_owned());
        }
    }

    Err(BenchmarkSafetyError::MissingApiKey {
        env_vars_checked: checked,
    })
}

// ---------------------------------------------------------------------------
// Fixture Subsets & Loading
// ---------------------------------------------------------------------------

/// Benchmark fixture subset selector.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum BenchmarkSubset {
    #[default]
    Minimal,
    CodeMixed,
    All,
}

impl BenchmarkSubset {
    #[must_use]
    pub fn parse(s: &str) -> Option<Self> {
        match s.to_ascii_lowercase().replace('_', "-").as_str() {
            "minimal" | "min" | "default" => Some(Self::Minimal),
            "code-mixed" | "codemixed" | "code" => Some(Self::CodeMixed),
            "all" | "full" => Some(Self::All),
            _ => None,
        }
    }

    #[must_use]
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Minimal => "minimal",
            Self::CodeMixed => "code-mixed",
            Self::All => "all",
        }
    }
}

/// A selected prompt fixture for benchmark execution.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BenchmarkFixtureItem {
    pub id: String,
    pub source_text: String,
    pub source_lang: String,
    pub target_lang: String,
    pub include_explanation: bool,
    pub protect_tokens: bool,
}

fn locate_fixture_dir() -> Option<PathBuf> {
    let candidates = [
        PathBuf::from("tests/fixtures/prompts"),
        PathBuf::from("crates/popglot-core/tests/fixtures/prompts"),
        PathBuf::from("../../tests/fixtures/prompts"),
    ];
    for path in &candidates {
        if path.exists() && path.is_dir() {
            return Some(path.clone());
        }
    }
    None
}

/// Raw fixture JSON for file parsing.
#[derive(Debug, Clone, Deserialize)]
struct RawPromptFixture {
    id: String,
    #[serde(default)]
    category: String,
    #[serde(default)]
    input_type: String,
    source_text: String,
    #[serde(default = "default_source_lang")]
    source_lang: String,
    #[serde(default = "default_target_lang")]
    target_lang: String,
    #[serde(default = "default_explanation")]
    include_explanation: bool,
    #[serde(default)]
    protect_tokens: bool,
}

fn default_source_lang() -> String {
    "auto".to_owned()
}
fn default_target_lang() -> String {
    "zh-CN".to_owned()
}
fn default_explanation() -> bool {
    true
}

/// Loads prompt fixtures according to subset and enforces the character cap.
#[must_use]
pub fn load_benchmark_fixtures(
    subset: BenchmarkSubset,
    max_chars: usize,
) -> Vec<BenchmarkFixtureItem> {
    let mut raw_fixtures = Vec::new();

    if let Some(dir) = locate_fixture_dir()
        && let Ok(entries) = std::fs::read_dir(&dir)
    {
        let mut paths: Vec<_> = entries
            .filter_map(Result::ok)
            .map(|e| e.path())
            .filter(|p| p.extension().is_some_and(|ext| ext == "json"))
            .collect();
        paths.sort();

        for path in paths {
            if let Ok(content) = std::fs::read_to_string(&path)
                && let Ok(fixture) = serde_json::from_str::<RawPromptFixture>(&content)
                && (fixture.input_type == "text" || fixture.input_type.is_empty())
            {
                raw_fixtures.push(fixture);
            }
        }
    }

    // Fallback if fixture directory was missing or empty
    if raw_fixtures.is_empty() {
        raw_fixtures.push(RawPromptFixture {
            id: FALLBACK_MINIMAL_FIXTURE_ID.to_owned(),
            category: "prose".to_owned(),
            input_type: "text".to_owned(),
            source_text: FALLBACK_MINIMAL_FIXTURE_TEXT.to_owned(),
            source_lang: "en".to_owned(),
            target_lang: "zh-CN".to_owned(),
            include_explanation: true,
            protect_tokens: false,
        });
    }

    let filtered: Vec<RawPromptFixture> = match subset {
        BenchmarkSubset::Minimal => {
            // Find prose_autumn or take the first fixture
            if let Some(first_prose) = raw_fixtures
                .iter()
                .find(|f| f.id == "prose_autumn" || f.category == "prose")
            {
                vec![first_prose.clone()]
            } else {
                vec![raw_fixtures[0].clone()]
            }
        }
        BenchmarkSubset::CodeMixed => {
            let selected: Vec<_> = raw_fixtures
                .iter()
                .filter(|f| {
                    f.category == "code_mixed"
                        || f.category == "token_protection"
                        || f.category == "tech_stack"
                        || f.id.contains("code")
                        || f.id.contains("tech")
                })
                .cloned()
                .collect();
            if selected.is_empty() {
                vec![raw_fixtures[0].clone()]
            } else {
                selected
            }
        }
        BenchmarkSubset::All => raw_fixtures,
    };

    // Apply cumulative character cap
    let mut total_chars = 0_usize;
    let mut result = Vec::new();

    for fixture in filtered {
        let fixture_len = fixture.source_text.chars().count();
        if total_chars + fixture_len > max_chars {
            if result.is_empty() {
                // If even the first fixture exceeds max_chars, take a truncated slice of the first
                let truncated: String = fixture.source_text.chars().take(max_chars).collect();
                result.push(BenchmarkFixtureItem {
                    id: fixture.id,
                    source_text: truncated,
                    source_lang: fixture.source_lang,
                    target_lang: fixture.target_lang,
                    include_explanation: fixture.include_explanation,
                    protect_tokens: fixture.protect_tokens,
                });
            }
            break;
        }
        total_chars += fixture_len;
        result.push(BenchmarkFixtureItem {
            id: fixture.id,
            source_text: fixture.source_text,
            source_lang: fixture.source_lang,
            target_lang: fixture.target_lang,
            include_explanation: fixture.include_explanation,
            protect_tokens: fixture.protect_tokens,
        });
    }

    result
}

// ---------------------------------------------------------------------------
// Results & Reporting Data Structures (No Secret / No Prompt / No Body)
// ---------------------------------------------------------------------------

/// Sanitized metric item for one executed fixture.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct BenchmarkItemResult {
    pub fixture_id: String,
    pub input_chars: usize,
    pub output_chars: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ttft_ms: Option<f64>,
    pub total_ms: f64,
    pub delta_count: usize,
    pub status: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub sanitized_error: Option<String>,
}

/// Complete benchmark execution report.
///
/// Contains ONLY sanitized metadata and timing stats. Never contains prompts,
/// translated text, HTTP headers, request bodies, or API keys.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct LiveBenchmarkReport {
    pub prompt_version: String,
    pub endpoint_fingerprint: String,
    pub provider: String,
    pub model: String,
    pub machine: String,
    pub timestamp_utc: String,
    pub dry_run: bool,
    pub safety_verified: bool,
    pub total_input_chars: usize,
    pub total_output_chars: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub avg_ttft_ms: Option<f64>,
    pub avg_total_ms: f64,
    pub items: Vec<BenchmarkItemResult>,
}

impl LiveBenchmarkReport {
    /// Formats a concise human-readable summary for console output.
    #[must_use]
    pub fn summary_text(&self) -> String {
        let mut out = String::new();
        let mode_tag = if self.dry_run {
            "DRY-RUN (OFFLINE)"
        } else {
            "LIVE NETWORK"
        };
        let _ = writeln!(out, "==================================================");
        let _ = writeln!(out, "PopGlot Live Provider Benchmark Summary [{mode_tag}]");
        let _ = writeln!(out, "==================================================");
        let _ = writeln!(out, "Prompt Version:        {}", self.prompt_version);
        let _ = writeln!(out, "Endpoint Fingerprint:  {}", self.endpoint_fingerprint);
        let _ = writeln!(out, "Provider:              {}", self.provider);
        let _ = writeln!(out, "Model:                 {}", self.model);
        let _ = writeln!(out, "Machine:               {}", self.machine);
        let _ = writeln!(out, "Timestamp UTC:         {}", self.timestamp_utc);
        let _ = writeln!(out, "Total Input Chars:     {}", self.total_input_chars);
        let _ = writeln!(out, "Total Output Chars:    {}", self.total_output_chars);
        if let Some(avg_ttft) = self.avg_ttft_ms {
            let _ = writeln!(out, "Avg TTFT:              {avg_ttft:.2} ms");
        }
        let _ = writeln!(out, "Avg Total Time:        {:.2} ms", self.avg_total_ms);
        let _ = writeln!(out, "Items Evaluated:       {}", self.items.len());
        for (i, item) in self.items.iter().enumerate() {
            let _ = write!(
                out,
                "  [{}] {}: status={}, in_chars={}, out_chars={}, deltas={}, time={:.1}ms",
                i + 1,
                item.fixture_id,
                item.status,
                item.input_chars,
                item.output_chars,
                item.delta_count,
                item.total_ms
            );
            if let Some(ttft) = item.ttft_ms {
                let _ = write!(out, ", ttft={ttft:.1}ms");
            }
            if let Some(ref err) = item.sanitized_error {
                let _ = write!(out, ", error={err}");
            }
            let _ = writeln!(out);
        }
        let _ = writeln!(out, "==================================================");
        out
    }
}

// ---------------------------------------------------------------------------
// Executor Abstraction & Implementations
// ---------------------------------------------------------------------------

/// Trait abstracting streaming execution for testing without real network calls.
pub trait BenchmarkStreamExecutor: Send + Sync {
    #[allow(clippy::too_many_arguments)]
    fn execute_stream<'a>(
        &'a self,
        provider_type: ProviderType,
        settings: &'a ProviderSettings,
        api_key: &'a str,
        request_id: &'a str,
        request: &'a TranslationRequest,
        delimiter: Option<&'a str>,
        cancellation: &'a CancellationToken,
        on_delta: Box<dyn FnMut(&str) + Send + 'a>,
    ) -> Pin<Box<dyn Future<Output = Result<(), ProviderError>> + Send + 'a>>;
}

/// Real provider client executor utilizing `ProviderClient::execute_stream`.
pub struct RealProviderClientExecutor {
    client: ProviderClient,
}

impl RealProviderClientExecutor {
    #[must_use]
    pub fn new(client: ProviderClient) -> Self {
        Self { client }
    }
}

impl BenchmarkStreamExecutor for RealProviderClientExecutor {
    #[allow(clippy::too_many_arguments)]
    fn execute_stream<'a>(
        &'a self,
        provider_type: ProviderType,
        settings: &'a ProviderSettings,
        api_key: &'a str,
        request_id: &'a str,
        request: &'a TranslationRequest,
        delimiter: Option<&'a str>,
        cancellation: &'a CancellationToken,
        mut on_delta: Box<dyn FnMut(&str) + Send + 'a>,
    ) -> Pin<Box<dyn Future<Output = Result<(), ProviderError>> + Send + 'a>> {
        Box::pin(async move {
            let provider = provider_for(provider_type);
            self.client
                .execute_stream(
                    provider.as_ref(),
                    settings,
                    api_key,
                    request_id,
                    request,
                    delimiter,
                    cancellation,
                    |delta| {
                        on_delta(delta);
                    },
                )
                .await
                .map(|_| ())
        })
    }
}

/// Spy / Mock executor for testing that records call counts and verifies zero unexpected network requests.
pub struct MockBenchmarkExecutor {
    pub call_count: AtomicUsize,
    pub simulated_deltas: Vec<String>,
    pub simulated_ttft_delay: Duration,
    pub simulated_chunk_delay: Duration,
    pub return_error: Option<String>,
}

impl Default for MockBenchmarkExecutor {
    fn default() -> Self {
        Self {
            call_count: AtomicUsize::new(0),
            simulated_deltas: vec!["Hello ".to_owned(), "World!".to_owned()],
            simulated_ttft_delay: Duration::from_millis(5),
            simulated_chunk_delay: Duration::from_millis(2),
            return_error: None,
        }
    }
}

impl BenchmarkStreamExecutor for MockBenchmarkExecutor {
    #[allow(clippy::too_many_arguments)]
    fn execute_stream<'a>(
        &'a self,
        _provider_type: ProviderType,
        _settings: &'a ProviderSettings,
        _api_key: &'a str,
        _request_id: &'a str,
        _request: &'a TranslationRequest,
        _delimiter: Option<&'a str>,
        _cancellation: &'a CancellationToken,
        mut on_delta: Box<dyn FnMut(&str) + Send + 'a>,
    ) -> Pin<Box<dyn Future<Output = Result<(), ProviderError>> + Send + 'a>> {
        self.call_count.fetch_add(1, Ordering::SeqCst);
        Box::pin(async move {
            if let Some(ref err_msg) = self.return_error {
                return Err(ProviderError::new(
                    ProviderErrorKind::Transport,
                    err_msg.clone(),
                ));
            }
            if !self.simulated_ttft_delay.is_zero() {
                tokio::time::sleep(self.simulated_ttft_delay).await;
            }
            for delta in &self.simulated_deltas {
                if !self.simulated_chunk_delay.is_zero() {
                    tokio::time::sleep(self.simulated_chunk_delay).await;
                }
                on_delta(delta);
            }
            Ok(())
        })
    }
}

// ---------------------------------------------------------------------------
// Benchmark Runner Configuration & Execution
// ---------------------------------------------------------------------------

/// Type alias for environment key lookup function in test configurations.
pub type EnvKeyLookup = Arc<dyn Fn(&str) -> Option<String> + Send + Sync>;

/// Configuration for live benchmark run.
#[derive(Clone)]
pub struct LiveBenchmarkConfig {
    pub settings: ProviderSettings,
    pub model_override: Option<String>,
    pub safety_flags: LiveBenchmarkSafetyFlags,
    pub subset: BenchmarkSubset,
    pub max_input_chars: usize,
    pub custom_text: Option<String>,
    pub env_key_override: Option<String>,
    pub env_key_lookup: Option<EnvKeyLookup>,
}

impl Default for LiveBenchmarkConfig {
    fn default() -> Self {
        Self {
            settings: ProviderSettings::default(),
            model_override: None,
            safety_flags: LiveBenchmarkSafetyFlags::default(),
            subset: BenchmarkSubset::Minimal,
            max_input_chars: DEFAULT_MAX_BENCHMARK_INPUT_CHARS,
            custom_text: None,
            env_key_override: None,
            env_key_lookup: None,
        }
    }
}

/// Runs the benchmark with strict safety gates and statistical aggregation.
///
/// # Errors
///
/// Returns [`BenchmarkSafetyError`] when safety gates (dual flags, `SafeDevMode`,
/// `NetworkEnabled`) fail, when the API key is missing, or if execution errors out.
#[allow(clippy::too_many_lines, clippy::cast_precision_loss)]
pub async fn run_live_benchmark<E: BenchmarkStreamExecutor>(
    config: &LiveBenchmarkConfig,
    executor: &E,
) -> Result<LiveBenchmarkReport, BenchmarkSafetyError> {
    let mut effective_settings = config.settings.clone();
    if let Some(ref model) = config.model_override {
        effective_settings.text_model.clone_from(model);
        effective_settings.vision_model.clone_from(model);
    }

    let endpoint_fingerprint = compute_endpoint_fingerprint(
        &effective_settings.api_base_url,
        &effective_settings.text_endpoint,
    );
    let provider_name = format!("{:?}", effective_settings.provider_type);
    let model_name = effective_settings.text_model.clone();
    let machine_name = format!("{}-{}", std::env::consts::OS, std::env::consts::ARCH);
    let timestamp_utc = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |d| d.as_secs())
        .to_string();

    let is_live = config.safety_flags.is_live_permitted();

    // Check safety settings
    if is_live {
        if effective_settings.safe_dev_mode {
            return Err(BenchmarkSafetyError::SafeDevModeBlocked);
        }
        if !effective_settings.network_enabled {
            return Err(BenchmarkSafetyError::NetworkDisabledBlocked);
        }
    }

    // Resolve API key if live mode is permitted
    let resolved_key = if is_live {
        if let Some(ref custom_key) = config.env_key_override {
            custom_key.clone()
        } else if let Some(ref lookup_fn) = config.env_key_lookup {
            let lookup_ref = Arc::clone(lookup_fn);
            resolve_benchmark_api_key_with_lookup(effective_settings.provider_type, move |k| {
                lookup_ref(k)
            })?
        } else {
            resolve_benchmark_api_key(effective_settings.provider_type)?
        }
    } else {
        String::new()
    };

    // Prepare fixtures
    let fixtures: Vec<BenchmarkFixtureItem> = if let Some(ref custom_text) = config.custom_text {
        let truncated: String = custom_text.chars().take(config.max_input_chars).collect();
        vec![BenchmarkFixtureItem {
            id: "custom_input".to_owned(),
            source_text: truncated,
            source_lang: "auto".to_owned(),
            target_lang: "zh-CN".to_owned(),
            include_explanation: effective_settings.include_explanation,
            protect_tokens: effective_settings.protect_code_tokens,
        }]
    } else {
        load_benchmark_fixtures(config.subset, config.max_input_chars)
    };

    let mut item_results = Vec::with_capacity(fixtures.len());
    let mut total_input_chars = 0_usize;
    let mut total_output_chars = 0_usize;
    let mut ttft_collector = Vec::new();
    let mut total_time_collector = Vec::new();

    let cancellation = CancellationToken::new();

    for (idx, fixture) in fixtures.iter().enumerate() {
        let input_len = fixture.source_text.chars().count();
        total_input_chars += input_len;

        if !is_live {
            // Dry-run mode: record dry run placeholder without calling executor
            item_results.push(BenchmarkItemResult {
                fixture_id: fixture.id.clone(),
                input_chars: input_len,
                output_chars: 0,
                ttft_ms: None,
                total_ms: 0.0,
                delta_count: 0,
                status: "dry_run_blocked".to_owned(),
                sanitized_error: None,
            });
            continue;
        }

        // Live execution mode
        let request_id = format!("bench-live-{}-{}", idx + 1, fixture.id);
        let request = TranslationRequest::text(
            &fixture.source_text,
            LanguagePair::new(&fixture.source_lang, &fixture.target_lang),
        )
        .with_explanation(fixture.include_explanation);

        let delimiter =
            generate_stream_delimiter().unwrap_or_else(|_| "PGMETA_live_bench_nonce".to_owned());

        let start_time = Instant::now();
        let mut first_delta_time: Option<Duration> = None;
        let mut delta_count = 0_usize;
        let mut output_chars = 0_usize;

        let exec_res = executor
            .execute_stream(
                effective_settings.provider_type,
                &effective_settings,
                &resolved_key,
                &request_id,
                &request,
                Some(&delimiter),
                &cancellation,
                Box::new(|delta| {
                    if !delta.is_empty() {
                        if first_delta_time.is_none() {
                            first_delta_time = Some(start_time.elapsed());
                        }
                        delta_count += 1;
                        output_chars += delta.chars().count();
                    }
                }),
            )
            .await;

        let total_elapsed = start_time.elapsed();
        let total_ms = (total_elapsed.as_secs_f64() * 1000.0 * 100.0).round() / 100.0;
        total_time_collector.push(total_ms);
        total_output_chars += output_chars;

        let ttft_ms = first_delta_time.map(|d| (d.as_secs_f64() * 1000.0 * 100.0).round() / 100.0);
        if let Some(ttft) = ttft_ms {
            ttft_collector.push(ttft);
        }

        match exec_res {
            Ok(()) => {
                item_results.push(BenchmarkItemResult {
                    fixture_id: fixture.id.clone(),
                    input_chars: input_len,
                    output_chars,
                    ttft_ms,
                    total_ms,
                    delta_count,
                    status: "success".to_owned(),
                    sanitized_error: None,
                });
            }
            Err(err) => {
                let sanitized = sanitize_error_string(&err.to_string());
                item_results.push(BenchmarkItemResult {
                    fixture_id: fixture.id.clone(),
                    input_chars: input_len,
                    output_chars,
                    ttft_ms,
                    total_ms,
                    delta_count,
                    status: "failed".to_owned(),
                    sanitized_error: Some(sanitized),
                });
            }
        }
    }

    let avg_ttft_ms = if ttft_collector.is_empty() {
        None
    } else {
        let sum: f64 = ttft_collector.iter().sum();
        Some(((sum / (ttft_collector.len() as f64)) * 100.0).round() / 100.0)
    };

    let avg_total_ms = if total_time_collector.is_empty() {
        0.0
    } else {
        let sum: f64 = total_time_collector.iter().sum();
        ((sum / (total_time_collector.len() as f64)) * 100.0).round() / 100.0
    };

    let report = LiveBenchmarkReport {
        prompt_version: STREAM_PROMPT_VERSION.to_owned(),
        endpoint_fingerprint,
        provider: provider_name,
        model: model_name,
        machine: machine_name,
        timestamp_utc,
        dry_run: !is_live,
        safety_verified: true,
        total_input_chars,
        total_output_chars,
        avg_ttft_ms,
        avg_total_ms,
        items: item_results,
    };

    if is_live {
        Ok(report)
    } else {
        Err(BenchmarkSafetyError::MissingSafetyFlags {
            live: config.safety_flags.live,
            i_understand_cost: config.safety_flags.i_understand_cost,
        })
    }
}

/// Generates an offline dry-run summary report without performing any network or API key operations.
#[must_use]
pub fn generate_dry_run_report(config: &LiveBenchmarkConfig) -> LiveBenchmarkReport {
    let mut effective_settings = config.settings.clone();
    if let Some(ref model) = config.model_override {
        effective_settings.text_model.clone_from(model);
        effective_settings.vision_model.clone_from(model);
    }

    let endpoint_fingerprint = compute_endpoint_fingerprint(
        &effective_settings.api_base_url,
        &effective_settings.text_endpoint,
    );
    let provider_name = format!("{:?}", effective_settings.provider_type);
    let model_name = effective_settings.text_model.clone();
    let machine_name = format!("{}-{}", std::env::consts::OS, std::env::consts::ARCH);
    let timestamp_utc = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |d| d.as_secs())
        .to_string();

    let fixtures: Vec<BenchmarkFixtureItem> = if let Some(ref custom_text) = config.custom_text {
        let truncated: String = custom_text.chars().take(config.max_input_chars).collect();
        vec![BenchmarkFixtureItem {
            id: "custom_input".to_owned(),
            source_text: truncated,
            source_lang: "auto".to_owned(),
            target_lang: "zh-CN".to_owned(),
            include_explanation: effective_settings.include_explanation,
            protect_tokens: effective_settings.protect_code_tokens,
        }]
    } else {
        load_benchmark_fixtures(config.subset, config.max_input_chars)
    };

    let mut item_results = Vec::with_capacity(fixtures.len());
    let mut total_input_chars = 0_usize;

    for fixture in &fixtures {
        let input_len = fixture.source_text.chars().count();
        total_input_chars += input_len;
        item_results.push(BenchmarkItemResult {
            fixture_id: fixture.id.clone(),
            input_chars: input_len,
            output_chars: 0,
            ttft_ms: None,
            total_ms: 0.0,
            delta_count: 0,
            status: "dry_run_blocked".to_owned(),
            sanitized_error: None,
        });
    }

    LiveBenchmarkReport {
        prompt_version: STREAM_PROMPT_VERSION.to_owned(),
        endpoint_fingerprint,
        provider: provider_name,
        model: model_name,
        machine: machine_name,
        timestamp_utc,
        dry_run: true,
        safety_verified: true,
        total_input_chars,
        total_output_chars: 0,
        avg_ttft_ms: None,
        avg_total_ms: 0.0,
        items: item_results,
    }
}
