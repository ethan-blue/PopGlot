using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PopGlot.Windows.Services.AiConnections;

namespace PopGlot.Windows.Sections;

public partial class ServicesSection
{
    private TaskCompletionSource<string>? _ompCallback;
    private IReadOnlyList<OmpRpcBackend.LoginProvider> _ompProviders = [];
    private bool _showNewOmpProviders;

    internal static IReadOnlyList<CliModel> AuthenticatedOmpModels(
        IReadOnlyList<CliModel> models, IReadOnlyList<OmpRpcBackend.LoginProvider> providers)
    {
        var authenticated = providers.Where(p => p.Authenticated).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        return models.Where(m => m.Provider is not null && authenticated.Contains(m.Provider)).ToArray();
    }

    internal void SetOmpProviders(IReadOnlyList<OmpRpcBackend.LoginProvider> providers, string? selectedId = null)
    {
        _ompProviders = providers;
        _showNewOmpProviders = !providers.Any(p => p.Authenticated);
        RefreshOmpProviderChoices(selectedId);
    }

    private void RefreshOmpProviderChoices(string? selectedId = null)
    {
        var choices = _ompProviders.Where(p => _showNewOmpProviders ? !p.Authenticated && p.Available : p.Authenticated).ToArray();
        OmpLoginProviderCombo.ItemsSource = choices;
        OmpLoginProviderCombo.SelectedItem = choices.FirstOrDefault(p => p.Id == selectedId) ?? choices.FirstOrDefault();
        UpdateOmpLoginButtons(_cliOperation is not null);
    }

    private void OmpAddProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_cliOperation is not null) return;
        _showNewOmpProviders = !_showNewOmpProviders;
        RefreshOmpProviderChoices();
        OmpLoginProviderCombo.Focus();
        OmpLoginProviderCombo.IsDropDownOpen = true;
    }
    internal Action<Uri> OmpOpenBrowser { get; set; } = uri =>
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

    private void UpdateOmpLoginButtons(bool busy)
    {
        if (OmpLoginPanel is null) return;
        var ompSelected = CliToolsListBox.SelectedItem is CliToolRow { ToolId: CliDiscovery.OmpToolId };
        OmpLoginPanel.Visibility = ompSelected ? Visibility.Visible : Visibility.Collapsed;
        OmpLoginProviderCombo.IsEnabled = !busy;
        OmpLoginButton.IsEnabled = !busy && _cliConnection is not null &&
            OmpLoginProviderCombo.SelectedItem is OmpRpcBackend.LoginProvider { Available: true };
        OmpLoginButton.Content = OmpLoginProviderCombo.SelectedItem is OmpRpcBackend.LoginProvider { Authenticated: true }
            ? "重新授权" : "通过 OMP 登录";
        OmpAddProviderButton.Content = _showNewOmpProviders ? "返回已登录提供方" : "登录其他提供方";
        OmpAddProviderButton.IsEnabled = !busy && (_showNewOmpProviders
            ? _ompProviders.Any(p => p.Authenticated) : _ompProviders.Any(p => !p.Authenticated && p.Available));
    }

    private void OmpLoginProvider_Changed(object sender, SelectionChangedEventArgs e)
        => UpdateOmpLoginButtons(_cliOperation is not null);

    private async void OmpLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_cliOperation is not null || _cliConnection is null ||
            OmpLoginProviderCombo.SelectedItem is not OmpRpcBackend.LoginProvider { Available: true } provider) return;
        var connection = _cliConnection.Clone();
        using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        _cliOperation = operation;
        _cliTestedModel = null; _cliTestedAt = null;
        CliConnectionStatus.Text = "正在通过 OMP 发起授权。请在浏览器中完成登录，可随时取消。";
        UpdateCliButtons();
        try
        {
            var result = await Task.Run(async () =>
            {
                using var backend = new OmpRpcBackend(connection);
                await backend.LoginAsync(provider.Id,
                    async (uri, instructions, ct) => await Dispatcher.InvokeAsync(() =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!ReferenceEquals(_cliOperation, operation)) throw new OperationCanceledException(ct);
                        OmpOpenBrowser(uri);
                        OmpAuthorizationInstructions.Text = instructions ?? "";
                        OmpAuthorizationInstructions.Visibility = string.IsNullOrWhiteSpace(instructions) ? Visibility.Collapsed : Visibility.Visible;
                        CliConnectionStatus.Text = "请在浏览器中完成授权，OMP 正在等待登录结果。";
                    }).Task,
                    (_, ct) => RequestOmpCallbackAsync(operation, ct), operation.Token);
                var providers = await backend.ListLoginProvidersAsync(operation.Token);
                var models = await backend.ListModelsAsync(operation.Token);
                return (providers, models);
            });
            if (!ReferenceEquals(_cliOperation, operation)) return;
            SetOmpProviders(result.providers, provider.Id);
            var models = AuthenticatedOmpModels(result.models, result.providers);
            CliModelCombo.ItemsSource = models;
            CliModelCombo.SelectedItem = models.FirstOrDefault(m => m.Provider == provider.Id) ?? models.FirstOrDefault();
            _cliReady = result.providers.Any(p => p.Authenticated);
            CliConnectionStatus.Text = result.providers.Any(p => p.Id == provider.Id && p.Authenticated)
                ? "OMP 已确认登录并保存账号。选择模型后可测试或保存。"
                : "OMP 已结束授权，但未确认账号状态。请刷新账号后检查，尚未验证可用。";
        }
        catch (OperationCanceledException)
        { if (ReferenceEquals(_cliOperation, operation)) CliConnectionStatus.Text = "OMP 登录已取消或超时。已有登录保留；可以刷新账号检查状态。"; }
        catch (Exception ex)
        { if (ReferenceEquals(_cliOperation, operation)) CliConnectionStatus.Text = $"OMP 登录未完成。{CliFailureText(ex)}"; }
        finally
        {
            if (ReferenceEquals(_cliOperation, operation))
            { _cliOperation = null; HideOmpCallback(); UpdateCliButtons(); }
        }
    }

    private async Task<string> RequestOmpCallbackAsync(CancellationTokenSource operation, CancellationToken ct)
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_cliOperation, operation)) throw new OperationCanceledException(ct);
            _ompCallback = pending;
            OmpCallbackInput.Clear();
            OmpCallbackPanel.Visibility = Visibility.Visible;
            CliConnectionStatus.Text = "OMP 需要授权回调。完成浏览器授权后粘贴回调地址或授权码，并提交。不要输入账号密码。";
            OmpCallbackInput.Focus();
        }).Task;
        try { return await pending.Task.WaitAsync(ct).ConfigureAwait(false); }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            { if (ReferenceEquals(_ompCallback, pending)) HideOmpCallback(); }).Task;
        }
    }

    private void OmpSubmitCallback_Click(object sender, RoutedEventArgs e)
    {
        if (_ompCallback is null || _cliOperation is null) return;
        var value = OmpCallbackInput.Text.Trim();
        if (value.Length == 0 || value.Length > 16384 || value.Contains('\r') || value.Contains('\n'))
        { CliConnectionStatus.Text = "请粘贴有效的单行授权回调地址或授权码。"; return; }
        _ompCallback.TrySetResult(value);
        OmpSubmitCallbackButton.IsEnabled = false;
        CliConnectionStatus.Text = "回调已提交，正在等待 OMP 确认并刷新模型…";
    }

    private void HideOmpCallback()
    {
        _ompCallback = null;
        if (OmpCallbackPanel is null) return;
        OmpCallbackPanel.Visibility = Visibility.Collapsed;
        OmpCallbackInput.Clear();
        OmpAuthorizationInstructions.Text = "";
        OmpAuthorizationInstructions.Visibility = Visibility.Collapsed;
        OmpSubmitCallbackButton.IsEnabled = true;
    }
}
