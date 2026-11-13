using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StreamDock.Core;

namespace StreamDock;

public partial class MainWindow : Window
{
    private readonly AppSettings settings;
    private readonly string dataRoot;
    private readonly bool testing;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly CancellationTokenSource lifetime = new();
    private DownloadQueue? queue;
    private MediaService? media;
    private SpotifyService? spotify;
    private ToolPaths? tools;
    private MediaInfo? selection;
    private SpotifyTrack? track;
    private bool stopped;
    private bool forceClose;
    private bool busy;
    private readonly DispatcherTimer stats = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public MainWindow(AppSettings settings, string dataRoot, bool testing)
    {
        this.settings = settings; this.dataRoot = dataRoot; this.testing = testing;
        InitializeComponent();
        FolderBox.Text = settings.DownloadDirectory; TrayCheck.IsChecked = settings.CloseToTray;
        StartupCheck.IsChecked = settings.AutoStart; ConcurrencyBox.SelectedIndex = Math.Clamp(settings.ConcurrentDownloads, 1, 4) - 1;
        ClientIdBox.Text = settings.SpotifyClientId; CookiesBox.Text = settings.CookiesFile;
        stats.Tick += (_, _) => RefreshStats();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("StreamDock/1.0");
        UrlBox.TextChanged += (_, _) => { if (selection is not null && UrlBox.Text.Trim() != selection.Url) { selection = null; MediaCard.Visibility = Visibility.Collapsed; } };
    }
    public async Task InitializeAsync()
    {
        tools = await ToolBundle.EnsureAsync(dataRoot, new Progress<string>(s => { StatusText.Text = s; LoadingText.Text = s; }));
        media = new MediaService(tools, settings, http, new ProcessRunner());
        queue = new DownloadQueue(media, Path.Combine(dataRoot, "queue.json")) { Concurrency = Math.Clamp(settings.ConcurrentDownloads, 1, 4) };
        queue.Finished += job => { RefreshStats(); if (!testing) ((App)Application.Current).Notify(job.State == JobState.Completed ? "Загрузка завершена" : "Не удалось загрузить", job.Title); };
        queue.PersistenceFailed += ex => Notice("Не удалось сохранить историю: " + ex.Message);
        JobsList.ItemsSource = queue.Jobs;
        spotify = new SpotifyService(http, new DpapiSecretStore(Path.Combine(dataRoot, "spotify.dat")), settings.SpotifyClientId);
        UpdateSpotifyStatus();
        LoadingOverlay.Visibility = Visibility.Collapsed; StatusText.Text = "●  Готов к работе · компоненты проверены";
        RefreshStats(); stats.Start();
    }
    private void WindowLoaded(object sender, RoutedEventArgs e) { if (SystemParameters.ClientAreaAnimation) Animate(PageHost); }
    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (DownloadsPage is null || SpotifyPage is null || SettingsPage is null || PageHost is null) return;
        var target = (sender as RadioButton)?.Tag as string;
        foreach (var page in new FrameworkElement[] { DownloadsPage, SpotifyPage, SettingsPage }) page.Visibility = page.Name == target ? Visibility.Visible : Visibility.Collapsed;
        if (SystemParameters.ClientAreaAnimation) Animate(PageHost);
    }
    private static void Animate(UIElement target)
    {
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        var translate = new TranslateTransform(); target.RenderTransform = translate;
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
    public void Notice(string text) { NoticeText.Text = text; NoticeBar.Visibility = Visibility.Visible; }
    private void DismissNotice(object sender, RoutedEventArgs e) => NoticeBar.Visibility = Visibility.Collapsed;
    private async Task Guard(Func<Task> action)
    {
        if (busy || stopped) return;
        busy = true; InspectButton.IsEnabled = false; ConnectButton.IsEnabled = false; FindTrackButton.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) { if (!stopped) Notice("Операция отменена или источник не ответил вовремя. Повторите попытку."); }
        catch (Exception ex) { Notice(ex.Message); }
        finally { busy = false; if (!stopped) { InspectButton.IsEnabled = true; ConnectButton.IsEnabled = true; FindTrackButton.IsEnabled = true; StatusText.Text = "●  Готов к работе"; } }
    }
    private async void InspectClick(object sender, RoutedEventArgs e) => await Inspect();
    private async void UrlKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await Inspect(); } }
    private Task Inspect() => Guard(async () =>
    {
        if (media is null) return;
        var url = UrlBox.Text.Trim(); Input.WebUrl(url); StatusText.Text = "Поиск медиа и доступного качества…";
        selection = await media.AnalyzeAsync(url, lifetime.Token);
        // Discard results if the user edited the input while the extractor ran.
        if (url != UrlBox.Text.Trim()) { selection = null; return; }
        MediaTitle.Text = selection.Title;
        MediaDescription.Text = selection.Site + (selection.Duration > 0 ? $"  ·  {TimeSpan.FromSeconds(selection.Duration):h\\:mm\\:ss}" : "") + (selection.Size > 0 ? "  ·  " + MediaService.Bytes(selection.Size.Value) : "");
        ModeBox.SelectedIndex = selection.Kind == MediaKind.Video ? 0 : 2;
        ModeBox.IsEnabled = selection.Kind != MediaKind.Gallery;
        PopulateQuality(); MediaCard.Visibility = Visibility.Visible;
        Animate(MediaCard);
    });
    private void ModeChanged(object sender, SelectionChangedEventArgs e) { if (QualityBox is not null) PopulateQuality(); }
    private void PopulateQuality()
    {
        QualityBox.Items.Clear();
        if (ModeBox.SelectedIndex == 1)
        {
            foreach (var rate in new[] { 320, 256, 192, 128 }) QualityBox.Items.Add(new ComboBoxItem { Content = $"{rate} кбит/с · MP3", Tag = rate });
        }
        else if (ModeBox.SelectedIndex == 0 && selection?.Kind == MediaKind.Video)
        {
            var heights = selection.Heights?.Where(h => h >= 144).ToArray() ?? [];
            QualityBox.Items.Add(new ComboBoxItem { Content = "Лучшее качество", Tag = 0 });
            foreach (var height in new[] { 4320, 2160, 1440, 1080, 720, 480, 360, 240, 144 }.Where(h => heights.Length == 0 || (h <= heights.Max() && heights.Any(x => x <= h))))
                QualityBox.Items.Add(new ComboBoxItem { Content = $"До {height}p", Tag = height });
        }
        else QualityBox.Items.Add(new ComboBoxItem { Content = "Исходное качество", Tag = 0 });
        QualityBox.SelectedIndex = 0;
        QualityBox.IsEnabled = ModeBox.SelectedIndex != 2;
    }
    private void AddClick(object sender, RoutedEventArgs e)
    {
        if (queue is null || selection is null) return;
        try
        {
            if (ModeBox.SelectedIndex == 0 && selection.Kind == MediaKind.File) throw new InvalidOperationException("Для прямой ссылки выберите исходный файл или MP3 для аудио/видео.");
            if (ModeBox.SelectedIndex == 2 && selection.Kind == MediaKind.Video) throw new InvalidOperationException("Для медиа со страницы выберите видео или MP3.");
            var mode = ModeBox.SelectedIndex == 1 ? DownloadMode.Mp3 : ModeBox.SelectedIndex == 2 ? DownloadMode.Original : DownloadMode.Video;
            var quality = QualityBox.SelectedItem is ComboBoxItem item && item.Tag is int n ? n : 0;
            queue.Add(selection, new(mode, mode == DownloadMode.Video ? quality : 0, mode == DownloadMode.Mp3 ? quality : 320), settings.DownloadDirectory);
            selection = null; MediaCard.Visibility = Visibility.Collapsed; UrlBox.Clear(); RefreshStats();
        }
        catch (Exception ex) { Notice(ex.Message); }
    }
    private void RefreshStats()
    {
        if (queue is null) return;
        var active = queue.Jobs.Count(j => j.IsActive); var waiting = queue.Jobs.Count(j => j.State == JobState.Queued); var done = queue.Jobs.Count(j => j.State == JobState.Completed);
        QueueStats.Text = queue.Jobs.Count == 0 ? "Пока пусто" : $"{active} в работе  ·  {waiting} ожидают  ·  {done} готовы";
        EmptyState.Visibility = queue.Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Content = queue.Paused ? "▶  Продолжить" : "Ⅱ  Пауза очереди";
    }
    private void PauseClick(object sender, RoutedEventArgs e) { if (queue is not null) { queue.Paused = !queue.Paused; RefreshStats(); Notice(queue.Paused ? "Новые задания приостановлены. Текущие загрузки продолжаются; для остановки нажмите ✕ рядом с файлом." : "Очередь продолжена."); } }
    private void ClearClick(object sender, RoutedEventArgs e) { queue?.ClearCompleted(); RefreshStats(); }
    private void CancelJobClick(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is DownloadJob job) queue?.Cancel(job); }
    private void RetryClick(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is DownloadJob job) queue?.Retry(job); }
    private void JobFolderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadJob job) return;
        Try(() => WindowsServices.OpenFolder(job.OutputPath ?? job.Directory));
    }
    public void ShowDownloadsFolder() => Try(() => WindowsServices.OpenFolder(settings.DownloadDirectory));
    private void OpenDownloads(object sender, RoutedEventArgs e) => ShowDownloadsFolder();
    private void OpenDataClick(object sender, RoutedEventArgs e) => Try(() => WindowsServices.OpenFolder(dataRoot));
    private void OpenComponentsClick(object sender, RoutedEventArgs e) { if (tools is not null) Try(() => WindowsServices.OpenFolder(tools.Root)); }
    private void ChooseFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Куда сохранять загрузки", InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : settings.DownloadDirectory };
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }
    private void ChooseCookiesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Cookies в формате Netscape", Filter = "Cookies (*.txt)|*.txt|Все файлы (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) CookiesBox.Text = dialog.FileName;
    }
    private void SaveSettingsClick(object sender, RoutedEventArgs e) => Try(() => { ApplySettings(); Notice("Настройки сохранены."); });
    private void ApplySettings()
    {
        var directory = Path.GetFullPath(FolderBox.Text.Trim()); Directory.CreateDirectory(directory);
        var cookies = CookiesBox.Text.Trim(); if (cookies.Length > 0 && !File.Exists(cookies)) throw new FileNotFoundException("Выбранный файл cookies не найден.");
        var id = ClientIdBox.Text.Trim(); if (id.Length > 0 && (id.Length != 32 || !id.All(Uri.IsHexDigit))) throw new ArgumentException("Client ID Spotify должен содержать 32 шестнадцатеричных символа.");
        if (!testing) WindowsServices.SetStartup(StartupCheck.IsChecked == true);
        settings.DownloadDirectory = directory; settings.CloseToTray = TrayCheck.IsChecked == true; settings.AutoStart = StartupCheck.IsChecked == true;
        settings.ConcurrentDownloads = ConcurrencyBox.SelectedIndex + 1; settings.CookiesFile = cookies; settings.SpotifyClientId = id;
        spotify?.SetClientId(id); if (queue is not null) queue.Concurrency = settings.ConcurrentDownloads;
        Storage.Write(Path.Combine(dataRoot, "settings.json"), settings); UpdateSpotifyStatus();
    }
    private void UpdateSpotifyStatus()
    {
        var connected = spotify?.Connected == true;
        SpotifyStatus.Text = connected ? "Аккаунт подключён · можно искать текущий трек" : "Для первого входа укажите Client ID в настройках";
        ConnectButton.Content = connected ? "Переподключить" : "Подключить Spotify";
    }
    private async void ConnectSpotifyClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (spotify is null) return;
        ApplySettings(); StatusText.Text = "Ожидание входа в Spotify через браузер…";
        await spotify.ConnectAsync(WindowsServices.OpenUrl, lifetime.Token); UpdateSpotifyStatus(); Notice("Spotify подключён. Включите песню и нажмите «Найти текущий трек».");
    });
    private void DisconnectSpotifyClick(object sender, RoutedEventArgs e) => Try(() => { spotify?.Disconnect(); track = null; CandidatesList.ItemsSource = null; DownloadTrackButton.IsEnabled = false; UpdateSpotifyStatus(); Notice("Токены Spotify удалены с компьютера."); });
    private void SpotifyDashboardClick(object sender, RoutedEventArgs e) => Try(() => WindowsServices.OpenUrl("https://developer.spotify.com/dashboard"));
    private async void FindTrackClick(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (spotify is null || media is null) return;
        DownloadTrackButton.IsEnabled = false; CandidatesList.ItemsSource = null;
        StatusText.Text = "Чтение текущего трека Spotify…";
        track = await spotify.CurrentTrackAsync(lifetime.Token);
        if (track is null) { TrackTitle.Text = "Сейчас ничего не играет"; TrackArtist.Text = "Включите песню в Spotify и повторите поиск"; return; }
        TrackTitle.Text = track.Title; TrackArtist.Text = track.Artist + $"  ·  {TimeSpan.FromMilliseconds(track.DurationMs):m\\:ss}";
        StatusText.Text = "Поиск подходящей записи на YouTube…";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var candidates = await media.SearchTrackAsync(track, deadline.Token);
        CandidatesList.ItemsSource = candidates; CandidatesList.SelectedIndex = candidates.Count > 0 ? 0 : -1; DownloadTrackButton.IsEnabled = candidates.Count > 0;
        if (candidates.Count == 0) Notice("Подходящих записей не найдено. Можно добавить внешнюю ссылку вручную во вкладке загрузок.");
        else if (candidates[0].Score < .65) Notice("Точного совпадения нет. Проверьте название, исполнителя и длительность перед скачиванием.");
    });
    private void DownloadTrackClick(object sender, RoutedEventArgs e)
    {
        if (CandidatesList.SelectedItem is not SearchCandidate candidate || queue is null) return;
        queue.Add(new(candidate.Url, candidate.Title, "YouTube · найдено по Spotify", MediaKind.Video, candidate.Duration), new(DownloadMode.Mp3), settings.DownloadDirectory);
        DownloadsNav.IsChecked = true; RefreshStats(); Notice("Выбранная внешняя запись добавлена в очередь MP3.");
    }
    private void Try(Action action) { try { action(); } catch (Exception ex) { Notice(ex.Message); } }
    private void DragTitle(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button) return;
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (forceClose) return;
        e.Cancel = true;
        if (settings.CloseToTray) Hide(); else await ((App)Application.Current).QuitAsync();
    }
    public async Task StopAsync()
    {
        if (stopped) return; stopped = true; lifetime.Cancel(); stats.Stop();
        if (queue is not null) await queue.StopAsync();
        spotify?.Dispose(); http.Dispose(); lifetime.Dispose(); forceClose = true; Close();
    }
    public async Task RunUiSmokeAsync(bool trayOnly, string report, string? screenshot)
    {
        if (queue is null || media is null || spotify is null || tools is null) throw new InvalidOperationException("UI services were not initialized");
        if (trayOnly)
        {
            if (IsVisible) throw new InvalidOperationException("--tray opened the main window");
            Storage.Write(report, new { Success = true, WindowVisible = IsVisible, TrayStartup = true }); return;
        }
        if (!IsVisible) throw new InvalidOperationException("Normal startup did not show the main window");
        queue.Paused = true;
        var a = queue.Add(new("https://example.com/video", "Океан с высоты · короткий фильм", "YouTube", MediaKind.Video), new(DownloadMode.Video), settings.DownloadDirectory);
        a.State = JobState.Downloading; a.Percent = 67; a.Detail = "128 МБ / 191 МБ  ·  8,4 МБ/с  ·  0:08";
        var b = queue.Add(new("https://example.com/audio", "Ночная прогулка — инструментальная запись", "Внешний источник", MediaKind.Video), new(DownloadMode.Mp3), settings.DownloadDirectory);
        b.State = JobState.Completed; b.Percent = 100; b.Detail = "Сохранено · Ночная прогулка.mp3";
        RefreshStats();
        // Exercise all navigation pages, templates and format selectors on Windows.
        foreach (var page in new FrameworkElement[] { SpotifyPage, SettingsPage, DownloadsPage })
        {
            foreach (var other in new FrameworkElement[] { SpotifyPage, SettingsPage, DownloadsPage }) other.Visibility = other == page ? Visibility.Visible : Visibility.Collapsed;
            UpdateLayout();
        }
        selection = new("https://example.com/video", "Проверка выбора качества", "Fixture", MediaKind.Video, Heights: [1080, 720, 360]);
        ModeBox.SelectedIndex = 0; PopulateQuality(); if (QualityBox.Items.Count < 3) throw new InvalidOperationException("Quality selector missing");
        ModeBox.SelectedIndex = 1; if (QualityBox.Items.Count != 4) throw new InvalidOperationException("MP3 options missing");
        MediaCard.Visibility = Visibility.Collapsed;
        await Task.Delay(650); UpdateLayout();
        if (screenshot is not null)
        {
            var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!); using var stream = File.Create(screenshot); encoder.Save(stream);
        }
        // Closing to tray must keep the process and queue alive and permit reopening.
        Close(); if (IsVisible) throw new InvalidOperationException("Close did not hide window");
        Show(); if (!IsVisible) throw new InvalidOperationException("Window did not restore from tray");
        Storage.Write(report, new { Success = true, NormalStartup = true, Navigation = true, Mp3Options = true, QualityOptions = true, CloseAndRestore = true, QueueRows = queue.Jobs.Count });
    }
}
