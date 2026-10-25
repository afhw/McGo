using System;
using System.Linq;
using Microsoft.UI.Xaml.Controls;

namespace McGo.WinUI;

/// <summary>
/// 各页面共用的控件读写辅助。
///
/// 抽出来的原因：ComboText 原本在 5 个页面各有一份、ClampDouble 3 份、NumberValue 4 份，
/// 改一处容易漏掉其余几处。这些都只依赖控件本身，放在一起更不容易走样。
/// </summary>
internal static class UiHelpers
{
    /// <summary>读取下拉框当前项的文字；没有选中项时返回 <paramref name="fallback"/>。</summary>
    internal static string ComboText(ComboBox combo, string fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item)
        {
            return item.Content?.ToString() ?? fallback;
        }

        return combo.SelectedItem?.ToString() ?? fallback;
    }

    /// <summary>读取下拉框当前项的 Tag；Tag 缺失时退回内容文字。</summary>
    internal static string ComboTag(ComboBox combo, string fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item)
        {
            if (item.Tag is not null)
            {
                return item.Tag.ToString() ?? fallback;
            }

            var content = item.Content?.ToString() ?? "";
            if (string.Equals(content, "不安装", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(content, "只装原版", StringComparison.OrdinalIgnoreCase))
            {
                return fallback;
            }

            return content;
        }

        return combo.SelectedItem?.ToString() ?? fallback;
    }

    /// <summary>按内容或 Tag 选中项。找不到时保持原选择不变。</summary>
    internal static void SetComboText(ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>按 Tag 选中项；找不到时回到第一项。</summary>
    internal static void SetComboTag(ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString() ?? "", value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    /// <summary>把 NumberBox 的值取整并夹到范围内；值为 NaN 时用 <paramref name="fallback"/>。</summary>
    internal static int NumberValue(NumberBox box, int min, int max, int fallback)
    {
        if (double.IsNaN(box.Value))
        {
            return fallback;
        }

        return Math.Clamp((int)Math.Round(box.Value), min, max);
    }

    /// <summary>读取 Slider 的值并夹到范围内（用于内存滑块这类整数值控件）。</summary>
    internal static int NumberValue(Slider slider, int min, int max, int fallback)
    {
        if (double.IsNaN(slider.Value))
        {
            return fallback;
        }

        return Math.Clamp((int)Math.Round(slider.Value), min, max);
    }

    /// <summary>把整数夹到范围内（供 NumberBox/Slider 赋值使用）。</summary>
    internal static double ClampDouble(int value, int min, int max) => Math.Clamp(value, min, max);
}
