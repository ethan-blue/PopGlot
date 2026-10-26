using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PopGlot.Windows.Sections;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.LogicTests;

internal static partial class Program
{
    private static async Task ProductSummaryStreamsAndReusesVisibleTask()
    {
        EnsureApplication();
        var executor = new ControlledSummaryExecutor();
        var section = new TranslateSection();
        var directory = Path.Combine(Path.GetTempPath(), "popglot-summary-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        section.Initialize(new TranslationCoordinator(executor: executor), vocabulary: null);
        section.FocusTranslate("original source", "zh-CN", "en", "保留的译文", TranslationSessionState.Completed);
        var window = new Window
        {
            Content = section, Width = 720, Height = 620,
            ShowInTaskbar = false, ShowActivated = false, Left = -12000, Top = -12000,
        };
        try
        {
            window.Show();
            FlushDispatcher(2);
            var request = section.TriggerSummaryAsync();
            executor.Buffer!.TryAppend("第一段要点");
            SpinUntil(() => section.SummaryStreamText == "第一段要点", "first summary body must paint before terminal");
            True(!request.IsCompleted, "a visible body cannot depend on terminal completion");
            Equal("第一段要点", section.StreamResultBox.Text);
            True(section.StreamResultBox.IsVisible && !section.ResultBox.IsVisible,
                "stream and previous final cannot occupy the same visible result layer");
            True(!section.StarButton.IsEnabled && !((Button)section.FindName("TranslateResultSpeakButton")).IsEnabled,
                "pending summary must not save or speak the backing translation");
            SaveProductSurface(window, "workbench-summary-stream");
            section.ShowTranslationReading();
            Equal("保留的译文", section.ResultBox.Text);
            await section.TriggerSummaryAsync();
            Equal(1, executor.RequestCount, "returning to the active summary must not submit a second inference");
            Equal("第一段要点", section.StreamResultBox.Text);
            executor.Buffer.TryAppend("，但响应未完整结束");
            executor.Complete("第一段要点，但响应未完整结束", partial: true);
            await request;
            True(section.IsHoldingSummary && !section.IsSummaryComplete,
                "an unfinished response remains a partial reading, not a successful summary");
            Equal("第一段要点，但响应未完整结束", section.SummaryStreamText);
            True(((Button)section.FindName("TranslateResultCopyButton")).IsEnabled,
                "received partial content remains manually copyable");
            True(!((Button)section.FindName("TranslateResultSpeakButton")).IsEnabled && !section.StarButton.IsEnabled,
                "partial summary must not open speech or vocabulary side effects");
            section.ShowTranslationReading();
            Equal("保留的译文", section.ResultBox.Text);
            True(section.StarButton.IsEnabled, "returning to completed translation restores its own action gate");
        }
        finally
        {
            section.CancelSummary();
            window.Close();
            Directory.Delete(directory, true);
        }
    }

    private static void ProductQuickRouteSwitchRetranslatesAndPreservesVision()
    {
        EnsureApplication();
        var (_, directory) = NewIsolatedTranslateSection();
        var originalSettings = CoreBridge.GetSettings();
        var shell = ShellSettingsStore.Load();
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var requestCount = 0;
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                var body = await ReadSummaryHttpRequestAsync(stream);
                Equal(index == 0 ? "text-a" : "text-b", body.GetProperty("model").GetString(),
                    "the real inference request must follow the selected route");
                Interlocked.Increment(ref requestCount);
                var instructions = body.GetProperty("messages")[0].GetProperty("content").GetString()!;
                var marker = System.Text.RegularExpressions.Regex.Match(instructions, @"PGMETA-[a-f0-9-]+").Value;
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"));
                if (index == 1)
                {
                    await WriteSummarySseDeltaAsync(stream, "新路线");
                    secondStarted.TrySetResult();
                    await finishSecond.Task;
                }
                await WriteSummarySseDeltaAsync(stream, (index == 0 ? "保留的查询结果" : "的查询结果") +
                    "\n" + marker + "\n{\"detected_source_lang\":\"en\",\"explanation\":\"\",\"warnings\":[]}");
                await WriteSummaryHttpChunkAsync(stream, "data: [DONE]\n\n"u8.ToArray());
                await stream.WriteAsync("0\r\n\r\n"u8.ToArray());
            }
        });
        QuickSearchWindow? window = null;
        ContextMenu? menu = null;
        try
        {
            var first = new ProviderProfile
            {
                Id = "quick-first", Name = "本机 A", TextModel = "text-a",
                ApiBaseUrl = $"http://127.0.0.1:{port}/v1", VisionModel = "vision-a",
                SupportsText = true, SupportsVision = true,
            };
            var second = first.Clone();
            second.Id = "quick-second";
            second.Name = string.Concat(Enumerable.Repeat("很长的自定义供应商名称", 20));
            second.TextModel = "text-b";
            ProfileManager.Save(new CoreProductConfig
            {
                Profiles = [first, second], ActiveProfileId = first.Id, VisionProfileId = first.Id,
            });
            ProfileManager.ApplyActiveToCore(ProfileManager.Load());
            CoreBridge.SaveSettings(CoreBridge.GetSettings() with { SourceLanguage = "en", TargetLanguage = "zh-CN" });
            window = new QuickSearchWindow(new HistoryStore(Path.Combine(directory, "history.json")),
                new VocabularyStore(Path.Combine(directory, "vocabulary.json"))) { ForceClose = true, Width = 480 };
            window.SearchInputBox.Text = "saved query";
            window.Show();
            FlushDispatcher(3);
            window.EnterKeycapButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SpinUntil(() => window.State.Stage is QuickSearchUiStage.Completed or QuickSearchUiStage.Failed,
                "the quick-search surface must complete its real local request");
            Equal(QuickSearchUiStage.Completed, window.State.Stage, window.State.ErrorMessage ?? "quick search completed");
            Equal("保留的查询结果", window.State.FinalRenderedText);
            var blocked = TestIsolation.BlockedPublicSends;
            var selector = (Button)window.FindName("RouteSelectorButton");
            selector.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            FlushDispatcher(2);
            menu = (ContextMenu?)typeof(QuickSearchWindow).GetField("_routeMenu",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window);
            True(menu is { IsOpen: true }, "route switching must open directly from the quick-search surface");
            SaveProductSurface(menu!, "quick-route-menu");
            var activeItem = menu!.Items.OfType<MenuItem>().Single(candidate => candidate.Tag as string == first.Id);
            activeItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            FlushDispatcher(2);
            Equal(1, Volatile.Read(ref requestCount), "choosing the current model must not resend text");
            Equal(QuickSearchUiStage.Completed, window.State.Stage, "choosing the current model preserves the result");
            var item = menu!.Items.OfType<MenuItem>().Single(candidate => candidate.Tag as string == second.Id);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            // 切换立即生效：选中新路线后用当前输入直接重译（与浮窗、工作台
            // 同一契约），不再要求用户再按一次 Enter。
            SpinUntil(() => ProfileManager.Load().ActiveProfileId == second.Id && selector.IsEnabled,
                "selecting a quick-search route must persist and release its busy state");
            SpinUntil(() => secondStarted.Task.IsCompleted && window.State.AccumulatedText.Length > 0,
                "the new model must start streaming before completion is released");
            True(selector.IsEnabled, "the model selector must be usable while the new model is still streaming");
            Equal(QuickSearchUiStage.Streaming, window.State.Stage);
            finishSecond.TrySetResult();
            SpinUntil(() => window.State.Stage is QuickSearchUiStage.Completed or QuickSearchUiStage.Failed,
                "the route switch must retranslate the current query through the new route");
            Equal(QuickSearchUiStage.Completed, window.State.Stage, window.State.ErrorMessage ?? "retranslation completed");
            Equal("新路线的查询结果", window.State.FinalRenderedText);
            Equal("saved query", window.SearchInputBox.Text);
            Equal(first.Id, ProfileManager.Load().VisionProfileId, "text selection must preserve the independent image route");
            Equal("text-b", ProfileManager.ResolveRoutes().Text!.Profile.TextModel);
            Equal("text-b", CoreBridge.GetSettings().TextModel, "the actual request must use the newly selected model");
            Equal(blocked, TestIsolation.BlockedPublicSends, "the route switch must use only the isolated local service");
            Equal(2, Volatile.Read(ref requestCount), "choosing a route must resubmit through the new route exactly once");
            menu.IsOpen = false;
            window.UpdateLayout();
            var status = window.FooterStatusBlock.TransformToAncestor(window).TransformBounds(new Rect(window.FooterStatusBlock.RenderSize));
            var route = selector.TransformToAncestor(window).TransformBounds(new Rect(selector.RenderSize));
            True(status.Right <= route.Left + 0.5, "long route labels cannot overlap the status at minimum width");
            SaveProductSurface(window, "quick-long-route");
            ThemeService.Apply(ThemePreference.Light);
            FlushDispatcher(2);
            SaveProductSurface(window, "quick-long-route-light");
            service.GetAwaiter().GetResult();
            Equal(2, Volatile.Read(ref requestCount));
        }
        finally
        {
            finishSecond.TrySetResult();
            if (menu is not null) menu.IsOpen = false;
            window?.Close();
            listener.Stop();
            if (!service.IsCompleted)
                _ = service.ContinueWith(failed => { _ = failed.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            ThemeService.Apply(ThemePreference.Dark);
            ShellSettingsStore.Save(shell);
            ProfileManager.ResetForTests();
            CoreBridge.SaveSettings(originalSettings);
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static async Task ProductSummaryRouteChangeRetiresBusyState()
    {
        EnsureApplication();
        var executor = new ControlledSummaryExecutor();
        var section = new TranslateSection();
        section.Initialize(new TranslationCoordinator(executor: executor), vocabulary: null);
        var window = new Window { Content = section, Width = 720, Height = 620,
            ShowActivated = false, ShowInTaskbar = false, Left = -12000, Top = -12000 };
        try
        {
            window.Show();
            section.FocusTranslate("source", "zh-CN", "en", "原译文", TranslationSessionState.Completed);
            var request = section.TriggerSummaryAsync();
            var body = string.Join("\n", Enumerable.Repeat("这是已收到的要点。`long_identifier_0123456789` 必须保持原样。", 40));
            executor.Buffer!.TryAppend(body);
            SpinUntil(() => section.SummaryStreamText == body, "the summary must show real body before changing route");
            SaveProductSurface(window, "workbench-summary-long");
            ProfileManager.BumpRevision();
            await request.WaitAsync(TimeSpan.FromSeconds(2));
            True(!section.IsSummaryComplete && section.IsHoldingSummary);
            Equal(Visibility.Collapsed, ((FrameworkElement)section.FindName("TranslateProgress")).Visibility,
                "a superseded route cannot leave an ownerless progress indicator");
            Equal(Visibility.Collapsed, ((FrameworkElement)section.FindName("TranslateStreamIndicator")).Visibility);
            True(((Button)section.FindName("TranslateResultCopyButton")).IsEnabled);
            True(!((Button)section.FindName("TranslateResultSpeakButton")).IsEnabled && !section.StarButton.IsEnabled);
            executor.Complete("obsolete terminal");
            section.ShowTranslationReading();
            Equal("原译文", section.ResultBox.Text);
        }
        finally { section.CancelSummary(); window.Close(); }
    }

    private static void SaveProductSurface(FrameworkElement surface, string name)
    {
        surface.UpdateLayout();
        var path = Path.Combine(FindProjectRoot(), "artifacts", "unified-product-ui", name + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(surface.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(surface.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
