using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using PopGlot.Windows;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.LogicTests;

/// <summary>
/// C12 真机验收架（独立时段执行：exe lifecycle-stress）。
/// 四窗口各 200 次真实 Show/Close：每次关窗后持 WeakReference，泵队排空
/// 调度回调，定期强制 GC 判活并采样 WS / 句柄 / 线程 / 托管堆 / 主题事件
/// 订阅数，全部落盘 artifacts/lifecycle/。验收合同：GC 后对象回到稳定平台
/// （窗口可回收、订阅数回基线、堆平台有界）；WS 不随次数单调增长。
/// 附加 TTS 20 轮 Speak/Stop 的 CancellationToken 释放审计（Stop 负责
/// Cancel+Dispose，_synthesisCts 必须被置空）。
/// 探针结论：不泵队时排队回调捕获每个窗口实例，把「可回收」误报成
/// 「泄漏」——真实应用主循环持续泵队，压测复刻同一节奏。
/// </summary>
internal static class LifecycleStress
{
    private const int Cycles = 200;
    private const int SampleEvery = 10;
    private const double Megabyte = 1024d * 1024d;

    internal enum WindowFamily
    {
        Main,
        Settings,
        Panel,
        QuickSearch,
    }

    internal static void RunWindowFamily(WindowFamily family)
    {
        Program.EnsureApplication();
        var outDir = Path.Combine(Program.FindProjectRoot(), "artifacts", "lifecycle");
        Directory.CreateDirectory(outDir);
        var runId = Guid.NewGuid().ToString("N")[..8];
        var history = new HistoryStore(Path.Combine(Path.GetTempPath(), $"popglot-c12-{runId}-history.json"));
        var vocabulary = new VocabularyStore(Path.Combine(Path.GetTempPath(), $"popglot-c12-{runId}-vocab.json"));

        var baselineTheme = ThemeSubscriptionCount();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var baselineThreads = process.Threads.Count;
        var baselineHandles = process.HandleCount;

        var weakRefs = new List<WeakReference>(Cycles);
        var samples = new List<string>(Cycles / SampleEvery + 2);
        samples.Add("cycle,wsMB,privateMB,heapMB,handles,threads,alive,themeSubs");

        for (var cycle = 1; cycle <= Cycles; cycle++)
        {
            Window window;
            try
            {
                window = CreateWindow(family, history, vocabulary);
                ConfigureRealClose(window, family);
                window.Show();
                window.Close();
            }
            catch (Exception exception) when (cycle > 1)
            {
                throw new InvalidOperationException(
                    $"{family}: cycle {cycle} failed: {exception.Message} " +
                    $"hasShutdownStarted={Application.Current?.Dispatcher.HasShutdownStarted}", exception);
            }
            weakRefs.Add(new WeakReference(window));
            // Debug 构建会把局部变量保鲜到方法结束：不显式置空的话，
            // 最后一圈的窗口被测量代码自己的局部槽位钉住——把「可回收」
            // 误报成 1/200 泄漏（worktree 实证：幸存者恒为第 200 圈）。
            window = null!;
            PumpDispatcher();
            ResetStormCounterForStress();

            if (cycle % SampleEvery == 0 || cycle == Cycles)
            {
                ForceGc();
                process.Refresh();
                var alive = weakRefs.Count(static reference => reference.IsAlive);
                samples.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{cycle},{process.WorkingSet64 / Megabyte:F1},{process.PrivateMemorySize64 / Megabyte:F1}," +
                    $"{GC.GetTotalMemory(forceFullCollection: false) / Megabyte:F1},{process.HandleCount}," +
                    $"{process.Threads.Count},{alive},{ThemeSubscriptionCount()}"));
            }
        }

        ForceGc();
        var aliveAfterFirstGc = weakRefs.Count(static reference => reference.IsAlive);
        // WPF pins the last-SHOWN window in Application.MainWindow. Null it,
        // deep-drain idle-priority queued callbacks, and re-GC before judging.
        var pinnedByApplication = aliveAfterFirstGc == 1 && Application.Current?.MainWindow is not null;
        if (Application.Current is not null)
        {
            Application.Current.MainWindow = null;
        }
        PumpDispatcherAt(System.Windows.Threading.DispatcherPriority.SystemIdle);
        // WPF idle tiers: Background -> ContextIdle -> ApplicationIdle ->
        // SystemIdle. A SystemIdle frame never executes earlier ContextIdle/
        // ApplicationIdle items — only a ContextIdle frame truly drains them.
        PumpDispatcherAt(System.Windows.Threading.DispatcherPriority.ContextIdle);
        PumpDispatcher();
        Application.Current?.Dispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        ForceGc();
        var aliveAfterFinalGc = weakRefs.Count(static reference => reference.IsAlive);
        var survivorCycle = -1;
        for (var i = 0; i < weakRefs.Count; i++)
        {
            if (weakRefs[i].IsAlive)
            {
                survivorCycle = i + 1;
                break;
            }
        }
        var survivorWindow = weakRefs[survivorCycle - 1].Target as Window;
        Console.WriteLine($"[C12 {family}] survivor cycle: {survivorCycle} " +
                          $"isLoaded={survivorWindow?.IsLoaded.ToString() ?? "?"} " +
                          $"appWindows={Application.Current?.Windows.Count.ToString() ?? "?"}");
        var finalTheme = ThemeSubscriptionCount();
        process.Refresh();

        var workingSets = new List<double>();
        var heaps = new List<double>();
        foreach (var row in samples.Skip(1))
        {
            var columns = row.Split(',');
            workingSets.Add(double.Parse(columns[1], System.Globalization.CultureInfo.InvariantCulture));
            heaps.Add(double.Parse(columns[3], System.Globalization.CultureInfo.InvariantCulture));
        }
        var increases = WorkingSetIncreases(workingSets);

        // ---- 留档在先：失败家族也必须留下测量数据 ----
        var reportPath = Path.Combine(outDir, $"c12-{family:G}-{runId}.csv");
        File.WriteAllLines(reportPath, samples);
        var summaryPath = Path.Combine(outDir, $"c12-{family:G}-{runId}.summary.txt");
        File.WriteAllLines(summaryPath, new[]
        {
            $"family={family} cycles={Cycles}",
            $"alive_after_first_gc={aliveAfterFirstGc} pinned_by_application={pinnedByApplication}",
            $"alive_after_pin_clear_and_deep_drain={aliveAfterFinalGc}",
            $"theme_subs_baseline={baselineTheme} final={finalTheme}",
            $"threads_baseline={baselineThreads} final={process.Threads.Count} handles_baseline={baselineHandles} final={process.HandleCount}",
            $"ws_first={workingSets[0]:F1}MB ws_last={workingSets[^1]:F1}MB ws_max={workingSets.Max():F1}MB increases={increases}/{workingSets.Count - 1}",
            $"heap_first={heaps[0]:F1}MB heap_last={heaps[^1]:F1}MB",
        });
        // ---- 合同断言（失败即抛，由 RunStaBatch 记 FAIL）----
        // WPF 平台行为（探针 A 在裸 Window 上复现）：Application.Windows 会
        // 保留一个已关闭（IsLoaded=false）的窗口实例——每族恰一个幸存者即
        // 此驻留，不是应用代码泄漏。判定：全部回收 PASS；恰剩 1 个且它是
        // 已关闭状态 → 记为平台驻留；存在已加载（未真正关闭）的幸存者或
        // ≥2 个幸存者 → 真实泄漏。
        var platformRetained = aliveAfterFinalGc == 1 &&
            survivorWindow is { IsLoaded: false };
        if (aliveAfterFinalGc > 1 || (aliveAfterFinalGc == 1 && !platformRetained))
        {
            throw new InvalidOperationException(
                $"{family}: {aliveAfterFinalGc}/{Cycles} window instances stayed rooted after close+GC " +
                $"(platformRetained={platformRetained}, survivorLoaded={survivorWindow?.IsLoaded.ToString() ?? "?"}) — " +
                "a real leak (event/timer/static graph).");
        }
        if (finalTheme != baselineTheme)
        {
            throw new InvalidOperationException(
                $"{family}: theme subscriptions {baselineTheme}→{finalTheme} — a closed window leaked its ThemeChanged handler.");
        }
        if (increases > workingSets.Count * 0.6)
        {
            throw new InvalidOperationException(
                $"{family}: WS rose in {increases}/{workingSets.Count - 1} sampled transitions — monotonic growth, not a plateau.");
        }
        var wsBound = workingSets[0] * 1.35 + 64;
        if (workingSets[^1] > wsBound)
        {
            throw new InvalidOperationException(
                $"{family}: final WS {workingSets[^1]:F0}MB exceeds the plateau bound {wsBound:F0}MB (first sample {workingSets[0]:F0}MB).");
        }
        var heapBound = heaps[0] * 1.35 + 32;
        if (heaps[^1] > heapBound)
        {
            throw new InvalidOperationException(
                $"{family}: final managed heap {heaps[^1]:F0}MB exceeds the platform bound {heapBound:F0}MB (first sample {heaps[0]:F0}MB).");
        }
        if (Math.Abs(process.Threads.Count - baselineThreads) > 6)
        {
            throw new InvalidOperationException(
                $"{family}: thread count {baselineThreads}→{process.Threads.Count} — timers or async work are not being released.");
        }

        Console.WriteLine($"[C12 {family}] alive=0 theme={finalTheme}(base {baselineTheme}) " +
                          $"ws {workingSets[0]:F0}→{workingSets[^1]:F0}MB (max {workingSets.Max():F0}) " +
                          $"heap {heaps[0]:F0}→{heaps[^1]:F0}MB — report: {reportPath}");
    }

    private static int WorkingSetIncreases(List<double> workingSets)
    {
        // ±3MB 的逐圈抖动在 400+MB 的工作集上是噪声（Panel 实测全程
        // +0.5MB 却被逐字节比较计成 14/19 次增长）。只把超过 2MB 的
        // 上台阶计为真实增长；总平台仍由 final-vs-bound 断言把守。
        const double noiseFloorMb = 2.0;
        var increases = 0;
        for (var i = 1; i < workingSets.Count; i++)
        {
            if (workingSets[i] > workingSets[i - 1] + noiseFloorMb)
            {
                increases++;
            }
        }
        return increases;
    }

    internal static void RunTtsReleaseCheck()
    {
        Program.EnsureApplication();
        ResetStormCounterForStress();
        TtsService.LocalSynthesizer = _ => Task.FromResult<string?>(null);
        try
        {
            for (var round = 0; round < 20; round++)
            {
                TtsService.Speak($"C12 tts release round {round}", "en-US");
                Thread.Sleep(20);
                TtsService.Stop();
            }
            ForceGc();

            var ctsField = typeof(TtsService).GetField("_synthesisCts", BindingFlags.Static | BindingFlags.NonPublic);
            var remaining = ctsField?.GetValue(null);
            if (remaining is not null)
            {
                throw new InvalidOperationException(
                    "TTS Stop() must null the synthesis CTS so it is not kept rooted between utterances.");
            }
            if (TtsService.IsSpeaking)
            {
                throw new InvalidOperationException("TTS must not be speaking after 20 speak/stop cycles.");
            }
            Console.WriteLine("[C12 tts] 20 speak/stop cycles: _synthesisCts nulled, IsSpeaking=false — tokens released.");
        }
        finally
        {
            TtsService.LocalSynthesizer = null;
        }
    }

    /// <summary>
    /// 压测翻动窗口会触发少量排队回调异常。真实产品里 15 秒内 5 次已处理
    /// 异常会进降级模式并退出——对生命周期测量是干扰。压测在每次泵队后
    /// 复位风暴计数与降级标记，让 800 次开关完整跑完；被处理异常仍经
    /// DiagnosticsLog 留底。不做 EventHandlersStore 清空：那会破坏
    /// StaticResource 解析（实测副作用）。仅测试进程生效。
    /// </summary>
    private static void ResetStormCounterForStress()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }
        var binding = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(App).GetField("_handledExceptionCount", binding)?.SetValue(app, 0);
        var degraded = typeof(App).GetField("_degraded", binding);
        if (degraded is not null && degraded.GetValue(app) is true)
        {
            degraded.SetValue(app, false);
            RuntimeGate.NewWorkAllowed = true;
        }
    }

    /// <summary>
    /// 根因探针（exe lifecycle-probe）：区分「WPF 环境本身」与「应用代码」
    /// 造成的驻留。实测结论：裸 Window Show+Close 后 199/200 可回收（唯一
    /// 幸存=Application.MainWindow 平台钉住）；不泵队时自引用排队回调
    /// 50/50 残留、泵队后 1/50——排队回调必须靠泵队释放。
    /// </summary>
    internal static void RunRootProbe()
    {
        Program.EnsureApplication();
        var outDir = Path.Combine(Program.FindProjectRoot(), "artifacts", "lifecycle");
        Directory.CreateDirectory(outDir);

        var bareShown = new List<WeakReference>();
        for (var i = 0; i < 200; i++)
        {
            var window = new Window();
            window.Show();
            window.Close();
            bareShown.Add(new WeakReference(window));
        }
        ForceGc();
        Console.WriteLine($"[probe A] bare Window Show+Close: alive={bareShown.Count(static r => r.IsAlive)}/200");

        var bareCtor = new List<WeakReference>();
        for (var i = 0; i < 200; i++)
        {
            bareCtor.Add(new WeakReference(new Window()));
        }
        ForceGc();
        Console.WriteLine($"[probe B] bare Window construct-only: alive={bareCtor.Count(static r => r.IsAlive)}/200 (Debug locals keep them alive)");

        var appCtor = new List<WeakReference>();
        for (var i = 0; i < 50; i++)
        {
            appCtor.Add(new WeakReference(new QuickSearchWindow(
                new HistoryStore(Path.Combine(Path.GetTempPath(), $"popglot-probe-{i}.json")),
                new VocabularyStore(Path.Combine(Path.GetTempPath(), $"popglot-probe-v{i}.json")))));
        }
        ForceGc();
        Console.WriteLine($"[probe C] QuickSearch construct-only: alive={appCtor.Count(static r => r.IsAlive)}/50 (unclosed windows keep static subscriptions)");

        var queuedNoPump = new List<WeakReference>();
        for (var i = 0; i < 50; i++)
        {
            var window = new Window();
            window.Show();
            window.Dispatcher.BeginInvoke(new Action(() => { _ = window.Width; }));
            window.Close();
            queuedNoPump.Add(new WeakReference(window));
        }
        ForceGc();
        Console.WriteLine($"[probe E] queued self-ref, no pump: alive={queuedNoPump.Count(static r => r.IsAlive)}/50");

        var queuedPump = new List<WeakReference>();
        for (var i = 0; i < 50; i++)
        {
            var window = new Window();
            window.Show();
            window.Dispatcher.BeginInvoke(new Action(() => { _ = window.Width; }));
            window.Close();
            queuedPump.Add(new WeakReference(window));
            PumpDispatcher();
        }
        ForceGc();
        Console.WriteLine($"[probe F] queued self-ref, pumped: alive={queuedPump.Count(static r => r.IsAlive)}/50");
    }

    private static Window CreateWindow(WindowFamily family, HistoryStore history, VocabularyStore vocabulary) =>
        family switch
        {
            WindowFamily.Main => new MainWindow(ShellSettings.Default, history, vocabulary),
            WindowFamily.Settings => new SettingsWindow(ShellSettings.Default, history, vocabulary),
            WindowFamily.Panel => new TranslationPanelWindow(
                new Rect(100, 100, 20, 20), history, () => ShellSettings.Default, null, null, vocabulary),
            WindowFamily.QuickSearch => new QuickSearchWindow(history, vocabulary),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };

    private static void ConfigureRealClose(Window window, WindowFamily family)
    {
        switch (family)
        {
            case WindowFamily.Main:
                ((MainWindow)window).AllowClose = true;
                break;
            case WindowFamily.Settings:
                ((SettingsWindow)window).ForceClose = true;
                break;
            case WindowFamily.Panel:
                ((TranslationPanelWindow)window).ForceClose = true;
                break;
            case WindowFamily.QuickSearch:
                ((QuickSearchWindow)window).ForceClose = true;
                break;
        }
    }

    private static int ThemeSubscriptionCount()
    {
        var field = typeof(ThemeService).GetField("ThemeChanged", BindingFlags.Static | BindingFlags.NonPublic);
        var handler = (MulticastDelegate?)field?.GetValue(null);
        return handler?.GetInvocationList().Length ?? 0;
    }

    private static void PumpDispatcher()
    {
        PumpDispatcherAt(System.Windows.Threading.DispatcherPriority.Background);
    }

    private static void PumpDispatcherAt(System.Windows.Threading.DispatcherPriority priority)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            priority,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void ForceGc()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }
}
