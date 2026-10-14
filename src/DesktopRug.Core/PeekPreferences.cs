using System.Globalization;
using System.Text.Json.Serialization;

namespace DesktopRug.Core;

public enum PeekActivationMode { Timed, Toggle, Hold }

[Flags]
public enum PeekModifiers { None = 0, Alt = 1, Control = 2, Shift = 4 }

/// <summary>A Windows virtual-key shortcut, independent of any UI framework.</summary>
public sealed record PeekShortcut
{
    public PeekModifiers Modifiers { get; init; } = PeekModifiers.Control | PeekModifiers.Alt;
    public int Key { get; init; } = 0x50;

    [JsonIgnore]
    public string KeyName => Key switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)Key).ToString(),
        >= 0x70 and <= 0x87 => "F" + (Key - 0x6F).ToString(CultureInfo.InvariantCulture),
        0x1B => "Esc",
        _ => "Unknown"
    };

    public static IReadOnlyList<string> AvailableKeys { get; } = Array.AsReadOnly(
        Enumerable.Range('A', 26).Concat(Enumerable.Range('0', 10)).Concat(Enumerable.Range(0x70, 24))
            .Where(key => key != 0x7B).Select(key => new PeekShortcut { Key = key }.KeyName).ToArray());

    public bool TryValidate(out string? error)
    {
        if ((Modifiers & ~(PeekModifiers.Control | PeekModifiers.Alt | PeekModifiers.Shift)) != 0
            || (Modifiers & (PeekModifiers.Control | PeekModifiers.Alt)) == 0)
            error = "Include Ctrl or Alt in the Peek shortcut. Shift can also be added.";
        else if (Modifiers == (PeekModifiers.Control | PeekModifiers.Alt) && Key is 0x1B or 0x52)
            error = "Ctrl+Alt+Esc and Ctrl+Alt+R are reserved for emergency hide and show / hide.";
        else if (Key == 0x7B)
            error = "F12 is reserved by Windows for the debugger. Choose another key.";
        else if (Key is not (>= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x87))
            error = "Choose a letter, number, or function key other than F12.";
        else error = null;
        return error is null;
    }

    public static bool TryParseKey(string? text, out int key)
    {
        string value = text?.Trim().ToUpperInvariant() ?? "";
        key = 0;
        if (value.Length == 1 && value[0] is >= 'A' and <= 'Z' or >= '0' and <= '9') key = value[0];
        else if (value.StartsWith('F') && int.TryParse(value.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            && number is >= 1 and <= 24 && number != 12) key = 0x6F + number;
        return key != 0;
    }

    public override string ToString() => string.Join('+', new[]
    {
        Modifiers.HasFlag(PeekModifiers.Control) ? "Ctrl" : null,
        Modifiers.HasFlag(PeekModifiers.Alt) ? "Alt" : null,
        Modifiers.HasFlag(PeekModifiers.Shift) ? "Shift" : null,
        KeyName
    }.Where(part => part is not null));
}

/// <summary>Deterministic Peek transitions. The caller supplies its clock and keyboard release events.</summary>
public sealed class PeekState
{
    private bool _toggle;
    private DateTimeOffset _until;
    public bool KeyboardHeld { get; private set; }

    public bool IsActive(DateTimeOffset now) => _toggle || KeyboardHeld || now < _until;

    public void Activate(PeekActivationMode mode, TimeSpan duration, DateTimeOffset now, bool isKeyboard = false)
    {
        // Repeated keyboard messages must not toggle a held gesture off.
        if (mode == PeekActivationMode.Hold && isKeyboard)
        {
            Reset(); KeyboardHeld = true; return;
        }
        bool active = IsActive(now);
        Reset();
        if (active) return;
        if (mode == PeekActivationMode.Toggle) _toggle = true;
        else _until = now + TimeSpan.FromSeconds(Math.Clamp(duration.TotalSeconds, 3, 30));
    }

    public void ReleaseKeyboard() => KeyboardHeld = false;
    public void Reset() { _toggle = false; KeyboardHeld = false; _until = default; }
}
