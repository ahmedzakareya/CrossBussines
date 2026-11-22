using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalErStepDef(string Code, string Ar, string En, int DueMinutes, bool Optional = false);
	public sealed record HospitalErStepState(HospitalErStepDef Def, DateTime DueAtUtc, HospitalErPathwayStep? Done, string State, int? Minutes);

	public static class HospitalErStepStatus
	{
		public const string OnTime = "OnTime", Late = "Late", NotIndicated = "NotIndicated", Due = "Due", Overdue = "Overdue";
	}

	// ==========================================================================================
	// HOSPITAL H237 — the pathways' steps and their clocks, free of the database. Targets from the guidelines:
	//   · SEPSIS (Surviving Sepsis Campaign hour-1 bundle): lactate, blood cultures before antibiotics, antibiotics within
	//     the hour; 30 mL/kg crystalloid within 3 h when hypotensive or lactate ≥ 4 (otherwise not indicated).
	//   · STROKE (AHA/ASA): glucose at once, door-to-CT 25 min, CT read 45 min, the thrombolysis decision 60 min.
	//   · STEMI (ESC/AHA): ECG within 10 min, aspirin, the catheter lab alerted 30 min, door-to-balloon 90 min.
	//   · TRAUMA (ATLS): primary survey 10 min, FAST 20 min, blood group and crossmatch 30 min, CT 60 min.
	// A step is ON TIME when done by its due time from the door, LATE after; undone it is DUE, then OVERDUE.
	// ==========================================================================================
	public static class HospitalErPathwayRules
	{
		public static readonly IReadOnlyDictionary<string, IReadOnlyList<HospitalErStepDef>> Steps = new Dictionary<string, IReadOnlyList<HospitalErStepDef>>
		{
			[HospitalErPathwayKind.Sepsis] = new HospitalErStepDef[]
			{
				new("LACTATE", "قياس اللاكتات", "Lactate measured", 60),
				new("CULTURES", "مزارع دم قبل المضاد", "Blood cultures before antibiotics", 60),
				new("ANTIBIOTICS", "إعطاء المضاد الحيوي", "Antibiotics given", 60),
				new("FLUIDS", "سوائل 30 مل/كغ", "Fluids 30 mL/kg", 180, true),
			},
			[HospitalErPathwayKind.Stroke] = new HospitalErStepDef[]
			{
				new("GLUCOSE", "قياس السكر", "Glucose checked", 15),
				new("CT", "الأشعة المقطعية للدماغ", "Brain CT done", 25),
				new("CT_READ", "قراءة الأشعة", "CT read", 45),
				new("DECISION", "قرار الإذابة", "Thrombolysis decision", 60),
			},
			[HospitalErPathwayKind.Stemi] = new HospitalErStepDef[]
			{
				new("ECG", "تخطيط القلب", "ECG done", 10),
				new("ASPIRIN", "إعطاء الأسبرين", "Aspirin given", 15),
				new("CATH_ALERT", "تنبيه مختبر القسطرة", "Cath lab alerted", 30),
				new("BALLOON", "فتح الشريان", "Balloon / reperfusion", 90),
			},
			[HospitalErPathwayKind.Trauma] = new HospitalErStepDef[]
			{
				new("PRIMARY", "المسح الأولي", "Primary survey", 10),
				new("FAST", "سونار FAST", "FAST scan", 20),
				new("CROSSMATCH", "فصيلة الدم والتوافق", "Group and crossmatch", 30),
				new("CT", "الأشعة المقطعية", "CT scan", 60),
			},
		};

		public static IReadOnlyList<HospitalErStepState> Evaluate(string kind, DateTime doorUtc, IReadOnlyList<HospitalErPathwayStep> done, DateTime nowUtc) =>
			Steps.TryGetValue(kind, out var defs) ? defs.Select(d =>
			{
				var due = doorUtc.AddMinutes(d.DueMinutes);
				var row = done.FirstOrDefault(x => x.Code == d.Code);
				if (row != null)
					return new HospitalErStepState(d, due, row, row.NotIndicated ? HospitalErStepStatus.NotIndicated : row.At <= due ? HospitalErStepStatus.OnTime : HospitalErStepStatus.Late,
						row.NotIndicated ? null : (int)Math.Round((row.At - doorUtc).TotalMinutes));
				return new HospitalErStepState(d, due, null, nowUtc > due ? HospitalErStepStatus.Overdue : HospitalErStepStatus.Due, null);
			}).ToList() : Array.Empty<HospitalErStepState>();

		public static string? StepRefusal(string kind, string? code, bool notIndicated, string? note)
		{
			if (!Steps.TryGetValue(kind, out var defs) || defs.All(d => d.Code != code)) return "not_found";
			if (notIndicated && string.IsNullOrWhiteSpace(note)) return "Write why it is not indicated";
			return (note ?? "").Trim().Length > 300 ? "The note is 300 characters at most" : null;
		}

		public static string? PreArrivalRefusal(DateTime expectedLocal, DateTime nowLocal, string? complaint, int? esi, string? pathway)
		{
			if (string.IsNullOrWhiteSpace(complaint) || complaint.Trim().Length > 300) return "Write the complaint (300 characters at most)";
			if (expectedLocal < nowLocal.AddHours(-2) || expectedLocal > nowLocal.AddHours(12)) return "The expected time is within the next 12 hours";
			if (esi is < 1 or > 5) return "ESI is 1 to 5";
			return pathway != null && !HospitalErPathwayKind.All.Contains(pathway) ? "not_found" : null;
		}

		/// The share of a pathway's timed steps done on time (not-indicated steps left out).
		public static decimal? Compliance(IReadOnlyList<HospitalErStepState> steps)
		{
			var timed = steps.Where(s => s.State is HospitalErStepStatus.OnTime or HospitalErStepStatus.Late or HospitalErStepStatus.Overdue).ToList();
			return timed.Count == 0 ? null : Math.Round(timed.Count(s => s.State == HospitalErStepStatus.OnTime) * 100m / timed.Count, 0);
		}
	}
}
