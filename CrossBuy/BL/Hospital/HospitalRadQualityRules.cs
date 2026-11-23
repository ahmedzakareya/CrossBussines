using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalRadCategory(string System, string Code, string En, string Ar, int? FollowUpMonths, string? Recommendation);

	// ==========================================================================================
	// HOSPITAL H243 — the radiology quality rules, free of the database.
	//   · CATEGORIES and the follow-up each calls for, as the ACR systems publish them (summarised):
	//       BI-RADS 0 recall (1 month), 3 short-interval follow-up (6 months), 4/5 biopsy (1 month), 6 known cancer;
	//       Lung-RADS 2 annual (12), 3 in 6 months, 4A in 3 months, 4B/4X tissue sampling or PET (1);
	//       LI-RADS 3 repeat in 3–6 months (6), 4 in ≤3 months (3), 5/M/TIV multidisciplinary (1);
	//       TI-RADS 3 follow-up (12), 4 (12), 5 FNA if ≥1 cm (1); PI-RADS 3 consider biopsy (3), 4/5 biopsy (1).
	//     A category with no follow-up records the category only.
	//   · EFFECTIVE DOSE (mSv) = DLP × the adult region factor k (ICRP 103 / AAPM 96 values):
	//       head 0.0021, neck 0.0059, chest 0.014, abdomen-pelvis 0.015, chest-abdomen-pelvis 0.015, spine 0.015.
	//   · Above the REFERENCE LEVEL when CTDIvol or DLP passes the region's typical diagnostic reference level
	//     (adult, commonly cited values: head 60 mGy / 1000 mGy·cm; chest 15 / 600; abdomen-pelvis 15 / 900).
	// ==========================================================================================
	public static class HospitalRadQualityRules
	{
		public static readonly IReadOnlyList<HospitalRadCategory> Categories = new HospitalRadCategory[]
		{
			new("BI-RADS", "0", "Incomplete — needs more imaging", "غير مكتمل — يحتاج تصويرًا إضافيًا", 1, "Additional views or ultrasound"),
			new("BI-RADS", "1", "Negative", "سلبي", null, null),
			new("BI-RADS", "2", "Benign", "حميد", null, null),
			new("BI-RADS", "3", "Probably benign", "حميد غالبًا", 6, "Short-interval follow-up in 6 months"),
			new("BI-RADS", "4", "Suspicious", "مشتبه", 1, "Tissue diagnosis (biopsy)"),
			new("BI-RADS", "5", "Highly suggestive of malignancy", "مرجّح للخباثة بشدة", 1, "Tissue diagnosis (biopsy)"),
			new("BI-RADS", "6", "Known biopsy-proven malignancy", "خباثة مثبتة بالخزعة", null, null),
			new("Lung-RADS", "1", "Negative", "سلبي", 12, "Annual low-dose CT"),
			new("Lung-RADS", "2", "Benign appearance", "مظهر حميد", 12, "Annual low-dose CT"),
			new("Lung-RADS", "3", "Probably benign", "حميد غالبًا", 6, "Low-dose CT in 6 months"),
			new("Lung-RADS", "4A", "Suspicious", "مشتبه", 3, "Low-dose CT in 3 months; PET/CT if a solid part ≥ 8 mm"),
			new("Lung-RADS", "4B", "Very suspicious", "مشتبه جدًا", 1, "Diagnostic CT, PET/CT or tissue sampling"),
			new("Lung-RADS", "4X", "Very suspicious with added features", "مشتبه جدًا مع علامات إضافية", 1, "Diagnostic CT, PET/CT or tissue sampling"),
			new("LI-RADS", "LR-1", "Definitely benign", "حميد قطعًا", null, null),
			new("LI-RADS", "LR-2", "Probably benign", "حميد غالبًا", null, null),
			new("LI-RADS", "LR-3", "Intermediate probability", "احتمال متوسط", 6, "Repeat or alternative imaging in 3–6 months"),
			new("LI-RADS", "LR-4", "Probably HCC", "سرطان كبد غالبًا", 3, "Multidisciplinary discussion; imaging in ≤ 3 months"),
			new("LI-RADS", "LR-5", "Definitely HCC", "سرطان كبد قطعًا", 1, "Multidisciplinary discussion"),
			new("LI-RADS", "LR-M", "Malignant, not HCC-specific", "خبيث غير نوعي", 1, "Multidisciplinary discussion; consider biopsy"),
			new("LI-RADS", "LR-TIV", "Tumour in vein", "ورم في الوريد", 1, "Multidisciplinary discussion"),
			new("TI-RADS", "TR1", "Benign", "حميد", null, null),
			new("TI-RADS", "TR2", "Not suspicious", "غير مشتبه", null, null),
			new("TI-RADS", "TR3", "Mildly suspicious", "مشتبه بدرجة خفيفة", 12, "Follow-up if ≥ 1.5 cm; FNA if ≥ 2.5 cm"),
			new("TI-RADS", "TR4", "Moderately suspicious", "مشتبه بدرجة متوسطة", 12, "Follow-up if ≥ 1 cm; FNA if ≥ 1.5 cm"),
			new("TI-RADS", "TR5", "Highly suspicious", "مشتبه بدرجة عالية", 1, "FNA if ≥ 1 cm; follow-up if ≥ 0.5 cm"),
			new("PI-RADS", "1", "Very low", "منخفض جدًا", null, null),
			new("PI-RADS", "2", "Low", "منخفض", null, null),
			new("PI-RADS", "3", "Intermediate", "متوسط", 3, "Consider biopsy (PSA density)"),
			new("PI-RADS", "4", "High", "مرتفع", 1, "Targeted biopsy"),
			new("PI-RADS", "5", "Very high", "مرتفع جدًا", 1, "Targeted biopsy"),
		};

		public static IReadOnlyList<string> Systems => Categories.Select(c => c.System).Distinct().ToList();
		public static HospitalRadCategory? Find(string? system, string? code) => Categories.FirstOrDefault(c => c.System == system && c.Code == code);

		public static DateTime? DueDate(HospitalRadCategory c, DateTime reportDay) => c.FollowUpMonths is int m ? reportDay.Date.AddMonths(m) : null;
		public static bool Overdue(HospitalRadFollowUp f, DateTime today) => f.Status == HospitalRadFollowUpStatus.Open && f.DueDate is DateTime d && today.Date > d.Date;

		public static readonly IReadOnlyDictionary<string, (decimal K, decimal? CtdiRef, decimal? DlpRef)> Regions = new Dictionary<string, (decimal, decimal?, decimal?)>
		{
			["Head"] = (0.0021m, 60m, 1000m), ["Neck"] = (0.0059m, null, null), ["Chest"] = (0.014m, 15m, 600m),
			["AbdPelvis"] = (0.015m, 15m, 900m), ["ChestAbdPelvis"] = (0.015m, 15m, 1200m), ["Spine"] = (0.015m, null, null), ["Other"] = (0m, null, null),
		};

		public static decimal? EffectiveMsv(string region, decimal? dlp) =>
			dlp is decimal d && Regions.TryGetValue(region, out var r) && r.K > 0 ? Math.Round(d * r.K, 2, MidpointRounding.AwayFromZero) : null;

		public static bool AboveReference(string region, decimal? ctdi, decimal? dlp) =>
			Regions.TryGetValue(region, out var r) && ((ctdi is decimal c && r.CtdiRef is decimal cr && c > cr) || (dlp is decimal d && r.DlpRef is decimal dr && d > dr));

		public static string? DoseRefusal(string modality, string? region, decimal? ctdi, decimal? dlp, decimal? dap)
		{
			if (!Regions.ContainsKey(region ?? "")) return "Choose the body region";
			if (ctdi is < 0 or > 500 || dlp is < 0 or > 20000 || dap is < 0 or > 100000) return "A value is outside what can be measured";
			if (modality == HospitalRadModality.CT && dlp == null) return "A CT dose needs its DLP";
			if (modality is HospitalRadModality.MRI or HospitalRadModality.Ultrasound) return "This exam uses no ionising radiation";
			return ctdi == null && dlp == null && dap == null ? "Enter at least one dose value" : null;
		}
	}
}
