using System.Reflection;
using System.Runtime.CompilerServices;

namespace MyApp.Services;

public static class ProjectPaths
{
    public static string AppName { get; } =
        Assembly.GetEntryAssembly()?.GetName().Name ?? AppDomain.CurrentDomain.FriendlyName;

    public static string ProjectDirectory { get; } = Resolve();

    private static string Resolve([CallerFilePath] string sourceFile = "")
    {
        string? dir = Path.GetDirectoryName(Path.GetDirectoryName(sourceFile));
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            return dir;
        }

        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppName);
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}
