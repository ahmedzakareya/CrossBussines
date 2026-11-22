using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Hospital
{
	// ==========================================================================================
	// HOSPITAL ACCOMMODATION — admissions and bed stays (unit H3).
	//
	// An admission is one inpatient episode; a bed stay is each stretch of it in one bed. Admit
	// opens both, a transfer ends the open stay and opens the next, discharge ends the stay and the
	// admission. The bed's Status follows: Occupied while a stay is open on it, Cleaning after.
	//
	// The database keeps the two rules a race could break (deploy/sql/hospital_h3_admissions.sql):
	// at most one open admission per patient, at most one open stay per bed.
	// ==========================================================================================

	public static class HospitalAdmissionStatus
	{
		public const string Active = "Active";
		public const string Discharged = "Discharged";
	}

	public static class HospitalStayEndReasons
	{
		public const string Transfer = "transfer";
		public const string Discharge = "discharge";
	}

	public class HospitalAdmission
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int PatientId { get; set; }
		public string Status { get; set; } = HospitalAdmissionStatus.Active;
		public DateTime AdmittedAt { get; set; }
		public DateTime? DischargedAt { get; set; }
		public string? Diagnosis { get; set; }
		/// Employee.ID of the attending doctor, when one is named.
		public int? AttendingEmployeeId { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public int? DischargedByEmployeeId { get; set; }
	}

	public class HospitalBedStay
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int AdmissionId { get; set; }
		public int PatientId { get; set; }
		public int BedId { get; set; }
		public int RoomId { get; set; }
		public DateTime StartedAt { get; set; }
		public DateTime? EndedAt { get; set; }
		public string? EndReason { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	public static class HospitalAdmissionsModel
	{
		public static void Configure(ModelBuilder b)
		{
			HospitalCapacityModel.Configure(b);   // H239: discharge plans
			HospitalDrgModel.Configure(b);   // H241: DRG groups and assignments
			b.Entity<HospitalAdmission>(e =>
			{
				e.HasIndex(x => new { x.CompanyId, x.PatientId }).IsUnique().HasFilter("[Status] = 'Active'").HasDatabaseName("UX_HospitalAdmissions_OneOpenPerPatient");
				e.HasIndex(x => new { x.CompanyId, x.PatientId, x.AdmittedAt }).HasDatabaseName("IX_HospitalAdmissions_Patient");
			});
			b.Entity<HospitalBedStay>(e =>
			{
				e.HasOne<HospitalAdmission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.NoAction);
				e.HasIndex(x => new { x.CompanyId, x.BedId }).IsUnique().HasFilter("[EndedAt] IS NULL").HasDatabaseName("UX_HospitalBedStays_OneOpenPerBed");
				e.HasIndex(x => new { x.CompanyId, x.AdmissionId });
				e.HasIndex(x => new { x.CompanyId, x.RoomId, x.EndedAt });
			});
		}
	}
}
