using MyApp.Models.Chat;
using MyApp.Models.Settings;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyApp.Services;

public static class ChatStore
{
    private static string Folder => Path.Combine(AppPaths.DataDirectory, "chats");

    private static string FileFor(string notePath)
    {
        string key = Path.GetFullPath(notePath).ToLowerInvariant();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(Folder, hash[..16] + ".json");
    }

    public static NoteChat Load(string notePath)
    {
        string file = FileFor(notePath);
        try
        {
            if (File.Exists(file))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(file), AppJsonContext.Default.NoteChat) ?? new NoteChat();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not read chat {file}: {ex.Message}");
        }
        return new NoteChat();
    }

    public static void Save(string notePath, NoteChat chat)
    {
        string file = FileFor(notePath);
        try
        {
            Directory.CreateDirectory(Folder);
            chat.NotePath = notePath;
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(chat, AppJsonContext.Default.NoteChat));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not save chat {file}: {ex.Message}");
        }
    }
}
