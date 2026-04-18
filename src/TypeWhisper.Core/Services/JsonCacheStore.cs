using System.Text.Json;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Tiny helper for services that cache a JSON-backed list on disk with
/// best-effort read/write and directory creation. All failures are swallowed.
/// </summary>
internal static class JsonCacheStore
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    public static List<T> Load<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path)) ?? [];
        }
        catch { return []; }
    }

    public static void Save<T>(string path, List<T> data)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(data, IndentedOptions));
        }
        catch { }
    }
}
