namespace CrossBuy.BL
{
    // Data model for the Restaurant / POS main dashboard (Views/Pos/Dashboard.cshtml).
    // Built in PosController.Dashboard() from live PosOrders/PosShifts/PosTerminals — no fabricated data.
    public class PosDashboardDto
    {
        public int Year { get; set; }
        public decimal SalesToday { get; set; }
        public decimal SalesMonth { get; set; }
        public decimal SalesYtd { get; set; }
        public int OrdersToday { get; set; }
        public int OrdersMonth { get; set; }
        public decimal AvgTicket => OrdersMonth > 0 ? System.Math.Round(SalesMonth / OrdersMonth, 2) : 0m;
        public int OpenOrders { get; set; }
        public int OpenShifts { get; set; }
        public int OccupiedTables { get; set; }
        public int Terminals { get; set; }

        public List<PosDayPoint> Last7 { get; set; } = new();
        public List<PosTypeSlice> ByType { get; set; } = new();
        public List<PosRecentOrder> Recent { get; set; } = new();
        public List<PosTopItem> TopItems { get; set; } = new();
    }

    public class PosDayPoint { public DateTime Date { get; set; } public decimal Total { get; set; } }
    public class PosTypeSlice { public string Type { get; set; } = ""; public int Count { get; set; } public decimal Amount { get; set; } public int Pct { get; set; } }
    public class PosRecentOrder { public string? ReceiptNo { get; set; } public string Type { get; set; } = ""; public decimal Total { get; set; } public DateTime When { get; set; } public string Status { get; set; } = ""; }
    public class PosTopItem { public string Name { get; set; } = ""; public decimal Qty { get; set; } public decimal Amount { get; set; } public int Pct { get; set; } }
}