using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopRug.Core;
using Xunit;

namespace DesktopRug.Tests;

public sealed class RugMaterialTests
{
    [Theory]
    [InlineData(RugPattern.Kashan, "Rich red Persian", "ایرانی قرمز سیر", .8f)]
    [InlineData(RugPattern.Tabriz, "Light ivory Persian", "ایرانی عاجی روشن", .7f)]
    [InlineData(RugPattern.Modern, "Neutral tonal", "خنثی با طیف ملایم", 1.35f)]
    public void ReferenceMaterialsKeepTheirAssignedLabelsAndProportions(RugPattern pattern, string english, string persian, float widthToHeight)
    {
        Assert.Equal(english, UiText.Pattern(AppLanguage.English, pattern));
        Assert.Equal(persian, UiText.Pattern(AppLanguage.Persian, pattern));
        Assert.Equal(widthToHeight, new RugSettings { Pattern = pattern }.AspectRatio);
        Assert.Equal(widthToHeight, new RugSettings { Pattern = pattern, Scale = 2, Size = RugSize.ExtraLarge }.AspectRatio);
    }

    [Theory]
    [InlineData("Kashan", RugPattern.Kashan, "Rich red Persian")]
    [InlineData("Tabriz", RugPattern.Tabriz, "Light ivory Persian")]
    [InlineData("Modern", RugPattern.Modern, "Neutral tonal")]
    public void LegacySettingsNamesSelectNewMaterialsWithoutMigration(string storedName, RugPattern expected, string label)
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var settings = JsonSerializer.Deserialize<RugSettings>($$"""{"Pattern":"{{storedName}}"}""", options)!.Validated();
        Assert.Equal(expected, settings.Pattern);
        Assert.Equal(label, UiText.Pattern(AppLanguage.English, settings.Pattern));
        string saved = JsonSerializer.Serialize(settings, options);
        Assert.Contains($"\"Pattern\":\"{storedName}\"", saved);
        Assert.DoesNotContain("AspectRatio", saved);
    }

    [Fact]
    public void CustomImagesRetainTheExistingMaterialAspectContract()
    {
        Assert.Equal(10f / 7f, new RugSettings { Pattern = RugPattern.Custom }.AspectRatio);
        Assert.Equal("Custom", UiText.Pattern(AppLanguage.English, RugPattern.Custom));
        Assert.Equal("دلخواه", UiText.Pattern(AppLanguage.Persian, RugPattern.Custom));
    }

    [Fact]
    public void MaterialHelpAndFallbackUseCurrentNamesInBothLanguages()
    {
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            string help = UiText.Get(language, "Three woven materials from the reference images");
            string fallback = UiText.Get(language, "Custom image unavailable. The original image is unchanged; using Rich red Persian.");
            Assert.DoesNotContain("procedural", help, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Kashan", fallback, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(UiText.Pattern(language, RugPattern.Kashan), fallback);
        }
    }
}
