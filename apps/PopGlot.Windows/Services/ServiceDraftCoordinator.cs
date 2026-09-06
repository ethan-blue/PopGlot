using System.Text.Json;

namespace PopGlot.Windows.Services;

/// <summary>Plain values one editor draft needs, read by the view from its controls.</summary>
internal sealed record ServiceDraftInputs(
    string Name,
    string BaseUrl,
    ProviderType ProviderType,
    string TextEndpoint,
    string VisionEndpoint,
    string TextModel,
    string VisionModel,
    IReadOnlyDictionary<string, string> ExtraHeaders,
    string AnthropicVersion,
    bool AllowInsecureTls);

/// <summary>
/// Pure draft validation, profile construction and model
/// catalog/recommendation coordination for the service editor (T17). No WPF
/// control is touched here: the view reads its controls, hands plain values
/// over, and renders the outcome — so every rule below is testable headless
/// and the same logic backs preview and save.
/// </summary>
internal static class ServiceDraftCoordinator
{
    /// <summary>
    /// Returns the first validation problem with the draft, or null when the
    /// values are usable. Pure: no control, no I/O, no persistence.
    /// </summary>
    public static string? Validate(string? name, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "请先填写服务名称。";
        }
        var trimmedUrl = baseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedUrl))
        {
            return "API Base URL 不能为空。";
        }
        var isLocal = ProviderSettings.IsLocalBaseUrl(trimmedUrl);
        if (!trimmedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !isLocal)
        {
            return "API Base URL 必须使用 HTTPS；仅本机或局域网服务允许 HTTP。";
        }
        return null;
    }

    /// <summary>Builds the persisted profile from plain values. Pure.</summary>
    public static ProviderProfile BuildDraft(ServiceDraftInputs inputs)
    {
        var baseUrl = inputs.BaseUrl.Trim();
        var isLocal = ProviderSettings.IsLocalBaseUrl(baseUrl);
        return new ProviderProfile
        {
            Name = inputs.Name,
            ProviderType = inputs.ProviderType,
            ApiBaseUrl = baseUrl,
            TextEndpoint = string.IsNullOrWhiteSpace(inputs.TextEndpoint)
                ? "/chat/completions" : inputs.TextEndpoint.Trim(),
            VisionEndpoint = string.IsNullOrWhiteSpace(inputs.VisionEndpoint)
                ? "/chat/completions" : inputs.VisionEndpoint.Trim(),
            TextModel = inputs.TextModel,
            VisionModel = inputs.VisionModel,
            ExtraHeaders = new Dictionary<string, string>(
                inputs.ExtraHeaders, StringComparer.OrdinalIgnoreCase),
            AnthropicVersion = string.IsNullOrWhiteSpace(inputs.AnthropicVersion)
                ? "2023-06-01" : inputs.AnthropicVersion.Trim(),
            SupportsText = !string.IsNullOrWhiteSpace(inputs.TextModel),
            SupportsVision = !string.IsNullOrWhiteSpace(inputs.VisionModel),
            AllowInsecureTls = inputs.AllowInsecureTls,
            IsLocal = isLocal,
        };
    }

    /// <summary>
    /// Runs the text and (when the vision model is separate) vision
    /// recommendations. Pure: same inputs always yield the same ranking.
    /// </summary>
    public static (ModelRecommendationResult Text, ModelRecommendationResult? Vision) ComputeRecommendations(
        ProviderType providerType,
        bool isLocal,
        IReadOnlyList<ModelDescriptor> descriptors,
        ModelPreference preference,
        string? currentTextModel,
        string? currentVisionModel,
        bool visionSharedWithText)
    {
        var textResult = ModelRecommendationService.Recommend(new ModelRecommendationRequest(
            ProviderType: providerType,
            IsLocal: isLocal,
            Models: descriptors,
            TargetUsage: ModelTargetUsage.Text,
            Preference: preference,
            CurrentModelId: currentTextModel));

        if (visionSharedWithText)
        {
            return (textResult, null);
        }

        var visionResult = ModelRecommendationService.Recommend(new ModelRecommendationRequest(
            ProviderType: providerType,
            IsLocal: isLocal,
            Models: descriptors,
            TargetUsage: ModelTargetUsage.Vision,
            Preference: preference,
            CurrentModelId: currentVisionModel));
        return (textResult, visionResult);
    }

    /// <summary>
    /// Parses the header editor text ("Name: Value" per line) into a
    /// dictionary. A line without a separator is a user mistake and fails
    /// loudly instead of silently dropping a header the user meant to send.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseHeaders(string? editorText)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(editorText))
        {
            return headers;
        }
        foreach (var rawLine in editorText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || separator == line.Length - 1)
            {
                throw new InvalidOperationException($"自定义请求头格式无效：{line}（应为 Header: Value）");
            }
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return headers;
    }
}
