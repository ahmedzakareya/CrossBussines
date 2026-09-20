namespace CrossBuy.Views.Account
{
    // ============================================================================================
    // THE TWO SHAPES THE PASSWORD REVEAL SWAPS BETWEEN.
    //
    // DRAWN, NOT TAKEN FROM THE ICON FONT. Keenicons ships exactly one eye and one eye-slash; both
    // are a decade old in shape - a heavy lens with a bar through it - and neither sits with the
    // rest of this card. There is no modern variant in the font to pick instead.
    //
    // HERE RATHER THAN IN THE VIEW because the same two strings are needed twice: once as the
    // control's resting state in the markup, and once in the script that swaps them on a press.
    // Written inline they would be two pairs that could drift apart.
    // ============================================================================================
    public static class LoginIcons
    {
        /// <summary>Shown while the password is visible - press to hide.</summary>
        public const string EyeOn =
            "<svg viewBox='0 0 24 24' fill='none' aria-hidden='true'>" +
            "<path d='M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z' " +
            "stroke='currentColor' stroke-width='1.7' stroke-linejoin='round'/>" +
            "<circle cx='12' cy='12' r='3' stroke='currentColor' stroke-width='1.7'/></svg>";

        /// <summary>Shown while the password is hidden - press to reveal. The resting state.</summary>
        public const string EyeOff =
            "<svg viewBox='0 0 24 24' fill='none' aria-hidden='true'>" +
            "<path d='M2.5 12S6 5.5 12 5.5c1.4 0 2.7.36 3.85.93M21.5 12s-1.2 2.23-3.4 4.05" +
            "M9.9 9.9a3 3 0 0 0 4.2 4.2' stroke='currentColor' stroke-width='1.7' stroke-linecap='round' stroke-linejoin='round'/>" +
            "<path d='M17.6 16.3A9.6 9.6 0 0 1 12 18.5c-2.7 0-4.9-1.3-6.4-2.6' stroke='currentColor' stroke-width='1.7' stroke-linecap='round' stroke-linejoin='round'/>" +
            "<path d='M4 20 20 4' stroke='currentColor' stroke-width='1.7' stroke-linecap='round'/></svg>";
    }
}
