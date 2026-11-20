using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace PopGlot.Windows.Services;

/// <summary>
/// A saved physical-pixel screen region for one-key repeat capture and translation
/// (e.g. game dialogue boxes, subtitles, HUD panels).
/// </summary>
internal sealed record FixedCaptureRegion(
    string Id,
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    int VirtualScreenX,
    int VirtualScreenY,
    int VirtualScreenWidth,
    int VirtualScreenHeight,
    string? MonitorDeviceName = null,
    double MonitorDpiScale = 1.0)
{
    public const int MaxPresetCount = 8;
    public const int MaxNameLength = 32;
    public const int MinimumSide = 6;
    public const long MaxPixels = 16_000_000;
    public const string DefaultName = "默认区域";

    [JsonIgnore]
    public Rect PixelRect => new(X, Y, Width, Height);

    [JsonIgnore]
    public Rect SavedVirtualBounds => new(VirtualScreenX, VirtualScreenY, VirtualScreenWidth, VirtualScreenHeight);

    [JsonIgnore]
    public bool HasValidGeometry =>
        Width >= MinimumSide &&
        Height >= MinimumSide &&
        (long)Width * Height <= MaxPixels;

    public string FormatBoundsSummary() => $"{Width}×{Height} @ ({X}, {Y})";

    public string FormatCompactDimensions() => $"{Width}×{Height}";
}

/// <summary>
/// Value-equality immutable list of <see cref="FixedCaptureRegion"/> presets so
/// <see cref="ShellSettings"/> retains structural record equality and direct
/// JSON serialization round-trips (including <see cref="UserDataBackupService"/>).
/// </summary>
[JsonConverter(typeof(FixedRegionPresetListJsonConverter))]
internal sealed class FixedRegionPresetList : IReadOnlyList<FixedCaptureRegion>, IEquatable<FixedRegionPresetList>
{
    public static FixedRegionPresetList Empty { get; } = new(Array.Empty<FixedCaptureRegion>());

    private readonly FixedCaptureRegion[] _items;

    public FixedRegionPresetList(IEnumerable<FixedCaptureRegion>? items)
    {
        if (items is null)
        {
            _items = Array.Empty<FixedCaptureRegion>();
            return;
        }
        _items = items.Where(static r => r is not null).ToArray();
    }

    public int Count => _items.Length;

    public FixedCaptureRegion this[int index] => _items[index];

    public IEnumerator<FixedCaptureRegion> GetEnumerator() =>
        ((IEnumerable<FixedCaptureRegion>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public List<FixedCaptureRegion> ToList() => [.. _items];

    public bool Equals(FixedRegionPresetList? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        if (other is null || _items.Length != other._items.Length)
        {
            return false;
        }
        for (var i = 0; i < _items.Length; i++)
        {
            if (!Equals(_items[i], other._items[i]))
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as FixedRegionPresetList);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_items.Length);
        for (var i = 0; i < _items.Length; i++)
        {
            hash.Add(_items[i]);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(FixedRegionPresetList? left, FixedRegionPresetList? right) =>
        Equals(left, right);

    public static bool operator !=(FixedRegionPresetList? left, FixedRegionPresetList? right) =>
        !Equals(left, right);
}

internal sealed class FixedRegionPresetListJsonConverter : JsonConverter<FixedRegionPresetList>
{
    public override FixedRegionPresetList Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return FixedRegionPresetList.Empty;
        }
        var list = JsonSerializer.Deserialize<List<FixedCaptureRegion>>(ref reader, options);
        return list is { Count: > 0 }
            ? new FixedRegionPresetList(list)
            : FixedRegionPresetList.Empty;
    }

    public override void Write(Utf8JsonWriter writer, FixedRegionPresetList value, JsonSerializerOptions options)
    {
        if (value is null || value.Count == 0)
        {
            writer.WriteStartArray();
            writer.WriteEndArray();
            return;
        }
        JsonSerializer.Serialize(writer, value.ToList(), options);
    }
}

internal enum FixedRegionResolutionStatus
{
    Exact,
    Clamped,
    OutOfBounds,
}

internal sealed record FixedRegionResolution(
    FixedRegionResolutionStatus Status,
    Rect PixelRect,
    bool WasClamped,
    string? StatusHint = null)
{
    public bool CanCapture => Status is FixedRegionResolutionStatus.Exact or FixedRegionResolutionStatus.Clamped;
}

/// <summary>
/// Pure geometry & preset resolver for fixed capture regions across multi-monitor
/// and mixed-DPI desktop layouts (raw Win32 physical pixels, zero DPI drift).
/// </summary>
internal static class FixedRegionResolver
{
    public const string ClampedStatusHint = "已适配当前屏幕";
    public const string OutOfBoundsStatusHint = "已保存的固定区域超出当前屏幕，请重新框选";

    /// <summary>
    /// Snaps a dragged selection rect in physical pixels onto exact integer bounds
    /// inside <paramref name="virtualBounds"/>, matching <see cref="ScreenCaptureService"/>
    /// 1:1 so saved coordinates have zero drift on reload and re-capture.
    /// </summary>
    public static FixedCaptureRegion CreateFromSelection(
        Rect pixelBounds,
        Rect virtualBounds,
        string? id = null,
        string? name = null,
        string? monitorDeviceName = null,
        double monitorDpiScale = 1.0)
    {
        if (!IsFiniteRect(pixelBounds) || !IsFiniteRect(virtualBounds))
        {
            throw new InvalidOperationException("固定区域坐标无效。");
        }

        var x = SafeToInt(Math.Floor(pixelBounds.X));
        var y = SafeToInt(Math.Floor(pixelBounds.Y));
        var width = SafeToInt(Math.Ceiling(pixelBounds.Width));
        var height = SafeToInt(Math.Ceiling(pixelBounds.Height));

        var vx = SafeToInt(Math.Floor(virtualBounds.X));
        var vy = SafeToInt(Math.Floor(virtualBounds.Y));
        var vw = SafeToInt(Math.Ceiling(virtualBounds.Width));
        var vh = SafeToInt(Math.Ceiling(virtualBounds.Height));

        var clampedLeft = Math.Max((long)x, vx);
        var clampedTop = Math.Max((long)y, vy);
        var clampedRight = Math.Min((long)x + width, (long)vx + vw);
        var clampedBottom = Math.Min((long)y + height, (long)vy + vh);
        var clampedWidth = (int)Math.Max(0, clampedRight - clampedLeft);
        var clampedHeight = (int)Math.Max(0, clampedBottom - clampedTop);

        if (clampedWidth < FixedCaptureRegion.MinimumSide || clampedHeight < FixedCaptureRegion.MinimumSide)
        {
            throw new InvalidOperationException(
                $"选区太小（{clampedWidth}×{clampedHeight}）。请拖出至少 {FixedCaptureRegion.MinimumSide}×{FixedCaptureRegion.MinimumSide} 像素的区域。");
        }
        if ((long)clampedWidth * clampedHeight > FixedCaptureRegion.MaxPixels)
        {
            throw new InvalidOperationException("固定区域超过 1600 万像素上限，请缩小选区。");
        }

        var cleanId = NormalizeId(id);
        var cleanName = NormalizeName(name);
        var cleanDevice = string.IsNullOrWhiteSpace(monitorDeviceName) ? null : monitorDeviceName.Trim();
        var cleanScale = SanitizeDpiScale(monitorDpiScale);

        return new FixedCaptureRegion(
            Id: cleanId,
            Name: cleanName,
            X: (int)clampedLeft,
            Y: (int)clampedTop,
            Width: clampedWidth,
            Height: clampedHeight,
            VirtualScreenX: vx,
            VirtualScreenY: vy,
            VirtualScreenWidth: vw,
            VirtualScreenHeight: vh,
            MonitorDeviceName: cleanDevice,
            MonitorDpiScale: cleanScale);
    }

    /// <summary>
    /// Resolves a saved physical-pixel region against the current virtual desktop bounds.
    /// Exact containment captures untouched coordinates; partial overhang clamps cleanly;
    /// disconnected monitors or sub-minimum intersections refuse capture.
    /// </summary>
    public static FixedRegionResolution Resolve(FixedCaptureRegion region, Rect currentVirtualBounds)
    {
        ArgumentNullException.ThrowIfNull(region);

        if (!region.HasValidGeometry || !IsFiniteRect(currentVirtualBounds))
        {
            return new FixedRegionResolution(
                FixedRegionResolutionStatus.OutOfBounds,
                Rect.Empty,
                WasClamped: false,
                StatusHint: OutOfBoundsStatusHint);
        }

        var vx = (long)SafeToInt(Math.Floor(currentVirtualBounds.X));
        var vy = (long)SafeToInt(Math.Floor(currentVirtualBounds.Y));
        var vw = (long)SafeToInt(Math.Ceiling(currentVirtualBounds.Width));
        var vh = (long)SafeToInt(Math.Ceiling(currentVirtualBounds.Height));
        if (vw < FixedCaptureRegion.MinimumSide || vh < FixedCaptureRegion.MinimumSide)
        {
            return new FixedRegionResolution(
                FixedRegionResolutionStatus.OutOfBounds,
                Rect.Empty,
                WasClamped: false,
                StatusHint: OutOfBoundsStatusHint);
        }

        var left = (long)region.X;
        var top = (long)region.Y;
        var right = left + region.Width;
        var bottom = top + region.Height;
        var vRight = vx + vw;
        var vBottom = vy + vh;

        if (left >= vx && top >= vy && right <= vRight && bottom <= vBottom)
        {
            return new FixedRegionResolution(
                FixedRegionResolutionStatus.Exact,
                new Rect(region.X, region.Y, region.Width, region.Height),
                WasClamped: false,
                StatusHint: null);
        }

        var clampedLeft = Math.Max(left, vx);
        var clampedTop = Math.Max(top, vy);
        var clampedRight = Math.Min(right, vRight);
        var clampedBottom = Math.Min(bottom, vBottom);
        var clampedWidth = clampedRight - clampedLeft;
        var clampedHeight = clampedBottom - clampedTop;

        if (clampedWidth < FixedCaptureRegion.MinimumSide ||
            clampedHeight < FixedCaptureRegion.MinimumSide ||
            clampedWidth * clampedHeight > FixedCaptureRegion.MaxPixels)
        {
            return new FixedRegionResolution(
                FixedRegionResolutionStatus.OutOfBounds,
                Rect.Empty,
                WasClamped: false,
                StatusHint: OutOfBoundsStatusHint);
        }

        return new FixedRegionResolution(
            FixedRegionResolutionStatus.Clamped,
            new Rect(clampedLeft, clampedTop, clampedWidth, clampedHeight),
            WasClamped: true,
            StatusHint: ClampedStatusHint);
    }

    /// <summary>
    /// True when two physical-pixel rectangles have positive area overlap
    /// (touching edges alone do not occlude the capture region).
    /// </summary>
    public static bool OverlapsWindow(Rect regionPixels, Rect windowPixels)
    {
        if (!IsFiniteRect(regionPixels) || !IsFiniteRect(windowPixels))
        {
            return false;
        }
        if (regionPixels.Width <= 0 || regionPixels.Height <= 0 ||
            windowPixels.Width <= 0 || windowPixels.Height <= 0)
        {
            return false;
        }
        return regionPixels.Left < windowPixels.Right &&
               regionPixels.Right > windowPixels.Left &&
               regionPixels.Top < windowPixels.Bottom &&
               regionPixels.Bottom > windowPixels.Top;
    }

    /// <summary>
    /// Drops invalid regions, sanitizes DPI scale/names/IDs, deduplicates IDs,
    /// caps preset count at <see cref="FixedCaptureRegion.MaxPresetCount"/>, and
    /// coalesces <paramref name="activeId"/> to a valid entry or null.
    /// </summary>
    public static (FixedRegionPresetList Regions, string? ActiveId) Normalize(
        IEnumerable<FixedCaptureRegion>? regions,
        string? activeId)
    {
        if (regions is null)
        {
            return (FixedRegionPresetList.Empty, null);
        }

        var list = new List<FixedCaptureRegion>(FixedCaptureRegion.MaxPresetCount);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in regions)
        {
            if (raw is null || !raw.HasValidGeometry)
            {
                continue;
            }
            if (list.Count >= FixedCaptureRegion.MaxPresetCount)
            {
                break;
            }

            var cleanId = NormalizeId(raw.Id);
            if (!seenIds.Add(cleanId))
            {
                continue;
            }

            var cleanName = NormalizeName(raw.Name);
            var cleanScale = SanitizeDpiScale(raw.MonitorDpiScale);
            var cleanDevice = string.IsNullOrWhiteSpace(raw.MonitorDeviceName) ? null : raw.MonitorDeviceName.Trim();

            var sanitized = raw with
            {
                Id = cleanId,
                Name = cleanName,
                MonitorDeviceName = cleanDevice,
                MonitorDpiScale = cleanScale,
            };
            list.Add(sanitized);
        }

        if (list.Count == 0)
        {
            return (FixedRegionPresetList.Empty, null);
        }

        var presetList = new FixedRegionPresetList(list);
        var resolvedActiveId = Select(presetList, activeId);
        return (presetList, resolvedActiveId);
    }

    public static (FixedRegionPresetList Regions, string ActiveId) Upsert(
        FixedRegionPresetList? existing,
        FixedCaptureRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (!region.HasValidGeometry)
        {
            throw new InvalidOperationException("固定区域尺寸无效，无法保存。");
        }

        var (normalizedExisting, _) = Normalize(existing, null);
        var items = normalizedExisting.ToList();
        var cleanId = NormalizeId(region.Id);
        var sanitized = region with
        {
            Id = cleanId,
            Name = NormalizeName(region.Name),
            MonitorDeviceName = string.IsNullOrWhiteSpace(region.MonitorDeviceName) ? null : region.MonitorDeviceName.Trim(),
            MonitorDpiScale = SanitizeDpiScale(region.MonitorDpiScale),
        };

        var existingIndex = items.FindIndex(r => string.Equals(r.Id, cleanId, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            items[existingIndex] = sanitized;
        }
        else
        {
            if (items.Count >= FixedCaptureRegion.MaxPresetCount)
            {
                throw new InvalidOperationException($"最多只能保存 {FixedCaptureRegion.MaxPresetCount} 个固定区域预设。");
            }
            items.Add(sanitized);
        }

        var (finalList, finalActive) = Normalize(items, cleanId);
        return (finalList, finalActive!);
    }

    public static (FixedRegionPresetList Regions, string? ActiveId) Remove(
        FixedRegionPresetList? existing,
        string? regionId,
        string? currentActiveId)
    {
        var (normalizedExisting, _) = Normalize(existing, currentActiveId);
        if (normalizedExisting.Count == 0 || string.IsNullOrWhiteSpace(regionId))
        {
            return (normalizedExisting, Select(normalizedExisting, currentActiveId));
        }

        var remaining = normalizedExisting
            .Where(r => !string.Equals(r.Id, regionId.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Normalize(remaining, currentActiveId);
    }

    public static string? Select(FixedRegionPresetList? existing, string? regionId)
    {
        if (existing is null || existing.Count == 0)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(regionId))
        {
            var trimmed = regionId.Trim();
            for (var i = 0; i < existing.Count; i++)
            {
                if (string.Equals(existing[i].Id, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return existing[i].Id;
                }
            }
        }
        return existing[0].Id;
    }

    public static string GenerateNextPresetName(IReadOnlyList<FixedCaptureRegion>? existing)
    {
        if (existing is null || existing.Count == 0)
        {
            return FixedCaptureRegion.DefaultName;
        }

        var used = new HashSet<string>(
            existing.Select(static r => r.Name),
            StringComparer.OrdinalIgnoreCase);

        if (!used.Contains(FixedCaptureRegion.DefaultName))
        {
            return FixedCaptureRegion.DefaultName;
        }

        for (var i = 2; i <= 99; i++)
        {
            var candidate = $"区域 {i}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
        return $"区域 {existing.Count + 1}";
    }

    public static string NormalizeName(string? name, string fallback = FixedCaptureRegion.DefaultName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }
        var cleaned = new string(name.Trim().Select(static c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            return fallback;
        }
        return cleaned.Length <= FixedCaptureRegion.MaxNameLength
            ? cleaned
            : cleaned[..FixedCaptureRegion.MaxNameLength].TrimEnd();
    }

    private static string NormalizeId(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? Guid.NewGuid().ToString("N")[..12]
            : id.Trim();

    private static double SanitizeDpiScale(double scale) =>
        double.IsFinite(scale) && scale >= 0.25 && scale <= 10.0 ? scale : 1.0;

    private static bool IsFiniteRect(Rect rect) =>
        !rect.IsEmpty &&
        double.IsFinite(rect.X) &&
        double.IsFinite(rect.Y) &&
        double.IsFinite(rect.Width) &&
        double.IsFinite(rect.Height);

    private static int SafeToInt(double value) =>
        double.IsFinite(value)
            ? (int)Math.Clamp(value, int.MinValue, int.MaxValue)
            : throw new InvalidOperationException("固定区域坐标无效。");
}
