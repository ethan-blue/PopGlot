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
    private void ReloadChatGptAccounts(string? selectedId = null)
    {
        _loadingAccounts = true;
        try
        {
            selectedId ??= (ChatGptAccountCombo.SelectedItem as ChatGptAccount)?.Id;
            var accounts = ChatGptAccountStore.List(); ChatGptAccountCombo.ItemsSource = accounts;
            ChatGptAccountCombo.SelectedItem = accounts.FirstOrDefault(a => a.Id == selectedId) ?? accounts.FirstOrDefault();
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
        ChatGptCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ChatGptAccountCombo.IsEnabled = ChatGptModelCombo.IsEnabled = !busy;
        ChatGptRefreshButton.IsEnabled = ChatGptLogoutButton.IsEnabled = !busy && signedIn;
        ChatGptTestButton.IsEnabled = ChatGptSaveUseButton.IsEnabled = !busy && signedIn && ChatGptModelCombo.SelectedItem is CliModel;
        ChatGptDisconnectButton.IsEnabled = !busy && _directProfileId is not null;
        if (OmpImportButton is not null)
        {
            OmpImportPanel.Visibility = _cliKind == EngineExecutionKind.OmpRpc && _cliConnection is not null &&
                CliModelCombo.SelectedItem is CliModel importModel && OmpApiKeyImporter.Supports(importModel.Provider) ? Visibility.Visible : Visibility.Collapsed;
            OmpImportButton.IsEnabled = !busy && _cliKind == EngineExecutionKind.OmpRpc && _cliConnection is not null &&
                CliModelCombo.SelectedItem is CliModel model && OmpApiKeyImporter.Supports(model.Provider) && OmpImportConsent.IsChecked == true;
        }
    }
    private void ChatGptAccount_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAccounts || ChatGptStatus is null) return;
        CancelCliOperation(); _directProfileId = null; _directTestIdentity = null; ChatGptModelCombo.ItemsSource = null;
        ChatGptStatus.Text = "选择账号后刷新模型；已退出的账号可以重新官方登录。"; UpdateCliButtons();
    }
    private void ChatGptModel_Changed(object sender, SelectionChangedEventArgs e)
    { _directTestIdentity = null; _directTestedAt = null; UpdateChatGptButtons(); }
    private string DirectIdentity() => $"{(ChatGptAccountCombo.SelectedItem as ChatGptAccount)?.Id}|{(ChatGptModelCombo.SelectedItem as CliModel)?.Id}";
    private ProviderProfile CaptureDirectProfile()
    {
        if (ChatGptAccountCombo.SelectedItem is not ChatGptAccount account || ChatGptModelCombo.SelectedItem is not CliModel model)
            throw new InvalidOperationException("请先登录并选择 ChatGPT 模型。");
        return new ProviderProfile { Id = _directProfileId ?? "chatgpt-" + Guid.NewGuid().ToString("N"),
            Name = $"ChatGPT · {account.DisplayName} · {model.DisplayName}", ExecutionKind = EngineExecutionKindNames.ChatGptDirect,
            DirectConnection = new() { AccountId = account.Id, ModelId = model.Id, Enabled = true }, TextModel = model.Id,
            ApiBaseUrl = "https://api.openai.com/v1", TextEndpoint = "/responses", CredentialTarget = "", SupportsVision = false, VisionModel = "" };
    }
    private async Task DirectOperation(Func<CancellationToken, Task<string>> work, string starting)
    {
        if (_cliOperation is not null) return;
        using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(6)); _cliOperation = operation;
        ChatGptStatus.Text = starting; UpdateCliButtons();
        try
        {
            var message = await work(operation.Token);
            if (ReferenceEquals(_cliOperation, operation)) ChatGptStatus.Text = message;
        }
        catch (OperationCanceledException)
        { if (ReferenceEquals(_cliOperation, operation)) ChatGptStatus.Text = "直连操作已取消或超时。"; }
        catch (Exception ex)
        { if (ReferenceEquals(_cliOperation, operation)) ChatGptStatus.Text = CliFailureText(ex); }
        finally { if (ReferenceEquals(_cliOperation, operation)) { _cliOperation = null; UpdateCliButtons(); } }
    }
    private async void ChatGptLogin_Click(object sender, RoutedEventArgs e)
    {
        var selected = ReferenceEquals(sender, ChatGptAddAccountButton) ? null : ChatGptAccountCombo.SelectedItem as ChatGptAccount;
        await DirectOperation(async ct =>
        {
            var account = await ChatGptOAuth.SignInAsync(selected, ct); ct.ThrowIfCancellationRequested();
            ReloadChatGptAccounts(account.Id); _directProfileId = null; ChatGptModelCombo.ItemsSource = null;
            using var backend = new ChatGptDirectBackend(new() { AccountId = account.Id, Enabled = true });
            var models = await backend.ListModelsAsync(ct); ct.ThrowIfCancellationRequested();
            ChatGptModelCombo.ItemsSource = models; ChatGptModelCombo.SelectedIndex = models.Count > 0 ? 0 : -1;
            return "官方登录已保存。选择模型并测试后，可保存并使用直连。";
        }, "正在打开官方登录页面。完成授权后请回到 PopGlot；可取消等待。");
    }
    private async void ChatGptRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (ChatGptAccountCombo.SelectedItem is not ChatGptAccount account) return;
        var selectedModel = (ChatGptModelCombo.SelectedItem as CliModel)?.Id;
        await DirectOperation(async ct =>
        {
            using var backend = new ChatGptDirectBackend(new() { AccountId = account.Id, Enabled = true });
            var models = await backend.ListModelsAsync(ct); ct.ThrowIfCancellationRequested();
            ChatGptModelCombo.ItemsSource = models;
            ChatGptModelCombo.SelectedItem = models.FirstOrDefault(m => m.Id == selectedModel) ?? models.FirstOrDefault();
            return models.Count > 0 ? "模型已刷新，选择后可测试翻译。" : "此账号没有返回可选择的文字模型。";
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
    private async void ChatGptSaveUse_Click(object sender, RoutedEventArgs e)
    {
        if (_cliOperation is not null) return;
        var profile = CaptureDirectProfile(); var editingId = _directProfileId;
        if (_directTestIdentity == DirectIdentity())
        { profile.LastTestOutcome = "ok"; profile.LastTestedAtUtc = _directTestedAt; profile.LastTestFingerprint = ProfileVerification.EvidenceFingerprint(profile, false); }
        await DirectOperation(async ct =>
        {
            await Task.Run(() => ProfileManager.SaveCliProfile(profile, editingId, true)); ct.ThrowIfCancellationRequested();
            _directProfileId = profile.Id; RefreshProfilesList(); EditorEmpty.Visibility = Visibility.Collapsed; ProfileChanged?.Invoke();
            return "已保存并切换为 ChatGPT 官方直连文字引擎。";
        }, "正在保存直连引擎…");
    }
    private async void ChatGptDisconnect_Click(object sender, RoutedEventArgs e)
    {
        var id = _directProfileId;
        await DirectOperation(async ct =>
        {
            if (id is not null) await Task.Run(() => ProfileManager.DisableCliProfile(id)); ct.ThrowIfCancellationRequested();
            _directProfileId = null; _directTestIdentity = null; ChatGptModelCombo.ItemsSource = null;
            RefreshProfilesList(); EditorEmpty.Visibility = Visibility.Collapsed; ProfileChanged?.Invoke();
            return "已断开此引擎。ChatGPT 登录保留，可重新选择模型保存。";
        }, "正在断开直连引擎…");
    }
    private async void ChatGptLogout_Click(object sender, RoutedEventArgs e)
    {
        if (ChatGptAccountCombo.SelectedItem is not ChatGptAccount account) return;
        await DirectOperation(async ct =>
        {
            try { await ChatGptOAuth.SignOutAsync(account.Id, ct); }
            finally { if (!ct.IsCancellationRequested) { _directTestIdentity = null; ChatGptModelCombo.ItemsSource = null; ProfileChanged?.Invoke(); } }
            return "已退出此账号。账号与工作区注册保留，之后可重新授权。";
        }, "正在退出此账号并撤销官方会话…");
    }
    private void OpenDirectProfile(ProviderProfile profile)
    {
        OpenCliConnections_Click(this, new RoutedEventArgs()); ReloadChatGptAccounts(profile.DirectConnection?.AccountId);
        _directProfileId = profile.Id; _directTestIdentity = null;
        var modelId = profile.DirectConnection?.ModelId ?? profile.TextModel;
        var model = new CliModel(modelId, modelId); ChatGptModelCombo.ItemsSource = new[] { model }; ChatGptModelCombo.SelectedItem = model;
        ChatGptStatus.Text = "可刷新此账号模型、测试翻译或重新授权。"; UpdateCliButtons();
    }
    private void OmpImportConsent_Changed(object sender, RoutedEventArgs e) => UpdateChatGptButtons();
    private async void OmpImport_Click(object sender, RoutedEventArgs e)
    {
        if (_cliOperation is not null || CliModelCombo.SelectedItem is not CliModel model || !OmpApiKeyImporter.Supports(model.Provider)) return;
        var confirmed = OmpImportConsent.IsChecked == true;
        await DirectOperation(async ct =>
        {
            var profile = await Task.Run(() => OmpApiKeyImporter.Import(model.Provider!, model.Id, confirmed, ct: ct), ct);
            ct.ThrowIfCancellationRequested(); RefreshProfilesList(); EditorEmpty.Visibility = Visibility.Collapsed; ProfileChanged?.Invoke();
            OmpImportConsent.IsChecked = false;
            return $"已保存并切换到 {profile.Name}。尚未测试 API 直连，可在引擎列表测试。";
        }, "正在导入所选提供方的普通 API Key…");
    }
}
