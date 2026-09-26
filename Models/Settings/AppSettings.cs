using MyApp.Services;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MyApp.Models.Settings
{
    public sealed class AppSettings : IStoreRoot
    {
        public int Version { get; set; } = 1;

        public AppearanceSettings Appearance { get; set; } = new();

        public EditorSettings Editor { get; set; } = new();

        [JsonIgnore]
        public IEnumerable<INotifyPropertyChanged> Sections => new INotifyPropertyChanged[] { Appearance, Editor };
    }
}
