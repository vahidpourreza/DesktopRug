using System.Text.Json;

namespace DesktopRug.App.Services;

internal sealed class AppLog(string directory)
{
    private readonly object _sync = new();
    internal void Write(string level, string evt, string? detail = null)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "desktop-rug.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, Path.Combine(directory, "desktop-rug.previous.log"), true);
                File.AppendAllText(path, JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, level, evt, detail }) + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
