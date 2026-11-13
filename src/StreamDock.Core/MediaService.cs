using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamDock.Core;

public interface IDownloader
{
    Task<string> DownloadAsync(DownloadJob job, IProgress<DownloadProgress> progress, CancellationToken cancellation);
}

public sealed partial class MediaService(ToolPaths tools, AppSettings settings, HttpClient http, IProcessRunner runner) : IDownloader
{
    private List<string> YtArgs()
    {
        var args = new List<string> { "--ignore-config", "--no-playlist", "--encoding", "utf-8", "--no-warnings", "--socket-timeout", "25", "--retries", "3", "--extractor-retries", "2", "--ffmpeg-location", tools.Root, "--js-runtimes", "deno:" + tools.Deno, "--no-remote-components" };
        if (!string.IsNullOrWhiteSpace(settings.CookiesFile))
        {
            if (!File.Exists(settings.CookiesFile)) throw new FileNotFoundException("Файл cookies не найден. Проверьте настройки.");
            args.AddRange(["--cookies", settings.CookiesFile]);
        }
        return args;
    }
    private List<string> GalleryArgs()
    {
        var args = new List<string> { "-X", "utf8", "-m", "gallery_dl", "--ignore-config", "--no-input", "--no-colors", "--retries", "3", "--timeout", "25" };
        if (!string.IsNullOrWhiteSpace(settings.CookiesFile)) args.AddRange(["--cookies", settings.CookiesFile]);
        return args;
    }

    public async Task<MediaInfo> AnalyzeAsync(string value, CancellationToken cancellation)
    {
        var uri = Input.WebUrl(value);
        if (uri.Host.Equals("open.spotify.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Для Spotify откройте вкладку Spotify и подключите аккаунт. Ссылка не содержит исходный аудиофайл.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        // Image galleries need their own extractor; videos and reels stay with yt-dlp.
        if ((uri.Host.EndsWith("instagram.com", StringComparison.OrdinalIgnoreCase) && !uri.AbsolutePath.Contains("/reel", StringComparison.OrdinalIgnoreCase)) || uri.AbsolutePath.Contains("/photo/", StringComparison.OrdinalIgnoreCase))
        {
            try { return await AnalyzeGallery(uri, ct); }
            catch (InvalidOperationException) { }
        }
        MediaInfo? direct = await ProbeFile(uri, ct);
        if (direct is not null) return direct;
        var args = YtArgs(); args.AddRange(["--dump-single-json", "--skip-download", "--", uri.AbsoluteUri]);
        var result = await runner.RunAsync(tools.YtDlp, args, cancellation: ct);
        if (result.ExitCode == 0)
        {
            using var doc = JsonDocument.Parse(result.Output);
            var item = doc.RootElement;
            var heights = item.TryGetProperty("formats", out var formats) ? formats.EnumerateArray().Select(f => Int(f, "height")).Where(h => h > 0).Distinct().OrderDescending().ToArray() : [];
            return new(uri.AbsoluteUri, Str(item, "title") ?? "Медиа", Str(item, "extractor_key") ?? uri.Host, MediaKind.Video, Number(item, "duration"), Str(item, "thumbnail"), heights);
        }
        try { return await AnalyzeGallery(uri, ct); }
        catch (InvalidOperationException ex) { throw new InvalidOperationException(FriendlyError(result.Error + "\n" + ex.Message)); }
    }

    private async Task<MediaInfo?> ProbeFile(Uri uri, CancellationToken ct)
    {
        // Known media pages often reject HEAD or serve a video MIME for player endpoints.
        if (new[] { "youtube.com", "youtu.be", "tiktok.com", "instagram.com" }.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Contains("html", StringComparison.OrdinalIgnoreCase) || type.Contains("json", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(type)) return null;
            var name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "download");
            return new(uri.AbsoluteUri, Input.SafeName(name), uri.Host, MediaKind.File, Size: response.Content.Headers.ContentLength);
        }
        catch (HttpRequestException) { return null; }
    }

    private async Task<MediaInfo> AnalyzeGallery(Uri uri, CancellationToken ct)
    {
        var args = GalleryArgs(); args.AddRange(["--dump-json", "--range", "1", "--", uri.AbsoluteUri]);
        var result = await runner.RunAsync(tools.Python, args, cancellation: ct);
        if (result.ExitCode != 0) throw new InvalidOperationException(FriendlyError(result.Error));
        using var doc = JsonDocument.Parse(result.Output);
        string? title = null; string? image = null;
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (entry.GetArrayLength() < 2) continue;
            var type = entry[0].GetInt32();
            if (type == 2 && entry[1].ValueKind == JsonValueKind.Object) title = Str(entry[1], "title") ?? Str(entry[1], "description") ?? Str(entry[1], "post_id") ?? Str(entry[1], "id");
            if (type == 3 && entry[1].ValueKind == JsonValueKind.String) image = entry[1].GetString();
        }
        if (image is null) throw new InvalidOperationException("В галерее не найдены доступные изображения.");
        return new(uri.AbsoluteUri, string.IsNullOrWhiteSpace(title) ? "Галерея · " + uri.Host : title[..Math.Min(title.Length, 180)], uri.Host, MediaKind.Gallery, Thumbnail: image);
    }

    public async Task<string> DownloadAsync(DownloadJob job, IProgress<DownloadProgress> progress, CancellationToken cancellation)
    {
        Input.WebUrl(job.Media.Url);
        var folder = Path.Combine(Path.GetFullPath(job.Directory), Input.SafeName(job.Media.Title)[..Math.Min(Input.SafeName(job.Media.Title).Length, 70)] + "_" + job.Id[..8]);
        Directory.CreateDirectory(folder);
        if (job.Media.Kind == MediaKind.File && job.Options.Mode != DownloadMode.Mp3) return await DownloadFile(job, folder, progress, cancellation);
        if (job.Media.Kind == MediaKind.Gallery)
        {
            var args = GalleryArgs(); args.AddRange(["--destination", folder, "--directory", "", "--write-metadata", "--download-archive", Path.Combine(folder, ".archive.sqlite3"), "--", job.Media.Url]);
            var count = 0;
            var result = await runner.RunAsync(tools.Python, args, line =>
            {
                if (!string.IsNullOrWhiteSpace(line)) progress.Report(new(0, $"Сохранено объектов: {++count}. Галерея загружается…"));
            }, cancellation);
            if (result.ExitCode != 0) throw new InvalidOperationException(FriendlyError(result.Error));
            if (!Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(f => !Path.GetFileName(f).StartsWith('.') && !f.EndsWith(".json"))) throw new InvalidOperationException("Галерея не вернула файлов. Возможно, нужны cookies.");
            progress.Report(new(100, "Галерея сохранена")); return folder;
        }
        var yt = YtArgs();
        yt.AddRange(["--newline", "--progress", "--no-simulate", "--windows-filenames", "--continue", "--no-mtime", "--paths", folder, "--output", "%(title).100B [%(id)s].%(ext)s", "--progress-template", "download:SD_PROGRESS:%(progress)j", "--progress-template", "postprocess:SD_PROCESS:%(progress)j", "--print", "after_move:SD_FILE:%(filepath)j"]);
        if (job.Options.Mode == DownloadMode.Mp3)
            yt.AddRange(["--format", "bestaudio/best", "--extract-audio", "--audio-format", "mp3", "--audio-quality", job.Options.AudioBitrate.ToString(CultureInfo.InvariantCulture) + "K", "--embed-metadata", "--embed-thumbnail", "--convert-thumbnails", "jpg"]);
        else
        {
            var limit = job.Options.MaxHeight > 0 ? $"[height<={job.Options.MaxHeight}]" : "";
            yt.AddRange(["--format", $"bv*{limit}+ba/b{limit}", "--merge-output-format", "mkv", "--embed-metadata"]);
        }
        yt.AddRange(["--", job.Media.Url]);
        string? output = null;
        var run = await runner.RunAsync(tools.YtDlp, yt, line =>
        {
            if (line.StartsWith("SD_FILE:"))
            {
                try { output = JsonSerializer.Deserialize<string>(line[8..]); } catch (JsonException) { }
            }
            else if (line.StartsWith("SD_PROCESS:")) progress.Report(new(99, "FFmpeg · обработка и сохранение…", true));
            else if (line.StartsWith("SD_PROGRESS:"))
            {
                try
                {
                    using var data = JsonDocument.Parse(line[12..]); var p = data.RootElement;
                    var total = Number(p, "total_bytes"); if (total <= 0) total = Number(p, "total_bytes_estimate");
                    var percent = total > 0 ? Number(p, "downloaded_bytes") * 100 / total : 0;
                    var speed = Number(p, "speed"); var eta = Number(p, "eta");
                    var detail = $"{Bytes(Number(p, "downloaded_bytes"))}{(total > 0 ? " / " + Bytes(total) : "")}  ·  {Bytes(speed)}/с{(eta > 0 ? $"  ·  {TimeSpan.FromSeconds(Math.Min(eta, 864000)):m\\:ss}" : "")}";
                    progress.Report(new(Math.Min(percent, 99), detail));
                }
                catch (JsonException) { }
            }
        }, cancellation);
        if (run.ExitCode != 0) throw new InvalidOperationException(FriendlyError(run.Error));
        if (output is null || !File.Exists(output)) throw new IOException("Загрузчик завершился без готового файла. Проверьте доступность ссылки.");
        progress.Report(new(100, "Файл сохранён")); return output;
    }

    private async Task<string> DownloadFile(DownloadJob job, string folder, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(job.Media.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? job.Media.Title;
        var target = Path.Combine(folder, Input.SafeName(name)); var part = target + ".part";
        var total = response.Content.Headers.ContentLength;
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920]; long received = 0; var clock = System.Diagnostics.Stopwatch.StartNew(); var last = TimeSpan.Zero;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), ct); received += n;
                if (clock.Elapsed - last < TimeSpan.FromMilliseconds(150)) continue;
                last = clock.Elapsed;
                progress.Report(new(total > 0 ? Math.Min(99, received * 100d / total.Value) : 0, $"{Bytes(received)}{(total > 0 ? " / " + Bytes(total.Value) : "")}  ·  {Bytes(received / Math.Max(clock.Elapsed.TotalSeconds, .01))}/с"));
            }
            if (total.HasValue && received != total.Value) throw new IOException("Соединение оборвалось до завершения файла. Нажмите повторить.");
        }
        ct.ThrowIfCancellationRequested(); File.Move(part, target, true);
        progress.Report(new(100, "Файл сохранён")); return target;
    }

    public async Task<IReadOnlyList<SearchCandidate>> SearchTrackAsync(SpotifyTrack track, CancellationToken cancellation)
    {
        var args = YtArgs(); args.AddRange(["--flat-playlist", "--playlist-end", "6", "--dump-single-json", "--", $"ytsearch6:{track.Artist} {track.Title} audio"]);
        // Search is intentionally a small playlist, while ordinary URL downloads never expand playlists.
        args.Remove("--no-playlist"); args.Insert(0, "--yes-playlist");
        var result = await runner.RunAsync(tools.YtDlp, args, cancellation: cancellation);
        if (result.ExitCode != 0) throw new InvalidOperationException(FriendlyError(result.Error));
        using var doc = JsonDocument.Parse(result.Output);
        if (!doc.RootElement.TryGetProperty("entries", out var entries)) return [];
        return entries.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e =>
        {
            var id = Str(e, "id"); var url = Str(e, "webpage_url") ?? Str(e, "url") ?? "https://www.youtube.com/watch?v=" + id;
            var title = Str(e, "title") ?? "Без названия"; var uploader = Str(e, "uploader") ?? Str(e, "channel") ?? "YouTube";
            return new SearchCandidate(url, title, uploader, Number(e, "duration"), MatchScore(track, title, uploader, Number(e, "duration")));
        }).OrderByDescending(c => c.Score).ToArray();
    }
    public static double MatchScore(SpotifyTrack track, string title, string uploader, double duration)
    {
        static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
        static double Overlap(string target, string source)
        {
            var words = Normalize(target).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
            return words.Length == 0 ? 0 : words.Count(w => Normalize(source).Contains(w, StringComparison.Ordinal)) / (double)words.Length;
        }
        var score = .45 * Overlap(track.Title, title) + .30 * Overlap(track.Artist, title + " " + uploader);
        if (duration > 0 && track.DurationMs > 0) score += .25 * Math.Max(0, 1 - Math.Abs(duration - track.DurationMs / 1000d) / Math.Max(15, track.DurationMs / 1000d * .2));
        foreach (var word in new[] { "cover", "karaoke", "sped up", "slowed", "remix", "live" })
            if (title.Contains(word, StringComparison.OrdinalIgnoreCase) && !track.Title.Contains(word, StringComparison.OrdinalIgnoreCase)) score -= .2;
        return Math.Clamp(score, 0, 1);
    }
    public static string FriendlyError(string error)
    {
        if (error.Contains("Sign in", StringComparison.OrdinalIgnoreCase) || error.Contains("login", StringComparison.OrdinalIgnoreCase) || error.Contains("cookies", StringComparison.OrdinalIgnoreCase))
            return "Сайт требует вход или проверку пользователя. Укажите экспортированный cookies.txt в настройках и повторите. " + Tail(error);
        if (error.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase)) return "Ссылка не поддерживается или на странице нет доступного медиа. Для файла используйте прямую ссылку.";
        return string.IsNullOrWhiteSpace(error) ? "Источник не вернул файл. Попробуйте другую ссылку." : Tail(error);
    }
    private static string Tail(string text) { var s = string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3)); return s[..Math.Min(s.Length, 700)]; }
    public static string Bytes(double n) => n >= 1073741824 ? $"{n / 1073741824:0.0} ГБ" : n >= 1048576 ? $"{n / 1048576:0.0} МБ" : n >= 1024 ? $"{n / 1024:0} КБ" : $"{n:0} Б";
    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double Number(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : 0;
    private static int Int(JsonElement e, string p) => (int)Number(e, p);
}
