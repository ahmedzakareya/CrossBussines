using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	public static class HospitalSurveillanceKind
	{
		/// Possible sepsis: qSOFA ≥ 2, or SIRS ≥ 2 with an infection on record.
		public const string Sepsis = "Sepsis";
		/// Venous thromboembolism: every admitted patient is assessed within 24 hours of admission.
		public const string Vte = "Vte";
		public static readonly IReadOnlyList<string> All = new[] { Sepsis, Vte };
	}

	public static class HospitalSurveillanceStatus
	{
		public const string Open = "Open";
		public const string Answered = "Answered";
		public static readonly IReadOnlyList<string> All = new[] { Open, Answered };
	}

	public static class HospitalSurveillanceResponse
	{
		public const string BundleStarted = "BundleStarted";
		public const string NotSepsis = "NotSepsis";
		public const string ProphylaxisOrdered = "ProphylaxisOrdered";
		public const string NotIndicated = "NotIndicated";
		public const string Contraindicated = "Contraindicated";
		public static IReadOnlyList<string> For(string kind) => kind == HospitalSurveillanceKind.Sepsis
			? new[] { BundleStarted, NotSepsis }
			: new[] { ProphylaxisOrdered, NotIndicated, Contraindicated };
		public static readonly IReadOnlyList<string> All = new[] { BundleStarted, NotSepsis, ProphylaxisOrdered, NotIndicated, Contraindicated };
	}

	// HOSPITAL H212 — an alert the surveillance raised by itself from the record (no one asked for it): what fired it (the
	// criteria and the reading), when it is due by, and the clinician's answer — the decision, and why when it is "no".
	public class HospitalSurveillanceAlert
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public int AdmissionId { get; set; }
		public string Kind { get; set; } = HospitalSurveillanceKind.Sepsis;
		/// The criteria met, as "RR=24;SBP=96;AVPU=C" (shown with the screen's own labels).
		public string Criteria { get; set; } = "";
		public int? Score { get; set; }
		public int? ObservationId { get; set; }
		public DateTime RaisedAt { get; set; }
		public DateTime DueAt { get; set; }
		public string Status { get; set; } = HospitalSurveillanceStatus.Open;
		public string? Response { get; set; }
		public string? Note { get; set; }
		public DateTime? AnsweredAt { get; set; }
		public int? AnsweredByEmployeeId { get; set; }
	}

	public static class HospitalSurveillanceModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<HospitalSurveillanceAlert>(e =>
			{
				e.ToTable("HospitalSurveillanceAlerts");
				e.Property(x => x.Kind).HasMaxLength(10);
				e.Property(x => x.Criteria).HasMaxLength(200);
				e.Property(x => x.Status).HasMaxLength(10);
				e.Property(x => x.Response).HasMaxLength(20);
				e.Property(x => x.Note).HasMaxLength(500);
				e.HasIndex(x => new { x.CompanyId, x.Status, x.Kind });
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId, x.Kind });
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
		}
	}
}
