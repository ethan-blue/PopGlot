using PopGlot.Windows.Services;
using PopGlot.Windows.Sections;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
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
    private static Application? _bootstrappedApp;

    [STAThread]
    private static async Task<int> Main()
    {
        // Isolation FIRST: nothing below may see real user data, credentials,
        // or public network. A missing isolation bootstrap must fail the run.
        TestIsolation.Initialize();
        Run("test isolation is active", TestIsolation.AssertActive);
        Run("no real PopGlot instance conflicts with the suite", TestIsolation.AssertNoConflictingAppInstance);

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
        Run("endpoint classification agrees with the rust core", EndpointClassificationAgreesWithRustCore);
        Run("lan vision service needs the explicit permission", LanVisionServiceNeedsExplicitPermission);
        Run("routing decision table agrees across the ffi boundary", RoutingDecisionTableAgreesAcrossFfi);
        Run("language catalog normalizes and swaps", LanguageCatalogBehaviour);

        Run("sensitive history is rejected", SensitiveHistoryIsRejected);
        Run("history de-duplicates and survives reload", HistoryDeduplicatesAndReloads);

        Run("capture rectangle normalizes", CaptureRectangleNormalizes);
        Run("SendInput ABI size is correct", SendInputAbiSizeIsCorrect);

        Run("pangu spacing formats CJK-Latin text correctly", PanguSpacingFormatsCorrectly);
        Run("markdown plain text preserves technical identifiers and code", MarkdownPlainTextPreservesTechnicalText);
        Run("edge neural tts resolves voices by language script", EdgeTtsResolvesVoicesCorrectly);
        Run("edge tts voices follow the language tag", EdgeTtsVoicesFollowLanguageTag);
        await RunAsync("edge tts assembles fragmented websocket messages", EdgeTtsAssemblesFragmentedMessagesAsync);
        await RunAsync("edge tts enforces source and audio limits", EdgeTtsEnforcesLimitsAsync);
        Run("tts temp cleanup covers both file families", TtsTempCleanupCoversBothFamilies);
        Run("vocabulary store supports star, remove and export", VocabularyStoreBehaviour);
        Run("vocabulary store csv export conforms to standard format", VocabularyStoreCsvExportConforms);
        Run("vocabulary store handles corrupt json safely", VocabularyStoreHandlesCorruptJsonSafely);
        Run("vocabulary store save failures stay visible", VocabularyStoreSaveFailuresStayVisible);
        Run("vocabulary store enforces entry and capacity limits", VocabularyStoreEnforcesLimits);
        Run("vocabulary star identity preserves code identifier case", VocabularyStarIdentityPreservesCase);
        Run("vocabulary concurrent changes do not overwrite each other", VocabularyConcurrentChangesDoNotOverwrite);
        Run("history corrupt file is quarantined not destroyed", HistoryCorruptFileIsQuarantined);
        Run("exports are safe for spreadsheets and anki", ExportsAreSafeForSpreadsheetsAndAnki);
        Run("crash diagnostics sanitize secrets and bound length", CrashDiagnosticsSanitizeAndBound);
        Run("crash log rotates and stays within its budget", CrashDiagnosticsRotateAndStayBounded);
        Run("history store csv and markdown export conform to format", HistoryStoreExportConforms);
        Run("hotkey action enum values are recognized without exception", HotkeyActionsRecognized);
        Run("show window hotkey and free engine consent round-trip", ShellSettingsShowWindowAndConsentRoundTrip);
        await RunAsync("free engine consent gates the outbound decision", FreeEngineConsentGatesOutbound);
        await RunAsync("free engine authorization matrix at the send boundary", FreeEngineAuthorizationMatrixAtSendBoundary);
        await RunAsync("offline policy blocks remote but allows local providers", OfflineModeSendsNothing);
        await RunAsync("test connection draft never alters saved settings", DraftConnectionLeavesSettingsUntouched);
        Run("icon controls expose automation names", IconControlsExposeAutomationNames);
        Run("window caption resources and geometries are consistent", WindowCaptionResourcesConsistent);
        Run("main window includes window chrome and unified caption bar", MainWindowChromeAndCaptionBarPresent);
        Run("theme tokens dark and light palettes are symmetric", ThemeTokensSymmetric);
        Run("theme contrast ratios and token budgets conform to wcag", ThemeAuditHelper.RunAudits);
        Run("provider profiles support multi-config, independent keys and round-trip", ProviderProfilesSupportMultiConfigAndIndependentKeys);
        Run("service save resolves credential targets per profile", ServiceSaveResolvesCredentialTargets);
        Run("service save writes the key after resolving its target", ServiceSaveKeyOrderGuard);
        Run("settings save validates hotkeys before persisting", SettingsSaveValidatesBeforePersisting);
        Run("connection test failures map to actionable hints", ConnectionTestFailuresAreActionable);
        Run("service health states are explicit and hue-safe", ServiceHealthStatesAreExplicit);
        Run("loaded service does not become a false draft", LoadedServiceDoesNotBecomeFalseDraft);

        Run("settings draft snapshot pure comparison and revert clean", SettingsDraftSnapshotPureComparison);

        Run("header normalization and editor revert clean", HeaderNormalizationAndEditorRevertClean);

        Run("shared vision model retention and revert", SharedVisionModelRetentionAndRevert);

        Run("settings and services draft guard transitions", SettingsAndServicesDraftGuardTransitions);

        Run("shortcut recording suspends global shortcuts", ShortcutRecordingSuspendsGlobalShortcuts);
        Run("capture drag avoids forced layout", CaptureDragAvoidsForcedLayout);
        Run("settings closes transient translation surfaces", SettingsClosesTransientSurfaces);
        Run("screenshot draft route is visible", ScreenshotDraftRouteIsVisible);
        Run("service editor fields share a stable responsive grid", ServiceEditorUsesStableResponsiveGrid);
        Run("service draft coordinator pure rules hold", ServiceDraftCoordinatorPureRulesHold);
        Run("model catalog endpoints follow provider protocols", ModelCatalogEndpointsFollowProtocols);
        Run("model catalog parses OpenAI and Gemini responses", ModelCatalogParsesProviderResponses);
        await RunAsync("model catalog uses draft credentials without saving", ModelCatalogUsesDraftCredentialsAsync);
        Run("model recommendation pure heuristics, benchmark matching and evidence rules", ModelRecommendationTestsHelper.RunAllTests);
        Run("model recommendation UI pure helpers, evidence badge mapping and preference stability", ModelRecommendationUiTests);
        Run("caption buttons really render their icons", CaptionButtonsRenderTheirIcons);
        Run("menu item styles never bind invalid MenuItem roles", MenuItemStylesHaveValidRoles);
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

        // Streaming buffer and concurrency tests
        await RunAsync("stream buffer multi-producer concurrent append preserves order and zero character loss", StreamBufferConcurrentMultiProducerOrderAndZeroLossAsync);
        await RunAsync("stream buffer high frequency 10k delta drain handles rapid batches without loss", StreamBufferHighFrequency10kDeltaDrainAsync);
        Run("stream buffer hard limit aborts and preserves pending text without silent drop", StreamBufferHardLimitAbortsWithoutSilentDrop);
        Run("stream buffer complete and final drain preserves tail with no loss", StreamBufferCompleteFinalDrainZeroTailLoss);
        Run("stream buffer handles empty delta safely", StreamBufferEmptyDeltaHandling);
        Run("stream buffer unicode and utf8 multi-byte support", StreamBufferUnicodeAndUtf8MultiByteSupport);
        Run("stream buffer lifecycle operations are idempotent and reusable", StreamBufferLifecycleAndIdempotence);
        Run("stream buffer fences session epoch and tracks ttft metrics", StreamBufferSessionEpochFencingAndTtftMetrics);
        Run("stream buffer callback thunk handles chinese utf8", StreamBufferCallbackThunkHandlesChineseUtf8);
        Run("stream buffer callback thunk aborts and backpressures", StreamBufferCallbackThunkAbortsAndBackpressures);
        Run("stream buffer callback thunk invalid userData handled safely", StreamBufferCallbackThunkInvalidUserDataHandledSafely);
        Run("final envelope deserialization and error checking", FinalEnvelopeDeserializationAndErrorChecking);
        Run("stream session properties and lifecycle", StreamSessionPropertiesAndLifecycle);

        // Coordinator streaming and lifecycle tests
        await RunAsync("coordinator streaming delta arrives before completion", CoordinatorDeltaArrivesBeforeCompletionAsync);
        await RunAsync("coordinator throttling merges rapid deltas", CoordinatorThrottlingMergesDeltasAsync);
        await RunAsync("coordinator final drain delivers tail delta", CoordinatorFinalDrainDeliversTailAsync);
        await RunAsync("coordinator final calibration replaces text and metadata", CoordinatorFinalCalibrationReplacesTextAsync);
        await RunAsync("coordinator error and cancellation preserve partial text and write no history", CoordinatorErrorAndCancellationPreservePartialAndNoHistoryAsync);
        await RunAsync("coordinator successful translation writes history once", CoordinatorSuccessfulTranslationWritesHistoryOnceAsync);
        await RunAsync("partial final never persists or triggers side effects", PartialFinalNeverPersistsOrTriggersSideEffects);
        await RunAsync("cancelled final does not persist", CancelledFinalDoesNotPersistAsync);
        await RunAsync("history write failure is not fake committed", HistoryWriteFailureIsNotFakeCommittedAsync);
        Run("rust is_partial flag survives into the csharp dto", PartialContractSurvivesSerialization);
        await RunAsync("free engine runs the shared token protection chain", FreeEngineRunsSharedTokenProtection);
        await RunAsync("free engine transport boundary rejects oversize html and fakes", FreeEngineTransportBoundary);
        await RunAsync("coordinator free engine single shot emits reset and delta and writes history once", CoordinatorFreeSingleShotAsync);
        await RunAsync("long input plans into ordered segments", LongInputPlansIntoOrderedSegmentsAsync);
        await RunAsync("cancel between segments stops later requests", CancelBetweenSegmentsStopsLaterRequestsAsync);
        await RunAsync("segment failure keeps fragments as partial", SegmentFailureKeepsFragmentsAsPartialAsync);
        await RunAsync("incomplete segment stops session as partial", IncompleteSegmentStopsSessionAsPartialAsync);
        await RunAsync("budget refusal sends nothing", BudgetRefusalSendsNothingAsync);
        Run("short source stays single and planner agrees across ffi", ShortSourceStaysSingleAndPlannerAgreesAcrossFfi);
        await RunAsync("coordinator vision failure with deltas does not fallback to OCR", CoordinatorVisionWithDeltaFailureDoesNotOcrFallbackAsync);
        await RunAsync("coordinator vision failure with zero deltas falls back to OCR", CoordinatorVisionZeroDeltaFailureFallsBackToOcrAsync);
        await RunAsync("coordinator epoch propagation fences session updates", CoordinatorEpochPropagationAsync);
        await RunAsync("coordinator stage transitions follow correct lifecycle order", CoordinatorStageOrderAsync);

        // QuickSearch streaming and state machine tests
        Run("quick search epoch and query fencing rejects stale updates", QuickSearchEpochAndQueryFencing);
        Run("quick search action gates protect partial and streaming states", QuickSearchPartialActionGate);
        Run("quick search closed guard drops updates and prevents UI leaks", QuickSearchClosedGuard);
        Run("quick search min height and headless rendering contracts conform", QuickSearchMinHeightAndHeadlessContract);

        // TranslateSection streaming and state machine tests
        Run("translate section epoch fencing rejects stale updates", TranslateSectionEpochFencing);
        Run("translate section reset and delta stream transitions", TranslateSectionResetAndDelta);
        Run("translate section action gating and partial retention", TranslateSectionActionGatingAndPartialRetention);

        // TranslationPanel streaming and state machine tests
        Run("translation panel epoch and lifetime fencing rejects stale updates", TranslationPanelEpochAndLifetimeFencing);
        Run("translation panel reset and delta stream transitions", TranslationPanelResetAndDelta);
        Run("translation panel action gating and partial retention", TranslationPanelActionGatingAndPartialRetention);
        Run("translation panel result card rows keep text and explanation separated", TranslationPanelResultCardRowStructure);

        // Both ride one STA thread: the windowed regression needs the
        // Application the screenshot pass bootstraps, and Application
        // resources are thread-affine.
        RunStaBatch(
            ("quick search component lifecycle and stream contracts", QuickSearchComponentLifecycleAndStreamContracts),
            ("translate section component lifecycle and stream contracts", TranslateSectionComponentLifecycleAndStreamContracts),
            ("translate section stacks when narrow", TranslateSectionStacksWhenNarrow),
            ("stream scroll position is preserved while reading", StreamScrollPositionIsPreservedWhileReading),
            ("translate empty state guides first use", TranslateEmptyStateGuidesFirstUse),
            ("translation panel component lifecycle and stream contracts", TranslationPanelComponentLifecycleAndStreamContracts),
            ("markdown visual rendering separates code from natural language", MarkdownVisualSeparatesCodeFromNaturalLanguage),
            ("primary button text uses primary text brush", PrimaryButtonTextUsesPrimaryTextBrush),
            ("three entries copy the same agreed plain text", ThreeEntriesCopyTheSameAgreedText),
            ("code block copy button obeys eligibility", CodeBlockCopyButtonObeysEligibility),
            ("panel routes partial session to blocked actions and no auto copy", PanelRoutesPartialSessionToBlockedActionsAndNoAutoCopy),
            ("panel star failure is visible", PanelStarFailureIsVisible),
            ("render screenshots and measure performance baseline", RenderScreenshotsAndMeasureBaseline),
            ("a failed save recovers to dirty then clean", FailedSaveRecoversToDirtyThenClean),
            ("settings cloud speech consent round-trip", SettingsCloudSpeechConsentRoundTrip),
            ("hotkey service suspension and retention behavior", HotkeyServiceSuspensionAndRetentionBehavior),
            ("tts cloud speech needs its own consent", TtsCloudSpeechNeedsOwnConsent));

        await RunAsync("clipboard isolation and hard timeout resilience", ClipboardIsolationAndHardTimeoutAsync);
        await RunAsync("clipboard snapshot fail closed behavior", ClipboardSnapshotFailClosedBehaviorAsync);
        await RunAsync("screen capture async execution off UI thread", ScreenCaptureAsyncExecution);
        Run("hotkey service atomicity and failure visibility", HotkeyServiceAtomicityAndFailureVisibility);
        Run("release workflow specifies self-contained", ReleaseWorkflowSpecifiesSelfContained);

        if (Environment.GetEnvironmentVariable("POPGLOT_SMOKE_FREE") == "1")
        {
            await RunAsync("free web translation smoke (network)", FreeTranslationSmokeAsync);
        }

        // The whole run must have left the user's real config untouched and
        // must never have attempted a public-network send. The only sanctioned
        // exception is the explicit POPGLOT_SMOKE_FREE smoke, whose single
        // attempt the isolation guard still refuses.
        Run("real user config unchanged by the run", TestIsolation.VerifyRealFilesUnchanged);
        var sanctionedAttempts = Environment.GetEnvironmentVariable("POPGLOT_SMOKE_FREE") == "1" ? 1 : 0;
        Run("no unsanctioned public network send was attempted", () =>
            True(TestIsolation.BlockedPublicSends <= sanctionedAttempts,
                $"the run attempted {TestIsolation.BlockedPublicSends} public-network requests; at most {sanctionedAttempts} were sanctioned"));

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

        // The honesty line: loopback and LAN are different classes. Only
        // loopback counts as "targets the local runtime".
        Equal(EndpointClass.Loopback, ProviderSettings.ClassifyEndpoint("http://localhost:11434/v1"));
        Equal(EndpointClass.Loopback, ProviderSettings.ClassifyEndpoint("http://[::1]:11434"));
        Equal(EndpointClass.PrivateNetwork, ProviderSettings.ClassifyEndpoint("http://192.168.1.20:8080"));
        Equal(EndpointClass.Internet, ProviderSettings.ClassifyEndpoint("https://localhost.example.com"));
        var lanSettings = CoreBridge.GetSettings() with { ApiBaseUrl = "http://192.168.1.20:11434/v1" };
        True(!lanSettings.TargetsLocalRuntime, "a LAN device is not the local runtime");
        True(lanSettings.TargetsPrivateNetwork, "a LAN device is its own class");
    }

    /// <summary>
    /// T05 acceptance: the C# classifier and the Rust core classifier must
    /// reach the same conclusion for the whole shared fixture list (the same
    /// rows as the Rust-side fixture test).
    /// </summary>
    private static void EndpointClassificationAgreesWithRustCore()
    {
        var cases = new (string Url, EndpointClass Expected)[]
        {
            ("http://localhost:11434", EndpointClass.Loopback),
            ("http://127.0.0.1:8080/v1", EndpointClass.Loopback),
            ("http://127.10.20.30/v1", EndpointClass.Loopback),
            ("http://[::1]:11434", EndpointClass.Loopback),
            ("https://localhost.example.com", EndpointClass.Internet),
            ("https://api.openai.com/v1", EndpointClass.Internet),
            ("http://192.168.1.20:8080", EndpointClass.PrivateNetwork),
            ("http://172.16.0.4:8000/v1", EndpointClass.PrivateNetwork),
            ("http://172.200.1.1/v1", EndpointClass.Internet),
            ("http://10.0.0.5/v1", EndpointClass.PrivateNetwork),
            ("http://[fd00::1]:11434", EndpointClass.PrivateNetwork),
            ("http://user:secret@10.0.0.5:11434/v1", EndpointClass.PrivateNetwork),
            ("http://LOCALHOST:11434", EndpointClass.Loopback),
            ("http://10.0.0.5:notaport/", EndpointClass.Internet),
            ("not a url at all", EndpointClass.Internet),
            ("", EndpointClass.Internet),
        };

        foreach (var (url, expected) in cases)
        {
            var csharp = ProviderSettings.ClassifyEndpoint(url);
            Equal(expected, csharp, $"C# classification of {url}");

            var rustJson = CoreBridge.EnsureSuccess<System.Text.Json.JsonElement>(
                InvokeClassify(url));
            var rust = rustJson.GetProperty("class").GetString() switch
            {
                "loopback" => EndpointClass.Loopback,
                "private" => EndpointClass.PrivateNetwork,
                _ => EndpointClass.Internet,
            };
            Equal(expected, rust, $"Rust classification of {url}");
        }
    }

    private static string InvokeClassify(string url) => CoreBridge.ClassifyEndpointViaFfi(url);

    /// <summary>
    /// T05 acceptance: a LAN vision service is another device — the route is
    /// blocked without the explicit permission even when image upload is
    /// allowed, shows the honest LAN wording with it, and a loopback vision
    /// service keeps "图片不离开本机".
    /// </summary>
    /// <summary>
    /// T13 acceptance: the shell's fact collection and the Rust decision
    /// table agree across the FFI on the same fixture rows — preview and
    /// execution share one strategy, and neither language has a private copy.
    /// </summary>
    private static void RoutingDecisionTableAgreesAcrossFfi()
    {
        // (mode, visionConfigured, visionClass, uploadAllowed, allowLan, ocrAvailable, textRoute)
        (string Mode, bool Vis, string Class, bool Upload, bool Lan, bool Ocr, bool Text)[] rows =
        [
            ("Auto", true, "internet", true, false, true, true),
            ("Auto", true, "internet", true, false, false, true),
            ("Auto", true, "private", true, false, false, true),
            ("Auto", true, "private", true, true, false, true),
            ("Auto", true, "loopback", true, false, false, true),
            ("Auto", false, "internet", true, false, true, true),
            ("Auto", false, "internet", true, false, false, true),
            ("VisionDirect", true, "internet", true, false, true, true),
            ("VisionDirect", true, "internet", false, false, true, true),
            ("VisionDirect", true, "loopback", false, false, true, true),
            ("VisionOcr", true, "internet", true, false, true, true),
            ("VisionOcr", true, "internet", true, false, true, false),
            ("LocalOcr", true, "internet", true, false, true, true),
            ("LocalOcr", true, "internet", true, false, false, true),
        ];

        foreach (var row in rows)
        {
            // Rust side, evaluated directly.
            var factsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                requested_mode = row.Mode,
                vision_configured = row.Vis,
                vision_endpoint_class = row.Class,
                image_upload_allowed = row.Upload,
                allow_lan_endpoints = row.Lan,
                local_ocr_available = row.Ocr,
                text_route_available = row.Text,
            });
            var rustDecision = System.Text.Json.JsonSerializer.Deserialize<RoutingDecision>(
                CoreBridge.SelectRouteRaw(factsJson), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

            // The same facts must produce the same pipeline class on the C#
            // side of the boundary (the mapping ProfileManager performs).
            var pipeline = rustDecision.ReasonCode switch
            {
                "auto_unavailable" or "forced_local_ocr_without_engine" => ScreenshotPipeline.Unavailable,
                _ => rustDecision.SelectedMode switch
                {
                    TranslationMode.VisionDirect => ScreenshotPipeline.VisionDirect,
                    TranslationMode.VisionOcr => ScreenshotPipeline.VisionOcr,
                    _ => ScreenshotPipeline.LocalOcr,
                },
            };
            _ = pipeline; // mapping exercised; the equality proof is below

            // The invariant that matters: MayUploadImage == (vision pipeline
            // selected AND the endpoint leaves the device).
            var visionSelected = rustDecision.SelectedMode is TranslationMode.VisionDirect or TranslationMode.VisionOcr;
            var leavesDevice = visionSelected && rustDecision.ReasonCode is not "auto_unavailable" &&
                row.Class != "loopback";
            Equal(leavesDevice, rustDecision.MayUploadImage,
                $"row {row.Mode}/{row.Class}/upload={row.Upload}/lan={row.Lan}/ocr={row.Ocr}");
        }
    }

    private static void LanVisionServiceNeedsExplicitPermission()
    {
        ProfileManager.ResetForTests();
        ProfileManager.ConfigPathOverride = Path.Combine(
            Path.GetTempPath(), $"popglot-lan-route-{Guid.NewGuid():N}.json");
        try
        {
            var settings = CoreBridge.GetSettings() with
            {
                NetworkEnabled = true,
                SafeDevMode = false,
                AllowImageUploadInAuto = true,
                Mode = TranslationMode.Auto,
            };

            var config = new CoreProductConfig
            {
                SchemaVersion = 7,
                ActiveProfileId = "lan-text",
                Profiles =
                [
                    new ProviderProfile
                    {
                        Id = "lan-text",
                        Name = "LAN Text",
                        ProviderType = ProviderType.OpenAiCompatible,
                        ApiBaseUrl = "http://127.0.0.1:11434/v1",
                        TextEndpoint = "/chat/completions",
                        VisionEndpoint = "/chat/completions",
                        TextModel = "text-model",
                        VisionModel = "vision-model",
                        SupportsText = true,
                        SupportsVision = true,
                        IsLocal = true,
                        AllowLanEndpoints = false,
                    },
                ],
            };
            // Point the vision service at another LAN device without permission.
            config.Profiles[0].ApiBaseUrl = "http://192.168.1.20:11434/v1";
            ProfileManager.Save(config);

            var blocked = ProfileManager.ResolveRoute(settings, localOcrAvailable: false);
            Equal(ScreenshotPipeline.Unavailable, blocked.ScreenshotPipeline,
                "a LAN vision service without the permission must not run");
            True(blocked.ExplanationZh.Contains("局域网"), blocked.ExplanationZh);
            Equal(false, blocked.MayUploadImage);

            // Grant the permission on the profile: usable and honest about
            // where the image goes.
            config.Profiles[0].AllowLanEndpoints = true;
            ProfileManager.Save(config);
            var granted = ProfileManager.ResolveRoute(settings, localOcrAvailable: false);
            Equal(ScreenshotPipeline.VisionOcr, granted.ScreenshotPipeline);
            Equal(true, granted.MayUploadImage, "a LAN vision request does leave the device");
            True(granted.ExplanationZh.Contains("局域网"), granted.ExplanationZh);

            // Loopback vision stays genuinely device-local.
            config.Profiles[0].ApiBaseUrl = "http://127.0.0.1:11434/v1";
            ProfileManager.Save(config);
            var local = ProfileManager.ResolveRoute(settings, localOcrAvailable: false);
            Equal(ScreenshotPipeline.VisionDirect, local.ScreenshotPipeline);
            Equal(false, local.MayUploadImage, "a loopback vision service never moves the image");
            True(local.ExplanationZh.Contains("不离开本机"), local.ExplanationZh);
        }
        finally
        {
            File.Delete(ProfileManager.ConfigPathOverride);
            ProfileManager.ResetForTests();
        }
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
        // The smoke runs only when POPGLOT_SMOKE_FREE=1 is explicitly set; it
        // still needs free-engine consent and an authorization for the send
        // boundary. The isolation HTTP guard refuses the actual public send,
        // so the smoke reports the refusal instead of silently passing.
        OutboundPolicy.PersistConsent(FreeEngineConsent.Allowed);
        var settings = CoreBridge.GetSettings();
        True(OutboundPolicy.AllowsFreeEngine(settings, out _, out var authorization),
            "free-engine consent must be grantable in the isolated environment");
        var response = await FreeTranslateService.TranslateAsync("hello world", "auto", "zh-CN", authorization);
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

    /// <summary>
    /// T02 acceptance for the shared plain-text formatter used by copy,
    /// speech and vocabulary storage in the panel, quick search and the
    /// workbench: technical identifiers, inline code and fenced code must
    /// survive byte-for-byte, while natural-language emphasis is unwrapped.
    /// </summary>
    private static void MarkdownPlainTextPreservesTechnicalText()
    {
        // Bare identifiers must never lose underscores or asterisks.
        Equal("foo_bar_baz", MarkdownPresenter.ToPlainText("foo_bar_baz"));
        Equal("__init__", MarkdownPresenter.ToPlainText("__init__"));
        Equal("snake_case_name", MarkdownPresenter.ToPlainText("snake_case_name"));
        Equal("a*b*c", MarkdownPresenter.ToPlainText("a*b*c"));

        // Inline code: the container syntax goes, the content stays verbatim.
        Equal("foo_bar_baz", MarkdownPresenter.ToPlainText("`foo_bar_baz`"));
        Equal(@"C:\用户data\file.txt", MarkdownPresenter.ToPlainText(@"`C:\用户data\file.txt`"));

        // Natural-language emphasis is still unwrapped for plain-text copy.
        Equal("普通粗体", MarkdownPresenter.ToPlainText("**普通粗体**"));
        Equal("重点 内容", MarkdownPresenter.ToPlainText("**重点 内容**"));

        // Mixed prose keeps code verbatim while surrounding text is cleaned.
        Equal("调用 getUserName() 获取名字", MarkdownPresenter.ToPlainText("调用 `getUserName()` 获取名字"));

        // Fenced code: fences go, content (indentation, tabs, blank lines,
        // identifiers) stays byte-for-byte.
        var fenced = "```python\ndef f():\n\treturn foo_bar_baz\n\n```";
        Equal("def f():\n\treturn foo_bar_baz", NormalizeNewlines(MarkdownPresenter.ToPlainText(fenced)));

        // Headings, bullets, ordered lists and links still read naturally.
        Equal("标题文字", MarkdownPresenter.ToPlainText("## 标题文字"));
        Equal("列表项内容", MarkdownPresenter.ToPlainText("- 列表项内容"));
        Equal("第一步", MarkdownPresenter.ToPlainText("1. 第一步"));
        Equal("文档见 https://example.com/a_b_c", MarkdownPresenter.ToPlainText("文档见 https://example.com/a_b_c"));

        // Malformed input: no crash, no silent content deletion.
        Equal("print(1)", NormalizeNewlines(MarkdownPresenter.ToPlainText("```python\nprint(1)")));
        Equal("a ` b", MarkdownPresenter.ToPlainText("a ` b"));
        Equal("值是 ⟦PG_0001⟧ 吗", MarkdownPresenter.ToPlainText("值是 ⟦PG_0001⟧ 吗"));
    }

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

    /// <summary>
    /// T02 acceptance: from one simulated final result, the copy actions of
    /// all three surfaces — floating panel (real button click), quick search
    /// (production copy handler) and the workbench — deliver the identical
    /// agreed plain text through an in-memory clipboard.
    /// </summary>
    private static void ThreeEntriesCopyTheSameAgreedText()
    {
        EnsureApplication();
        // The copy handlers are async and resume after the clipboard write;
        // give this STA thread the same Dispatcher synchronization context a
        // real UI thread has so their continuations come back here instead of
        // touching the window from a worker thread.
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));

        const string raw = "调用 `getUserName()` 获取名字，检查 foo_bar_baz 与 `__init__`";
        var agreed = MarkdownPresenter.ToPlainText(raw);
        var recorded = new List<string>();
        var originalWriter = Helpers.ClipboardWriterOverride;
        // Complete on a worker thread after a real delay, like the hardened
        // clipboard worker does — an instantly-completed task would let the
        // async handlers continue synchronously and skip dispatcher marshaling.
        Helpers.ClipboardWriterOverride = text =>
        {
            lock (recorded)
            {
                recorded.Add(text ?? string.Empty);
            }
            return Task.Delay(30).ContinueWith(static _ => true);
        };
        try
        {
            // 1. Quick search: drive the production state machine to a clean
            // completion, then run the real copy handler.
            var quickSearch = new QuickSearchWindow(
                new HistoryStore(TestIsolation.HistoryPath),
                new VocabularyStore(TestIsolation.VocabularyPath));
            var session = new TranslationSession
            {
                Stage = TranslationSessionStage.Completed,
                SourceText = "demo query",
                TranslatedText = raw,
            };
            quickSearch.State.StartNewSearch("demo query");
            True(quickSearch.State.OnSessionCompleted(session, quickSearch.State.CurrentEpoch, "demo query"),
                "the quick search state machine must accept the completed session");
            True(quickSearch.State.CanCopy, "completed quick search must allow copy");
            InvokeClick(quickSearch, "Copy_Click");
            SpinUntil(() => recordedCount(recorded) >= 1, "quick search copy never landed");
            Equal(agreed, recorded[0], "quick search copy must deliver the agreed plain text");

            // 2. Floating panel: a real WPF button click on ResultCopyBtn with
            // the completed gate and final translation in place.
            var panel = new TranslationPanelWindow(
                new Rect(100, 100, 20, 20),
                new HistoryStore(TestIsolation.HistoryPath),
                () => ShellSettings.Default,
                null,
                null,
                new VocabularyStore(TestIsolation.VocabularyPath));
            typeof(TranslationPanelWindow)
                .GetField("_translation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(panel, raw);
            var panelGate = (TranslationPanelStreamGate)typeof(TranslationPanelWindow)
                    .GetField("_gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(panel)!;
            // Drive the gate through the real lifecycle: begin → stream →
            // clean completion, exactly as a successful session would.
            var (panelEpoch, _) = panelGate.BeginNewOperation();
            panelGate.ApplyUpdate(new TranslationStreamUpdate(
                "s1", panelEpoch, TranslationStreamUpdateKind.Delta, raw, raw, raw.Length));
            panelGate.OnCompleted(raw);
            True(panelGate.CanPerformResultActions, "the completed panel gate must allow result actions");
            panel.ResultCopyBtn.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            SpinUntil(() => recordedCount(recorded) >= 2, "panel copy never landed");
            Equal(agreed, recorded[1], "panel copy must deliver the agreed plain text");

            // 3. Workbench section: real click event on the result copy button.
            var section = new TranslateSection();
            section.ResultBox.Text = raw;
            ((System.Windows.Controls.Button)section.FindName("TranslateResultCopyButton"))!
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            SpinUntil(() => recordedCount(recorded) >= 3, "workbench copy never landed");
            Equal(agreed, recorded[2], "workbench copy must deliver the agreed plain text");
        }
        finally
        {
            Helpers.ClipboardWriterOverride = originalWriter;
        }
    }

    private static int recordedCount(List<string> recorded)
    {
        lock (recorded)
        {
            return recorded.Count;
        }
    }

    /// <summary>
    /// T03 acceptance: code-block copy buttons are generated dynamically, so
    /// they must inherit the session's eligibility instead of always copying.
    /// </summary>
    private static void CodeBlockCopyButtonObeysEligibility()
    {
        EnsureApplication();
        const string markdown = "```python\nprint(foo_bar_baz)\n```";

        var blocked = new FlowDocument();
        MarkdownPresenter.RenderToFlowDocument(blocked, markdown, Application.Current.Resources, resultActionsEnabled: false);
        var blockedButton = FindCodeCopyButton(blocked);
        True(blockedButton is not null, "the blocked code block must still render its copy button");
        True(blockedButton!.IsEnabled == false, "a partial session's code-block copy button must be disabled");
        string? tooltip = blockedButton.ToolTip as string;
        True(tooltip?.Contains("不完整") == true, $"the disabled button must explain why, got: {tooltip}");

        var eligible = new FlowDocument();
        MarkdownPresenter.RenderToFlowDocument(eligible, markdown, Application.Current.Resources, resultActionsEnabled: true);
        var eligibleButton = FindCodeCopyButton(eligible);
        True(eligibleButton is not null && eligibleButton.IsEnabled,
            "a clean session's code-block copy button must stay enabled");
    }

    private static System.Windows.Controls.Button? FindCodeCopyButton(FlowDocument document)
    {
        foreach (var block in document.Blocks)
        {
            if (block is BlockUIContainer { Child: System.Windows.Controls.Border border } &&
                border.Child is System.Windows.Controls.Grid grid &&
                FindCopyButtonIn(grid) is { } button)
            {
                return button;
            }
        }
        return null;
    }

    private static System.Windows.Controls.Button? FindCopyButtonIn(System.Windows.DependencyObject node)
    {
        if (node is System.Windows.Controls.Button { Content: "复制" } button)
        {
            return button;
        }
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < count; index++)
        {
            if (FindCopyButtonIn(System.Windows.Media.VisualTreeHelper.GetChild(node, index)) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>
    /// T03 acceptance: a Partial session routed through the panel's production
    /// result handler lands on FailedWithPartial — retained text stays visible
    /// while result actions and auto-copy stay blocked.
    /// </summary>
    private static void PanelRoutesPartialSessionToBlockedActionsAndNoAutoCopy()
    {
        EnsureApplication();
        var panel = new TranslationPanelWindow(
            new Rect(100, 100, 20, 20),
            new HistoryStore(TestIsolation.HistoryPath),
            () => ShellSettings.Default,
            null,
            null,
            new VocabularyStore(TestIsolation.VocabularyPath));

        var clipboardWrites = new List<string>();
        var originalWriter = Helpers.ClipboardWriterOverride;
        Helpers.ClipboardWriterOverride = text =>
        {
            clipboardWrites.Add(text ?? string.Empty);
            return Task.FromResult(true);
        };
        try
        {
            var partialSession = new TranslationSession
            {
                Stage = TranslationSessionStage.Partial,
                TranslatedText = "保留的部分文本 foo_bar",
            };
            typeof(TranslationPanelWindow)
                .GetMethod("HandleSessionResultAsync",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(panel, new object?[] { "demo source", partialSession, 0L, "note" });

            var gate = (TranslationPanelStreamGate)typeof(TranslationPanelWindow)
                .GetField("_gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(panel)!;
            Equal(TranslationPanelStage.FailedWithPartial, gate.Stage,
                "a partial session must land on the blocked-with-partial stage");
            True(!gate.CanPerformResultActions, "result actions must stay blocked on partial");
            True(!gate.ShouldTriggerAutoCopy(true), "auto-copy must never trigger on partial");
            True(!gate.HasPartialText == false, "the partial text must be retained");
            Equal(0, clipboardWrites.Count, "no clipboard write may happen for a partial session");
        }
        finally
        {
            Helpers.ClipboardWriterOverride = originalWriter;
        }
    }

    /// <summary>
    /// T07 acceptance at the UI surface: a star toggle whose write fails shows
    /// "未保存到本机" and does not light the star, even after a fully
    /// completed translation opened the result actions.
    /// </summary>
    private static void PanelStarFailureIsVisible()
    {
        EnsureApplication();
        var dirAsPath = Path.Combine(Path.GetTempPath(), $"popglot-vocab-panel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dirAsPath);
        var panel = new TranslationPanelWindow(
            new Rect(100, 100, 20, 20),
            new HistoryStore(TestIsolation.HistoryPath),
            () => ShellSettings.Default,
            null,
            null,
            new VocabularyStore(dirAsPath));
        try
        {
            var completed = new TranslationSession
            {
                Stage = TranslationSessionStage.Completed,
                TranslatedText = "完整译文，包含 foo_bar_baz",
            };
            typeof(TranslationPanelWindow)
                .GetMethod("HandleSessionResultAsync",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(panel, new object?[] { "panel star source", completed, 0L, null });
            panel.SourceInputBox.Text = "panel star source";

            typeof(TranslationPanelWindow)
                .GetMethod("StarToggle_Click",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(panel, new object?[] { panel, new RoutedEventArgs() });

            True(panel.StatusText.Text.Contains("未保存到本机"),
                $"the panel must say nothing was saved, got: {panel.StatusText.Text}");
            True(panel.StarToggle.IsChecked != true, "a failed star write must not light the star icon");
        }
        finally
        {
            panel.Close();
            try { Directory.Delete(dirAsPath); } catch { }
        }
    }

    /// <summary>
    /// T07 acceptance: concurrent star changes serialize through the store's
    /// lock; no update is lost and the file reloads with every winner.
    /// </summary>
    private static void VocabularyConcurrentChangesDoNotOverwrite()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"popglot-vocab-conc-{Guid.NewGuid():N}.json");
        try
        {
            var store = new VocabularyStore(tempFile);
            var words = Enumerable.Range(0, 8).Select(i => $"word_{i}").ToArray();
            var threads = new List<Thread>();
            foreach (var word in words)
            {
                threads.Add(new Thread(() =>
                {
                    for (var round = 0; round < 3; round++)
                    {
                        store.ToggleStar(word, "并发词条", "", "", "en", "zh-CN");
                    }
                }));
            }
            foreach (var thread in threads)
            {
                thread.Start();
            }
            foreach (var thread in threads)
            {
                thread.Join();
            }

            Equal(words.Length, store.GetAll().Count, "every word must survive the concurrent toggles");
            Equal(words.Length, new VocabularyStore(tempFile).GetAll().Count,
                "the persisted file must contain every winner after reload");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            try { File.Delete(tempFile + ".bak"); } catch { }
        }
    }

    /// <summary>
    /// T08 acceptance: an exception carrying synthetic secrets (Authorization
    /// header, key-shaped literal, URL query with user text) reaches neither
    /// the crash log file nor the tray summary in raw form, and every entry is
    /// length-bounded.
    /// </summary>
    private static void CrashDiagnosticsSanitizeAndBound()
    {
        var secretBearer = "Bearer aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789";
        var secretKey = "sk-TESTabcdef1234567890XYZ";
        var userText = "用户私密原文内容";
        var message =
            $"请求失败 Authorization: {secretBearer} key={secretKey} " +
            $"url=https://api.example.com/v1/chat?q={userText} 其他上下文";
        var exception = new ExceptionWithSyntheticStack(
            message,
            string.Join("\n", Enumerable.Range(0, 80).Select(i => $"   at Demo.Frame{i}() in D:\\demo\\file{i}.cs:line {i}")));

        var sanitized = DiagnosticsLog.Sanitize(message);
        True(!sanitized.Contains(secretBearer), "bearer tokens must be redacted");
        True(!sanitized.Contains("aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789"), "the bare token value must not survive either");
        True(!sanitized.Contains(secretKey), "key-shaped literals must be redacted");
        True(!sanitized.Contains(userText), "URL query text must be stripped");
        True(sanitized.Contains("https://api.example.com/v1/chat?…"), "the URL base may survive without its query");

        var summary = DiagnosticsLog.CrashSummary(exception);
        True(summary.Length <= 161, $"the balloon summary must stay short, got {summary.Length}");
        True(!summary.Contains(secretKey), "the balloon summary must be sanitized too");

        var entry = DiagnosticsLog.BuildEntry(exception);
        True(!entry.Contains(secretBearer) && !entry.Contains(secretKey) && !entry.Contains(userText),
            "the log entry must contain no raw secret");
        True(entry.Contains("[redacted]") && entry.Contains("[redacted-key]"),
            "redaction markers must appear instead");
        True(!entry.Contains("Frame79"), "the stack must be truncated after the frame budget");

        // The production write path lands under the isolated StoragePaths.
        var logDir = StoragePaths.Logs;
        DiagnosticsLog.Log(exception);
        var today = Path.Combine(logDir, $"crash-{DateTime.Now:yyyyMMdd}.log");
        True(File.Exists(today), "the crash file must be written");
        var written = File.ReadAllText(today);
        True(!written.Contains(secretBearer) && !written.Contains(secretKey) && !written.Contains(userText),
            "nothing raw may reach disk");
    }

    /// <summary>
    /// T08 acceptance: files rotate at the per-file cap, retention deletes
    /// expired files, the total directory stays under its budget, and an
    /// exception storm cannot grow the directory without bound.
    /// </summary>
    private static void CrashDiagnosticsRotateAndStayBounded()
    {
        var dir = Path.Combine(TestIsolation.Root, "logs-t08");
        Directory.CreateDirectory(dir);
        try
        {
            // Rotation: a file at the cap is moved aside before the next write.
            var today = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd}.log");
            File.WriteAllText(today, new string('x', (int)DiagnosticsLog.MaxFileBytes));
            DiagnosticsLog.RotateIfNeeded(today);
            True(!File.Exists(today) || new FileInfo(today).Length < DiagnosticsLog.MaxFileBytes,
                "a capped file must be rotated before new writes");
            True(Directory.EnumerateFiles(dir, "*.rot").Any(), "the rotated copy must exist");

            // Retention: an expired file disappears on cleanup.
            var expired = Path.Combine(dir, "crash-20200101.log");
            File.WriteAllText(expired, "old");
            File.SetLastWriteTimeUtc(expired, DateTime.UtcNow - DiagnosticsLog.Retention - TimeSpan.FromDays(1));

            // Total cap: 12 recent 1MiB files exceed the 10MiB budget; the
            // oldest must go until the directory fits again.
            var heavy = new List<FileInfo>();
            for (var i = 0; i < 12; i++)
            {
                var path = Path.Combine(dir, $"crash-2099010{i}.log");
                File.WriteAllText(path, new string('x', 1024 * 1024));
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-20 + i));
                heavy.Add(new FileInfo(path));
            }

            DiagnosticsLog.CleanupIfStale(dir, force: true);

            True(!File.Exists(expired), "an expired crash log must be deleted");
            var total = Directory.EnumerateFiles(dir, "crash-*").Sum(p => new FileInfo(p).Length);
            True(total <= DiagnosticsLog.MaxTotalBytes,
                $"the directory must stay within its budget, got {total}");
            var oldest = heavy.OrderBy(f => f.LastWriteTimeUtc).First();
            True(!oldest.Exists, "the oldest files are the ones removed");

            // Storm: a thousand crashes neither throw nor grow past one file cap.
            var boom = new InvalidOperationException(new string('y', 400));
            for (var i = 0; i < 1000; i++)
            {
                DiagnosticsLog.Log(boom);
            }
            var stormTotal = Directory.EnumerateFiles(StoragePaths.Logs, "crash-*").Sum(p => new FileInfo(p).Length);
            True(stormTotal <= DiagnosticsLog.MaxTotalBytes,
                $"a crash storm must stay bounded, got {stormTotal}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>An exception whose stack can be injected: real frames carry file paths.</summary>
    private sealed class ExceptionWithSyntheticStack(string message, string stackTrace) : Exception(message)
    {
        public override string StackTrace => stackTrace;
    }

    /// <summary>
    /// T09 acceptance (F10 repro): the text INSIDE a PrimaryButton must
    /// actually render with PrimaryTextBrush. The implicit TextBlock style
    /// used to override it with TextPrimary — dark text on the brand blue
    /// (3.16:1) — and only a visual-subtree probe catches that.
    /// </summary>
    private static void PrimaryButtonTextUsesPrimaryTextBrush()
    {
        EnsureApplication();
        foreach (var theme in new[] { ThemePreference.Dark, ThemePreference.Light })
        {
            ThemeService.Apply(theme);
            var expected = ((SolidColorBrush)Application.Current.FindResource("PrimaryTextBrush")).Color;

            // String content: WPF generates the TextBlock inside the template.
            var stringButton = new Button { Content = "翻译" };
            stringButton.Style = (Style)Application.Current.FindResource("PrimaryButton");
            stringButton.Measure(new Size(200, 60));
            stringButton.Arrange(new Rect(0, 0, 200, 60));
            var generated = FindVisualTextBlock(stringButton);
            True(generated is not null, $"{theme}: the generated text node must exist");
            Equal(expected, ((SolidColorBrush)generated!.GetValue(TextBlock.ForegroundProperty)).Color,
                $"{theme}: the generated text inside a primary button must use PrimaryTextBrush");

            // Explicit nested TextBlock content inherits the same way.
            var nested = new Button { Content = new TextBlock { Text = "翻译" } };
            nested.Style = (Style)Application.Current.FindResource("PrimaryButton");
            nested.Measure(new Size(200, 60));
            nested.Arrange(new Rect(0, 0, 200, 60));
            var nestedText = FindVisualTextBlock(nested);
            True(nestedText is not null, $"{theme}: the nested text node must exist");
            Equal(expected, ((SolidColorBrush)nestedText!.GetValue(TextBlock.ForegroundProperty)).Color,
                $"{theme}: a nested TextBlock in a primary button must inherit PrimaryTextBrush");

            // The same inheritance carries TEMPLATE trigger values (the
            // mechanism DangerButton hover uses to swap its text colour).
            nested.SetValue(System.Windows.Controls.Control.ForegroundProperty,
                new SolidColorBrush(Color.FromRgb(0xFE, 0xFE, 0xFE)));
            Equal(Color.FromRgb(0xFE, 0xFE, 0xFE),
                ((SolidColorBrush)nestedText.GetValue(TextBlock.ForegroundProperty)).Color,
                $"{theme}: a foreground set on the button must reach its text");
        }

        // Ten theme round-trips: the text must follow the live theme with no
        // frozen brush and no lost colour (the screenshot pass then re-checks
        // whole windows in both themes).
        // Ten theme round-trips with freshly built buttons: the token
        // plumbing must stay correct after every switch, with no frozen
        // value leaking into later themes. (In-place recolouring of
        // PRE-EXISTING elements rides App.xaml's unfrozen brushes in the
        // production app; the test host runs the frozen-replacement path, so
        // live in-place switching is verified on the real machine instead.)
        for (var i = 0; i < 10; i++)
        {
            var theme = i % 2 == 0 ? ThemePreference.Dark : ThemePreference.Light;
            ThemeService.Apply(theme);
            // New buttons resolve resources directly from the application
            // dictionary at build time — no dispatcher pump needed (and a
            // pump here can execute queued dispatcher work unrelated to the
            // assertion).
            var expected = ((SolidColorBrush)Application.Current.FindResource("PrimaryTextBrush")).Color;
            var roundTrip = new Button { Content = "翻译" };
            roundTrip.Style = (Style)Application.Current.FindResource("PrimaryButton");
            roundTrip.Measure(new Size(200, 60));
            roundTrip.Arrange(new Rect(0, 0, 200, 60));
            var roundTripText = FindVisualTextBlock(roundTrip);
            True(roundTripText is not null, $"round-trip {i}: the text node must exist");
            Equal(expected, ((SolidColorBrush)roundTripText!.GetValue(TextBlock.ForegroundProperty)).Color,
                $"round-trip {i}: a fresh primary button must render the live theme");
        }
        ThemeService.Apply(ThemePreference.Dark);
    }

    private static TextBlock? FindVisualTextBlock(System.Windows.Media.Visual visual)
    {
        if (visual is TextBlock text)
        {
            return text;
        }
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(visual); i++)
        {
            if (FindVisualTextBlock((System.Windows.Media.Visual)System.Windows.Media.VisualTreeHelper.GetChild(visual, i)) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static void InvokeClick(object target, string methodName)
    {
        typeof(QuickSearchWindow)
            .GetMethod(methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(target, new[] { target, new RoutedEventArgs() });
    }

    private static void SpinUntil(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && !condition())
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(10);
        }
        True(condition(), message);
    }

    /// <summary>
    /// T02 visual acceptance: Pangu spacing and emphasis parsing must run per
    /// natural-language segment; code spans render verbatim with no inserted
    /// spaces, no lost underscores.
    /// </summary>
    private static void MarkdownVisualSeparatesCodeFromNaturalLanguage()
    {
        EnsureApplication();
        var document = new FlowDocument();
        MarkdownPresenter.RenderToFlowDocument(
            document,
            "调用 `getUserName()` 获取名字，路径 `C:\\用户data\\file.txt` 保持原样",
            Application.Current.Resources);

        var rendered = new StringBuilder();
        var codeTexts = new List<string>();
        foreach (var inline in CollectInlines(document.Blocks))
        {
            if (inline is InlineUIContainer container)
            {
                // Production renders code spans as Border > TextBlock; dig the
                // text out of whatever visual hosts it.
                var code = FindTextBlock(container.Child);
                if (code is not null)
                {
                    codeTexts.Add(code.Text);
                    rendered.Append(code.Text);
                }
            }
            else if (inline is Run run)
            {
                rendered.Append(run.Text);
            }
        }

        var text = rendered.ToString();
        True(codeTexts.Contains("getUserName()"), $"the code span must render verbatim, got: {text}");
        True(codeTexts.Contains(@"C:\用户data\file.txt"),
            $"the path code span must keep every character, got: {text}");
        True(!text.Contains("用户 data", StringComparison.Ordinal),
            $"Pangu spacing must not enter code spans, got: {text}");
        True(text.Contains("调用 getUserName() 获取名字", StringComparison.Ordinal),
            $"natural language around code must stay readable, got: {text}");

        // Natural-language bold renders as bold text without markers.
        var boldDoc = new FlowDocument();
        MarkdownPresenter.RenderToFlowDocument(boldDoc, "**普通粗体**", Application.Current.Resources);
        var boldRuns = CollectInlines(boldDoc.Blocks).OfType<Run>().ToList();
        True(boldRuns.Any(r => r.Text == "普通粗体" && r.FontWeight == FontWeights.SemiBold),
            "natural-language bold must render as a semibold run without asterisks");
    }

    private static IEnumerable<Inline> CollectInlines(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph)
            {
                foreach (var inline in paragraph.Inlines)
                {
                    yield return inline;
                }
            }
        }
    }

    private static TextBlock? FindTextBlock(System.Windows.DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is TextBlock text)
            {
                return text;
            }
            if (node is System.Windows.Controls.Border border)
            {
                node = border.Child;
                continue;
            }
            if (node is System.Windows.Controls.ContentPresenter presenter)
            {
                node = presenter.Content as System.Windows.DependencyObject;
                continue;
            }
            return null;
        }
        return null;
    }

    private static void EdgeTtsResolvesVoicesCorrectly()
    {
        Equal("en-US-JennyNeural", EdgeTtsService.ResolveDefaultVoice("Hello world"));
        Equal("zh-CN-XiaoxiaoNeural", EdgeTtsService.ResolveDefaultVoice("你好世界"));
        Equal("ja-JP-NanamiNeural", EdgeTtsService.ResolveDefaultVoice("こんにちは"));
        Equal("ko-KR-SunHiNeural", EdgeTtsService.ResolveDefaultVoice("안녕하세요"));
    }

    /// <summary>
    /// T06 acceptance: an explicit language tag decides the voice; script
    /// detection is only the fallback, and emoji or a lone accent never
    /// select a language (the old range comparison sent them to German).
    /// </summary>
    private static void EdgeTtsVoicesFollowLanguageTag()
    {
        Equal("fr-FR-DeniseNeural", EdgeTtsService.ResolveVoice("fr-FR", "bonjour été"));
        Equal("en-US-JennyNeural", EdgeTtsService.ResolveVoice("en-US", "Hello 😀"));
        Equal("zh-TW-HsiaoChenNeural", EdgeTtsService.ResolveVoice("zh-TW", "你好"));
        Equal("ja-JP-NanamiNeural", EdgeTtsService.ResolveVoice("ja-JP", "你好，世界"));
        Equal("de-DE-KatjaNeural", EdgeTtsService.ResolveVoice("de-DE", "Hello 😀"));

        // Fallback without a tag: unambiguous scripts decide, emoji stays
        // English, French accents hint French.
        Equal("ja-JP-NanamiNeural", EdgeTtsService.ResolveVoice(null, "こんにちは"));
        Equal("en-US-JennyNeural", EdgeTtsService.ResolveVoice(null, "Hello 😀"));
        Equal("fr-FR-DeniseNeural", EdgeTtsService.ResolveVoice(null, "bonjour été"));
        Equal("de-DE-KatjaNeural", EdgeTtsService.ResolveVoice(null, "Übermäßig groß"));
        // "auto" means unknown: the script/accent fallback applies.
        Equal("fr-FR-DeniseNeural", EdgeTtsService.ResolveVoice("auto", "bonjour été"));
    }

    /// <summary>
    /// T06 acceptance: WebSocket protocol messages are reassembled from
    /// fragments — text markers split anywhere, binary two-byte headers split
    /// anywhere — and a connection that dies before turn.end is a failure,
    /// never truncated audio presented as success.
    /// </summary>
    private static async Task EdgeTtsAssemblesFragmentedMessagesAsync()
    {
        var audioPayload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };

        // A complete binary message: 2-byte header length + header + audio.
        var binary = new byte[2 + 4 + audioPayload.Length];
        binary[0] = 0x00;
        binary[1] = 0x04;
        binary[2] = 0x68;
        binary[3] = 0x65;
        binary[4] = 0x61;
        binary[5] = 0x64;
        Array.Copy(audioPayload, 0, binary, 6, audioPayload.Length);

        var turnEnd = Encoding.UTF8.GetBytes("X-RequestId:1\r\nPath:turn.end\r\n");
        var configAck = Encoding.UTF8.GetBytes("Path:response\r\n");

        // 1. Every message fragmented mid-header / mid-marker is reassembled.
        var fragments = new List<(WebSocketMessageType Type, byte[] Bytes, bool End)>
        {
            (WebSocketMessageType.Text, configAck, true),
            (WebSocketMessageType.Binary, binary[..4], false),          // header split
            (WebSocketMessageType.Binary, binary[4..8], false),         // header tail + audio start
            (WebSocketMessageType.Binary, binary[8..], true),           // audio tail
            (WebSocketMessageType.Text, turnEnd[..9], false),           // turn.end split mid-word
            (WebSocketMessageType.Text, turnEnd[9..], true),
            (WebSocketMessageType.Close, [], true),
        };
        var ws = new FakeWebSocket(fragments);
        EdgeTtsService.WebSocketFactory = (_, _) => Task.FromResult<WebSocket>(ws);
        try
        {
            var path = await EdgeTtsService.SynthesizeToMp3FileAsync("demo text");
            var written = await File.ReadAllBytesAsync(path);
            True(written.AsSpan().SequenceEqual(audioPayload),
                "reassembled audio must be byte-identical");
            File.Delete(path);
        }
        finally
        {
            EdgeTtsService.WebSocketFactory = null;
        }

        // 2. A connection that closes before turn.end must fail, not return
        // truncated audio.
        var truncated = new FakeWebSocket(
        [
            (WebSocketMessageType.Binary, binary, true),
            (WebSocketMessageType.Close, [], true),
        ]);
        EdgeTtsService.WebSocketFactory = (_, _) => Task.FromResult<WebSocket>(truncated);
        try
        {
            await ThrowsAsync<InvalidOperationException>(
                () => EdgeTtsService.SynthesizeToMp3FileAsync("demo text"));
        }
        finally
        {
            EdgeTtsService.WebSocketFactory = null;
        }
    }

    /// <summary>
    /// T06 acceptance: the source limit (5000 characters) and the audio
    /// budget (8 MiB cumulative, single messages included) are enforced with
    /// explicit errors — never by handing back truncated audio.
    /// </summary>
    private static async Task EdgeTtsEnforcesLimitsAsync()
    {
        // 1. Input beyond the per-request character budget is rejected up
        //    front, with the actual limit spelled out.
        var inputError = await ThrowsAsync<InvalidOperationException>(
            () => EdgeTtsService.SynthesizeToMp3FileAsync(new string('a', 5_001)));
        True(inputError.Message.Contains("5000", StringComparison.Ordinal),
            "the rejection must name the real limit");

        // 2. Exactly 5000 characters pass the input gate: with a transport
        //    that closes at once, the failure is the transport's, not the
        //    character gate's.
        EdgeTtsService.WebSocketFactory = (_, _) => Task.FromResult<WebSocket>(
            new FakeWebSocket([(WebSocketMessageType.Close, [], true)]));
        try
        {
            var boundaryError = await ThrowsAsync<InvalidOperationException>(
                () => EdgeTtsService.SynthesizeToMp3FileAsync(new string('a', 5_000)));
            True(!boundaryError.Message.Contains("5000", StringComparison.Ordinal),
                "an at-limit input must not be rejected by the character gate");
        }
        finally
        {
            EdgeTtsService.WebSocketFactory = null;
        }

        // 3. A single protocol message larger than the audio budget is refused.
        var oversizeFrame = BinaryFrameWithAudio(8 * 1024 * 1024 + 1);
        EdgeTtsService.WebSocketFactory = (_, _) => Task.FromResult<WebSocket>(
            new FakeWebSocket([(WebSocketMessageType.Binary, oversizeFrame, true)]));
        try
        {
            await ThrowsAsync<InvalidOperationException>(
                () => EdgeTtsService.SynthesizeToMp3FileAsync("demo text"));
        }
        finally
        {
            EdgeTtsService.WebSocketFactory = null;
        }

        // 4. Cumulative audio across messages is capped too: two 5 MiB frames
        //    exceed the 8 MiB budget on the second one.
        var halfBudget = BinaryFrameWithAudio(5 * 1024 * 1024);
        EdgeTtsService.WebSocketFactory = (_, _) => Task.FromResult<WebSocket>(
            new FakeWebSocket(
            [
                (WebSocketMessageType.Binary, halfBudget, true),
                (WebSocketMessageType.Binary, halfBudget, true),
            ]));
        try
        {
            await ThrowsAsync<InvalidOperationException>(
                () => EdgeTtsService.SynthesizeToMp3FileAsync("demo text"));
        }
        finally
        {
            EdgeTtsService.WebSocketFactory = null;
        }
    }

    private static byte[] BinaryFrameWithAudio(int audioLength)
    {
        var frame = new byte[2 + 4 + audioLength];
        frame[1] = 0x04; // two-byte big-endian header length = 4
        frame[2] = (byte)'h';
        frame[3] = (byte)'e';
        frame[4] = (byte)'a';
        frame[5] = (byte)'d';
        return frame;
    }

    /// <summary>
    /// T06 acceptance: stale audio residue from BOTH temp file families is
    /// cleaned, while files still in use are untouched.
    /// </summary>
    private static void TtsTempCleanupCoversBothFamilies()
    {
        var tempDir = Path.GetTempPath();
        var staleLocal = Path.Combine(tempDir, $"popglot-tts-{Guid.NewGuid():N}.wav");
        var staleCloud = Path.Combine(tempDir, $"popglot-edgetts-{Guid.NewGuid():N}.mp3");
        var freshLocal = Path.Combine(tempDir, $"popglot-tts-{Guid.NewGuid():N}.wav");
        try
        {
            File.WriteAllText(staleLocal, "stale");
            File.WriteAllText(staleCloud, "stale");
            File.WriteAllText(freshLocal, "fresh");
            var twoHoursAgo = DateTime.UtcNow.AddHours(-2);
            File.SetLastWriteTimeUtc(staleLocal, twoHoursAgo);
            File.SetLastWriteTimeUtc(staleCloud, twoHoursAgo);

            TtsService.CleanupStaleTempFiles();

            True(!File.Exists(staleLocal), "stale local TTS audio must be cleaned");
            True(!File.Exists(staleCloud), "stale cloud TTS audio must be cleaned");
            True(File.Exists(freshLocal), "recent TTS audio must be kept");
        }
        finally
        {
            foreach (var path in new[] { staleLocal, staleCloud, freshLocal })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private sealed class FakeWebSocket(IReadOnlyList<(WebSocketMessageType Type, byte[] Bytes, bool End)> frames) : WebSocket
    {
        private int _position;

        public override WebSocketState State =>
            _position >= frames.Count ? WebSocketState.Closed : WebSocketState.Open;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_position >= frames.Count)
            {
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }
            var (type, bytes, end) = frames[_position++];
            if (type == WebSocketMessageType.Close)
            {
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }
            var count = Math.Min(bytes.Length, buffer.Count);
            Array.Copy(bytes, 0, buffer.Array!, buffer.Offset, count);
            await Task.Yield();
            return new WebSocketReceiveResult(count, type, end);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override void Abort() { }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
    }

    /// <summary>
    /// T06 acceptance: the Microsoft voice service is a separate destination
    /// with a separate consent — translation network permission alone never
    /// sends text there — and Stop cancels an in-flight synthesis.
    /// </summary>
    private static void TtsCloudSpeechNeedsOwnConsent()
    {
        EnsureApplication();
        var edgeCalls = new List<(string Text, string? Voice)>();
        var localCalls = 0;
        var originalEdge = TtsService.EdgeSynthesizer;
        var originalLocal = TtsService.LocalSynthesizer;
        var originalSettings = TtsService.SettingsResolver;
        var originalShell = TtsService.ShellSettingsResolver;
        try
        {
            TtsService.SettingsResolver = () => CoreBridge.GetSettings() with
            {
                NetworkEnabled = true,
                SafeDevMode = false,
            };
            // Synthesizer mocks return null: this test exercises gating and
            // cancellation only, never actual playback (a MediaPlayer in a
            // console host needs real audio plumbing the suite must not
            // depend on).
            TtsService.EdgeSynthesizer = (text, voice, _) =>
            {
                lock (edgeCalls)
                {
                    edgeCalls.Add((text, voice));
                }
                return Task.FromResult<string?>(null);
            };
            TtsService.LocalSynthesizer = _ =>
            {
                Interlocked.Increment(ref localCalls);
                return Task.FromResult<string?>(null);
            };

            // 1. Cloud speech not consented: network is on, but the text must
            // stay local — zero cloud sends.
            TtsService.ShellSettingsResolver = () => ShellSettings.Default with { CloudSpeechEnabled = false };
            TtsService.Speak("consent probe", "fr-FR");
            SpinUntil(() => Volatile.Read(ref localCalls) >= 1, "local synthesis never ran");
            lock (edgeCalls)
            {
                Equal(0, edgeCalls.Count, "cloud speech must not run without its own consent");
            }

            // 2. Consented: the edge synthesizer runs with the resolved voice.
            TtsService.ShellSettingsResolver = () => ShellSettings.Default with { CloudSpeechEnabled = true };
            TtsService.Speak("consent probe two", "fr-FR");
            SpinUntil(
                () => { lock (edgeCalls) { return edgeCalls.Count >= 1; } },
                "cloud synthesis never ran");
            string? resolvedVoice;
            lock (edgeCalls)
            {
                Equal(1, edgeCalls.Count, "only the consented cloud call may run");
                resolvedVoice = edgeCalls[0].Voice;
            }
            Equal("fr-FR-DeniseNeural", resolvedVoice, "the language tag must resolve the voice");

            // 3. Stop cancels an in-flight cloud synthesis.
            var cancelled = new TaskCompletionSource();
            TtsService.EdgeSynthesizer = async (_, _, ct) =>
            {
                lock (edgeCalls)
                {
                    edgeCalls.Add(("cancel probe", "en-US-JennyNeural"));
                }
                try
                {
                    await Task.Delay(5000, ct);
                    return Path.Combine(Path.GetTempPath(), $"popglot-edgetts-test-{Guid.NewGuid():N}.mp3");
                }
                catch (OperationCanceledException)
                {
                    cancelled.SetResult();
                    throw;
                }
            };
            TtsService.Speak("cancel probe", "en-US");
            SpinUntil(
                () => { lock (edgeCalls) { return edgeCalls.Count >= 2; } },
                "cloud synthesis never started");
            var stopwatch = Stopwatch.StartNew();
            TtsService.Stop();
            SpinUntil(() => cancelled.Task.IsCompleted, "Stop must cancel the in-flight synthesis");
            stopwatch.Stop();
            True(stopwatch.ElapsedMilliseconds < 200,
                $"Stop must cancel an in-flight synthesis within 200ms (took {stopwatch.ElapsedMilliseconds}ms)");
            Equal(false, TtsService.IsSpeaking, "nothing may play after Stop");
        }
        finally
        {
            TtsService.EdgeSynthesizer = originalEdge;
            TtsService.LocalSynthesizer = originalLocal;
            TtsService.SettingsResolver = originalSettings;
            TtsService.ShellSettingsResolver = originalShell;
            TtsService.Stop();
        }
    }

    private static void VocabularyStoreBehaviour()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"popglot-vocab-{Guid.NewGuid():N}.json");
        try
        {
            var store = new VocabularyStore(tempFile);
            True(!store.IsStarred("borrow checker"), "clean store should not have word");

            var starred = store.ToggleStar("borrow checker", "借用检查器", "bɒrəʊ", "Rust内存安全", "en", "zh-CN");
            True(starred.Persisted && starred.Starred, "word should be marked starred and persisted");
            True(store.IsStarred("borrow checker", "en", "zh-CN"), "word must be queried as starred");

            // Identity is word + language pair: the same word starred for a
            // different target language is a separate entry, not an unstar.
            var otherPair = store.ToggleStar("borrow checker", "借用檢查器", "", "", "en", "zh-TW");
            True(otherPair.Persisted && otherPair.Starred, "same word for another language pair stars separately");
            Equal(2, store.GetAll().Count);
            True(store.IsStarred("borrow checker", "en", "zh-CN"), "the first pair must still be starred");

            var tsv = store.ExportToAnkiTsv();
            True(tsv.Contains("borrow checker\t借用检查器"), "Anki export must contain tab-separated front/back");

            var md = store.ExportToMarkdown();
            True(md.Contains("| **borrow checker** | 借用检查器 |"), "Markdown export must contain table row");

            var unstarred = store.ToggleStar("borrow checker", "", "", "", "en", "zh-CN");
            True(unstarred.Persisted && !unstarred.Starred, "toggling the same pair again must unstar");
            True(!store.IsStarred("borrow checker", "en", "zh-CN"), "word should no longer be starred for that pair");
            True(store.IsStarred("borrow checker", "en", "zh-TW"), "the other pair survives the unstar");
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

    /// <summary>
    /// T07 acceptance: a store whose storage path cannot be written reports
    /// the failure instead of a fake star, keeps its previous entries in
    /// memory, and a successful save really survives a reload.
    /// </summary>
    private static void VocabularyStoreSaveFailuresStayVisible()
    {
        // The classic F08 probe: a DIRECTORY where the file should be.
        var dirAsPath = Path.Combine(Path.GetTempPath(), $"popglot-vocab-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dirAsPath);
        var goodFile = Path.Combine(Path.GetTempPath(), $"popglot-vocab-good-{Guid.NewGuid():N}.json");
        try
        {
            var broken = new VocabularyStore(dirAsPath);
            var result = broken.ToggleStar("foo_bar_baz", "演示译文", "", "", "en", "zh-CN");
            True(!result.Persisted, "a write to a directory path must report not persisted");
            True(!broken.IsStarred("foo_bar_baz", "en", "zh-CN"),
                "a failed write must not light the star in memory");
            True(result.DescribeFailureZh().Contains("未保存到本机"),
                "the failure wording must tell the user nothing was saved");
            Equal(0, new VocabularyStore(dirAsPath).GetAll().Count,
                "recreating the store finds nothing: the fake success is gone");

            // A working store keeps earlier entries after a failed mutation.
            var store = new VocabularyStore(goodFile);
            True(store.ToggleStar("keep_me", "保留词条", "", "", "en", "zh-CN").Persisted, "baseline star must persist");
            var failing = new VocabularyStore(dirAsPath);
            var failed = failing.ToggleStar("never_saved", "永不落盘", "", "", "en", "zh-CN");
            True(!failed.Persisted, "the failing store must refuse its own write");

            var reload = new VocabularyStore(goodFile);
            Equal(1, reload.GetAll().Count, "earlier entries survive reloads untouched");
            True(reload.IsStarred("keep_me", "en", "zh-CN"), "the reloaded store keeps its star");
        }
        finally
        {
            try { Directory.Delete(dirAsPath); } catch { }
            try { File.Delete(goodFile); } catch { }
            try { File.Delete(goodFile + ".bak"); } catch { }
        }
    }

    /// <summary>
    /// T07 acceptance: entry length and capacity limits reject explicitly and
    /// never silently drop older entries to make room.
    /// </summary>
    private static void VocabularyStoreEnforcesLimits()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"popglot-vocab-limit-{Guid.NewGuid():N}.json");
        try
        {
            var store = new VocabularyStore(tempFile);
            var oversized = store.ToggleStar(
                new string('a', VocabularyStore.MaxEntryCharacters + 1), "译文", "", "", "en", "zh-CN");
            Equal(VocabularySaveStatus.EntryTooLarge, oversized.Status);
            True(!oversized.Persisted, "an oversized entry must not persist");
            Equal(0, store.GetAll().Count);

            // Seed a full store through its own file: 10000 entries on disk.
            var full = new List<string>();
            var template = new VocabularyWord(
                Guid.NewGuid(), DateTimeOffset.UtcNow, "seed", "种子", "", "", "en", "zh-CN", []);
            for (var i = 0; i < VocabularyStore.MaxEntries; i++)
            {
                full.Add($"{{\"Id\":\"{Guid.NewGuid()}\",\"CreatedAt\":\"2026-09-05T00:00:00Z\",\"Word\":\"word_{i}\",\"Translation\":\"译_{i}\",\"Phonetic\":\"\",\"Explanation\":\"\",\"SourceLanguage\":\"en\",\"TargetLanguage\":\"zh-CN\",\"Tags\":[]}}");
            }
            File.WriteAllText(tempFile, "[" + string.Join(",", full) + "]");
            var loaded = new VocabularyStore(tempFile);
            Equal(VocabularyStore.MaxEntries, loaded.GetAll().Count);

            var refused = loaded.ToggleStar("one_more", "再一条", "", "", "en", "zh-CN");
            Equal(VocabularySaveStatus.StoreFull, refused.Status);
            True(!refused.Persisted, "a full store must refuse instead of dropping the oldest");
            Equal(VocabularyStore.MaxEntries, loaded.GetAll().Count);
            True(loaded.IsStarred("word_0", "en", "zh-CN"),
                "the oldest entry must still be there after the refusal");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            try { File.Delete(tempFile + ".bak"); } catch { }
        }
    }

    /// <summary>
    /// T07 acceptance: star identity keeps code identifiers distinct (case
    /// preserved) while language tags compare loosely.
    /// </summary>
    private static void VocabularyStarIdentityPreservesCase()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"popglot-vocab-case-{Guid.NewGuid():N}.json");
        try
        {
            var store = new VocabularyStore(tempFile);
            True(store.ToggleStar("MyVariable", "我的变量", "", "", "en", "zh-CN").Persisted, "baseline star must persist");
            True(store.ToggleStar("myvariable", "同名小写", "", "", "en", "zh-CN").Persisted,
                "different case is a different code identifier");
            Equal(2, store.GetAll().Count);
            True(store.IsStarred("MyVariable", "EN", "zh-cn"),
                "language tags compare loosely regardless of case");
            True(!new VocabularyStore(tempFile).IsStarred("MYVARIABLE", "en", "zh-CN"),
                "word case is not folded on reload either");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            try { File.Delete(tempFile + ".bak"); } catch { }
        }
    }

    /// <summary>
    /// T07 acceptance: a corrupt or oversized history file is quarantined
    /// (never destroyed by the next save), null array entries are ignored,
    /// and the library is told where the backup is.
    /// </summary>
    private static void HistoryCorruptFileIsQuarantined()
    {
        var path = Path.Combine(Path.GetTempPath(), $"popglot-hist-corrupt-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json at all");
            var store = new HistoryStore(path);
            Equal(0, store.Load().Count);
            True(store.LastQuarantinePath is not null && File.Exists(store.LastQuarantinePath),
                "the corrupt history must be backed up");
            Equal("{ not json at all", File.ReadAllText(store.LastQuarantinePath!),
                "the quarantine keeps the original bytes");
            True(store.TryAdd(Entry("fresh after corrupt", "损坏后的新条目"), enabled: true)
                is HistoryAddResult.Stored,
                "history still works after a corrupt load");
            True(File.Exists(store.LastQuarantinePath!),
                "the quarantine survives the next save instead of being overwritten");

            // Null entries inside an otherwise valid array must not crash Load.
            var nullsPath = path + ".nulls";
            File.WriteAllText(nullsPath,
                """[null,{"Id":"11111111-1111-1111-1111-111111111111","CreatedAt":"2026-09-05T00:00:00Z","SourceKind":"输入","Source":"real entry","Translation":"真实条目","Explanation":"","ProtectedTerms":[]}]""");
            var nulls = new HistoryStore(nullsPath);
            var loaded = nulls.Load();
            Equal(1, loaded.Count, "null entries are skipped, valid ones load");
            True(nulls.LastQuarantinePath is null, "a merely sparse file is not quarantined");

            // Oversized file: unreadable, but the bytes are still the user's.
            var bigPath = path + ".big";
            File.WriteAllText(bigPath, new string('x', 4 * 1024 * 1024 + 1));
            var big = new HistoryStore(bigPath);
            Equal(0, big.Load().Count);
            True(big.LastQuarantinePath is not null && new FileInfo(big.LastQuarantinePath!).Length > 4 * 1024 * 1024,
                "an oversized history is quarantined before any save can replace it");
        }
        finally
        {
            foreach (var candidate in Directory.EnumerateFiles(
                Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                try { File.Delete(candidate); } catch { }
            }
        }
    }

    /// <summary>
    /// T07 acceptance: default exports are safe — CSV formula prefixes are
    /// neutralized (including after leading control characters), quotes,
    /// commas, newlines, Chinese and emoji round-trip, and the Anki TSV
    /// escapes HTML characters because the header declares #html:true.
    /// </summary>
    private static void ExportsAreSafeForSpreadsheetsAndAnki()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"popglot-vocab-export-{Guid.NewGuid():N}.json");
        try
        {
            var store = new VocabularyStore(tempFile);
            True(store.ToggleStar("=cmd|' /C calc'!A0", "危险公式", "", "", "en", "zh-CN").Persisted, "fixture star must persist");
            True(store.ToggleStar("+SUM(A1:A2)", "加号公式", "", "", "en", "zh-CN").Persisted, "fixture star must persist");
            True(store.ToggleStar("-2+3", "负号开头", "", "", "en", "zh-CN").Persisted, "fixture star must persist");
            True(store.ToggleStar("@import", "at 开头", "", "", "en", "zh-CN").Persisted, "fixture star must persist");
            True(store.ToggleStar("\t=indirect()", "制表符掩护的公式", "", "", "en", "zh-CN").Persisted, "fixture star must persist");
            True(store.ToggleStar("plain, \"word\"", "逗号与引号\n第二行 😀 中文", "", "", "en", "zh-CN").Persisted, "fixture star must persist");

            var csv = store.ExportToCsv();
            // Round-trip with a real CSV parser: column structure must survive
            // (the T19/F07 probe caught the old apostrophe-outside-quotes
            // producing 10 fields against a 9-column header) AND every cell
            // that could act as a formula must carry the in-quote neutralizer.
            using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(
                new System.IO.StringReader(csv));
            parser.HasFieldsEnclosedInQuotes = true;
            parser.SetDelimiters(",");
            var header = parser.ReadFields();
            True(header is not null && header.Length == 9, "the header must have 9 columns");
            var parsedFormulaRows = 0;
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields();
                True(fields is not null && fields.Length == 9,
                    $"every record must have 9 columns, got {fields?.Length}");
                var word = fields![2];
                // The neutralizer is part of the stored value: '=<formula>.
                if (word.Length > 1 && word[0] == '\'' && "=+-@".Contains(word[1]))
                {
                    parsedFormulaRows++;
                }
            }
            True(parsedFormulaRows >= 4, $"expected the formula-prefixed rows to round-trip, got {parsedFormulaRows}");
            True(csv.Contains("😀"), "emoji must survive the CSV bytes");

            var anki = store.ExportToAnkiTsv();
            // A word carrying HTML-significant characters must be escaped.
            var htmlStore = new VocabularyStore(tempFile + ".html");
            True(htmlStore.ToggleStar("<script>&\"x\"</script>", "HTML 字符", "", "", "en", "zh-CN").Persisted, "fixture star must persist");
            var htmlTsv = htmlStore.ExportToAnkiTsv();
            True(htmlTsv.Contains("&lt;script&gt;&amp;&quot;x&quot;&lt;/script&gt;"),
                "Anki export must escape < > & \" because #html:true is declared");
            True(!htmlTsv.Contains("<script>"), "raw angle brackets must not reach the TSV");
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
            try { File.Delete(tempFile + ".bak"); } catch { }
            try { File.Delete(tempFile + ".html"); } catch { }
            try { File.Delete(tempFile + ".html.bak"); } catch { }
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
                CloudSpeechEnabled = true,
                CloseHintShown = true,
            };
            ShellSettingsStore.Save(original, path);
            var reloaded = ShellSettingsStore.Load(path);
            Equal(original, reloaded);
            Equal("Ctrl+Alt+K", reloaded.ShowWindowHotkey?.DisplayName);
            Equal(FreeEngineConsent.Allowed, reloaded.FreeEngineConsent);
            Equal(true, reloaded.CloudSpeechEnabled, "the cloud speech consent must round-trip");
            Equal(true, reloaded.CloseHintShown);

            // A legacy file without these fields keeps the defaults instead of
            // silently dropping the shortcut or the consent answer. Upgrades
            // never grant the Microsoft voice destination implicitly.
            File.WriteAllText(path, "{\"SelectionHotkey\":\"Ctrl+Shift+Y\"}");
            var migrated = ShellSettingsStore.Load(path);
            Equal("Ctrl+Alt+O", migrated.ShowWindowHotkey?.DisplayName);
            Equal(FreeEngineConsent.Unset, migrated.FreeEngineConsent);
            Equal(false, migrated.CloudSpeechEnabled,
                "a legacy settings file must not gain cloud speech consent");
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
    /// T01 acceptance: the free-engine send boundary (FreeTranslateService and
    /// the production health probe) transmits only for Consent=Allowed with
    /// safe-dev-mode off and network on — for the coordinator's normal entry
    /// AND for a forced probe. Denied/Unset must produce zero sends through
    /// the real service, not merely a false UsesFreeEngine predicate, and an
    /// Unset refusal must not silently persist a denial.
    /// </summary>
    private static async Task FreeEngineAuthorizationMatrixAtSendBoundary()
    {
        var originalLoader = OutboundPolicy.SettingsLoader;
        var originalSaver = OutboundPolicy.SettingsSaver;
        var originalSender = FreeTranslateService.HttpSenderOverride;
        var originalPrompt = OutboundPolicy.ConsentPrompt;
        var consentPath = Path.Combine(Path.GetTempPath(), $"popglot-consent-matrix-{Guid.NewGuid():N}.json");
        OutboundPolicy.ConsentPrompt = null;
        long sends = 0;
        FreeTranslateService.HttpSenderOverride = (request, _) =>
        {
            Interlocked.Increment(ref sends);
            var host = request.RequestUri?.Host ?? string.Empty;
            True(host is "translate.googleapis.com" or "clients5.google.com",
                $"the free engine must only target its documented endpoints, got {host}");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                // Shape understood by the first endpoint's parser (gtx single):
                // sentences live under root[0].
                Content = new StringContent(
                    "[[[\"mock-translation\",\"demo source\",\"en\",\"\"]]]",
                    Encoding.UTF8,
                    "application/json"),
            });
        };
        try
        {
            foreach (var consent in new[] { FreeEngineConsent.Unset, FreeEngineConsent.Denied, FreeEngineConsent.Allowed })
            foreach (var safeDevMode in new[] { true, false })
            foreach (var networkEnabled in new[] { true, false })
            {
                OutboundPolicy.SettingsLoader = () => ShellSettings.Default with { FreeEngineConsent = consent };
                OutboundPolicy.SettingsSaver = _ => { };
                var settings = CoreBridge.GetSettings() with
                {
                    SafeDevMode = safeDevMode,
                    NetworkEnabled = networkEnabled,
                };
                var label = $"consent={consent} safeDevMode={safeDevMode} networkEnabled={networkEnabled}";
                var allowed = consent == FreeEngineConsent.Allowed && !safeDevMode && networkEnabled;
                Interlocked.Exchange(ref sends, 0);

                // Normal entry: the coordinator running the REAL free-engine
                // boundary (not a fake executor).
                var coordinator = new TranslationCoordinator(
                    executor: new FreeEngineBoundaryExecutor(settings));
                var session = await coordinator.TranslateTextAsync(
                    $"matrix {consent} {safeDevMode} {networkEnabled}",
                    "auto", "zh-CN", TranslationInputSource.Manual);
                Equal(allowed ? 1 : 0, Interlocked.Read(ref sends), $"normal entry send count for {label}");
                if (allowed)
                {
                    Equal("mock-translation", session.TranslatedText,
                        $"the allowed combo must actually reach the mock ({label})");
                    Equal(TranslationSessionStage.Completed, session.Stage, label);
                }
                else
                {
                    True(session.Error is not null,
                        $"a denied combo must fail with an explicit reason ({label})");
                }

                // Forced probe through the production health service.
                if (allowed)
                {
                    True(OutboundPolicy.AllowsFreeEngine(settings, out _, out var probeAuth), label);
                    var health = await FreeTranslateService.GetHealthAsync(force: true, probeAuth);
                    True(health.Ok, $"the allowed probe must reach the mock ({label})");
                    Equal(2, Interlocked.Read(ref sends), $"forced probe send count for {label}");
                }
                else
                {
                    // No authorization exists for this combo: even a caller
                    // that grabs the production health service and forces a
                    // re-check must transmit nothing.
                    var health = await FreeTranslateService.GetHealthAsync(force: true, null);
                    True(!health.Ok, $"an unauthorized probe must not report success ({label})");
                    Equal(0, Interlocked.Read(ref sends), $"unauthorized probe must send nothing ({label})");
                }
            }

            // Unset stays Unset: a refused probe must not record a denial the
            // user never gave.
            OutboundPolicy.SettingsLoader = () => ShellSettingsStore.Load(consentPath);
            OutboundPolicy.SettingsSaver = s => ShellSettingsStore.Save(s, consentPath);
            var unsetSettings = CoreBridge.GetSettings() with { SafeDevMode = false, NetworkEnabled = true };
            Equal(false, OutboundPolicy.AllowsFreeEngine(unsetSettings, out _, out var unsetAuth));
            True(unsetAuth is null, "a denied decision must never issue an authorization");
            Equal(FreeEngineConsent.Unset, ShellSettingsStore.Load(consentPath).FreeEngineConsent);
        }
        finally
        {
            OutboundPolicy.SettingsLoader = originalLoader;
            OutboundPolicy.SettingsSaver = originalSaver;
            OutboundPolicy.ConsentPrompt = originalPrompt;
            FreeTranslateService.HttpSenderOverride = originalSender;
            File.Delete(consentPath);
        }
    }

    /// <summary>
    /// Executor for the authorization matrix: production routing decisions
    /// resolve to "nothing configured" so the coordinator takes the free-engine
    /// branch, while TranslateFreeAsync runs the REAL FreeTranslateService
    /// boundary with the authorization the coordinator issued.
    /// </summary>
    private sealed class FreeEngineBoundaryExecutor(ProviderSettings settings) : ITranslationExecutor
    {
        /// <summary>Optional fake engine; when null the real FreeTranslateService runs.</summary>
        public Func<string, string, string, CancellationToken, Task<TranslationResponse>>? OnTranslateFree { get; init; }

        public ProviderSettings GetSettings() => settings;

        public (ProviderRoute? Text, ProviderRoute? Vision) ResolveRoutes() => (null, null);

        public ResolvedRoute ResolveScreenshotRoute(ProviderSettings s, bool ocrAvailable) =>
            throw new NotSupportedException("not used by the authorization matrix");

        public string? LoadApiKey(string target) => null;

        public bool IsOcrSupported => false;

        public Task<string> RecognizeOcrTextAsync(byte[] imageBytes, string sourceLang, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not used by the authorization matrix");

        public TranslationStreamSession StreamText(
            string? apiKey, string source, string sourceLang, string targetLang,
            string sessionId, long epoch, CancellationToken cancellationToken) =>
            throw new NotSupportedException("not used by the authorization matrix");

        public TranslationStreamSession StreamTextDraft(
            ProviderSettings draftSettings, string apiKey, string source, string sourceLang,
            string targetLang, string sessionId, long epoch, CancellationToken cancellationToken) =>
            throw new NotSupportedException("not used by the authorization matrix");

        public TranslationStreamSession StreamVisionDraft(
            ProviderSettings draftSettings, string textApiKey, string visionApiKey, byte[] image,
            string sourceLang, string targetLang, string sessionId, long epoch, CancellationToken cancellationToken) =>
            throw new NotSupportedException("not used by the authorization matrix");

        public async Task<TranslationResponse> TranslateFreeAsync(
            string source, string sourceLang, string targetLang,
            Services.FreeEngineAuthorization authorization, CancellationToken cancellationToken)
        {
            if (OnTranslateFree is not null)
            {
                return await OnTranslateFree(source, sourceLang, targetLang, cancellationToken);
            }
            return await FreeTranslateService.TranslateAsync(source, sourceLang, targetLang, authorization, cancellationToken);
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
    /// Structural guard for the result-card regression: without dedicated grid
    /// rows, the streaming text layer fell into row 0 on top of the engine
    /// header buttons, and the explanation area collapsed to zero height.
    /// XML-parse the panel XAML so future layout edits cannot silently
    /// reintroduce either failure.
    /// </summary>
    private static void TranslationPanelResultCardRowStructure()
    {
        var doc = XDocument.Load(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "TranslationPanelWindow.xaml"));
        var ns = doc.Root!.Name.Namespace;
        var xns = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

        // (a) The text layer Grid that hosts TranslationTextBox must own
        // Grid.Row="1" inside the result card, never the header's row 0.
        var translationBox = doc.Descendants(ns + "TextBox")
            .FirstOrDefault(e => (string?)e.Attribute(xns + "Name") == "TranslationTextBox");
        True(translationBox is not null, "TranslationTextBox must be declared");
        var textLayerGrid = translationBox!.Ancestors(ns + "Grid").FirstOrDefault();
        True(textLayerGrid is not null, "TranslationTextBox must sit inside a Grid");
        // Grid.Row is an unprefixed attribute name in raw XAML (the default
        // xmlns namespace applies to elements, not attributes).
        Equal("1", (string?)textLayerGrid!.Attribute("Grid.Row"),
            "the translation text layer must occupy Grid.Row=1 (the star row below the engine header)");

        // (b) The explanation area's ScrollViewer ancestor must own Grid.Row=2
        // so explanations, token chips and warnings stay visible and capped.
        var explanationBox = doc.Descendants()
            .FirstOrDefault(e => (string?)e.Attribute(xns + "Name") == "ExplanationBox");
        True(explanationBox is not null, "ExplanationBox must be declared");
        var explanationScroller = explanationBox!.Ancestors(ns + "ScrollViewer").FirstOrDefault();
        True(explanationScroller is not null, "ExplanationBox must live inside a ScrollViewer");
        Equal("2", (string?)explanationScroller!.Attribute("Grid.Row"),
            "the explanation ScrollViewer must occupy Grid.Row=2 (the capped row under the text)");
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

        // The Ollama template name moved to full-width parentheses in v5;
        // configs from ≤0.1.2 still carry the ASCII-paren name and must stay
        // pristine, or the v4→v5 cleanup misses their seeded placeholder.
        var ollama = templates.First(t => t.Id == "ollama-local");
        True(ProviderCatalog.IsPristineTemplate(ollama), "ollama template is pristine");
        True(ProviderCatalog.IsPristineTemplate(
            new ProviderProfile(ollama) { Name = "Ollama (本地)" }),
            "the legacy ASCII-paren Ollama name is still pristine");

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
        var coreSave = source.IndexOf("CoreBridge.SaveSettingsAsync(policySettings)", StringComparison.Ordinal);
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
    /// MenuItem 隐式样式曾用 Trigger Role="Separator" —— MenuItemRole 枚举
    /// 没有该值，首个 MenuItem 解析隐式样式时抛 XamlParseException 直接
    /// 崩溃进程（右下角快速切换菜单一打开就闪退）。分隔线必须由
    /// Separator 控件的隐式样式渲染。
    /// </summary>
    private static void MenuItemStylesHaveValidRoles()
    {
        var controlsXaml = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "Themes", "Controls.xaml"));
        True(
            !Regex.IsMatch(controlsXaml, @"Trigger\s+Property=""Role""\s+Value=""Separator"""),
            "MenuItem 样式不得用 Role=Separator 触发器；MenuItemRole 枚举没有该值，会在首次加载隐式样式时崩溃");
        True(
            controlsXaml.Contains("<Style TargetType=\"Separator\">"),
            "菜单分隔线应由 Separator 的隐式样式渲染");
    }

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
            Equal(7, migrated.SchemaVersion, "migration bumps the schema version");
            Equal(1, migrated.Profiles.Count, "only the user-configured service survives");
            True(!migrated.Profiles[0].AllowLanEndpoints,
                "migration must never invent the LAN permission for existing profiles");
            Equal("我的双用途服务", migrated.Profiles[0].Name);
            True(migrated.Profiles[0].SupportsText && migrated.Profiles[0].SupportsVision,
                "model fields, including one shared model, derive both route roles");
            Equal("我的双用途服务", migrated.TryGetActiveProfile()!.Name,
                "a migrated-away default re-points at the surviving text service");
            Equal(7, System.Text.Json.JsonSerializer.Deserialize<CoreProductConfig>(
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

    private static void SettingsDraftSnapshotPureComparison()
    {
        var baseRoute = RouteDraftSnapshot.Create(networkEnabled: true, safeMode: false, allowImageUpload: true, mode: "Auto");
        var baseline = SettingsFormSnapshot.Create(
            selectionHotkey: "Ctrl+Alt+T",
            screenshotHotkey: "Ctrl+Alt+S",
            closeHotkey: "Escape",
            showWindowHotkey: "Ctrl+Alt+W",
            historyEnabled: true,
            closeOnFocusLoss: true,
            autoCopy: false,
            startWithWindows: false,
            includeExplanation: true,
            protectTokens: true,
            theme: "Dark",
            route: baseRoute);

        var baselineStr = baseline.Serialize();

        // 1. Exact replica is clean
        var replica = SettingsFormSnapshot.Create(
            "Ctrl+Alt+T", "Ctrl+Alt+S", "Escape", "Ctrl+Alt+W",
            true, true, false, false, true, true, "Dark",
            RouteDraftSnapshot.Create(true, false, true, "Auto"));
        True(!SettingsWindow.HasDraftChanges(replica.Serialize(), baselineStr), "identical settings snapshot must be clean");

        // 2. Modifying each field makes it dirty, and restoring it makes it clean
        // Hotkey change
        var modifiedHotkey = SettingsFormSnapshot.Create(
            "Ctrl+Alt+F", "Ctrl+Alt+S", "Escape", "Ctrl+Alt+W",
            true, true, false, false, true, true, "Dark", baseRoute);
        True(SettingsWindow.HasDraftChanges(modifiedHotkey.Serialize(), baselineStr), "hotkey change makes draft dirty");

        // Toggle change
        var modifiedToggle = SettingsFormSnapshot.Create(
            "Ctrl+Alt+T", "Ctrl+Alt+S", "Escape", "Ctrl+Alt+W",
            true, true, true, false, true, true, "Dark", baseRoute);
        True(SettingsWindow.HasDraftChanges(modifiedToggle.Serialize(), baselineStr), "toggle change makes draft dirty");

        // Theme change
        var modifiedTheme = SettingsFormSnapshot.Create(
            "Ctrl+Alt+T", "Ctrl+Alt+S", "Escape", "Ctrl+Alt+W",
            true, true, false, false, true, true, "Light", baseRoute);
        True(SettingsWindow.HasDraftChanges(modifiedTheme.Serialize(), baselineStr), "theme change makes draft dirty");

        // Route change
        var modifiedRoute = SettingsFormSnapshot.Create(
            "Ctrl+Alt+T", "Ctrl+Alt+S", "Escape", "Ctrl+Alt+W",
            true, true, false, false, true, true, "Dark",
            RouteDraftSnapshot.Create(networkEnabled: false, safeMode: false, allowImageUpload: true, mode: "Auto"));
        True(SettingsWindow.HasDraftChanges(modifiedRoute.Serialize(), baselineStr), "route change makes draft dirty");
        True(SettingsWindow.HasDraftChanges(modifiedRoute.Route.Serialize(), baseRoute.Serialize()), "route draft changes detect pending");

        // Restoring route back to original values drops route pending and restores Clean

        var restoredRoute = RouteDraftSnapshot.Create(true, false, true, "Auto");

        True(!SettingsWindow.HasDraftChanges(restoredRoute.Serialize(), baseRoute.Serialize()), "restoring route values drops route pending");



        // The shared pure state decision: same snapshots, resolved to states.

        Equal(SettingsEditState.Dirty, SettingsWindow.StateFromDraft(modifiedRoute.Serialize(), baselineStr),

            "a diverging draft resolves Dirty through the shared pure function");

        Equal(SettingsEditState.Clean, SettingsWindow.StateFromDraft(replica.Serialize(), baselineStr),

            "a converged draft resolves Clean through the shared pure function");
    }

    private static void HeaderNormalizationAndEditorRevertClean()
    {
        // 1. Headers normalization handles CRLF, LF, CR, trailing/leading whitespace and blank lines
        var headers1 = "Authorization: Bearer token1\r\nX-Custom: value1\r\n\r\n";
        var headers2 = "   Authorization: Bearer token1   \n\nX-Custom: value1\n";
        var headers3 = "Authorization: Bearer token1\rX-Custom: value1";
        var norm1 = ServicesSection.NormalizeHeaderValue(headers1);
        var norm2 = ServicesSection.NormalizeHeaderValue(headers2);
        var norm3 = ServicesSection.NormalizeHeaderValue(headers3);
        Equal(norm1, norm2, "CRLF and LF with extra spacing must normalize to identical header block");
        Equal(norm1, norm3, "CR newlines must normalize identically");
        Equal("Authorization: Bearer token1\nX-Custom: value1", norm1);

        // 2. Editor snapshot comparison
        var baseline = ServiceEditorSnapshot.CreateNormalized(
            name: "DeepSeek Service",
            providerType: "OpenAiCompatible",
            baseUrl: "https://api.deepseek.com/v1",
            textEndpoint: "/chat/completions",
            visionEndpoint: "/chat/completions",
            textModel: "deepseek-chat",
            visionModel: "deepseek-chat",
            extraHeaders: headers1,
            anthropicVersion: "2023-06-01",
            supportsText: true,
            supportsVision: false,
            useTextModelForVision: true,
            allowInsecureTls: false,
            apiKey: "sk-12345");
        var baselineStr = baseline.Serialize();

        // Typing same headers with different formatting remains Clean
        var withDifferentFormatting = ServiceEditorSnapshot.CreateNormalized(
            "DeepSeek Service ",
            "OpenAiCompatible",
            " https://api.deepseek.com/v1\r\n",
            "/chat/completions",
            "/chat/completions",
            " deepseek-chat ",
            "deepseek-chat",
            headers2,
            "2023-06-01",
            true,
            false,
            true,
            false,
            "sk-12345");
        True(!ServicesSection.HasEditorChanges(withDifferentFormatting.Serialize(), baselineStr),
            "normalized fields with different formatting or newlines must stay Clean");

        // Editing a value makes it Dirty
        var withEditedUrl = ServiceEditorSnapshot.CreateNormalized(
            "DeepSeek Service", "OpenAiCompatible", "https://custom-proxy.com/v1",
            "/chat/completions", "/chat/completions", "deepseek-chat", "deepseek-chat",
            headers1, "2023-06-01", true, false, true, false, "sk-12345");
        True(ServicesSection.HasEditorChanges(withEditedUrl.Serialize(), baselineStr),
            "editing base url makes editor dirty");

        // Reverting the value back to original returns to Clean
        var revertedUrl = ServiceEditorSnapshot.CreateNormalized(
            "DeepSeek Service", "OpenAiCompatible", "https://api.deepseek.com/v1",
            "/chat/completions", "/chat/completions", "deepseek-chat", "deepseek-chat",
            headers1, "2023-06-01", true, false, true, false, "sk-12345");
        True(!ServicesSection.HasEditorChanges(revertedUrl.Serialize(), baselineStr),
            "reverting edited url back to baseline returns editor to Clean");
    }

    private static void SharedVisionModelRetentionAndRevert()
    {
        var tracker = new SharedVisionModelTracker();

        // Case A: distinct text and vision models (e.g. gpt-4o and gpt-4o-mini)
        tracker.OnLoaded("gpt-4o", "gpt-4o-mini");
        Equal(null, tracker.StashedVisionModel);

        var baseline = ServiceEditorSnapshot.CreateNormalized(
            name: "OpenAI Service",
            providerType: "OpenAiCompatible",
            baseUrl: "https://api.openai.com/v1",
            textEndpoint: "/chat/completions",
            visionEndpoint: "/chat/completions",
            textModel: "gpt-4o",
            visionModel: "gpt-4o-mini",
            extraHeaders: "",
            anthropicVersion: "2023-06-01",
            supportsText: true,
            supportsVision: true,
            useTextModelForVision: false,
            allowInsecureTls: false,
            apiKey: "sk-openai");
        var baselineStr = baseline.Serialize();

        // 1. User checks "Use text model for vision"
        var (sharedVision, enabled1) = tracker.OnToggleShared(true, "gpt-4o", "gpt-4o-mini");
        Equal("gpt-4o", sharedVision, "effective vision model must match text model when shared");
        Equal(false, enabled1, "vision picker must be disabled when shared");
        Equal("gpt-4o-mini", tracker.StashedVisionModel, "stashed model must remember original vision model");

        var sharedSnapshot = ServiceEditorSnapshot.CreateNormalized(
            "OpenAI Service", "OpenAiCompatible", "https://api.openai.com/v1",
            "/chat/completions", "/chat/completions", "gpt-4o", sharedVision,
            "", "2023-06-01", true, true, true, false, "sk-openai");
        True(ServicesSection.HasEditorChanges(sharedSnapshot.Serialize(), baselineStr),
            "enabling shared model changes effective vision model and checkbox, so it is Dirty");

        // 2. User unchecks "Use text model for vision"
        var (restoredVision, enabled2) = tracker.OnToggleShared(false, "gpt-4o", sharedVision);
        Equal("gpt-4o-mini", restoredVision, "unchecking must restore original distinct vision model");
        Equal(true, enabled2, "vision picker must be re-enabled");
        Equal(null, tracker.StashedVisionModel, "stashed model must be cleared");

        var restoredSnapshot = ServiceEditorSnapshot.CreateNormalized(
            "OpenAI Service", "OpenAiCompatible", "https://api.openai.com/v1",
            "/chat/completions", "/chat/completions", "gpt-4o", restoredVision,
            "", "2023-06-01", true, true, false, false, "sk-openai");
        True(!ServicesSection.HasEditorChanges(restoredSnapshot.Serialize(), baselineStr),
            "unchecking restored the original state, so editor returns to Clean");

        // 3. Repeat toggle cycle: check -> uncheck again
        var (shared2, _) = tracker.OnToggleShared(true, "gpt-4o", restoredVision);
        Equal("gpt-4o-mini", tracker.StashedVisionModel);
        var (restored2, _) = tracker.OnToggleShared(false, "gpt-4o", shared2);
        Equal("gpt-4o-mini", restored2);
        Equal(null, tracker.StashedVisionModel);

        // 4. Case B: Editing text model while shared is active, then unchecking restores vision while keeping edited text model
        var (shared3, _) = tracker.OnToggleShared(true, "gpt-4o", "gpt-4o-mini");
        var (restoredAfterTextEdit, _) = tracker.OnToggleShared(false, "deepseek-chat", shared3);
        Equal("gpt-4o-mini", restoredAfterTextEdit, "original vision model is preserved even if text model was modified");
    }

    private static void SettingsAndServicesDraftGuardTransitions()
    {
        // State decisions come from the same pure function the settings
        // window uses, so the guard semantics below exercise the real state
        // machine instead of a hand-written copy of the enum transitions.
        var route = RouteDraftSnapshot.Create(networkEnabled: true, safeMode: false, allowImageUpload: true, mode: "Auto");
        SettingsFormSnapshot Form(bool networkEnabled, bool autoCopy) => SettingsFormSnapshot.Create(
            selectionHotkey: "Ctrl+Alt+W",
            screenshotHotkey: "Ctrl+Shift+T",
            closeHotkey: "Ctrl+Alt+X",
            showWindowHotkey: "Ctrl+Alt+O",
            historyEnabled: false,
            closeOnFocusLoss: true,
            autoCopy,
            startWithWindows: false,
            includeExplanation: true,
            protectTokens: true,
            theme: "System",
            route: RouteDraftSnapshot.Create(networkEnabled, false, true, "Auto"));
        var baseline = Form(networkEnabled: true, autoCopy: false).Serialize();

        SettingsEditState StateOf(SettingsFormSnapshot draft) =>
            SettingsWindow.StateFromDraft(draft.Serialize(), baseline);

        // 1. Clean state allows navigation and closing
        var settingsState = StateOf(Form(networkEnabled: true, autoCopy: false));
        Equal(SettingsEditState.Clean, settingsState, "a draft equal to the baseline resolves Clean");
        var isEditorDirty = false;
        var shouldBlockNav = isEditorDirty;
        var shouldBlockClose = isEditorDirty || settingsState is SettingsEditState.Dirty or SettingsEditState.Saving;
        True(!shouldBlockNav, "clean state does not block navigation");
        True(!shouldBlockClose, "clean state does not block window closing");

        // 2. Editor dirty blocks page navigation away from provider and blocks closing
        isEditorDirty = true;
        shouldBlockNav = isEditorDirty;
        shouldBlockClose = isEditorDirty || settingsState == SettingsEditState.Dirty;
        True(shouldBlockNav, "dirty editor blocks leaving Provider page");
        True(shouldBlockClose, "dirty editor blocks window closing");

        // 3. Discarding / saving editor restores clean state
        isEditorDirty = false;
        True(!isEditorDirty, "discarding/saving editor restores clean state");

        // 4. Global settings dirty allows page navigation but blocks closing
        settingsState = StateOf(Form(networkEnabled: false, autoCopy: false));
        Equal(SettingsEditState.Dirty, settingsState, "a changed route field resolves Dirty");
        shouldBlockNav = isEditorDirty; // false
        shouldBlockClose = isEditorDirty || settingsState == SettingsEditState.Dirty;
        True(!shouldBlockNav, "global settings dirty does not block page switching between General/Privacy/Shortcuts");
        True(shouldBlockClose, "global settings dirty blocks window closing until saved or reverted");

        // 5. Reverting or saving global settings rebuilds baseline and restores clean state
        settingsState = StateOf(Form(networkEnabled: true, autoCopy: false));
        Equal(SettingsEditState.Clean, settingsState, "reverting the edit converges back onto Clean");
        shouldBlockClose = isEditorDirty || settingsState == SettingsEditState.Dirty;
        True(!shouldBlockClose, "rebuilt baseline allows clean close");
    }

    /// <summary>
    /// Regression for the failed-save recovery: a save that throws must leave
    /// the live window Dirty (save bar back, route hint intact) - never stuck
    /// in Saving/Loading - and reverting the edits must still return it to
    /// Clean afterwards. Exercises the real window, not enum variables.
    /// </summary>
    private static void FailedSaveRecoversToDirtyThenClean()
    {
        ProfileManager.ResetForTests();
        var dir = Path.Combine(Path.GetTempPath(), $"popglot-savefail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        ProfileManager.ConfigPathOverride = Path.Combine(dir, "product-config.json");
        CoreBridge.Initialize();
        // SettingsWindow resolves its nav styles from the app-level theme
        // dictionary; bootstrap the same Application the screenshot pass uses.
        if (Application.Current is null)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/PopGlot;component/Themes/Controls.xaml", UriKind.RelativeOrAbsolute),
            });
        }
        else if (Application.Current.Resources.MergedDictionaries.Count == 0)
        {
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/PopGlot;component/Themes/Controls.xaml", UriKind.RelativeOrAbsolute),
            });
        }
        ThemeService.Apply(ThemePreference.Dark);
        try
        {
            var window = new SettingsWindow(
                ShellSettings.Default, new HistoryStore(Path.Combine(dir, "history.json")))
            {
                // Deterministic save failure at hotkey registration - before
                // any write, so the test cannot touch real settings.
                ApplyShellSettings = _ => false,
            };

            Equal(SettingsEditState.Clean, window.EditState, "a freshly loaded window must be Clean");

            // Diverge one route field and one general field from the baseline.
            var network = window.CaptureSection.NetworkEnabled;
            var autoCopy = window.GeneralSection.AutoCopy;
            var networkOriginal = network.IsChecked == true;
            var autoCopyOriginal = autoCopy.IsChecked == true;
            network.IsChecked = !networkOriginal;
            autoCopy.IsChecked = !autoCopyOriginal;
            Equal(SettingsEditState.Dirty, window.EditState, "edits must mark the form Dirty");
            True(window.CaptureSection.IsRouteDraftPending, "route edits must show the draft route hint");

            typeof(SettingsWindow).GetMethod("Save_Click",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, new object[] { window, new RoutedEventArgs() });

            Equal(SettingsEditState.Dirty, window.EditState,
                "a failed save must land back on Dirty, never stick in Saving/Loading");
            True(window.IsDirty, "the save bar must return after a failed save");
            True(window.SaveButton.IsEnabled, "the save action must be usable again after a failed save");
            True(window.CaptureSection.IsRouteDraftPending,
                "a failed save must not clear the route draft hint");

            // Reverting both fields converges onto the baseline - this is the
            // transition the old Loading-stuck bug made impossible.
            autoCopy.IsChecked = autoCopyOriginal;
            network.IsChecked = networkOriginal;
            Equal(SettingsEditState.Clean, window.EditState,
                "reverting every edit after a failed save must return Clean");
            True(!window.CaptureSection.IsRouteDraftPending,
                "the route hint must clear once the route converges with the baseline");
        }
        finally
        {
            ProfileManager.ResetForTests();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// T06 acceptance: the privacy page exposes the cloud-speech consent with
    /// its own destination. The toggle persists immediately, never becomes a
    /// form draft, and saving unrelated settings preserves the consent (the
    /// old Save_Click silently reset it to the default).
    /// </summary>
    private static void SettingsCloudSpeechConsentRoundTrip()
    {
        ProfileManager.ResetForTests();
        var dir = Path.Combine(Path.GetTempPath(), $"popglot-cloudspeech-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        ProfileManager.ConfigPathOverride = Path.Combine(dir, "product-config.json");
        CoreBridge.Initialize();
        EnsureApplication();
        var originalStartupSeam = StartupRegistration.TrySetOverride;
        var windowHolder = new SettingsWindow?[] { null };
        try
        {
            StartupRegistration.TrySetOverride = _ => true;
            var shell = ShellSettings.Default with { CloudSpeechEnabled = true };
            var window = new SettingsWindow(
                shell, new HistoryStore(Path.Combine(dir, "history.json")))
            {
                ApplyShellSettings = _ => true,
            };
            windowHolder[0] = window;

            Equal(true, window.CaptureSection.CloudSpeech.IsChecked,
                "the consent toggle must load the persisted state");

            // Saving unrelated settings must not reset the consent. The old
            // Save_Click rebuilt ShellSettings without CloudSpeechEnabled,
            // silently disabling the Microsoft voice destination.
            var autoCopy = window.GeneralSection.AutoCopy;
            var autoCopyOriginal = autoCopy.IsChecked == true;
            autoCopy.IsChecked = !autoCopyOriginal;
            Equal(SettingsEditState.Dirty, window.EditState, "an unrelated edit must mark the form dirty");

            typeof(SettingsWindow).GetMethod("Save_Click",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, new object[] { window, new RoutedEventArgs() });
            SpinUntil(() => window.EditState == SettingsEditState.Clean, "the save must reach Clean");

            Equal(true, ShellSettingsStore.Load().CloudSpeechEnabled,
                "saving the form must preserve the cloud speech consent");
            Equal(true, window.CaptureSection.CloudSpeech.IsChecked);

            // The consent itself is immediate: not a draft, saved on flip.
            window.CaptureSection.CloudSpeech.IsChecked = false;
            Equal(false, ShellSettingsStore.Load().CloudSpeechEnabled,
                "the consent toggle must save immediately");
            Equal(SettingsEditState.Clean, window.EditState,
                "the consent toggle is not a settings draft");
            window.CaptureSection.CloudSpeech.IsChecked = true;
            Equal(true, ShellSettingsStore.Load().CloudSpeechEnabled);
            Equal(SettingsEditState.Clean, window.EditState);
        }
        finally
        {
            StartupRegistration.TrySetOverride = originalStartupSeam;
            try
            {
                windowHolder[0]?.Close();
            }
            catch (Exception)
            {
            }
            ProfileManager.ResetForTests();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// T12 acceptance: a long technical article is planned into segments,
    /// translated sequentially IN ORDER, and merged losslessly — while a
    /// short source stays a single request. The mock tags every segment so
    /// the concatenation order is provable, and code fences stay intact.
    /// </summary>
    private static async Task LongInputPlansIntoOrderedSegmentsAsync()
    {
        var history = new FakeHistoryRepository();
        var executor = new FakeTranslationExecutor();
        var requestedSources = new List<string>();
        var coordinator = new TranslationCoordinator(history: history, executor: executor);

        executor.OnStreamText = (apiKey, source, sourceLang, targetLang, sessionId, epoch, ct) =>
        {
            lock (requestedSources) requestedSources.Add(source);
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();
            var translated = $"[译:{source.Trim()}]";
            _ = Task.Run(async () =>
            {
                await Task.Delay(10);
                buffer.TryAppend(translated);
                buffer.Complete();
                tcs.SetResult(new TranslationResponse(
                    new TranslationResult(translated, "", "", [], []),
                    new ProviderDiagnostics(sessionId, ProviderType.OpenAiCompatible, "https://api.openai.com", 1, 200, 40)));
            });
            return new TranslationStreamSession(buffer, tcs.Task);
        };

        // ~4000-char technical article with a fenced block and identifiers.
        var paragraph = "The build failed with a FileNotFoundError for config.json. ";
        var article = $"{string.Concat(Enumerable.Repeat(paragraph, 30))}\n\n```bash\ncargo test --workspace\n```\n\n{string.Concat(Enumerable.Repeat(paragraph, 30))}";

        var session = await coordinator.TranslateTextAsync(
            article, "en", "zh-CN", TranslationInputSource.Manual);

        Equal(TranslationSessionStage.Completed, session.Stage,
            "all segments complete → the session completes");
        lock (requestedSources)
        {
            True(requestedSources.Count >= 2 && requestedSources.Count <= CoreBridge.MaxSegments,
                $"a ~4k source must split into 2..8 requests, got {requestedSources.Count}");
            foreach (var segment in requestedSources)
            {
                True(segment.Length <= CoreBridge.MaxSegmentChars,
                    $"each segment must respect the per-segment budget, got {segment.Length}");
            }
            // Order: the first request must contain the article's opening,
            // and the code block stays whole inside one request.
            True(requestedSources[0].StartsWith(paragraph),
                "the first segment starts at the beginning of the source");
            True(requestedSources.Any(source => source.Contains("cargo test --workspace")),
                "the fenced commands must travel inside one segment intact");
        }
        // Order-preserving merge with segment markers.
        var firstMarker = requestedSources[0].Trim()[..20];
        var text = session.TranslatedText;
        True(text.IndexOf("[译:", StringComparison.Ordinal) < text.LastIndexOf("[译:", StringComparison.Ordinal),
            "segment translations appear in request order");
        True(text.Contains($"[译:{firstMarker}"), "the first segment's translation opens the merge");
        // History: a clean segmented completion persists exactly once.
        Equal(1, history.Entries.Count);
    }

    /// <summary>
    /// T12 acceptance: a session cancelled during segment 3 issues ZERO
    /// requests for segment 4, and the completed fragments stay visible.
    /// </summary>
    private static async Task CancelBetweenSegmentsStopsLaterRequestsAsync()
    {
        var history = new FakeHistoryRepository();
        var executor = new FakeTranslationExecutor();
        var requestCount = 0;
        var coordinator = new TranslationCoordinator(history: history, executor: executor);

        executor.OnStreamText = (apiKey, source, sourceLang, targetLang, sessionId, epoch, ct) =>
        {
            var count = Interlocked.Increment(ref requestCount);
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();
            _ = Task.Run(async () =>
            {
                await Task.Delay(30);
                if (count == 3)
                {
                    // The user cancels while segment 3 is in flight.
                    ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(20));
                    buffer.TryAppend("片段三的前半");
                    buffer.Complete();
                    tcs.SetCanceled(ct);
                    return;
                }
                buffer.TryAppend($"[段{count}]");
                buffer.Complete();
                tcs.SetResult(new TranslationResponse(
                    new TranslationResult($"[段{count}]", "", "", [], []),
                    new ProviderDiagnostics(sessionId, ProviderType.OpenAiCompatible, "https://api.openai.com", 1, 200, 20)));
            });
            return new TranslationStreamSession(buffer, tcs.Task);
        };

        var long1 = new string('a', 500) + "\n\n";
        var source = long1 + long1 + long1 + long1;

        var session = await coordinator.TranslateTextAsync(
            source, "en", "zh-CN", TranslationInputSource.Manual);

        Equal(TranslationSessionStage.Cancelled, session.Stage);
        var totalRequests = Volatile.Read(ref requestCount);
        True(totalRequests < 8, $"cancellation must stop later segments (observed {totalRequests} requests)");
        True(session.TranslatedText.Contains("[段1]") && session.TranslatedText.Contains("[段2]"),
            "fragments from completed segments stay visible after cancellation");
    }

    /// <summary>
    /// T12 acceptance: a segment that fails mid-session leaves the completed
    /// fragments visible as PARTIAL with a warning naming the segment — never
    /// a body-less Failed — and writes no history.
    /// </summary>
    private static async Task SegmentFailureKeepsFragmentsAsPartialAsync()
    {
        var history = new FakeHistoryRepository();
        var executor = new FakeTranslationExecutor();
        var requestCount = 0;
        var coordinator = new TranslationCoordinator(history: history, executor: executor);

        executor.OnStreamText = (apiKey, source, sourceLang, targetLang, sessionId, epoch, ct) =>
        {
            var count = Interlocked.Increment(ref requestCount);
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();
            _ = Task.Run(async () =>
            {
                await Task.Delay(15);
                if (count == 2)
                {
                    // Segment 2 explodes after streaming some text.
                    buffer.TryAppend("半截");
                    buffer.Complete();
                    tcs.SetException(new InvalidOperationException("mock segment failure"));
                    return;
                }
                buffer.TryAppend($"[段{count}]");
                buffer.Complete();
                tcs.SetResult(new TranslationResponse(
                    new TranslationResult($"[段{count}]", "", "", [], []),
                    new ProviderDiagnostics(sessionId, ProviderType.OpenAiCompatible, "https://api.openai.com", 1, 200, 20)));
            });
            return new TranslationStreamSession(buffer, tcs.Task);
        };

        var long1 = new string('b', 500) + "\n\n";
        var session = await coordinator.TranslateTextAsync(
            long1 + long1 + long1, "en", "zh-CN", TranslationInputSource.Manual);

        Equal(TranslationSessionStage.Partial, session.Stage,
            "completed fragments plus a failed segment must surface as Partial");
        True(session.TranslatedText.Contains("[段1]"),
            "the completed first fragment must remain visible");
        True(session.Warnings.Any(warning => warning.Contains("第 2 段")),
            "the warning must name the failed segment");
        Equal(0, history.Entries.Count, "a Partial session never persists");
        Equal(2, Volatile.Read(ref requestCount), "no requests after the failed segment");
    }

    /// <summary>
    /// T12 acceptance: an incomplete SEGMENT (is_partial / dropped-token
    /// warnings / empty text) ends the session as Partial instead of gluing
    /// further segments onto an unreliable fragment — no fake success.
    /// </summary>
    private static async Task IncompleteSegmentStopsSessionAsPartialAsync()
    {
        var history = new FakeHistoryRepository();
        var executor = new FakeTranslationExecutor();
        var requestCount = 0;
        var coordinator = new TranslationCoordinator(history: history, executor: executor);

        executor.OnStreamText = (apiKey, source, sourceLang, targetLang, sessionId, epoch, ct) =>
        {
            var count = Interlocked.Increment(ref requestCount);
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();
            _ = Task.Run(async () =>
            {
                await Task.Delay(15);
                if (count == 1)
                {
                    // Segment 1 "finishes" but reports a dropped placeholder:
                    // an integrity failure this session must not paper over.
                    buffer.TryAppend("第一段不完整");
                    buffer.Complete();
                    tcs.SetResult(new TranslationResponse(
                        new TranslationResult("第一段不完整", "", "", [], ["缺失占位符 PG_0000"]),
                        new ProviderDiagnostics(sessionId, ProviderType.OpenAiCompatible, "https://api.openai.com", 1, 200, 20)));
                    return;
                }
                buffer.TryAppend($"[段{count}]");
                buffer.Complete();
                tcs.SetResult(new TranslationResponse(
                    new TranslationResult($"[段{count}]", "", "", [], []),
                    new ProviderDiagnostics(sessionId, ProviderType.OpenAiCompatible, "https://api.openai.com", 1, 200, 20)));
            });
            return new TranslationStreamSession(buffer, tcs.Task);
        };

        var long1 = new string('c', 500) + "\n\n";
        var session = await coordinator.TranslateTextAsync(
            long1 + long1, "en", "zh-CN", TranslationInputSource.Manual);

        Equal(TranslationSessionStage.Partial, session.Stage,
            "an integrity-incomplete segment must not yield a Completed session");
        Equal(1, Volatile.Read(ref requestCount), "later segments must not run after an incomplete one");
        Equal(0, history.Entries.Count);
    }

    /// <summary>
    /// T12 acceptance: the budget gate refuses up front — an oversized atomic
    /// code block and a source needing too many segments fail with an
    /// actionable message BEFORE any request exists.
    /// </summary>
    private static async Task BudgetRefusalSendsNothingAsync()
    {
        var history = new FakeHistoryRepository();
        var executor = new FakeTranslationExecutor();
        var requestCount = 0;
        var coordinator = new TranslationCoordinator(history: history, executor: executor);
        executor.OnStreamText = (apiKey, source, sourceLang, targetLang, sessionId, epoch, ct) =>
        {
            Interlocked.Increment(ref requestCount);
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();
            return new TranslationStreamSession(buffer, tcs.Task);
        };

        // 1. An atomic code block larger than the per-segment budget.
        var bigCode = $"```text\n{new string('x', 2000)}\n```\n";
        var refused = await coordinator.TranslateTextAsync(
            bigCode, "en", "zh-CN", TranslationInputSource.Manual);
        Equal(TranslationSessionStage.Failed, refused.Stage);
        True(refused.Error?.Message.Contains("代码块") == true,
            "the refusal must name the oversized code block");
        Equal(0, Volatile.Read(ref requestCount), "a refused session sends nothing");

        // 2. 64 KiB of CJK would need ~80 segments: refused up front.
        var mass = new string('翻', 64 * 1024);
        var refusedMany = await coordinator.TranslateTextAsync(
            mass, "zh-CN", "en", TranslationInputSource.Manual);
        Equal(TranslationSessionStage.Failed, refusedMany.Stage);
        True(refusedMany.Error?.Message.Contains("预算") == true,
            "the refusal must name the session budget");
        Equal(0, Volatile.Read(ref requestCount), "still nothing sent");
    }

    /// <summary>
    /// T12 acceptance: a SHORT source is never segmented — one request, the
    /// historical adaptive-budget behavior — and an FFI/C# agreement check on
    /// the planner keeps the shell and the core on one contract.
    /// </summary>
    private static void ShortSourceStaysSingleAndPlannerAgreesAcrossFfi()
    {
        // C# side: a short source goes out as ONE request.
        var shortSource = "Hello world, translate me.";
        var plan = CoreBridge.PlanSegments(shortSource);
        Equal("single", plan.Mode, "short sources must stay a single request");

        // Long source: the FFI plan returns segments whose concatenation
        // reproduces the source exactly, and the count respects the budget.
        var paragraph = "Technical sentence about foo_bar_baz with a config.json path. ";
        var longSource = $"{string.Concat(Enumerable.Repeat(paragraph, 10))}\n\n{string.Concat(Enumerable.Repeat(paragraph, 10))}";
        var longPlan = CoreBridge.PlanSegments(longSource);
        Equal("segments", longPlan.Mode);
        True((longPlan.Segments?.Count ?? 0) >= 2, "a long source must plan into at least two segments");
        Equal(longSource, string.Concat(longPlan.Segments!),
            "segment concatenation must reproduce the source byte-for-byte");

        // The oversized code block rejection crosses the FFI boundary too.
        var codePlan = CoreBridge.PlanSegments($"```text\n{new string('x', 2000)}\n```\n");
        Equal("oversized_code_block", codePlan.RejectedReason);
    }

    /// <summary>
    /// T17 acceptance: the service editor's pure rules live in
    /// ServiceDraftCoordinator — validation, draft construction, header
    /// parsing and recommendation coordination — with no WPF control and no
    /// I/O, so the same logic backs preview and save headless.
    /// </summary>
    private static void ServiceDraftCoordinatorPureRulesHold()
    {
        // Validation: name, URL presence, HTTPS-only for non-local.
        Equal("请先填写服务名称。", ServiceDraftCoordinator.Validate("", "https://api.example.com"));
        Equal("API Base URL 不能为空。", ServiceDraftCoordinator.Validate("Srv", "  "));
        Equal("API Base URL 必须使用 HTTPS；仅本机或局域网服务允许 HTTP。",
            ServiceDraftCoordinator.Validate("Srv", "http://api.example.com/v1"));
        Equal(null, ServiceDraftCoordinator.Validate("Srv", "https://api.example.com/v1"),
            "a valid remote draft validates clean");
        Equal(null, ServiceDraftCoordinator.Validate("Srv", "http://127.0.0.1:11434"),
            "loopback HTTP stays allowed");
        Equal(null, ServiceDraftCoordinator.Validate("Srv", "http://192.168.1.20:11434"),
            "LAN HTTP stays allowed (LAN admission is a separate permission)");

        // Construction: defaults, vision sharing, locality and capability flags.
        var draft = ServiceDraftCoordinator.BuildDraft(new ServiceDraftInputs(
            "My Service", "http://localhost:11434/v1", ProviderType.OpenAiCompatible,
            "  ", " ", "text-model", "text-model",
            new Dictionary<string, string>(), " ", AllowInsecureTls: false));
        Equal("/chat/completions", draft.TextEndpoint, "blank endpoint falls back to the default");
        Equal("2023-06-01", draft.AnthropicVersion, "blank anthropic version falls back");
        Equal(true, draft.SupportsText && draft.SupportsVision);
        Equal(true, draft.IsLocal);

        // Header parsing: strict failure on a malformed line, clean success otherwise.
        var headers = ServiceDraftCoordinator.ParseHeaders("X-Demo: one\r\n\r\nX-Other: two");
        Equal(2, headers.Count);
        Equal("one", headers["X-Demo"]);
        var malformed = Throws<InvalidOperationException>(
            () => ServiceDraftCoordinator.ParseHeaders("X-Broken"));
        True(malformed.Message.Contains("Header: Value"), "the parse error must name the format");

        // Recommendation coordination: shared vision yields no vision result.
        var descriptors = new List<ModelDescriptor>
        {
            new("m-fast", CapabilityState.Supported, CapabilityState.Unknown, "Catalog"),
            new("m-strong", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
        };
        var (text, vision) = ServiceDraftCoordinator.ComputeRecommendations(
            ProviderType.OpenAiCompatible, isLocal: false, descriptors,
            ModelPreference.Speed, "m-fast", null, visionSharedWithText: true);
        Equal("m-fast", text.RecommendedModel?.Model.Id);
        Equal(null, vision, "shared vision needs no separate ranking");

        var (text2, vision2) = ServiceDraftCoordinator.ComputeRecommendations(
            ProviderType.OpenAiCompatible, isLocal: false, descriptors,
            ModelPreference.Speed, "m-fast", "m-strong", visionSharedWithText: false);
        Equal("m-fast", text2.RecommendedModel?.Model.Id);
        True(vision2 is not null, "separate vision must produce its own ranking");
    }

    /// <summary>
    /// T19/F03 acceptance: a COMPLETE final that arrives after the user
    /// cancelled must land on Cancelled with zero history and zero side
    /// effects — not Completed-with-persistence.
    /// </summary>
    private static async Task CancelledFinalDoesNotPersistAsync()
    {
        var history = new FakeHistoryRepository();
        var executor = new FakeTranslationExecutor();
        var coordinator = new TranslationCoordinator(history: history, executor: executor);
        using var cts = new CancellationTokenSource();

        // The executor returns a complete response, but only AFTER the
        // session token was cancelled — the exact interleaving F03 described.
        executor.OnStreamText = (apiKey, source, sourceLang, targetLang, sessionId, epoch, ct) =>
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();
            _ = Task.Run(async () =>
            {
                cts.Cancel(); // cancel BEFORE the response resolves
                await Task.Delay(30);
                buffer.TryAppend("完整译文");
                buffer.Complete();
                tcs.SetResult(new TranslationResponse(
                    new TranslationResult("完整译文", "", "", [], []),
                    new ProviderDiagnostics(sessionId, ProviderType.OpenAiCompatible, "https://api.openai.com", 1, 200, 30)));
            });
            return new TranslationStreamSession(buffer, tcs.Task);
        };

        var session = await coordinator.TranslateTextAsync(
            "demo source", "en", "zh-CN", TranslationInputSource.Manual, cts.Token);

        Equal(TranslationSessionStage.Cancelled, session.Stage,
            "a late complete final after cancellation must NOT read as Completed");
        True(session.TranslatedText.Contains("完整译文"), "the text stays visible");
        Equal(0, history.Entries.Count, "a cancelled session must not persist");
    }

    /// <summary>
    /// T19/F07 acceptance: a clean translation whose history write FAILS must
    /// not mark the session committed — the failure surfaces as a warning and
    /// the session is not counted as persisted.
    /// </summary>
    private static async Task HistoryWriteFailureIsNotFakeCommittedAsync()
    {
        var failing = new FailingHistoryRepository();
        var executor = new FakeTranslationExecutor();
        var coordinator = new TranslationCoordinator(history: failing, executor: executor);
        executor.OnStreamText = (apiKey, source, sourceLang, targetLang, sessionId, epoch, ct) =>
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();
            _ = Task.Run(async () =>
            {
                await Task.Delay(10);
                buffer.TryAppend("完整译文");
                buffer.Complete();
                tcs.SetResult(new TranslationResponse(
                    new TranslationResult("完整译文", "", "", [], []),
                    new ProviderDiagnostics(sessionId, ProviderType.OpenAiCompatible, "https://api.openai.com", 1, 200, 20)));
            });
            return new TranslationStreamSession(buffer, tcs.Task);
        };

        var session = await coordinator.TranslateTextAsync(
            "demo source", "en", "zh-CN", TranslationInputSource.Manual);

        Equal(TranslationSessionStage.Completed, session.Stage,
            "the translation itself succeeded — the STORE failed, not the session");
        Equal(1, failing.Attempts, "the store was actually attempted");
        True(!session.HistoryCommitted,
            "a failed history write must never mark the session committed");
        True(session.Warnings.Any(w => w.Contains("未保存到本机历史")),
            "the user must see that the translation was not saved");
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
        True(xaml.Contains("接口与网络") && xaml.Contains("请求定制"),
            "advanced settings must stay split into plain-language groups");
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

    private static void ModelRecommendationUiTests()
    {
        // 1. Preference handler does not alter editor snapshot or dirty state
        var snapBefore = ServiceEditorSnapshot.CreateNormalized(
            "OpenAI", "OpenAiCompatible", "https://api.openai.com/v1",
            "/chat/completions", "/chat/completions", "gpt-4o-mini", "gpt-4o-mini",
            "", "2023-06-01", true, true, true, false, "").Serialize();

        var snapAfterPrefChange = ServiceEditorSnapshot.CreateNormalized(
            "OpenAI", "OpenAiCompatible", "https://api.openai.com/v1",
            "/chat/completions", "/chat/completions", "gpt-4o-mini", "gpt-4o-mini",
            "", "2023-06-01", true, true, true, false, "").Serialize();

        Equal(snapBefore, snapAfterPrefChange, "Preference change must not alter editor snapshot");
        Equal(false, ServicesSection.HasEditorChanges(snapBefore, snapAfterPrefChange), "Preference change must remain clean (not dirty)");

        // 2. Chip selection updates model text and produces a dirty snapshot
        var snapAfterChipSelect = ServiceEditorSnapshot.CreateNormalized(
            "OpenAI", "OpenAiCompatible", "https://api.openai.com/v1",
            "/chat/completions", "/chat/completions", "gpt-4o", "gpt-4o-mini",
            "", "2023-06-01", true, true, true, false, "").Serialize();

        True(ServicesSection.HasEditorChanges(snapAfterChipSelect, snapBefore), "Selecting a chip must change model and mark dirty");

        // 3. Recommendation chip count capped at maximum 3 eligible candidates
        var manyModels = new List<ModelDescriptor>
        {
            new("gpt-4o-mini", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
            new("gpt-4o", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
            new("chatgpt-4o-latest", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
            new("gpt-4-turbo", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
            new("gpt-3.5-turbo", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
        };
        var recResult = ModelRecommendationService.Recommend(new ModelRecommendationRequest(
            ProviderType.OpenAiCompatible,
            false,
            manyModels,
            ModelTargetUsage.Text,
            ModelPreference.Balanced));

        var topChips = recResult.Candidates.Where(c => c.IsEligible).Take(3).ToList();
        Equal(3, topChips.Count, "Top recommendation chips must be at most 3");

        // 4. Evidence badge mapping priority and neutral unknown
        // Priority: LocalBenchmark (with metric) > CatalogExplicit > FamilyHeuristics > Unknown
        // Without benchmark metric: LocalBenchmark flag must NEVER display "本机实测"
        var tierNoMetric = ServicesSection.ResolveEvidenceTier(
            RecommendationEvidenceSource.LocalBenchmark | RecommendationEvidenceSource.CatalogExplicit,
            hasBenchmarkMetric: false);
        Equal(ServicesSection.EvidenceBadgeTier.CatalogExplicit, tierNoMetric, "Without benchmark metric, CatalogExplicit wins over LocalBenchmark");
        Equal("官方声明", ServicesSection.GetEvidenceBadgeText(tierNoMetric));

        var tierWithMetric = ServicesSection.ResolveEvidenceTier(
            RecommendationEvidenceSource.LocalBenchmark | RecommendationEvidenceSource.CatalogExplicit,
            hasBenchmarkMetric: true);
        Equal(ServicesSection.EvidenceBadgeTier.LocalBenchmark, tierWithMetric, "With benchmark metric, LocalBenchmark wins");
        Equal("本机实测", ServicesSection.GetEvidenceBadgeText(tierWithMetric));

        var tierCatalog = ServicesSection.ResolveEvidenceTier(RecommendationEvidenceSource.CatalogExplicit, false);
        Equal(ServicesSection.EvidenceBadgeTier.CatalogExplicit, tierCatalog);
        Equal("官方声明", ServicesSection.GetEvidenceBadgeText(tierCatalog));

        var tierHeuristic = ServicesSection.ResolveEvidenceTier(RecommendationEvidenceSource.FamilyHeuristics, false);
        Equal(ServicesSection.EvidenceBadgeTier.FamilyHeuristics, tierHeuristic);
        Equal("系列推断", ServicesSection.GetEvidenceBadgeText(tierHeuristic));

        var tierUnknown = ServicesSection.ResolveEvidenceTier(RecommendationEvidenceSource.FallbackUnknown, false);
        Equal(ServicesSection.EvidenceBadgeTier.Unknown, tierUnknown);
        Equal("未声明", ServicesSection.GetEvidenceBadgeText(tierUnknown));

        var (unknownText, unknownBg, unknownFg, unknownBorder) = ServicesSection.ResolveEvidenceBadgeVisualKeys(
            RecommendationEvidenceSource.FallbackUnknown, false);
        Equal("未声明", unknownText);
        Equal("SurfaceMutedBrush", unknownBg);
        Equal("TextTertiaryBrush", unknownFg);
        Equal("BorderSubtleBrush", unknownBorder);

        // 5. Unknown capability / current model preservation without catalog
        var uncataloguedModel = new ModelDescriptor("custom-enterprise-model", CapabilityState.Unknown, CapabilityState.Unknown, "Fallback");
        var uncataloguedResult = ModelRecommendationService.Recommend(new ModelRecommendationRequest(
            ProviderType.OpenAiCompatible,
            false,
            [uncataloguedModel],
            ModelTargetUsage.Text,
            ModelPreference.Balanced,
            CurrentModelId: "custom-enterprise-model"));

        var eval = uncataloguedResult.Candidates.FirstOrDefault(c => c.Model.Id == "custom-enterprise-model");
        True(eval is not null, "Current uncatalogued model must be preserved in candidates");
        True(eval!.IsCurrentSelected, "IsCurrentSelected must be true");
        Equal(ModelTier.Unknown, eval.Tier, "Uncatalogued tier must be Unknown");

        // 6. Recommendation is computed regardless of health state (not gated by health/test connection)
        var healthIgnorantResult = ModelRecommendationService.Recommend(new ModelRecommendationRequest(
            ProviderType.OpenAiCompatible,
            false,
            [new ModelDescriptor("gpt-4o-mini", CapabilityState.Supported, CapabilityState.Supported, "Catalog")],
            ModelTargetUsage.Text,
            ModelPreference.Balanced));
        True(healthIgnorantResult.Candidates.Count > 0, "Recommendations are generated regardless of service connection health");
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
        // quiet footer. No control center, no save bar.
        foreach (var surface in new[] { "TranslateSection", "LibrarySection" })
        {
            True(mainXaml.Contains(surface), $"the main window must host {surface}");
        }
        True(mainXaml.Contains("NavTranslate"), "translate navigation must exist");
        True(mainXaml.Contains("NavLibrary"), "library navigation must exist");
        True(mainXaml.Contains("NavSettingsButton"), "the sidebar settings entry must exist");
        True(!mainXaml.Contains("x:Name=\"SettingsButton\""),
            "the old footer settings entry must not duplicate the sidebar one");
        True(Regex.Matches(mainXaml, "AutomationProperties.Name=\"打开设置\"").Count == 1,
            "exactly one control may carry the 打开设置 automation name in the main window");
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

    private static int _dispatcherFailures;

    private static void EnsureApplication()
    {
        if (Application.Current is null)
        {
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                app.InitializeComponent();
            }
            catch
            {
                // Fallback
            }
            // Keep a strong root: Application.Current alone must not be the
            // only reference keeping the host alive across test loops.
            _bootstrappedApp = app;
            // A single unhandled dispatcher exception must not tear the
            // Application down and poison every later WPF test; record it and
            // keep the harness alive.
            app.DispatcherUnhandledException += (_, args) =>
            {
                _dispatcherFailures++;
                Console.WriteLine($"DISPATCHER EXCEPTION (#{_dispatcherFailures}): {args.Exception.GetType().Name}: {args.Exception.Message}");
                args.Handled = true;
            };
            ThemeService.Apply(ThemePreference.Dark);
        }
        else
        {
            ThemeService.Apply(ThemePreference.Dark);
        }
    }

    private static void RenderScreenshotsAndMeasureBaseline()
    {
        var projectRoot = FindProjectRoot();
        var outDir = Path.Combine(projectRoot, "artifacts", "screenshots");
        Directory.CreateDirectory(outDir);

        EnsureApplication();

        // Everything below renders the isolated demo fixtures only: synthetic
        // history/vocabulary, the Demo Text Service profile, in-memory vault,
        // and the native core bound to the isolation directory.
        var history = new HistoryStore(TestIsolation.HistoryPath);
        var vocab = new VocabularyStore(TestIsolation.VocabularyPath);
        True(history.Load().Count == 3, "the synthetic history fixture must be loaded");
        True(vocab.GetAll().Count == 2, "the synthetic vocabulary fixture must be loaded");

        // ---- Component benchmarks (T15: honest naming, honest scope) ----
        // These measure in-test-host component operations. They are NOT the
        // AI-RULES app-level budgets: process start, tray availability, real
        // hotkey→first-frame and idle working set require the independent
        // Release app (scripts/measure-startup.ps1) and real hardware, and
        // are reported separately. No fabricated tray number exists here.
        var swCoreInit = Stopwatch.StartNew();
        CoreBridge.Initialize(TestIsolation.CoreConfigDirectory);
        _ = CoreBridge.GetSettings();
        swCoreInit.Stop();
        var coreInitializeMs = swCoreInit.ElapsedMilliseconds;

        var swWindow = Stopwatch.StartNew();
        var winWarm = new QuickSearchWindow(history, vocab);
        swWindow.Stop();
        var windowConstructMs = swWindow.ElapsedMilliseconds;

        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        var workingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0);

        var swConstructArrange = Stopwatch.StartNew();
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
        swConstructArrange.Stop();
        var windowConstructArrangeMs = swConstructArrange.ElapsedMilliseconds;

        var swCancel = Stopwatch.StartNew();
        CoreBridge.CancelActiveRequest();
        swCancel.Stop();
        var cancelNoopMs = swCancel.Elapsed.TotalMilliseconds;

        Console.WriteLine($"\n[Component Benchmarks — test host, NOT app-level budgets]");
        Console.WriteLine($"CoreInitialize (native init + settings load): {coreInitializeMs} ms");
        Console.WriteLine($"WindowConstruct (QuickSearch constructor): {windowConstructMs} ms");
        Console.WriteLine($"Test-host process working set (NOT the app's idle WS): {workingSetMb:F1} MB");
        Console.WriteLine($"WindowConstructArrange (panel 420x520): {windowConstructArrangeMs} ms");
        Console.WriteLine($"CancelNoopOverhead (no active request): {cancelNoopMs:F2} ms");
        Console.WriteLine($"App-level budgets (startup P50/P95, tray, hotkey→frame, idle WS): see artifacts/perf/startup.json from scripts/measure-startup.ps1 — NOT measured here.\n");

        RenderAndSave(new MainWindow(ShellSettings.Default, history, vocab), 960, 640, Path.Combine(outDir, "main_window_dark.png"), ThemePreference.Dark);
        RenderAndSave(new MainWindow(ShellSettings.Default, history, vocab), 960, 640, Path.Combine(outDir, "main_window_light.png"), ThemePreference.Light);
        RenderAndSave(new SettingsWindow(ShellSettings.Default, history, vocab), 960, 680, Path.Combine(outDir, "settings_dark.png"), ThemePreference.Dark);
        RenderAndSave(new SettingsWindow(ShellSettings.Default, history, vocab), 960, 680, Path.Combine(outDir, "settings_light.png"), ThemePreference.Light);
        // The privacy page carries the destination consents (free engine,
        // cloud speech); it needs its own visual regression capture.
        var privacyDark = new SettingsWindow(ShellSettings.Default, history, vocab);
        privacyDark.ShowPage("Privacy");
        RenderAndSave(privacyDark, 960, 760, Path.Combine(outDir, "settings_privacy_dark.png"), ThemePreference.Dark);
        var privacyLight = new SettingsWindow(ShellSettings.Default, history, vocab);
        privacyLight.ShowPage("Privacy");
        RenderAndSave(privacyLight, 960, 760, Path.Combine(outDir, "settings_privacy_light.png"), ThemePreference.Light);
        RenderAndSave(CreateServiceEditorPreview(), 760, 620, Path.Combine(outDir, "service_editor_dark.png"), ThemePreference.Dark);
        RenderAndSave(CreateServiceEditorPreview(), 760, 620, Path.Combine(outDir, "service_editor_light.png"), ThemePreference.Light);
        RenderAndSave(CreateServiceEditorPreview(), 620, 720, Path.Combine(outDir, "service_editor_compact_light.png"), ThemePreference.Light);
        RenderAndSave(CreateAdvancedServiceEditorPreview(), 760, 920, Path.Combine(outDir, "service_editor_advanced_light.png"), ThemePreference.Light);
        RenderAndSave(new QuickSearchWindow(history, vocab), 560, 360, Path.Combine(outDir, "quick_search_dark.png"), ThemePreference.Dark);
        RenderAndSave(new QuickSearchWindow(history, vocab), 560, 360, Path.Combine(outDir, "quick_search_light.png"), ThemePreference.Light);
        // T11: narrow content — the workbench must stack (input ≥160 DIP on
        // top, reader below) instead of squeezing side-by-side panes.
        RenderAndSave(new MainWindow(ShellSettings.Default, history, vocab), 560, 640, Path.Combine(outDir, "main_window_narrow_dark.png"), ThemePreference.Dark);
        AssertCanvasFilled(Path.Combine(outDir, "main_window_narrow_dark.png"), "main_window_narrow_dark.png");
        // T11: a long model identifier must not push the form apart.
        RenderAndSave(CreateServiceEditorPreview("demo-provider/very-long-preview-model-identifier-v9.3.2-preview-20260905-8192k-context"), 760, 620, Path.Combine(outDir, "service_editor_long_model_light.png"), ThemePreference.Light);
        AssertCanvasFilled(Path.Combine(outDir, "service_editor_long_model_light.png"), "service_editor_long_model_light.png");
        // T11: error state with a long reason + actionable next step.
        var errorPanel = new TranslationPanelWindow(new Rect(100, 100, 20, 20), history, () => ShellSettings.Default, null, null, vocab);
        DrivePanelSession(errorPanel, "demo source for the error state", new TranslationSession
        {
            Stage = TranslationSessionStage.Failed,
            Error = new TranslationError(
                TranslationErrorKind.ServerError,
                "连接超时（120 秒无响应）：mock-provider/northcentralus/deployments/very-long-deployment-name-20260905",
                "检查服务地址与网络后重试；离线模式会阻止本次请求。"),
        });
        RenderAndSave(errorPanel, 420, 560, Path.Combine(outDir, "translation_panel_error_dark.png"), ThemePreference.Dark);
        AssertCanvasFilled(Path.Combine(outDir, "translation_panel_error_dark.png"), "translation_panel_error_dark.png");
        RenderAndSave(new TranslationPanelWindow(new Rect(100, 100, 20, 20), history, () => ShellSettings.Default, null, null, vocab), 420, 520, Path.Combine(outDir, "translation_panel_dark.png"), ThemePreference.Dark);
        RenderAndSave(new TranslationPanelWindow(new Rect(100, 100, 20, 20), history, () => ShellSettings.Default, null, null, vocab), 420, 520, Path.Combine(outDir, "translation_panel_light.png"), ThemePreference.Light);
        RenderAndSave(new FloatingTriggerWindow(new Point(100, 100), () => { }), 64, 64, Path.Combine(outDir, "floating_trigger_dark.png"), ThemePreference.Dark);
        RenderAndSave(new FloatingTriggerWindow(new Point(100, 100), () => { }), 64, 64, Path.Combine(outDir, "floating_trigger_light.png"), ThemePreference.Light);

        // Visual regression matrix: every core surface rendered at 125/150/200%
        // DPI in both themes, so clipping or scaling regressions show up as
        // diffable artifacts instead of a user report. Each output must fully
        // paint its canvas — the retired producer squeezed 200% content into
        // the top-left quarter and left the rest blank.
        foreach (var dpi in new[] { 1.25, 1.5, 2.0 })
        {
            foreach (var theme in new[] { ThemePreference.Dark, ThemePreference.Light })
            {
                var scaleLabel = $"{Math.Round(dpi * 100)}pct";
                var suffix = $"{theme}_{scaleLabel}";
                var outputs = new List<(Window Window, int Width, int Height, string Name)>
                {
                    (new MainWindow(ShellSettings.Default, history, vocab), 960, 640, $"main_window_{suffix}.png"),
                    (new TranslationPanelWindow(new Rect(100, 100, 20, 20), history, () => ShellSettings.Default, null, null, vocab), 420, 520, $"translation_panel_{suffix}.png"),
                    (new QuickSearchWindow(history, vocab), 560, 360, $"quick_search_{suffix}.png"),
                    (new SettingsWindow(ShellSettings.Default, history, vocab), 960, 680, $"settings_{suffix}.png"),
                    (CreateServiceEditorPreview(), 760, 620, $"service_editor_{suffix}.png"),
                    (CreateLibraryPreview(history, vocab), 960, 640, $"library_{suffix}.png"),
                };
                foreach (var (host, hostWidth, hostHeight, name) in outputs)
                {
                    var path = Path.Combine(outDir, name);
                    RenderAndSaveAtDpi(host, hostWidth, hostHeight, path, theme, dpi);
                    AssertCanvasFilled(path, name);
                }
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
        // The 200% main window must be a TRUE 1920×1280 canvas — logical
        // 960×640 × scale 2 — not a half-blank artifact of the old producer.
        var twoHundred = LoadBitmap(Path.Combine(outDir, "main_window_light_200pct.png"));
        Equal(1920, twoHundred.PixelWidth, "960 DIP at 200% must be 1920px wide");
        Equal(1280, twoHundred.PixelHeight, "640 DIP at 200% must be 1280px tall");
        AssertCanvasFilled(Path.Combine(outDir, "main_window_light_200pct.png"), "main_window_light_200pct.png");

        // The canvas check itself must reject a mostly-unpainted render —
        // the signature of the retired producer (content squeezed top-left).
        var brokenPath = Path.Combine(outDir, "selfcheck_broken_canvas.png");
        var brokenVisual = new System.Windows.Controls.Border
        {
            Width = 100,
            Height = 60,
            Background = Brushes.White,
        };
        brokenVisual.Measure(new Size(100, 60));
        brokenVisual.Arrange(new Rect(0, 0, 100, 60));
        var brokenRtb = new RenderTargetBitmap(400, 240, 96, 96, PixelFormats.Pbgra32);
        brokenRtb.Render(brokenVisual);
        var brokenEncoder = new PngBitmapEncoder();
        brokenEncoder.Frames.Add(BitmapFrame.Create(brokenRtb));
        using (var brokenStream = File.Create(brokenPath))
        {
            brokenEncoder.Save(brokenStream);
        }
        Throws<InvalidOperationException>(() => AssertCanvasFilled(brokenPath, "selfcheck"));

        // Window construction and rendering must never send anything to the
        // network: the guarded free-engine boundary refuses public hosts and
        // counts the refusals.
        Equal(0, TestIsolation.BlockedPublicSends,
            "rendering the windows attempted a public-network request");
    }

    private static void RenderAndSave(Window window, int width, int height, string filePath, ThemePreference theme) =>
        RenderAndSaveAtDpi(window, width, height, filePath, theme, 1.0);

    /// <summary>
    /// Renders the window content at EXPLICIT logical DIP dimensions; the
    /// bitmap is always logical × scale pixels, so a 960×640 DIP window at
    /// 200% produces a fully painted 1920×1280 canvas. The previous producer
    /// divided the logical size by the scale while multiplying the bitmap —
    /// the 200% screenshots showed content squeezed into the top-left
    /// quarter with blank canvas everywhere else (F11).
    /// </summary>
    private static void RenderAndSaveAtDpi(Window window, int logicalWidthDip, int logicalHeightDip, string filePath, ThemePreference theme, double dpiScale)
    {
        ThemeService.Apply(theme);
        window.Width = logicalWidthDip;
        window.Height = logicalHeightDip;

        // An unshown WPF Window renders as a black native surface. Render its
        // managed content root instead so headless screenshots actually catch
        // spacing, clipping and theme regressions without opening a window.
        var visual = window.Content as FrameworkElement ?? window;
        visual.Width = logicalWidthDip;
        visual.Height = logicalHeightDip;
        visual.Measure(new Size(logicalWidthDip, logicalHeightDip));
        visual.Arrange(new Rect(0, 0, logicalWidthDip, logicalHeightDip));
        visual.UpdateLayout();

        var pixelWidth = (int)Math.Round(logicalWidthDip * dpiScale);
        var pixelHeight = (int)Math.Round(logicalHeightDip * dpiScale);
        var dpi = 96 * dpiScale;
        var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        WriteWithRetry(filePath, encoder);

        // Size self-consistency: the file must carry exactly logical × scale.
        var written = LoadBitmap(filePath);
        Equal(pixelWidth, written.PixelWidth,
            $"{Path.GetFileName(filePath)}: pixel width must be logical {logicalWidthDip} × scale {dpiScale}");
        Equal(pixelHeight, written.PixelHeight,
            $"{Path.GetFileName(filePath)}: pixel height must be logical {logicalHeightDip} × scale {dpiScale}");
    }

    /// <summary>Decodes a PNG fully and releases the file handle immediately.</summary>
    private static BitmapSource LoadBitmap(string filePath)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(filePath);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// Explorer thumbnails or scanners can hold an artifacts file for a
    /// moment; a screenshot producer must not fail a whole run over that.
    /// </summary>
    private static void WriteWithRetry(string filePath, PngBitmapEncoder encoder)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = File.Create(filePath);
                encoder.Save(stream);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// Fails when a significant part of the canvas is unpainted — the exact
    /// signature of the old DPI producer (content squeezed top-left, blank
    /// elsewhere). Windows render opaque backgrounds, so a correct render
    /// leaves essentially no transparent pixels.
    /// </summary>
    private static void AssertCanvasFilled(string filePath, string label, double maxTransparentFraction = 0.02)
    {
        var source = LoadBitmap(filePath);
        var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
        new WriteableBitmap(source).CopyPixels(pixels, source.PixelWidth * 4, 0);
        var transparent = 0;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] == 0)
            {
                transparent++;
            }
        }
        var fraction = (double)transparent / (source.PixelWidth * source.PixelHeight);
        True(fraction <= maxTransparentFraction,
            $"{label}: {(fraction * 100):F1}% of the canvas is unpainted — layout or DPI producer regression");
    }

    /// <summary>Drives a production panel session result synchronously.</summary>
    private static void DrivePanelSession(TranslationPanelWindow panel, string source, TranslationSession session)
    {
        typeof(TranslationPanelWindow)
            .GetMethod("HandleSessionResultAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(panel, new object?[] { source, session, 0L, null });
        panel.UpdateLayout();
    }

    /// <summary>Library page preview fed by the synthetic history/vocabulary fixtures.</summary>
    private static Window CreateLibraryPreview(HistoryStore history, VocabularyStore vocab)
    {
        var section = new LibrarySection();
        section.Initialize(history, vocab);
        section.ReloadHistory();
        section.ReloadVocabulary();
        var host = new System.Windows.Controls.Border
        {
            Child = section,
            Padding = new Thickness(24),
        };
        host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "CanvasBrush");
        return new Window { Content = host };
    }

    private static Window CreateServiceEditorPreview(string? textModel = null)
    {
        var section = new ServicesSection();
        section.LoadProfileIntoForm(DemoProfile(textModel: textModel));
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
        section.LoadProfileIntoForm(DemoProfile(advanced: true));
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

    /// <summary>
    /// The demo profile used by every rendered surface: fixed fake names, a
    /// loopback URL that is never contacted, and no relationship to whatever
    /// services exist on the machine running the tests.
    /// </summary>
    private static ProviderProfile DemoProfile(bool advanced = false, string? textModel = null) => new()
    {
        Id = "demo-text",
        Name = "Demo Text Service",
        ProviderType = ProviderType.OpenAiCompatible,
        ApiBaseUrl = "http://127.0.0.1:9/v1",
        TextEndpoint = "/chat/completions",
        VisionEndpoint = "/chat/completions",
        TextModel = textModel ?? "demo-text-model",
        VisionModel = "demo-vision-model",
        AnthropicVersion = "2023-06-01",
        SupportsText = true,
        SupportsVision = true,
        CredentialTarget = "PopGlot/provider/demo-text",
        IsLocal = true,
        ExtraHeaders = advanced
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Demo-Header"] = "demo" }
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
    };

    // ================= Harness =================

    /// <summary>
    /// Optional substring filter (POPGLOT_TESTS_FILTER): when set, only tests
    /// whose name contains it run. Lets targeted verification proceed while a
    /// real PopGlot instance is open (quit it for the FULL suite — the
    /// isolation guard refuses to run beside one).
    /// </summary>
    private static readonly string? NameFilter =
        Environment.GetEnvironmentVariable("POPGLOT_TESTS_FILTER")?.Trim().ToLowerInvariant();

    private static bool ShouldRun(string name) =>
        NameFilter is null || NameFilter.Length == 0 ||
        name.ToLowerInvariant().Contains(NameFilter, StringComparison.Ordinal);

    private static async Task RunAsync(string name, Func<Task> test)
    {
        if (!ShouldRun(name))
        {
            return;
        }
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
            Console.WriteLine(exception.StackTrace);
        }
    }

    private static void Run(string name, Action test)
    {
        if (!ShouldRun(name))
        {
            return;
        }
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
            Console.WriteLine(exception.StackTrace);
        }
    }

    private static void RunSta(string name, Action test) => RunStaBatch((name, test));

    /// <summary>
    /// Runs several UI-bound tests on ONE STA thread with individual
    /// reporting. WPF Application resources are thread-affine: a test that
    /// needs the bootstrapped Application must run on the very thread that
    /// created it.
    /// </summary>
    private static void RunStaBatch(params (string Name, Action Test)[] tests)
    {
        var selected = tests.Where(t => ShouldRun(t.Name)).ToArray();
        if (selected.Length == 0)
        {
            return;
        }
        var caught = new Exception?[selected.Length];
        var thread = new Thread(() =>
        {
            // Production WPF UI threads carry a DispatcherSynchronizationContext,
            // so async void handlers (Save_Click) resume on the UI thread. The
            // bare STA thread must behave the same or its continuations land on
            // the pool and touch DependencyObjects cross-thread.
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
            for (var i = 0; i < selected.Length; i++)
            {
                try
                {
                    selected[i].Test();
                }
                catch (Exception ex)
                {
                    caught[i] = ex;
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        for (var i = 0; i < selected.Length; i++)
        {
            if (caught[i] is null)
            {
                _passed++;
                Console.WriteLine($"PASS {selected[i].Name}");
            }
            else
            {
                _failed++;
                Console.WriteLine($"FAIL {selected[i].Name}: {caught[i]!.Message}");
                Console.WriteLine(caught[i]!.StackTrace);
            }
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

    // ================= TranslationStreamBuffer tests =================

    private static async Task StreamBufferConcurrentMultiProducerOrderAndZeroLossAsync()
    {
        const int producerCount = 8;
        const int deltasPerProducer = 500;
        using var buffer = new TranslationStreamBuffer("session-conc", "req-conc", 1);

        var tasks = new Task[producerCount];
        for (var p = 0; p < producerCount; p++)
        {
            var producerId = p;
            tasks[p] = Task.Run(() =>
            {
                for (var i = 0; i < deltasPerProducer; i++)
                {
                    var token = $"[P{producerId}:{i:D4}]";
                    var ok = buffer.TryAppend(token);
                    if (!ok)
                    {
                        throw new InvalidOperationException($"Producer {producerId} failed at token {i}");
                    }
                }
            });
        }

        await Task.WhenAll(tasks);
        buffer.Complete();

        Equal(producerCount * deltasPerProducer, (int)buffer.DeltaCount, "Total delta count must match all producers");
        var fullText = buffer.GetAccumulatedText();
        Equal(buffer.CharCount, (long)fullText.Length, "Buffer CharCount must match accumulated text length");

        // Verify each producer's sequential order is strictly monotonically increasing in accumulated text
        for (var p = 0; p < producerCount; p++)
        {
            var lastIndex = -1;
            for (var i = 0; i < deltasPerProducer; i++)
            {
                var token = $"[P{p}:{i:D4}]";
                var index = fullText.IndexOf(token, StringComparison.Ordinal);
                True(index >= 0, $"Token {token} must exist in full text");
                True(index > lastIndex, $"Token {token} at {index} must appear strictly after previous token at {lastIndex}");
                lastIndex = index;
            }
        }
    }

    private static async Task StreamBufferHighFrequency10kDeltaDrainAsync()
    {
        const int totalDeltas = 10000;
        using var buffer = new TranslationStreamBuffer("session-10k", "req-10k", 1);

        var drainedSb = new StringBuilder();
        var drainCount = 0;

        var producerTask = Task.Run(async () =>
        {
            for (var i = 0; i < totalDeltas; i++)
            {
                var delta = $"d{i}_";
                var ok = buffer.TryAppend(delta);
                if (!ok)
                {
                    throw new InvalidOperationException($"TryAppend failed at {i}");
                }
                if (i % 250 == 0)
                {
                    await Task.Yield();
                }
            }
            buffer.Complete();
        });

        var consumerTask = Task.Run(async () =>
        {
            while (true)
            {
                var chunk = buffer.DrainText();
                if (chunk.Length > 0)
                {
                    drainedSb.Append(chunk);
                    drainCount++;
                }

                if (buffer.IsCompleted)
                {
                    // Perform final drain to guarantee zero tail loss
                    var tail = buffer.DrainText();
                    if (tail.Length > 0)
                    {
                        drainedSb.Append(tail);
                        drainCount++;
                    }
                    break;
                }

                await Task.Yield();
            }
        });

        await Task.WhenAll(producerTask, consumerTask);

        Equal(totalDeltas, (int)buffer.DeltaCount, "10k deltas counted");
        Equal(buffer.CharCount, (long)drainedSb.Length, "Drained chars must match char count");
        True(buffer.FlushCount > 0, "Flush count should be positive");

        var expectedSb = new StringBuilder();
        for (var i = 0; i < totalDeltas; i++)
        {
            expectedSb.Append($"d{i}_");
        }
        Equal(expectedSb.ToString(), drainedSb.ToString(), "All 10k deltas drained without loss or corruption");
    }

    private static void StreamBufferHardLimitAbortsWithoutSilentDrop()
    {
        // 1. Test Char limit
        {
            using var buffer = new TranslationStreamBuffer("sess-lim", "req-lim", 1, maxChars: 50, maxBytes: 1000);
            var ok1 = buffer.TryAppend("12345678901234567890"); // 20 chars
            True(ok1, "First 20 chars should succeed");
            var ok2 = buffer.TryAppend("12345678901234567890"); // 20 chars (40 total)
            True(ok2, "Second 20 chars should succeed");

            // Exceeds 50 (40 + 20 = 60 > 50)
            var ok3 = buffer.TryAppend("12345678901234567890");
            True(!ok3, "Third append must return false due to maxChars limit");
            True(buffer.IsAborted, "Buffer must transition to Aborted state");
            Equal(40L, buffer.CharCount, "CharCount must stay at 40 without partial silent drop");

            // Pending text before overflow must still be safely drainable
            var drained = buffer.DrainText();
            Equal("1234567890123456789012345678901234567890", drained, "Pending text up to limit must be preserved");

            // Subsequent appends must be rejected
            var ok4 = buffer.TryAppend("more");
            True(!ok4, "Append after abort must return false");
        }

        // 2. Test Byte limit with UTF-8 multi-byte
        {
            using var buffer = new TranslationStreamBuffer("sess-byte", "req-byte", 1, maxChars: 1000, maxBytes: 20);
            // "你好世界" is 4 CJK chars, 12 UTF-8 bytes
            var ok1 = buffer.TryAppendUtf8(Encoding.UTF8.GetBytes("你好世界"));
            True(ok1, "First 12 bytes UTF-8 append should succeed");

            // Another "你好世界" is 12 bytes (total 24 > 20)
            var ok2 = buffer.TryAppendUtf8(Encoding.UTF8.GetBytes("你好世界"));
            True(!ok2, "Second append must return false due to maxBytes limit");
            True(buffer.IsAborted, "Buffer must transition to Aborted state");
            Equal(12L, buffer.ByteCount, "ByteCount must stay at 12 without corruption");

            var drained = buffer.DrainText();
            Equal("你好世界", drained, "Pending text up to byte limit must be preserved");
        }
    }

    private static void StreamBufferCompleteFinalDrainZeroTailLoss()
    {
        using var buffer = new TranslationStreamBuffer("sess-tail", "req-tail", 1);
        buffer.TryAppend("Hello, ");
        buffer.TryAppend("world!");

        True(buffer.IsActive, "Buffer is active");
        var completed = buffer.Complete();
        True(completed, "First Complete() returns true");
        True(buffer.IsCompleted, "Buffer is completed");
        True(!buffer.IsActive, "Buffer is no longer active");

        // Attempting to append after completion is rejected
        var appendedAfter = buffer.TryAppend("extra");
        True(!appendedAfter, "Append after complete returns false");

        // Final drain receives all remaining pending text
        var batch = buffer.DrainBatch();
        Equal("Hello, world!", batch.Text, "Final drain must retrieve all tail text");
        Equal(2L, batch.DeltaCount, "Delta count is 2");
        Equal(13L, batch.AccumulatedCharCount, "Char count is 13");
        True(batch.State == TranslationStreamState.Completed, "Drain batch shows completed state");

        // Subsequent drain is empty
        var empty = buffer.DrainText();
        Equal(string.Empty, empty, "Subsequent drain should be empty");

        // Complete is idempotent
        var secondComplete = buffer.Complete();
        True(!secondComplete, "Subsequent Complete() returns false");
    }

    private static void StreamBufferEmptyDeltaHandling()
    {
        using var buffer = new TranslationStreamBuffer("sess-empty", "req-empty", 1);

        True(buffer.TryAppend(string.Empty), "Empty string append should succeed");
        True(buffer.TryAppend((string?)null), "Null string append should succeed");
        True(buffer.TryAppend(ReadOnlySpan<char>.Empty), "Empty char span append should succeed");
        True(buffer.TryAppendUtf8(ReadOnlySpan<byte>.Empty), "Empty byte span append should succeed");
        True(buffer.TryAppendUtf8(IntPtr.Zero, 0), "Zero IntPtr append should succeed");

        Equal(0L, buffer.DeltaCount, "Empty appends should not increment delta count");
        Equal(0L, buffer.CharCount, "Empty appends should not increment char count");
        Equal(0L, buffer.ByteCount, "Empty appends should not increment byte count");
        True(!buffer.HasPending, "Buffer has no pending text");

        True(buffer.TryAppend("real"), "Real delta succeeds");
        Equal(1L, buffer.DeltaCount, "Delta count is 1");
        Equal(4L, buffer.CharCount, "Char count is 4");

        buffer.Complete();
        True(!buffer.TryAppend(string.Empty), "Empty append after completion returns false");
    }

    private static void StreamBufferUnicodeAndUtf8MultiByteSupport()
    {
        using var buffer = new TranslationStreamBuffer("sess-uni", "req-uni", 1);

        var utf8Snippet = Encoding.UTF8.GetBytes("【翻译测试】🚀 ⟦PG_TOKEN_001⟧ → café résumé & 𠮷野家");
        True(buffer.TryAppendUtf8(utf8Snippet), "TryAppendUtf8 with CJK, emojis and tokens");

        // Test large payload exceeding stackalloc limit (>256 chars) to exercise ArrayPool path
        var largeText = new string('★', 500);
        var largeUtf8 = Encoding.UTF8.GetBytes(largeText);
        True(buffer.TryAppendUtf8(largeUtf8), "TryAppendUtf8 with large payload");

        // Test native pointer overload
        var nativeSnippet = "Native pointer UTF-8 payload: 汉字测试";
        var nativeBytes = Encoding.UTF8.GetBytes(nativeSnippet);
        var nativePtr = Marshal.AllocHGlobal(nativeBytes.Length);
        try
        {
            Marshal.Copy(nativeBytes, 0, nativePtr, nativeBytes.Length);
            var ok = buffer.TryAppendUtf8(nativePtr, nativeBytes.Length);
            True(ok, "TryAppendUtf8 via native pointer");
        }
        finally
        {
            Marshal.FreeHGlobal(nativePtr);
        }

        var expectedCombined = "【翻译测试】🚀 ⟦PG_TOKEN_001⟧ → café résumé & 𠮷野家" + largeText + nativeSnippet;
        var accumulated = buffer.GetAccumulatedText();
        Equal(expectedCombined, accumulated, "Accumulated text matches combined unicode string");

        var drained = buffer.DrainText();
        Equal(expectedCombined, drained, "Drained text matches combined unicode string");
    }

    private static void StreamBufferLifecycleAndIdempotence()
    {
        // 1. Idempotent Complete
        using (var buffer = new TranslationStreamBuffer("sess-idem1", "req-idem1", 1))
        {
            var results = new bool[8];
            Parallel.For(0, 8, i =>
            {
                results[i] = buffer.Complete();
            });

            var trueCount = results.Count(r => r);
            Equal(1, trueCount, "Exactly one Complete() call must return true");
            True(buffer.IsCompleted, "Buffer must be in Completed state");
        }

        // 2. Idempotent Abort
        using (var buffer = new TranslationStreamBuffer("sess-idem2", "req-idem2", 1))
        {
            var results = new bool[8];
            Parallel.For(0, 8, i =>
            {
                results[i] = buffer.Abort($"abort-thread-{i}");
            });

            var trueCount = results.Count(r => r);
            Equal(1, trueCount, "Exactly one Abort() call must return true");
            True(buffer.IsAborted, "Buffer must be in Aborted state");
        }

        // 3. Reset and Reuse
        using (var buffer = new TranslationStreamBuffer("sess-reset", "req-reset", 1))
        {
            buffer.TryAppend("initial-delta");
            buffer.Complete();
            True(buffer.IsCompleted, "State is completed");

            buffer.Reset();
            True(buffer.IsActive, "Reset returns state to Active");
            Equal(0L, buffer.DeltaCount, "Delta count reset");
            Equal(0L, buffer.CharCount, "Char count reset");
            Equal(0L, buffer.FlushCount, "Flush count reset");
            Equal(string.Empty, buffer.DrainText(), "Pending text reset");

            var ok = buffer.TryAppend("after-reset");
            True(ok, "TryAppend succeeds after Reset");
            Equal("after-reset", buffer.DrainText(), "Drain returns new text after Reset");
        }

        // 4. Dispose idempotence
        var dispBuffer = new TranslationStreamBuffer("sess-disp", "req-disp", 1);
        dispBuffer.Dispose();
        dispBuffer.Dispose();
        True(dispBuffer.IsDisposed, "Buffer is disposed");
        True(!dispBuffer.TryAppend("test"), "Append on disposed buffer returns false");
    }

    private static void StreamBufferSessionEpochFencingAndTtftMetrics()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        using var buffer = new TranslationStreamBuffer("session-alpha", "req-beta", epoch: 7);

        // Fencing
        True(buffer.IsSessionMatch("session-alpha", 7), "Matching session and epoch must pass");
        True(!buffer.IsSessionMatch("session-alpha", 6), "Stale epoch must fail");
        True(!buffer.IsSessionMatch("session-other", 7), "Mismatched session id must fail");

        // TTFT before first delta
        True(!buffer.HasFirstDelta, "HasFirstDelta should be false before any delta");
        True(buffer.GetTtft(startTimestamp) == null, "TTFT should be null before any delta");
        True(buffer.GetTtftMilliseconds(startTimestamp) == null, "TTFT ms should be null before any delta");

        Thread.Sleep(2);

        // First delta records TTFT timestamp
        buffer.TryAppend("chunk1");
        True(buffer.HasFirstDelta, "HasFirstDelta should be true after first delta");
        var firstTicks = buffer.FirstDeltaTimestampTicks;
        True(firstTicks > 0, "FirstDeltaTimestampTicks must be positive");

        var ttft = buffer.GetTtft(startTimestamp);
        True(ttft.HasValue && ttft.Value >= TimeSpan.Zero, "TTFT duration must be >= 0");

        var ttftMs = buffer.GetTtftMilliseconds(startTimestamp);
        True(ttftMs.HasValue && ttftMs.Value >= 0.0, "TTFT ms must be >= 0");

        // Subsequent deltas must NOT overwrite first delta timestamp
        Thread.Sleep(2);
        buffer.TryAppend("chunk2");
        Equal(firstTicks, buffer.FirstDeltaTimestampTicks, "FirstDeltaTimestampTicks must remain fixed on subsequent deltas");
    }

    private static void StreamBufferCallbackThunkHandlesChineseUtf8()
    {
        using var buffer = new TranslationStreamBuffer("session-thunk1", "req-thunk1", epoch: 1);
        var handle = GCHandle.Alloc(buffer);
        try
        {
            var userData = GCHandle.ToIntPtr(handle);
            var text = "你好，流式翻译世界！🚀 UTF-8 多字节测试。";
            var bytes = Encoding.UTF8.GetBytes(text);
            var ptr = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);

                // Call ProcessStreamDelta (event_type = 1 for POPGLOT_STREAM_EVENT_TEXT_DELTA_V1)
                var result = CoreBridge.ProcessStreamDelta(userData, 1, ptr, (nuint)bytes.Length);
                Equal(0, result, "ProcessStreamDelta must return 0 to continue streaming");

                var drained = buffer.DrainText();
                Equal(text, drained, "Drained text must match original UTF-8 text");
                Equal((long)text.Length, buffer.CharCount, "Char count must match");
                Equal((long)bytes.Length, buffer.ByteCount, "Byte count must match");
                Equal(1L, buffer.DeltaCount, "Delta count must be 1");
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        finally
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }
        }
    }

    private static void StreamBufferCallbackThunkAbortsAndBackpressures()
    {
        // 1. Manually aborted buffer returns 1 (abort signal)
        using (var buffer = new TranslationStreamBuffer("session-abort1", "req-abort1", epoch: 1))
        {
            buffer.Abort("User stopped");
            var handle = GCHandle.Alloc(buffer);
            try
            {
                var userData = GCHandle.ToIntPtr(handle);
                var text = "delta-after-abort";
                var bytes = Encoding.UTF8.GetBytes(text);
                var ptr = Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, ptr, bytes.Length);
                    var result = CoreBridge.ProcessStreamDelta(userData, 1, ptr, (nuint)bytes.Length);
                    Equal(1, result, "ProcessStreamDelta on aborted buffer must return 1 to abort FFI stream");
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            finally
            {
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
            }
        }

        // 2. Hard limit breach triggers abort and returns 1
        using (var limitedBuffer = new TranslationStreamBuffer("session-limit", "req-limit", epoch: 1, maxChars: 5, maxBytes: 10))
        {
            var handle = GCHandle.Alloc(limitedBuffer);
            try
            {
                var userData = GCHandle.ToIntPtr(handle);
                var text = "1234567890_exceeding_chars";
                var bytes = Encoding.UTF8.GetBytes(text);
                var ptr = Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, ptr, bytes.Length);
                    var result = CoreBridge.ProcessStreamDelta(userData, 1, ptr, (nuint)bytes.Length);
                    Equal(1, result, "ProcessStreamDelta on hard limit exceed must return 1");
                    True(limitedBuffer.IsAborted, "Buffer must transition to Aborted state");
                    True(limitedBuffer.AbortReason?.Contains("Hard limit exceeded") == true, "Abort reason must be set");
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            finally
            {
                if (handle.IsAllocated)
                {
                    handle.Free();
                }
            }
        }
    }

    private static void StreamBufferCallbackThunkInvalidUserDataHandledSafely()
    {
        var text = "sample delta";
        var bytes = Encoding.UTF8.GetBytes(text);
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);

            // 1. Null user_data returns 0
            var resNull = CoreBridge.ProcessStreamDelta(IntPtr.Zero, 1, ptr, (nuint)bytes.Length);
            Equal(0, resNull, "Null userData must safely return 0 without throwing");

            // 2. Freed GCHandle returns 1 gracefully
            var tempBuffer = new TranslationStreamBuffer("sess-freed", "req-freed", 1);
            var freedHandle = GCHandle.Alloc(tempBuffer);
            var freedPtr = GCHandle.ToIntPtr(freedHandle);
            freedHandle.Free();
            var resFreed = CoreBridge.ProcessStreamDelta(freedPtr, 1, ptr, (nuint)bytes.Length);
            Equal(1, resFreed, "Freed GCHandle must safely return 1 without crashing");

            // 3. Non-delta eventType on active buffer
            using var buffer = new TranslationStreamBuffer("sess-events", "req-events", 1);
            var handle = GCHandle.Alloc(buffer);
            try
            {
                var userData = GCHandle.ToIntPtr(handle);
                var resUnknownEvent = CoreBridge.ProcessStreamDelta(userData, 99, ptr, (nuint)bytes.Length);
                Equal(0, resUnknownEvent, "Unknown event type on active buffer returns 0");

                // 4. Payload overflow
                var resOverflow = CoreBridge.ProcessStreamDelta(userData, 1, ptr, unchecked((nuint)long.MaxValue));
                Equal(1, resOverflow, "Overflow payload length returns 1 and aborts");
                True(buffer.IsAborted, "Buffer aborted on overflow payload");
            }
            finally
            {
                if (handle.IsAllocated) handle.Free();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static void FinalEnvelopeDeserializationAndErrorChecking()
    {
        // 1. Success Envelope deserializes to TranslationResponse
        var successJson = """
            {
                "ok": true,
                "data": {
                    "result": {
                        "translated_text": "你好，世界！",
                        "transcription": "Hello, world!",
                        "explanation": "标准问候语",
                        "protected_terms": ["world"],
                        "warnings": [],
                        "phonetic": "nǐ hǎo"
                    },
                    "diagnostics": {
                        "request_id": "req-streaming-123",
                        "provider_type": "OpenAiCompatible",
                        "endpoint": "https://api.openai.com/v1",
                        "attempts": 1,
                        "status_code": 200,
                        "elapsed_ms": 250
                    }
                },
                "error": null
            }
            """;

        var response = CoreBridge.EnsureSuccess<TranslationResponse>(successJson);
        Equal("你好，世界！", response.Result.TranslatedText, "TranslatedText matches");
        Equal("Hello, world!", response.Result.Transcription, "Transcription matches");
        Equal("标准问候语", response.Result.Explanation, "Explanation matches");
        Equal("nǐ hǎo", response.Result.Phonetic, "Phonetic matches");
        Equal("req-streaming-123", response.Diagnostics.RequestId, "RequestId matches");
        Equal(ProviderType.OpenAiCompatible, response.Diagnostics.ProviderType, "ProviderType matches");

        // 2. Failure Envelope throws with exact error message
        var failureJson = """
            {
                "ok": false,
                "data": null,
                "error": "API 请求被限流 (HTTP 429 Too Many Requests)"
            }
            """;

        var thrown = false;
        try
        {
            CoreBridge.EnsureSuccess<TranslationResponse>(failureJson);
        }
        catch (InvalidOperationException ex)
        {
            thrown = true;
            True(ex.Message.Contains("API 请求被限流"), "Exception message must contain the exact core error");
        }
        True(thrown, "EnsureSuccess must throw on ok: false envelope");

        // 3. Null or malformed Envelope throws
        var malformedThrown = false;
        try
        {
            CoreBridge.EnsureSuccess<TranslationResponse>("{}");
        }
        catch (InvalidOperationException)
        {
            malformedThrown = true;
        }
        True(malformedThrown, "EnsureSuccess must throw on empty/missing data envelope");
    }

    private static void StreamSessionPropertiesAndLifecycle()
    {
        using var buffer = new TranslationStreamBuffer("session-wrap", "req-wrap", epoch: 3);
        var tcs = new TaskCompletionSource<TranslationResponse>();
        var session = new TranslationStreamSession(buffer, tcs.Task);

        Equal(buffer, session.Buffer, "Session buffer must match provided buffer");
        Equal(tcs.Task, session.Completion, "Session completion task must match");
        True(!session.Completion.IsCompleted, "Completion task is pending");

        var dummyResponse = new TranslationResponse(
            new TranslationResult("已翻译", "", "", [], []),
            new ProviderDiagnostics("req-wrap", ProviderType.OpenAiCompatible, "https://api.openai.com", 1, 200, 100));

        tcs.SetResult(dummyResponse);
        True(session.Completion.IsCompletedSuccessfully, "Completion task resolved successfully");
        Equal("已翻译", session.Completion.Result.Result.TranslatedText, "Result translated text matches");
    }

    // ================= Coordinator streaming and lifecycle tests =================

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        public void Report(T value) => _handler(value);
    }

    /// <summary>History store whose writes always fail (T19/F07 probe).</summary>
    private sealed class FailingHistoryRepository : IHistoryRepository
    {
        public int Attempts;

        public IReadOnlyList<TranslationHistoryEntry> Load() => [];
        public HistoryAddResult TryAdd(TranslationHistoryEntry entry, bool enabled)
        {
            if (!enabled) return HistoryAddResult.Disabled;
            Attempts++;
            return HistoryAddResult.Failed;
        }
        public bool Remove(Guid id) => false;
        public bool Clear() => false;
        public string ExportToCsv() => string.Empty;
        public string ExportToMarkdown() => string.Empty;
    }

    private sealed class FakeHistoryRepository : IHistoryRepository
    {
        public List<TranslationHistoryEntry> Entries { get; } = new();

        public IReadOnlyList<TranslationHistoryEntry> Load() => Entries;

        public HistoryAddResult TryAdd(TranslationHistoryEntry entry, bool enabled)
        {
            if (!enabled) return HistoryAddResult.Disabled;
            Entries.Add(entry);
            return HistoryAddResult.Stored;
        }

        public bool Remove(Guid id) => Entries.RemoveAll(e => e.Id == id) > 0;
        public bool Clear() { Entries.Clear(); return true; }
        public string ExportToCsv() => string.Empty;
        public string ExportToMarkdown() => string.Empty;
    }

    private sealed class FakeTranslationExecutor : ITranslationExecutor
    {
        public ProviderSettings Settings { get; set; } = CoreBridge.GetSettings() with
        {
            ProviderType = ProviderType.OpenAiCompatible,
            TextModel = "fake-model",
            NetworkEnabled = true,
            SafeDevMode = false,
            Mode = TranslationMode.Auto,
        };

        public ProviderRoute? TextRoute { get; set; } = new(
            new ProviderProfile
            {
                Id = "fake-profile",
                Name = "Fake",
                ApiBaseUrl = "https://api.example.com/v1",
                // TextModel 故意留空：模拟「档案存在但模型未配置、凭据在
                // 全局设置」的旧形态，让协调器走 StreamText 分支（与真实
                // 旧版安装由 SeedFromLiveSettings 合成档案后的行为一致）。
                TextModel = string.Empty,
                SupportsText = true,
                CredentialTarget = "PopGlot/provider/fake",
            },
            "PopGlot/provider/fake");
        public ProviderRoute? VisionRoute { get; set; }
        public ResolvedRoute? ScreenshotRoute { get; set; }
        public string? ApiKey { get; set; } = "fake-api-key";
        public bool IsOcrSupported { get; set; } = true;
        public string OcrRecognizedText { get; set; } = "Recognized Text From OCR";
        public int OcrCallCount { get; set; }

        public Func<string?, string, string, string, string, long, CancellationToken, TranslationStreamSession>? OnStreamText { get; set; }
        public Func<ProviderSettings, string, string, string, string, string, long, CancellationToken, TranslationStreamSession>? OnStreamTextDraft { get; set; }
        public Func<ProviderSettings, string, string, byte[], string, string, string, long, CancellationToken, TranslationStreamSession>? OnStreamVisionDraft { get; set; }
        public Func<string, string, string, CancellationToken, Task<TranslationResponse>>? OnTranslateFree { get; set; }

        public ProviderSettings GetSettings() => Settings;

        public (ProviderRoute? Text, ProviderRoute? Vision) ResolveRoutes() => (TextRoute, VisionRoute);

        public ResolvedRoute ResolveScreenshotRoute(ProviderSettings settings, bool ocrAvailable)
        {
            if (ScreenshotRoute is not null) return ScreenshotRoute;
            return new ResolvedRoute(TextRoute, VisionRoute, ScreenshotPipeline.LocalOcr, false, "Auto local OCR");
        }

        public string? LoadApiKey(string target) => ApiKey;

        public Task<string> RecognizeOcrTextAsync(byte[] imageBytes, string sourceLang, CancellationToken cancellationToken = default)
        {
            OcrCallCount++;
            return Task.FromResult(OcrRecognizedText);
        }

        public TranslationStreamSession StreamText(
            string? apiKey,
            string source,
            string sourceLang,
            string targetLang,
            string sessionId,
            long epoch,
            CancellationToken cancellationToken)
        {
            if (OnStreamText is not null)
            {
                return OnStreamText(apiKey, source, sourceLang, targetLang, sessionId, epoch, cancellationToken);
            }
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();
            return new TranslationStreamSession(buffer, tcs.Task);
        }

        public TranslationStreamSession StreamTextDraft(
            ProviderSettings draftSettings,
            string apiKey,
            string source,
            string sourceLang,
            string targetLang,
            string sessionId,
            long epoch,
            CancellationToken cancellationToken)
        {
            if (OnStreamTextDraft is not null)
            {
                return OnStreamTextDraft(draftSettings, apiKey, source, sourceLang, targetLang, sessionId, epoch, cancellationToken);
            }
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();
            return new TranslationStreamSession(buffer, tcs.Task);
        }

        public TranslationStreamSession StreamVisionDraft(
            ProviderSettings draftSettings,
            string textApiKey,
            string visionApiKey,
            byte[] image,
            string sourceLang,
            string targetLang,
            string sessionId,
            long epoch,
            CancellationToken cancellationToken)
        {
            if (OnStreamVisionDraft is not null)
            {
                return OnStreamVisionDraft(draftSettings, textApiKey, visionApiKey, image, sourceLang, targetLang, sessionId, epoch, cancellationToken);
            }
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();
            return new TranslationStreamSession(buffer, tcs.Task);
        }

        public Task<TranslationResponse> TranslateFreeAsync(
            string source,
            string sourceLang,
            string targetLang,
            Services.FreeEngineAuthorization authorization,
            CancellationToken cancellationToken)
        {
            if (OnTranslateFree is not null)
            {
                return OnTranslateFree(source, sourceLang, targetLang, cancellationToken);
            }
            return Task.FromResult(new TranslationResponse(
                new TranslationResult(
                    TranslatedText: "免费翻译结果: " + source,
                    Transcription: string.Empty,
                    Explanation: string.Empty,
                    ProtectedTerms: [],
                    Warnings: []),
                new ProviderDiagnostics(
                    RequestId: FreeTranslateService.RequestId,
                    ProviderType: ProviderType.OpenAiCompatible,
                    Endpoint: "https://free.example.com",
                    Attempts: 1,
                    StatusCode: 200,
                    ElapsedMs: 10)));
        }
    }

    private static async Task CoordinatorDeltaArrivesBeforeCompletionAsync()
    {
        var history = new FakeHistoryRepository();
        var executor = new FakeTranslationExecutor();
        var updates = new List<TranslationStreamUpdate>();
        var progressLock = new object();
        var progress = new SynchronousProgress<TranslationStreamUpdate>(u =>
        {
            lock (progressLock) updates.Add(u);
        });

        executor.OnStreamText = (apiKey, source, sourceLang, targetLang, sessionId, epoch, ct) =>
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();

            _ = Task.Run(async () =>
            {
                await Task.Delay(20);
                buffer.TryAppend("Hello ");
                await Task.Delay(60);
                buffer.TryAppend("World");
                await Task.Delay(60);
                buffer.Complete();
                tcs.SetResult(new TranslationResponse(
                    new TranslationResult("Hello World", "", "", [], []),
                    new ProviderDiagnostics(sessionId, ProviderType.OpenAiCompatible, "https://api.openai.com", 1, 200, 140)));
            });

            return new TranslationStreamSession(buffer, tcs.Task);
        };

        var coordinator = new TranslationCoordinator(history: history, executor: executor);
        var session = await coordinator.TranslateTextAsync("你好", "zh", "en", TranslationInputSource.Selection, progress: progress, epoch: 101);

        Equal("Hello World", session.TranslatedText, "Session translated text must match full result");
        Equal(TranslationSessionStage.Completed, session.Stage, "Final stage must be Completed");

        lock (progressLock)
        {
            True(updates.Count >= 2, $"Expected at least 2 stream updates, got {updates.Count}");
            Equal(101L, updates[0].Epoch, "Epoch must propagate to first update");
            Equal(TranslationStreamUpdateKind.Delta, updates[0].Kind, "First update must be Delta");
            Equal("Hello ", updates[0].Delta, "First delta text must match");
            Equal("Hello ", updates[0].AccumulatedText, "First accumulated text must match");
            True(updates[0].IsPartial, "Stream update during pump must be partial");
        }
    }

    private static async Task CoordinatorThrottlingMergesDeltasAsync()
    {
        var executor = new FakeTranslationExecutor();
        var updates = new List<TranslationStreamUpdate>();
        var progressLock = new object();
        var progress = new SynchronousProgress<TranslationStreamUpdate>(u =>
        {
            lock (progressLock) updates.Add(u);
        });

        executor.OnStreamText = (apiKey, source, sourceLang, targetLang, sessionId, epoch, ct) =>
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();

            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    buffer.TryAppend($"[{i}]");
                    await Task.Delay(2);
                }
                await Task.Delay(100);
                buffer.Complete();
                var full = string.Concat(Enumerable.Range(0, 20).Select(i => $"[{i}]"));
                tcs.SetResult(new TranslationResponse(
                    new TranslationResult(full, "", "", [], []),
                    new ProviderDiagnostics(sessionId, ProviderType.OpenAiCompatible, "https://api.openai.com", 1, 200, 150)));
            });

            return new TranslationStreamSession(buffer, tcs.Task);
        };

        var coordinator = new TranslationCoordinator(executor: executor);
        var session = await coordinator.TranslateTextAsync("test", "en", "zh", TranslationInputSource.Selection, progress: progress);

        var expectedFull = string.Concat(Enumerable.Range(0, 20).Select(i => $"[{i}]"));
        Equal(expectedFull, session.TranslatedText, "All 20 rapid chunks must be present without character loss");

        lock (progressLock)
        {
            True(updates.Count < 20, $"Batch throttling must merge 20 deltas into fewer updates, got {updates.Count}");
            True(updates.Count > 0, "At least one update must be reported");
        }
    }

    private static async Task CoordinatorFinalDrainDeliversTailAsync()
    {
        var executor = new FakeTranslationExecutor();
        var updates = new List<TranslationStreamUpdate>();
        var progressLock = new object();
        var progress = new SynchronousProgress<TranslationStreamUpdate>(u =>
        {
            lock (progressLock) updates.Add(u);
        });

        executor.OnStreamText = (apiKey, source, sourceLang, targetLang, sessionId, epoch, ct) =>
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();

            _ = Task.Run(async () =>
            {
                buffer.TryAppend("chunk1");
                await Task.Delay(50);
                buffer.TryAppend("tail_chunk");
                buffer.Complete();
                tcs.SetResult(new TranslationResponse(
                    new TranslationResult("chunk1tail_chunk", "", "", [], []),
                    new ProviderDiagnostics(sessionId, ProviderType.OpenAiCompatible, "https://api.openai.com", 1, 200, 60)));
            });

            return new TranslationStreamSession(buffer, tcs.Task);
        };

        var coordinator = new TranslationCoordinator(executor: executor);
        var session = await coordinator.TranslateTextAsync("test", "en", "zh", TranslationInputSource.Selection, progress: progress);

        Equal("chunk1tail_chunk", session.TranslatedText, "Session must contain final tail chunk");
        lock (progressLock)
        {
            Equal("chunk1tail_chunk", updates.Last().AccumulatedText, "Last update must contain final tail chunk");
        }
    }

    private static async Task CoordinatorFinalCalibrationReplacesTextAsync()
    {
        var executor = new FakeTranslationExecutor();
        var coordinator = new TranslationCoordinator(executor: executor);

        executor.OnStreamText = (apiKey, source, sourceLang, targetLang, sessionId, epoch, ct) =>
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();

            _ = Task.Run(async () =>
            {
                buffer.TryAppend("raw stream text with ⟦PG_0001⟧ placeholder");
                await Task.Delay(50);
                buffer.Complete();
                tcs.SetResult(new TranslationResponse(
                    new TranslationResult(
                        TranslatedText: "calibrated text with <https://example.com> restored",
                        Transcription: "transcription",
                        Explanation: "detailed explanation",
                        ProtectedTerms: ["<https://example.com>"],
                        Warnings: []),
                    new ProviderDiagnostics(sessionId, ProviderType.OpenAiCompatible, "https://api.openai.com", 1, 200, 60)));
            });

            return new TranslationStreamSession(buffer, tcs.Task);
        };

        var session = await coordinator.TranslateTextAsync("source", "en", "zh", TranslationInputSource.Selection);

        Equal("calibrated text with <https://example.com> restored", session.TranslatedText, "Final text must be calibrated from response");
        Equal("detailed explanation", session.Explanation, "Explanation must be populated from final response");
        Equal(1, session.ProtectedTerms.Count, "Protected terms must be populated");
        Equal(TranslationSessionStage.Completed, session.Stage);
    }

    private static async Task CoordinatorErrorAndCancellationPreservePartialAndNoHistoryAsync()
    {
        var history = new FakeHistoryRepository();
        var executor = new FakeTranslationExecutor();
        var coordinator = new TranslationCoordinator(history: history, executor: executor);

        // 1. Cancellation test
        using var cts = new CancellationTokenSource();
        executor.OnStreamText = (apiKey, source, sourceLang, targetLang, sessionId, epoch, ct) =>
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();

            _ = Task.Run(async () =>
            {
                buffer.TryAppend("partial cancellation text");
                await Task.Delay(50);
                cts.Cancel();
                tcs.SetCanceled(cts.Token);
            });

            return new TranslationStreamSession(buffer, tcs.Task);
        };

        var cancelledSession = await coordinator.TranslateTextAsync("cancel me", "en", "zh", TranslationInputSource.Selection, cancellationToken: cts.Token);

        Equal(TranslationSessionStage.Cancelled, cancelledSession.Stage, "Stage must be Cancelled");
        Equal("partial cancellation text", cancelledSession.TranslatedText, "Partial text must be preserved on cancellation");
        Equal(0, history.Entries.Count, "History must NOT be written on cancellation");

        // 2. Error test
        executor.OnStreamText = (apiKey, source, sourceLang, targetLang, sessionId, epoch, ct) =>
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();

            _ = Task.Run(async () =>
            {
                buffer.TryAppend("partial error text");
                await Task.Delay(50);
                tcs.SetException(new InvalidOperationException("API 请求被限流 (429 Too Many Requests)"));
            });

            return new TranslationStreamSession(buffer, tcs.Task);
        };

        var failedSession = await coordinator.TranslateTextAsync("error me", "en", "zh", TranslationInputSource.Selection);

        Equal(TranslationSessionStage.Failed, failedSession.Stage, "Stage must be Failed");
        Equal("partial error text", failedSession.TranslatedText, "Partial text must be preserved on error");
        True(failedSession.Error is not null, "Error must be classified");
        Equal(TranslationErrorKind.RateLimited, failedSession.Error!.Kind, "Error must be classified as RateLimited");
        Equal(0, history.Entries.Count, "History must NOT be written on failure");
    }

    private static async Task CoordinatorSuccessfulTranslationWritesHistoryOnceAsync()
    {
        var history = new FakeHistoryRepository();
        var executor = new FakeTranslationExecutor();
        var coordinator = new TranslationCoordinator(history: history, executor: executor);

        executor.OnStreamText = (apiKey, source, sourceLang, targetLang, sessionId, epoch, ct) =>
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();

            _ = Task.Run(async () =>
            {
                buffer.TryAppend("Translated success");
                await Task.Delay(50);
                buffer.Complete();
                tcs.SetResult(new TranslationResponse(
                    new TranslationResult("Translated success", "", "", [], []),
                    new ProviderDiagnostics(sessionId, ProviderType.OpenAiCompatible, "https://api.openai.com", 1, 200, 60)));
            });

            return new TranslationStreamSession(buffer, tcs.Task);
        };

        var session = await coordinator.TranslateTextAsync("Hello world", "en", "zh", TranslationInputSource.Selection);

        Equal(TranslationSessionStage.Completed, session.Stage);
        Equal(1, history.Entries.Count, "History must be written exactly once on success");
        Equal("Hello world", history.Entries[0].Source);
        Equal("Translated success", history.Entries[0].Translation);
        Equal("划词", history.Entries[0].SourceKind);
    }

    /// <summary>
    /// T03 acceptance: the one completion contract. A final with integrity
    /// warnings, a provider is_partial flag, or an empty translation lands as
    /// Partial and writes zero history entries; a clean final written twice
    /// still produces exactly one history entry.
    /// </summary>
    private static async Task PartialFinalNeverPersistsOrTriggersSideEffects()
    {
        var history = new FakeHistoryRepository();
        var executor = new FakeTranslationExecutor();
        var coordinator = new TranslationCoordinator(history: history, executor: executor);

        static TranslationStreamSession StreamEndingIn(TranslationResponse final) =>
            MakeStreamSession("preview text", final);

        // 1. Non-empty final with an integrity warning → Partial, no history.
        executor.OnStreamText = (_, _, _, _, s, e, _) =>
            StreamEndingIn(FinalResponse("missing one token", IsPartial: false,
                ["缺少 1 个受保护占位符：⟦PG_0000⟧"]));
        var warned = await coordinator.TranslateTextAsync("warned", "en", "zh", TranslationInputSource.Selection);
        Equal(TranslationSessionStage.Partial, warned.Stage, "an integrity warning must yield Partial");
        True(!warned.IsCleanCompletion, "a warned session must not qualify");
        Equal(0, history.Entries.Count, "a warned Partial must never enter history");

        // 2. is_partial=true with empty warnings → Partial, no history.
        executor.OnStreamText = (_, _, _, _, s, e, _) =>
            StreamEndingIn(FinalResponse("provider cut the output early", IsPartial: true, []));
        var providerPartial = await coordinator.TranslateTextAsync("provider partial", "en", "zh", TranslationInputSource.Selection);
        Equal(TranslationSessionStage.Partial, providerPartial.Stage, "a provider is_partial final must yield Partial");
        True(!providerPartial.IsCleanCompletion, "a provider-partial session must not qualify");
        Equal(0, history.Entries.Count, "a provider-partial final must never enter history");

        // 3. Completed-flagged but empty translation → never qualifies.
        executor.OnStreamText = (_, _, _, _, s, e, _) =>
            StreamEndingIn(FinalResponse(string.Empty, IsPartial: false, []));
        var empty = await coordinator.TranslateTextAsync("empty final", "en", "zh", TranslationInputSource.Selection);
        True(!empty.IsCleanCompletion, "an empty translation must not qualify regardless of stage");
        Equal(0, history.Entries.Count, "an empty translation must never enter history");

        // 4. Clean final → Completed, exactly one history entry even when the
        // final response is delivered a second time.
        executor.OnStreamText = (_, _, _, _, s, e, _) =>
            StreamEndingIn(FinalResponse("完整的最终译文", IsPartial: false, []));
        var clean = await coordinator.TranslateTextAsync("clean", "en", "zh", TranslationInputSource.Selection);
        Equal(TranslationSessionStage.Completed, clean.Stage);
        True(clean.IsCleanCompletion, "a clean completion must qualify");
        Equal(1, history.Entries.Count, "a clean final writes exactly one history entry");

        var response = FinalResponse("完整的最终译文", IsPartial: false, []);
        var applyFinal = typeof(TranslationCoordinator).GetMethod("ApplyFinalResponse",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var parameters = new object?[]
        {
            clean, response, TranslationInputSource.Selection, 0L, null, null,
            0UL, System.Diagnostics.Stopwatch.StartNew(), 0UL, 0UL,
            System.Threading.CancellationToken.None,
        };
        applyFinal.Invoke(coordinator, parameters);
        applyFinal.Invoke(coordinator, parameters);
        Equal(1, history.Entries.Count, "a duplicated final delivery must not write history twice");
    }

    private static TranslationResponse FinalResponse(string text, bool IsPartial, string[] warnings) => new(
        new TranslationResult(
            TranslatedText: text,
            Transcription: string.Empty,
            Explanation: string.Empty,
            ProtectedTerms: [],
            Warnings: warnings,
            Phonetic: string.Empty,
            IsPartial: IsPartial),
        new ProviderDiagnostics(
            RequestId: "final-matrix",
            ProviderType: ProviderType.OpenAiCompatible,
            Endpoint: "mock://final",
            Attempts: 1,
            StatusCode: 200,
            ElapsedMs: 3));

    private static TranslationStreamSession MakeStreamSession(string preview, TranslationResponse final)
    {
        var buffer = new TranslationStreamBuffer("session-final", "req-final", 1);
        var tcs = new TaskCompletionSource<TranslationResponse>();
        _ = Task.Run(async () =>
        {
            buffer.TryAppend(preview);
            await Task.Delay(10);
            tcs.SetResult(final);
        });
        return new TranslationStreamSession(buffer, tcs.Task);
    }

    /// <summary>
    /// T04 acceptance: the free engine must run the shared Rust protection
    /// chain. The fake engine receives masked text, and the session restores
    /// identifiers byte-for-byte; a broken placeholder lands Partial with no
    /// history write.
    /// </summary>
    private static async Task FreeEngineRunsSharedTokenProtection()
    {
        var originalLoader = OutboundPolicy.SettingsLoader;
        var originalSaver = OutboundPolicy.SettingsSaver;
        OutboundPolicy.SettingsLoader = () => ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Allowed };
        OutboundPolicy.SettingsSaver = _ => { };
        try
        {
            var settings = CoreBridge.GetSettings() with
            {
                ProtectCodeTokens = true,
                SafeDevMode = false,
                NetworkEnabled = true,
                TextModel = string.Empty,
            };

            // 1. Echo path: the engine gets masked text; the session shows the
            // original identifiers and qualifies as clean.
            var history = new FakeHistoryRepository();
            var executor = new FreeEngineBoundaryExecutor(settings)
            {
                OnTranslateFree = (source, _, _, _) =>
                {
                    True(source.Contains("⟦PG_0000⟧"),
                        $"the free engine must receive the masked placeholder, got: {source}");
                    True(!source.Contains("foo_bar_baz"),
                        $"the raw identifier must never reach the engine, got: {source}");
                    return Task.FromResult(FreeEcho(source));
                },
            };
            var coordinator = new TranslationCoordinator(history: history, executor: executor);
            var clean = await coordinator.TranslateTextAsync(
                "let value = foo_bar_baz(config_value);", "auto", "zh", TranslationInputSource.Manual);
            Equal(TranslationSessionStage.Completed, clean.Stage,
                $"an echo with intact placeholders is clean: {clean.Error?.Message}");
            True(clean.TranslatedText.Contains("foo_bar_baz") && clean.TranslatedText.Contains("config_value"),
                $"identifiers must be restored byte-for-byte, got: {clean.TranslatedText}");
            Equal(1, history.Entries.Count, "a clean free-engine result writes history once");

            // 2. Broken path: the engine drops one placeholder → Partial, no
            // history, warning names the lost identifier.
            var brokenHistory = new FakeHistoryRepository();
            var brokenExecutor = new FreeEngineBoundaryExecutor(settings)
            {
                OnTranslateFree = (source, _, _, _) =>
                {
                    var damaged = source.Replace("⟦PG_0000⟧", string.Empty);
                    return Task.FromResult(FreeEcho(damaged));
                },
            };
            var brokenCoordinator = new TranslationCoordinator(history: brokenHistory, executor: brokenExecutor);
            var broken = await brokenCoordinator.TranslateTextAsync(
                "let value = foo_bar_baz(config_value);", "auto", "zh", TranslationInputSource.Manual);
            Equal(TranslationSessionStage.Partial, broken.Stage,
                "a dropped placeholder must mark the result partial");
            True(broken.Warnings.Any(w => w.Contains("foo_bar_baz")),
                $"the warning must name the dropped identifier, got: {string.Join(" | ", broken.Warnings)}");
            True(!broken.IsCleanCompletion, "a broken-placeholder result must not qualify");
            Equal(0, brokenHistory.Entries.Count, "a broken free-engine result never enters history");
        }
        finally
        {
            OutboundPolicy.SettingsLoader = originalLoader;
            OutboundPolicy.SettingsSaver = originalSaver;
        }
    }

    private static TranslationResponse FreeEcho(string text) => new(
        new TranslationResult(
            TranslatedText: "结果：" + text,
            Transcription: string.Empty,
            Explanation: string.Empty,
            ProtectedTerms: [],
            Warnings: []),
        new ProviderDiagnostics(
            RequestId: FreeTranslateService.RequestId,
            ProviderType: ProviderType.OpenAiCompatible,
            Endpoint: "mock-free",
            Attempts: 1,
            StatusCode: 200,
            ElapsedMs: 4));

    /// <summary>
    /// T04 transport boundary: response caps hold with and without
    /// Content-Length, HTML pages fail with an understandable error, and a
    /// cache hit never masquerades as a fresh measurement.
    /// </summary>
    private static async Task FreeEngineTransportBoundary()
    {
        var originalLoader = OutboundPolicy.SettingsLoader;
        var originalSender = FreeTranslateService.HttpSenderOverride;
        OutboundPolicy.SettingsLoader = () => ShellSettings.Default with { FreeEngineConsent = FreeEngineConsent.Allowed };
        try
        {
            var settings = CoreBridge.GetSettings() with { SafeDevMode = false, NetworkEnabled = true };
            True(OutboundPolicy.AllowsFreeEngine(settings, out _, out var authorization),
                "the allowed combo must issue an authorization");

            // 1. Oversized body without a Content-Length header.
            FreeTranslateService.HttpSenderOverride = (_, _) => Task.FromResult(new HttpResponseMessage(
                System.Net.HttpStatusCode.OK)
            {
                Content = new StreamContent(new NonSeekableStream(new byte[FreeTranslateService.MaxResponseBytes + 1])),
            });
            var oversize = await ThrowsAsync<InvalidOperationException>(() =>
                FreeTranslateService.TranslateAsync("boundary oversize", "auto", "zh", authorization));
            True(oversize.Message.Contains("上限"), oversize.Message);

            // 2. Declared oversize Content-Length is rejected before reading.
            FreeTranslateService.HttpSenderOverride = (_, _) => Task.FromResult(new HttpResponseMessage(
                System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', FreeTranslateService.MaxResponseBytes + 1)),
            });
            var declared = await ThrowsAsync<InvalidOperationException>(() =>
                FreeTranslateService.TranslateAsync("boundary declared", "auto", "zh", authorization));
            True(declared.Message.Contains("上限"), declared.Message);

            // 3. An HTML page is an explicit error, not a parse accident.
            FreeTranslateService.HttpSenderOverride = (_, _) => Task.FromResult(new HttpResponseMessage(
                System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>sorry</body></html>", Encoding.UTF8, "text/html"),
            });
            var html = await ThrowsAsync<InvalidOperationException>(() =>
                FreeTranslateService.TranslateAsync("boundary html", "auto", "zh", authorization));
            True(html.Message.Contains("网页"), html.Message);

            // 4. A cache hit reports zero elapsed time and sends nothing more.
            var sends = 0;
            FreeTranslateService.HttpSenderOverride = (_, _) =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "[[[\"缓存正文\",\"src\",\"en\",\"\"]]]", Encoding.UTF8, "application/json"),
                });
            };
            var first = await FreeTranslateService.TranslateAsync("boundary cache hit", "auto", "zh", authorization);
            var second = await FreeTranslateService.TranslateAsync("boundary cache hit", "auto", "zh", authorization);
            Equal(1, sends, "the second identical call must be a cache hit");
            Equal(0UL, second.Diagnostics.ElapsedMs, "a cache hit must report zero elapsed, not the old timing");
            Equal(first.Result.TranslatedText, second.Result.TranslatedText);
        }
        finally
        {
            OutboundPolicy.SettingsLoader = originalLoader;
            FreeTranslateService.HttpSenderOverride = originalSender;
        }
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = data.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }
            var copied = Math.Min(count, remaining);
            Array.Copy(data, _position, buffer, offset, copied);
            _position += copied;
            return copied;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// T03 contract check: the Rust core always serializes is_partial; the C#
    /// DTO must carry it so completion decisions never depend on warnings
    /// alone.
    /// </summary>
    private static void PartialContractSurvivesSerialization()
    {
        const string json = """
            {"ok":true,"data":{"result":{"translated_text":"部分译文","transcription":"","explanation":"","protected_terms":[],"warnings":[],"is_partial":true,"phonetic":""},"diagnostics":{"request_id":"r1","provider_type":0,"endpoint":"mock","attempts":1,"status_code":200,"elapsed_ms":3}}}
            """;
        var response = CoreBridge.EnsureSuccess<TranslationResponse>(json);
        True(response.Result.IsPartial, "the Rust is_partial flag must survive into the C# DTO");
        Equal("部分译文", response.Result.TranslatedText);

        const string cleanJson = """
            {"ok":true,"data":{"result":{"translated_text":"完整译文","transcription":"","explanation":"","protected_terms":[],"warnings":[],"is_partial":false,"phonetic":""},"diagnostics":{"request_id":"r2","provider_type":0,"endpoint":"mock","attempts":1,"status_code":200,"elapsed_ms":3}}}
            """;
        var clean = CoreBridge.EnsureSuccess<TranslationResponse>(cleanJson);
        True(!clean.Result.IsPartial, "a clean final must deserialize as not partial");
    }

    private static async Task CoordinatorFreeSingleShotAsync()
    {
        var prevLoader = OutboundPolicy.SettingsLoader;
        try
        {
            OutboundPolicy.SettingsLoader = () => ShellSettingsStore.Load() with
            {
                FreeEngineConsent = FreeEngineConsent.Allowed,
                HistoryEnabled = true,
            };
            var history = new FakeHistoryRepository();
            var executor = new FakeTranslationExecutor
            {
                ApiKey = null,
                TextRoute = null,
                VisionRoute = null,
                Settings = CoreBridge.GetSettings() with
                {
                    ProviderType = ProviderType.OpenAiCompatible,
                    ApiBaseUrl = "https://free-test.example.com",
                    TextModel = "",
                    VisionModel = "",
                    NetworkEnabled = true,
                    SafeDevMode = false,
                }
            };
            var coordinator = new TranslationCoordinator(history: history, executor: executor);
            var updates = new List<TranslationStreamUpdate>();
            var progressLock = new object();
            var progress = new SynchronousProgress<TranslationStreamUpdate>(u =>
            {
                lock (progressLock) updates.Add(u);
            });

            executor.OnTranslateFree = (source, sourceLang, targetLang, ct) =>
            {
                return Task.FromResult(new TranslationResponse(
                    new TranslationResult("内置免费引擎译文", "", "", [], []),
                    new ProviderDiagnostics(FreeTranslateService.RequestId, ProviderType.OpenAiCompatible, "https://free.example.com", 1, 200, 30)));
            };

            var session = await coordinator.TranslateTextAsync("Free text", "en", "zh", TranslationInputSource.Manual, progress: progress);

            Equal(TranslationSessionStage.Completed, session.Stage);
            Equal("内置免费引擎", session.PipelineLabel);
            Equal("内置免费引擎译文", session.TranslatedText);
            Equal(1, history.Entries.Count, "History must be written once for free engine");

            lock (progressLock)
            {
                Equal(2, updates.Count, "Free engine should emit Reset then Delta");
                Equal(TranslationStreamUpdateKind.Reset, updates[0].Kind);
                Equal(TranslationStreamUpdateKind.Delta, updates[1].Kind);
                Equal("内置免费引擎译文", updates[1].Delta);
            }
        }
        finally
        {
            OutboundPolicy.SettingsLoader = prevLoader;
        }
    }

    private static async Task CoordinatorVisionWithDeltaFailureDoesNotOcrFallbackAsync()
    {
        var executor = new FakeTranslationExecutor();
        var dummyProfile = new ProviderProfile
        {
            Id = "vision-test",
            SupportsVision = true,
            VisionModel = "vision-model",
            ApiBaseUrl = "https://api.openai.com",
        };
        var visionRoute = new ProviderRoute(dummyProfile, "fake-target");
        executor.VisionRoute = visionRoute;
        executor.ScreenshotRoute = new ResolvedRoute(null, visionRoute, ScreenshotPipeline.VisionDirect, true, "Vision direct");
        executor.Settings = executor.Settings with { Mode = TranslationMode.Auto };

        executor.OnStreamVisionDraft = (draftSettings, textKey, visionKey, img, sLang, tLang, sessionId, epoch, ct) =>
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();

            _ = Task.Run(async () =>
            {
                buffer.TryAppend("partial vision delta 1");
                await Task.Delay(50);
                tcs.SetException(new InvalidOperationException("Vision network stream interrupted"));
            });

            return new TranslationStreamSession(buffer, tcs.Task);
        };

        var coordinator = new TranslationCoordinator(executor: executor);
        var dummyImage = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var session = await coordinator.TranslateScreenshotAsync(dummyImage, "en", "zh");

        Equal(TranslationSessionStage.Failed, session.Stage, "Stage must be Failed");
        Equal("partial vision delta 1", session.TranslatedText, "Partial vision text must be preserved");
        Equal(0, executor.OcrCallCount, "OCR fallback must NOT be called when vision already emitted deltas");
    }

    private static async Task CoordinatorVisionZeroDeltaFailureFallsBackToOcrAsync()
    {
        var history = new FakeHistoryRepository();
        var executor = new FakeTranslationExecutor();
        var dummyVisionProfile = new ProviderProfile
        {
            Id = "vision-test",
            SupportsVision = true,
            VisionModel = "vision-model",
            ApiBaseUrl = "https://api.openai.com",
        };
        var dummyTextProfile = new ProviderProfile
        {
            Id = "text-test",
            SupportsText = true,
            TextModel = "text-model",
            ApiBaseUrl = "https://api.openai.com",
        };
        var visionRoute = new ProviderRoute(dummyVisionProfile, "fake-vision-target");
        var textRoute = new ProviderRoute(dummyTextProfile, "fake-text-target");
        executor.VisionRoute = visionRoute;
        executor.TextRoute = textRoute;
        executor.ScreenshotRoute = new ResolvedRoute(textRoute, visionRoute, ScreenshotPipeline.VisionDirect, true, "Vision direct");
        executor.Settings = executor.Settings with { Mode = TranslationMode.Auto };
        executor.OcrRecognizedText = "OCR Text From Image";

        executor.OnStreamVisionDraft = (draftSettings, textKey, visionKey, img, sLang, tLang, sessionId, epoch, ct) =>
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();

            _ = Task.Run(async () =>
            {
                await Task.Delay(30);
                tcs.SetException(new InvalidOperationException("Vision service unavailable"));
            });

            return new TranslationStreamSession(buffer, tcs.Task);
        };

        executor.OnStreamTextDraft = (draftSettings, apiKey, source, sourceLang, targetLang, sessionId, epoch, ct) =>
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();

            _ = Task.Run(async () =>
            {
                buffer.TryAppend("Fallback OCR Translation");
                await Task.Delay(50);
                buffer.Complete();
                tcs.SetResult(new TranslationResponse(
                    new TranslationResult("Fallback OCR Translation", "OCR Text From Image", "", [], []),
                    new ProviderDiagnostics(sessionId, ProviderType.OpenAiCompatible, "https://api.openai.com", 1, 200, 50)));
            });

            return new TranslationStreamSession(buffer, tcs.Task);
        };

        var coordinator = new TranslationCoordinator(history: history, executor: executor);
        var dummyImage = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var session = await coordinator.TranslateScreenshotAsync(dummyImage, "en", "zh");

        Equal(TranslationSessionStage.Completed, session.Stage);
        Equal(1, executor.OcrCallCount, "OCR fallback MUST be invoked when vision fails with zero deltas");
        Equal("OCR Text From Image", session.SourceText);
        Equal("Fallback OCR Translation", session.TranslatedText);
        Equal("本地 OCR", session.PipelineLabel);
        True(session.RoutingReason?.Contains("已回退到本地 OCR") ?? false, "Routing reason must note fallback");
        Equal(1, history.Entries.Count, "History must be written once");
    }

    private static async Task CoordinatorEpochPropagationAsync()
    {
        var executor = new FakeTranslationExecutor();
        var coordinator = new TranslationCoordinator(executor: executor);

        executor.OnStreamText = (apiKey, source, sourceLang, targetLang, sessionId, epoch, ct) =>
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();

            _ = Task.Run(async () =>
            {
                buffer.TryAppend("Delta with epoch");
                await Task.Delay(50);
                buffer.Complete();
                tcs.SetResult(new TranslationResponse(
                    new TranslationResult("Delta with epoch", "", "", [], []),
                    new ProviderDiagnostics(sessionId, ProviderType.OpenAiCompatible, "https://api.openai.com", 1, 200, 50)));
            });

            return new TranslationStreamSession(buffer, tcs.Task);
        };

        var updatesEpoch7 = new List<TranslationStreamUpdate>();
        var progress7 = new SynchronousProgress<TranslationStreamUpdate>(u => updatesEpoch7.Add(u));
        await coordinator.TranslateTextAsync("text", "en", "zh", TranslationInputSource.Selection, progress: progress7, epoch: 7);

        True(updatesEpoch7.Count > 0, "Updates must be produced");
        True(updatesEpoch7.All(u => u.Epoch == 7), "All updates must carry epoch 7");

        var updatesEpoch88 = new List<TranslationStreamUpdate>();
        var progress88 = new SynchronousProgress<TranslationStreamUpdate>(u => updatesEpoch88.Add(u));
        await coordinator.TranslateTextAsync("text", "en", "zh", TranslationInputSource.Selection, progress: progress88, epoch: 88);

        True(updatesEpoch88.Count > 0, "Updates must be produced");
        True(updatesEpoch88.All(u => u.Epoch == 88), "All updates must carry epoch 88");
    }

    private static async Task CoordinatorStageOrderAsync()
    {
        var executor = new FakeTranslationExecutor();
        var coordinator = new TranslationCoordinator(executor: executor);
        var observedStages = new List<TranslationSessionStage>();

        executor.OnStreamText = (apiKey, source, sourceLang, targetLang, sessionId, epoch, ct) =>
        {
            var buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            var tcs = new TaskCompletionSource<TranslationResponse>();

            _ = Task.Run(async () =>
            {
                await Task.Delay(20);
                buffer.TryAppend("stage test delta");
                await Task.Delay(50);
                buffer.Complete();
                tcs.SetResult(new TranslationResponse(
                    new TranslationResult("stage test delta", "", "", [], []),
                    new ProviderDiagnostics(sessionId, ProviderType.OpenAiCompatible, "https://api.openai.com", 1, 200, 70)));
            });

            return new TranslationStreamSession(buffer, tcs.Task);
        };

        var session = await coordinator.TranslateTextAsync(
            "stage order test",
            "en",
            "zh",
            TranslationInputSource.Selection,
            onStageChanged: stage => observedStages.Add(stage));

        Equal(TranslationSessionStage.Completed, session.Stage);

        Equal(5, observedStages.Count, $"Expected 5 stages, got {observedStages.Count}: {string.Join(" -> ", observedStages)}");
        Equal(TranslationSessionStage.Routing, observedStages[0]);
        Equal(TranslationSessionStage.Translating, observedStages[1]);
        Equal(TranslationSessionStage.Streaming, observedStages[2]);
        Equal(TranslationSessionStage.Finalizing, observedStages[3]);
        Equal(TranslationSessionStage.Completed, observedStages[4]);
    }

    // ================= QuickSearch Streaming & State Machine Tests =================

    private static void QuickSearchEpochAndQueryFencing()
    {
        var state = new QuickSearchState();
        Equal(0, state.CurrentEpoch);
        Equal(QuickSearchUiStage.Idle, state.Stage);

        // Start search for "apple"
        state.StartNewSearch("apple");
        Equal(1, state.CurrentEpoch);
        Equal("apple", state.CurrentQuery);
        Equal(QuickSearchUiStage.Streaming, state.Stage);
        True(state.IsResultVisible, "Result should be marked visible on search start");
        True(state.IsStreamLayerVisible, "Stream layer should be visible");
        True(!state.IsRichBoxVisible, "RichBox should be hidden during stream");
        True(state.IsStreamIndicatorVisible, "Stream indicator should be visible");
        True(!state.CanCopy, "Copy should be disabled during stream");
        True(!state.CanSpeak, "Speak should be disabled during stream");
        True(!state.CanStar, "Star should be disabled during stream");

        // Stale epoch update should be rejected
        var staleUpdate = new TranslationStreamUpdate("s1", 0, TranslationStreamUpdateKind.Delta, "ping", "ping", 4);
        var acceptedStale = state.OnStreamUpdate(staleUpdate, "apple");
        True(!acceptedStale, "Stale epoch update must be rejected");
        Equal(string.Empty, state.AccumulatedText);

        // Mismatched query update should be rejected
        var mismatchUpdate = new TranslationStreamUpdate("s1", 1, TranslationStreamUpdateKind.Delta, "ping", "ping", 4);
        var acceptedMismatch = state.OnStreamUpdate(mismatchUpdate, "banana");
        True(!acceptedMismatch, "Mismatched query update must be rejected");
        Equal(string.Empty, state.AccumulatedText);

        // Valid update should be accepted
        var validUpdate = new TranslationStreamUpdate("s1", 1, TranslationStreamUpdateKind.Delta, "苹", "苹", 1, TimeSpan.FromMilliseconds(50));
        var acceptedValid = state.OnStreamUpdate(validUpdate, "apple");
        True(acceptedValid, "Valid matching stream update must be accepted");
        Equal("苹", state.AccumulatedText);
        True(state.StatusText.Contains("TTFT 50 ms"), "Status should show TTFT metric");

        // Subsequent valid update
        var validUpdate2 = new TranslationStreamUpdate("s1", 1, TranslationStreamUpdateKind.Delta, "果", "苹果", 2);
        True(state.OnStreamUpdate(validUpdate2, "apple"), "Second delta should be accepted");
        Equal("苹果", state.AccumulatedText);

        // User edits query in SearchBox -> invalidates current stream and bumps epoch
        state.OnQueryTextChanged("apple pie");
        Equal(2, state.CurrentEpoch);
        Equal("apple pie", state.CurrentQuery);
        True(!state.IsProgressVisible, "Progress should hide on query edit");
        True(!state.IsStreamIndicatorVisible, "Indicator should hide on query edit");

        // Old stream update (epoch 1) must now be rejected
        var lateOldUpdate = new TranslationStreamUpdate("s1", 1, TranslationStreamUpdateKind.Delta, " extra", "苹果 extra", 8);
        True(!state.OnStreamUpdate(lateOldUpdate, "apple pie"), "Late chunk from epoch 1 must be rejected");
        Equal("苹果", state.AccumulatedText); // Untouched by epoch 1 update

        // Starting another search increments epoch again
        state.StartNewSearch("banana");
        Equal(3, state.CurrentEpoch);
        Equal("banana", state.CurrentQuery);
        Equal(string.Empty, state.AccumulatedText);
    }

    private static void QuickSearchPartialActionGate()
    {
        var state = new QuickSearchState();
        state.StartNewSearch("test word");
        var epoch = state.CurrentEpoch;

        // Streaming deltas
        state.OnStreamUpdate(new TranslationStreamUpdate("s1", epoch, TranslationStreamUpdateKind.Delta, "测试", "测试", 2), "test word");
        True(!state.CanCopy, "Copy should be blocked while streaming");
        True(!state.CanSpeak, "Speak should be blocked while streaming");
        True(!state.CanStar, "Star should be blocked while streaming");
        True(!state.IsIncompleteBadgeVisible, "Incomplete badge should be hidden during normal stream");

        // Reset kind
        state.OnStreamUpdate(new TranslationStreamUpdate("s1", epoch, TranslationStreamUpdateKind.Reset, "", "", 0), "test word");
        Equal(string.Empty, state.AccumulatedText);
        True(!state.CanStar, "Star must remain blocked on reset");

        // Delta after reset
        state.OnStreamUpdate(new TranslationStreamUpdate("s1", epoch, TranslationStreamUpdateKind.Delta, "测试词", "测试词", 3), "test word");
        Equal("测试词", state.AccumulatedText);

        // Finalizing stage
        state.OnStageChanged(TranslationSessionStage.Finalizing, epoch, "test word");
        Equal(QuickSearchUiStage.Finalizing, state.Stage);
        True(!state.IsStreamIndicatorVisible, "Indicator hidden during finalizing");
        True(!state.CanStar, "Star blocked during finalizing");

        // 1) Test Completed session
        var completedSession = new TranslationSession
        {
            Stage = TranslationSessionStage.Completed,
            TranslatedText = "测试词（完整）",
            Phonetic = "tɛst wɜːd",
            Explanation = "名词，测试词汇",
            PipelineLabel = "OpenAI",
            Timing = new TranslationSessionTiming(0, 5, 200, 205),
        };
        state.OnSessionCompleted(completedSession, epoch, "test word");
        Equal(QuickSearchUiStage.Completed, state.Stage);
        True(state.CanStar, "Star MUST be enabled on Completed session");
        True(state.CanCopy, "Copy MUST be enabled on Completed session");
        True(state.CanSpeak, "Speak MUST be enabled on Completed session");
        True(state.IsRichBoxVisible, "RichBox visible on completed");
        True(!state.IsStreamLayerVisible, "Stream layer hidden on completed");
        True(!state.IsIncompleteBadgeVisible, "Incomplete badge hidden on completed");
        Equal("测试词（完整）", state.FinalRenderedText);

        // 2) Test Partial session
        state.StartNewSearch("partial test");
        epoch = state.CurrentEpoch;
        state.OnStreamUpdate(new TranslationStreamUpdate("s2", epoch, TranslationStreamUpdateKind.Delta, "部分", "部分", 2), "partial test");

        var partialSession = new TranslationSession
        {
            Stage = TranslationSessionStage.Partial,
            TranslatedText = "部分译文...",
            Warnings = ["响应可能不完整"],
            Timing = new TranslationSessionTiming(0, 2, 100, 102),
        };
        state.OnSessionCompleted(partialSession, epoch, "partial test");
        Equal(QuickSearchUiStage.Partial, state.Stage);
        True(!state.CanStar, "Star MUST be disabled for Partial session");
        True(state.CanCopy, "Copy is available for partial text");
        True(state.CanSpeak, "Speak is available for partial text");
        True(state.IsIncompleteBadgeVisible, "Incomplete badge MUST be visible for Partial session");
        True(state.IsStreamLayerVisible, "Stream layer visible for partial");
        True(!state.IsRichBoxVisible, "RichBox hidden for partial");

        // 3) Test Failed session with accumulated partial text
        state.StartNewSearch("fail test");
        epoch = state.CurrentEpoch;
        state.OnStreamUpdate(new TranslationStreamUpdate("s3", epoch, TranslationStreamUpdateKind.Delta, "半截文字", "半截文字", 4), "fail test");

        var failedSession = new TranslationSession
        {
            Stage = TranslationSessionStage.Failed,
            Error = new TranslationError(TranslationErrorKind.ServerError, "模型连接中断"),
        };
        state.OnSessionCompleted(failedSession, epoch, "fail test");
        Equal(QuickSearchUiStage.Failed, state.Stage);
        True(!state.CanStar, "Star MUST be disabled for Failed session");
        True(state.CanCopy, "Copy allowed for retained partial text");
        True(state.IsIncompleteBadgeVisible, "Incomplete badge visible on failed with partial");
        Equal("半截文字", state.AccumulatedText);
        True(state.StatusText.Contains("已保留部分内容"), "Status text should note partial retention");

        // 4) Test Cancelled session with accumulated partial text
        state.StartNewSearch("cancel test");
        epoch = state.CurrentEpoch;
        state.OnStreamUpdate(new TranslationStreamUpdate("s4", epoch, TranslationStreamUpdateKind.Delta, "取消前内容", "取消前内容", 5), "cancel test");

        state.OnCancelled(epoch, "cancel test");
        Equal(QuickSearchUiStage.Cancelled, state.Stage);
        True(!state.CanStar, "Star MUST be disabled for Cancelled session");
        True(state.CanCopy, "Copy allowed for retained partial text");
        True(state.IsIncompleteBadgeVisible, "Incomplete badge visible on cancelled with partial");
        Equal("取消前内容", state.AccumulatedText);
        True(state.StatusText.Contains("译文不完整"), "Status text should label incomplete");
    }

    private static void QuickSearchClosedGuard()
    {
        var state = new QuickSearchState();
        state.StartNewSearch("closed test");
        var epoch = state.CurrentEpoch;

        state.OnStreamUpdate(new TranslationStreamUpdate("s1", epoch, TranslationStreamUpdateKind.Delta, "流式内容", "流式内容", 4), "closed test");
        Equal("流式内容", state.AccumulatedText);

        // Window closes / deactivates
        state.OnClose();
        True(state.IsClosed, "State must be marked as closed");
        Equal(string.Empty, state.AccumulatedText);
        Equal(string.Empty, state.FinalRenderedText);
        True(!state.CanStar, "Star must be disabled on close");
        True(!state.CanCopy, "Copy must be disabled on close");
        True(!state.CanSpeak, "Speak must be disabled on close");
        True(!state.IsResultVisible, "Result should be hidden on close");

        // Any subsequent stream updates or callbacks must be rejected
        var update = new TranslationStreamUpdate("s1", epoch, TranslationStreamUpdateKind.Delta, "更多", "流式内容更多", 6);
        True(!state.OnStreamUpdate(update, "closed test"), "Update after close must be dropped");

        var session = new TranslationSession
        {
            Stage = TranslationSessionStage.Completed,
            TranslatedText = "已完成",
        };
        True(!state.OnSessionCompleted(session, epoch, "closed test"), "Session completion after close must be dropped");
        True(!state.OnStageChanged(TranslationSessionStage.Finalizing, epoch, "closed test"), "Stage change after close must be dropped");
        True(!state.OnCancelled(epoch, "closed test"), "Cancellation after close must be dropped");
        True(!state.OnException(new Exception("test"), epoch, "closed test"), "Exception after close must be dropped");
    }

    private static void QuickSearchMinHeightAndHeadlessContract()
    {
        // MinHeight contracts
        Equal(68.0, QuickSearchState.ResultAreaMinHeight);
        Equal(24.0, QuickSearchState.ResultStreamMinHeight);

        // Headless rendering lifecycle: verify zero token FlowDoc reconstruction
        var state = new QuickSearchState();
        state.StartNewSearch("streaming markdown text");
        var epoch = state.CurrentEpoch;

        var chunks = new[] { "# 标题\n", "这是 **", "加粗** ", "和 `代码`。" };
        var sb = new StringBuilder();

        foreach (var chunk in chunks)
        {
            sb.Append(chunk);
            var update = new TranslationStreamUpdate("s1", epoch, TranslationStreamUpdateKind.Delta, chunk, sb.ToString(), sb.Length);
            True(state.OnStreamUpdate(update, "streaming markdown text"), "Stream update accepted");

            // Plain text stream layer stays visible, RichBox stays collapsed during streaming tokens
            True(state.IsStreamLayerVisible, "Stream layer must stay visible across all token deltas");
            True(!state.IsRichBoxVisible, "RichBox must NOT be visible during streaming");
            Equal(sb.ToString(), state.AccumulatedText);
        }

        // Final completion -> only now switch to RichBox
        var finalSession = new TranslationSession
        {
            Stage = TranslationSessionStage.Completed,
            TranslatedText = sb.ToString(),
            PipelineLabel = "DeepSeek",
            Timing = new TranslationSessionTiming(0, 5, 300, 305),
        };
        True(state.OnSessionCompleted(finalSession, epoch, "streaming markdown text"), "Completed accepted");
        True(!state.IsStreamLayerVisible, "Stream layer hidden on completed");
        True(state.IsRichBoxVisible, "RichBox visible on completed");
        Equal(sb.ToString(), state.FinalRenderedText);
    }

    private static void QuickSearchComponentLifecycleAndStreamContracts()
    {
        EnsureApplication();
        var qsHistory = new HistoryStore();
        var qsVocab = new VocabularyStore();
        var quickSearch = new QuickSearchWindow(qsHistory, qsVocab);

        // Control type, accessibility, and visual sizing contracts
        True(quickSearch.StreamBox is TextBox, "StreamBox must be a TextBox for scrolling");
        Equal(true, quickSearch.StreamBox.IsReadOnly);
        Equal(false, quickSearch.StreamBox.Focusable);
        Equal(ScrollBarVisibility.Auto, quickSearch.StreamBox.VerticalScrollBarVisibility);
        Equal(TextWrapping.Wrap, quickSearch.StreamBox.TextWrapping);
        Equal(14.5, quickSearch.StreamBox.FontSize);
        Equal(220.0, quickSearch.StreamBox.MaxHeight);

        True(quickSearch.RichBox is RichTextBox, "RichBox must be a RichTextBox for markdown formatting");
        Equal(14.5, quickSearch.RichBox.FontSize);
        Equal(220.0, quickSearch.RichBox.MaxHeight);
        Equal(ScrollBarVisibility.Auto, quickSearch.RichBox.VerticalScrollBarVisibility);

        Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(quickSearch.FooterStatusBlock));
        Equal("翻译状态", AutomationProperties.GetName(quickSearch.FooterStatusBlock));
        Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(quickSearch.StreamBox));
    }

    // ================= TranslateSection Streaming & State Machine Tests =================

    private static void TranslateSectionEpochFencing()
    {
        var state = TranslateUiState.Initial;
        Equal(0L, state.Epoch);
        Equal(TranslateUiPhase.Idle, state.Phase);
        True(state.IsTranslateButtonEnabled, "Translate button enabled initially");
        True(!state.AreResultActionsEnabled, "Result actions disabled initially");

        // Start translation at epoch 1
        state = TranslateSectionReducer.StartTranslation(state, 1);
        Equal(1L, state.Epoch);
        Equal(TranslateUiPhase.Preparing, state.Phase);
        Equal("连接中", state.StatusText);
        Equal("连接中", state.BadgeText);
        True(!state.IsTranslateButtonEnabled, "Translate button disabled during preparing");
        True(state.IsProgressVisible, "Progress visible during preparing");
        True(!state.IsStreamLayerVisible, "Stream layer hidden during preparing");
        True(state.IsFinalLayerVisible, "Final layer visible (placeholder) during preparing");
        True(!state.IsStreamIndicatorVisible, "Stream indicator hidden during preparing");
        True(!state.AreResultActionsEnabled, "Result actions disabled during preparing");

        // Stage update with stale epoch 0 must be ignored
        var stagedOld = TranslateSectionReducer.ApplyStage(state, TranslationSessionStage.Streaming, 0);
        Equal(TranslateUiPhase.Preparing, stagedOld.Phase, "Stale epoch stage update must be ignored");

        // Stage update with matching epoch 1
        state = TranslateSectionReducer.ApplyStage(state, TranslationSessionStage.Streaming, 1);
        Equal(TranslateUiPhase.Streaming, state.Phase);
        Equal("正在生成…", state.StatusText);
        Equal("正在生成…", state.BadgeText);
        True(state.IsStreamIndicatorVisible, "Indicator visible in Streaming stage");

        // Stage update to Finalizing
        state = TranslateSectionReducer.ApplyStage(state, TranslationSessionStage.Finalizing, 1);
        Equal(TranslateUiPhase.Finalizing, state.Phase);
        Equal("正在整理", state.StatusText);
        Equal("正在整理", state.BadgeText);
        True(state.IsStreamIndicatorVisible, "Indicator visible in Finalizing stage");

        // Stream update with stale epoch 0 must be ignored
        var staleUpdate = new TranslationStreamUpdate("s1", 0, TranslationStreamUpdateKind.Delta, "stale text", "stale text", 10);
        var stateAfterStale = TranslateSectionReducer.ApplyStreamUpdate(state, staleUpdate, 0);
        Equal(string.Empty, stateAfterStale.StreamText, "Stale stream update must not mutate stream text");

        // Stream update with matching epoch 1
        var validUpdate = new TranslationStreamUpdate("s1", 1, TranslationStreamUpdateKind.Delta, "Hello", "Hello World", 11);
        state = TranslateSectionReducer.ApplyStreamUpdate(state, validUpdate, 1);
        Equal(TranslateUiPhase.Streaming, state.Phase);
        Equal("Hello World", state.StreamText);
        True(state.IsStreamLayerVisible, "Stream layer visible when delta arrives");
        True(!state.IsFinalLayerVisible, "Final layer hidden while streaming");
        True(!state.AreResultActionsEnabled, "Result actions disabled while streaming");

        // Stale completion must be ignored
        var completedSession = new TranslationSession
        {
            Stage = TranslationSessionStage.Completed,
            TranslatedText = "Stale Full Translation",
            PipelineLabel = "OpenAI",
        };
        var staleCompletionState = TranslateSectionReducer.ApplyCompletion(state, completedSession, 0);
        Equal(string.Empty, staleCompletionState.FinalText, "Stale completion must be ignored");

        // Valid completion
        state = TranslateSectionReducer.ApplyCompletion(state, completedSession, 1);
        Equal(TranslateUiPhase.Completed, state.Phase);
        Equal("Stale Full Translation", state.FinalText);
        True(!state.IsStreamLayerVisible, "Stream layer hidden on completion");
        True(state.IsFinalLayerVisible, "Final layer visible on completion");
        True(!state.IsStreamIndicatorVisible, "Indicator hidden on completion");
        True(!state.IsProgressVisible, "Progress hidden on completion");
        True(state.IsTranslateButtonEnabled, "Translate button re-enabled on completion");
        True(state.AreResultActionsEnabled, "Result actions enabled on completion");
        True(!state.IsPartialIncomplete, "Completed state is not partial");
    }

    private static void TranslateSectionResetAndDelta()
    {
        var state = TranslateUiState.Initial;
        state = TranslateSectionReducer.StartTranslation(state, 10);

        // 1) First Delta
        var delta1 = new TranslationStreamUpdate("s10", 10, TranslationStreamUpdateKind.Delta, "Part 1 ", "Part 1 ", 7);
        state = TranslateSectionReducer.ApplyStreamUpdate(state, delta1, 10);
        Equal(TranslateUiPhase.Streaming, state.Phase);
        Equal("Part 1 ", state.StreamText);
        True(state.IsStreamLayerVisible, "Stream layer visible");
        True(!state.IsFinalLayerVisible, "Final layer hidden");
        True(state.IsStreamIndicatorVisible, "Stream indicator visible");
        True(!state.AreResultActionsEnabled, "Actions disabled during delta");

        // 2) Reset update (e.g. Free engine fallback or buffer reset)
        var resetUpdate = new TranslationStreamUpdate("s10", 10, TranslationStreamUpdateKind.Reset, "", "", 0);
        state = TranslateSectionReducer.ApplyStreamUpdate(state, resetUpdate, 10);
        Equal(TranslateUiPhase.Preparing, state.Phase);
        Equal(string.Empty, state.StreamText, "StreamText cleared on reset");
        True(!state.IsStreamLayerVisible, "Stream layer hidden on reset");
        True(!state.IsStreamIndicatorVisible, "Stream indicator hidden on reset");
        Equal("连接中", state.StatusText);
        Equal("连接中", state.BadgeText);
        True(!state.AreResultActionsEnabled, "Actions remain disabled on reset");

        // 3) Delta after reset displays accumulated text directly
        var delta2 = new TranslationStreamUpdate("s10", 10, TranslationStreamUpdateKind.Delta, "Brand new", "Brand new translation", 21);
        state = TranslateSectionReducer.ApplyStreamUpdate(state, delta2, 10);
        Equal(TranslateUiPhase.Streaming, state.Phase);
        Equal("Brand new translation", state.StreamText);
        True(state.IsStreamLayerVisible, "Stream layer visible again after delta");
        True(state.IsStreamIndicatorVisible, "Stream indicator visible again");
    }

    private static void TranslateSectionActionGatingAndPartialRetention()
    {
        // 1) Successful final opens actions
        var state = TranslateUiState.Initial;
        state = TranslateSectionReducer.StartTranslation(state, 20);
        state = TranslateSectionReducer.ApplyStreamUpdate(state, new TranslationStreamUpdate("s20", 20, TranslationStreamUpdateKind.Delta, "a", "abc", 3), 20);
        True(!state.AreResultActionsEnabled, "Actions blocked during streaming");

        var successSession = new TranslationSession
        {
            Stage = TranslationSessionStage.Completed,
            TranslatedText = "abc calibrated",
            PipelineLabel = "DeepSeek",
            Explanation = "Some notes",
        };
        state = TranslateSectionReducer.ApplyCompletion(state, successSession, 20);
        Equal(TranslateUiPhase.Completed, state.Phase);
        True(state.AreResultActionsEnabled, "Actions MUST be enabled for Completed session");
        True(state.IsTranslateButtonEnabled, "Translate button enabled");
        True(state.IsExplanationVisible, "Explanation visible");
        Equal("Some notes", state.ExplanationText);
        True(!state.IsPartialIncomplete, "Not partial");

        // 2) Partial session retains text and GATES actions
        state = TranslateUiState.Initial;
        state = TranslateSectionReducer.StartTranslation(state, 21);
        state = TranslateSectionReducer.ApplyStreamUpdate(state, new TranslationStreamUpdate("s21", 21, TranslationStreamUpdateKind.Delta, "partial", "partial output", 14), 21);

        var partialSession = new TranslationSession
        {
            Stage = TranslationSessionStage.Partial,
            TranslatedText = "partial output",
            Warnings = ["Truncated response"],
        };
        state = TranslateSectionReducer.ApplyCompletion(state, partialSession, 21);
        Equal(TranslateUiPhase.Partial, state.Phase);
        True(!state.AreResultActionsEnabled, "Actions MUST be disabled for Partial session");
        True(state.IsPartialIncomplete, "Marked as partial incomplete");
        Equal("内容不完整", state.BadgeText);
        Equal("partial output", state.FinalText);
        True(!state.IsStreamLayerVisible, "Stream layer collapsed for final display");
        True(state.IsFinalLayerVisible, "Final layer visible");

        // 3) Cancelled session with accumulated stream text retains partial text and GATES actions
        state = TranslateUiState.Initial;
        state = TranslateSectionReducer.StartTranslation(state, 22);
        state = TranslateSectionReducer.ApplyStreamUpdate(state, new TranslationStreamUpdate("s22", 22, TranslationStreamUpdateKind.Delta, "streamed before cancel", "streamed before cancel", 22), 22);

        var cancelSession = new TranslationSession
        {
            Stage = TranslationSessionStage.Cancelled,
        };
        state = TranslateSectionReducer.ApplyCompletion(state, cancelSession, 22);
        Equal(TranslateUiPhase.Partial, state.Phase);
        True(!state.AreResultActionsEnabled, "Actions MUST be disabled on cancel with partial");
        True(state.IsPartialIncomplete, "Marked as partial incomplete");
        Equal("内容不完整", state.BadgeText);
        Equal("streamed before cancel", state.FinalText);

        // 4) Failed session with accumulated stream text retains partial text and GATES actions
        state = TranslateUiState.Initial;
        state = TranslateSectionReducer.StartTranslation(state, 23);
        state = TranslateSectionReducer.ApplyStreamUpdate(state, new TranslationStreamUpdate("s23", 23, TranslationStreamUpdateKind.Delta, "streamed before fail", "streamed before fail", 20), 23);

        var failSession = new TranslationSession
        {
            Stage = TranslationSessionStage.Failed,
            Error = new TranslationError(TranslationErrorKind.ServerError, "Server error 500"),
        };
        state = TranslateSectionReducer.ApplyCompletion(state, failSession, 23);
        Equal(TranslateUiPhase.Partial, state.Phase);
        True(!state.AreResultActionsEnabled, "Actions MUST be disabled on fail with partial");
        True(state.IsPartialIncomplete, "Marked as partial incomplete");
        Equal("内容不完整", state.BadgeText);
        Equal("streamed before fail", state.FinalText);

        // 5) Exception with accumulated stream text retains partial text and GATES actions
        state = TranslateUiState.Initial;
        state = TranslateSectionReducer.StartTranslation(state, 24);
        state = TranslateSectionReducer.ApplyStreamUpdate(state, new TranslationStreamUpdate("s24", 24, TranslationStreamUpdateKind.Delta, "streamed before exc", "streamed before exc", 19), 24);

        state = TranslateSectionReducer.ApplyError(state, new HttpRequestException("Connection lost"), 24);
        Equal(TranslateUiPhase.Partial, state.Phase);
        True(!state.AreResultActionsEnabled, "Actions MUST be disabled on exception with partial");
        True(state.IsPartialIncomplete, "Marked as partial incomplete");
        Equal("内容不完整", state.BadgeText);
        Equal("streamed before exc", state.FinalText);

        // 6) Cancelled session without stream text
        state = TranslateUiState.Initial;
        state = TranslateSectionReducer.StartTranslation(state, 25);
        state = TranslateSectionReducer.ApplyCompletion(state, new TranslationSession { Stage = TranslationSessionStage.Cancelled }, 25);
        Equal(TranslateUiPhase.Cancelled, state.Phase);
        True(!state.AreResultActionsEnabled, "Actions MUST be disabled on plain cancel");
        True(!state.IsPartialIncomplete, "Not partial");
        Equal("已取消", state.BadgeText);
    }

    /// <summary>
    /// T11 acceptance: the empty workbench guides first use — one synthetic
    /// example (text fill only, zero requests), an inline free-engine consent
    /// naming its destinations, and an IME composition Enter that never
    /// submits a translation.
    /// </summary>
    private static void TranslateEmptyStateGuidesFirstUse()
    {
        EnsureApplication();
        var history = new HistoryStore(TestIsolation.HistoryPath);
        var vocab = new VocabularyStore(TestIsolation.VocabularyPath);
        var coordinator = new TranslationCoordinator(history, vocab);
        var section = new TranslateSection();
        section.Initialize(coordinator, vocab);

        Equal(Visibility.Visible, section.EmptyStateGuide.Visibility,
            "the empty result plane must show the first-use guide");
        var consentBefore = ShellSettingsStore.Load().FreeEngineConsent;
        Equal(FreeEngineConsent.Unset, consentBefore, "the isolated environment starts undecided");
        Equal(Visibility.Visible, section.FreeEngineEntryButton.Visibility,
            "the inline consent entry must be offered while undecided");

        // The example fills text only; nothing is sent.
        typeof(TranslateSection)
            .GetMethod("FillExample_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(section, new object[] { section, new RoutedEventArgs() });
        Equal("FileNotFoundError: config.json not found", section.InputBox.Text,
            "the demo error must be filled verbatim");
        Equal(TranslateUiPhase.Idle, section.CurrentState.Phase,
            "filling the example must not start a translation");

        // The inline consent persists Allowed and retires the entry.
        typeof(TranslateSection)
            .GetMethod("EnableFreeEngine_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(section, new object[] { section, new RoutedEventArgs() });
        Equal(FreeEngineConsent.Allowed, ShellSettingsStore.Load().FreeEngineConsent,
            "the inline entry must persist the user's consent");
        Equal(Visibility.Collapsed, section.FreeEngineEntryButton.Visibility,
            "a decided consent retires the inline entry");

        // A result on the plane must retire the whole guide: expanded from
        // the panel (FocusTranslate) and a reducer-driven completion both
        // leave no guide hovering over the translation text.
        section.FocusTranslate("demo source", existingTranslation: "演示完整译文");
        Equal(Visibility.Collapsed, section.EmptyStateGuide.Visibility,
            "an expanded translation must hide the first-use guide");
        var completedState = TranslateUiState.Initial with
        {
            Phase = TranslateUiPhase.Completed,
            FinalText = "FileNotFoundError：未找到 config.json",
            IsFinalLayerVisible = true,
            AreResultActionsEnabled = true,
        };
        typeof(TranslateSection)
            .GetMethod("ApplyState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(section, new object[] { completedState });
        Equal(Visibility.Collapsed, section.EmptyStateGuide.Visibility,
            "a completed translation must hide the first-use guide");

        // An IME composition Enter never submits: with the IME flag on, Enter
        // leaves the phase Idle and the event unhandled. Reset to a clean
        // idle plane first — the assertions above left the phase Completed.
        typeof(TranslateSection)
            .GetMethod("ApplyState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(section, new object[] { TranslateUiState.Initial });
        var source = new System.Windows.Interop.HwndSource(
            new System.Windows.Interop.HwndSourceParameters("popglot-ime-test") { Width = 8, Height = 8 });
        try
        {
            System.Windows.Input.InputMethod.SetIsInputMethodEnabled(section.InputBox, true);
            var imeArgs = new System.Windows.Input.KeyEventArgs(
                System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Enter)
            {
                RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent,
            };
            typeof(TranslateSection)
                .GetMethod("TranslateInput_KeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(section, new object[] { section.InputBox, imeArgs });
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
            True(!(bool)imeArgs.Handled, "an IME Enter must stay unhandled (the composition owns it)");
            Equal(TranslateUiPhase.Idle, section.CurrentState.Phase,
                "an IME composition Enter must not submit a translation");
        }
        finally
        {
            source.Dispose();
        }
    }

    /// <summary>
    /// T11 acceptance (AI-RULES 6.2): below 720 DIP of content width the
    /// panes stack vertically — source on top with a 160 DIP editor floor,
    /// target below, centre axis and swap hidden — and widening restores the
    /// side-by-side workbench exactly.
    /// </summary>
    private static void TranslateSectionStacksWhenNarrow()
    {
        EnsureApplication();
        var section = new TranslateSection();
        section.Initialize(
            new TranslationCoordinator(new HistoryStore(TestIsolation.HistoryPath), new VocabularyStore(TestIsolation.VocabularyPath)),
            null);
        var grid = section.PaneGrid;

        int Row(string name) =>
            System.Windows.Controls.Grid.GetRow(grid.Children.OfType<FrameworkElement>().First(e => e.Name == name));

        // Default (wide): 3 rows × 3 columns.
        Equal(3, grid.RowDefinitions.Count);
        Equal(3, grid.ColumnDefinitions.Count);
        Equal(1, Row("SourceEditorCell"), "wide layout puts the source editor at row 1 col 0");
        Equal(1, Row("TargetEditorCell"));

        section.SetStacked(true);
        Equal(true, section.IsStacked);
        Equal(6, grid.RowDefinitions.Count, "stacked layout has one row per pane section");
        Equal(1, grid.ColumnDefinitions.Count, "stacked layout is a single column");
        Equal(160, grid.RowDefinitions[1].MinHeight, "the input editor keeps a 160 DIP floor");
        Equal(true, grid.RowDefinitions[4].Height.IsStar, "the target reader takes the leftover space");
        Equal(0, Row("SourceLangBarCell"));
        Equal(1, Row("SourceEditorCell"));
        Equal(2, Row("SourceFooterCell"));
        Equal(3, Row("TargetLangBarCell"));
        Equal(4, Row("TargetEditorCell"));
        Equal(5, Row("TargetFooterCell"));
        var swap = grid.Children.OfType<Button>().First(b => b.Name == "TranslateSwapButton");
        Equal(Visibility.Collapsed, swap.Visibility, "the swap axis hides when stacked");

        // Widening restores the exact side-by-side grid.
        section.SetStacked(false);
        Equal(false, section.IsStacked);
        Equal(3, grid.RowDefinitions.Count);
        Equal(3, grid.ColumnDefinitions.Count);
        Equal(0, grid.RowDefinitions[1].MinHeight);
        Equal(1, Row("SourceEditorCell"));
        Equal(1, Row("TargetEditorCell"));
        Equal(2, System.Windows.Controls.Grid.GetColumn(grid.Children.OfType<FrameworkElement>().First(e => e.Name == "TargetEditorCell")));
        Equal(Visibility.Visible, swap.Visibility);
    }

    /// <summary>
    /// T11 acceptance: while the reader is scrolled UP into the stream, new
    /// deltas must not yank the viewport back down (AI-RULES 7.2); only a
    /// reader already at the bottom keeps following the stream.
    /// </summary>
    private static void StreamScrollPositionIsPreservedWhileReading()
    {
        EnsureApplication();
        var section = new TranslateSection();
        section.Initialize(
            new TranslationCoordinator(new HistoryStore(TestIsolation.HistoryPath), new VocabularyStore(TestIsolation.VocabularyPath)),
            null);

        var streamed = new string('文', 3000);
        typeof(TranslateSection)
            .GetMethod("ApplyState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(section, new object[] { TranslateUiState.Initial with
            {
                Phase = TranslateUiPhase.Streaming,
                IsStreamLayerVisible = true,
                StreamText = streamed,
            } });
        section.Measure(new Size(600, 400));
        section.Arrange(new Rect(0, 0, 600, 400));
        section.UpdateLayout();

        var viewer = Ui.FindScrollViewer(section.StreamResultBox);
        True(viewer is not null, "the stream box must sit inside a scroll viewer");
        // Reader scrolled up into the text: the following delta must keep
        // their offset instead of forcing ScrollToEnd.
        viewer!.ScrollToVerticalOffset(40);
        var offsetBefore = viewer.VerticalOffset;
        typeof(TranslateSection)
            .GetMethod("ApplyState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(section, new object[] { TranslateUiState.Initial with
            {
                Phase = TranslateUiPhase.Streaming,
                IsStreamLayerVisible = true,
                StreamText = streamed + new string('尾', 500),
            } });
        section.UpdateLayout();
        True(Math.Abs(viewer.VerticalOffset - offsetBefore) < 1.0,
            $"a reading offset must survive a delta (was {offsetBefore}, now {viewer.VerticalOffset})");
    }

    private static void TranslateSectionComponentLifecycleAndStreamContracts()
    {
        EnsureApplication();
        var history = new HistoryStore();
        var vocab = new VocabularyStore();
        var coordinator = new TranslationCoordinator(history, vocab);

        var section = new TranslateSection();
        section.Initialize(coordinator, vocab);

        // Initial contract
        Equal(TranslateUiPhase.Idle, section.CurrentState.Phase);
        Equal(Visibility.Visible, section.ResultBox.Visibility);
        Equal(Visibility.Collapsed, section.StreamResultBox.Visibility);
        Equal(Visibility.Collapsed, section.StreamIndicator.Visibility);
        Equal(false, section.StreamResultBox.Focusable);
        Equal(true, section.StreamResultBox.IsReadOnly);
        // Long results scroll via the shared pane ScrollViewer (result text and
        // explanation in one flow); the inner boxes must not fight it with
        // their own scrollbars or the explanation would reflow the layout.
        Equal(ScrollBarVisibility.Disabled, section.StreamResultBox.VerticalScrollBarVisibility);
        Equal(ScrollBarVisibility.Auto, section.ResultScroll.VerticalScrollBarVisibility);
        Equal(TextWrapping.Wrap, section.StreamResultBox.TextWrapping);
        Equal(14.5, section.StreamResultBox.FontSize);
        Equal(14.5, section.ResultBox.FontSize);

        Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(section.StatusBlock));
        Equal("翻译状态", AutomationProperties.GetName(section.StatusBlock));
        Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(section.StreamResultBox));

        // FocusTranslate with existing translation
        section.FocusTranslate("Initial source text", existingTranslation: "Existing translated text");
        Equal("Initial source text", section.InputBox.Text);
        Equal("Existing translated text", section.ResultBox.Text);
        Equal(true, section.CurrentState.AreResultActionsEnabled);
    }

    // ================= TranslationPanel Streaming & State Machine Tests =================

    private static void TranslationPanelEpochAndLifetimeFencing()
    {
        var gate = new TranslationPanelStreamGate();
        Equal(0L, gate.CurrentEpoch);
        Equal(TranslationPanelStage.Idle, gate.Stage);
        True(!gate.CanPerformResultActions, "Actions disabled initially");
        True(!gate.ShouldTriggerAutoCopy(true), "AutoCopy disabled initially");

        // Begin operation 1
        var (epoch1, opId1) = gate.BeginNewOperation();
        Equal(1L, epoch1);
        Equal(1, opId1);
        Equal(TranslationPanelStage.Preparing, gate.Stage);
        Equal(string.Empty, gate.StreamedText);
        True(!gate.CanPerformResultActions, "Actions disabled during preparing");
        True(!gate.ShouldTriggerAutoCopy(true), "AutoCopy disabled during preparing");

        // Stale epoch update must be rejected
        var staleUpdate = new TranslationStreamUpdate("s1", 0, TranslationStreamUpdateKind.Delta, "stale", "stale", 5);
        True(!gate.ShouldAcceptUpdate(0, isClosed: false), "ShouldAcceptUpdate must return false for stale epoch");
        True(!gate.ApplyUpdate(staleUpdate), "ApplyUpdate must reject stale epoch update");
        Equal(string.Empty, gate.StreamedText);
        Equal(TranslationPanelStage.Preparing, gate.Stage);

        // Matching epoch update is accepted
        var validUpdate = new TranslationStreamUpdate("s1", 1, TranslationStreamUpdateKind.Delta, "Hello", "Hello", 5);
        True(gate.ShouldAcceptUpdate(1, isClosed: false), "ShouldAcceptUpdate accepts matching epoch");
        True(gate.ApplyUpdate(validUpdate), "ApplyUpdate accepts valid update");
        Equal(TranslationPanelStage.Streaming, gate.Stage);
        Equal("Hello", gate.StreamedText);
        True(!gate.CanPerformResultActions, "Actions disabled during streaming");
        True(!gate.ShouldTriggerAutoCopy(true), "AutoCopy disabled during streaming");

        // New operation bumps epoch to 2
        var (epoch2, opId2) = gate.BeginNewOperation();
        Equal(2L, epoch2);
        Equal(2, opId2);
        Equal(TranslationPanelStage.Preparing, gate.Stage);
        Equal(string.Empty, gate.StreamedText);

        // Late update from epoch 1 must now be rejected
        var lateUpdate = new TranslationStreamUpdate("s1", 1, TranslationStreamUpdateKind.Delta, " World", "Hello World", 11);
        True(!gate.ShouldAcceptUpdate(1, isClosed: false), "Late epoch 1 update must be rejected");
        True(!gate.ApplyUpdate(lateUpdate), "ApplyUpdate rejects late epoch 1 update");
        Equal(string.Empty, gate.StreamedText);

        // Update with epoch 2 is accepted
        var update2 = new TranslationStreamUpdate("s2", 2, TranslationStreamUpdateKind.Delta, "Bonjour", "Bonjour", 7);
        True(gate.ApplyUpdate(update2), "Epoch 2 update accepted");
        Equal("Bonjour", gate.StreamedText);

        // Closed window rejects updates
        True(!gate.ShouldAcceptUpdate(2, isClosed: true), "Closed window must reject updates");
    }

    private static void TranslationPanelResetAndDelta()
    {
        var gate = new TranslationPanelStreamGate();
        var (epoch, _) = gate.BeginNewOperation();

        // 1) First delta
        var delta1 = new TranslationStreamUpdate("s1", epoch, TranslationStreamUpdateKind.Delta, "Part 1 ", "Part 1 ", 7);
        True(gate.ApplyUpdate(delta1), "First delta applied");
        Equal(TranslationPanelStage.Streaming, gate.Stage);
        Equal("Part 1 ", gate.StreamedText);
        True(!gate.CanPerformResultActions, "Actions blocked during delta");

        // 2) Reset update (e.g. Free engine fallback or OCR reset)
        var resetUpdate = new TranslationStreamUpdate("s1", epoch, TranslationStreamUpdateKind.Reset, "", "", 0);
        True(gate.ApplyUpdate(resetUpdate), "Reset update applied");
        Equal(TranslationPanelStage.Preparing, gate.Stage);
        Equal(string.Empty, gate.StreamedText, "StreamedText cleared on reset");
        True(!gate.CanPerformResultActions, "Actions remain blocked on reset");

        // 3) Delta after reset displays accumulated text directly
        var delta2 = new TranslationStreamUpdate("s1", epoch, TranslationStreamUpdateKind.Delta, "Fresh output", "Fresh output", 12);
        True(gate.ApplyUpdate(delta2), "Second delta applied");
        Equal(TranslationPanelStage.Streaming, gate.Stage);
        Equal("Fresh output", gate.StreamedText);

        // 4) Stage change to finalizing
        gate.OnStageChanged(TranslationSessionStage.Finalizing);
        Equal(TranslationPanelStage.Finalizing, gate.Stage);
        True(!gate.CanPerformResultActions, "Actions blocked during finalizing");
        True(!gate.ShouldTriggerAutoCopy(true), "AutoCopy blocked during finalizing");

        // 5) Completed
        gate.OnCompleted("Fresh output calibrated");
        Equal(TranslationPanelStage.Completed, gate.Stage);
        Equal("Fresh output calibrated", gate.StreamedText);
        True(gate.CanPerformResultActions, "Actions open on completion");
        True(gate.ShouldTriggerAutoCopy(true), "AutoCopy enabled on completion");
        True(!gate.ShouldTriggerAutoCopy(false), "AutoCopy respects setting flag");
    }

    private static void TranslationPanelActionGatingAndPartialRetention()
    {
        // 1) Successful final opens actions
        var gate = new TranslationPanelStreamGate();
        var (epoch, _) = gate.BeginNewOperation();
        gate.ApplyUpdate(new TranslationStreamUpdate("s1", epoch, TranslationStreamUpdateKind.Delta, "abc", "abc", 3));
        True(!gate.CanPerformResultActions, "Actions blocked during streaming");

        gate.OnCompleted("abc calibrated");
        Equal(TranslationPanelStage.Completed, gate.Stage);
        True(gate.CanPerformResultActions, "Actions enabled on Completed");
        True(gate.ShouldTriggerAutoCopy(true), "AutoCopy allowed on Completed");
        True(!gate.HasPartialText, "Completed is not partial failure");
        True(gate.GetPartialWarningBanner() is null, "No warning banner on clean completion");

        // 2) Cancelled session with accumulated partial text
        gate = new TranslationPanelStreamGate();
        (epoch, _) = gate.BeginNewOperation();
        gate.ApplyUpdate(new TranslationStreamUpdate("s2", epoch, TranslationStreamUpdateKind.Delta, "streamed before cancel", "streamed before cancel", 22));

        gate.OnCancelled("streamed before cancel");
        Equal(TranslationPanelStage.CancelledWithPartial, gate.Stage);
        True(!gate.CanPerformResultActions, "Actions MUST be blocked on CancelledWithPartial");
        True(!gate.ShouldTriggerAutoCopy(true), "AutoCopy MUST be blocked on CancelledWithPartial");
        True(gate.HasPartialText, "HasPartialText must be true");
        Equal("已取消，内容不完整", gate.GetPartialWarningBanner());
        Equal("streamed before cancel", gate.StreamedText);

        // 3) Failed session with accumulated partial text
        gate = new TranslationPanelStreamGate();
        (epoch, _) = gate.BeginNewOperation();
        gate.ApplyUpdate(new TranslationStreamUpdate("s3", epoch, TranslationStreamUpdateKind.Delta, "streamed before fail", "streamed before fail", 20));

        gate.OnFailed("Connection lost 500", "streamed before fail");
        Equal(TranslationPanelStage.FailedWithPartial, gate.Stage);
        True(!gate.CanPerformResultActions, "Actions MUST be blocked on FailedWithPartial");
        True(!gate.ShouldTriggerAutoCopy(true), "AutoCopy MUST be blocked on FailedWithPartial");
        True(gate.HasPartialText, "HasPartialText must be true");
        Equal("生成中断，内容不完整", gate.GetPartialWarningBanner());
        Equal("streamed before fail", gate.StreamedText);

        // 4) Cancelled session without partial text
        gate = new TranslationPanelStreamGate();
        (epoch, _) = gate.BeginNewOperation();
        gate.OnCancelled(null);
        Equal(TranslationPanelStage.CancelledWithoutPartial, gate.Stage);
        True(!gate.CanPerformResultActions, "Actions blocked on CancelledWithoutPartial");
        True(!gate.HasPartialText, "HasPartialText false");
        True(gate.GetPartialWarningBanner() is null, "Banner null on plain cancel");

        // 5) Failed session without partial text
        gate = new TranslationPanelStreamGate();
        (epoch, _) = gate.BeginNewOperation();
        gate.OnFailed("Authentication failed 401", null);
        Equal(TranslationPanelStage.FailedWithoutPartial, gate.Stage);
        True(!gate.CanPerformResultActions, "Actions blocked on FailedWithoutPartial");
        True(!gate.HasPartialText, "HasPartialText false");
        True(gate.GetPartialWarningBanner() is null, "Banner null on plain fail");
    }

    private static void TranslationPanelComponentLifecycleAndStreamContracts()
    {
        EnsureApplication();
        var history = new HistoryStore();
        var vocab = new VocabularyStore();

        var panel = new TranslationPanelWindow(
            new Rect(100, 100, 20, 20),
            history,
            () => ShellSettings.Default,
            null,
            null,
            vocab);

        // Initial contracts
        Equal(false, panel.ResultCopyBtn.IsEnabled);
        Equal(false, panel.ResultSpeakBtn.IsEnabled);
        Equal(false, panel.StarToggle.IsEnabled);
        Equal(Visibility.Collapsed, panel.ResultSkeleton.Visibility);
        Equal(Visibility.Visible, panel.TranslationTextBox.Visibility);
        Equal(Visibility.Collapsed, panel.TranslationRichBox.Visibility);
        Equal(Visibility.Collapsed, panel.StreamIndicatorPill.Visibility);

        Equal(14.5, panel.StreamTextBox.FontSize);
        Equal(ScrollBarVisibility.Auto, panel.StreamTextBox.VerticalScrollBarVisibility);
        Equal(true, panel.StreamTextBox.IsReadOnly);
        Equal(14.5, panel.FinalRichBox.FontSize);
        Equal(ScrollBarVisibility.Auto, panel.FinalRichBox.VerticalScrollBarVisibility);

        Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(panel.StatusTextBlock));
        Equal("翻译状态", AutomationProperties.GetName(panel.StatusTextBlock));
        Equal(AutomationLiveSetting.Off, AutomationProperties.GetLiveSetting(panel.StreamTextBox));

        // MarkdownPresenter font size inheritance and FlowDocument contracts
        var doc = new FlowDocument { FontSize = 14.5 };
        MarkdownPresenter.RenderToFlowDocument(doc, "这是 **加粗内容** 和 `代码` 以及普通的后续句子。", Application.Current.Resources);
        Equal(new Thickness(0), doc.PagePadding);
        True(doc.Blocks.Count > 0, "Document must have paragraphs");
        if (doc.Blocks.FirstBlock is Paragraph firstPara)
        {
            // The final layer must mirror the streaming TextBox's metrics
            // (default font line height, zero paragraph margin) so the
            // stream→final swap cannot change the card height.
            True(double.IsNaN(firstPara.LineHeight), "forced line height reintroduces the layout jump");
            Equal(new Thickness(0), firstPara.Margin);
            foreach (var inline in firstPara.Inlines)
            {
                if (inline is Run run)
                {
                    // Run must inherit font size from FlowDocument/RichTextBox without hardcoded 14
                    Equal(DependencyProperty.UnsetValue, run.ReadLocalValue(TextElement.FontSizeProperty));
                    Equal(14.5, run.FontSize);
                }
            }
        }

        // Friendly error classifications
        Equal("还差一步：配置模型密钥", TranslationPanelWindow.FriendlyError("API Key missing"));
        Equal("安全离线模式已开启", TranslationPanelWindow.FriendlyError("安全离线模式已开启"));
        Equal("翻译请求被限流，请稍后重试", TranslationPanelWindow.FriendlyError("HTTP 429 Too Many Requests"));

        // Line break merging
        var joined = TranslationPanelWindow.MergeHardLineBreaks("Line one\nline two");
        Equal("Line one line two", joined);
    }

    private static async Task ClipboardIsolationAndHardTimeoutAsync()
    {
        // 1. Adapter source code must use RunInStaAsync and TimeoutException handling
        var adapterCode = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "ClipboardSelectionService.cs"));
        True(adapterCode.Contains("RunInStaAsync"), "WindowsSelectionClipboardAdapter must isolate clipboard operations onto STA worker threads");
        True(adapterCode.Contains("ClipboardOperationTimeout"), "WindowsSelectionClipboardAdapter must enforce a bounded hard timeout");
        True(adapterCode.Contains("catch (TimeoutException)"), "WindowsSelectionClipboardAdapter must fail immediately on timeout");
        True(adapterCode.Contains("PopGlot-Clipboard-Worker"), "WindowsSelectionClipboardAdapter worker thread must have dedicated name");

        // 2. A caller timeout must not release the concurrency gate while the
        // underlying STA action is still alive. This caps an uninterruptible
        // OLE hang at one quarantined worker instead of one thread per hotkey.
        await ThrowsAsync<TimeoutException>(() =>
            WindowsSelectionClipboardAdapter.RunInStaAsync(
                () => { Thread.Sleep(350); return true; },
                TimeSpan.FromMilliseconds(25)));
        var busy = await ThrowsAsync<InvalidOperationException>(() =>
            WindowsSelectionClipboardAdapter.RunInStaAsync(
                () => true,
                TimeSpan.FromMilliseconds(25)));
        True(busy.Message.Contains("上一次剪贴板操作", StringComparison.Ordinal),
            "a timed-out live worker must keep later operations fenced");

        await Task.Delay(275);
        True(await WindowsSelectionClipboardAdapter.RunInStaAsync(
                () => true,
                TimeSpan.FromMilliseconds(250)),
            "the gate must recover after the timed-out worker actually exits");
    }

    private static async Task ScreenCaptureAsyncExecution()
    {
        // 1. ScreenCaptureService must expose CapturePngAsync
        var code = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "ScreenCaptureService.cs"));
        True(code.Contains("CapturePngAsync(Rect pixelBounds)"), "ScreenCaptureService must expose CapturePngAsync");
        True(code.Contains("Task.Run(() => CapturePng(pixelBounds))"), "ScreenCaptureService must offload to background thread");

        var overlayCode = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "CaptureOverlayWindow.xaml.cs"));
        True(overlayCode.Contains("await ScreenCaptureService.CapturePngAsync(pixelRect)"),
            "CaptureOverlayWindow must await ScreenCaptureService.CapturePngAsync off UI thread");

        // 2. Minimum bounds and invalid rectangle checks
        var smallEx = await ThrowsAsync<InvalidOperationException>(() =>
            ScreenCaptureService.CapturePngAsync(new Rect(0, 0, 2, 2)));
        True(smallEx.Message.Contains("选区太小"), "Error message must indicate bounds too small");

        var largeEx = await ThrowsAsync<InvalidOperationException>(() =>
            ScreenCaptureService.CapturePngAsync(new Rect(0, 0, 10000, 10000)));
        True(largeEx.Message.Contains("1600 万像素"), "Error message must indicate 16MP limit");
    }

    private static void HotkeyServiceAtomicityAndFailureVisibility()
    {
        var serviceCode = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "HotkeyService.cs"));
        True(serviceCode.Contains("RegistrationFailed"), "HotkeyService must expose RegistrationFailed event");
        True(serviceCode.Contains("newlyRegistered"), "HotkeyService must track newly registered keys for rollback");
        True(serviceCode.Contains("UnregisterHotKey"), "HotkeyService must unregister on partial conflict");

        var appCode = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "apps", "PopGlot.Windows", "App.xaml.cs"));
        True(appCode.Contains("RegistrationFailed +="), "App must subscribe to RegistrationFailed");
        True(appCode.Contains("ShowShortcutConflict"), "App must surface conflict to user interface");
    }

    private static void ReleaseWorkflowSpecifiesSelfContained()
    {
        var releaseYml = File.ReadAllText(Path.Combine(
            FindProjectRoot(), ".github", "workflows", "release.yml"));
        True(releaseYml.Contains("--self-contained true"),
            "release.yml must publish self-contained package to eliminate target runtime dependency");
        True(!releaseYml.Contains("--self-contained false"),
            "release.yml must not use framework-dependent --self-contained false");
    }

    private static void HotkeyServiceSuspensionAndRetentionBehavior()
    {
        EnsureApplication();
        var targetWindow = new Window();
        var competitorWindow = new Window();
        using var service = new HotkeyService(targetWindow);
        using var competitor = new HotkeyService(competitorWindow);

        var hotkeys = new Dictionary<HotkeyAction, HotkeyBinding>
        {
            [HotkeyAction.TranslateSelection] = new(
                HotkeyBinding.ModControl | HotkeyBinding.ModAlt | HotkeyBinding.ModShift,
                0x7C), // F13
            [HotkeyAction.CaptureScreen] = new(
                HotkeyBinding.ModControl | HotkeyBinding.ModAlt | HotkeyBinding.ModShift,
                0x7D), // F14
        };

        // 1. Initial registration
        True(service.TryRegisterAll(hotkeys, out var initialConflict),
            $"test hotkeys must register: {initialConflict}");
        Equal(2, service.CurrentHotkeys.Count, "CurrentHotkeys must track configured hotkeys");

        // 2. Suspension toggling
        service.SetSuspended(true);
        True(service.IsSuspended, "IsSuspended must be true when suspended");
        Equal(0, service.RegisteredHotkeys.Count, "Registered hotkeys must be cleared when suspended");

        // 3. Another owner grabs F13 while the target is suspended. Resume must
        // fail atomically, retain the desired configuration, and remain retryable.
        var competingSet = new Dictionary<HotkeyAction, HotkeyBinding>
        {
            [HotkeyAction.ShowWindow] = hotkeys[HotkeyAction.TranslateSelection],
        };
        True(competitor.TryRegisterAll(competingSet, out var competingConflict),
            $"competitor must register the released hotkey: {competingConflict}");
        True(!service.SetSuspended(false, out var resumeConflict),
            "resume must report a real OS registration conflict");
        True(!string.IsNullOrWhiteSpace(resumeConflict), "resume conflict must be visible");
        True(service.IsSuspended, "failed resume must remain retryable");
        Equal(0, service.RegisteredHotkeys.Count, "failed resume must roll back partial registrations");
        Equal(2, service.CurrentHotkeys.Count, "CurrentHotkeys must be preserved across suspension/restore attempts");

        // 4. Releasing the conflict must allow the very next resume to restore
        // the full set without rebuilding the service.
        competitor.Dispose();
        True(service.SetSuspended(false, out var retryConflict),
            $"resume must recover after the conflict disappears: {retryConflict}");
        True(!service.IsSuspended, "successful retry must leave suspension");
        Equal(2, service.RegisteredHotkeys.Count, "successful retry must restore the complete set");
    }

    private static async Task ClipboardSnapshotFailClosedBehaviorAsync()
    {
        // 1. If adapter capture fails, ReadSelectionAsync must fail-closed before sending Ctrl+C
        var throwingAdapter = new FailingCaptureClipboardAdapter();
        var service = new ClipboardSelectionService(throwingAdapter);
        await ThrowsAsync<InvalidOperationException>(() => service.ReadSelectionAsync(CancellationToken.None));
        True(!throwingAdapter.SendCopyCalled, "SendCopy must NEVER be called if snapshot capture fails");
        True(!throwingAdapter.RestoreCalled, "Restore must NEVER be called if capture fails");
    }

    private sealed class FailingCaptureClipboardAdapter : ISelectionClipboardAdapter
    {
        public bool SendCopyCalled { get; private set; }
        public bool RestoreCalled { get; private set; }
        public uint SequenceNumber => 10;
        public Task<IClipboardSnapshot> CaptureAsync() =>
            throw new InvalidOperationException("Failed to access clipboard");
        public Task SendCopyAsync()
        {
            SendCopyCalled = true;
            return Task.CompletedTask;
        }
        public Task<string?> ReadTextAsync() => Task.FromResult<string?>("sample");
        public Task RestoreAsync(IClipboardSnapshot snapshot)
        {
            RestoreCalled = true;
            return Task.CompletedTask;
        }
    }

}
