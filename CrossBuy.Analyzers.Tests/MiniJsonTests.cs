namespace CrossBuy.Analyzers.Tests;

/// <summary>
/// The baseline reader.
///
/// Hand-rolled parsers are where quiet defects live, and this one runs inside the compiler: a throw here would
/// surface as a build failure with no obvious cause. So it is tested as its own unit, including the malformed
/// cases, and the analyzer's contract is that unparseable means "absent" rather than "crash".
/// </summary>
public class MiniJsonTests
{
    [Fact]
    public void The_real_baseline_shape_parses_with_its_counts_and_ids()
    {
        var document = BaselineDocument.TryParse("""
{
  "schema": 1,
  "frozenBaseline": { "mutating": 388, "attributeProtected": 157, "inBodyProtected": 88, "gaps": 143 },
  "rules": [ "This baseline may only SHRINK." ],
  "count": 2,
  "entries": [
    { "id": "AccountController.Login", "controller": "AccountController", "action": "Login",
      "classification": "AnonymousByDesign" },
    { "id": "AdminController.AddAttachments", "controller": "AdminController", "action": "AddAttachments",
      "classification": "AuthorizationGap" }
  ]
}
""");

        Assert.NotNull(document);
        Assert.Equal(388, document!.FrozenMutating);
        Assert.Equal(157, document.FrozenAttributeProtected);
        Assert.Equal(88, document.FrozenInBodyProtected);
        Assert.Equal(143, document.FrozenGaps);
        Assert.Equal(2, document.DeclaredCount);
        Assert.Equal(2, document.Entries.Count);
        Assert.True(document.Entries["AccountController.Login"].IsAnonymousByDesign);
        Assert.False(document.Entries["AdminController.AddAttachments"].IsAnonymousByDesign);
    }

    [Theory]
    [InlineData("escaped \\\"quote\\\"", "escaped \"quote\"")]
    [InlineData("tab\\tnewline\\n", "tab\tnewline\n")]
    [InlineData("unicode \\u0623", "unicode أ")]   // Arabic alef — the baseline may carry Arabic reasons
    [InlineData("slash \\/ and backslash \\\\", "slash / and backslash \\")]
    public void String_escapes_are_decoded(string encoded, string expected)
    {
        var document = BaselineDocument.TryParse(
            $@"{{ ""count"": 1, ""entries"": [ {{ ""id"": ""X.Y"", ""classification"": ""{encoded}"" }} ] }}");

        Assert.Equal(expected, document!.Entries["X.Y"].Classification);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{ \"entries\": [ { \"id\": ")]
    [InlineData("{ \"count\": 1 } trailing")]
    [InlineData("{ \"a\" 1 }")]
    [InlineData("[1, 2,]")]
    [InlineData("{ \"s\": \"unterminated }")]
    public void Malformed_input_returns_null_rather_than_throwing(string json)
    {
        Assert.Null(BaselineDocument.TryParse(json));
    }

    [Fact]
    public void An_entry_without_an_id_is_skipped_rather_than_keyed_on_an_empty_string()
    {
        var document = BaselineDocument.TryParse(
            @"{ ""count"": 2, ""entries"": [ { ""controller"": ""X"" }, { ""id"": ""X.Y"" } ] }");

        Assert.Single(document!.Entries);
        Assert.True(document.Entries.ContainsKey("X.Y"));
    }

    [Fact]
    public void Empty_objects_and_arrays_parse()
    {
        var document = BaselineDocument.TryParse(@"{ ""frozenBaseline"": {}, ""entries"": [] }");

        Assert.NotNull(document);
        Assert.Empty(document!.Entries);
        Assert.Equal(0, document.FrozenGaps);
    }
}
