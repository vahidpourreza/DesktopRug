using Microsoft.Win32;

namespace DesktopRug.App.Services;

internal static class StartupService
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue("DesktopRug") is string command && Environment.ProcessPath is { } path
            && string.Equals(command.Trim(), CommandFor(path), StringComparison.OrdinalIgnoreCase);
    }
    internal static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled)
        {
            string path = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the application executable.");
            key.SetValue("DesktopRug", CommandFor(path));
        }
        else key.DeleteValue("DesktopRug", false);
    }
    internal static string CommandFor(string path) => $"\"{path}\" --background";
}
