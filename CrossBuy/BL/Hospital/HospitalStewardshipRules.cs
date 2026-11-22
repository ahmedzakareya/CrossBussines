using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	public static class HospitalAbxState
	{
		/// Restricted, not yet approved, still inside its first 24 h.
		public const string AwaitingApproval = "AwaitingApproval";
		/// Restricted and not approved past 24 h.
		public const string ApprovalOverdue = "ApprovalOverdue";
		/// Restricted and rejected, yet still running.
		public const string RejectedRunning = "RejectedRunning";
		/// 48 h or more on therapy without a time-out.
		public const string TimeOutDue = "TimeOutDue";
		/// Past 72 h without a time-out.
		public const string TimeOutOverdue = "TimeOutOverdue";
		public const string Reviewed = "Reviewed";
		public const string Early = "Early";
		public static readonly IReadOnlyList<string> All = new[] { ApprovalOverdue, RejectedRunning, TimeOutOverdue, AwaitingApproval, TimeOutDue, Early, Reviewed };
		public static bool Urgent(string s) => s is ApprovalOverdue or RejectedRunning or TimeOutOverdue;
	}

	// ==========================================================================================
	// HOSPITAL H235 — the stewardship rules, free of the database.
	// ==========================================================================================
	public static class HospitalStewardshipRules
	{
		public const int ApprovalHours = 24, TimeOutFromHours = 48, TimeOutByHours = 72;

		/// Where an antimicrobial order stands. The approval question comes first for a restricted medicine.
		public static string State(bool restricted, DateTime startUtc, DateTime nowUtc, string? approval, bool timedOut)
		{
			double h = (nowUtc - startUtc).TotalHours;
			if (restricted && approval == HospitalAbxDecision.Rejected) return HospitalAbxState.RejectedRunning;
			if (restricted && approval != HospitalAbxDecision.Approved) return h > ApprovalHours ? HospitalAbxState.ApprovalOverdue : HospitalAbxState.AwaitingApproval;
			if (timedOut) return HospitalAbxState.Reviewed;
			if (h > TimeOutByHours) return HospitalAbxState.TimeOutOverdue;
			return h >= TimeOutFromHours ? HospitalAbxState.TimeOutDue : HospitalAbxState.Early;
		}

		public static string? ReviewRefusal(string kind, string? decision, string? note)
		{
			if (!HospitalAbxReviewKind.All.Contains(kind)) return "not_found";
			var allowed = kind == HospitalAbxReviewKind.Approval ? HospitalAbxDecision.ForApproval : HospitalAbxDecision.ForTimeOut;
			if (!allowed.Contains(decision ?? "")) return "Choose the decision";
			bool needsWhy = decision is HospitalAbxDecision.Rejected or HospitalAbxDecision.Narrow or HospitalAbxDecision.Stop;
			if (needsWhy && string.IsNullOrWhiteSpace(note)) return "Write the reason";
			return (note ?? "").Trim().Length > 500 ? "The note is 500 characters at most" : null;
		}

		/// Days of therapy an order contributes to a period: each calendar day (ward time) on which it was running counts once.
		public static int DaysOfTherapy(DateTime startLocal, DateTime? stopLocal, DateTime fromLocal, DateTime toExclusiveLocal)
		{
			var a = startLocal.Date > fromLocal.Date ? startLocal.Date : fromLocal.Date;
			var endInclusive = (stopLocal ?? toExclusiveLocal.AddDays(-1)).Date;
			if (endInclusive >= toExclusiveLocal.Date) endInclusive = toExclusiveLocal.Date.AddDays(-1);
			return endInclusive < a ? 0 : (int)(endInclusive - a).TotalDays + 1;
		}

		/// DOT per 1 000 patient-days, the benchmark rate (CDC NHSN AU).
		public static decimal? Per1000(int dot, int patientDays) => patientDays <= 0 ? null : Math.Round(dot * 1000m / patientDays, 1);
	}
}
