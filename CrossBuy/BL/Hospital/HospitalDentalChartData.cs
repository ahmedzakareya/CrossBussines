namespace CrossBuy.BL.Hospital
{
	/// What the shared dental tabs (Views/Shared/_HospitalDentalTabs) draw, and for whom: the dentist (`Staff`; `Edit` with
	/// `consult`) or the patient on the portal. A person's photo and an image's link differ by audience, so the page says.
	/// H31: `Pictures` are the imaging orders' pictures, `PictureSrc` the URL a picture is read through (staff and
	/// patient read through different endpoints), `ShowOrder` which orders this audience sees (a patient: released ones).
	public sealed record HospitalDentalTabsModel(HospitalDentalChartView Chart, bool Staff, bool Edit, Func<HospitalDentist, string?> Photo, Func<HospitalDentalImageRow, string?> ImageLink,
		IReadOnlyDictionary<int, IReadOnlyList<HospitalRadImageRow>>? Pictures = null, Func<int, string?>? PictureSrc = null, Func<HospitalDentalImageRow, bool>? ShowOrder = null);

	// ==========================================================================================
	// HOSPITAL — the odontogram's data, as the shared renderer (wwwroot/js/hospital/hospital-dental.js, `chart`)
	// reads it. ONE place for both screens that draw it: the dentist's chart (Hospital/Dental/Chart) and the
	// patient's dental report on the portal (H30) — so the patient sees exactly the drawing the dentist saw.
	// ==========================================================================================
	public static class HospitalDentalChartData
	{
		/// The renderer's word for a mark: cond / plan / done / exist.
		public static string MarkKey(string status) => status switch
		{
			Models.Context.Hospital.HospitalDentalMark.Condition => "cond",
			Models.Context.Hospital.HospitalDentalMark.Planned => "plan",
			Models.Context.Hospital.HospitalDentalMark.Completed => "done",
			_ => "exist",
		};

		/// The renderer's word for a whole-tooth work: crown / rct / implant / extract.
		public static string WorkKey(string work) => work switch
		{
			Models.Context.Hospital.HospitalToothWork.Crown => "crown",
			Models.Context.Hospital.HospitalToothWork.RootCanal => "rct",
			Models.Context.Hospital.HospitalToothWork.Implant => "implant",
			_ => "extract",
		};

		/// The perio exam per tooth as the renderer's perioArch reads it: [six depths, six recessions, six bleeding flags].
		public static Dictionary<string, object> Perio(HospitalDentalChartView chart) =>
			chart.Perio?.Teeth.ToDictionary(t => t.Tooth.ToString(), t => (object)new[]
			{
				HospitalDentalRules.Sites(t.Depths, 0, 15) ?? new int[6], HospitalDentalRules.Sites(t.Recession, -5, 15) ?? new int[6],
				(HospitalDentalRules.Bleeding(t.Bleeding) ?? new bool[6]).Select(b => b ? 1 : 0).ToArray(),
			}) ?? new Dictionary<string, object>();

		/// Every charted tooth: its surfaces' marks, its whole-tooth works, missing / impacted, and the plan codes on it.
		public static Dictionary<string, object> Teeth(HospitalDentalChartView chart) => chart.Teeth.ToDictionary(k => k.Key.ToString(), v => (object)new
		{
			s = v.Value.Surfaces.ToDictionary(x => x.Key, x => MarkKey(x.Value)),
			tx = v.Value.Work.Select(w => new { k = WorkKey(w.Work), s = MarkKey(w.Mark) }),
			missing = v.Value.Missing, impacted = v.Value.Impacted,
			plan = v.Value.Planned,
		});
	}
}
