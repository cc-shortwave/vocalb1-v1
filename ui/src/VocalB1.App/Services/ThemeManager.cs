using System.Windows;

namespace VocalB1.App.Services;

/// <summary>
/// Dark / light theme switching: only the palette dictionary in the application resources is replaced.
/// Styles and controls all reference colors through DynamicResource, so the UI does not need to be rebuilt.
/// </summary>
public static class ThemeManager
{
    private const string DarkPalette = "pack://application:,,,/Themes/Palette.Dark.xaml";
    private const string LightPalette = "pack://application:,,,/Themes/Palette.Light.xaml";

    /// <summary>Whether the light theme is currently active.</summary>
    public static bool IsLight { get; private set; }

    /// <summary>Raised after the palette changes; custom-drawn controls use it to refresh their cached brushes.</summary>
    public static event EventHandler? Changed;

    public static void Apply(bool light)
    {
        var app = Application.Current;
        if (app is null)
            return;

        var palette = new ResourceDictionary
        {
            Source = new Uri(light ? LightPalette : DarkPalette, UriKind.Absolute),
        };

        var merged = app.Resources.MergedDictionaries;
        if (merged.Count == 0)
            merged.Add(palette);
        else
            merged[0] = palette;

        IsLight = light;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
