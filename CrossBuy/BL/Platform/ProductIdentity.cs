namespace CrossBuy.BL.Platform
{
	// =================================================================================================
	// THE ONE PLACE THE PRODUCT NAMES ITSELF.
	//
	// Before this type the visible product name was a string literal repeated across layouts, login
	// screens, print headers, an email sender name and a web manifest. Renaming it meant finding every
	// copy, and the next rename would mean finding them again. Everything a human READS now resolves
	// here; a second literal is a regression, and BrandRegressionTests fails the build if one appears.
	//
	// THIS IS THE VISIBLE NAME ONLY, AND THAT DISTINCTION IS THE WHOLE POINT.
	// It deliberately does NOT rename, and must never be used to rename: namespaces, assemblies, the
	// solution, the database, DbContext, SQL objects, routes, API contracts, configuration keys,
	// environment variables, authentication schemes or cookie names. Those are IDENTIFIERS - other
	// systems, stored sessions and deployed configuration are bound to them by their exact spelling,
	// and changing one to match a marketing name breaks a running install for no user-visible gain.
	// The assembly is still CrossBuy.dll on purpose.
	//
	// NOT `Brand`. In this codebase Brand already means the TENANT's own trade name - the merchant
	// identity printed on their receipts, with its own logo and colours, resolved per company by
	// BrandService. That is the customer's brand. This is OUR product's name, and the two appear on
	// the same page for different reasons, so they do not share a word.
	//
	// NOT LOCALIZED, AND THAT IS A DECISION RATHER THAN AN OMISSION. A product name is the same string
	// in every language: the Arabic resources already read `العملاء — CrossBuy`, keeping the Latin mark
	// inside Arabic text, which is how the product was always presented. Giving it an Arabic
	// translation would invent a second product name nobody has approved. The surrounding sentences
	// stay localized exactly as they were, so RTL and the Cairo face are untouched by this type.
	//
	// NOT CONFIGURATION. There is no appsettings key and no options binding, because a deployment that
	// can rename the product is a deployment that can disagree with its own documentation, its support
	// channel and its contracts. One compiled constant means every install says the same thing.
	// =================================================================================================
	public static class ProductIdentity
	{
		// The canonical product name. Everything visible resolves to this or is derived from it.
		public const string Name = "CrossBusiness Platform";

		// The short mark, for places with no room for the full name: a collapsed sidebar, a browser tab
		// suffix, a narrow print header. It is a PREFIX of Name rather than an unrelated abbreviation,
		// so the two never read as different products.
		public const string ShortName = "CrossBusiness";

		// The tagline. Separate from Name because it is a sentence about the product, not its label,
		// and it belongs only where there is room to say something.
		public const string Tagline = "The Cross-Business Platform";

		// Sub-surfaces - the cashier terminal, the hypermarket front, the POS shell - are NOT separate
		// products and must not acquire separate names. They are the short mark plus what the surface
		// is, composed here so the relationship is enforced rather than remembered: a future
		// `CrossBusiness Till` cannot drift away from the family because it is built from ShortName.
		//
		// The suffix is the caller's already-localized word where one exists; passing a raw English
		// noun is fine for surfaces that have never been localized, which is why this takes a string
		// rather than a resource key.
		public static string Surface(string suffix)
			=> string.IsNullOrWhiteSpace(suffix) ? Name : ShortName + " " + suffix.Trim();

		// A browser title. Kept here rather than in each layout because the separator was already
		// inconsistent across layouts (a hyphen in three, a middot in three), and one of them is now
		// wrong wherever it is spelled by hand.
		public static string PageTitle(string? page)
			=> string.IsNullOrWhiteSpace(page) ? Name : page.Trim() + " — " + Name;

		// =========================================================================================
		// VISUAL IDENTITY - THE PALETTE AND THE DELIVERED PRODUCTION ASSETS.
		//
		// Both halves are now decided. D-36 records the colour values and crossbuy-brand.css is
		// converged to them; the approved artwork package has been delivered and is what renders.
		// =========================================================================================
		public static class Palette
		{
			// The SINGLE canonical product-brand colour. White on it measures 7.05:1.
			public const string Primary = "#3A5795";

			// Supporting ONLY, and never a second primary. It must also never carry white body
			// text: that measures 4.23:1 and fails AA, which is why button hovers take Dark and
			// links take Primary rather than this.
			public const string Supporting = "#637BAD";

			public const string Accent = "#D4AF37";
			public const string Dark = "#1F2D4D";
			public const string Light = "#F5F7FB";
		}

		public static class Assets
		{
			private const string Logos = "~/Backend-assets/media/logos/";
			private const string Misc = "~/Backend-assets/media/misc/";

			// ---- THE LOGOS -----------------------------------------------------------------------
			//
			// NAMED BY THE GROUND THEY SIT ON, NOT BY THEIR OWN COLOUR, and with this artwork the
			// distinction is no longer subtle: each variant carries its OWN OPAQUE PLATE - the
			// light-ground file paints a white rectangle behind the mark, the dark-ground file paints
			// #1F2D4D. Put one on the wrong theme and you do not get a low-contrast wordmark, you get
			// a solid block of the wrong colour across the header. The supplied filenames use the
			// same convention: -light means FOR light, which is the opposite of the retired
			// crossbuy-logo-light.svg, where light meant light-COLOURED.
			public const string LogoOnLight = Logos + "crossbusiness-logo-horizontal-light.svg";
			public const string LogoOnDark = Logos + "crossbusiness-logo-horizontal-dark.svg";

			// The square mark, for icon-only contexts. Every favicon size is cut from this same
			// artwork, which is what keeps the browser tab and the installed app one identity.
			public const string Icon = Logos + "crossbusiness-icon.svg";

			// ---- FAVICONS ------------------------------------------------------------------------
			//
			// PREFIXED crossbusiness- ON PURPOSE. The supplied package names these favicon.ico and
			// favicon-NN.png, and this directory ALREADY holds a Metronic favicon.ico and favicon.png
			// that twelve views still reference. Dropping the package in under its own names would
			// have silently overwritten vendor files and made the change invisible in a diff. The
			// semantic contract is unchanged; only the on-disk basename is disambiguated.
			public const string FaviconIco = Logos + "crossbusiness-favicon.ico";
			public const string Favicon16 = Logos + "crossbusiness-favicon-16.png";
			public const string Favicon32 = Logos + "crossbusiness-favicon-32.png";
			public const string Favicon64 = Logos + "crossbusiness-favicon-64.png";
			public const string Favicon192 = Logos + "crossbusiness-favicon-192.png";
			public const string Favicon512 = Logos + "crossbusiness-favicon-512.png";

			// ---- ILLUSTRATIONS -------------------------------------------------------------------
			public const string SignInIllustration = Misc + "crossbusiness-signin-illustration.png";
			public const string CashierIllustration = Misc + "crossbusiness-cashier-illustration.png";

			// The whole delivered package, so a test can prove every one of them exists rather than
			// a broken image being discovered on the sign-in page of a live install.
			public static readonly string[] All =
			{
				LogoOnLight, LogoOnDark, Icon, FaviconIco,
				Favicon16, Favicon32, Favicon64, Favicon192, Favicon512,
				SignInIllustration, CashierIllustration,
			};

			// The favicon sizes a document or manifest may declare, paired with the pixel dimensions
			// the file actually has. A declaration that disagrees with its file is how a browser ends
			// up picking a blurry icon, and it is exactly what was already wrong here: the manifest
			// claimed 192x192 for a 1024x1158 image and 512x512 for a 240x147 Metronic placeholder.
			public static readonly (string Path, int Size)[] FaviconSizes =
			{
				(Favicon16, 16), (Favicon32, 32), (Favicon64, 64), (Favicon192, 192), (Favicon512, 512),
			};

			// TRUE now that the approved package is in the repository AND referenced. A test ties
			// this to the files in both directions, so it cannot run ahead of them.
			public static readonly bool FinalAssetsDelivered = true;

			// ---- RETIRED ---------------------------------------------------------------------------
			//
			// The previous artwork, kept on disk and named here so "no product surface still points at
			// it" is an enforceable rule rather than a claim. Deleting these is a separate decision:
			// they are unreferenced by the product, but this repository has other consumers and a file
			// that is merely unused is not proven safe to remove.
			public static readonly string[] Retired =
			{
				Logos + "crossbuy-logo.svg",
				Logos + "crossbuy-logo-light.svg",
				Logos + "crossbuy-favicon.svg",
				Misc + "crossbuy-auth.svg",
				Misc + "crossbuy-cashier.svg",
			};

			// The brand primary in force in crossbuy-brand.css. Restated here so the theme-color meta
			// tags that paint the mobile browser chrome are provably the same colour as the shell.
			public const string ActivePrimary = Palette.Primary;
		}
	}
}
