using System.Windows;
using System.Windows.Controls;
using PopGlot.Windows.Services;
using PopGlot.Windows.Services.AiConnections;

namespace PopGlot.Windows.Sections;

public partial class ServicesSection
{
    public sealed class CliToolRow
    {
        public string ToolId { get; init; } = "";
        public string ToolName { get; init; } = "";
        public string EntryPath { get; init; } = "";
        public string StateBadge { get; init; } = "已检测到";
        public string StateDetail { get; init; } = "选择后连接，读取工具自己提供的账号状态和模型。";
    }
    private CliConnectionSettings? _cliConnection;
    private EngineExecutionKind _cliKind;
    private CancellationTokenSource? _cliOperation;
    private string? _cliProfileId;
    private string? _cliTestedModel;
    private DateTime? _cliTestedAt;

    private void OpenCliConnections_Click(object sender, RoutedEventArgs e)
    {
        AddEngineHeaderButton.Visibility = Visibility.Collapsed;
        ConnectLocalToolHeaderButton.Visibility = Visibility.Collapsed;
        BackToListHeaderButton.Visibility = Visibility.Visible;
        EditorEmpty.Visibility = Visibility.Collapsed;
        EditorForm.Visibility = Visibility.Collapsed;
        CliConnectionsPanel.Visibility = Visibility.Visible;
        if (CliToolsListBox.Items.Count == 0) DetectCliTools_Click(sender, e);
        ReloadChatGptAccounts();
        EditorOpenStateChanged?.Invoke();
    }

    private void DetectCliTools_Click(object sender, RoutedEventArgs e)
    {
        if (_cliOperation is not null) return;
        var rows = new List<CliToolRow>();
        foreach (var (id, name) in new[] { (CliDiscovery.OmpToolId, "oh-my-pi"), (CliDiscovery.CodexToolId, "Codex CLI") })
            rows.AddRange(CliDiscovery.Discover(id).Select(tool => new CliToolRow { ToolId = id, ToolName = name, EntryPath = tool.ExecutablePath }));
        CliToolsListBox.ItemsSource = rows;
        CliToolsListBox.SelectedIndex = rows.Count > 0 ? 0 : -1;
        CliDetectStatusText.Text = rows.Count > 0 ? $"发现 {rows.Count} 个安装。选择工具后点击「连接」。" : "未发现工具。安装 Codex CLI 或 oh-my-pi 后重新检测。";
    }

    private void CliToolsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CliConnectButton is null) return;
        CancelCliOperation();
        _cliConnection = null; _cliProfileId = null; _cliTestedModel = null; _cliTestedAt = null;
        CliModelCombo.ItemsSource = null;
        OmpLoginProviderCombo.ItemsSource = null;
        CliConnectionStatus.Text = "连接后可选择模型并测试翻译。登录由工具自己管理，无需复制 API Key。";
        UpdateCliButtons();
    }

    private async void CliConnect_Click(object sender, RoutedEventArgs e)
    {
        if (CliToolsListBox.SelectedItem is not CliToolRow row || _cliOperation is not null) return;
        var operation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        _cliOperation = operation;
        _cliConnection = null; _cliTestedModel = null; _cliTestedAt = null; CliModelCombo.ItemsSource = null;
        OmpLoginProviderCombo.ItemsSource = null;
        _cliKind = row.ToolId == CliDiscovery.CodexToolId ? EngineExecutionKind.CodexAppServer : EngineExecutionKind.OmpRpc;
        var connection = new CliConnectionSettings { Enabled = true, ExecutablePath = row.EntryPath };
        CliConnectionStatus.Text = "正在检查版本、账号状态和模型…";
        UpdateCliButtons();
        try
        {
            var result = await Task.Run(async () =>
            {
                using var backend = CliTranslationExecutor.CreateBackend(_cliKind, connection);
                var account = await backend.CheckAccountAsync(operation.Token);
                var models = account.State == CliConnectionState.NeedsLogin ? Array.Empty<CliModel>() : await backend.ListModelsAsync(operation.Token);
                var providers = backend is OmpRpcBackend omp ? await omp.ListLoginProvidersAsync(operation.Token) : Array.Empty<OmpRpcBackend.LoginProvider>();
                return (account, models, providers);
            });
            if (!ReferenceEquals(_cliOperation, operation)) return;
            _cliConnection = connection;
            OmpLoginProviderCombo.ItemsSource = result.providers;
            OmpLoginProviderCombo.SelectedItem = result.providers.FirstOrDefault(p => p.Authenticated) ?? result.providers.FirstOrDefault(p => p.Available);
            CliModelCombo.ItemsSource = result.models;
            var existing = ProfileManager.Load().Profiles.FirstOrDefault(p => p.ResolvedExecutionKind == _cliKind &&
                string.Equals(p.CliConnection?.ExecutablePath, row.EntryPath, StringComparison.OrdinalIgnoreCase));
            _cliProfileId = existing?.Id;
            CliModelCombo.SelectedItem = result.models.FirstOrDefault(m => m.Id == existing?.CliConnection?.ModelId && m.Provider == existing?.CliConnection?.UpstreamProviderId)
                ?? result.models.FirstOrDefault(m => m.IsDefault) ?? result.models.FirstOrDefault();
            CliConnectionStatus.Text = result.account.DetailZh ?? "账号状态已读取，选择模型后测试。";
            if (result.models.Count == 0 && result.account.State != CliConnectionState.NeedsLogin)
                CliConnectionStatus.Text = "连接成功，但工具未返回支持的模型。请先在工具中配置提供方后重试。";
        }
        catch (OperationCanceledException)
        { if (ReferenceEquals(_cliOperation, operation)) CliConnectionStatus.Text = "连接已取消或超时。检查工具登录和网络后重试。"; }
        catch (Exception ex)
        { if (ReferenceEquals(_cliOperation, operation)) CliConnectionStatus.Text = CliFailureText(ex); }
        finally
        { if (ReferenceEquals(_cliOperation, operation)) { _cliOperation = null; UpdateCliButtons(); } operation.Dispose(); }
    }

    private void CliModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CliTestButton is null) return;
        _cliTestedModel = null; _cliTestedAt = null;
        OmpImportConsent.IsChecked = false;
        if (_cliConnection is { } connection && CliModelCombo.SelectedItem is CliModel model)
        { connection.ModelId = model.Id; connection.UpstreamProviderId = model.Provider; }
        UpdateCliButtons();
    }

    private ProviderProfile CaptureCliProfile()
    {
        if (_cliConnection is null || CliModelCombo.SelectedItem is not CliModel model)
            throw new InvalidOperationException("请先连接并选择模型。");
        var row = (CliToolRow)CliToolsListBox.SelectedItem;
        return new ProviderProfile
        {
            Id = _cliProfileId ?? $"cli-{Guid.NewGuid():N}", Name = $"{row.ToolName} · {model.DisplayName}",
            ExecutionKind = EngineExecutionKindNames.Format(_cliKind), CliConnection = _cliConnection.Clone(),
            TextModel = model.Id, SupportsText = true, SupportsVision = false, VisionModel = "",
            ApiBaseUrl = "", TextEndpoint = "", VisionEndpoint = "", CredentialTarget = "", IsLocal = false,
        };
    }

    private async void CliTest_Click(object sender, RoutedEventArgs e)
    {
        if (_cliOperation is not null) return;
        var profile = CaptureCliProfile();
        var operation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _cliOperation = operation; _cliTestedModel = null; _cliTestedAt = null;
        CliConnectionStatus.Text = "正在将测试句 Hello world 翻译为中文…";
        UpdateCliButtons();
        try
        {
            var stream = CliTranslationExecutor.Stream(profile, "Hello world", "en", "zh", null, operation.Token, enforceSavedState: false);
            var response = await stream.Completion;
            if (!ReferenceEquals(_cliOperation, operation)) return;
            if (response.Result.IsPartial || response.Result.Warnings.Count > 0 || string.IsNullOrWhiteSpace(response.Result.TranslatedText))
                throw new InvalidOperationException("工具返回了不完整的翻译，请重试。");
            _cliTestedModel = ModelIdentity(); _cliTestedAt = DateTime.UtcNow;
            CliConnectionStatus.Text = $"测试通过：{response.Result.TranslatedText}（{response.ElapsedMs / 1000.0:F1} 秒）。可保存并使用。";
        }
        catch (OperationCanceledException)
        { if (ReferenceEquals(_cliOperation, operation)) CliConnectionStatus.Text = "测试已取消，未保存测试结果。"; }
        catch (Exception ex)
        { if (ReferenceEquals(_cliOperation, operation)) CliConnectionStatus.Text = CliFailureText(ex); }
        finally
        { if (ReferenceEquals(_cliOperation, operation)) { _cliOperation = null; UpdateCliButtons(); } operation.Dispose(); }
    }

    private async void CliSave_Click(object sender, RoutedEventArgs e)
    {
        if (_cliOperation is not null) return;
        using var operation = new CancellationTokenSource();
        _cliOperation = operation; UpdateCliButtons();
        try
        {
            var profile = CaptureCliProfile();
            if (_cliTestedModel == ModelIdentity())
            {
                profile.LastTestOutcome = "ok"; profile.LastTestedAtUtc = _cliTestedAt;
                profile.LastTestFingerprint = ProfileVerification.EvidenceFingerprint(profile, false);
            }
            var use = ReferenceEquals(sender, CliSaveUseButton);
            var editingId = _cliProfileId;
            CliSaveButton.IsEnabled = CliSaveUseButton.IsEnabled = false;
            await Task.Run(() => ProfileManager.SaveCliProfile(profile, editingId, use));
            if (!ReferenceEquals(_cliOperation, operation)) { ProfileChanged?.Invoke(); return; }
            _cliProfileId = profile.Id;
            RefreshProfilesList(); EditorEmpty.Visibility = Visibility.Collapsed;
            ProfileChanged?.Invoke();
            CliConnectionStatus.Text = use ? "已保存并切换为文字引擎。现在可以翻译。" : "连接已保存。可在引擎列表中切换使用。";
        }
        catch (Exception ex) { if (ReferenceEquals(_cliOperation, operation)) CliConnectionStatus.Text = CliFailureText(ex); }
        finally { if (ReferenceEquals(_cliOperation, operation)) _cliOperation = null; UpdateCliButtons(); }
    }

    private async void CliDisconnect_Click(object sender, RoutedEventArgs e)
    {
        CancelCliOperation();
        using var operation = new CancellationTokenSource();
        _cliOperation = operation;
        var profileId = _cliProfileId;
        _cliConnection = null; _cliTestedModel = null; _cliTestedAt = null; CliModelCombo.ItemsSource = null;
        UpdateCliButtons();
        try
        {
            if (profileId is { } id) await Task.Run(() => ProfileManager.DisableCliProfile(id));
            if (!ReferenceEquals(_cliOperation, operation)) { ProfileChanged?.Invoke(); return; }
            CliConnectionStatus.Text = "已断开 PopGlot 的连接。工具自己的登录保留。";
            RefreshProfilesList(); EditorEmpty.Visibility = Visibility.Collapsed; ProfileChanged?.Invoke();
        }
        catch (Exception ex) { if (ReferenceEquals(_cliOperation, operation)) CliConnectionStatus.Text = CliFailureText(ex); }
        finally { if (ReferenceEquals(_cliOperation, operation)) { _cliOperation = null; UpdateCliButtons(); } }
    }

    private void CliCancel_Click(object sender, RoutedEventArgs e)
    { CancelCliOperation(); CliConnectionStatus.Text = ChatGptStatus.Text = "操作已取消。"; UpdateCliButtons(); }
    private void CancelCliOperation()
    { var operation = _cliOperation; _cliOperation = null; operation?.Cancel(); _ompCallback?.TrySetCanceled(); HideOmpCallback(); }
    private string ModelIdentity() => $"{_cliConnection?.ExecutablePath}|{_cliConnection?.ExpectedVersion}|{_cliConnection?.UpstreamProviderId}|{_cliConnection?.ModelId}";
    private void UpdateCliButtons()
    {
        var busy = _cliOperation is not null;
        var ready = _cliConnection is not null && CliModelCombo.SelectedItem is CliModel;
        CliConnectButton.IsEnabled = !busy && CliToolsListBox.SelectedItem is CliToolRow;
        DetectCliToolsButton.IsEnabled = CliToolsListBox.IsEnabled = CliModelCombo.IsEnabled = !busy;
        CliTestButton.IsEnabled = CliSaveButton.IsEnabled = CliSaveUseButton.IsEnabled = ready && !busy;
        CliDisconnectButton.IsEnabled = !busy && (_cliConnection is not null || _cliProfileId is not null);
        CliCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateOmpLoginButtons(busy);
        UpdateChatGptButtons();
    }
    private static string CliFailureText(Exception ex) => ex is InvalidOperationException
        ? ex.Message : "账号或工具操作失败。请检查登录状态、程序入口和网络后重试。";
}
