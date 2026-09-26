using CommunityToolkit.Mvvm.ComponentModel;

namespace MyApp.Models
{
    public partial class NoteTab : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Title))]
        public partial string? FilePath { get; set; }

        [ObservableProperty]
        public partial bool IsDirty { get; set; }

        [ObservableProperty]
        public partial bool IsActive { get; set; }

        public string? Rtf { get; set; }

        public int SelectionStart { get; set; } = int.MaxValue;
        public int SelectionEnd { get; set; } = int.MaxValue;

        public string SavedRtf { get; set; } = "";

        public string DefaultExtension { get; init; } = ".rtf";

        public string Extension => FilePath is null ? DefaultExtension : Path.GetExtension(FilePath).ToLowerInvariant();

        public string Title => FilePath is null
            ? (DefaultExtension == ".rtf" ? "Untitled" : "Untitled" + DefaultExtension)
            : Path.GetFileNameWithoutExtension(FilePath);

        public bool IsPlainTextFile => Extension is ".txt" or ".md";
    }
}
