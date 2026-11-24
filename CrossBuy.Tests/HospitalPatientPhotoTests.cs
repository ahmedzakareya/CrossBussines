using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL — the patient's photo on the document platform, with no database: the type is read from the bytes,
	// who may do what to a patient's documents, the URL the screens keep reading, and the registry switch.
	// ==========================================================================================
	public class HospitalPatientPhotoTests
	{
		[Fact]
		public void The_type_is_read_from_the_bytes_never_from_the_name()
		{
			Assert.Equal((".jpg", "image/jpeg"), HospitalPatientPhotoRules.Sniff(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
			Assert.Equal((".png", "image/png"), HospitalPatientPhotoRules.Sniff(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A }));
			Assert.Equal((".webp", "image/webp"), HospitalPatientPhotoRules.Sniff("RIFF\0\0\0\0WEBP"u8));
			Assert.Null(HospitalPatientPhotoRules.Sniff("not an image"u8));
			Assert.Null(HospitalPatientPhotoRules.Sniff("GIF89a"u8));
			Assert.Null(HospitalPatientPhotoRules.Sniff(System.Array.Empty<byte>()));
		}

		[Theory]
		[InlineData(DocumentAction.View, HospitalActions.Read)]
		[InlineData(DocumentAction.Download, HospitalActions.Read)]
		[InlineData(DocumentAction.Upload, HospitalActions.Register)]
		[InlineData(DocumentAction.Replace, HospitalActions.Register)]
		[InlineData(DocumentAction.Delete, HospitalActions.Register)]
		[InlineData(DocumentAction.Manage, HospitalActions.Manage)]
		public void Reading_a_patients_documents_is_read_and_changing_them_is_register(DocumentAction action, string expected) =>
			Assert.Equal(expected, new HospitalPatientDocumentOwnerResolver(null!).ModuleActionFor(action));

		[Fact]
		public void An_unknown_act_maps_to_an_action_the_module_never_grants()
		{
			var r = new HospitalPatientDocumentOwnerResolver(null!);
			Assert.Equal(EntityRegistry.HospitalPatient, r.EntityType);
			Assert.DoesNotContain(r.ModuleActionFor(DocumentAction.Submit), HospitalActions.All);
		}

		[Fact]
		public void The_screens_keep_reading_a_url_that_changes_with_every_new_photo()
		{
			Assert.Equal("/Hospital/Patient/3/Photo?d=3&v=6", HospitalPatientPhotoRules.Url(3, 3, 6));
			Assert.NotEqual(HospitalPatientPhotoRules.Url(3, 3, 6), HospitalPatientPhotoRules.Url(3, 3, 7));
			Assert.True(System.Text.Json.JsonDocument.Parse(HospitalPatientPhotoRules.Metadata).RootElement.ValueKind == System.Text.Json.JsonValueKind.Object);
		}

		[Fact]
		public void A_patient_record_takes_files_and_stays_out_of_the_record_picker()
		{
			var d = new EntityRegistry(null!).GetDefinition(EntityRegistry.HospitalPatient);   // the definitions are static; no query
			Assert.True(d.SupportsFiles);
			Assert.False(d.ListedInRecordPicker);
			Assert.Equal(EntityRegistry.ScopeHospital, d.PermissionScope);
		}
	
		// H35: an imaging order's pictures — read to view, rad to attach (every radiologist holds rad)
		[Theory]
		[InlineData(DocumentAction.View, HospitalActions.Read)]
		[InlineData(DocumentAction.Download, HospitalActions.Read)]
		[InlineData(DocumentAction.Upload, HospitalActions.Rad)]
		[InlineData(DocumentAction.Replace, HospitalActions.Rad)]
		[InlineData(DocumentAction.Delete, HospitalActions.Rad)]
		[InlineData(DocumentAction.Manage, HospitalActions.Manage)]
		public void Reading_an_orders_pictures_is_read_and_attaching_is_rad(DocumentAction action, string expected) =>
			Assert.Equal(expected, new HospitalRadOrderDocumentOwnerResolver(null!).ModuleActionFor(action));

		[Fact]
		public void An_imaging_order_takes_files_only()
		{
			var d = new EntityRegistry(null!).GetDefinition(EntityRegistry.HospitalRadOrder);
			Assert.True(d.SupportsFiles);
			Assert.False(d.SupportsSearch || d.ListedInRecordPicker);
			Assert.True(d.SupportsComments);   // 2026-11-24: a conversation on every hospital record, clinical-read (HospitalActions.ClinicalRecords)
			Assert.Equal(EntityRegistry.ScopeHospital, d.PermissionScope);
			Assert.DoesNotContain(new HospitalRadOrderDocumentOwnerResolver(null!).ModuleActionFor(DocumentAction.Submit), HospitalActions.All);
			Assert.True(System.Text.Json.JsonDocument.Parse(HospitalRadImageService.Metadata).RootElement.ValueKind == System.Text.Json.JsonValueKind.Object);
		}
	}
}
