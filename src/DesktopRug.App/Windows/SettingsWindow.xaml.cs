using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using DesktopRug.App.Rendering;
using DesktopRug.App.Services;
using DesktopRug.Core;
using Microsoft.Win32;
using SkiaSharp;

namespace DesktopRug.App.Windows;

public partial class SettingsWindow : Window
{
    private readonly RugController _controller;
    private SKBitmap? _preview;
    private string? _customPath;
    private bool _ready, _saving, _updatingLanguage;
    private AppLanguage _language;
    private UiTranslation? _saveError;
    private string? _previewErrorKey;
    private sealed record Choice<T>(T Value, string Label);
    private static readonly (string Name, string Key)[] TextResources =
    [
        ("Title", "Desktop Rug · Settings"), ("Brand", "DESKTOP RUG"), ("Heading", "A softer desktop."),
        ("Description", "A little woven calm. Your files stay exactly where they are."),
        ("Language", "Language"), ("LanguageAccessible", "Interface language"), ("LanguageHelp", "Language changes after saving."),
        ("Pattern", "Weave & pattern"), ("PatternAccessible", "Weave and pattern"), ("ChooseImage", "Choose a local image…"),
        ("Size", "Rug size"), ("Simulation", "Simulation"), ("QualityAccessible", "Simulation quality"),
        ("Scale", "Size adjustment"), ("ScaleAccessible", "Rug size adjustment"), ("Rotation", "Rotation"),
        ("RotationAccessible", "Rotation in degrees"), ("Shadow", "Soft shadow"), ("Detail", "Weave detail"), ("Lighting", "Surface lighting"),
        ("Display", "Display"), ("Bumps", "Let real desktop icons gently lift the fabric"), ("Sound", "Quiet fabric sounds"),
        ("Startup", "Launch at Windows sign-in"), ("PeekHeading", "Peek at your desktop icons"), ("Activation", "Activation"),
        ("Duration", "Duration"), ("PeekModeAccessible", "Peek activation mode"), ("PeekDurationAccessible", "Peek duration"),
        ("Shortcut", "Keyboard shortcut"), ("CtrlAccessible", "Include Ctrl in Peek shortcut"), ("AltAccessible", "Include Alt in Peek shortcut"),
        ("ShiftAccessible", "Include Shift in Peek shortcut"), ("PeekKeyAccessible", "Peek shortcut key"),
        ("RightClick", "Right-click the rug to Peek"),
        ("ShortcutHelp", "Use Ctrl or Alt, with an optional Shift. Emergency hide and show / hide shortcuts stay fixed."),
        ("HelpHeading", "Make yourself at home"),
        ("Gestures", "Drag the middle to move across displays. Pull a corner to lift and fold. Scroll to rotate. Double-click to roll or unroll."),
        ("DesktopHelp", "The rug stays on the desktop beneath your application windows. It sleeps when the fabric settles."),
        ("Hide", "Hide rug"), ("SavingAccessible", "Saving settings"), ("Cancel", "Cancel"), ("Save", "Save changes")
    ];

    internal SettingsWindow(RugController controller)
    {
        _controller = controller; InitializeComponent();
        Preview.Paint += PaintPreview;
        MaxHeight = SystemParameters.WorkArea.Height - 24;
        var settings = controller.Settings; _customPath = settings.CustomTexturePath; _language = settings.Language;
        LanguageChoice.ItemsSource = new[] { new Choice<AppLanguage>(AppLanguage.English, "English"), new Choice<AppLanguage>(AppLanguage.Persian, "فارسی") };
        RugScale.Value = settings.Scale;
        Rotation.Value = settings.Rotation > 180 ? settings.Rotation - 360 : settings.Rotation;
        Shadow.Value = settings.Shadow; Lighting.Value = settings.Lighting; Detail.Value = settings.TextureDetail;
        Bumps.IsChecked = settings.IconBumps; Sound.IsChecked = settings.Sound; Startup.IsChecked = settings.LaunchAtLogin;
        Monitor.ItemsSource = controller.Displays;
        Monitor.SelectedItem = controller.Displays.FirstOrDefault(d => d.DeviceName == settings.Monitor)
            ?? controller.Displays.FirstOrDefault(d => d.IsPrimary) ?? controller.Displays[0];
        PeekControl.IsChecked = settings.PeekShortcut.Modifiers.HasFlag(PeekModifiers.Control);
        PeekAlt.IsChecked = settings.PeekShortcut.Modifiers.HasFlag(PeekModifiers.Alt);
        PeekShift.IsChecked = settings.PeekShortcut.Modifiers.HasFlag(PeekModifiers.Shift);
        PeekKey.ItemsSource = PeekShortcut.AvailableKeys; PeekKey.Text = settings.PeekShortcut.KeyName;
        PeekRightClick.IsChecked = settings.PeekOnRightClick;
        PreviewLanguage(_language);
        _ready = true; RefreshPreview(); UpdateStatus();
        _controller.Changed += UpdateStatus;
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
        Closed += (_, _) => { _controller.Changed -= UpdateStatus; Preview.Paint -= PaintPreview; _preview?.Dispose(); _preview = null; };
    }

    private string T(string key, params object[] args) => UiText.Get(_language, key, args);
    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_updatingLanguage && LanguageChoice.SelectedValue is AppLanguage language) PreviewLanguage(language);
    }

    /// <summary>Local preview only: labels/direction change while draft values and the running app stay intact.</summary>
    internal void PreviewLanguage(AppLanguage language)
    {
        if (_saving) return;
        _updatingLanguage = true;
        try
        {
            var settings = _controller.Settings;
            var pattern = Pattern.SelectedValue is RugPattern p ? p : settings.Pattern;
            var size = SizeChoice.SelectedValue is RugSize s ? s : settings.Size;
            var quality = Quality.SelectedValue is SimulationQuality q ? q : settings.Quality;
            var mode = PeekMode.SelectedValue is PeekActivationMode m ? m : settings.PeekMode;
            int seconds = PeekDuration.SelectedValue is int d ? d : settings.PeekSeconds;
            _language = Enum.IsDefined(language) ? language : AppLanguage.English;
            LanguageChoice.SelectedValue = _language;
            foreach (var item in TextResources) Resources["T." + item.Name] = T(item.Key);
            FlowDirection = UiText.IsRtl(_language) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            Language = XmlLanguage.GetLanguage(UiText.Culture(_language).Name);
            ImageName.TextAlignment = UiText.IsRtl(_language) ? TextAlignment.Right : TextAlignment.Left;
            Pattern.ItemsSource = Enum.GetValues<RugPattern>().Select(value => new Choice<RugPattern>(value, UiText.Pattern(_language, value))).ToArray();
            Pattern.SelectedValue = pattern;
            SizeChoice.ItemsSource = Enum.GetValues<RugSize>().Select(value => new Choice<RugSize>(value, UiText.Size(_language, value))).ToArray();
            SizeChoice.SelectedValue = size;
            Quality.ItemsSource = Enum.GetValues<SimulationQuality>().Select(value => new Choice<SimulationQuality>(value, UiText.Quality(_language, value))).ToArray();
            Quality.SelectedValue = quality;
            PeekMode.ItemsSource = Enum.GetValues<PeekActivationMode>().Select(value => new { Mode = value, Label = UiText.PeekMode(_language, value) }).ToArray();
            PeekMode.SelectedValue = mode;
            PeekDuration.ItemsSource = Enumerable.Range(3, 28).Select(value => new { Seconds = value, Label = T("{0} sec", value) }).ToArray();
            PeekDuration.SelectedValue = seconds;
            UpdateImageName();
            SaveError.Text = _saveError is null ? "" : UiText.IsRtl(_language) ? _saveError.Persian : _saveError.English;
            UpdatePeekHelp(); UpdateStatus();
        }
        finally { _updatingLanguage = false; }
    }

    private void UpdateStatus()
    {
        Diagnostics.Text = _previewErrorKey is { } key ? T(key) : _controller.GetDiagnostic(_language);
        Shortcuts.Text = _controller.GetHotkeyDiagnostic(_language);
    }
    private void PeekModeChanged(object sender, SelectionChangedEventArgs e) { if (_ready && !_updatingLanguage) UpdatePeekHelp(); }
    private void UpdatePeekHelp()
    {
        var mode = PeekMode.SelectedValue is PeekActivationMode selected ? selected : PeekActivationMode.Timed;
        PeekDuration.IsEnabled = mode != PeekActivationMode.Toggle;
        PeekHelp.Text = T(mode switch
        {
            PeekActivationMode.Toggle => "Peek stays active until you press the shortcut again or restore the rug from its tray menu.",
            PeekActivationMode.Hold => "Hold the keyboard shortcut to Peek; release any key to restore. Tray and right-click Peek use the duration above.",
            _ => "The rug returns after the selected duration. Press the shortcut again to restore it sooner."
        });
    }
    private void PatternChanged(object sender, SelectionChangedEventArgs e) { if (_ready && !_updatingLanguage) RefreshPreview(); }
    private void RefreshPreview()
    {
        UpdateImageName();
        try
        {
            var bitmap = PatternFactory.Create(_controller.Settings with { Pattern = (RugPattern)Pattern.SelectedValue, CustomTexturePath = _customPath });
            _preview?.Dispose(); _preview = bitmap;
            _previewErrorKey = null;
            Preview.InvalidateVisual();
            UpdateStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _preview?.Dispose(); _preview = null; Preview.InvalidateVisual();
            _previewErrorKey = "Choose a readable image to use the custom pattern."; UpdateStatus();
        }
    }
    private void UpdateImageName()
    {
        bool custom = Pattern.SelectedValue is RugPattern.Custom;
        ImageName.Text = custom
            ? _customPath is null ? T("Choose a local image…") : Path.GetFileName(_customPath)
            : T("Three woven materials from the reference images");
    }
    private void PaintPreview(SKCanvas canvas, int canvasWidth, int canvasHeight)
    {
        if (_preview is null || canvasWidth <= 0 || canvasHeight <= 0 || _preview.Width <= 0 || _preview.Height <= 0) return;
        const float rotation = -4;
        float angle = MathF.Abs(rotation) * MathF.PI / 180;
        float cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        // Contain the rotated bitmap on both axes, including portrait materials and wide custom images.
        float rotatedWidth = _preview.Width * cos + _preview.Height * sin;
        float rotatedHeight = _preview.Width * sin + _preview.Height * cos;
        float scale = Math.Min(canvasWidth * .88f / rotatedWidth, canvasHeight * .84f / rotatedHeight);
        float width = _preview.Width * scale, height = _preview.Height * scale;
        canvas.Save(); canvas.Translate(canvasWidth / 2f, canvasHeight / 2f); canvas.RotateDegrees(rotation);
        canvas.DrawBitmap(_preview, new SKRect(-width / 2, -height / 2, width / 2, height / 2), new SKSamplingOptions(SKFilterMode.Linear));
        canvas.Restore();
    }
    private void ChooseImage(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = T("Choose a rug image"), Filter = T("Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp"), CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        _customPath = picker.FileName; Pattern.SelectedValue = RugPattern.Custom; RefreshPreview();
    }
    private void SetError(string key)
    {
        _saveError = new(UiText.Get(AppLanguage.English, key), UiText.Get(AppLanguage.Persian, key));
        SaveError.Text = T(key);
    }
    private async void Apply(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        _saveError = null; SaveError.Text = "";
        if (!PeekShortcut.TryParseKey(PeekKey.Text, out int key))
        { SetError("Choose a letter, number, or function key other than F12."); PeekKey.Focus(); return; }
        var shortcut = new PeekShortcut
        {
            Key = key,
            Modifiers = (PeekControl.IsChecked == true ? PeekModifiers.Control : PeekModifiers.None)
                | (PeekAlt.IsChecked == true ? PeekModifiers.Alt : PeekModifiers.None)
                | (PeekShift.IsChecked == true ? PeekModifiers.Shift : PeekModifiers.None)
        };
        if (!shortcut.TryValidate(out string? error)) { SetError(error!); PeekKey.Focus(); return; }
        var settings = _controller.Settings with
        {
            Language = _language,
            Pattern = (RugPattern)Pattern.SelectedValue,
            Size = (RugSize)SizeChoice.SelectedValue,
            Scale = (float)RugScale.Value,
            Quality = (SimulationQuality)Quality.SelectedValue,
            Rotation = (float)Rotation.Value,
            Shadow = (float)Shadow.Value,
            Lighting = (float)Lighting.Value,
            TextureDetail = (float)Detail.Value,
            IconBumps = Bumps.IsChecked == true,
            Sound = Sound.IsChecked == true,
            LaunchAtLogin = Startup.IsChecked == true,
            Monitor = ((DisplayInfo)Monitor.SelectedItem).DeviceName,
            CustomTexturePath = _customPath,
            PeekSeconds = PeekDuration.SelectedValue is int seconds ? seconds : 8,
            PeekMode = PeekMode.SelectedValue is PeekActivationMode mode ? mode : PeekActivationMode.Timed,
            PeekShortcut = shortcut,
            PeekOnRightClick = PeekRightClick.IsChecked == true
        };
        _saving = true; SettingsPanel.IsEnabled = false; ApplyButton.Content = T("Saving…"); SaveProgress.Visibility = Visibility.Visible;
        try
        {
            if (await _controller.ApplySettingsAsync(settings)) { _saving = false; Close(); }
            else
            {
                _saveError = new(_controller.GetDiagnostic(AppLanguage.English), _controller.GetDiagnostic(AppLanguage.Persian));
                SaveError.Text = UiText.IsRtl(_language) ? _saveError.Persian : _saveError.English;
            }
        }
        finally
        {
            _saving = false; SettingsPanel.IsEnabled = true;
            ApplyButton.SetResourceReference(ContentProperty, "T.Save"); SaveProgress.Visibility = Visibility.Collapsed;
        }
    }
    private void HideRug(object sender, RoutedEventArgs e) => _controller.HideRug();
    private void Cancel(object sender, RoutedEventArgs e) => Close();
}
