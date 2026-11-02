using System.Windows;

namespace XSDAudioRack;

/// <summary>浅色 / 深色主题切换（ThemeManager.Current 持久化在 autosave.json）。</summary>
public static class ThemeManager
{
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static string Current { get; private set; } = Light;

    /// <summary>应用主题。name 不是 Dark 时一律按 Light 处理。</summary>
    public static void Apply(string? name)
    {
        Current = name == Dark ? Dark : Light;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var palette = new ResourceDictionary
        {
            Source = new System.Uri(
                $"pack://application:,,,/Themes/{Current}.xaml", System.UriKind.Absolute)
        };
        // 只换调色板字典（下标 0），Controls.xaml 等其他字典保持不动
        if (dictionaries.Count > 0) dictionaries[0] = palette;
        else dictionaries.Add(palette);
    }
}
