using Microsoft.AspNetCore.Mvc.Razor;

namespace CrossBuy
{
	// Global Razor base page. Exposes the bilingual helper T("عربي","English") to EVERY view so views don't each
	// need a local `string T(...)` function. Views that still declare their own local T() simply shadow this
	// (no conflict). Culture is read from Context.Items["Culture"] — the same source used across the app.
	public abstract class CbRazorPage<TModel> : RazorPage<TModel>
	{
		protected string T(string ar, string en)
			=> (Context?.Items != null && Context.Items.TryGetValue("Culture", out var c) && c?.ToString() == "ar") ? ar : en;
	}
}
