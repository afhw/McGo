using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using McGo.Core.App;
using McGo.Core.Downloads;
using McGo.Core.Logging;
using McGo.WinUI.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace McGo.WinUI;

public partial class App : Application
{
    private Window? _window;
    private static string? _smokeReportPath;
    private static string? _smokeScreenshotDirectory;
    private static string? _smokePageTag;
    private static string? _smokeWorkingDirectory;
    private static string? _smokeGameDirectoryOverride;
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Contains("--installer", StringComparer.OrdinalIgnoreCase) ||
            commandLine.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            _window = new InstallerWindow(InstallerLaunchOptions.Parse(commandLine));
            MainWindow = _window;
            _window.Activate();
            return;
        }

        var smokePages = commandLine.Contains("--smoke-pages", StringComparer.OrdinalIgnoreCase);
        if (smokePages)
        {
            _smokeReportPath = commandLine
                .FirstOrDefault(value => value.StartsWith("--smoke-report=", StringComparison.OrdinalIgnoreCase))?
                ["--smoke-report=".Length..];
            _smokeScreenshotDirectory = commandLine
                .FirstOrDefault(value => value.StartsWith("--smoke-screenshot-dir=", StringComparison.OrdinalIgnoreCase))?
                ["--smoke-screenshot-dir=".Length..];
            _smokePageTag = commandLine
                .FirstOrDefault(value => value.StartsWith("--smoke-page=", StringComparison.OrdinalIgnoreCase))?
                ["--smoke-page=".Length..];
            if (!string.IsNullOrWhiteSpace(_smokeScreenshotDirectory))
            {
                _smokeScreenshotDirectory = Path.GetFullPath(_smokeScreenshotDirectory);
                Directory.CreateDirectory(_smokeScreenshotDirectory);
            }
            // 工作目录始终用临时目录：SaveConfig 需要可写位置，
            // 指到真实游戏目录会撞上只读的配置文件。真实数据目录只作为游戏目录传给页面。
            _smokeWorkingDirectory = Path.Combine(Path.GetTempPath(), $"mcgo-page-smoke-{Environment.ProcessId}");
            Directory.CreateDirectory(_smokeWorkingDirectory);
            var smokeGameDir = commandLine.FirstOrDefault(value => value.StartsWith("--smoke-game-dir=", StringComparison.OrdinalIgnoreCase))?
                ["--smoke-game-dir=".Length..];
            if (!string.IsNullOrWhiteSpace(smokeGameDir))
            {
                _smokeGameDirectoryOverride = Path.GetFullPath(smokeGameDir);
            }
        }
        else
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        }

        AppLog.Info("App", $"McGo {AppConstants.AppVersion} 启动，进程 {Environment.ProcessId}，架构 {RuntimeInformation.ProcessArchitecture}。");

        _window = new MainWindow();
        if (!string.IsNullOrWhiteSpace(_smokeGameDirectoryOverride) && _window is MainWindow smokeWindow)
        {
            smokeWindow.ApplySmokeGameDirectory(_smokeGameDirectoryOverride);
        }
        MainWindow = _window;
        _window.Activate();

        if (smokePages)
        {
            if (commandLine.Contains("--smoke-global-task", StringComparer.OrdinalIgnoreCase))
            {
                DownloadTaskManager.Current.Enqueue("smoke", "全局下载视觉烟测", async (record, token) =>
                {
                    DownloadTaskManager.Current.Update(record, item =>
                    {
                        item.Progress = 42;
                        item.Detail = "正在验证跨页面任务状态";
                    });
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return "不会完成";
                });
            }

            _ = RunPageSmokeAsync((MainWindow)_window);
            return;
        }

        MusicPlaybackService.ApplySavedSettings();
    }

    private static async Task RunPageSmokeAsync(MainWindow window)
    {
        var results = new List<string>();
        var failed = false;
        window.RunSmokeChecks();
        var targets = new[]
        {
            new Windows.Graphics.SizeInt32(820, 720),
            new Windows.Graphics.SizeInt32(1440, 900),
            new Windows.Graphics.SizeInt32(1920, 1080),
        };
        foreach (var requested in targets)
        {
            // 物理屏幕放不下的尺寸无法真实测到：窗口会被挤出屏幕，
            // 截图和布局都会失真。这里如实报告实际测到的尺寸，不假装测过。
            var size = FitToScreen(window, requested);
            ResizeToClientSize(window, size);
            await WaitForLayoutAsync(window, size);
            if (size.Width != requested.Width || size.Height != requested.Height)
            {
                results.Add($"SKIP {requested.Width}x{requested.Height}: 屏幕不足，实际按 {size.Width}x{size.Height} 测量");
            }
            var pageTags = new[] { "home", "launch", "download", "resources", "modpacks", "online", "accounts", "settings", "logs", "about" };
            foreach (var tag in pageTags.Where(tag => string.IsNullOrWhiteSpace(_smokePageTag) || string.Equals(tag, _smokePageTag, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    window.NavigateToTag(tag);
                    await Task.Delay(string.IsNullOrWhiteSpace(_smokeScreenshotDirectory) ? 150 : 650);
                    switch (window.CurrentPage)
                    {
                        case Pages.HomePage homePage:
                            homePage.RunSmokeChecks();
                            break;
                        case Pages.AccountsPage accountsPage:
                            accountsPage.RunSmokeChecks();
                            break;
                        case Pages.DownloadPage downloadPage:
                            downloadPage.RunSmokeChecks();
                            break;
                        case Pages.LaunchPage launchPage:
                            launchPage.RunSmokeChecks();
                            break;
                        case Pages.ModpacksPage modpacksPage:
                            modpacksPage.RunSmokeChecks();
                            break;
                        case Pages.OnlinePage onlinePage:
                            onlinePage.RunSmokeChecks();
                            break;
                        case Pages.ResourcesPage resourcesPage:
                            resourcesPage.RunSmokeChecks();
                            break;
                        case Pages.SettingsPage settingsPage:
                            settingsPage.RunSmokeChecks();
                            break;
                        case Pages.LogsPage logsPage:
                            logsPage.RunSmokeChecks();
                            break;
                    }

                    // 布局与文案健康检查：宁可测试误报，也不要让用户看到坏界面
                    if (window.CurrentPage is FrameworkElement pageRoot)
                    {
                        var overflow = FindHorizontalOverflow(pageRoot);
                        if (overflow.Count > 0)
                        {
                            throw new InvalidOperationException(
                                $"页面 {tag} 在 {size.Width}x{size.Height} 下横向溢出：" + string.Join("；", overflow));
                        }

                        var textProblems = FindTextProblems(pageRoot);
                        if (textProblems.Count > 0)
                        {
                            throw new InvalidOperationException(
                                $"页面 {tag} 在 {size.Width}x{size.Height} 下有乱码文案：" + string.Join("；", textProblems));
                        }

                        var clipped = FindClippedTexts(pageRoot);
                        if (clipped.Count > 0)
                        {
                            throw new InvalidOperationException(
                                $"页面 {tag} 在 {size.Width}x{size.Height} 下有被裁掉的文字：" + string.Join("；", clipped));
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(_smokeScreenshotDirectory))
                    {
                        await CaptureSmokeScreenshotAsync(window, size, tag);
                        if (window.CurrentPage is Pages.LaunchPage launchPage)
                        {
                            launchPage.SelectVersionSettingsForSmoke(settings: true);
                            await Task.Delay(150);
                            await CaptureSmokeScreenshotAsync(window, size, "launch-settings");
                            launchPage.SelectVersionSettingsForSmoke(settings: false);
                        }
                        else if (window.CurrentPage is Pages.AccountsPage accountsPage)
                        {
                            foreach (var mode in new[] { "offline", "microsoft", "external" })
                            {
                                accountsPage.SelectLoginModeForSmoke(mode);
                                await Task.Delay(150);
                                await CaptureSmokeScreenshotAsync(window, size, "accounts-" + mode);
                            }

                            accountsPage.SelectLoginModeForSmoke("overview");
                        }
                        else if (window.CurrentPage is Pages.OnlinePage onlinePage)
                        {
                            foreach (var section in new[] { "nat", "servers", "capsule" })
                            {
                                onlinePage.SelectOnlineSectionForSmoke(section);
                                await Task.Delay(150);
                                await CaptureSmokeScreenshotAsync(window, size, "online-" + section);
                            }

                            onlinePage.SelectOnlineSectionForSmoke("p2p");
                        }
                        else if (window.CurrentPage is Pages.ModpacksPage modpacksPage)
                        {
                            foreach (var section in new[] { "export", "capsule", "crash" })
                            {
                                modpacksPage.SelectModpackSectionForSmoke(section);
                                await Task.Delay(150);
                                await CaptureSmokeScreenshotAsync(window, size, "modpacks-" + section);
                            }

                            modpacksPage.SelectModpackSectionForSmoke("import");
                        }
                        else if (window.CurrentPage is Pages.DownloadPage downloadPage)
                        {
                            foreach (var section in new[] { "addons", "tasks" })
                            {
                                downloadPage.SelectDownloadSectionForSmoke(section);
                                await Task.Delay(75);
                                downloadPage.BringDownloadSectionIntoViewForSmoke(section);
                                await Task.Delay(150);
                                await CaptureSmokeScreenshotAsync(window, size, "download-" + section);
                            }

                            downloadPage.SelectDownloadSectionForSmoke("vanilla");
                        }
                        else if (window.CurrentPage is Pages.SettingsPage settingsPage)
                        {
                            foreach (var section in new[] { "download", "appearance", "music", "update" })
                            {
                                settingsPage.SelectSettingsSectionForSmoke(section);
                                await Task.Delay(150);
                                await CaptureSmokeScreenshotAsync(window, size, "settings-" + section);
                            }

                            settingsPage.SelectSettingsSectionForSmoke("environment");
                        }
                        else if (window.CurrentPage is Pages.ResourcesPage resourcesPage)
                        {
                            resourcesPage.SelectResourceSectionForSmoke("local");
                            await Task.Delay(75);
                            resourcesPage.BringResourceSectionIntoViewForSmoke("local");
                            await Task.Delay(150);
                            await CaptureSmokeScreenshotAsync(window, size, "resources-local");
                            resourcesPage.SelectResourceSectionForSmoke("market");
                        }
                    }

                    results.Add($"PASS {size.Width}x{size.Height} {tag}");
                }
                catch (Exception exc)
                {
                    failed = true;
                    results.Add($"FAIL {size.Width}x{size.Height} {tag}: {exc}");
                }
            }
        }

        WriteSmokeReport(results);
        CleanupSmokeWorkingDirectory();
        Environment.Exit(failed ? 1 : 0);
    }

    /// <summary>
    /// 把目标尺寸裁剪到当前屏幕实际可容纳的范围（单位 DIP）。
    /// 否则窗口会被推出屏幕，截图只截到残片，测出来的也不是目标尺寸的布局。
    /// </summary>
    private static Windows.Graphics.SizeInt32 FitToScreen(MainWindow window, Windows.Graphics.SizeInt32 requested)
    {
        // DisplayArea.WorkArea 返回的是物理像素，AppWindow.MoveAndResize 也吃物理像素，
        // 所以这里不要再用 DPI 去换算，否则会把可容纳尺寸算小（2K + 150% 会被误判成 1707x888）。
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;
        // requested 是 DIP，换算成物理像素再比较
        var requestedPhysicalWidth = (int)Math.Round(requested.Width * scale);
        var requestedPhysicalHeight = (int)Math.Round(requested.Height * scale);
        var fits = requestedPhysicalWidth <= work.Width && requestedPhysicalHeight <= work.Height;
        if (fits)
        {
            return requested;
        }

        // 用工作区的物理像素换算回 DIP 作为可容纳上限
        var maxWidth = (int)Math.Floor(work.Width / scale);
        var maxHeight = (int)Math.Floor(work.Height / scale);
        return new Windows.Graphics.SizeInt32(
            Math.Min(requested.Width, maxWidth),
            Math.Min(requested.Height, maxHeight));
    }

    /// <summary>
    /// 找出横向溢出：任何子元素超出其可视祖先的右边界即为布局缺陷。
    /// 这类问题靠肉眼看截图很容易漏，必须自动检测。
    /// </summary>
    private static List<string> FindHorizontalOverflow(FrameworkElement root, double tolerance = 2.0)
    {
        var problems = new List<string>();
        // 以可视窗口宽度为准；页面根元素在水平 Disabled 的 ScrollViewer 里
        // 可能按内容期望宽度排布，用它自己的宽度会漏检真实溢出。
        var viewportRight = root.XamlRoot?.Size.Width ?? root.ActualWidth;
        var rootRight = viewportRight;
        Walk(root);

        void Walk(DependencyObject node)
        {
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is not FrameworkElement element || element.Visibility == Visibility.Collapsed)
                {
                    continue;
                }

                if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
                {
                    continue;
                }

                // 只看用户可见内容的实际右边界；装饰性内部元素（如 SelectorBar 的选中条）
                // 与水平滚动容器不参与判定，避免误报。
                if (IsDecoration(element))
                {
                    Walk(element);
                    continue;
                }

                // 元素被裁剪在其祖先范围内属正常（ScrollViewer 内部滚动），
                // 只有真正超出窗口右边界才算溢出缺陷。
                var transform = element.TransformToVisual(root);
                var origin = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                var right = origin.X + element.ActualWidth;
                if (right > rootRight + tolerance)
                {
                    var name = string.IsNullOrWhiteSpace(element.Name) ? element.GetType().Name : element.Name;
                    problems.Add($"{name} 右边界 {right:F0} 超出 {rootRight:F0}");
                }

                Walk(element);
            }
        }

        return problems.Take(5).ToList();
    }

    /// <summary>
    /// 文案健康检查：抓两类真正会让人一眼看出"界面坏了"的问题。
    /// 1) 乱码标识符泄漏到界面（曾经的 Java→Jaaa、App→ppp 事故）；
    /// 2) 可见文字被容器裁掉（曾经的"更新"被截成"更多..."）。
    /// </summary>
    private static List<string> FindTextProblems(FrameworkElement root)
    {
        var problems = new List<string>();
        var suspicious = new[]
        {
            "Jaaa", "jaaa", "ppp", "pccount", "Seraice", "psync",
            "Naaigate", "Responsiae", "priaate", "pctual", "Inaalid",
            "aar ", "Eaentprgs", "pllow", "JpVp", "TODO", "FIXME",
        };

        foreach (var text in EnumerateVisibleTexts(root))
        {
            foreach (var token in suspicious)
            {
                if (text.Contains(token, StringComparison.Ordinal))
                {
                    problems.Add($"{text} ← 含疑似乱码「{token}」");
                    break;
                }
            }
        }

        return problems.Distinct().Take(6).ToList();
    }

    /// <summary>
    /// 检查文字是否被容器横向裁掉：文字自然宽度明显超过控件可用宽度时，
    /// 说明用户看到的是省略号或不完整文案。
    /// </summary>
    private static List<string> FindClippedTexts(FrameworkElement root)
    {
        var problems = new List<string>();
        foreach (var block in Enumerate<TextBlock>(root))
        {
            if (block.Visibility != Visibility.Visible || string.IsNullOrWhiteSpace(block.Text))
            {
                continue;
            }

            // 明确会换行或会用省略号的文字不算裁切（那是设计选择）。
            // 默认 TextWrapping 是 NoWrap，不能直接比较枚举值。
            if (block.TextTrimming != TextTrimming.None || block.TextWrapping == TextWrapping.Wrap)
            {
                continue;
            }

            var available = block.ActualWidth - block.Padding.Left - block.Padding.Right;
            if (available <= 0)
            {
                continue;
            }

            // PlaceholderHeader 这类模板内部文字有自己的可用宽度规则，跳过
            if (IsInsideTemplate(block))
            {
                continue;
            }

            // 元素没进可视树时 DesiredSize 不可靠，这里把测量元素临时挂到页面根上，
            // 量完立刻移除，既拿到真实自然宽度又不影响最终布局。
            var natural = MeasureNaturalWidth(root, block);
            if (natural > available + 2)
            {
                problems.Add($"{block.Text}（需要 {natural:F0}，只有 {available:F0}）");
            }
        }

        return problems.Distinct().Take(6).ToList();
    }

    /// <summary>
    /// 测量文字的自然宽度：临时挂到页面根的隐藏容器里参与一次测量，随后移除。
    /// 直接对游离元素调用 Measure 拿不到正确结果。
    /// </summary>
    private static double MeasureNaturalWidth(FrameworkElement host, TextBlock source)
    {
        var probe = new TextBlock
        {
            Text = source.Text,
            FontFamily = source.FontFamily,
            FontSize = source.FontSize,
            FontWeight = source.FontWeight,
            FontStyle = source.FontStyle,
            TextWrapping = TextWrapping.NoWrap,
            Opacity = 0,
            IsHitTestVisible = false,
        };

        var staging = host as Grid ?? new Grid();
        staging.Children.Add(probe);
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = probe.DesiredSize.Width;
        staging.Children.Remove(probe);
        return width;
    }

    /// <summary>判断元素是否位于控件模板内部（如 TextBox 的占位符），这类文字不按页面布局判定。</summary>
    private static bool IsInsideTemplate(DependencyObject element)
    {
        var current = VisualTreeHelper.GetParent(element);
        while (current is not null)
        {
            if (current is TextBox or PasswordBox or NumberBox or ComboBox or AutoSuggestBox)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static IEnumerable<string> EnumerateVisibleTexts(FrameworkElement root)
    {
        foreach (var block in Enumerate<TextBlock>(root))
        {
            if (block.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(block.Text))
            {
                yield return block.Text;
            }
        }

        foreach (var button in Enumerate<Button>(root))
        {
            if (button.Visibility == Visibility.Visible && button.Content is string content && !string.IsNullOrWhiteSpace(content))
            {
                yield return content;
            }
        }
    }

    /// <summary>按可视树枚举指定类型的元素。</summary>
    private static IEnumerable<T> Enumerate<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Enumerate<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>水平滚动容器里的内容按设计会超出可视宽度，不参与溢出判定。</summary>
    private static bool IsHorizontallyScrollable(FrameworkElement element) =>
        element is ScrollViewer { HorizontalScrollBarVisibility: not ScrollBarVisibility.Disabled };

    /// <summary>纯装饰元素不参与溢出判定。</summary>
    private static bool IsDecoration(FrameworkElement element) =>
        element.Name is "HighlightBackground" or "HighlightPill" ||
        element.GetType().Name is "HighlightBackground" or "SelectorBarItemPill";

    /// <summary>
    /// 按“客户区”尺寸调整窗口：AppWindow.Resize 设置的是含边框的外框尺寸，
    /// 直接用它会让内容比预期小一圈，响应式断点判断也会跟着偏。
    /// </summary>
    private static void ResizeToClientSize(MainWindow window, Windows.Graphics.SizeInt32 size)
    {
        // AppWindow.MoveAndResize 的参数是物理像素，而目标 size 是 DIP。
        // AppWindow 尺寸还含边框，需补上非客户区，让“客户区”等于目标逻辑尺寸。
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;

        var root = window.SmokeCaptureRoot;
        var nonClientWidth = 0;
        var nonClientHeight = 0;
        if (root.XamlRoot is not null && root.ActualWidth > 0 && root.ActualHeight > 0)
        {
            var outer = window.AppWindow.Size;
            nonClientWidth = Math.Max(0, (int)Math.Round((outer.Width / scale) - root.ActualWidth));
            nonClientHeight = Math.Max(0, (int)Math.Round((outer.Height / scale) - root.ActualHeight));
        }

        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32
        {
            X = window.AppWindow.Position.X,
            Y = window.AppWindow.Position.Y,
            Width = (int)Math.Round((size.Width + nonClientWidth) * scale),
            Height = (int)Math.Round((size.Height + nonClientHeight) * scale),
        });
    }

    /// <summary>等到内容真正完成布局（尺寸稳定且大于 0），避免截到中间态。</summary>
    private static async Task WaitForLayoutAsync(MainWindow window, Windows.Graphics.SizeInt32 size)
    {
        var root = window.SmokeCaptureRoot;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(50);
            root.UpdateLayout();
            if (root.ActualWidth > 0 && root.ActualHeight > 0)
            {
                return;
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// 按窗口客户区真实尺寸渲染整窗截图。
    /// 关键点：
    /// 1) 渲染窗口内容根（含自定义标题栏），而不是页面内容——否则标题栏区域会被裁掉；
    /// 2) 用 RenderAsync(element, width, height) 显式指定 DIP 尺寸，避免按缩放比例取整后尺寸漂移；
    /// 3) PNG 写入真实 DPI 元数据，避免 150% 缩放下像素被错误解读。
    /// </summary>
    /// <summary>
    /// 截取窗口真实画面。
    /// 不能用 RenderTargetBitmap：WinUI 3 的 NavigationView 面板与虚拟化列表走合成层，
    /// RenderTargetBitmap 会漏画（曾出现只画出 1 个导航项、内容整体偏移）。
    /// 这里用 PrintWindow + PW_RENDERFULLCONTENT 让 DWM 把整窗合成结果画到内存位图上。
    /// </summary>
    private static async Task CaptureSmokeScreenshotAsync(MainWindow window, Windows.Graphics.SizeInt32 size, string tag)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;

        var captured = CaptureWindowPixels(hwnd, out var pixelWidth, out var pixelHeight);
        if (captured is null || pixelWidth <= 0 || pixelHeight <= 0)
        {
            throw new InvalidOperationException($"页面 {tag} 截图为空。");
        }

        var folder = await StorageFolder.GetFolderFromPathAsync(_smokeScreenshotDirectory!);
        var fileName = $"{size.Width}x{size.Height}-{tag}.png";
        var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
        await using var stream = await file.OpenStreamForWriteAsync();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
        var dpiMetadata = 96.0 * scale;
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)pixelWidth,
            (uint)pixelHeight,
            dpiMetadata,
            dpiMetadata,
            captured);
        await encoder.FlushAsync();
    }

    /// <summary>用 PrintWindow 把窗口内容画进 32 位 DIB，返回 BGRA 像素。</summary>
    private static byte[]? CaptureWindowPixels(IntPtr hwnd, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!GetClientRect(hwnd, out var client))
        {
            return null;
        }

        width = client.Right - client.Left;
        height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        try
        {
            hdcScreen = GetDC(hwnd);
            hdcMem = CreateCompatibleDC(hdcScreen);
            var header = new BitmapInfoHeader
            {
                biSize = System.Runtime.InteropServices.Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height, // 负值表示自上而下，避免逐行翻转
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            hBitmap = CreateDIBSection(hdcMem, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return null;
            }

            previous = SelectObject(hdcMem, hBitmap);
            // 2 = PW_RENDERFULLCONTENT，让 DWM 渲染完整的合成内容（含 WinUI 3 合成层）
            if (!PrintWindow(hwnd, hdcMem, 2))
            {
                return null;
            }

            var pixels = new byte[width * height * 4];
            System.Runtime.InteropServices.Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                SelectObject(hdcMem, previous);
            }

            if (hBitmap != IntPtr.Zero)
            {
                DeleteObject(hBitmap);
            }

            if (hdcMem != IntPtr.Zero)
            {
                DeleteDC(hdcMem);
            }

            if (hdcScreen != IntPtr.Zero)
            {
                ReleaseDC(hwnd, hdcScreen);
            }
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    private static void CleanupSmokeWorkingDirectory()
    {
        if (string.IsNullOrWhiteSpace(_smokeWorkingDirectory))
        {
            return;
        }

        try
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            Directory.Delete(_smokeWorkingDirectory, recursive: true);
        }
        catch
        {
        }
    }

    private static void WriteSmokeReport(IEnumerable<string> lines)
    {
        if (string.IsNullOrWhiteSpace(_smokeReportPath))
        {
            return;
        }

        var path = Path.GetFullPath(_smokeReportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(_smokeReportPath))
        {
            try
            {
                File.AppendAllText(_smokeReportPath, $"FAIL unhandled: {args.Exception}{Environment.NewLine}");
            }
            finally
            {
                CleanupSmokeWorkingDirectory();
                Environment.Exit(1);
            }
        }

        try
        {
            AppLog.Error("App", args.Message, args.Exception);
            var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "winui-crash.log"),
                $"{DateTimeOffset.Now:O} {args.Message}{Environment.NewLine}{args.Exception}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
