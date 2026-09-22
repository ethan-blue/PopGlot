using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Windows.Input;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

internal enum HotkeyAction
{
    TranslateSelection,
    CaptureScreen,
    ClosePanel,
    ShowWindow,
    QuickSearch,
}

internal enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>
/// One global hotkey, stored as a portable "Ctrl+Alt+W" string.
/// </summary>
/// <remarks>
/// The previous build offered a fixed list of six combinations, so a user whose
/// combination was taken by another app had no way out. Any modifier + key
/// combination is accepted now, validated the same way Windows validates it.
/// </remarks>
internal sealed record HotkeyBinding(uint Modifiers, uint VirtualKey)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    public static HotkeyBinding SelectionDefault => new(ModControl | ModAlt, 0x57); // Ctrl+Alt+W
    public static HotkeyBinding ScreenshotDefault => new(ModControl | ModAlt, 0x20); // Ctrl+Alt+Space
    public static HotkeyBinding CloseDefault => new(ModControl | ModAlt, 0x58); // Ctrl+Alt+X
    public static HotkeyBinding ShowWindowDefault => new(ModControl | ModAlt, 0x4F); // Ctrl+Alt+O
    public static HotkeyBinding QuickSearchDefault => new(ModControl | ModAlt, 0x51); // Ctrl+Alt+Q

    public string DisplayName
    {
        get
        {
            // Windows writes its own shortcuts Win-first ("Win+Shift+S"), so
            // that order is what users expect to read back.
            var parts = new List<string>(4);
            if ((Modifiers & ModWin) != 0) parts.Add("Win");
            if ((Modifiers & ModControl) != 0) parts.Add("Ctrl");
            if ((Modifiers & ModAlt) != 0) parts.Add("Alt");
            if ((Modifiers & ModShift) != 0) parts.Add("Shift");
            parts.Add(KeyName(VirtualKey));
            return string.Join("+", parts);
        }
    }

    /// <summary>A combination Windows will actually hand back to us.</summary>
    /// <remarks>
    /// Bare keys and Shift-only combinations would swallow ordinary typing
    /// system-wide, so at least one of Ctrl/Alt/Win is required.
    /// </remarks>
    public bool IsValid =>
        (Modifiers & (ModControl | ModAlt | ModWin)) != 0 &&
        VirtualKey != 0 &&
        !IsModifierKey(VirtualKey);

    public static bool IsModifierKey(uint virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C or // Shift, Ctrl, Alt, LWin, RWin
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private static string KeyName(uint virtualKey) => virtualKey switch
    {
        0x20 => "Space",
        0x0D => "Enter",
        0x09 => "Tab",
        0x1B => "Esc",
        0xBC => ",",
        0xBE => ".",
        0xBF => "/",
        0xBA => ";",
        0xDE => "'",
        0xDB => "[",
        0xDD => "]",
        0xDC => "\\",
        0xBD => "-",
        0xBB => "=",
        0xC0 => "`",
        _ => FriendlyKeyName(virtualKey),
    };

    private static string FriendlyKeyName(uint virtualKey)
    {
        var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);
        var name = key.ToString();
        // WPF spells the digit row `D0`..`D9`, which reads as nonsense in a
        // shortcut label.
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
        {
            return name[1..];
        }
        return name;
    }

    /// <summary>
    /// Parses any user-typed or persisted shortcut string. Accepts both
    /// the new readable syntax ("Ctrl+Alt+W") and the legacy preset ids
    /// ("ctrl-alt-w", "ctrl-shift-t", etc.).
    /// </summary>
    public static HotkeyBinding Parse(string? input, HotkeyBinding fallback)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return fallback;
        }

        var text = input.Trim();
        var fromLegacy = ParseLegacyId(text);
        if (fromLegacy is not null)
        {
            return fromLegacy;
        }

        var tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2)
        {
            return fallback;
        }

        uint modifiers = 0;
        uint virtualKey = 0;
        foreach (var raw in tokens)
        {
            var token = raw.ToLowerInvariant();
            switch (token)
            {
                case "ctrl" or "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win" or "windows" or "super":
                    modifiers |= ModWin;
                    break;
                default:
                    virtualKey = ParseKeyName(raw);
                    break;
            }
        }

        var candidate = new HotkeyBinding(modifiers, virtualKey);
        return candidate.IsValid ? candidate : fallback;
    }

    private static uint ParseKeyName(string name)
    {
        var upper = name.Trim().ToUpperInvariant();
        if (upper.Length == 1)
        {
            var ch = upper[0];
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return ch;
            }
            return ch switch
            {
                ' ' => 0x20,
                ',' => 0xBC,
                '.' => 0xBE,
                '/' => 0xBF,
                ';' => 0xBA,
                '\'' => 0xDE,
                '[' => 0xDB,
                ']' => 0xDD,
                '\\' => 0xDC,
                '-' => 0xBD,
                '=' => 0xBB,
                '`' => 0xC0,
                _ => 0,
            };
        }

        return upper switch
        {
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            _ => Enum.TryParse<Key>(upper, ignoreCase: true, out var wpfKey)
                ? (uint)KeyInterop.VirtualKeyFromKey(wpfKey)
                : 0,
        };
    }

    private static HotkeyBinding? ParseLegacyId(string id) => id.ToLowerInvariant() switch
    {
        "ctrl-alt-w" => SelectionDefault,
        "ctrl-alt-space" => ScreenshotDefault,
        "ctrl-alt-x" => CloseDefault,
        "ctrl-alt-o" => ShowWindowDefault,
        "ctrl-alt-q" => QuickSearchDefault,
        "ctrl-shift-f" => new(ModControl | ModShift, 0x46),
        "ctrl-shift-t" => new(ModControl | ModShift, 0x54),
        "ctrl-shift-x" => new(ModControl | ModShift, 0x58),
        _ => null,
    };

    public string Serialize() => DisplayName;
}

internal sealed record ShellSettings(
    int SchemaVersion,
    HotkeyBinding SelectionHotkey,
    HotkeyBinding ScreenshotHotkey,
    HotkeyBinding CloseHotkey,
    bool HistoryEnabled,
    ThemePreference Theme,
    // C05/F08: default OFF — focus loss never destroys a panel session;
    // users who want auto-hide opt in explicitly.
    bool ClosePanelOnFocusLoss = false,
    bool CopyTranslationAutomatically = false,
    bool StartWithWindows = false,
    HotkeyBinding? ShowWindowHotkey = null,
    FreeEngineConsent FreeEngineConsent = FreeEngineConsent.Unset,
    bool CloseHintShown = false,
    bool CloudSpeechEnabled = false,
    bool CloseMainWindowToTray = true,
    // 首次引导闸门：只有全新安装（从未有过设置文件）才是 false。升级与
    // 一键同意一样活在表单之外，任何设置保存都不得把老用户拉回引导。
    bool HasCompletedOnboarding = false,
    HotkeyBinding? QuickSearchHotkey = null,
    // Which public text service the free engine may contact. Missing on
    // older files means Google, the only choice those builds had.
    FreeEngineProvider FreeEngineProvider = FreeEngineProvider.Google)
{
    public HotkeyBinding QuickSearchHotkey { get; init; } =
        QuickSearchHotkey ?? HotkeyBinding.QuickSearchDefault;

    public const int CurrentSchemaVersion = 3;

    public static ShellSettings Default => new(
        CurrentSchemaVersion,
        HotkeyBinding.SelectionDefault,
        HotkeyBinding.ScreenshotDefault,
        HotkeyBinding.CloseDefault,
        HistoryEnabled: true,
        ThemePreference.System,
        ClosePanelOnFocusLoss: false,
        CopyTranslationAutomatically: false,
        StartWithWindows: false,
        ShowWindowHotkey: HotkeyBinding.ShowWindowDefault,
        FreeEngineConsent: FreeEngineConsent.Unset,
        CloseHintShown: false,
        CloudSpeechEnabled: false,
        CloseMainWindowToTray: true,
        HasCompletedOnboarding: false,
        QuickSearchHotkey: HotkeyBinding.QuickSearchDefault);

    public IReadOnlyDictionary<HotkeyAction, HotkeyBinding> Hotkeys
    {
        get
        {
            var dict = new Dictionary<HotkeyAction, HotkeyBinding>
            {
                [HotkeyAction.TranslateSelection] = SelectionHotkey,
                [HotkeyAction.CaptureScreen] = ScreenshotHotkey,
                [HotkeyAction.ClosePanel] = CloseHotkey,
                [HotkeyAction.QuickSearch] = QuickSearchHotkey,
            };
            if (ShowWindowHotkey is not null && ShowWindowHotkey.IsValid)
            {
                dict[HotkeyAction.ShowWindow] = ShowWindowHotkey;
            }
            return dict;
        }
    }

    /// <summary>Returns the first problem with this set, or null when usable.</summary>
    public string? ValidateHotkeys()
    {
        foreach (var (action, binding) in Hotkeys)
        {
            if (!binding.IsValid)
            {
                return $"{ActionName(action)}的快捷键“{binding.DisplayName}”无效：至少需要包含 Ctrl、Alt 或 Win，并搭配一个普通按键。";
            }
        }

        var duplicate = Hotkeys
            .GroupBy(pair => pair.Value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        return duplicate is null
            ? null
            : $"快捷键 {duplicate.Key} 被重复用于{string.Join("、", duplicate.Select(pair => ActionName(pair.Key)))}。";
    }

    internal static string ActionName(HotkeyAction action) => action switch
    {
        HotkeyAction.TranslateSelection => "划词翻译",
        HotkeyAction.CaptureScreen => "截图翻译",
        HotkeyAction.ClosePanel => "关闭浮窗",
        HotkeyAction.ShowWindow => "打开主窗口",
        HotkeyAction.QuickSearch => "极速查词",
        _ => action.ToString(),
    };
}

internal static class ShellSettingsStore
{
    // Resolve lazily so an isolated/test data root installed before use cannot
    // be bypassed by an eager beforefieldinit static initializer.
    private static string DefaultSettingsPath => StoragePaths.ShellSettings;

    private static readonly object CacheLock = new();
    private static ShellSettings? _cachedSettings;
    private static string? _cachedSettingsPath;
    private static DateTime _cachedLastWriteUtc;
    private static long _cachedLength = -1;
    private static byte[]? _cachedHash;

    internal static ShellSettings? CurrentCachedSettings
    {
        get
        {
            lock (CacheLock)
            {
                return _cachedSettings;
            }
        }
    }

    /// <summary>Test seam: clears cached settings snapshot.</summary>
    internal static void InvalidateCache()
    {
        lock (CacheLock)
        {
            _cachedSettings = null;
            _cachedSettingsPath = null;
            _cachedLastWriteUtc = default;
            _cachedLength = -1;
            _cachedHash = null;
        }
    }

    /// <summary>
    /// Test seam: reports the cached snapshot for one path without touching
    /// disk, so tests can verify that failure paths never poison the cache.
    /// </summary>
    internal static bool TryPeekCacheForTest(string path, out ShellSettings? settings, out DateTime lastWriteUtc)
    {
        lock (CacheLock)
        {
            if (_cachedSettings is not null &&
                string.Equals(_cachedSettingsPath, path, StringComparison.OrdinalIgnoreCase))
            {
                settings = _cachedSettings;
                lastWriteUtc = _cachedLastWriteUtc;
                return true;
            }
        }
        settings = null;
        lastWriteUtc = default;
        return false;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // PopGlot writes plain UTF-8 with no BOM; combined with the
    // flush-to-disk + atomic move below, every persisted file is complete.
    // Files written by other editors may still carry a UTF-8 BOM, which
    // Load tolerates (see the deserialization input there).
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static ShellSettings Load(string? settingsPath = null)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        try
        {
            if (!File.Exists(path))
            {
                var def = ShellSettings.Default;
                lock (CacheLock)
                {
                    _cachedSettings = def;
                    _cachedSettingsPath = path;
                    _cachedLastWriteUtc = default;
                    _cachedLength = -1;
                    _cachedHash = null;
                }
                return def;
            }

            var fileInfo = new FileInfo(path);
            var lastWrite = fileInfo.LastWriteTimeUtc;
            var bytes = File.ReadAllBytes(path);
            var length = bytes.LongLength;
            var hash = SHA256.HashData(bytes);
            lock (CacheLock)
            {
                if (_cachedSettings is not null &&
                    string.Equals(_cachedSettingsPath, path, StringComparison.OrdinalIgnoreCase) &&
                    _cachedLastWriteUtc == lastWrite &&
                    _cachedLength == length &&
                    _cachedHash is not null &&
                    hash.AsSpan().SequenceEqual(_cachedHash))
                {
                    return _cachedSettings;
                }
            }

            // JsonSerializer rejects a BOM and only reads UTF-8, but the old
            // File.ReadAllText pipeline autodetected UTF-8/UTF-16 BOMs written
            // by external editors. An exotic prefix used to fail the whole Load
            // into the fallback and silently reset the user's hotkeys/theme.
            // The decoding below shapes the deserialization input ONLY: length
            // + hash above are computed over the full original bytes, so cache
            // identity is unchanged. Malformed payloads still throw inside the
            // deserializer and fail closed through the same catch.
            var jsonBytes = DecodeJsonPayloadForDeserialization(bytes);
            var persisted = JsonSerializer.Deserialize<PersistedShellSettings>(jsonBytes, JsonOptions);
            if (persisted is null)
            {
                var def = ShellSettings.Default;
                lock (CacheLock)
                {
                    _cachedSettings = def;
                    _cachedSettingsPath = path;
                    _cachedLastWriteUtc = lastWrite;
                    _cachedLength = length;
                    _cachedHash = hash;
                }
                return def;
            }

            var defaults = ShellSettings.Default;
            var result = new ShellSettings(
                ShellSettings.CurrentSchemaVersion,
                // v1 had a single `ShortcutId`; v2 split it into three ids;
                // v3 stores readable combinations. All three parse here.
                HotkeyBinding.Parse(
                    persisted.SelectionHotkey ?? persisted.SelectionShortcutId,
                    defaults.SelectionHotkey),
                HotkeyBinding.Parse(
                    persisted.ScreenshotHotkey ?? persisted.ScreenshotShortcutId ?? persisted.ShortcutId,
                    defaults.ScreenshotHotkey),
                HotkeyBinding.Parse(
                    persisted.CloseHotkey ?? persisted.CloseShortcutId,
                    defaults.CloseHotkey),
                persisted.HistoryEnabled ?? (persisted.SchemaVersion is not null && persisted.SchemaVersion >= 3 ? defaults.HistoryEnabled : false),
                persisted.Theme ?? defaults.Theme,
                persisted.ClosePanelOnFocusLoss ?? defaults.ClosePanelOnFocusLoss,
                persisted.CopyTranslationAutomatically ?? defaults.CopyTranslationAutomatically,
                persisted.StartWithWindows ?? defaults.StartWithWindows,
                persisted.ShowWindowHotkey is not null
                    ? HotkeyBinding.Parse(persisted.ShowWindowHotkey, defaults.ShowWindowHotkey ?? HotkeyBinding.ShowWindowDefault)
                    : defaults.ShowWindowHotkey,
                persisted.FreeEngineConsent is not null
                    ? Enum.TryParse<FreeEngineConsent>(persisted.FreeEngineConsent, ignoreCase: true, out var consent)
                        ? consent
                        : FreeEngineConsent.Unset
                    : FreeEngineConsent.Unset,
                persisted.CloseHintShown ?? false,
                // Cloud speech (Microsoft voice service) is an independent
                // consent that upgrades never grant implicitly.
                persisted.CloudSpeechEnabled ?? false,
                persisted.CloseMainWindowToTray ?? defaults.CloseMainWindowToTray,
                // 旧配置兼容：该字段出现之前的既有配置文件一律视为已完成引导，
                // 升级路径永远不会给老用户弹首次引导；只有 Default（无文件）
                // 才是 false，全新安装才进入引导。
                persisted.HasCompletedOnboarding ?? true,
                persisted.QuickSearchHotkey is not null
                    ? HotkeyBinding.Parse(persisted.QuickSearchHotkey, defaults.QuickSearchHotkey)
                    : defaults.QuickSearchHotkey,
                persisted.FreeEngineProvider is not null &&
                Enum.TryParse<FreeEngineProvider>(persisted.FreeEngineProvider, ignoreCase: true, out var freeProvider)
                    ? freeProvider
                    : FreeEngineProvider.Google);

            lock (CacheLock)
            {
                _cachedSettings = result;
                _cachedSettingsPath = path;
                _cachedLastWriteUtc = lastWrite;
                _cachedLength = length;
                _cachedHash = hash;
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A transient failure (file locked, JSON mid-write, momentary
            // access denial) is a fallback, not a snapshot of the file.
            // Caching Default here used to make the failure sticky: the
            // poisoned entry evicted the last-known-good settings, later
            // Loads answered from it without re-reading, and a Save built on
            // those defaults overwrote the user's real settings. Return the
            // defaults but keep the previous cache entry untouched — the
            // timestamp guard makes every subsequent Load retry the file.
            // (Only the missing-file branch above may cache Default: there
            // the file provably does not exist yet.)
            return ShellSettings.Default;
        }
    }

    /// <summary>
    /// Maps the raw file bytes to the UTF-8 payload the JSON deserializer
    /// consumes. Supported: UTF-8 without BOM (what PopGlot writes), UTF-8
    /// BOM, UTF-16 LE (FF FE) and UTF-16 BE (FE FF) — the encodings the
    /// previous <c>File.ReadAllText</c> pipeline autodetected. The UTF-16
    /// forms are decoded and re-encoded as UTF-8 for the deserializer only;
    /// cache timestamp/length/SHA-256 always stay computed over the FULL
    /// original bytes by the caller.
    /// </summary>
    private static byte[] DecodeJsonPayloadForDeserialization(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return bytes[3..]; // UTF-8 BOM: the payload itself is already UTF-8.
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.UTF8.GetBytes(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.UTF8.GetBytes(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2));
        }
        return bytes;
    }

    public static void Save(ShellSettings settings, string? settingsPath = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validationError = settings.ValidateHotkeys();
        if (validationError is not null)
        {
            throw new InvalidOperationException(validationError);
        }

        var path = settingsPath ?? DefaultSettingsPath;
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Unable to resolve the PopGlot settings directory.");
        Directory.CreateDirectory(directory);

        var persisted = new PersistedShellSettings(
            ShellSettings.CurrentSchemaVersion,
            ShortcutId: null,
            SelectionShortcutId: null,
            ScreenshotShortcutId: null,
            CloseShortcutId: null,
            settings.SelectionHotkey.Serialize(),
            settings.ScreenshotHotkey.Serialize(),
            settings.CloseHotkey.Serialize(),
            settings.HistoryEnabled,
            settings.Theme,
            settings.ClosePanelOnFocusLoss,
            settings.CopyTranslationAutomatically,
            settings.StartWithWindows,
            settings.ShowWindowHotkey?.Serialize(),
            settings.FreeEngineConsent.ToString(),
            settings.CloseHintShown,
            settings.CloudSpeechEnabled,
            settings.CloseMainWindowToTray,
            settings.HasCompletedOnboarding,
            settings.QuickSearchHotkey.Serialize(),
            settings.FreeEngineProvider.ToString());

        // Write through a temporary file so a crash mid-write cannot leave the
        // user without settings on the next launch: exclusive-access write,
        // flush all the way to disk, then an atomic swap into place.
        var temporaryPath = path + ".tmp";
        var payload = Utf8NoBom.GetBytes(JsonSerializer.Serialize(persisted, JsonOptions));
        using (var stream = new FileStream(
            temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(payload, 0, payload.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, path, overwrite: true);

        var lastWrite = File.GetLastWriteTimeUtc(path);
        var hash = SHA256.HashData(payload);
        lock (CacheLock)
        {
            _cachedSettings = settings;
            _cachedSettingsPath = path;
            _cachedLastWriteUtc = lastWrite;
            _cachedLength = payload.LongLength;
            _cachedHash = hash;
        }
    }

    private sealed record PersistedShellSettings(
        int? SchemaVersion,
        string? ShortcutId,
        string? SelectionShortcutId,
        string? ScreenshotShortcutId,
        string? CloseShortcutId,
        string? SelectionHotkey,
        string? ScreenshotHotkey,
        string? CloseHotkey,
        bool? HistoryEnabled,
        ThemePreference? Theme,
        bool? ClosePanelOnFocusLoss,
        bool? CopyTranslationAutomatically,
        bool? StartWithWindows,
        string? ShowWindowHotkey = null,
        string? FreeEngineConsent = null,
        bool? CloseHintShown = null,
        bool? CloudSpeechEnabled = null,
        bool? CloseMainWindowToTray = null,
        bool? HasCompletedOnboarding = null,
        string? QuickSearchHotkey = null,
        string? FreeEngineProvider = null);
}
