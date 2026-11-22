using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	/// The worst values of the first 24 hours, as APACHE II takes them. A missing value scores 0 (assumed normal), as the
	/// original method does.
	public sealed class HospitalApacheInput
	{
		public decimal? TempC { get; set; }
		public int? Map { get; set; }
		public int? HeartRate { get; set; }
		public int? RespRate { get; set; }
		/// Fraction of inspired oxygen, 0.21..1.
		public decimal? FiO2 { get; set; }
		public int? PaO2 { get; set; }
		/// Alveolar-arterial gradient (mmHg), used when FiO2 ≥ 0.5.
		public int? AaGradient { get; set; }
		public decimal? Ph { get; set; }
		public int? Sodium { get; set; }
		public decimal? Potassium { get; set; }
		/// Creatinine, mg/dL.
		public decimal? Creatinine { get; set; }
		public bool AcuteRenalFailure { get; set; }
		public decimal? Hematocrit { get; set; }
		/// White cells, ×10³/µL.
		public decimal? Wbc { get; set; }
		public int? Gcs { get; set; }
		public int? Age { get; set; }
		public bool ChronicHealth { get; set; }
		/// "NonOperative", "Emergency" or "Elective" (post-operative).
		public string? Admission { get; set; }
	}

	public sealed record HospitalApacheResult(int Total, int Physiology, int AgePoints, int ChronicPoints, IReadOnlyList<(string Key, int Points)> Parts, int ApproxMortalityPct);

	public sealed record HospitalBundleItem(string Code, string Ar, string En);

	// ==========================================================================================
	// HOSPITAL H238 — the intensive-care scores, infusion protocol and device bundles, free of the database.
	//   · GCS: eye 1–4, verbal 1–5, motor 1–6; 3–15. Severe ≤ 8, moderate 9–12, mild 13–15. An intubated patient's
	//     verbal is recorded as "T" and scored 1 (the total then carries the T).
	//   · APACHE II (Knaus 1985): twelve physiology variables at their worst in 24 h, (15 − GCS), age points and chronic
	//     health points; the approximate hospital mortality is the original paper's band for the score.
	//   · A titration stays inside the protocol set at the start: within min..max, and no single step larger than the step.
	//   · The bundles: VAP prevention (IHI ventilator bundle), central-line maintenance, urinary-catheter maintenance.
	// ==========================================================================================
	public static class HospitalIcuCareRules
	{
		// ------------------------------------------------------------------ GCS

		public static string? GcsRefusal(int eye, int verbal, int motor) =>
			eye is < 1 or > 4 || verbal is < 1 or > 5 || motor is < 1 or > 6 ? "Choose each part of the Glasgow scale" : null;

		public static int Gcs(int eye, int verbal, int motor) => eye + verbal + motor;

		public static string GcsBand(int total) => total <= 8 ? "Severe" : total <= 12 ? "Moderate" : "Mild";

		// ------------------------------------------------------------------ APACHE II

		public static int TempPoints(decimal t) => t >= 41 ? 4 : t >= 39 ? 3 : t >= 38.5m ? 1 : t >= 36 ? 0 : t >= 34 ? 1 : t >= 32 ? 2 : t >= 30 ? 3 : 4;
		public static int MapPoints(int m) => m >= 160 ? 4 : m >= 130 ? 3 : m >= 110 ? 2 : m >= 70 ? 0 : m >= 50 ? 2 : 4;
		public static int HrPoints(int h) => h >= 180 ? 4 : h >= 140 ? 3 : h >= 110 ? 2 : h >= 70 ? 0 : h >= 55 ? 2 : h >= 40 ? 3 : 4;
		public static int RrPoints(int r) => r >= 50 ? 4 : r >= 35 ? 3 : r >= 25 ? 1 : r >= 12 ? 0 : r >= 10 ? 1 : r >= 6 ? 2 : 4;
		public static int OxygenPoints(decimal? fio2, int? pao2, int? aa)
		{
			if (fio2 is decimal f && f >= 0.5m) return aa is int g ? (g >= 500 ? 4 : g >= 350 ? 3 : g >= 200 ? 2 : 0) : 0;
			return pao2 is int p ? (p > 70 ? 0 : p >= 61 ? 1 : p >= 55 ? 3 : 4) : 0;
		}
		public static int PhPoints(decimal p) => p >= 7.7m ? 4 : p >= 7.6m ? 3 : p >= 7.5m ? 1 : p >= 7.33m ? 0 : p >= 7.25m ? 2 : p >= 7.15m ? 3 : 4;
		public static int NaPoints(int n) => n >= 180 ? 4 : n >= 160 ? 3 : n >= 155 ? 2 : n >= 150 ? 1 : n >= 130 ? 0 : n >= 120 ? 2 : n >= 111 ? 3 : 4;
		public static int KPoints(decimal k) => k >= 7 ? 4 : k >= 6 ? 3 : k >= 5.5m ? 1 : k >= 3.5m ? 0 : k >= 3 ? 1 : k >= 2.5m ? 2 : 4;
		public static int CreatininePoints(decimal c, bool arf) => (c >= 3.5m ? 4 : c >= 2 ? 3 : c >= 1.5m ? 2 : c >= 0.6m ? 0 : 2) * (arf ? 2 : 1);
		public static int HctPoints(decimal h) => h >= 60 ? 4 : h >= 50 ? 2 : h >= 46 ? 1 : h >= 30 ? 0 : h >= 20 ? 2 : 4;
		public static int WbcPoints(decimal w) => w >= 40 ? 4 : w >= 20 ? 2 : w >= 15 ? 1 : w >= 3 ? 0 : w >= 1 ? 2 : 4;
		public static int AgePoints(int a) => a >= 75 ? 6 : a >= 65 ? 5 : a >= 55 ? 3 : a >= 45 ? 2 : 0;
		public static int ChronicPoints(bool chronic, string? admission) => !chronic ? 0 : admission == "Elective" ? 2 : 5;

		/// The original paper's approximate hospital mortality for a score band (non-operative and post-operative together).
		public static int ApproxMortality(int score) => score switch { <= 4 => 4, <= 9 => 8, <= 14 => 15, <= 19 => 25, <= 24 => 40, <= 29 => 55, <= 34 => 75, _ => 85 };

		public static string? ApacheRefusal(HospitalApacheInput i)
		{
			if (i.Gcs is < 3 or > 15) return "GCS is 3 to 15";
			if (i.FiO2 is < 0.21m or > 1m) return "FiO2 is 0.21 to 1";
			if (i.Age is < 0 or > 120) return "Enter the age";
			if (i.Admission is not (null or "NonOperative" or "Emergency" or "Elective")) return "not_found";
			if (i.Ph is < 6.5m or > 8m || i.TempC is < 25 or > 45 || i.Sodium is < 90 or > 200 || i.Potassium is < 1 or > 10) return "A value is outside what can be measured";
			return null;
		}

		public static HospitalApacheResult Apache(HospitalApacheInput i)
		{
			var parts = new List<(string, int)>
			{
				("Temp", i.TempC is decimal t ? TempPoints(t) : 0), ("MAP", i.Map is int m ? MapPoints(m) : 0), ("HR", i.HeartRate is int h ? HrPoints(h) : 0),
				("RR", i.RespRate is int r ? RrPoints(r) : 0), ("Oxygen", OxygenPoints(i.FiO2, i.PaO2, i.AaGradient)), ("pH", i.Ph is decimal p ? PhPoints(p) : 0),
				("Na", i.Sodium is int n ? NaPoints(n) : 0), ("K", i.Potassium is decimal k ? KPoints(k) : 0), ("Creatinine", i.Creatinine is decimal c ? CreatininePoints(c, i.AcuteRenalFailure) : 0),
				("Hct", i.Hematocrit is decimal x ? HctPoints(x) : 0), ("WBC", i.Wbc is decimal w ? WbcPoints(w) : 0), ("GCS", i.Gcs is int g ? 15 - g : 0),
			};
			int phys = parts.Sum(x => x.Item2), age = i.Age is int a ? AgePoints(a) : 0, chronic = ChronicPoints(i.ChronicHealth, i.Admission);
			int total = phys + age + chronic;
			return new HospitalApacheResult(total, phys, age, chronic, parts, ApproxMortality(total));
		}

		public static string Detail(IEnumerable<(string Key, int Points)> parts) => string.Join(";", parts.Select(p => p.Key + "=" + p.Points));

		// ------------------------------------------------------------------ infusions

		public static readonly IReadOnlyList<string> DoseUnits = new[] { "mcg/kg/min", "mcg/min", "mg/h", "units/h", "units/kg/h", "mg/kg/h" };

		public static string? ProtocolRefusal(string? drug, string? unit, decimal min, decimal max, decimal step, decimal dose, string? target)
		{
			if (string.IsNullOrWhiteSpace(drug) || drug.Trim().Length > 100) return "Enter the drug";
			if (!DoseUnits.Contains(unit ?? "")) return "Choose the dose unit";
			if (min < 0 || max <= min) return "The maximum dose must be above the minimum";
			if (step <= 0 || step > max - min) return "The largest step is above zero and within the range";
			if (dose < min || dose > max) return "The dose is outside the protocol's range";
			return string.IsNullOrWhiteSpace(target) || target.Trim().Length > 100 ? "Write the target (for example MAP ≥ 65)" : null;
		}

		/// Why this titration is refused, or null: inside min..max, one step at most, a change, and a reason.
		public static string? TitrateRefusal(decimal current, decimal next, decimal min, decimal max, decimal step, string? reason)
		{
			if (next == current) return "The new dose is the current dose";
			if (next < min || next > max) return "The dose is outside the protocol's range";
			if (Math.Abs(next - current) > step) return "One change is at most the protocol's step";
			return string.IsNullOrWhiteSpace(reason) ? "Write why (the value against the target)" : null;
		}

		/// The pump rate in mL/h for a dose, or null when the bag's strength (or the weight, for a per-kg unit) is missing.
		public static decimal? RateMlH(decimal dose, string unit, decimal? concPerMl, decimal? weightKg)
		{
			if (concPerMl is not decimal c || c <= 0) return null;
			bool perKg = unit.Contains("/kg/");
			if (perKg && weightKg is not > 0) return null;
			decimal perHour = dose * (perKg ? weightKg!.Value : 1m) * (unit.EndsWith("/min") ? 60m : 1m);
			return Math.Round(perHour / c, 1, MidpointRounding.AwayFromZero);
		}

		// ------------------------------------------------------------------ devices and bundles

		public static readonly IReadOnlyDictionary<string, IReadOnlyList<HospitalBundleItem>> Bundles = new Dictionary<string, IReadOnlyList<HospitalBundleItem>>
		{
			[HospitalIcuDeviceKind.Ventilator] = new HospitalBundleItem[]
			{
				new("HOB", "رفع رأس السرير 30–45°", "Head of bed 30–45°"),
				new("SAT", "إيقاف التخدير اليومي", "Daily sedation interruption"),
				new("SBT", "تقييم جاهزية الفطام", "Spontaneous breathing trial assessed"),
				new("ORAL", "العناية بالفم", "Oral care"),
				new("PUD", "الوقاية من القرحة", "Peptic ulcer prophylaxis"),
				new("DVT", "الوقاية من الجلطات", "DVT prophylaxis"),
			},
			[HospitalIcuDeviceKind.CentralLine] = new HospitalBundleItem[]
			{
				new("NEED", "مراجعة الحاجة إلى القسطرة", "Need for the line reviewed"),
				new("DRESS", "الضماد سليم ومؤرخ", "Dressing clean, intact and dated"),
				new("HUB", "تعقيم المنفذ قبل الاستخدام", "Hub scrubbed before access"),
				new("CHG", "الاستحمام بالكلورهيكسيدين", "Chlorhexidine bathing"),
			},
			[HospitalIcuDeviceKind.UrinaryCatheter] = new HospitalBundleItem[]
			{
				new("NEED", "مراجعة الحاجة إلى القسطرة", "Need for the catheter reviewed"),
				new("CLOSED", "نظام تصريف مغلق", "Closed drainage kept"),
				new("BAG", "الكيس أسفل المثانة", "Bag below the bladder"),
				new("CARE", "العناية بمكان القسطرة", "Meatal care"),
			},
		};

		public static string? DeviceRefusal(string? kind, DateTime insertedWard, DateTime nowWard, string? site)
		{
			if (!HospitalIcuDeviceKind.All.Contains(kind ?? "")) return "Choose the device";
			if (insertedWard > nowWard.AddMinutes(5)) return "The insertion time has not come yet";
			if (insertedWard < nowWard.AddDays(-90)) return "The insertion is within the last 90 days";
			return (site ?? "").Length > 60 ? "The site is 60 characters at most" : null;
		}

		/// The ticked codes kept (known ones only, in the bundle's order) and whether the bundle is complete.
		public static (string Items, bool Complete) Check(string kind, IEnumerable<string>? ticked)
		{
			var items = Bundles.TryGetValue(kind, out var b) ? b : Array.Empty<HospitalBundleItem>();
			var set = new HashSet<string>(ticked ?? Array.Empty<string>());
			var kept = items.Where(x => set.Contains(x.Code)).Select(x => x.Code).ToList();
			return (string.Join(",", kept), items.Count > 0 && kept.Count == items.Count);
		}

		/// Device-days: the calendar days (ward clock) the device was in, counting the day of insertion.
		public static int DeviceDays(DateTime insertedWard, DateTime? removedWard, DateTime nowWard) =>
			Math.Max(1, ((removedWard ?? nowWard).Date - insertedWard.Date).Days + 1);
	}
}
