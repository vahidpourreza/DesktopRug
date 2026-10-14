using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopRug.Core;

public sealed class SettingsService(string directory)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public string FilePath => Path.Combine(directory, "settings.json");
    public string? Diagnostic { get; private set; }

    public async Task<RugSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            await using var stream = File.OpenRead(FilePath);
            var settings = await JsonSerializer.DeserializeAsync<RugSettings>(stream, Options);
            if (settings is null) throw new JsonException("Settings were null.");
            return settings.Validated();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            Diagnostic = "Settings could not be read; safe defaults were restored.";
            // Preserve the original file for recovery; only our own settings are ever touched.
            try { File.Copy(FilePath, Path.Combine(directory, $"settings-recovery-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json"), false); }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException) { }
            return new();
        }
    }

    public async Task SaveAsync(RugSettings settings)
    {
        await _writeLock.WaitAsync();
        string temporary = Path.Combine(directory, "settings.pending.json");
        try
        {
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, settings.Validated(), Options);
                await stream.FlushAsync();
                stream.Flush(true);
            }
            File.Move(temporary, FilePath, true);
        }
        finally { _writeLock.Release(); }
    }
}
