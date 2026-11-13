using System.Text.Json;

namespace StreamDock.Core;

public static class Storage
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static T Read<T>(string path, Func<T> fallback)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? fallback(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return fallback(); }
    }
    public static void Write<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, Json));
        File.Move(temp, path, true);
    }
}

public static class Input
{
    public static Uri WebUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Введите полную ссылку http:// или https:// без логина и пароля.");
        return uri;
    }
    public static string SafeName(string name)
    {
        const string invalid = "<>:\"/\\|?*";
        var safe = new string(name.Select(c => c < 32 || invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (safe.Length > 150) safe = safe[..150];
        var stem = safe.Split('.')[0];
        if (string.IsNullOrEmpty(safe) || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase)) safe = "file_" + safe;
        return safe;
    }
}
