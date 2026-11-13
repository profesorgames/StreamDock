namespace StreamDock.Core;

public sealed record ToolPaths(string Root)
{
    public string YtDlp => Path.Combine(Root, "yt-dlp.exe");
    public string Ffmpeg => Path.Combine(Root, "ffmpeg.exe");
    public string Ffprobe => Path.Combine(Root, "ffprobe.exe");
    public string Deno => Path.Combine(Root, "deno.exe");
    public string Python => Path.Combine(Root, "python", "python.exe");
}
