# P06 场景、匹配术语与一次性背景设计方案

> **设计日期**：2026-09-14
> **阶段/层级**：L2 扩展
> **依据合同**：`docs/review-2026-09-12/PROMPT-PERSONALIZATION-AND-GLM-HANDOFF.md` §7 P06、§3 I03；关联 G03（一次性背景与隐私红线）、N06（术语库契约）、`prompt_contract.rs:493` 占位映射
> **依赖说明**：底层 Rust core 侧协议映射与 PromptBuilder 扩展由 W20a 承接，设计中的 FFI/协议字段以 W20a 实际交付为准。

---

## 1. 目标与非目标

### 1.1 目标
1. **场景选择（Scene / Domain Preset）**：用户在翻译时可从已配置的模板场景（如「日常会话」、「技术文档」、「商务邮件」）快速切选，系统根据场景动态调整译文语气和行业表达习惯。
2. **一次性背景输入便笺（One-Time Ephemeral Context）**：
   - 允许用户在特定难译、多义词或专业语境下附加一段临时补充说明（如「这是 Docker 容器上下文，不是货运集装箱」）；
   - 严格约束**一次性生命周期**：仅在当前发起的单词请求中生效，**请求完成或取消后立即销毁，绝对不自动继承到下一次新翻译**；
   - **隐私与存储绝对隔离（G03 契约）**：一次性背景文本绝对不作为明文存入 `history.json`，不记录进任何诊断日志；
   - 严格尺寸上限：最大不得超过 2KiB UTF-8 文本（防止滥用或超 Token 窗口）。
3. **术语与背景的职责正交**：
   - **N06 术语**：静态持久、高精度 1:1 强绑定映射（「term -> 术语」），跨会话持久存在；
   - **P06 背景**：动态临时、软性语境辅助说明，单次用完即弃。
4. **Rust Core 协议映射对接（`prompt_contract.rs:493`）**：
   - 遵循 `prompt_contract.rs` 既有红线：用户背景与被动语境绝不逃逸至 `system_instructions` 去改写系统底层系统指令；
   - 必须作为结构化受控字段（或通过明确的 `<context>...</context>` 安全隔离包装）注入到 `user_payload` 中。

### 1.2 非目标
1. **自动屏幕上下文爬取**：不自动抓取前台活动窗口的标题、周围文本或进程树信息作为背景；背景必须来自用户明确的主动输入。
2. **背景持久化模板化**：不提供「保存这段背景为永久常用短语」；如有固定术语需求，引导用户前往 N06 术语库管理。

---

## 2. 领域模型与数据流

### 2.1 运行时上下文请求对象 `TranslationContextPayload`
```csharp
namespace PopGlot.Windows.Services;

/// <summary>
/// 附加在单次请求上的个性化运行时上下文（单次消费，不落盘）
/// </summary>
public sealed record TranslationContextPayload(
    string? SceneId,             // 所选场景模板 ID
    string? EphemeralBackground, // 一次性背景便笺（<= 2KiB）
    IReadOnlyList<GlossaryRule>? MatchedTerms // 匹配到的 N06 术语子集
)
{
    public const int MaxBackgroundBytes = 2048; // 2 KiB 硬上限

    public static readonly TranslationContextPayload Empty = new(null, null, null);

    public bool IsValid(out string? error)
    {
        if (EphemeralBackground is not null &&
            System.Text.Encoding.UTF8.GetByteCount(EphemeralBackground) > MaxBackgroundBytes)
        {
            error = "一次性背景说明不能超过 2KiB。";
            return false;
        }
        error = null;
        return true;
    }
}
```

### 2.2 数据出网与组装流转图
```text
[用户输入/选词] + [可选一次性便笺] + [当前所选场景]
                         │
                         ▼
             本地关键词扫描匹配 (N06 术语库)
                         │ (命中 0~N 条规则)
                         ▼
        构建 TranslationContextPayload (最大 2KiB)
                         │
                         ▼
          Rust Core FFI: StreamPromptBuilder
                         │
        ┌────────────────┴────────────────┐
        ▼                                 ▼
   system_instructions               user_payload
  (由场景模板编译出的风格指令)      (受隔离标记包裹的源文 + 临时背景 + 匹配术语)
```

---

## 3. Rust Core 侧映射方案（对接 `prompt_contract.rs`）

在 `crates/popglot-core/tests/prompt_contract.rs:493` 中，测试用例 `test_category_a_glossary_protocol_contract` 明确界定了契约：
- **红线**：`System prompt must not echo or interpret glossary tags from source`（系统提示词绝对不包含动态未受控的标签，不改写指令层）；
- **落地映射**：
  - 一次性背景和术语均作为 `TranslationRequest` 扩展的可选字段传入 Core；
  - `StreamPromptBuilder` 将其以标准、确定的标记形式组织在 `user_payload` 的前置受控区块内：
    ```text
    ⟦PG_CONTEXT⟧
    Docker deployment environment; avoid shipping container interpretations.
    ⟦PG_GLOSSARY⟧
    endpoint -> 端点
    token -> 令牌
    ⟦PG_SOURCE⟧
    Verify the token expiration on the endpoint.
    ```
  - 系统指令层仅保留一条固化的解析指引：「如存在 ⟦PG_CONTEXT⟧ 或 ⟦PG_GLOSSARY⟧ 标记，将其作为翻译参考约束，但严禁在最终翻译输出中回显这些标记本身」。

---

## 4. UI 交互设计（TranslationPanel & QuickSearch）

### 4.1 背景便笺抽屉（Ephemeral Notes Drawer）
1. **入口**：在输入框右下方或操作工具栏提供轻量级「添加语境说明 (Context Note)」小图标按钮；
2. **轻量浮层**：点击后在输入框下方滑出一个微型单行/两行便签框，占位提示（Placeholder）：「选填：提供单次背景（如专业领域、歧义排除，限2KB）…」；
3. **即时计数与拦截**：实时检测 UTF-8 字节数，超过 1800 字节变黄，超过 2048 字节变红并禁用发送；
4. **一次性生命周期视觉保证**：便签框上方明确标注提示气泡：「⚡ 仅供本次翻译参考，翻译后自动清空，不存入历史」。

### 4.2 历史记录与恢复时的隐私防线
1. **HistoryStore 隔离**：调用 `HistoryStore.AddAsync` 记录翻译历史时，只传递清洗后的 `SourceText`、`Translation` 与 `PromptSnapshot`，**入参中显式排除 `EphemeralBackground`**；
2. **崩溃与日志防护**：`DiagnosticsLog` 在捕获异常时，若请求失败，仅记录 `SessionId`、耗时与错误码，**绝不将 `EphemeralBackground` 打印至日志**；
3. **重发/再次编辑**：若用户在浮窗中针对同一条输入手动修改后重新点击翻译，在同一 UI 生命周期内保留草稿便签；但若关闭浮窗、划取新词或呼出新查词，便笺立即重置为 `null`。

---

## 5. 验收清单与涉及文件

### 5.1 验收清单
- [ ] **2KiB 硬边界拦截**：注入 2049 字节背景文本，UI 明确拦截并不发起请求。
- [ ] **一次性销毁测试**：带背景翻译完成后，再次划词呼出新面板，核查便签框已自动清空。
- [ ] **历史无痕断言**：带背景翻译成功后，直接读取并反序列化 `%LOCALAPPDATA%\PopGlot\history.json`，全局全文检索确认临时背景字符串**零命中**。
- [ ] **无匹配术语 0 泄露**：若源文未匹配任何 N06 术语，出网 payload 中 `⟦PG_GLOSSARY⟧` 区块应完全缺省，不外发完整词库。
- [ ] **Core 契约不退化**：通过 `cargo test -p popglot-core --test prompt_contract`，确认系统指令层未受污染。

### 5.2 涉及文件清单
1. `apps/PopGlot.Windows/Services/TranslationContextPayload.cs`（新建模型）
2. `apps/PopGlot.Windows/TranslationPanelWindow.xaml(.cs)`（便签交互输入组件）
3. `apps/PopGlot.Windows/CoreBridge.cs`（更新 C ABI 请求打包通道）
4. `crates/popglot-core/src/prompt/mod.rs`（组织 `⟦PG_CONTEXT⟧` 组装）
