using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using PopGlot.Windows.Services;

namespace PopGlot.Windows;

/// <summary>
/// C25 离线帮助查看器：渲染随包分发的本机 Markdown 帮助文档（help/ 目录）。
/// 纯本地、零网络——找不到文档时给出诚实的本机提示，绝不尝试在线拉取。
/// </summary>
public partial class HelpWindow : Window
{
    /// <summary>帮助目录与文件名的单一事实来源；测试据此守文档漂移。</summary>
    internal static readonly (string Title, string File)[] Articles =
    {
        ("帮助首页", "index.md"),
        ("快速上手", "getting-started.md"),
        ("隐私与数据安全", "privacy-and-security.md"),
        ("快捷键全景", "keyboard-shortcuts.md"),
        ("翻译引擎配置", "provider-setup/index.md"),
        ("故障排查", "troubleshooting.md"),
    };

    private bool _suppressListEvents;

    public HelpWindow() : this(null)
    {
    }

    public HelpWindow(string? initialArticleFile)
    {
        InitializeComponent();
        var missing = new List<string>();
        foreach (var (title, file) in Articles)
        {
            var item = new ListBoxItem { Content = title };
            if (ResolveArticlePath(file) is null)
            {
                missing.Add(file);
                item.IsEnabled = false;
                item.ToolTip = "本机文档缺失";
            }
            ArticleList.Items.Add(item);
        }

        if (missing.Count > 0)
        {
            var missingText = string.Join("、", missing);
            ArticleMeta.Text = $"本机文档缺失：{missingText}。请重新安装或解压完整的 PopGlot 包。";
        }

        if (ResolveHelpRoot() is null)
        {
            // 整个 help/ 目录都不在：诚实降级为占位说明，绝不联网补内容。
            ArticleList.IsEnabled = false;
            RenderFallback("帮助文档不存在", "没有找到随包分发的本机帮助目录（help/）。请重新解压完整的 PopGlot 压缩包后再试。");
        }
        else
        {
            _suppressListEvents = true;
            var targetIndex = 0;
            if (!string.IsNullOrEmpty(initialArticleFile))
            {
                for (var i = 0; i < Articles.Length; i++)
                {
                    if (string.Equals(Articles[i].File, initialArticleFile, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Articles[i].Title, initialArticleFile, StringComparison.OrdinalIgnoreCase))
                    {
                        targetIndex = i;
                        break;
                    }
                }
            }
            ArticleList.SelectedIndex = targetIndex;
            _suppressListEvents = false;
            LoadSelectedArticle();
        }
    }

    public void SelectArticle(string fileOrTitle)
    {
        if (string.IsNullOrEmpty(fileOrTitle)) return;
        for (var i = 0; i < Articles.Length; i++)
        {
            if (string.Equals(Articles[i].File, fileOrTitle, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Articles[i].Title, fileOrTitle, StringComparison.OrdinalIgnoreCase))
            {
                ArticleList.SelectedIndex = i;
                break;
            }
        }
    }

    /// <summary>help/ 根目录：随包分发目录优先，开发/测试宿主回退仓库 docs/help。</summary>
    internal static string? ResolveHelpRoot()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "help");
        if (Directory.Exists(packaged))
        {
            return packaged;
        }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "help");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    internal static string? ResolveArticlePath(string file)
    {
        var root = ResolveHelpRoot();
        return root is null ? null : Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
    }

    private void ArticleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressListEvents)
        {
            return;
        }
        LoadSelectedArticle();
    }

    private void LoadSelectedArticle()
    {
        if (ArticleList.SelectedItem is not ListBoxItem selected || selected.Content is not string title)
        {
            return;
        }
        var index = -1;
        for (var i = 0; i < Articles.Length; i++)
        {
            if (Articles[i].Title == title)
            {
                index = i;
                break;
            }
        }
        if (index < 0)
        {
            return;
        }
        var (articleTitle, file) = Articles[index];
        var path = ResolveArticlePath(file);
        if (path is null || !File.Exists(path))
        {
            ArticleTitle.Text = articleTitle;
            RenderFallback(articleTitle, "这篇文档在本机缺失。请重新解压完整的 PopGlot 压缩包。");
            return;
        }

        string markdown;
        try
        {
            // 帮助文档是小文件（全目录 < 64 KiB），同步读取即可「毫秒级打开」。
            markdown = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ArticleTitle.Text = articleTitle;
            RenderFallback(articleTitle, $"无法读取本机文档：{exception.Message}");
            return;
        }

        ArticleTitle.Text = articleTitle;
        ArticleViewer.Document.Blocks.Clear();
        MarkdownPresenter.RenderToFlowDocument(
            ArticleViewer.Document,
            StripLocalMarkdownLinks(markdown),
            Application.Current?.Resources ?? new ResourceDictionary(),
            resultActionsEnabled: true);
        ArticleViewer.ScrollToHome();
    }

    /// <summary>
    /// 随包文档里的 `[文字](xxx.md)` 是给 GitHub 用的仓库内链接；查看器里的
    /// 文章切换由左侧目录承担，渲染前剥掉链接语法只留文字。外部 http(s)
    /// 链接同样剥壳（本窗口承诺离线，不允许点开外链的错觉）。
    /// </summary>
    internal static string StripLocalMarkdownLinks(string markdown)
    {
        if (string.IsNullOrEmpty(markdown) || !markdown.Contains("]("))
        {
            return markdown;
        }
        return System.Text.RegularExpressions.Regex.Replace(
            markdown,
            @"\[([^\]\n]+)\]\((?:https?://|[^)\s]+\.md)\)",
            "$1");
    }

    private void RenderFallback(string title, string message)
    {
        ArticleViewer.Document.Blocks.Clear();
        MarkdownPresenter.RenderToFlowDocument(
            ArticleViewer.Document,
            $"# {title}\n\n{message}\n",
            Application.Current?.Resources ?? new ResourceDictionary(),
            resultActionsEnabled: false);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        var maximized = WindowState == WindowState.Maximized;
        Ui.SetIcon(MaximizeBtn, (System.Windows.Media.Geometry)FindResource(
            maximized ? "IconCaptionRestore" : "IconCaptionMax"));
        AutomationProperties.SetName(MaximizeBtn, maximized ? "还原" : "最大化");
        MaximizeBtn.ToolTip = maximized ? "还原" : "最大化";
    }

    private void HelpWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
