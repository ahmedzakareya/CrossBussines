using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL
{
	// ==========================================================================================
	// HOSPITAL AUTHORIZATION — the module policy for the first industry pack.
	//
	// H1 covers the FACILITY only: reading the building (rooms, beds, the map) and administering it
	// (room types, floors, wards, the floor and room drawings). Clinical actions — admitting a
	// patient, reading a record — arrive with the units that own them, as new action names here.
	//
	// BOOTSTRAP: until a company assigns its first Hospital role, the module is open for BOTH read
	// and manage. Calendar refuses manage while bootstrap-open; that works for a calendar, which is
	// usable without administration. A hospital is not: until someone draws a floor there is nothing
	// to read, and this codebase has no screen that assigns PlatformRoleAssignments rows — denying
	// manage would leave a new hospital unconfigurable. The first HospitalAdministrator row closes it.
	// ==========================================================================================

	// Namespace CrossBuy.BL, like every module access service, though the file lives with its pack.
	public interface IHospitalAccessService : IModuleAccessService { }

	public static class HospitalActions
	{
		public const string Read = "read";
		/// The platform's middle tier (ViewConfidential). Distinct from both neighbours so a Confidential
		/// record row is never readable on the base grant; today it is the administrator's, like manage.
		public const string ConfidentialView = "confidential-view";
		/// Facility administration: room types, floors, wards, rooms, beds and their drawings.
		public const string Manage = "manage";
		/// H2: open and change a patient's file (registration desk work). Staff and administrators;
		/// every open is written to the record-access log by HospitalPatientService.
		public const string Register = "register";
		/// H3: accommodate a patient in a bed, transfer, discharge. Staff and administrators — the
		/// ward desk does this all day; the rules (vacant bed, sex policy) are the service's, not the role's.
		public const string Admit = "admit";
		/// H4: book appointments and run the clinics board (arrive, call next, finish). Staff and
		/// administrators; the booking rules are HospitalOutpatientService's.
		public const string Book = "book";
		/// H5: write and sign a consultation (the clinical note, diagnoses, prescription). Doctors and
		/// administrators — desk staff run the day, they do not document care.
		public const string Consult = "consult";
		/// H6: the cashier's work - add services to a bill, take payments, open and close a shift, close
		/// a bill. Staff and administrators; the catalogue and billing settings stay with `manage`.
		public const string Bill = "bill";
		/// H8: the laboratory bench - collect and receive specimens, enter results. Lab technicians,
		/// specialists and administrators.
		public const string Lab = "lab";
		/// H8: verify a result so the doctor sees it - the second of the two steps. Lab specialists and
		/// administrators; the service also refuses the person who entered the result, unless an administrator.
		public const string LabVerify = "lab-verify";
		/// H9: the imaging floor - patients arrive, scans start and end, rooms go into maintenance. Radiographers,
		/// radiologists and administrators.
		public const string Rad = "rad";
		/// H9: write and sign an imaging report so the doctor sees it. Radiologists and administrators.
		public const string RadReport = "rad-report";

		/// H10: review and dispense prescriptions from the pharmacy store. Pharmacists and administrators.
		public const string Pharmacy = "pharmacy";

		public static readonly IReadOnlyCollection<string> All = new[] { Read, ConfidentialView, Manage, Register, Admit, Book, Consult, Bill, Lab, LabVerify, Rad, RadReport, Pharmacy };
	}

	public static class HospitalRoles
	{
		public const string HospitalAdministrator = "HospitalAdministrator";
		/// Sees the building and its beds and registers patients; changes nothing about the building.
		public const string HospitalStaff = "HospitalStaff";
		/// H5: a practitioner. Reads the module, runs their clinic (call next) and documents consultations.
		public const string HospitalDoctor = "HospitalDoctor";
		/// H8: the lab bench - collects, receives and enters results.
		public const string HospitalLabTechnician = "HospitalLabTechnician";
		/// H8: verifies results (and does a technician's work).
		public const string HospitalLabSpecialist = "HospitalLabSpecialist";
		/// H9: performs the scans.
		public const string HospitalRadiographer = "HospitalRadiographer";
		/// H9: writes and signs imaging reports (and does a radiographer's work).
		public const string HospitalRadiologist = "HospitalRadiologist";

		/// H10: reviews and dispenses prescriptions.
		public const string HospitalPharmacist = "HospitalPharmacist";

		public static readonly IReadOnlyList<string> All = new[] { HospitalAdministrator, HospitalStaff, HospitalDoctor, HospitalLabTechnician, HospitalLabSpecialist, HospitalRadiographer, HospitalRadiologist, HospitalPharmacist };
	}

	public sealed class HospitalAccessService : ModuleAccessServiceBase, IHospitalAccessService
	{
		public HospitalAccessService(IPlatformRoleDirectory roles, ILogger<HospitalAccessService> log)
			: base(roles, log) { }

		public override string Scope => EntityRegistry.ScopeHospital;
		public override IReadOnlyCollection<string> Actions => HospitalActions.All;

		protected override Task<bool> EvaluateAsync(
			BusinessContext context, string action, PermissionTarget? target,
			IReadOnlyList<RoleGrant> grants, bool bootstrapOpen, CancellationToken cancellationToken)
		{
			if (bootstrapOpen) return Task.FromResult(true);

			bool admin = Holds(grants, HospitalRoles.HospitalAdministrator);
			bool staff = Holds(grants, HospitalRoles.HospitalStaff);
			bool doctor = Holds(grants, HospitalRoles.HospitalDoctor);
			bool labSpecialist = Holds(grants, HospitalRoles.HospitalLabSpecialist);
			bool labTech = labSpecialist || Holds(grants, HospitalRoles.HospitalLabTechnician);
			bool radiologist = Holds(grants, HospitalRoles.HospitalRadiologist);
			bool radiographer = radiologist || Holds(grants, HospitalRoles.HospitalRadiographer);
			bool pharmacist = Holds(grants, HospitalRoles.HospitalPharmacist);

			// Facility rows carry no per-record owner, so every decision is a module-level one; the
			// company of a named record is enforced by the base gate and by the service's own WHERE.
			return Task.FromResult(action switch
			{
				HospitalActions.Read => admin || staff || doctor || labTech || radiographer || pharmacist,
				HospitalActions.ConfidentialView => admin,
				HospitalActions.Manage => admin,
				HospitalActions.Register => admin || staff,
				HospitalActions.Admit => admin || staff,
				HospitalActions.Book => admin || staff || doctor,
				HospitalActions.Consult => admin || doctor,
				HospitalActions.Bill => admin || staff,
				HospitalActions.Lab => admin || labTech,
				HospitalActions.LabVerify => admin || labSpecialist,
				HospitalActions.Rad => admin || radiographer,
				HospitalActions.RadReport => admin || radiologist,
				HospitalActions.Pharmacy => admin || pharmacist,
				_ => false,
			});
		}
	}
}
