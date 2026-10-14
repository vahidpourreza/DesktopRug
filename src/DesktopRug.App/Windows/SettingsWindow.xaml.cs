using System.Windows;
using System.Windows.Controls;
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
    private bool _ready;
    private bool _saving;
    internal SettingsWindow(RugController controller)
    {
        _controller = controller; InitializeComponent();
        Preview.Paint += PaintPreview;
        MaxHeight = SystemParameters.WorkArea.Height - 24;
        var settings = controller.Settings; _customPath = settings.CustomTexturePath;
        Pattern.ItemsSource = Enum.GetValues<RugPattern>(); Pattern.SelectedItem = settings.Pattern;
        SizeChoice.ItemsSource = Enum.GetValues<RugSize>(); SizeChoice.SelectedItem = settings.Size;
        RugScale.Value = settings.Scale;
        Quality.ItemsSource = Enum.GetValues<SimulationQuality>(); Quality.SelectedItem = settings.Quality;
        Rotation.Value = settings.Rotation > 180 ? settings.Rotation - 360 : settings.Rotation;
        Shadow.Value = settings.Shadow; Lighting.Value = settings.Lighting; Detail.Value = settings.TextureDetail;
        Bumps.IsChecked = settings.IconBumps; Sound.IsChecked = settings.Sound; Startup.IsChecked = settings.LaunchAtLogin;
        Monitor.ItemsSource = controller.Displays;
        Monitor.SelectedItem = controller.Displays.FirstOrDefault(d => d.DeviceName == settings.Monitor)
            ?? controller.Displays.FirstOrDefault(d => d.IsPrimary) ?? controller.Displays[0];
        PeekDuration.ItemsSource = Enumerable.Range(3, 28).Select(seconds => new { Seconds = seconds, Label = $"{seconds} sec" }).ToArray();
        PeekDuration.SelectedValue = settings.PeekSeconds;
        PeekMode.ItemsSource = new[]
        {
            new { Mode = PeekActivationMode.Timed, Label = "Timed" },
            new { Mode = PeekActivationMode.Toggle, Label = "Toggle on / off" },
            new { Mode = PeekActivationMode.Hold, Label = "Hold keyboard shortcut" }
        };
        PeekMode.SelectedValue = settings.PeekMode;
        PeekControl.IsChecked = settings.PeekShortcut.Modifiers.HasFlag(PeekModifiers.Control);
        PeekAlt.IsChecked = settings.PeekShortcut.Modifiers.HasFlag(PeekModifiers.Alt);
        PeekShift.IsChecked = settings.PeekShortcut.Modifiers.HasFlag(PeekModifiers.Shift);
        PeekKey.ItemsSource = PeekShortcut.AvailableKeys; PeekKey.Text = settings.PeekShortcut.KeyName;
        PeekRightClick.IsChecked = settings.PeekOnRightClick;
        _ready = true; UpdatePeekHelp(); RefreshPreview(); UpdateStatus();
        _controller.Changed += UpdateStatus;
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
        Closed += (_, _) => { _controller.Changed -= UpdateStatus; Preview.Paint -= PaintPreview; _preview?.Dispose(); _preview = null; };
    }
    private void UpdateStatus() { Diagnostics.Text = _controller.Diagnostic; Shortcuts.Text = _controller.HotkeyDiagnostic; }
    private void PeekModeChanged(object sender, SelectionChangedEventArgs e) { if (_ready) UpdatePeekHelp(); }
    private void UpdatePeekHelp()
    {
        var mode = PeekMode.SelectedValue is PeekActivationMode selected ? selected : PeekActivationMode.Timed;
        PeekDuration.IsEnabled = mode != PeekActivationMode.Toggle;
        PeekHelp.Text = mode switch
        {
            PeekActivationMode.Toggle => "Peek stays active until you press the shortcut again or restore the rug from its tray menu.",
            PeekActivationMode.Hold => "Hold the keyboard shortcut to Peek; release any key to restore. Tray and right-click Peek use the duration above.",
            _ => "The rug returns after the selected duration. Press the shortcut again to restore it sooner."
        };
    }
    private void PatternChanged(object sender, SelectionChangedEventArgs e) { if (_ready) RefreshPreview(); }
    private void RefreshPreview()
    {
        try
        {
            var bitmap = PatternFactory.Create(_controller.Settings with { Pattern = (RugPattern)Pattern.SelectedItem, CustomTexturePath = _customPath });
            _preview?.Dispose(); _preview = bitmap; ImageName.Text = _customPath is null ? "Three original procedural designs" : Path.GetFileName(_customPath);
            Preview.InvalidateVisual();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Diagnostics.Text = "Choose a readable image to use the custom pattern."; }
    }
    private void PaintPreview(SKCanvas canvas, int canvasWidth, int canvasHeight)
    {
        if (_preview is null) return;
        float height = canvasHeight * .84f, width = height * _preview.Width / _preview.Height;
        canvas.Translate(canvasWidth / 2f, canvasHeight / 2f); canvas.RotateDegrees(-4);
        canvas.DrawBitmap(_preview, new SKRect(-width / 2, -height / 2, width / 2, height / 2), new SKSamplingOptions(SKFilterMode.Linear));
    }
    private void ChooseImage(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Choose a rug image", Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        _customPath = picker.FileName; Pattern.SelectedItem = RugPattern.Custom; RefreshPreview();
    }
    private async void Apply(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        SaveError.Text = "";
        if (!PeekShortcut.TryParseKey(PeekKey.Text, out int key))
        { SaveError.Text = "Choose a letter, number, or function key other than F12."; PeekKey.Focus(); return; }
        var shortcut = new PeekShortcut
        {
            Key = key,
            Modifiers = (PeekControl.IsChecked == true ? PeekModifiers.Control : PeekModifiers.None)
                | (PeekAlt.IsChecked == true ? PeekModifiers.Alt : PeekModifiers.None)
                | (PeekShift.IsChecked == true ? PeekModifiers.Shift : PeekModifiers.None)
        };
        if (!shortcut.TryValidate(out string? error)) { SaveError.Text = error; PeekKey.Focus(); return; }
        var settings = _controller.Settings with
        {
            Pattern = (RugPattern)Pattern.SelectedItem,
            Size = (RugSize)SizeChoice.SelectedItem,
            Scale = (float)RugScale.Value,
            Quality = (SimulationQuality)Quality.SelectedItem,
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
        _saving = true; SettingsPanel.IsEnabled = false; ApplyButton.Content = "Saving…"; SaveProgress.Visibility = Visibility.Visible;
        try
        {
            if (await _controller.ApplySettingsAsync(settings)) { _saving = false; Close(); }
            else SaveError.Text = _controller.Diagnostic;
        }
        finally { _saving = false; SettingsPanel.IsEnabled = true; ApplyButton.Content = "Save changes"; SaveProgress.Visibility = Visibility.Collapsed; }
    }
    private void HideRug(object sender, RoutedEventArgs e) => _controller.HideRug();
    private void Cancel(object sender, RoutedEventArgs e) => Close();
}
