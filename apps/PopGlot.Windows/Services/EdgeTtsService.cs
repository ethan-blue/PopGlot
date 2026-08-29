using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PopGlot.Windows.Services;

/// <summary>
/// Ultra-high quality, natural neural Text-To-Speech powered by Microsoft Edge Read Aloud protocol.
/// Requires zero API key or configuration.
/// </summary>
internal static class EdgeTtsService
{
    private const string TrustedToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string Endpoint = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";
    private const string ChromiumUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0";

    public static async Task<string> SynthesizeToMp3FileAsync(
        string text,
        string? preferredVoice = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("Text cannot be empty", nameof(text));
        }

        var voice = preferredVoice ?? ResolveDefaultVoice(trimmed);
        var locale = voice[..voice.LastIndexOf('-', voice.LastIndexOf('-') - 1)];

        var connectionId = Guid.NewGuid().ToString("N");
        var uri = new Uri($"{Endpoint}?TrustedClientToken={TrustedToken}&ConnectionId={connectionId}");

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", ChromiumUserAgent);
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        await ws.ConnectAsync(uri, linkedCts.Token);

        // 1. Send speech.config
        var dateHeader = DateTime.UtcNow.ToString("r");
        var configMessage =
            $"X-Timestamp:{dateHeader}\r\n" +
            "Content-Type:application/json; charset=utf-8\r\n" +
            "Path:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}";

        var configBytes = Encoding.UTF8.GetBytes(configMessage);
        await ws.SendAsync(configBytes, WebSocketMessageType.Text, true, linkedCts.Token);

        // 2. Send SSML request
        var requestId = Guid.NewGuid().ToString("N");
        var escapedText = new XText(trimmed).ToString();
        var ssml =
            $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{locale}'>" +
            $"<voice name='{voice}'><prosody rate='0%' pitch='0%'>{escapedText}</prosody></voice></speak>";

        var ssmlMessage =
            $"X-RequestId:{requestId}\r\n" +
            $"X-Timestamp:{dateHeader}\r\n" +
            "Content-Type:application/ssml+xml\r\n" +
            $"Path:ssml\r\n\r\n{ssml}";

        var ssmlBytes = Encoding.UTF8.GetBytes(ssmlMessage);
        await ws.SendAsync(ssmlBytes, WebSocketMessageType.Text, true, linkedCts.Token);

        // 3. Receive binary audio payload chunks
        using var audioStream = new MemoryStream();
        var buffer = new byte[16 * 1024];

        while (ws.State == WebSocketState.Open && !linkedCts.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, linkedCts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var textPayload = Encoding.UTF8.GetString(buffer, 0, result.Count);
                if (textPayload.Contains("Path:turn.end", StringComparison.Ordinal))
                {
                    break;
                }
            }
            else if (result.MessageType == WebSocketMessageType.Binary)
            {
                if (result.Count >= 2)
                {
                    // 2-byte big-endian header length
                    var headerLength = (buffer[0] << 8) | buffer[1];
                    var payloadOffset = 2 + headerLength;
                    if (result.Count > payloadOffset)
                    {
                        var dataLength = result.Count - payloadOffset;
                        audioStream.Write(buffer, payloadOffset, dataLength);
                    }
                }
            }
        }

        if (audioStream.Length == 0)
        {
            throw new InvalidOperationException("No audio bytes received from Edge TTS service.");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"popglot-edgetts-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(tempPath, audioStream.ToArray(), linkedCts.Token);
        return tempPath;
    }

    public static string ResolveDefaultVoice(string text)
    {
        foreach (var ch in text)
        {
            if (ch is >= '一' and <= '鿿') return "zh-CN-XiaoxiaoNeural"; // 微软晓晓（自然中文女声）
            if (ch is >= '぀' and <= 'ヿ') return "ja-JP-NanamiNeural";   // 日文女声
            if (ch is >= '가' and <= '힯') return "ko-KR-SunHiNeural";   // 韩文女声
            if (ch is >= 'Ѐ' and <= 'ӿ') return "ru-RU-SvetlanaNeural"; // 俄语女声
            if (ch is >= 'ä' or 'ö' or 'ü' or 'ß') return "de-DE-KatjaNeural"; // 德语
            if (ch is >= 'é' or 'è' or 'à' or 'ç') return "fr-FR-DeniseNeural"; // 法语
        }
        return "en-US-JennyNeural"; // 微软 Jenny（自然美音女声）
    }
}
