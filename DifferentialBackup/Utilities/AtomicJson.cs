using System.Text.Json;

namespace DifferentialBackup.Utilities;

internal static class AtomicJson
{
    public static bool IsTemporaryName(string name, string metadataName) =>
        name.Length == metadataName.Length + 37 && name.StartsWith(metadataName + ".", StringComparison.Ordinal) &&
        name.EndsWith(".tmp", StringComparison.Ordinal) &&
        Guid.TryParseExact(name.Substring(metadataName.Length + 1, 32), "N", out _);

    public static T Read<T>(string path)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException($"Empty metadata: '{path}'."); }
        catch (JsonException ex) { throw new InvalidDataException($"Malformed metadata: '{path}'.", ex); }
    }

    public static void Write<T>(string path, T value, bool overwrite = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
