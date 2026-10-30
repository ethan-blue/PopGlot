using PopGlot.Windows;

namespace PopGlot.Windows.PureTests;

internal static partial class Program
{
    private static void OcrCjkSpaceCollapse()
    {
        Equal("无法连接到远程服务器", WindowsOcrService.CollapseCjkAdjacentSpaces("无 法 连 接 到 远 程 服 务 器"),
            "engine-inserted spaces between CJK characters are removed");
        Equal("无法连接到远程服务器，请检查网络", WindowsOcrService.CollapseCjkAdjacentSpaces("无法连接到远程服务器 ， 请检查网络"),
            "spaces around CJK punctuation are removed when both neighbours are CJK");
        Equal("hello world", WindowsOcrService.CollapseCjkAdjacentSpaces("hello world"),
            "Latin word spacing survives");
        Equal("使用 GitHub Actions", WindowsOcrService.CollapseCjkAdjacentSpaces("使用 GitHub Actions"),
            "the CJK-Latin boundary space survives");
        Equal("第 2 行", WindowsOcrService.CollapseCjkAdjacentSpaces("第 2 行"),
            "spaces around digits survive");
        Equal(string.Empty, WindowsOcrService.CollapseCjkAdjacentSpaces(string.Empty), "empty stays empty");
        Equal(" 无法 ", WindowsOcrService.CollapseCjkAdjacentSpaces(" 无 法 "), "edge spaces are kept as-is (only inner pairs collapse)");
    }

    private static void OcrScaleCandidates()
    {
        Equal(Array.Empty<int>(), WindowsOcrService.ScaleCandidates(null, 800, 60),
            "no line structure means no upscaling");
        Equal(Array.Empty<int>(), WindowsOcrService.ScaleCandidates(30, 800, 60),
            "large lines are never upscaled");
        True(new[] { 2, 3 }.SequenceEqual(WindowsOcrService.ScaleCandidates(12, 800, 60)),
            "small lines try 2x and 3x");
        True(new[] { 2, 3 }.SequenceEqual(WindowsOcrService.ScaleCandidates(27, 800, 60)),
            "the boundary below SmallLineHeightPx still upscales");
        True(WindowsOcrService.ScaleCandidates(12, 50000, 100).Length == 0,
            "candidates that exceed the engine dimension cap are dropped");
    }

    private static void OcrScoreFunction()
    {
        Equal(0d, WindowsOcrService.Score(""), "empty text scores zero");
        Equal(0d, WindowsOcrService.Score("   "), "whitespace-only text scores zero");
        Equal(33d, WindowsOcrService.Score("The operation completed successfully"),
            "solid Latin text scores its glyph count");
        Equal(10d, WindowsOcrService.Score("无法连接到远程服务器"),
            "CJK text without spaces scores full glyph count");
        True(WindowsOcrService.Score("T h e r e i s n o t e x t") < WindowsOcrService.Score("There is no text"),
            "fragmented text scores below the same words unfragmented");
        Equal(0d, WindowsOcrService.Score("T h e r e i s n o t e x t"),
            "fully fragmented text scores like empty (all signal lost)");
    }
}
