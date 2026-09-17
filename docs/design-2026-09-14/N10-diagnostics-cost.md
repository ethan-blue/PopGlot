# N10 服务诊断与费用透明设计方案

> **设计日期**：2026-09-14
> **阶段/层级**：L1 状态，L2 扩展
> **依据合同**：`docs/review-2026-09-12/PRODUCT-EXPANSION-REVIEW-V2.md` §5 N10；关联 C16（服务配置流程）、C21（证据与状态标识）、C23（Provider 网络诊断）、G04（发送透明度）
> **前置依赖**：现有 `FreeTranslateService.GetHealthAsync`、`TranslationPanelWindow._operation` CancellationToken 链路、`SettingsWindow`

---

## 1. 目标与非目标

### 1.1 目标
1. **服务健康状态卡（Health Status Card）**：在设置页引擎管理与主窗底部状态中，展示结构化的健康状态，包含：最近成功/失败的确切时间戳、端到端探测延迟（ms）、HTTP 响应状态码及诊断摘要。
2. **在途请求取消按钮（In-Flight Request Cancellation）**：浮窗、查词及主工作台在发起翻译后，立即呈现显式「取消 (Esc / Cancel)」按钮，与底层的 `_operation`（`CancellationTokenSource`）无缝联动，实现 ≤100ms UI 即时响应与后台有界退出。
3. **诊断信息分层展示（Layered Diagnostics）**：
   - 第一层（摘要）：用户友好语义提示（如「网络超时」、「API Key 无效」、「模型服务限流 429」）；
   - 第二层（技术详情）：目标 Endpoint 域名、DNS/TLS/Connect 阶段划分、HTTP Method 及 Status Code；
   - 第三层（原始错误码与安全日志）：默认收拢折叠的原始异常调用栈，提供一键「复制诊断信息」（脱敏过滤 API Key 与源文）。
4. **本地费用与用量透明（Usage & Request Counter）**：
   - 建立本地请求计数器（日/月/总请求数、输入总字符数、输出总字符数、估算 Token 数）；
   - **零数据上报原则**：所有用量数据 100% 停留在本地 SQLite/JSON，绝不向任何外部遥测端点或账单服务器上传用户使用量。

### 1.2 非目标
1. **自动结算与实时扣费充值**：PopGlot 作为轻量客户端，不代理用户与 LLM 供应商的任何信用卡或账单结算，不虚构精确金额计费（界面明确标注「费用以各云服务商后台结算为准」）。
2. **多模型自动竞价分流**：不在此波引入复杂的成本驱动型动态模型切换（违背用户对固定模型的确定性预期）。

---

## 2. 领域模型与数据结构

### 2.1 健康状态模型 `ServiceHealthSnapshot`
```csharp
namespace PopGlot.Windows.Services;

public sealed record ServiceHealthSnapshot(
    string ProfileId,
    string ProfileName,
    bool IsReachable,
    long LatencyMs,
    DateTimeOffset CheckedAt,
    string Summary,
    int? HttpStatusCode,
    string? ErrorDetails
)
{
    public static ServiceHealthSnapshot Unknown(string id, string name) =>
        new(id, name, false, 0, DateTimeOffset.MinValue, "未检测", null, null);
}
```

### 2.2 本地用量计数器模型 `EngineUsageMetrics`
```csharp
public sealed record EngineUsageMetrics(
    string ProfileId,
    long TotalRequests,
    long TotalSuccessCount,
    long TotalFailureCount,
    long TotalSourceChars,
    long TotalResultChars,
    DateTimeOffset FirstUsedAt,
    DateTimeOffset LastUsedAt
);
```

---

## 3. UI 交互流与信息架构

### 3.1 在途请求取消交互流（与既有 `_cts` 接缝）
```text
[用户触发翻译] ──► 立即渲染 Loading 状态 + 显示「取消 (Esc)」主按钮
                          │
         ┌────────────────┴────────────────┐
         ▼                                 ▼
   [正常流式完成]                  [用户点击取消 / 按 Esc]
         │                                 │
         │                        触发 _operation.Cancel()
         │                                 │
         ▼                                 ▼
   保留完整译文                    保留已吐出的 Partial 译文
   更新成功计数                    状态栏展示「已取消」+ 不记入错误数
```

### 3.2 诊断分层呈现（SettingsWindow / Failure Cards）
当连接测试失败或翻译异常时，在卡片上按以下 3 层递进展示：
1. **摘要层（Header）**：
   - 🔴 图标 +「连接失败：API 密钥无效 (401 Unauthorized)」
   - 时间戳：`今天 22:15:30`，耗时：`420 ms`
2. **详情层（Body）**：
   - 域名：`api.deepseek.com`
   - 协议：`OpenAI Chat Compatible (HTTPS)`
   - 阶段：`TLS 握手成功 -> 认证握手阶段被拒`
3. **折叠层（Expander: [展开原始错误与诊断]）**：
   - 包含脱敏后的原始 Response Body 摘要（已剥离 Authorization Header 与 Key）；
   - 包含复制按钮：「📋 复制安全诊断报告」。

---

## 4. 验收清单与涉及文件

### 4.1 验收清单
- [ ] **取消即时性**：发起长文本请求，在首个 chunk 到达前点击取消，断言 UI 在 100ms 内恢复可交互，且无多余 HTTP 轮询。
- [ ] **时间戳诚实度**：手动执行「测试连接」，核验状态卡片呈现精确的更新时间戳与耗时。
- [ ] **脱敏复制安全**：故意填入带 Key 的错误配置并触发诊断复制，核查剪贴板内容确认绝对不包含明文 Key。
- [ ] **本地计数准确性**：连续成功 3 次、失败 1 次，核验用量统计中 Success=3, Fail=1, Total=4。

### 4.2 涉及文件清单
1. `apps/PopGlot.Windows/Services/ServiceHealthStore.cs`（健康状态与本地用量跟踪）
2. `apps/PopGlot.Windows/Sections/ServicesSection.xaml(.cs)`（诊断信息分层与健康卡片）
3. `apps/PopGlot.Windows/TranslationPanelWindow.xaml(.cs)`（取消按钮与状态联动）
