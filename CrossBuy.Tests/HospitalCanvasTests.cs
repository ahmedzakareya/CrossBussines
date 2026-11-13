using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H99 — the clinical canvas, with no database: the server places every mark from its coordinates (the
	// browser never names the region or the side), the finding's code comes from the kind's own list, the ink is rebuilt
	// from its numbers alone, a lesion keeps its number across visits; the two tables against their slice.
	// ==========================================================================================
	public class HospitalCanvasTests
	{
		[Fact]
		public void A_body_mark_is_placed_by_the_server()
		{
			var front = HospitalCanvasRules.BodyAt(96, 120);
			Assert.Equal(("trunk", "F"), (front!.Value.Region.Key, front.Value.View));
			var back = HospitalCanvasRules.BodyAt(330, 150);
			Assert.Equal(("trunk", "B"), (back!.Value.Region.Key, back.Value.View));
			Assert.Equal("armR", HospitalCanvasRules.BodyAt(30, 150)!.Value.Region.Key);
			// from behind the patient's right is on the viewer's right: the arm drawn on the viewer's left is the LEFT arm
			Assert.Equal(("armL", "B"), (HospitalCanvasRules.BodyAt(230 + 30, 150)!.Value.Region.Key, HospitalCanvasRules.BodyAt(230 + 30, 150)!.Value.View));
			Assert.Equal("legR", HospitalCanvasRules.BodyAt(230 + 120, 300)!.Value.Region.Key);
			Assert.Null(HospitalCanvasRules.BodyAt(5, 5));        // off the body
			Assert.Null(HospitalCanvasRules.BodyAt(215, 300));    // between the two figures

			var (marks, err, _) = HospitalCanvasRules.Place(HospitalCanvasKind.Body, new[] { new HospitalCanvasMarkInput { Finding = "Naevus", View = "B", X = 96, Y = 120, SizeMm = 5 } });
			Assert.Null(err);
			Assert.Equal(("trunk", "F"), (marks![0].Region, marks[0].Side));   // the browser said "B": the coordinates win
			Assert.Equal("A mark must sit on the body itself", HospitalCanvasRules.Place(HospitalCanvasKind.Body, new[] { new HospitalCanvasMarkInput { Finding = "Naevus", X = 5, Y = 5 } }).Error);
			Assert.Equal("Choose the finding of every mark", HospitalCanvasRules.Place(HospitalCanvasKind.Body, new[] { new HospitalCanvasMarkInput { Finding = "haem", X = 96, Y = 120 } }).Error);
			Assert.Equal("A size is between 0 and 500 mm", HospitalCanvasRules.Place(HospitalCanvasKind.Body, new[] { new HospitalCanvasMarkInput { Finding = "Ulcer", X = 96, Y = 120, SizeMm = 900 } }).Error);
			// "carried" without the mark it continues is not carried
			Assert.False(HospitalCanvasRules.Place(HospitalCanvasKind.Body, new[] { new HospitalCanvasMarkInput { Finding = "Ulcer", X = 96, Y = 120, Carried = true } }).Marks![0].Carried);
		}

		[Fact]
		public void The_fundus_finding_sets_the_colour_and_the_code()
		{
			Assert.Equal("PP@12", HospitalCanvasRules.FundusAt(150, 120));
			Assert.Equal("MP@3", HospitalCanvasRules.FundusAt(230, 150));
			Assert.Equal("FP@6", HospitalCanvasRules.FundusAt(150, 280));
			Assert.Null(HospitalCanvasRules.FundusAt(5, 5));
			var (m, err, _) = HospitalCanvasRules.Place(HospitalCanvasKind.Fundus, new[] { new HospitalCanvasMarkInput { Finding = "detach", View = "L", X = 230, Y = 150 } });
			Assert.Null(err);
			Assert.Equal(("SNOMED 42059000", "L", "MP@3"), (m![0].FindingCode, m[0].Side, m[0].Region));
			Assert.Equal("#2563EB", HospitalCanvasRules.FundusFindings.Single(f => f.Key == "detach").Color);   // detachment is blue, always
			Assert.Equal("Say which eye every mark is on", HospitalCanvasRules.Place(HospitalCanvasKind.Fundus, new[] { new HospitalCanvasMarkInput { Finding = "haem", X = 150, Y = 150 } }).Error);
		}

		[Fact]
		public void The_ink_is_rebuilt_from_its_numbers_alone()
		{
			var (ink, err) = HospitalCanvasRules.Ink("[{\"c\":\"#7239EA\",\"w\":2.5,\"p\":[[1,2,0.5],[3,4,0.6]],\"x\":\"</script><script>alert(1)</script>\"}]");
			Assert.Null(err);
			Assert.DoesNotContain("script", ink);
			Assert.Contains("\"p\":[[1,2,0.5],[3,4,0.6]]", ink);
			Assert.Contains("#7239EA", HospitalCanvasRules.Ink("[{\"c\":\"red;</style>\",\"p\":[[1,2],[3,4]]}]").Ink);   // a colour that is not #RRGGBB is replaced
			Assert.Equal("[]", HospitalCanvasRules.Ink("[{\"p\":[[1,2]]}]").Ink);   // a single point is no stroke
			Assert.Equal("The free drawing could not be read", HospitalCanvasRules.Ink("{\"p\":1}").Error);
			Assert.Equal("The free drawing could not be read", HospitalCanvasRules.Ink("[{\"p\":[[\"a\",2]]}]").Error);
			Assert.Equal("The free drawing is too large to keep", HospitalCanvasRules.Ink(new string(' ', HospitalCanvasRules.MaxInkChars + 1)).Error);
			Assert.Equal("[]", HospitalCanvasRules.Ink(null).Ink);
			Assert.Null(HospitalCanvasRules.StateRefusal("{\"a\":1}"));
			Assert.Equal("The drawing's data could not be read", HospitalCanvasRules.StateRefusal("[1]"));
		}

		[Fact]
		public void A_lesion_keeps_its_number_across_visits()
		{
			var marks = new List<HospitalCanvasMark> { new() { CarriedFromMarkId = 40 }, new(), new() { CarriedFromMarkId = 41 }, new() };
			HospitalCanvasRules.Number(marks, new Dictionary<int, int> { [40] = 2, [41] = 5 }, 6);
			Assert.Equal(new[] { 2, 7, 5, 8 }, marks.Select(m => m.LesionNo));
		}

		[Fact]
		public void The_tables_match_their_slice_and_follow_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h99_canvas.sql");
			s += Environment.NewLine + HospitalSliceParity.Slice("hospital_h111_canvas_print.sql");   // H111: the drawing's picture
			s += Environment.NewLine + HospitalSliceParity.Slice("hospital_h112_patient_pain.sql");   // H112: the patient's pain drawing
			s += Environment.NewLine + HospitalSliceParity.Slice("hospital_h113_photo_consent_measure.sql");   // H113: the photo's consent, the measure
			var m = HospitalSliceParity.Model(HospitalCanvasModel.Configure);
			foreach (var (t, n) in new (Type, string)[] { (typeof(HospitalCanvasSheet), "HospitalCanvasSheets"), (typeof(HospitalCanvasMark), "HospitalCanvasMarks") })
			{ HospitalSliceParity.NamesItsTable(m, s, t, n); HospitalSliceParity.ColumnsMatch(m, s, t, n); }
			foreach (var k in HospitalCanvasKind.All) Assert.Contains("'" + k + "'", s);
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalCanvasSheet));
			Assert.Contains(HospitalPatientMergeRules.PatientTables, t => t.Entity == typeof(HospitalCanvasMark));
		}
	}
}
