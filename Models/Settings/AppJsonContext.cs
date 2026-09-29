using MyApp.Models.Chat;
using System.Text.Json.Serialization;

namespace MyApp.Models.Settings
{
    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
    [JsonSerializable(typeof(AppSettings))]
    [JsonSerializable(typeof(AppState))]
    [JsonSerializable(typeof(NoteChat))]
    internal sealed partial class AppJsonContext : JsonSerializerContext
    {
    }
}
