namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // HOSPITAL H50 — the dashboard's numbers, as KPIs over the H49 management datasets. Every tile is a field the
    // report already publishes, summed, averaged or counted by the platform's own shaper (KpiEvaluator): a tile and
    // the report it opens can never disagree, and a person who may not open the report gets no tile (the dataset's
    // permission is checked as well as the KPI's). The period is the caller's (today, this month), passed as the
    // dataset's own From/To.
    // ============================================================================================
    public static class HospitalKpis
    {
        public const string Appointments = "hospital.outpatient.appointments";
        public const string Seen = "hospital.outpatient.seen";
        public const string ErWaiting = "hospital.er.waiting";
        public const string ErDoorToDoctor = "hospital.er.door-to-doctor";
        public const string BedsTotal = "hospital.beds.total";
        public const string BedsOccupied = "hospital.beds.occupied";
        public const string Revenue = "hospital.revenue";
        public const string Collected = "hospital.cash.collected";
        public const string Claimed = "hospital.claims.claimed";
        public const string ClaimsRejected = "hospital.claims.rejected";
        public const string CriticalOpen = "hospital.critical.unacknowledged";
        public const string PreauthPending = "hospital.preauth.pending";

        private static readonly ReportFilter[] None = Array.Empty<ReportFilter>();
        private const string C = HospitalReportPermissions.View, M = HospitalReportPermissions.Finance, Owner = "Hospital";

        public static IEnumerable<KpiDefinition> All()
        {
            yield return new(Appointments, Owner, HospitalReportCodes.DoctorProductivity, "Appointments", ReportAggregate.Sum, C,
                "Appointment date within From/To; not cancelled", "Company; one row per doctor", None);
            yield return new(Seen, Owner, HospitalReportCodes.DoctorProductivity, "Seen", ReportAggregate.Sum, C,
                "Appointment date within From/To; status Done", "Company; one row per doctor", None);
            yield return new(ErWaiting, Owner, HospitalReportCodes.ErWaiting, "WaitingNow", ReportAggregate.Sum, C,
                "Arrivals within From/To still in triage or waiting now", "Company; one row per ER visit", None);
            yield return new(ErDoorToDoctor, Owner, HospitalReportCodes.ErWaiting, "DoorToDoctorMin", ReportAggregate.Average, C,
                "Arrivals within From/To that have been seen; minutes from arrival to the doctor", "Company; one row per ER visit", None);
            yield return new(BedsTotal, Owner, HospitalReportCodes.BedOccupancy, "Beds", ReportAggregate.Sum, C,
                "Active beds of active wards now", "Company; one row per ward", None);
            yield return new(BedsOccupied, Owner, HospitalReportCodes.BedOccupancy, "OccupiedNow", ReportAggregate.Sum, C,
                "Bed stays open now", "Company; one row per ward", None);
            yield return new(Revenue, Owner, HospitalReportCodes.Revenue, "Gross", ReportAggregate.Sum, M,
                "Lines of the bills closed within From/To", "Company; one row per bill line, before VAT", None);
            yield return new(Collected, Owner, HospitalReportCodes.CashierCollection, "Amount", ReportAggregate.Sum, M,
                "Receipts within From/To", "Company; one row per receipt, in the base currency", None);
            yield return new(Claimed, Owner, HospitalReportCodes.PayerPerformance, "Claimed", ReportAggregate.Sum, M,
                "Claim batches created within From/To, past draft", "Company; one row per insurer", None);
            yield return new(ClaimsRejected, Owner, HospitalReportCodes.PayerPerformance, "Rejected", ReportAggregate.Sum, M,
                "Claim batches created within From/To; rejected lines, resubmitted or not", "Company; one row per insurer", None);
            yield return new(CriticalOpen, Owner, HospitalReportCodes.CriticalValues, "Acknowledged", ReportAggregate.Count, C,
                "Critical-value alerts sent within From/To and not acknowledged", "Company; one row per alert",
                new[] { ReportFilter.Eq("Acknowledged", "0") });
            yield return new(PreauthPending, Owner, HospitalReportCodes.PendingApprovals, "RequestNo", ReportAggregate.Count, M,
                "Pre-authorisations an insurer has not answered, now", "Company; one row per request", None);
        }
    }
}
