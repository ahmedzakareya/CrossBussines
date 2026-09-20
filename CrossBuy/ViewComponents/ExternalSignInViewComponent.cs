using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.ViewComponents
{
    // ============================================================================================
    // THE SINGLE SIGN-ON ROW ON THE SIGN-IN PAGE.
    //
    // BOTH BUTTONS ARE ALWAYS DRAWN, because the design draws both. What varies is what each one
    // DOES, and the component is honest about it:
    //
    //   configured here  -> a link to the real challenge endpoint, which starts the OAuth flow.
    //   not configured   -> the same button, which when pressed says so in the card's own notice
    //                       and names who can set it up.
    //
    // The alternative shapes were both worse. Hiding the row when nothing is configured meant the
    // page did not match the design on any installation that had not set the secrets up yet.
    // Drawing a button that silently does nothing is the href="#" defect this whole screen was
    // rebuilt to remove: it looked like a way in, and nothing ever admitted it was not one.
    //
    // "CONFIGURED" IS NOT A FLAG SOMEBODY SETS. It is whether the authentication scheme exists at
    // runtime, which Program.cs only registers when a ClientId and ClientSecret are both present.
    // There is no way to claim a provider works without actually having given it credentials.
    // ============================================================================================
    public class ExternalSignInViewComponent : ViewComponent
    {
        private readonly SignInManager<Users> _signInManager;

        public ExternalSignInViewComponent(SignInManager<Users> signInManager)
        {
            _signInManager = signInManager;
        }

        // The brand marks are inline SVG: both companies require their own mark on a sign-in button,
        // and neither ships in the icon font this application uses.
        private const string MicrosoftMark =
            "<svg viewBox='0 0 21 21' xmlns='http://www.w3.org/2000/svg' aria-hidden='true'>" +
            "<rect x='1' y='1' width='9' height='9' fill='#F25022'/>" +
            "<rect x='11' y='1' width='9' height='9' fill='#7FBA00'/>" +
            "<rect x='1' y='11' width='9' height='9' fill='#00A4EF'/>" +
            "<rect x='11' y='11' width='9' height='9' fill='#FFB900'/></svg>";

        private const string GoogleMark =
            "<svg viewBox='0 0 48 48' xmlns='http://www.w3.org/2000/svg' aria-hidden='true'>" +
            "<path fill='#4285F4' d='M45.1 24.5c0-1.6-.1-3.2-.4-4.7H24v8.9h11.8c-.5 2.8-2 5.1-4.4 6.7v5.5h7.1c4.2-3.8 6.6-9.5 6.6-16.4z'/>" +
            "<path fill='#34A853' d='M24 46c5.9 0 10.9-2 14.5-5.3l-7.1-5.5c-2 1.3-4.5 2.1-7.4 2.1-5.7 0-10.5-3.8-12.2-9H4.5v5.7C8.1 41.2 15.4 46 24 46z'/>" +
            "<path fill='#FBBC05' d='M11.8 28.3c-.4-1.3-.7-2.7-.7-4.3s.3-3 .7-4.3v-5.7H4.5A22 22 0 0 0 2 24c0 3.6.9 6.9 2.5 9.9l7.3-5.6z'/>" +
            "<path fill='#EA4335' d='M24 10.7c3.2 0 6.1 1.1 8.4 3.3l6.3-6.3C34.9 4.1 29.9 2 24 2 15.4 2 8.1 6.8 4.5 13.9l7.3 5.7c1.7-5.1 6.5-8.9 12.2-8.9z'/></svg>";

        public async Task<IViewComponentResult> InvokeAsync()
        {
            var schemes = (await _signInManager.GetExternalAuthenticationSchemesAsync())
                .Select(s => s.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var model = new ExternalSignInModel
            {
                Providers = new List<ExternalProvider>
                {
                    new() { Scheme = "Microsoft", Display = "Microsoft", Mark = MicrosoftMark, Configured = schemes.Contains("Microsoft") },
                    new() { Scheme = "Google",    Display = "Google",    Mark = GoogleMark,    Configured = schemes.Contains("Google") },
                }
            };

            return View(model);
        }
    }

    public sealed class ExternalSignInModel
    {
        public List<ExternalProvider> Providers { get; set; } = new();
    }

    public sealed class ExternalProvider
    {
        public string Scheme { get; set; } = "";
        public string Display { get; set; } = "";
        public string Mark { get; set; } = "";

        /// <summary>True only when the scheme actually exists at runtime — that is, when a ClientId
        /// and ClientSecret were supplied and Program.cs registered the provider.</summary>
        public bool Configured { get; set; }
    }
}
