# N01 多会话仓设计方案：会话暂存、误关恢复与工作台接续

> **设计日期**：2026-09-14
> **阶段/层级**：L1 可靠日用核心能力
> **依据合同**：`docs/review-2026-09-12/PRODUCT-EXPANSION-REVIEW-V2.md` §5 N01；关联 C05（关闭/失焦/取消/恢复）、C12（生命周期与内存回收）、C14（极速查词与浮窗）
> **前置依赖**：C05 生命周期合同已由 A05/A06/W17 落地；C12 内存边界约束；无新增 NuGet 依赖

---

## 1. 目标与非目标

### 1.1 目标
1. **多会话暂存**：支持最多 5 个非活动会话暂存，单会话及累计总文本上限 2MiB，超过 30 分钟无访问自动淘汰，优先淘汰最旧的非活动会话。
2. **误关恢复（恢复 ≠ 重发）**：托盘菜单「恢复最近翻译」与快捷键呼出时，精确恢复上一次会话的内容、状态（含 partial 阶段性结果）、滚动偏好与源/目标语言设置；绝不触发重复 HTTP 请求（零网络发送）。
3. **工作台无缝接续**：从浮窗或查词窗口点击「展开至工作台」时，直接携带已有会话及所有结果进入 `MainWindow`，工作台若已有未提交草稿，提供显式保留/替换选择。
4. **内存与生命周期可控**：图像绝对不进入常驻会话仓库（任务结束即释放底层 byte[]/BitmapSource）；内存暂存遵循 C12，退出应用时显式清空，不跨进程泄漏。

### 1.2 非目标
1. **跨进程/跨重启持久化**：本波设计定位于纯内存会话仓（In-Memory Session Store），跨重启恢复草稿属于后续可选扩展，默认应用退出即完全清空。
2. **会话历史检索**：会话暂存不是 `HistoryStore`（历史记录按用户偏好落盘持久化）；会话仓仅面向「未完成/刚完成」的近态多任务切换与防丢回退。
3. **图像暂存重试**：截图任务的源图仅在 OCR/视觉网络传输活跃期间持有，失败或取消后即刻从内存中解除引用，重试需引导用户重新截图。

---

## 2. 领域模型与数据结构

### 2.1 会话条目模型 `StoredSession`
```csharp
namespace PopGlot.Windows.Services;

public enum SessionOrigin
{
    TranslationPanel, // 划词/主浮窗
    QuickSearch,      // 极速查词
    ScreenshotOcr,    // 截图 OCR
    ScreenshotVision  // 截图视觉翻译
}

public sealed record StoredSession(
    string SessionId,
    SessionOrigin Origin,
    string SourceText,
    string SourceLanguage,
    string TargetLanguage,
    string? EngineProfileId,
    string? EngineName,
    TranslationSessionState State,
    string? ResultText,
    string? ExplanationText,
    bool IsPartial,
    DateTime CreatedUtc,
    DateTime LastAccessedUtc,
    int TextByteCount
);
```

### 2.2 会话仓核心状态与预算限制
```csharp
public sealed class SessionStoreOptions
{
    public const int MaxSessionCount = 5;
    public const int MaxTotalTextBytes = 2 * 1024 * 1024; // 2 MiB
    public static readonly TimeSpan EvictionTtl = TimeSpan.FromMinutes(30);
}
```

- **计数上限**：`_sessions` 内部采用 `LinkedList<StoredSession>` + `Dictionary<string, LinkedListNode<StoredSession>>` 维护 LRU 淘汰链表。
- **大小计算**：`TextByteCount = Encoding.UTF8.GetByteCount(SourceText) + Encoding.UTF8.GetByteCount(ResultText ?? "") + Encoding.UTF8.GetByteCount(ExplanationText ?? "")`。
- **淘汰算法**：
  1. 惰性淘汰：每次 `Store(...)`、`TryRestoreRecent(...)` 或 `PruneExpired()` 调用时，移除 `DateTime.UtcNow - LastAccessedUtc > EvictionTtl` 的项。
  2. 容量淘汰：当条目数达到 5 或累计 `TextByteCount + next.TextByteCount > MaxTotalTextBytes` 时，从链表尾部（最旧且非活动）移除，绝不淘汰当前前台正在显示的活跃会话。

---

## 3. 接口设计与系统交互

### 3.1 核心服务接口 `ISessionStore`
```csharp
public interface ISessionStore
{
    /// <summary>暂存一个已隐藏或切换走但可能需要接续的会话</summary>
    bool TryStore(StoredSession session, out string? rejectionReason);

    /// <summary>获取最晚访问的一个非空会话（恢复最近翻译）</summary>
    StoredSession? PeekRecent();

    /// <summary>取出并激活最近的会话</summary>
    StoredSession? PopRecent();

    /// <summary>按 ID 精确获取</summary>
    StoredSession? Get(string sessionId);

    /// <summary>清除所有内存会话（退出应用或显式清空）</summary>
    void Clear();

    /// <summary>当前暂存指标</summary>
    (int Count, int TotalBytes) GetUsageMetrics();
}
```

### 3.2 恢复语义与 TranslationPanelStreamGate / App 接缝
1. **恢复不重发（Zero-Resend Contract）**：
   - 浮窗恢复时，调用 `TranslationPanelWindow.RestoreSession(StoredSession session)`。
   - 若 `session.IsPartial == true` 或 `session.State == TranslationSessionState.Cancelled`，直接调用 `RenderPartialOrCancelled(session.ResultText)`，状态栏显示「已恢复未完成内容（未重发）」，不触发底层 `_coordinator.TranslateTextAsync`。
   - 用户如需重新获取完整翻译，必须显式点击输入区的「重新翻译」按钮或按 Enter，此时生成全新 `SessionId` 并走正规发送门禁。
2. **与 App.xaml.cs 的调度联动**：
   - 现有的 `RestoreRecentSurface()`（`App.xaml.cs:1099`）目前仅在活跃面板实例内存活时单纯做 `Show()`，一旦发生 `DestroyActivePanel()`，旧会话即彻底丢失。
   - 接入改造：`DestroyActivePanel()` 前，提取旧面板快照通过 `_sessionStore.TryStore(...)` 放入仓中；`RestoreRecentSurface()` 若当前无活跃可见窗口，从 `_sessionStore.PopRecent()` 恢复并使用已有数据重建轻量窗口展示。

### 3.3 与 MainWindow 工作台接续交互
1. `TranslationPanelWindow` 标题栏或操作区提供「在工作台打开」图标动作。
2. 点击后触发 `App.OpenInMainWindow(StoredSession session)`：
   - 激活 `MainWindow` 并导航至 `TranslateSection`；
   - 检查 `TranslateSection` 当前是否有脏输入（未翻译且有内容，或正在翻译中）；
   - 若有冲突，通过轻量级 In-App 弹层提示：「保留当前工作台草稿」或「载入浮窗会话内容」；
   - 载入后直接呈现原文与已有的译文结果，不触发重新请求。

---

## 4. 内存预算与 C12 验收防线

1. **零图像引用泄漏**：
   - `StoredSession` 中仅包含 UTF-8 字符串与结构体枚举，绝不包含 `byte[]`、`Stream` 或 `BitmapSource` 字段。
   - 截图 OCR 完成后，通过局部临时变量销毁位图，仅将识别后的文字与翻译文本传入 `StoredSession`。
2. **C12 验收防线协同**：
   - 在 200 次窗口开关测试中，每次窗口关闭都可能调用 `TryStore`。由于硬限制 `MaxSessionCount = 5`，即使执行 200 次关闭，会话仓占用上限严格受制于 `5 条 / 2MiB`，GC 触发后老旧节点被回收，WorkingSet 绝不因此出现单调无界增长。
   - `App.OnExit()` 显式调用 `_sessionStore.Clear()`，解除所有节点引用。

---

## 5. 验收清单与实施改动范围

### 5.1 自动化与静态验收用例
- [ ] `SessionStore_CapacityLimit`：存入 6 个会话，验证第 1 个（最旧者）被自动剔除，保留 5 个。
- [ ] `SessionStore_ByteBudgetLimit`：存入 1 个 1.5MB 文本会话与 1 个 1.0MB 会话，验证总上限拦截并如实返回超限原因。
- [ ] `SessionStore_TtlExpiration`：模拟 31 分钟前访问记录，调用 `PruneExpired` 确认自动清理。
- [ ] `Restore_NeverSendsNetworkRequest`：恢复 partial 会话，断言 mock 网络发送器计数为 0。
- [ ] `ImageNotStored`：执行一次截图翻译并暂存，反射核查会话仓对象图不包含任何 GDI+/WPF 位图句柄。

### 5.2 涉及文件清单
1. **新建**：
   - `apps/PopGlot.Windows/Services/SessionStore.cs`（接口与实现）
   - `tests/PopGlot.Windows.PureTests/SessionStoreTests.cs`（针对性纯逻辑测试）
2. **改造接缝**：
   - `apps/PopGlot.Windows/App.xaml.cs`（`RestoreRecentSurface`、`DestroyActivePanel` 挂接）
   - `apps/PopGlot.Windows/TranslationPanelWindow.xaml.cs`（提取 `StoredSession` 快照、恢复展示接入口）
   - `apps/PopGlot.Windows/Sections/TranslateSection.xaml.cs`（工作台接续与草稿冲突判定）
