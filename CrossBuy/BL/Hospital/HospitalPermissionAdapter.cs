using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.ModulePermissions
{
	// The platform's view of the Hospital policy — what the Communication Platform, search and the
	// record links ask before showing a hospital record. Kept beside the module rather than in the
	// shared adapters file, so the pack stays one folder.
	//
	// Three tiers, three distinct module actions: a collapsed tier would let the base grant read a
	// Confidential row. Namespace CrossBuy.BL.ModulePermissions, like every adapter.
	public class HospitalPermissionAdapter : ModulePermissionAdapterBase
	{
		public HospitalPermissionAdapter(IEnumerable<IModuleAccessService> modules) : base(modules) { }

		public override string Scope => EntityRegistry.ScopeHospital;
		protected override string ModuleScope => EntityRegistry.ScopeHospital;
		protected override string ReasonPrefix => "hospital";

		protected override string? MapAction(string platformAction) => platformAction switch
		{
			PlatformActions.View => HospitalActions.Read,
			PlatformActions.ViewConfidential => HospitalActions.ConfidentialView,
			PlatformActions.ViewRestricted => HospitalActions.Manage,
			_ => null,
		};

		protected override PermissionTarget? BuildTarget(PermissionCheckRequest request)
			=> new PermissionTarget
			{
				EntityType = request.EntityType, EntityId = request.EntityId,
				CompanyId = request.Context.CompanyId,
			};
	}
}
