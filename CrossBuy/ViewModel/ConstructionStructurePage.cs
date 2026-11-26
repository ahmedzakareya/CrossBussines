using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Accounting;

namespace CrossBuy.ViewModel;

public sealed record ConstructionStructurePage(Project Project,ConstructionStructureWorkspace Workspace,bool CanManage);
