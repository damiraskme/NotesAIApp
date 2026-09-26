using CommunityToolkit.Mvvm.ComponentModel;

namespace MyApp.Models.Settings
{
    public sealed partial class SessionState : ObservableObject
    {
        [ObservableProperty]
        public partial List<string> OpenTabs { get; set; } = new();

        [ObservableProperty]
        public partial string? ActiveTab { get; set; }

        [ObservableProperty]
        public partial List<string> RecentFiles { get; set; } = new();
    }
}
