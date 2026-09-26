using MyApp.Models.Settings;

namespace MyApp.Services;

public sealed class SettingsService
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly JsonStore<AppSettings> _settings;
    private readonly JsonStore<AppState> _state;

    public AppSettings Settings => _settings.Value;

    public AppState State => _state.Value;

    public SettingsService()
    {
        _settings = new JsonStore<AppSettings>(
            Path.Combine(AppPaths.DataDirectory, "settings.json"), AppJsonContext.Default.AppSettings, SaveDelay);
        _state = new JsonStore<AppState>(
            Path.Combine(AppPaths.DataDirectory, "state.json"), AppJsonContext.Default.AppState, SaveDelay);

        if (!_settings.LoadedFromDisk) ImportLegacySettings();
        if (!_state.LoadedFromDisk) ImportLegacySession();
    }

    public void Flush()
    {
        _settings.Flush();
        _state.Flush();
    }

    private void ImportLegacySettings()
    {
        string legacyPath = Path.Combine(ProjectPaths.ProjectDirectory, "Settings.txt");
        try
        {
            if (File.Exists(legacyPath))
            {
                foreach (string line in File.ReadAllLines(legacyPath))
                {
                    int split = line.IndexOf('=');
                    if (split <= 0) continue;

                    string key = line[..split].Trim();
                    string value = line[(split + 1)..].Trim();
                    if (key == "Theme") Settings.Appearance.Theme = value;
                    else if (key == "AutoSave" && bool.TryParse(value, out bool autoSave)) Settings.Editor.AutoSave = autoSave;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not import {legacyPath}: {ex.Message}");
        }

        _settings.ScheduleSave();
        _settings.Flush();
    }

    private void ImportLegacySession()
    {
        string legacyPath = Path.Combine(ProjectPaths.ProjectDirectory, "OpenTabs.txt");
        try
        {
            if (File.Exists(legacyPath))
            {
                var tabs = new List<string>();
                foreach (string line in File.ReadAllLines(legacyPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    bool isActive = line.StartsWith('*');
                    string path = isActive ? line[1..] : line;
                    tabs.Add(path);
                    if (isActive) State.Session.ActiveTab = path;
                }
                State.Session.OpenTabs = tabs;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not import {legacyPath}: {ex.Message}");
        }

        _state.ScheduleSave();
        _state.Flush();
    }
}
