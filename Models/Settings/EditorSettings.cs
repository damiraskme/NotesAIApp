using CommunityToolkit.Mvvm.ComponentModel;

namespace MyApp.Models.Settings
{
    public sealed partial class EditorSettings : ObservableObject
    {
        [ObservableProperty]
        public partial bool AutoSave { get; set; }

        [ObservableProperty]
        public partial double AutoSaveDelaySeconds { get; set; } = 2;
    }
}
