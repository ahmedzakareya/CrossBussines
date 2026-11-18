using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	public static class HospitalDeviceVitalStatus
	{
		/// Waiting for a nurse: a monitor's numbers are not the patient's chart until someone at the bedside says so.
		public const string Pending = "Pending";
		/// Charted: the nurse added what a monitor cannot measure (consciousness, oxygen) and NEWS2 was scored.
		public const string Accepted = "Accepted";
		/// Not charted: an artefact (a probe off, the patient moving) — with the nurse's reason.
		public const string Discarded = "Discarded";
		/// Never reached a patient: unreadable, no bed or patient, the bed's patient not the one named.
		public const string Refused = "Refused";
		/// The same device and control id again: kept, changes nothing.
		public const string Duplicate = "Duplicate";
		public static readonly IReadOnlyList<string> All = new[] { Pending, Accepted, Discarded, Refused, Duplicate };
	}

	// HOSPITAL H201 — one set of vital signs a bedside monitor sent (HL7 v2 ORU^R01), as it came, with its fate.
	public class HospitalDeviceVital
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Device { get; set; } = "";
		public string MessageId { get; set; } = "";
		/// The bed the monitor names (PV1-3) and the patient's file number (PID-3), as sent.
		public string? BedCode { get; set; }
		public string? Mrn { get; set; }
		public int? AdmissionId { get; set; }
		public int? PatientId { get; set; }
		public DateTime? TakenAt { get; set; }
		public int? HeartRate { get; set; }
		public int? SpO2 { get; set; }
		public int? RespRate { get; set; }
		public int? SystolicBp { get; set; }
		public int? DiastolicBp { get; set; }
		public decimal? TempC { get; set; }
		public string Status { get; set; } = HospitalDeviceVitalStatus.Pending;
		public string? Detail { get; set; }
		public string Raw { get; set; } = "";
		public DateTime ReceivedAt { get; set; }
		public int? ReceivedByEmployeeId { get; set; }
		public DateTime? DecidedAt { get; set; }
		public int? DecidedByEmployeeId { get; set; }
		/// Accepted: the observation it became.
		public int? ObservationId { get; set; }
	}

	public static class HospitalDeviceVitalModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDeviceVital>(e =>
			{
				e.ToTable("HospitalDeviceVitals");   // no DbSet: it names its table
				e.Property(x => x.Device).HasMaxLength(60);
				e.Property(x => x.MessageId).HasMaxLength(60);
				e.Property(x => x.BedCode).HasMaxLength(30);
				e.Property(x => x.Mrn).HasMaxLength(30);
				e.Property(x => x.TempC).HasPrecision(4, 1);
				e.Property(x => x.Status).HasMaxLength(12);
				e.Property(x => x.Detail).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.Device, x.MessageId });
				e.HasIndex(x => new { x.CompanyId, x.Status, x.ReceivedAt });
			});
		}
	}
}
