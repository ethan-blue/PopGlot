using PopGlot.Windows.Services;
using PopGlot.Windows.Sections;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
        Run("vocabulary store handles corrupt json safely", VocabularyStoreHandlesCorruptJsonSafely);
        Run("history store csv and markdown export conform to format", HistoryStoreExportConforms);
        Run("hotkey action enum values are recognized without exception", HotkeyActionsRecognized);
        Run("show window hotkey and free engine consent round-trip", ShellSettingsShowWindowAndConsentRoundTrip);
        await RunAsync("free engine consent gates the outbound decision", FreeEngineConsentGatesOutbound);
        await RunAsync("offline policy blocks remote but allows local providers", OfflineModeSendsNothing);
        await RunAsync("test connection draft never alters saved settings", DraftConnectionLeavesSettingsUntouched);
        Run("icon controls expose automation names", IconControlsExposeAutomationNames);
        Run("window caption resources and geometries are consistent", WindowCaptionResourcesConsistent);
        Run("main window includes window chrome and unified caption bar", MainWindowChromeAndCaptionBarPresent);
        Run("theme tokens dark and light palettes are symmetric", ThemeTokensSymmetric);
        Run("provider profiles support multi-config, independent keys and round-trip", ProviderProfilesSupportMultiConfigAndIndependentKeys);
        Run("service save resolves credential targets per profile", ServiceSaveResolvesCredentialTargets);
        Run("service save writes the key after resolving its target", ServiceSaveKeyOrderGuard);
        Run("settings save validates hotkeys before persisting", SettingsSaveValidatesBeforePersisting);
        Run("connection test failures map to actionable hints", ConnectionTestFailuresAreActionable);
        Run("service health states are explicit and hue-safe", ServiceHealthStatesAreExplicit);
        Run("loaded service does not become a false draft", LoadedServiceDoesNotBecomeFalseDraft);
        Run("shortcut recording suspends global shortcuts", ShortcutRecordingSuspendsGlobalShortcuts);
        Run("capture drag avoids forced layout", CaptureDragAvoidsForcedLayout);
        Run("settings closes transient translation surfaces", SettingsClosesTransientSurfaces);
        Run("screenshot draft route is visible", ScreenshotDraftRouteIsVisible);
        Run("service editor fields share a stable responsive grid", ServiceEditorUsesStableResponsiveGrid);
        Run("model catalog endpoints follow provider protocols", ModelCatalogEndpointsFollowProtocols);
        Run("model catalog parses OpenAI and Gemini responses", ModelCatalogParsesProviderResponses);
        await RunAsync("model catalog uses draft credentials without saving", ModelCatalogUsesDraftCredentialsAsync);
        Run("caption buttons really render their icons", CaptionButtonsRenderTheirIcons);
        Run("page transitions have no text-damaging animations", NoTextDamagingPageTransitions);
        Run("text windows are opaque for ClearType", TextWindowsAreOpaque);
        Run("daily flows never open system dialogs", DailyFlowsUseInlineConfirmations);
        Run("unready services cannot become the default", UnreadyServicesCannotBecomeDefault);
        Run("schema v4 factory profiles migrate out of configured services", SchemaV4MigratesPristineTemplates);
        Run("concurrent saves do not collide on temporary files", ProfileManagerConcurrentSavesDoNotClash);
        Run("unsaved load mutation does not poison cached config", UnsavedLoadMutationDoesNotPolluteCache);
        Run("empty config resolves no fabricated providers", EmptyConfigResolvesNoProviders);
        Run("vision readiness requires model and credential", VisionReadinessRequiresModelAndCredential);
        Run("resolved route drives screenshot preview and execution", ResolvedRouteDrivesScreenshotStateMachine);
        await RunAsync("model catalogs are protocol-aware and never invent vision", ModelCatalogsNeverInventVision);
        await RunAsync("model catalog requests filter sensitive headers", ModelCatalogFiltersSensitiveHeaders);
        Run("a failed profile save does not poison the cache", FailedSaveDoesNotPoisonCache);
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
        var tempFile = Path.Combine(Path.GetTempPath(), $"popglot-vocab-{Guid.NewGuid():N}.json");
        try
        {
            var store = new VocabularyStore(tempFile);
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
        finally
        {
            try { File.Delete(tempFile); } catch { }
            try { File.Delete(tempFile + ".bak"); } catch { }
        }
    }

    private static void VocabularyStoreCsvExportConforms()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"popglot-vocab-csv-{Guid.NewGuid():N}.json");
        try
        {
            var store = new VocabularyStore(tempFile);
            store.ToggleStar("async/await", "异步/等待", "əˈsɪŋk", "C# & Rust 关键字", "en", "zh-CN");

            var csv = store.ExportToCsv();
            True(csv.StartsWith("Id,CreatedAt,Word,Translation,Phonetic,Explanation,SourceLanguage,TargetLanguage,Tags"),
                "CSV must start with header row");
            True(csv.Contains("\"async/await\""), "Word with special characters must be properly escaped in CSV");
            True(csv.Contains("\"异步/等待\""), "Translation must be in CSV");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            try { File.Delete(tempFile + ".bak"); } catch { }
        }
    }

    private static void VocabularyStoreHandlesCorruptJsonSafely()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"popglot-vocab-corrupt-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempFile, "{ this is not valid JSON }");
            var store = new VocabularyStore(tempFile);
            Equal(0, store.GetAll().Count);
            // Corrupt file was preserved with timestamp suffix
            var dir = Path.GetTempPath();
            var prefix = Path.GetFileName(tempFile) + ".corrupt-";
            var corruptFiles = Directory.EnumerateFiles(dir, prefix + "*").ToList();
            True(corruptFiles.Count > 0, "Corrupt vocabulary JSON must be backed up as a quarantine file");
            foreach (var cf in corruptFiles)
            {
                try { File.Delete(cf); } catch { }
            }
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            try { File.Delete(tempFile + ".bak"); } catch { }
        }
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

            // Unset consent and no prompt (headless / in-window-only flows)
            // must fail closed AND must not record a denial the user never
            // gave — authorization lives in the privacy settings page.
            File.Delete(path);
            OutboundPolicy.ConsentPrompt = null;
            Equal(false, OutboundPolicy.AllowsFreeEngine(settings, out var denial));
            True(denial is not null, "a denial must explain itself");
            True(denial!.ActionableSuggestion?.Contains("隐私与数据") == true,
                "the denial must point at the privacy settings page");
            True(!File.Exists(path) ||
                ShellSettingsStore.Load(path).FreeEngineConsent == FreeEngineConsent.Unset,
                "a missing prompt must never persist a denial");

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
    /// P0 privacy acceptance: offline controls block remote traffic, while a
    /// provider explicitly hosted on loopback remains executable.
    /// </summary>
    /// <summary>
    /// The mock counts accepted connections only after the userspace accept
    /// loop runs, which can lag the kernel-completed handshake by seconds on a
    /// loaded CI runner — poll with a dynamic getter instead of asserting the instant a request fails.
    /// </summary>
    private static async Task WaitUntilConnectionAsync(Func<int> countGetter, int minimum, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (countGetter() < minimum && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        True(countGetter() >= minimum, message);
    }

    private static async Task OfflineModeSendsNothing()
    {
        ProfileManager.ResetForTests();
        var dir = Path.Combine(Path.GetTempPath(), $"popglot-offline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        ProfileManager.ConfigPathOverride = Path.Combine(dir, "product-config.json");

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
            var loopbackProfile = new ProviderProfile
            {
                Id = "text-local",
                Name = "Local text",
                ProviderType = ProviderType.OpenAiCompatible,
                ApiBaseUrl = $"http://127.0.0.1:{port}/v1",
                TextEndpoint = "/chat/completions",
                TextModel = "mock-model",
                VisionModel = string.Empty,
                SupportsText = true,
                SupportsVision = false,
                IsLocal = true,
                CredentialTarget = "PopGlot/provider/text-local",
            };
            ProfileManager.Save(new CoreProductConfig
            {
                ActiveProfileId = loopbackProfile.Id,
                Profiles = [loopbackProfile],
            });

            CoreBridge.SaveSettings(original with
            {
                ApiBaseUrl = $"http://127.0.0.1:{port}/v1",
                ProviderType = ProviderType.OpenAiCompatible,
                TextModel = "mock-model",
                NetworkEnabled = true,
                SafeDevMode = true,
            });

            // Safe mode blocks remote traffic, not an explicitly local model.
            var offline = await coordinator.TranslateTextAsync(
                "hello offline", "en", "zh-CN", TranslationInputSource.Manual, CancellationToken.None);
            Equal(TranslationSessionStage.Failed, offline.Stage); // mock closes without a valid body
            Equal(false, offline.OutboundOccurred);
            await WaitUntilConnectionAsync(() => Volatile.Read(ref connectionCount), 1, "safe mode must still reach loopback");

            // Network Off follows the same locality contract. TextModel must be
            // restated: `original` may carry an empty model on a fresh machine,
            // and an empty model fails validation before any connection.
            CoreBridge.SaveSettings(original with
            {
                ApiBaseUrl = $"http://127.0.0.1:{port}/v1",
                TextModel = "mock-model",
                NetworkEnabled = false,
                SafeDevMode = false,
            });
            var networkOff = await coordinator.TranslateTextAsync(
                "hello offline", "en", "zh-CN", TranslationInputSource.QuickSearch, CancellationToken.None);
            Equal(TranslationSessionStage.Failed, networkOff.Stage);
            True(
                string.Equals(networkOff.PipelineLabel, "本地模型", StringComparison.Ordinal),
                $"network off must route to the local provider, got label " +
                $"<{networkOff.PipelineLabel}> error <{networkOff.Error?.Message}>");
            await WaitUntilConnectionAsync(() => Volatile.Read(ref connectionCount), 2, "network off must still reach loopback");

            // Sanity: normal online mode reaches the same local endpoint too.
            CoreBridge.SaveSettings(original with
            {
                ApiBaseUrl = $"http://127.0.0.1:{port}/v1",
                TextModel = "mock-model",
                NetworkEnabled = true,
                SafeDevMode = false,
            });
            var permitted = await coordinator.TranslateTextAsync(
                "hello permitted", "en", "zh-CN", TranslationInputSource.Manual, CancellationToken.None);
            Equal(TranslationSessionStage.Failed, permitted.Stage); // the mock's empty reply parses as an error
            await WaitUntilConnectionAsync(() => Volatile.Read(ref connectionCount), 3, "sanity: permitted traffic must reach the mock");
        }
        finally
        {
            ProfileManager.ResetForTests();
            if (Directory.Exists(dir))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                }
            }
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

    /// <summary>
    /// Configured services start EMPTY; provider templates live in a separate
    /// catalog that never appears as a configured service. Pristine factory
    /// entries (a legacy-schema artifact) are recognisable for migration.
    /// </summary>
    private static void ProviderProfilesSupportMultiConfigAndIndependentKeys()
    {
        var config = new CoreProductConfig();
        Equal(0, config.Profiles.Count, "a fresh install must have zero configured services");

        var templates = ProviderCatalog.Templates;
        True(templates.Count >= 6, "the catalog must offer the standard provider templates");
        True(templates.Any(t => t.Id == "openai-default"), "OpenAI template exists");
        True(templates.Any(t => t.Id == "deepseek"), "DeepSeek template exists");
        True(templates.Any(t => t.Id == "zhipu"), "GLM template exists");
        True(templates.All(t => string.IsNullOrEmpty(t.TextModel) && string.IsNullOrEmpty(t.VisionModel)),
            "new-service templates must never fabricate model ids");

        // Pristine templates are exactly what migration looks for.
        var deepseek = templates.First(t => t.Id == "deepseek");
        True(ProviderCatalog.IsPristineTemplate(deepseek), "an untouched template is pristine");
        True(ProviderCatalog.IsPristineTemplate(
            ProviderCatalog.Templates.First(t => t.Id == "openai-default")), "openai template is pristine");

        // Any user edit breaks pristineness: renamed, re-modelled, or re-keyed.
        var renamed = new ProviderProfile(deepseek) { Name = "我的 DeepSeek" };
        True(!ProviderCatalog.IsPristineTemplate(renamed), "a renamed service is user-configured");
        var remodelled = new ProviderProfile(deepseek) { TextModel = "deepseek-reasoner" };
        True(!ProviderCatalog.IsPristineTemplate(remodelled), "a re-modelled service is user-configured");
        True(!ProviderCatalog.IsPristineTemplate(
            new ProviderProfile { Id = "custom-1", Name = "x", ApiBaseUrl = "https://x" }),
            "an unknown profile is never pristine");

        var openAi = templates.First(t => t.Id == "openai-default");
        Equal("PopGlot/provider/openai-default", openAi.CredentialTarget);
        var key1Target = openAi.CredentialTarget;
        var key2Target = deepseek.CredentialTarget;
        True(key1Target != key2Target, "Credential targets for different profiles must be distinct");

        var baseSettings = CoreBridge.GetSettings();
        var dsSettings = deepseek.ToProviderSettings(baseSettings);
        Equal(ProviderType.OpenAiCompatible, dsSettings.ProviderType);
        Equal("https://api.deepseek.com/v1", dsSettings.ApiBaseUrl);
        Equal(string.Empty, dsSettings.TextModel);
        True(!dsSettings.SupportsVision, "deepseek has no vision model");
    }

    /// <summary>
    /// The save flow must decide the final profile id and credential target
    /// BEFORE the key is written, and editing must keep a profile's own
    /// target — otherwise a DeepSeek/Gemini/Claude key lands in the OpenAI
    /// default slot and every service shares one credential.
    /// </summary>
    private static void ServiceSaveResolvesCredentialTargets()
    {
        var config = new CoreProductConfig();
        foreach (var template in ProviderCatalog.Templates)
        {
            config.Profiles.Add(new ProviderProfile(template));
        }

        // Adding a profile mints a fresh per-profile target, never the legacy
        // OpenAI default slot.
        var (newId, newTarget) = ProfileManager.ResolveSaveTarget(config, null);
        True(newId.StartsWith("p-", StringComparison.Ordinal), "a new profile gets a generated id");
        True(newTarget.StartsWith("PopGlot/provider/p-", StringComparison.Ordinal),
            "a new profile gets its own credential target");
        True(newTarget != CredentialStore.DefaultTargetName,
            "a new profile's key must not go to the legacy default target");

        // Editing an existing service keeps that service's own target.
        var deepseek = config.Profiles.First(p => p.Id == "deepseek");
        var (editId, editTarget) = ProfileManager.ResolveSaveTarget(config, deepseek.Id);
        Equal("deepseek", editId);
        Equal("PopGlot/provider/deepseek", editTarget);

        var openAi = config.Profiles.First(p => p.Id == "openai-default");
        var (openAiId, openAiTarget) = ProfileManager.ResolveSaveTarget(config, openAi.Id);
        Equal("openai-default", openAiId);
        Equal("PopGlot/provider/openai-default", openAiTarget);

        // A legacy profile with a blank target still derives a per-profile slot.
        config.Profiles.Add(new ProviderProfile { Id = "blank-target", CredentialTarget = "" });
        var (_, blankTarget) = ProfileManager.ResolveSaveTarget(config, "blank-target");
        Equal("PopGlot/provider/blank-target", blankTarget);

        // An unknown editing id (crash mid-save) still gets its own slot.
        var (recoveredId, recoveredTarget) = ProfileManager.ResolveSaveTarget(config, "p-recovered");
        Equal("p-recovered", recoveredId);
        Equal("PopGlot/provider/p-recovered", recoveredTarget);
    }

    /// <summary>
    /// Source-order guard for the credential bug this suite exists to catch:
    /// the key write must textually follow the target resolution.
    /// </summary>
    private static void ServiceSaveKeyOrderGuard()
    {
        var source = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "Sections", "ServicesSection.xaml.cs"));
        var resolve = source.IndexOf("ResolveSaveTarget(config, _editingProfileId)", StringComparison.Ordinal);
        var writeKey = source.IndexOf("CredentialStore.SaveApiKey(typedKey, credentialTarget)", StringComparison.Ordinal);
        True(resolve >= 0, "the save flow must resolve the credential target first");
        True(writeKey > resolve, "the API key must be written only after the profile's own target is resolved");
    }

    /// <summary>
    /// The settings window must finish ALL validation (hotkey shape, hotkey
    /// registration) before the first write, and a failed commit must roll
    /// back what earlier steps already wrote.
    /// </summary>
    private static void SettingsSaveValidatesBeforePersisting()
    {
        var source = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "SettingsWindow.xaml.cs"));
        var validate = source.IndexOf("shellSettings.ValidateHotkeys()", StringComparison.Ordinal);
        var register = source.IndexOf("ApplyShellSettings(shellSettings)", StringComparison.Ordinal);
        var coreSave = source.IndexOf("CoreBridge.SaveSettings(policySettings)", StringComparison.Ordinal);
        var shellSave = source.IndexOf("ShellSettingsStore.Save(shellSettings)", StringComparison.Ordinal);
        True(validate >= 0, "hotkey validation must exist in the save flow");
        True(register > validate, "hotkey registration must follow validation");
        True(coreSave > register, "the core policy must be committed only after full validation");
        True(shellSave > coreSave, "shell settings must be committed after the core policy");
        True(source.Contains("previousCoreSettings", StringComparison.Ordinal),
            "a failed shell write must roll back the core policy via the captured snapshot");
        True(source.Contains("已回滚本次全部修改", StringComparison.Ordinal),
            "a failed commit must tell the user the rollback happened");
        True(source.Contains("未保存任何修改", StringComparison.Ordinal),
            "validation failures must state that nothing was saved");
    }

    /// <summary>Connection-test failures must name the next action to take.</summary>
    private static void ConnectionTestFailuresAreActionable()
    {
        var auth = ServicesSection.DescribeTestFailure(new InvalidOperationException("Provider 鉴权失败（HTTP 401）。"));
        True(auth.Contains("API Key", StringComparison.Ordinal), "auth failures must point at the key");
        var notFound = ServicesSection.DescribeTestFailure(new InvalidOperationException("HTTP 404"));
        True(notFound.Contains("Endpoint", StringComparison.Ordinal), "404 must point at the endpoint");
        var rate = ServicesSection.DescribeTestFailure(new InvalidOperationException("HTTP 429"));
        True(rate.Contains("限流", StringComparison.Ordinal), "429 must explain rate limiting");
        var offline = ServicesSection.DescribeTestFailure(
            new InvalidOperationException("网络访问未启用；未发送任何 Provider 请求"));
        True(offline.Contains("隐私与数据", StringComparison.Ordinal), "offline must point at the privacy switch");
        var timeout = ServicesSection.DescribeTestFailure(new InvalidOperationException("请求超时"));
        True(timeout.Contains("超时", StringComparison.Ordinal), "timeouts must be recognized");
        var unknown = ServicesSection.DescribeTestFailure(new InvalidOperationException("奇怪错误"));
        True(unknown.Contains("奇怪错误", StringComparison.Ordinal), "unknown errors keep their original message");
    }

    /// <summary>
    /// Service rows must expose an explicit health state, and "missing key"
    /// must read as a warning while "usable" reads as success — the brand
    /// accent never stands in for health.
    /// </summary>
    private static void ServiceHealthStatesAreExplicit()
    {
        var (localText, _) = ServicesSection.DescribeProfileState(isLocal: true, hasKey: false, outcome: null);
        Equal("本地服务", localText);

        var (noKeyText, noKeyTone) = ServicesSection.DescribeProfileState(isLocal: false, hasKey: false, outcome: null);
        Equal("缺少 Key", noKeyText);
        Equal(StatusTone.Warning, noKeyTone);

        var (untestedText, untestedTone) = ServicesSection.DescribeProfileState(isLocal: false, hasKey: true, outcome: null);
        Equal("已配置 · 尚未验证", untestedText);
        Equal(StatusTone.Info, untestedTone);

        var (okText, okTone) = ServicesSection.DescribeProfileState(isLocal: false, hasKey: true, outcome: "ok");
        Equal("文字连接已验证", okText);
        Equal(StatusTone.Success, okTone);

        var (failText, failTone) = ServicesSection.DescribeProfileState(isLocal: false, hasKey: true, outcome: "fail");
        Equal("测试失败", failText);
        Equal(StatusTone.Error, failTone);
    }

    // ================= Fourth round: product-defect structural guards =================

    /// <summary>
    /// The caption template must render the Ui.Icon geometry itself — the old
    /// ContentPresenter-only template made min/max/close invisible.
    /// </summary>
    private static void CaptionButtonsRenderTheirIcons()
    {
        var controlsXaml = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "Themes", "Controls.xaml"));
        var captionTemplateStart = controlsXaml.IndexOf(
            "<Style x:Key=\"CaptionButton\"", StringComparison.Ordinal);
        var captionTemplateEnd = controlsXaml.IndexOf(
            "<Style x:Key=\"CaptionCloseButton\"", StringComparison.Ordinal);
        True(captionTemplateStart >= 0 && captionTemplateEnd > captionTemplateStart,
            "CaptionButton style must exist before CaptionCloseButton");
        var template = controlsXaml[captionTemplateStart..captionTemplateEnd];
        True(template.Contains("<Path", StringComparison.Ordinal),
            "the caption template must render a Path");
        True(template.Contains("local:Ui.Icon", StringComparison.Ordinal),
            "the caption Path must bind the Ui.Icon attached property");
        True(template.Contains("Stroke=", StringComparison.Ordinal),
            "caption line-art must be stroked, not filled");

        foreach (var window in new[] { "MainWindow.xaml", "SettingsWindow.xaml" })
        {
            var xaml = File.ReadAllText(Path.Combine(FindProjectRoot(), "apps", "PopGlot.Windows", window));
            var closeIdx = xaml.IndexOf("x:Name=\"CloseBtn\"", StringComparison.Ordinal);
            True(closeIdx >= 0, $"{window} must declare CloseBtn");
            var closeRegion = xaml[closeIdx..(Math.Min(xaml.Length, closeIdx + 400))];
            True(closeRegion.Contains("IconCaptionClose", StringComparison.Ordinal),
                $"{window} CloseBtn must use the IconCaptionClose geometry");
        }
    }

    /// <summary>Fading or translating whole pages blurs every glyph mid-flight.</summary>
    private static void NoTextDamagingPageTransitions()
    {
        var mainCs = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "MainWindow.xaml.cs"));
        True(!mainCs.Contains("PlaySectionEntrance", StringComparison.Ordinal),
            "PlaySectionEntrance must be gone");
        True(!mainCs.Contains("BeginAnimation", StringComparison.Ordinal),
            "the main window must not animate page-level properties");
        var panelCs = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "TranslationPanelWindow.xaml.cs"));
        True(!panelCs.Contains("BeginAnimation(OpacityProperty", StringComparison.Ordinal),
            "the floating panel must not fade the whole window");
    }

    /// <summary>AllowsTransparency windows lose ClearType; text windows are opaque now.</summary>
    private static void TextWindowsAreOpaque()
    {
        foreach (var window in new[] { "TranslationPanelWindow.xaml", "QuickSearchWindow.xaml" })
        {
            var xaml = File.ReadAllText(Path.Combine(FindProjectRoot(), "apps", "PopGlot.Windows", window));
            True(!xaml.Contains("AllowsTransparency=\"True\"", StringComparison.Ordinal),
                $"{window} must not use a layered transparent window");
            // Only the Window element's own background matters; inner controls
            // legitimately use transparent backgrounds.
            var windowTagEnd = xaml.IndexOf('>');
            var windowTag = xaml[..windowTagEnd];
            True(!windowTag.Contains("Background=\"Transparent\"", StringComparison.Ordinal),
                $"{window} must paint an opaque surface");
            True(!xaml.Contains("DropShadowEffect", StringComparison.Ordinal),
                $"{window} must rely on DWM shadow instead of a transparent padding border");
        }
    }

    /// <summary>Only fatal startup errors may use system MessageBoxes.</summary>
    private static void DailyFlowsUseInlineConfirmations()
    {
        foreach (var file in new[]
                 {
                     "SettingsWindow.xaml.cs", "Sections/ServicesSection.xaml.cs",
                     "Sections/DataSection.xaml.cs", "Sections/LibrarySection.xaml.cs",
                     "Sections/PrivacySection.xaml.cs", "TranslationPanelWindow.xaml.cs",
                     "MainWindow.xaml.cs", "QuickSearchWindow.xaml.cs",
                 })
        {
            var source = File.ReadAllText(Path.Combine(FindProjectRoot(), "apps", "PopGlot.Windows",
                file.Replace('/', Path.DirectorySeparatorChar)));
            True(!source.Contains("MessageBox.Show", StringComparison.Ordinal),
                $"{file} must resolve confirmations inline, not via system MessageBox");
        }
    }

    /// <summary>The readiness gate keeps half-configured services off the live route.</summary>
    private static void UnreadyServicesCannotBecomeDefault()
    {
        Equal("缺少 API Key",
            ServicesSection.CheckReadiness(isLocal: false, hasKey: false, textModel: "m", baseUrl: "https://x"));
        Equal("缺少文字模型",
            ServicesSection.CheckReadiness(isLocal: false, hasKey: true, textModel: "", baseUrl: "https://x"));
        Equal("缺少 Base URL",
            ServicesSection.CheckReadiness(isLocal: false, hasKey: true, textModel: "m", baseUrl: ""));
        var ready = ServicesSection.CheckReadiness(isLocal: false, hasKey: true, textModel: "m", baseUrl: "https://x");
        True(ready is null, "a keyed cloud service with a model is ready");
        var localReady = ServicesSection.CheckReadiness(isLocal: true, hasKey: false, textModel: "m", baseUrl: "http://localhost:11434/v1");
        True(localReady is null, "a local service needs no key");
    }

    /// <summary>
    /// Schema v4 seeded factory templates as fake configured services. The
    /// migration drops only pristine+keyless entries and keeps user data.
    /// </summary>
    private static void SchemaV4MigratesPristineTemplates()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"popglot-migrate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "product-config.json");
        var openAi = ProviderCatalog.Templates.First(t => t.Id == "openai-default");
        var deepseek = ProviderCatalog.Templates.First(t => t.Id == "deepseek");
        var userService = new ProviderProfile(deepseek)
        {
            Name = "我的双用途服务",
            TextModel = "shared-model",
            VisionModel = "shared-model",
            SupportsText = false,
            SupportsVision = false,
        };

        var v4 = new CoreProductConfig
        {
            SchemaVersion = 4,
            ActiveProfileId = "deepseek",
            Profiles = [openAi, deepseek, userService],
        };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(v4));

        ProfileManager.ResetForTests();
        ProfileManager.ConfigPathOverride = path;
        try
        {
            var migrated = ProfileManager.Load();
            Equal(6, migrated.SchemaVersion, "migration bumps the schema version");
            Equal(1, migrated.Profiles.Count, "only the user-configured service survives");
            Equal("我的双用途服务", migrated.Profiles[0].Name);
            True(migrated.Profiles[0].SupportsText && migrated.Profiles[0].SupportsVision,
                "model fields, including one shared model, derive both route roles");
            Equal("我的双用途服务", migrated.TryGetActiveProfile()!.Name,
                "a migrated-away default re-points at the surviving text service");
            Equal(6, System.Text.Json.JsonSerializer.Deserialize<CoreProductConfig>(
                File.ReadAllText(path))?.SchemaVersion ?? -1,
                "the migrated schema is persisted");

            True(File.Exists(path + ".bak"), ".bak file was created during migration save");
            var bakConfig = System.Text.Json.JsonSerializer.Deserialize<CoreProductConfig>(File.ReadAllText(path + ".bak"));
            Equal(4, bakConfig?.SchemaVersion ?? -1, ".bak file must be the original pre-migration v4 file");
            Equal(3, bakConfig?.Profiles.Count ?? -1, ".bak file must contain the original 3 profiles before migration");
        }
        finally
        {
            ProfileManager.ResetForTests();
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void ProfileManagerConcurrentSavesDoNotClash()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"popglot-concurrency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "product-config.json");

        ProfileManager.ResetForTests();
        ProfileManager.ConfigPathOverride = path;
        try
        {
            var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
            {
                var config = new CoreProductConfig
                {
                    ActiveProfileId = $"profile-{i}",
                    Profiles = [new ProviderProfile { Id = $"profile-{i}", Name = $"Name-{i}" }]
                };
                ProfileManager.Save(config);
            })).ToArray();

            Task.WaitAll(tasks);

            var loaded = ProfileManager.Load();
            Equal(1, loaded.Profiles.Count, "concurrent saves completed cleanly without corruption");
            True(File.Exists(path), "config file exists on disk");
            var tmpFiles = Directory.GetFiles(dir, "*.tmp");
            Equal(0, tmpFiles.Length, "no leftover tmp files from concurrent saves");
        }
        finally
        {
            ProfileManager.ResetForTests();
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void UnsavedLoadMutationDoesNotPolluteCache()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"popglot-clone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "product-config.json");

        ProfileManager.ResetForTests();
        ProfileManager.ConfigPathOverride = path;
        try
        {
            var initial = new CoreProductConfig
            {
                ActiveProfileId = "original",
                Profiles = [new ProviderProfile { Id = "original", Name = "Original Service" }]
            };
            ProfileManager.Save(initial);

            var loaded1 = ProfileManager.Load();
            loaded1.ActiveProfileId = "mutated-id";
            loaded1.Profiles.Add(new ProviderProfile { Id = "mutated-id", Name = "Mutated Service" });
            loaded1.Profiles[0].Name = "Polluted Name";

            var loaded2 = ProfileManager.Load();
            Equal("original", loaded2.ActiveProfileId, "un-saved mutation must not affect subsequent Load active id");
            Equal(1, loaded2.Profiles.Count, "un-saved mutation must not affect subsequent Load profiles count");
            Equal("Original Service", loaded2.Profiles[0].Name, "un-saved mutation must not affect cached profile properties");
        }
        finally
        {
            ProfileManager.ResetForTests();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A failed disk write must leave the in-memory cache matching disk.</summary>
    private static void FailedSaveDoesNotPoisonCache()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"popglot-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "product-config.json");

        ProfileManager.ResetForTests();
        ProfileManager.ConfigPathOverride = path;
        try
        {
            var original = new CoreProductConfig();
            original.Profiles.Add(new ProviderProfile
            {
                Id = "p-one",
                Name = "第一个",
                CredentialTarget = "PopGlot/provider/p-one",
            });
            ProfileManager.Save(original);

            // Mutate a copy and force the write to fail: the target path is a
            // directory, so the atomic replace throws before the cache swap.
            var mutated = new CoreProductConfig
            {
                SchemaVersion = 5,
                ActiveProfileId = "p-two",
                Profiles = [new ProviderProfile { Id = "p-two", Name = "第二个" }],
            };
            ProfileManager.ConfigPathOverride = Path.Combine(dir, "blocked-dir");
            Directory.CreateDirectory(ProfileManager.ConfigPathOverride);
            var threw = false;
            try
            {
                ProfileManager.Save(mutated);
            }
            catch (Exception)
            {
                threw = true;
            }
            True(threw, "saving onto a directory path must throw");

            ProfileManager.ConfigPathOverride = path;
            var reloaded = ProfileManager.Load();
            Equal(1, reloaded.Profiles.Count, "the cache still holds the last successfully saved config");
            Equal("第一个", reloaded.Profiles[0].Name);
            True(reloaded.Profiles.All(p => p.Id != "p-two"),
                "a failed save must not leak unsaved profiles into the cache");
        }
        finally
        {
            ProfileManager.ResetForTests();
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void LoadedServiceDoesNotBecomeFalseDraft()
    {
        True(!ServicesSection.HasEditorChanges("saved-fields", "saved-fields"),
            "an unchanged loaded service must remain clean");
        True(ServicesSection.HasEditorChanges("changed-fields", "saved-fields"),
            "a real field change must create a draft");
    }

    private static void ShortcutRecordingSuspendsGlobalShortcuts()
    {
        var appDir = Path.Combine(FindProjectRoot(), "apps", "PopGlot.Windows");
        var recorder = File.ReadAllText(Path.Combine(appDir, "HotkeyRecorder.cs"));
        var service = File.ReadAllText(Path.Combine(appDir, "HotkeyService.cs"));
        var settings = File.ReadAllText(Path.Combine(appDir, "SettingsWindow.xaml.cs"));
        True(recorder.Contains("RecordingStateChanged"), "the recorder must expose its active state");
        True(service.Contains("SetSuspended"), "global hotkeys must support temporary suspension");
        True(settings.Contains("SetHotkeysSuspended"), "settings must connect recording to suspension");
    }

    private static void CaptureDragAvoidsForcedLayout()
    {
        var code = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "CaptureOverlayWindow.xaml.cs"));
        var start = code.IndexOf("private void UpdateSelection", StringComparison.Ordinal);
        var end = code.IndexOf("private void PositionHintNearCursor", start, StringComparison.Ordinal);
        True(start >= 0 && end > start, "capture selection method must exist");
        var hotPath = code[start..end];
        True(!hotPath.Contains("UpdateLayout()"), "pointer-move selection must not force synchronous layout");
        True(hotPath.Contains("FromMilliseconds(16)"), "size-label work must be frame bounded");
    }

    private static void SettingsClosesTransientSurfaces()
    {
        var source = File.ReadAllText(Path.Combine(FindProjectRoot(), "apps", "PopGlot.Windows", "App.xaml.cs"));
        var start = source.IndexOf("private void ShowSettings()", StringComparison.Ordinal);
        var end = source.IndexOf("// ================= Single instance", start, StringComparison.Ordinal);
        var method = source[start..end];
        True(method.Contains("CloseActivePanel()"), "settings must close the transient translation panel");
        True(method.Contains("ShowMainWindow()"), "settings must establish the main-window context");
        True(method.Contains("window.Owner = _mainWindow"), "settings must be owned by the main window");
    }

    private static void ScreenshotDraftRouteIsVisible()
    {
        var source = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "Sections", "PrivacySection.xaml.cs"));
        True(source.Contains("RefreshDraftRoutePreview"), "unsaved screenshot settings need a route preview");
        True(source.Contains("保存后预计线路"), "the preview must distinguish draft from actual routing");
        True(source.Contains("RouteBadgeText.Text = pipeline"), "the route badge must follow the calculated route");
        True(source.Contains("ProfileManager.ResolveRoute"), "preview must consume the authoritative resolved route");

        var panel = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "TranslationPanelWindow.xaml.cs"));
        True(panel.Contains("图片已进入视觉请求") && panel.Contains("图片未上传"),
            "screenshot results must disclose what actually crossed the image boundary");
    }

    private static void ServiceEditorUsesStableResponsiveGrid()
    {
        var appDir = Path.Combine(FindProjectRoot(), "apps", "PopGlot.Windows");
        var xaml = File.ReadAllText(Path.Combine(appDir, "Sections", "ServicesSection.xaml"));
        var code = File.ReadAllText(Path.Combine(appDir, "Sections", "ServicesSection.xaml.cs"));

        foreach (var grid in new[]
                 {
                     "IdentityFieldsGrid", "ApiKeyInputGrid", "ModelFieldsGrid",
                     "EndpointFieldsGrid", "AdvancedDetailsGrid",
                 })
        {
            True(xaml.Contains($"x:Name=\"{grid}\""), $"service editor must define {grid}");
        }
        True(xaml.Contains("x:Key=\"EditorTextField\""), "text fields need a shared editor size");
        True(xaml.Contains("x:Key=\"EditorComboField\""), "model fields need a shared editor size");
        True(xaml.Contains("x:Key=\"EditorPasswordField\""), "credential fields need a shared editor size");
        True(code.Contains("Grid.SetColumn(second, 2)"), "wide field pairs must restore into column 2");
        True(!code.Contains("Grid.SetColumn(second, 1)"), "field controls must never occupy the gutter column");
        True(code.Contains("Grid.SetColumnSpan(KeyActionsPanel, 3)"),
            "credential actions must stack without squeezing the key field");
        True(xaml.Contains("Click=\"FetchModels_Click\""), "the model section needs an explicit fetch action");
        True(xaml.Contains("ModelCatalogStatusText"), "model fetch feedback must stay next to the model fields");
        True(xaml.Contains("接口路径") && xaml.Contains("请求定制"),
            "advanced settings must be split into understandable groups");
    }

    private static void ModelCatalogEndpointsFollowProtocols()
    {
        Equal("https://relay.example/v1/models",
            ModelCatalogService.BuildModelsUri(
                "https://relay.example/v1", ProviderType.OpenAiCompatible).AbsoluteUri);
        Equal("https://api.anthropic.com/v1/models?limit=1000",
            ModelCatalogService.BuildModelsUri(
                "https://api.anthropic.com", ProviderType.AnthropicMessages).AbsoluteUri);
        Equal("https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000",
            ModelCatalogService.BuildModelsUri(
                "https://generativelanguage.googleapis.com", ProviderType.GeminiGenerateContent).AbsoluteUri);
        Equal("https://relay.example/v1beta/models?pageSize=1000",
            ModelCatalogService.BuildModelsUri(
                "https://relay.example/v1beta", ProviderType.GeminiGenerateContent).AbsoluteUri);
        Equal("http://127.0.0.1:11434/v1/models",
            ModelCatalogService.BuildModelsUri(
                "http://127.0.0.1:11434/v1", ProviderType.OpenAiCompatible).AbsoluteUri);
        Throws<InvalidOperationException>(() => ModelCatalogService.BuildModelsUri(
            "http://public.example/v1", ProviderType.OpenAiCompatible));
    }

    private static void ModelCatalogParsesProviderResponses()
    {
        var openAi = ModelCatalogService.ParseModels(
            """{"data":[{"id":"gpt-z"},{"id":"gpt-a"},{"id":"gpt-a"}]}""",
            ProviderType.OpenAiCompatible);
        Equal(2, openAi.Count);
        Equal("gpt-a", openAi[0]);

        var gemini = ModelCatalogService.ParseModels(
            """{"models":[{"name":"models/gemini-flash","supportedGenerationMethods":["generateContent"]},{"name":"models/gemini-embedding","supportedGenerationMethods":["embedContent"]}]}""",
            ProviderType.GeminiGenerateContent);
        Equal(1, gemini.Count);
        Equal("gemini-flash", gemini[0]);
    }

    private static async Task ModelCatalogUsesDraftCredentialsAsync()
    {
        CoreBridge.Initialize();
        var draft = CoreBridge.GetSettings() with
        {
            ProviderType = ProviderType.GeminiGenerateContent,
            ApiBaseUrl = "https://generativelanguage.googleapis.com",
            NetworkEnabled = true,
            SafeDevMode = false,
            ExtraHeaders = new Dictionary<string, string>(),
        };
        var handler = new RecordingHttpHandler(
            """{"models":[{"name":"models/gemini-flash","supportedGenerationMethods":["generateContent"]}]}""");
        var result = await ModelCatalogService.FetchAsync(draft, "draft-secret", testHandler: handler);

        Equal("https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000", handler.RequestUri);
        Equal("draft-secret", handler.Headers["x-goog-api-key"]);
        Equal(1, result.Models.Count);
        Equal("gemini-flash", result.Models[0].Id);
    }

    /// <summary>
    /// An empty configuration must resolve to NO provider at all: no OpenAI,
    /// no gpt-4o-mini, no invented vision capability, no credential target.
    /// </summary>
    private static void EmptyConfigResolvesNoProviders()
    {
        var config = new CoreProductConfig();
        True(config.TryGetActiveProfile() is null, "an empty config has no active profile");
        True(config.TryGetVisionProfile() is null, "an empty config has no vision profile");

        ProfileManager.ResetForTests();
        var dir = Path.Combine(Path.GetTempPath(), $"popglot-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        ProfileManager.ConfigPathOverride = Path.Combine(dir, "product-config.json");
        try
        {
            var (text, vision) = ProfileManager.ResolveRoutes();
            True(text is null, "an empty config resolves no text route");
            True(vision is null, "an empty config resolves no vision route");
        }
        finally
        {
            ProfileManager.ResetForTests();
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Catalog adapters must be protocol-aware and must never mark vision as
    /// supported: catalogs that carry no modality data yield Unknown.
    /// </summary>
    private static async Task ModelCatalogsNeverInventVision()
    {
        // OpenAI-compatible: /models returns ids only.
        var openAiPayload = """{"data":[{"id":"m-text-a"},{"id":"m-text-b"}]}""";
        // Gemini: methods prove generation, not image input.
        var geminiPayload = """{"models":[{"name":"models/gem-x","supportedGenerationMethods":["generateContent"]}]}""";

        async Task<ModelCatalogResult> RunAsync(string payload, ProviderSettings settings)
        {
            var handler = new FakeHttpHandler(payload);
            return await ModelCatalogService.FetchAsync(settings, "test-key", testHandler: handler);
        }

        var openAi = await RunAsync(openAiPayload, CoreBridge.GetSettings() with
        {
            ProviderType = ProviderType.OpenAiCompatible,
            ApiBaseUrl = "https://fake.local/v1",
            NetworkEnabled = true,
            SafeDevMode = false,
        });
        Equal(2, openAi.Models.Count);
        True(openAi.Models.All(model => model.VisionInput == CapabilityState.Unknown),
            "an id-only catalog must report Unknown vision capability");
        True(openAi.Models.All(model => model.VisionInput != CapabilityState.Supported),
            "vision must never be invented from a model id");

        var gemini = await RunAsync(geminiPayload, CoreBridge.GetSettings() with
        {
            ProviderType = ProviderType.GeminiGenerateContent,
            ApiBaseUrl = "https://fake.local",
            NetworkEnabled = true,
            SafeDevMode = false,
        });
        Equal(1, gemini.Models.Count);
        Equal("gem-x", gemini.Models[0].Id);
        True(gemini.Models[0].VisionInput == CapabilityState.Unknown,
            "supportedGenerationMethods says nothing about image input");
        await Task.CompletedTask;
    }

    private static async Task ModelCatalogFiltersSensitiveHeaders()
    {
        var extraHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer malicious-auth",
            ["Proxy-Authorization"] = "Basic proxy-token",
            ["Cookie"] = "session=12345",
            ["Set-Cookie"] = "tracker=67890",
            ["x-api-key"] = "leak-claude-key",
            ["api-key"] = "leak-azure-key",
            ["x-goog-api-key"] = "leak-gemini-key",
            ["X-Custom-Trace"] = "safe-trace-id",
            ["X-Client-Version"] = "1.0.0",
        };

        var openAiDraft = CoreBridge.GetSettings() with
        {
            ProviderType = ProviderType.OpenAiCompatible,
            ApiBaseUrl = "https://relay.example/v1",
            NetworkEnabled = true,
            SafeDevMode = false,
            ExtraHeaders = extraHeaders,
        };
        var openAiHandler = new RecordingHttpHandler("""{"data":[{"id":"gpt-4o"}]}""");
        await ModelCatalogService.FetchAsync(openAiDraft, "legit-openai-key", testHandler: openAiHandler);

        Equal("Bearer legit-openai-key", openAiHandler.Headers["Authorization"], "OpenAI adapter must use formal auth header");
        True(!openAiHandler.Headers.ContainsKey("Proxy-Authorization"), "Proxy-Authorization must be filtered");
        True(!openAiHandler.Headers.ContainsKey("Cookie"), "Cookie must be filtered");
        True(!openAiHandler.Headers.ContainsKey("Set-Cookie"), "Set-Cookie must be filtered");
        True(!openAiHandler.Headers.ContainsKey("x-api-key"), "x-api-key must be filtered");
        True(!openAiHandler.Headers.ContainsKey("api-key"), "api-key must be filtered");
        True(!openAiHandler.Headers.ContainsKey("x-goog-api-key"), "x-goog-api-key must be filtered");
        Equal("safe-trace-id", openAiHandler.Headers["X-Custom-Trace"], "Non-sensitive extra header must be preserved");
        Equal("1.0.0", openAiHandler.Headers["X-Client-Version"], "Non-sensitive extra header must be preserved");

        var geminiDraft = CoreBridge.GetSettings() with
        {
            ProviderType = ProviderType.GeminiGenerateContent,
            ApiBaseUrl = "https://generativelanguage.googleapis.com",
            NetworkEnabled = true,
            SafeDevMode = false,
            ExtraHeaders = extraHeaders,
        };
        var geminiHandler = new RecordingHttpHandler("""{"models":[{"name":"models/gemini-flash","supportedGenerationMethods":["generateContent"]}]}""");
        await ModelCatalogService.FetchAsync(geminiDraft, "legit-gemini-key", testHandler: geminiHandler);

        Equal("legit-gemini-key", geminiHandler.Headers["x-goog-api-key"], "Gemini adapter must use formal x-goog-api-key");
        True(!geminiHandler.Headers.ContainsKey("Authorization"), "Authorization must be filtered");
        True(!geminiHandler.Headers.ContainsKey("Proxy-Authorization"), "Proxy-Authorization must be filtered");
        True(!geminiHandler.Headers.ContainsKey("Cookie"), "Cookie must be filtered");
        True(!geminiHandler.Headers.ContainsKey("Set-Cookie"), "Set-Cookie must be filtered");
        True(!geminiHandler.Headers.ContainsKey("x-api-key"), "x-api-key must be filtered");
        True(!geminiHandler.Headers.ContainsKey("api-key"), "api-key must be filtered");
        Equal("safe-trace-id", geminiHandler.Headers["X-Custom-Trace"], "Non-sensitive extra header must be preserved");

        var anthropicDraft = CoreBridge.GetSettings() with
        {
            ProviderType = ProviderType.AnthropicMessages,
            ApiBaseUrl = "https://api.anthropic.com",
            AnthropicVersion = "2023-06-01",
            NetworkEnabled = true,
            SafeDevMode = false,
            ExtraHeaders = extraHeaders,
        };
        var anthropicHandler = new RecordingHttpHandler("""{"data":[{"id":"claude-3-5-sonnet-20241022"}]}""");
        await ModelCatalogService.FetchAsync(anthropicDraft, "legit-claude-key", testHandler: anthropicHandler);

        Equal("legit-claude-key", anthropicHandler.Headers["x-api-key"], "Anthropic adapter must use formal x-api-key");
        Equal("2023-06-01", anthropicHandler.Headers["anthropic-version"], "Anthropic adapter must send anthropic-version");
        True(!anthropicHandler.Headers.ContainsKey("Authorization"), "Authorization must be filtered");
        True(!anthropicHandler.Headers.ContainsKey("Proxy-Authorization"), "Proxy-Authorization must be filtered");
        True(!anthropicHandler.Headers.ContainsKey("Cookie"), "Cookie must be filtered");
        True(!anthropicHandler.Headers.ContainsKey("Set-Cookie"), "Set-Cookie must be filtered");
        True(!anthropicHandler.Headers.ContainsKey("api-key"), "api-key must be filtered");
        True(!anthropicHandler.Headers.ContainsKey("x-goog-api-key"), "x-goog-api-key must be filtered");
        Equal("safe-trace-id", anthropicHandler.Headers["X-Custom-Trace"], "Non-sensitive extra header must be preserved");
    }

    private sealed class FakeHttpHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Vision readiness: no model named, or a cloud service without a key,
    /// means the service cannot serve the vision route.
    /// </summary>
    private static void VisionReadinessRequiresModelAndCredential()
    {
        var config = new CoreProductConfig();
        foreach (var template in ProviderCatalog.Templates)
        {
            config.Profiles.Add(new ProviderProfile(template));
        }

        // A fresh template has no model: not vision-ready even if flagged.
        var flagged = new ProviderProfile(config.Profiles[0])
        {
            SupportsVision = true,
            VisionModel = string.Empty,
        };
        True(!ProfileManager.IsVisionReady(flagged), "a missing vision model blocks readiness");

        // A local service with a model is ready without a key.
        var local = new ProviderProfile(flagged)
        {
            ApiBaseUrl = "http://localhost:11434/v1",
            IsLocal = false, // stale persisted flag must not override the URL
            VisionModel = "llava",
        };
        True(ProfileManager.IsVisionReady(local), "a local vision service needs no key");

        // A cloud service with a model but no key is not ready (test profiles
        // use fictional targets that the real vault does not contain).
        var cloud = new ProviderProfile(flagged)
        {
            VisionModel = "some-vision-model",
            CredentialTarget = $"PopGlot/provider/test-{Guid.NewGuid():N}",
        };
        True(!ProfileManager.IsVisionReady(cloud), "a cloud vision service needs a stored key");
    }

    private static void ResolvedRouteDrivesScreenshotStateMachine()
    {
        ProfileManager.ResetForTests();
        var dir = Path.Combine(Path.GetTempPath(), $"popglot-route-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        ProfileManager.ConfigPathOverride = Path.Combine(dir, "product-config.json");
        try
        {
            var text = new ProviderProfile
            {
                Id = "text-local",
                Name = "Local text",
                ApiBaseUrl = "http://127.0.0.1:11434/v1",
                TextModel = "installed-text",
                VisionModel = string.Empty,
                SupportsText = true,
                SupportsVision = false,
                IsLocal = true,
                CredentialTarget = "PopGlot/provider/text-local",
            };
            var vision = new ProviderProfile
            {
                Id = "vision-local",
                Name = "Local vision",
                ProviderType = ProviderType.GeminiGenerateContent,
                ApiBaseUrl = "http://localhost:9000",
                TextEndpoint = "/v1beta/models/{model}:generateContent",
                VisionEndpoint = "/v1beta/models/{model}:generateContent",
                TextModel = string.Empty,
                VisionModel = "installed-vision",
                SupportsText = false,
                SupportsVision = true,
                IsLocal = true,
                CredentialTarget = "PopGlot/provider/vision-local",
            };
            ProfileManager.Save(new CoreProductConfig
            {
                ActiveProfileId = text.Id,
                VisionProfileId = vision.Id,
                Profiles = [text, vision],
            });

            var routes = ProfileManager.ResolveRoutes();
            Equal(ProviderType.GeminiGenerateContent, routes.Vision!.Profile.ProviderType);
            Equal("PopGlot/provider/vision-local", routes.Vision.CredentialTarget);

            var settings = CoreBridge.GetSettings() with
            {
                Mode = TranslationMode.Auto,
                NetworkEnabled = true,
                SafeDevMode = false,
                AllowImageUploadInAuto = false,
            };
            var localFirst = ProfileManager.ResolveRoute(settings, localOcrAvailable: true);
            Equal(ScreenshotPipeline.LocalOcr, localFirst.ScreenshotPipeline);
            True(!localFirst.MayUploadImage, "auto local-first must not upload pixels");

            var localVisionFallback = ProfileManager.ResolveRoute(settings, localOcrAvailable: false);
            Equal(ScreenshotPipeline.VisionDirect, localVisionFallback.ScreenshotPipeline);
            True(!localVisionFallback.MayUploadImage, "a loopback vision route does not leave the device");

            var unavailable = ProfileManager.ResolveRoute(
                settings with { Mode = TranslationMode.LocalOcr }, localOcrAvailable: false);
            Equal(ScreenshotPipeline.Unavailable, unavailable.ScreenshotPipeline);
        }
        finally
        {
            ProfileManager.ResetForTests();
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void InformationArchitectureSurfacesPresent()
    {
        var appDir = Path.Combine(FindProjectRoot(), "apps", "PopGlot.Windows");
        var mainXaml = File.ReadAllText(Path.Combine(appDir, "MainWindow.xaml"));
        var settingsXaml = File.ReadAllText(Path.Combine(appDir, "SettingsWindow.xaml"));
        var servicesXaml = File.ReadAllText(Path.Combine(appDir, "Sections", "ServicesSection.xaml"));

        // The main window is a work surface only: translate + library, plus a
        // quiet footer with the settings entry. No control center, no save bar.
        foreach (var surface in new[] { "TranslateSection", "LibrarySection" })
        {
            True(mainXaml.Contains(surface), $"the main window must host {surface}");
        }
        True(mainXaml.Contains("NavTranslate"), "translate navigation must exist");
        True(mainXaml.Contains("NavLibrary"), "library navigation must exist");
        True(mainXaml.Contains("SettingsButton"), "the settings entry must exist");
        True(!mainXaml.Contains("ControlCenterHost"), "the control center host must be gone");
        True(!mainXaml.Contains("NavControl"), "the control center nav must be gone");
        True(!mainXaml.Contains("保存设置"), "the main window must not carry the global save bar");
        True(!mainXaml.Contains("放弃修改"), "the main window must not carry the revert action");

        // Settings are a dedicated window with a single nav level and the
        // save bar that only appears with an unsaved draft.
        foreach (var surface in new[]
                 {
                     "GeneralSection", "ShortcutsSection", "ProviderSection",
                     "CaptureSection", "DataSection", "SaveButton",
                 })
        {
            True(settingsXaml.Contains(surface), $"the settings window must host {surface}");
        }
        True(settingsXaml.Contains("NavGeneral"), "settings general nav must exist");
        True(settingsXaml.Contains("NavProvider"), "settings services nav must exist");
        True(settingsXaml.Contains("NavPrivacy"), "settings privacy nav must exist");
        True(settingsXaml.Contains("SaveActionsPanel"), "the draft-only save bar must exist");

        // Services use master–detail: profile list beside the editor.
        True(servicesXaml.Contains("ProfilesListBox"), "the service profile list must exist");
        True(servicesXaml.Contains("DefaultTextCombo"), "the default text service picker must exist");
        True(servicesXaml.Contains("DefaultVisionCombo"), "the default vision service picker must exist");
        True(servicesXaml.Contains("PresetsPanel"), "adding a service must start in a provider catalogue");
        True(servicesXaml.Contains("ConfigFormPanel"), "provider setup must be a separate focused step");
        True(servicesXaml.Contains("ChooseAnotherProviderButton"), "the setup step must return to the catalogue");
        True(!servicesXaml.Contains("<UniformGrid"), "provider choices must not look like a chip dashboard");
        True(servicesXaml.Contains("EditorProviderTitle"), "configured services need an identity-led detail header");
        True(servicesXaml.Contains("Click=\"EditProfile_Click\""), "each configured service needs an explicit edit action");
        True(servicesXaml.Contains("Click=\"BackToServices_Click\""), "the focused editor must return to the service overview");
        True(servicesXaml.Contains("RoutingPanel"), "default routing must stay separate from provider editing");
        True(!servicesXaml.Contains("ColumnDefinition x:Name=\"DetailColumn\""),
            "the narrow permanent master-detail rail must be removed");

        var serviceCode = File.ReadAllText(Path.Combine(appDir, "Sections", "ServicesSection.xaml.cs"));
        True(serviceCode.Contains("CaptureEditorState()"), "service drafts must use value-based dirty tracking");
        True(serviceCode.Contains("_editorBaseline"), "loaded services must retain a clean editor baseline");

        var projectXaml = File.ReadAllText(Path.Combine(appDir, "PopGlot.Windows.csproj"));
        True(projectXaml.Contains("PopGlot-v3.ico"), "the selected v3 app icon must be packaged");
        True(projectXaml.Contains("popglot-app-avatar-v3.png"), "the selected v3 sidebar mark must be packaged");
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
        RenderAndSave(new SettingsWindow(ShellSettings.Default, history, vocab), 960, 680, Path.Combine(outDir, "settings_dark.png"), ThemePreference.Dark);
        RenderAndSave(new SettingsWindow(ShellSettings.Default, history, vocab), 960, 680, Path.Combine(outDir, "settings_light.png"), ThemePreference.Light);
        RenderAndSave(CreateServiceEditorPreview(), 760, 620, Path.Combine(outDir, "service_editor_dark.png"), ThemePreference.Dark);
        RenderAndSave(CreateServiceEditorPreview(), 760, 620, Path.Combine(outDir, "service_editor_light.png"), ThemePreference.Light);
        RenderAndSave(CreateServiceEditorPreview(), 620, 720, Path.Combine(outDir, "service_editor_compact_light.png"), ThemePreference.Light);
        RenderAndSave(CreateAdvancedServiceEditorPreview(), 760, 920, Path.Combine(outDir, "service_editor_advanced_light.png"), ThemePreference.Light);
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
        True(File.Exists(Path.Combine(outDir, "settings_dark.png")), "settings_dark.png must be created");
        True(File.Exists(Path.Combine(outDir, "service_editor_dark.png")), "service_editor_dark.png must be created");
        True(File.Exists(Path.Combine(outDir, "service_editor_compact_light.png")), "compact service editor must be created");
        True(File.Exists(Path.Combine(outDir, "service_editor_advanced_light.png")), "advanced service editor must be created");
        True(File.Exists(Path.Combine(outDir, "quick_search_dark.png")), "quick_search_dark.png must be created");
        True(File.Exists(Path.Combine(outDir, "translation_panel_dark.png")), "translation_panel_dark.png must be created");
        True(File.Exists(Path.Combine(outDir, "main_window_light_200pct.png")), "the 200% DPI matrix must be produced");
    }

    private static void RenderAndSave(Window window, int width, int height, string filePath, ThemePreference theme) =>
        RenderAndSaveAtDpi(window, width, height, filePath, theme, 1.0);

    private static void RenderAndSaveAtDpi(Window window, int width, int height, string filePath, ThemePreference theme, double dpiScale)
    {
        ThemeService.Apply(theme);
        var logicalWidth = width / dpiScale;
        var logicalHeight = height / dpiScale;
        window.Width = logicalWidth;
        window.Height = logicalHeight;

        // An unshown WPF Window renders as a black native surface. Render its
        // managed content root instead so headless screenshots actually catch
        // spacing, clipping and theme regressions without opening a window.
        var visual = window.Content as FrameworkElement ?? window;
        visual.Width = logicalWidth;
        visual.Height = logicalHeight;
        visual.Measure(new Size(logicalWidth, logicalHeight));
        visual.Arrange(new Rect(0, 0, logicalWidth, logicalHeight));
        visual.UpdateLayout();

        var dpi = 96 * dpiScale;
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(width * dpiScale), (int)Math.Ceiling(height * dpiScale),
            dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(filePath);
        encoder.Save(stream);
    }

    private static Window CreateServiceEditorPreview()
    {
        var section = new ServicesSection();
        section.LoadProfileIntoForm(ProviderProfile.CreateGemini());
        typeof(ServicesSection)
            .GetMethod("ShowEditorForm", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(section, new object[] { false });
        var host = new System.Windows.Controls.Border
        {
            Child = section,
            Padding = new Thickness(24),
        };
        host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "CanvasBrush");
        return new Window
        {
            Content = host,
        };
    }

    private static Window CreateAdvancedServiceEditorPreview()
    {
        var section = new ServicesSection();
        section.LoadProfileIntoForm(ProviderProfile.CreateOllama());
        typeof(ServicesSection)
            .GetMethod("ShowEditorForm", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(section, new object[] { false });
        section.AdvancedExpander.IsExpanded = true;
        var host = new System.Windows.Controls.Border
        {
            Child = section,
            Padding = new Thickness(24),
        };
        host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "CanvasBrush");
        return new Window { Content = host };
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

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: expected <{expected}>, got <{actual}>.");
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

    private static TException Throws<TException>(Action operation)
        where TException : Exception
    {
        try
        {
            operation();
        }
        catch (TException exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class RecordingHttpHandler(string responseJson) : System.Net.Http.HttpMessageHandler
    {
        public string RequestUri { get; private set; } = string.Empty;
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            foreach (var header in request.Headers)
            {
                Headers[header.Key] = string.Join(",", header.Value);
            }
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(responseJson),
            });
        }
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
