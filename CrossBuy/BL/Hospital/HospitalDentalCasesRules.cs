using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H17 — the rules of the long dental cases, with no database: an orthodontic contract's progress,
	// what is due on it and a missed adjustment; an implant's stages, its stability and the survival rate.
	// ==========================================================================================
	public static class HospitalDentalCasesRules
	{
		/// An implant with an ISQ below this is not loaded yet (the healing is extended).

		/// Whole months from the start to today (0 in the first month).
		public static int MonthsElapsed(DateTime start, DateTime today)
		{
			if (today.Date < start.Date) return 0;
			int m = (today.Year - start.Year) * 12 + today.Month - start.Month;
			return today.Day < start.Day ? m - 1 : m;
		}

		public static int Progress(DateTime start, int plannedMonths, DateTime today) =>
			plannedMonths <= 0 ? 100 : Math.Min(100, MonthsElapsed(start, today) * 100 / plannedMonths);

		/// What should have been paid by today: the first instalment on the start day, one more each month, never more
		/// than the contract. With no instalment the contract is paid in full at the start.
		public static decimal ExpectedPaid(decimal total, decimal instalment, DateTime start, DateTime today) =>
			today.Date < start.Date ? 0 : instalment <= 0 ? total : Math.Min(total, instalment * (MonthsElapsed(start, today) + 1));

		public static decimal Due(decimal total, decimal instalment, DateTime start, DateTime today, decimal paid) =>
			Math.Max(0, ExpectedPaid(total, instalment, start, today) - paid);

		public static bool Missed(string status, DateTime? nextVisit, DateTime today) => HospitalOrthoStatus.Open(status) && nextVisit is DateTime n && n.Date < today.Date;

		/// Why an orthodontic contract cannot be opened, or null.
		public static string? CaseRefusal(string appliance, int months, int? stages, decimal total, decimal instalment)
		{
			if (!HospitalOrthoAppliance.All.Contains(appliance)) return "Choose the appliance";
			if (months is < 1 or > 60) return "The treatment is 1 to 60 months";
			if (HospitalOrthoAppliance.Staged(appliance) ? stages is not (>= 1 and <= 99) : stages != null) return "Clear aligners are planned in 1 to 99 stages; other appliances have none";
			if (total < 0 || instalment < 0) return "An amount cannot be negative";
			if (instalment > total) return "The instalment cannot be more than the contract";
			return null;
		}

		public static bool CanMove(string from, string to) => (from, to) switch
		{
			(HospitalImplantStage.Planned, HospitalImplantStage.Healing) => true,
			(HospitalImplantStage.Healing, HospitalImplantStage.Restored) => true,
			(HospitalImplantStage.Healing or HospitalImplantStage.Restored, HospitalImplantStage.Failed) => true,
			_ => false,
		};

		/// Placing a fixture records its lot and day (and its stability when measured).
		public static string? PlaceRefusal(string? lot, DateTime? placedOn, int? isq, DateTime today)
		{
			if (string.IsNullOrWhiteSpace(lot)) return "Record the fixture's lot";
			if (placedOn is not DateTime d || d.Date > today.Date) return "Enter the day it was placed (not in the future)";
			if (isq is < 1 or > 100) return "ISQ is 1 to 100";
			return null;
		}

		/// A restored implant must have reached a loadable stability.
		// H168: there is no one ISQ for every case — the loading decision is the clinic's protocol (HospitalImplantStabilityRules).

		/// Of the implants placed in the last three years, the share not failed.
		public static decimal? SurvivalRate(IEnumerable<(DateTime? Placed, string Stage)> implants, DateTime today)
		{
			var placed = implants.Where(i => i.Placed is DateTime p && p >= today.AddYears(-3)).ToList();
			return placed.Count == 0 ? null : Math.Round(placed.Count(i => i.Stage != HospitalImplantStage.Failed) * 100m / placed.Count, 1);
		}
	}
}
