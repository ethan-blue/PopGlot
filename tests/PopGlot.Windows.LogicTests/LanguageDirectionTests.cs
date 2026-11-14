using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.LogicTests;

internal static partial class Program
{
    private static async Task LanguageDirectionLoopbackAndSameLanguageGuard()
    {
        EnsureApplication();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var originalConfig = ProfileManager.Load();
        var originalSettings = CoreBridge.GetSettings();
        var originalShell = ShellSettingsStore.Load();
        var credentialTarget = $"PopGlot/tests/language-direction-{Guid.NewGuid():N}";
        var historyPath = Path.Combine(TestIsolation.Root, $"language-direction-{Guid.NewGuid():N}.json");
        var history = new HistoryStore(historyPath);
        Window? window = null;
        Window? sameLanguageWindow = null;
        Task<List<JsonElement>>? server = null;
        try
        {
            var config = ProfileManager.Load();
            var profile = config.TryGetActiveProfile() ?? throw new InvalidOperationException("the isolated text profile is missing");
            profile.ApiBaseUrl = $"http://127.0.0.1:{port}/v1";
            profile.TextModel = "language-direction-loopback";
            profile.SupportsText = true;
            profile.IsLocal = true;
            profile.CredentialTarget = credentialTarget;
            ProfileManager.Save(config);
            ProfileManager.ApplyActiveToCore(config);
            CredentialStore.SaveApiKey("loopback-only-language-test-key", credentialTarget);
            await CoreBridge.UpdateSettingsAsync(settings => settings with
            {
                SourceLanguage = LanguageCatalog.Auto,
                TargetLanguage = "zh-CN",
                NetworkEnabled = true,
                SafeDevMode = false,
            });
            ShellSettingsStore.Save(originalShell with { HistoryEnabled = true });

            server = Task.Run(async () =>
            {
                var bodies = new List<JsonElement>(3);
                for (var index = 0; index < 3; index++)
                {
                    using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(8));
                    using var network = client.GetStream();
                    bodies.Add(await ReadSummaryHttpRequestAsync(network));
                    await network.WriteAsync(Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"));
                    await WriteSummarySseDeltaAsync(network, "loopback translation result");
                    var terminal = JsonSerializer.Serialize(new
                    {
                        choices = new[] { new { delta = new { }, finish_reason = "stop" } },
                    });
                    try
                    {
                        await WriteSummaryHttpChunkAsync(network, Encoding.UTF8.GetBytes($"data: {terminal}\n\n"));
                        await WriteSummaryHttpChunkAsync(network, "data: [DONE]\n\n"u8.ToArray());
                        await network.WriteAsync("0\r\n\r\n"u8.ToArray());
                    }
                    // Best-effort tail: the client may close once it has the
                    // terminal (Release-mode Rust clients close sooner than Debug).
                    catch (IOException) { }
                    catch (SocketException) { }
                }
                return bodies;
            });

            var section = new TranslateSection();
            section.Initialize(new TranslationCoordinator(history: history), vocabulary: null);
            section.TranslateInput.Text = "中文中文中文 hello";
            window = new Window
            {
                Content = section,
                Width = 720,
                Height = 620,
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = -12000,
                Top = -12000,
            };
            window.Show();
            FlushDispatcher(2);

            await section.TranslateAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Equal("en", ((LanguageOption)section.TranslateTargetLang.SelectedItem).Tag,
                "Chinese auto-source input must select English before sending");
            await WaitForLanguagePairAsync("auto", "en");
            section.TranslateInput.Text = "Hello world from an English source";
            await section.TranslateAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Equal("zh-CN", ((LanguageOption)section.TranslateTargetLang.SelectedItem).Tag,
                "English auto-source input must select Simplified Chinese before sending");
            await WaitForLanguagePairAsync("auto", "zh-CN");
            section.TranslateInput.Clear();
            section.TranslateSourceLang.SelectedItem = LanguageCatalog.ResolveSource("ja");
            section.TranslateInput.Text = "こんにちは世界";
            await section.TranslateAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Equal("zh-CN", ((LanguageOption)section.TranslateTargetLang.SelectedItem).Tag,
                "an explicitly selected Japanese source must leave the current target unchanged");


            var bodies = await server.WaitAsync(TimeSpan.FromSeconds(8));
            Equal(3, bodies.Count, "each workbench translation must reach the local provider exactly once");
            AssertRequestTarget(bodies[0], "English");
            AssertRequestTarget(bodies[1], "Simplified Chinese");
            AssertRequestTarget(bodies[2], "Simplified Chinese");
            var entries = history.Load();
            var chineseEntry = entries.Single(entry => entry.Source == "中文中文中文 hello");
            Equal("auto", chineseEntry.SourceLanguage);
            Equal("en", chineseEntry.TargetLanguage);
            var englishEntry = entries.Single(entry => entry.Source == "Hello world from an English source");
            Equal("auto", englishEntry.SourceLanguage);
            Equal("zh-CN", englishEntry.TargetLanguage);
            var japaneseEntry = entries.Single(entry => entry.Source == "こんにちは世界");
            Equal("ja", japaneseEntry.SourceLanguage);
            Equal("zh-CN", japaneseEntry.TargetLanguage);

            await CoreBridge.UpdateSettingsAsync(settings => settings with
            {
                SourceLanguage = "zh-CN",
                TargetLanguage = "zh-CN",
            });
            var executor = new FakeTranslationExecutor();
            var sends = 0;
            executor.OnStreamText = (_, _, _, _, _, _, _) =>
            {
                Interlocked.Increment(ref sends);
                throw new InvalidOperationException("same-language protection must run before transport");
            };
            executor.OnStreamTextTask = (_, _, _, _, _, _, _, _, _, _) =>
            {
                Interlocked.Increment(ref sends);
                throw new InvalidOperationException("same-language protection must run before transport");
            };
            var sameLanguageSection = new TranslateSection();
            sameLanguageSection.Initialize(new TranslationCoordinator(executor: executor), vocabulary: null);
            sameLanguageSection.TranslateInput.Text = "两个方向相同";
            sameLanguageWindow = new Window
            {
                Content = sameLanguageSection,
                Width = 720,
                Height = 620,
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = -12000,
                Top = -12000,
            };
            sameLanguageWindow.Show();
            FlushDispatcher(2);
            await sameLanguageSection.TranslateAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Equal(0, sends, "same-language requests must stop before any provider executor call");
            Equal("原文与目标语言相同，请更换语言方向。", sameLanguageSection.StatusBlock.Text);
        }
        finally
        {
            sameLanguageWindow?.Close();
            window?.Close();
            listener.Stop();
            if (server is not null)
            {
                try { await server; }
                catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or TimeoutException) { }
            }
            try { CredentialStore.DeleteApiKey(credentialTarget); } catch (Exception) { }
            ProfileManager.Save(originalConfig);
            ProfileManager.ApplyActiveToCore(originalConfig);
            CoreBridge.SaveSettings(originalSettings);
            ShellSettingsStore.Save(originalShell);
            try { File.Delete(historyPath); } catch (IOException) { }
        }
    }

    private static void AssertRequestTarget(JsonElement body, string target)
    {
        var systemMessage = body.GetProperty("messages").EnumerateArray()
            .First(message => message.GetProperty("role").GetString() == "system")
            .GetProperty("content").GetString();
        True(systemMessage?.Contains(target, StringComparison.OrdinalIgnoreCase) == true,
            $"the actual local request prompt must target {target}, got '{systemMessage}'");
    }

    private static async Task WaitForLanguagePairAsync(string source, string target)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var settings = CoreBridge.GetSettings();
            if (LanguageCatalog.Normalize(settings.SourceLanguage) == source &&
                LanguageCatalog.Normalize(settings.TargetLanguage) == target) return;
            await Task.Delay(10);
        }
        var finalSettings = CoreBridge.GetSettings();
        Equal(source, LanguageCatalog.Normalize(finalSettings.SourceLanguage));
        Equal(target, LanguageCatalog.Normalize(finalSettings.TargetLanguage));
    }
    private static async Task LanguageSwapLoopbackReplacesSourceAndRetranslates()
    {
        EnsureApplication();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var originalConfig = ProfileManager.Load();
        var originalSettings = CoreBridge.GetSettings();
        var originalShell = ShellSettingsStore.Load();
        var credentialTarget = $"PopGlot/tests/language-swap-{Guid.NewGuid():N}";
        var historyPath = Path.Combine(TestIsolation.Root, $"language-swap-{Guid.NewGuid():N}.json");
        var history = new HistoryStore(historyPath);
        var received = Enumerable.Range(0, 8)
            .Select(_ => new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var streamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Window? workbenchWindow = null;
        TranslationPanelWindow? panel = null;
        QuickSearchWindow? quickSearch = null;
        Task? server = null;

        try
        {
            var config = ProfileManager.Load();
            var profile = config.TryGetActiveProfile() ?? throw new InvalidOperationException("the isolated text profile is missing");
            profile.ApiBaseUrl = $"http://127.0.0.1:{port}/v1";
            profile.TextModel = "language-swap-loopback";
            profile.SupportsText = true;
            profile.IsLocal = true;
            profile.CredentialTarget = credentialTarget;
            ProfileManager.Save(config);
            ProfileManager.ApplyActiveToCore(config);
            CredentialStore.SaveApiKey("loopback-only-language-test-key", credentialTarget);
            await CoreBridge.UpdateSettingsAsync(settings => settings with
            {
                SourceLanguage = "en",
                TargetLanguage = "zh-CN",
                NetworkEnabled = true,
                SafeDevMode = false,
            });
            ShellSettingsStore.Save(originalShell with { HistoryEnabled = true });

            server = Task.Run(async () =>
            {
                var handlers = new List<Task>(received.Length);
                for (var index = 0; index < received.Length; index++)
                {
                    var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    var requestIndex = index;
                    handlers.Add(HandleRequestAsync(client, requestIndex));
                }
                await Task.WhenAll(handlers);
            });

            async Task HandleRequestAsync(TcpClient client, int index)
            {
                using (client)
                using (var network = client.GetStream())
                {
                    received[index].TrySetResult(await ReadSummaryHttpRequestAsync(network));
                    await network.WriteAsync(Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"));
                    if (index == 6)
                    {
                        await WriteSummarySseDeltaAsync(network, "stream partial");
                        streamStarted.TrySetResult();
                        await releaseStream.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    }
                    else
                    {
                        await WriteSummarySseDeltaAsync(network, "loopback translation result");
                    }

                    var terminal = JsonSerializer.Serialize(new
                    {
                        choices = new[] { new { delta = new { }, finish_reason = "stop" } },
                    });
                    try
                    {
                        await WriteSummaryHttpChunkAsync(network, Encoding.UTF8.GetBytes($"data: {terminal}\n\n"));
                        await WriteSummaryHttpChunkAsync(network, "data: [DONE]\n\n"u8.ToArray());
                        await network.WriteAsync("0\r\n\r\n"u8.ToArray());
                    }
                    // The client may close the connection as soon as it has the
                    // terminal (or, for the in-flight swap at index 6, when the
                    // request is cancelled). The [DONE]/chunk terminator writes
                    // are best-effort; a disconnect there is not a test failure.
                    // Release-mode Rust clients close sooner than Debug, so this
                    // must tolerate IOException/SocketException for every handler.
                    catch (IOException) { }
                    catch (SocketException) { }
                }
            }

            var workbench = new TranslateSection();
            workbench.Initialize(new TranslationCoordinator(history: history), vocabulary: null);
            workbench.TranslateInput.Text = "workbench source";
            workbenchWindow = new Window
            {
                Content = workbench,
                Width = 720,
                Height = 620,
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = -12000,
                Top = -12000,
            };
            workbenchWindow.Show();
            FlushDispatcher(2);

            await workbench.TranslateAsync().WaitAsync(TimeSpan.FromSeconds(8));
            var firstWorkbenchRequest = await received[0].Task.WaitAsync(TimeSpan.FromSeconds(8));
            Equal("workbench source", RequestSourceText(firstWorkbenchRequest));
            AssertRequestTarget(firstWorkbenchRequest, "Simplified Chinese");
            Equal("loopback translation result", workbench.CurrentState.FinalText);
            var historyBeforeWorkbenchRepeat = history.Load().Count;
            await workbench.TranslateAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Equal("loopback translation result", workbench.CurrentState.FinalText);
            Equal(TranslationRoundTripCache.ReusedStatus, workbench.CurrentState.StatusText);
            Equal(historyBeforeWorkbenchRepeat, history.Load().Count,
                "repeating a completed workbench translation must hit cache without another history write");

            var historyBeforeWorkbenchSwap = history.Load().Count;
            ClickButton((System.Windows.Controls.Button)workbench.FindName("TranslateSwapButton"));
            await WaitUntilAsync(() => workbench.CurrentState.Phase == TranslateUiPhase.Completed &&
                workbench.CurrentState.FinalText == "workbench source",
                "workbench reverse swap must reuse the exact earlier source result");
            Equal("loopback translation result", workbench.TranslateInput.Text);
            Equal(TranslationRoundTripCache.ReusedStatus, workbench.CurrentState.StatusText);
            Equal(historyBeforeWorkbenchSwap, history.Load().Count,
                "a cached reverse translation must not create a history entry");

            workbench.TranslateInput.Clear();
            ClickButton((System.Windows.Controls.Button)workbench.FindName("TranslateSwapButton"));
            workbench.TranslateInput.Text = "source for target selection";
            workbench.TranslateTargetLang.SelectedItem = LanguageCatalog.ResolveTarget("ja");
            var targetChangeRequest = await received[1].Task.WaitAsync(TimeSpan.FromSeconds(8));
            Equal("source for target selection", RequestSourceText(targetChangeRequest));
            AssertRequestTarget(targetChangeRequest, "Japanese");
            await WaitUntilAsync(() => workbench.CurrentState.Phase == TranslateUiPhase.Completed,
                $"changing the target with source text must retranslate; phase={workbench.CurrentState.Phase}, status={workbench.CurrentState.StatusText}");
            await CoreBridge.UpdateSettingsAsync(settings => settings with
            {
                SourceLanguage = "en",
                TargetLanguage = "zh-CN",
            });
            panel = new TranslationPanelWindow(
                new Rect(100, 100, 20, 20), history, () => ShellSettings.Default,
                vocabulary: new VocabularyStore(TestIsolation.VocabularyPath));
            panel.Show();
            FlushDispatcher(2);
            await panel.StartTextAsync("panel source").WaitAsync(TimeSpan.FromSeconds(8));
            var firstPanelRequest = await received[2].Task.WaitAsync(TimeSpan.FromSeconds(8));
            Equal("panel source", RequestSourceText(firstPanelRequest));
            ClickButton(panel.SwapLangButton);
            await WaitUntilAsync(() => panel.CurrentSessionState == TranslationSessionState.Completed &&
                panel.StatusTextBlock.Text == TranslationRoundTripCache.ReusedStatus,
                "floating-panel reverse swap must render a cached completion");
            Equal("loopback translation result", panel.SourceInputBox.Text);
            Equal(TranslationRoundTripCache.ReusedStatus, panel.StatusTextBlock.Text);
            var panelReverseText = new System.Windows.Documents.TextRange(
                panel.FinalRichBox.Document.ContentStart, panel.FinalRichBox.Document.ContentEnd).Text.Trim();
            Equal("panel source", panelReverseText,
                "floating-panel cache must restore the exact earlier source instead of issuing a request");

            var retryButton = (System.Windows.Controls.Button)typeof(TranslationPanelWindow)
                .GetField("RetryButton", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(panel)!;
            ClickButton(retryButton);
            var panelRetryRequest = await received[3].Task.WaitAsync(TimeSpan.FromSeconds(8));
            Equal("loopback translation result", RequestSourceText(panelRetryRequest));
            AssertRequestTarget(panelRetryRequest, "English");
            await WaitUntilAsync(() => panel.CurrentSessionState == TranslationSessionState.Completed &&
                panel.StatusTextBlock.Text != TranslationRoundTripCache.ReusedStatus,
                "manual retry after cache reuse must reach a fresh provider completion");

            await CoreBridge.UpdateSettingsAsync(settings => settings with
            {
                SourceLanguage = "en",
                TargetLanguage = "zh-CN",
            });
            quickSearch = new QuickSearchWindow(history, new VocabularyStore(TestIsolation.VocabularyPath));
            quickSearch.Show();
            FlushDispatcher(2);
            quickSearch.SearchInputBox.Text = "quick search source";
            await InvokeQuickSearchTranslateAsync(quickSearch);
            var firstQuickSearchRequest = await received[4].Task.WaitAsync(TimeSpan.FromSeconds(8));
            Equal("quick search source", RequestSourceText(firstQuickSearchRequest));
            await WaitUntilAsync(() => quickSearch.State.FinalRenderedText == "loopback translation result",
                "quick-search initial result must complete before swap");
            await InvokeQuickSearchTranslateAsync(quickSearch);
            await WaitUntilAsync(() => quickSearch.State.Stage == QuickSearchUiStage.Completed &&
                quickSearch.State.CurrentQuery == "quick search source" &&
                quickSearch.State.FinalRenderedText == "loopback translation result",
                "repeating the same quick-search query must reuse the completed result");
            Equal(TranslationRoundTripCache.ReusedStatus, quickSearch.State.StatusText);

            await CoreBridge.UpdateSettingsAsync(settings => settings with { IncludeExplanation = false });
            var concurrentSettingsUpdate = Task.Run(async () =>
                await CoreBridge.UpdateSettingsAsync(settings => settings with { IncludeExplanation = true }));
            ClickButton(quickSearch.LangBadgeButton);
            await concurrentSettingsUpdate.WaitAsync(TimeSpan.FromSeconds(8));
            await WaitUntilAsync(() => quickSearch.State.Stage == QuickSearchUiStage.Completed &&
                quickSearch.State.CurrentQuery == "loopback translation result" &&
                quickSearch.State.FinalRenderedText == "quick search source",
                "quick-search reverse swap must restore the cached original without a provider request");
            Equal("loopback translation result", quickSearch.SearchInputBox.Text);
            Equal(TranslationRoundTripCache.ReusedStatus, quickSearch.State.StatusText);
            True(CoreBridge.GetSettings().IncludeExplanation,
                "language swap persistence must preserve a concurrent update to another setting");
            quickSearch.SearchInputBox.Text = "quick search source edited";
            await InvokeQuickSearchTranslateAsync(quickSearch);
            var editedQuickSearchRequest = await received[5].Task.WaitAsync(TimeSpan.FromSeconds(8));
            Equal("quick search source edited", RequestSourceText(editedQuickSearchRequest));
            AssertRequestTarget(editedQuickSearchRequest, "English");
            await WaitUntilAsync(() => quickSearch.State.FinalRenderedText == "loopback translation result",
                "changing quick-search source text must issue a fresh provider request");

            await CoreBridge.UpdateSettingsAsync(settings => settings with
            {
                SourceLanguage = "en",
                TargetLanguage = "zh-CN",
            });
            workbench.TranslateInput.Clear();
            workbench.TranslateSourceLang.SelectedItem = LanguageCatalog.ResolveSource("en");
            workbench.TranslateTargetLang.SelectedItem = LanguageCatalog.ResolveTarget("zh-CN");
            workbench.TranslateInput.Text = "stream source";
            var originalStream = workbench.TranslateAsync();
            await streamStarted.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await WaitUntilAsync(() => workbench.CurrentState.Phase == TranslateUiPhase.Streaming,
                "the first stream delta must be visible before swap");
            var swapButton = (System.Windows.Controls.Button)workbench.FindName("TranslateSwapButton");
            True(swapButton.IsEnabled, "swap must remain available while a translation streams");
            ClickButton(swapButton);
            var streamSwapRequest = await received[7].Task.WaitAsync(TimeSpan.FromSeconds(8));
            Equal("stream partial", RequestSourceText(streamSwapRequest));
            AssertRequestTarget(streamSwapRequest, "English");
            await originalStream.WaitAsync(TimeSpan.FromSeconds(8));
            releaseStream.TrySetResult();
            await WaitUntilAsync(() => workbench.CurrentState.Phase == TranslateUiPhase.Completed,
                "the swapped stream must complete without accepting stale output");

            await server.WaitAsync(TimeSpan.FromSeconds(20));
            Equal(8, received.Count(item => item.Task.IsCompletedSuccessfully),
                "only initial translations, the target change, manual retry, edited query and in-flight swap may reach the provider");
        }
        finally
        {
            releaseStream.TrySetResult();
            if (quickSearch is not null)
            {
                quickSearch.ForceClose = true;
                quickSearch.Close();
            }
            if (panel is not null)
            {
                panel.ForceClose = true;
                panel.Close();
            }
            workbenchWindow?.Close();
            listener.Stop();
            if (server is not null)
            {
                try { await server; }
                catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or TimeoutException) { }
            }
            try { CredentialStore.DeleteApiKey(credentialTarget); } catch (Exception) { }
            ProfileManager.Save(originalConfig);
            ProfileManager.ApplyActiveToCore(originalConfig);
            CoreBridge.SaveSettings(originalSettings);
            ShellSettingsStore.Save(originalShell);
            try { File.Delete(historyPath); } catch (IOException) { }
        }
    }

    private static string RequestSourceText(JsonElement body)
    {
        var userMessage = body.GetProperty("messages").EnumerateArray()
            .First(message => message.GetProperty("role").GetString() == "user");
        using var payload = JsonDocument.Parse(userMessage.GetProperty("content").GetString()!);
        return payload.RootElement.GetProperty("source_text").GetString()!;
    }

    private static void ClickButton(System.Windows.Controls.Button button) =>
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static async Task InvokeQuickSearchTranslateAsync(QuickSearchWindow window)
    {
        var method = typeof(QuickSearchWindow).GetMethod(
            "PerformTranslateAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("QuickSearchWindow.PerformTranslateAsync was not found");
        var operation = method.Invoke(window, null) as Task
            ?? throw new InvalidOperationException("QuickSearchWindow.PerformTranslateAsync returned no task");
        await operation.WaitAsync(TimeSpan.FromSeconds(8));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        True(condition(), failure);
    }
}
