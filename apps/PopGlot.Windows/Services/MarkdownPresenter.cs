using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PopGlot.Windows.Services;

/// <summary>
/// Converts raw translation markdown and technical text into pixel-perfect WPF FlowDocument/Inlines.
/// Formats inline code, code blocks, bold text, lists, and auto-applies CJK-Latin Pangu spacing.
/// </summary>
internal static partial class MarkdownPresenter
{
    // Auto-spacing between CJK and English/numbers (Pangu spacing algorithm)
    [GeneratedRegex(@"([\u4e00-\u9fa5\u3040-\u30ff])([a-zA-Z0-9_\$#@`])")]
    private static partial Regex CjkToLatinRegex();

    [GeneratedRegex(@"([a-zA-Z0-9_\$#@`%])([\u4e00-\u9fa5\u3040-\u30ff])")]
    private static partial Regex LatinToCjkRegex();

    public static string FormatPangu(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s1 = CjkToLatinRegex().Replace(text, "$1 $2");
        return LatinToCjkRegex().Replace(s1, "$1 $2");
    }

    /// <summary>
    /// Renders markdown formatted blocks into a RichTextBox or StackPanel container.
    /// </summary>
    public static void RenderToFlowDocument(
        FlowDocument document,
        string markdownText,
        ResourceDictionary resources)
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
                    var codeBlock = CreateCodeBlockElement(codeContent, codeLanguage, monoFont, textPrimaryBrush, inputBrush, borderSubtleBrush, accentBrush);
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
                continue;
            }

            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 2, 0, 4),
                LineHeight = 22,
                FontFamily = uiFont,
            };

            // Bullet points
            if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal) || line.TrimStart().StartsWith("* ", StringComparison.Ordinal))
            {
                var bulletIndex = line.IndexOfAny(['-', '*']);
                var bulletContent = line[(bulletIndex + 2)..];

                var bulletDot = new Run(" • ")
                {
                    Foreground = accentBrush,
                    FontWeight = FontWeights.Bold
                };
                paragraph.Inlines.Add(bulletDot);
                AppendFormattedSpans(paragraph.Inlines, bulletContent, resources);
            }
            else
            {
                AppendFormattedSpans(paragraph.Inlines, line, resources);
            }

            document.Blocks.Add(paragraph);
        }

        // Handle unclosed code block if any
        if (inCodeBlock && codeBlockBuilder.Length > 0)
        {
            var codeContent = codeBlockBuilder.ToString().TrimEnd();
            var codeBlock = CreateCodeBlockElement(codeContent, codeLanguage, monoFont, textPrimaryBrush, inputBrush, borderSubtleBrush, accentBrush);
            document.Blocks.Add(new BlockUIContainer(codeBlock));
        }
    }

    private static void AppendFormattedSpans(
        InlineCollection inlines,
        string text,
        ResourceDictionary resources)
    {
        var formatted = FormatPangu(text);
        var textPrimaryBrush = (Brush)(resources["TextPrimaryBrush"] ?? Brushes.White);
        var accentBrush = (Brush)(resources["AccentBrush"] ?? Brushes.Teal);
        var accentSoftBrush = (Brush)(resources["AccentSoftBrush"] ?? Brushes.DarkSlateGray);
        var monoFont = (FontFamily)(resources["MonoFontFamily"] ?? new FontFamily("Cascadia Mono, Consolas"));

        // Match inline tokens: `code` or **bold** or [term]
        var pattern = @"(`[^`]+`|\*\*[^*]+\*\*|⟦PG_\d{4}⟧)";
        var parts = Regex.Split(formatted, pattern);

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;

            if (part.StartsWith('`') && part.EndsWith('`') && part.Length >= 2)
            {
                var codeText = part[1..^1];
                var codeBorder = new Border
                {
                    Background = accentSoftBrush,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(2, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = codeText,
                        FontFamily = monoFont,
                        FontSize = 12.5,
                        Foreground = accentBrush,
                        FontWeight = FontWeights.Medium,
                    }
                };
                inlines.Add(new InlineUIContainer(codeBorder));
            }
            else if (part.StartsWith("**", StringComparison.Ordinal) && part.EndsWith("**", StringComparison.Ordinal) && part.Length >= 4)
            {
                var boldText = part[2..^2];
                inlines.Add(new Run(boldText)
                {
                    FontWeight = FontWeights.SemiBold,
                    Foreground = textPrimaryBrush,
                });
            }
            else if (part.StartsWith("⟦PG_", StringComparison.Ordinal) && part.EndsWith('⟧'))
            {
                // Protected token placeholder
                inlines.Add(new Run(part)
                {
                    FontFamily = monoFont,
                    Foreground = accentBrush,
                    FontWeight = FontWeights.Bold,
                });
            }
            else
            {
                inlines.Add(new Run(part)
                {
                    Foreground = textPrimaryBrush,
                });
            }
        }
    }

    private static UIElement CreateCodeBlockElement(
        string code,
        string? lang,
        FontFamily monoFont,
        Brush textPrimary,
        Brush background,
        Brush borderBrush,
        Brush accentBrush)
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
            FontSize = 10.5,
            Padding = new Thickness(8, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        copyButton.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(code);
                copyButton.Content = "已复制 ✓";
            }
            catch { }
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
