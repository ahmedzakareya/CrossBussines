using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H58 — BIRTHS (the prototype's "medical records: births … newborn-to-mother"). A birth is registered on the
	// mother's file: when, how, the baby's sex and measures, Apgar at 1 and 5 minutes, live or stillborn. A LIVE birth
	// opens the newborn's own patient file (through the patient registry's own save) linked to the mother here. Numbered
	// (BR-000001), final once registered (a wrong one is voided with a reason). The birth notification prints only
	// through Report Studio (Hospital.BirthNotice). CrossBuy/deploy/sql/hospital_h58_births.sql builds the table.
	// ==========================================================================================
	public static class HospitalDeliveryMode
	{
		public const string Normal = "Normal";
		public const string Assisted = "Assisted";
		public const string Caesarean = "Caesarean";
		public static readonly IReadOnlyList<string> All = new[] { Normal, Assisted, Caesarean };
	}

	public static class HospitalBirthOutcome
	{
		public const string Live = "Live";
		public const string Stillbirth = "Stillbirth";
		public static readonly IReadOnlyList<string> All = new[] { Live, Stillbirth };
	}

	public class HospitalBirth
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string BirthNo { get; set; } = "";
		public int MotherPatientId { get; set; }
		public int? MotherAdmissionId { get; set; }
		public int? NewbornPatientId { get; set; }
		public DateTime BornAt { get; set; }
		public string Sex { get; set; } = HospitalPatientSex.Male;
		public string Outcome { get; set; } = HospitalBirthOutcome.Live;
		public string DeliveryMode { get; set; } = HospitalDeliveryMode.Normal;
		public int? GestationWeeks { get; set; }
		public int BirthOrder { get; set; } = 1;
		public int WeightGrams { get; set; }
		public decimal? LengthCm { get; set; }
		public decimal? HeadCm { get; set; }
		public int? Apgar1 { get; set; }
		public int? Apgar5 { get; set; }
		public int? AttendantPractitionerId { get; set; }
		public string? Note { get; set; }
		public DateTime RegisteredAt { get; set; }
		public int RegisteredByEmployeeId { get; set; }
		public DateTime? VoidedAt { get; set; }
		public int? VoidedByEmployeeId { get; set; }
		public string? VoidReason { get; set; }
	}

	public static class HospitalBirthModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalBirth>(e =>
			{
				e.ToTable("HospitalBirths");   // no DbSet: it names its table
				e.Property(x => x.BirthNo).HasMaxLength(20);
				e.Property(x => x.Sex).HasMaxLength(1);
				e.Property(x => x.Outcome).HasMaxLength(12);
				e.Property(x => x.DeliveryMode).HasMaxLength(12);
				e.Property(x => x.LengthCm).HasPrecision(5, 1);
				e.Property(x => x.HeadCm).HasPrecision(5, 1);
				e.Property(x => x.Note).HasMaxLength(500);
				e.Property(x => x.VoidReason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.BirthNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.MotherPatientId });
			});
		}
	}
}
