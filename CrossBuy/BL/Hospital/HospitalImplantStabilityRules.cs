using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H168 — the rules of implant stability by protocol (see HospitalImplantStability.cs). Pure, so the tests
	// hold them without a database.
	// ==========================================================================================
	public static class HospitalImplantStabilityRules
	{
		/// The least ISQ the clinic's protocol accepts for this loading kind — null when there is no approved protocol, or
		/// it sets no threshold for the kind (then nothing is blocked; the dentist decides on the measurements shown).
		public static int? MinIsq(HospitalImplantProtocol? p, string? loading) => p == null ? null : loading switch
		{
			HospitalImplantLoading.Immediate => p.MinIsqImmediate,
			HospitalImplantLoading.Early => p.MinIsqEarly,
			HospitalImplantLoading.Conventional => p.MinIsqConventional,
			_ => null,
		};

		public static string? RefuseMeasure(string? moment, int? isq, int? torque, DateTime? on, DateTime today)
		{
			if (!HospitalImplantMoment.All.Contains(moment ?? "")) return "Choose when the stability was measured";
			if (isq == null && torque == null) return "Enter the ISQ or the insertion torque";
			if (isq is < 1 or > 100) return "ISQ is 1 to 100";
			if (torque is < 1 or > 150) return "The insertion torque is 1 to 150 N·cm";
			if (on is not DateTime d || d.Date > today.Date) return "Enter the day it was measured (not in the future)";
			return null;
		}

		public static string? RefuseProtocol(int? immediate, int? early, int? conventional, int? torque, string? reference)
		{
			if (string.IsNullOrWhiteSpace(reference)) return "Name the guideline or document the protocol follows";
			foreach (var v in new[] { immediate, early, conventional }) if (v is < 1 or > 100) return "ISQ is 1 to 100";
			if (torque is < 1 or > 150) return "The insertion torque is 1 to 150 N·cm";
			return null;
		}

		/// The measurement a loading decision rests on: for immediate loading, the one taken at placement; otherwise the
		/// latest taken before loading — never one dated after the restoration.
		public static HospitalImplantMeasure? Basis(IEnumerable<HospitalImplantMeasure> measures, string? loading, DateTime restoredOn)
		{
			var moment = loading == HospitalImplantLoading.Immediate ? HospitalImplantMoment.Placement : HospitalImplantMoment.BeforeLoading;
			return measures.Where(m => m.Moment == moment && m.MeasuredOn.Date <= restoredOn.Date).OrderByDescending(m => m.MeasuredOn).ThenByDescending(m => m.ID).FirstOrDefault();
		}

		/// Whether the basis is below what the protocol asks for this loading kind (ISQ; and torque for immediate loading).
		public static bool BelowProtocol(HospitalImplantProtocol? p, string? loading, HospitalImplantMeasure basis)
		{
			var min = MinIsq(p, loading);
			if (min is int i && (basis.Isq is not int v || v < i)) return true;
			if (loading == HospitalImplantLoading.Immediate && p?.MinTorqueImmediate is int t && (basis.TorqueNcm is not int q || q < t)) return true;
			return false;
		}

		/// The refusal for a restoration, or null. Below the protocol needs the dentist's documented exception.
		public static string? RefuseRestoration(string? loading, string? restoration, HospitalImplantMeasure? basis, HospitalImplantProtocol? protocol, string? exceptionReason)
		{
			if (!HospitalImplantLoading.All.Contains(loading ?? "")) return "Choose how the implant is loaded";
			if (!HospitalImplantRestoration.All.Contains(restoration ?? "")) return "Choose a provisional or a definitive restoration";
			if (basis == null) return loading == HospitalImplantLoading.Immediate ? "Record the stability at placement first" : "Record the stability before loading first";
			if (BelowProtocol(protocol, loading, basis) && string.IsNullOrWhiteSpace(exceptionReason))
				return "Below the clinic's protocol for this loading: record why the dentist accepts the exception";
			return null;
		}
	}
}
