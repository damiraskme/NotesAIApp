using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MyApp.Messages;
using MyApp.Models;
using MyApp.Models.Settings;
using MyApp.Services;
using System.Collections.ObjectModel;

namespace MyApp.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        public SettingsService Storage { get; }

        public AppSettings Settings => Storage.Settings;

        [ObservableProperty]
        public partial bool IsAlwaysOnTop { get; set; } = false;

        [ObservableProperty]
        public partial bool IsPythonBusy { get; set; } = false;

        public string DefaultFilePath { get; }

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
            Storage = new SettingsService();
            DefaultFilePath = Path.Combine(AppPaths.DataDirectory, "SavedNote.rtf");
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
            SessionState session = Storage.State.Session;
            List<string> paths = session.OpenTabs.Where(File.Exists).ToList();
            activePath = paths.Contains(session.ActiveTab ?? string.Empty) ? session.ActiveTab : null;
            return paths;
        }

        public void SaveSession()
        {
            SessionState session = Storage.State.Session;
            session.OpenTabs = Tabs.Where(t => t.FilePath is not null).Select(t => t.FilePath!).ToList();
            session.ActiveTab = ActiveTab?.FilePath;
        }

        [RelayCommand]
        private void TriggerSaveNote()
        {
            WeakReferenceMessenger.Default.Send(new RequestSaveMessage());
        }
    }
}
