using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using PopGlot.Windows.Services;
using PopGlot.Windows.Sections;

namespace PopGlot.Windows.LogicTests;

internal static partial class Program
{
    private static async Task SummaryStreamingFirstContentPrecedesTerminal()
    {
        var executor = new ControlledSummaryExecutor();
        var coordinator = new TranslationCoordinator(executor: executor);
        var lifecycle = new SummaryLifecycleCoordinator();
        var reading = new ReadingModeState();
        var identity = coordinator.CreateSummarySnapshot("source", "en", "zh", TextTaskKind.Summarize, 0).Identity;
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deltas = new List<string>();
        var started = 0;
        SummaryExecutionResult? terminal = null;
        var request = lifecycle.ExecuteSummaryAsync(coordinator, reading, "source", "en", "zh",
            () => true, () => "source", () => "zh", () => started++,
            result => terminal = result,
            error => throw new InvalidOperationException(error.Message),
            text => { deltas.Add(text); first.TrySetResult(text); });

        Equal(1, started, "onStarting must run once before transport");
        Equal(1, executor.RequestCount, "one summary must start exactly one stream");
        executor.Buffer!.TryAppend("首段");
        Equal("首段", await first.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        True(!request.IsCompleted && terminal is null, "real body must appear before the terminal response");
        True(!reading.HasSummary(identity), "an in-flight fragment is not a successful cache entry");
        Equal(TextTaskKind.Summarize, executor.LastTask);
        Equal("source", executor.LastSource);
        Equal("en", executor.LastSourceLanguage);
        Equal("zh", executor.LastTargetLanguage);
        Equal("summary-model", executor.LastSettings!.TextModel);
        Equal("isolated-test-key", executor.LastApiKey);

        // The running request has its own route snapshot, not this mutable
        // profile object. A normal saved edit also bumps the config revision
        // and is fenced in SummaryStreamingLateArrivalsAreIsolated below.
        executor.Profile.TextModel = "changed-after-submit";
        Equal("summary-model", executor.LastSettings.TextModel);
        executor.Buffer.TryAppend("与后段");
        executor.Complete("首段与后段");
        await request.WaitAsync(TimeSpan.FromSeconds(2));
        Equal("首段与后段", deltas[^1]);
        True(terminal is { IsCurrent: true, SummaryText: "首段与后段" });
        True(reading.HasSummary(identity), "only the clean terminal body enters cache");
        Equal(1, executor.RequestCount, "finalization must not send a second request");
    }

    private static async Task SummaryStreamingLateArrivalsAreIsolated()
    {
        foreach (var change in new[] { "cancel", "source", "target", "route" })
        {
            var executor = new ControlledSummaryExecutor();
            var coordinator = new TranslationCoordinator(executor: executor);
            var lifecycle = new SummaryLifecycleCoordinator();
            var reading = new ReadingModeState();
            var identity = coordinator.CreateSummarySnapshot("source", "en", "zh", TextTaskKind.Summarize, 0).Identity;
            var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = "source";
            var target = "zh";
            var updates = new List<string>();
            SummaryExecutionResult? terminal = null;
            SummaryErrorResult? failure = null;
            var request = lifecycle.ExecuteSummaryAsync(coordinator, reading, source, "en", target,
                () => true, () => source, () => target, () => { },
                result => terminal = result, error => failure = error,
                text => { updates.Add(text); first.TrySetResult(true); });
            executor.Buffer!.TryAppend("first");
            await first.Task.WaitAsync(TimeSpan.FromSeconds(2));
            switch (change)
            {
                case "cancel": lifecycle.Cancel(); break;
                case "source": source = "edited"; break;
                case "target": target = "ja"; break;
                case "route": ProfileManager.BumpRevision(); break;
            }
            executor.Buffer.TryAppend("late");
            if (change is "source" or "target")
            {
                executor.Complete("firstlate");
            }
            await request.WaitAsync(TimeSpan.FromSeconds(2));
            Equal(1, updates.Count, $"{change}: stale delta must not refresh the view");
            if (change is "cancel" or "route")
            {
                True(executor.LastToken.IsCancellationRequested, $"{change}: transport must be cancelled");
                True(terminal is null, $"{change}: cancellation must not trigger success");
                True(!reading.HasSummary(identity), $"{change}: no cancelled fragment may enter cache");
                True(failure is { WasCancelled: true });
                // A deliberately uncooperative executor completes late.
                executor.Complete("firstlate");
            }
            else
            {
                True(terminal is { IsCurrent: false }, $"{change}: old input must not get a current terminal");
            }
            Equal(1, executor.RequestCount);
        }

        var preCancelledExecutor = new ControlledSummaryExecutor();
        var preCancelledLifecycle = new SummaryLifecycleCoordinator();
        await preCancelledLifecycle.ExecuteSummaryAsync(new TranslationCoordinator(executor: preCancelledExecutor),
            new ReadingModeState(), "source", "en", "zh", () => true, () => "source", () => "zh",
            preCancelledLifecycle.Cancel, _ => throw new InvalidOperationException("cancel-on-start must not succeed"),
            _ => { }, _ => throw new InvalidOperationException("cancel-on-start must not paint"));
        Equal(0, preCancelledExecutor.RequestCount, "onStarting cancellation must stop before transport");
    }

    private static async Task SummaryStreamingPartialNeverCachesAsSuccess()
    {
        var executor = new ControlledSummaryExecutor();
        var coordinator = new TranslationCoordinator(executor: executor);
        var lifecycle = new SummaryLifecycleCoordinator();
        var reading = new ReadingModeState();
        var identity = coordinator.CreateSummarySnapshot("source", "en", "zh", TextTaskKind.Summarize, 0).Identity;
        var success = false;
        SummaryErrorResult? failure = null;
        var request = lifecycle.ExecuteSummaryAsync(coordinator, reading, "source", "en", "zh",
            () => true, () => "source", () => "zh", () => { }, _ => success = true,
            error => failure = error, _ => { });
        executor.Buffer!.TryAppend("unfinished");
        executor.Complete("unfinished", partial: true);
        await request.WaitAsync(TimeSpan.FromSeconds(2));
        True(!success && failure is { WasCancelled: false, IsCurrent: true });
        True(!reading.HasSummary(identity), "a partial terminal must not be remembered as a successful summary");
        True(!lifecycle.IsRunning);
    }

    private static async Task SummaryStreamingFreezesPromptAndCacheIdentity()
    {
        var executor = new ControlledSummaryExecutor();
        var coordinator = new TranslationCoordinator(executor: executor);
        var original = coordinator.CreateSummarySnapshot("source", "en", "zh", TextTaskKind.Summarize, 8);
        var anchor = new PromptAnchorSnapshot("Use concise terminology.", "test-style", 7);
        var identity = original.Identity with { PromptTemplateId = anchor.TemplateId, PromptTemplateRevision = anchor.Revision };
        var snapshot = original with { Identity = identity, PreferenceAnchor = anchor };
        var request = coordinator.RunSummaryTaskAsync(snapshot);
        True(ReferenceEquals(anchor, executor.LastAnchor), "native task must receive the exact captured anchor, even without a delta observer");
        executor.Complete("summary");
        await request.WaitAsync(TimeSpan.FromSeconds(2));
        var reading = new ReadingModeState();
        reading.RememberSummary(identity, "summary", "", true);
        True(reading.HasSummary(identity));
        True(!reading.HasSummary(identity with { PromptTemplateRevision = 8 }), "edited template revision must miss old cache");
        True(!reading.HasSummary(identity with { PromptTemplateId = "other-style" }), "a different template must miss old cache");
    }

    private static async Task SummaryNativeStreamUsesSingleSnapshotRequest()
    {
        foreach (var cancel in new[] { false, true })
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var releaseTerminal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            JsonElement captured = default;
            var requests = 0;
            var service = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync();
                using var network = connection.GetStream();
                captured = await ReadSummaryHttpRequestAsync(network);
                Interlocked.Increment(ref requests);
                var system = captured.GetProperty("messages")[0].GetProperty("content").GetString()!;
                var delimiterStart = system.IndexOf("PGMETA-", StringComparison.Ordinal);
                True(delimiterStart >= 0, "the real request must carry its generated delimiter");
                var delimiter = "PGMETA-" + new string(system[(delimiterStart + 7)..]
                    .TakeWhile(ch => Uri.IsHexDigit(ch) || ch == '-').ToArray());
                await network.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"));
                await WriteSummarySseDeltaAsync(network, "first");
                await releaseTerminal.Task;
                // The tail (delta + [DONE] + terminator) completes the response.
                // The client may close the connection as soon as it has consumed
                // it, so the write-completion can surface a 10053 even though the
                // bytes were delivered (Release-mode timing). Tolerate that here;
                // the assertions below on the completed response text are the real
                // check that the tail arrived.
                try
                {
                    await WriteSummarySseDeltaAsync(network,
                        $" tail\n{delimiter}\n{{\"detected_source_lang\":\"en\",\"transcription\":\"\",\"explanation\":\"\",\"warnings\":[]}}");
                    await WriteSummaryHttpChunkAsync(network, Encoding.ASCII.GetBytes("data: [DONE]\n\n"));
                    await network.WriteAsync("0\r\n\r\n"u8.ToArray());
                    await network.FlushAsync();
                }
                catch (IOException) { }
                catch (SocketException) { }
            });

            using var cancellation = new CancellationTokenSource();
            var settings = CoreBridge.GetSettings() with
            {
                ProviderType = ProviderType.OpenAiCompatible,
                ApiBaseUrl = $"http://127.0.0.1:{port}",
                TextEndpoint = "/chat/completions",
                TextModel = "snapshot-model",
                SupportsText = true, NetworkEnabled = true, SafeDevMode = false,
            };
            var anchor = new PromptAnchorSnapshot("Use terse wording.", "loopback-style", 3);
            var stream = CoreBridge.RunTextTaskStream("isolated-key", "source", "en", "en",
                TextTaskKind.Summarize, cancellationToken: cancellation.Token,
                routeSettings: settings, preferenceAnchor: anchor);
            try
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                string first = "";
                while (first.Length == 0)
                {
                    True(DateTime.UtcNow < deadline, "real native first content did not arrive");
                    if (stream.Completion.IsFaulted) await stream.Completion;
                    stream.Buffer.TryDrain(out first);
                    if (string.IsNullOrEmpty(first)) await Task.Delay(10);
                }
                Equal("first", first);
                True(!stream.Completion.IsCompleted, "the FFI must expose body before terminal SSE");
                Equal(1, requests);
                Equal("snapshot-model", captured.GetProperty("model").GetString());
                True(captured.GetProperty("stream").GetBoolean());
                Equal(384, captured.GetProperty("max_tokens").GetInt32());
                var system = captured.GetProperty("messages")[0].GetProperty("content").GetString()!;
                True(system.Contains("Summarize the supplied text", StringComparison.Ordinal));
                True(system.Contains(anchor.Preference, StringComparison.Ordinal));
                True(!system.Contains("translation engine", StringComparison.Ordinal));
                foreach (var key in new[] { "reasoning", "reasoning_effort", "thinking" })
                    True(!captured.TryGetProperty(key, out _), $"unknown gateway model must not acquire {key}");
                using var payload = JsonDocument.Parse(captured.GetProperty("messages")[1].GetProperty("content").GetString()!);
                Equal("source", payload.RootElement.GetProperty("source_text").GetString());
                if (cancel)
                {
                    cancellation.Cancel();
                    try
                    {
                        await stream.Completion.WaitAsync(TimeSpan.FromSeconds(3));
                        throw new InvalidOperationException("cancelled native summary must not succeed");
                    }
                    catch (OperationCanceledException) { }
                    releaseTerminal.TrySetResult(true);
                    await service.WaitAsync(TimeSpan.FromSeconds(3));
                    True(!stream.Buffer.GetAccumulatedText().Contains("tail", StringComparison.Ordinal),
                        "late native content must be fenced after cancellation");
                }
                else
                {
                    releaseTerminal.TrySetResult(true);
                    var response = await stream.Completion.WaitAsync(TimeSpan.FromSeconds(3));
                    Equal("first tail", response.Result.TranslatedText);
                    True(!response.Result.IsPartial);
                    Equal(1, response.Diagnostics!.Attempts);
                    await service.WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
            finally
            {
                cancellation.Cancel();
                releaseTerminal.TrySetResult(true);
                listener.Stop();
                await service.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
    }

    private static async Task NativeIncompleteStatusUsesThreeSurfaces()
    {
        EnsureApplication();
        foreach (var fixture in new[]
                 {
                     (FinishReasonLength: true, Badge: "输出截断", Status: "输出截断 · 内容不完整",
                         Banner: "输出达到长度上限，内容不完整", Reason: "模型输出达到长度上限，译文可能被截断。"),
                     (FinishReasonLength: false, Badge: "连接早闭", Status: "连接早闭 · 内容可能不完整",
                         Banner: "服务提前结束响应，内容可能不完整", Reason: "SSE 流在协议完成事件前正常结束；译文可能不完整。"),
                 })
        {
            var session = await RunNativeIncompleteStreamAsync(fixture.FinishReasonLength);
            Equal(TranslationSessionStage.Partial, session.Stage);
            True(!string.IsNullOrWhiteSpace(session.TranslatedText), "the real native stream must retain its emitted body");
            True(session.Warnings.Any(warning => warning.Contains(fixture.Reason, StringComparison.Ordinal)),
                "the native response must carry the protocol reason into the UI session");

            var epoch = 1L;
            var workbench = TranslateSectionReducer.StartTranslation(TranslateUiState.Initial, epoch);
            workbench = TranslateSectionReducer.ApplyStreamUpdate(workbench,
                new TranslationStreamUpdate("status-session", epoch, TranslationStreamUpdateKind.Delta,
                    session.TranslatedText, session.TranslatedText, session.TranslatedText.Length), epoch);
            workbench = TranslateSectionReducer.ApplyCompletion(workbench, session, epoch);
            Equal(fixture.Badge, workbench.BadgeText);
            Equal(fixture.Status, workbench.StatusText);
            True(workbench.ExplanationText.Contains(fixture.Reason, StringComparison.Ordinal),
                "workbench explanation must show the native warning reason");
            True(workbench.IsPartialIncomplete && !workbench.AreResultActionsEnabled,
                "workbench must retain its partial action gate");
            var workbenchView = new TranslateSection();
            workbenchView.ApplyState(workbench);
            True(ReferenceEquals(workbenchView.EngineBadge.Foreground, workbenchView.TryFindResource("WarningBrush")) &&
                 ReferenceEquals(workbenchView.StatusBlock.Foreground, workbenchView.TryFindResource("WarningBrush")),
                "workbench truncation and early-close states must use warning tone");

            var history = new HistoryStore(TestIsolation.HistoryPath);
            var vocabulary = new VocabularyStore(TestIsolation.VocabularyPath);
            var quick = new QuickSearchWindow(history, vocabulary);
            try
            {
                quick.State.StartNewSearch("native status source");
                quick.State.OnSessionCompleted(session, quick.State.CurrentEpoch, "native status source");
                typeof(QuickSearchWindow).GetMethod("SyncUiWithState",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(quick, null);
                Equal(fixture.Badge, quick.IncompleteBadgeBlock.Text);
                Equal(fixture.Status, quick.FooterStatusBlock.Text);
                True(ReferenceEquals(quick.IncompleteBadgeBlock.Foreground, quick.TryFindResource("WarningBrush")),
                    "quick search truncation and early-close badges must use warning tone");
                True(ReferenceEquals(quick.StatusDotShape.Fill, quick.TryFindResource("WarningBrush")),
                    "quick search truncation and early-close status dots must use warning tone");
                True(quick.State.Explanation?.Contains(fixture.Reason, StringComparison.Ordinal) == true,
                    "quick search explanation must show the native warning reason");
                True(quick.State.CanCopy && !quick.State.CanSpeak && !quick.State.CanStar,
                    "quick search must keep manual copy while blocking side effects");
            }
            finally
            {
                quick.Close();
            }

            var panel = new TranslationPanelWindow(new System.Windows.Rect(100, 100, 20, 20), history,
                () => ShellSettings.Default, null, null, vocabulary);
            try
            {
                DrivePanelSession(panel, "native status source", session);
                Equal(fixture.Badge, panel.EngineBadge.Text);
                Equal(fixture.Status, panel.StatusTextBlock.Text);
                Equal(fixture.Banner, panel.WarningText.Text);
                True(ReferenceEquals(panel.EngineBadge.Foreground, panel.TryFindResource("WarningBrush")),
                    "floating panel truncation and early-close badge must use warning tone");
                var warningBox = (System.Windows.Controls.Border)panel.FindName("WarningBox")!;
                var statusDot = (System.Windows.Controls.Border)panel.FindName("StatusDot")!;
                True(ReferenceEquals(warningBox.Background, panel.TryFindResource("WarningSoftBrush")) &&
                     ReferenceEquals(warningBox.BorderBrush, panel.TryFindResource("WarningBrush")) &&
                     ReferenceEquals(statusDot.Background, panel.TryFindResource("WarningBrush")),
                    "floating panel incomplete states must use warning card and status-dot tones");
                True(panel.ExplanationText.Text.Contains(fixture.Reason, StringComparison.Ordinal),
                    "floating panel explanation must show the native warning reason");
                True(!panel.ResultSpeakBtn.IsEnabled && !panel.StarToggle.IsEnabled,
                    "floating panel must keep speak and star gated");
                True(panel.ResultCopyBtn.IsEnabled, "floating panel must retain manual copy");
            }
            finally
            {
                panel.ForceClose = true;
                panel.Close();
            }
        }
        var serverFailure = new TranslationSession
        {
            Stage = TranslationSessionStage.Failed,
            TranslatedText = "已接收的部分输出",
            Error = new TranslationError(TranslationErrorKind.ServerError, "HTTP 500 Internal Server Error"),
        };
        var failedEpoch = 1L;
        var failedWorkbench = TranslateSectionReducer.StartTranslation(TranslateUiState.Initial, failedEpoch);
        failedWorkbench = TranslateSectionReducer.ApplyStreamUpdate(failedWorkbench,
            new TranslationStreamUpdate("failed-status", failedEpoch, TranslationStreamUpdateKind.Delta,
                serverFailure.TranslatedText, serverFailure.TranslatedText, serverFailure.TranslatedText.Length), failedEpoch);
        failedWorkbench = TranslateSectionReducer.ApplyCompletion(failedWorkbench, serverFailure, failedEpoch);
        Equal("生成失败", failedWorkbench.BadgeText);
        Equal("生成失败 · 内容不完整", failedWorkbench.StatusText);
        var failedWorkbenchView = new TranslateSection();
        failedWorkbenchView.ApplyState(failedWorkbench);
        True(ReferenceEquals(failedWorkbenchView.EngineBadge.Foreground, failedWorkbenchView.TryFindResource("DangerBrush")) &&
             ReferenceEquals(failedWorkbenchView.StatusBlock.Foreground, failedWorkbenchView.TryFindResource("DangerBrush")),
            "workbench request failures must use danger tone");

        var failedState = new QuickSearchState();
        failedState.StartNewSearch("server failure");
        failedState.OnSessionCompleted(serverFailure, failedState.CurrentEpoch, "server failure");
        Equal("生成失败", failedState.IncompleteBadgeText);
        Equal("生成失败 · 内容不完整", failedState.StatusText);
        True(!failedState.IsPartialWarningTone, "a true request failure must use danger tone");
        var failedQuick = new QuickSearchWindow(new HistoryStore(TestIsolation.HistoryPath),
            new VocabularyStore(TestIsolation.VocabularyPath));
        failedQuick.State.StartNewSearch("server failure");
        failedQuick.State.OnSessionCompleted(serverFailure, failedQuick.State.CurrentEpoch, "server failure");
        typeof(QuickSearchWindow).GetMethod("SyncUiWithState",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(failedQuick, null);
        True(ReferenceEquals(failedQuick.IncompleteBadgeBlock.Foreground, failedQuick.TryFindResource("DangerBrush")),
            "quick search request failures must use danger tone");
        True(ReferenceEquals(failedQuick.StatusDotShape.Fill, failedQuick.TryFindResource("DangerBrush")),
            "quick search request failures must use danger tone");
        failedQuick.Close();

        var failurePanel = new TranslationPanelWindow(new System.Windows.Rect(100, 100, 20, 20),
            new HistoryStore(TestIsolation.HistoryPath), () => ShellSettings.Default, null, null,
            new VocabularyStore(TestIsolation.VocabularyPath));
        try
        {
            DrivePanelSession(failurePanel, "server failure", serverFailure);
            Equal("生成失败", failurePanel.EngineBadge.Text);
            Equal("生成失败 · 内容不完整", failurePanel.StatusTextBlock.Text);
            Equal("生成失败，内容不完整", failurePanel.WarningText.Text);
            True(ReferenceEquals(failurePanel.EngineBadge.Foreground, failurePanel.TryFindResource("DangerBrush")),
                "floating panel request failures must use danger tone");
            var failureWarningBox = (System.Windows.Controls.Border)failurePanel.FindName("WarningBox")!;
            var failureStatusDot = (System.Windows.Controls.Border)failurePanel.FindName("StatusDot")!;
            True(ReferenceEquals(failureWarningBox.Background, failurePanel.TryFindResource("DangerSoftBrush")) &&
                 ReferenceEquals(failureWarningBox.BorderBrush, failurePanel.TryFindResource("DangerBrush")) &&
                 ReferenceEquals(failureStatusDot.Background, failurePanel.TryFindResource("DangerBrush")),
                "floating panel true failures must use danger card and status-dot tones");
        }
        finally
        {
            failurePanel.ForceClose = true;
            failurePanel.Close();
        }
    }

    private static async Task<TranslationSession> RunNativeIncompleteStreamAsync(bool finishReasonLength)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestCount = 0;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var network = client.GetStream();
            await ReadSummaryHttpRequestAsync(network);
            Interlocked.Increment(ref requestCount);
            await network.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"));
            await WriteSummarySseDeltaAsync(network, "保留的部分译文");
            // The terminal (when present) drives the partial classification the
            // test asserts; the chunk terminator after it is best-effort because
            // the client may close once it has the terminal.
            if (finishReasonLength)
            {
                var terminal = JsonSerializer.Serialize(new
                {
                    choices = new[] { new { delta = new { }, finish_reason = "length" } },
                });
                await WriteSummaryHttpChunkAsync(network, Encoding.UTF8.GetBytes($"data: {terminal}\n\n"));
            }
            try
            {
                await network.WriteAsync("0\r\n\r\n"u8.ToArray());
            }
            catch (IOException) { }
            catch (SocketException) { }
        });

        var settings = CoreBridge.GetSettings() with
        {
            ProviderType = ProviderType.OpenAiCompatible,
            ApiBaseUrl = $"http://127.0.0.1:{port}",
            TextEndpoint = "/chat/completions",
            TextModel = "status-loopback-model",
            SupportsText = true,
            NetworkEnabled = true,
            SafeDevMode = false,
        };
        try
        {
            var stream = CoreBridge.TranslateTextDraftStream(settings, "isolated-status-key",
                "native status source", "en", "zh-CN", requestId: Guid.NewGuid().ToString("N"));
            var response = await stream.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await server.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(1, requestCount, "one isolated native request must reach the loopback server");
            return new TranslationSession
            {
                Stage = response.Result.IsPartial ? TranslationSessionStage.Partial : TranslationSessionStage.Completed,
                TranslatedText = response.Result.TranslatedText,
                Explanation = response.Result.Explanation,
                Warnings = response.Result.Warnings,
            };
        }
        finally
        {
            listener.Stop();
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<JsonElement> ReadSummaryHttpRequestAsync(NetworkStream network)
    {
        using var request = new MemoryStream();
        var buffer = new byte[4096];
        var bodyStart = -1;
        var contentLength = 0;
        while (request.Length < 128 * 1024)
        {
            var count = await network.ReadAsync(buffer);
            if (count == 0) throw new IOException("native request ended before its body");
            request.Write(buffer, 0, count);
            if (bodyStart < 0)
            {
                var headerEnd = request.GetBuffer().AsSpan(0, (int)request.Length).IndexOf("\r\n\r\n"u8);
                if (headerEnd >= 0)
                {
                    bodyStart = headerEnd + 4;
                    var headers = Encoding.ASCII.GetString(request.GetBuffer(), 0, headerEnd);
                    var lengthLine = headers.Split("\r\n")
                        .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    contentLength = int.Parse(lengthLine[(lengthLine.IndexOf(':') + 1)..].Trim());
                }
            }
            if (bodyStart >= 0 && request.Length >= bodyStart + contentLength)
            {
                using var json = JsonDocument.Parse(request.GetBuffer().AsMemory(bodyStart, contentLength));
                return json.RootElement.Clone();
            }
        }
        throw new IOException("native request exceeded fixture bound");
    }

    private static Task WriteSummarySseDeltaAsync(NetworkStream network, string content)
    {
        var json = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content } } } });
        return WriteSummaryHttpChunkAsync(network, Encoding.UTF8.GetBytes($"data: {json}\n\n"));
    }

    private static async Task WriteSummaryHttpChunkAsync(NetworkStream network, byte[] chunk)
    {
        await network.WriteAsync(Encoding.ASCII.GetBytes($"{chunk.Length:X}\r\n"));
        await network.WriteAsync(chunk);
        await network.WriteAsync("\r\n"u8.ToArray());
        await network.FlushAsync();
    }

    private static async Task SummarySnapshotFailureReturnsTerminalWithoutTransport()
    {
        var executor = new ControlledSummaryExecutor { OnReadSettings = ProfileManager.BumpRevision };
        var lifecycle = new SummaryLifecycleCoordinator();
        SummaryErrorResult? terminal = null;
        await lifecycle.ExecuteSummaryAsync(new TranslationCoordinator(executor: executor), new ReadingModeState(),
            "source", "en", "zh", () => true, () => "source", () => "zh",
            () => throw new InvalidOperationException("a failed snapshot must not start transport"),
            _ => throw new InvalidOperationException("a failed snapshot cannot succeed"),
            error => terminal = error);
        True(terminal is { WasCancelled: true, IsCurrent: true, Identity: null },
            "snapshot invalidation must terminate the owning view instead of escaping to application-wide cancellation");
        Equal(0, executor.RequestCount);
        True(!lifecycle.IsRunning);
    }

    private sealed class ControlledSummaryExecutor : ITranslationExecutor
    {
        public ProviderProfile Profile { get; } = new()
        {
            Id = "summary-profile", Name = "Summary fixture", TextModel = "summary-model",
            ApiBaseUrl = "http://127.0.0.1:65530", SupportsText = true,
            CredentialTarget = "isolated-summary-test",
        };
        private readonly TaskCompletionSource<TranslationResponse> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TranslationStreamBuffer? Buffer { get; private set; }
        public int RequestCount { get; private set; }
        public string? LastApiKey { get; private set; }
        public string? LastSource { get; private set; }
        public string? LastSourceLanguage { get; private set; }
        public string? LastTargetLanguage { get; private set; }
        public TextTaskKind LastTask { get; private set; }
        public ProviderSettings? LastSettings { get; private set; }
        public PromptAnchorSnapshot? LastAnchor { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Action? OnReadSettings { get; init; }
        public ProviderSettings GetSettings()
        {
            OnReadSettings?.Invoke();
            return CoreBridge.GetSettings() with { NetworkEnabled = true, SafeDevMode = false };
        }
        public (ProviderRoute? Text, ProviderRoute? Vision) ResolveRoutes() => (new ProviderRoute(Profile, Profile.CredentialTarget), null);
        public string? LoadApiKey(string target) => "isolated-test-key";
        public TranslationStreamSession StreamTextTask(string? apiKey, string source, string sourceLang, string targetLang,
            TextTaskKind task, string sessionId, long epoch, CancellationToken cancellationToken,
            ProviderSettings? routeSettings = null, PromptAnchorSnapshot? preferenceAnchor = null)
        {
            RequestCount++;
            LastApiKey = apiKey; LastSource = source; LastSourceLanguage = sourceLang; LastTargetLanguage = targetLang;
            LastTask = task; LastSettings = routeSettings; LastAnchor = preferenceAnchor; LastToken = cancellationToken;
            Buffer = new TranslationStreamBuffer(sessionId, sessionId, epoch);
            return new TranslationStreamSession(Buffer, _completion.Task);
        }
        public void Complete(string body, bool partial = false)
        {
            Buffer?.Complete();
            _completion.TrySetResult(new TranslationResponse(new TranslationResult(body, "", "", [], [], IsPartial: partial),
                new ProviderDiagnostics("summary-test", ProviderType.OpenAiCompatible, "loopback-fixture", 1, 200, 100)));
        }
        public bool IsOcrSupported => false;
        public ResolvedRoute ResolveScreenshotRoute(ProviderSettings settings, bool ocrAvailable) => throw new NotSupportedException();
        public Task<string> RecognizeOcrTextAsync(byte[] imageBytes, string sourceLang, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public TranslationStreamSession StreamText(string? apiKey, string source, string sourceLang, string targetLang, string sessionId, long epoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public TranslationStreamSession StreamTextDraft(ProviderSettings draftSettings, string apiKey, string source, string sourceLang, string targetLang, string sessionId, long epoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public TranslationStreamSession StreamVisionDraft(ProviderSettings draftSettings, string textApiKey, string visionApiKey, byte[] image, string sourceLang, string targetLang, string sessionId, long epoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TranslationResponse> TranslateFreeAsync(string source, string sourceLang, string targetLang, FreeEngineAuthorization authorization, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
