using CrossBuy.BL.Communication;
using CrossBuy.Models.Communication;
using Xunit;

namespace CrossBuy.Tests.Communication;

public sealed class CommTemplateFrenchLocalizationTests
{
    [Fact]
    public void Catalog_publishes_six_localized_parts_for_each_template()
    {
        var catalog = new CommTemplateCatalog();

        Assert.Equal(catalog.All().Count * 6, catalog.RequiredResourceKeys().Count);
        Assert.All(catalog.All(), definition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.DefaultTitleFr));
            Assert.False(string.IsNullOrWhiteSpace(definition.DefaultBodyFr));
        });
    }

    [Fact]
    public void Resource_provider_renders_french_without_changing_arabic_or_english()
    {
        var catalog = new CommTemplateCatalog();
        var template = catalog.Get(CommTemplateKeys.MentionedInComment);
        var renderer = new CommTemplateRenderer(new ResourceCommTemplateTextProvider());

        var rendered = renderer.Render(template, new Dictionary<string, string>
        {
            [CommTemplateTokens.ActorName] = "Camille",
            [CommTemplateTokens.EntityLabel] = "فاتورة 42",
            [CommTemplateTokens.EntityLabelEn] = "Invoice 42",
            [CommTemplateTokens.Excerpt] = "Please review",
        }, url: null);

        Assert.Equal("Camille vous a mentionné", rendered.TitleFr);
        Assert.Contains("Invoice 42", rendered.BodyFr);
        Assert.Equal("Camille mentioned you", rendered.TitleEn);
        Assert.Equal("أشار إليك Camille", rendered.TitleAr);
    }
    // G8 — THE KEYS ARE PRESENT, AND THIS IS THE TEST THAT CAN SAY SO.
    //
    // The two tests above cannot. Every resx value is byte-identical to the built-in default it replaces,
    // and ResourceCommTemplateTextProvider falls back to that default whenever GetString returns null — so
    // "Camille vous a mentionne" renders identically whether the resx was read or silently missed. Those
    // assertions pass against a deployment with ZERO Comm.Notification keys. They are vacuous with respect
    // to the resx wiring, which is the only thing G8 was ever about.
    //
    // This asks the ResourceManager directly, key by key, under the culture the provider uses for that part.
    // A missing key returns null and is named in the failure rather than absorbed by a fallback.
    //
    // THE CULTURE IS PART OF THE KEY, NOT THE FILE ALONE: the catalog publishes .TitleAr/.TitleEn/.TitleFr,
    // and the provider resolves each under an explicit CultureInfo, so Arabic text is reachable from an
    // English request. That is why the keys are partitioned across SharedResources.{ar,en,fr}.resx (7 Title
    // + 7 Body each) rather than duplicated into all three.
    [Fact]
    public void Every_published_resource_key_is_actually_present_in_the_resx_files()
    {
        var catalog = new CommTemplateCatalog();
        var keys = catalog.RequiredResourceKeys();

        Assert.Equal(catalog.All().Count * 6, keys.Count);

        var missing = new List<string>();
        foreach (var key in keys)
        {
            var culture = key.EndsWith("Ar", StringComparison.Ordinal) ? "ar"
                        : key.EndsWith("Fr", StringComparison.Ordinal) ? "fr"
                        : "en";

            var value = CrossBuy.Resources.SharedResources.ResourceManager.GetString(
                key, System.Globalization.CultureInfo.GetCultureInfo(culture));

            if (string.IsNullOrWhiteSpace(value)) { missing.Add($"{key} [{culture}]"); }
        }

        Assert.Empty(missing);
    }

    // THE CONTROL for the test above. If GetString returned the neutral fallback for an unknown key instead
    // of null, every key would look present and the guard would be worthless — which is precisely the
    // failure mode it exists to rule out.
    [Fact]
    public void A_key_that_does_not_exist_resolves_to_null_so_the_presence_check_can_fail()
    {
        var absent = CrossBuy.Resources.SharedResources.ResourceManager.GetString(
            "Comm.Notification.NoSuchTemplate.TitleEn",
            System.Globalization.CultureInfo.GetCultureInfo("en"));

        Assert.Null(absent);
    }
}
