using PopGlot.Windows.Services;

namespace PopGlot.Windows.LogicTests;

/// <summary>
/// Independent test helper verifying the ModelRecommendationService logic.
/// Covers unknowns, non-generative filtering, 3 preferences, vision modality,
/// local parameter heuristics, preview/experimental, reasoner penalties,
/// current model preservation, benchmark matching/mismatch, and evidence sources.
/// </summary>
public static class ModelRecommendationTestsHelper
{
    public static void RunAllTests()
    {
        TestUnknownsAndNullsPreserved();
        TestNonGenerativeFilteredOut();
        TestThreePreferences();
        TestVisionModalityRules();
        TestLocalModelParameterHeuristics();
        TestPreviewAndExperimentalFlagging();
        TestReasonerThinkingPenalized();
        TestCurrentSelectedAlwaysPreserved();
        TestBenchmarkScopeMatchingAndRejection();
        TestEvidenceSourcesAndReasons();
    }

    private static void TestUnknownsAndNullsPreserved()
    {
        var model = new ModelDescriptor(
            Id: "custom-enterprise-translator",
            TextGeneration: CapabilityState.Unknown,
            VisionInput: CapabilityState.Unknown,
            CapabilitySource: "CustomCatalog");

        var request = new ModelRecommendationRequest(
            ProviderType: ProviderType.OpenAiCompatible,
            IsLocal: false,
            Models: [model],
            TargetUsage: ModelTargetUsage.Text,
            Preference: ModelPreference.Balanced);

        var result = ModelRecommendationService.Recommend(request);
        Assert(result.Candidates.Count == 1, "Should have 1 candidate");
        var eval = result.Candidates[0];

        Assert(eval.EstimatedParameterSize == null, "Unknown parameter size must be null");
        Assert(eval.Tier == ModelTier.Unknown, "Unknown tier must remain ModelTier.Unknown");
        Assert(eval.Model.VisionInput == CapabilityState.Unknown, "VisionInput must remain Unknown");
        Assert(eval.EvidenceSources.HasFlag(RecommendationEvidenceSource.FallbackUnknown),
            "Should fallback to FallbackUnknown evidence source");
        Assert(eval.BenchmarkEvidence == null, "Must not fabricate benchmark evidence");
    }

    private static void TestNonGenerativeFilteredOut()
    {
        var models = new List<ModelDescriptor>
        {
            new("text-embedding-3-small", CapabilityState.Supported, CapabilityState.Unsupported, "Catalog"),
            new("text-embedding-ada-002", CapabilityState.Supported, CapabilityState.Unsupported, "Catalog"),
            new("tts-1", CapabilityState.Supported, CapabilityState.Unsupported, "Catalog"),
            new("whisper-1", CapabilityState.Supported, CapabilityState.Unsupported, "Catalog"),
            new("text-moderation-latest", CapabilityState.Supported, CapabilityState.Unsupported, "Catalog"),
            new("bge-reranker-large", CapabilityState.Supported, CapabilityState.Unsupported, "Catalog"),
            new("dall-e-3", CapabilityState.Supported, CapabilityState.Unsupported, "Catalog"),
            new("explicit-unsupported-model", CapabilityState.Unsupported, CapabilityState.Unsupported, "Catalog"),
            new("gpt-4o-mini", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
        };

        var request = new ModelRecommendationRequest(
            ProviderType: ProviderType.OpenAiCompatible,
            IsLocal: false,
            Models: models,
            TargetUsage: ModelTargetUsage.Text,
            Preference: ModelPreference.Speed);

        var result = ModelRecommendationService.Recommend(request);

        // Only gpt-4o-mini should be eligible
        var eligible = result.Candidates.Where(c => c.IsEligible).ToList();
        Assert(eligible.Count == 1, $"Expected 1 eligible candidate, got {eligible.Count}");
        Assert(eligible[0].Model.Id == "gpt-4o-mini", "Expected gpt-4o-mini to be recommended");

        // The non-generative models should have IsEligible = false with FilterReason
        var embeddings = result.AllEvaluations.First(e => e.Model.Id == "text-embedding-3-small");
        Assert(!embeddings.IsEligible, "Embedding model must not be eligible");
        Assert(!string.IsNullOrEmpty(embeddings.FilterReason), "Embedding model must have a filter reason");

        var moderation = result.AllEvaluations.First(e => e.Model.Id == "text-moderation-latest");
        Assert(!moderation.IsEligible, "Moderation model must not be eligible");

        var unsupported = result.AllEvaluations.First(e => e.Model.Id == "explicit-unsupported-model");
        Assert(!unsupported.IsEligible, "Explicit unsupported model must not be eligible");
    }

    private static void TestThreePreferences()
    {
        var models = new List<ModelDescriptor>
        {
            new("claude-3-5-haiku-20241022", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
            new("claude-3-5-sonnet-20241022", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
            new("claude-3-opus-20240229", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
        };

        // 1. Speed preference -> haiku should win
        var speedRequest = new ModelRecommendationRequest(
            ProviderType: ProviderType.AnthropicMessages,
            IsLocal: false,
            Models: models,
            TargetUsage: ModelTargetUsage.Text,
            Preference: ModelPreference.Speed);
        var speedResult = ModelRecommendationService.Recommend(speedRequest);
        Assert(speedResult.RecommendedModel?.Model.Id == "claude-3-5-haiku-20241022",
            $"Speed preference should pick haiku, got {speedResult.RecommendedModel?.Model.Id}");
        Assert(speedResult.RecommendedModel?.Tier == ModelTier.Speed, "haiku should be ModelTier.Speed");

        // 2. Quality preference -> sonnet or opus should win
        var qualityRequest = new ModelRecommendationRequest(
            ProviderType: ProviderType.AnthropicMessages,
            IsLocal: false,
            Models: models,
            TargetUsage: ModelTargetUsage.Text,
            Preference: ModelPreference.Quality);
        var qualityResult = ModelRecommendationService.Recommend(qualityRequest);
        Assert(qualityResult.RecommendedModel?.Tier == ModelTier.Quality,
            $"Quality preference should pick a Quality tier model, got {qualityResult.RecommendedModel?.Model.Id}");
        Assert(qualityResult.RecommendedModel?.Model.Id.Contains("sonnet", StringComparison.OrdinalIgnoreCase) == true ||
               qualityResult.RecommendedModel?.Model.Id.Contains("opus", StringComparison.OrdinalIgnoreCase) == true,
            "Quality preference should pick sonnet or opus");

        // 3. Balanced preference -> computes valid candidate list
        var balancedRequest = new ModelRecommendationRequest(
            ProviderType: ProviderType.AnthropicMessages,
            IsLocal: false,
            Models: models,
            TargetUsage: ModelTargetUsage.Text,
            Preference: ModelPreference.Balanced);
        var balancedResult = ModelRecommendationService.Recommend(balancedRequest);
        Assert(balancedResult.RecommendedModel != null, "Balanced preference must have a recommendation");
        Assert(balancedResult.Candidates.Count == 3, "All 3 models should be eligible candidates");
    }

    private static void TestVisionModalityRules()
    {
        var models = new List<ModelDescriptor>
        {
            new("gpt-4o-vision-supported", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
            new("gpt-4o-vision-unknown", CapabilityState.Supported, CapabilityState.Unknown, "Catalog"),
            new("text-only-model", CapabilityState.Supported, CapabilityState.Unsupported, "Catalog"),
        };

        var request = new ModelRecommendationRequest(
            ProviderType: ProviderType.OpenAiCompatible,
            IsLocal: false,
            Models: models,
            TargetUsage: ModelTargetUsage.Vision,
            Preference: ModelPreference.Speed);

        var result = ModelRecommendationService.Recommend(request);

        // text-only-model must be excluded (ineligible)
        var textOnlyEval = result.AllEvaluations.First(e => e.Model.Id == "text-only-model");
        Assert(!textOnlyEval.IsEligible, "Unsupported vision model must be ineligible");
        Assert(textOnlyEval.FilterReason?.Contains("视觉") == true, "Filter reason must mention vision");

        // gpt-4o-vision-supported must rank higher than gpt-4o-vision-unknown
        var supportedEval = result.Candidates.First(c => c.Model.Id == "gpt-4o-vision-supported");
        var unknownEval = result.Candidates.First(c => c.Model.Id == "gpt-4o-vision-unknown");

        Assert(supportedEval.Score > unknownEval.Score, "Supported vision must score higher than Unknown vision");
        Assert(unknownEval.Warnings.Any(w => w.Contains("未知")), "Unknown vision must have a warning");
        Assert(result.RecommendedModel?.Model.Id == "gpt-4o-vision-supported", "Recommended model must be vision-supported");
    }

    private static void TestLocalModelParameterHeuristics()
    {
        var models = new List<ModelDescriptor>
        {
            new("qwen2.5:1.5b", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
            new("qwen2.5:7b", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
            new("llama-3.1:70b", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
        };

        // 1. Parameter extraction
        var speedRequest = new ModelRecommendationRequest(
            ProviderType: ProviderType.OpenAiCompatible,
            IsLocal: true,
            Models: models,
            TargetUsage: ModelTargetUsage.Text,
            Preference: ModelPreference.Speed);

        var speedResult = ModelRecommendationService.Recommend(speedRequest);
        var qwen15 = speedResult.Candidates.First(c => c.Model.Id == "qwen2.5:1.5b");
        var qwen7 = speedResult.Candidates.First(c => c.Model.Id == "qwen2.5:7b");
        var llama70 = speedResult.Candidates.First(c => c.Model.Id == "llama-3.1:70b");

        Assert(Math.Abs(qwen15.EstimatedParameterSize!.Value - 1.5) < 0.01, "1.5b extracted");
        Assert(Math.Abs(qwen7.EstimatedParameterSize!.Value - 7.0) < 0.01, "7b extracted");
        Assert(Math.Abs(llama70.EstimatedParameterSize!.Value - 70.0) < 0.01, "70b extracted");

        Assert(qwen15.Tier == ModelTier.Speed, "1.5b local should be Speed tier");
        Assert(llama70.Tier == ModelTier.Quality, "70b local should be Quality tier");

        // In Speed preference, 1.5b should rank above 70b
        Assert(qwen15.Score > llama70.Score, "1.5b should score higher than 70b under Speed preference");
        Assert(speedResult.RecommendedModel?.Model.Id == "qwen2.5:1.5b", "1.5b should be recommended for Speed");

        // In Quality preference, 70b should rank above 1.5b
        var qualityRequest = speedRequest with { Preference = ModelPreference.Quality };
        var qualityResult = ModelRecommendationService.Recommend(qualityRequest);
        var qwen15Q = qualityResult.Candidates.First(c => c.Model.Id == "qwen2.5:1.5b");
        var llama70Q = qualityResult.Candidates.First(c => c.Model.Id == "llama-3.1:70b");
        Assert(llama70Q.Score > qwen15Q.Score, "70b should score higher than 1.5b under Quality preference");
        Assert(qualityResult.RecommendedModel?.Model.Id == "llama-3.1:70b", "70b should be recommended for Quality");
    }

    private static void TestPreviewAndExperimentalFlagging()
    {
        var models = new List<ModelDescriptor>
        {
            new("gemini-2.0-flash", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
            new("gemini-2.0-flash-exp", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
            new("gpt-4o-2024-11-20-preview", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
        };

        var request = new ModelRecommendationRequest(
            ProviderType: ProviderType.GeminiGenerateContent,
            IsLocal: false,
            Models: models,
            TargetUsage: ModelTargetUsage.Text,
            Preference: ModelPreference.Speed);

        var result = ModelRecommendationService.Recommend(request);
        var stableFlash = result.Candidates.First(c => c.Model.Id == "gemini-2.0-flash");
        var expFlash = result.Candidates.First(c => c.Model.Id == "gemini-2.0-flash-exp");
        var previewGpt = result.Candidates.First(c => c.Model.Id == "gpt-4o-2024-11-20-preview");

        Assert(!stableFlash.IsPreviewOrExperimental, "Stable flash should not be preview");
        Assert(expFlash.IsPreviewOrExperimental, "gemini-2.0-flash-exp must be marked preview/exp");
        Assert(previewGpt.IsPreviewOrExperimental, "preview model must be marked preview/exp");

        Assert(expFlash.Warnings.Any(w => w.Contains("预览") || w.Contains("实验")),
            "Exp model must have stability warning");
        Assert(stableFlash.Score > expFlash.Score, "Stable model must score higher than exp version");
    }

    private static void TestReasonerThinkingPenalized()
    {
        var models = new List<ModelDescriptor>
        {
            new("deepseek-chat", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
            new("deepseek-reasoner", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
            new("o1-mini", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
            new("qwqa-32b", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
        };

        var request = new ModelRecommendationRequest(
            ProviderType: ProviderType.OpenAiCompatible,
            IsLocal: false,
            Models: models,
            TargetUsage: ModelTargetUsage.Text,
            Preference: ModelPreference.Speed);

        var result = ModelRecommendationService.Recommend(request);
        var chat = result.Candidates.First(c => c.Model.Id == "deepseek-chat");
        var reasoner = result.Candidates.First(c => c.Model.Id == "deepseek-reasoner");
        var o1 = result.Candidates.First(c => c.Model.Id == "o1-mini");
        var qwqa = result.Candidates.First(c => c.Model.Id == "qwqa-32b");

        Assert(reasoner.IsReasoner, "deepseek-reasoner is reasoner");
        Assert(o1.IsReasoner, "o1-mini is reasoner");
        Assert(qwqa.IsReasoner, "qwqa-32b is reasoner");
        Assert(!chat.IsReasoner, "deepseek-chat is not reasoner");

        Assert(reasoner.Tier == ModelTier.Reasoner, "Tier is Reasoner");
        Assert(reasoner.Warnings.Any(w => w.Contains("思考") || w.Contains("推理") || w.Contains("降权")),
            "Reasoner must have latency warning");

        Assert(chat.Score > reasoner.Score, "deepseek-chat should score higher than reasoner in Speed mode");
    }

    private static void TestCurrentSelectedAlwaysPreserved()
    {
        var models = new List<ModelDescriptor>
        {
            new("gpt-4o-mini", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
            new("text-embedding-3-small", CapabilityState.Supported, CapabilityState.Unsupported, "Catalog"),
        };

        // Case 1: Current model is in catalog and happens to be filtered/non-generative
        var request1 = new ModelRecommendationRequest(
            ProviderType: ProviderType.OpenAiCompatible,
            IsLocal: false,
            Models: models,
            TargetUsage: ModelTargetUsage.Text,
            Preference: ModelPreference.Speed,
            CurrentModelId: "text-embedding-3-small");

        var result1 = ModelRecommendationService.Recommend(request1);
        var currentInResult = result1.Candidates.FirstOrDefault(c => c.Model.Id == "text-embedding-3-small");
        Assert(currentInResult != null, "Current selected model must be preserved in Candidates even if non-generative");
        Assert(currentInResult!.IsCurrentSelected, "IsCurrentSelected flag must be true");

        // Case 2: Current model is NOT even in the catalog list
        var request2 = new ModelRecommendationRequest(
            ProviderType: ProviderType.OpenAiCompatible,
            IsLocal: false,
            Models: [new("gpt-4o-mini", CapabilityState.Supported, CapabilityState.Supported, "Catalog")],
            TargetUsage: ModelTargetUsage.Text,
            Preference: ModelPreference.Speed,
            CurrentModelId: "legacy-custom-endpoint-model");

        var result2 = ModelRecommendationService.Recommend(request2);
        var preservedSynthetic = result2.Candidates.FirstOrDefault(c => c.Model.Id == "legacy-custom-endpoint-model");
        Assert(preservedSynthetic != null, "Configured model not in catalog must be synthetically preserved");
        Assert(preservedSynthetic!.IsCurrentSelected, "Synthetic candidate has IsCurrentSelected = true");
        Assert(result2.UserCanOverride, "UserCanOverride must be true");
    }

    private static void TestBenchmarkScopeMatchingAndRejection()
    {
        var context = new ModelBenchmarkContext(
            Endpoint: "https://api.openai.com/v1",
            PromptVersion: "popglot-translation-stream-v1",
            MachineId: "dev-machine-001");

        var validMetric = new ModelBenchmarkMetric(
            Endpoint: "https://api.openai.com/v1",
            ModelId: "gpt-4o-mini",
            PromptVersion: "popglot-translation-stream-v1",
            MachineId: "dev-machine-001",
            Timestamp: DateTimeOffset.UtcNow,
            TtftMs: 240,
            CharsPerSecond: 68.5,
            TotalDurationMs: 800,
            Success: true);

        var wrongEndpointMetric = new ModelBenchmarkMetric(
            Endpoint: "http://127.0.0.1:11434/v1",
            ModelId: "gpt-4o",
            PromptVersion: "popglot-translation-stream-v1",
            MachineId: "dev-machine-001",
            Timestamp: DateTimeOffset.UtcNow,
            TtftMs: 150,
            CharsPerSecond: 90.0,
            TotalDurationMs: 500,
            Success: true);

        var wrongMachineMetric = new ModelBenchmarkMetric(
            Endpoint: "https://api.openai.com/v1",
            ModelId: "gpt-4o",
            PromptVersion: "popglot-translation-stream-v1",
            MachineId: "other-machine-999",
            Timestamp: DateTimeOffset.UtcNow,
            TtftMs: 150,
            CharsPerSecond: 90.0,
            TotalDurationMs: 500,
            Success: true);

        var wrongPromptMetric = new ModelBenchmarkMetric(
            Endpoint: "https://api.openai.com/v1",
            ModelId: "gpt-4o",
            PromptVersion: "v0-experimental",
            MachineId: "dev-machine-001",
            Timestamp: DateTimeOffset.UtcNow,
            TtftMs: 150,
            CharsPerSecond: 90.0,
            TotalDurationMs: 500,
            Success: true);

        var failedMetric = new ModelBenchmarkMetric(
            Endpoint: "https://api.openai.com/v1",
            ModelId: "gpt-4o",
            PromptVersion: "popglot-translation-stream-v1",
            MachineId: "dev-machine-001",
            Timestamp: DateTimeOffset.UtcNow,
            TtftMs: 100,
            CharsPerSecond: 100.0,
            TotalDurationMs: 200,
            Success: false);

        var models = new List<ModelDescriptor>
        {
            new("gpt-4o-mini", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
            new("gpt-4o", CapabilityState.Supported, CapabilityState.Supported, "Catalog"),
        };

        var request = new ModelRecommendationRequest(
            ProviderType: ProviderType.OpenAiCompatible,
            IsLocal: false,
            Models: models,
            TargetUsage: ModelTargetUsage.Text,
            Preference: ModelPreference.Speed,
            BenchmarkContext: context,
            BenchmarkMetrics: [validMetric, wrongEndpointMetric, wrongMachineMetric, wrongPromptMetric, failedMetric]);

        var result = ModelRecommendationService.Recommend(request);
        var miniEval = result.Candidates.First(c => c.Model.Id == "gpt-4o-mini");
        var gpt4oEval = result.Candidates.First(c => c.Model.Id == "gpt-4o");

        // gpt-4o-mini matches context
        Assert(miniEval.BenchmarkEvidence != null, "gpt-4o-mini should adopt the valid benchmark");
        Assert(miniEval.EvidenceSources.HasFlag(RecommendationEvidenceSource.LocalBenchmark),
            "EvidenceSources must contain LocalBenchmark");
        Assert(miniEval.DetailedReasons.Any(r => r.Contains("实测基准") && r.Contains("240ms")),
            "Detailed reasons must explain benchmark match");

        // gpt-4o had mismatched endpoint/machine/prompt and failed metrics -> NONE should be adopted
        Assert(gpt4oEval.BenchmarkEvidence == null, "gpt-4o must NOT adopt mismatched benchmark data");
        Assert(!gpt4oEval.EvidenceSources.HasFlag(RecommendationEvidenceSource.LocalBenchmark),
            "EvidenceSources must NOT contain LocalBenchmark when mismatched");
    }

    private static void TestEvidenceSourcesAndReasons()
    {
        var model = new ModelDescriptor(
            Id: "gemini-1.5-flash",
            TextGeneration: CapabilityState.Supported,
            VisionInput: CapabilityState.Supported,
            CapabilitySource: "GoogleCatalog");

        var request = new ModelRecommendationRequest(
            ProviderType: ProviderType.GeminiGenerateContent,
            IsLocal: false,
            Models: [model],
            TargetUsage: ModelTargetUsage.Vision,
            Preference: ModelPreference.Speed);

        var result = ModelRecommendationService.Recommend(request);
        var eval = result.RecommendedModel!;

        Assert(eval.EvidenceSources.HasFlag(RecommendationEvidenceSource.CatalogExplicit),
            "Should have CatalogExplicit evidence for vision support");
        Assert(eval.EvidenceSources.HasFlag(RecommendationEvidenceSource.FamilyHeuristics),
            "Should have FamilyHeuristics for flash naming");
        Assert(!string.IsNullOrWhiteSpace(eval.PrimaryReason), "Primary reason must not be empty");
        Assert(eval.PrimaryReason.Contains("mini/flash/haiku") || eval.PrimaryReason.Contains("轻量"),
            "Primary reason should mention lightweight / flash");
        Assert(result.Summary.Contains("gemini-1.5-flash"), "Summary should mention recommended model ID");
        Assert(result.Summary.Contains("手动覆盖"), "Summary should remind user they can override");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"[ModelRecommendationTest] Assertion Failed: {message}");
        }
    }
}
