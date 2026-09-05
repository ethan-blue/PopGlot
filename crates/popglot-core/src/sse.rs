//! Incremental, byte-oriented decoding of Server-Sent Events (SSE).
//!
//! The decoder deliberately accepts bytes rather than `&str`: transport chunks
//! may split a UTF-8 code point, a line, or several complete events.

use std::str::Utf8Error;
use thiserror::Error;

/// The default maximum number of bytes permitted in one SSE event.
///
/// Matches the documented 256 KiB contract in ARCHITECTURE.md; legitimate
/// single frames (long Anthropic text deltas, dense screenshot transcriptions)
/// must not abort the whole stream.
pub const DEFAULT_MAX_EVENT_BYTES: usize = 256 * 1024;
/// The default maximum raw byte length of one SSE line, excluding its LF.
pub const DEFAULT_MAX_LINE_BYTES: usize = DEFAULT_MAX_EVENT_BYTES;

/// A decoded SSE event.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SseEvent {
    /// The event type, defaulting to `message` when no `event:` field is sent.
    pub event: String,
    /// All `data:` fields joined with a newline, as required by SSE.
    pub data: String,
}

/// Errors produced while incrementally decoding SSE bytes.
#[derive(Debug, Error)]
pub enum SseError {
    #[error("SSE input is not valid UTF-8: {0}")]
    InvalidUtf8(#[from] Utf8Error),
    #[error("SSE event exceeds the {limit}-byte limit")]
    EventTooLarge { limit: usize },
    #[error("SSE line exceeds the {limit}-byte limit")]
    LineTooLarge { limit: usize },
    #[error("SSE stream ended in the middle of a UTF-8 sequence")]
    IncompleteUtf8,
}

/// Incremental SSE decoder with a per-event byte limit.
#[derive(Debug)]
pub struct SseDecoder {
    max_event_bytes: usize,
    max_line_bytes: usize,
    line: Vec<u8>,
    event_bytes: usize,
    event_name: Option<String>,
    data: String,
    has_data: bool,
}

impl Default for SseDecoder {
    fn default() -> Self {
        Self::new(DEFAULT_MAX_EVENT_BYTES)
    }
}

impl SseDecoder {
    /// Creates a decoder which rejects events larger than `max_event_bytes`.
    #[must_use]
    pub fn new(max_event_bytes: usize) -> Self {
        Self {
            max_event_bytes,
            max_line_bytes: max_event_bytes,
            line: Vec::new(),
            event_bytes: 0,
            event_name: None,
            data: String::new(),
            has_data: false,
        }
    }

    /// Pushes another transport chunk and returns every complete event found.
    ///
    /// A chunk can contain several events, and any part of an event may be
    /// split across calls. UTF-8 is decoded only after a complete line has
    /// arrived, so a code point split between chunks is safe.
    ///
    /// # Errors
    ///
    /// Returns [`SseError`] for invalid UTF-8 or an event exceeding the
    /// configured byte limit.
    pub fn push(&mut self, bytes: &[u8]) -> Result<Vec<SseEvent>, SseError> {
        let mut events = Vec::new();
        let mut start = 0;
        while let Some(relative_end) = bytes[start..].iter().position(|&byte| byte == b'\n') {
            let end = start + relative_end;
            self.append_line_bytes(&bytes[start..end])?;
            let line = std::mem::take(&mut self.line);
            self.consume_line(&line, &mut events)?;
            start = end + 1;
        }
        self.append_line_bytes(&bytes[start..])?;
        Ok(events)
    }

    /// Finishes the stream.
    ///
    /// A valid final line is treated as terminated by EOF, allowing a final
    /// event without the customary extra blank line. Invalid trailing bytes
    /// return an explicit error instead of being silently discarded.
    ///
    /// # Errors
    ///
    /// Returns [`SseError`] for invalid trailing UTF-8 or an oversized final
    /// event.
    pub fn finish(&mut self) -> Result<Vec<SseEvent>, SseError> {
        let mut events = Vec::new();
        if !self.line.is_empty() {
            let line = std::mem::take(&mut self.line);
            if std::str::from_utf8(&line).is_err() {
                return Err(SseError::IncompleteUtf8);
            }
            self.consume_line(&line, &mut events)?;
        }
        self.dispatch(&mut events);
        Ok(events)
    }

    fn append_line_bytes(&mut self, bytes: &[u8]) -> Result<(), SseError> {
        if bytes.len() > self.max_line_bytes.saturating_sub(self.line.len()) {
            return Err(SseError::LineTooLarge {
                limit: self.max_line_bytes,
            });
        }
        self.line.extend_from_slice(bytes);
        Ok(())
    }

    fn consume_line(
        &mut self,
        raw_line: &[u8],
        events: &mut Vec<SseEvent>,
    ) -> Result<(), SseError> {
        let line = raw_line.strip_suffix(b"\r").unwrap_or(raw_line);
        let line = std::str::from_utf8(line)?;
        if line.starts_with(':') {
            // Comments are heartbeats: validate UTF-8, but do not let them
            // consume the current event's byte budget.
            return Ok(());
        }

        let added_bytes = raw_line.len() + 1;
        if self.event_bytes + added_bytes > self.max_event_bytes {
            return Err(SseError::EventTooLarge {
                limit: self.max_event_bytes,
            });
        }
        self.event_bytes += added_bytes;

        if line.is_empty() {
            self.dispatch(events);
        } else {
            let (field, value) = line.split_once(':').map_or((line, ""), |(field, value)| {
                (field, value.strip_prefix(' ').unwrap_or(value))
            });
            match field {
                "event" => self.event_name = Some(value.to_owned()),
                "data" => {
                    if self.has_data {
                        self.data.push('\n');
                    }
                    self.data.push_str(value);
                    self.has_data = true;
                }
                // `id`, `retry`, and extension fields do not affect the
                // generic event payload and are intentionally ignored.
                _ => {}
            }
        }
        Ok(())
    }

    fn dispatch(&mut self, events: &mut Vec<SseEvent>) {
        if self.has_data {
            events.push(SseEvent {
                event: self
                    .event_name
                    .take()
                    .unwrap_or_else(|| "message".to_owned()),
                data: std::mem::take(&mut self.data),
            });
            self.has_data = false;
        } else {
            self.event_name = None;
        }
        self.event_bytes = 0;
    }
}

#[cfg(test)]
mod tests {
    use super::{SseDecoder, SseError, SseEvent};

    fn event(event: &str, data: &str) -> SseEvent {
        SseEvent {
            event: event.to_owned(),
            data: data.to_owned(),
        }
    }

    #[test]
    fn decodes_lf_crlf_and_multiple_events_in_one_chunk() {
        let mut decoder = SseDecoder::new(1024);
        let events = decoder
            .push(b"event: one\ndata: first\ndata: second\n\nevent: two\r\ndata: third\r\n\r\n")
            .expect("valid SSE");
        assert_eq!(
            events,
            vec![event("one", "first\nsecond"), event("two", "third")]
        );
    }

    #[test]
    fn handles_comments_and_default_message_event() {
        let mut decoder = SseDecoder::default();
        assert!(decoder.push(b": heartbeat\n\n").unwrap().is_empty());
        assert_eq!(
            decoder.push(b"data: hello\n\n").unwrap(),
            vec![event("message", "hello")]
        );
    }

    #[test]
    fn oversized_unterminated_comment_line_is_rejected() {
        let mut decoder = SseDecoder::new(16);
        assert!(matches!(
            decoder.push(&[b':'; 17]),
            Err(SseError::LineTooLarge { limit: 16 })
        ));
    }

    #[test]
    fn many_short_heartbeats_do_not_consume_the_event_budget() {
        let mut decoder = SseDecoder::new(16);
        for _ in 0..128 {
            decoder.push(b": ok\n").expect("short heartbeat");
        }
        assert_eq!(
            decoder.push(b"data: ok\n\n").unwrap(),
            vec![event("message", "ok")]
        );
    }

    #[test]
    fn dispatches_legal_empty_data_events() {
        let mut decoder = SseDecoder::new(1024);
        assert_eq!(
            decoder.push(b"event: ping\ndata:\n\n").unwrap(),
            vec![event("ping", "")]
        );
        assert_eq!(
            decoder.push(b"data\ndata:\n\n").unwrap(),
            vec![event("message", "\n")]
        );
    }

    #[test]
    fn preserves_utf8_split_across_chunks() {
        let mut decoder = SseDecoder::new(1024);
        assert!(decoder.push("data: 你".as_bytes()).unwrap().is_empty());
        assert_eq!(
            decoder.push("好\n\n".as_bytes()).unwrap(),
            vec![event("message", "你好")]
        );
    }

    #[test]
    fn finish_dispatches_a_complete_tail_frame() {
        let mut decoder = SseDecoder::new(1024);
        decoder.push(b"event: done\ndata: tail\n").unwrap();
        assert_eq!(decoder.finish().unwrap(), vec![event("done", "tail")]);
    }

    #[test]
    fn finish_dispatches_an_empty_data_tail_frame() {
        let mut decoder = SseDecoder::new(1024);
        decoder.push(b"event: done\ndata:\n").unwrap();
        assert_eq!(decoder.finish().unwrap(), vec![event("done", "")]);
    }

    #[test]
    fn finish_rejects_invalid_utf8_tail() {
        let mut decoder = SseDecoder::new(1024);
        decoder.push(b"data: ").unwrap();
        decoder.line.push(0xff);
        assert!(matches!(decoder.finish(), Err(SseError::IncompleteUtf8)));
    }

    #[test]
    fn rejects_oversized_events() {
        let mut decoder = SseDecoder::new(8);
        assert!(matches!(
            decoder.push(b"data: a\ndata: b\n"),
            Err(SseError::EventTooLarge { limit: 8 })
        ));
    }

    #[test]
    fn incomplete_line_is_completed_by_a_later_chunk() {
        let mut decoder = SseDecoder::new(1024);
        assert!(decoder.push(b"data: hel").unwrap().is_empty());
        assert_eq!(
            decoder.push(b"lo\n\n").unwrap(),
            vec![event("message", "hello")]
        );
    }
}
