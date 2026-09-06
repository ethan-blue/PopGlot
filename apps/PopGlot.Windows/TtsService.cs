using System.IO;
using System.Windows;
using System.Windows.Media;
using Windows.Media.SpeechSynthesis;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

/// <summary>
/// Speaks source or translated text with natural neural Edge voices, falling back to Windows SAPI/WinRT.
/// </summary>
internal static class TtsService
{
    private static readonly Lock Gate = new();
    private static MediaPlayer? _player;
    private static string? _currentFile;
    private static int _generation;
    private static CancellationTokenSource? _synthesisCts;

    /// <summary>Test seam: overrides settings lookup for online TTS gating.</summary>
    internal static Func<ProviderSettings>? SettingsResolver { get; set; }

    /// <summary>Test seam: overrides shell settings lookup (cloud speech consent).</summary>
    internal static Func<ShellSettings>? ShellSettingsResolver { get; set; }

    /// <summary>
    /// Test seam: overrides Edge neural TTS synthesis. Arguments are the text,
    /// the resolved voice and the cancellation token owned by TtsService.
    /// </summary>
    internal static Func<string, string?, CancellationToken, Task<string?>>? EdgeSynthesizer { get; set; }

    /// <summary>Test seam: overrides local offline synthesis. May return null to skip playback.</summary>
    internal static Func<string, Task<string?>>? LocalSynthesizer { get; set; }

    /// <summary>Raised when speech synthesis begins or ends.</summary>
    public static event EventHandler<bool>? SpeakingStateChanged;

    /// <summary>True while an utterance is playing.</summary>
    public static bool IsSpeaking
    {
        get
        {
            lock (Gate)
            {
                return _player is not null;
            }
        }
    }

    /// <summary>
    /// Speaks <paramref name="text"/> in <paramref name="languageTag"/>,
    /// replacing any current utterance. The cloud voice service needs its own
    /// explicit consent — the translation network permission alone never
    /// authorises sending text to Microsoft speech.
    /// </summary>
    public static void Speak(string? text, string? languageTag = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Stop();
        int generation;
        CancellationTokenSource synthesisCts;
        lock (Gate)
        {
            generation = ++_generation;
            synthesisCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _synthesisCts = synthesisCts;
        }

        _ = Task.Run(async () =>
        {
            string? path = null;
            try
            {
                var settings = SettingsResolver?.Invoke() ?? CoreBridge.GetSettings();
                var shellSettings = ShellSettingsResolver?.Invoke() ?? ShellSettingsStore.Load();
                var networkAllowed = settings.NetworkEnabled && !settings.SafeDevMode;
                // The Microsoft voice service is a separate destination with a
                // separate consent; upgrading never grants it implicitly.
                var cloudSpeechAllowed = networkAllowed && shellSettings.CloudSpeechEnabled;

                if (cloudSpeechAllowed)
                {
                    try
                    {
                        var voice = EdgeTtsService.ResolveVoice(languageTag, text);
                        path = EdgeSynthesizer is not null
                            ? await EdgeSynthesizer(text, voice, synthesisCts.Token)
                            : await EdgeTtsService.SynthesizeToMp3FileAsync(text, voice, synthesisCts.Token);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // Fall back to offline Windows Speech Synthesis.
                        path = LocalSynthesizer is not null
                            ? await LocalSynthesizer(text)
                            : await SynthesizeLocalToFileAsync(text);
                    }
                }
                else
                {
                    // Offline, safe mode, or cloud speech not consented:
                    // strictly local synthesis, never touch the network.
                    path = LocalSynthesizer is not null
                        ? await LocalSynthesizer(text)
                        : await SynthesizeLocalToFileAsync(text, languageTag);
                }

                if (path is not null)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => StartPlayback(path, generation));
                }
            }
            catch (OperationCanceledException)
            {
                // Stop() or a new Speak() cancelled this synthesis; the file is
                // cleaned by Stop and nothing may play.
                TryDelete(path);
            }
            catch (Exception)
            {
                TryDelete(path);
            }
        });
    }

    public static void Stop()
    {
        MediaPlayer? player;
        string? file;
        CancellationTokenSource? synthesis;
        lock (Gate)
        {
            _generation++;
            player = _player;
            file = _currentFile;
            _player = null;
            _currentFile = null;
            synthesis = _synthesisCts;
            _synthesisCts = null;
        }

        // Cancel an in-flight cloud synthesis too — stopping must not leave a
        // request running that nobody will use.
        try
        {
            synthesis?.Cancel();
            synthesis?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        if (player is null)
        {
            return;
        }

        void Close()
        {
            player.Stop();
            player.Close();
            TryDelete(file);
            SpeakingStateChanged?.Invoke(null, false);
        }

        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            Application.Current.Dispatcher.BeginInvoke(Close);
        }
        else
        {
            Close();
        }
    }

    private static async Task<string> SynthesizeLocalToFileAsync(string text, string? languageTag = null)
    {
        using var synthesizer = new SpeechSynthesizer();
        var voice = SelectLocalVoice(text, languageTag);
        if (voice is not null)
        {
            synthesizer.Voice = voice;
        }

        using var stream = await synthesizer.SynthesizeTextToStreamAsync(text);
        var path = Path.Combine(
            Path.GetTempPath(),
            $"popglot-tts-{Guid.NewGuid():N}.wav");
        await using (var source = stream.AsStreamForRead())
        await using (var file = File.Create(path))
        {
            await source.CopyToAsync(file);
        }
        return path;
    }

    private static void StartPlayback(string path, int generation)
    {
        lock (Gate)
        {
            if (generation != _generation)
            {
                TryDelete(path);
                return;
            }
        }

        var player = new MediaPlayer();
        player.MediaEnded += (_, _) => Stop();
        player.MediaFailed += (_, _) => Stop();
        player.Open(new Uri(path));
        player.Play();

        lock (Gate)
        {
            _player = player;
            _currentFile = path;
        }
        SpeakingStateChanged?.Invoke(null, true);
    }

    private static VoiceInformation? SelectLocalVoice(string text, string? languageTag)
    {
        var voices = SpeechSynthesizer.AllVoices;
        if (voices.Count == 0)
        {
            return null;
        }
        var prefix = !string.IsNullOrWhiteSpace(languageTag) &&
            !languageTag.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? languageTag.Split('-')[0]
                : DetectLanguagePrefix(text);
        return voices.FirstOrDefault(voice =>
                voice.Language.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?? SpeechSynthesizer.DefaultVoice;
    }

    private static string DetectLanguagePrefix(string text)
    {
        foreach (var character in text)
        {
            if (character is >= '一' and <= '鿿') return "zh";
            if (character is >= '぀' and <= 'ヿ') return "ja";
            if (character is >= '가' and <= '힯') return "ko";
            if (character is >= 'Ѐ' and <= 'ӿ') return "ru";
            if (character is >= '؀' and <= 'ۿ') return "ar";
        }
        return "en";
    }

    /// <summary>
    /// Cleans up any stale temporary TTS audio files from prior sessions —
    /// both the local (<c>popglot-tts-*</c>) and cloud (<c>popglot-edgetts-*</c>)
    /// file families.
    /// </summary>
    public static void CleanupStaleTempFiles(TimeSpan? olderThan = null)
    {
        try
        {
            var tempDir = Path.GetTempPath();
            var threshold = DateTime.UtcNow - (olderThan ?? TimeSpan.FromMinutes(10));
            foreach (var pattern in (string[])["popglot-tts-*.*", "popglot-edgetts-*.*"])
            {
                foreach (var file in Directory.EnumerateFiles(tempDir, pattern))
                {
                    try
                    {
                        var lastWrite = File.GetLastWriteTimeUtc(file);
                        if (lastWrite < threshold)
                        {
                            File.Delete(file);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string? path, bool isRetry = false)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            if (!isRetry)
            {
                // File might be briefly locked by MediaPlayer; retry once after 500ms
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    TryDelete(path, isRetry: true);
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
