# P05 请求快照、缓存与历史可追溯设计方案

> **设计日期**：2026-09-14
> **阶段/层级**：L2 扩展
> **依据合同**：`docs/review-2026-09-12/PROMPT-PERSONALIZATION-AND-GLM-HANDOFF.md` §4.4、§7 P05；关联 N01（会话暂存）、N05（语言与风格）、N07（历史检索与版本）、G04（缓存命中零出网授权）
> **依赖说明**：底层 Rust core 侧模板标识及编译由 W20a 承接，本设计中引用的核心 ABI 约定以 W20a 实际交付物为准。

---

## 1. 目标与非目标

### 1.1 目标
1. **精确请求快照**：每次发起的翻译请求均携带不可变快照元数据（`TemplateId`、`TemplateRevision`、`CompiledPromptVersion`、`EngineProfileId`、`EngineModel`、`SourceLanguage`、`TargetLanguage`）。
2. **历史可追溯性**：历史记录中清晰展示「当时使用了哪个提示词模板版本」及「生效模型」；即便后续用户修改、重命名或删除了该模板，既有历史条目中的版本标识与译文结果绝不被回溯污染。
3. **安全缓存命中（G04 契约）**：
   - 缓存 Key 计算严格绑定四元组：`Hash(SourceText + TemplateId + TemplateRevision + CompiledBytesSha256 + EngineModel + LanguagePair)`；
   - 缓存命中时立即复用结果，**绝对不消费发送授权（Send Count 不增加，Zero-Network）**；
   - 相同模板 ID 但内容 revision 变更、或切换了模型，强制使旧缓存失效（Cache Miss），杜绝陈旧输出穿透。
4. **历史存储预算兼容**：快照元数据深度内嵌至 `TranslationHistoryEntry`，单条元数据增量控制在 ≤ 200 字节，严密守住 `HistoryStore` 现有的 `4MB / 200 条 / 90 天` 物理硬上限。

### 1.2 非目标
1. **历史中全量存储编译后的完整 System Prompt**：绝不在每条历史记录中全量复制 8KiB 的 system instructions 文本（避免 200 条迅速挤爆 4MB 限制）；仅记录轻量级版本锚点与校验 Hash。
2. **测试试译写入正式历史**：模板编辑器中的「可取消试译」操作被标记为 `IsTrial == true`，绝对不落盘写入 `HistoryStore`，不自动触发剪贴板复制或 TTS。

---

## 2. 领域模型与快照数据结构

### 2.1 模板快照元数据结构 `PromptSnapshot`
```csharp
namespace PopGlot.Windows.Services;

/// <summary>
/// 提示词模板版本与编译锚点快照（不可变）
/// </summary>
public sealed record PromptSnapshot(
    string TemplateId,          // 模板唯一 ID（内置为 built-in-faithful 等）
    int Revision,               // 模板修订版本号（用户每次编辑保存递增）
    string TemplateNameSnapshot,// 当时展示的模板名称（如 "代码注释精修"）
    string CompiledHash,        // 编译产物的 SHA256 前 16 位十六进制
    string EngineProfileId,     // 所属引擎配置 ID
    string EngineModelName      // 所属模型标识（如 "glm-4-flash"）
);
```

### 2.2 扩展现有 `TranslationHistoryEntry`
在 `HistoryStore.cs` 中向条目结构体安全追加可选快照字段（向前兼容 schema）：
```csharp
internal sealed record TranslationHistoryEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    string SourceKind,
    string Source,
    string Translation,
    string Explanation,
    IReadOnlyList<string> ProtectedTerms,
    string SourceLanguage = "auto",
    string TargetLanguage = "zh-CN",
    PromptSnapshot? PromptMeta = null // P05 新增快照锚点
);
```

---

## 3. 缓存命中语义与 G04 授权契约

### 3.1 复合缓存 Key 计算规则
```text
CacheKey = SHA256(
    UTF8(SourceText) + "|" +
    SourceLanguage + "->" + TargetLanguage + "|" +
    EngineProfileId + ":" + EngineModelName + "|" +
    TemplateId + "@r" + Revision + ":" + CompiledHash
)
```
- **内容敏感**：一旦用户微调模板正文，W20a 编译器输出不同的 `CompiledHash` 且 `Revision` 递增，新请求生成的 `CacheKey` 自然与旧缓存隔离。
- **模板删除保护**：用户若在设置中删除了 `my-custom-template`，旧历史记录仍保有 `PromptSnapshot("my-custom-template", 3, "旧模板", ...)`。再次翻译相同内容时，系统回退至默认风格，生成不同的 `CacheKey`，重新执行请求。

### 3.2 命中判定与授权消费流转
```text
[用户发起翻译] ──► 计算 CacheKey
                       │
                       ├─► [Cache Hit] ──► 取出结果 ──► 直接 UI 渲染 (Send Count 不变, 零出网)
                       │
                       └─► [Cache Miss] ──► 检查 OutboundPolicy (TryClaimSend 授权)
                                                 │
                                                 ▼
                                        网络请求与流式返回
                                                 │
                                                 ▼
                                        写入 Cache 与 HistoryStore
```

---

## 4. 存储预算与 HistoryStore 共存防线

1. **体积精细计算**：
   - 原 `TranslationHistoryEntry` 基础字段约 300~800 字节；
   - `PromptSnapshot` 包含短字符串与整数，JSON 序列化增量约 120~180 字节；
   - 200 条历史记录累计快照元数据增量：`200 × 180 B ≈ 36 KB`，仅占 4MiB 物理上限的 **0.88%**，完全在安全冗余范围之内。
2. **反序列化兼容性（Zero-Crash Guarantee）**：
   - 针对老版本产生的 `history.json`（无 `PromptMeta` 字段），JSON 反序列化时自动解析为 `null`；
   - 界面渲染时若 `PromptMeta == null`，优雅降级回显为「默认（内置经典）」；
   - 损坏或未知 Schema 异常继续触发既有的 `LastQuarantinePath` 隔离逻辑，绝不发生崩溃。

---

## 5. UI 呈现与交互说明（LibrarySection & History Panel）

1. **历史记录项详情面板**：
   - 在历史详情卡片底部标签栏中，在「服务模型」、「字符数」旁增加提示词小徽章：
     - 内置风格显示：`[🏷️ 忠实翻译 · 内置]`；
     - 自定义模板显示：`[🏷️ 技术文档 (v3)]`，鼠标悬停 ToolTip 展示完整快照：「模板：技术文档 / 版本：r3 (a1b2c3d4) / 模型：deepseek-chat」。
2. **「重新翻译」语义**：
   - 从历史记录中点击「重新翻译」时，系统并不强行回滚用户当前的全局模板设置，而是弹出微提示：「将以当前最新偏好重新翻译该段内容」，引导生成全新 ID 的活动会话。

---

## 6. 验收清单与涉及文件

### 6.1 验收清单
- [ ] **版本冻结断言**：使用模板 A (v1) 翻译并存入历史；修改模板 A 为 v2；复核历史记录中的条目，确认其版本标识仍严格锁定为 A (v1)。
- [ ] **缓存独立性**：相同原文，分别在 v1 与 v2 下请求，验证 v2 不命中 v1 缓存；相同参数再次请求 v2，验证 100% 缓存命中且 Mock Http 发送计数为 0。
- [ ] **历史容量硬门禁**：高频生成 200 条带有完整 `PromptSnapshot` 的历史，验证落盘文件大小合规（远低于 4MB），超出 200 条时老条目 FIFO 安全滚出。
- [ ] **试译不污染**：在编辑器中点击「试译」，验证 `HistoryStore.Count` 不增加。

### 6.2 涉及文件清单
1. `apps/PopGlot.Windows/Services/PromptSnapshot.cs`（新建领域模型）
2. `apps/PopGlot.Windows/HistoryStore.cs`（扩展条目与兼容反序列化）
3. `apps/PopGlot.Windows/Sections/LibrarySection.xaml(.cs)`（历史详情徽章展示）
4. `tests/PopGlot.Windows.PureTests/HistorySnapshotTests.cs`（单元与回归测试）
