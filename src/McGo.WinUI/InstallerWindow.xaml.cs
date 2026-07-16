using McGo.Core.App;
using McGo.WinUI.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace McGo.WinUI;

public sealed partial class InstallerWindow : Window
{
    private readonly InstallerLaunchOptions _options;
    private bool _completed;
    private string? _installedExecutable;

    internal InstallerWindow(InstallerLaunchOptions options)
    {
        _options = options;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(InstallerTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        ApplyTitleBarColors();
        AppWindow.Resize(new SizeInt32(900, 680));

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        InstallPathBox.Text = options.InstallDirectory;
        VersionText.Text = $"v{AppConstants.AppVersion}";
        ConfigureMode();
        Activated += InstallerWindowActivated;
    }

    private void ConfigureMode()
    {
        if (!_options.Uninstall)
        {
            return;
        }

        InstallerTitleBar.Title = "McGo 卸载程序";
        HeadingText.Text = "卸载 McGo";
        DescriptionText.Text = "移除客户端程序。默认保留账号、设置、日志和游戏数据。";
        InstallPathBox.IsReadOnly = true;
        DesktopShortcutCheckBox.Visibility = Visibility.Collapsed;
        RemoveDataCheckBox.Visibility = Visibility.Visible;
        NoticeBar.Message = "删除游戏数据可能包含较大的 .minecraft 目录，此操作无法撤销。";
        NoticeBar.Severity = InfoBarSeverity.Warning;
        PrimaryButton.Content = "卸载";
        StepTwoMarker.Visibility = Visibility.Collapsed;
        StepThreeMarker.Visibility = Visibility.Collapsed;
    }

    private async void InstallerWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= InstallerWindowActivated;
        if (_options.SmokeTest)
        {
            await RunSmokeTestAsync();
        }
    }

    private async void ChooseInstallDirectory(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            InstallPathBox.Text = Path.Combine(folder.Path, "McGo");
        }
    }

    private async void PrimaryAction(object sender, RoutedEventArgs e)
    {
        if (_completed)
        {
            Finish();
            return;
        }

        await ExecuteAsync(registerInstallation: true);
    }

    private async Task ExecuteAsync(bool registerInstallation)
    {
        SetBusyState();
        try
        {
            if (_options.Uninstall)
            {
                ProgressHeadingText.Text = "正在卸载";
                ProgressStatusText.Text = "正在移除系统入口";
                InstallProgressBar.IsIndeterminate = true;
                await Task.Delay(250);
                InstallerService.BeginUninstall(
                    InstallPathBox.Text.Trim(),
                    RemoveDataCheckBox.IsChecked == true,
                    registerInstallation);
                ShowComplete("卸载已准备完成", "关闭此窗口后将移除客户端文件。", launchOption: false);
                return;
            }

            var installDirectory = Path.GetFullPath(InstallPathBox.Text.Trim());
            var progress = new Progress<InstallProgress>(value =>
            {
                InstallProgressBar.Value = value.Percentage;
                ProgressPercentText.Text = $"{value.Percentage}%";
                ProgressStatusText.Text = value.Status;
            });
            await InstallerService.InstallAsync(
                _options.SourceDirectory,
                installDirectory,
                DesktopShortcutCheckBox.IsChecked == true,
                registerInstallation,
                progress);
            _installedExecutable = Path.Combine(installDirectory, "McGo.WinUI.exe");
            ShowComplete("安装完成", "McGo 已安装，可以立即启动。", launchOption: true);
        }
        catch (Exception exc)
        {
            OptionsPanel.Visibility = Visibility.Visible;
            ProgressPanel.Visibility = Visibility.Collapsed;
            NoticeBar.IsOpen = true;
            NoticeBar.Severity = InfoBarSeverity.Error;
            NoticeBar.Message = exc.Message;
            PrimaryButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private void SetBusyState()
    {
        OptionsPanel.Visibility = Visibility.Collapsed;
        CompletePanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        PrimaryButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        StepOneMarker.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorDefaultBrush"];
        StepTwoMarker.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    }

    private void ShowComplete(string heading, string description, bool launchOption)
    {
        _completed = true;
        OptionsPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        CompletePanel.Visibility = Visibility.Visible;
        CompleteHeadingText.Text = heading;
        CompleteDescriptionText.Text = description;
        LaunchAfterInstallCheckBox.Visibility = launchOption ? Visibility.Visible : Visibility.Collapsed;
        StepTwoMarker.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorDefaultBrush"];
        StepThreeMarker.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        PrimaryButton.Content = "完成";
        PrimaryButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Collapsed;
    }

    private void Finish()
    {
        if (_installedExecutable is not null && LaunchAfterInstallCheckBox.IsChecked == true)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _installedExecutable,
                WorkingDirectory = Path.GetDirectoryName(_installedExecutable),
                UseShellExecute = true,
            });
        }

        Close();
    }

    private void CancelInstaller(object sender, RoutedEventArgs e) => Close();

    private async Task RunSmokeTestAsync()
    {
        try
        {
            await Task.Delay(500);
            await CaptureSmokeScreenshotAsync();
            await ExecuteAsync(registerInstallation: false);
            if (!_completed || (!_options.Uninstall && !File.Exists(_installedExecutable)))
            {
                throw new InvalidOperationException("安装器烟测未完成文件安装。");
            }

            WriteSmokeReport("PASS WinUI installer");
            Environment.Exit(0);
        }
        catch (Exception exc)
        {
            WriteSmokeReport("FAIL WinUI installer: " + exc);
            Environment.Exit(1);
        }
    }

    private async Task CaptureSmokeScreenshotAsync()
    {
        if (string.IsNullOrWhiteSpace(_options.SmokeScreenshotPath))
        {
            return;
        }

        var path = Path.GetFullPath(_options.SmokeScreenshotPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(RootGrid);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            throw new InvalidOperationException("安装器截图为空。");
        }

        var pixels = await bitmap.GetPixelsAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)!);
        var file = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        await using var stream = await file.OpenStreamForWriteAsync();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth,
            (uint)bitmap.PixelHeight,
            96,
            96,
            pixels.ToArray());
        await encoder.FlushAsync();
    }

    private void WriteSmokeReport(string value)
    {
        if (string.IsNullOrWhiteSpace(_options.SmokeReportPath))
        {
            return;
        }

        var path = Path.GetFullPath(_options.SmokeReportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value + Environment.NewLine);
    }

    private void ApplyTitleBarColors()
    {
        var titleBar = AppWindow.TitleBar;
        titleBar.BackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.InactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonInactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonForegroundColor = Color.FromArgb(255, 255, 255, 255);
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(170, 255, 255, 255);
        titleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 255, 255, 255);
        titleBar.ButtonPressedForegroundColor = Color.FromArgb(255, 255, 255, 255);
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(40, 255, 255, 255);
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(70, 255, 255, 255);
    }
}
