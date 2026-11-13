using System;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using StreamDock.Core;
using Forms = System.Windows.Forms;

namespace StreamDock;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private EventWaitHandle? activate;
    private RegisteredWaitHandle? registration;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? icon;
    private bool ownsMutex;
    private bool quitting;
    public static string DataRoot { get; private set; } = "";
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, error) =>
        {
            try { File.AppendAllText(Path.Combine(DataRoot, "errors.log"), DateTimeOffset.Now + " " + error.Exception + Environment.NewLine); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (MainWindow is MainWindow main) main.Notice("Ошибка: " + error.Exception.Message);
            error.Handled = true;
        };
        try
        {
            var testing = e.Args.Contains("--self-test") || e.Args.Contains("--ui-smoke") || e.Args.Contains("--tray-smoke");
            DataRoot = testing ? Path.Combine(Path.GetTempPath(), "StreamDock-check-" + Guid.NewGuid().ToString("N")) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamDock");
            Directory.CreateDirectory(DataRoot);
            if (e.Args.Contains("--self-test"))
            {
                var report = Arg(e.Args, "--report") ?? Path.Combine(Environment.CurrentDirectory, "self-test.json");
                var result = await SelfTests.RunAsync(DataRoot, report);
                Shutdown(result ? 0 : 1); return;
            }
            if (!testing)
            {
                var sid = WindowsIdentity.GetCurrent().User!.Value;
                instanceMutex = new Mutex(true, "Local\\StreamDock-" + sid, out ownsMutex);
                activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\StreamDock-show-" + sid);
                if (!ownsMutex) { if (!e.Args.Contains("--tray")) activate.Set(); Shutdown(); return; }
            }
            var settings = Storage.Read(Path.Combine(DataRoot, "settings.json"), () => new AppSettings());
            if (!testing) settings.AutoStart = WindowsServices.HasStartup();
            var window = new MainWindow(settings, DataRoot, testing);
            MainWindow = window;
            SetupTray(window);
            if (activate is not null) registration = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => Dispatcher.BeginInvoke(() => Restore(window)), null, Timeout.Infinite, false);
            if (!e.Args.Contains("--tray") && !e.Args.Contains("--tray-smoke")) window.Show();
            await window.InitializeAsync();
            if (testing)
            {
                var report = Arg(e.Args, "--report") ?? Path.Combine(Environment.CurrentDirectory, "ui-smoke.json");
                await window.RunUiSmokeAsync(e.Args.Contains("--tray-smoke"), report, Arg(e.Args, "--screenshot"));
                await QuitAsync();
            }
        }
        catch (Exception ex)
        {
            if (e.Args.Any(a => a.EndsWith("-smoke") || a == "--self-test"))
            {
                var path = Arg(e.Args, "--report");
                if (path is not null) Storage.Write(path, new { Success = false, Error = ex.ToString() });
            }
            else MessageBox.Show("StreamDock не удалось запустить.\n\n" + ex.Message, "StreamDock", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private static string? Arg(string[] args, string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? Path.GetFullPath(args[i + 1]) : null; }
    private void SetupTray(MainWindow window)
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream("StreamDock.icon.ico")!;
        icon = new System.Drawing.Icon(stream);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть StreamDock", null, (_, _) => Dispatcher.BeginInvoke(() => Restore(window)));
        menu.Items.Add("Папка загрузок", null, (_, _) => Dispatcher.BeginInvoke(window.ShowDownloadsFolder));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.BeginInvoke(async () => await QuitAsync()));
        tray = new Forms.NotifyIcon { Icon = icon, Text = "StreamDock · загрузчик медиа", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(() => Restore(window));
        tray.BalloonTipClicked += (_, _) => Dispatcher.BeginInvoke(() => Restore(window));
    }
    private static void Restore(MainWindow window) { window.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); }
    public void Notify(string title, string message)
    {
        if (tray is null || MainWindow?.IsVisible == true) return;
        tray.BalloonTipTitle = title; tray.BalloonTipText = message[..Math.Min(message.Length, 200)]; tray.ShowBalloonTip(4000);
    }
    public async Task QuitAsync()
    {
        if (quitting) return; quitting = true;
        if (MainWindow is MainWindow window) await window.StopAsync();
        Shutdown();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (tray is not null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        icon?.Dispose(); registration?.Unregister(null); activate?.Dispose();
        if (ownsMutex) instanceMutex?.ReleaseMutex(); instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
