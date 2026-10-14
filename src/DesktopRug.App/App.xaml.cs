using System.Windows;
using DesktopRug.App.Services;
using DesktopRug.Core;

namespace DesktopRug.App;

public partial class App : Application
{
    private Mutex? _instance;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _signalRegistration;
    private RugController? _controller;
    private AppLog? _log;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopRug");
        _log = new(directory);
        DispatcherUnhandledException += (_, args) =>
        {
            _log.Write("error", "unhandled-ui", args.Exception.GetType().Name);
            _controller?.HideRug(); args.Handled = true; Shutdown(1);
        };
        if (e.Args.Contains("--diagnostics"))
        {
            int index = Array.IndexOf(e.Args, "--diagnostics");
            string output = e.Args.Length > index + 1 ? Path.GetFullPath(e.Args[index + 1]) : Path.Combine(directory, "diagnostics");
            int code = await Diagnostics.RunAsync(output); Shutdown(code); return;
        }
        _instance = new(true, @"Local\DesktopRug.Instance", out bool first);
        if (!first)
        {
            try { using var signal = EventWaitHandle.OpenExisting(@"Local\DesktopRug.Show"); signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            _instance.Dispose(); _instance = null; Shutdown(); return;
        }
        try
        {
            var store = new SettingsService(directory); var settings = await store.LoadAsync();
            if (store.Diagnostic is { } diagnostic) _log.Write("warning", "settings-recovered", diagnostic);
            _controller = new(settings, store, _log);
            _showSignal = new(false, EventResetMode.AutoReset, @"Local\DesktopRug.Show");
            _signalRegistration = ThreadPool.RegisterWaitForSingleObject(_showSignal,
                (_, _) => Dispatcher.BeginInvoke(() => _controller?.ShowRug()), null, Timeout.Infinite, false);
        }
        catch (Exception exception)
        {
            _log.Write("error", "startup-failed", exception.GetType().Name + ": " + exception.Message);
            MessageBox.Show("Desktop Rug could not start. Details are in LocalAppData\\DesktopRug\\desktop-rug.log. Your desktop has not been changed.", "Desktop Rug", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _signalRegistration?.Unregister(null); _showSignal?.Dispose(); _controller?.Dispose();
        if (_instance is not null) { _instance.ReleaseMutex(); _instance.Dispose(); }
        base.OnExit(e);
    }
}
