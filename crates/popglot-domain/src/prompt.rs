//! Prompt template domain model, white-listed variable compiler, and validation.
//!
//! Implements contracts P01 (domain model and quotas) and P02 (four-variable pure compiler)
//! from `docs/review-2026-09-12/PROMPT-PERSONALIZATION-AND-GLM-HANDOFF.md` §4 & §7.

use serde::{Deserialize, Serialize};

/// Schema version for prompt template definitions.
pub const PROMPT_SCHEMA_VERSION: u32 = 1;

/// Maximum number of personal (custom) templates a user may store.
pub const MAX_CUSTOM_TEMPLATES: usize = 50;

/// Maximum length of a template name in Unicode scalar values.
pub const MAX_TEMPLATE_NAME_SCALARS: usize = 64;

/// Maximum length of a template description in Unicode scalar values.
pub const MAX_TEMPLATE_DESCRIPTION_SCALARS: usize = 256;

/// Maximum size of template instruction body in UTF-8 bytes (8 KiB).
pub const MAX_TEMPLATE_BODY_BYTES: usize = 8 * 1024;

/// Maximum length of domain in Unicode scalar values.
pub const MAX_DOMAIN_SCALARS: usize = 256;

/// Maximum size of domain in UTF-8 bytes (1 KiB).
pub const MAX_DOMAIN_BYTES: usize = 1024;

/// Maximum length of audience in Unicode scalar values.
pub const MAX_AUDIENCE_SCALARS: usize = 256;

/// Maximum size of audience in UTF-8 bytes (1 KiB).
pub const MAX_AUDIENCE_BYTES: usize = 1024;

/// Maximum size of single compiled personal instruction in UTF-8 bytes (12 KiB).
pub const MAX_COMPILED_BYTES: usize = 12 * 1024;

/// Maximum storage file size in bytes (4 MiB).
pub const MAX_PROMPT_FILE_BYTES: u64 = 4 * 1024 * 1024;

/// Maximum number of past revisions retained per template.
pub const MAX_RETAINED_REVISIONS: usize = 5;

/// Built-in template ID for faithful translation.
pub const BUILTIN_FAITHFUL_ID: &str = "faithful";

/// Built-in template ID for natural expression.
pub const BUILTIN_NATURAL_ID: &str = "natural";

/// Built-in template ID for formal writing.
pub const BUILTIN_FORMAL_ID: &str = "formal";

/// A prompt template definition representing a translation style or domain preference.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PromptTemplate {
    pub id: String,
    pub schema_version: u32,
    pub name: String,
    pub description: String,
    pub instruction: String,
    #[serde(default)]
    pub domain: String,
    #[serde(default)]
    pub audience: String,
    pub enabled: bool,
    pub is_built_in: bool,
    pub revision: u64,
    #[serde(default)]
    pub created_at: u64,
    #[serde(default)]
    pub updated_at: u64,
    #[serde(default)]
    pub past_revisions: Vec<PastRevision>,
}

/// A snapshot of a previous revision of a prompt template.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PastRevision {
    pub revision: u64,
    pub instruction: String,
    #[serde(default)]
    pub domain: String,
    #[serde(default)]
    pub audience: String,
    #[serde(default)]
    pub updated_at: u64,
}

/// Variables passed into the pure compiler to resolve placeholders.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct PromptVariables {
    #[serde(default)]
    pub source_language: String,
    pub target_language: String,
    #[serde(default)]
    pub domain: Option<String>,
    #[serde(default)]
    pub audience: Option<String>,
}

/// The result of compiling a prompt template with concrete variables.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CompiledPrompt {
    pub template_id: String,
    pub revision: u64,
    pub compiled_text: String,
}

/// Errors that can occur during prompt template compilation or validation.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, thiserror::Error)]
#[serde(tag = "kind", content = "details", rename_all = "camelCase")]
pub enum CompileError {
    #[error(
        "未知占位符变量 '{{{{{name}}}}}'，位置: {position}。允许的变量为: source_language, target_language, domain, audience"
    )]
    UnknownVariable { name: String, position: usize },

    #[error("占位符未闭合，位置: {position}")]
    UnclosedPlaceholder { position: usize },

    #[error("目标语言不能为空")]
    EmptyTargetLanguage,

    #[error("模板名称不能为空或超过 {limit} 字符 (实际 {count} 字符)")]
    InvalidNameLength { count: usize, limit: usize },

    #[error("模板描述超过 {limit} 字符 (实际 {count} 字符)")]
    DescriptionTooLong { count: usize, limit: usize },

    #[error("模板正文超过 {limit} 字节上限 (实际 {size} 字节)")]
    InstructionTooLarge { size: usize, limit: usize },

    #[error("领域 (domain) 超过上限 (字符数: {count}/{scalar_limit}, 字节数: {size}/{byte_limit})")]
    DomainTooLarge {
        count: usize,
        scalar_limit: usize,
        size: usize,
        byte_limit: usize,
    },

    #[error(
        "受众 (audience) 超过上限 (字符数: {count}/{scalar_limit}, 字节数: {size}/{byte_limit})"
    )]
    AudienceTooLarge {
        count: usize,
        scalar_limit: usize,
        size: usize,
        byte_limit: usize,
    },

    #[error("编译后指令超过 {limit} 字节上限 (实际 {size} 字节)")]
    CompiledTooLarge { size: usize, limit: usize },

    #[error("模板 ID '{0}' 无效：必须仅包含 ASCII 字母、数字、短横线或下划线，且不能为空")]
    InvalidId(String),
}

impl PromptTemplate {
    /// Returns the built-in "忠实翻译" template.
    #[must_use]
    pub fn builtin_faithful() -> Self {
        Self {
            id: BUILTIN_FAITHFUL_ID.to_owned(),
            schema_version: PROMPT_SCHEMA_VERSION,
            name: "忠实翻译".to_owned(),
            description: "保持原文信息、语气强度与结构，不增补背景，在不改变原意的前提下自然表达。".to_owned(),
            instruction: "忠实传达原文含义，保持信息、否定、条件和语气强度。不增补背景，不把推测变成事实，不擅自省略。目标语言为 {{target_language}}；在不改变原意的前提下自然表达。".to_owned(),
            domain: String::new(),
            audience: String::new(),
            enabled: true,
            is_built_in: true,
            revision: 1,
            created_at: 0,
            updated_at: 0,
            past_revisions: Vec::new(),
        }
    }

    /// Returns the built-in "日常自然表达" template.
    #[must_use]
    pub fn builtin_natural() -> Self {
        Self {
            id: BUILTIN_NATURAL_ID.to_owned(),
            schema_version: PROMPT_SCHEMA_VERSION,
            name: "日常自然表达".to_owned(),
            description: "使用目标语言常见自然的表达，避免生硬逐词对应，保留人物关系与信息完整性。".to_owned(),
            instruction: "使用目标语言常见而自然的表达，避免生硬逐词对应；保留人物关系、情绪、否定和信息完整性。不要为了流畅改写事实，不自动添加网络俚语或不符合原文的亲密语气。".to_owned(),
            domain: String::new(),
            audience: String::new(),
            enabled: true,
            is_built_in: true,
            revision: 1,
            created_at: 0,
            updated_at: 0,
            past_revisions: Vec::new(),
        }
    }

    /// Returns the built-in "正式书面" template.
    #[must_use]
    pub fn builtin_formal() -> Self {
        Self {
            id: BUILTIN_FORMAL_ID.to_owned(),
            schema_version: PROMPT_SCHEMA_VERSION,
            name: "正式书面".to_owned(),
            description: "使用自然、礼貌、正式但不过度客套的用语，保持原文承诺、责任主体与请求强度。".to_owned(),
            instruction: "使用自然、礼貌、正式但不过度客套的 {{target_language}}。保持原文承诺、责任主体、金额、日期、条件与请求强度。原文没有称呼、结尾或承诺时不新增。领域：{{domain}}；受众：{{audience}}。".to_owned(),
            domain: String::new(),
            audience: String::new(),
            enabled: true,
            is_built_in: true,
            revision: 1,
            created_at: 0,
            updated_at: 0,
            past_revisions: Vec::new(),
        }
    }

    /// Returns the default list of all built-in templates.
    #[must_use]
    pub fn builtins() -> Vec<Self> {
        vec![
            Self::builtin_faithful(),
            Self::builtin_natural(),
            Self::builtin_formal(),
        ]
    }

    /// Validates the structure, scalar counts, byte limits, and syntax of this template.
    ///
    /// # Errors
    ///
    /// Returns [`CompileError`] if any field violates contract limits or if the template
    /// body contains unknown variables or syntax errors.
    pub fn validate(&self) -> Result<(), CompileError> {
        if self.id.trim().is_empty()
            || !self
                .id
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_')
        {
            return Err(CompileError::InvalidId(self.id.clone()));
        }

        let name_count = self.name.chars().count();
        if name_count == 0 || name_count > MAX_TEMPLATE_NAME_SCALARS {
            return Err(CompileError::InvalidNameLength {
                count: name_count,
                limit: MAX_TEMPLATE_NAME_SCALARS,
            });
        }

        let desc_count = self.description.chars().count();
        if desc_count > MAX_TEMPLATE_DESCRIPTION_SCALARS {
            return Err(CompileError::DescriptionTooLong {
                count: desc_count,
                limit: MAX_TEMPLATE_DESCRIPTION_SCALARS,
            });
        }

        let domain_count = self.domain.chars().count();
        if domain_count > MAX_DOMAIN_SCALARS || self.domain.len() > MAX_DOMAIN_BYTES {
            return Err(CompileError::DomainTooLarge {
                count: domain_count,
                scalar_limit: MAX_DOMAIN_SCALARS,
                size: self.domain.len(),
                byte_limit: MAX_DOMAIN_BYTES,
            });
        }

        let audience_count = self.audience.chars().count();
        if audience_count > MAX_AUDIENCE_SCALARS || self.audience.len() > MAX_AUDIENCE_BYTES {
            return Err(CompileError::AudienceTooLarge {
                count: audience_count,
                scalar_limit: MAX_AUDIENCE_SCALARS,
                size: self.audience.len(),
                byte_limit: MAX_AUDIENCE_BYTES,
            });
        }

        if self.instruction.len() > MAX_TEMPLATE_BODY_BYTES {
            return Err(CompileError::InstructionTooLarge {
                size: self.instruction.len(),
                limit: MAX_TEMPLATE_BODY_BYTES,
            });
        }

        // Dry-run compilation against a valid dummy variable set to verify variable syntax.
        let dummy = PromptVariables {
            source_language: "auto".to_owned(),
            target_language: "zh-CN".to_owned(),
            domain: None,
            audience: None,
        };
        let _ = compile_instruction(&self.instruction, &self.domain, &self.audience, &dummy)?;

        Ok(())
    }
}

/// Compiles a template into a [`CompiledPrompt`] by resolving white-listed placeholders.
///
/// # Errors
///
/// Returns [`CompileError`] on unknown variables, unclosed placeholders, missing target language,
/// or exceeded byte budgets.
pub fn compile_prompt(
    template: &PromptTemplate,
    variables: &PromptVariables,
) -> Result<CompiledPrompt, CompileError> {
    let compiled_text = compile_instruction(
        &template.instruction,
        &template.domain,
        &template.audience,
        variables,
    )?;
    Ok(CompiledPrompt {
        template_id: template.id.clone(),
        revision: template.revision,
        compiled_text,
    })
}

/// Resolves a single white-listed placeholder name to its replacement text.
///
/// # Errors
///
/// Returns [`CompileError::UnknownVariable`] for names outside the white-list and
/// [`CompileError::EmptyTargetLanguage`] when the target language is blank.
fn resolve_variable<'a>(
    var_name: &str,
    open_pos: usize,
    variables: &'a PromptVariables,
    default_domain: &'a str,
    default_audience: &'a str,
) -> Result<&'a str, CompileError> {
    match var_name {
        "source_language" => {
            if variables.source_language.trim().is_empty()
                || variables
                    .source_language
                    .trim()
                    .eq_ignore_ascii_case("auto")
            {
                Ok("自动识别")
            } else {
                Ok(variables.source_language.trim())
            }
        }
        "target_language" => {
            let target = variables.target_language.trim();
            if target.is_empty() {
                return Err(CompileError::EmptyTargetLanguage);
            }
            Ok(target)
        }
        "domain" => Ok(variables
            .domain
            .as_deref()
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .unwrap_or_else(|| default_domain.trim())),
        "audience" => Ok(variables
            .audience
            .as_deref()
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .unwrap_or_else(|| default_audience.trim())),
        _ => Err(CompileError::UnknownVariable {
            name: var_name.to_owned(),
            position: open_pos,
        }),
    }
}

/// Pure compiler: expands white-listed variables in `instruction` without recursion.
///
/// Supported placeholders:
/// - `{{source_language}}`: Evaluates to `variables.source_language` or "自动识别" if empty/auto.
/// - `{{target_language}}`: Evaluates to `variables.target_language`. Required.
/// - `{{domain}}`: Evaluates to `variables.domain` if non-empty, otherwise template default `domain`.
/// - `{{audience}}`: Evaluates to `variables.audience` if non-empty, otherwise template default `audience`.
///
/// Escaping:
/// - `\{{` expands to literal `{{` without triggering variable substitution.
///
/// # Errors
///
/// Returns [`CompileError`] if the instruction, default domain, or default audience exceed
/// their size budgets, if a placeholder references a variable outside the white-list,
/// if a placeholder is unclosed, or if the target language resolves to an empty string.
///
/// Security invariants (P02):
/// - No recursion: variable expansion output is never re-scanned.
/// - Hard limits: instruction <= 8KiB, compiled <= 12KiB.
/// - Unknown placeholders immediately halt compilation with error and position.
pub fn compile_instruction(
    instruction: &str,
    default_domain: &str,
    default_audience: &str,
    variables: &PromptVariables,
) -> Result<String, CompileError> {
    if instruction.len() > MAX_TEMPLATE_BODY_BYTES {
        return Err(CompileError::InstructionTooLarge {
            size: instruction.len(),
            limit: MAX_TEMPLATE_BODY_BYTES,
        });
    }

    let domain_count = default_domain.chars().count();
    if domain_count > MAX_DOMAIN_SCALARS || default_domain.len() > MAX_DOMAIN_BYTES {
        return Err(CompileError::DomainTooLarge {
            count: domain_count,
            scalar_limit: MAX_DOMAIN_SCALARS,
            size: default_domain.len(),
            byte_limit: MAX_DOMAIN_BYTES,
        });
    }

    let audience_count = default_audience.chars().count();
    if audience_count > MAX_AUDIENCE_SCALARS || default_audience.len() > MAX_AUDIENCE_BYTES {
        return Err(CompileError::AudienceTooLarge {
            count: audience_count,
            scalar_limit: MAX_AUDIENCE_SCALARS,
            size: default_audience.len(),
            byte_limit: MAX_AUDIENCE_BYTES,
        });
    }

    let mut output = String::with_capacity(instruction.len() + 64);
    let bytes = instruction.as_bytes();
    let mut cursor = 0;

    while cursor < bytes.len() {
        let remainder = &instruction[cursor..];

        // 1. Escaped opening brace: \{{ -> emit {{
        if remainder.starts_with(r"\{{") {
            output.push_str("{{");
            cursor += 3;
            continue;
        }

        // 2. Variable placeholder opening: {{
        if let Some(placeholder_content) = remainder.strip_prefix("{{") {
            let open_pos = cursor;
            if let Some(close_idx) = placeholder_content.find("}}") {
                let raw_var = &placeholder_content[..close_idx];
                let var_name = raw_var.trim();
                let resolved = resolve_variable(
                    var_name,
                    open_pos,
                    variables,
                    default_domain,
                    default_audience,
                )?;

                output.push_str(resolved);
                // Advance past }}
                cursor += 2 + close_idx + 2;
                continue;
            }
            return Err(CompileError::UnclosedPlaceholder { position: open_pos });
        }

        // 3. Regular character
        let ch = instruction[cursor..]
            .chars()
            .next()
            .unwrap_or(char::REPLACEMENT_CHARACTER);
        output.push(ch);
        cursor += ch.len_utf8();
    }

    if output.len() > MAX_COMPILED_BYTES {
        return Err(CompileError::CompiledTooLarge {
            size: output.len(),
            limit: MAX_COMPILED_BYTES,
        });
    }

    Ok(output)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builtins_are_valid_and_compile_cleanly() {
        for builtin in PromptTemplate::builtins() {
            assert!(builtin.is_built_in);
            builtin.validate().expect("built-in template must be valid");

            let vars = PromptVariables {
                source_language: "en".to_owned(),
                target_language: "zh-CN".to_owned(),
                domain: Some("计算机科学".to_owned()),
                audience: Some("软件工程师".to_owned()),
            };
            let compiled = compile_prompt(&builtin, &vars).expect("compile must succeed");
            assert_eq!(compiled.template_id, builtin.id);
            assert_eq!(compiled.revision, builtin.revision);
            assert!(!compiled.compiled_text.is_empty());
            assert!(!compiled.compiled_text.contains("{{"));
        }
    }

    #[test]
    fn four_variables_expand_correctly() {
        let template_text = "源: {{source_language}}, 目标: {{target_language}}, 领域: {{domain}}, 受众: {{audience}}";
        let vars = PromptVariables {
            source_language: "en".to_owned(),
            target_language: "ja".to_owned(),
            domain: Some("医学".to_owned()),
            audience: Some("临床医生".to_owned()),
        };
        let compiled = compile_instruction(template_text, "", "", &vars).expect("compile ok");
        assert_eq!(compiled, "源: en, 目标: ja, 领域: 医学, 受众: 临床医生");
    }

    #[test]
    fn auto_source_language_resolves_to_localized_label() {
        let template_text = "识别: {{source_language}} -> {{target_language}}";
        let vars = PromptVariables {
            source_language: "auto".to_owned(),
            target_language: "zh-CN".to_owned(),
            domain: None,
            audience: None,
        };
        let compiled = compile_instruction(template_text, "", "", &vars).expect("compile ok");
        assert_eq!(compiled, "识别: 自动识别 -> zh-CN");

        let empty_source = PromptVariables {
            source_language: String::new(),
            target_language: "zh-CN".to_owned(),
            domain: None,
            audience: None,
        };
        let compiled2 =
            compile_instruction(template_text, "", "", &empty_source).expect("compile ok");
        assert_eq!(compiled2, "识别: 自动识别 -> zh-CN");
    }

    #[test]
    fn unknown_variable_is_rejected_with_position() {
        let template_text = "Translate {{source_text}} to {{target_language}}";
        let vars = PromptVariables {
            source_language: "en".to_owned(),
            target_language: "zh-CN".to_owned(),
            domain: None,
            audience: None,
        };
        let err = compile_instruction(template_text, "", "", &vars).unwrap_err();
        match err {
            CompileError::UnknownVariable { name, position } => {
                assert_eq!(name, "source_text");
                assert_eq!(position, 10);
            }
            other => panic!("expected UnknownVariable, got: {other:?}"),
        }
    }

    #[test]
    fn unclosed_placeholder_is_rejected_with_position() {
        let template_text = "Hello {{target_language world";
        let vars = PromptVariables {
            source_language: "en".to_owned(),
            target_language: "zh-CN".to_owned(),
            domain: None,
            audience: None,
        };
        let err = compile_instruction(template_text, "", "", &vars).unwrap_err();
        match err {
            CompileError::UnclosedPlaceholder { position } => {
                assert_eq!(position, 6);
            }
            other => panic!("expected UnclosedPlaceholder, got: {other:?}"),
        }
    }

    #[test]
    fn escaped_braces_become_literal_without_expansion() {
        let template_text =
            r"Use \{{target_language}} for literal, but real is {{target_language}}";
        let vars = PromptVariables {
            source_language: "en".to_owned(),
            target_language: "zh-CN".to_owned(),
            domain: None,
            audience: None,
        };
        let compiled = compile_instruction(template_text, "", "", &vars).expect("compile ok");
        assert_eq!(
            compiled,
            "Use {{target_language}} for literal, but real is zh-CN"
        );
    }

    #[test]
    fn non_recursive_variable_expansion() {
        let template_text = "Domain is {{domain}}, target is {{target_language}}";
        let vars = PromptVariables {
            source_language: "en".to_owned(),
            target_language: "zh-CN".to_owned(),
            domain: Some("{{target_language}} injection attempt".to_owned()),
            audience: None,
        };
        let compiled = compile_instruction(template_text, "", "", &vars).expect("compile ok");
        assert_eq!(
            compiled,
            "Domain is {{target_language}} injection attempt, target is zh-CN"
        );
    }

    #[test]
    fn empty_target_language_fails() {
        let template_text = "Target is {{target_language}}";
        let vars = PromptVariables {
            source_language: "en".to_owned(),
            target_language: "   ".to_owned(),
            domain: None,
            audience: None,
        };
        let err = compile_instruction(template_text, "", "", &vars).unwrap_err();
        assert_eq!(err, CompileError::EmptyTargetLanguage);
    }

    #[test]
    fn oversized_template_body_is_rejected() {
        let oversized = "a".repeat(MAX_TEMPLATE_BODY_BYTES + 1);
        let vars = PromptVariables {
            source_language: "en".to_owned(),
            target_language: "zh-CN".to_owned(),
            domain: None,
            audience: None,
        };
        let err = compile_instruction(&oversized, "", "", &vars).unwrap_err();
        assert!(matches!(err, CompileError::InstructionTooLarge { .. }));
    }

    #[test]
    fn template_validation_checks_field_limits_and_syntax() {
        let mut template = PromptTemplate::builtin_faithful();
        template.id = "valid-id_1".to_owned();
        template.is_built_in = false;
        assert!(template.validate().is_ok());

        // Invalid ID
        template.id = "invalid id with spaces".to_owned();
        assert!(matches!(
            template.validate(),
            Err(CompileError::InvalidId(_))
        ));

        // Invalid name length
        template.id = "valid-id".to_owned();
        template.name = String::new();
        assert!(matches!(
            template.validate(),
            Err(CompileError::InvalidNameLength { count: 0, .. })
        ));

        // Invalid unknown variable in instruction
        template.name = "Test".to_owned();
        template.instruction = "Translate {{api_key_leak}}".to_owned();
        assert!(matches!(
            template.validate(),
            Err(CompileError::UnknownVariable { .. })
        ));
    }
}
