using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamDock.Core;

namespace StreamDock;

public static class SelfTests
{
    public static async Task<bool> RunAsync(string dataRoot, string report)
    {
        var checks = new List<object>();
        try
        {
            var tools = await ToolBundle.EnsureAsync(dataRoot);
            var runner = new ProcessRunner();
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            async Task<ProcessResult> Run(string name, string executable, params string[] args)
            {
                var result = await runner.RunAsync(executable, args, cancellation: limit.Token);
                if (result.ExitCode != 0) throw new InvalidOperationException(name + ": " + result.Error + result.Output);
                checks.Add(new { Name = name, Success = true, Output = (result.Output + result.Error)[..Math.Min((result.Output + result.Error).Length, 500)] });
                return result;
            }
            await Run("yt-dlp", tools.YtDlp, "--version");
            await Run("ffmpeg", tools.Ffmpeg, "-version");
            await Run("ffprobe", tools.Ffprobe, "-version");
            await Run("deno", tools.Deno, "eval", "console.log('StreamDock JS runtime OK')");
            await Run("embedded-python", tools.Python, "--version");
            await Run("gallery-dl", tools.Python, "-m", "gallery_dl", "--version");
            await Run("gallery-imports", tools.Python, "-c", "import requests, certifi, gallery_dl; from gallery_dl import extractor; import ssl; assert len(extractor.modules) > 100; print('TLS and gallery extractors OK')");
            var store = new DpapiSecretStore(Path.Combine(dataRoot, "secret-test.dat"));
            store.Write("token-roundtrip-✓"); if (store.Read() != "token-roundtrip-✓") throw new InvalidOperationException("DPAPI roundtrip failed");
            store.Delete(); checks.Add(new { Name = "DPAPI", Success = true });
            var source = Path.Combine(dataRoot, "fixture.mp4");
            await Run("generate-video-fixture", tools.Ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=blue:s=320x180:d=1", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:v", "libx264", "-c:a", "aac", "-shortest", "-y", source);
            using var fixture = new LocalFixture(await File.ReadAllBytesAsync(source));
            using var http = new System.Net.Http.HttpClient();
            var service = new MediaService(tools, new AppSettings(), http, runner);
            var info = await service.AnalyzeAsync(fixture.Url, limit.Token);
            if (info.Kind != MediaKind.File) throw new InvalidOperationException("Direct file detection failed");
            var progress = new Progress<DownloadProgress>();
            var raw = await service.DownloadAsync(new DownloadJob { Media = info, Options = new(DownloadMode.Original), Directory = Path.Combine(dataRoot, "files") }, progress, limit.Token);
            if (!File.ReadAllBytes(raw).SequenceEqual(File.ReadAllBytes(source))) throw new InvalidOperationException("Downloaded file bytes differ");
            checks.Add(new { Name = "HTTP-file-download", Success = true });
            var audio = await service.DownloadAsync(new DownloadJob { Media = info, Options = new(DownloadMode.Mp3), Directory = Path.Combine(dataRoot, "music") }, progress, limit.Token);
            var probe = await Run("MP3-probe", tools.Ffprobe, "-v", "error", "-show_entries", "stream=codec_name", "-of", "json", audio);
            using (var json = JsonDocument.Parse(probe.Output))
                if (json.RootElement.GetProperty("streams")[0].GetProperty("codec_name").GetString() != "mp3") throw new InvalidOperationException("Output is not MP3");
            checks.Add(new { Name = "yt-dlp-to-FFmpeg-MP3", Success = true, Bytes = new FileInfo(audio).Length });
            // Test startup formatting under a private test value, leaving the actual Run entry untouched.
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\StreamDock\SelfTest"))
            {
                key.SetValue("Run", $"\"{Environment.ProcessPath}\" --tray");
                if (key.GetValue("Run") is not string command || !command.EndsWith("\" --tray", StringComparison.Ordinal)) throw new InvalidOperationException("Startup quoting failed");
            }
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\StreamDock\SelfTest", false);
            checks.Add(new { Name = "startup-command", Success = true });
            Storage.Write(report, new { Success = true, OS = Environment.OSVersion.ToString(), Executable = Environment.ProcessPath, Checks = checks });
            return true;
        }
        catch (Exception ex)
        {
            Storage.Write(report, new { Success = false, Error = ex.ToString(), Checks = checks }); return false;
        }
    }
    private sealed class LocalFixture : IDisposable
    {
        private readonly HttpListener listener = new();
        public string Url { get; }
        public LocalFixture(byte[] bytes)
        {
            var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
            var root = $"http://127.0.0.1:{port}/"; Url = root + "fixture.mp4";
            listener.Prefixes.Add(root); listener.Start();
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    try
                    {
                        var context = await listener.GetContextAsync();
                        context.Response.ContentType = "video/mp4"; context.Response.ContentLength64 = bytes.Length;
                        if (context.Request.HttpMethod != "HEAD") await context.Response.OutputStream.WriteAsync(bytes);
                        context.Response.Close();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException) { break; }
                }
            });
        }
        public void Dispose() => listener.Close();
    }
}
