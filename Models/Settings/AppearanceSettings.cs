using CommunityToolkit.Mvvm.ComponentModel;
using MyApp.Services;

namespace MyApp.Models.Settings
{
    public sealed partial class AppearanceSettings : ObservableObject
    {
        [ObservableProperty]
        public partial string Theme { get; set; } = ThemeService.DefaultTheme;
    }
}
