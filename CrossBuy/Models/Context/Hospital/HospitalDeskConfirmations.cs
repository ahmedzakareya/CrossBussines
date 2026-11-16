using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL H141 — confirming tomorrow's appointments from the front desk. The reminder SMS goes out on its own (H70); the
	// desk still calls the patients who have not answered and records what each said:
	//   · Confirmed: the patient comes;
	//   · NoAnswer: called, nobody answered (the count of calls is kept);
	//   · WantsToMove: the patient asked for another time. The desk moves the appointment, and the new one is confirmed.
	// A patient who cancels is cancelled on the appointment itself, not here. One row per appointment; the last call wins.
	// CrossBuy/deploy/sql/hospital_h141_desk_confirmations.sql builds it.
	// ==========================================================================================

	public static class HospitalConfirmStatus
	{
		public const string Confirmed = "Confirmed";
		public const string NoAnswer = "NoAnswer";
		public const string WantsToMove = "WantsToMove";
		public static readonly IReadOnlyList<string> All = new[] { Confirmed, NoAnswer, WantsToMove };
		public static bool IsKnown(string? s) => s != null && All.Contains(s, StringComparer.Ordinal);
	}

	public class HospitalDeskConfirmation
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AppointmentId { get; set; }
		public string Status { get; set; } = HospitalConfirmStatus.Confirmed;
		public string? Note { get; set; }
		public int Calls { get; set; }
		public DateTime LastAt { get; set; }
		public int? ByEmployeeId { get; set; }
	}

	public static class HospitalDeskConfirmationModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalDeskConfirmation>(e =>
			{
				e.ToTable("HospitalDeskConfirmations");   // no DbSet: it names its table
				e.Property(x => x.Status).HasMaxLength(20);
				e.Property(x => x.Note).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.AppointmentId }).IsUnique();
			});
		}
	}
}
