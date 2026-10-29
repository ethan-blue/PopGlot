using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PopGlot.Windows.Services;

internal sealed record UpdateCheckResult(
    bool Ok,
    bool UpToDate,
    string CurrentVersion,
    string? LatestVersion = null,
    string? ReleaseUrl = null,
    string? InstallerName = null,
    string? Error = null);

/// <summary>
/// 应用内更新检查（0.1.35，Level 3 的第一档：<b>只提示，不静默</b>）。
///
/// 只做一件事：读 GitHub Releases 的 latest，把「有没有新版、版本号多少、
/// 发布页在哪」如实告诉用户。下载与安装完全交给用户——本服务不下载任何
/// 文件、不替换任何文件、不触碰用户数据。
/// </summary>
internal static class UpdateService
{
    public const string RepositoryOwner = "ethan-blue";
    public const string RepositoryName = "PopGlot";

    /// <summary>GitHub Releases API：只读公开信息，不需要任何令牌。</summary>
    internal const string LatestReleaseEndpoint =
        "https://api.github.com/repos/ethan-blue/PopGlot/releases/latest";

    internal const string ReleasesPageUrl = "https://github.com/ethan-blue/PopGlot/releases/latest";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders =
        {
            // GitHub API 要求 UA；同时只接受 JSON。
            UserAgent = { new ProductInfoHeaderValue("PopGlot", CurrentVersion) },
            Accept = { new MediaTypeWithQualityHeaderValue("application/vnd.github+json") },
        },
    };

    /// <summary>Test seam: replaces the HTTP boundary. Production leaves it null.</summary>
    internal static Func<string, CancellationToken, Task<string?>>? HttpGetOverride { get; set; }

    public static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = CurrentVersion;
        try
        {
            var json = HttpGetOverride is { } getter
                ? await getter(LatestReleaseEndpoint, cancellationToken)
                : await Http.GetStringAsync(LatestReleaseEndpoint, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new UpdateCheckResult(false, true, current, Error: "没有读到发布信息。");
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString()
                : null;
            var latest = Normalize(tag);
            var releaseUrl = root.TryGetProperty("html_url", out var urlElement)
                ? urlElement.GetString()
                : ReleasesPageUrl;

            // 安装包资产名：只做展示与提示，不参与任何自动下载。
            string? installerName = null;
            if (root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("name", out var nameElement))
                    {
                        continue;
                    }
                    var name = nameElement.GetString();
                    if (name is not null && name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        installerName = name;
                        break;
                    }
                }
            }

            if (latest is null)
            {
                return new UpdateCheckResult(false, true, current, Error: "发布信息里没有可识别的版本号。");
            }

            return new UpdateCheckResult(
                true,
                !IsNewer(latest, current),
                current,
                latest,
                releaseUrl,
                installerName);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(false, true, current, Error: "检查已取消。");
        }
        catch (Exception exception)
        {
            // 更新检查是增值功能：网络不通、被墙、API 限流都不能打扰用户。
            return new UpdateCheckResult(false, true, current, Error: exception.Message);
        }
    }

    /// <summary>把 "v0.1.35" / "0.1.35" 规范成 "0.1.35"。</summary>
    internal static string? Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }
        var trimmed = tag.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// 纯数字点分段比较（忽略 -rc.1 之类的预发布后缀之外的差异）。
    /// 任一侧无法解析时保守返回 false——宁可漏报，不可谎报。
    /// </summary>
    internal static bool IsNewer(string candidate, string current)
    {
        static int[] Parts(string value)
        {
            var head = value.Split('-', 2)[0];
            var segments = head.Split('.');
            var parsed = new int[Math.Max(3, segments.Length)];
            for (var index = 0; index < segments.Length; index++)
            {
                if (int.TryParse(segments[index], out var number))
                {
                    parsed[index] = number;
                }
            }
            return parsed;
        }

        var left = Parts(candidate);
        var right = Parts(current);
        var length = Math.Max(left.Length, right.Length);
        for (var index = 0; index < length; index++)
        {
            var a = index < left.Length ? left[index] : 0;
            var b = index < right.Length ? right[index] : 0;
            if (a != b)
            {
                return a > b;
            }
        }
        return false;
    }
}
