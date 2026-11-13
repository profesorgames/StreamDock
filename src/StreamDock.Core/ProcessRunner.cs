using System.Diagnostics;
using System.Text;

namespace StreamDock.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, Action<string>? line = null, CancellationToken cancellation = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, Action<string>? line = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        // Prevent a host Python configuration from changing the bundled gallery runtime.
        start.Environment.Remove("PYTHONHOME"); start.Environment.Remove("PYTHONPATH");
        using var process = new Process { StartInfo = start };
        process.Start();
        using var registration = cancellation.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        });
        var output = new StringBuilder(); var error = new StringBuilder();
        async Task Drain(StreamReader reader, StringBuilder buffer, bool stdout)
        {
            while (await reader.ReadLineAsync() is { } value)
            {
                if (buffer.Length < 16_000_000) buffer.AppendLine(value);
                if (stdout) line?.Invoke(value);
            }
        }
        await Task.WhenAll(Drain(process.StandardOutput, output, true), Drain(process.StandardError, error, false), process.WaitForExitAsync());
        cancellation.ThrowIfCancellationRequested();
        return new(process.ExitCode, output.ToString(), error.ToString());
    }
}
