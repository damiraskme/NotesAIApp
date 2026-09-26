using System.Text.Json.Serialization;

namespace MyApp.Models.Settings
{
    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(AppSettings))]
    [JsonSerializable(typeof(AppState))]
    internal sealed partial class AppJsonContext : JsonSerializerContext
    {
    }
}
