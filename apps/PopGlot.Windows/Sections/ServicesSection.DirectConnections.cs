using System.Windows;
using System.Windows.Controls;
using PopGlot.Windows.Services;
using PopGlot.Windows.Services.AiConnections;

namespace PopGlot.Windows.Sections;

public partial class ServicesSection
{
    private bool _loadingAccounts;
    private string? _directProfileId;
    private string? _directTestIdentity;
    private DateTime? _directTestedAt;
    private bool _directModelsCurrent;
    private void ReloadChatGptAccounts(string? selectedId = null)
    {
        _loadingAccounts = true;
        try
        {
            selectedId ??= (ChatGptAccountCombo.SelectedItem as ChatGptAccount)?.Id;
            var accounts = ChatGptAccountStore.List(); ChatGptAccountCombo.ItemsSource = accounts;
            ChatGptAccountCombo.SelectedItem = selectedId is null ? accounts.FirstOrDefault() : accounts.FirstOrDefault(a => a.Id == selectedId);
            ChatGptAccountDetails.Visibility = accounts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _loadingAccounts = false; UpdateChatGptButtons(); }
    }
    private void UpdateChatGptButtons()
    {
        if (ChatGptLoginButton is null) return;
        var busy = _cliOperation is not null;
        var account = ChatGptAccountCombo.SelectedItem as ChatGptAccount;
        var signedIn = account is not null && ChatGptAccountStore.HasCredentials(account.Id);
        ChatGptLoginButton.IsEnabled = ChatGptAddAccountButton.IsEnabled = !busy;
        ChatGptCancelButton.Visibility = busy && !_cliCommitInProgress ? Visibility.Visible : Visibility.Collapsed;
        ChatGptAccountCombo.IsEnabled = ChatGptModelCombo.IsEnabled = !busy;
        ChatGptRefreshButton.IsEnabled = ChatGptLogoutButton.IsEnabled = !busy && signedIn;
        ChatGptTestButton.IsEnabled = ChatGptSaveButton.IsEnabled = ChatGptSaveUseButton.IsEnabled = !busy && signedIn && _directModelsCurrent && ChatGptModelCombo.SelectedItem is CliModel;
        ChatGptDisconnectButton.IsEnabled = !busy && _directProfileId is not null;
        if (OmpImportButton is not null)
        {
            OmpImportPanel.Visibility = _cliReady && _cliKind == EngineExecutionKind.OmpRpc && _cliConnection is not null &&
                CliModelCombo.SelectedItem is CliModel importModel && OmpApiKeyImporter.Supports(importModel.Provider) ? Visibility.Visible : Visibility.Collapsed;
            OmpImportButton.IsEnabled = !busy && _cliReady && _cliKind == EngineExecutionKind.OmpRpc && _cliConnection is not null &&
                CliModelCombo.SelectedItem is CliModel model && OmpApiKeyImporter.Supports(model.Provider) && OmpImportConsent.IsChecked == true;
        }
    }
    private void ChatGptAccount_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAccounts || ChatGptStatus is null) return;
        var selected = ChatGptAccountCombo.SelectedItem as ChatGptAccount;
        if (_editorDirty && e.RemovedItems.Count > 0)
        {
            _loadingAccounts = true;
            try { ChatGptAccountCombo.SelectedItem = e.RemovedItems[0]; }
            finally { _loadingAccounts = false; }
            BeginDraftGuard("更换账号前请先处理当前修改。", () =>
            {
                // Saving the previous new-account draft may have minted an
                // ID. Continue editing that profile rather than resetting it.
                ShowConnectionEditor(direct: true, adding: _directProfileId is null);
                SetDirectSourceIdentity();
                _loadingAccounts = true;
                try { ChatGptAccountCombo.SelectedItem = selected; }
                finally { _loadingAccounts = false; }
                SelectDirectAccount();
                // The guard may have resolved via discard (back to picker)
                // or save; either way this continuation lands on a fresh
                // direct editor for the requested account. Take that as the
                // clean baseline instead of comparing against whatever the
                // discard reloaded.
                ClearEditorDirty();
            });
            return;
        }
        SelectDirectAccount();
    }

    private void SelectDirectAccount()
    {
        CancelCliOperation();
        _directTestIdentity = null; _directTestedAt = null; _directModelsCurrent = false;
        ChatGptModelCombo.ItemsSource = null;
        ChatGptStatus.Text = "刷新当前账号的模型后继续。";
        UpdateCliButtons();
        MarkEditorDirty();
    }
    private void ChatGptModel_Changed(object sender, SelectionChangedEventArgs e)
    { _directTestIdentity = null; _directTestedAt = null; UpdateChatGptButtons(); MarkEditorDirty(); }
    private string DirectIdentity() => $"{(ChatGptAccountCombo.SelectedItem as ChatGptAccount)?.Id}|{(ChatGptModelCombo.SelectedItem as CliModel)?.Id}";
    private ProviderProfile CaptureDirectProfile()
    {
        if (!_directModelsCurrent) throw new InvalidOperationException("请先刷新此账号的当前模型列表。");
        if (ChatGptAccountCombo.SelectedItem is not ChatGptAccount account || ChatGptModelCombo.SelectedItem is not CliModel model)
            throw new InvalidOperationException("请先登录并选择 ChatGPT 模型。");
        var existing = ProfileManager.Load().Profiles.FirstOrDefault(p => p.Id == _directProfileId);
        return new ProviderProfile { Id = _directProfileId ?? "chatgpt-" + Guid.NewGuid().ToString("N"),
            Name = existing?.Name ?? $"ChatGPT · {account.DisplayName} · {model.DisplayName}", ExecutionKind = EngineExecutionKindNames.ChatGptDirect,
            DirectConnection = new() { AccountId = account.Id, ModelId = model.Id, Enabled = true }, TextModel = model.Id,
            ApiBaseUrl = "https://api.openai.com/v1", TextEndpoint = "/responses", CredentialTarget = "", SupportsVision = false, VisionModel = "" };
    }
    private async Task<bool> DirectOperation(Func<CancellationToken, Task<string>> work, string starting, bool committing = false)
    {
        if (_cliOperation is not null) return false;
        using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(6)); _cliOperation = operation;
        _cliCommitInProgress = committing;
        var status = ChatGptConnectionPanel.Visibility == Visibility.Visible ? ChatGptStatus : CliConnectionStatus;
        status.Text = starting; UpdateCliButtons();
        try
        {
            var message = await work(operation.Token);
            if (!ReferenceEquals(_cliOperation, operation)) return false;
            status.Text = message;
            return true;
        }
        catch (OperationCanceledException)
        { if (ReferenceEquals(_cliOperation, operation)) status.Text = "操作已取消或超时。"; return false; }
        catch (Exception ex)
        {
            if (ex is ProfileActivationException)
            { RefreshProfilesList(); EditorEmpty.Visibility = Visibility.Collapsed; ProfileChanged?.Invoke(); }
            if (ReferenceEquals(_cliOperation, operation)) status.Text = CliFailureText(ex);
            return false;
        }
        finally { if (ReferenceEquals(_cliOperation, operation)) { _cliCommitInProgress = false; _cliOperation = null; UpdateCliButtons(); } }
    }
    private async void ChatGptLogin_Click(object sender, RoutedEventArgs e)
    {
        var selected = ReferenceEquals(sender, ChatGptAddAccountButton) ? null : ChatGptAccountCombo.SelectedItem as ChatGptAccount;
        var selectedModel = (ChatGptModelCombo.SelectedItem as CliModel)?.Id;
        await DirectOperation(async ct =>
        {
            var account = await ChatGptOAuth.SignInAsync(selected, ct); ct.ThrowIfCancellationRequested();
            ReloadChatGptAccounts(account.Id); _directModelsCurrent = false;
            _directTestIdentity = null; _directTestedAt = null;
            if (selected?.Id != account.Id)
            {
                selectedModel = null;
                ChatGptModelCombo.ItemsSource = null;
            }
            MarkEditorDirty();
            using var backend = new ChatGptDirectBackend(new() { AccountId = account.Id, Enabled = true });
            var models = await backend.ListModelsAsync(ct); ct.ThrowIfCancellationRequested();
            _directModelsCurrent = true;
            ChatGptModelCombo.ItemsSource = models;
            ChatGptModelCombo.SelectedItem = selectedModel is null ? models.FirstOrDefault() : models.FirstOrDefault(m => m.Id == selectedModel);
            return selectedModel is not null && ChatGptModelCombo.SelectedItem is null
                ? "原模型未出现在当前列表中。请选择一个模型后再保存。" : "官方登录已保存。选择模型后可测试或保存。";
        }, "正在打开官方登录页面。完成授权后请回到 PopGlot；可取消等待。");
    }
    private async void ChatGptRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (ChatGptAccountCombo.SelectedItem is not ChatGptAccount account) return;
        var selectedModel = (ChatGptModelCombo.SelectedItem as CliModel)?.Id;
        _directModelsCurrent = false; _directTestIdentity = null; _directTestedAt = null;
        await DirectOperation(async ct =>
        {
            using var backend = new ChatGptDirectBackend(new() { AccountId = account.Id, Enabled = true });
            var models = await backend.ListModelsAsync(ct); ct.ThrowIfCancellationRequested();
            _directModelsCurrent = true;
            ChatGptModelCombo.ItemsSource = models;
            ChatGptModelCombo.SelectedItem = selectedModel is null ? models.FirstOrDefault() : models.FirstOrDefault(m => m.Id == selectedModel);
            return models.Count == 0 ? "此账号没有返回可选择的文字模型。"
                : ChatGptModelCombo.SelectedItem is null ? "原模型未出现在当前列表中。请选择一个模型后再保存。"
                : "模型已刷新，可测试或保存。";
        }, "正在读取此账号与工作区的官方模型列表…");
    }
    private async void ChatGptTest_Click(object sender, RoutedEventArgs e)
    {
        if (_cliOperation is not null) return;
        var profile = CaptureDirectProfile(); var identity = DirectIdentity();
        _directTestIdentity = null;
        await DirectOperation(async ct =>
        {
            var response = await CliTranslationExecutor.Stream(profile, "Hello world", "en", "zh", null, ct, enforceSavedState: false).Completion;
            ct.ThrowIfCancellationRequested();
            if (response.Result.IsPartial || response.Result.Warnings.Count > 0 || string.IsNullOrWhiteSpace(response.Result.TranslatedText))
                throw new InvalidOperationException("直连翻译未完整返回，请重试。");
            _directTestIdentity = identity; _directTestedAt = DateTime.UtcNow;
            return $"直连测试通过：{response.Result.TranslatedText}（{response.ElapsedMs / 1000.0:F1} 秒）。";
        }, "正在通过官方 Responses API 测试翻译…");
    }
    private async void ChatGptSaveUse_Click(object sender, RoutedEventArgs e) =>
        await SaveDirectConnectionAsync(ReferenceEquals(sender, ChatGptSaveUseButton));

    private async Task<bool> SaveDirectConnectionAsync(bool use)
    {
        if (_cliOperation is not null) return false;
        return await DirectOperation(async ct =>
        {
            var profile = CaptureDirectProfile();
            var editingId = _directProfileId;
            if (_directTestIdentity == DirectIdentity())
            { profile.LastTestOutcome = "ok"; profile.LastTestedAtUtc = _directTestedAt; profile.LastTestFingerprint = ProfileVerification.EvidenceFingerprint(profile, false); }
            try { await Task.Run(() => ProfileManager.SaveCliProfile(profile, editingId, use)); }
            catch (ProfileActivationException) { _directProfileId = profile.Id; throw; }
            _directProfileId = _editingProfileId = profile.Id;
            RefreshProfilesList(); EditorEmpty.Visibility = Visibility.Collapsed; ProfileChanged?.Invoke();
            ClearEditorDirty();
            return use ? "已保存并使用 ChatGPT。" : "已保存 ChatGPT 连接。";
        }, "正在保存…", committing: true);
    }
    private async void ChatGptDisconnect_Click(object sender, RoutedEventArgs e)
    {
        var id = _directProfileId;
        await DirectOperation(async ct =>
        {
            if (id is not null) await Task.Run(() => ProfileManager.DisableCliProfile(id));
            _directTestIdentity = null; _directTestedAt = null; _directModelsCurrent = false; ChatGptModelCombo.ItemsSource = null;
            RefreshProfilesList(); EditorEmpty.Visibility = Visibility.Collapsed; ProfileChanged?.Invoke();
            ClearEditorDirty();
            return "已断开此引擎。ChatGPT 登录保留，可重新选择模型保存。";
        }, "正在断开直连引擎…", committing: true);
    }
    private async void ChatGptLogout_Click(object sender, RoutedEventArgs e)
    {
        if (ChatGptAccountCombo.SelectedItem is not ChatGptAccount account) return;
        await DirectOperation(async ct =>
        {
            try { await ChatGptOAuth.SignOutAsync(account.Id, ct); }
            finally
            {
                if (!ct.IsCancellationRequested)
                { _directTestIdentity = null; _directTestedAt = null; _directModelsCurrent = false; ChatGptModelCombo.ItemsSource = null; }
                if (!ct.IsCancellationRequested) ClearEditorDirty();
                ProfileChanged?.Invoke();
            }
            return "已退出此账号。账号与工作区注册保留，之后可重新授权。";
        }, "正在退出此账号并撤销官方会话…");
    }
    private void ChatGptSource_Click(object sender, RoutedEventArgs e)
    {
        if (_cliCommitInProgress || _saveInProgress) return;
        ShowConnectionEditor(direct: true, adding: true);
        _editingProfileId = _directProfileId = null;
        _directTestIdentity = null; _directTestedAt = null; _directModelsCurrent = false;
        ChatGptModelCombo.ItemsSource = null;
        SetDirectSourceIdentity();
        ReloadChatGptAccounts();
        ChatGptStatus.Text = ChatGptAccountCombo.SelectedItem is null
            ? "在官方页面登录后选择模型。" : "已保存账号。刷新模型以检查当前授权。";
        ClearEditorDirty();
    }

    private void SetDirectSourceIdentity()
    {
        ConnectionSourceTitle.Text = "ChatGPT";
        ConnectionSourceDetail.Text = "官方账号直连，无需 CLI 或 API Key。";
        ConnectionSourceDetail.ToolTip = "通过官方浏览器授权；内容直接发送到 OpenAI，并使用所选 ChatGPT 计划额度。账号授权独立保存并由 Windows 加密保护。请求可用性需显式测试确认。";
    }

    private void OpenDirectProfile(ProviderProfile profile)
    {
        if (_cliCommitInProgress || _saveInProgress) return;
        ShowConnectionEditor(direct: true, adding: false);
        _editingProfileId = profile.Id;
        SetDirectSourceIdentity();
        ReloadChatGptAccounts(profile.DirectConnection?.AccountId);
        RestoreDirectProfile(profile);
        ClearEditorDirty();
        UpdateCliButtons();
    }
    private void RestoreDirectProfile(ProviderProfile profile)
    {
        _directProfileId = profile.Id; _directTestIdentity = null; _directTestedAt = null; _directModelsCurrent = false;
        var modelId = profile.DirectConnection?.ModelId ?? profile.TextModel;
        var model = new CliModel(modelId, modelId); ChatGptModelCombo.ItemsSource = new[] { model }; ChatGptModelCombo.SelectedItem = model;
        ChatGptStatus.Text = "已回显保存的模型。刷新模型后可测试或保存。";
    }
    private void OmpImportConsent_Changed(object sender, RoutedEventArgs e) => UpdateChatGptButtons();
    private async void OmpImport_Click(object sender, RoutedEventArgs e)
    {
        if (_cliOperation is not null || CliModelCombo.SelectedItem is not CliModel model || !OmpApiKeyImporter.Supports(model.Provider)) return;
        var confirmed = OmpImportConsent.IsChecked == true;
        await DirectOperation(async ct =>
        {
            var profile = await Task.Run(() => OmpApiKeyImporter.Import(model.Provider!, model.Id, confirmed, ct: ct), ct);
            RefreshProfilesList(); EditorEmpty.Visibility = Visibility.Collapsed; ProfileChanged?.Invoke();
            OmpImportConsent.IsChecked = false;
            return $"已保存并切换到 {profile.Name}。尚未测试 API 直连，可在引擎列表测试。";
        }, "正在导入所选提供方的普通 API Key…", committing: true);
    }
}
