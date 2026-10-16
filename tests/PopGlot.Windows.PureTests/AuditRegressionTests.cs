using System.IO;
using System.Net;
using System.Net.Http;
using PopGlot.Windows;
using PopGlot.Windows.Services;
using PopGlot.Windows.Services.AiConnections;

namespace PopGlot.Windows.PureTests;

internal static partial class Program
{
    private static void TranslationLanguageQuality()
    {
        const string english = "Please restart the application before changing the translation settings again.";
        const string chinese = "请先重新启动应用程序，然后再修改当前使用的翻译设置。";
        True(TranslationQuality.Check(english, english, "auto", "zh-CN") is not null, "English echo is not a clean Chinese translation");
        True(TranslationQuality.Check(chinese, chinese, "auto", "en") is not null, "Chinese echo is not a clean English translation");
        True(TranslationQuality.Check(english, chinese, "auto", "zh-CN") is null, "Chinese translation is accepted");
        True(TranslationQuality.Check(chinese, english, "zh-CN", "en") is null, "English translation is accepted");
        True(TranslationQuality.Check(english, english, "auto", "en") is null, "same-language auto input is valid");
        True(TranslationQuality.Check(english, english, "en", "en") is null, "explicit same-language content is valid");
        True(TranslationQuality.Check(chinese, chinese, "zh-CN", "zh-TW") is null, "script conversion is valid");
        True(TranslationQuality.Check("OpenAI", "OpenAI", "auto", "zh-CN") is null, "short names are not translation failures");
        True(TranslationQuality.Check(english, "```text\n" + english + "\n```", "auto", "zh-CN") is null, "fenced technical text is not prose");
        True(TranslationQuality.Check("const value = '" + english + "';", english, "auto", "zh-CN") is null, "machine syntax abstains");
        True(TranslationQuality.Check("https://example.test", "https://example.test", "auto", "zh-CN") is null, "URLs are preserved");
        True(TranslationQuality.Check(english, "API", "auto", "zh-CN") is null, "a short acronym is ambiguous");
    }

    private static void OcrDimensionLimits()
    {
        Equal((4096u, 256u), WindowsOcrService.FitDimensions(8192, 512, 4096), "wide capture fits per-edge limit");
        Equal((256u, 4096u), WindowsOcrService.FitDimensions(512, 8192, 4096), "tall capture fits per-edge limit");
        Equal((640u, 480u), WindowsOcrService.FitDimensions(640, 480, 4096), "small images are never upscaled");
        Equal((1u, 4096u), WindowsOcrService.FitDimensions(1, 10000, 4096), "thin image retains nonzero edge");
        var refused = false;
        try { WindowsOcrService.FitDimensions(0, 512, 4096); } catch (ArgumentOutOfRangeException) { refused = true; }
        True(refused, "invalid dimensions are rejected before decoding");
    }

    private static void MyMemoryScriptBoundaries()
    {
        Equal("ja", FreeTranslateService.ResolveMyMemorySource("auto", "翻訳してください"), "kana identifies Japanese");
        Equal("ko", FreeTranslateService.ResolveMyMemorySource("auto", "안녕하세요"), "Hangul identifies Korean");
        Equal("fr", FreeTranslateService.ResolveMyMemorySource("fr", "bonjour"), "explicit source always wins");
        foreach (var text in new[] { "Привет мир", "école", "你好 hello", "12345" })
        {
            var refused = false;
            try { FreeTranslateService.ResolveMyMemorySource("auto", text); } catch (FreeTranslateException) { refused = true; }
            True(refused, "ambiguous or unsupported auto source requires an explicit choice");
        }
    }

    private static void OmpBunDiscovery(string root)
    {
        var install = Path.Combine(root, "bun installation with spaces");
        var bin = Path.Combine(install, "bin");
        var package = Path.Combine(install, "install", "global", "node_modules", "@oh-my-pi", "pi-coding-agent");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(package, "src"));
        File.WriteAllText(Path.Combine(bin, "omp.cmd"), "DO NOT EXECUTE THIS SHIM");
        File.WriteAllText(Path.Combine(bin, "bun.exe"), "NOT AN EXECUTABLE; discovery must not launch it");
        var manifest = Path.Combine(package, "package.json");
        File.WriteAllText(manifest, """{"name":"@oh-my-pi/pi-coding-agent","bin":{"omp":"src/cli.ts"}}""");
        var script = Path.Combine(package, "src", "cli.ts");
        File.WriteAllText(script, "// offline discovery fixture");
        var original = Environment.GetEnvironmentVariable("BUN_INSTALL");
        try
        {
            Environment.SetEnvironmentVariable("BUN_INSTALL", install);
            Equal(script, CliLauncher.Resolve("omp", Path.Combine(bin, "omp.cmd")), "Bun shim maps to official package, not shell");
            var start = CliLauncher.CreateStartInfo("omp", script);
            Equal(Path.Combine(bin, "bun.exe"), start.FileName, "launches the selected installation's Bun");
            Equal(script, start.ArgumentList.Single(), "script path is one argument, including spaces");
            True(CliDiscovery.Discover("omp", [bin]).Any(item => item.ExecutablePath == script), "discovery retains package-based installations");
            File.WriteAllText(manifest, """{"name":"@oh-my-pi/pi-coding-agent","bin":{"omp":"../../evil.ts"}}""");
            True(CliLauncher.ResolveOmpPackage(Path.Combine(bin, "omp.cmd")) is null, "bin traversal cannot choose arbitrary code");
            File.WriteAllText(manifest, """{"name":"unrelated-package","bin":{"omp":"src/cli.ts"}}""");
            True(CliLauncher.ResolveOmpPackage(Path.Combine(bin, "omp.cmd")) is null, "same-named unrelated package is not accepted");
        }
        finally { Environment.SetEnvironmentVariable("BUN_INSTALL", original); }
    }

    private sealed class SizedBody(long length) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(count, length - BytesRead);
            buffer.AsSpan(offset, read).Fill((byte)' ');
            BytesRead += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = (int)Math.Min(buffer.Length, length - BytesRead);
            buffer.Span[..read].Fill((byte)' ');
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BodyHandler(bool falseLength) : HttpMessageHandler
    {
        public readonly List<SizedBody> Bodies = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = new SizedBody(FreeTranslateService.MaxResponseBytes * 2L);
            Bodies.Add(body);
            var content = new StreamContent(body);
            if (falseLength) content.Headers.ContentLength = 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static async Task FreeResponseStreamingCap()
    {
        var originalSender = FreeTranslateService.HttpSenderOverride;
        var originalClient = FreeTranslateService.HttpClientOverrideForTest;
        var shell = ShellSettingsStore.Load(consentPath());
        ShellSettingsStore.Save(shell with { FreeEngineConsent = FreeEngineConsent.Allowed }, consentPath());
        try
        {
            foreach (var falseLength in new[] { false, true })
            {
                using var handler = new BodyHandler(falseLength);
                using var client = new HttpClient(handler);
                FreeTranslateService.HttpClientOverrideForTest = client;
                FreeTranslateService.HttpSenderOverride = null;
                True(OutboundPolicy.AllowsFreeEngine(DemoSettings(), out _, out var authorization), "isolated consent is granted");
                await ThrowsAsync<FreeTranslateException>(() => FreeTranslateService.TranslateAsync(
                    "bounded response " + Guid.NewGuid(), "en", "zh-CN", authorization, provider: FreeEngineProvider.Google));
                True(handler.Bodies.Count > 0, "uses actual HttpClient completion behavior without any network");
                True(handler.Bodies.All(body => body.BytesRead <= FreeTranslateService.MaxResponseBytes + 64 * 1024),
                    "stops reading at cap plus one chunk, BEFORE HttpClient can buffer the entire response");
            }
        }
        finally
        {
            FreeTranslateService.HttpSenderOverride = originalSender;
            FreeTranslateService.HttpClientOverrideForTest = originalClient;
            ShellSettingsStore.Save(shell, consentPath());
        }
    }
}
