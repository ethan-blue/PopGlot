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

    /// <summary>Speaks <paramref name="text"/>, replacing any current utterance.</summary>
    public static void Speak(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Stop();
        int generation;
        lock (Gate)
        {
            generation = ++_generation;
        }

        _ = Task.Run(async () =>
        {
            string? path = null;
            try
            {
                // First try ultra-natural Edge Neural TTS
                try
                {
                    path = await EdgeTtsService.SynthesizeToMp3FileAsync(text);
                }
                catch
                {
                    // Fall back to offline Windows Speech Synthesis
                    path = await SynthesizeLocalToFileAsync(text);
                }

                if (path is not null)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => StartPlayback(path, generation));
                }
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
        lock (Gate)
        {
            _generation++;
            player = _player;
            file = _currentFile;
            _player = null;
            _currentFile = null;
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

    private static async Task<string> SynthesizeLocalToFileAsync(string text)
    {
        using var synthesizer = new SpeechSynthesizer();
        var voice = SelectLocalVoice(text);
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

    private static VoiceInformation? SelectLocalVoice(string text)
    {
        var voices = SpeechSynthesizer.AllVoices;
        if (voices.Count == 0)
        {
            return null;
        }
        var prefix = DetectLanguagePrefix(text);
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

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
