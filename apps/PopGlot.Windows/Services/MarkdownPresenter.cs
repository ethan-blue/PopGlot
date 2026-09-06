using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PopGlot.Windows.Services;

/// <summary>
/// Converts raw translation markdown and technical text into pixel-perfect WPF FlowDocument/Inlines.
/// Formats inline code, code blocks, bold text, lists, headings, and auto-applies CJK-Latin Pangu spacing.
/// </summary>
internal static partial class MarkdownPresenter
{
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
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return markdown ?? string.Empty;
        }

        var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var sb = new StringBuilder(markdown.Length);
        bool inCodeBlock = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine;
            var trimmed = line.TrimStart();

            // Handle code block fences
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock)
            {
                sb.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                sb.AppendLine();
                continue;
            }

            // Headings: # , ## , etc.
            if (trimmed.StartsWith('#'))
            {
                int hLevel = 0;
                while (hLevel < trimmed.Length && trimmed[hLevel] == '#') hLevel++;
                if (hLevel >= 1 && hLevel <= 6 && hLevel < trimmed.Length && trimmed[hLevel] == ' ')
                {
                    line = trimmed[(hLevel + 1)..].Trim();
                }
            }
            // Bullet points: - , * , +
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                     trimmed.StartsWith("* ", StringComparison.Ordinal) ||
                     trimmed.StartsWith("+ ", StringComparison.Ordinal))
            {
                line = trimmed[2..].Trim();
            }
            // Ordered list: 1. , 2) , etc.
            else
            {
                var match = Regex.Match(trimmed, @"^\d+[\.\)]\s+(.*)$");
                if (match.Success)
                {
                    line = match.Groups[1].Value.Trim();
                }
            }

            sb.AppendLine(TransformNaturalSegments(line, static segment => StripNaturalEmphasis(segment)).TrimEnd());
        }

        return sb.ToString().Trim();
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

        var lines = markdownText.Replace("\r\n", "\n").Split('\n');
        var textPrimaryBrush = (Brush)(resources["TextPrimaryBrush"] ?? Brushes.White);
        var textSecondaryBrush = (Brush)(resources["TextSecondaryBrush"] ?? Brushes.Gray);
        var accentBrush = (Brush)(resources["AccentBrush"] ?? Brushes.Teal);
        var inputBrush = (Brush)(resources["InputBrush"] ?? Brushes.DarkSlateGray);
        var borderSubtleBrush = (Brush)(resources["BorderSubtleBrush"] ?? Brushes.DimGray);
        var monoFont = (FontFamily)(resources["MonoFontFamily"] ?? new FontFamily("Cascadia Mono, Consolas"));
        var uiFont = (FontFamily)(resources["UiFontFamily"] ?? new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"));

        bool inCodeBlock = false;
        bool addParagraphSpacing = false;
        var codeBlockBuilder = new StringBuilder();
        string? codeLanguage = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine;

            // Handle code block fences
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (!inCodeBlock)
                {
                    inCodeBlock = true;
                    codeLanguage = line.TrimStart()[3..].Trim();
                    codeBlockBuilder.Clear();
                    continue;
                }
                else
                {
                    inCodeBlock = false;
                    var codeContent = codeBlockBuilder.ToString().TrimEnd();
                    var codeBlock = CreateCodeBlockElement(codeContent, codeLanguage, monoFont, textPrimaryBrush, inputBrush, borderSubtleBrush, accentBrush, resultActionsEnabled);
                    document.Blocks.Add(new BlockUIContainer(codeBlock));
                    continue;
                }
            }

            if (inCodeBlock)
            {
                codeBlockBuilder.AppendLine(line);
                continue;
            }

            // Normal paragraph line
            if (string.IsNullOrWhiteSpace(line))
            {
                addParagraphSpacing = document.Blocks.Count > 0;
                continue;
            }

            var trimmedLine = line.TrimStart();

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
                        Margin = new Thickness(0, addParagraphSpacing || document.Blocks.Count > 0 ? 6 : 0, 0, 2),
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
                    continue;
                }
            }

            var paragraph = new Paragraph
            {
                // Zero margin and default (font-metric) line height: the
                // streaming TextBox layer this replaces has neither forced
                // spacing nor a custom LineHeight, so the stream→final swap
                // must not change the card's height. Forced 22px lines and
                // per-paragraph margins were the layout jump.
                Margin = new Thickness(0, addParagraphSpacing ? 6 : 0, 0, 0),
                FontFamily = uiFont,
            };

            // Bullet points (- , * , + )
            if (trimmedLine.StartsWith("- ", StringComparison.Ordinal) ||
                trimmedLine.StartsWith("* ", StringComparison.Ordinal) ||
                trimmedLine.StartsWith("+ ", StringComparison.Ordinal))
            {
                var bulletContent = trimmedLine[2..].Trim();
                var bulletDot = new Run(" • ")
                {
                    Foreground = accentBrush,
                    FontWeight = FontWeights.Bold
                };
                paragraph.Inlines.Add(bulletDot);
                AppendFormattedSpans(paragraph.Inlines, bulletContent, resources);
            }
            // Numbered lists (1. , 2) , etc.)
            else if (Regex.Match(trimmedLine, @"^(\d+[\.\)])\s+(.*)$") is { Success: true } numMatch)
            {
                var numPrefix = numMatch.Groups[1].Value + " ";
                var numContent = numMatch.Groups[2].Value.Trim();
                var numRun = new Run(numPrefix)
                {
                    Foreground = accentBrush,
                    FontWeight = FontWeights.SemiBold
                };
                paragraph.Inlines.Add(numRun);
                AppendFormattedSpans(paragraph.Inlines, numContent, resources);
            }
            else
            {
                AppendFormattedSpans(paragraph.Inlines, line, resources);
            }

            document.Blocks.Add(paragraph);
            addParagraphSpacing = false;
        }

        // Handle unclosed code block if any
        if (inCodeBlock && codeBlockBuilder.Length > 0)
        {
            var codeContent = codeBlockBuilder.ToString().TrimEnd();
            var codeBlock = CreateCodeBlockElement(codeContent, codeLanguage, monoFont, textPrimaryBrush, inputBrush, borderSubtleBrush, accentBrush, resultActionsEnabled);
            document.Blocks.Add(new BlockUIContainer(codeBlock));
        }
    }

    private static void AppendFormattedSpans(
        InlineCollection inlines,
        string text,
        ResourceDictionary resources)
    {
        var textPrimaryBrush = (Brush)(resources["TextPrimaryBrush"] ?? Brushes.White);
        var accentBrush = (Brush)(resources["AccentBrush"] ?? Brushes.Teal);
        var accentSoftBrush = (Brush)(resources["AccentSoftBrush"] ?? Brushes.DarkSlateGray);
        var monoFont = (FontFamily)(resources["MonoFontFamily"] ?? new FontFamily("Cascadia Mono, Consolas"));

        // Structure first: split on code spans / bold / protected tokens, then
        // apply Pangu spacing per natural-language segment only. Spacing the
        // whole line up front used to push spaces INSIDE code spans and paths.
        var pattern = @"(`[^`]+`|\*\*[^*]+\*\*|__[^_]+__|⟦PG_\d{4}⟧)";
        var parts = Regex.Split(text, pattern);

        // Plan the pieces as plain text first so Pangu spacing can cross the
        // seam between a natural segment and an adjacent token (使用`ls`命令
        // renders as 使用 ls 命令) without ever touching token contents.
        var pieces = new List<(PieceKind Kind, string Text)>();
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            if (part.StartsWith('`') && part.EndsWith('`') && part.Length >= 2)
            {
                pieces.Add((PieceKind.Code, part[1..^1]));
            }
            else if ((part.StartsWith("**", StringComparison.Ordinal) && part.EndsWith("**", StringComparison.Ordinal) && part.Length >= 4) ||
                     (part.StartsWith("__", StringComparison.Ordinal) && part.EndsWith("__", StringComparison.Ordinal) && part.Length >= 4))
            {
                pieces.Add((PieceKind.Bold, part[2..^2]));
            }
            else if (part.StartsWith("⟦PG_", StringComparison.Ordinal) && part.EndsWith('⟧'))
            {
                pieces.Add((PieceKind.Token, part));
            }
            else
            {
                pieces.Add((PieceKind.Natural, part));
            }
        }

        for (var index = 0; index < pieces.Count; index++)
        {
            var (kind, pieceText) = pieces[index];
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
                    inlines.Add(new Run(spaced)
                    {
                        FontWeight = FontWeights.SemiBold,
                        Foreground = textPrimaryBrush,
                    });
                }
                else
                {
                    inlines.Add(new Run(spaced) { Foreground = textPrimaryBrush });
                }
            }
            else if (kind == PieceKind.Code)
            {
                var codeBorder = new Border
                {
                    Background = accentSoftBrush,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(2, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = pieceText,
                        FontFamily = monoFont,
                        FontSize = 12.5,
                        Foreground = accentBrush,
                        FontWeight = FontWeights.Medium,
                    }
                };
                inlines.Add(new InlineUIContainer(codeBorder));
            }
            else
            {
                // Protected token placeholder
                inlines.Add(new Run(pieceText)
                {
                    FontFamily = monoFont,
                    Foreground = accentBrush,
                    FontWeight = FontWeights.Bold,
                });
            }
        }
    }

    private enum PieceKind
    {
        Natural,
        Code,
        Bold,
        Token,
    }

    // The character classes mirror FormatPangu's CJK↔Latin regexes so seams
    // behave exactly like intra-segment spacing.
    private static bool IsLatinish(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '$' or '#' or '@' or '`' or '%';

    private static bool IsCjkish(char c) => c is >= '\u3040' and <= '\u30FF' or >= '\u4E00' and <= '\u9FA5';

    private static bool NeedsSeamSpace(char left, char right) =>
        !char.IsWhiteSpace(left) && !char.IsWhiteSpace(right) &&
        ((IsCjkish(left) && IsLatinish(right)) || (IsLatinish(left) && IsCjkish(right)));

    private static char LastContentChar((PieceKind Kind, string Text) piece) => piece.Kind switch
    {
        PieceKind.Code => piece.Text.Length > 0 ? piece.Text[^1] : '`',
        PieceKind.Token => piece.Text.Length > 0 ? piece.Text[^1] : ' ',
        _ => piece.Text.Length > 0 ? piece.Text[^1] : ' ',
    };

    private static char FirstContentChar((PieceKind Kind, string Text) piece) => piece.Kind switch
    {
        PieceKind.Code => piece.Text.Length > 0 ? piece.Text[0] : '`',
        _ => piece.Text.Length > 0 ? piece.Text[0] : ' ',
    };

    private static UIElement CreateCodeBlockElement(
        string code,
        string? lang,
        FontFamily monoFont,
        Brush textPrimary,
        Brush background,
        Brush borderBrush,
        Brush accentBrush,
        bool resultActionsEnabled = true)
    {
        var outerBorder = new Border
        {
            Background = background,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 6, 0, 8),
            Padding = new Thickness(10, 8, 10, 8)
        };

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
                Foreground = accentBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
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
            Foreground = textPrimary,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
        };
        Grid.SetRow(codeBox, 1);

        grid.Children.Add(header);
        grid.Children.Add(codeBox);
        outerBorder.Child = grid;
        return outerBorder;
    }
}
