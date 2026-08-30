using System.Text.RegularExpressions;

namespace PopGlot.Windows.Services;

/// <summary>
/// User recommendation preference.
/// </summary>
internal enum ModelPreference
{
    Speed,
    Balanced,
    Quality,
}

/// <summary>
/// Target scenario for translation.
/// </summary>
internal enum ModelTargetUsage
{
    Text,
    Vision,
}

/// <summary>
/// Source of evidence backing a recommendation or capability assessment.
/// </summary>
[Flags]
internal enum RecommendationEvidenceSource
{
    None = 0,
    /// <summary>Explicit facts declared by the provider's catalog endpoint.</summary>
    CatalogExplicit = 1 << 0,
    /// <summary>General model family name and parameter size heuristics.</summary>
    FamilyHeuristics = 1 << 1,
    /// <summary>Explicit benchmark measurements on the exact current endpoint and machine.</summary>
    LocalBenchmark = 1 << 2,
    /// <summary>No conclusive evidence; treated as unknown without invented capabilities.</summary>
    FallbackUnknown = 1 << 3,
}

/// <summary>
/// Categorized speed/capability tier based on family heuristics or verified benchmarks.
/// </summary>
internal enum ModelTier
{
    Unknown,
    Speed,
    Balanced,
    Quality,
    Reasoner,
}

/// <summary>
/// Context identifying the current execution environment for benchmark correlation.
/// </summary>
internal sealed record ModelBenchmarkContext(
    string Endpoint,
    string PromptVersion,
    string MachineId);

/// <summary>
/// A single benchmark observation. Must strictly match the endpoint, model, prompt version, and machine to be adopted.
/// </summary>
internal sealed record ModelBenchmarkMetric(
    string Endpoint,
    string ModelId,
    string PromptVersion,
    string MachineId,
    DateTimeOffset Timestamp,
    double TtftMs,
    double CharsPerSecond,
    double TotalDurationMs,
    bool Success = true)
{
    public bool MatchesContext(ModelBenchmarkContext? context, string targetModelId)
    {
        if (context is null || !Success)
        {
            return false;
        }

        if (!string.Equals(ModelId?.Trim(), targetModelId?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(PromptVersion?.Trim(), context.PromptVersion?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(MachineId?.Trim(), context.MachineId?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return NormalizeEndpoint(Endpoint) == NormalizeEndpoint(context.Endpoint);
    }

    public static string NormalizeEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return string.Empty;
        }

        var trimmed = endpoint.Trim().TrimEnd('/');
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}".ToLowerInvariant();
        }

        return trimmed.ToLowerInvariant();
    }
}

/// <summary>
/// Evaluation of an individual model candidate.
/// </summary>
internal sealed record ModelCandidateEvaluation(
    ModelDescriptor Model,
    bool IsEligible,
    string? FilterReason,
    bool IsCurrentSelected,
    ModelTier Tier,
    double? EstimatedParameterSize,
    bool IsReasoner,
    bool IsPreviewOrExperimental,
    RecommendationEvidenceSource EvidenceSources,
    string PrimaryReason,
    IReadOnlyList<string> DetailedReasons,
    IReadOnlyList<string> Warnings,
    ModelBenchmarkMetric? BenchmarkEvidence,
    double Score);

/// <summary>
/// Input request for computing recommendations.
/// </summary>
internal sealed record ModelRecommendationRequest(
    ProviderType ProviderType,
    bool IsLocal,
    IReadOnlyList<ModelDescriptor> Models,
    ModelTargetUsage TargetUsage,
    ModelPreference Preference,
    string? CurrentModelId = null,
    ModelBenchmarkContext? BenchmarkContext = null,
    IReadOnlyList<ModelBenchmarkMetric>? BenchmarkMetrics = null);

/// <summary>
/// Final recommendation result.
/// </summary>
internal sealed record ModelRecommendationResult(
    ModelPreference Preference,
    ModelTargetUsage TargetUsage,
    string? CurrentModelId,
    ModelCandidateEvaluation? RecommendedModel,
    IReadOnlyList<ModelCandidateEvaluation> Candidates,
    IReadOnlyList<ModelCandidateEvaluation> AllEvaluations,
    string Summary,
    bool UserCanOverride = true);

/// <summary>
/// Pure C# model recommendation engine based on explainable heuristics, catalog facts, and optional local benchmarks.
/// Does not invent fake latencies, fake context windows, or hardcoded leaderboard rankings.
/// </summary>
internal static class ModelRecommendationService
{
    private static readonly Regex NonGenerativePattern = new(
        @"(?i)(?:^|[\-_:/])(?:text-)?(?:embed(?:ding)?|tts|whisper|audio|speech|voice|moderation|guard|safety|filter|shield|rerank(?:er)?|dall-e|imagen|midjourney|stable-diffusion|flux|bge-rerank|cohere-rerank)(?:$|[\-_:/])",
        RegexOptions.Compiled);

    private static readonly Regex ReasonerPattern = new(
        @"(?i)(?:^|[\-_:/])(?:reason(?:er|ing)?|thinking|deepseek-r1|r1|o1|o3|o4|qwqa|qvq|thought|cot)(?:$|[\-_:/])",
        RegexOptions.Compiled);

    private static readonly Regex PreviewPattern = new(
        @"(?i)(?:^|[\-_:/])(?:preview|experimental|exp|beta|alpha|canary|nightly|test|dev)(?:$|[\-_:/])",
        RegexOptions.Compiled);

    private static readonly Regex SpeedKeywordPattern = new(
        @"(?i)(?:^|[\-_:/])(?:mini|flash|haiku|nano|micro|small|lite|instant|turbo)(?:$|[\-_:/])",
        RegexOptions.Compiled);

    private static readonly Regex QualityKeywordPattern = new(
        @"(?i)(?:^|[\-_:/])(?:pro|sonnet|opus|plus|large|max|ultra|flagship)(?:$|[\-_:/])",
        RegexOptions.Compiled);

    private static readonly Regex ParameterSizePattern = new(
        @"(?i)(?:^|[\-_:\s/])([0-9]+(?:\.[0-9]+)?)[bB](?:[\-_:\s/]|$)",
        RegexOptions.Compiled);

    public static ModelRecommendationResult Recommend(ModelRecommendationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var models = request.Models ?? [];
        var evaluations = new List<ModelCandidateEvaluation>(models.Count);

        var currentNormalized = request.CurrentModelId?.Trim();
        var currentEvaluated = false;

        foreach (var model in models)
        {
            var isCurrent = !string.IsNullOrEmpty(currentNormalized) &&
                            string.Equals(model.Id?.Trim(), currentNormalized, StringComparison.OrdinalIgnoreCase);

            if (isCurrent)
            {
                currentEvaluated = true;
            }

            var eval = EvaluateModel(model, request, isCurrent);
            evaluations.Add(eval);
        }

        // If the current configured model wasn't present in the catalog list, preserve it as a candidate anyway
        if (!currentEvaluated && !string.IsNullOrWhiteSpace(currentNormalized))
        {
            var syntheticDescriptor = new ModelDescriptor(
                Id: currentNormalized,
                TextGeneration: CapabilityState.Unknown,
                VisionInput: request.TargetUsage == ModelTargetUsage.Vision ? CapabilityState.Unknown : CapabilityState.Supported,
                CapabilitySource: "CurrentConfiguration");

            var syntheticEval = EvaluateModel(syntheticDescriptor, request, isCurrentSelected: true);
            evaluations.Add(syntheticEval);
        }

        // Eligible candidates: must be eligible, OR be the currently selected model (always preserved)
        var eligibleCandidates = evaluations
            .Where(e => e.IsEligible || e.IsCurrentSelected)
            .OrderByDescending(e => e.Score)
            .ToList();

        var topRecommended = eligibleCandidates.FirstOrDefault(e => e.IsEligible) ??
                             eligibleCandidates.FirstOrDefault();

        var summary = BuildSummary(request, topRecommended, eligibleCandidates.Count);

        return new ModelRecommendationResult(
            Preference: request.Preference,
            TargetUsage: request.TargetUsage,
            CurrentModelId: request.CurrentModelId,
            RecommendedModel: topRecommended,
            Candidates: eligibleCandidates,
            AllEvaluations: evaluations,
            Summary: summary,
            UserCanOverride: true);
    }

    private static ModelCandidateEvaluation EvaluateModel(
        ModelDescriptor model,
        ModelRecommendationRequest request,
        bool isCurrentSelected)
    {
        var modelId = model.Id ?? string.Empty;
        var detailedReasons = new List<string>();
        var warnings = new List<string>();
        var evidence = RecommendationEvidenceSource.None;

        // 1. Modality & Non-Generative Check
        var isNonGenerative = IsNonGenerative(model);
        var isEligible = true;
        string? filterReason = null;

        if (isNonGenerative)
        {
            isEligible = false;
            filterReason = "非生成式翻译模型（Embedding / 音频 / 审核 / 重排序 / 图像生成等）";
        }
        else if (request.TargetUsage == ModelTargetUsage.Vision)
        {
            if (model.VisionInput == CapabilityState.Unsupported)
            {
                isEligible = false;
                filterReason = "目录明确声明不支持视觉输入";
                evidence |= RecommendationEvidenceSource.CatalogExplicit;
            }
            else if (model.VisionInput == CapabilityState.Supported)
            {
                evidence |= RecommendationEvidenceSource.CatalogExplicit;
                detailedReasons.Add("目录明确支持视觉图像输入");
            }
            else
            {
                // CapabilityState.Unknown
                warnings.Add("视觉支持状态未知（目录未声明）；未做强推荐，建议验证后使用");
            }
        }
        else if (model.TextGeneration == CapabilityState.Unsupported)
        {
            isEligible = false;
            filterReason = "目录明确声明不支持文本生成";
            evidence |= RecommendationEvidenceSource.CatalogExplicit;
        }

        // 2. Reasoner & Preview Detection
        var isReasoner = ReasonerPattern.IsMatch(modelId);
        var isPreview = PreviewPattern.IsMatch(modelId);

        if (isReasoner)
        {
            warnings.Add("深度思考/推理模型（首字延迟高且带有思考过程，高频快翻已降权）");
            evidence |= RecommendationEvidenceSource.FamilyHeuristics;
        }

        if (isPreview)
        {
            warnings.Add("预览版或实验性模型（接口规范与稳定性未知）");
            evidence |= RecommendationEvidenceSource.FamilyHeuristics;
        }

        // 3. Parameter Size Heuristics (Local models & tagged names)
        var paramSize = ExtractParameterSize(modelId);
        if (paramSize.HasValue)
        {
            evidence |= RecommendationEvidenceSource.FamilyHeuristics;
        }

        // 4. Tier Determination
        var tier = DetermineTier(modelId, isReasoner, paramSize, request.IsLocal);
        if (tier != ModelTier.Unknown)
        {
            evidence |= RecommendationEvidenceSource.FamilyHeuristics;
        }

        // 5. Benchmark Matching
        ModelBenchmarkMetric? matchedBenchmark = null;
        if (request.BenchmarkMetrics is not null && request.BenchmarkContext is not null)
        {
            foreach (var metric in request.BenchmarkMetrics)
            {
                if (metric.MatchesContext(request.BenchmarkContext, modelId))
                {
                    matchedBenchmark = metric;
                    evidence |= RecommendationEvidenceSource.LocalBenchmark;
                    detailedReasons.Add(
                        $"匹配当前端点与本机实测基准 (Prompt: {metric.PromptVersion}, TTFT: {metric.TtftMs:F0}ms, 速度: {metric.CharsPerSecond:F1} 字符/秒)");
                    break;
                }
            }
        }

        // 6. Fallback if no evidence sources found
        if (evidence == RecommendationEvidenceSource.None)
        {
            evidence = RecommendationEvidenceSource.FallbackUnknown;
        }

        // 7. Scoring
        var score = CalculateScore(
            model,
            tier,
            paramSize,
            isReasoner,
            isPreview,
            request.Preference,
            request.TargetUsage,
            request.IsLocal,
            matchedBenchmark);

        // 8. Primary Reason Formulation
        var primaryReason = BuildPrimaryReason(
            tier,
            request.Preference,
            paramSize,
            isReasoner,
            isPreview,
            request.TargetUsage,
            model.VisionInput,
            matchedBenchmark,
            isCurrentSelected);

        return new ModelCandidateEvaluation(
            Model: model,
            IsEligible: isEligible,
            FilterReason: filterReason,
            IsCurrentSelected: isCurrentSelected,
            Tier: tier,
            EstimatedParameterSize: paramSize,
            IsReasoner: isReasoner,
            IsPreviewOrExperimental: isPreview,
            EvidenceSources: evidence,
            PrimaryReason: primaryReason,
            DetailedReasons: detailedReasons,
            Warnings: warnings,
            BenchmarkEvidence: matchedBenchmark,
            Score: score);
    }

    private static bool IsNonGenerative(ModelDescriptor model)
    {
        if (model.TextGeneration == CapabilityState.Unsupported)
        {
            return true;
        }

        return NonGenerativePattern.IsMatch(model.Id ?? string.Empty);
    }

    private static double? ExtractParameterSize(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var match = ParameterSizePattern.Match(modelId);
        if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var size))
        {
            return size;
        }

        return null;
    }

    private static ModelTier DetermineTier(string modelId, bool isReasoner, double? paramSize, bool isLocal)
    {
        if (isReasoner)
        {
            return ModelTier.Reasoner;
        }

        if (SpeedKeywordPattern.IsMatch(modelId))
        {
            return ModelTier.Speed;
        }

        if (QualityKeywordPattern.IsMatch(modelId))
        {
            return ModelTier.Quality;
        }

        if (isLocal && paramSize.HasValue)
        {
            if (paramSize.Value <= 4.0)
            {
                return ModelTier.Speed;
            }

            if (paramSize.Value >= 30.0)
            {
                return ModelTier.Quality;
            }

            return ModelTier.Balanced;
        }

        if (modelId.Contains("chat", StringComparison.OrdinalIgnoreCase) ||
            modelId.Contains("instruct", StringComparison.OrdinalIgnoreCase))
        {
            return ModelTier.Balanced;
        }

        return ModelTier.Unknown;
    }

    private static double CalculateScore(
        ModelDescriptor model,
        ModelTier tier,
        double? paramSize,
        bool isReasoner,
        bool isPreview,
        ModelPreference preference,
        ModelTargetUsage targetUsage,
        bool isLocal,
        ModelBenchmarkMetric? benchmark)
    {
        var score = 50.0;

        // Preference weighting
        switch (preference)
        {
            case ModelPreference.Speed:
                score += tier switch
                {
                    ModelTier.Speed => 40.0,
                    ModelTier.Balanced => 20.0,
                    ModelTier.Quality => 0.0,
                    ModelTier.Reasoner => -40.0,
                    _ => 10.0,
                };
                if (isLocal && paramSize.HasValue)
                {
                    if (paramSize.Value <= 3.5) score += 15.0;
                    else if (paramSize.Value <= 8.0) score += 5.0;
                    else if (paramSize.Value >= 30.0) score -= 20.0;
                }
                break;

            case ModelPreference.Balanced:
                score += tier switch
                {
                    ModelTier.Balanced => 35.0,
                    ModelTier.Speed => 25.0,
                    ModelTier.Quality => 25.0,
                    ModelTier.Reasoner => -30.0,
                    _ => 15.0,
                };
                if (isLocal && paramSize.HasValue)
                {
                    if (paramSize.Value >= 4.0 && paramSize.Value <= 16.0) score += 15.0;
                }
                break;

            case ModelPreference.Quality:
                score += tier switch
                {
                    ModelTier.Quality => 40.0,
                    ModelTier.Balanced => 20.0,
                    ModelTier.Speed => 5.0,
                    ModelTier.Reasoner => -10.0,
                    _ => 10.0,
                };
                if (isLocal && paramSize.HasValue)
                {
                    if (paramSize.Value >= 30.0) score += 25.0;
                    else if (paramSize.Value >= 14.0) score += 15.0;
                    else if (paramSize.Value <= 3.5) score -= 10.0;
                }
                break;
        }

        // Vision modality weighting
        if (targetUsage == ModelTargetUsage.Vision)
        {
            if (model.VisionInput == CapabilityState.Supported)
            {
                score += 20.0;
            }
            else if (model.VisionInput == CapabilityState.Unknown)
            {
                // Unknown vision capability is penalized compared to explicitly supported models
                score -= 25.0;
            }
        }

        // Experimental penalty
        if (isPreview)
        {
            score -= 15.0;
        }

        // Benchmark real-world boost (only if exact match)
        if (benchmark is not null && benchmark.Success)
        {
            score += 10.0;

            if (preference == ModelPreference.Speed)
            {
                if (benchmark.TtftMs > 0 && benchmark.TtftMs <= 500) score += 20.0;
                else if (benchmark.TtftMs > 0 && benchmark.TtftMs <= 1000) score += 10.0;
                else if (benchmark.TtftMs > 2500) score -= 15.0;

                if (benchmark.CharsPerSecond >= 50) score += 15.0;
                else if (benchmark.CharsPerSecond >= 25) score += 8.0;
            }
            else if (preference == ModelPreference.Balanced)
            {
                if (benchmark.TtftMs > 0 && benchmark.TtftMs <= 1000) score += 10.0;
                if (benchmark.CharsPerSecond >= 30) score += 10.0;
            }
            else if (preference == ModelPreference.Quality)
            {
                if (benchmark.TtftMs > 6000) score -= 10.0;
            }
        }

        return score;
    }

    private static string BuildPrimaryReason(
        ModelTier tier,
        ModelPreference preference,
        double? paramSize,
        bool isReasoner,
        bool isPreview,
        ModelTargetUsage targetUsage,
        CapabilityState visionInput,
        ModelBenchmarkMetric? benchmark,
        bool isCurrentSelected)
    {
        var parts = new List<string>();

        if (isCurrentSelected)
        {
            parts.Add("当前已配置模型");
        }

        if (benchmark is not null && benchmark.Success)
        {
            parts.Add($"实测响应快（TTFT ~{benchmark.TtftMs:F0}ms）");
        }
        else if (isReasoner)
        {
            parts.Add("深度思考模型（延迟高，不建议即时快翻）");
        }
        else if (tier == ModelTier.Speed)
        {
            parts.Add(paramSize.HasValue
                ? $"轻量小参数族系（~{paramSize.Value:G}B），通常响应延迟低"
                : "轻量快速族系（如 mini/flash/haiku），通常响应速度快");
        }
        else if (tier == ModelTier.Quality)
        {
            parts.Add(paramSize.HasValue
                ? $"高参数量族系（~{paramSize.Value:G}B），语言理解与翻译表达更精细"
                : "高性能旗舰族系（如 pro/sonnet/opus），翻译质量通常更精准");
        }
        else if (tier == ModelTier.Balanced)
        {
            parts.Add("标准均衡族系，兼顾响应与翻译表达");
        }
        else
        {
            parts.Add("通用生成模型");
        }

        if (targetUsage == ModelTargetUsage.Vision)
        {
            if (visionInput == CapabilityState.Supported)
            {
                parts.Add("具备目录验证的视觉支持");
            }
            else if (visionInput == CapabilityState.Unknown)
            {
                parts.Add("视觉支持待验证");
            }
        }

        if (isPreview)
        {
            parts.Add("预览实验版本");
        }

        return string.Join("；", parts);
    }

    private static string BuildSummary(
        ModelRecommendationRequest request,
        ModelCandidateEvaluation? recommended,
        int eligibleCount)
    {
        if (recommended is null)
        {
            return "未在候选列表中找到适用的生成模型。";
        }

        var prefLabel = request.Preference switch
        {
            ModelPreference.Speed => "速度优先",
            ModelPreference.Balanced => "均衡推荐",
            ModelPreference.Quality => "质量优先",
            _ => "默认推荐",
        };

        var usageLabel = request.TargetUsage == ModelTargetUsage.Vision ? "视觉翻译" : "文本翻译";

        return $"{prefLabel}（{usageLabel}，共 {eligibleCount} 个可用候选）：首选推荐 {recommended.Model.Id}，{recommended.PrimaryReason}。用户可随时手动覆盖切换。";
    }
}
