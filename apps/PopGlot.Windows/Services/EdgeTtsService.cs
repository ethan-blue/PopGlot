using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Xml.Linq;

namespace PopGlot.Windows.Services;

/// <summary>
/// Natural neural Text-To-Speech over the Microsoft Edge Read Aloud protocol.
/// Destination: speech.platform.bing.com (Microsoft voice service). The user
/// must have enabled cloud speech explicitly; nothing here is contacted for
/// offline playback.
/// </summary>
internal static class EdgeTtsService
{
    private const string TrustedToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private const string Endpoint = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";
    private const string ChromiumUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0";

    private const int MaxSourceCharacters = 5_000;
    private const int MaxAudioBytes = 8 * 1024 * 1024;

    /// <summary>Test seam: supplies a fake transport so synthesis is verifiable offline.</summary>
    internal static Func<Uri, CancellationToken, Task<WebSocket>>? WebSocketFactory { get; set; }

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
        if (trimmed.Length > MaxSourceCharacters)
        {
            throw new InvalidOperationException($"云端朗读单次最多 {MaxSourceCharacters} 个字符。");
        }

        var voice = preferredVoice ?? ResolveDefaultVoice(trimmed);
        var locale = voice[..voice.LastIndexOf('-', voice.LastIndexOf('-') - 1)];

        var connectionId = Guid.NewGuid().ToString("N");
        var uri = new Uri($"{Endpoint}?TrustedClientToken={TrustedToken}&ConnectionId={connectionId}");

        using var ws = WebSocketFactory is { } factory
            ? await factory(uri, cancellationToken)
            : await ConnectAsync(uri, cancellationToken);

        // 1. Send speech.config
        var dateHeader = DateTime.UtcNow.ToString("r");
        var configMessage =
            $"X-Timestamp:{dateHeader}\r\n" +
            "Content-Type:application/json; charset=utf-8\r\n" +
            "Path:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}";

        await SendTextMessageAsync(ws, configMessage, cancellationToken);

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

        await SendTextMessageAsync(ws, ssmlMessage, cancellationToken);

        // 3. Receive messages, assembling fragments per protocol message.
        using var audioStream = new MemoryStream();
        var turnEnded = false;
        while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var (messageType, payload) = await ReceiveMessageAsync(ws, cancellationToken);
            if (messageType == WebSocketMessageType.Close)
            {
                break;
            }
            if (messageType == WebSocketMessageType.Text)
            {
                var textPayload = Encoding.UTF8.GetString(payload);
                if (textPayload.Contains("Path:turn.end", StringComparison.Ordinal))
                {
                    turnEnded = true;
                    break;
                }
                continue;
            }

            // Binary frame: 2-byte big-endian header length, then headers, then audio.
            if (payload.Length >= 2)
            {
                var headerLength = (payload[0] << 8) | payload[1];
                var payloadOffset = 2 + headerLength;
                if (payload.Length > payloadOffset)
                {
                    if (audioStream.Length + (payload.Length - payloadOffset) > MaxAudioBytes)
                    {
                        throw new InvalidOperationException("云端朗读音频超过大小上限，已中止。");
                    }
                    audioStream.Write(payload, payloadOffset, payload.Length - payloadOffset);
                }
            }
        }

        if (!turnEnded)
        {
            // A connection that died (or was closed by the service) before the
            // turn finished must not present truncated audio as success.
            audioStream.SetLength(0);
            throw cancellationToken.IsCancellationRequested
                ? new OperationCanceledException(cancellationToken)
                : new InvalidOperationException("云端朗读连接在完成前中断，未获得完整音频。");
        }

        if (audioStream.Length == 0)
        {
            throw new InvalidOperationException("云端语音服务没有返回音频内容。");
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"popglot-edgetts-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(tempPath, audioStream.ToArray(), cancellationToken);
        return tempPath;
    }

    private static async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("User-Agent", ChromiumUserAgent);
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        await ws.ConnectAsync(uri, cancellationToken);
        return ws;
    }

    private static async Task SendTextMessageAsync(WebSocket ws, string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    /// <summary>
    /// Receives ONE complete protocol message. WS messages may arrive in any
    /// number of fragments: the two-byte binary header cannot be parsed until
    /// EndOfMessage, and a text marker like turn.end may be split anywhere.
    /// </summary>
    private static async Task<(WebSocketMessageType Type, byte[] Payload)> ReceiveMessageAsync(
        WebSocket ws, CancellationToken ct)
    {
        using var message = new MemoryStream();
        WebSocketMessageType type;
        bool endOfMessage;
        do
        {
            var buffer = new byte[16 * 1024];
            var result = await ws.ReceiveAsync(buffer, ct);
            type = result.MessageType;
            endOfMessage = result.EndOfMessage;
            if (type == WebSocketMessageType.Close)
            {
                return (type, []);
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxAudioBytes)
            {
                throw new InvalidOperationException("云端朗读单条消息超过大小上限，已中止。");
            }
        }
        while (!endOfMessage);
        return (type, message.ToArray());
    }

    /// <summary>
    /// Resolves the cloud voice. An explicit language tag from the app wins
    /// (the user chose the language); character-script detection is only the
    /// fallback for unknown tags, and emoji or accented letters never imply a
    /// language by themselves.
    /// </summary>
    public static string ResolveVoice(string? languageTag, string text)
    {
        if (!string.IsNullOrWhiteSpace(languageTag) &&
            !languageTag.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var tag = languageTag.ToLowerInvariant();
            var prefix = tag.Split('-')[0];
            return prefix switch
            {
                "zh" => tag.StartsWith("zh-tw", StringComparison.OrdinalIgnoreCase) ||
                        tag.StartsWith("zh-hk", StringComparison.OrdinalIgnoreCase)
                    ? "zh-TW-HsiaoChenNeural"
                    : "zh-CN-XiaoxiaoNeural",
                "ja" => "ja-JP-NanamiNeural",
                "ko" => "ko-KR-SunHiNeural",
                "ru" => "ru-RU-SvetlanaNeural",
                "de" => "de-DE-KatjaNeural",
                "fr" => "fr-FR-DeniseNeural",
                "es" => "es-ES-ElviraNeural",
                "pt" => "pt-BR-FranciscaNeural",
                "it" => "it-IT-ElsaNeural",
                "ar" => "ar-EG-SalmaNeural",
                _ => "en-US-JennyNeural",
            };
        }

        // Script fallback only: unambiguous scripts decide. The old
        // `ch >= 'ä'` range comparison sent emoji and French accents to the
        // German voice.
        foreach (var ch in text)
        {
            if (ch is >= '一' and <= '鿿') return "zh-CN-XiaoxiaoNeural";
            if (ch is >= '぀' and <= 'ヿ') return "ja-JP-NanamiNeural";
            if (ch is >= '가' and <= '힯') return "ko-KR-SunHiNeural";
            if (ch is >= 'Ѐ' and <= 'ӿ') return "ru-RU-SvetlanaNeural";
            if (ch is >= '؀' and <= 'ۿ') return "ar-EG-SalmaNeural";
        }
        return ResolveAccentHintVoice(text);
    }

    /// <summary>Legacy entry: script fallback without a language tag.</summary>
    public static string ResolveDefaultVoice(string text) => ResolveVoice(null, text);

    /// <summary>
    /// Distinct accented letters hint French or German; anything else falls
    /// back to the neutral English voice instead of guessing a language.
    /// </summary>
    private static string ResolveAccentHintVoice(string text)
    {
        foreach (var ch in text)
        {
            if (ch is 'à' or 'â' or 'ç' or 'é' or 'è' or 'ê' or 'ë' or 'î' or 'ï' or 'ô' or 'ù' or 'û' or 'œ')
            {
                return "fr-FR-DeniseNeural";
            }
            if (ch is 'ä' or 'ö' or 'ü' or 'ß')
            {
                return "de-DE-KatjaNeural";
            }
        }
        return "en-US-JennyNeural";
    }
}
