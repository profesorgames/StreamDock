using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace StreamDock.Core;

public enum MediaKind { Video, Gallery, File }
public enum DownloadMode { Video, Mp3, Original }
public enum JobState { Queued, Downloading, Processing, Completed, Cancelled, Failed, Interrupted }

public sealed record MediaInfo(string Url, string Title, string Site, MediaKind Kind,
    double Duration = 0, string? Thumbnail = null, IReadOnlyList<int>? Heights = null, long? Size = null);
public sealed record DownloadOptions(DownloadMode Mode, int MaxHeight = 1080, int AudioBitrate = 320);
public sealed record DownloadProgress(double Percent, string Detail, bool Processing = false);
public sealed record SpotifyTrack(string Id, string Title, string Artist, int DurationMs, string? ImageUrl);
public sealed record SearchCandidate(string Url, string Title, string Uploader, double Duration, double Score)
{
    public string Description => $"{Uploader}  ·  {TimeSpan.FromSeconds(Duration):m\\:ss}  ·  совпадение {Score:P0}";
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name); return true;
    }
}

public sealed class DownloadJob : Observable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public MediaInfo Media { get; set; } = new("", "", "", MediaKind.File);
    public DownloadOptions Options { get; set; } = new(DownloadMode.Video);
    public string Directory { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    private JobState state;
    public JobState State { get => state; set { if (Set(ref state, value)) { Changed(nameof(Status)); Changed(nameof(CanRetry)); Changed(nameof(IsActive)); } } }
    private double percent;
    public double Percent { get => percent; set => Set(ref percent, Math.Clamp(value, 0, 100)); }
    private string detail = "Ожидает своей очереди";
    public string Detail { get => detail; set => Set(ref detail, value); }
    private string? outputPath;
    public string? OutputPath { get => outputPath; set => Set(ref outputPath, value); }
    [JsonIgnore] public string Title => Media.Title;
    [JsonIgnore] public string Subtitle => $"{Media.Site}  /  {(Options.Mode == DownloadMode.Mp3 ? $"MP3 · {Options.AudioBitrate} кбит/с" : Media.Kind == MediaKind.Gallery ? "Изображения" : Options.Mode == DownloadMode.Original ? "Исходный файл" : Options.MaxHeight == 0 ? "Лучшее качество" : $"до {Options.MaxHeight}p")}";
    [JsonIgnore] public string Status => State switch
    {
        JobState.Queued => "В очереди", JobState.Downloading => "Загрузка", JobState.Processing => "Обработка",
        JobState.Completed => "Готово", JobState.Cancelled => "Отменено", JobState.Interrupted => "Прервано", _ => "Ошибка"
    };
    [JsonIgnore] public bool CanRetry => State is JobState.Failed or JobState.Cancelled or JobState.Interrupted;
    [JsonIgnore] public bool IsActive => State is JobState.Downloading or JobState.Processing;
}

public sealed class AppSettings
{
    public string DownloadDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "StreamDock");
    public bool CloseToTray { get; set; } = true;
    public bool AutoStart { get; set; }
    public int ConcurrentDownloads { get; set; } = 2;
    public string SpotifyClientId { get; set; } = "";
    public string CookiesFile { get; set; } = "";
}
