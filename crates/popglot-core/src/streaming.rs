//! Pure incremental assembly for text-first translation streams.
//!
//! This module deliberately knows nothing about providers or transports. It
//! only separates a random trailer delimiter from visible text and restores
//! protected placeholders without losing characters at chunk boundaries.

use popglot_domain::{ProtectedToken, protected_token_variants};
use serde::{Deserialize, Serialize};

/// Optional metadata emitted after a text-first translation.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct TranslationMetadata {
    pub detected_source_lang: String,
    pub transcription: String,
    pub explanation: String,
    pub warnings: Vec<String>,
}

/// The final result of [`TextFirstAssembler`].
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TextFirstResult {
    pub text: String,
    pub metadata: Option<TranslationMetadata>,
    pub warnings: Vec<String>,
}

/// Incrementally separates visible translation text from a JSON trailer.
#[derive(Debug, Clone)]
pub struct TextFirstAssembler {
    delimiter: String,
    pending: String,
    trailer: String,
    body: String,
    matched: bool,
    finished: bool,
}

impl TextFirstAssembler {
    /// Creates an assembler for the request's random delimiter.
    #[must_use]
    pub fn new(delimiter: impl Into<String>) -> Self {
        Self {
            delimiter: delimiter.into(),
            pending: String::new(),
            trailer: String::new(),
            body: String::new(),
            matched: false,
            finished: false,
        }
    }

    /// Supplies another UTF-8 text chunk and returns only newly visible text.
    ///
    /// Before the delimiter is found, only the shortest suffix that can still
    /// be its prefix is held back. Once found, all following text is trailer
    /// data and is never returned as visible text.
    pub fn push(&mut self, chunk: &str) -> String {
        if self.finished || chunk.is_empty() {
            return String::new();
        }
        self.pending.push_str(chunk);
        if self.matched {
            self.trailer.push_str(&self.pending);
            self.pending.clear();
            return String::new();
        }
        self.emit_pending()
    }

    /// Returns the final newly visible delta for a callback, if any.
    ///
    /// Calling this before [`Self::finish`] is the streaming path: the returned
    /// tail has already been appended to the final body, so the later result is
    /// a snapshot rather than text that callers should append again. Calling it
    /// after `finish` returns an empty string.
    pub fn finish_delta(&mut self) -> String {
        if self.finished {
            return String::new();
        }

        self.finished = true;
        if self.matched {
            self.trailer.push_str(&self.pending);
            self.pending.clear();
            String::new()
        } else {
            let tail = std::mem::take(&mut self.pending);
            self.body.push_str(&tail);
            tail
        }
    }

    /// Finishes assembly, flushing an unmatched tail and parsing a matched JSON
    /// trailer. Invalid or missing metadata never removes visible body text.
    #[must_use]
    pub fn finish(&mut self) -> TextFirstResult {
        let _ = self.finish_delta();

        let mut warnings = Vec::new();
        let metadata = if self.matched {
            if let Ok(metadata) = serde_json::from_str::<TranslationMetadata>(self.trailer.trim()) {
                Some(metadata)
            } else {
                warnings.push("translation metadata 无法解析，已保留正文。".to_owned());
                None
            }
        } else {
            None
        };
        if !self.matched && !self.delimiter.is_empty() {
            warnings.push("translation metadata trailer 缺失，已保留正文。".to_owned());
        }
        TextFirstResult {
            text: self.body.clone(),
            metadata,
            warnings,
        }
    }

    fn emit_pending(&mut self) -> String {
        if self.delimiter.is_empty() {
            let visible = std::mem::take(&mut self.pending);
            self.body.push_str(&visible);
            return visible;
        }

        if let Some(index) = self.pending.find(&self.delimiter) {
            let mut visible = self.pending[..index].to_owned();
            // The stream protocol puts its delimiter on the next line. Keep a
            // prospective final line ending buffered until this point, then
            // remove exactly that protocol separator. A user-authored final
            // newline remains before it (and therefore remains in the body).
            strip_protocol_separator(&mut visible);
            self.body.push_str(&visible);
            self.pending.drain(..index + self.delimiter.len());
            self.matched = true;
            self.trailer.push_str(&self.pending);
            self.pending.clear();
            return visible;
        }

        let delimiter_keep = longest_suffix_prefix(&self.pending, &self.delimiter);
        let separator_start = protocol_separator_start(&self.pending, delimiter_keep);
        let keep = self.pending.len() - separator_start;
        let visible = self.pending[..separator_start].to_owned();
        self.pending.drain(..self.pending.len() - keep);
        self.body.push_str(&visible);
        visible
    }
}

/// Incrementally restores protected placeholders in streamed text.
#[derive(Debug, Clone)]
pub struct StreamingTokenRestorer {
    tokens: Vec<ProtectedToken>,
    variants: Vec<Vec<String>>,
    pending: String,
    restored: String,
    matched: Vec<bool>,
    finished: bool,
}

impl StreamingTokenRestorer {
    /// Creates a restorer using the domain crate's canonical token mapping.
    #[must_use]
    pub fn new(tokens: &[ProtectedToken]) -> Self {
        Self {
            tokens: tokens.to_vec(),
            variants: tokens
                .iter()
                .enumerate()
                .map(|(index, token)| protected_token_variants(&token.placeholder, index))
                .collect(),
            pending: String::new(),
            restored: String::new(),
            matched: vec![false; tokens.len()],
            finished: false,
        }
    }

    /// Creates a restorer directly from [`popglot_domain::ProtectedText`].
    #[must_use]
    pub fn from_protected_text(protected: &popglot_domain::ProtectedText) -> Self {
        Self::new(&protected.tokens)
    }

    /// Supplies a chunk and returns the newly displayable restored delta.
    pub fn push(&mut self, chunk: &str) -> String {
        if self.finished || chunk.is_empty() {
            return String::new();
        }
        self.pending.push_str(chunk);
        self.emit(false)
    }

    /// Finishes and returns the complete restored text plus dropped-token info.
    #[must_use]
    pub fn finish(&mut self) -> popglot_domain::RestoredText {
        let _ = self.finish_delta();
        popglot_domain::RestoredText {
            text: self.restored.clone(),
            dropped_terms: self
                .tokens
                .iter()
                .enumerate()
                .filter(|(index, _)| !self.matched[*index])
                .map(|(_, token)| token.original.clone())
                .collect(),
        }
    }

    /// Returns only the final unbuffered tail, useful when the caller batches
    /// deltas and wants the final call to have the same delta semantics.
    pub fn finish_delta(&mut self) -> String {
        if self.finished {
            return String::new();
        }
        self.finished = true;
        self.emit(true)
    }

    fn emit(&mut self, at_finish: bool) -> String {
        let mut output = String::new();
        loop {
            let Some((start, token_index, variant)) = self.earliest_match(at_finish) else {
                let keep = if at_finish {
                    0
                } else {
                    longest_incomplete_special_suffix(&self.pending, &self.variants)
                };
                let emit_len = self.pending.len() - keep;
                output.push_str(&self.pending[..emit_len]);
                self.pending.drain(..emit_len);
                break;
            };
            output.push_str(&self.pending[..start]);
            self.pending.drain(..start + variant.len());
            output.push_str(&self.tokens[token_index].original);
            self.matched[token_index] = true;
        }
        self.restored.push_str(&output);
        output
    }

    fn earliest_match(&self, at_finish: bool) -> Option<(usize, usize, String)> {
        // A bare compatibility spelling can be complete while the canonical
        // bracketed spelling is still split across chunks. Do not consume the
        // bare spelling from inside that longer candidate. A complete variant
        // at the end is not partial and must be restored immediately.
        let partial_start = if at_finish {
            self.pending.len()
        } else {
            self.pending.len() - longest_incomplete_special_suffix(&self.pending, &self.variants)
        };
        self.variants
            .iter()
            .enumerate()
            .flat_map(|(token_index, variants)| {
                variants
                    .iter()
                    .enumerate()
                    .filter_map(move |(order, variant)| {
                        self.pending
                            .find(variant)
                            .map(|start| (start, token_index, order, variant))
                    })
            })
            .filter(|(start, _, _, _)| *start < partial_start)
            .min_by_key(|(start, token_index, order, _)| (*start, *token_index, *order))
            .map(|(start, token_index, _, variant)| (start, token_index, variant.clone()))
    }
}

/// Returns the byte offset of the earliest suffix that must remain buffered:
/// a prospective protocol line ending immediately before a delimiter prefix.
fn protocol_separator_start(value: &str, delimiter_keep: usize) -> usize {
    let delimiter_start = value.len() - delimiter_keep;
    let before_delimiter = &value[..delimiter_start];
    if before_delimiter.ends_with("\r\n") {
        delimiter_start - 2
    } else if before_delimiter.ends_with('\n') {
        delimiter_start - 1
    } else {
        delimiter_start
    }
}

fn strip_protocol_separator(value: &mut String) {
    if value.ends_with("\r\n") {
        value.truncate(value.len() - 2);
    } else if value.ends_with('\n') {
        value.truncate(value.len() - 1);
    }
}

fn longest_suffix_prefix(value: &str, pattern: &str) -> usize {
    value
        .char_indices()
        .map(|(index, _)| index)
        .chain(std::iter::once(value.len()))
        .filter(|&start| pattern.starts_with(&value[start..]))
        .map(|start| value.len() - start)
        .max()
        .unwrap_or(0)
}

fn longest_incomplete_special_suffix(value: &str, variants: &[Vec<String>]) -> usize {
    value
        .char_indices()
        .map(|(index, _)| index)
        .chain(std::iter::once(value.len()))
        .filter(|&start| {
            let suffix = &value[start..];
            variants
                .iter()
                .flatten()
                .any(|variant| suffix.len() < variant.len() && variant.starts_with(suffix))
        })
        .map(|start| value.len() - start)
        .max()
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use popglot_domain::protect_tokens;

    #[test]
    fn delimiter_crosses_chunks_and_metadata_is_hidden() {
        let mut assembler = TextFirstAssembler::new("<<<PG_META_x>>>");
        assert_eq!(assembler.push("中文<<<PG_"), "中文");
        assert_eq!(assembler.push("META_x>>>"), "");
        assert_eq!(assembler.push(r#"{"explanation":"ok"}"#), "");
        let result = assembler.finish();
        assert_eq!(result.text, "中文");
        assert_eq!(result.metadata.unwrap().explanation, "ok");
    }

    #[test]
    fn unmatched_delimiter_tail_is_not_lost() {
        let mut assembler = TextFirstAssembler::new("<<<PG_META_x>>>");
        let mut text = assembler.push("尾<<<PG_");
        text.push_str(&assembler.push("not-a-trailer"));
        let result = assembler.finish();
        assert_eq!(result.text, text);
    }

    #[test]
    fn finish_delta_flushes_an_unmatched_delimiter_tail_once() {
        let mut assembler = TextFirstAssembler::new("<<<PG_META_x>>>");
        assert_eq!(assembler.push("尾<<<PG_"), "尾");
        assert_eq!(assembler.finish_delta(), "<<<PG_");
        assert!(assembler.finish_delta().is_empty());
        assert_eq!(assembler.finish().text, "尾<<<PG_");
    }

    #[test]
    fn removes_only_the_protocol_line_ending_before_delimiter() {
        let delimiter = "PG_META_0123456789";
        let mut assembler = TextFirstAssembler::new(delimiter);
        assert_eq!(assembler.push("第一行\r\n"), "第一行");
        assert_eq!(assembler.push(&format!("\r\n{delimiter}")), "\r\n");
        assert_eq!(assembler.push("{\"warnings\":[]}"), "");
        let result = assembler.finish();
        assert_eq!(result.text, "第一行\r\n");
        assert!(result.metadata.is_some());
    }

    #[test]
    fn malformed_or_missing_metadata_keeps_body() {
        let mut bad = TextFirstAssembler::new("::meta::");
        assert_eq!(bad.push("正文::meta::{bad"), "正文");
        assert!(bad.finish().metadata.is_none());
        assert_eq!(bad.finish().text, "正文");

        let mut missing = TextFirstAssembler::new("::meta::");
        missing.push("正文");
        let result = missing.finish();
        assert_eq!(result.text, "正文");
        assert!(!result.warnings.is_empty());
    }

    #[test]
    fn ordinary_brackets_and_unicode_are_not_consumed() {
        let protected = protect_tokens("const userName = 你好;");
        let mut restorer = StreamingTokenRestorer::new(&protected.tokens);
        let input = "[abc] <tag> ⟦普通⟧ ";
        let mut output = restorer.push(input);
        output.push_str(&restorer.push(&protected.sanitized_text));
        let final_text = restorer.finish().text;
        assert_eq!(final_text, output);
        assert_eq!(output, format!("{input}const userName = 你好;"));
    }

    #[test]
    fn restores_a_complete_token_at_the_end_of_pending_text() {
        let protected = protect_tokens("const answer = getAnswer();");
        let mut restorer = StreamingTokenRestorer::new(&protected.tokens);
        assert_eq!(
            restorer.push(&protected.sanitized_text),
            "const answer = getAnswer();"
        );
        let result = restorer.finish();
        assert_eq!(result.text, "const answer = getAnswer();");
        assert!(result.dropped_terms.is_empty());
    }

    #[test]
    fn finish_delta_restores_complete_tokens_before_flushing_plain_tail() {
        let protected = protect_tokens("const answer = getAnswer();");
        let mut restorer = StreamingTokenRestorer::new(&protected.tokens);
        assert_eq!(restorer.push("前缀"), "前缀");
        restorer.pending.push_str(&protected.sanitized_text);
        restorer.pending.push('尾');
        assert_eq!(restorer.finish_delta(), "const answer = getAnswer();尾");
        assert_eq!(restorer.finish().text, "前缀const answer = getAnswer();尾");
    }

    #[test]
    fn reports_dropped_terms_after_finishing() {
        let protected = protect_tokens("const answer = getAnswer();");
        let mut restorer = StreamingTokenRestorer::new(&protected.tokens);
        assert_eq!(restorer.push("译文"), "译文");
        let result = restorer.finish();
        assert_eq!(result.text, "译文");
        assert_eq!(
            result.dropped_terms,
            vec![protected.tokens[0].original.clone()]
        );
    }

    #[test]
    fn delimiter_splits_at_every_unicode_boundary() {
        for delimiter in ["<<<PG_META_x>>>", "⟦尾部元数据⟧", "::元数据::"] {
            let wire = format!("中文{delimiter}{{\"warnings\":[]}}");
            for split in wire.char_indices().map(|(index, _)| index) {
                let (left, right) = wire.split_at(split);
                let mut assembler = TextFirstAssembler::new(delimiter);
                let mut deltas = assembler.push(left);
                deltas.push_str(&assembler.push(right));
                let result = assembler.finish();
                assert_eq!(deltas, "中文");
                assert_eq!(result.text, deltas);
                assert!(result.metadata.is_some());
            }
        }
    }

    #[test]
    fn every_token_variant_splits_at_every_unicode_boundary() {
        let protected = protect_tokens("const userName = getUserName();");
        for (token_index, variants) in protected
            .tokens
            .iter()
            .enumerate()
            .map(|(index, token)| (index, protected_token_variants(&token.placeholder, index)))
        {
            for variant in variants {
                let wire = format!("前{variant}后");
                for split in wire.char_indices().map(|(index, _)| index) {
                    let (left, right) = wire.split_at(split);
                    let mut restorer = StreamingTokenRestorer::new(&protected.tokens);
                    let mut deltas = restorer.push(left);
                    deltas.push_str(&restorer.push(right));
                    let final_text = restorer.finish().text;
                    let expected = format!("前{}后", protected.tokens[token_index].original);
                    assert_eq!(deltas, expected, "variant={variant:?}, split={split}");
                    assert_eq!(final_text, expected, "variant={variant:?}, split={split}");
                }
            }
        }
    }
}
