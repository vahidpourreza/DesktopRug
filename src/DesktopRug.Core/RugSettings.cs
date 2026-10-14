using System.Text.Json.Serialization;

namespace DesktopRug.Core;

public enum RugPattern { Kashan, Tabriz, Modern, Custom }
public enum RugSize { Small, Medium, Large, ExtraLarge }
public enum SimulationQuality { Economy, Balanced, High }

public sealed record RugSettings
{
    public int Version { get; init; } = 1;
    public double CenterX { get; init; } = .43;
    public double CenterY { get; init; } = .5;
    public RugSize Size { get; init; } = RugSize.Medium;
    public float Scale { get; init; } = 1;
    public float Rotation { get; init; } = -5;
    public RugPattern Pattern { get; init; } = RugPattern.Kashan;
    public SimulationQuality Quality { get; init; } = SimulationQuality.Balanced;
    public bool Sound { get; init; }
    public bool IconBumps { get; init; } = true;
    public bool LaunchAtLogin { get; init; }
    public string Monitor { get; init; } = "";
    public string? CustomTexturePath { get; init; }
    public float Shadow { get; init; } = .7f;
    public float Lighting { get; init; } = .7f;
    public float TextureDetail { get; init; } = .8f;
    public int PeekSeconds { get; init; } = 8;
    public PeekActivationMode PeekMode { get; init; } = PeekActivationMode.Timed;
    public PeekShortcut PeekShortcut { get; init; } = new();
    public bool PeekOnRightClick { get; init; } = true;

    [JsonIgnore] public float Width => Size switch { RugSize.Small => 420, RugSize.Large => 840, RugSize.ExtraLarge => 1040, _ => 640 };

    public RugSettings Validated() => this with
    {
        Version = 1,
        CenterX = double.IsFinite(CenterX) ? Math.Clamp(CenterX, .05, .95) : .43,
        CenterY = double.IsFinite(CenterY) ? Math.Clamp(CenterY, .05, .95) : .5,
        Rotation = float.IsFinite(Rotation) ? ((Rotation % 360) + 360) % 360 : 0,
        Size = Enum.IsDefined(Size) ? Size : RugSize.Medium,
        Scale = float.IsFinite(Scale) ? Math.Clamp(Scale, .25f, 3) : 1,
        Pattern = Enum.IsDefined(Pattern) ? Pattern : RugPattern.Kashan,
        Quality = Enum.IsDefined(Quality) ? Quality : SimulationQuality.Balanced,
        Shadow = float.IsFinite(Shadow) ? Math.Clamp(Shadow, 0, 1) : .7f,
        Lighting = float.IsFinite(Lighting) ? Math.Clamp(Lighting, 0, 1) : .7f,
        TextureDetail = float.IsFinite(TextureDetail) ? Math.Clamp(TextureDetail, 0, 1) : .8f,
        PeekSeconds = Math.Clamp(PeekSeconds, 3, 30),
        PeekMode = Enum.IsDefined(PeekMode) ? PeekMode : PeekActivationMode.Timed,
        PeekShortcut = PeekShortcut is not null && PeekShortcut.TryValidate(out _) ? PeekShortcut : new(),
        Monitor = Monitor ?? "",
        CustomTexturePath = string.IsNullOrWhiteSpace(CustomTexturePath) ? null : CustomTexturePath
    };
}
