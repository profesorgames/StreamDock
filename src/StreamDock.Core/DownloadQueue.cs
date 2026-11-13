using System.Collections.ObjectModel;

namespace StreamDock.Core;

// All public mutations and progress callbacks run on the UI synchronization context.
public sealed class DownloadQueue : Observable, IDisposable
{
    private readonly IDownloader downloader;
    private readonly string historyPath;
    private readonly Dictionary<string, CancellationTokenSource> running = [];
    private readonly List<Task> tasks = [];
    private bool disposed;
    public ObservableCollection<DownloadJob> Jobs { get; } = [];
    public int Concurrency { get; set; } = 2;
    private bool paused;
    public bool Paused { get => paused; set { Set(ref paused, value); Pump(); } }
    public event Action<DownloadJob>? Finished;
    public event Action<Exception>? PersistenceFailed;

    public DownloadQueue(IDownloader downloader, string historyPath)
    {
        this.downloader = downloader; this.historyPath = historyPath;
        foreach (var job in Storage.Read<List<DownloadJob>>(historyPath, () => []))
        {
            if (job.State is JobState.Downloading or JobState.Processing or JobState.Queued) { job.State = JobState.Interrupted; job.Detail = "Нажмите ↻, чтобы продолжить после перезапуска"; }
            Jobs.Add(job);
        }
    }
    public DownloadJob Add(MediaInfo media, DownloadOptions options, string directory)
    {
        if (disposed) throw new ObjectDisposedException(nameof(DownloadQueue));
        var job = new DownloadJob { Media = media, Options = options, Directory = Path.GetFullPath(directory) };
        Jobs.Insert(0, job); Save(); Pump(); return job;
    }
    public void Retry(DownloadJob job)
    {
        if (!job.CanRetry || running.ContainsKey(job.Id)) return;
        job.Percent = 0; job.State = JobState.Queued; job.Detail = "Ожидает своей очереди"; Save(); Pump();
    }
    public void Cancel(DownloadJob job)
    {
        if (running.TryGetValue(job.Id, out var source)) source.Cancel();
        else if (job.State == JobState.Queued) { job.State = JobState.Cancelled; job.Detail = "Отменено пользователем"; Save(); }
    }
    public void ClearCompleted()
    {
        foreach (var job in Jobs.Where(j => j.State == JobState.Completed).ToArray()) Jobs.Remove(job);
        Save();
    }
    private void Pump()
    {
        if (paused || disposed) return;
        foreach (var job in Jobs.Reverse().Where(j => j.State == JobState.Queued).ToArray())
        {
            if (running.Count >= Math.Clamp(Concurrency, 1, 4)) break;
            var cts = new CancellationTokenSource(); running.Add(job.Id, cts);
            job.State = JobState.Downloading; job.Detail = "Подключение к источнику…";
            tasks.RemoveAll(t => t.IsCompleted);
            tasks.Add(Run(job, cts));
        }
        Save(); Changed(nameof(Jobs));
    }
    private async Task Run(DownloadJob job, CancellationTokenSource cts)
    {
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                if (!job.IsActive || cts.IsCancellationRequested) return;
                job.Percent = p.Percent; job.Detail = p.Detail;
                job.State = p.Processing ? JobState.Processing : JobState.Downloading;
            });
            job.OutputPath = await downloader.DownloadAsync(job, progress, cts.Token);
            job.State = JobState.Completed; job.Percent = 100; job.Detail = "Сохранено · " + Path.GetFileName(job.OutputPath);
        }
        catch (OperationCanceledException) { job.State = JobState.Cancelled; job.Detail = "Отменено пользователем"; }
        catch (Exception ex) { job.State = JobState.Failed; job.Detail = ex.Message; }
        finally
        {
            running.Remove(job.Id); cts.Dispose(); Save();
            if (!disposed) { Finished?.Invoke(job); Pump(); }
        }
    }
    private void Save()
    {
        try { Storage.Write(historyPath, Jobs.ToList()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PersistenceFailed?.Invoke(ex); }
    }
    public async Task StopAsync()
    {
        disposed = true;
        foreach (var cts in running.Values.ToArray()) cts.Cancel();
        await Task.WhenAll(tasks.ToArray()); Save();
    }
    public void Dispose()
    {
        disposed = true;
        foreach (var cts in running.Values.ToArray()) cts.Cancel();
        Save(); GC.SuppressFinalize(this);
    }
}
