namespace CrossBuy.BL
{
    /// <summary>
    /// ONE folding of Arabic, for MATCHING only — never for storage or display.
    /// </summary>
    /// <remarks>
    /// Arabic arrives written several ways for the same word: "الأصناف" and "الاصناف", "مستشفى" and
    /// "مستشفي", "الفاتورة" and "الفاتوره". A person typing any of them means the same screen, so a
    /// matcher that compares the raw strings fails for reasons the person cannot see and cannot fix.
    ///
    /// This folding was written for the assistant's intent matcher and lived private inside it. It is
    /// here because the header search box now needs exactly the same question answered — and the one
    /// thing worse than no folding is TWO foldings that drift, so that the same word finds a screen
    /// and misses a record. Anything that matches Arabic the user typed calls this.
    ///
    /// It is deliberately narrow: tashkeel and tatweel carry no lexical weight in a lookup, the alef
    /// forms collapse, final yaa and taa marbouta collapse. It does NOT stem, and it does not touch
    /// anything but the characters named — a folding that guesses is a matcher that lies.
    /// </remarks>
    public static class ArabicText
    {
        public static string Fold(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (ch >= 'ً' && ch <= 'ْ') continue;   // tashkeel
                if (ch == 'ـ') continue;                     // tatweel
                sb.Append(ch switch
                {
                    'أ' or 'إ' or 'آ' => 'ا',   // أ إ آ -> ا
                    'ى' => 'ي',                           // ى -> ي
                    'ة' => 'ه',                           // ة -> ه
                    _ => char.ToLowerInvariant(ch),
                });
            }
            return sb.ToString();
        }
    }
}
