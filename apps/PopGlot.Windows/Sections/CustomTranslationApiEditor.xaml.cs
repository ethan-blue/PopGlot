using System.Windows;
using System.Windows.Controls;
using PopGlot.Windows.Services;

namespace PopGlot.Windows.Sections;

public partial class CustomTranslationApiEditor : System.Windows.Controls.UserControl
{
    private CancellationTokenSource? _testCts;
    private ConfirmButton? _clearKey;
    internal event Action? RouteChanged;
    public CustomTranslationApiEditor()
    {
        InitializeComponent();
        try { Fill(CustomTranslationApi.Load()); }
        catch { StatusText.Text = "自定义接口配置无法读取，请重新填写后保存。"; }
        _clearKey = ConfirmButton.Attach(ClearApiKeyButton, "确认清除？", () =>
        {
            try
            {
                CredentialStore.DeleteApiKey(ReadDraft().CredentialTarget);
                KeyInput.Clear(); StatusText.Text = "已清除当前地址的密钥。";
            }
            catch (Exception ex) { StatusText.Text = SecretRedactor.Redact(ex.Message); }
        });
        Unloaded += (_, _) => { _testCts?.Cancel(); _clearKey?.Disarm(); };
    }
    private CustomTranslationApiOptions ReadDraft() => new(NameInput.Text.Trim(), EndpointInput.Text.Trim(),
        TemplateInput.Text, ResponseInput.Text.Trim(), HeaderInput.Text.Trim(), PrefixInput.Text);
    private void Fill(CustomTranslationApiOptions value)
    {
        NameInput.Text = value.Name; EndpointInput.Text = value.Endpoint;
        TemplateInput.Text = value.RequestTemplate; ResponseInput.Text = value.ResponsePointer;
        HeaderInput.Text = value.ApiKeyHeader; PrefixInput.Text = value.ApiKeyPrefix;
        KeyInput.Clear();
    }
    private void LibreTemplate_Click(object sender, RoutedEventArgs e)
    {
        var old = ReadDraft();
        Fill(new CustomTranslationApiOptions(Endpoint: old.Endpoint));
        StatusText.Text = "已填 LibreTranslate 模板；请填写自己的服务地址。模板不修改已保存配置。";
    }
    private void DeepLxTemplate_Click(object sender, RoutedEventArgs e)
    {
        var old = ReadDraft();
        Fill(new CustomTranslationApiOptions("DeepLX", old.Endpoint,
            "{\"text\":\"{text}\",\"source_lang\":\"{sourceUpper}\",\"target_lang\":\"{targetUpper}\"}", "/data"));
        StatusText.Text = "已填 DeepLX 模板；语言自动转换为 EN / ZH 等大写代码。特殊地区代码可在模板中配置。";
    }
    private void DeepLApiTemplate_Click(object sender, RoutedEventArgs e)
    {
        Fill(CustomTranslationApiPresets.DeepLApi());
        StatusText.Text = "已填 DeepL 官方 API 模板，需自己的 API Key；默认自动识别原文。Pro 用户请改为官方 Pro 地址；本按钮不联网、不保存。";
    }
    private void CaiyunTemplate_Click(object sender, RoutedEventArgs e)
    {
        Fill(CustomTranslationApiPresets.Caiyun());
        StatusText.Text = "已填彩云小译官方 API 模板，需自己的 Token。仅支持服务开通的语言方向；本按钮不联网、不保存。";
    }
    private void Save(bool use)
    {
        try
        {
            var options = ReadDraft();
            CustomTranslationApi.Validate(options);
            // An endpoint change never copies another endpoint's stored credential.
            if (!string.IsNullOrWhiteSpace(KeyInput.Password))
                CredentialStore.SaveApiKey(KeyInput.Password, options.CredentialTarget);
            CustomTranslationApi.Save(options);
            KeyInput.Clear();
            if (use && !ProfileManager.TrySwitchToFreeEngine(FreeEngineProvider.Custom, out var error))
            { StatusText.Text = "已保存，未启用：" + error; return; }
            StatusText.Text = use ? "已保存并使用。请按 Enter 翻译；自动调用需显式选择「所有引擎」。" : "已保存；没有发送测试请求。";
            RouteChanged?.Invoke();
        }
        catch (Exception ex) { StatusText.Text = SecretRedactor.Redact(ex.Message); }
    }
    private void Save_Click(object sender, RoutedEventArgs e) => Save(false);
    private void SaveUse_Click(object sender, RoutedEventArgs e) => Save(true);
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_testCts is not null) { _testCts.Cancel(); return; }
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _testCts = cts;
        TestButton.Content = "取消测试";
        try
        {
            var options = ReadDraft(); CustomTranslationApi.Validate(options);
            if (!OutboundPolicy.AllowsFreeEngine(CoreBridge.GetSettings(), out var denial, out var authorization))
                throw new InvalidOperationException(denial?.Message);
            StatusText.Text = "正在发送固定测试句（不含你的待译文本）…";
            var key = string.IsNullOrWhiteSpace(KeyInput.Password) ? null : KeyInput.Password;
            var response = await CustomTranslationApi.TranslateAsync("Hello world", "en", "zh", authorization!, cts.Token, options, key);
            StatusText.Text = $"测试返回译文：{response.Result.TranslatedText} · {response.Diagnostics?.ElapsedMs} ms（未保存配置）";
        }
        catch (OperationCanceledException) { StatusText.Text = "测试已取消或超过 30 秒。"; }
        catch (Exception ex) { StatusText.Text = SecretRedactor.Redact(ex.Message); }
        finally { _testCts = null; cts.Dispose(); TestButton.Content = "测试翻译"; }
    }
}
