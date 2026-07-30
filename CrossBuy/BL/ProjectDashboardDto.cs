namespace CrossBuy.BL
{
    // Data model for the Projects & Contracting main dashboard (Views/Project/Dashboard.cshtml).
    // Built in ProjectController.Dashboard() from live data — mirrors the HR/Accounting dashboard style.
    public class ProjectDashboardDto
    {
        public int Year { get; set; }
        // counts by status bucket
        public int Total { get; set; }
        public int Active { get; set; }
        public int Completed { get; set; }
        public int OnHold { get; set; }
        public int Draft { get; set; }
        public int Cancelled { get; set; }
        // money rollups
        public decimal ContractValue { get; set; }   // Σ project contract value
        public decimal Budget { get; set; }           // Σ project budget
        public decimal Revenue { get; set; }          // Σ revenue (GL 4xxx tagged to projects), YTD
        public decimal Cost { get; set; }             // Σ cost (GL 5xxx tagged to projects), YTD
        public decimal Profit => System.Math.Round(Revenue - Cost, 2);

        public List<PrjStatusSlice> StatusDist { get; set; } = new();
        public List<PrjBarRow> TopByValue { get; set; } = new();
        public List<PrjPnlRow> TopByProfit { get; set; } = new();
        public List<PrjRecentRow> Recent { get; set; } = new();
    }

    public class PrjStatusSlice { public string Key { get; set; } = ""; public int Count { get; set; } }
    public class PrjBarRow { public string? Name { get; set; } public string? NameEn { get; set; } public string Code { get; set; } = ""; public decimal Value { get; set; } public int Pct { get; set; } }
    public class PrjPnlRow { public string Code { get; set; } = ""; public string? Name { get; set; } public decimal Revenue { get; set; } public decimal Cost { get; set; } public decimal Profit { get; set; } public decimal? Budget { get; set; } }
    public class PrjRecentRow { public string Code { get; set; } = ""; public string? Name { get; set; } public string? NameEn { get; set; } public string Status { get; set; } = ""; public decimal? ContractValue { get; set; } public string? Customer { get; set; } public DateTime? StartDate { get; set; } public DateTime? EndDate { get; set; } }
}