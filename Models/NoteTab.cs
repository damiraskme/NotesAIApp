using CommunityToolkit.Mvvm.ComponentModel;
using MyApp.Services;

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

        public string SavedSnapshot { get; set; } = "";

        public string DefaultExtension { get; init; } = TextPackService.Extension;

        public IReadOnlyDictionary<string, string> KnownAssets { get; set; } = new Dictionary<string, string>();

        public IReadOnlyDictionary<string, byte[]> OriginalAssets { get; set; } = new Dictionary<string, byte[]>();

        public string Extension => FilePath is null ? DefaultExtension : Path.GetExtension(FilePath).ToLowerInvariant();

        public string Title => FilePath is null
            ? (DefaultExtension == TextPackService.Extension ? "Untitled" : "Untitled" + DefaultExtension)
            : Path.GetFileNameWithoutExtension(FilePath);

        public EditorMode Mode => Extension switch
        {
            ".txt" => EditorMode.PlainText,
            ".md" => EditorMode.Markdown,
            _ => EditorMode.RichText,
        };

        public bool IsPlainTextFile => Mode != EditorMode.RichText;

        public bool IsTextPack => Extension == TextPackService.Extension;
    }
}
