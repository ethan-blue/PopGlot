using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PopGlot.Windows.Sections;

/// <summary>One row in the template lists, shaped for display.</summary>
internal sealed record TemplateRow(
    string Id,
    string DisplayName,
    string DisplayDescription,
    string Meta,
    Visibility ActiveBadge,
    Visibility ViewButtonVisibility,
    Visibility ActivateButtonVisibility,
    Visibility ActionsVisibility,
    Visibility ConfirmDeleteVisibility,
    string DeleteConfirmText,
    bool IsActive);

/// <summary>
/// Normalized snapshot of the prompt editor's five fields. The editor is dirty
/// exactly when the live snapshot differs from the baseline — never because a
/// flag latched on — so editing a value back to its saved form clears the
/// badge on its own.
/// </summary>
internal sealed record PromptEditorSnapshot(
    string Name,
    string Description,
    string Instruction,
    string Domain,
    string Audience,
    bool Enabled)
{
    public static PromptEditorSnapshot Capture(
        string? name, string? description, string? instruction, string? domain, string? audience,
        bool enabled = true) =>
        new(name ?? string.Empty,
            description ?? string.Empty,
            instruction ?? string.Empty,
            domain ?? string.Empty,
            audience ?? string.Empty,
            enabled);

    public string Serialize() => string.Join('\u001f',
        Name, Description, Instruction, Domain, Audience, Enabled ? "1" : "0");
}

/// <summary>
/// Prompt template management page (0.1.6): built-in faithful/natural/formal
/// are read-only and copyable as drafts; custom templates get full CRUD
/// against the CoreBridge prompt APIs with the Rust-enforced 50-item quota.
/// The compile preview runs <see cref="CoreBridge.CompilePromptPreview"/> — a
/// pure local function with zero network — and saving/deleting/activating all
/// go through the bridge's serialized prompt queue. All long operations stay
/// asynchronous: the UI thread only initiates.
/// </summary>
public partial class PromptSection : System.Windows.Controls.UserControl
{
    // Mirror of the Rust domain quotas (crates/popglot-domain/src/prompt.rs).
    private const int MaxCustomTemplates = 50;
    private const int MaxNameChars = 64;
    private const int MaxDescriptionChars = 256;
    private const int MaxInstructionBytes = 8 * 1024;
    private const int MaxDomainChars = 256;
    private const int MaxDomainBytes = 1024;
    private const int MaxAudienceChars = 256;
    private const int MaxAudienceBytes = 1024;

    private const string FaithfulId = "faithful";

    private IReadOnlyList<PromptTemplateDto> _templates = [];
    private string _activeId = FaithfulId;
    private bool _loading;
    private bool _saving;
    private bool _isAdding;
    private bool _viewingBuiltin;
    private bool _templatesLoading;
    private bool _loadFailed;
    private string? _editingId;
    private bool _editorDirty;
    private PromptEditorSnapshot _editorSaved = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, true);
    private Action? _pendingAfterDraft;
    /// <summary>正在等待行内删除确认的规则 Id；null 表示没有确认中的删除。
    /// 确认区无 3 秒限时，靠显式「取消」/Esc/切换上下文解除。</summary>
    private string? _confirmingDeleteId;
    /// <summary>重绘后要聚焦的确认按钮所属规则 Id（一次性）。</summary>
    private string? _focusConfirmId;
    /// <summary>取消确认后要把焦点还回去的行删除按钮所属规则 Id（一次性）。</summary>
    private string? _focusRowDeleteId;
    private bool? _compact;
    private readonly DispatcherTimer _previewDebounce;

    /// <summary>Raised when the section needs to show a status message.</summary>
    internal event Action<string, StatusTone>? StatusChanged;

    /// <summary>Raised when the editor opens or closes so the window can hide its own save bar.</summary>
    internal event Action? EditorOpenStateChanged;

    /// <summary>True while the editor holds unsaved changes.</summary>
    internal bool IsEditorDirty => _editorDirty;

    /// <summary>True while the focused editor form is visible.</summary>
    internal bool IsEditorOpen => EditorForm?.Visibility == Visibility.Visible;

    public PromptSection()
    {
        InitializeComponent();
        // 预览编译是本地纯函数，但逐键调用 FFI 仍然浪费；合并为停顿后的一次。
        _previewDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _previewDebounce.Tick += (_, _) =>
        {
            _previewDebounce.Stop();
            RefreshPreview();
        };
        _ = LoadTemplatesAsync();
    }

    /// <summary>Re-reads templates from the core; safe to call on every page show.</summary>
    internal void NotifyShown() => _ = LoadTemplatesAsync();

    // ===================== Loading =====================

    private async Task LoadTemplatesAsync()
    {
        if (_templatesLoading)
        {
            return;
        }
        _templatesLoading = true;
        RetryLoadButton.IsEnabled = false;
        try
        {
            IReadOnlyList<PromptTemplateDto> templates;
            PromptTemplateDto active;
            try
            {
                (templates, active) = await Task.Run(() =>
                    (CoreBridge.ListPromptTemplates(), CoreBridge.GetActivePromptTemplate()));
            }
            catch (Exception exception)
            {
                // 读取失败时列表内容不可信：以内联错误态取代正常空态，
                // 绝不能让「还没有自定义模板」在坏数据上谎报。
                ShowLoadError(exception.Message);
                return;
            }
            _templates = templates;
            _activeId = string.IsNullOrWhiteSpace(active.Id) ? FaithfulId : active.Id;
            _loadFailed = false;
            // 重读列表意味着页面重新进入：未完成的删除确认不跨上下文残留。
            ClearDeleteConfirm();
            ApplyListErrorState();
            PaintLists();
        }
        finally
        {
            _templatesLoading = false;
            RetryLoadButton.IsEnabled = true;
        }
    }

    private void RetryLoad_Click(object sender, RoutedEventArgs e) => _ = LoadTemplatesAsync();

    private void ShowLoadError(string message)
    {
        _loadFailed = true;
        LoadErrorText.Text = $"无法从本机核心读取模板：{message}";
        ApplyListErrorState();
        StatusChanged?.Invoke($"读取提示词模板失败：{message}", StatusTone.Error);
    }

    /// <summary>
    /// 单一事实源：读取失败 → 错误面板可见、列表隐藏（编辑器打开时只藏
    /// 列表，错误面板不压到编辑器上）；成功或编辑器打开 → 面板收起。
    /// </summary>
    private void ApplyListErrorState()
    {
        var editorOpen = EditorForm?.Visibility == Visibility.Visible;
        LoadErrorPanel.Visibility = _loadFailed && !editorOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        ListHost.Visibility = _loadFailed || editorOpen
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void PaintLists()
    {
        var builtins = _templates.Where(t => t.IsBuiltIn).ToList();
        var customs = _templates.Where(t => !t.IsBuiltIn).ToList();

        BuiltinsList.ItemsSource = builtins.Select(t => RowFor(t, isBuiltIn: true)).ToList();
        CustomsList.ItemsSource = customs.Select(t => RowFor(t, isBuiltIn: false)).ToList();

        var quotaFull = customs.Count >= MaxCustomTemplates;
        CustomQuotaText.Text = customs.Count == 0 ? string.Empty : $"{customs.Count} 条";
        AddTemplateButton.IsEnabled = !quotaFull;
        AddTemplateButton.ToolTip = quotaFull
            ? $"已达上限（{MaxCustomTemplates} 个），请先删除"
            : "创建一个自定义提示词模板";
        CustomsEmptyPanel.Visibility = customs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CustomsList.Visibility = customs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var active = _templates.FirstOrDefault(t => string.Equals(t.Id, _activeId, StringComparison.Ordinal))
            ?? _templates.FirstOrDefault(t => string.Equals(t.Id, FaithfulId, StringComparison.Ordinal));
        if (active is not null)
        {
            ActiveKindText.Text = active.IsBuiltIn ? "内置" : "自定义";
            ActiveTemplateName.Text = active.Name;
            ActiveTemplateDescription.Text = string.IsNullOrWhiteSpace(active.Description)
                ? "（无说明）"
                : active.Description;
            ActiveTemplateMeta.Text = $"{(active.IsBuiltIn ? "内置模板" : "自定义模板")} · 修订 {active.Revision}";
        }
    }

    private TemplateRow RowFor(PromptTemplateDto template, bool isBuiltIn)
    {
        var isActive = string.Equals(template.Id, _activeId, StringComparison.Ordinal);
        // 停用是模板自身的开关：停用的模板即使被设为当前风格也不会被使用
        // （Rust 回退到内置 faithful），所以列表里必须如实标出。
        var meta = $"{(isBuiltIn ? "内置" : "自定义")} · 修订 {template.Revision}"
            + (template.Enabled ? string.Empty : " · 已停用");
        var confirming = string.Equals(template.Id, _confirmingDeleteId, StringComparison.Ordinal);
        return new TemplateRow(
            template.Id,
            template.Name,
            string.IsNullOrWhiteSpace(template.Description) ? "（无说明）" : template.Description,
            meta,
            isActive ? Visibility.Visible : Visibility.Collapsed,
            isBuiltIn ? Visibility.Visible : Visibility.Collapsed,
            isActive ? Visibility.Collapsed : Visibility.Visible,
            confirming ? Visibility.Collapsed : Visibility.Visible,
            confirming ? Visibility.Visible : Visibility.Collapsed,
            $"删除「{template.Name}」？模板的历史修订版本会一并删除，此操作无法撤销。",
            isActive);
    }

    // ===================== Editor open/close =====================

    private void OpenEditor(PromptTemplateDto template, bool adding, bool viewingBuiltin)
    {
        ClearDeleteConfirm();
        HideDraftGuard();
        _editingId = template.Id;
        _isAdding = adding;
        _viewingBuiltin = viewingBuiltin;

        _loading = true;
        try
        {
            NameTextBox.Text = template.Name;
            DescriptionTextBox.Text = template.Description;
            InstructionTextBox.Text = template.Instruction;
            DomainTextBox.Text = template.Domain;
            AudienceTextBox.Text = template.Audience;
            EnabledToggle.IsChecked = template.Enabled;
        }
        finally
        {
            _loading = false;
        }
        _editorSaved = PromptEditorSnapshot.Capture(
            template.Name, template.Description, template.Instruction, template.Domain, template.Audience,
            template.Enabled);

        EditorTitleText.Text = adding ? "添加翻译规则" : template.Name;
        ReadOnlyBadge.Visibility = viewingBuiltin ? Visibility.Visible : Visibility.Collapsed;
        // 启用开关是自定义模板的产品能力：内置只读（不显示开关），与
        // 「设为当前翻译风格」是两回事——停用≠取消当前风格。
        EnabledPanel.Visibility = viewingBuiltin ? Visibility.Collapsed : Visibility.Visible;
        EnabledHintText.Text = template.Enabled
            ? "停用后不参与翻译"
            : "已停用，不参与翻译";
        EditorMetaText.Text = viewingBuiltin
            ? "内置规则，只能查看"
            : adding
                ? "保存后可以直接选择使用"
                : "自定义规则";

        ListHost.Visibility = Visibility.Collapsed;
        EditorForm.Visibility = Visibility.Visible;
        SaveTemplateButton.Visibility = viewingBuiltin ? Visibility.Collapsed : Visibility.Visible;
        CancelEditButton.Visibility = viewingBuiltin ? Visibility.Collapsed : Visibility.Visible;
        CopyBuiltinButton.Visibility = viewingBuiltin ? Visibility.Visible : Visibility.Collapsed;
        DeleteTemplateButton.Visibility = !viewingBuiltin && !adding ? Visibility.Visible : Visibility.Collapsed;
        // 编辑器盖住列表视图时，读取错误面板也一并收起，避免叠在编辑器上。
        ApplyListErrorState();

        ResetEditorDirty();
        UpdateCounters();
        UpdateInteractivity();
        RefreshPreview();
        EditorOpenStateChanged?.Invoke();
    }

    private void ShowList()
    {
        ClearDeleteConfirm();
        HideDraftGuard();
        _editorDirty = false;
        _editingId = null;
        _isAdding = false;
        _viewingBuiltin = false;
        EditorForm.Visibility = Visibility.Collapsed;
        // 回到列表视图时如实恢复上一次加载的状态：读取失败仍然显示内联
        // 错误，而不是把（可能空/陈旧的）列表当成功内容展示。
        ApplyListErrorState();
        PaintLists();
        EditorOpenStateChanged?.Invoke();
    }

    /// <summary>
    /// 关窗前的整理：编辑器打开且内容干净时直接收起（回到列表，恢复窗口侧
    /// 可见保存栏）；脏编辑器保持原样，仍由窗口层调用 BeginDraftGuard 解决。
    /// </summary>
    internal void CloseCleanEditor()
    {
        if (IsEditorOpen && !_editorDirty)
        {
            ShowList();
        }
    }

    // ===================== Row actions =====================

    private void ViewTemplate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id) return;
        var template = _templates.FirstOrDefault(t => t.Id == id);
        if (template is null) return;
        OpenEditor(template, adding: false, viewingBuiltin: true);
    }

    private void EditTemplate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id) return;
        var template = _templates.FirstOrDefault(t => t.Id == id);
        if (template is null) return;
        OpenEditor(template, adding: false, viewingBuiltin: false);
    }

    private void AddTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureQuotaForNew()) return;
        OpenEditor(new PromptTemplateDto(Id: NewTemplateId(), Name: string.Empty), adding: true, viewingBuiltin: false);
    }

    private void CopyTemplate_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as Button)?.Tag as string;
        CopyToCustomDraft(id);
    }

    private void CopyBuiltinEditor_Click(object sender, RoutedEventArgs e) => CopyToCustomDraft(_editingId);

    private void CopyToCustomDraft(string? sourceId)
    {
        var source = _templates.FirstOrDefault(t => t.Id == sourceId);
        if (source is null) return;
        if (!EnsureQuotaForNew()) return;
        var draft = new PromptTemplateDto(
            Id: NewTemplateId(),
            Name: $"{source.Name} 副本",
            Description: source.Description,
            Instruction: source.Instruction,
            Domain: source.Domain,
            Audience: source.Audience,
            Enabled: true,
            IsBuiltIn: false);
        OpenEditor(draft, adding: true, viewingBuiltin: false);
        StatusChanged?.Invoke($"已复制为自定义模板「{source.Name} 副本」，调整后保存", StatusTone.Info);
    }

    /// <summary>The quota is enforced by Rust; the entry point checks it early
    /// so a doomed draft is never opened in the first place.</summary>
    private bool EnsureQuotaForNew()
    {
        if (_templates.Count(t => !t.IsBuiltIn) < MaxCustomTemplates)
        {
            return true;
        }
        StatusChanged?.Invoke(
            $"自定义模板数量已达上限（{MaxCustomTemplates} 个），无法继续新增；请删除不需要的模板。",
            StatusTone.Warning);
        return false;
    }

    /// <summary>Rust-owned IDs must be ASCII letters/digits/-/_ (prompt.rs validate).</summary>
    private static string NewTemplateId() => $"custom-{Guid.NewGuid():N}";

    /// <summary>自定义规则行的「使用这条规则」按钮。</summary>
    private async void ActivateTemplate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id) return;
        await ActivateTemplateByIdAsync(id);
    }

    /// <summary>
    /// 内置语气单选项。绑定回填（PaintLists 重建 ItemsSource 时 OneWay 推送
    /// IsChecked）也会走到这里：与 _activeId 相同的回填直接忽略，只有用户
    /// 的新选择才发起激活，避免数据回填被当成操作。
    /// </summary>
    private async void BuiltinChoice_Checked(object sender, RoutedEventArgs e)
    {
        if ((sender as RadioButton)?.Tag is not string id || _saving) return;
        if (string.Equals(id, _activeId, StringComparison.Ordinal)) return;
        await ActivateTemplateByIdAsync(id);
    }

    private async Task ActivateTemplateByIdAsync(string id)
    {
        var template = _templates.FirstOrDefault(t => t.Id == id);
        if (template is null) return;
        _saving = true;
        UpdateInteractivity();
        try
        {
            await CoreBridge.SetActivePromptTemplateAsync(id);
            // 激活写的是「当前风格指向」；停用的模板激活后 Rust 仍回退内置
            // faithful，必须重读对齐，卡片才不会说谎。
            _activeId = CoreBridge.GetActivePromptTemplate().Id;
            ClearDeleteConfirm();
            PaintLists();
            StatusChanged?.Invoke(
                !template.Enabled
                    ? $"已选择「{template.Name}」，但它当前未启用，翻译会使用「准确」"
                    : $"已选择：{template.Name}",
                !template.Enabled ? StatusTone.Warning : StatusTone.Success);
        }
        catch (Exception exception)
        {
            // 激活失败：重建列表让单选项回到数据真值，不保留虚假的选中外观。
            PaintLists();
            StatusChanged?.Invoke($"切换翻译风格失败：{exception.Message}", StatusTone.Error);
        }
        finally
        {
            _saving = false;
            UpdateInteractivity();
        }
    }

    // ===================== Delete (inline confirm) =====================

    /// <summary>
    /// 行删除第一步：不武装按钮、不倒计时。行内展开确认区，焦点转到
    /// 确认区的「删除」；按钮位置与宽度全程不变。
    /// </summary>
    private void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _saving) return;
        var id = button.Tag as string;
        if (string.IsNullOrEmpty(id) || _templates.All(t => t.Id != id)) return;
        _confirmingDeleteId = id;
        _focusConfirmId = id;
        _focusRowDeleteId = null;
        PaintLists();
    }

    private void CancelDeleteConfirm_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id) return;
        if (!string.Equals(_confirmingDeleteId, id, StringComparison.Ordinal)) return;
        _confirmingDeleteId = null;
        _focusConfirmId = null;
        _focusRowDeleteId = id;
        PaintLists();
    }

    private async void DeleteConfirmed_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id) return;
        await DeleteTemplateByIdAsync(id);
    }

    /// <summary>模板被重绘进树后，把焦点交到刚展开的确认区删除按钮。</summary>
    private void DeleteConfirmButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button &&
            string.Equals(button.Tag as string, _focusConfirmId, StringComparison.Ordinal))
        {
            _focusConfirmId = null;
            button.Focus();
        }
    }

    /// <summary>取消确认重绘后，把焦点还给那一行的删除按钮。</summary>
    private void RowDeleteButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button &&
            string.Equals(button.Tag as string, _focusRowDeleteId, StringComparison.Ordinal))
        {
            _focusRowDeleteId = null;
            button.Focus();
        }
    }

    /// <summary>编辑器操作栏的删除按钮：同一条确认契约，无 3 秒限时。</summary>
    private void EditorDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || string.IsNullOrEmpty(_editingId)) return;
        if (_templates.All(t => t.Id != _editingId)) return;
        var name = _templates.FirstOrDefault(t => t.Id == _editingId)?.Name ?? NameTextBox.Text.Trim();
        EditorDeleteConfirmText.Text = $"删除「{name}」？模板的历史修订版本会一并删除，此操作无法撤销。";
        EditorDeleteConfirmPanel.Visibility = Visibility.Visible;
        DeleteTemplateButton.Visibility = Visibility.Collapsed;
        EditorDeleteConfirmButton.Focus();
    }

    private void EditorDeleteConfirmCancel_Click(object sender, RoutedEventArgs e) => HideEditorDeleteConfirm(focusBack: true);

    private async void EditorDeleteConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || string.IsNullOrEmpty(_editingId)) return;
        await DeleteTemplateByIdAsync(_editingId);
    }

    private void HideEditorDeleteConfirm(bool focusBack = false)
    {
        EditorDeleteConfirmPanel.Visibility = Visibility.Collapsed;
        // 只有编辑器仍处于「该按钮本应可见」的形态时才恢复它，避免关闭
        // 编辑器后的残留显示。
        if (IsEditorOpen && !_viewingBuiltin && !_isAdding)
        {
            DeleteTemplateButton.Visibility = Visibility.Visible;
            if (focusBack)
            {
                DeleteTemplateButton.Focus();
            }
        }
    }

    /// <summary>删除确认的统一解除：行内确认与编辑器确认一并收起。</summary>
    private void ClearDeleteConfirm()
    {
        _confirmingDeleteId = null;
        _focusConfirmId = null;
        _focusRowDeleteId = null;
        HideEditorDeleteConfirm();
    }

    /// <summary>Esc：只服务删除确认的显式取消，不抢其他处理过的键。</summary>
    private void PromptSection_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Escape)
        {
            return;
        }
        if (EditorDeleteConfirmPanel.Visibility == Visibility.Visible)
        {
            HideEditorDeleteConfirm(focusBack: true);
            e.Handled = true;
            return;
        }
        if (_confirmingDeleteId is not null)
        {
            var id = _confirmingDeleteId;
            _confirmingDeleteId = null;
            _focusRowDeleteId = id;
            PaintLists();
            e.Handled = true;
        }
    }

    private async Task DeleteTemplateByIdAsync(string id)
    {
        if (_saving) return;
        _saving = true;
        UpdateInteractivity();
        try
        {
            await CoreBridge.DeletePromptTemplateAsync(id);
            var wasActive = string.Equals(_activeId, id, StringComparison.Ordinal);
            _templates = _templates.Where(t => t.Id != id).ToList();
            if (wasActive)
            {
                // Rust resets the active template to built-in faithful on delete;
                // re-read so the card never lies about what will be used.
                _activeId = CoreBridge.GetActivePromptTemplate().Id;
            }
            ClearDeleteConfirm();
            var deletedWasEditing = string.Equals(_editingId, id, StringComparison.Ordinal);
            if (deletedWasEditing && EditorForm.Visibility == Visibility.Visible)
            {
                ShowList();
                AddTemplateButton.Focus();
            }
            else
            {
                PaintLists();
                AddTemplateButton.Focus();
            }
            StatusChanged?.Invoke(
                wasActive
                    ? "已删除，翻译方式已切换为「准确」"
                    : "已删除模板",
                StatusTone.Success);
        }
        catch (Exception exception)
        {
            // 失败保留确认区，用户可直接重试或显式取消；状态条说明原因。
            StatusChanged?.Invoke($"删除失败：{exception.Message}", StatusTone.Error);
        }
        finally
        {
            _saving = false;
            UpdateInteractivity();
        }
    }

    // ===================== Save =====================

    private async void SaveTemplate_Click(object sender, RoutedEventArgs e) => await TrySaveAsync();

    /// <summary>
    /// Validates locally (same budgets as Rust), then persists through the
    /// bridge's serialized prompt queue and adopts the authoritative result
    /// Rust returns (revision, timestamps). A failure keeps the draft fully
    /// editable for a retry.
    /// </summary>
    private async Task<bool> TrySaveAsync()
    {
        if (_viewingBuiltin || _saving) return false;
        var name = NameTextBox.Text.Trim();
        var draft = new PromptTemplateDto(
            Id: _editingId ?? NewTemplateId(),
            Name: name,
            Description: DescriptionTextBox.Text.Trim(),
            Instruction: InstructionTextBox.Text,
            Domain: DomainTextBox.Text,
            Audience: AudienceTextBox.Text,
            // 启用状态来自编辑器开关，绝不无条件写回 true——否则停用操作
            // 永远无法持久化（Rust store 依赖 enabled 做激活回退）。
            Enabled: EnabledToggle.IsChecked == true,
            IsBuiltIn: false);

        var validationError = ValidateDraft(name, draft.Description, draft.Instruction, draft.Domain, draft.Audience);
        if (validationError is not null)
        {
            StatusChanged?.Invoke($"保存失败：{validationError}", StatusTone.Error);
            return false;
        }

        _saving = true;
        UpdateInteractivity();
        try
        {
            var saved = await CoreBridge.SavePromptTemplateAsync(draft);
            var customs = _templates.Where(t => !t.IsBuiltIn).ToList();
            var position = customs.FindIndex(t => t.Id == saved.Id);
            if (position >= 0)
            {
                customs[position] = saved;
            }
            else
            {
                customs.Add(saved);
            }
            _templates = _templates.Where(t => t.IsBuiltIn).Concat(customs).ToList();
            if (!saved.Enabled && string.Equals(_activeId, saved.Id, StringComparison.Ordinal))
            {
                try
                {
                    // 停用的模板即使指向当前风格，Rust 也回退内置 faithful；
                    // 重读对齐，当前风格卡与列表徽标才不会说谎。
                    _activeId = CoreBridge.GetActivePromptTemplate().Id;
                }
                catch
                {
                    // 对齐读取失败时保持旧值；下一次列表刷新会再次对齐。
                }
            }
            OpenEditor(saved, adding: false, viewingBuiltin: false);
            StatusChanged?.Invoke(
                saved.Enabled
                    ? $"已保存「{saved.Name}」（修订 {saved.Revision}）"
                    : $"已保存「{saved.Name}」（修订 {saved.Revision}）；停用中，不参与翻译",
                StatusTone.Success);
            return true;
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"保存失败：{exception.Message}（草稿已保留，可修改后重试）", StatusTone.Error);
            return false;
        }
        finally
        {
            _saving = false;
            UpdateInteractivity();
        }
    }

    /// <summary>Client-side mirror of prompt.rs validate; both sides guard,
    /// and the Rust side remains authoritative on save.</summary>
    internal static string? ValidateDraft(
        string name, string description, string instruction, string domain, string audience)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "模板名称不能为空。";
        }
        if (name.Length > MaxNameChars)
        {
            return $"模板名称不能超过 {MaxNameChars} 个字符（当前 {name.Length} 个）。";
        }
        if (description.Length > MaxDescriptionChars)
        {
            return $"模板说明不能超过 {MaxDescriptionChars} 个字符（当前 {description.Length} 个）。";
        }
        var instructionBytes = Encoding.UTF8.GetByteCount(instruction);
        if (instructionBytes > MaxInstructionBytes)
        {
            return $"模板正文超过 {MaxInstructionBytes} 字节上限（当前 {instructionBytes} 字节）。请精简后再保存。";
        }
        var domainBytes = Encoding.UTF8.GetByteCount(domain);
        if (domain.Length > MaxDomainChars || domainBytes > MaxDomainBytes)
        {
            return $"领域超过上限（字符 {domain.Length}/{MaxDomainChars}，字节 {domainBytes}/{MaxDomainBytes}）。";
        }
        var audienceBytes = Encoding.UTF8.GetByteCount(audience);
        if (audience.Length > MaxAudienceChars || audienceBytes > MaxAudienceBytes)
        {
            return $"受众超过上限（字符 {audience.Length}/{MaxAudienceChars}，字节 {audienceBytes}/{MaxAudienceBytes}）。";
        }
        return null;
    }

    // ===================== Dirty tracking =====================

    private void EditorField_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateCounters();
        if (_loading)
        {
            return;
        }
        MarkEditorDirty();
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    /// <summary>启用开关也是草稿的一部分：改动即可脏，改回原值自动回到干净。</summary>
    private void EnabledToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        EnabledHintText.Text = EnabledToggle.IsChecked == true
            ? "停用后不参与翻译"
            : "已停用，不参与翻译";
        MarkEditorDirty();
    }

    private PromptEditorSnapshot CaptureEditorDraft() => PromptEditorSnapshot.Capture(
        NameTextBox.Text, DescriptionTextBox.Text, InstructionTextBox.Text, DomainTextBox.Text, AudienceTextBox.Text,
        EnabledToggle.IsChecked == true);

    private void MarkEditorDirty()
    {
        if (_viewingBuiltin || _saving) return;
        _editorDirty = !string.Equals(
            CaptureEditorDraft().Serialize(), _editorSaved.Serialize(), StringComparison.Ordinal);
        UpdateEditorDirtyBadge();
    }

    private void ResetEditorDirty()
    {
        _editorSaved = CaptureEditorDraft();
        _editorDirty = false;
        UpdateEditorDirtyBadge();
    }

    internal void ClearEditorDirty() => ResetEditorDirty();

    private void UpdateEditorDirtyBadge() =>
        EditorDirtyBadge.Visibility = _editorDirty ? Visibility.Visible : Visibility.Collapsed;

    // ===================== Draft guard =====================

    /// <summary>
    /// Resolves an unsaved draft INLINE before a pending action proceeds —
    /// returning to the list, switching settings pages, or closing the window.
    /// No system dialog; the bar offers save/discard/cancel.
    /// </summary>
    internal void BeginDraftGuard(string message, Action? proceed = null)
    {
        if (!_editorDirty)
        {
            proceed?.Invoke();
            return;
        }
        _pendingAfterDraft = proceed;
        DraftGuardText.Text = message;
        DraftGuardBar.Visibility = Visibility.Visible;
        EditorActionBar.Visibility = Visibility.Collapsed;
    }

    private void HideDraftGuard()
    {
        DraftGuardBar.Visibility = Visibility.Collapsed;
        if (EditorForm.Visibility == Visibility.Visible)
        {
            EditorActionBar.Visibility = Visibility.Visible;
        }
        _pendingAfterDraft = null;
    }

    private async void DraftSave_Click(object sender, RoutedEventArgs e)
    {
        // Capture the pending navigation BEFORE saving: a successful save
        // reopens the editor, which clears the guard bar (and with it the
        // pending action) as part of its own reset.
        var proceed = _pendingAfterDraft;
        if (await TrySaveAsync())
        {
            HideDraftGuard();
            proceed?.Invoke();
        }
        // Save failed: keep the bar so the user can retry or discard.
    }

    private void DraftDiscard_Click(object sender, RoutedEventArgs e)
    {
        var proceed = _pendingAfterDraft;
        _loading = true;
        try
        {
            NameTextBox.Text = _editorSaved.Name;
            DescriptionTextBox.Text = _editorSaved.Description;
            InstructionTextBox.Text = _editorSaved.Instruction;
            DomainTextBox.Text = _editorSaved.Domain;
            AudienceTextBox.Text = _editorSaved.Audience;
            EnabledToggle.IsChecked = _editorSaved.Enabled;
            EnabledHintText.Text = _editorSaved.Enabled
                ? "停用后不参与翻译"
                : "已停用，不参与翻译";
        }
        finally
        {
            _loading = false;
        }
        ResetEditorDirty();
        UpdateCounters();
        RefreshPreview();
        HideDraftGuard();
        proceed?.Invoke();
    }

    private void DraftCancel_Click(object sender, RoutedEventArgs e) => HideDraftGuard();

    // ===================== Navigation =====================

    private void BackToList_Click(object sender, RoutedEventArgs e)
    {
        if (_editorDirty)
        {
            BeginDraftGuard(
                "有未保存修改，请先保存或放弃。",
                ShowList);
            return;
        }
        ShowList();
    }

    private void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        // An explicit cancel IS the discard decision; no extra confirmation.
        ShowList();
    }

    // ===================== Field helpers =====================

    private void VariableChip_Click(object sender, RoutedEventArgs e)
    {
        if (_viewingBuiltin || _saving) return;
        if ((sender as Button)?.Content is not string token || token.Length == 0) return;
        var start = InstructionTextBox.SelectionStart;
        var length = InstructionTextBox.SelectionLength;
        var text = InstructionTextBox.Text;
        text = text.Remove(start, length).Insert(start, token);
        InstructionTextBox.Text = text;
        InstructionTextBox.CaretIndex = start + token.Length;
        InstructionTextBox.Focus();
    }

    private void UpdateCounters()
    {
        var nameChars = NameTextBox.Text.Length;
        NameCounterText.Text = CounterText(nameChars, MaxNameChars);
        SetCounterTone(NameCounterText, nameChars, MaxNameChars);

        var descriptionChars = DescriptionTextBox.Text.Length;
        DescriptionCounterText.Text = CounterText(descriptionChars, MaxDescriptionChars);
        SetCounterTone(DescriptionCounterText, descriptionChars, MaxDescriptionChars);

        // 指令正文没有 MaxLength 截断：字符与 UTF-8 字节同时展示，超限由
        // 保存校验拒绝（Rust 配额一致），绝不静默丢弃用户文字。
        var instructionChars = InstructionTextBox.Text.Length;
        var instructionBytes = Encoding.UTF8.GetByteCount(InstructionTextBox.Text);
        InstructionCounterText.Text = CounterText(instructionChars, null, instructionBytes, MaxInstructionBytes);
        SetCounterTone(InstructionCounterText, null, null, instructionBytes, MaxInstructionBytes);

        var domainChars = DomainTextBox.Text.Length;
        var domainBytes = Encoding.UTF8.GetByteCount(DomainTextBox.Text);
        DomainCounterText.Text = CounterText(domainChars, MaxDomainChars, domainBytes, MaxDomainBytes);
        SetCounterTone(DomainCounterText, domainChars, MaxDomainChars, domainBytes, MaxDomainBytes);

        var audienceChars = AudienceTextBox.Text.Length;
        var audienceBytes = Encoding.UTF8.GetByteCount(AudienceTextBox.Text);
        AudienceCounterText.Text = CounterText(audienceChars, MaxAudienceChars, audienceBytes, MaxAudienceBytes);
        SetCounterTone(AudienceCounterText, audienceChars, MaxAudienceChars, audienceBytes, MaxAudienceBytes);
    }

    /// <summary>字符 + UTF-8 字节用量；任一维度超限追加「已超出」。</summary>
    private static string CounterText(int chars, int? charLimit, int? bytes = null, int? byteLimit = null)
    {
        var text = charLimit.HasValue && !bytes.HasValue
            ? $"{chars}/{charLimit}"
            : $"{chars} 字符 · {bytes}/{byteLimit} 字节";
        var over = (charLimit.HasValue && chars > charLimit.Value) ||
            (bytes.HasValue && byteLimit.HasValue && bytes.Value > byteLimit.Value);
        return over ? $"{text}（已超出）" : text;
    }

    private static void SetCounterTone(
        TextBlock counter, int? charCount = null, int? charLimit = null, int? bytes = null, int? byteLimit = null)
    {
        var over = (charCount.HasValue && charLimit.HasValue && charCount.Value > charLimit.Value) ||
            (bytes.HasValue && byteLimit.HasValue && bytes.Value > byteLimit.Value);
        var charUsage = charCount.HasValue && charLimit.HasValue && charLimit.Value > 0
            ? (double)charCount.Value / charLimit.Value
            : 0;
        var byteUsage = bytes.HasValue && byteLimit.HasValue && byteLimit.Value > 0
            ? (double)bytes.Value / byteLimit.Value
            : 0;
        counter.SetResourceReference(TextBlock.ForegroundProperty,
            over ? "DangerBrush" : Math.Max(charUsage, byteUsage) >= 0.9 ? "WarningBrush" : "TextTertiaryBrush");
    }

    // ===================== Compile preview (zero network) =====================

    private void RefreshPreview()
    {
        if (EditorForm.Visibility != Visibility.Visible) return;
        var template = new PromptTemplateDto(
            Id: _editingId ?? "preview",
            Name: NameTextBox.Text.Trim(),
            Description: DescriptionTextBox.Text.Trim(),
            Instruction: InstructionTextBox.Text,
            Domain: DomainTextBox.Text,
            Audience: AudienceTextBox.Text,
            Enabled: true,
            IsBuiltIn: _viewingBuiltin);
        // domain/audience stay null so the preview falls back to the template
        // defaults above — exactly what a translation without explicit
        // variables would send.
        var variables = new PromptVariablesDto(
            SourceLanguage: "auto",
            TargetLanguage: "zh-CN",
            Domain: null,
            Audience: null);
        try
        {
            var compiled = CoreBridge.CompilePromptPreview(template, variables);
            PreviewTextBox.Text = compiled.CompiledText;
            PreviewStatusText.Text = "预览已生成（未联网）";
            PreviewStatusText.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
        }
        catch (Exception exception)
        {
            PreviewTextBox.Text = string.Empty;
            PreviewStatusText.Text = $"无法生成预览：{exception.Message}";
            PreviewStatusText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        }
    }

    private async void CopyPreview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var copied = await Helpers.CopyToClipboardAsync(PreviewTextBox.Text);
            StatusChanged?.Invoke(
                copied ? "已复制预览" : "复制失败：剪贴板被其他程序占用。",
                copied ? StatusTone.Success : StatusTone.Warning);
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"复制失败：{exception.Message}", StatusTone.Error);
        }
    }

    // ===================== Interactivity & layout =====================

    private void UpdateInteractivity()
    {
        var busy = _saving;
        var readOnly = _viewingBuiltin;
        // 只读查看内置模板时，编译预览仍必须可选可复制：不再整面板禁用
        // （禁用父级会让 PreviewTextBox 连选择都做不到），只禁用可编辑输入。
        EditorFieldsPanel.IsEnabled = true;
        var editable = !readOnly && !busy;
        NameTextBox.IsEnabled = editable;
        DescriptionTextBox.IsEnabled = editable;
        InstructionTextBox.IsEnabled = editable;
        DomainTextBox.IsEnabled = editable;
        AudienceTextBox.IsEnabled = editable;
        EnabledToggle.IsEnabled = editable;
        VariableChipsPanel.IsEnabled = editable;
        SaveTemplateButton.IsEnabled = !busy;
        SaveTemplateButton.Content = busy ? "正在保存…" : "保存";
        CancelEditButton.IsEnabled = !busy;
        DeleteTemplateButton.IsEnabled = !busy;
        EditorDeleteConfirmPanel.IsEnabled = !busy;
        CopyBuiltinButton.IsEnabled = !busy;
        // 保存进行中预览仍可查看与复制：复制按钮不参与 busy 门控，
        // PreviewTextBox 始终可选。
        CopyPreviewButton.IsEnabled = true;
        DraftSaveButton.IsEnabled = !busy;
        DraftDiscardButton.IsEnabled = !busy;
        // 保存中列表同时禁鼠标与键盘：IsHitTestVisible 挡住指针，
        // IsEnabled 让 Tab 焦点与键盘激活一并失效（按钮呈现禁用态）。
        ListHost.IsEnabled = !busy;
        ListHost.IsHitTestVisible = !busy;
    }

    /// <summary>
    /// Stacks the field pairs vertically below the settings window's 700 DIP
    /// breakpoint so nothing crowds or overlaps on narrow windows.
    /// </summary>
    internal void SetCompact(bool compact)
    {
        if (_compact.HasValue && _compact.Value == compact) return;
        _compact = compact;
        if (NameDescriptionGrid.ColumnDefinitions.Count < 3 || DomainAudienceGrid.ColumnDefinitions.Count < 3) return;

        if (compact)
        {
            NameDescriptionGrid.ColumnDefinitions[1].Width = new GridLength(0);
            // B1: 说明/受众落进 Row 1，这一行必须随之变 Auto——XAML 里它是
            // 收起的 0 高度，不改的话堆叠后的字段会掉进 0 高行里不可见。
            NameDescriptionGrid.RowDefinitions[1].Height = GridLength.Auto;
            Grid.SetColumn(DescriptionPanel, 0);
            Grid.SetRow(DescriptionPanel, 1);
            DescriptionPanel.Margin = new Thickness(0, 14, 0, 0);

            DomainAudienceGrid.ColumnDefinitions[1].Width = new GridLength(0);
            DomainAudienceGrid.RowDefinitions[1].Height = GridLength.Auto;
            Grid.SetColumn(AudiencePanel, 0);
            Grid.SetRow(AudiencePanel, 1);
            AudiencePanel.Margin = new Thickness(0, 14, 0, 0);
        }
        else
        {
            NameDescriptionGrid.ColumnDefinitions[1].Width = new GridLength(14);
            // 非紧凑布局该行没有任何内容，恢复为 0 高度。
            NameDescriptionGrid.RowDefinitions[1].Height = new GridLength(0);
            Grid.SetColumn(DescriptionPanel, 2);
            Grid.SetRow(DescriptionPanel, 0);
            DescriptionPanel.Margin = new Thickness(0);

            DomainAudienceGrid.ColumnDefinitions[1].Width = new GridLength(14);
            DomainAudienceGrid.RowDefinitions[1].Height = new GridLength(0);
            Grid.SetColumn(AudiencePanel, 2);
            Grid.SetRow(AudiencePanel, 0);
            AudiencePanel.Margin = new Thickness(0);
        }
    }
}
