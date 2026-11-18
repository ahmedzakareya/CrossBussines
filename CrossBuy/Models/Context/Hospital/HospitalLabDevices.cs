using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H198 — results from the laboratory's analysers (HL7 v2 ORU^R01). Every message received is kept as it came,
	// with what became of it; a device's own test code is linked once to an analyte of the catalogue (when it is not the
	// analyte's own code). A message is taken once: the same device and message control id again is a duplicate.
	// deploy/sql/hospital_h198_lab_devices.sql.
	// ==========================================================================================
	public static class HospitalDeviceMessageStatus
	{
		public const string Accepted = "Accepted";
		/// Some results applied; some codes unknown or not final.
		public const string Partial = "Partial";
		public const string Refused = "Refused";
		public const string Duplicate = "Duplicate";
		public static readonly IReadOnlyList<string> All = new[] { Accepted, Partial, Refused, Duplicate };
	}

	public class HospitalDeviceMessage
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		/// MSH-3, the sending application (the analyser or its middleware).
		public string Device { get; set; } = "";
		/// MSH-10, the message control id.
		public string MessageId { get; set; } = "";
		public string? AccessionNo { get; set; }
		public int? LabOrderId { get; set; }
		public string Status { get; set; } = HospitalDeviceMessageStatus.Refused;
		public int Applied { get; set; }
		/// Why it was refused, or the codes it could not place ("K, NA (not final)").
		public string? Detail { get; set; }
		public string Raw { get; set; } = "";
		public DateTime ReceivedAt { get; set; }
		public int? ReceivedByEmployeeId { get; set; }
	}

	/// A device's own code for an analyte of the catalogue.
	public class HospitalDeviceCodeMap
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Device { get; set; } = "";
		public string DeviceCode { get; set; } = "";
		public int AnalyteId { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public static class HospitalLabDeviceModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDeviceMessage>(e =>
			{
				e.ToTable("HospitalDeviceMessages");   // no DbSet: it names its table
				e.Property(x => x.Device).HasMaxLength(60);
				e.Property(x => x.MessageId).HasMaxLength(60);
				e.Property(x => x.AccessionNo).HasMaxLength(30);
				e.Property(x => x.Status).HasMaxLength(12);
				e.Property(x => x.Detail).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.Device, x.MessageId });
				e.HasIndex(x => new { x.CompanyId, x.ReceivedAt });
			});
			b.Entity<HospitalDeviceCodeMap>(e =>
			{
				e.ToTable("HospitalDeviceCodeMaps");
				e.Property(x => x.Device).HasMaxLength(60);
				e.Property(x => x.DeviceCode).HasMaxLength(40);
				e.HasIndex(x => new { x.CompanyId, x.Device, x.DeviceCode }).IsUnique();
			});
		}
	}
}
