using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MyApp.Services;

public sealed record ThemeInfo(string Name, string DisplayName);

public static class ThemeService
{
    public const string DefaultTheme = "Yellow";

    public static IReadOnlyList<ThemeInfo> Themes { get; } = new[]
    {
        new ThemeInfo("Dark", "Dark"),
        new ThemeInfo("Light", "Light"),
        new ThemeInfo("Blue", "Blue"),
        new ThemeInfo("Yellow", "Yellow"),
        new ThemeInfo("Orange", "Orange"),
        new ThemeInfo("HighContrast", "High contrast"),
        new ThemeInfo("ColorblindRedGreen", "Colorblind: red-green"),
        new ThemeInfo("ColorblindBlueYellow", "Colorblind: blue-yellow"),
    };

    public static string CurrentTheme { get; private set; } = DefaultTheme;

    public static bool Apply(string name, FrameworkElement root)
    {
        if (!Themes.Any(t => t.Name == name)) return false;

        ResourceDictionary palette;
        try
        {
            palette = new ResourceDictionary { Source = new Uri($"ms-appx:///Themes/Palettes/{name}.xaml") };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex.Message);
            return false;
        }

        ResourceDictionary resources = Application.Current.Resources;
        foreach (var (key, value) in palette)
        {
            if (key is string colorKey && colorKey.EndsWith("Color") && value is Color color
                && resources.TryGetValue(colorKey[..^"Color".Length] + "Brush", out object brush)
                && brush is SolidColorBrush solid)
            {
                solid.Color = color;
            }
        }

        root.RequestedTheme = palette.TryGetValue("NoteBaseTheme", out object baseTheme) && baseTheme as string == "Dark"
            ? ElementTheme.Dark
            : ElementTheme.Light;

        CurrentTheme = name;
        return true;
    }
}
