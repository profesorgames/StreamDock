using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using StreamDock.Core;

if (args.FirstOrDefault() == "--child") { Console.Write(JsonSerializer.Serialize(args.Skip(1))); return 0; }
if (args.FirstOrDefault() == "--sleep") { Console.WriteLine("ready"); await Task.Delay(30000); return 0; }
return SingleThreadContext.Run(Suite.Run);

static class Suite
{
    private static int passed;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "StreamDock-tests-" + Guid.NewGuid().ToString("N"));
    public static async Task<int> Run()
    {
        Directory.CreateDirectory(Root);
        try
        {
            await Test("URL validation and Windows filenames", () =>
            {
                foreach (var input in new[] { "file:///C:/Windows/win.ini", "javascript:alert(1)", "ytsearch:hello", "https://user:pass@example.com", "not a url" }) Throws<ArgumentException>(() => Input.WebUrl(input));
                Check(Input.WebUrl(" https://example.com/a?q=x%20y ").Host == "example.com");
                Check(Input.SafeName("../A:B?C.mp3") == "_A_B_C.mp3");
                Check(Input.SafeName("CON.txt") == "file_CON.txt"); Check(Input.SafeName("LPT9") == "file_LPT9");
                Check(Input.SafeName(new string('a', 500)).Length == 150); return Task.CompletedTask;
            });
            await Test("PKCE RFC 7636 vector and fresh cryptographic state", () =>
            {
                Check(SpotifyService.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
                var a = SpotifyService.CreatePkce(); var b = SpotifyService.CreatePkce();
                Check(a.Verifier != b.Verifier && a.State != b.State && a.Challenge == SpotifyService.Challenge(a.Verifier));
                Check(!a.Challenge.Contains('=') && a.Verifier.Length >= 43); return Task.CompletedTask;
            });
            await Test("Spotify matching prefers correct artist, duration and original version", () =>
            {
                var song = new SpotifyTrack("1", "Morning Sun", "Example Artist", 180000, null);
                var correct = MediaService.MatchScore(song, "Example Artist - Morning Sun (Official Audio)", "Example Artist - Topic", 180);
                Check(correct > .99);
                Check(correct > MediaService.MatchScore(song, "Morning Sun cover live", "Someone", 250));
                Check(MediaService.MatchScore(song, "Morning Sun", "Example Artist", 600) < correct); return Task.CompletedTask;
            });
            await Test("Atomic storage recovers malformed or missing history", () =>
            {
                var path = Path.Combine(Root, "state.json"); Storage.Write(path, new AppSettings { ConcurrentDownloads = 3 });
                Check(Storage.Read(path, () => new AppSettings()).ConcurrentDownloads == 3);
                File.WriteAllText(path, "{broken"); Check(Storage.Read(path, () => new AppSettings()).ConcurrentDownloads == 2);
                return Task.CompletedTask;
            });
            await Test("Direct file detection, safe attachment name and byte-exact download", async () =>
            {
                var bytes = Encoding.UTF8.GetBytes("portable download ✓");
                using var http = new HttpClient(new Handler(request =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "../../CON.txt" }; return response;
                }));
                var service = Service(http, new FakeRunner()); var info = await service.AnalyzeAsync("https://example.com/a", default);
                Check(info.Kind == MediaKind.File);
                var target = await service.DownloadAsync(new DownloadJob { Media = info, Directory = Root, Options = new(DownloadMode.Original) }, new Progress<DownloadProgress>(), default);
                Check(Path.GetFullPath(target).StartsWith(Root)); Check(File.ReadAllBytes(target).SequenceEqual(bytes));
                Check(!File.Exists(target + ".part"));
            });
            await Test("Cancelled HTTP download never becomes a finished file", async () =>
            {
                using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream()) }));
                var service = Service(http, new FakeRunner()); using var cts = new CancellationTokenSource(100);
                await ThrowsAsync<OperationCanceledException>(() => service.DownloadAsync(new DownloadJob { Media = new("https://example.com/slow", "slow.bin", "test", MediaKind.File), Directory = Root }, new Progress<DownloadProgress>(), cts.Token));
                Check(!Directory.EnumerateFiles(Root, "slow.bin", SearchOption.AllDirectories).Any());
            });
            await Test("Signed file URL is recognized when the server rejects HEAD", async () =>
            {
                using var http = new HttpClient(new Handler(request =>
                {
                    if (request.Method == HttpMethod.Head) return new(HttpStatusCode.Forbidden);
                    Check(request.Headers.Range?.Ranges.Single().To == 0);
                    var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([42]) };
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, 4096); return response;
                }));
                var info = await Service(http, new FakeRunner()).AnalyzeAsync("https://example.com/image.png?signature=test", default);
                Check(info.Kind == MediaKind.File && info.Size == 4096);
            });
            await Test("Video extractor parses available resolutions and propagates quality cap", async () =>
            {
                using var http = HtmlHttp();
                var runner = new FakeRunner { Output = """{"title":"Sample","duration":12,"extractor_key":"Youtube","formats":[{"height":1080},{"height":720},{"height":720},{"height":null}]}""" };
                var service = Service(http, runner); var info = await service.AnalyzeAsync("https://www.youtube.com/watch?v=x", default);
                Check(info.Heights!.SequenceEqual([1080, 720]));
                var output = Path.Combine(Root, "video.mkv"); File.WriteAllText(output, "fixture"); runner.Output = "";
                runner.Lines = ["SD_PROGRESS:{\"downloaded_bytes\":50,\"total_bytes\":100,\"speed\":100,\"eta\":1}", "SD_FILE:" + JsonSerializer.Serialize(output)];
                var file = await service.DownloadAsync(new DownloadJob { Media = info, Directory = Root, Options = new(DownloadMode.Video, 720) }, new Progress<DownloadProgress>(), default);
                Check(file == output); var format = runner.Arguments[runner.Arguments.IndexOf("--format") + 1];
                Check(format == "bv*[height<=720]+ba/b[height<=720]"); Check(runner.Arguments.Contains("--no-playlist"));
                Check(runner.Arguments.Contains("--no-remote-components"));
            });
            await Test("MP3 extraction includes FFmpeg, bitrate, metadata and no shell invocation", async () =>
            {
                using var http = HtmlHttp(); var output = Path.Combine(Root, "song.mp3"); File.WriteAllText(output, "fixture");
                var runner = new FakeRunner { Lines = ["SD_FILE:" + JsonSerializer.Serialize(output)] }; var service = Service(http, runner);
                var url = "https://example.com/video?a=1&b=$(test);x=2";
                await service.DownloadAsync(new DownloadJob { Media = new(url, "Sample", "Test", MediaKind.Video), Options = new(DownloadMode.Mp3, AudioBitrate: 192), Directory = Root }, new Progress<DownloadProgress>(), default);
                Check(runner.Arguments.Contains("192K") && runner.Arguments.Contains("--extract-audio") && runner.Arguments.Contains("--embed-metadata"));
                Check(runner.Arguments[^1] == url && runner.Arguments[^2] == "--");
            });
            await Test("Instagram gallery and external search parse independent extractors", async () =>
            {
                using var http = HtmlHttp(); var runner = new FakeRunner { Output = """[[2,{"description":"Gallery"}],[3,"https://example.com/img.jpg",{"extension":"jpg"}]]""" };
                var service = Service(http, runner); var info = await service.AnalyzeAsync("https://www.instagram.com/p/abc/", default);
                Check(info.Kind == MediaKind.Gallery && info.Thumbnail!.EndsWith("img.jpg")); Check(runner.Arguments.Contains("gallery_dl"));
                runner.Output = """{"entries":[{"id":"bad","url":"https://youtube.com/watch?v=bad","title":"Song cover","uploader":"Other","duration":400},{"id":"ok","url":"https://youtube.com/watch?v=ok","title":"Artist - Song","uploader":"Artist","duration":180}]}""";
                var results = await service.SearchTrackAsync(new("id", "Song", "Artist", 180000, null), default);
                Check(results[0].Url.EndsWith("ok")); Check(runner.Arguments.Contains("--yes-playlist") && !runner.Arguments.Contains("--no-playlist"));
            });
            await Test("Extractor failure explains login requirements", () =>
            {
                Check(MediaService.FriendlyError("ERROR: Sign in to confirm you're not a bot").Contains("cookies.txt"));
                return Task.CompletedTask;
            });
            await Test("Spotify refresh is used, 204 is handled, disconnect deletes token", async () =>
            {
                var secrets = new MemorySecrets { Value = JsonSerializer.Serialize(new SpotifyToken("expired", "refresh", DateTimeOffset.UtcNow.AddDays(-1), new string('a', 32))) };
                var requests = 0;
                using var http = new HttpClient(new Handler(request =>
                {
                    requests++;
                    if (request.RequestUri!.AbsolutePath == "/api/token") return Json("""{"access_token":"fresh","expires_in":3600}""");
                    Check(request.Headers.Authorization?.Parameter == "fresh"); return new(HttpStatusCode.NoContent);
                }));
                using var spotify = new SpotifyService(http, secrets, new string('a', 32));
                Check(await spotify.CurrentTrackAsync(default) is null); Check(requests == 2);
                Check(secrets.Value!.Contains("refresh")); spotify.Disconnect(); Check(!spotify.Connected && secrets.Value is null);
            });
            await Test("Spotify current-track metadata and access failure", async () =>
            {
                var secrets = new MemorySecrets { Value = JsonSerializer.Serialize(new SpotifyToken("valid", "refresh", DateTimeOffset.UtcNow.AddDays(1), new string('a', 32))) };
                using var http = new HttpClient(new Handler(_ => Json("""{"item":{"type":"track","id":"t","name":"Song","artists":[{"name":"Artist"}],"duration_ms":180000,"album":{"images":[{"url":"https://example.com/cover.jpg"}]}}}""")));
                using var spotify = new SpotifyService(http, secrets, new string('a', 32)); var song = await spotify.CurrentTrackAsync(default);
                Check(song?.Artist == "Artist" && song.DurationMs == 180000);
                spotify.SetClientId(new string('b', 32)); await ThrowsAsync<InvalidOperationException>(() => spotify.CurrentTrackAsync(default));
            });
            await Test("Queue preserves FIFO, bounds concurrency, cancels, retries and restores interrupted jobs", async () =>
            {
                var downloader = new GatedDownloader(); var path = Path.Combine(Root, "queue.json");
                using var queue = new DownloadQueue(downloader, path) { Concurrency = 2, Paused = true };
                var a = queue.Add(new("https://example.com/1", "1", "test", MediaKind.File), new(DownloadMode.Original), Root);
                var b = queue.Add(new("https://example.com/2", "2", "test", MediaKind.File), new(DownloadMode.Original), Root);
                var c = queue.Add(new("https://example.com/3", "3", "test", MediaKind.File), new(DownloadMode.Original), Root);
                Check(downloader.Started.Count == 0); queue.Paused = false;
                await Until(() => downloader.Started.Count == 2); Check(downloader.Started.SequenceEqual([a.Id, b.Id]));
                Check(c.State == JobState.Queued); queue.Cancel(a); await Until(() => a.State == JobState.Cancelled && c.IsActive);
                downloader.Release(b.Id); downloader.Release(c.Id); await Until(() => b.State == JobState.Completed && c.State == JobState.Completed);
                queue.Retry(a); await Until(() => a.IsActive); downloader.Release(a.Id); await Until(() => a.State == JobState.Completed);
                Check(downloader.Maximum == 2); queue.ClearCompleted(); Check(queue.Jobs.Count == 0);
                queue.Paused = true; queue.Add(new("https://example.com/4", "4", "test", MediaKind.File), new(DownloadMode.Original), Root);
                using var restored = new DownloadQueue(downloader, path); Check(restored.Jobs.Single().State == JobState.Interrupted);
                await queue.StopAsync();
            });
            await Test("Process runner preserves Unicode and literal shell characters", async () =>
            {
                var (exe, prefix) = Child(); var values = new[] { "a b", "$(echo unsafe)", "a; echo unsafe", "песня ✓", "\"quote\"" };
                var result = await new ProcessRunner().RunAsync(exe, prefix.Concat(new[] { "--child" }).Concat(values));
                Check(result.ExitCode == 0); Check(JsonSerializer.Deserialize<string[]>(result.Output)!.SequenceEqual(values));
            });
            await Test("Cancellation terminates a running process tree", async () =>
            {
                var (exe, prefix) = Child(); using var cts = new CancellationTokenSource();
                var task = new ProcessRunner().RunAsync(exe, prefix.Concat(["--sleep"]), line => { if (line == "ready") cts.Cancel(); }, cts.Token);
                await ThrowsAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));
            });
            Console.WriteLine($"PASS: {passed} tests"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }
    private static (string, string[]) Child()
    {
        var exe = Environment.ProcessPath!;
        return (exe, Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? [Assembly.GetExecutingAssembly().Location] : []);
    }
    private static async Task Test(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task Until(Func<bool> done) { using var cts = new CancellationTokenSource(5000); while (!done()) await Task.Delay(10, cts.Token); }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static HttpClient HtmlHttp() => new(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("<html></html>", Encoding.UTF8, "text/html") }));
    private static MediaService Service(HttpClient http, IProcessRunner runner) => new(new ToolPaths(Root), new AppSettings(), http, runner);
}

sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
}
sealed class FakeRunner : IProcessRunner
{
    public string Output = "";
    public string[] Lines = [];
    public List<string> Arguments = [];
    public Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, Action<string>? line = null, CancellationToken cancellation = default)
    { Arguments = arguments.ToList(); foreach (var value in Lines) line?.Invoke(value); return Task.FromResult(new ProcessResult(0, Output, "")); }
}
sealed class MemorySecrets : ISecretStore
{
    public string? Value;
    public string? Read() => Value;
    public void Write(string value) => Value = value;
    public void Delete() => Value = null;
}
sealed class GatedDownloader : IDownloader
{
    private readonly Dictionary<string, TaskCompletionSource> gates = [];
    private int active;
    public int Maximum;
    public readonly List<string> Started = [];
    public void Release(string id) => gates[id].TrySetResult();
    public async Task<string> DownloadAsync(DownloadJob job, IProgress<DownloadProgress> progress, CancellationToken cancellation)
    {
        active++; Maximum = Math.Max(Maximum, active); Started.Add(job.Id);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); gates[job.Id] = gate;
        try { await gate.Task.WaitAsync(cancellation); return "fixture"; } finally { active--; }
    }
}
sealed class SlowStream : Stream
{
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { await Task.Delay(10000, cancellationToken); return 0; }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
sealed class SingleThreadContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback, object?)> queue = [];
    public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));
    public static int Run(Func<Task<int>> main)
    {
        var context = new SingleThreadContext(); SetSynchronizationContext(context); var task = main();
        _ = task.ContinueWith(_ => context.queue.CompleteAdding(), TaskScheduler.Default);
        foreach (var (callback, state) in context.queue.GetConsumingEnumerable()) callback(state);
        return task.GetAwaiter().GetResult();
    }
}
