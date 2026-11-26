using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;

namespace CrossBuy.ViewModel;

public sealed record ConstructionBudgetPage(Project Project,
    List<ConstructionBudgetVersion> Versions,
    ConstructionBudgetDocument? Document,
    ConstructionStructureWorkspace Structure,
    ConstructionBudgetPolicy? Policy,
    bool CanManage,
    int ActorEmployeeId,
    string? CurrencyCode,
    List<ConstructionBudgetDraftLine>? RejectedLines = null,
    string? RefusalCode = null,
    ConstructionBudgetExposure? Exposure = null);
