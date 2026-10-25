using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace McGo.WinUI.Dialogs;

/// <summary>
/// 统一的 ContentDialog 构造入口。
///
/// 为什么要用它：ContentDialog 的内容在弹出时会被重新挂到 Popup（独立 XamlRoot）下，
/// 脱离了页面 RootGrid 的 RequestedTheme 继承链，于是主题回退到系统设置。
/// 系统是浅色时，深色界面上就会弹出纯白对话框（WinUI 已知限制，见
/// https://github.com/microsoft/microsoft-ui-xaml/issues/2331 ）。
/// 这里把调用方所在窗口的主题显式同步给对话框，让弹层与界面保持一致。
/// </summary>
internal static class AppDialog
{
    /// <summary>按 <paramref name="anchor"/> 所在的窗口主题创建对话框。</summary>
    internal static ContentDialog Create(FrameworkElement anchor, string title, object content, string primaryText, string closeText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = anchor.XamlRoot,
            RequestedTheme = ResolveTheme(anchor),
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
        };
        return dialog;
    }

    /// <summary>
    /// 取锚点元素的实际主题。优先用应用记录的窗口主题：
    /// 元素被移入 Popup 后 ActualTheme 会变成系统主题，正是需要绕开的地方。
    /// </summary>
    private static ElementTheme ResolveTheme(FrameworkElement anchor)
    {
        var appTheme = App.MainWindow is MainWindow window
            ? window.CurrentTheme
            : ElementTheme.Default;
        return appTheme == ElementTheme.Default ? anchor.ActualTheme : appTheme;
    }
}
