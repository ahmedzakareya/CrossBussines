using CrossBuy.BL;
using CrossBuy.BL.Platform;

namespace CrossBuy.BL.ModulePermissions
{
	// ==========================================================================================
	// HOSPITAL H156 — the hospital publishes its role vocabulary to the platform, like HR, Projects, Tasks and
	// Communication: without it the platform's grants screen cannot assign a hospital role at all, and the module stays
	// BOOTSTRAP-OPEN (every employee may do everything) for want of a way to configure it.
	//
	// H192: registered with the others in Program.cs (one AddSingleton<IPlatformPermissionVocabulary, …> line; the owner
	// approved that change on 2026-11-18). The first hospital role assigned in a company ends the bootstrap-open mode for
	// EVERY hospital user of that company — assign the roles of all hospital staff together.
	// ==========================================================================================
	public sealed class HospitalPermissionVocabulary : IPlatformPermissionVocabulary
	{
		public string Scope => EntityRegistry.ScopeHospital;
		public IReadOnlyList<string> Roles => HospitalRoles.All;
		public string? ManageAction => HospitalActions.Manage;
	}
}
