namespace MyApp.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = Resolve();

    private static string Resolve()
    {
        string directory;
        try
        {
            directory = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        catch (Exception)
        {
            directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProjectPaths.AppName);
        }

        Directory.CreateDirectory(directory);
        return directory;
    }
}
