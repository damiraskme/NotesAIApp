using MyApp.Services;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MyApp.Models.Settings
{
    public sealed class AppState : IStoreRoot
    {
        public int Version { get; set; } = 1;

        public SessionState Session { get; set; } = new();

        [JsonIgnore]
        public IEnumerable<INotifyPropertyChanged> Sections => new INotifyPropertyChanged[] { Session };
    }
}
