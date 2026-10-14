using System.Windows;
using DesktopRug.App.Services;
using DesktopRug.Core;

namespace DesktopRug.App;

public partial class App : Application
{
    private SingleInstanceService? _singleInstance;
    private RugController? _controller;
    private AppLog? _log;
    private AppLanguage _startupLanguage = AppLanguage.English;

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
        if (e.Args.Contains("--material-diagnostics"))
        {
            int index = Array.IndexOf(e.Args, "--material-diagnostics");
            string output = e.Args.Length > index + 1 ? Path.GetFullPath(e.Args[index + 1]) : Path.Combine(directory, "material-diagnostics");
            var report = MaterialDiagnostics.Run(output);
            Shutdown(report.TryGetValue("materialChecksPassed", out var passed) && passed is true ? 0 : 1);
            return;
        }
        if (e.Args.Contains("--interaction-diagnostics"))
        {
            int index = Array.IndexOf(e.Args, "--interaction-diagnostics");
            string output = e.Args.Length > index + 1 ? Path.GetFullPath(e.Args[index + 1]) : Path.Combine(directory, "interaction-diagnostics");
            var report = await InteractionRegressionDiagnostics.RunAsync(output);
            Shutdown(report.TryGetValue("interactionRegressionChecksPassed", out var passed) && passed is true ? 0 : 1);
            return;
        }
        if (e.Args.Contains("--language-diagnostics"))
        {
            int index = Array.IndexOf(e.Args, "--language-diagnostics");
            string output = e.Args.Length > index + 1 ? Path.GetFullPath(e.Args[index + 1]) : Path.Combine(directory, "language-diagnostics");
            var report = await LanguageDiagnostics.RunAsync(output);
            Shutdown(report.TryGetValue("languageChecksPassed", out var passed) && passed is true ? 0 : 1);
            return;
        }
        if (e.Args.Contains("--diagnostics"))
        {
            int index = Array.IndexOf(e.Args, "--diagnostics");
            string output = e.Args.Length > index + 1 ? Path.GetFullPath(e.Args[index + 1]) : Path.Combine(directory, "diagnostics");
            int code = await Diagnostics.RunAsync(output); Shutdown(code); return;
        }
        try
        {
            _singleInstance = new(action =>
            {
                if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(action);
            }, () => _controller?.ShowRug());
            if (!_singleInstance.IsPrimary)
            {
                _singleInstance.SignalExisting();
                _singleInstance.Dispose(); _singleInstance = null;
                Shutdown(); return;
            }
            var store = new SettingsService(directory); var settings = await store.LoadAsync();
            _startupLanguage = settings.Language;
            if (store.Diagnostic is { } diagnostic) _log.Write("warning", "settings-recovered", diagnostic);
            _controller = new(settings, store, _log);
        }
        catch (Exception exception)
        {
            _log.Write("error", "startup-failed", exception.GetType().Name);
            MessageBox.Show(UiText.Get(_startupLanguage, "Desktop Rug could not start. Details are in LocalAppData\\DesktopRug\\desktop-rug.log. Your desktop has not been changed."),
                UiText.Get(_startupLanguage, "Desktop Rug"), MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK,
                UiText.IsRtl(_startupLanguage) ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        // Retain ownership until the old tray and overlay are gone, so a concurrent relaunch
        // cannot create a second visible instance during teardown.
        try { _controller?.Dispose(); }
        finally { _singleInstance?.Dispose(); base.OnExit(e); }
    }
}
