using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using PopGlot.Windows.Services;
using PopGlot.Windows.Services.AiConnections;
using PopGlot.Windows.Sections;

namespace PopGlot.Windows.LogicTests;

internal static partial class Program
{
    private static void ManagedAndAccountDeletionFlows()
    {
        EnsureApplication();
        var originalConfig = ProfileManager.Load();
        var originalSettings = CoreBridge.GetSettings();
        var createdAccountIds = new List<string>();
        var suffix = Guid.NewGuid().ToString("N");
        var api = ProviderProfile.CreateOpenAi();
        api.Id = "delete-api-" + suffix;
        api.Name = "API delete fixture";
        api.ApiBaseUrl = "http://127.0.0.1:11434/v1";
        api.TextModel = "fixture";
        api.CredentialTarget = "";
        api.SupportsText = true;
        api.SupportsVision = false;
        api.VisionModel = "";
        ProfileManager.SaveApiProfile(api, null);

        var managedRow = NewManagedProfile("delete-managed-row-" + suffix);
        ProfileManager.SaveCliProfile(managedRow, null, true);
        var managedEditor = NewManagedProfile("delete-managed-editor-" + suffix);
        ProfileManager.SaveCliProfile(managedEditor, null, false);

        var section = new ServicesSection();
        var window = new Window { Content = section, Width = 1040, Height = 760, ShowInTaskbar = false };
        try
        {
            window.Show();
            FlushDispatcher(3);
            section.RefreshProfilesList();
            window.UpdateLayout();

            OpenProfile(section, managedEditor.Id);
            Equal(Visibility.Visible, section.EditorActionBar.Visibility,
                "editing a saved managed engine must show the shared delete/save footer");
            Equal(Visibility.Visible, section.EditorForm.Visibility, "the saved-engine editor must remain open");
            Equal(Visibility.Visible, section.DeleteServiceButton.Visibility, "the editor delete action must be enabled in the visual tree");
            True(section.EditorActionBar.IsVisible && section.DeleteServiceButton.IsVisible,
                "the editor delete action and footer must be actually visible in the hosted WPF surface");
            var tooltip = section.DeleteServiceButton.ToolTip?.ToString() ?? "";
            True(tooltip.Contains("删除引擎不会删除账号登录", StringComparison.Ordinal),
                "managed-engine delete guidance must explain that account login remains");
            True(!tooltip.Contains("API Key", StringComparison.Ordinal),
                "managed-engine delete guidance must not claim an API key is removed");
            section.ProfilesListBox.SelectedItem = Row(section, managedEditor.Id);
            section.DeleteServiceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal("确认删除？", section.DeleteServiceButton.Content,
                "the editor delete action must require a first confirmation click");
            True(ProfileManager.Load().Profiles.Any(profile => profile.Id == managedEditor.Id),
                "the first editor delete click must not delete the profile");
            section.DeleteServiceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SpinUntil(() => ProfileManager.Load().Profiles.All(profile => profile.Id != managedEditor.Id),
                "the second editor delete click must remove the managed profile");

            typeof(ServicesSection).GetMethod("ShowConnectionEditor", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.Invoke(section, [false, true]);
            Equal(Visibility.Collapsed, section.EditorActionBar.Visibility,
                "the shared footer must stay hidden while adding a managed engine");
            Equal(Visibility.Visible, section.CliActionRow.Visibility,
                "the add-mode managed actions must remain available");

            section.RefreshProfilesList();
            window.UpdateLayout();
            DeleteProfileRow(section, window, api.Id);
            True(ProfileManager.Load().Profiles.All(profile => profile.Id != api.Id),
                "the list-row delete action must remove an API profile");
            DeleteProfileRow(section, window, managedRow.Id);
            var afterDefaultDelete = ProfileManager.Load();
            True(afterDefaultDelete.Profiles.All(profile => profile.Id != managedRow.Id),
                "the list-row delete action must remove a managed profile");
            True(afterDefaultDelete.ActiveProfileId != managedRow.Id &&
                (afterDefaultDelete.ActiveProfileId.Length == 0 || afterDefaultDelete.Profiles.Any(profile => profile.Id == afterDefaultDelete.ActiveProfileId)),
                "deleting the default managed engine must leave a valid fallback route");

            var remaining = SaveAccount("remaining-" + suffix);
            createdAccountIds.Add(remaining.Id);
            var removed = SaveAccount("removed-" + suffix);
            createdAccountIds.Add(removed.Id);
            var direct = new ProviderProfile
            {
                Id = "delete-direct-" + suffix,
                Name = "Direct account fixture",
                ExecutionKind = EngineExecutionKindNames.Format(EngineExecutionKind.ChatGptDirect),
                DirectConnection = new DirectConnectionSettings { AccountId = removed.Id, ModelId = "fixture", Enabled = true },
                TextModel = "fixture",
                SupportsText = true,
                SupportsVision = false,
                CredentialTarget = "",
            };
            ProfileManager.SaveCliProfile(direct, null, false);
            ChatGptAccountStore.RemoveAccount(removed.Id);
            OpenProfile(section, direct.Id);
            True(section.ChatGptStatus.Text.Contains("账号已移除，请重新登录或选择其他账号", StringComparison.Ordinal),
                "a profile referencing a removed account must explain recovery");
            True(section.ChatGptAccountCombo.SelectedItem is null && section.ChatGptAccountCombo.Items.Count == 1 &&
                ((ChatGptAccount)section.ChatGptAccountCombo.Items[0]).Id == remaining.Id,
                "opening the profile must not silently select a different registered account");

            var dead = SaveAccount("dead-" + suffix);
            createdAccountIds.Add(dead.Id);
            ChatGptAccountStore.ClearCredentials(dead.Id);
            typeof(ServicesSection).GetMethod("ReloadChatGptAccounts", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.Invoke(section, [dead.Id]);
            True(section.ChatGptForgetAccountButton.IsEnabled,
                "the account-removal action must remain enabled for a registered account without credentials");
            section.ChatGptForgetAccountButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            True(ChatGptAccountStore.List().Any(account => account.Id == dead.Id),
                "the first account-removal click must only arm confirmation");
            section.ChatGptForgetAccountButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SpinUntil(() => ChatGptAccountStore.List().All(account => account.Id != dead.Id),
                "the confirmed account-removal action must delete a dead account");
            True(section.ChatGptStatus.Text.Contains("已删除此账号", StringComparison.Ordinal),
                "dead-account removal must report its local result");

            True(FindVisualChildren<TextBlock>(section).Any(text =>
                    text.Text == "OMP 账号由 oh-my-pi 管理；如需注销请在 OMP 中操作后刷新账号。"),
                "OMP login instructions must state that logout is managed by OMP");
        }
        finally
        {
            window.Close();
            FlushDispatcher(2);
            foreach (var accountId in createdAccountIds) ChatGptAccountStore.RemoveAccount(accountId);
            ProfileManager.SaveAsync(originalConfig).GetAwaiter().GetResult();
            CoreBridge.SaveSettings(originalSettings);
        }
    }

    private static ProviderProfile NewManagedProfile(string id) => new()
    {
        Id = id,
        Name = "Managed fixture " + id,
        ExecutionKind = EngineExecutionKindNames.Format(EngineExecutionKind.OmpRpc),
        CliConnection = new CliConnectionSettings
        {
            ExecutablePath = Environment.ProcessPath!,
            ExpectedVersion = CliLauncher.ReferenceToolVersion(EngineExecutionKind.OmpRpc),
            ModelId = "fixture",
            UpstreamProviderId = "fixture-provider",
            Enabled = true,
        },
        TextModel = "fixture",
        SupportsText = true,
        SupportsVision = false,
        CredentialTarget = "",
    };

    private static ChatGptAccount SaveAccount(string subject)
    {
        var account = ChatGptAccountStore.SaveValidated("fixture-client", subject, subject,
            new ChatGptCredentials("access", "refresh", "id-token", "scope", DateTimeOffset.UtcNow.AddHours(1),
                DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N")), null);
        return account;
    }


    private static void ManagedEditorUnifiedLoginSurface()
    {
        EnsureApplication();
        var originalConfig = ProfileManager.Load();
        var originalSettings = CoreBridge.GetSettings();
        var createdAccounts = new List<string>();
        var section = new ServicesSection();
        var window = new Window { Content = section, Width = 1040, Height = 760, ShowInTaskbar = false };
        try
        {
            window.Show();
            FlushDispatcher(3);
            // 添加态：动态单按钮，无英文文案。
            typeof(ServicesSection).GetMethod("ChatGptSource_Click", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.Invoke(section, [section.ChatGptSourceButton, new RoutedEventArgs()]);
            FlushDispatcher(2);
            True(section.ChatGptLoginButton.Content as string == "登录 ChatGPT",
                $"zero-account state must say 登录 ChatGPT, got {section.ChatGptLoginButton.Content}");
            var loginCardButtons = FindVisualChildren<Button>(section.ChatGptConnectionPanel)
                .Select(button => button.Content as string ?? "").ToList();
            True(loginCardButtons.All(text => !text.Contains("Continue with", StringComparison.Ordinal)),
                "the unified login card must not carry the old English button copy");

            // 已登录账号 → 添加账号；死账号 → 重新授权。
            var live = SaveAccount("t4-live");
            createdAccounts.Add(live.Id);
            typeof(ServicesSection).GetMethod("ReloadChatGptAccounts", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.Invoke(section, [live.Id]);
            FlushDispatcher(2);
            True(section.ChatGptLoginButton.Content as string == "添加账号",
                $"a signed-in selection must offer 添加账号, got {section.ChatGptLoginButton.Content}");
            ChatGptAccountStore.ClearCredentials(live.Id);
            typeof(ServicesSection).GetMethod("ReloadChatGptAccounts", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.Invoke(section, [live.Id]);
            FlushDispatcher(2);
            True(section.ChatGptLoginButton.Content as string == "重新授权",
                $"a dead-account selection must offer 重新授权, got {section.ChatGptLoginButton.Content}");
            True(section.ChatGptAccountDetails.IsVisible,
                "the account-and-model card must stay visible even while accounts exist");

            // 断开按钮在连接账号卡内（不在账号与模型卡内）。
            var disconnectHost = section.ChatGptDisconnectButton;
            DependencyObject? node = disconnectHost;
            var crossedDetails = false;
            while (node is not null && !ReferenceEquals(node, section.ChatGptConnectionPanel))
            {
                if (ReferenceEquals(node, section.ChatGptAccountDetails)) crossedDetails = true;
                node = VisualTreeHelper.GetParent(node);
            }
            True(node is not null && !crossedDetails,
                "断开引擎 must live in the connection-account card, beside CliDisconnectButton's placement");

            // 编辑态：底栏保存可见，名称回显。
            var credentialed = SaveAccount("t4-credentialed");
            createdAccounts.Add(credentialed.Id);
            var direct = new ProviderProfile
            {
                Id = "t4-direct-" + Guid.NewGuid().ToString("N"),
                Name = "T4 direct fixture",
                ExecutionKind = EngineExecutionKindNames.ChatGptDirect,
                DirectConnection = new() { AccountId = credentialed.Id, ModelId = "fixture", Enabled = true },
                TextModel = "fixture", SupportsText = true, SupportsVision = false, CredentialTarget = "",
            };
            ProfileManager.SaveCliProfile(direct, null, false);
            typeof(ServicesSection).GetMethod("OpenDirectProfile", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.Invoke(section, [direct]);
            FlushDispatcher(2);
            True(section.EditorActionBar.IsVisible && section.SaveServiceButton.IsVisible,
                "editing a saved direct profile must show the shared footer save action");
            True(section.ConnectionNameTextBox.Text == direct.Name,
                "the display-name field must echo the saved managed profile name");

            // 托管列表状态带时间戳。
            var tested = NewManagedProfile("t4-stamp-" + Guid.NewGuid().ToString("N"));
            tested.LastTestOutcome = "ok";
            tested.LastTestedAtUtc = DateTime.UtcNow.AddMinutes(-3);
            tested.LastTestFingerprint = ProfileVerification.EvidenceFingerprint(tested, false);
            var describe = typeof(ServicesSection).GetMethod("DescribeProfileState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                binder: null, types: [typeof(ProviderProfile), typeof(bool), typeof(string)], modifiers: null)!;
            var (text, _) = ((string, StatusTone))describe.Invoke(null, [tested, false, null])!;
            True(text.StartsWith("上次测试成功 · ", StringComparison.Ordinal) && text.Length > "上次测试成功 · ".Length,
                $"managed list state must carry the MM-dd HH:mm timestamp, got {text}");
        }
        finally
        {
            window.Close();
            FlushDispatcher(2);
            foreach (var accountId in createdAccounts) ChatGptAccountStore.RemoveAccount(accountId);
            ProfileManager.SaveAsync(originalConfig).GetAwaiter().GetResult();
            CoreBridge.SaveSettings(originalSettings);
        }
    }

    private static void ManagedProfileRowsShowAccountOwnership()
    {
        EnsureApplication();
        var originalConfig = ProfileManager.Load();
        var originalSettings = CoreBridge.GetSettings();
        var createdAccounts = new List<string>();
        var section = new ServicesSection();
        var window = new Window { Content = section, Width = 1040, Height = 760, ShowInTaskbar = false };
        try
        {
            window.Show();
            FlushDispatcher(3);
            var first = SaveAccount("t10-first-label");
            var second = SaveAccount("t10-second-label");
            createdAccounts.Add(first.Id);
            createdAccounts.Add(second.Id);
            var profileA = new ProviderProfile
            {
                Id = "t10-direct-a-" + Guid.NewGuid().ToString("N"),
                Name = "T10 direct A",
                ExecutionKind = EngineExecutionKindNames.ChatGptDirect,
                DirectConnection = new() { AccountId = first.Id, ModelId = "fixture", Enabled = true },
                TextModel = "fixture", SupportsText = true, SupportsVision = false, CredentialTarget = "",
            };
            var profileB = new ProviderProfile
            {
                Id = "t10-direct-b-" + Guid.NewGuid().ToString("N"),
                Name = "T10 direct B",
                ExecutionKind = EngineExecutionKindNames.ChatGptDirect,
                DirectConnection = new() { AccountId = second.Id, ModelId = "fixture", Enabled = true },
                TextModel = "fixture", SupportsText = true, SupportsVision = false, CredentialTarget = "",
            };
            ProfileManager.SaveCliProfile(profileA, null, false);
            ProfileManager.SaveCliProfile(profileB, null, false);
            section.RefreshProfilesList();
            FlushDispatcher(2);
            var summaryA = Row(section, profileA.Id).Summary;
            var summaryB = Row(section, profileB.Id).Summary;
            True(summaryA.Contains("t10-first-label", StringComparison.Ordinal) &&
                summaryB.Contains("t10-second-label", StringComparison.Ordinal),
                $"the two account-backed rows must be distinguishable, got: {summaryA} | {summaryB}");
            True(summaryA != summaryB, "different accounts must produce different summaries");
        }
        finally
        {
            window.Close();
            FlushDispatcher(2);
            foreach (var accountId in createdAccounts) ChatGptAccountStore.RemoveAccount(accountId);
            ProfileManager.SaveAsync(originalConfig).GetAwaiter().GetResult();
            CoreBridge.SaveSettings(originalSettings);
        }
    }
    private static void OpenProfile(ServicesSection section, string profileId) =>
        typeof(ServicesSection).GetMethod("OpenProfileInEditor", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!.Invoke(section, [profileId]);

    private static ProfilesRow Row(ServicesSection section, string id) =>
        section.ProfilesListBox.Items.Cast<ProfilesRow>().Single(row => row.Id == id);

    private static void DeleteProfileRow(ServicesSection section, Window window, string profileId)
    {
        FlushDispatcher(3);
        window.UpdateLayout();
        var button = FindVisualChildren<Button>(section).FirstOrDefault(candidate =>
            candidate.Tag?.ToString() == profileId && AutomationProperties.GetName(candidate) == "删除引擎");
        True(button is not null, "the profile row must expose its delete button");
        button!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        True(ProfileManager.Load().Profiles.Any(profile => profile.Id == profileId),
            "the first list-row delete click must only arm confirmation");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        SpinUntil(() => ProfileManager.Load().Profiles.All(profile => profile.Id != profileId),
            "the second list-row delete click must remove the profile");
        FlushDispatcher(3);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
}
