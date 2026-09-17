//! Safe atomic persistence and lifecycle management for prompt templates.
//!
//! Implements contracts P01 (domain model and safe storage) and P07 (quotas and backups)
//! from `docs/review-2026-09-12/PROMPT-PERSONALIZATION-AND-GLM-HANDOFF.md` §4 & §7.

use popglot_domain::prompt::{
    BUILTIN_FAITHFUL_ID, BUILTIN_FORMAL_ID, BUILTIN_NATURAL_ID, CompileError, MAX_CUSTOM_TEMPLATES,
    MAX_PROMPT_FILE_BYTES, MAX_RETAINED_REVISIONS, PROMPT_SCHEMA_VERSION, PastRevision,
    PromptTemplate,
};
use serde::{Deserialize, Serialize};
use std::fs::{self, File};
use std::io::Write;
use std::path::{Path, PathBuf};

const PROMPT_FILE_NAME: &str = "prompt-templates.json";

/// Errors that can occur during prompt store operations.
#[derive(Debug, thiserror::Error)]
pub enum PromptStoreError {
    #[error("内置模板受系统保护，不可直接修改或删除")]
    BuiltInProtected,

    #[error("自定义模板数量已达上限 ({MAX_CUSTOM_TEMPLATES} 个)，无法继续添加")]
    QuotaExceeded,

    #[error("模板未找到: {0}")]
    NotFound(String),

    #[error("模板校验失败: {0}")]
    Validation(#[from] CompileError),

    #[error("模板存储文件超过 4MiB 上限")]
    FileTooLarge,

    #[error("检测到更高版本的 schema (v{0})，为避免损坏数据已拒绝写入")]
    FutureSchema(u32),

    #[error("I/O 错误: {0}")]
    Io(#[from] std::io::Error),

    #[error("JSON 序列化或解析错误: {0}")]
    Serialization(#[from] serde_json::Error),
}

/// Persistent configuration shape for prompt templates.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PromptConfig {
    /// Missing field means a legacy file from before versioning existed; it
    /// deserializes as 0 and is normalized to [`PROMPT_SCHEMA_VERSION`] on open.
    #[serde(default)]
    pub schema_version: u32,
    #[serde(default = "default_active_template")]
    pub active_template_id: Option<String>,
    #[serde(default)]
    pub custom_templates: Vec<PromptTemplate>,
}

/// Serde default factory: the `Option` shape is required by the field type.
#[allow(clippy::unnecessary_wraps)]
fn default_active_template() -> Option<String> {
    Some(BUILTIN_FAITHFUL_ID.to_owned())
}

impl Default for PromptConfig {
    fn default() -> Self {
        Self {
            schema_version: PROMPT_SCHEMA_VERSION,
            active_template_id: default_active_template(),
            custom_templates: Vec::new(),
        }
    }
}

/// Manages atomic file operations and runtime caching for prompt templates.
#[derive(Debug)]
pub struct PromptStore {
    path: PathBuf,
    config: PromptConfig,
    startup_notice: Option<String>,
}

impl PromptStore {
    /// Opens or initializes a prompt template store rooted at `directory`.
    ///
    /// # Errors
    ///
    /// Returns [`PromptStoreError::Io`] if the directory cannot be created.
    /// Corrupt files are backed up safely with a timestamp and defaults are returned.
    pub fn open(config_directory: impl AsRef<Path>) -> Result<Self, PromptStoreError> {
        let directory = config_directory.as_ref();
        fs::create_dir_all(directory)?;
        let path = directory.join(PROMPT_FILE_NAME);
        let mut startup_notice = None;
        let mut legacy_schema = false;

        let config = if path.exists() {
            let metadata = fs::metadata(&path)?;
            if metadata.len() > MAX_PROMPT_FILE_BYTES {
                let timestamp = current_timestamp();
                let corrupt_name = format!("prompt-templates.oversized-{timestamp}.json");
                let corrupt_path = directory.join(&corrupt_name);
                let _ = fs::rename(&path, &corrupt_path);
                startup_notice = Some(format!(
                    "prompt-templates.json 超过 4MiB 上限，已重置为默认模板；原文件保留为 {corrupt_name}。"
                ));
                PromptConfig::default()
            } else {
                let bytes = fs::read(&path)?;
                let loaded = String::from_utf8(bytes)
                    .ok()
                    .and_then(|json| serde_json::from_str::<PromptConfig>(&json).ok());
                if let Some(mut loaded) = loaded {
                    if loaded.schema_version > PROMPT_SCHEMA_VERSION {
                        startup_notice = Some(format!(
                            "prompt-templates.json 为更新的版本 (v{})，以只读模式运行，不覆盖数据。",
                            loaded.schema_version
                        ));
                    } else if loaded.schema_version < PROMPT_SCHEMA_VERSION {
                        // Older schemas stay fully compatible: keep every
                        // template byte-for-byte and only lift the marker to
                        // the current version (normalized below by a
                        // best-effort write-back).
                        loaded.schema_version = PROMPT_SCHEMA_VERSION;
                        legacy_schema = true;
                    }
                    loaded
                } else {
                    let timestamp = current_timestamp();
                    let corrupt_name = format!("prompt-templates.corrupt-{timestamp}.json");
                    let corrupt_path = directory.join(&corrupt_name);
                    let _ = fs::rename(&path, &corrupt_path);
                    tracing::warn!("Corrupted prompt templates backed up to {:?}", corrupt_path);
                    startup_notice = Some(format!(
                        "prompt-templates.json 无法解析，已重置为默认模板；原文件保留为 {corrupt_name}。"
                    ));
                    PromptConfig::default()
                }
            }
        } else {
            PromptConfig::default()
        };

        let store = Self {
            path,
            config,
            startup_notice,
        };
        if legacy_schema {
            // Best-effort write-back of the normalized schema marker: a failed
            // write must not block startup because the in-memory config is
            // already normalized and the next successful mutation persists the
            // whole config at the current version anyway.
            if let Err(error) = store.persist(&store.config) {
                tracing::warn!("Failed to persist normalized prompt schema: {error}");
            }
        }
        Ok(store)
    }

    /// Creates an in-memory store for isolated testing.
    #[must_use]
    pub fn memory(path: PathBuf) -> Self {
        Self {
            path,
            config: PromptConfig::default(),
            startup_notice: None,
        }
    }

    /// Returns and clears any pending startup notice.
    pub fn take_startup_notice(&mut self) -> Option<String> {
        self.startup_notice.take()
    }

    /// Returns the storage file path.
    #[must_use]
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Returns all available templates: built-ins followed by custom templates.
    #[must_use]
    pub fn list_templates(&self) -> Vec<PromptTemplate> {
        let mut all = PromptTemplate::builtins();
        all.extend(self.config.custom_templates.clone());
        all
    }

    /// Gets a template by ID, checking built-ins first, then custom templates.
    #[must_use]
    pub fn get_template(&self, id: &str) -> Option<PromptTemplate> {
        PromptTemplate::builtins()
            .into_iter()
            .find(|t| t.id == id)
            .or_else(|| {
                self.config
                    .custom_templates
                    .iter()
                    .find(|t| t.id == id)
                    .cloned()
            })
    }

    /// Returns the currently active template (falls back to "faithful" if none set or invalid).
    #[must_use]
    pub fn active_template(&self) -> PromptTemplate {
        if let Some(id) = &self.config.active_template_id
            && let Some(t) = self.get_template(id)
            && t.enabled
        {
            return t;
        }
        PromptTemplate::builtin_faithful()
    }

    /// Returns the active template ID.
    #[must_use]
    pub fn active_template_id(&self) -> Option<&str> {
        self.config.active_template_id.as_deref()
    }

    /// Sets the active template ID and atomically commits to disk.
    ///
    /// # Errors
    ///
    /// Returns [`PromptStoreError::NotFound`] if the template does not exist.
    pub fn set_active_template_id(&mut self, id: Option<&str>) -> Result<(), PromptStoreError> {
        if let Some(template_id) = id
            && self.get_template(template_id).is_none()
        {
            return Err(PromptStoreError::NotFound(template_id.to_owned()));
        }
        let mut next = self.config.clone();
        next.active_template_id = id.map(str::to_owned);
        self.persist(&next)?;
        self.config = next;
        Ok(())
    }

    /// Saves or updates a custom template. Built-in templates cannot be saved.
    ///
    /// Automatically manages revisions, timestamps, scalar validations, and the 50-item quota.
    ///
    /// # Errors
    ///
    /// Returns error on built-in ID collision, quota overflow, or validation failure.
    pub fn save_custom_template(
        &mut self,
        mut template: PromptTemplate,
    ) -> Result<PromptTemplate, PromptStoreError> {
        if is_builtin_id(&template.id) || template.is_built_in {
            return Err(PromptStoreError::BuiltInProtected);
        }

        template.is_built_in = false;
        template.validate()?;

        let now = current_timestamp();
        let mut next = self.config.clone();

        if let Some(pos) = next
            .custom_templates
            .iter()
            .position(|t| t.id == template.id)
        {
            let existing = &next.custom_templates[pos];
            let content_changed = existing.instruction != template.instruction
                || existing.domain != template.domain
                || existing.audience != template.audience;

            if content_changed {
                let mut past = existing.past_revisions.clone();
                past.push(PastRevision {
                    revision: existing.revision,
                    instruction: existing.instruction.clone(),
                    domain: existing.domain.clone(),
                    audience: existing.audience.clone(),
                    updated_at: existing.updated_at,
                });
                while past.len() > MAX_RETAINED_REVISIONS {
                    past.remove(0);
                }
                template.past_revisions = past;
                template.revision = existing.revision + 1;
                template.updated_at = now;
            } else {
                template.revision = existing.revision;
                template.past_revisions.clone_from(&existing.past_revisions);
                template.updated_at = existing.updated_at;
            }
            template.created_at = existing.created_at;
            next.custom_templates[pos] = template.clone();
        } else {
            if next.custom_templates.len() >= MAX_CUSTOM_TEMPLATES {
                return Err(PromptStoreError::QuotaExceeded);
            }
            template.revision = 1;
            template.created_at = now;
            template.updated_at = now;
            template.past_revisions = Vec::new();
            next.custom_templates.push(template.clone());
        }

        self.persist(&next)?;
        self.config = next;
        Ok(template)
    }

    /// Deletes a custom template by ID.
    ///
    /// Built-in templates cannot be deleted. If the active template was deleted,
    /// it resets to "faithful".
    ///
    /// # Errors
    ///
    /// Returns error if the template is built-in or not found.
    pub fn delete_custom_template(&mut self, id: &str) -> Result<(), PromptStoreError> {
        if is_builtin_id(id) {
            return Err(PromptStoreError::BuiltInProtected);
        }

        let pos = self
            .config
            .custom_templates
            .iter()
            .position(|t| t.id == id)
            .ok_or_else(|| PromptStoreError::NotFound(id.to_owned()))?;

        let mut next = self.config.clone();
        next.custom_templates.remove(pos);

        if next.active_template_id.as_deref() == Some(id) {
            next.active_template_id = Some(BUILTIN_FAITHFUL_ID.to_owned());
        }

        self.persist(&next)?;
        self.config = next;
        Ok(())
    }

    /// Atomically persists the candidate configuration to disk.
    fn persist(&self, config: &PromptConfig) -> Result<(), PromptStoreError> {
        if config.schema_version > PROMPT_SCHEMA_VERSION {
            return Err(PromptStoreError::FutureSchema(config.schema_version));
        }

        let json = serde_json::to_string_pretty(config)?;
        if json.len() as u64 > MAX_PROMPT_FILE_BYTES {
            return Err(PromptStoreError::FileTooLarge);
        }

        if let Some(parent) = self.path.parent() {
            fs::create_dir_all(parent)?;
        }

        let temp_path = self.path.with_extension("tmp");
        let bak_path = self.path.with_extension("bak");

        {
            let mut file = File::create(&temp_path)?;
            file.write_all(json.as_bytes())?;
            file.sync_all()?;
        }

        if self.path.exists() {
            let _ = fs::copy(&self.path, &bak_path);
        }

        fs::rename(&temp_path, &self.path)?;
        Ok(())
    }
}

fn is_builtin_id(id: &str) -> bool {
    id == BUILTIN_FAITHFUL_ID || id == BUILTIN_NATURAL_ID || id == BUILTIN_FORMAL_ID
}

fn current_timestamp() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |d| d.as_secs())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_store_contains_builtins_and_defaults_to_faithful() {
        let temp_dir =
            std::env::temp_dir().join(format!("popglot-prompt-test-{}", current_timestamp()));
        let store = PromptStore::open(&temp_dir).expect("open store");

        let templates = store.list_templates();
        assert_eq!(templates.len(), 3);
        assert_eq!(store.active_template().id, BUILTIN_FAITHFUL_ID);

        let _ = fs::remove_dir_all(temp_dir);
    }

    #[test]
    fn custom_template_crud_and_quota() {
        let temp_dir =
            std::env::temp_dir().join(format!("popglot-prompt-crud-{}", current_timestamp()));
        let mut store = PromptStore::open(&temp_dir).expect("open store");

        // Built-ins cannot be modified or deleted
        let faithful = PromptTemplate::builtin_faithful();
        assert!(matches!(
            store.save_custom_template(faithful),
            Err(PromptStoreError::BuiltInProtected)
        ));
        assert!(matches!(
            store.delete_custom_template(BUILTIN_FAITHFUL_ID),
            Err(PromptStoreError::BuiltInProtected)
        ));

        // Create custom template
        let custom = PromptTemplate {
            id: "my-style".to_owned(),
            schema_version: PROMPT_SCHEMA_VERSION,
            name: "我的风格".to_owned(),
            description: "用于特定领域的翻译".to_owned(),
            instruction: "请翻译为 {{target_language}}，注意保持严谨。".to_owned(),
            domain: "技术".to_owned(),
            audience: "开发者".to_owned(),
            enabled: true,
            is_built_in: false,
            revision: 0,
            created_at: 0,
            updated_at: 0,
            past_revisions: Vec::new(),
        };

        let saved = store.save_custom_template(custom.clone()).expect("save ok");
        assert_eq!(saved.revision, 1);
        assert_eq!(store.list_templates().len(), 4);

        // Update content increments revision and tracks past revision
        let mut updated = saved.clone();
        updated.instruction = "新翻译为 {{target_language}}，领域: {{domain}}。".to_owned();
        let saved_v2 = store.save_custom_template(updated).expect("update ok");
        assert_eq!(saved_v2.revision, 2);
        assert_eq!(saved_v2.past_revisions.len(), 1);
        assert_eq!(saved_v2.past_revisions[0].revision, 1);

        // Metadata only update keeps revision
        let mut meta_only = saved_v2.clone();
        meta_only.description = "更新描述".to_owned();
        let saved_v2_meta = store
            .save_custom_template(meta_only)
            .expect("meta update ok");
        assert_eq!(saved_v2_meta.revision, 2);

        // Set active
        store
            .set_active_template_id(Some("my-style"))
            .expect("set active");
        assert_eq!(store.active_template().id, "my-style");

        // Reload from disk
        let reloaded = PromptStore::open(&temp_dir).expect("reopen");
        assert_eq!(reloaded.list_templates().len(), 4);
        assert_eq!(reloaded.active_template().id, "my-style");

        // Delete custom template resets active to faithful
        let mut store = reloaded;
        store.delete_custom_template("my-style").expect("delete ok");
        assert_eq!(store.list_templates().len(), 3);
        assert_eq!(store.active_template().id, BUILTIN_FAITHFUL_ID);

        let _ = fs::remove_dir_all(temp_dir);
    }

    #[test]
    fn corrupt_prompt_file_is_backed_up_and_reset_safely() {
        let temp_dir =
            std::env::temp_dir().join(format!("popglot-prompt-corrupt-{}", current_timestamp()));
        fs::create_dir_all(&temp_dir).expect("create dir");
        let file_path = temp_dir.join(PROMPT_FILE_NAME);
        fs::write(&file_path, "{ not valid json").expect("write corrupt");

        let mut store = PromptStore::open(&temp_dir).expect("open corrupt store");
        assert!(store.take_startup_notice().is_some());
        assert_eq!(store.list_templates().len(), 3);

        // Check corrupt backup file exists
        let entries = fs::read_dir(&temp_dir).expect("read dir");
        let has_corrupt_backup = entries
            .filter_map(Result::ok)
            .any(|e| e.file_name().to_string_lossy().contains("corrupt"));
        assert!(has_corrupt_backup);

        let _ = fs::remove_dir_all(temp_dir);
    }

    fn scratch_directory(label: &str) -> PathBuf {
        let suffix = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .expect("clock")
            .as_nanos();
        std::env::temp_dir().join(format!("popglot-prompt-{label}-{suffix}"))
    }

    fn custom_template(id: &str) -> PromptTemplate {
        PromptTemplate {
            id: id.to_owned(),
            schema_version: PROMPT_SCHEMA_VERSION,
            name: format!("模板 {id}"),
            description: "内联测试模板".to_owned(),
            instruction: "翻译为 {{target_language}}。".to_owned(),
            domain: String::new(),
            audience: String::new(),
            enabled: true,
            is_built_in: false,
            revision: 0,
            created_at: 0,
            updated_at: 0,
            past_revisions: Vec::new(),
        }
    }

    #[test]
    fn non_utf8_prompt_file_is_backed_up_and_reset_safely() {
        let temp_dir = scratch_directory("non-utf8");
        fs::create_dir_all(&temp_dir).expect("create dir");
        // 0xFF 在 UTF-8 中永远非法：文件损坏为非 UTF-8 字节时必须回退默认并
        // 保留原始字节，而不是让 open 失败或静默丢数据。
        fs::write(temp_dir.join(PROMPT_FILE_NAME), [0xFF_u8, 0xFE, 0x7B, 0x7D])
            .expect("write non-utf8 prompts");

        let mut store = PromptStore::open(&temp_dir).expect("store must still open");
        let notice = store.take_startup_notice().expect("startup notice");
        assert!(notice.contains("prompt-templates.corrupt-"), "{notice}");
        assert!(store.take_startup_notice().is_none(), "notice is one-shot");
        assert_eq!(store.list_templates().len(), 3);
        assert_eq!(store.config.schema_version, PROMPT_SCHEMA_VERSION);

        let has_backup = fs::read_dir(&temp_dir)
            .expect("read dir")
            .filter_map(Result::ok)
            .any(|entry| {
                entry
                    .file_name()
                    .to_string_lossy()
                    .starts_with("prompt-templates.corrupt-")
            });
        assert!(has_backup, "non-utf8 bytes must be preserved in a backup");

        let _ = fs::remove_dir_all(temp_dir);
    }

    #[test]
    fn future_schema_mutations_fail_without_polluting_memory() {
        let temp_dir = scratch_directory("future-schema");
        fs::create_dir_all(&temp_dir).expect("create dir");
        let future_version = PROMPT_SCHEMA_VERSION + 1;
        let future = PromptConfig {
            schema_version: future_version,
            active_template_id: Some("future-keeper".to_owned()),
            custom_templates: vec![custom_template("future-keeper")],
        };
        fs::write(
            temp_dir.join(PROMPT_FILE_NAME),
            serde_json::to_string_pretty(&future).expect("serialize future config"),
        )
        .expect("write future schema file");

        let mut store = PromptStore::open(&temp_dir).expect("open future store");
        let notice = store.take_startup_notice().expect("read-only notice");
        assert!(notice.contains("只读"), "{notice}");

        let before = store.config.clone();
        let disk_before = fs::read(temp_dir.join(PROMPT_FILE_NAME)).expect("read disk");

        assert!(matches!(
            store.set_active_template_id(Some(BUILTIN_NATURAL_ID)),
            Err(PromptStoreError::FutureSchema(v)) if v == future_version
        ));
        assert!(matches!(
            store.save_custom_template(custom_template("future-addition")),
            Err(PromptStoreError::FutureSchema(_))
        ));
        assert!(matches!(
            store.delete_custom_template("future-keeper"),
            Err(PromptStoreError::FutureSchema(_))
        ));

        // candidate 先过 persist，失败时内存与磁盘都必须保持原样。
        assert_eq!(store.config, before, "failed writes must not touch memory");
        assert_eq!(
            fs::read(temp_dir.join(PROMPT_FILE_NAME)).expect("read disk after"),
            disk_before,
            "failed writes must not touch disk"
        );
        assert!(
            !temp_dir.join("prompt-templates.tmp").exists(),
            "rejected writes must not leave a temp file"
        );

        // 只读不等于不可读：future 数据照常参与查询与激活。
        assert_eq!(store.list_templates().len(), 4);
        assert_eq!(store.active_template().id, "future-keeper");
        assert!(store.get_template("future-keeper").is_some());

        let _ = fs::remove_dir_all(temp_dir);
    }

    #[test]
    fn custom_template_quota_allows_exactly_fifty() {
        let temp_dir = scratch_directory("quota");
        let mut store = PromptStore::open(&temp_dir).expect("open store");

        for index in 0..MAX_CUSTOM_TEMPLATES {
            let template = custom_template(&format!("style-{index:02}"));
            store
                .save_custom_template(template)
                .unwrap_or_else(|error| panic!("save template {index} failed: {error}"));
        }
        assert_eq!(store.config.custom_templates.len(), MAX_CUSTOM_TEMPLATES);

        assert!(matches!(
            store.save_custom_template(custom_template("overflow")),
            Err(PromptStoreError::QuotaExceeded)
        ));
        // 被拒绝的第 51 个不得污染内存，也不得写盘。
        assert_eq!(store.config.custom_templates.len(), MAX_CUSTOM_TEMPLATES);
        let reloaded = PromptStore::open(&temp_dir).expect("reopen");
        assert_eq!(reloaded.list_templates().len(), 3 + MAX_CUSTOM_TEMPLATES);

        let _ = fs::remove_dir_all(temp_dir);
    }

    #[test]
    fn revisions_evict_oldest_beyond_max_retained() {
        let temp_dir = scratch_directory("revisions");
        let mut store = PromptStore::open(&temp_dir).expect("open store");

        let template = custom_template("eviction");
        let mut current = store.save_custom_template(template).expect("save v1");
        assert_eq!(current.revision, 1);

        for version in 2..=7 {
            current.instruction = format!("第 {version} 版 {{{{target_language}}}}");
            current = store
                .save_custom_template(current.clone())
                .unwrap_or_else(|error| panic!("update to v{version} failed: {error}"));
        }

        assert_eq!(current.revision, 7);
        assert_eq!(current.past_revisions.len(), MAX_RETAINED_REVISIONS);
        assert_eq!(
            current.past_revisions[0].revision, 2,
            "v1 must be evicted first"
        );
        assert_eq!(
            current.past_revisions[MAX_RETAINED_REVISIONS - 1].revision,
            6
        );
        assert!(current.past_revisions.iter().all(|rev| rev.revision != 1));

        // 淘汰策略同样作用在持久化副本上。
        let reloaded = PromptStore::open(&temp_dir).expect("reopen");
        let kept = reloaded.get_template("eviction").expect("template kept");
        assert_eq!(kept.revision, 7);
        assert_eq!(kept.past_revisions.len(), MAX_RETAINED_REVISIONS);
        assert_eq!(kept.past_revisions[0].revision, 2);

        let _ = fs::remove_dir_all(temp_dir);
    }

    #[test]
    fn disabled_active_template_falls_back_to_faithful() {
        let temp_dir = scratch_directory("disabled-fallback");
        let mut store = PromptStore::open(&temp_dir).expect("open store");

        store
            .save_custom_template(custom_template("night-style"))
            .expect("save custom");
        store
            .set_active_template_id(Some("night-style"))
            .expect("activate");
        assert_eq!(store.active_template().id, "night-style");

        let mut disabled = store.get_template("night-style").expect("template exists");
        disabled.enabled = false;
        store.save_custom_template(disabled).expect("disable");

        // ID 保留以便用户重新启用，但运行时激活解析回退到内置 faithful。
        assert_eq!(store.active_template_id(), Some("night-style"));
        assert_eq!(store.active_template().id, BUILTIN_FAITHFUL_ID);

        // 回退在重新加载（模拟重启）后依然成立。
        let mut reloaded = PromptStore::open(&temp_dir).expect("reopen");
        assert_eq!(reloaded.active_template().id, BUILTIN_FAITHFUL_ID);

        // 重新启用后恢复激活。
        let mut reactivated = reloaded
            .get_template("night-style")
            .expect("still readable");
        assert!(!reactivated.enabled);
        reactivated.enabled = true;
        reloaded
            .save_custom_template(reactivated)
            .expect("re-enable");
        assert_eq!(reloaded.active_template().id, "night-style");

        let _ = fs::remove_dir_all(temp_dir);
    }

    #[test]
    fn legacy_schema_is_normalized_but_future_schema_stays_read_only() {
        // 旧 schema：完全兼容，打开即归一到当前版本并把迁移写回磁盘。
        let legacy_dir = scratch_directory("legacy-schema");
        fs::create_dir_all(&legacy_dir).expect("create dir");
        let legacy = PromptConfig {
            schema_version: PROMPT_SCHEMA_VERSION - 1,
            active_template_id: Some("legacy-style".to_owned()),
            custom_templates: vec![custom_template("legacy-style")],
        };
        fs::write(
            legacy_dir.join(PROMPT_FILE_NAME),
            serde_json::to_string_pretty(&legacy).expect("serialize legacy"),
        )
        .expect("write legacy file");

        let mut legacy_store = PromptStore::open(&legacy_dir).expect("open legacy store");
        assert!(
            legacy_store.take_startup_notice().is_none(),
            "旧 schema 属于受支持的迁移，不应触发告警"
        );
        assert_eq!(legacy_store.config.schema_version, PROMPT_SCHEMA_VERSION);
        assert_eq!(legacy_store.list_templates().len(), 4);
        assert_eq!(legacy_store.active_template().id, "legacy-style");

        let on_disk: PromptConfig = serde_json::from_str(
            &fs::read_to_string(legacy_dir.join(PROMPT_FILE_NAME)).expect("read normalized file"),
        )
        .expect("parse normalized file");
        assert_eq!(on_disk.schema_version, PROMPT_SCHEMA_VERSION);
        assert_eq!(on_disk.custom_templates.len(), 1);

        // 连 schemaVersion 字段都不存在的更老文件同样安全归一（按 v0 处理）。
        let ancient_dir = scratch_directory("ancient-schema");
        fs::create_dir_all(&ancient_dir).expect("create dir");
        fs::write(
            ancient_dir.join(PROMPT_FILE_NAME),
            r#"{"activeTemplateId":"faithful","customTemplates":[]}"#,
        )
        .expect("write ancient file");
        let mut ancient = PromptStore::open(&ancient_dir).expect("open ancient store");
        assert!(ancient.take_startup_notice().is_none());
        assert_eq!(ancient.config.schema_version, PROMPT_SCHEMA_VERSION);
        assert_eq!(ancient.active_template().id, BUILTIN_FAITHFUL_ID);

        // future schema：只读，文件内容保持逐字节不变。
        let future_dir = scratch_directory("future-untouched");
        fs::create_dir_all(&future_dir).expect("create dir");
        let future = PromptConfig {
            schema_version: PROMPT_SCHEMA_VERSION + 9,
            active_template_id: None,
            custom_templates: Vec::new(),
        };
        fs::write(
            future_dir.join(PROMPT_FILE_NAME),
            serde_json::to_string_pretty(&future).expect("serialize future"),
        )
        .expect("write future file");
        let disk_before = fs::read(future_dir.join(PROMPT_FILE_NAME)).expect("read future file");
        let mut future_store = PromptStore::open(&future_dir).expect("open future store");
        assert!(future_store.take_startup_notice().is_some());
        assert_eq!(
            future_store.config.schema_version,
            PROMPT_SCHEMA_VERSION + 9
        );
        assert_eq!(
            fs::read(future_dir.join(PROMPT_FILE_NAME)).expect("read future after"),
            disk_before,
            "future schema file must stay byte-identical"
        );

        let _ = fs::remove_dir_all(legacy_dir);
        let _ = fs::remove_dir_all(ancient_dir);
        let _ = fs::remove_dir_all(future_dir);
    }
}
