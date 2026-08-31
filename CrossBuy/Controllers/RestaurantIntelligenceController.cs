using CrossBuy.BL;
using CrossBuy.BL.Platform;
// [SessionValidation] and [InvPerm] live in CrossBuy.Models, and this using was missing, so the file
// did not compile. Added from another session to unblock the build - the owning tab should keep it.
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// THE RESTAURANT INVENTORY INTELLIGENCE SURFACE — read-only, and deliberately its own controller.
	//
	// It is NOT an action on InventoryController. That file is shared (SHF-26) under a rule that admits
	// conversation endpoints only and forbids stock, pricing, procurement, warehouse, approval or costing
	// behaviour from changing under it. A management read has no business arriving through that door, and a
	// new controller costs nothing.
	//
	// NOTHING HERE WRITES. No stock movement, no journal entry, no business event, no recommendation row.
	// Opening a report must never be an operational fact — a manager looking at waste has not created waste.
	//
	// [SessionValidation] on the class AND [InvPerm("read")] on the action, and neither is
	// ceremony. Without SessionValidation the screen is reachable unauthenticated: the company resolver
	// would fail and the page would render empty, which looks safe and is not - "it shows nothing" is a
	// data accident, not an authorization. InvPerm("read") is the Inventory module's own answer to "may
	// you look at stock at all", asked before the action runs rather than inferred from an empty result.
	//
	// InvPerm sits on the ACTION, not here: InvPermAttribute is declared [AttributeUsage(AttributeTargets.Method)]
	// because it is an IAsyncActionFilter, so a class-level copy is a compile error (CS0592) - which is what it was.
	// Every other use in InventoryController is on a method too.
	[SessionValidation]
	public class RestaurantIntelligenceController : Controller
	{
		private readonly IRestaurantInventoryIntelligenceService _intel;
		private readonly IRequestCompanyResolver _company;

		public RestaurantIntelligenceController(IRestaurantInventoryIntelligenceService intel, IRequestCompanyResolver company)
		{ _intel = intel; _company = company; }

		private int? _companyId;

		/// The company comes from the resolver, never from the query string. 0 when nothing resolves, and 0 is
		/// a company id no row can hold, so an unresolved session reads nothing rather than someone else's data.
		private async Task<int> CompanyIdAsync()
		{
			if (_companyId.HasValue) return _companyId.Value;
			var scope = await _company.ResolveAsync();
			_companyId = scope.Ok ? scope.CompanyId : 0;
			return _companyId.Value;
		}

		[HttpGet]
		[InvPerm("read")]
		public async Task<IActionResult> Index(int? branchId, DateTime? from, DateTime? to)
		{
			int co = await CompanyIdAsync();
			var model = new RestaurantIntelligenceVm
			{
				BranchId = branchId ?? 0,
				From = from?.Date ?? DateTime.Today.AddDays(-6),
				To = to?.Date ?? DateTime.Today,
			};

			if (co == 0 || model.BranchId <= 0)
			{
				// A KEY, not a sentence. The controller states WHICH message; the view resolves it against
				// its own resources, so an English reader is not handed Arabic and an Arabic reader is not
				// handed an English key.
				model.MessageKey = "Choose a branch to see inventory intelligence";
				return View(model);
			}

			var variance = await _intel.VarianceAsync(co, model.BranchId, model.From, model.To);
			if (!variance.Ok) { model.Message = variance.Error; return View(model); }

			model.Equation = variance.Equation;
			model.Variance = variance.Rows;
			model.Replenishment = await _intel.ReplenishmentAsync(co, model.BranchId);
			model.Shortages = await _intel.ShortagesAsync(co, model.BranchId, model.From, model.To);
			return View(model);
		}
	}

	public sealed class RestaurantIntelligenceVm
	{
		public int BranchId { get; set; }
		public DateTime From { get; set; }
		public DateTime To { get; set; }
		/// Text the SERVICE produced. It is already localised at source and is passed through unchanged.
		public string? Message { get; set; }

		/// A resource key this CONTROLLER chose. The view localises it. Kept separate from Message so a
		/// service string is never looked up as a key and rendered as one when it is missing.
		public string? MessageKey { get; set; }
		public string Equation { get; set; } = "";
		public IReadOnlyList<ConsumptionVarianceRow> Variance { get; set; } = Array.Empty<ConsumptionVarianceRow>();
		public IReadOnlyList<ReplenishmentRecommendation> Replenishment { get; set; } = Array.Empty<ReplenishmentRecommendation>();
		public IReadOnlyList<ShortageSignal> Shortages { get; set; } = Array.Empty<ShortageSignal>();
	}
}
