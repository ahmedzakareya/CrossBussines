using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H260 — what an operative note must hold before it is signed, free of the database.
	// ==========================================================================================
	public static class HospitalOperativeNoteRules
	{
		/// The fields a signature needs, in the order the form shows them (the key is the field's name on the form).
		public static IReadOnlyList<(string Key, string Label)> Required => new[]
		{
			("postOpDiagnosis", "Post-operative diagnosis"), ("procedurePerformed", "Procedure performed"), ("findings", "Findings"),
			("technique", "Technique"), ("bloodLossMl", "Estimated blood loss"), ("complications", "Complications"), ("postOpPlan", "Post-operative plan"),
		};

		/// The required fields left empty (labels). Blood loss 0 is an answer; complications must be written ("none").
		public static IReadOnlyList<string> Missing(HospitalOperativeNote n)
		{
			var miss = new List<string>();
			static bool E(string? s) => string.IsNullOrWhiteSpace(s);
			if (E(n.PostOpDiagnosis)) miss.Add("Post-operative diagnosis");
			if (E(n.ProcedurePerformed)) miss.Add("Procedure performed");
			if (E(n.Findings)) miss.Add("Findings");
			if (E(n.Technique)) miss.Add("Technique");
			if (n.BloodLossMl == null) miss.Add("Estimated blood loss");
			if (E(n.Complications)) miss.Add("Complications");
			if (E(n.PostOpPlan)) miss.Add("Post-operative plan");
			return miss;
		}

		public static string? EditRefusal(string caseStatus, DateTime? signedAt, int? bloodLossMl)
		{
			if (signedAt != null) return "The operative note is signed and locked";
			if (caseStatus is not (HospitalSurgeryStatus.InProgress or HospitalSurgeryStatus.Done)) return "The operative note is written once the operation has started";
			return bloodLossMl is < 0 or > 20000 ? "Enter the blood loss in millilitres (0 to 20000)" : null;
		}

		/// Only the case's own surgeon signs (the employee the surgeon's practitioner record is linked to).
		public static string? SignRefusal(HospitalOperativeNote n, int? surgeonEmployeeId, int? signer)
		{
			if (n.SignedAt != null) return "The operative note is signed and locked";
			if (surgeonEmployeeId == null || signer != surgeonEmployeeId) return "Only the case's surgeon signs the operative note";
			return Missing(n).Count > 0 ? "Complete the operative note before signing: {0}" : null;
		}

		/// An unsigned note of a case started more than 24 hours ago (the case's clock: the theatre records local time).
		public static bool Overdue(DateTime? startedAt, DateTime? signedAt, DateTime now) => signedAt == null && startedAt is DateTime s && (now - s).TotalHours > 24;
	}
}
