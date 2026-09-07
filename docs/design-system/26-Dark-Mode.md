# 26 — Dark Mode

Dark mode follows Metronic’s `[data-bs-theme="dark"]` switch and the token overrides. It is a supported design contract, but production activation remains a separate implementation decision.

Dark canvas is `#0D1523`, surface `#151F33`, rules `#24304A`, primary action `#4B86E8`. Elevation is expressed by lighter surfaces and restrained black shadows. Avoid pure black/white and saturated status backgrounds.

All semantic pairs require AA contrast. Images, logos, charts, editors, calendars, Select2 menus and embedded report content need explicit dark behavior. Printed/exported reports default to light unless the export requests a dark presentation. User choice should persist without a flash of the wrong theme.

Never create dark mode by inverting the page or reducing opacity globally.
