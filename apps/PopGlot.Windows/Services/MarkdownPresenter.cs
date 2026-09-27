using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PopGlot.Windows.Services;

/// <summary>
/// Converts raw translation markdown and technical text into pixel-perfect WPF FlowDocument/Inlines.
/// Formats inline code, code blocks, bold text, lists, headings, and auto-applies CJK-Latin Pangu spacing.
/// </summary>
internal static partial class MarkdownPresenter
{
    // .NET balancing groups handle nested URL parentheses and escaped
    // delimiters while reserving the final ')' for the Markdown link close.
    private const string MarkdownLinkPattern = @"\[(?<text>(?:\\.|[^\]])+)\]\((?<url>(?:\\.|[^()\\]|(?<depth>\()|(?<-depth>\)))*(?(depth)(?!)))\)";
    // Auto-spacing between CJK and English/numbers (Pangu spacing algorithm)
    [GeneratedRegex(@"([\u4e00-\u9fa5\u3040-\u30ff])([a-zA-Z0-9_\$#@`])")]
    private static partial Regex CjkToLatinRegex();

    [GeneratedRegex(@"([a-zA-Z0-9_\$#@`%])([\u4e00-\u9fa5\u3040-\u30ff])")]
    private static partial Regex LatinToCjkRegex();

    // Inline code spans. Splitting on this FIRST is what keeps technical text
    // intact: everything inside a span is copied, spoken and spaced verbatim.
    [GeneratedRegex(@"`[^`]+`")]
    private static partial Regex InlineCodeRegex();

    /// <summary>
    /// Emphasis runs whose content looks like natural language (whitespace or
    /// CJK/full-width characters). Identifier-like content — `foo_bar_baz`,
    /// `__init__`, `a*b*c`, `**GDP**` — never matches, so technical delimiters
    /// are preserved instead of being guessed away.
    /// </summary>
    [GeneratedRegex(@"(\*\*|\*|__|_)([^\s*_]+(?:[ \t]+[^\s*_]+)*)\1")]
    private static partial Regex EmphasisRegex();

    public static string FormatPangu(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s1 = CjkToLatinRegex().Replace(text, "$1 $2");
        return LatinToCjkRegex().Replace(s1, "$1 $2");
    }

    private static bool LooksLikeNaturalLanguage(string content)
    {
        foreach (var ch in content)
        {
            if (char.IsWhiteSpace(ch) || ch >= 0x2E80)
            {
                // CJK, kana, Hangul, full-width forms and CJK punctuation all
                // read as natural language; ASCII-only runs do not.
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Removes natural-language emphasis markers from one non-code segment.
    /// </summary>
    private static string StripNaturalEmphasis(string segment) =>
        EmphasisRegex().Replace(segment, match =>
            LooksLikeNaturalLanguage(match.Groups[2].Value) ? match.Groups[2].Value : match.Value);

    /// <summary>
    /// Converts markdown translation text into clean, unformatted plain text
    /// suitable for clipboard copy, speech synthesis (TTS), and vocabulary book storage.
    ///
    /// Structure is parsed FIRST: fenced blocks and inline code spans are
    /// preserved byte-for-byte, and only natural-language segments get
    /// headings/bullets/emphasis unwrapped. Unparseable input is kept as-is.
    /// </summary>
    /// <remarks>
    /// C04 newline and fidelity contract: input CRLF/CR is normalized once and
    /// the output is always LF. Code content between fences — indentation,
    /// trailing spaces, blank lines, the block's final newline — is preserved
    /// verbatim; only the fence lines themselves are removed. The output is
    /// never Trim()-ed as a whole, so a code block at either edge keeps its
    /// exact bytes.
    /// </remarks>
    /// <summary>A07: how one line relates to a fenced code block.</summary>
    internal enum FenceLineKind
    {
        /// <summary>Not a fence line.</summary>
        None,
        /// <summary>Opens a block (run ≥3, info string optional).</summary>
        Open,
        /// <summary>Closes the matching block (same marker, run ≥ opener, no info).</summary>
        Close,
    }

    /// <summary>One classified fence line: marker, run length and info string.</summary>
    internal readonly record struct FenceMatch(
        FenceLineKind Kind,
        char Marker,
        int RunLength,
        string InfoString,
        string TrimmedLine);

    /// <summary>
    /// A07: the single fence grammar for every markdown path. A fence line is
    /// a run of ≥3 identical backticks or tildes (leading whitespace allowed).
    /// A line with only the run closes the block whose opener used the SAME
    /// marker with a run ≤ this one; anything after the run makes it an
    /// opener (info string). Backtick info strings may not contain a
    /// backtick (that line is plain text), tilde info strings may.
    /// </summary>
    internal static FenceMatch ClassifyFenceLine(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length < 3)
        {
            return new FenceMatch(FenceLineKind.None, default, 0, string.Empty, trimmed);
        }
        var marker = trimmed[0];
        if (marker is not '`' and not '~')
        {
            return new FenceMatch(FenceLineKind.None, default, 0, string.Empty, trimmed);
        }
        var run = 0;
        while (run < trimmed.Length && trimmed[run] == marker)
        {
            run++;
        }
        if (run < 3)
        {
            return new FenceMatch(FenceLineKind.None, default, 0, string.Empty, trimmed);
        }
        var rest = trimmed[run..].Trim();
        if (rest.Length == 0)
        {
            return new FenceMatch(FenceLineKind.Close, marker, run, string.Empty, trimmed);
        }
        if (marker == '`' && rest.Contains('`'))
        {
            // A backtick inside the info string would break the fence.
            return new FenceMatch(FenceLineKind.None, default, 0, string.Empty, trimmed);
        }
        return new FenceMatch(FenceLineKind.Open, marker, run, rest, trimmed);
    }

    /// <summary>True when <paramref name="close"/> terminates a block opened by <paramref name="open"/>.</summary>
    private static bool ClosesBlock(FenceMatch open, FenceMatch close) =>
        close.Kind == FenceLineKind.Close &&
        close.Marker == open.Marker &&
        close.RunLength >= open.RunLength;

    public static string ToPlainText(string? markdown, bool preserveListStructure = false)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return markdown ?? string.Empty;
        }

        var normalized = markdown.Replace("\r\n", "\n").Replace("\r", "\n");
        var sb = new StringBuilder(normalized.Length);
        var position = 0;
        FenceMatch? openFence = null;

        while (position < normalized.Length)
        {
            var lineEnd = normalized.IndexOf('\n', position);
            var hasNewline = lineEnd >= 0;
            var line = hasNewline ? normalized[position..lineEnd] : normalized[position..];
            position = hasNewline ? lineEnd + 1 : normalized.Length;

            if (openFence is { } open)
            {
                // A07: only a matching closer of sufficient length ends the
                // block; shorter runs of the same or another marker are code.
                if (ClosesBlock(open, ClassifyFenceLine(line)))
                {
                    openFence = null;
                    continue;
                }
                sb.Append(line);
                if (hasNewline)
                {
                    sb.Append('\n');
                }
                continue;
            }

            var fence = ClassifyFenceLine(line);
            // Outside a block, ANY fence line — bare or with an info string —
            // opens one (A07: a bare run classifies as Close but has nothing
            // to close).
            if (fence.Kind != FenceLineKind.None)
            {
                openFence = fence;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                if (hasNewline)
                {
                    sb.Append('\n');
                }
                continue;
            }

            if (IsTableLine(line) && hasNewline)
            {
                var nextEnd = normalized.IndexOf('\n', position);
                var delimiter = (nextEnd >= 0 ? normalized[position..nextEnd] : normalized[position..]).Trim();
                if (IsTableDelimiterLine(delimiter))
                {
                    var tableRows = new List<string> { line, delimiter };
                    position = nextEnd >= 0 ? nextEnd + 1 : normalized.Length;
                    while (position < normalized.Length)
                    {
                        var end = normalized.IndexOf('\n', position);
                        var row = (end >= 0 ? normalized[position..end] : normalized[position..]).Trim();
                        if (!IsTableLine(row)) break;
                        tableRows.Add(row);
                        position = end >= 0 ? end + 1 : normalized.Length;
                    }
                    for (var rowIndex = 0; rowIndex < tableRows.Count; rowIndex++)
                    {
                        if (rowIndex == 1) continue; // alignment syntax
                        if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
                        sb.Append(string.Join("\t", SplitTableRow(tableRows[rowIndex]).Select(PlainTableCell)));
                        if (rowIndex + 1 < tableRows.Count) sb.Append('\n');
                    }
                    continue;
                }
            }

            var trimmed = line.TrimStart();
            var prose = line;
            var outputPrefix = string.Empty;
            // Headings: # , ## , etc.
            if (trimmed.StartsWith('#'))
            {
                int hLevel = 0;
                while (hLevel < trimmed.Length && trimmed[hLevel] == '#') hLevel++;
                if (hLevel >= 1 && hLevel <= 6 && hLevel < trimmed.Length && trimmed[hLevel] == ' ')
                {
                    prose = trimmed[(hLevel + 1)..].Trim();
                }
            }
            // Bullet points: - , * , +
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                     trimmed.StartsWith("* ", StringComparison.Ordinal) ||
                     trimmed.StartsWith("+ ", StringComparison.Ordinal))
            {
                var marker = trimmed[..1];
                outputPrefix = preserveListStructure ? line[..(line.Length - trimmed.Length)] + marker + " " : string.Empty;
                prose = trimmed[2..].Trim();
            }
            // Ordered list: 1. , 2) , etc.
            else
            {
                var match = Regex.Match(trimmed, @"^\d+[\.\)]\s+(.*)$");
                if (match.Success)
                {
                    outputPrefix = preserveListStructure
                        ? line[..(line.Length - trimmed.Length)] + match.Groups[0].Value[..match.Groups[0].Value.IndexOfAny([' ', '\t'])] + " "
                        : string.Empty;
                    prose = match.Groups[1].Value.Trim();
                }
                else if (trimmed.StartsWith('>'))
                {
                    outputPrefix = preserveListStructure ? "> " : string.Empty;
                    prose = trimmed.Length > 1 && trimmed[1] == ' ' ? trimmed[2..].Trim() : trimmed[1..].Trim();
                }
            }

            // Prose lines keep display hygiene (no trailing whitespace) but
            // nothing beyond the line itself is ever trimmed away.
            var formatted = TransformNaturalSegments(prose, static segment =>
            {
                // Link conversion is a prose operation. Inline-code spans are
                // removed from the transform stream and copied verbatim.
                var linksExpanded = Regex.Replace(segment, MarkdownLinkPattern, match =>
                {
                    var label = match.Groups["text"].Value;
                    var url = match.Groups["url"].Value;
                    return label.Equals(url, StringComparison.OrdinalIgnoreCase) ? url : $"{label} ({url})";
                });
                return StripNaturalEmphasis(linksExpanded);
            });
            if (hasNewline && EndsWithMarkdownHardBreak(prose))
                formatted = formatted[..^1];
            sb.Append(outputPrefix).Append(formatted.TrimEnd());
            if (hasNewline)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Splits one line into inline-code spans and natural segments, applies
    /// <paramref name="transform"/> to natural segments only, and reassembles
    /// the line with code content untouched.
    /// </summary>
    public static string TransformNaturalSegments(string line, Func<string, string> transform)
    {
        var builder = new StringBuilder(line.Length);
        var position = 0;
        foreach (Match match in InlineCodeRegex().Matches(line))
        {
            builder.Append(transform(line[position..match.Index]));
            builder.Append(match.Value[1..^1]); // strip the backticks, keep content verbatim
            position = match.Index + match.Length;
        }
        builder.Append(transform(line[position..]));
        return builder.ToString();
    }

    /// <summary>
    /// Renders markdown formatted blocks into a RichTextBox or FlowDocument container.
    /// When <paramref name="resultActionsEnabled"/> is false — partial, cancelled
    /// or failed content — the per-code-block copy buttons render disabled, so
    /// dynamically generated controls obey the same eligibility as the toolbar.
    /// </summary>
    public static void RenderToFlowDocument(
        FlowDocument document,
        string markdownText,
        ResourceDictionary resources,
        bool resultActionsEnabled = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Blocks.Clear();
        document.PagePadding = new Thickness(0);

        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return;
        }

        // C04/F06: code interiors are captured by offset in the normalized
        // text, so the copy button delivers the exact bytes between the
        // fences — indentation, trailing spaces, blank lines and the block's
        // final newline all survive; only the fence lines are removed.
        var normalized = markdownText.Replace("\r\n", "\n").Replace("\r", "\n");
        document.SetResourceReference(FlowDocument.ForegroundProperty, "TextPrimaryBrush");
        var monoFont = (FontFamily)(resources["MonoFontFamily"] ?? new FontFamily("Cascadia Mono, Consolas"));
        var uiFont = (FontFamily)(resources["UiFontFamily"] ?? new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"));

        var position = 0;
        FenceMatch? openFence = null;
        bool addParagraphSpacing = false;
        string? lastProseLine = null;
        var codeStart = 0;
        string? codeLanguage = null;

        void FlushCodeBlock(string code)
        {
            var codeBlock = CreateCodeBlockElement(code, codeLanguage, monoFont, resultActionsEnabled);
            document.Blocks.Add(new BlockUIContainer(codeBlock));
        }

        while (position < normalized.Length)
        {
            var lineStart = position;
            var lineEnd = normalized.IndexOf('\n', position);
            var hasNewline = lineEnd >= 0;
            var line = hasNewline ? normalized[position..lineEnd] : normalized[position..];
            position = hasNewline ? lineEnd + 1 : normalized.Length;

            // A07: the shared fence grammar decides open/close/content.
            if (openFence is { } open)
            {
                if (ClosesBlock(open, ClassifyFenceLine(line)))
                {
                    openFence = null;
                    FlushCodeBlock(normalized[codeStart..lineStart]);
                    lastProseLine = null;
                    continue;
                }
                // Code content is captured by offsets; nothing per-line here.
                continue;
            }

            var fence = ClassifyFenceLine(line);
            if (fence.Kind != FenceLineKind.None)
            {
                openFence = fence;
                codeLanguage = fence.InfoString.Length > 0 ? fence.InfoString : null;
                codeStart = position;
                lastProseLine = null;
                continue;
            }

            // Normal paragraph line
            if (string.IsNullOrWhiteSpace(line))
            {
                addParagraphSpacing = document.Blocks.Count > 0;
                lastProseLine = null;
                continue;
            }

            var trimmedLine = line.TrimStart();

            // Markdown thematic breaks are layout, not content.
            var thematicCandidate = trimmedLine.Trim();
            if (thematicCandidate is "---" or "***" or "___")
            {
                var rule = new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, document.Blocks.Count > 0 ? 10 : 2, 0, 10),
                    SnapsToDevicePixels = true,
                };
                rule.SetResourceReference(Border.BackgroundProperty, "BorderSubtleBrush");
                document.Blocks.Add(new BlockUIContainer(rule));
                addParagraphSpacing = false;
                lastProseLine = null;
                continue;
            }

            // Headings: # , ## , ###
            if (trimmedLine.StartsWith('#'))
            {
                int hLevel = 0;
                while (hLevel < trimmedLine.Length && trimmedLine[hLevel] == '#') hLevel++;
                if (hLevel >= 1 && hLevel <= 6 && hLevel < trimmedLine.Length && trimmedLine[hLevel] == ' ')
                {
                    var headingText = trimmedLine[(hLevel + 1)..].Trim();
                    var headingPara = new Paragraph
                    {
                        Margin = new Thickness(0, addParagraphSpacing || document.Blocks.Count > 0 ? 12 : 0, 0, 4),
                        FontFamily = uiFont,
                        FontWeight = FontWeights.SemiBold,
                    };
                    double headingSize = hLevel switch
                    {
                        1 => 17.0,
                        2 => 15.5,
                        _ => 14.5,
                    };
                    headingPara.FontSize = headingSize;
                    AppendFormattedSpans(headingPara.Inlines, headingText, resources);
                    document.Blocks.Add(headingPara);
                    addParagraphSpacing = false;
                    lastProseLine = null;
                    continue;
                }
            }

            // Markdown Table: lines starting with '|' and followed by a delimiter line '| :--- | ---: |'
            if (IsTableLine(trimmedLine))
            {
                var nextPos = lineEnd >= 0 ? lineEnd + 1 : normalized.Length;
                if (nextPos < normalized.Length)
                {
                    var nextEnd = normalized.IndexOf('\n', nextPos);
                    var nextLine = (nextEnd >= 0 ? normalized[nextPos..nextEnd] : normalized[nextPos..]).Trim();
                    if (IsTableDelimiterLine(nextLine))
                    {
                        var tableLines = new List<string> { trimmedLine, nextLine };
                        var curPos = nextEnd >= 0 ? nextEnd + 1 : normalized.Length;
                        while (curPos < normalized.Length)
                        {
                            var e = normalized.IndexOf('\n', curPos);
                            var l = (e >= 0 ? normalized[curPos..e] : normalized[curPos..]).Trim();
                            if (string.IsNullOrWhiteSpace(l) || !IsTableLine(l))
                            {
                                break;
                            }
                            tableLines.Add(l);
                            curPos = e >= 0 ? e + 1 : normalized.Length;
                        }
                        position = curPos;
                        var tableBlock = CreateTableBlock(tableLines, uiFont, resources);
                        document.Blocks.Add(tableBlock);
                        addParagraphSpacing = false;
                        lastProseLine = null;
                        continue;
                    }
                }
            }

            // Blockquotes: > ...
            if (trimmedLine.StartsWith('>'))
            {
                var quoteContent = trimmedLine.Length > 1 && trimmedLine[1] == ' '
                    ? trimmedLine[2..].Trim()
                    : trimmedLine[1..].Trim();
                var quotePara = new Paragraph
                {
                    Margin = new Thickness(0, addParagraphSpacing || document.Blocks.Count > 0 ? 8 : 2, 0, 4),
                    Padding = new Thickness(10, 2, 0, 2),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    FontFamily = uiFont,
                    FontStyle = FontStyles.Italic,
                    Tag = "blockquote",
                };
                quotePara.SetResourceReference(Block.BorderBrushProperty, "AccentBrush");
                AppendFormattedSpans(quotePara.Inlines, quoteContent, resources);
                document.Blocks.Add(quotePara);
                addParagraphSpacing = false;
                lastProseLine = null;
                continue;
            }

            var isBullet = trimmedLine.StartsWith("- ", StringComparison.Ordinal) ||
                           trimmedLine.StartsWith("* ", StringComparison.Ordinal) ||
                           trimmedLine.StartsWith("+ ", StringComparison.Ordinal);
            var numberedMatch = Regex.Match(trimmedLine, @"^(\d+[\.\)])\s+(.*)$");

            var indentSpaces = line.Length - trimmedLine.Length;
            var nestLevel = Math.Clamp(indentSpaces / 2, 0, 5);

            if (isBullet)
            {
                var bulletContent = trimmedLine[2..].Trim();
                var bulletPara = new Paragraph
                {
                    Margin = new Thickness(16 + nestLevel * 16, addParagraphSpacing ? 8 : 2, 0, 2),
                    TextIndent = -14,
                    FontFamily = uiFont,
                    Tag = "list",
                };
                var bulletDot = new Run("• ")
                {
                    FontWeight = FontWeights.Bold
                };
                bulletDot.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                bulletPara.Inlines.Add(bulletDot);
                AppendFormattedSpans(bulletPara.Inlines, bulletContent, resources);
                document.Blocks.Add(bulletPara);
                addParagraphSpacing = false;
                lastProseLine = null;
                continue;
            }

            if (numberedMatch.Success)
            {
                var numPrefix = numberedMatch.Groups[1].Value + " ";
                var numContent = numberedMatch.Groups[2].Value.Trim();
                var numPara = new Paragraph
                {
                    Margin = new Thickness(20 + nestLevel * 16, addParagraphSpacing ? 8 : 2, 0, 2),
                    TextIndent = -20,
                    FontFamily = uiFont,
                    Tag = "list",
                };
                var numRun = new Run(numPrefix)
                {
                    FontWeight = FontWeights.SemiBold
                };
                numRun.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                numPara.Inlines.Add(numRun);
                AppendFormattedSpans(numPara.Inlines, numContent, resources);
                document.Blocks.Add(numPara);
                addParagraphSpacing = false;
                lastProseLine = null;
                continue;
            }

            // 1. Markdown hard break: previous line ended with two or more spaces or a backslash
            if (lastProseLine != null && (lastProseLine.EndsWith("  ", StringComparison.Ordinal) || EndsWithMarkdownHardBreak(lastProseLine)) &&
                document.Blocks.LastBlock is Paragraph prevHard && Equals(prevHard.Tag, "prose"))
            {
                if (EndsWithMarkdownHardBreak(lastProseLine) && prevHard.Inlines.LastInline is Run endingRun && endingRun.Text.EndsWith('\\'))
                    endingRun.Text = endingRun.Text[..^1];
                prevHard.Inlines.Add(new LineBreak());
                AppendFormattedSpans(prevHard.Inlines, trimmedLine, resources);
                lastProseLine = trimmedLine;
                continue;
            }

            // The renderer cannot distinguish OCR wraps from addresses or verse.
            // Preserve the model's line boundaries; semantic reflow belongs to
            // translation (or an explicit user action), never a display heuristic.

            // 3. Independent paragraph or structured line (dialogue, email greeting/closing, sentences)
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, addParagraphSpacing ? 6 : 0, 0, 0),
                FontFamily = uiFont,
                Tag = "prose",
            };
            AppendFormattedSpans(paragraph.Inlines, trimmedLine, resources);
            document.Blocks.Add(paragraph);
            lastProseLine = trimmedLine;
            addParagraphSpacing = false;
        }

        // Handle unclosed code block if any: everything after the opening
        // fence is the block, verbatim.
        if (openFence is not null && codeStart < normalized.Length)
        {
            FlushCodeBlock(normalized[codeStart..]);
        }
    }

    private static void AppendFormattedSpans(
        InlineCollection inlines,
        string text,
        ResourceDictionary resources)
    {
        var monoFont = (FontFamily)(resources["MonoFontFamily"] ?? new FontFamily("Cascadia Mono, Consolas"));

        // Structure first: split on links / code spans / bold / protected tokens, then
        // apply Pangu spacing per natural-language segment only.
        var pattern = $"({MarkdownLinkPattern}|`[^`]+`|\\*\\*[^*]+\\*\\*|__[^_]+__|⟦PG_\\d{{4}}⟧)";
        var parts = Regex.Split(text, pattern);

        var pieces = new List<FormattedPiece>();
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            if (part.StartsWith('`') && part.EndsWith('`') && part.Length >= 2)
            {
                pieces.Add(new FormattedPiece(PieceKind.Code, part[1..^1]));
            }
            else if ((part.StartsWith("**", StringComparison.Ordinal) && part.EndsWith("**", StringComparison.Ordinal) && part.Length >= 4) ||
                     (part.StartsWith("__", StringComparison.Ordinal) && part.EndsWith("__", StringComparison.Ordinal) && part.Length >= 4))
            {
                pieces.Add(new FormattedPiece(PieceKind.Bold, part[2..^2]));
            }
            else if (part.StartsWith("⟦PG_", StringComparison.Ordinal) && part.EndsWith('⟧'))
            {
                pieces.Add(new FormattedPiece(PieceKind.Token, part));
            }
            else if (part.StartsWith('[') && part.EndsWith(')'))
            {
                var match = Regex.Match(part, "^" + MarkdownLinkPattern + "$" );
                if (match.Success)
                {
                    pieces.Add(new FormattedPiece(PieceKind.Link, match.Groups["text"].Value, match.Groups["url"].Value));
                }
                else
                {
                    pieces.Add(new FormattedPiece(PieceKind.Natural, part));
                }
            }
            else
            {
                pieces.Add(new FormattedPiece(PieceKind.Natural, part));
            }
        }

        for (var index = 0; index < pieces.Count; index++)
        {
            var piece = pieces[index];
            var (kind, pieceText, extra) = piece;
            if (kind is PieceKind.Natural or PieceKind.Bold)
            {
                var spaced = FormatPangu(pieceText);
                if (index > 0 && spaced.Length > 0)
                {
                    var previousChar = LastContentChar(pieces[index - 1]);
                    if (NeedsSeamSpace(previousChar, spaced[0]))
                    {
                        spaced = " " + spaced;
                    }
                }
                if (index + 1 < pieces.Count && spaced.Length > 0)
                {
                    var nextChar = FirstContentChar(pieces[index + 1]);
                    if (NeedsSeamSpace(spaced[^1], nextChar))
                    {
                        spaced += " ";
                    }
                }
                if (kind == PieceKind.Bold)
                {
                    var run = new Run(spaced)
                    {
                        FontWeight = FontWeights.SemiBold,
                    };
                    run.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
                    inlines.Add(run);
                }
                else
                {
                    var run = new Run(spaced);
                    run.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
                    inlines.Add(run);
                }
            }
            else if (kind == PieceKind.Link)
            {
                var linkText = pieceText;
                var rawUrl = extra?.Trim() ?? string.Empty;
                if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) &&
                    (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                     uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                {
                    var linkRun = new Run(FormatPangu(linkText))
                    {
                        TextDecorations = TextDecorations.Underline,
                    };
                    var hyperlink = new Hyperlink(linkRun)
                    {
                        NavigateUri = uri,
                        ToolTip = uri.AbsoluteUri,
                        Cursor = Cursors.Hand,
                    };
                    hyperlink.SetResourceReference(Hyperlink.ForegroundProperty, "AccentBrush");
                    hyperlink.RequestNavigate += (_, e) =>
                    {
                        try
                        {
                            if (e.Uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                                e.Uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                            {
                                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                            }
                        }
                        catch
                        {
                        }
                        e.Handled = true;
                    };
                    inlines.Add(hyperlink);
                }
                else
                {
                    var safeRun = new Run(pieceText);
                    safeRun.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
                    inlines.Add(safeRun);
                }
            }
            else if (kind == PieceKind.Code)
            {
                var codeText = new TextBlock
                {
                    Text = pieceText,
                    FontFamily = monoFont,
                    FontSize = 12.5,
                    FontWeight = FontWeights.Medium,
                };
                codeText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

                var codeBorder = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(2, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = codeText
                };
                codeBorder.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
                inlines.Add(new InlineUIContainer(codeBorder));
            }
            else
            {
                // Protected token placeholder
                var tokenRun = new Run(pieceText)
                {
                    FontFamily = monoFont,
                    FontWeight = FontWeights.Bold,
                };
                tokenRun.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                inlines.Add(tokenRun);
            }
        }
    }

    private readonly record struct FormattedPiece(PieceKind Kind, string Text, string? Extra = null);

    private enum PieceKind
    {
        Natural,
        Code,
        Bold,
        Token,
        Link,
    }

    // The character classes mirror FormatPangu's CJK↔Latin regexes so seams
    // behave exactly like intra-segment spacing.
    private static bool IsLatinish(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '$' or '#' or '@' or '`' or '%';

    private static bool IsCjkish(char c) => c is >= '\u3040' and <= '\u30FF' or >= '\u4E00' and <= '\u9FA5';

    private static bool NeedsSeamSpace(char left, char right) =>
        !char.IsWhiteSpace(left) && !char.IsWhiteSpace(right) &&
        ((IsCjkish(left) && IsLatinish(right)) || (IsLatinish(left) && IsCjkish(right)));

    private static char LastContentChar(FormattedPiece piece) => piece.Kind switch
    {
        PieceKind.Code => piece.Text.Length > 0 ? piece.Text[^1] : '`',
        PieceKind.Token => piece.Text.Length > 0 ? piece.Text[^1] : ' ',
        _ => piece.Text.Length > 0 ? piece.Text[^1] : ' ',
    };

    private static char FirstContentChar(FormattedPiece piece) => piece.Kind switch
    {
        PieceKind.Code => piece.Text.Length > 0 ? piece.Text[0] : '`',
        _ => piece.Text.Length > 0 ? piece.Text[0] : ' ',
    };

    private static UIElement CreateCodeBlockElement(
        string code,
        string? lang,
        FontFamily monoFont,
        bool resultActionsEnabled = true)
    {
        var outerBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 6, 0, 8),
            Padding = new Thickness(10, 8, 10, 8)
        };
        outerBorder.SetResourceReference(Border.BackgroundProperty, "InputBrush");
        outerBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header with language tag and copy button
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        if (!string.IsNullOrEmpty(lang))
        {
            var langBadge = new TextBlock
            {
                Text = lang.ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            langBadge.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            header.Children.Add(langBadge);
        }

        var copyButton = new Button
        {
            Content = "复制",
            Width = 64,
            FontSize = 10.5,
            Padding = new Thickness(8, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        if (!resultActionsEnabled)
        {
            // The session is partial/incomplete: the dynamically created code
            // block obeys the same eligibility as the outer result actions.
            copyButton.IsEnabled = false;
            copyButton.ToolTip = "内容不完整，复制已禁用";
        }
        copyButton.Click += async (_, _) =>
        {
            copyButton.IsEnabled = false;
            copyButton.Content = await PopGlot.Windows.Sections.Helpers.CopyToClipboardAsync(code)
                ? "已复制"
                : "复制失败";
            await Task.Delay(1200);
            copyButton.Content = "复制";
            copyButton.IsEnabled = resultActionsEnabled;
        };
        header.Children.Add(copyButton);

        var codeBox = new TextBox
        {
            Text = code,
            FontFamily = monoFont,
            FontSize = 12.5,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
        };
        codeBox.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush");
        Grid.SetRow(codeBox, 1);

        grid.Children.Add(header);
        grid.Children.Add(codeBox);
        outerBorder.Child = grid;
        return outerBorder;
    }

    private static bool IsTableLine(string line)
    {
        var t = line.Trim();
        return SplitTableRow(t).Count > 1;
    }

    private static bool IsTableDelimiterLine(string line)
    {
        var t = line.Trim();
        var cells = SplitTableRow(t);
        if (cells.Count == 0) return false;
        foreach (var cell in cells)
        {
            var c = cell.Trim();
            if (!Regex.IsMatch(c, @"^:?-{3,}:?$"))
            {
                return false;
            }
        }
        return true;
    }

    private static Block CreateTableBlock(
        List<string> tableLines,
        FontFamily uiFont,
        ResourceDictionary resources)
    {
        var border = new Border
        {
            Margin = new Thickness(0, 8, 0, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
        };
        border.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        border.SetResourceReference(Border.BackgroundProperty, "InputBrush");

        var headerCells = SplitTableRow(tableLines[0]);
        var colCount = headerCells.Count;
        if (colCount == 0)
        {
            var fallback = new Paragraph { FontFamily = uiFont };
            fallback.Inlines.Add(new Run(string.Join("\n", tableLines)));
            return fallback;
        }

        var alignments = new TextAlignment[colCount];
        if (tableLines.Count > 1)
        {
            var alignCells = SplitTableRow(tableLines[1]);
            for (var c = 0; c < colCount; c++)
            {
                if (c < alignCells.Count)
                {
                    var ac = alignCells[c].Trim();
                    if (ac.StartsWith(':') && ac.EndsWith(':'))
                    {
                        alignments[c] = TextAlignment.Center;
                    }
                    else if (ac.EndsWith(':'))
                    {
                        alignments[c] = TextAlignment.Right;
                    }
                    else
                    {
                        alignments[c] = TextAlignment.Left;
                    }
                }
                else
                {
                    alignments[c] = TextAlignment.Left;
                }
            }
        }

        var grid = new Grid();
        for (var c = 0; c < colCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var rowCount = tableLines.Count - 1; // 1 header + (N - 2) data rows
        for (var r = 0; r < rowCount; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var headerBg = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(5, 5, 0, 0),
        };
        headerBg.SetResourceReference(Border.BackgroundProperty, "SurfaceMutedBrush");
        headerBg.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
        Grid.SetRow(headerBg, 0);
        Grid.SetColumnSpan(headerBg, colCount);
        grid.Children.Add(headerBg);

        for (var c = 0; c < colCount; c++)
        {
            var cellBorder = new Border
            {
                Padding = new Thickness(10, 6, 10, 6),
                BorderThickness = new Thickness(0, 0, c < colCount - 1 ? 1 : 0, 0),
            };
            cellBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

            var cellText = new TextBlock
            {
                FontFamily = uiFont,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = alignments[c],
                TextWrapping = TextWrapping.Wrap,
            };
            cellText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var headerParagraph = new Paragraph();
            AppendFormattedSpans(headerParagraph.Inlines, headerCells[c], resources);
            foreach (var inline in headerParagraph.Inlines.ToList())
            {
                headerParagraph.Inlines.Remove(inline);
                cellText.Inlines.Add(inline);
            }
            cellBorder.Child = cellText;

            Grid.SetRow(cellBorder, 0);
            Grid.SetColumn(cellBorder, c);
            grid.Children.Add(cellBorder);
        }

        for (var r = 2; r < tableLines.Count; r++)
        {
            var gridRow = r - 1;
            var dataCells = SplitTableRow(tableLines[r]);

            for (var c = 0; c < colCount; c++)
            {
                var isLastRow = gridRow == rowCount - 1;
                var cellBorder = new Border
                {
                    Padding = new Thickness(10, 5, 10, 5),
                    BorderThickness = new Thickness(0, 0, c < colCount - 1 ? 1 : 0, isLastRow ? 0 : 1),
                };
                cellBorder.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");

                var cellContent = c < dataCells.Count ? dataCells[c] : string.Empty;
                var cellText = new TextBlock
                {
                    FontFamily = uiFont,
                    TextAlignment = alignments[c],
                    TextWrapping = TextWrapping.Wrap,
                };
                cellText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

                var cellPara = new Paragraph();
                AppendFormattedSpans(cellPara.Inlines, cellContent, resources);
                foreach (var inline in cellPara.Inlines.ToList())
                {
                    cellPara.Inlines.Remove(inline);
                    cellText.Inlines.Add(inline);
                }

                cellBorder.Child = cellText;
                Grid.SetRow(cellBorder, gridRow);
                Grid.SetColumn(cellBorder, c);
                grid.Children.Add(cellBorder);
            }
        }

        border.Child = grid;
        return new BlockUIContainer(border);
    }

    internal static List<string> SplitTableRow(string rowLine)
    {
        var trimmed = rowLine.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];
        var result = new List<string>();
        var cell = new StringBuilder();
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] == '|')
            {
                var slashCount = 0;
                for (var j = i - 1; j >= 0 && trimmed[j] == '\\'; j--) slashCount++;
                if (slashCount % 2 == 1)
                {
                    cell.Length--; // Only consume the escape for this pipe.
                    cell.Append('|');
                    continue;
                }
                result.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(trimmed[i]);
            }
        }
        result.Add(cell.ToString().Trim());
        return result;
    }

    private static bool EndsWithMarkdownHardBreak(string line)
    {
        var slashes = 0;
        for (var i = line.Length - 1; i >= 0 && line[i] == '\\'; i--) slashes++;
        return slashes % 2 == 1;
    }

    private static string PlainTableCell(string cell) => TransformNaturalSegments(cell, static segment =>
    {
        var unwrapped = Regex.Replace(segment, @"(\*\*|__)(?<content>.*?)\1", match => match.Groups["content"].Value);
        return StripNaturalEmphasis(unwrapped);
    });

}
