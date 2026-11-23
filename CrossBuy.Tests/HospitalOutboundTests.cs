using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H247 — outbound HL7 feeds. What must never happen: a separator inside a value breaking the message; a
	// discharge sent as an admission; a result without its accession, value or flag; an ACK misread; an enabled feed
	// with nowhere to send; a model naming a table its slice does not create.
	// ==========================================================================================
	public class HospitalOutboundTests
	{
		private static readonly HospitalPatient P = new() { ID = 25, Mrn = "MRN-25", Name = "مريض", NameEn = "Sara Ali", Sex = HospitalPatientSex.Female, BirthDate = new DateTime(1990, 3, 4), NationalId = "290030412345" };
		private static readonly DateTime Now = new(2026, 11, 23, 9, 30, 0);

		[Fact]
		public void Text_is_escaped_so_a_value_never_breaks_a_field()
		{
			Assert.Equal("A\\F\\B\\S\\C\\R\\D\\T\\E\\E\\", HospitalHl7Out.Esc("A|B^C~D&E\\"));
			Assert.Equal("line one line two", HospitalHl7Out.Esc("line one\rline two"));
			Assert.Equal("", HospitalHl7Out.Esc(null));
		}

		[Fact]
		public void An_admission_message_carries_the_patient_and_the_stay()
		{
			var v = new HospitalHl7Visit("ADM-11", "W1", "201", "201-A", null, new DateTime(2026, 11, 20, 8, 0, 0), null);
			var m = HospitalHl7Out.Adt("A01", "F1-A01-adm-11", P, v, "HIS", "MOH", "CB1", new DateTime(2026, 11, 20, 8, 0, 0), Now);
			var seg = m.TrimEnd('\r').Split('\r');
			Assert.Equal(4, seg.Length);
			Assert.StartsWith("MSH|^~\\&|CROSSBUY|CB1|HIS|MOH|20261123093000||ADT^A01^ADT_A01|F1-A01-adm-11|P|2.5.1", seg[0]);
			Assert.Equal("EVN|A01|20261120080000", seg[1]);
			Assert.Equal("PID|1||MRN-25^^^CROSSBUY^MR~290030412345^^^KW^NI||Ali^Sara||19900304|F", seg[2]);
			var pv1 = seg[3].Split('|');
			Assert.Equal("I", pv1[2]); Assert.Equal("W1^201^201-A", pv1[3]); Assert.Equal("ADM-11", pv1[19]); Assert.Equal("20261120080000", pv1[44]);
			Assert.Contains("ADT^A03^ADT_A03", HospitalHl7Out.Adt("A03", "x", P, v with { DischargedAt = Now }, null, null, "CB1", Now, Now));
		}

		[Fact]
		public void A_result_message_carries_accession_values_ranges_and_flags()
		{
			var res = new[]
			{
				new HospitalHl7Result("K", "Potassium", "6.2", "NM", "mmol/L", HospitalHl7Out.Range(3.5m, 5.1m), HospitalHl7Out.Flag(HospitalLabFlag.CriticalHigh)),
				new HospitalHl7Result("COM", "Comment", "Haemolysed", "ST", null, null, null),
			};
			var m = HospitalHl7Out.Oru("C1", P, "LAB-000040", "BMP", "Basic panel", new DateTime(2026, 11, 23, 7, 0, 0), new DateTime(2026, 11, 23, 9, 0, 0), res, null, null, "CB1", Now);
			var seg = m.TrimEnd('\r').Split('\r');
			Assert.Equal(5, seg.Length);
			Assert.StartsWith("OBR|1|LAB-000040|LAB-000040|BMP^Basic panel|||20261123070000", seg[2]);
			Assert.EndsWith("|20261123090000|||F", seg[2]);
			Assert.Equal("OBX|1|NM|K^Potassium||6.2|mmol/L|3.5-5.1|HH|||F", seg[3]);
			Assert.Equal("OBX|2|ST|COM^Comment||Haemolysed||||||F", seg[4]);
			Assert.Equal("<10", HospitalHl7Out.Range(null, 10m));
			Assert.Null(HospitalHl7Out.Flag(HospitalLabFlag.Normal));
		}

		[Fact]
		public void Framing_and_acknowledgements()
		{
			var f = HospitalHl7Out.Frame("MSH|x\r");
			Assert.Equal(0x0B, f[0]); Assert.Equal(0x1C, f[^2]); Assert.Equal(0x0D, f[^1]);
			Assert.Equal("AA", HospitalHl7Out.AckCode("\vMSH|^~\\&|R|R|||20261123||ACK|9|P|2.5.1\rMSA|AA|C1\r\u001c\r"));
			Assert.Equal("AE", HospitalHl7Out.AckCode("MSH|x\nMSA|AE|C1|bad"));
			Assert.Null(HospitalHl7Out.AckCode("garbage"));
		}

		[Fact]
		public void An_enabled_feed_needs_somewhere_to_send()
		{
			Assert.Null(HospitalHl7Out.FeedRefusal("DWH", "Warehouse", HospitalFeedKind.Adt, null, null, false));
			Assert.NotNull(HospitalHl7Out.FeedRefusal("DWH", "Warehouse", HospitalFeedKind.Adt, null, null, true));
			Assert.NotNull(HospitalHl7Out.FeedRefusal("DWH", "Warehouse", HospitalFeedKind.Adt, "10.0.0.5", 70000, true));
			Assert.Null(HospitalHl7Out.FeedRefusal("DWH", "Warehouse", HospitalFeedKind.Oru, "10.0.0.5", 2575, true));
			Assert.NotNull(HospitalHl7Out.FeedRefusal("DWH", "Warehouse", "XML", null, null, false));
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalOutboundModel.Configure);
		private static string Slice() => HospitalSliceParity.Slice("hospital_h247_outbound_feeds.sql");

		[Theory]
		[InlineData(typeof(HospitalOutboundFeed), "HospitalOutboundFeeds")]
		[InlineData(typeof(HospitalOutboundMessage), "HospitalOutboundMessages")]
		public void Every_column_is_read_in_the_type_its_slice_creates(Type entity, string table)
		{
			HospitalSliceParity.NamesItsTable(Model(), Slice(), entity, table);
			HospitalSliceParity.ColumnsMatch(Model(), Slice(), entity, table);
		}
	}
}
