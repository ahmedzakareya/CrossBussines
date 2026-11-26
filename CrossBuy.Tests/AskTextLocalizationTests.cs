using System.Globalization;
using CrossBuy.BL.Platform.Ai;
using Xunit;

public sealed class AskTextLocalizationTests
{
    [Theory]
    [InlineData("ar", "مرحبا")]
    [InlineData("en", "Hello")]
    [InlineData("fr", "Bonjour")]
    public void Ask_text_selects_the_requested_registered_language(string cultureName, string expected)
    {
        var text = AskText.Of("مرحبا", "Hello", "Bonjour");

        Assert.Equal(expected, text.ForCulture(CultureInfo.GetCultureInfo(cultureName)));
    }

    [Fact]
    public void Missing_translation_falls_back_to_English_instead_of_returning_blank()
    {
        var text = AskText.Of("مرحبا", "Hello");

        Assert.Equal("Hello", text.ForCulture(CultureInfo.GetCultureInfo("fr")));
    }
}
