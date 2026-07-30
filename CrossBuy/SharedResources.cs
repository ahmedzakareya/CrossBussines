namespace CrossBuy
{
    // Marker type for shared, cross-module UI strings.
    // Its computed resource base name is "CrossBuy.Resources.SharedResources",
    // so IStringLocalizer<CrossBuy.SharedResources> / IHtmlLocalizer<CrossBuy.SharedResources>
    // resolve the SAME resx trio (Resources/SharedResources[.en|.ar].resx) already used by the
    // strongly-typed CrossBuy.Resources.SharedResources class. New shared keys therefore need
    // only a resx entry — no Designer.cs regeneration.
    public class SharedResources
    {
    }
}
