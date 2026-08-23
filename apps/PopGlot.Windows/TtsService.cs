using System.IO;
using System.Media;
using Windows.Media.SpeechSynthesis;

namespace PopGlot.Windows;

internal static class TtsService
{
    private static readonly Lazy<SpeechSynthesizer> SynthesizerInstance = new(() => new SpeechSynthesizer());

    public static void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                var synth = SynthesizerInstance.Value;
                var hasChinese = text.Any(c => c >= 0x4E00 && c <= 0x9FA5);
                var voices = SpeechSynthesizer.AllVoices;

                var voice = hasChinese
                    ? voices.FirstOrDefault(v => v.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || v.DisplayName.Contains("Chinese") || v.DisplayName.Contains("Huihui") || v.DisplayName.Contains("Yaoyao"))
                    : voices.FirstOrDefault(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase) || v.DisplayName.Contains("English") || v.DisplayName.Contains("Zira") || v.DisplayName.Contains("David"));

                if (voice is not null)
                {
                    synth.Voice = voice;
                }

                var speechStream = await synth.SynthesizeTextToStreamAsync(text);
                using var netStream = speechStream.AsStreamForRead();
                using var player = new SoundPlayer(netStream);
                player.Play();
            }
            catch
            {
                // Ignore TTS playback failures
            }
        });
    }
}
