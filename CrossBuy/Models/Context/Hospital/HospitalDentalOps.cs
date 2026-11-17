using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL DENTISTRY — the practice (unit H16), on top of H15's chart and plans.
	//
	//   · CHAIRS: a dental practice schedules its operatories, not only its doctors. A chair appointment IS an
	//     H4 appointment (the patient, the dentist, the day, the flow booked → arrived → in the chair → done);
	//     HospitalDentalVisit adds what dentistry needs: the chair, the procedure (its chair time is the slot's
	//     length), the tooth, the plan line it carries out and the lab case it waits for.
	//   · LAB CASES: a crown, a bridge, veneers, an aligner — a prescription to a lab with its tooth, type,
	//     material, shade and the date it is due, tied to the appointment it is fitted at; the stages from
	//     prescribed to fitted, and a remake with whose fault it was.
	//   · RECALL: the hygiene recall of each patient (every 6 months by default); a finished preventive visit
	//     moves it on.
	//   · STERILISATION: autoclave cycles with their chemical and biological indicators; a kit is used on a
	//     patient from a cycle, so a failed indicator names who it reached.
	// CrossBuy/deploy/sql/hospital_h16_dental_practice.sql builds the tables (prepared, not applied).
	// ==========================================================================================

	public class HospitalDentalChair
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		/// The dentist or hygienist who usually works it (a booking may name another).
		public int? PractitionerId { get; set; }
		public int SortOrder { get; set; }
		public bool IsActive { get; set; } = true;
	}

	/// The dental side of an H4 appointment.
	public class HospitalDentalVisit
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AppointmentId { get; set; }
		public int ChairId { get; set; }
		public int? ServiceId { get; set; }
		public int? Tooth { get; set; }
		public int? PlanLineId { get; set; }
		public int? LabCaseId { get; set; }
	}

	public static class HospitalLabCaseStage
	{
		public const string Prescribed = "Prescribed";
		public const string Sent = "Sent";
		public const string Fabricating = "Fabricating";
		public const string Shipped = "Shipped";
		public const string Received = "Received";
		public const string Fitted = "Fitted";
		public const string Remake = "Remake";
		/// The pipeline, in order (a remake goes back to the lab: Sent).
		public static readonly IReadOnlyList<string> Pipeline = new[] { Prescribed, Sent, Fabricating, Shipped, Received, Fitted };
		public static readonly IReadOnlyList<string> All = new[] { Prescribed, Sent, Fabricating, Shipped, Received, Fitted, Remake };
	}

	public static class HospitalLabCaseType
	{
		public static readonly IReadOnlyList<string> All = new[] { "Crown", "Bridge", "Veneer", "Inlay", "ImplantCrown", "Denture", "Retainer", "Aligner", "NightGuard" };
	}

	public class HospitalDentalLab
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string? Phone { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public class HospitalDentalLabCase
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string CaseNo { get; set; } = "";
		public int PatientId { get; set; }
		public int? PlanLineId { get; set; }
		/// "26", "11–21" for a bridge or veneers, null for a whole-arch appliance.
		public string? Teeth { get; set; }
		public string CaseType { get; set; } = "Crown";
		public string Material { get; set; } = "";
		public string? Shade { get; set; }
		public int LabId { get; set; }
		public DateTime DueDate { get; set; }
		/// The appointment the work is fitted at.
		public int? FitAppointmentId { get; set; }
		public string Stage { get; set; } = HospitalLabCaseStage.Prescribed;
		public DateTime? SentAt { get; set; }
		public DateTime? ReceivedAt { get; set; }
		public DateTime? FittedAt { get; set; }
		/// A remake: whose fault ("Lab" or "Clinic") and why.
		public string? RemakeFault { get; set; }
		public string? RemakeReason { get; set; }
		public byte Remakes { get; set; }
		/// What the lab charges, in the facility's currency.
		public decimal Cost { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public class HospitalDentalRecall
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public byte IntervalMonths { get; set; } = 6;
		public DateTime? LastVisit { get; set; }
		public DateTime DueDate { get; set; }
		public byte Attempts { get; set; }
		public DateTime? LastContactAt { get; set; }
		public DateTime UpdatedAt { get; set; }
	}

	public static class HospitalIndicator
	{
		public const string Pending = "Pending";
		public const string Pass = "Pass";
		public const string Fail = "Fail";
		public static readonly IReadOnlyList<string> All = new[] { Pending, Pass, Fail };
	}

	public class HospitalSterilCycle
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string CycleNo { get; set; } = "";
		/// The autoclave, as the room calls it ("Autoclave 1").
		public string Unit { get; set; } = "";
		public DateTime RunAt { get; set; }
		public string Load { get; set; } = "";
		public string Chemical { get; set; } = HospitalIndicator.Pending;
		public string Biological { get; set; } = HospitalIndicator.Pending;
		public int? OperatorEmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
		// H169: "not failed" is not "released". The physical record of the run (time at temperature, temperature,
		// pressure, and the operator's word that they met the programme), whether the load holds implantable items
		// (then the biological indicator must pass before release), and who released it, when. A failure found after
		// release recalls the cycle: every patient it was used on is followed up by infection control.
		public int? ExposureMinutes { get; set; }
		public decimal? TemperatureC { get; set; }
		public decimal? PressureKpa { get; set; }
		public bool? PhysicalOk { get; set; }
		public bool ImplantLoad { get; set; }
		public DateTime? ReleasedAt { get; set; }
		public int? ReleasedByEmployeeId { get; set; }
		public DateTime? RecalledAt { get; set; }
		public string? RecallReason { get; set; }
	}

	/// A kit from a cycle, used on a patient.
	public class HospitalSterilUse
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int CycleId { get; set; }
		public int PatientId { get; set; }
		public int? AppointmentId { get; set; }
		public DateTime UsedAt { get; set; }
		public int? UsedByEmployeeId { get; set; }
		/// H169: the pack was checked intact (wrap, seal, dry, its own indicator turned) before it was opened.
		public bool? PackIntact { get; set; }
	}

	/// H169: a patient a recalled cycle's kit was used on — followed up by infection control until closed.
	public class HospitalSterilRecall
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int CycleId { get; set; }
		public int PatientId { get; set; }
		public int UseId { get; set; }
		public DateTime CreatedAt { get; set; }
		public DateTime? NotifiedAt { get; set; }
		public int? NotifiedByEmployeeId { get; set; }
		public string? Outcome { get; set; }
		public DateTime? ClosedAt { get; set; }
		public int? ClosedByEmployeeId { get; set; }
	}

	public static class HospitalDentalOpsModel
	{
		public static void Configure(ModelBuilder b)
		{
			// no DbSet for any of them: each names its table, or EF would look for the singular name
			b.Entity<HospitalDentalChair>(e =>
			{
				e.ToTable("HospitalDentalChairs");
				e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
			});
			b.Entity<HospitalDentalVisit>(e =>
			{
				e.ToTable("HospitalDentalVisits");
				e.Property(x => x.Tooth).HasColumnType("tinyint").HasConversion<byte?>();
				e.HasIndex(x => x.AppointmentId).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.ChairId });
			});
			b.Entity<HospitalDentalLab>(e => e.ToTable("HospitalDentalLabs"));
			b.Entity<HospitalDentalLabCase>(e =>
			{
				e.ToTable("HospitalDentalLabCases");
				e.Property(x => x.DueDate).HasColumnType("date");
				e.Property(x => x.Cost).HasPrecision(18, 3);
				e.HasIndex(x => new { x.CompanyId, x.CaseNo }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.Stage });
			});
			b.Entity<HospitalDentalRecall>(e =>
			{
				e.ToTable("HospitalDentalRecalls");
				e.Property(x => x.LastVisit).HasColumnType("date");
				e.Property(x => x.DueDate).HasColumnType("date");
				e.HasIndex(x => new { x.CompanyId, x.PatientId }).IsUnique();
			});
			b.Entity<HospitalSterilCycle>(e =>
			{
				e.ToTable("HospitalSterilCycles");
				e.Property(x => x.TemperatureC).HasColumnType("decimal(5,1)");   // H169
				e.Property(x => x.PressureKpa).HasColumnType("decimal(6,1)");
				e.Property(x => x.RecallReason).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.CycleNo }).IsUnique();
			});
			b.Entity<HospitalSterilRecall>(e =>   // H169
			{
				e.ToTable("HospitalSterilRecalls");
				e.Property(x => x.Outcome).HasMaxLength(300);
				e.HasIndex(x => new { x.CompanyId, x.CycleId, x.UseId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.PatientId });
			});
			b.Entity<HospitalSterilUse>(e =>
			{
				e.ToTable("HospitalSterilUses");
				e.HasIndex(x => new { x.CompanyId, x.CycleId });
			});
		}
	}
}
