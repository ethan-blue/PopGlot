using System.Runtime.InteropServices;

namespace PopGlot.Windows;

// ================= Native declarations (CoreBridge.Native.cs) =================
//
// The managed side of the Rust core's C ABI: one [LibraryImport] projection per
// popglot_ffi entry point. Pure declarations only — the marshalling and
// envelope logic that calls them stays in CoreBridge.cs.
internal static partial class CoreBridge
{
    private static partial class NativeMethods
    {
        [LibraryImport(LibraryName, EntryPoint = "popglot_initialize", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint Initialize(string configDirectory);

        [LibraryImport(LibraryName, EntryPoint = "popglot_get_settings")]
        internal static partial nint GetSettings();

        [LibraryImport(LibraryName, EntryPoint = "popglot_take_startup_notice")]
        internal static partial nint TakeStartupNotice();

        [LibraryImport(LibraryName, EntryPoint = "popglot_save_settings", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint SaveSettings(string json);

        [LibraryImport(LibraryName, EntryPoint = "popglot_list_prompt_templates")]
        internal static partial nint ListPromptTemplates();

        [LibraryImport(LibraryName, EntryPoint = "popglot_get_active_prompt_template")]
        internal static partial nint GetActivePromptTemplate();

        [LibraryImport(LibraryName, EntryPoint = "popglot_set_active_prompt_template", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint SetActivePromptTemplate(string? templateId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_save_prompt_template", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint SavePromptTemplate(string templateJson);

        [LibraryImport(LibraryName, EntryPoint = "popglot_delete_prompt_template", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint DeletePromptTemplate(string templateId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_compile_prompt", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint CompilePrompt(string templateJson, string variablesJson);

        [LibraryImport(LibraryName, EntryPoint = "popglot_plan_screenshot_route")]
        internal static partial nint PlanScreenshotRoute(int localOcrAvailable, int credentialPresent);

        [LibraryImport(LibraryName, EntryPoint = "popglot_select_route", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint SelectRoute(string factsJson);

        [LibraryImport(LibraryName, EntryPoint = "popglot_test_connection_draft", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TestConnectionDraft(string draftJson, string apiKey, string? requestId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_text_draft_v1", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TranslateTextDraftV1(
            string draftJson,
            string apiKey,
            string source,
            string sourceLang,
            string targetLang,
            string? requestId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_text_task_stream_v1", StringMarshalling = StringMarshalling.Utf8)]
        internal static unsafe partial nint TextTaskStreamV1(
            string apiKey,
            string source,
            string sourceLang,
            string targetLang,
            string task,
            string requestId,
            string? preference,
            string? templateId,
            ulong templateRevision,
            delegate* unmanaged[Cdecl]<nint, int, nint, nuint, int> callback,
            nint userData);

        [LibraryImport(LibraryName, EntryPoint = "popglot_text_task_draft_stream_v1", StringMarshalling = StringMarshalling.Utf8)]
        internal static unsafe partial nint TextTaskDraftStreamV1(
            string settingsJson,
            string apiKey,
            string source,
            string sourceLang,
            string targetLang,
            string task,
            string requestId,
            string? preference,
            string? templateId,
            ulong templateRevision,
            delegate* unmanaged[Cdecl]<nint, int, nint, nuint, int> callback,
            nint userData);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_text_v3", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TranslateTextV3(
            string apiKey,
            string source,
            string? sourceLang,
            string? targetLang,
            string? requestId,
            string? preference,
            string? templateId,
            ulong templateRevision);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_text_stream_v2", StringMarshalling = StringMarshalling.Utf8)]
        internal static unsafe partial nint TranslateTextStreamV2(
            string apiKey,
            string source,
            string? sourceLang,
            string? targetLang,
            string? requestId,
            string? preference,
            string? templateId,
            ulong templateRevision,
            delegate* unmanaged[Cdecl]<nint, int, nint, nuint, int> callback,
            nint userData);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_text_draft_stream_v2", StringMarshalling = StringMarshalling.Utf8)]
        internal static unsafe partial nint TranslateTextDraftStreamV2(
            string settingsJson,
            string apiKey,
            string source,
            string? sourceLang,
            string? targetLang,
            string? requestId,
            string? preference,
            string? templateId,
            ulong templateRevision,
            delegate* unmanaged[Cdecl]<nint, int, nint, nuint, int> callback,
            nint userData);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_vision_v3", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TranslateVisionV3(
            string apiKey,
            string visionApiKey,
            string settingsJson,
            string mediaType,
            string imageBase64,
            string sourceLang,
            string targetLang,
            string requestId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_translate_vision_draft_stream_v1", StringMarshalling = StringMarshalling.Utf8)]
        internal static unsafe partial nint TranslateVisionDraftStreamV1(
            string apiKey,
            string? visionApiKey,
            string? settingsJson,
            string mediaType,
            string imageBase64,
            string? sourceLang,
            string? targetLang,
            string? requestId,
            delegate* unmanaged[Cdecl]<nint, int, nint, nuint, int> callback,
            nint userData);


        [LibraryImport(LibraryName, EntryPoint = "popglot_cancel_request", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int CancelRequest(string requestId);

        [LibraryImport(LibraryName, EntryPoint = "popglot_protect_tokens", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint ProtectTokens(string text);

        [LibraryImport(LibraryName, EntryPoint = "popglot_restore_tokens", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint RestoreTokens(string translated, string tokensJson);

        [LibraryImport(LibraryName, EntryPoint = "popglot_classify_endpoint", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint ClassifyEndpoint(string url);

        [LibraryImport(LibraryName, EntryPoint = "popglot_plan_segments", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint PlanSegments(string text, int maxSegmentChars, int maxSegments);

        [LibraryImport(LibraryName, EntryPoint = "popglot_cancel_active_request")]
        internal static partial int CancelActiveRequest();

        [LibraryImport(LibraryName, EntryPoint = "popglot_free_string")]
        internal static partial void FreeString(nint value);

        [LibraryImport(LibraryName, EntryPoint = "popglot_text_job_prepare_v1", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TextJobPrepare(string requestJson);

        [LibraryImport(LibraryName, EntryPoint = "popglot_text_job_push_v1", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TextJobPush(string jobHandle, string utf8Delta);

        [LibraryImport(LibraryName, EntryPoint = "popglot_text_job_finish_v1", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint TextJobFinish(string jobHandle, string completionState);

        [LibraryImport(LibraryName, EntryPoint = "popglot_text_job_free_v1", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial void TextJobFree(string jobHandle);
    }
}
