namespace MyApp.Services;

public static class LaunchOptions
{
    public const string NewWindowArgument = "--new-window";

    public static bool IsNewWindow { get; } = Environment.GetCommandLineArgs().Contains(NewWindowArgument);
}
