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
    internal SettingsWindow(RugController controller)
    {
        _controller = controller; InitializeComponent();
        Preview.Paint += PaintPreview;
        MaxHeight = SystemParameters.WorkArea.Height - 24;
        var settings = controller.Settings; _customPath = settings.CustomTexturePath;
        Pattern.ItemsSource = Enum.GetValues<RugPattern>(); Pattern.SelectedItem = settings.Pattern;
        SizeChoice.ItemsSource = Enum.GetValues<RugSize>(); SizeChoice.SelectedItem = settings.Size;
        Quality.ItemsSource = Enum.GetValues<SimulationQuality>(); Quality.SelectedItem = settings.Quality;
        Rotation.Value = settings.Rotation > 180 ? settings.Rotation - 360 : settings.Rotation;
        Shadow.Value = settings.Shadow; Lighting.Value = settings.Lighting; Detail.Value = settings.TextureDetail;
        Bumps.IsChecked = settings.IconBumps; Sound.IsChecked = settings.Sound; Startup.IsChecked = settings.LaunchAtLogin;
        Monitor.ItemsSource = controller.Displays;
        Monitor.SelectedItem = controller.Displays.FirstOrDefault(d => d.DeviceName == settings.Monitor)
            ?? controller.Displays.FirstOrDefault(d => d.IsPrimary) ?? controller.Displays[0];
        PeekDuration.ItemsSource = new[] { 3, 5, 8, 10, 15, 20, 30 }; PeekDuration.SelectedItem = settings.PeekSeconds;
        _ready = true; RefreshPreview(); UpdateStatus();
        _controller.Changed += UpdateStatus;
        Closed += (_, _) => { _controller.Changed -= UpdateStatus; Preview.Paint -= PaintPreview; _preview?.Dispose(); _preview = null; };
    }
    private void UpdateStatus() { Diagnostics.Text = _controller.Diagnostic; Shortcuts.Text = _controller.HotkeyDiagnostic; }
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
    private void Apply(object sender, RoutedEventArgs e)
    {
        var settings = _controller.Settings with
        {
            Pattern = (RugPattern)Pattern.SelectedItem,
            Size = (RugSize)SizeChoice.SelectedItem,
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
            PeekSeconds = PeekDuration.SelectedItem is int seconds ? seconds : 8
        };
        ApplyButton.IsEnabled = false; ApplyButton.Content = "Saving…";
        try { if (_controller.ApplySettings(settings)) Close(); }
        finally { ApplyButton.IsEnabled = true; ApplyButton.Content = "Save changes"; }
    }
    private void HideRug(object sender, RoutedEventArgs e) => _controller.HideRug();
    private void Cancel(object sender, RoutedEventArgs e) => Close();
}
