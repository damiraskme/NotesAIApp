using Microsoft.UI.Dispatching;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MyApp.Services;

public interface IStoreRoot
{
    IEnumerable<INotifyPropertyChanged> Sections { get; }
}

public sealed class JsonStore<T> where T : class, IStoreRoot, new()
{
    private readonly string _path;
    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly DispatcherQueueTimer? _saveTimer;
    private bool _hasPendingChanges;

    public T Value { get; }

    public bool LoadedFromDisk { get; }

    public JsonStore(string path, JsonTypeInfo<T> typeInfo, TimeSpan saveDelay)
    {
        _path = path;
        _typeInfo = typeInfo;
        (Value, LoadedFromDisk) = Load();

        if (DispatcherQueue.GetForCurrentThread() is DispatcherQueue queue)
        {
            _saveTimer = queue.CreateTimer();
            _saveTimer.Interval = saveDelay;
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (s, e) => Flush();
        }

        foreach (INotifyPropertyChanged section in Value.Sections)
        {
            section.PropertyChanged += (s, e) => ScheduleSave();
        }
    }

    public void ScheduleSave()
    {
        _hasPendingChanges = true;
        if (_saveTimer is null)
        {
            Flush();
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Flush()
    {
        _saveTimer?.Stop();
        if (!_hasPendingChanges) return;

        try
        {
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Value, _typeInfo));
            File.Move(temp, _path, overwrite: true);
            _hasPendingChanges = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not save {_path}: {ex.Message}");
        }
    }

    private (T Value, bool FromDisk) Load()
    {
        if (!File.Exists(_path)) return (new T(), false);

        try
        {
            T? value = JsonSerializer.Deserialize(File.ReadAllText(_path), _typeInfo);
            return (value ?? new T(), value is not null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not read {_path}: {ex.Message}");
            try
            {
                File.Copy(_path, _path + ".corrupt", overwrite: true);
            }
            catch (Exception copyEx)
            {
                System.Diagnostics.Debug.WriteLine($"Could not back up {_path}: {copyEx.Message}");
            }
            return (new T(), false);
        }
    }
}
