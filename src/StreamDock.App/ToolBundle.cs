using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamDock.Core;

namespace StreamDock;

public sealed record BundleFile(string Path, string Sha256, long Size);
public sealed record BundleManifest(string Id, BundleFile[] Files);

public static class ToolBundle
{
    public static async Task<ToolPaths> EnsureAsync(string dataRoot, IProgress<string>? progress = null)
    {
        using var manifestStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("StreamDock.tools.manifest.json")!;
        var manifest = (await JsonSerializer.DeserializeAsync<BundleManifest>(manifestStream))!;
        var parent = Path.Combine(dataRoot, "components");
        var target = Path.Combine(parent, manifest.Id);
        Directory.CreateDirectory(parent);
        // A lock is also used by self-test processes, independently of the UI single-instance mutex.
        var lockPath = Path.Combine(parent, "extract.lock");
        FileStream? gate = null;
        for (var attempt = 0; gate is null; attempt++)
        {
            try { gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 120) { await Task.Delay(500); }
        }
        using (gate)
        {
            progress?.Report("Проверка встроенных компонентов…");
            if (await Verify(target, manifest)) return new(target);
            progress?.Report("Подготовка компонентов · первый запуск может занять минуту…");
            var stage = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(stage);
                using var archiveStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("StreamDock.tools.zip")!;
                using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
                await Task.Run(() => archive.ExtractToDirectory(stage));
                if (!await Verify(stage, manifest)) throw new IOException("Повреждён встроенный архив компонентов. Скачайте EXE заново.");
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(stage, target);
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
        return new(target);
    }
    private static async Task<bool> Verify(string directory, BundleManifest manifest)
    {
        if (!Directory.Exists(directory)) return false;
        foreach (var item in manifest.Files)
        {
            var path = Path.GetFullPath(Path.Combine(directory, item.Path));
            if (!path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            if (!File.Exists(path) || new FileInfo(path).Length != item.Size) return false;
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream);
            if (!Convert.ToHexString(hash).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
