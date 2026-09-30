using MyApp.Models.Chat;

namespace MyApp.Services;

public interface IAiProvider
{
    string Id { get; }

    string DisplayName { get; }

    bool RequiresApiKey { get; }

    Task<string> SendAsync(IReadOnlyList<ChatMessage> history, string prompt, CancellationToken cancellationToken);
}

public sealed class PythonBackendProvider : IAiProvider
{
    public string Id => "python";

    public string DisplayName => "Python backend";

    public bool RequiresApiKey => false;

    public async Task<string> SendAsync(IReadOnlyList<ChatMessage> history, string prompt, CancellationToken cancellationToken)
    {
        PythonResult result = await PythonService.RunAsync("process", prompt, cancellationToken);
        if (result.Error is not null) throw new InvalidOperationException(result.Error);

        return string.Join(Environment.NewLine, new[] { result.Text, result.Message }.Where(r => !string.IsNullOrEmpty(r)));
    }
}

public sealed class NotConnectedProvider : IAiProvider
{
    public NotConnectedProvider(string id, string displayName, bool requiresApiKey)
    {
        Id = id;
        DisplayName = displayName;
        RequiresApiKey = requiresApiKey;
    }

    public bool RequiresApiKey { get; }

    public string Id { get; }

    public string DisplayName { get; }

    public Task<string> SendAsync(IReadOnlyList<ChatMessage> history, string prompt, CancellationToken cancellationToken) =>
        throw new InvalidOperationException($"{DisplayName} is not connected yet.");
}

public static class AiProviders
{
    public static IReadOnlyList<IAiProvider> All { get; } = new IAiProvider[]
    {
        new PythonBackendProvider(),
        new NotConnectedProvider("claude", "Claude", requiresApiKey: true),
        new NotConnectedProvider("openai", "OpenAI", requiresApiKey: true),
        new NotConnectedProvider("ollama", "Local (Ollama)", requiresApiKey: false),
    };

    public static IAiProvider Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];
}
