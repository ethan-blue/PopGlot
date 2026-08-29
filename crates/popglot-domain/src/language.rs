//! Language tags shared by every shell, prompt, and free-engine fallback.
//!
//! The shell shows localized names; the model prompt needs a stable English
//! name. Keeping both in one table stops the two from drifting apart.

use serde::{Deserialize, Serialize};

/// Sentinel meaning "let the model or engine detect the source language".
pub const AUTO_LANGUAGE: &str = "auto";

/// Every language the UI offers, in display order.
///
/// `tag` is the canonical value persisted in settings and sent to the free web
/// engine. `english_name` is what the model prompt asks for.
pub const SUPPORTED_LANGUAGES: &[(&str, &str, &str)] = &[
    ("auto", "自动检测", "the detected source language"),
    ("zh-CN", "简体中文", "Simplified Chinese"),
    ("zh-TW", "繁體中文", "Traditional Chinese"),
    ("en", "英语", "English"),
    ("ja", "日语", "Japanese"),
    ("ko", "韩语", "Korean"),
    ("fr", "法语", "French"),
    ("de", "德语", "German"),
    ("es", "西班牙语", "Spanish"),
    ("pt", "葡萄牙语", "Portuguese"),
    ("ru", "俄语", "Russian"),
    ("it", "意大利语", "Italian"),
    ("ar", "阿拉伯语", "Arabic"),
    ("hi", "印地语", "Hindi"),
    ("th", "泰语", "Thai"),
    ("vi", "越南语", "Vietnamese"),
];

/// Canonicalizes user or legacy input into a tag from [`SUPPORTED_LANGUAGES`].
///
/// Unknown tags are returned lowercased rather than rejected: a user pointing
/// at a model that supports more languages than this table should still work.
#[must_use]
pub fn normalize_language_tag(tag: &str) -> String {
    let lowered = tag.trim().to_ascii_lowercase();
    match lowered.as_str() {
        "" | "auto" | "detect" | "自动" | "自动检测" => AUTO_LANGUAGE.to_owned(),
        "zh" | "zh-cn" | "zh-hans" | "zh_hans" | "chs" | "中文" | "简体中文" | "汉语" => {
            "zh-CN".to_owned()
        }
        "zh-tw" | "zh-hant" | "zh_hant" | "cht" | "繁体中文" | "繁體中文" => {
            "zh-TW".to_owned()
        }
        "en" | "en-us" | "en-gb" | "eng" | "英语" | "英文" => "en".to_owned(),
        "ja" | "jp" | "ja-jp" | "日语" | "日文" => "ja".to_owned(),
        "ko" | "kr" | "ko-kr" | "韩语" | "韩文" => "ko".to_owned(),
        "fr" | "fr-fr" | "法语" | "法文" => "fr".to_owned(),
        "de" | "de-de" | "德语" | "德文" => "de".to_owned(),
        "es" | "es-es" | "西班牙语" => "es".to_owned(),
        "pt" | "pt-br" | "葡萄牙语" => "pt".to_owned(),
        "ru" | "ru-ru" | "俄语" => "ru".to_owned(),
        "it" | "it-it" | "意大利语" => "it".to_owned(),
        "ar" | "阿拉伯语" => "ar".to_owned(),
        "hi" | "印地语" => "hi".to_owned(),
        "th" | "泰语" => "th".to_owned(),
        "vi" | "越南语" => "vi".to_owned(),
        other => other.to_owned(),
    }
}

/// English name used inside model prompts.
#[must_use]
pub fn language_english_name(tag: &str) -> String {
    let normalized = normalize_language_tag(tag);
    SUPPORTED_LANGUAGES
        .iter()
        .find(|(candidate, _, _)| *candidate == normalized)
        .map_or(normalized, |(_, _, english)| (*english).to_owned())
}

/// Localized name used in shell UI and status text.
#[must_use]
pub fn language_display_name(tag: &str) -> String {
    let normalized = normalize_language_tag(tag);
    SUPPORTED_LANGUAGES
        .iter()
        .find(|(candidate, _, _)| *candidate == normalized)
        .map_or(normalized, |(_, display, _)| (*display).to_owned())
}

/// A normalized source/target pair for one translation request.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct LanguagePair {
    pub source: String,
    pub target: String,
}

impl Default for LanguagePair {
    fn default() -> Self {
        Self {
            source: AUTO_LANGUAGE.to_owned(),
            target: "zh-CN".to_owned(),
        }
    }
}

impl LanguagePair {
    #[must_use]
    pub fn new(source: &str, target: &str) -> Self {
        let target = normalize_language_tag(target);
        // An empty or auto target has no meaning: something must be produced.
        let target = if target.is_empty() || target == AUTO_LANGUAGE {
            "zh-CN".to_owned()
        } else {
            target
        };
        Self {
            source: normalize_language_tag(source),
            target,
        }
    }

    #[must_use]
    pub fn source_is_auto(&self) -> bool {
        self.source == AUTO_LANGUAGE
    }

    #[must_use]
    pub fn source_english_name(&self) -> String {
        language_english_name(&self.source)
    }

    #[must_use]
    pub fn target_english_name(&self) -> String {
        language_english_name(&self.target)
    }

    /// The instruction fragment describing this pair to a model.
    #[must_use]
    pub fn instruction(&self) -> String {
        if self.source_is_auto() {
            format!(
                "Detect the source language automatically and translate the content into {}.",
                self.target_english_name()
            )
        } else {
            format!(
                "Translate the content from {} into {}.",
                self.source_english_name(),
                self.target_english_name()
            )
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn legacy_and_localized_tags_normalize() {
        assert_eq!(normalize_language_tag("ZH"), "zh-CN");
        assert_eq!(normalize_language_tag("中文"), "zh-CN");
        assert_eq!(normalize_language_tag("繁體中文"), "zh-TW");
        assert_eq!(normalize_language_tag("EN-US"), "en");
        assert_eq!(normalize_language_tag(""), AUTO_LANGUAGE);
    }

    #[test]
    fn unknown_tags_pass_through_instead_of_failing() {
        assert_eq!(normalize_language_tag("nl"), "nl");
        assert_eq!(language_english_name("nl"), "nl");
    }

    #[test]
    fn auto_target_falls_back_to_a_real_language() {
        let pair = LanguagePair::new("en", "auto");
        assert_eq!(pair.target, "zh-CN");
    }

    #[test]
    fn instruction_mentions_both_ends_when_source_is_explicit() {
        let pair = LanguagePair::new("en", "ja");
        let instruction = pair.instruction();
        assert!(instruction.contains("English"));
        assert!(instruction.contains("Japanese"));
    }

    #[test]
    fn auto_source_asks_the_model_to_detect() {
        let pair = LanguagePair::new("auto", "en");
        assert!(pair.instruction().starts_with("Detect the source language"));
    }
}
