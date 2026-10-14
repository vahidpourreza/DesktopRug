using System.Drawing;
using System.Windows.Forms;
using DesktopRug.App.Interop;
using DesktopRug.Core;
using SkiaSharp;

namespace DesktopRug.App.Services;

internal sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly Icon _drawing;
    private readonly RugController _controller;
    private AppLanguage? _language;
    private bool _languageRefreshPending, _disposed;
    internal TrayService(RugController controller, AppLog log, bool visible = true)
    {
        _controller = controller;
        _drawing = CreateIcon();
        _icon = new() { Icon = _drawing, ContextMenuStrip = _menu, Visible = visible };
        _icon.DoubleClick += (_, _) => controller.ToggleRug();
        _menu.Opening += (_, _) => Rebuild();
        _menu.Closed += (_, _) => { if (_languageRefreshPending) Rebuild(); };
        _controller.Changed += RefreshLanguage;
        RefreshLanguage();
        log.Write("information", "tray-ready");
    }
    internal void RefreshLanguage()
    {
        if (_disposed || _language == _controller.Settings.Language) return;
        _language = _controller.Settings.Language;
        string tooltip = UiText.Get(_language.Value, "Desktop Rug · Ctrl+Alt+Esc to hide");
        // NotifyIcon accepts at most 63 UTF-16 characters on older Windows implementations.
        int limit = tooltip.Length > 63 && char.IsHighSurrogate(tooltip[62]) ? 62 : 63;
        _icon.Text = tooltip.Length > limit ? tooltip[..limit] : tooltip;
        // A save must not replace items underneath a currently open native menu. Its next open
        // (or the normal close event) rebuilds labels and RTL layout without closing/reopening it.
        if (_menu.Visible) _languageRefreshPending = true;
        else Rebuild();
    }
    private void Rebuild()
    {
        if (_disposed) return;
        _languageRefreshPending = false;
        AppLanguage language = _controller.Settings.Language;
        _menu.RightToLeft = UiText.IsRtl(language) ? RightToLeft.Yes : RightToLeft.No;
        while (_menu.Items.Count > 0) { var item = _menu.Items[0]; _menu.Items.RemoveAt(0); item.Dispose(); }
        void Item(string key, Action action, params object[] args) => _menu.Items.Add(UiText.Get(language, key, args), null, (_, _) => action());
        Item("Show rug", _controller.ShowRug); Item("Hide rug    Ctrl+Alt+Esc", _controller.HideRug);
        if (_controller.PeekActive) Item("End peek", _controller.Peek);
        else Item("Peek at desktop    {0}", _controller.Peek, _controller.Settings.PeekShortcut);
        _menu.Items.Add(new ToolStripSeparator());
        Item("Flatten rug", _controller.Flatten); Item("Restore saved position", _controller.RestorePosition);
        Item(_controller.IsRolled ? "Unroll rug" : "Roll up rug", _controller.ToggleRoll);
        var sizeMenu = new ToolStripMenuItem(UiText.Get(language, "Rug size"));
        sizeMenu.DropDown.RightToLeft = _menu.RightToLeft;
        foreach (RugSize size in Enum.GetValues<RugSize>())
        {
            var option = new ToolStripMenuItem(UiText.Size(language, size)) { Checked = _controller.Settings.Size == size };
            option.Click += async (_, _) => await _controller.ApplySettingsAsync(_controller.Settings with { Size = size }); sizeMenu.DropDownItems.Add(option);
        }
        _menu.Items.Add(sizeMenu);
        var patternMenu = new ToolStripMenuItem(UiText.Get(language, "Pattern"));
        patternMenu.DropDown.RightToLeft = _menu.RightToLeft;
        foreach (RugPattern pattern in new[] { RugPattern.Kashan, RugPattern.Tabriz, RugPattern.Modern })
        {
            var option = new ToolStripMenuItem(UiText.Pattern(language, pattern)) { Checked = _controller.Settings.Pattern == pattern };
            option.Click += async (_, _) => await _controller.ApplySettingsAsync(_controller.Settings with { Pattern = pattern }); patternMenu.DropDownItems.Add(option);
        }
        _menu.Items.Add(patternMenu);
        Item("Rotate clockwise", () => _controller.Rotate(15));
        _menu.Items.Add(new ToolStripSeparator());
        void Toggle(string label, bool value, Func<RugSettings> settings)
        {
            var item = new ToolStripMenuItem(UiText.Get(language, label)) { Checked = value };
            item.Click += async (_, _) => await _controller.ApplySettingsAsync(settings()); _menu.Items.Add(item);
        }
        Toggle("Fabric sounds", _controller.Settings.Sound, () => _controller.Settings with { Sound = !_controller.Settings.Sound });
        Toggle("Icon-aware bumps", _controller.Settings.IconBumps, () => _controller.Settings with { IconBumps = !_controller.Settings.IconBumps });
        Toggle("Launch at sign-in", _controller.Settings.LaunchAtLogin, () => _controller.Settings with { LaunchAtLogin = !_controller.Settings.LaunchAtLogin });
        _menu.Items.Add(new ToolStripSeparator()); Item("Settings…", _controller.OpenSettings); Item("Exit Desktop Rug", _controller.Exit);
    }
    private static Icon CreateIcon()
    {
        using var bitmap = new SKBitmap(64, 64); using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.Transparent);
        canvas.Translate(32, 32); canvas.RotateDegrees(-12);
        using var paint = new SKPaint { IsAntialias = true, Color = new(126, 42, 52) };
        canvas.DrawRoundRect(new SKRect(-23, -17, 23, 17), 2, 2, paint);
        paint.Color = new(234, 207, 159); paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 2;
        canvas.DrawRect(-19, -13, 38, 26, paint);
        using var diamond = new SKPathBuilder(); diamond.MoveTo(0, -10); diamond.LineTo(12, 0); diamond.LineTo(0, 10); diamond.LineTo(-12, 0); diamond.Close();
        using var diamondPath = diamond.Detach(); canvas.DrawPath(diamondPath, paint);
        for (int y = -14; y <= 14; y += 4) { canvas.DrawLine(-26, y, -23, y, paint); canvas.DrawLine(23, y, 26, y, paint); }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100); using var stream = data.AsStream();
        using var drawing = new Bitmap(stream); nint handle = drawing.GetHicon();
        try { using var temporary = Icon.FromHandle(handle); return (Icon)temporary.Clone(); }
        finally { Native.DestroyIcon(handle); }
    }
    internal static void ExportIcon(string path)
    {
        using var icon = CreateIcon(); using var stream = File.Create(path); icon.Save(stream);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _controller.Changed -= RefreshLanguage;
        _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); _drawing.Dispose();
    }
}
