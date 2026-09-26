using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PopGlot.Windows.Services;

/// <summary>
/// R07: Bounded, allowlist-only diagnostics exporter.
/// Exports only strictly non-sensitive metadata: AppVersion, OsVersion, Timestamp,
/// ErrorCode, Stage, Protocol, SanitizedRoute, ElapsedMs.
/// Absolutely excludes: source text, translated text, screenshots, tokens, headers, keys, full query URLs.
/// Default 0 network calls.
/// </summary>
internal static class SafeDiagnosticsExporter
{
    public static readonly IReadOnlyList<string> WhitelistedFields = new[]
    {
        "app_version",
        "os_version",
        "timestamp_utc",
        "error_code",
        "stage",
        "protocol",
        "sanitized_route",
        "elapsed_ms"
    };

    public sealed record SafeDiagnosticRecord(
        [property: JsonPropertyName("event_id")] string EventId,
        [property: JsonPropertyName("stage")] string Stage,
        [property: JsonPropertyName("error_code")] string ErrorCode,
        [property: JsonPropertyName("protocol")] string Protocol,
        [property: JsonPropertyName("sanitized_route")] string SanitizedRoute,
        [property: JsonPropertyName("elapsed_ms")] ulong ElapsedMs,
        [property: JsonPropertyName("timestamp_utc")] DateTime TimestampUtc);

    public sealed record SafeDiagnosticsPackage(
        [property: JsonPropertyName("app_version")] string AppVersion,
        [property: JsonPropertyName("os_version")] string OsVersion,
        [property: JsonPropertyName("timestamp_utc")] DateTime TimestampUtc,
        [property: JsonPropertyName("whitelisted_fields")] IReadOnlyList<string> WhitelistedFields,
        [property: JsonPropertyName("records")] IReadOnlyList<SafeDiagnosticRecord> Records,
        [property: JsonPropertyName("zero_network_export")] bool ZeroNetworkExport = true);

    public static string SanitizeRoute(ProviderType providerType, string? modelName)
    {
        var safeModel = string.IsNullOrWhiteSpace(modelName)
            ? "default"
            : DiagnosticsLog.Sanitize(modelName.Trim());
        return $"{providerType} ({safeModel})";
    }

    public static SafeDiagnosticsPackage CreatePackage(
        IEnumerable<SafeDiagnosticRecord>? records = null,
        string? appVersion = null)
    {
        var version = appVersion ?? "0.1.10";
        var os = Environment.OSVersion.ToString();
        var recordList = records?.ToList() ?? new List<SafeDiagnosticRecord>();

        return new SafeDiagnosticsPackage(
            AppVersion: version,
            OsVersion: os,
            TimestampUtc: DateTime.UtcNow,
            WhitelistedFields: WhitelistedFields,
            Records: recordList,
            ZeroNetworkExport: true);
    }

    public static SafeDiagnosticRecord CreateRecord(
        string eventId,
        string stage,
        string errorCode,
        string protocol,
        string sanitizedRoute,
        ulong elapsedMs,
        DateTime? timestamp = null)
    {
        // Sanitize every field as defense-in-depth
        var cleanEventId = DiagnosticsLog.Sanitize(eventId);
        var cleanStage = DiagnosticsLog.Sanitize(stage);
        var cleanErrorCode = DiagnosticsLog.Sanitize(errorCode);
        var cleanProtocol = DiagnosticsLog.Sanitize(protocol);
        var cleanRoute = DiagnosticsLog.Sanitize(sanitizedRoute);

        return new SafeDiagnosticRecord(
            EventId: cleanEventId,
            Stage: cleanStage,
            ErrorCode: cleanErrorCode,
            Protocol: cleanProtocol,
            SanitizedRoute: cleanRoute,
            ElapsedMs: elapsedMs,
            TimestampUtc: timestamp ?? DateTime.UtcNow);
    }

    public static string FormatJson(SafeDiagnosticsPackage package)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return JsonSerializer.Serialize(package, options);
    }

    public static bool TryExportToFile(SafeDiagnosticsPackage package, string destinationPath, out string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = FormatJson(package);
            File.WriteAllText(destinationPath, json, System.Text.Encoding.UTF8);
            message = $"诊断包已安全导出至：{destinationPath}";
            return true;
        }
        catch (Exception ex)
        {
            message = $"导出诊断失败：{ex.Message}";
            return false;
        }
    }
}
