using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL — clinical dietary (unit H43), as the approved prototype draws it (#/dietary: "Lunch orders — Ward 3":
	// patient and bed, diet, allergy, kitchen). A DIET ORDER is the patient's diet while admitted — one open at a time;
	// a change closes the old one, so the history stays. A MEAL ORDER is one patient's tray for one round of one day
	// (breakfast, lunch, snack, dinner): sent to the kitchen with the diet of the moment, held when the patient is
	// nil by mouth, then served. The kitchen's production counts are read from these.
	// CrossBuy/deploy/sql/hospital_h43_dietary.sql.
	// ==========================================================================================

	public static class HospitalDiet
	{
		public const string Regular = "Regular";
		public const string Diabetic = "Diabetic";
		public const string LowSalt = "LowSalt";
		public const string Renal = "Renal";
		public const string Soft = "Soft";
		public const string ClearFluids = "ClearFluids";
		public const string TubeFeed = "TubeFeed";
		public const string Npo = "NPO";
		public static readonly IReadOnlyList<string> All = new[] { Regular, Diabetic, LowSalt, Renal, Soft, ClearFluids, TubeFeed, Npo };
	}

	public static class HospitalMealRound
	{
		public const string Breakfast = "Breakfast";
		public const string Lunch = "Lunch";
		public const string Snack = "Snack";
		public const string Dinner = "Dinner";
		public static readonly IReadOnlyList<string> All = new[] { Breakfast, Lunch, Snack, Dinner };
	}

	public static class HospitalMealStatus
	{
		public const string Sent = "Sent";
		public const string Held = "Held";
		public const string Served = "Served";
		public static readonly IReadOnlyList<string> All = new[] { Sent, Held, Served };
	}

	public class HospitalDietOrder
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public string Diet { get; set; } = HospitalDiet.Regular;
		public string? Note { get; set; }
		public DateTime StartedAt { get; set; }
		public int? OrderedByEmployeeId { get; set; }
		public DateTime? EndedAt { get; set; }
	}

	public class HospitalMealOrder
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public int WardId { get; set; }
		/// The ward-clock day of the round.
		public DateTime Day { get; set; }
		public string Round { get; set; } = HospitalMealRound.Lunch;
		/// The diet when it was sent (kept even if the order changes later).
		public string Diet { get; set; } = HospitalDiet.Regular;
		public string Status { get; set; } = HospitalMealStatus.Sent;
		public DateTime SentAt { get; set; }
		public int? SentByEmployeeId { get; set; }
		public DateTime? ServedAt { get; set; }
	}

	public static class HospitalDietaryModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDietOrder>(e =>
			{
				e.ToTable("HospitalDietOrders");   // no DbSet: each names its table
				e.Property(x => x.Diet).HasMaxLength(20);
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.AdmissionId, x.EndedAt });
			});
			b.Entity<HospitalMealOrder>(e =>
			{
				e.ToTable("HospitalMealOrders");
				e.Property(x => x.Day).HasColumnType("date");
				e.Property(x => x.Round).HasMaxLength(10);
				e.Property(x => x.Diet).HasMaxLength(20);
				e.Property(x => x.Status).HasMaxLength(10);
				e.HasIndex(x => new { x.AdmissionId, x.Day, x.Round }).IsUnique();
			});
		}
	}
}
