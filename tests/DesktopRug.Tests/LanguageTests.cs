using System.Globalization;
using System.Text.RegularExpressions;
using DesktopRug.Core;
using Xunit;

namespace DesktopRug.Tests;

public sealed class LanguageTests
{
    [Fact]
    public async Task LanguagePersistsAndOlderSettingsKeepEnglishDefault()
    {
        string directory = Path.Combine(Path.GetTempPath(), "DesktopRug-Language-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsService(directory);
            var expected = (new RugSettings { Language = AppLanguage.Persian, CenterX = .61, PeekMode = PeekActivationMode.Toggle }).Validated();
            await store.SaveAsync(expected);
            Assert.Equal(expected, await store.LoadAsync());
            string content = await File.ReadAllTextAsync(store.FilePath);
            Assert.Contains("\"Language\": \"Persian\"", content);
            await File.WriteAllTextAsync(store.FilePath, "{\"Version\":1,\"CenterX\":0.61}");
            var previousVersion = await store.LoadAsync();
            Assert.Equal(AppLanguage.English, previousVersion.Language);
            Assert.Equal(.61, previousVersion.CenterX);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void InvalidLanguageFallsBackToEnglishWithoutChangingOtherSettings()
    {
        var settings = (new RugSettings { Language = (AppLanguage)999, Sound = true, Scale = 1.5f }).Validated();
        Assert.Equal(AppLanguage.English, settings.Language);
        Assert.True(settings.Sound);
        Assert.Equal(1.5f, settings.Scale);
        Assert.Equal("Save changes", UiText.Get((AppLanguage)999, "Save changes"));
        Assert.False(UiText.IsRtl((AppLanguage)999));
    }

    [Fact]
    public void CatalogueHasCompleteLanguagePairsAndMatchingFormatArguments()
    {
        Assert.True(UiText.Catalogue.Count >= 100);
        foreach ((string key, UiTranslation pair) in UiText.Catalogue)
        {
            Assert.False(string.IsNullOrWhiteSpace(pair.English), key);
            Assert.False(string.IsNullOrWhiteSpace(pair.Persian), key);
            Assert.Equal(key, pair.English);
            Assert.Equal(pair.English, UiText.Get(AppLanguage.English, key));
            Assert.Equal(pair.Persian, UiText.Get(AppLanguage.Persian, key));
            Assert.Equal(PlaceholderIds(pair.English), PlaceholderIds(pair.Persian));
            // Format both variants so malformed braces also fail, independently of regex matching.
            object[] samples = Enumerable.Range(0, 10).Select(value => (object)value).ToArray();
            Assert.NotNull(string.Format(CultureInfo.InvariantCulture, pair.English, samples));
            Assert.NotNull(string.Format(CultureInfo.InvariantCulture, pair.Persian, samples));
        }
    }

    [Theory]
    [InlineData("Language")]
    [InlineData("Desktop Rug · Settings")]
    [InlineData("Peek activation mode")]
    [InlineData("Choose a letter, number, or function key other than F12.")]
    [InlineData("Windows could not replace the shortcut. The previous shortcut remains active.")]
    [InlineData("Settings saved.")]
    [InlineData("Explorer accessibility timed out.")]
    [InlineData("Exit Desktop Rug")]
    public void VisibleUiAndFailureMessagesHavePersianTranslations(string key)
    {
        Assert.True(UiText.Catalogue.ContainsKey(key));
        Assert.NotEqual(key, UiText.Get(AppLanguage.Persian, key));
    }

    [Fact]
    public void EveryEnumChoiceHasADistinctTranslatedLabel()
    {
        CheckChoices(Enum.GetValues<RugPattern>().Select(value => UiText.Pattern(AppLanguage.Persian, value)));
        CheckChoices(Enum.GetValues<RugSize>().Select(value => UiText.Size(AppLanguage.Persian, value)));
        CheckChoices(Enum.GetValues<SimulationQuality>().Select(value => UiText.Quality(AppLanguage.Persian, value)));
        CheckChoices(Enum.GetValues<PeekActivationMode>().Select(value => UiText.PeekMode(AppLanguage.Persian, value)));
        Assert.Equal("Extra large", UiText.Size(AppLanguage.English, RugSize.ExtraLarge));
        Assert.Equal("کاشان", UiText.Pattern(AppLanguage.Persian, RugPattern.Kashan));
    }

    [Fact]
    public void FormatsWithSelectedCultureWithoutChangingShortcutTextOrProcessCulture()
    {
        CultureInfo currentCulture = CultureInfo.CurrentCulture;
        CultureInfo currentUiCulture = CultureInfo.CurrentUICulture;
        const string shortcut = "Ctrl+Alt+F5";
        string value = UiText.Get(AppLanguage.Persian, "Peek: {0} ({1}).", shortcut,
            UiText.PeekMode(AppLanguage.Persian, PeekActivationMode.Hold));
        Assert.Contains(shortcut, value);
        Assert.Contains("با نگه‌داشتن میان‌بر", value);
        Assert.Equal(string.Format(CultureInfo.GetCultureInfo("fa-IR"), "{0:N2}", 1234.5), UiText.Get(AppLanguage.Persian, "{0:N2}", 1234.5));
        Assert.Equal(string.Format(CultureInfo.GetCultureInfo("en-US"), "{0:N2}", 1234.5), UiText.Get(AppLanguage.English, "{0:N2}", 1234.5));
        Assert.Same(currentCulture, CultureInfo.CurrentCulture);
        Assert.Same(currentUiCulture, CultureInfo.CurrentUICulture);
    }

    [Fact]
    public void UnknownKeysFallBackToEnglishAndRtlIsOnlyPersian()
    {
        Assert.Equal("A future command", UiText.Get(AppLanguage.Persian, "A future command"));
        Assert.Equal("A future command: Ctrl+P", UiText.Get(AppLanguage.Persian, "A future command: {0}", "Ctrl+P"));
        Assert.True(UiText.IsRtl(AppLanguage.Persian));
        Assert.False(UiText.IsRtl(AppLanguage.English));
    }

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Persian)]
    public void TrayTooltipFitsWindowsLimit(AppLanguage language) =>
        Assert.InRange(UiText.Get(language, "Desktop Rug · Ctrl+Alt+Esc to hide").Length, 1, 63);

    private static string[] PlaceholderIds(string value) => Regex.Matches(value, @"(?<!\{)\{(\d+)(?:[^{}]*)\}(?!\})")
        .Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();

    private static void CheckChoices(IEnumerable<string> labels)
    {
        string[] values = labels.ToArray();
        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
        Assert.All(values, value => Assert.Matches(@"[\u0600-\u06FF]", value));
    }
}
