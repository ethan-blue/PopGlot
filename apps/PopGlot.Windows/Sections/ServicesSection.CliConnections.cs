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
        public string StateBadge { get; init; } = "仅发现入口";
        public string StateDetail { get; init; } = "账号与模型尚未检查";
    }
    private CliConnectionSettings? _cliConnection;
    private EngineExecutionKind _cliKind;
    private CancellationTokenSource? _cliOperation;
    private CancellationTokenSource? _cliScanOperation;
    private string? _cliProfileId;
    private string? _cliTestedModel;
    private DateTime? _cliTestedAt;
    private bool _cliReady;
    private bool _cliCommitInProgress;
    internal Func<string, CancellationToken, IReadOnlyList<DiscoveredCliTool>> DiscoverCliTools { get; set; } =
        (toolId, ct) => CliDiscovery.Discover(toolId, cancellationToken: ct);

    private void ResetCliConnection()
    {
        _cliReady = false;
        _cliConnection = null; _cliProfileId = null; _cliTestedModel = null; _cliTestedAt = null;
        CliModelCombo.ItemsSource = null;
        OmpLoginProviderCombo.ItemsSource = null;
    }

    private void OpenCliProfile(ProviderProfile profile)
    {
        if (_cliCommitInProgress || _saveInProgress) return;
        OpenCliConnections_Click(this, new RoutedEventArgs());
        CancelCliOperation();
        var rows = (CliToolsListBox.ItemsSource as IEnumerable<CliToolRow>)?.ToList() ?? [];
        var toolId = profile.ResolvedExecutionKind == EngineExecutionKind.CodexAppServer ? CliDiscovery.CodexToolId : CliDiscovery.OmpToolId;
        var selected = rows.FirstOrDefault(r => r.ToolId == toolId &&
            string.Equals(r.EntryPath, profile.CliConnection?.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            selected = new CliToolRow { ToolId = toolId, ToolName = toolId == CliDiscovery.CodexToolId ? "Codex CLI" : "oh-my-pi",
                EntryPath = profile.CliConnection?.ExecutablePath ?? "", StateBadge = "已保存入口", StateDetail = "点击连接以检查当前状态" };
            rows.Add(selected);
            CliToolsListBox.ItemsSource = rows;
        }
        CliToolsListBox.SelectedItem = selected;
        RestoreCliProfile(profile);
    }

    private void RestoreCliProfile(ProviderProfile profile)
    {
        _cliProfileId = profile.Id; _cliKind = profile.ResolvedExecutionKind;
        _cliConnection = profile.CliConnection?.Clone(); _cliReady = false;
        _cliTestedModel = null; _cliTestedAt = null;
        var model = new CliModel(profile.CliConnection?.ModelId ?? profile.TextModel,
            profile.CliConnection?.ModelId ?? profile.TextModel,
            Provider: profile.ResolvedExecutionKind == EngineExecutionKind.OmpRpc ? profile.CliConnection?.UpstreamProviderId : null);
        CliModelCombo.ItemsSource = new[] { model }; CliModelCombo.SelectedItem = model;
        CliConnectionStatus.Text = "已回显保存的模型。点击「连接 / 刷新账号」检查当前状态。";
        UpdateCliButtons();
    }

    private void OpenCliConnections_Click(object sender, RoutedEventArgs e)
    {
        if (_cliCommitInProgress || _saveInProgress) return;
        CancelCliOperation();
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

    private async void DetectCliTools_Click(object sender, RoutedEventArgs e)
    {
        if (_cliOperation is not null) return;
        var selected = CliToolsListBox.SelectedItem as CliToolRow;
        var saved = _cliProfileId is { } id ? ProfileManager.Load().Profiles.FirstOrDefault(p => p.Id == id) : null;
        using var operation = new CancellationTokenSource();
        _cliOperation = _cliScanOperation = operation;
        _cliReady = false; _cliTestedModel = null; _cliTestedAt = null;
        OmpLoginProviderCombo.ItemsSource = null;
        CliDetectStatusText.Text = "正在检测本机工具入口…";
        UpdateCliButtons();
        try
        {
            var rows = await Task.Run(() =>
            {
                var found = new List<CliToolRow>();
                foreach (var (toolId, name) in new[] { (CliDiscovery.OmpToolId, "oh-my-pi"), (CliDiscovery.CodexToolId, "Codex CLI") })
                {
                    operation.Token.ThrowIfCancellationRequested();
                    found.AddRange(DiscoverCliTools(toolId, operation.Token).Select(tool =>
                        new CliToolRow { ToolId = toolId, ToolName = name, EntryPath = tool.ExecutablePath }));
                }
                foreach (var profile in ProfileManager.Load().Profiles.Where(p => p.ExecutionFacts.IsCliExecution && p.CliConnection is not null))
                {
                    operation.Token.ThrowIfCancellationRequested();
                    var toolId = profile.ResolvedExecutionKind == EngineExecutionKind.CodexAppServer ? CliDiscovery.CodexToolId : CliDiscovery.OmpToolId;
                    if (found.Any(r => r.ToolId == toolId && string.Equals(r.EntryPath, profile.CliConnection!.ExecutablePath, StringComparison.OrdinalIgnoreCase))) continue;
                    found.Add(new CliToolRow { ToolId = toolId, ToolName = toolId == CliDiscovery.CodexToolId ? "Codex CLI" : "oh-my-pi",
                        EntryPath = profile.CliConnection!.ExecutablePath, StateBadge = "已保存入口", StateDetail = "本次未发现，请连接检查或重新安装工具" });
                }
                return found;
            }, operation.Token);
            if (!ReferenceEquals(_cliOperation, operation)) return;
            _cliOperation = null;
            CliToolsListBox.ItemsSource = rows;
            CliToolsListBox.SelectedItem = rows.FirstOrDefault(r => r.ToolId == selected?.ToolId &&
                string.Equals(r.EntryPath, selected.EntryPath, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault();
            if (saved is not null) RestoreCliProfile(saved);
            CliDetectStatusText.Text = rows.Count > 0 ? $"{rows.Count} 个工具入口；账号和可用性需点击连接检查。" : "未发现工具。安装 Codex CLI 或 oh-my-pi 后重新检测。";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (ReferenceEquals(_cliOperation, operation)) CliDetectStatusText.Text = "检测失败。请检查工具安装位置后重试。"; }
        finally
        {
            if (ReferenceEquals(_cliScanOperation, operation)) _cliScanOperation = null;
            if (ReferenceEquals(_cliOperation, operation)) _cliOperation = null;
            UpdateCliButtons();
        }
    }

    private void CliToolsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CliConnectButton is null) return;
        CancelCliOperation();
        ResetCliConnection();
        var row = CliToolsListBox.SelectedItem as CliToolRow;
        var saved = ProfileManager.Load().Profiles.FirstOrDefault(p => p.ExecutionFacts.IsCliExecution &&
            p.ResolvedExecutionKind == (row?.ToolId == CliDiscovery.CodexToolId ? EngineExecutionKind.CodexAppServer : EngineExecutionKind.OmpRpc) &&
            string.Equals(p.CliConnection?.ExecutablePath, row?.EntryPath, StringComparison.OrdinalIgnoreCase));
        if (saved is not null) RestoreCliProfile(saved);
        else CliConnectionStatus.Text = "点击连接检查账号，再选择模型。";
        UpdateCliButtons();
    }

    private async void CliConnect_Click(object sender, RoutedEventArgs e)
    {
        if (CliToolsListBox.SelectedItem is not CliToolRow row || _cliOperation is not null) return;
        var editingId = _cliProfileId;
        var existing = ProfileManager.Load().Profiles.FirstOrDefault(p => p.Id == editingId);
        var selectedModel = CliModelCombo.SelectedItem as CliModel;
        var operation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        _cliOperation = operation;
        _cliReady = false; _cliConnection = null; _cliTestedModel = null; _cliTestedAt = null; CliModelCombo.ItemsSource = null;
        OmpLoginProviderCombo.ItemsSource = null;
        var kind = row.ToolId == CliDiscovery.CodexToolId ? EngineExecutionKind.CodexAppServer : EngineExecutionKind.OmpRpc;
        _cliKind = kind;
        var connection = new CliConnectionSettings { Enabled = true, ExecutablePath = row.EntryPath };
        CliConnectionStatus.Text = "正在检查版本、账号状态和模型…";
        UpdateCliButtons();
        try
        {
            var result = await Task.Run(async () =>
            {
                using var backend = CliTranslationExecutor.CreateBackend(kind, connection);
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
            _cliReady = result.account.State != CliConnectionState.NeedsLogin;
            _cliProfileId = existing?.Id;
            CliModelCombo.SelectedItem = result.models.FirstOrDefault(m => m.Id == selectedModel?.Id &&
                (kind != EngineExecutionKind.OmpRpc || m.Provider == selectedModel?.Provider))
                ?? (selectedModel is null ? result.models.FirstOrDefault(m => m.IsDefault) ?? result.models.FirstOrDefault() : null);
            CliConnectionStatus.Text = result.account.DetailZh ?? "账号状态已读取，选择模型后测试。";
            if (result.models.Count > 0 && selectedModel is not null && CliModelCombo.SelectedItem is null)
                CliConnectionStatus.Text = "原模型未出现在当前列表中。请选择一个模型后再保存。";
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
        if (!_cliReady || _cliConnection is null || CliModelCombo.SelectedItem is not CliModel model)
            throw new InvalidOperationException("请先连接并选择当前可用模型。");
        var row = (CliToolRow)CliToolsListBox.SelectedItem;
        var existing = ProfileManager.Load().Profiles.FirstOrDefault(p => p.Id == _cliProfileId);
        return new ProviderProfile
        {
            Id = _cliProfileId ?? $"cli-{Guid.NewGuid():N}", Name = existing?.Name ?? $"{row.ToolName} · {model.DisplayName}",
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
        _cliCommitInProgress = true;
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
            RefreshProfilesList();
            ProfileChanged?.Invoke();
            if (!ReferenceEquals(_cliOperation, operation)) return;
            _cliProfileId = profile.Id;
            EditorEmpty.Visibility = Visibility.Collapsed;
            CliConnectionStatus.Text = use ? "已保存并切换为文字引擎。现在可以翻译。" : "连接已保存。可在引擎列表中切换使用。";
        }
        catch (Exception ex)
        {
            if (ex is ProfileActivationException activation)
            { _cliProfileId = activation.ProfileId; RefreshProfilesList(); EditorEmpty.Visibility = Visibility.Collapsed; ProfileChanged?.Invoke(); }
            if (ReferenceEquals(_cliOperation, operation)) CliConnectionStatus.Text = CliFailureText(ex);
        }
        finally { _cliCommitInProgress = false; if (ReferenceEquals(_cliOperation, operation)) _cliOperation = null; UpdateCliButtons(); }
    }

    private async void CliDisconnect_Click(object sender, RoutedEventArgs e)
    {
        CancelCliOperation();
        if (_cliCommitInProgress) return;
        using var operation = new CancellationTokenSource();
        _cliOperation = operation;
        _cliCommitInProgress = true;
        var profileId = _cliProfileId;
        _cliReady = false;
        UpdateCliButtons();
        try
        {
            if (profileId is { } id) await Task.Run(() => ProfileManager.DisableCliProfile(id));
            _cliConnection = null; _cliTestedModel = null; _cliTestedAt = null; CliModelCombo.ItemsSource = null;
            RefreshProfilesList(); EditorEmpty.Visibility = Visibility.Collapsed; ProfileChanged?.Invoke();
            if (!ReferenceEquals(_cliOperation, operation)) return;
            CliConnectionStatus.Text = "已断开 PopGlot 的连接。工具自己的登录保留。";
        }
        catch (Exception ex) { if (ReferenceEquals(_cliOperation, operation)) CliConnectionStatus.Text = CliFailureText(ex); }
        finally { _cliCommitInProgress = false; if (ReferenceEquals(_cliOperation, operation)) { _cliOperation = null; UpdateCliButtons(); } }
    }

    private void CliCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cliCommitInProgress) return;
        CancelCliOperation(); CliConnectionStatus.Text = ChatGptStatus.Text = "操作已取消。";
        CliDetectStatusText.Text = "检测结果仅表示工具入口；可重新检测。";
        UpdateCliButtons();
    }
    private void CancelCliOperation()
    {
        if (_cliCommitInProgress) return;
        var operation = _cliOperation; _cliOperation = null;
        if (operation is not null && ReferenceEquals(_cliScanOperation, operation))
        { _cliScanOperation = null; CliDetectStatusText.Text = "检测已取消，可重新检测。"; }
        operation?.Cancel(); _ompCallback?.TrySetCanceled(); HideOmpCallback();
    }
    private string ModelIdentity() => $"{_cliConnection?.ExecutablePath}|{_cliConnection?.ExpectedVersion}|{_cliConnection?.UpstreamProviderId}|{_cliConnection?.ModelId}";
    private void UpdateCliButtons()
    {
        var busy = _cliOperation is not null;
        var ready = _cliReady && _cliConnection is not null && CliModelCombo.SelectedItem is CliModel;
        CliConnectButton.IsEnabled = !busy && CliToolsListBox.SelectedItem is CliToolRow;
        DetectCliToolsButton.IsEnabled = CliToolsListBox.IsEnabled = CliModelCombo.IsEnabled = !busy;
        CliTestButton.IsEnabled = CliSaveButton.IsEnabled = CliSaveUseButton.IsEnabled = ready && !busy;
        CliDisconnectButton.IsEnabled = !busy && (_cliConnection is not null || _cliProfileId is not null);
        CliCancelButton.Visibility = busy && !_cliCommitInProgress ? Visibility.Visible : Visibility.Collapsed;
        BackToListHeaderButton.IsEnabled = !_cliCommitInProgress && !_saveInProgress;
        UpdateOmpLoginButtons(busy);
        UpdateChatGptButtons();
    }
    private static string CliFailureText(Exception ex) => ex is InvalidOperationException
        ? ex.Message : "账号或工具操作失败。请检查登录状态、程序入口和网络后重试。";
}
