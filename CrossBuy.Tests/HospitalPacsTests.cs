using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H199 — the imaging archive (PACS). What must never happen: a viewer link that opens another patient's
	// study (the accession number pasted raw, so "&" or "#" in it changes the address), an address that is not a web
	// address, or an order message whose delimiters are broken by a name — the worklist would show the wrong patient.
	// ==========================================================================================
	public class HospitalPacsTests
	{
		[Fact]
		public void The_viewer_address_must_be_a_web_address_naming_the_study()
		{
			Assert.Null(HospitalPacsRules.CheckViewer(null));   // empty disconnects
			Assert.Null(HospitalPacsRules.CheckViewer("https://pacs.local/viewer?AccessionNumber={accession}&PatientID={mrn}"));
			Assert.Equal("The viewer address must name the study by {0}", HospitalPacsRules.CheckViewer("https://pacs.local/viewer?PatientID={mrn}"));
			Assert.Equal("The viewer address names an unknown placeholder", HospitalPacsRules.CheckViewer("https://pacs.local/v?a={accession}&s={study}"));
			Assert.Equal("The viewer address must be a web address (http or https)", HospitalPacsRules.CheckViewer("javascript:alert({accession})"));
			Assert.Equal("The viewer address must be a web address (http or https)", HospitalPacsRules.CheckViewer("file:///c:/{accession}"));
			Assert.Equal("The viewer address is too long", HospitalPacsRules.CheckViewer("https://p/" + new string('a', 500) + "{accession}"));
			Assert.Equal("The receiving system's name may not hold HL7 delimiters", HospitalPacsRules.CheckReceiver("PACS|X"));
			Assert.Null(HospitalPacsRules.CheckReceiver("ORTHANC"));
		}

		[Fact]
		public void The_link_encodes_each_value()
		{
			Assert.Null(HospitalPacsRules.ViewerLink(null, "RAD-000001", "MRN-1"));
			Assert.Equal("https://pacs.local/v?acc=RAD-000001&pid=MRN-100023",
				HospitalPacsRules.ViewerLink("https://pacs.local/v?acc={accession}&pid={mrn}", "RAD-000001", "MRN-100023"));
			Assert.Equal("https://pacs.local/v?acc=A%26B%23C", HospitalPacsRules.ViewerLink("https://pacs.local/v?acc={accession}", "A&B#C", null));
		}

		[Fact]
		public void The_order_message_carries_the_accession_patient_and_modality()
		{
			var o = new HospitalRadOrder { AccessionNo = "RAD-000042", Code = "CT-HEAD", Name = "أشعة مقطعية للرأس", NameEn = "CT head", Modality = HospitalRadModality.CT, Priority = HospitalLabPriority.Stat, OrderedAt = new DateTime(2026, 11, 18, 9, 0, 0) };
			var p = new HospitalPatient { Mrn = "MRN-100023", Name = "مريض تجريبي", NameEn = "Test O|Brien^Jr", Sex = HospitalPatientSex.Female, BirthDate = new DateTime(1990, 5, 1) };
			var m = HospitalPacsRules.Orm(o, p, "ORTHANC", new DateTime(2026, 11, 18, 9, 5, 0));
			var seg = m.TrimEnd('\r').Split('\r');
			Assert.Equal(new[] { "MSH", "PID", "PV1", "ORC", "OBR" }, seg.Select(s => s[..3]));
			var msh = seg[0].Split('|');
			Assert.Equal(("ORTHANC", "ORM^O01^ORM_O01", "2.5", "UNICODE UTF-8"), (msh[4], msh[8], msh[11], msh[17]));
			var pid = seg[1].Split('|');
			Assert.Equal(("MRN-100023^^^HOSP^MR", "O\\F\\Brien\\S\\Jr^Test", "19900501", "F"), (pid[3], pid[5], pid[7], pid[8]));
			var orc = seg[3].Split('|');
			Assert.Equal(("NW", "RAD-000042", "RAD-000042"), (orc[1], orc[2], orc[3]));
			var obr = seg[4].Split('|');
			Assert.Equal(28, obr.Length);
			Assert.Equal(("RAD-000042", "CT-HEAD^CT head^L", "RAD-000042", "CT", "^^^^^S"), (obr[2], obr[4], obr[18], obr[24], obr[27]));
			// the message parses as the same HL7 our analyser reader reads: five segments, no stray delimiter from a name
			Assert.DoesNotContain("\n", m);
		}

		[Fact]
		public void Text_fields_escape_the_delimiters()
		{
			Assert.Equal("a\\F\\b\\S\\c\\T\\d\\R\\e\\E\\f g", HospitalPacsRules.Esc("a|b^c&d~e\\f\ng"));
			Assert.Equal(("R", "A", "S"), (HospitalPacsRules.Priority(HospitalLabPriority.Routine), HospitalPacsRules.Priority(HospitalLabPriority.Urgent), HospitalPacsRules.Priority(HospitalLabPriority.Stat)));
			Assert.Equal("Single", HospitalPacsRules.PersonName("Single"));
		}

		[Fact]
		public void The_table_matches_its_slice()
		{
			var model = HospitalSliceParity.Model(HospitalPacsModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h199_pacs.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalPacsSettings), "HospitalPacsSettings");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalPacsSettings), "HospitalPacsSettings");
		}
	}
}
