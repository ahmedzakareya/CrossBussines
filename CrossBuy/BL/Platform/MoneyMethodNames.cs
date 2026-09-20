namespace CrossBuy.BL.Platform
{
    // Receipt.Method and Payment.Method store an English code on the row. Anywhere it is SHOWN it has to be
    // read, so the mapping lives in one place rather than being repeated at each screen — AccountingController
    // already carried a private copy for the movement summary, and a second copy here would be the two
    // drifting apart the first time a method is added.
    public static class MoneyMethodNames
    {
        public static string Arabic(string? method) => (method ?? "") switch
        {
            "Cash" => "نقدي",
            "Bank" => "تحويل بنكي",
            "Cheque" or "Check" => "شيك",
            "Card" => "بطاقة",
            _ => method ?? "",
        };

        public static string English(string? method) => (method ?? "") switch
        {
            "Bank" => "Bank transfer",
            "Check" => "Cheque",
            _ => method ?? "",
        };

        public static string For(string? method, bool isArabic) => isArabic ? Arabic(method) : English(method);
    }
}
