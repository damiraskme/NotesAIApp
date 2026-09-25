using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MyApp.Messages;
using MyApp.Models;
using MyApp.Services;
using System.Collections.ObjectModel;

namespace MyApp.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly AppSettings appSettings;

        [ObservableProperty]
        public partial bool IsAutoSaveEnabled { get; set; } = false;

        [ObservableProperty]
        public partial bool IsAlwaysOnTop { get; set; } = false;

        [ObservableProperty]
        public partial bool IsPythonBusy { get; set; } = false;

        public string DefaultFilePath { get; }

        private string SessionFilePath { get; }

        public ObservableCollection<NoteTab> Tabs { get; } = new();

        [ObservableProperty]
        public partial NoteTab? ActiveTab { get; set; }

        partial void OnActiveTabChanged(NoteTab? oldValue, NoteTab? newValue)
        {
            if (oldValue is not null) oldValue.IsActive = false;
            if (newValue is not null) newValue.IsActive = true;
        }

        public MainViewModel()
        {
            appSettings = new AppSettings();
            DefaultFilePath = Path.Combine(ProjectPaths.ProjectDirectory, "SavedNote.rtf");
            SessionFilePath = Path.Combine(ProjectPaths.ProjectDirectory, "OpenTabs.txt");
        }

        public bool SaveNote(string path, string content)
        {
            try
            {
                File.WriteAllText(path, content);
                System.Diagnostics.Debug.WriteLine($"Saved: {path}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
                return false;
            }
        }

        public string? LoadNote(string? path)
        {
            try
            {
                return path is not null && File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
                return null;
            }
        }

        public List<string> LoadSession(out string? activePath)
        {
            activePath = null;
            try
            {
                if (File.Exists(SessionFilePath))
                {
                    var paths = new List<string>();
                    foreach (string line in File.ReadAllLines(SessionFilePath))
                    {
                        bool isActive = line.StartsWith('*');
                        string path = isActive ? line[1..] : line;
                        if (!File.Exists(path)) continue;

                        paths.Add(path);
                        if (isActive) activePath = path;
                    }
                    return paths;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
            return new List<string>();
        }

        public void SaveSession()
        {
            try
            {
                File.WriteAllLines(SessionFilePath, Tabs
                    .Where(t => t.FilePath is not null)
                    .Select(t => t == ActiveTab ? "*" + t.FilePath : t.FilePath!));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }

        [RelayCommand]
        private void TriggerSaveNote()
        {
            WeakReferenceMessenger.Default.Send(new RequestSaveMessage());
        }
    }
}
