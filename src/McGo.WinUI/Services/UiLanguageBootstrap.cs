using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace McGo.WinUI.Services;

internal static class UiLanguageBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Published builds contain only zh-CN MUI files, including on non-Chinese Windows.
        const uint languageName = 0x8;
        const string languages = "zh-CN\0\0";
        if (!SetProcessPreferredUILanguages(languageName, languages, out _) ||
            !SetThreadPreferredUILanguages(languageName, languages, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot select the bundled zh-CN UI resources.");
        }
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessPreferredUILanguages(uint flags, string languages, out uint count);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPreferredUILanguages(uint flags, string languages, out uint count);
}
