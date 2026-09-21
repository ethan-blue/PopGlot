using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

/// <summary>
/// The managed side of the Rust core's C ABI.
/// </summary>
internal static partial class CoreBridge
{
    private const string LibraryName = "popglot_ffi";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Prompt 模板专用 JSON 约定。Rust 域的 PromptTemplate / PastRevision /
    /// PromptVariables / CompiledPrompt 都是 <c>#[serde(rename_all = "camelCase")]</c>，
    /// 与设置文档的 snake_case 契约不同，因此独立成一套 camelCase 序列化选项，
    /// 两个文档格式永不互相污染。WhenWritingNull 保证可空集合（PastRevisions）
    /// 序列化为「字段缺席」而非 <c>null</c> —— Rust 侧 Vec 带 #[serde(default)]
    /// 接受缺席、拒绝显式 null。
    /// </summary>
    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Lock SettingsGate = new();
    private static readonly SemaphoreSlim SaveQueue = new(1, 1);

    /// <summary>
    /// Prompt 持久化专用串行队列，与设置写入的 <see cref="SaveQueue"/> 相互独立：
    /// prompt-templates.json 与 settings.json 互不阻塞，同类写操作之间保持发起顺序。
    /// </summary>
    private static readonly SemaphoreSlim PromptQueue = new(1, 1);

    private static ProviderSettings? _cachedSettings;

    public static void Initialize(string? configDirectory = null)
    {
        var resolvedDirectory = configDirectory ?? StoragePaths.CoreConfigDirectory;
        Directory.CreateDirectory(resolvedDirectory);
        EnsureSuccess<string>(Invoke(() => NativeMethods.Initialize(resolvedDirectory)));
    }

    /// <summary>
    /// Returns and clears the core's one-shot startup notice, e.g. that a
    /// corrupted settings file was backed up and defaults restored.
    /// </summary>
    public static string TakeStartupNotice()
    {
        try
        {
            return EnsureSuccess<string>(Invoke(NativeMethods.TakeStartupNotice));
        }
        catch (Exception)
        {
            // A missing notice must never break startup.
            return string.Empty;
        }
    }

    /// <summary>
    /// Current provider settings.
    /// </summary>
    public static ProviderSettings GetSettings()
    {
        lock (SettingsGate)
        {
            _cachedSettings ??= EnsureSuccess<ProviderSettings>(Invoke(NativeMethods.GetSettings));
            return _cachedSettings;
        }
    }

    public static void SaveSettings(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        lock (SettingsGate)
        {
            EnsureSuccess<string>(Invoke(() => NativeMethods.SaveSettings(json)));
            _cachedSettings = settings;
        }
    }

    /// <summary>
    /// 后台线程执行设置持久化（Rust 侧写盘含 flush+rename，慢磁盘/杀软
    /// 扫描时可达秒级）。排队串行以保持调用顺序，UI 线程只发起不等待。
    /// </summary>
    public static async Task SaveSettingsAsync(ProviderSettings settings)
    {
        await SaveQueue.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() => SaveSettings(settings)).ConfigureAwait(false);
        }
        finally
        {
            SaveQueue.Release();
        }
    }

    /// <summary>Asks the core which screenshot pipeline the settings imply.</summary>
    public static RoutingDecision PlanScreenshotRoute(bool localOcrAvailable, bool credentialPresent) =>
        EnsureSuccess<RoutingDecision>(Invoke(() => NativeMethods.PlanScreenshotRoute(
            localOcrAvailable ? 1 : 0,
            credentialPresent ? 1 : 0)));

    // -------------------------------------------------------------------
    // Prompt templates — popglot_list_prompt_templates / get_active /
    // set_active / save / delete / compile_prompt。每个调用都经 Invoke
    // （finally 中释放 native 字符串）+ EnsureSuccess（校验 envelope）。
    // -------------------------------------------------------------------

    /// <summary>All prompt templates: built-ins first, custom templates after.</summary>
    public static IReadOnlyList<PromptTemplateDto> ListPromptTemplates() =>
        EnsurePromptSuccess<IReadOnlyList<PromptTemplateDto>>(Invoke(NativeMethods.ListPromptTemplates));

    /// <summary>The active prompt template (defaults to the built-in faithful).</summary>
    public static PromptTemplateDto GetActivePromptTemplate() =>
        EnsurePromptSuccess<PromptTemplateDto>(Invoke(NativeMethods.GetActivePromptTemplate));

    /// <summary>
    /// 后台线程切换激活模板（Rust 侧同步写盘含 flush+rename）。null/空白重置回
    /// 内置 faithful；未知 ID 会收到错误 envelope。走独立的 Prompt 串行队列，
    /// 保持发起顺序且不阻塞调用方。
    /// </summary>
    public static Task SetActivePromptTemplateAsync(
        string? templateId,
        CancellationToken cancellationToken = default) =>
        RunPromptPersistenceAsync(
            () => EnsureSuccess<string>(Invoke(
                () => NativeMethods.SetActivePromptTemplate(NormalizeTemplateId(templateId)))),
            cancellationToken);

    /// <summary>
    /// 后台线程保存或更新一个自定义模板。Rust 侧负责校验、配额、revision 与
    /// 时间戳，返回持久化后的权威结果。走独立的 Prompt 串行队列。
    /// </summary>
    public static Task<PromptTemplateDto> SavePromptTemplateAsync(
        PromptTemplateDto template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        var templateJson = JsonSerializer.Serialize(template, PromptJsonOptions);
        return RunPromptPersistenceAsync(
            () => EnsurePromptSuccess<PromptTemplateDto>(Invoke(
                () => NativeMethods.SavePromptTemplate(templateJson))),
            cancellationToken);
    }

    /// <summary>
    /// 后台线程按 ID 删除一个自定义模板；删除激活中的模板时 Rust 侧自动重置
    /// 回内置 faithful。走独立的 Prompt 串行队列。
    /// </summary>
    public static Task DeletePromptTemplateAsync(
        string templateId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        return RunPromptPersistenceAsync(
            () => EnsureSuccess<string>(Invoke(
                () => NativeMethods.DeletePromptTemplate(templateId))),
            cancellationToken);
    }

    /// <summary>
    /// 编译预览：把模板与变量交给核心的白名单占位符纯函数编译器。纯本地计算，
    /// 零网络、不发翻译请求、不触碰任何持久化状态，可在 UI 线程直接调用。
    /// </summary>
    public static CompiledPromptDto CompilePromptPreview(
        PromptTemplateDto template,
        PromptVariablesDto variables)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(variables);
        var templateJson = JsonSerializer.Serialize(template, PromptJsonOptions);
        var variablesJson = JsonSerializer.Serialize(variables, PromptJsonOptions);
        return EnsurePromptSuccess<CompiledPromptDto>(Invoke(
            () => NativeMethods.CompilePrompt(templateJson, variablesJson)));
    }

    /// <summary>
    /// Rust 域内置 faithful 模板 ID（popglot-domain 的 BUILTIN_FAITHFUL_ID）。
    /// faithful 在 Rust 侧解析为「无偏好注入」，因此绝不编译、绝不传显式锚点。
    /// </summary>
    internal const string FaithfulTemplateId = "faithful";

    /// <summary>
    /// 把一个模板快照编译成翻译会话的偏好锚点（preference + templateId +
    /// revision）。全程本地：GetActivePromptTemplate 读配置、CompilePrompt 纯
    /// 函数编译，零网络、不发翻译请求。偏好正文由 Rust 生成，C# 绝不代传
    /// 编辑器正文。
    /// <para>语义与 FFI 的 resolve_active_preference 一一对应：</para>
    /// <list type="bullet">
    /// <item>模板缺失或 ID 为空 → null（沿用旧默认：Rust 请求起点自行解析）；</item>
    /// <item>faithful → null 且不编译：Rust 对 faithful 返回 None，传 null 与
    /// 旧默认请求字节级等价；</item>
    /// <item>其他模板 → 经 CompilePrompt 编译出显式锚点，语言变量按
    /// resolve_languages 的同一套规范化处理；</item>
    /// <item>编译失败 → null（对应 Rust 的 .ok()）：锚点只是风格保证，绝不
    /// 让它弄断翻译。</item>
    /// </list>
    /// </summary>
    internal static PromptAnchorSnapshot? CompilePromptAnchor(
        PromptTemplateDto? template,
        string? sourceLang,
        string? targetLang)
    {
        if (template is null || string.IsNullOrWhiteSpace(template.Id))
        {
            return null;
        }
        if (string.Equals(template.Id, FaithfulTemplateId, StringComparison.Ordinal))
        {
            // faithful：不编译。null 锚点让 Rust 走原解析路径（→ None），
            // 与旧默认请求字节级等价。
            return null;
        }

        try
        {
            var settings = GetSettings();
            var (source, target) = ResolvePromptLanguages(sourceLang, targetLang, settings);
            var compiled = CompilePromptPreview(
                template,
                new PromptVariablesDto(SourceLanguage: source, TargetLanguage: target));
            if (string.IsNullOrWhiteSpace(compiled.CompiledText) ||
                !string.Equals(compiled.TemplateId, template.Id, StringComparison.Ordinal))
            {
                return null;
            }
            return new PromptAnchorSnapshot(compiled.CompiledText, compiled.TemplateId, compiled.Revision);
        }
        catch (Exception)
        {
            // 编译失败回退旧默认（resolve_active_preference 的 .ok() 同款）。
            return null;
        }
    }

    /// <summary>
    /// FFI resolve_languages 的 C# 镜像：请求语言空缺回落设置存量，再统一经
    /// <see cref="NormalizeLanguageTag"/> 规范化；空/auto 的目标语言按
    /// LanguagePair::new 回落 zh-CN。锚点编译变量必须与 Rust 请求起点自行编译
    /// 时完全一致，偏好正文才能字节级相同。
    /// </summary>
    private static (string Source, string Target) ResolvePromptLanguages(
        string? sourceLang,
        string? targetLang,
        ProviderSettings settings)
    {
        var source = string.IsNullOrWhiteSpace(sourceLang)
            ? NormalizeLanguageTag(settings.SourceLanguage)
            : NormalizeLanguageTag(sourceLang);
        var target = string.IsNullOrWhiteSpace(targetLang)
            ? NormalizeLanguageTag(settings.TargetLanguage)
            : NormalizeLanguageTag(targetLang);
        if (target is "" or "auto")
        {
            target = "zh-CN";
        }
        return (source, target);
    }

    /// <summary>
    /// popglot-domain normalize_language_tag 的 C# 镜像：ASCII 小写 + 别名表。
    /// 变量表与 crates/popglot-domain/src/language.rs 逐条对应，未知标签小写
    /// 透传；不得增删别名，否则锚点编译结果与 Rust 侧出现字节级漂移。
    /// </summary>
    private static string NormalizeLanguageTag(string? tag)
    {
        var lowered = AsciiToLower((tag ?? string.Empty).Trim());
        return lowered switch
        {
            "" or "auto" or "detect" or "自动" or "自动检测" => "auto",
            "zh" or "zh-cn" or "zh-hans" or "zh_hans" or "chs" or "中文" or "简体中文" or "汉语" => "zh-CN",
            "zh-tw" or "zh-hant" or "zh_hant" or "cht" or "繁体中文" or "繁體中文" => "zh-TW",
            // 注意：zh_tw（下划线）不在 Rust 表内，按 unknown 小写透传。
            "en" or "en-us" or "en-gb" or "eng" or "英语" or "英文" => "en",
            "ja" or "jp" or "ja-jp" or "日语" or "日文" => "ja",
            "ko" or "kr" or "ko-kr" or "韩语" or "韩文" => "ko",
            "fr" or "fr-fr" or "法语" or "法文" => "fr",
            "de" or "de-de" or "德语" or "德文" => "de",
            "es" or "es-es" or "西班牙语" => "es",
            "pt" or "pt-br" or "葡萄牙语" => "pt",
            "ru" or "ru-ru" or "俄语" => "ru",
            "it" or "it-it" or "意大利语" => "it",
            "ar" or "阿拉伯语" => "ar",
            "hi" or "印地语" => "hi",
            "th" or "泰语" => "th",
            "vi" or "越南语" => "vi",
            _ => lowered,
        };
    }

    /// <summary>to_ascii_lowercase 的等价实现：只折叠 ASCII，不触碰非 ASCII。</summary>
    private static string AsciiToLower(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'A' and <= 'Z')
            {
                chars[i] = (char)(chars[i] + ('a' - 'A'));
            }
        }
        return new string(chars);
    }

    /// <summary>
    /// Prompt 持久化串行队列（独立于设置写入的 SaveQueue）：排队保持调用顺序，
    /// 慢磁盘上的写盘不阻塞调用线程。
    /// </summary>
    private static async Task<T> RunPromptPersistenceAsync<T>(
        Func<T> blockingWork,
        CancellationToken cancellationToken)
    {
        await PromptQueue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(blockingWork, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            PromptQueue.Release();
        }
    }

    /// <summary>空/空白 ID 归一化为 null，按 Rust 契约重置回内置 faithful。</summary>
    private static string? NormalizeTemplateId(string? templateId) =>
        string.IsNullOrWhiteSpace(templateId) ? null : templateId;

    /// <summary>
    /// Prompt 契约最小自检（纯本地、零 FFI、零 IO，可重复执行）：语言镜像
    /// 别名表、resolve_languages 回退规则、Prompt envelope camelCase 绑定
    /// （schemaVersion / isBuiltIn / pastRevisions / templateId /
    /// compiledText）与 PastRevisions null 折叠。返回失败清单，空即全部通过；
    /// 供 LogicTests 等宿主一次性校验，不在此处自动触发。
    /// </summary>
    internal static IReadOnlyList<string> PromptContractSelfCheck()
    {
        var failures = new List<string>();

        void Expect(string actual, string expected, string what)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                failures.Add($"{what}: 期望 \"{expected}\"，实际 \"{actual}\"");
            }
        }

        // normalize_language_tag 别名表逐行抽查（每条 Rust 分支至少一条）。
        (string Input, string Expected)[] languageFixtures =
        [
            ("", "auto"), ("AUTO", "auto"), ("Detect", "auto"), ("自动", "auto"), ("自动检测", "auto"),
            ("zh", "zh-CN"), ("zh-cn", "zh-CN"), ("zh_hans", "zh-CN"), ("CHS", "zh-CN"),
            ("中文", "zh-CN"), ("简体中文", "zh-CN"), ("汉语", "zh-CN"),
            ("zh-Hant", "zh-TW"), ("zh_hant", "zh-TW"), ("cht", "zh-TW"),
            ("繁体中文", "zh-TW"), ("繁體中文", "zh-TW"), ("zh_tw", "zh_tw"),
            ("en", "en"), ("en-US", "en"), ("eng", "en"), ("英语", "en"), ("英文", "en"),
            ("JP", "ja"), ("ja-jp", "ja"), ("日语", "ja"),
            ("kr", "ko"), ("KO-KR", "ko"), ("韩语", "ko"),
            ("fr-fr", "fr"), ("法语", "fr"),
            ("de-de", "de"), ("德文", "de"),
            ("es-es", "es"), ("西班牙语", "es"),
            ("PT-BR", "pt"), ("葡萄牙语", "pt"),
            ("ru-ru", "ru"), ("俄语", "ru"),
            ("it-it", "it"), ("意大利语", "it"),
            ("ar", "ar"), ("阿拉伯语", "ar"),
            ("HI", "hi"), ("印地语", "hi"),
            ("th", "th"), ("泰语", "th"),
            ("vi", "vi"), ("越南语", "vi"),
            ("es-419", "es-419"), // 未知标签小写透传
        ];
        foreach (var (input, expected) in languageFixtures)
        {
            Expect(NormalizeLanguageTag(input), expected, $"NormalizeLanguageTag(\"{input}\")");
        }

        // resolve_languages 回退：空缺回落设置存量，auto/空目标回落 zh-CN，
        // 非空请求规范化后生效。
        var mirrorSettings = new ProviderSettings(
            SchemaVersion: 1, ProviderType.OpenAiCompatible, ApiBaseUrl: "", TextEndpoint: "", VisionEndpoint: "",
            TextModel: "", VisionModel: "", ExtraHeaders: new Dictionary<string, string>(), AnthropicVersion: "",
            SupportsText: true, SupportsVision: false, NetworkEnabled: true,
            TranslationMode.Auto, AllowImageUploadInAuto: false, SafeDevMode: false,
            AllowLanEndpoints: false, AllowInsecureTls: false, ApiKeyConfigured: false,
            SourceLanguage: "AUTO", TargetLanguage: "",
            IncludeExplanation: false, ProtectCodeTokens: false);
        var (mirrorSource, mirrorTarget) = ResolvePromptLanguages(null, null, mirrorSettings);
        Expect(mirrorSource, "auto", "ResolvePromptLanguages 存量 source");
        Expect(mirrorTarget, "zh-CN", "ResolvePromptLanguages 空/auto 目标回退");
        var (reqSource, reqTarget) = ResolvePromptLanguages(" EN-GB ", "auto", mirrorSettings);
        Expect(reqSource, "en", "ResolvePromptLanguages 请求 source 规范化");
        Expect(reqTarget, "zh-CN", "ResolvePromptLanguages 请求 auto 目标回退");

        // Prompt envelope：camelCase 数据体必须经 PromptJsonOptions 解开，
        // 多词字段逐个验证绑定。
        const string promptEnvelopeJson =
            "{\"ok\":true,\"data\":{\"id\":\"tech\",\"schemaVersion\":2,\"name\":\"技术文档\"," +
            "\"instruction\":\"译成 {{targetLanguage}}\",\"enabled\":true,\"isBuiltIn\":false," +
            "\"revision\":7,\"createdAt\":1,\"updatedAt\":2," +
            "\"pastRevisions\":[{\"revision\":6,\"instruction\":\"旧稿\",\"domain\":\"IT\",\"audience\":\"dev\",\"updatedAt\":9}]," +
            "\"compiledText\":\"锚点正文\",\"templateId\":\"tech\"}}";
        try
        {
            var compiled = EnsurePromptSuccess<CompiledPromptDto>(promptEnvelopeJson);
            Expect(compiled.TemplateId, "tech", "EnsurePromptSuccess CompiledPrompt.templateId");
            Expect(compiled.CompiledText, "锚点正文", "EnsurePromptSuccess CompiledPrompt.compiledText");
            if (compiled.Revision != 7UL)
            {
                failures.Add($"EnsurePromptSuccess CompiledPrompt.revision: 期望 7，实际 {compiled.Revision}");
            }

            var template = EnsurePromptSuccess<PromptTemplateDto>(promptEnvelopeJson);
            Expect(template.Id, "tech", "EnsurePromptSuccess Template.id");
            if (template.SchemaVersion != 2U)
            {
                failures.Add($"EnsurePromptSuccess Template.schemaVersion: 期望 2，实际 {template.SchemaVersion}");
            }
            if (template.IsBuiltIn)
            {
                failures.Add("EnsurePromptSuccess Template.isBuiltIn: 期望 false");
            }
            if (template.PastRevisions is not { Count: 1 } ||
                template.PastRevisions[0].Revision != 6UL ||
                !string.Equals(template.PastRevisions[0].Instruction, "旧稿", StringComparison.Ordinal))
            {
                failures.Add("EnsurePromptSuccess Template.pastRevisions 未正确绑定");
            }

            // snake_case 路径必须解析不出多词字段 —— 防止有人把 Prompt 数据
            // 改回 EnsureSuccess 时无告警回归。
            var stale = EnsureSuccess<CompiledPromptDto>(promptEnvelopeJson);
            if (!string.IsNullOrEmpty(stale.TemplateId) || !string.IsNullOrEmpty(stale.CompiledText))
            {
                failures.Add("EnsureSuccess(snake_case) 意外绑定 camelCase 多词字段，防回归断言失效");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"Prompt envelope 自检抛出异常：{ex.Message}");
        }

        // PastRevisions null 必须折叠成「字段缺席」——Rust Vec 拒绝显式 null。
        var nullJson = JsonSerializer.Serialize(
            new PromptTemplateDto("faithful") { PastRevisions = null },
            PromptJsonOptions);
        if (nullJson.Contains("\"pastRevisions\"", StringComparison.Ordinal))
        {
            failures.Add($"PastRevisions null 应整体缺席，实际：{nullJson}");
        }
        var emptyJson = JsonSerializer.Serialize(
            new PromptTemplateDto("faithful") { PastRevisions = [] },
            PromptJsonOptions);
        if (!emptyJson.Contains("\"pastRevisions\":[]", StringComparison.Ordinal))
        {
            failures.Add($"PastRevisions 空集合应序列化为 []，实际：{emptyJson}");
        }

        // faithful 锚点必须为 null 且不触发任何编译（CompilePromptAnchor 对
        // faithful 在触碰 settings/FFI 之前短路）。
        var faithfulAnchor = CompilePromptAnchor(
            new PromptTemplateDto(FaithfulTemplateId) { Instruction = "内置默认" }, "zh-CN", "en");
        if (faithfulAnchor is not null)
        {
            failures.Add("faithful 锚点必须为 null（与旧默认字节级等价）");
        }
        var missingAnchor = CompilePromptAnchor(null, null, null);
        if (missingAnchor is not null)
        {
            failures.Add("缺失模板锚点必须为 null");
        }

        return failures;
    }

    /// <summary>
    /// The screenshot routing DECISION TABLE, evaluated by the Rust domain
    /// from the shell-collected facts. This is the single strategy source:
    /// the settings preview and the actual capture must both come through
    /// here, so the two languages can never diverge again.
    /// </summary>
    public static RoutingDecision SelectRoute(ScreenshotRouteFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return EnsureSuccess<RoutingDecision>(Invoke(() =>
            NativeMethods.SelectRoute(JsonSerializer.Serialize(facts, JsonOptions))));
    }

    /// <summary>Raw JSON in, raw JSON out — the cross-language parity probe.</summary>
    internal static string SelectRouteRaw(string factsJson) =>
        Invoke(() => NativeMethods.SelectRoute(factsJson));

    /// <summary>
    /// Translates one screenshot through a draft settings snapshot with a
    /// dedicated vision provider. The text key and the vision key travel
    /// separately; the settings JSON is used without being persisted.
    /// </summary>
    public static Task<TranslationResponse> TranslateVisionDraftAsync(
        ProviderSettings draftSettings,
        string textApiKey,
        string visionApiKey,
        byte[] image,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draftSettings);
        var reqId = requestId ?? Guid.NewGuid().ToString("N");
        var draftJson = JsonSerializer.Serialize(draftSettings, JsonOptions);
        var imageBase64 = Convert.ToBase64String(image);
        // The draft is already the selected vision provider's complete
        // settings. Pass its credential as the primary key; never let the
        // text route become the authentication source for this request.
        var effectiveKey = string.IsNullOrWhiteSpace(visionApiKey) ? "local" : visionApiKey;
        return RunCancellableAsync(
            () => EnsureSuccess<TranslationResponse>(Invoke(
                () => NativeMethods.TranslateVisionV3(
                    effectiveKey, effectiveKey, draftJson, "image/png", imageBase64,
                    sourceLang, targetLang, reqId))),
            reqId,
            cancellationToken);
    }

    /// <summary>
    /// Executes text translation using the complete provider snapshot supplied
    /// by the caller. This is used for OCR output so the text route cannot be
    /// accidentally replaced by the Core's stale mirrored settings.
    /// </summary>
    public static Task<TranslationResponse> TranslateTextDraftAsync(
        ProviderSettings draftSettings,
        string apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draftSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var reqId = requestId ?? Guid.NewGuid().ToString("N");
        var draftJson = JsonSerializer.Serialize(draftSettings, JsonOptions);
        var effectiveKey = string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey;
        return RunCancellableAsync(
            () => EnsureSuccess<TranslationResponse>(Invoke(
                () => NativeMethods.TranslateTextDraftV1(
                    draftJson, effectiveKey, source, sourceLang, targetLang, reqId))),
            reqId,
            cancellationToken);
    }

    public static Task<TranslationResponse> TestConnectionDraftAsync(
        ProviderSettings draftSettings,
        string apiKey,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draftSettings);
        var reqId = requestId ?? Guid.NewGuid().ToString("N");
        var draftJson = JsonSerializer.Serialize(draftSettings, JsonOptions);
        var effectiveKey = string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey;
        return RunCancellableAsync(
            () => EnsureSuccess<TranslationResponse>(Invoke(
                () => NativeMethods.TestConnectionDraft(draftJson, effectiveKey, reqId))),
            reqId,
            cancellationToken);
    }

    /// <summary>
    /// Translates text through the configured Provider only. Whether the
    /// built-in free engine may be used instead is a privacy decision owned by
    /// <see cref="Services.TranslationCoordinator"/>, never by the bridge.
    /// </summary>
    /// <remarks>
    /// <paramref name="preferenceAnchor"/> 为 null 时保持旧默认：Rust 在请求
    /// 起点按 active 模板自行解析编译后的偏好文本。非 null 时（会话起点经
    /// CompilePrompt 编译出的快照）按显式锚点原样发送，多段/重试在途不变。
    /// </remarks>
    public static async Task<TranslationResponse> RunTextTaskAsync(
        string? apiKey,
        string source,
        string sourceLang,
        string targetLang,
        TextTaskKind task,
        CancellationToken cancellationToken = default,
        ProviderSettings? routeSettings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var settings = GetSettings();
        if ((settings.SafeDevMode || !settings.NetworkEnabled) && !settings.TargetsLocalRuntime)
        {
            throw new InvalidOperationException(
                "安全离线模式或网络访问已禁用；总结和快速解释需要已配置的本地或在线模型。");
        }
        var usesConfiguredProvider = !string.IsNullOrWhiteSpace(apiKey) || settings.TargetsLocalRuntime;
        if (!usesConfiguredProvider)
        {
            throw new InvalidOperationException("请先配置模型服务，再使用总结或快速解释。");
        }
        var effectiveKey = string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey;
        var requestId = $"text-task-{Guid.NewGuid():N}";
        var wireTask = task == TextTaskKind.Summarize ? "summarize" : "explain";
        return await RunCancellableAsync(
            () => EnsureSuccess<TranslationResponse>(Invoke(() => routeSettings is null
                ? NativeMethods.TextTaskV1(
                    effectiveKey, source, sourceLang, targetLang, wireTask, requestId)
                : NativeMethods.TextTaskDraftV1(
                    JsonSerializer.Serialize(routeSettings, JsonOptions), effectiveKey, source,
                    sourceLang, targetLang, wireTask, requestId))),
            requestId,
            cancellationToken);
    }

    public static async Task<TranslationResponse> TranslateTextAsync(
        string? apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        CancellationToken cancellationToken = default,
        PromptAnchorSnapshot? preferenceAnchor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var settings = GetSettings();
        if (settings.SafeDevMode || !settings.NetworkEnabled)
        {
            if (!settings.TargetsLocalRuntime)
            {
                throw new InvalidOperationException(
                    "安全离线模式或网络翻译已禁用；未发送任何在线翻译请求。可在设置中配置本地模型或开启网络。");
            }
        }

        var usesConfiguredProvider = !string.IsNullOrWhiteSpace(apiKey) || settings.TargetsLocalRuntime;
        if (!usesConfiguredProvider)
        {
            throw new InvalidOperationException(
                "尚未配置模型服务；未发送任何请求。可配置 API Key、本地模型地址，或允许内置免费引擎。");
        }

        var effectiveKey = string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey;
        var reqId = requestId ?? Guid.NewGuid().ToString("N");
        // 无显式锚点时偏好锚点传 null：由 Rust 在请求起点按当前 active 模板
        // 自行解析编译后的偏好文本；C# 永不代传编辑器正文。
        return await RunCancellableAsync(
            () => EnsureSuccess<TranslationResponse>(Invoke(
                () => NativeMethods.TranslateTextV3(
                    effectiveKey, source, sourceLang, targetLang, reqId,
                    preference: preferenceAnchor?.Preference,
                    templateId: preferenceAnchor?.TemplateId,
                    templateRevision: preferenceAnchor?.Revision ?? 0))),
            reqId,
            cancellationToken);
    }

    /// <summary>
    /// Streams text translation through the configured active provider.
    /// </summary>
    /// <remarks>
    /// <paramref name="preferenceAnchor"/> 为 null 时保持旧默认：Rust 在请求
    /// 起点按 active 模板自行解析偏好文本；非 null 时按会话级显式锚点原样
    /// 发送（多段/重试在途不变）。
    /// </remarks>
    public static TranslationStreamSession TranslateTextStream(
        string? apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        string? sessionId = null,
        long epoch = 0,
        TranslationStreamBuffer? buffer = null,
        CancellationToken cancellationToken = default,
        PromptAnchorSnapshot? preferenceAnchor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var settings = GetSettings();
        if (settings.SafeDevMode || !settings.NetworkEnabled)
        {
            if (!settings.TargetsLocalRuntime)
            {
                throw new InvalidOperationException(
                    "安全离线模式或网络翻译已禁用；未发送任何在线翻译请求。可在设置中配置本地模型或开启网络。");
            }
        }

        var usesConfiguredProvider = !string.IsNullOrWhiteSpace(apiKey) || settings.TargetsLocalRuntime;
        if (!usesConfiguredProvider)
        {
            throw new InvalidOperationException(
                "尚未配置模型服务；未发送任何请求。可配置 API Key、本地模型地址，或允许内置免费引擎。");
        }

        var effectiveKey = string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey;
        var reqId = requestId ?? Guid.NewGuid().ToString("N");
        var activeBuffer = buffer ?? new TranslationStreamBuffer(
            sessionId ?? Guid.NewGuid().ToString("N"),
            reqId,
            epoch);

        unsafe
        {
            // 无显式锚点时偏好锚点传 null：由 Rust 在请求起点解析 active 模板；
            // C# 永不代传编辑器正文。
            var completionTask = ExecuteStreamRequestAsync(
                activeBuffer,
                (cb, userData) => NativeMethods.TranslateTextStreamV2(
                    effectiveKey, source, sourceLang, targetLang, reqId,
                    preferenceAnchor?.Preference, preferenceAnchor?.TemplateId,
                    preferenceAnchor?.Revision ?? 0, cb, userData),
                reqId,
                cancellationToken);

            return new TranslationStreamSession(activeBuffer, completionTask);
        }
    }

    public static TranslationStreamSession TranslateTextStreamAsync(
        string? apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        string? sessionId = null,
        long epoch = 0,
        TranslationStreamBuffer? buffer = null,
        CancellationToken cancellationToken = default,
        PromptAnchorSnapshot? preferenceAnchor = null) =>
        TranslateTextStream(apiKey, source, sourceLang, targetLang, requestId, sessionId, epoch, buffer, cancellationToken, preferenceAnchor);

    /// <summary>
    /// Streams text translation through an unpersisted settings draft snapshot.
    /// </summary>
    /// <remarks>
    /// <paramref name="preferenceAnchor"/> 为 null 时保持旧默认：草稿设置只覆盖
    /// Provider，偏好由 Rust 在请求起点按 active 模板解析；非 null 时按会话级
    /// 显式锚点原样发送（多段/重试在途不变）。C# 永不代传编辑器正文。
    /// </remarks>
    public static TranslationStreamSession TranslateTextDraftStream(
        ProviderSettings draftSettings,
        string apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        string? sessionId = null,
        long epoch = 0,
        TranslationStreamBuffer? buffer = null,
        CancellationToken cancellationToken = default,
        PromptAnchorSnapshot? preferenceAnchor = null)
    {
        ArgumentNullException.ThrowIfNull(draftSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var reqId = requestId ?? Guid.NewGuid().ToString("N");
        var draftJson = JsonSerializer.Serialize(draftSettings, JsonOptions);
        var effectiveKey = string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey;
        var activeBuffer = buffer ?? new TranslationStreamBuffer(
            sessionId ?? Guid.NewGuid().ToString("N"),
            reqId,
            epoch);

        unsafe
        {
            var completionTask = ExecuteStreamRequestAsync(
                activeBuffer,
                (cb, userData) => NativeMethods.TranslateTextDraftStreamV2(
                    draftJson, effectiveKey, source, sourceLang, targetLang, reqId,
                    preferenceAnchor?.Preference, preferenceAnchor?.TemplateId,
                    preferenceAnchor?.Revision ?? 0, cb, userData),
                reqId,
                cancellationToken);

            return new TranslationStreamSession(activeBuffer, completionTask);
        }
    }

    public static TranslationStreamSession TranslateTextDraftStreamAsync(
        ProviderSettings draftSettings,
        string apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        string? sessionId = null,
        long epoch = 0,
        TranslationStreamBuffer? buffer = null,
        CancellationToken cancellationToken = default,
        PromptAnchorSnapshot? preferenceAnchor = null) =>
        TranslateTextDraftStream(draftSettings, apiKey, source, sourceLang, targetLang, requestId, sessionId, epoch, buffer, cancellationToken, preferenceAnchor);

    /// <summary>
    /// Streams screenshot translation through an unpersisted (or stored) vision settings draft.
    /// </summary>
    public static TranslationStreamSession TranslateVisionDraftStream(
        ProviderSettings draftSettings,
        string textApiKey,
        string visionApiKey,
        byte[] image,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        string? sessionId = null,
        long epoch = 0,
        TranslationStreamBuffer? buffer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draftSettings);
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length == 0 || image.Length > 8 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(image), "截图必须大于 0 且不超过 8 MiB。");
        }

        var reqId = requestId ?? Guid.NewGuid().ToString("N");
        var draftJson = JsonSerializer.Serialize(draftSettings, JsonOptions);
        var imageBase64 = Convert.ToBase64String(image);
        var effectiveKey = string.IsNullOrWhiteSpace(visionApiKey) ? "local" : visionApiKey;
        var activeBuffer = buffer ?? new TranslationStreamBuffer(
            sessionId ?? Guid.NewGuid().ToString("N"),
            reqId,
            epoch);

        unsafe
        {
            var completionTask = ExecuteStreamRequestAsync(
                activeBuffer,
                (cb, userData) => NativeMethods.TranslateVisionDraftStreamV1(
                    effectiveKey, effectiveKey, draftJson, "image/png", imageBase64,
                    sourceLang, targetLang, reqId, cb, userData),
                reqId,
                cancellationToken);

            return new TranslationStreamSession(activeBuffer, completionTask);
        }
    }

    public static TranslationStreamSession TranslateVisionDraftStreamAsync(
        ProviderSettings draftSettings,
        string textApiKey,
        string visionApiKey,
        byte[] image,
        string sourceLang,
        string targetLang,
        string? requestId = null,
        string? sessionId = null,
        long epoch = 0,
        TranslationStreamBuffer? buffer = null,
        CancellationToken cancellationToken = default) =>
        TranslateVisionDraftStream(draftSettings, textApiKey, visionApiKey, image, sourceLang, targetLang, requestId, sessionId, epoch, buffer, cancellationToken);

    /// <summary>
    /// Translates already-recognized text (e.g. from local OCR) through the
    /// configured provider, or — with an explicit, persisted consent — through
    /// the free web engine when nothing else is configured.
    /// </summary>
    internal static async Task<TranslationResponse> TranslateRecognizedTextAsync(
        string? apiKey,
        string source,
        string sourceLang,
        string targetLang,
        string? requestId,
        CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        var usesConfiguredProvider = !string.IsNullOrWhiteSpace(apiKey) || settings.TargetsLocalRuntime;
        if (usesConfiguredProvider)
        {
            return await TranslateTextAsync(apiKey, source, sourceLang, targetLang, requestId, cancellationToken);
        }

        if (!Services.OutboundPolicy.AllowsFreeEngine(settings, out var denial, out var authorization))
        {
            throw new InvalidOperationException(
                denial is null ? "未允许出网翻译。" : $"{denial.Message} {denial.ActionableSuggestion}".Trim());
        }

        return await FreeTranslateService.TranslateAsync(source, sourceLang, targetLang, authorization, cancellationToken);
    }

    /// The OCR fallback path: recognise locally, then translate the text via
    /// the unified entry — configured provider when present, the authorised
    /// free engine otherwise. Never bypasses the free-engine decision.
    /// </summary>
    public static async Task<(TranslationResponse Response, ulong OcrElapsedMs, ulong NetworkElapsedMs)>
        TranslateScreenshotViaOcrAsync(
            string? apiKey,
            byte[] image,
            string sourceLang,
            string targetLang,
            string? requestId = null,
            CancellationToken cancellationToken = default,
            ProviderSettings? textRouteSettings = null)
    {
        var ocrStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var recognized = await WindowsOcrService.RecognizeTextAsync(image, sourceLang);
        ocrStopwatch.Stop();
        if (string.IsNullOrWhiteSpace(recognized))
        {
            throw new InvalidOperationException(
                "本地 OCR 未能在所选区域识别到文字。请重新框选更清晰的区域，或在设置中开启截图上传以使用视觉模型。");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var networkStopwatch = System.Diagnostics.Stopwatch.StartNew();
        TranslationResponse response;
        if (textRouteSettings is not null &&
            (textRouteSettings.TextIsConfigured || textRouteSettings.TargetsLocalRuntime))
        {
            response = await TranslateTextDraftAsync(
                textRouteSettings,
                apiKey ?? string.Empty,
                recognized,
                sourceLang,
                targetLang,
                requestId,
                cancellationToken);
        }
        else
        {
            response = await TranslateRecognizedTextAsync(
                apiKey, recognized, sourceLang, targetLang, requestId, cancellationToken);
        }
        networkStopwatch.Stop();

        return (response with
            {
                Result = response.Result with { Transcription = recognized },
            },
            (ulong)ocrStopwatch.ElapsedMilliseconds,
            (ulong)networkStopwatch.ElapsedMilliseconds);
    }

    public static void CancelRequest(string requestId)
    {
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            NativeMethods.CancelRequest(requestId);
        }
    }

    /// <summary>
    /// Masks technical tokens with the SAME Rust regex set the configured
    /// providers use, so the built-in free engine can never run a divergent
    /// C# copy of the protection rules.
    /// </summary>
    public static ProtectedTextDto ProtectTokens(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return EnsureSuccess<ProtectedTextDto>(Invoke(() => NativeMethods.ProtectTokens(text)));
    }

    /// <summary>
    /// Raw Rust-side endpoint classification, used by tests to prove the C#
    /// classifier and the core agree on the same fixtures.
    /// </summary>
    internal static string ClassifyEndpointViaFfi(string url) =>
        Invoke(() => NativeMethods.ClassifyEndpoint(url));

    /// <summary>
    /// Restores placeholders exactly once and reports dropped, duplicated and
    /// unknown ones — the same contract the core enforces for the configured
    /// providers.
    /// </summary>
    public static RestoredTextDto RestoreTokens(string translated, IReadOnlyList<ProtectedTokenDto> tokens)
    {
        ArgumentException.ThrowIfNullOrEmpty(translated);
        ArgumentNullException.ThrowIfNull(tokens);
        var tokensJson = JsonSerializer.Serialize(tokens, JsonOptions);
        return EnsureSuccess<RestoredTextDto>(Invoke(() => NativeMethods.RestoreTokens(translated, tokensJson)));
    }

    /// <summary>Session output-budget constants shared with the Rust planner.</summary>
    internal const int MaxSegmentChars = 800;
    internal const int MaxSegments = 8;

    /// <summary>
    /// Plans how a long source is translated within one session: a single
    /// request, ordered segments (concatenation reproduces the source), or an
    /// explicit rejection that must surface BEFORE anything is sent.
    /// </summary>
    public static SegmentPlanDto PlanSegments(string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        return EnsureSuccess<SegmentPlanDto>(Invoke(() =>
            NativeMethods.PlanSegments(source, MaxSegmentChars, MaxSegments)));
    }

    public static void CancelActiveRequest()
    {
        NativeMethods.CancelActiveRequest();
    }

    private static async Task<T> RunCancellableAsync<T>(
        Func<T> blockingWork,
        string requestId,
        CancellationToken cancellationToken)
    {
        var work = Task.Run(blockingWork);
        if (!cancellationToken.CanBeCanceled)
        {
            return await work;
        }

        await using var registration = cancellationToken.Register(
            () => CancelRequest(requestId));
        try
        {
            return await work;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private unsafe delegate nint NativeStreamInvoker(
        delegate* unmanaged[Cdecl]<nint, int, nint, nuint, int> callback,
        nint userData);

    private static async Task<TranslationResponse> ExecuteStreamRequestAsync(
        TranslationStreamBuffer buffer,
        NativeStreamInvoker nativeStreamingCall,
        string requestId,
        CancellationToken cancellationToken)
    {
        var handle = GCHandle.Alloc(buffer);
        try
        {
            var userData = GCHandle.ToIntPtr(handle);
            return await RunCancellableAsync(
                () =>
                {
                    try
                    {
                        string rawJson;
                        unsafe
                        {
                            rawJson = Invoke(() => nativeStreamingCall(&StreamCallbackThunk, userData));
                        }
                        var response = EnsureSuccess<TranslationResponse>(rawJson);
                        buffer.Complete();
                        return response;
                    }
                    catch (Exception ex)
                    {
                        buffer.Abort(ex.Message);
                        throw;
                    }
                    finally
                    {
                        GC.KeepAlive(buffer);
                    }
                },
                requestId,
                cancellationToken);
        }
        finally
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }
    }

    internal static int ProcessStreamDelta(nint userData, int eventType, nint payloadPtr, nuint byteLen)
    {
        if (userData == 0)
        {
            return 0;
        }

        try
        {
            var handle = GCHandle.FromIntPtr(userData);
            if (!handle.IsAllocated)
            {
                return 1;
            }

            if (handle.Target is not TranslationStreamBuffer buffer)
            {
                return 1;
            }

            // eventType 1 = POPGLOT_STREAM_EVENT_TEXT_DELTA_V1
            if (eventType == 1)
            {
                if (payloadPtr == 0 || byteLen == 0)
                {
                    return buffer.IsActive ? 0 : 1;
                }

                if (byteLen > int.MaxValue)
                {
                    buffer.Abort("Payload length exceeds maximum supported size.");
                    return 1;
                }

                bool appended = buffer.TryAppendUtf8(payloadPtr, (int)byteLen);
                return appended ? 0 : 1;
            }

            return buffer.IsActive ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int StreamCallbackThunk(nint userData, int eventType, nint payloadPtr, nuint byteLen)
    {
        return ProcessStreamDelta(userData, eventType, payloadPtr, byteLen);
    }

    private static string Invoke(Func<nint> nativeCall)
    {
        var pointer = nativeCall();
        if (pointer == 0)
        {
            throw new InvalidOperationException("PopGlot Core returned an empty response.");
        }

        try
        {
            return Marshal.PtrToStringUTF8(pointer)
                ?? throw new InvalidOperationException("PopGlot Core returned invalid UTF-8.");
        }
        finally
        {
            NativeMethods.FreeString(pointer);
        }
    }

    internal static T EnsureSuccess<T>(string json) =>
        UnwrapEnvelope(JsonSerializer.Deserialize<Envelope<T>>(json, JsonOptions));

    /// <summary>
    /// Prompt 专用 envelope 反序列化：Rust 域的 PromptTemplate /
    /// CompiledPrompt 数据体是 camelCase，必须用 <see cref="PromptJsonOptions"/>
    /// 解开。若走 snake_case 的 <see cref="EnsureSuccess{T}"/>，多词字段
    /// （schemaVersion / isBuiltIn / pastRevisions / templateId /
    /// compiledText）会静默绑定失败并回落默认值 —— 单词字段恰好小写一致，
    /// 这种半残数据比报错更危险。
    /// </summary>
    internal static T EnsurePromptSuccess<T>(string json) =>
        UnwrapEnvelope(JsonSerializer.Deserialize<Envelope<T>>(json, PromptJsonOptions));

    private static T UnwrapEnvelope<T>(Envelope<T>? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("PopGlot Core response was empty.");
        }
        if (!response.Ok || response.Data is null)
        {
            throw new InvalidOperationException(response.Error ?? "PopGlot Core operation failed.");
        }
        return response.Data;
    }

    private sealed record Envelope<T>(bool Ok, T? Data, string? Error);

    private static partial class NativeMethods
    {
        [LibraryImport(LibraryName, EntryPoint = "popglot_initialize", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint Initialize(string configDirectory);

        [LibraryImport(LibraryName, EntryPoint = "popglot_get_settings")]
        internal static partial nint GetSettings();

        [LibraryImport(LibraryName, EntryPoint = "popglot_take_startup_notice")]
        internal static partial nint TakeStartupNotice();

        [LibraryImport(LibraryName, EntryPoint = "popglot_save_settings", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint SaveSettings(string json);

        [LibraryImport(LibraryName, EntryPoint = "popglot_list_prompt_templates")]
        internal static partial nint ListPromptTemplates();

        [LibraryImport(LibraryName, EntryPoint = "popglot_get_active_prompt_template")]
        internal static partial nint GetActivePromptTemplate();

        [LibraryImport(LibraryName, EntryPoint = "popglot_set_active_prompt_template", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint SetActivePromptTemplate(string? templateId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_save_prompt_template", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint SavePromptTemplate(string templateJson);

        [LibraryImport(LibraryName, EntryPoint = "popglot_delete_prompt_template", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint DeletePromptTemplate(string templateId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_compile_prompt", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint CompilePrompt(string templateJson, string variablesJson);

        [LibraryImport(LibraryName, EntryPoint = "popglot_plan_screenshot_route")]
        internal static partial nint PlanScreenshotRoute(int localOcrAvailable, int credentialPresent);

        [LibraryImport(LibraryName, EntryPoint = "popglot_select_route", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint SelectRoute(string factsJson);

        [LibraryImport(LibraryName, EntryPoint = "popglot_test_connection_draft", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TestConnectionDraft(string draftJson, string apiKey, string? requestId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_text_draft_v1", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TranslateTextDraftV1(
            string draftJson,
            string apiKey,
            string source,
            string sourceLang,
            string targetLang,
            string? requestId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_text_task_v1", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TextTaskV1(
            string apiKey, string source, string sourceLang, string targetLang, string task, string requestId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_text_task_draft_v1", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TextTaskDraftV1(
            string settingsJson, string apiKey, string source, string sourceLang,
            string targetLang, string task, string requestId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_text_v3", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TranslateTextV3(
            string apiKey,
            string source,
            string? sourceLang,
            string? targetLang,
            string? requestId,
            string? preference,
            string? templateId,
            ulong templateRevision);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_text_stream_v2", StringMarshalling = StringMarshalling.Utf8)]
        internal static unsafe partial nint TranslateTextStreamV2(
            string apiKey,
            string source,
            string? sourceLang,
            string? targetLang,
            string? requestId,
            string? preference,
            string? templateId,
            ulong templateRevision,
            delegate* unmanaged[Cdecl]<nint, int, nint, nuint, int> callback,
            nint userData);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_text_draft_stream_v2", StringMarshalling = StringMarshalling.Utf8)]
        internal static unsafe partial nint TranslateTextDraftStreamV2(
            string settingsJson,
            string apiKey,
            string source,
            string? sourceLang,
            string? targetLang,
            string? requestId,
            string? preference,
            string? templateId,
            ulong templateRevision,
            delegate* unmanaged[Cdecl]<nint, int, nint, nuint, int> callback,
            nint userData);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_vision_v3", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TranslateVisionV3(
            string apiKey,
            string visionApiKey,
            string settingsJson,
            string mediaType,
            string imageBase64,
            string sourceLang,
            string targetLang,
            string requestId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_vision_draft_stream_v1", StringMarshalling = StringMarshalling.Utf8)]
        internal static unsafe partial nint TranslateVisionDraftStreamV1(
            string apiKey,
            string? visionApiKey,
            string? settingsJson,
            string mediaType,
            string imageBase64,
            string? sourceLang,
            string? targetLang,
            string? requestId,
            delegate* unmanaged[Cdecl]<nint, int, nint, nuint, int> callback,
            nint userData);


        [LibraryImport(LibraryName, EntryPoint = "popglot_cancel_request", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int CancelRequest(string requestId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_protect_tokens", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint ProtectTokens(string text);

        [LibraryImport(LibraryName, EntryPoint = "popglot_restore_tokens", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint RestoreTokens(string translated, string tokensJson);

        [LibraryImport(LibraryName, EntryPoint = "popglot_classify_endpoint", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint ClassifyEndpoint(string url);

        [LibraryImport(LibraryName, EntryPoint = "popglot_plan_segments", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint PlanSegments(string text, int maxSegmentChars, int maxSegments);

        [LibraryImport(LibraryName, EntryPoint = "popglot_cancel_active_request")]
        internal static partial int CancelActiveRequest();

        [LibraryImport(LibraryName, EntryPoint = "popglot_free_string")]
        internal static partial void FreeString(nint value);
    }
}

internal enum TranslationMode
{
    Auto,
    LocalOcr,
    VisionDirect,
    /// 视觉模型识别截图文字，译文由文本模型翻译。
    VisionOcr,
}

internal enum ProviderType
{
    OpenAiCompatible,
    OpenAiResponses,
    AnthropicMessages,
    GeminiGenerateContent,
}

/// A dedicated vision provider: complete connection details for screenshot
/// traffic. The API key never travels inside this record — it is supplied per
/// request — so it is safe to persist as part of the settings document.
internal sealed record VisionProviderOverride(
    ProviderType ProviderType,
    string ApiBaseUrl,
    string VisionEndpoint,
    string VisionModel,
    IReadOnlyDictionary<string, string> ExtraHeaders,
    string AnthropicVersion,
    bool AllowInsecureTls = false,
    bool AllowLanEndpoints = false);

internal sealed record ProviderSettings(
    uint SchemaVersion,
    ProviderType ProviderType,
    string ApiBaseUrl,
    string TextEndpoint,
    string VisionEndpoint,
    string TextModel,
    string VisionModel,
    IReadOnlyDictionary<string, string> ExtraHeaders,
    string AnthropicVersion,
    bool SupportsText,
    bool SupportsVision,
    bool NetworkEnabled,
    TranslationMode Mode,
    bool AllowImageUploadInAuto,
    bool SafeDevMode,
    bool AllowLanEndpoints,
    bool AllowInsecureTls,
    bool ApiKeyConfigured,
    string SourceLanguage,
    string TargetLanguage,
    bool IncludeExplanation,
    bool ProtectCodeTokens,
    VisionProviderOverride? VisionProvider = null)
{
    public bool VisionIsConfigured => SupportsVision && !string.IsNullOrWhiteSpace(VisionModel);
    public bool TextIsConfigured => SupportsText && !string.IsNullOrWhiteSpace(TextModel);

    /// <summary>Loopback only: content genuinely stays on this machine.</summary>
    public bool TargetsLocalRuntime => ClassifyEndpoint(ApiBaseUrl) == EndpointClass.Loopback;

    /// <summary>Another device on the LAN: content has left the machine.</summary>
    public bool TargetsPrivateNetwork => ClassifyEndpoint(ApiBaseUrl) == EndpointClass.PrivateNetwork;

    internal static bool IsLocalBaseUrl(string? baseUrl) =>
        ClassifyEndpoint(baseUrl) != EndpointClass.Internet;

    /// <summary>
    /// The three-way host classification shared with the Rust core
    /// (AI-RULES §4.1). Unknown or malformed input classifies conservatively
    /// as Internet.
    /// </summary>
    internal static EndpointClass ClassifyEndpoint(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return EndpointClass.Internet;
        }
        var text = baseUrl.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return EndpointClass.Internet;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host.Length == 0)
        {
            return EndpointClass.Internet;
        }
        if (host is "localhost" or "::1" || host.EndsWith(".localhost", StringComparison.Ordinal))
        {
            return EndpointClass.Loopback;
        }
        if (System.Net.IPAddress.TryParse(host, out var address))
        {
            if (System.Net.IPAddress.IsLoopback(address))
            {
                return EndpointClass.Loopback;
            }
            var octets = address.GetAddressBytes();
            switch (octets.Length)
            {
                case 4:
                    return (octets[0], octets[1]) switch
                    {
                        (10, _) or (192, 168) => EndpointClass.PrivateNetwork,
                        (172, var second) when second is >= 16 and <= 31 => EndpointClass.PrivateNetwork,
                        _ => EndpointClass.Internet,
                    };
                case 16:
                    // IPv6 unique local addresses: fc00::/7.
                    if ((octets[0] & 0xFE) == 0xFC)
                    {
                        return EndpointClass.PrivateNetwork;
                    }
                    break;
            }
        }
        return EndpointClass.Internet;
    }
}

internal enum EndpointClass
{
    Loopback,
    PrivateNetwork,
    Internet,
}

internal sealed record RoutingDecision(
    TranslationMode SelectedMode,
    string ReasonCode,
    string ExplanationZh,
    bool MayUploadImage);

/// <summary>
/// The shell-collected facts one screenshot routing decision needs. Field
/// names bind to the Rust `RoutingContext` (snake_case). The shell owns
/// observation; the domain owns the decision.
/// </summary>
internal sealed record ScreenshotRouteFacts(
    string RequestedMode,
    bool VisionConfigured,
    string VisionEndpointClass,
    bool ImageUploadAllowed,
    bool AllowLanEndpoints,
    bool LocalOcrAvailable,
    bool TextRouteAvailable);

/// One protected placeholder, mirrored from the Rust domain's ProtectedToken.
internal sealed record ProtectedTokenDto(string Placeholder, string Original);

/// The masked text plus its token table, from `popglot_protect_tokens`.
internal sealed record ProtectedTextDto(string SanitizedText, IReadOnlyList<ProtectedTokenDto> Tokens);

/// Exactly-once restoration result, from `popglot_restore_tokens`.
internal sealed record RestoredTextDto(
    string Text,
    IReadOnlyList<string> DroppedTerms,
    IReadOnlyList<string> DuplicatedTerms,
    IReadOnlyList<string> UnknownPlaceholders);

/// <summary>
/// The shell-side view of the Rust session plan. <see cref="RejectedReason"/>
/// is null unless the source cannot be translated within the session budget
/// ("code_block_too_large" / "too_many_segments").
/// </summary>
internal sealed record SegmentPlanDto(
    string Mode,
    IReadOnlyList<string>? Segments = null,
    string? RejectedReason = null);

// ---------------------------------------------------------------------------
// Prompt template DTOs. Rust 域的四个 prompt 结构体均为
// #[serde(rename_all = "camelCase")]，与设置文档的 snake_case 不同；这些
// record 必须经 CoreBridge 的 PromptJsonOptions（camelCase）序列化。
// ---------------------------------------------------------------------------

/// <summary>
/// C# 镜像 Rust 域的 <c>PromptTemplate</c>：一种翻译风格或领域偏好的模板定义。
/// 与 ProviderSettings 一样不含任何凭据，可安全持久化。
/// </summary>
internal sealed record PromptTemplateDto(
    string Id,
    uint SchemaVersion = 1,
    string Name = "",
    string Description = "",
    string Instruction = "",
    string Domain = "",
    string Audience = "",
    bool Enabled = true,
    bool IsBuiltIn = false,
    ulong Revision = 1,
    ulong CreatedAt = 0,
    ulong UpdatedAt = 0,
    /// <summary>
    /// 参数默认值只能是常量，故保持 null；序列化由 PromptJsonOptions 的
    /// WhenWritingNull 把 null 折叠成「字段缺席」，Rust 侧 Vec 的
    /// #[serde(default)] 接受缺席、拒绝显式 null，契约因此闭合。
    /// </summary>
    IReadOnlyList<PastRevisionDto>? PastRevisions = null);

/// <summary>C# 镜像 Rust 域的 <c>PastRevision</c>：模板某个历史修订的快照。</summary>
internal sealed record PastRevisionDto(
    ulong Revision,
    string Instruction,
    string Domain = "",
    string Audience = "",
    ulong UpdatedAt = 0);

/// <summary>
/// C# 镜像 Rust 域的 <c>PromptVariables</c>：纯函数编译器的白名单变量输入。
/// Domain/Audience 为 null 时回落到模板默认值。
/// </summary>
internal sealed record PromptVariablesDto(
    string SourceLanguage = "",
    string TargetLanguage = "",
    string? Domain = null,
    string? Audience = null);

/// <summary>
/// C# 镜像 Rust 域的 <c>CompiledPrompt</c>：模板经变量展开后的编译结果，
/// 也是翻译请求偏好锚点 (templateId, revision, compiledText) 的形状。
/// </summary>
internal enum TextTaskKind
{
    Summarize,
    Explain,
}

internal sealed record CompiledPromptDto(
    string TemplateId,
    ulong Revision,
    string CompiledText);

/// <summary>
/// 一个翻译会话的偏好锚点快照：会话起点经 Rust CompilePrompt 本地编译出的
/// (preference, templateId, revision) 三元组。同一会话的所有 provider 分段
/// 与重试显式携带同一实例，Rust 按显式锚点原样发请求、不再按请求重新解析
/// active 模板 —— 多段/重试在途不变性。null 锚点保持旧默认：Rust 在请求
/// 起点自行解析（faithful 与编译失败时的字节级等价回退）。
/// </summary>
internal sealed record PromptAnchorSnapshot(
    string Preference,
    string TemplateId,
    ulong Revision);

internal sealed record TranslationResult(
    string TranslatedText,
    string Transcription,
    string Explanation,
    IReadOnlyList<string> ProtectedTerms,
    IReadOnlyList<string> Warnings,
    string Phonetic = "",
    bool IsPartial = false);

internal sealed record ProviderDiagnostics(
    string RequestId,
    ProviderType ProviderType,
    string Endpoint,
    byte Attempts,
    ushort StatusCode,
    ulong ElapsedMs);

internal sealed record TranslationResponse(
    TranslationResult Result,
    ProviderDiagnostics Diagnostics)
{
    public bool IsFreeEngine =>
        string.Equals(Diagnostics.RequestId, FreeTranslateService.RequestId, StringComparison.Ordinal);

    public string EngineLabel => IsFreeEngine ? "免费引擎" : Diagnostics.ProviderType.ToString();
}

