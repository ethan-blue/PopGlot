# N06 生词与专业术语库设计方案：领域映射、本地匹配与性能防线

> **设计日期**：2026-09-14
> **阶段/层级**：L2 效率完善
> **依据合同**：`docs/review-2026-09-12/PRODUCT-EXPANSION-REVIEW-V2.md` §5 N06；关联 C02（词库保护与损坏恢复）、C11（UI 线程零同步 I/O）、C20（分段与长标识符）、P06（匹配术语出网协议）
> **前置依赖**：现有 `VocabularyStore`（单写者队列已在 W8/W16 落地）、`DESIGN_SYSTEM.md`

---

## 1. 目标、架构取舍与非目标

### 1.1 核心取舍：独立文件 `glossary.json` vs 复用 `vocabulary.json`
在既有代码中，`VocabularyStore.cs` 负责生词本（星标收藏，`word -> translation/phonetic`）。针对专业术语库，做出如下架构决策：

- **决策结果**：**采用独立文件存储 `%LOCALAPPDATA%\PopGlot\glossary.json`，底层复用同一套 `VocabularyStore` 的 Channel 异步队列与原子安全写入机制。**
- **取舍论证**：
  1. **职责分离（Separation of Concerns）**：生词本是用户个人阅读学习的「收藏夹」（可能包含散乱短语与音标）；术语库是精准干预翻译产出结果的「编译级约束规则」（具备语言对定向、大小写敏感、优先匹配等编译器特征）。两者生命周期、清理逻辑完全不同。
  2. **降低出网过滤风险**：若混合在同一个 json 中，每次翻译请求需要全库过滤区分哪些是生词、哪些是术语，易造成隐式数据泄露。独立文件使术语加载与扫描天然具备物理隔离边界。
  3. **架构资产复用**：直接复用 W8/W16 锤炼完成的单写者后台队列（`Channel<T>`）、有界读取（`ReadBounded`）、损坏隔离（`Quarantine`）与 `Flush()` 测试缝隙。

### 1.2 目标
1. **术语条目模型**：支持原文（SourceTerm）、目标译文（TargetTerm）、源/目标语言对（LanguagePair）、是否区分大小写（CaseSensitive）、领域标签（DomainTag，如「计算机/医疗/法律」）与启用开关（IsEnabled）。
2. **本地高性能前缀/子串匹配（Zero-Network Filtering）**：
   - 翻译发起前，在本地内存中执行高精度的子串搜索匹配，优先采用最长匹配原则（Longest Match First）；
   - **零无用外发原则**：仅将**在源文中实际被匹配命中的已启用术语**随请求打包出网；未匹配术语 0 携带，严禁将完整术语库一股脑上传至 LLM。
3. **规模上限与性能预算**：
   - 单次请求携带上限：最多 100 条术语，总注入额外载荷 ≤ 16KiB；
   - 匹配执行耗时：在 10,000 条本地术语库规模下，源文匹配阶段耗时必须 ≤ 5ms，不得导致输入与点击出现迟滞。
4. **灵活的导入导出**：支持标准 UTF-8 JSON 及兼容 Excel/CAT 工具的标准两列/三列 CSV（`Source,Target,CaseSensitive`）导入导出。

### 1.3 非目标
1. **正则表达式支持**：首期只支持字面量（Literal Words / Phrases）及最长词优先匹配，不支持复杂动态正则表达式（规避 ReDoS 攻击与 LLM 理解漂移）。
2. **云端自动同步与协作库**：首期定位于本地离线术语管理，不涉及多租户协同或远程拉取。

---

## 2. 领域模型与核心数据结构

### 2.1 术语条目模型 `GlossaryEntry`
```csharp
namespace PopGlot.Windows.Services;

public sealed record GlossaryEntry(
    Guid Id,
    string SourceTerm,       // 原文词/短语（必填，去首尾空格）
    string TargetTerm,       // 目标翻译（必填）
    string SourceLanguage,   // "auto" 或具体语言代码如 "en"
    string TargetLanguage,   // 目标语言如 "zh-CN"
    bool CaseSensitive,      // 是否大小写敏感
    bool IsEnabled,          // 是否启用
    string? DomainTag,       // 可选领域标签（如 "Docker/K8s"）
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    public bool MatchesLanguage(string sourceLang, string targetLang)
    {
        var srcMatch = SourceLanguage == "auto" ||
                       string.Equals(SourceLanguage, sourceLang, StringComparison.OrdinalIgnoreCase);
        var tgtMatch = string.Equals(TargetLanguage, targetLang, StringComparison.OrdinalIgnoreCase);
        return srcMatch && tgtMatch;
    }
}
```

### 2.2 内存匹配索引树（Aho-Corasick 算法与最长匹配）
为了满足 10,000 条规则下 ≤5ms 的极致性能，内存中构建 `GlossaryMatcher`：
1. **分桶索引**：按 `LanguagePair` 进行首层分组划分；
2. **AC 自动机（Aho-Corasick Trie）**：对每个语言对桶内的 `SourceTerm` 构建双数组或轻量 AC 树，支持在单次遍历源文本流（`O(N + M)`）中找出所有命中的术语规则；
3. **区间去重与最长优先**：若同时命中「token」与「refresh token」，优先裁定长词「refresh token」胜出，被吞并的短词不重复计入，避免歧义干扰。

---

## 3. 存储与生命周期设计（GlossaryStore）

### 3.1 核心操作与 C02 继承
```csharp
public sealed class GlossaryStore : IGlossaryRepository
{
    private const int MaxEntries = 10_000;
    private const int MaxFileBytes = 8 * 1024 * 1024; // 8 MiB 限制
    private readonly string _path = StoragePaths.Glossary; // %LOCALAPPDATA%\PopGlot\glossary.json

    // 单写者后台 Channel 队列
    private readonly Channel<GlossaryPersistRequest> _persistChannel;
    
    // 内存快照保护（读操作零磁盘、零锁冲突）
    private readonly Lock _gate = new();
    private List<GlossaryEntry> _entries = [];
    private GlossaryMatcher _matcher = new([]);

    public void Flush(int timeoutMs = 5000);
    public Task<bool> UpsertAsync(GlossaryEntry entry);
    public Task<bool> DeleteAsync(Guid id);
    public IReadOnlyList<GlossaryEntry> Match(string sourceText, string srcLang, string tgtLang);
}
```

### 3.2 退出与崩溃防线
- 在 `App.xaml.cs` 的 `ExitApplication()` 与 `OnExit()` 中，与词库/历史平列接入 `_glossary.Flush()`；
- 读取严格遵循 `StrictUtf8` 与只读损坏隔离，若文件被外部非法截断，生成 `glossary.json.corrupt-<timestamp>` 副本，保留故障现场。

---

## 4. UI 呈现与信息架构（SettingsWindow / LibrarySection）

### 4.1 设置页/资料库信息架构扩展
1. **资料库侧栏导航新增子项**：
   - 现有的 `LibrarySection` 原仅有「历史记录」与「生词本」；
   - 增加第三个标签页：`[ 术语库 (Glossary) ]`，与生词本形成清晰认知区隔。
2. **术语列表视区设计**：
   - **顶部操作栏**：包含搜索框（按原文/译文/标签实时过滤）、`+ 添加术语` 主按钮、`导入/导出` 菜单；
   - **虚拟化数据表格**：复用 W10 的 `VirtualizingStackPanel`，支持快速启闭（SwitchCheckBox）、快速编辑弹窗、批量删除；
   - **生词转化快捷通道**：在生词本表格中，每行条目提供一个次要操作「转为术语」，点击后带入原文和释义，弹出微调弹窗确认语言对与大小写后直接落库。

---

## 5. 验收清单与涉及文件

### 5.1 验收清单
- [ ] **最长优先匹配验证**：库中存在「service」与「micro service」，源文输入「deploy micro service」，断言仅命中「micro service」，不产生短词重复覆盖。
- [ ] **大小写敏感度**：配置大小写敏感的「Rust」，输入「rust」不匹配，输入「Rust」精确命中。
- [ ] **10,000 条匹配性能压测**：构造 10,000 条合法术语加载入内存，对 4,000 字符源文执行 `Match`，Stopwatch 计时验证耗时 ≤ 5ms。
- [ ] **配额熔断**：单次匹配命中 120 条时，按匹配度与长度截断为前 100 条，总载荷绝对控制在 ≤ 16KiB。
- [ ] **无损 CSV 导入导出**：导出 CSV 再导入全新环境，核验特殊字符（逗号、双引号、换行符）解析 100% 完整不串行。

### 5.2 涉及文件清单
1. `apps/PopGlot.Windows/StoragePaths.cs`（新增 `Glossary => BaseDir/glossary.json`）
2. `apps/PopGlot.Windows/Services/GlossaryStore.cs`（存储与后台单写者队列）
3. `apps/PopGlot.Windows/Services/GlossaryMatcher.cs`（Aho-Corasick 高性能匹配引擎）
4. `apps/PopGlot.Windows/Sections/LibrarySection.xaml(.cs)`（扩展术语管理 UI）
5. `tests/PopGlot.Windows.PureTests/GlossaryTests.cs`（针对性纯逻辑与压测试验）
