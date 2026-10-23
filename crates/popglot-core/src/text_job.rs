//! P06: versioned pure-text jobs shared by every executor.
//!
//! An external executor (a local AI tool subprocess) must NOT get a second
//! copy of the translation semantics. A job reuses the exact production
//! pieces — `TranslationRequest`, `StreamPromptBuilder` (random delimiter,
//! versioned system instructions, source-as-data user payload),
//! `protect_tokens`/`StreamingTokenRestorer` and `TextFirstAssembler` — so a
//! CLI-produced result passes through the same text-fidelity and protection
//! contract as the HTTP path.
//!
//! Lifecycle: `prepare` registers a job under an unpredictable handle
//! (bounded registry), `push` feeds executor output chunks through the
//! delimiter assembler and token restorer and returns only newly visible
//! text, `finish` completes and returns the structured result, `free`
//! releases the job (idempotent, error/cancel-safe). Locks guard only the
//! registry map and one job's local assembler — never an async operation.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, OnceLock};

use popglot_domain::{LanguagePair, protect_tokens};
use serde::{Deserialize, Serialize};

use crate::provider::{
    ProviderError, ProviderErrorKind, StreamPromptBuilder, TextTask, TranslationRequest,
    generate_stream_delimiter,
};
use crate::streaming::{StreamingTokenRestorer, TextFirstAssembler};

/// Upper bound on simultaneously live jobs; a producer leaking handles can
/// never grow the registry without limit.
const MAX_LIVE_JOBS: usize = 32;

/// Upper bound on accumulated visible text per job (matches the C# stream
/// buffer's default char budget for one session).
const MAX_VISIBLE_CHARS: usize = 1_000_000;

/// The request a host hands to `prepare`. Carries task, languages, the
/// explanation switch and the frozen preference — NEVER credentials; the
/// source travels as its own field, never interpolated into instructions.
#[derive(Debug, Clone, Deserialize)]
pub struct TextJobRequest {
    pub source: String,
    pub source_language: Option<String>,
    pub target_language: Option<String>,
    #[serde(default = "default_task")]
    pub task: String,
    #[serde(default = "default_true")]
    pub include_explanation: bool,
    #[serde(default)]
    pub protect_code_tokens: bool,
    pub preference: Option<String>,
    pub template_id: Option<String>,
    #[serde(default)]
    pub template_revision: Option<u64>,
}

fn default_task() -> String {
    "translate".to_owned()
}

fn default_true() -> bool {
    true
}

impl TextJobRequest {
    fn task(&self) -> Result<TextTask, ProviderError> {
        match self.task.as_str() {
            "translate" => Ok(TextTask::Translate),
            "summarize" => Ok(TextTask::Summarize),
            "explain" => Ok(TextTask::Explain),
            other => Err(ProviderError::new(
                ProviderErrorKind::Configuration,
                format!("未知的任务类型：{other}"),
            )),
        }
    }
}

/// What `prepare` hands back to the host: the versioned prompt for the
/// executor (system instructions + source-as-data payload) and the opaque
/// handle. No credentials, no API shapes — those belong to the executor.
/// The delimiter is protocol data (it is already embedded in the system
/// instructions); returning it explicitly lets a host-side test harness
/// synthesize protocol-conforming output without parsing instructions.
#[derive(Debug, Serialize)]
pub struct PreparedTextJob {
    pub job_handle: String,
    pub prompt_version: &'static str,
    pub system_instructions: String,
    pub user_payload: String,
    pub delimiter: String,
}

/// Terminal completion states the executor can report.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TextJobCompletion {
    /// The executor finished its turn successfully.
    Completed,
    /// The turn was cancelled (user or policy); whatever is assembled stays
    /// visible as a partial and the job never reports Completed.
    Cancelled,
    /// The executor failed (crash, transport error, refusal).
    Failed,
}

/// The structured result of a finished job — the same fidelity contract the
/// HTTP path returns, with protected terms restored and metadata separated.
#[derive(Debug, Serialize)]
pub struct TextJobResult {
    pub state: &'static str,
    pub translated_text: String,
    pub explanation: String,
    pub protected_terms: Vec<String>,
    pub warnings: Vec<String>,
    pub visible_char_count: usize,
}

struct LiveJob {
    prompt_version: &'static str,
    system_instructions: String,
    user_payload: String,
    delimiter: String,
    assembler: TextFirstAssembler,
    restorer: Option<StreamingTokenRestorer>,
    protected_terms: Vec<String>,
    finished: bool,
    visible_chars: usize,
}

impl LiveJob {
    fn push(&mut self, delta: &str) -> Result<String, ProviderError> {
        if self.finished {
            return Ok(String::new());
        }
        let visible = self.assembler.push(delta);
        if self.visible_chars + visible.chars().count() > MAX_VISIBLE_CHARS {
            return Err(ProviderError::new(
                ProviderErrorKind::InvalidResponse,
                "本任务累计译文超过上限，已停止累积。",
            ));
        }
        let restored = match &mut self.restorer {
            Some(restorer) => restorer.push(&visible),
            None => visible,
        };
        self.visible_chars += restored.chars().count();
        Ok(restored)
    }
}

/// Bounded process-global registry of live jobs.
#[derive(Default)]
pub struct TextJobManager {
    jobs: Mutex<HashMap<String, Arc<Mutex<LiveJob>>>>,
}

impl TextJobManager {
    #[must_use]
    pub fn global() -> &'static Self {
        static MANAGER: OnceLock<TextJobManager> = OnceLock::new();
        MANAGER.get_or_init(Self::default)
    }

    /// Builds the job: validates the request, applies token protection when
    /// requested, generates the random delimiter and compiles the versioned
    /// prompt with the production builder.
    ///
    /// # Errors
    ///
    /// Configuration errors for empty/oversized sources, unknown tasks or a
    /// full registry.
    pub fn prepare(&self, request: &TextJobRequest) -> Result<PreparedTextJob, ProviderError> {
        let source = request.source.trim();
        if source.is_empty() || source.len() > crate::MAX_SOURCE_BYTES {
            return Err(ProviderError::new(
                ProviderErrorKind::Configuration,
                if source.is_empty() {
                    "翻译原文不能为空。".to_owned()
                } else {
                    format!("翻译原文不能超过 {} KiB。", crate::MAX_SOURCE_BYTES / 1024)
                },
            ));
        }
        let task = request.task()?;
        let languages = LanguagePair::new(
            request.source_language.as_deref().unwrap_or("auto"),
            request.target_language.as_deref().unwrap_or("zh-CN"),
        );

        // Placeholder protection exists to keep full translations faithful to
        // every protected token. A summarize/explain job may legitimately omit
        // source identifiers, so the same machinery here would both push the
        // model to reproduce every placeholder and report honest omissions as
        // integrity warnings. Reading tasks therefore always see the raw
        // source; identifiers they do mention stay exact by instruction.
        let (text_for_prompt, restorer, protected_terms) =
            if request.protect_code_tokens && task == TextTask::Translate {
                let protected = protect_tokens(source);
                let terms = protected
                    .tokens
                    .iter()
                    .map(|token| token.placeholder.clone())
                    .collect::<Vec<_>>();
                (
                    protected.sanitized_text,
                    Some(StreamingTokenRestorer::new(&protected.tokens)),
                    terms,
                )
            } else {
                (source.to_owned(), None, Vec::new())
            };

        let mut translation_request = TranslationRequest::text(text_for_prompt, languages)
            .with_explanation(request.include_explanation)
            .with_task(task);
        if let Some(preference) = &request.preference {
            translation_request = translation_request.with_preference(preference);
        }
        if let (Some(template_id), Some(revision)) =
            (&request.template_id, request.template_revision)
        {
            translation_request = translation_request.with_template_snapshot(template_id, revision);
        }
        let delimiter = generate_stream_delimiter()?;
        let prompt = StreamPromptBuilder::new(&translation_request, &delimiter)
            .build()
            .map_err(|error| {
                ProviderError::new(ProviderErrorKind::Configuration, error.to_string())
            })?;

        let job = LiveJob {
            prompt_version: prompt.version,
            system_instructions: prompt.system_instructions.clone(),
            user_payload: prompt.user_payload.clone(),
            delimiter: prompt.delimiter.clone(),
            assembler: TextFirstAssembler::new(prompt.delimiter),
            restorer,
            protected_terms,
            finished: false,
            visible_chars: 0,
        };

        let mut jobs = self
            .jobs
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner);
        if jobs.len() >= MAX_LIVE_JOBS {
            return Err(ProviderError::new(
                ProviderErrorKind::Configuration,
                "并存文本任务过多，请先完成或释放现有任务。",
            ));
        }
        let handle = loop {
            let candidate = format!("tj-{}", generate_stream_delimiter()?);
            if !jobs.contains_key(&candidate) {
                break candidate;
            }
        };
        jobs.insert(handle.clone(), Arc::new(Mutex::new(job)));
        let prepared = jobs
            .get(&handle)
            .and_then(|job| {
                let job = job.lock().ok()?;
                Some(PreparedTextJob {
                    job_handle: handle,
                    prompt_version: job.prompt_version,
                    system_instructions: job.system_instructions.clone(),
                    user_payload: job.user_payload.clone(),
                    delimiter: job.delimiter.clone(),
                })
            })
            .ok_or_else(|| {
                ProviderError::new(ProviderErrorKind::Configuration, "文本任务注册失败。")
            })?;
        Ok(prepared)
    }

    /// Feeds one executor output chunk; returns only newly visible (and
    /// token-restored) text.
    ///
    /// # Errors
    ///
    /// Configuration errors for unknown/finished handles; invalid-response
    /// when the accumulated text exceeds the cap.
    pub fn push(&self, handle: &str, delta: &str) -> Result<String, ProviderError> {
        let job = self.lookup(handle)?;
        let mut job = job
            .lock()
            .map_err(|_| ProviderError::new(ProviderErrorKind::Configuration, "任务状态损坏。"))?;
        job.push(delta)
    }

    /// Completes the job with the executor's terminal state and returns the
    /// structured result. The job is consumed (a second finish errors).
    ///
    /// # Errors
    ///
    /// Configuration errors for unknown/finished handles.
    pub fn finish(
        &self,
        handle: &str,
        completion: TextJobCompletion,
    ) -> Result<TextJobResult, ProviderError> {
        let job = self.lookup(handle)?;
        let mut job = job
            .lock()
            .map_err(|_| ProviderError::new(ProviderErrorKind::Configuration, "任务状态损坏。"))?;
        if job.finished {
            return Err(ProviderError::new(
                ProviderErrorKind::Configuration,
                "文本任务已结束，不能重复完成。",
            ));
        }
        job.finished = true;
        let tail = job.assembler.finish_delta();
        let assembled = job.assembler.finish();
        // The restorer's finish() returns the COMPLETE restored text plus
        // exactly-once integrity findings — a protected token split across
        // the final chunks still round-trips, and any dropped/duplicated
        // placeholder surfaces as an honest warning.
        let (text, integrity_warnings) = match &mut job.restorer {
            Some(restorer) => {
                if !tail.is_empty() {
                    let _ = restorer.push(&tail);
                }
                let outcome = restorer.finish();
                let mut warnings = Vec::new();
                if !outcome.dropped_terms.is_empty() {
                    warnings.push(format!(
                        "保护词未在译文中出现：{}",
                        outcome.dropped_terms.join("、")
                    ));
                }
                if !outcome.duplicated_terms.is_empty() {
                    warnings.push("译文包含重复的保护词。".to_owned());
                }
                if !outcome.unknown_placeholders.is_empty() {
                    warnings.push("译文中出现未知的保护占位符。".to_owned());
                }
                (outcome.text, warnings)
            }
            None => (assembled.text, Vec::new()),
        };
        let state = match completion {
            TextJobCompletion::Completed if !integrity_warnings.is_empty() => "partial",
            TextJobCompletion::Completed if text.trim().is_empty() => "partial",
            TextJobCompletion::Completed => "completed",
            TextJobCompletion::Cancelled => "cancelled",
            TextJobCompletion::Failed => "failed",
        };
        let mut warnings = assembled.warnings;
        warnings.extend(integrity_warnings);
        if completion != TextJobCompletion::Completed {
            warnings.push("执行器未正常完成，仅保留已生成内容。".to_owned());
        }
        let explanation = assembled
            .metadata
            .as_ref()
            .map(|metadata| metadata.explanation.clone())
            .unwrap_or_default();
        if let Some(metadata) = &assembled.metadata {
            warnings.extend(metadata.warnings.iter().cloned());
        }
        let result = TextJobResult {
            state,
            translated_text: text,
            explanation,
            protected_terms: job.protected_terms.clone(),
            warnings,
            visible_char_count: job.visible_chars,
        };
        // 复查修复 #3：finish 是终态——任务立即移出注册表（free 之后变
        // 幂等空操作），句柄槽位不可能被已完成任务占满；对已移除句柄的
        // push/finish 会得到「未知的文本任务句柄」错误而非静默空串。
        if let Ok(mut jobs) = self.jobs.lock() {
            jobs.remove(handle);
        }
        Ok(result)
    }

    /// Releases a job. Idempotent; safe from any error/cancel/finally path.
    pub fn free(&self, handle: &str) {
        if let Ok(mut jobs) = self.jobs.lock() {
            jobs.remove(handle);
        }
    }

    fn lookup(&self, handle: &str) -> Result<Arc<Mutex<LiveJob>>, ProviderError> {
        self.jobs
            .lock()
            .unwrap_or_else(std::sync::PoisonError::into_inner)
            .get(handle)
            .cloned()
            .ok_or_else(|| {
                ProviderError::new(ProviderErrorKind::Configuration, "未知的文本任务句柄。")
            })
    }

    /// Number of live jobs (diagnostics/tests).
    pub fn live_count(&self) -> usize {
        self.jobs.lock().map(|jobs| jobs.len()).unwrap_or_default()
    }
}
