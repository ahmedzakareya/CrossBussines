using CrossBuy.BL.Hospital;
using CrossBuy.BL.Reporting;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H113 — a clinical photo under the patient's consent and its scope, and a calibrated measure on the photo
	// (the prototype's canvas layer 4), with no database: the size from a ruler in the picture and its margin, the
	// refusals that keep a measure honest, a measure that survives a save only while it is still the mark's size, the
	// consent's scope, and the new columns against their slice.
	// ==========================================================================================
	public class HospitalCanvasPhotoTests
	{
		// a ruler of 10 mm across 200 px: 0.05 mm per pixel
		private static HospitalPhotoMeasureInput Ok() => new()
		{
			Ruler = new[] { 100m, 100m, 300m, 100m }, RulerMm = 10,
			Length = new[] { 0m, 0m, 120m, 160m },   // 200 px
			Width = new[] { 0m, 0m, 0m, 100m },       // 100 px
		};

		[Fact]
		public void The_size_comes_from_the_ruler_in_the_picture()
		{
			var (r, err) = HospitalPhotoMeasureRules.Measure(Ok());
			Assert.Null(err);
			Assert.Equal(10m, r!.LengthMm);
			Assert.Equal(5m, r.WidthMm);
			Assert.Equal(0.05m, r.MmPerPx);
			Assert.Equal(0.2m, r.PlusMinusMm);   // 2 px × 0.05 + 10 mm × 2 / 200 px
			var noWidth = Ok(); noWidth.Width = null;
			Assert.Null(HospitalPhotoMeasureRules.Measure(noWidth).Result!.WidthMm);
			// a longer stretch of ruler: a smaller margin for the same lesion
			var far = Ok(); far.Ruler = new[] { 0m, 0m, 1000m, 0m }; far.RulerMm = 50;
			Assert.True(HospitalPhotoMeasureRules.Measure(far).Result!.PlusMinusMm < 0.2m);
		}

		[Fact]
		public void A_measure_is_refused_when_it_cannot_be_trusted()
		{
			HospitalPhotoMeasureInput With(Action<HospitalPhotoMeasureInput> f) { var i = Ok(); f(i); return i; }
			string? E(Action<HospitalPhotoMeasureInput> f) => HospitalPhotoMeasureRules.Measure(With(f)).Error;
			Assert.Equal("Give the ruler's length in millimetres (1 to 300)", E(i => i.RulerMm = null));
			Assert.Equal("Give the ruler's length in millimetres (1 to 300)", E(i => i.RulerMm = 0.5m));
			Assert.Equal("Give the ruler's length in millimetres (1 to 300)", E(i => i.RulerMm = 301));
			Assert.Equal("Mark the two ends of the ruler's length on the photo", E(i => i.Ruler = new[] { 1m, 2m, 3m }));
			Assert.Equal("Mark the two ends of the ruler's length on the photo", E(i => i.Ruler = new[] { -1m, 0m, 100m, 0m }));
			Assert.Equal("Mark a longer stretch of the ruler: at least 50 pixels on the photo", E(i => i.Ruler = new[] { 0m, 0m, 49m, 0m }));
			Assert.Equal("Mark the two ends of the lesion's length", E(i => i.Length = null));
			Assert.Equal("Mark the two ends of the lesion's length", E(i => i.Length = new[] { 5m, 5m, 5m, 5m }));
			Assert.Equal("Mark the two ends of the lesion's width", E(i => i.Width = new[] { 5m, 5m, 6m, 5m }));
			Assert.Equal("The width is across the length: it cannot be longer than the length", E(i => i.Width = new[] { 0m, 0m, 0m, 300m }));
			Assert.Equal("The measured length is out of range: check the ruler", E(i => { i.RulerMm = 300; i.Ruler = new[] { 0m, 0m, 50m, 0m }; i.Length = new[] { 0m, 0m, 0m, 1000m }; i.Width = null; }));
		}

		[Fact]
		public void A_stored_measure_is_read_again_from_its_points()
		{
			var i = Ok(); var r = HospitalPhotoMeasureRules.Measure(i).Result!;
			var json = HospitalPhotoMeasureRules.Store(i, r);
			Assert.Equal(r, HospitalPhotoMeasureRules.Read(json));
			// a tampered stored result is ignored: the points decide
			var forged = json.Replace("\"lengthMm\":10", "\"lengthMm\":3");
			Assert.Equal(10m, HospitalPhotoMeasureRules.Read(forged)!.LengthMm);
			Assert.Null(HospitalPhotoMeasureRules.Read("{oops"));
			Assert.Null(HospitalPhotoMeasureRules.Read(null));
			// a saved-again mark keeps its measure only while its size is still the measured one
			Assert.Equal(json, HospitalPhotoMeasureRules.KeepIfSame(json, 10m));
			Assert.Null(HospitalPhotoMeasureRules.KeepIfSame(json, 12m));
			Assert.Null(HospitalPhotoMeasureRules.KeepIfSame(json, null));
		}

		[Fact]
		public void A_wider_scope_covers_the_narrower()
		{
			Assert.True(HospitalConsentPhotoScope.Covers(HospitalConsentPhotoScope.Record, HospitalConsentPhotoScope.Record));
			Assert.True(HospitalConsentPhotoScope.Covers(HospitalConsentPhotoScope.Publication, HospitalConsentPhotoScope.Teaching));
			Assert.False(HospitalConsentPhotoScope.Covers(HospitalConsentPhotoScope.Record, HospitalConsentPhotoScope.Teaching));
			Assert.False(HospitalConsentPhotoScope.Covers(null, HospitalConsentPhotoScope.Record));
			Assert.False(HospitalConsentPhotoScope.Covers("Everything", HospitalConsentPhotoScope.Record));
		}

		[Fact]
		public void A_photography_consent_names_its_scope()
		{
			var png = "data:image/png;base64," + Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 });
			var form = new HospitalConsentForm { ID = 1, Kind = HospitalConsentKind.Photography, IsActive = true, Body = "x", BodyEn = "x", Title = "t", TitleEn = "t" };
			HospitalConsentInput I(string decision, string? scope) => new() { FormId = 1, Language = "ar", Decision = decision, SignerKind = HospitalConsentSigner.Patient, WitnessEmployeeId = 9, Signature = png, PhotoScope = scope };
			Assert.Equal("Choose what the photographs may be used for", HospitalConsentRules.SignRefusal(I(HospitalConsentDecision.Given, null), form, 40, false, 7));
			Assert.Equal("Choose what the photographs may be used for", HospitalConsentRules.SignRefusal(I(HospitalConsentDecision.Given, "Everything"), form, 40, false, 7));
			Assert.Null(HospitalConsentRules.SignRefusal(I(HospitalConsentDecision.Given, HospitalConsentPhotoScope.Teaching), form, 40, false, 7));
			Assert.Null(HospitalConsentRules.SignRefusal(I(HospitalConsentDecision.Refused, null), form, 40, false, 7));   // a refusal has no scope
		}

		[Fact]
		public void The_paper_says_the_size_was_measured_on_a_photo()
		{
			var i = Ok(); var json = HospitalPhotoMeasureRules.Store(i, HospitalPhotoMeasureRules.Measure(i).Result!);
			var m = new HospitalCanvasMark { LesionNo = 3, Finding = "Naevus", Region = "trunk", Side = "F", SizeMm = 10m, MeasureJson = json };
			Assert.Contains("10 × 5 mm ±0.2", HospitalCanvasPaper.Findings(HospitalCanvasKind.Body, new[] { m }, false));
			Assert.Contains("(calibrated photo)", HospitalCanvasPaper.Findings(HospitalCanvasKind.Body, new[] { m }, false));
			m.SizeMm = 12m;   // typed over by hand: the photo's measure no longer speaks for it
			Assert.DoesNotContain("calibrated", HospitalCanvasPaper.Findings(HospitalCanvasKind.Body, new[] { m }, false));
		}

		/// Found live: three marks tapped on one spot over three saves all took lesion 9. An earlier mark is continued once.
		[Fact]
		public void Two_new_marks_on_one_spot_are_two_lesions()
		{
			var old = new[] { new HospitalCanvasMark { ID = 1, LesionNo = 9, Finding = "Naevus", X = 85, Y = 160 } };
			var a = new HospitalCanvasMark { Finding = "Naevus", X = 85, Y = 160 };
			var b = new HospitalCanvasMark { Finding = "Naevus", X = 85, Y = 160 };
			var c = new HospitalCanvasMark { Finding = "Papule", X = 85, Y = 160 };   // another finding is another lesion
			var map = HospitalCanvasRules.SameAsBefore(new[] { a, b, c }, old);
			Assert.Single(map);
			Assert.Same(old[0], map[a]);
			Assert.False(map.ContainsKey(b));
			// a carried mark is matched by what it carries, never by its spot
			var carried = new HospitalCanvasMark { Finding = "Naevus", X = 85, Y = 160, CarriedFromMarkId = 77 };
			Assert.Empty(HospitalCanvasRules.SameAsBefore(new[] { carried }, old));
		}

		/// Found live: a fixed box on the paper cut the marks off after four lines. Each mark is now its own row.
		[Fact]
		public void Every_mark_is_its_own_row_on_the_paper()
		{
			var marks = Enumerable.Range(1, 9).Select(i => new HospitalCanvasMark { LesionNo = i, Finding = "Naevus", Region = "trunk", Side = "F", SizeMm = i }).ToList();
			var lines = HospitalCanvasPaper.MarkLines(HospitalCanvasKind.Body, marks, true);
			Assert.Equal(9, lines.Count);
			Assert.StartsWith("9 · ", lines[8]);
			Assert.Equal(string.Join("\n", lines), HospitalCanvasPaper.Findings(HospitalCanvasKind.Body, marks, true));
			Assert.Empty(HospitalCanvasPaper.MarkLines(HospitalCanvasKind.Body, Array.Empty<HospitalCanvasMark>(), true));
			var d = HospitalDocumentDatasets.CanvasDrawing();
			Assert.Contains(d.Fields, f => f.Key == "Mark");
			Assert.Contains(d.Fields, f => f.Key == "LineNo");
		}

		[Fact]
		public void The_columns_match_their_slice()
		{
			var s = HospitalSliceParity.Slice("hospital_h113_photo_consent_measure.sql");
			Assert.Contains("ALTER TABLE dbo.HospitalConsents ADD PhotoScope nvarchar(12) NULL", s);
			Assert.Contains("ALTER TABLE dbo.HospitalCanvasMarks ADD PhotoConsentId int NULL", s);
			Assert.Contains("ALTER TABLE dbo.HospitalCanvasMarks ADD MeasureJson nvarchar(1000) NULL", s);
			foreach (var x in HospitalConsentPhotoScope.All) Assert.Contains("'" + x + "'", s);
			var consent = HospitalSliceParity.Model(HospitalConsentModel.Configure).FindEntityType(typeof(HospitalConsent))!;
			Assert.Equal(12, consent.FindProperty(nameof(HospitalConsent.PhotoScope))!.GetMaxLength());
			Assert.All(HospitalConsentPhotoScope.All, x => Assert.True(x.Length <= 12));
			Assert.Equal(new[] { "HospitalConsents" }, HospitalSliceParity.CheckedPrerequisites(s));
		}
	}
}
