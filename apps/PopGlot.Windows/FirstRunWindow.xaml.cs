using System.Windows;
using System.Windows.Input;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

/// <summary>
/// 首次配置向导（0.1.35）。
///
/// 隐私选择集中在这里做一次，而不是散落在设置页里一排「允许/不允许」开关：
/// 内置免费引擎默认开启，用户可以在这一页关掉；关掉的选择会被持久化，
/// 任何后续升级都不会静默翻回开启。向导只在全新安装（未完成过引导）时
/// 出现，「设置 → 通用 → 重新显示引导」可以再次打开它。
/// </summary>
public partial class FirstRunWindow : Window
{
    private const int StepCount = 3;
    private int _step;
    private bool _alreadyCompletedOnboarding;

    /// <summary>True when the user asked to jump straight to 设置 → 服务.</summary>
    internal bool OpenServicesRequested { get; private set; }

    public FirstRunWindow()
    {
        InitializeComponent();
        ThemeService.ApplyWindowChrome(this);
        // 窗口高度不能超过当前工作区，否则固定高度的向导在小屏/高缩放下会被裁掉。
        MaxHeight = SystemParameters.WorkArea.Height;
        LoadCurrentState();
        ApplyStep();
    }

    /// <summary>
    /// Esc 关闭、Enter 前进。按钮和开关自己处理 Enter/Space，这里只接住
    /// 没有被控件消费的 Enter，避免在开关上按 Enter 时误跳到下一步。
    /// </summary>
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseButton_Click(this, new RoutedEventArgs());
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None
            && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
        {
            e.Handled = true;
            if (_step < StepCount - 1)
            {
                Next_Click(this, new RoutedEventArgs());
            }
            else
            {
                Finish_Click(this, new RoutedEventArgs());
            }
        }
    }

    private void LoadCurrentState()
    {
        try
        {
            var shell = ShellSettingsStore.Load();
            _alreadyCompletedOnboarding = shell.HasCompletedOnboarding;
            SelectionShortcutText.Text = $"划词翻译　{shell.SelectionHotkey.DisplayName}";
            ScreenshotShortcutText.Text = $"截图翻译　{shell.ScreenshotHotkey.DisplayName}";
            QuickSearchShortcutText.Text = $"极速查词　{shell.QuickSearchHotkey.DisplayName}";
            if (shell.HasCompletedOnboarding)
            {
                // 从「设置 → 通用 → 重新显示引导」再次打开：展示的永远是已保存的真实选择。
                FreeEngineToggle.IsChecked = shell.FreeEngineConsent != FreeEngineConsent.Denied;
                HistoryToggle.IsChecked = shell.HistoryEnabled;
                StartupToggle.IsChecked = shell.StartWithWindows;
                DiagnosticsToggle.IsChecked = shell.IssueUploadConsent;
                CloseBtn.ToolTip = "关闭";
            }
        }
        catch
        {
            // Keep default onboarding preview if settings cannot be read yet.
        }

        try
        {
            var core = CoreBridge.GetSettings();
            if (_alreadyCompletedOnboarding)
            {
                CloudToggle.IsChecked = core.NetworkEnabled;
            }
        }
        catch
        {
            // Core settings may not be initialized in standalone test runs.
        }
    }

    private void ApplyStep()
    {
        WelcomeStep.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        PrivacyStep.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        DoneStep.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.IsEnabled = _step > 0;
        NextButton.Visibility = _step < StepCount - 1 ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = _step == StepCount - 1 ? Visibility.Visible : Visibility.Collapsed;
        StepIndicator.Text = $"第 {_step + 1} / {StepCount} 步";
        if (_step == StepCount - 1)
        {
            DoneSummary.Text = BuildSummary();
        }

        StepScroller.ScrollToTop();
        // 每一步都把焦点放到主按钮上，键盘用户直接按 Enter 就能继续。
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() => (_step == StepCount - 1 ? FinishButton : NextButton).Focus()));
    }

    private string BuildSummary()
    {
        var parts = new System.Collections.Generic.List<string>();
        parts.Add(CloudToggle.IsChecked == true
            ? "已允许连接云端翻译服务"
            : "已关闭云端翻译（只使用本机模型）");
        parts.Add(FreeEngineToggle.IsChecked == true
            ? "已开启公共翻译（Google / Bing / 有道词典 / MyMemory），边输边译的中间结果不存历史"
            : "公共翻译已关闭，不会发送任何文本");
        parts.Add(HistoryToggle.IsChecked == true ? "翻译历史会保存在本机" : "不保存翻译历史");
        parts.Add(StartupToggle.IsChecked == true ? "开机自动启动" : "开机不自动启动");
        parts.Add(DiagnosticsToggle.IsChecked == true ? "允许上报脱敏诊断" : "不上报任何诊断");
        return string.Join("；", parts) + "。";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
        {
            _step--;
            ApplyStep();
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step < StepCount - 1)
        {
            _step++;
            ApplyStep();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_alreadyCompletedOnboarding)
        {
            // 初次使用直接关闭：按当前展示的默认推荐值落盘，以后不再打扰。
            Persist();
        }
        TrySetDialogResult(false);
        Close();
    }

    private void OpenServices_Click(object sender, RoutedEventArgs e)
    {
        OpenServicesRequested = true;
        Persist();
        TrySetDialogResult(true);
        Close();
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        Persist();
        TrySetDialogResult(true);
        Close();
    }

    private void TrySetDialogResult(bool result)
    {
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            // Show()（非模态测试或独立预览）不允许设置 DialogResult。
        }
    }

    /// <summary>
    /// Writes the choices once. Nothing here may throw into a closing window:
    /// a failed write only means the wizard may appear again.
    /// </summary>
    private void Persist()
    {
        var cloud = CloudToggle.IsChecked == true;
        var freeEngine = FreeEngineToggle.IsChecked == true;
        var history = HistoryToggle.IsChecked == true;
        var startup = StartupToggle.IsChecked == true;
        var diagnostics = DiagnosticsToggle.IsChecked == true;

        try
        {
            ShellSettingsStore.Update(current => current with
            {
                FreeEngineConsent = freeEngine ? FreeEngineConsent.Allowed : FreeEngineConsent.Denied,
                HistoryEnabled = history,
                StartWithWindows = startup,
                IssueUploadConsent = diagnostics,
                HasCompletedOnboarding = true,
            });
        }
        catch (Exception)
        {
            // Best-effort: the wizard simply reappears next launch.
        }

        try
        {
            _ = CoreBridge.UpdateSettingsAsync(settings => settings with { NetworkEnabled = cloud });
        }
        catch (Exception)
        {
            // The core write is queued on a background thread; never block close.
        }

        try
        {
            if (startup)
            {
                StartupRegistration.EnsureRegistered();
            }
            else
            {
                StartupRegistration.RemoveRunEntry();
            }
        }
        catch (Exception)
        {
            // An OS-level startup change must never break the wizard.
        }
    }
}
