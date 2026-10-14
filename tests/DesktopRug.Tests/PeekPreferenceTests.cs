using DesktopRug.Core;
using Xunit;

namespace DesktopRug.Tests;

public sealed class PeekPreferenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("p", 0x50)]
    [InlineData(" 7 ", 0x37)]
    [InlineData("F1", 0x70)]
    [InlineData("f24", 0x87)]
    public void SupportedShortcutKeysParseWithoutUiDependencies(string text, int expected)
    { Assert.True(PeekShortcut.TryParseKey(text, out int key)); Assert.Equal(expected, key); }

    [Theory]
    [InlineData("F12")]
    [InlineData("F25")]
    [InlineData("Escape")]
    [InlineData("")]
    [InlineData("Ctrl+P")]
    public void UnsupportedKeysAreRejected(string text) => Assert.False(PeekShortcut.TryParseKey(text, out _));

    [Theory]
    [InlineData(PeekModifiers.None, 0x50)]
    [InlineData(PeekModifiers.Shift, 0x50)]
    [InlineData((PeekModifiers)8, 0x50)]
    [InlineData(PeekModifiers.Control | PeekModifiers.Alt, 0x1B)]
    [InlineData(PeekModifiers.Control | PeekModifiers.Alt, 0x52)]
    [InlineData(PeekModifiers.Control, 0x7B)]
    public void InvalidOrReservedShortcutsHaveActionableErrors(PeekModifiers modifiers, int key)
    {
        Assert.False((new PeekShortcut { Modifiers = modifiers, Key = key }).TryValidate(out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void DisplayedShortcutMatchesConfiguredChord()
    {
        Assert.Equal("Ctrl+Alt+P", new PeekShortcut().ToString());
        Assert.Equal("Alt+Shift+F5", (new PeekShortcut { Modifiers = PeekModifiers.Alt | PeekModifiers.Shift, Key = 0x74 }).ToString());
        Assert.DoesNotContain("F12", PeekShortcut.AvailableKeys);
    }

    [Fact]
    public void TimedPeekExpiresAndCanBeRestoredEarly()
    {
        var peek = new PeekState();
        peek.Activate(PeekActivationMode.Timed, TimeSpan.FromSeconds(8), Now, true);
        Assert.True(peek.IsActive(Now.AddSeconds(7.99)));
        Assert.False(peek.IsActive(Now.AddSeconds(8)));
        peek.Activate(PeekActivationMode.Timed, TimeSpan.FromSeconds(8), Now.AddSeconds(9), true);
        peek.Activate(PeekActivationMode.Timed, TimeSpan.FromSeconds(8), Now.AddSeconds(10), true);
        Assert.False(peek.IsActive(Now.AddSeconds(10)));
    }

    [Fact]
    public void TogglePeekPersistsUntilExplicitlyRestored()
    {
        var peek = new PeekState();
        peek.Activate(PeekActivationMode.Toggle, TimeSpan.FromSeconds(8), Now, true);
        Assert.True(peek.IsActive(Now.AddDays(1)));
        peek.ReleaseKeyboard();
        Assert.True(peek.IsActive(Now.AddDays(1)));
        peek.Activate(PeekActivationMode.Toggle, TimeSpan.FromSeconds(8), Now.AddDays(1));
        Assert.False(peek.IsActive(Now.AddDays(1)));
    }

    [Fact]
    public void HoldPeekRestoresOnKeyReleaseWithoutTimerDelay()
    {
        var peek = new PeekState();
        peek.Activate(PeekActivationMode.Hold, TimeSpan.FromSeconds(8), Now, true);
        peek.Activate(PeekActivationMode.Hold, TimeSpan.FromSeconds(8), Now.AddSeconds(1), true);
        Assert.True(peek.KeyboardHeld);
        Assert.True(peek.IsActive(Now.AddMinutes(1)));
        peek.ReleaseKeyboard();
        Assert.False(peek.KeyboardHeld);
        Assert.False(peek.IsActive(Now.AddMinutes(1)));
    }

    [Fact]
    public void HoldPreferenceUsesTimedPeekForTrayAndRightClick()
    {
        var peek = new PeekState();
        peek.Activate(PeekActivationMode.Hold, TimeSpan.FromSeconds(5), Now);
        Assert.False(peek.KeyboardHeld);
        Assert.True(peek.IsActive(Now.AddSeconds(4)));
        Assert.False(peek.IsActive(Now.AddSeconds(5)));
    }

    [Fact]
    public void EmergencyResetCancelsEveryPeekMode()
    {
        foreach (var mode in Enum.GetValues<PeekActivationMode>())
        {
            var peek = new PeekState();
            peek.Activate(mode, TimeSpan.FromSeconds(8), Now, true);
            peek.Reset();
            Assert.False(peek.IsActive(Now));
            Assert.False(peek.KeyboardHeld);
        }
    }

    [Fact]
    public void ShortcutConflictPreservesPreviousRegistration()
    {
        var registrar = new FakeRegistrar();
        using var binding = new PeekHotkeyBinding(registrar);
        Assert.True(binding.TrySetShortcut(new(), out _));
        int originalId = binding.Id;
        registrar.RejectedKey = 0x51;
        Assert.False(binding.TrySetShortcut(new() { Key = 0x51 }, out string? error));
        Assert.NotNull(error);
        Assert.Equal(new PeekShortcut(), binding.Shortcut);
        Assert.Equal(originalId, binding.Id);
        Assert.Equal(new PeekShortcut(), registrar.Active[originalId]);
        Assert.Single(registrar.Active);
        Assert.DoesNotContain("unregister:3", registrar.Calls);
    }

    [Fact]
    public void RebindingReservesNewShortcutBeforeReleasingOldOne()
    {
        var registrar = new FakeRegistrar();
        using var binding = new PeekHotkeyBinding(registrar);
        Assert.True(binding.TrySetShortcut(new(), out _));
        Assert.True(binding.TrySetShortcut(new() { Key = 0x51 }, out _));
        Assert.Equal(new[] { "register:3", "register:4", "unregister:3" }, registrar.Calls);
        Assert.Single(registrar.Active);
        Assert.Equal(4, binding.Id);
        int calls = registrar.Calls.Count;
        Assert.True(binding.TrySetShortcut(new() { Key = 0x51 }, out _));
        Assert.Equal(calls, registrar.Calls.Count);
    }

    [Fact]
    public void FailedOldShortcutReleaseRollsBackCandidate()
    {
        var registrar = new FakeRegistrar();
        using var binding = new PeekHotkeyBinding(registrar);
        Assert.True(binding.TrySetShortcut(new(), out _));
        registrar.RejectedUnregister = 3;
        Assert.False(binding.TrySetShortcut(new() { Key = 0x51 }, out _));
        Assert.Equal(3, binding.Id);
        Assert.Equal(new PeekShortcut(), binding.Shortcut);
        Assert.Single(registrar.Active);
        Assert.Equal(new[] { "register:3", "register:4", "unregister:3", "unregister:4" }, registrar.Calls);
    }

    [Fact]
    public void ReservedShortcutNeverReachesNativeRegistrationAndDisposeReleasesPeek()
    {
        var registrar = new FakeRegistrar();
        var binding = new PeekHotkeyBinding(registrar);
        Assert.False(binding.TrySetShortcut(new() { Key = 0x52 }, out _));
        Assert.Empty(registrar.Calls);
        Assert.True(binding.TrySetShortcut(new(), out _));
        binding.Dispose(); binding.Dispose();
        Assert.Empty(registrar.Active);
        Assert.Equal(new[] { "register:3", "unregister:3" }, registrar.Calls);
    }

    [Fact]
    public async Task PeekPreferencesRoundTripAndOlderSettingsKeepDefaults()
    {
        string folder = Path.Combine(Path.GetTempPath(), "DesktopRug-Peek-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsService(folder);
            var settings = new RugSettings
            {
                PeekMode = PeekActivationMode.Hold,
                PeekShortcut = new() { Modifiers = PeekModifiers.Control | PeekModifiers.Shift, Key = 0x74 },
                PeekOnRightClick = false,
                PeekSeconds = 13,
                Scale = 1.5f
            };
            await store.SaveAsync(settings);
            Assert.Equal(settings.Validated(), await store.LoadAsync());
            await File.WriteAllTextAsync(store.FilePath, "{\"Version\":1,\"PeekSeconds\":10}");
            var migrated = await store.LoadAsync();
            Assert.Equal(PeekActivationMode.Timed, migrated.PeekMode);
            Assert.Equal(new PeekShortcut(), migrated.PeekShortcut);
            Assert.True(migrated.PeekOnRightClick);
            Assert.Equal(1, migrated.Scale);
            Assert.Equal(10, migrated.PeekSeconds);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void CorruptPeekPreferencesAndScaleRecoverSafely()
    {
        var settings = (new RugSettings { PeekMode = (PeekActivationMode)999, PeekShortcut = null!, Scale = float.NaN }).Validated();
        Assert.Equal(PeekActivationMode.Timed, settings.PeekMode);
        Assert.Equal(new PeekShortcut(), settings.PeekShortcut);
        Assert.Equal(1, settings.Scale);
        Assert.Equal(new PeekShortcut(), (new RugSettings { PeekShortcut = new() { Key = 0x52 } }).Validated().PeekShortcut);
        Assert.Equal(.25f, (new RugSettings { Scale = 0 }).Validated().Scale);
        Assert.Equal(3, (new RugSettings { Scale = 99 }).Validated().Scale);
    }

    private sealed class FakeRegistrar : IPeekHotkeyRegistrar
    {
        public List<string> Calls { get; } = [];
        public Dictionary<int, PeekShortcut> Active { get; } = [];
        public int RejectedKey { get; set; }
        public int RejectedUnregister { get; set; }
        public bool TryRegister(int id, PeekShortcut shortcut, out string? error)
        {
            Calls.Add($"register:{id}");
            if (shortcut.Key == RejectedKey) { error = "Shortcut is in use."; return false; }
            Active.Add(id, shortcut); error = null; return true;
        }
        public bool Unregister(int id)
        {
            Calls.Add($"unregister:{id}");
            return id != RejectedUnregister && Active.Remove(id);
        }
    }
}
