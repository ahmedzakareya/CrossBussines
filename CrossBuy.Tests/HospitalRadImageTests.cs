using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H31 — an imaging order's pictures, with no database: what the bytes really are (never the browser's
	// word), when a picture may be attached or removed, the shared dental chart's words; and the model against its slice.
	// ==========================================================================================
	public class HospitalRadImageTests
	{
		private static byte[] B(params int[] v) => v.Select(x => (byte)x).ToArray();

		[Fact]
		public void The_type_is_read_from_the_bytes_signature()
		{
			Assert.Equal("image/jpeg", HospitalRadImageRules.Sniff(B(0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10)));
			Assert.Equal("image/png", HospitalRadImageRules.Sniff(B(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0)));
			Assert.Equal("image/webp", HospitalRadImageRules.Sniff("RIFF\0\0\0\0WEBPVP8 "u8.ToArray()));
		}

		[Fact]
		public void Anything_else_is_not_an_image_whatever_its_name_says()
		{
			Assert.Null(HospitalRadImageRules.Sniff("GIF89a......"u8.ToArray()));
			Assert.Null(HospitalRadImageRules.Sniff("%PDF-1.7 ...."u8.ToArray()));
			Assert.Null(HospitalRadImageRules.Sniff("<svg xmlns="u8.ToArray()));            // a script-capable image is refused
			Assert.Null(HospitalRadImageRules.Sniff("RIFF\0\0\0\0WAVEfmt "u8.ToArray()));   // RIFF, but not WebP
			Assert.Null(HospitalRadImageRules.Sniff(B(0xFF, 0xD8)));                        // too short to be sure
			Assert.Null(HospitalRadImageRules.Sniff(Array.Empty<byte>()));
		}

		[Theory]
		[InlineData(HospitalRadStatus.Scanning, 1000, "image/jpeg", null)]
		[InlineData(HospitalRadStatus.Reporting, HospitalRadImageRules.MaxBytes, "image/png", null)]
		[InlineData(HospitalRadStatus.Signed, 1000, "image/jpeg", "A signed report's images are locked")]
		[InlineData(HospitalRadStatus.Scanning, 0, "image/jpeg", "An image is up to 15 MB")]
		[InlineData(HospitalRadStatus.Scanning, HospitalRadImageRules.MaxBytes + 1, "image/jpeg", "An image is up to 15 MB")]
		[InlineData(HospitalRadStatus.Scanning, 1000, null, "Only JPEG, PNG or WebP images are attached")]
		public void A_picture_is_attached_only_to_an_open_exam_and_only_a_real_image(string status, long size, string? sniffed, string? why) =>
			Assert.Equal(why, HospitalRadImageRules.UploadRefusal(status, size, sniffed));

		[Fact]
		public void A_signed_report_keeps_its_pictures()
		{
			Assert.Equal("A signed report's images are locked", HospitalRadImageRules.RemoveRefusal(HospitalRadStatus.Signed));
			Assert.Null(HospitalRadImageRules.RemoveRefusal(HospitalRadStatus.Reporting));
		}

		// ---------------- the shared dental chart's words (H30) ----------------

		[Theory]
		[InlineData(HospitalDentalMark.Condition, "cond")]
		[InlineData(HospitalDentalMark.Planned, "plan")]
		[InlineData(HospitalDentalMark.Completed, "done")]
		[InlineData(HospitalDentalMark.Existing, "exist")]
		public void A_mark_is_the_renderers_word(string status, string key) => Assert.Equal(key, HospitalDentalChartData.MarkKey(status));

		[Theory]
		[InlineData(HospitalToothWork.Crown, "crown")]
		[InlineData(HospitalToothWork.RootCanal, "rct")]
		[InlineData(HospitalToothWork.Implant, "implant")]
		[InlineData(HospitalToothWork.Extraction, "extract")]
		public void A_work_is_the_renderers_word(string work, string key) => Assert.Equal(key, HospitalDentalChartData.WorkKey(work));

		// ---------------- the model against its slice ----------------

		private static string Slice() => HospitalSliceParity.Slice("hospital_h31_rad_images.sql");
		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalRadImagesModel.Configure);

		[Fact]
		public void The_model_names_the_table_its_slice_creates() => HospitalSliceParity.NamesItsTable(Model(), Slice(), typeof(HospitalRadImage), "HospitalRadImages");

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates() => HospitalSliceParity.ColumnsMatch(Model(), Slice() + HospitalSliceParity.Slice("hospital_h35_rad_images_on_documents.sql"), typeof(HospitalRadImage), "HospitalRadImages");   // H35 adds DocumentId

		[Fact]
		public void The_slice_checks_its_prerequisite_allows_every_type_and_alters_nothing()
		{
			Assert.Single(HospitalSliceParity.CheckedPrerequisites(Slice()));
			foreach (var t in HospitalRadImageRules.Types) Assert.Contains($"N'{t}'", Slice());
			Assert.DoesNotContain("ALTER TABLE", Slice(), StringComparison.OrdinalIgnoreCase);
		}
	}
}
