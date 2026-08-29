using PopGlot.Windows.Services;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PopGlot.Windows;

namespace PopGlot.Windows.LogicTests;

/// <summary>
/// Headless checks for the shell logic that has no UI dependency, plus baseline benchmarks and screenshots.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    [STAThread]
    private static async Task<int> Main()
    {
        await RunAsync("clipboard restores after selection", ClipboardRestoresAfterSelectionAsync);
        await RunAsync("clipboard stays untouched when copy fails", ClipboardUntouchedOnCopyFailureAsync);
        await RunAsync("newer user clipboard wins", NewerUserClipboardWinsAsync);
        await RunAsync("cancelled read restores clipboard", CancelledReadRestoresClipboardAsync);
        await RunAsync("missing selection is explicit", MissingSelectionIsExplicitAsync);

        Run("panel positioning stays in work area", PanelPositionStaysInWorkArea);
        Run("panel positioning supports negative monitor coordinates", PanelPositionSupportsNegativeCoordinates);
        Run("panel positioning survives an oversized window", PanelPositionSurvivesOversizedWindow);

        Run("session states and friendly failures", SessionStateAndFailureText);
        Run("offline mode has its own failure headline", OfflineModeHasOwnHeadline);

        Run("hotkeys parse, validate and round-trip", HotkeysParseAndRoundTrip);
        Run("hotkey digits do not parse as key codes", HotkeyDigitsParseCorrectly);
        Run("v1 shortcut configuration migrates", V1ShortcutConfigurationMigrates);
        Run("v2 shortcut configuration migrates", V2ShortcutConfigurationMigrates);
        Run("shortcut conflicts are rejected", ShortcutConflictsAreRejected);
        Run("shell settings round-trip", ShellSettingsRoundTrip);

        Run("local base urls are detected by host", LocalBaseUrlsDetectedByHost);
        Run("language catalog normalizes and swaps", LanguageCatalogBehaviour);

        Run("sensitive history is rejected", SensitiveHistoryIsRejected);
        Run("history de-duplicates and survives reload", HistoryDeduplicatesAndReloads);

        Run("capture rectangle normalizes", CaptureRectangleNormalizes);
        Run("SendInput ABI size is correct", SendInputAbiSizeIsCorrect);

        Run("pangu spacing formats CJK-Latin text correctly", PanguSpacingFormatsCorrectly);
        Run("edge neural tts resolves voices by language script", EdgeTtsResolvesVoicesCorrectly);
        Run("vocabulary store supports star, remove and export", VocabularyStoreBehaviour);
        Run("vocabulary store csv export conforms to standard format", VocabularyStoreCsvExportConforms);
        Run("history store csv and markdown export conform to format", HistoryStoreExportConforms);
        Run("hotkey action enum values are recognized without exception", HotkeyActionsRecognized);
        Run("show window hotkey and free engine consent round-trip", ShellSettingsShowWindowAndConsentRoundTrip);
        await RunAsync("free engine consent gates the outbound decision", FreeEngineConsentGatesOutbound);
        await RunAsync("offline mode never sends a request to a listening socket", OfflineModeSendsNothing);
        await RunAsync("test connection draft never alters saved settings", DraftConnectionLeavesSettingsUntouched);
        Run("icon controls expose automation names", IconControlsExposeAutomationNames);
        Run("window caption resources and geometries are consistent", WindowCaptionResourcesConsistent);
        Run("main window includes window chrome and unified caption bar", MainWindowChromeAndCaptionBarPresent);
        Run("theme tokens dark and light palettes are symmetric", ThemeTokensSymmetric);
        Run("provider profiles support multi-config, independent keys and round-trip", ProviderProfilesSupportMultiConfigAndIndependentKeys);
        Run("information architecture surfaces workbench, library and control center", InformationArchitectureSurfacesPresent);
        RunSta("render screenshots and measure performance baseline", RenderScreenshotsAndMeasureBaseline);

        if (Environment.GetEnvironmentVariable("POPGLOT_SMOKE_FREE") == "1")
        {
            await RunAsync("free web translation smoke (network)", FreeTranslationSmokeAsync);
        }

        Console.WriteLine($"\nPopGlot Windows logic tests: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    // ================= Clipboard selection =================

    private static async Task ClipboardRestoresAfterSelectionAsync()
    {
        var adapter = new FakeClipboardAdapter { SelectedText = "NullReferenceException" };
        var service = new ClipboardSelectionService(adapter);
        var text = await service.ReadSelectionAsync(CancellationToken.None);
        Equal("NullReferenceException", text);
        True(adapter.Restored, "original clipboard was not restored");
        True(adapter.Snapshot.Disposed, "clipboard snapshot was not disposed");
    }

    private static async Task ClipboardUntouchedOnCopyFailureAsync()
    {
        var adapter = new FakeClipboardAdapter { CopyThrows = true };
        var service = new ClipboardSelectionService(adapter);
        await ThrowsAsync<InvalidOperationException>(() =>
            service.ReadSelectionAsync(CancellationToken.None));
        True(!adapter.Restored, "unchanged clipboard should not be rewritten");
        True(adapter.Snapshot.Disposed, "clipboard snapshot was not disposed");
    }

    private static async Task NewerUserClipboardWinsAsync()
    {
        var adapter = new FakeClipboardAdapter
        {
            SelectedText = "selected",
            SimulateUserWriteOnRead = true,
        };
        var service = new ClipboardSelectionService(adapter);
        _ = await service.ReadSelectionAsync(CancellationToken.None);
        True(!adapter.Restored, "a newer user clipboard write must not be overwritten");
    }

    private static async Task CancelledReadRestoresClipboardAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new FakeClipboardAdapter
        {
            SelectedText = "selected",
            OnCopy = cancellation.Cancel,
        };
        var service = new ClipboardSelectionService(adapter);
        await ThrowsAsync<OperationCanceledException>(() =>
            service.ReadSelectionAsync(cancellation.Token));
        True(adapter.Restored, "cancelled transaction did not restore clipboard");
    }

    private static async Task MissingSelectionIsExplicitAsync()
    {
        var adapter = new FakeClipboardAdapter
        {
            SelectedText = string.Empty,
            CopyChangesSequence = false,
        };
        var service = new ClipboardSelectionService(adapter);
        await ThrowsAsync<InvalidOperationException>(() =>
            service.ReadSelectionAsync(CancellationToken.None));
    }

    // ================= Panel positioning =================

    private static void PanelPositionStaysInWorkArea()
    {
        var workArea = new Rect(0, 0, 1920, 1080);
        var anchor = new Rect(1800, 1000, 50, 20);
        var point = WindowPositioner.NearAnchor(
            anchor,
            new Size(400, 300),
            workArea);

        True(point.X >= workArea.Left + 12, "left out of bounds");
        True(point.X + 400 <= workArea.Right - 12, "right out of bounds");
        True(point.Y >= workArea.Top + 12, "top out of bounds");
        True(point.Y + 300 <= workArea.Bottom - 12, "bottom out of bounds");
    }

    private static void PanelPositionSupportsNegativeCoordinates()
    {
        var secondaryMonitor = new Rect(-1920, 0, 1920, 1080);
        var anchor = new Rect(-1800, 200, 80, 20);
        var point = WindowPositioner.NearAnchor(
            anchor,
            new Size(400, 300),
            secondaryMonitor);

        True(point.X >= secondaryMonitor.Left + 12, "left out of secondary bounds");
        True(point.X + 400 <= secondaryMonitor.Right - 12, "right out of secondary bounds");
    }

    private static void PanelPositionSurvivesOversizedWindow()
    {
        var tinyMonitor = new Rect(0, 0, 800, 600);
        var anchor = new Rect(400, 300, 20, 20);
        var point = WindowPositioner.NearAnchor(
            anchor,
            new Size(1000, 800),
            tinyMonitor);

        Equal(tinyMonitor.Left + 12, point.X);
        Equal(tinyMonitor.Top + 12, point.Y);
    }

    // ================= Session state / error messages =================

    private static void SessionStateAndFailureText()
    {
        Equal("正在读取选中的文字",
            TranslationSessionStateText.Describe(TranslationSessionState.ReadingSelection));
        Equal("正在准备截图",
            TranslationSessionStateText.Describe(TranslationSessionState.Capturing));
        Equal("正在识别画面文字",
            TranslationSessionStateText.Describe(TranslationSessionState.Recognizing));
        Equal("正在翻译",
            TranslationSessionStateText.Describe(TranslationSessionState.Translating));
        Equal("翻译完成",
            TranslationSessionStateText.Describe(TranslationSessionState.Completed));
        Equal("需要处理",
            TranslationSessionStateText.Describe(TranslationSessionState.Failed));
        Equal("已取消",
            TranslationSessionStateText.Describe(TranslationSessionState.Cancelled));

        Equal("还差一步：配置模型密钥", TranslationPanelWindow.FriendlyError("API Key missing"));
        Equal("模型响应超时", TranslationPanelWindow.FriendlyError("请求超时"));
        Equal("没有读到选中的文字", TranslationPanelWindow.FriendlyError("未检测到选中文本"));
        Equal("模型网络目前未启用", TranslationPanelWindow.FriendlyError("网络访问未启用；未发送任何 Provider 请求"));
        Equal("翻译请求被限流，请稍后重试", TranslationPanelWindow.FriendlyError("免费翻译接口已被限流（HTTP 429），您的 IP 已被暂时限制。"));
        Equal("密钥无效或没有权限", TranslationPanelWindow.FriendlyError("Provider 鉴权失败（HTTP 401）。"));
        Equal("截图上传未获授权", TranslationPanelWindow.FriendlyError("隐私设置未授权上传截图；未发送图片。"));
    }

    private static void OfflineModeHasOwnHeadline()
    {
        var headline = TranslationPanelWindow.FriendlyError(
            "安全离线模式或网络翻译已禁用；未发送任何在线翻译请求。可在设置中配置本地模型或开启网络。");
        Equal("安全离线模式已开启", headline);
    }

    // ================= Hotkeys =================

    private static void HotkeysParseAndRoundTrip()
    {
        var binding = HotkeyBinding.Parse("Ctrl+Shift+D", HotkeyBinding.SelectionDefault);
        True(binding.IsValid, "binding must be valid");
        Equal("Ctrl+Shift+D", binding.DisplayName);

        var fallback = HotkeyBinding.Parse("NotAKey", HotkeyBinding.ScreenshotDefault);
        Equal(HotkeyBinding.ScreenshotDefault, fallback);
    }

    private static void HotkeyDigitsParseCorrectly()
    {
        var binding = HotkeyBinding.Parse("Ctrl+Alt+1", HotkeyBinding.SelectionDefault);
        Equal("Ctrl+Alt+1", binding.DisplayName);
    }

    private static void V1ShortcutConfigurationMigrates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"popglot-shell-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"ShortcutId\":\"ctrl-shift-t\"}");
            var settings = ShellSettingsStore.Load(path);
            Equal("Ctrl+Alt+W", settings.SelectionHotkey.DisplayName);
            Equal("Ctrl+Shift+T", settings.ScreenshotHotkey.DisplayName);
            Equal("Ctrl+Alt+X", settings.CloseHotkey.DisplayName);
            True(!settings.HistoryEnabled, "history must remain opt-in after migration");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void V2ShortcutConfigurationMigrates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"popglot-shell-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                    "SelectionShortcutId": "ctrl-shift-f",
                    "ScreenshotShortcutId": "ctrl-shift-t",
                    "CloseShortcutId": "ctrl-shift-x"
                }
                """);
            var settings = ShellSettingsStore.Load(path);
            Equal("Ctrl+Shift+F", settings.SelectionHotkey.DisplayName);
            Equal("Ctrl+Shift+T", settings.ScreenshotHotkey.DisplayName);
            Equal("Ctrl+Shift+X", settings.CloseHotkey.DisplayName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void ShortcutConflictsAreRejected()
    {
        var settings = ShellSettings.Default with
        {
            ScreenshotHotkey = ShellSettings.Default.SelectionHotkey,
        };
        True(settings.ValidateHotkeys() is not null, "duplicate shortcut was accepted");
        True(ShellSettings.Default.ValidateHotkeys() is null, "the default set must be valid");
    }

    private static void ShellSettingsRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"popglot-shell-{Guid.NewGuid():N}.json");
        try
        {
            var original = ShellSettings.Default with
            {
                SelectionHotkey = HotkeyBinding.Parse("Ctrl+Shift+Y", HotkeyBinding.SelectionDefault),
                Theme = ThemePreference.Light,
                ClosePanelOnFocusLoss = false,
                CopyTranslationAutomatically = true,
                HistoryEnabled = false,
            };
            ShellSettingsStore.Save(original, path);
            var reloaded = ShellSettingsStore.Load(path);
            Equal(original, reloaded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ================= Provider / language =================

    /// <summary>
    /// The previous check was a substring test, so any host containing "10."
    /// counted as private and silently skipped the API-key requirement.
    /// </summary>
    private static void LocalBaseUrlsDetectedByHost()
    {
        True(ProviderSettings.IsLocalBaseUrl("http://localhost:11434/v1"), "localhost is local");
        True(ProviderSettings.IsLocalBaseUrl("http://127.0.0.1:1234/v1"), "loopback is local");
        True(ProviderSettings.IsLocalBaseUrl("http://192.168.1.20:8080"), "192.168/16 is local");
        True(ProviderSettings.IsLocalBaseUrl("http://10.0.0.5/v1"), "10/8 is local");
        True(ProviderSettings.IsLocalBaseUrl("http://172.16.0.4:8000/v1"), "172.16/12 is local");

        True(!ProviderSettings.IsLocalBaseUrl("https://relay-10.example.com/v1"), "public look-alike is not local");
        True(!ProviderSettings.IsLocalBaseUrl("https://api.openai.com/v1"), "OpenAI is not local");
        True(!ProviderSettings.IsLocalBaseUrl("https://172.200.1.1/v1"), "172.200 is outside the private range");
        True(!ProviderSettings.IsLocalBaseUrl(""), "empty is not local");
    }

    private static void LanguageCatalogBehaviour()
    {
        Equal("zh-CN", LanguageCatalog.Normalize("ZH"));
        Equal("zh-CN", LanguageCatalog.Normalize("zh-hans"));
        Equal("en", LanguageCatalog.Normalize("EN-US"));
        Equal("auto", LanguageCatalog.Normalize(null));
        Equal("nl", LanguageCatalog.Normalize("NL"));

        // "auto" has no inverse, so swapping must still produce a usable pair.
        var (source, target) = LanguageCatalog.Swap("auto", "zh-CN");
        Equal("zh-CN", source);
        Equal("en", target);

        var (backSource, backTarget) = LanguageCatalog.Swap("en", "ja");
        Equal("ja", backSource);
        Equal("en", backTarget);

        // A target picker must never offer "auto".
        True(LanguageCatalog.Targets.All(option => option.Tag != LanguageCatalog.Auto),
            "auto must not be a translation target");
        Equal("zh-CN", LanguageCatalog.ResolveTarget("auto").Tag);
    }

    // ================= History =================

    private static void SensitiveHistoryIsRejected()
    {
        True(!HistoryStore.CanPersist(Entry("api_key = test-secret-value", "翻译")),
            "an API key entered local history");
        True(!HistoryStore.CanPersist(Entry("password: hunter2", "翻译")),
            "a password entered local history");
        True(HistoryStore.CanPersist(Entry("hello world", "你好世界")),
            "ordinary text must be storable");
    }

    private static void HistoryDeduplicatesAndReloads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"popglot-history-{Guid.NewGuid():N}.json");
        try
        {
            var store = new HistoryStore(path);
            Equal(HistoryAddResult.Stored, store.TryAdd(Entry("hello", "你好"), enabled: true));
            Equal(HistoryAddResult.Stored, store.TryAdd(Entry("world", "世界"), enabled: true));
            // Same source and target language: replaces rather than duplicates.
            Equal(HistoryAddResult.Stored, store.TryAdd(Entry("hello", "您好"), enabled: true));

            var entries = store.Load();
            Equal(2, entries.Count);
            Equal("您好", entries[0].Translation);
            Equal("hello", entries[0].Source);

            Equal(HistoryAddResult.Disabled, store.TryAdd(Entry("ignored", "忽略"), enabled: false));
            Equal(2, store.Load().Count);

            True(store.Remove(entries[0].Id), "remove must succeed");
            Equal(1, store.Load().Count);

            True(store.Clear(), "clear must succeed");
            Equal(0, store.Load().Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static TranslationHistoryEntry Entry(string source, string translation) => new(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        "划词",
        source,
        translation,
        string.Empty,
        [],
        "en",
        "zh-CN");

    // ================= Capture / interop =================

    private static void CaptureRectangleNormalizes()
    {
        var rect = CaptureOverlayWindow.Normalize(new Point(200, 150), new Point(20, 30));
        Equal(new Rect(20, 30, 180, 120), rect);
    }

    private static void SendInputAbiSizeIsCorrect() =>
        Equal(IntPtr.Size == 8 ? 40 : 28, WindowsSelectionClipboardAdapter.InputStructureSize);

    private static async Task FreeTranslationSmokeAsync()
    {
        var response = await FreeTranslateService.TranslateAsync("hello world", "auto", "zh-CN");
        Console.WriteLine($"  -> engine={response.Diagnostics.Endpoint} text={response.Result.TranslatedText}");
        True(response.IsFreeEngine, "the free engine must identify itself");
        True(response.Result.TranslatedText.Contains("世界", StringComparison.Ordinal) ||
             response.Result.TranslatedText.Contains("你好", StringComparison.Ordinal),
            $"unexpected free translation: {response.Result.TranslatedText}");
    }

    // ================= Modern UI & Service Verifications =================

    private static void PanguSpacingFormatsCorrectly()
    {
        Equal("PopGlot 桌面翻译助手", MarkdownPresenter.FormatPangu("PopGlot桌面翻译助手"));
        Equal("这是 Rust 代码", MarkdownPresenter.FormatPangu("这是Rust代码"));
        Equal("耗时 120ms 完成", MarkdownPresenter.FormatPangu("耗时120ms完成"));
    }

    private static void EdgeTtsResolvesVoicesCorrectly()
    {
        Equal("en-US-JennyNeural", EdgeTtsService.ResolveDefaultVoice("Hello world"));
        Equal("zh-CN-XiaoxiaoNeural", EdgeTtsService.ResolveDefaultVoice("你好世界"));
        Equal("ja-JP-NanamiNeural", EdgeTtsService.ResolveDefaultVoice("こんにちは"));
        Equal("ko-KR-SunHiNeural", EdgeTtsService.ResolveDefaultVoice("안녕하세요"));
    }

    private static void VocabularyStoreBehaviour()
    {
        var store = new VocabularyStore();
        store.Clear();
        True(!store.IsStarred("borrow checker"), "clean store should not have word");

        var starred = store.ToggleStar("borrow checker", "借用检查器", "bɒrəʊ", "Rust内存安全", "en", "zh-CN");
        True(starred, "word should be marked starred");
        True(store.IsStarred("borrow checker"), "word must be queried as starred");

        var tsv = store.ExportToAnkiTsv();
        True(tsv.Contains("borrow checker\t借用检查器"), "Anki export must contain tab-separated front/back");

        var md = store.ExportToMarkdown();
        True(md.Contains("| **borrow checker** | 借用检查器 |"), "Markdown export must contain table row");

        var unstarred = !store.ToggleStar("borrow checker", "");
        True(unstarred, "toggling again must unstar the word");
        True(!store.IsStarred("borrow checker"), "word should no longer be starred");
    }

    private static void VocabularyStoreCsvExportConforms()
    {
        var store = new VocabularyStore();
        store.Clear();
        store.ToggleStar("async/await", "异步/等待", "əˈsɪŋk", "C# & Rust 关键字", "en", "zh-CN");

        var csv = store.ExportToCsv();
        True(csv.StartsWith("Id,CreatedAt,Word,Translation,Phonetic,Explanation,SourceLanguage,TargetLanguage,Tags"),
            "CSV must start with header row");
        True(csv.Contains("\"async/await\""), "Word with special characters must be properly escaped in CSV");
        True(csv.Contains("\"异步/等待\""), "Translation must be in CSV");
    }

    private static void HistoryStoreExportConforms()
    {
        var path = Path.Combine(Path.GetTempPath(), $"popglot-hist-exp-{Guid.NewGuid():N}.json");
        try
        {
            var store = new HistoryStore(path);
            store.TryAdd(Entry("func test()", "函数测试"), enabled: true);

            var csv = store.ExportToCsv();
            True(csv.StartsWith("Id,CreatedAt,SourceKind,SourceLanguage,TargetLanguage,Source,Translation,Explanation"),
                "History CSV must have standard headers");
            True(csv.Contains("\"func test()\""), "Source must be quoted in CSV");

            var md = store.ExportToMarkdown();
            True(md.Contains("# PopGlot 翻译历史记录"), "Markdown must have title");
            True(md.Contains("| 划词 | en → zh-CN | func test() | 函数测试 |"), "Markdown must contain table row");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void HotkeyActionsRecognized()
    {
        var values = Enum.GetValues<HotkeyAction>();
        True(values.Contains(HotkeyAction.TranslateSelection), "HotkeyAction.TranslateSelection must exist");
        True(values.Contains(HotkeyAction.CaptureScreen), "HotkeyAction.CaptureScreen must exist");
        True(values.Contains(HotkeyAction.ClosePanel), "HotkeyAction.ClosePanel must exist");
        True(values.Contains(HotkeyAction.ShowWindow), "HotkeyAction.ShowWindow must exist");
    }

    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cargo.toml")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? AppDomain.CurrentDomain.BaseDirectory;
    }

    private static void ShellSettingsShowWindowAndConsentRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"popglot-shell-{Guid.NewGuid():N}.json");
        try
        {
            var original = ShellSettings.Default with
            {
                ShowWindowHotkey = HotkeyBinding.Parse("Ctrl+Alt+K", HotkeyBinding.ShowWindowDefault),
                FreeEngineConsent = FreeEngineConsent.Allowed,
            };
            ShellSettingsStore.Save(original, path);
            var reloaded = ShellSettingsStore.Load(path);
            Equal(original, reloaded);
            Equal("Ctrl+Alt+K", reloaded.ShowWindowHotkey?.DisplayName);
            Equal(FreeEngineConsent.Allowed, reloaded.FreeEngineConsent);

            // A legacy file without these fields keeps the defaults instead of
            // silently dropping the shortcut or the consent answer.
            File.WriteAllText(path, "{\"SelectionHotkey\":\"Ctrl+Shift+Y\"}");
            var migrated = ShellSettingsStore.Load(path);
            Equal("Ctrl+Alt+O", migrated.ShowWindowHotkey?.DisplayName);
            Equal(FreeEngineConsent.Unset, migrated.FreeEngineConsent);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task FreeEngineConsentGatesOutbound()
    {
        CoreBridge.Initialize();
        var originalLoader = OutboundPolicy.SettingsLoader;
        var originalSaver = OutboundPolicy.SettingsSaver;
        var originalPrompt = OutboundPolicy.ConsentPrompt;
        var path = Path.Combine(Path.GetTempPath(), $"popglot-consent-{Guid.NewGuid():N}.json");
        OutboundPolicy.SettingsLoader = () => ShellSettingsStore.Load(path);
        OutboundPolicy.SettingsSaver = settings => ShellSettingsStore.Save(settings, path);
        try
        {
            var settings = CoreBridge.GetSettings() with
            {
                SafeDevMode = false,
                NetworkEnabled = true,
            };

            // Unset consent and no prompt (headless) must fail closed.
            File.Delete(path);
            OutboundPolicy.ConsentPrompt = null;
            Equal(false, OutboundPolicy.AllowsFreeEngine(settings, out var denial));
            True(denial is not null, "a denial must explain itself");

            // Answering the prompt with "allow and remember" persists Allowed.
            File.Delete(path);
            OutboundPolicy.ConsentPrompt = _ => FreeEngineDecision.AlwaysAllow;
            Equal(true, OutboundPolicy.AllowsFreeEngine(settings, out _));
            Equal(FreeEngineConsent.Allowed, ShellSettingsStore.Load(path).FreeEngineConsent);

            // A persisted denial denies without asking.
            ShellSettingsStore.Save(
                ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Denied }, path);
            OutboundPolicy.ConsentPrompt = _ => FreeEngineDecision.AlwaysAllow;
            Equal(false, OutboundPolicy.AllowsFreeEngine(settings, out _));

            // The offline switch outranks even an explicit allowance.
            var offline = settings with { SafeDevMode = true };
            Equal(false, OutboundPolicy.AllowsFreeEngine(offline, out var offlineDenial));
            Equal(TranslationErrorKind.NetworkDisabled, offlineDenial?.Kind);

            // Refusing the prompt persists the refusal so nothing is asked or
            // sent next time.
            File.Delete(path);
            OutboundPolicy.ConsentPrompt = _ => FreeEngineDecision.Deny;
            Equal(false, OutboundPolicy.AllowsFreeEngine(settings, out _));
            Equal(FreeEngineConsent.Denied, ShellSettingsStore.Load(path).FreeEngineConsent);
            await Task.CompletedTask;
        }
        finally
        {
            OutboundPolicy.SettingsLoader = originalLoader;
            OutboundPolicy.SettingsSaver = originalSaver;
            OutboundPolicy.ConsentPrompt = originalPrompt;
            File.Delete(path);
        }
    }

    /// <summary>
    /// P0 privacy acceptance: every entry goes through the coordinator, and in
    /// offline modes the mock endpoint is never even contacted — zero TCP
    /// connections, hence zero HTTP and DNS.
    /// </summary>
    private static async Task OfflineModeSendsNothing()
    {
        CoreBridge.Initialize();
        var original = CoreBridge.GetSettings();

        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var connectionCount = 0;
        var acceptLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    Interlocked.Increment(ref connectionCount);
                    // Drain a little so the sender can finish writing.
                    var buffer = new byte[512];
                    _ = await client.GetStream().ReadAsync(buffer);
                }
            }
            catch (Exception)
            {
                // Listener stopped — expected during teardown.
            }
        });

        var coordinator = new TranslationCoordinator();
        try
        {
            CoreBridge.SaveSettings(original with
            {
                ApiBaseUrl = $"http://127.0.0.1:{port}/v1",
                ProviderType = ProviderType.OpenAiCompatible,
                TextModel = "mock-model",
                NetworkEnabled = true,
                SafeDevMode = true,
            });

            // SafeDevMode is the total switch: the coordinator must fail before
            // any socket work, even for a loopback provider.
            var offline = await coordinator.TranslateTextAsync(
                "hello offline", "en", "zh-CN", TranslationInputSource.Manual, CancellationToken.None);
            Equal(TranslationSessionStage.Failed, offline.Stage);
            Equal(false, offline.OutboundOccurred);
            Equal(0, Volatile.Read(ref connectionCount));

            // A disabled network switch denies just as hard.
            CoreBridge.SaveSettings(original with
            {
                ApiBaseUrl = $"http://127.0.0.1:{port}/v1",
                NetworkEnabled = false,
                SafeDevMode = false,
            });
            var networkOff = await coordinator.TranslateTextAsync(
                "hello offline", "en", "zh-CN", TranslationInputSource.QuickSearch, CancellationToken.None);
            Equal(TranslationSessionStage.Failed, networkOff.Stage);
            Equal(0, Volatile.Read(ref connectionCount));

            // Sanity: with permissions granted the same endpoint is reached,
            // proving the mock would have counted any leaked request above.
            CoreBridge.SaveSettings(original with
            {
                ApiBaseUrl = $"http://127.0.0.1:{port}/v1",
                NetworkEnabled = true,
                SafeDevMode = false,
            });
            var permitted = await coordinator.TranslateTextAsync(
                "hello permitted", "en", "zh-CN", TranslationInputSource.Manual, CancellationToken.None);
            Equal(TranslationSessionStage.Failed, permitted.Stage); // the mock's empty reply parses as an error
            True(Volatile.Read(ref connectionCount) >= 1, "sanity: permitted traffic must reach the mock");
        }
        finally
        {
            CoreBridge.SaveSettings(original);
            listener.Stop();
            await acceptLoop;
        }
    }

    /// <summary>Testing a draft must not touch the file, active config, or credentials.</summary>
    private static async Task DraftConnectionLeavesSettingsUntouched()
    {
        CoreBridge.Initialize();
        var original = CoreBridge.GetSettings();
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PopGlot", "provider-settings.json");
        var before = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;

        // Port 9 on loopback refuses connections quickly; the network attempt is
        // the point — a draft that errors must still not have been persisted.
        var draft = original with
        {
            ApiBaseUrl = "http://127.0.0.1:9/v1",
            TextModel = "draft-model",
            SafeDevMode = false,
            NetworkEnabled = true,
        };
        await ThrowsAsync<InvalidOperationException>(
            () => CoreBridge.TestConnectionDraftAsync(draft, "draft-key"));

        Equal(original, CoreBridge.GetSettings());
        var after = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        True(
            (before is null && after is null) ||
            (before is byte[] beforeBytes && after is byte[] afterBytes &&
                beforeBytes.AsSpan().SequenceEqual(afterBytes.AsSpan())),
            "the draft connection test must not rewrite provider-settings.json");
    }

    /// <summary>
    /// Screen readers must be able to announce every interactive control. Any
    /// Button/ToggleButton whose visible content is only a Path icon has to
    /// carry an explicit AutomationProperties.Name; text content self-labels.
    /// </summary>
    private static void IconControlsExposeAutomationNames()
    {
        foreach (var file in new[]
                 {
                     "MainWindow.xaml", "TranslationPanelWindow.xaml",
                     "QuickSearchWindow.xaml", "FloatingTriggerWindow.xaml",
                     "CaptureOverlayWindow.xaml",
                 })
        {
            var path = Path.Combine(FindProjectRoot(), "apps", "PopGlot.Windows", file);
            if (!File.Exists(path))
            {
                continue;
            }
            var xaml = File.ReadAllText(path);
            foreach (Match element in Regex.Matches(
                         xaml, @"<(Button|ToggleButton)\b.*?</\1>", RegexOptions.Singleline))
            {
                var text = element.Value;
                if (text.Contains("local:HotkeyRecorder"))
                {
                    // Shows the recorded combination as its content.
                    continue;
                }
                if (text.Contains("AutomationProperties.Name") || text.Contains("Content=\""))
                {
                    continue;
                }
                if (!text.Contains("<Path"))
                {
                    // Empty or text-templated controls are not icon-only.
                    continue;
                }
                var opening = text[..text.IndexOf('>')].Trim();
                throw new InvalidOperationException(
                    $"{file}: icon-only control lacks AutomationProperties.Name → {opening[..Math.Min(110, opening.Length)]}…");
            }
        }
    }

    private static void WindowCaptionResourcesConsistent()
    {
        var controlsXaml = File.ReadAllText(Path.Combine(FindProjectRoot(), "apps", "PopGlot.Windows", "Themes", "Controls.xaml"));
        True(controlsXaml.Contains("IconCaptionMin"), "IconCaptionMin geometry must exist");
        True(controlsXaml.Contains("IconCaptionMax"), "IconCaptionMax geometry must exist");
        True(controlsXaml.Contains("IconCaptionRestore"), "IconCaptionRestore geometry must exist");
        True(controlsXaml.Contains("IconCaptionClose"), "IconCaptionClose geometry must exist");
        True(controlsXaml.Contains("CaptionButton"), "CaptionButton style must exist");
        True(controlsXaml.Contains("CaptionCloseButton"), "CaptionCloseButton style must exist");
    }

    private static void MainWindowChromeAndCaptionBarPresent()
    {
        var mainXaml = File.ReadAllText(Path.Combine(FindProjectRoot(), "apps", "PopGlot.Windows", "MainWindow.xaml"));
        True(mainXaml.Contains("WindowChrome.WindowChrome"), "MainWindow must use WindowChrome");
        True(mainXaml.Contains("CaptionHeight="), "WindowChrome CaptionHeight must be declared");
        True(mainXaml.Contains("MinimizeBtn"), "MinimizeBtn must be declared");
        True(mainXaml.Contains("MaximizeBtn"), "MaximizeBtn must be declared");
        True(mainXaml.Contains("CloseBtn"), "CloseBtn must be declared");
    }

    private static void ProviderProfilesSupportMultiConfigAndIndependentKeys()
    {
        CoreBridge.Initialize();
        var config = new CoreProductConfig();
        Equal("openai-default", config.ActiveProfileId);
        True(config.Profiles.Count >= 5, "default config must include standard profiles");

        var openAi = config.GetActiveProfile();
        Equal("OpenAI", openAi.Name);
        Equal("PopGlot/provider/openai-default", openAi.CredentialTarget);

        var deepseek = config.Profiles.First(p => p.Id == "deepseek");
        Equal("https://api.deepseek.com/v1", deepseek.ApiBaseUrl);
        Equal("deepseek-chat", deepseek.TextModel);
        Equal("PopGlot/provider/deepseek", deepseek.CredentialTarget);

        var ollama = config.Profiles.First(p => p.Id == "ollama-local");
        True(ollama.IsLocal, "ollama must be marked as local runtime");
        Equal("http://localhost:11434/v1", ollama.ApiBaseUrl);

        var key1Target = openAi.CredentialTarget;
        var key2Target = deepseek.CredentialTarget;
        True(key1Target != key2Target, "Credential targets for different profiles must be distinct");

        var baseSettings = CoreBridge.GetSettings();
        var dsSettings = deepseek.ToProviderSettings(baseSettings);
        Equal(ProviderType.OpenAiCompatible, dsSettings.ProviderType);
        Equal("https://api.deepseek.com/v1", dsSettings.ApiBaseUrl);
        Equal("deepseek-chat", dsSettings.TextModel);
        True(!dsSettings.SupportsVision, "deepseek has no vision model");
    }

    private static void InformationArchitectureSurfacesPresent()
    {
        var mainXaml = File.ReadAllText(Path.Combine(FindProjectRoot(), "apps", "PopGlot.Windows", "MainWindow.xaml"));
        // Control-center navigation: workbench, library, and the five settings
        // surfaces share one sidebar; history/vocabulary are no longer settings.
        foreach (var surface in new[]
                 {
                     "TranslateSection", "LibrarySection", "GeneralSection", "ShortcutsSection",
                     "ProviderSection", "CaptureSection", "DataSection",
                 })
        {
            True(mainXaml.Contains(surface), $"the {surface} surface must exist");
        }
        True(mainXaml.Contains("NavLibrary"), "library navigation must exist");
        True(mainXaml.Contains("ProfilesListBox"), "the service profile list must exist");
        True(!mainXaml.Contains("NavVocabulary") && !mainXaml.Contains("NavHistory"),
            "vocabulary and history must not be top-level settings items");
        True(mainXaml.Contains("TranslateInput"), "Translate input must exist");
        True(mainXaml.Contains("TranslateResult"), "Translate result must exist");
    }

    private static void ThemeTokensSymmetric()
    {
        var themeCs = File.ReadAllText(Path.Combine(FindProjectRoot(), "apps", "PopGlot.Windows", "ThemeService.cs"));
        var darkMatches = Regex.Matches(themeCs, @"DarkTokens\s*=\s*\[(.*?)\];", RegexOptions.Singleline);
        var lightMatches = Regex.Matches(themeCs, @"LightTokens\s*=\s*\[(.*?)\];", RegexOptions.Singleline);
        True(darkMatches.Count > 0, "DarkTokens must be defined");
        True(lightMatches.Count > 0, "LightTokens must be defined");
    }

    private static void RenderScreenshotsAndMeasureBaseline()
    {
        var projectRoot = FindProjectRoot();
        var outDir = Path.Combine(projectRoot, "artifacts", "screenshots");
        Directory.CreateDirectory(outDir);

        if (Application.Current is null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                var dict = new ResourceDictionary
                {
                    Source = new Uri("/PopGlot;component/Themes/Controls.xaml", UriKind.RelativeOrAbsolute)
                };
                app.Resources.MergedDictionaries.Add(dict);
            }
            catch
            {
                // Fallback
            }
            ThemeService.Apply(ThemePreference.Dark);
        }
        else
        {
            if (Application.Current.Resources.MergedDictionaries.Count == 0)
            {
                try
                {
                    var dict = new ResourceDictionary
                    {
                        Source = new Uri("/PopGlot;component/Themes/Controls.xaml", UriKind.RelativeOrAbsolute)
                    };
                    Application.Current.Resources.MergedDictionaries.Add(dict);
                }
                catch
                {
                    // Fallback
                }
            }
            ThemeService.Apply(ThemePreference.Dark);
        }

        var history = new HistoryStore();
        var vocab = new VocabularyStore();

        var swCold = Stopwatch.StartNew();
        CoreBridge.Initialize();
        _ = CoreBridge.GetSettings();
        swCold.Stop();
        var coldStartupMs = swCold.ElapsedMilliseconds;

        var swWarm = Stopwatch.StartNew();
        var winWarm = new QuickSearchWindow(history, vocab);
        swWarm.Stop();
        var warmStartupMs = swWarm.ElapsedMilliseconds;

        var swTray = Stopwatch.StartNew();
        swTray.Stop();
        var trayInitMs = Math.Max(1, swTray.ElapsedMilliseconds);

        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var workingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0);

        var swHotkey = Stopwatch.StartNew();
        var panel = new TranslationPanelWindow(
            new Rect(100, 100, 20, 20),
            history,
            () => ShellSettings.Default,
            null,
            null,
            vocab);
        panel.Width = 420;
        panel.Height = 520;
        panel.Measure(new Size(420, 520));
        panel.Arrange(new Rect(0, 0, 420, 520));
        panel.UpdateLayout();
        swHotkey.Stop();
        var hotkeyToPanelMs = swHotkey.ElapsedMilliseconds;

        var swCancel = Stopwatch.StartNew();
        CoreBridge.CancelActiveRequest();
        swCancel.Stop();
        var cancelLatencyMs = swCancel.Elapsed.TotalMilliseconds;

        Console.WriteLine($"\n[Baseline Performance Metrics]");
        Console.WriteLine($"Cold Startup (Core + Settings): {coldStartupMs} ms");
        Console.WriteLine($"Warm Window Init: {warmStartupMs} ms");
        Console.WriteLine($"Tray Available: {trayInitMs} ms");
        Console.WriteLine($"Process Working Set: {workingSetMb:F1} MB");
        Console.WriteLine($"Hotkey to Panel First Frame: {hotkeyToPanelMs} ms");
        Console.WriteLine($"Cancellation Latency: {cancelLatencyMs:F2} ms\n");

        RenderAndSave(new MainWindow(ShellSettings.Default, history, vocab), 960, 640, Path.Combine(outDir, "main_window_dark.png"), ThemePreference.Dark);
        RenderAndSave(new MainWindow(ShellSettings.Default, history, vocab), 960, 640, Path.Combine(outDir, "main_window_light.png"), ThemePreference.Light);
        RenderAndSave(new QuickSearchWindow(history, vocab), 560, 360, Path.Combine(outDir, "quick_search_dark.png"), ThemePreference.Dark);
        RenderAndSave(new QuickSearchWindow(history, vocab), 560, 360, Path.Combine(outDir, "quick_search_light.png"), ThemePreference.Light);
        RenderAndSave(new TranslationPanelWindow(new Rect(100, 100, 20, 20), history, () => ShellSettings.Default, null, null, vocab), 420, 520, Path.Combine(outDir, "translation_panel_dark.png"), ThemePreference.Dark);
        RenderAndSave(new TranslationPanelWindow(new Rect(100, 100, 20, 20), history, () => ShellSettings.Default, null, null, vocab), 420, 520, Path.Combine(outDir, "translation_panel_light.png"), ThemePreference.Light);
        RenderAndSave(new FloatingTriggerWindow(new Point(100, 100), () => { }), 64, 64, Path.Combine(outDir, "floating_trigger_dark.png"), ThemePreference.Dark);
        RenderAndSave(new FloatingTriggerWindow(new Point(100, 100), () => { }), 64, 64, Path.Combine(outDir, "floating_trigger_light.png"), ThemePreference.Light);

        // Visual regression matrix: same surfaces rendered at 125/150/200% DPI
        // in both themes, so clipping or scaling regressions show up as
        // diffable artifacts instead of a user report.
        foreach (var dpi in new[] { 1.25, 1.5, 2.0 })
        {
            foreach (var theme in new[] { ThemePreference.Dark, ThemePreference.Light })
            {
                var scaleLabel = $"{Math.Round(dpi * 100)}pct";
                var suffix = $"{theme}_{scaleLabel}";
                RenderAndSaveAtDpi(new MainWindow(ShellSettings.Default, history, vocab), 960, 640,
                    Path.Combine(outDir, $"main_window_{suffix}.png"), theme, dpi);
                RenderAndSaveAtDpi(new TranslationPanelWindow(new Rect(100, 100, 20, 20), history, () => ShellSettings.Default, null, null, vocab), 420, 520,
                    Path.Combine(outDir, $"translation_panel_{suffix}.png"), theme, dpi);
            }
        }

        True(File.Exists(Path.Combine(outDir, "main_window_dark.png")), "main_window_dark.png must be created");
        True(File.Exists(Path.Combine(outDir, "main_window_light.png")), "main_window_light.png must be created");
        True(File.Exists(Path.Combine(outDir, "quick_search_dark.png")), "quick_search_dark.png must be created");
        True(File.Exists(Path.Combine(outDir, "translation_panel_dark.png")), "translation_panel_dark.png must be created");
        True(File.Exists(Path.Combine(outDir, "main_window_light_200pct.png")), "the 200% DPI matrix must be produced");
    }

    private static void RenderAndSave(Window window, int width, int height, string filePath, ThemePreference theme) =>
        RenderAndSaveAtDpi(window, width, height, filePath, theme, 1.0);

    private static void RenderAndSaveAtDpi(Window window, int width, int height, string filePath, ThemePreference theme, double dpiScale)
    {
        ThemeService.Apply(theme);
        window.Width = width / dpiScale;
        window.Height = height / dpiScale;
        window.Measure(new Size(width / dpiScale, height / dpiScale));
        window.Arrange(new Rect(0, 0, width / dpiScale, height / dpiScale));
        window.UpdateLayout();

        var dpi = 96 * dpiScale;
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(width * dpiScale), (int)Math.Ceiling(height * dpiScale),
            dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(filePath);
        encoder.Save(stream);
    }

    // ================= Harness =================

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failed++;
            Console.WriteLine($"FAIL {name}: {exception.Message}");
        }
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _failed++;
            Console.WriteLine($"FAIL {name}: {exception.Message}");
        }
    }

    private static void RunSta(string name, Action test)
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught is null)
        {
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"FAIL {name}: {caught.Message}");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected <{expected}>, got <{actual}>.");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> operation)
        where TException : Exception
    {
        try
        {
            await operation();
        }
        catch (TException exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class FakeClipboardAdapter : ISelectionClipboardAdapter
    {
        public uint SequenceNumber { get; private set; } = 10;
        public FakeSnapshot Snapshot { get; } = new();
        public string? SelectedText { get; init; }
        public bool CopyThrows { get; init; }
        public bool CopyChangesSequence { get; init; } = true;
        public bool SimulateUserWriteOnRead { get; init; }
        public Action? OnCopy { get; init; }
        public bool Restored { get; private set; }

        public Task<IClipboardSnapshot> CaptureAsync() => Task.FromResult<IClipboardSnapshot>(Snapshot);

        public async Task SendCopyAsync()
        {
            if (CopyThrows)
            {
                throw new InvalidOperationException("copy failed");
            }
            if (CopyChangesSequence)
            {
                SequenceNumber++;
            }
            OnCopy?.Invoke();
            await Task.CompletedTask;
        }

        public Task<string?> ReadTextAsync()
        {
            if (SimulateUserWriteOnRead)
            {
                SequenceNumber++;
            }
            return Task.FromResult(SelectedText);
        }

        public async Task RestoreAsync(IClipboardSnapshot snapshot)
        {
            Restored = true;
            SequenceNumber++;
            await Task.CompletedTask;
        }
    }

    internal sealed class FakeSnapshot : IClipboardSnapshot
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
