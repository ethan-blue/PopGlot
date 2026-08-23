using PopGlot.Windows;
using System.IO;
using System.Windows;

namespace PopGlot.Windows.LogicTests;

internal static class Program
{
    private static int _passed;

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
        Run("session states and friendly failures", SessionStateAndFailureText);
        Run("v1 shortcut configuration migrates", V1ShortcutConfigurationMigrates);
        Run("shortcut conflicts are rejected", ShortcutConflictsAreRejected);
        Run("sensitive history is rejected", SensitiveHistoryIsRejected);
        Run("capture rectangle normalizes", CaptureRectangleNormalizes);
        Run("SendInput ABI size is correct", SendInputAbiSizeIsCorrect);
        if (Environment.GetEnvironmentVariable("POPGLOT_SMOKE_FREE") == "1")
        {
            await RunAsync("free web translation smoke (network)", FreeTranslationSmokeAsync);
        }
        Console.WriteLine($"PopGlot Windows logic tests: {_passed} passed.");
        return 0;
    }

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
        var adapter = new FakeClipboardAdapter { CopyChangesSequence = false };
        var service = new ClipboardSelectionService(adapter);
        var error = await ThrowsAsync<InvalidOperationException>(() =>
            service.ReadSelectionAsync(CancellationToken.None));
        True(error.Message.Contains("选中文本", StringComparison.Ordinal), "missing selection message is unclear");
        True(!adapter.Restored, "unchanged clipboard should not be rewritten");
    }

    private static void PanelPositionStaysInWorkArea()
    {
        var workArea = new Rect(0, 0, 1920, 1080);
        var point = WindowPositioner.NearAnchor(
            new Rect(1870, 1020, 30, 30),
            new Size(520, 420),
            workArea);
        True(point.X >= 12 && point.Y >= 12, "panel crossed top/left edge");
        True(point.X + 520 <= 1908 && point.Y + 420 <= 1068, "panel crossed bottom/right edge");
    }

    private static void PanelPositionSupportsNegativeCoordinates()
    {
        var workArea = new Rect(-1920, 0, 1920, 1080);
        var point = WindowPositioner.NearAnchor(
            new Rect(-60, 980, 30, 30),
            new Size(520, 420),
            workArea);
        True(point.X >= -1908 && point.X + 520 <= -12, "panel escaped secondary monitor horizontally");
        True(point.Y >= 12 && point.Y + 420 <= 1068, "panel escaped secondary monitor vertically");
    }

    private static void SessionStateAndFailureText()
    {
        Equal("正在翻译", TranslationSessionStateText.Describe(TranslationSessionState.Translating));
        Equal("还差一步：配置模型密钥", TranslationPanelWindow.FriendlyError("API Key missing"));
        Equal("模型响应超时", TranslationPanelWindow.FriendlyError("请求超时"));
        Equal("没有读到选中的文字", TranslationPanelWindow.FriendlyError("未检测到选中文本"));
        Equal("模型网络目前未启用", TranslationPanelWindow.FriendlyError("网络访问未启用；未发送任何 Provider 请求。"));
        Equal("翻译请求被限流，请稍后重试", TranslationPanelWindow.FriendlyError("免费翻译接口被限流（HTTP 429，本机 IP 已被暂时限制）"));
        Equal("密钥无效或没有权限", TranslationPanelWindow.FriendlyError("Provider 鉴权失败（HTTP 401）。"));
        Equal("截图上传未获授权", TranslationPanelWindow.FriendlyError("隐私设置未授权上传截图；未发送图片。"));
    }

    private static void V1ShortcutConfigurationMigrates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"popglot-shell-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"ShortcutId\":\"ctrl-shift-t\"}");
            var settings = ShellSettingsStore.Load(path);
            Equal("ctrl-alt-w", settings.SelectionShortcutId);
            Equal("ctrl-shift-t", settings.ScreenshotShortcutId);
            Equal("ctrl-alt-x", settings.CloseShortcutId);
            True(!settings.HistoryEnabled, "history must remain opt-in after migration");
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
            ScreenshotShortcutId = ShellSettings.Default.SelectionShortcutId,
        };
        True(settings.ValidateHotkeys() is not null, "duplicate shortcut was accepted");
    }

    private static void SensitiveHistoryIsRejected()
    {
        var entry = new TranslationHistoryEntry(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "划词",
            "api_key = test-secret-value",
            "翻译",
            string.Empty,
            []);
        True(!HistoryStore.CanPersist(entry), "sensitive content entered local history");
    }

    private static void CaptureRectangleNormalizes()
    {
        var rect = CaptureOverlayWindow.Normalize(new Point(200, 150), new Point(20, 30));
        Equal(new Rect(20, 30, 180, 120), rect);
    }

    private static void SendInputAbiSizeIsCorrect()
    {
        Equal(IntPtr.Size == 8 ? 40 : 28, WindowsSelectionClipboardAdapter.InputStructureSize);
    }

    private static async Task FreeTranslationSmokeAsync()
    {
        var response = await FreeTranslateService.TranslateAsync("hello world", "auto", "zh-CN");
        Console.WriteLine($"  -> engine={response.Diagnostics.Endpoint} status={response.Diagnostics.StatusCode} text={response.Result.TranslatedText}");
        True(response.Result.TranslatedText.Contains("世界") || response.Result.TranslatedText.Contains("你好"),
            $"unexpected free translation: {response.Result.TranslatedText}");
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        await test();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
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

        public IClipboardSnapshot Capture() => Snapshot;

        public void SendCopy()
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
        }

        public string? ReadText()
        {
            if (SimulateUserWriteOnRead)
            {
                SequenceNumber++;
            }
            return SelectedText;
        }

        public void Restore(IClipboardSnapshot snapshot)
        {
            Restored = true;
            SequenceNumber++;
        }
    }

    internal sealed class FakeSnapshot : IClipboardSnapshot
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
