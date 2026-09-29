using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyApp.Services;

public sealed record TextPackContent(string Markdown, IReadOnlyDictionary<string, byte[]> Assets);

public static class TextPackService
{
    public const string Extension = ".textpack";
    public const string AssetsFolder = "assets/";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static string CreatorIdentifier => "com." + ProjectPaths.AppName.ToLowerInvariant();

    public static TextPackContent Load(string path)
    {
        using ZipArchive zip = ZipFile.OpenRead(path);

        ZipArchiveEntry? info = zip.Entries
            .Where(e => e.Name.Equals("info.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Count(c => c == '/'))
            .FirstOrDefault();
        string root = info is null ? string.Empty : info.FullName[..^info.Name.Length];

        ZipArchiveEntry? text = zip.Entries.FirstOrDefault(e =>
            e.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && !e.FullName[root.Length..].Contains('/')
            && e.Name.StartsWith("text.", StringComparison.OrdinalIgnoreCase));

        string markdown = string.Empty;
        if (text is not null)
        {
            using var reader = new StreamReader(text.Open(), Utf8, detectEncodingFromByteOrderMarks: true);
            markdown = reader.ReadToEnd();
        }

        var assets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        string assetsPrefix = root + AssetsFolder;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.Name.Length == 0 || !entry.FullName.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            using Stream source = entry.Open();
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            assets[entry.FullName[root.Length..]] = buffer.ToArray();
        }

        return new TextPackContent(markdown, assets);
    }

    public static void Save(string path, TextPackContent content)
    {
        string root = Path.GetFileNameWithoutExtension(path) + ".textbundle/";
        string temp = path + ".tmp";

        using (FileStream stream = File.Create(temp))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteText(zip, root + "info.json", CreateInfo());
            WriteText(zip, root + "text.md", content.Markdown);
            zip.CreateEntry(root + AssetsFolder);

            foreach (var (name, bytes) in content.Assets)
            {
                ZipArchiveEntry entry = zip.CreateEntry(root + name, CompressionLevel.NoCompression);
                using Stream target = entry.Open();
                target.Write(bytes);
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    private static string CreateInfo()
    {
        var info = new JsonObject
        {
            ["version"] = 2,
            ["type"] = "net.daringfireball.markdown",
            ["transient"] = false,
            ["creatorIdentifier"] = CreatorIdentifier,
            [CreatorIdentifier] = new JsonObject { ["version"] = 1 },
        };
        return info.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Utf8);
        writer.Write(text);
    }
}
