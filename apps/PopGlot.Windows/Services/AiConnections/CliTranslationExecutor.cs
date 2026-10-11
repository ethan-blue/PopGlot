using System.Diagnostics;

namespace PopGlot.Windows.Services.AiConnections;

internal static class CliTranslationExecutor
{
    private sealed record ActiveJob(string ProfileId, CancellationTokenSource Cancellation);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ActiveJob> Active = new();
    public static void CancelProfile(string profileId)
    {
        foreach (var job in Active.Values.Where(job => job.ProfileId == profileId))
            try { job.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }
    public static ICliBackend CreateBackend(EngineExecutionKind kind, CliConnectionSettings connection) => kind switch
    {
        EngineExecutionKind.CodexAppServer => new CodexAppServerBackend(connection),
        EngineExecutionKind.OmpRpc => new OmpRpcBackend(connection),
        _ => throw new InvalidOperationException("无法识别本机工具连接。"),
    };

    public static TranslationStreamSession Stream(ProviderProfile profile, string source, string sourceLanguage,
        string targetLanguage, PromptAnchorSnapshot? preference, CancellationToken ct, long maxBytes = TranslationStreamBuffer.DefaultMaxBytes,
        string task = "translate")
    {
        var snapshot = profile.Clone();
        var buffer = new TranslationStreamBuffer(Guid.NewGuid().ToString("N"), maxBytes: maxBytes);
        var registeredDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        registeredDeadline.CancelAfter(TimeSpan.FromMinutes(3));
        var jobId = Guid.NewGuid(); Active[jobId] = new(snapshot.Id, registeredDeadline);
        var completion = Task.Run(async () =>
        {
            using var deadline = registeredDeadline;
            ICliBackend? backend = null;
            var watch = Stopwatch.StartNew();
            CoreBridge.PreparedTextJob? job = null;
            Task? activeRun = null;
            bool finished = false;
            ulong? submittedMs = null, firstDeltaMs = null;
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (snapshot.CliConnection is not { } connection || !snapshot.ExecutionFacts.TextCapable)
                    throw new InvalidOperationException("本机工具连接未配置完整，请重新连接并选择模型。");
                backend = CreateBackend(snapshot.ResolvedExecutionKind, connection);
                CliLauncher.RequirePolicy(snapshot.ResolvedExecutionKind, connection);
                var settings = CoreBridge.GetSettings();
                job = CoreBridge.PrepareTextJob(new(source, sourceLanguage, targetLanguage, Task: task,
                    IncludeExplanation: task == "translate", ProtectCodeTokens: settings.ProtectCodeTokens,
                    Preference: preference?.Preference, TemplateId: preference?.TemplateId, TemplateRevision: preference?.Revision));
                var run = backend.RunAsync(job.SystemInstructions, job.UserPayload, delta =>
                {
                    firstDeltaMs ??= (ulong)watch.ElapsedMilliseconds;
                    var visible = CoreBridge.PushTextJobDelta(job.JobHandle, delta);
                    if (!buffer.TryAppend(visible)) throw new InvalidOperationException("翻译内容超过上限，已停止连接。");
                }, deadline.Token, () => submittedMs = (ulong)watch.ElapsedMilliseconds);
                activeRun = run;
                while (!run.IsCompleted)
                {
                    await Task.WhenAny(run, Task.Delay(250, deadline.Token)).ConfigureAwait(false);
                    if (deadline.IsCancellationRequested) break;
                    CliLauncher.RequirePolicy(snapshot.ResolvedExecutionKind, connection);
                }
                await run.ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                var result = CoreBridge.FinishTextJob(job.JobHandle, "completed"); finished = true;
                buffer.Complete();
                return new TranslationResponse(new(result.TranslatedText, "", result.Explanation, result.ProtectedTerms, result.Warnings,
                    IsPartial: result.State != "completed"), null, snapshot.Name,
                    new(snapshot.ExecutionKind, connection.ModelId, connection.ExpectedVersion, (ulong)watch.ElapsedMilliseconds, true, submittedMs, firstDeltaMs));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { buffer.Abort("本机工具请求已取消或超时。"); throw new InvalidOperationException("本机工具请求已取消或超时，请检查连接状态后重试。"); }
            catch
            { buffer.Abort("本机工具请求未完成。"); throw; }
            finally
            {
                // Closing our process also fences all late events from the next request.
                deadline.Cancel();
                backend?.Dispose();
                if (activeRun is not null) { try { await activeRun.ConfigureAwait(false); } catch (Exception) { /* Preserve the original failure; settle all callbacks before freeing the job. */ } }
                Active.TryRemove(jobId, out _);
                if (job is not null)
                {
                    if (!finished) { try { CoreBridge.FinishTextJob(job.JobHandle, ct.IsCancellationRequested ? "cancelled" : "failed"); } catch (InvalidOperationException) { } }
                    CoreBridge.FreeTextJob(job.JobHandle);
                }
            }
        }, CancellationToken.None);
        return new(buffer, completion);
    }
}
