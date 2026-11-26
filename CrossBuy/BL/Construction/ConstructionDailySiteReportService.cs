using System.Data;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using CrossBuy.BL.Platform;
using CrossBuy.BL;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace CrossBuy.BL.Construction;

public sealed record ConstructionDailySiteReportPage(Project Project, IReadOnlyList<ConstructionSite> Sites,
    IReadOnlyList<ConstructionDailySiteReport> Reports, bool CanCreate, bool CanReview, bool CanApprove,
    int CurrentEmployeeId, IReadOnlyList<DsrChoice> Employees, IReadOnlyList<DsrChoice> Subcontracts,
    IReadOnlyList<DsrChoice> Assets, IReadOnlyList<DsrChoice> WbsNodes,
    IReadOnlyList<ConstructionDsrManpowerLine> ManpowerLines,
    IReadOnlyList<ConstructionDsrEquipmentLine> EquipmentLines,
    IReadOnlyList<DsrBoqChoice> BoqItems, IReadOnlyList<ConstructionDsrQuantityLine> QuantityLines,
    IReadOnlyList<DsrMaterialChoice> ReceiptLines, IReadOnlyList<DsrMaterialChoice> IssueLines,
    IReadOnlyList<ConstructionDsrMaterialLine> MaterialLines, IReadOnlyList<ConstructionDsrEventLine> Events,
    IReadOnlyDictionary<int, string> ItemLabels, IReadOnlyList<DsrChoice> LibraryFiles,
    IReadOnlyList<DsrAttachmentView> Attachments, IReadOnlyList<ConstructionDsrVisitorLine> Visitors,
    IReadOnlyList<ConstructionDsrInstructionLine> Instructions,
    string? Message = null);
public sealed record DsrChoice(int Id, string Label);
public sealed record DsrBoqChoice(int Id, string Label, string Unit);
public sealed record DsrMaterialChoice(int Id, string Label, decimal AvailableQuantity);
public sealed record DsrItemLabel(int Id, string Name, string? NameEn);
public sealed record DsrAttachmentView(int Id, int ReportId, int LibraryItemId, string FileName, string Kind,
    string? Caption, int? WbsNodeId, DateTime? TakenAt, decimal? Latitude, decimal? Longitude, DateTime AddedAt);
public sealed record ConstructionDailySiteReportResult(bool Ok, string Code, int? ReportId = null,
    byte[]? Version = null);

public interface IConstructionDailySiteReportService
{
    Task<ConstructionDailySiteReportPage?> ReadAsync(int projectId, CancellationToken ct = default);
    Task<ConstructionDailySiteReportResult> CreateDraftAsync(int projectId, int siteId, DateTime reportDate,
        string? weather, decimal? temperatureC, bool rainfallStopped, TimeSpan? workStart, TimeSpan? workEnd,
        decimal? totalHours, int shifts, string? nextDayPlan, string? remarks, CancellationToken ct = default);
    Task<ConstructionDailySiteReportResult> CreateAmendmentAsync(int projectId, int reportId, string reason,
        CancellationToken ct = default);
    Task<ConstructionDailySiteReportResult> RemoveAmendmentLineAsync(int projectId, int reportId,
        string lineType, int lineId, string reason, CancellationToken ct = default);
    Task<ConstructionDailySiteReportResult> UpdateAmendmentHeaderAsync(int projectId, int reportId,
        byte[] expectedVersion, string? weather, decimal? temperatureC, bool rainfallStopped,
        TimeSpan? workStart, TimeSpan? workEnd, decimal? totalHours, int shifts,
        string? nextDayPlan, string? remarks, string reason, CancellationToken ct = default);
    Task<ConstructionDailySiteReportResult> TransitionAsync(int projectId, int reportId, string transition,
        byte[] expectedVersion, string reason, CancellationToken ct = default);
    Task<ConstructionDailySiteReportResult> AddManpowerAsync(int projectId, int reportId, string source,
        string trade, int? employeeId, int? subcontractId, int headcount, int? wbsNodeId,
        decimal regularHours, decimal overtimeHours, CancellationToken ct = default);
    Task<ConstructionDailySiteReportResult> AddEquipmentAsync(int projectId, int reportId, int? fixedAssetId,
        int? subcontractId, string? equipmentDescription, int? operatorEmployeeId, int? wbsNodeId,
        decimal workingHours, decimal idleHours, decimal downtimeHours, string? downtimeReason,
        decimal? meterStart, decimal? meterEnd, decimal? fuelQuantity, CancellationToken ct = default);
    Task<ConstructionDailySiteReportResult> AddQuantityAsync(int projectId, int reportId, int wbsNodeId,
        int boqItemId, decimal quantity, string? location, string? remarks, CancellationToken ct = default);
    Task<ConstructionDailySiteReportResult> AddMaterialAsync(int projectId, int reportId, string direction,
        int sourceLineId, decimal quantity, int? wbsNodeId, string? deliveryNoteNo, string? remarks,
        CancellationToken ct = default);
    Task<ConstructionDailySiteReportResult> AddEventAsync(int projectId, int reportId, string eventType,
        string description, string severity, string? responsibleParty, int? wbsNodeId, DateTime? startedAt,
        DateTime? endedAt, DateTime? neededByDate, bool reportable, CancellationToken ct = default);
    Task<ConstructionDailySiteReportResult> AddAttachmentAsync(int projectId, int reportId, int libraryItemId,
        string kind, string? caption, int? wbsNodeId, DateTime? takenAt, decimal? latitude, decimal? longitude,
        CancellationToken ct = default);
    Task<ConstructionDailySiteReportResult> AddVisitorAsync(int projectId, int reportId, string visitorName,
        string? organisation, string purpose, DateTime? timeIn, DateTime? timeOut, CancellationToken ct = default);
    Task<ConstructionDailySiteReportResult> AddInstructionAsync(int projectId, int reportId, string source,
        string? reference, string description, CancellationToken ct = default);
}

/// <summary>Project-scoped DSR workflow. It records site evidence and never posts inventory or GL entries.</summary>
public sealed class ConstructionDailySiteReportService(CrossDbContext db, IBusinessContextAccessor contexts,
    IProjectsAccessService access, IConstructionAuditService audit) : IConstructionDailySiteReportService
{
    private const string Entity = "DailySiteReport";

    private async Task<BusinessContext?> ActorAsync(string action, int projectId, CancellationToken ct)
    {
        var actor = await contexts.TryGetCurrentAsync(ct);
        if (actor is not { IsAuthenticated: true } || actor.IsExternalPortal || actor.CompanyId <= 0 ||
            actor.EmployeeId is not > 0 || db.CompanyScope.AllowsCrossCompany ||
            db.CompanyScope.FilterCompanyId != actor.CompanyId) return null;
        return await access.CanAsync(actor, action,
            PermissionTarget.ForProject(projectId, actor.CompanyId), ct) ? actor : null;
    }

    public async Task<ConstructionDailySiteReportPage?> ReadAsync(int projectId, CancellationToken ct = default)
    {
        var actor = await ActorAsync(ProjectsActions.SiteReportRead, projectId, ct);
        if (actor == null) return null;
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == projectId && x.CompanyID == actor.CompanyId, ct);
        if (project == null) return null;
        var sites = await db.Set<ConstructionSite>().AsNoTracking().Where(x =>
            x.CompanyID == actor.CompanyId && x.ProjectId == projectId).OrderBy(x => x.Code).ToListAsync(ct);
        var reports = await db.Set<ConstructionDailySiteReport>().AsNoTracking().Where(x =>
            x.CompanyID == actor.CompanyId && x.ProjectId == projectId).OrderByDescending(x => x.ReportDate)
            .ThenByDescending(x => x.SiteId).ThenByDescending(x => x.RevisionNo).Take(100).ToListAsync(ct);
        var reportIds = reports.Select(x => x.ID).ToArray();
        var manpower = reportIds.Length == 0 ? new List<ConstructionDsrManpowerLine>() : await db.Set<ConstructionDsrManpowerLine>()
            .AsNoTracking().Where(x => x.CompanyID == actor.CompanyId && reportIds.Contains(x.ReportId)).OrderBy(x => x.ID).ToListAsync(ct);
        var equipment = reportIds.Length == 0 ? new List<ConstructionDsrEquipmentLine>() : await db.Set<ConstructionDsrEquipmentLine>()
            .AsNoTracking().Where(x => x.CompanyID == actor.CompanyId && reportIds.Contains(x.ReportId)).OrderBy(x => x.ID).ToListAsync(ct);
        var quantities = reportIds.Length == 0 ? new List<ConstructionDsrQuantityLine>() : await db.Set<ConstructionDsrQuantityLine>()
            .AsNoTracking().Where(x => x.CompanyID == actor.CompanyId && reportIds.Contains(x.ReportId)).OrderBy(x => x.ID).ToListAsync(ct);
        var materials = reportIds.Length == 0 ? new List<ConstructionDsrMaterialLine>() : await db.Set<ConstructionDsrMaterialLine>()
            .AsNoTracking().Where(x => x.CompanyID == actor.CompanyId && reportIds.Contains(x.ReportId)).OrderBy(x => x.ID).ToListAsync(ct);
        var events = reportIds.Length == 0 ? new List<ConstructionDsrEventLine>() : await db.Set<ConstructionDsrEventLine>()
            .AsNoTracking().Where(x => x.CompanyID == actor.CompanyId && reportIds.Contains(x.ReportId))
            .OrderBy(x => x.EventType).ThenBy(x => x.ID).ToListAsync(ct);
        var employees = await db.Employee.AsNoTracking().Where(x => x.EmpCompanyID == actor.CompanyId)
            .OrderBy(x => x.FullName).Select(x => new DsrChoice(x.ID, x.FullNameEn ?? x.FullName)).ToListAsync(ct);
        var subs = await db.Subcontracts.AsNoTracking().Where(x => x.CompanyID == actor.CompanyId && x.ProjectId == projectId && x.Status == "Active")
            .OrderBy(x => x.ID).Select(x => new DsrChoice(x.ID, x.Description ?? $"Subcontract #{x.ID}")).ToListAsync(ct);
        var assets = await db.FixedAssets.AsNoTracking().Where(x => x.CompanyID == actor.CompanyId && x.Status == "Active")
            .OrderBy(x => x.AssetNo).Select(x => new DsrChoice(x.ID, x.AssetNo == null ? x.Name : x.AssetNo + " — " + x.Name)).ToListAsync(ct);
        var wbs = await db.Set<WbsNode>().AsNoTracking().Where(x => x.CompanyID == actor.CompanyId && x.ProjectId == projectId && x.Status == "Active")
            .OrderBy(x => x.Path).Select(x => new DsrChoice(x.ID, x.Code + " — " + (x.NameEn ?? x.Name))).ToListAsync(ct);
        var boqRows = await db.BoqItems.AsNoTracking().Where(x => x.CompanyID == actor.CompanyId && x.ProjectId == projectId && x.Status == "Active")
            .OrderBy(x => x.SortOrder).Select(x => new { x.ID, x.Code, x.Description, x.DescriptionEn, x.Unit }).ToListAsync(ct);
        var boq = boqRows.Select(x => new DsrBoqChoice(x.ID,
            (x.Code == null ? "" : x.Code + " — ") + DisplayName.Of(x.Description, x.DescriptionEn), x.Unit ?? "")).ToList();
        var receiptRows = await (from line in db.GoodsReceiptLines.AsNoTracking()
            join receipt in db.GoodsReceipts.AsNoTracking() on line.GoodsReceiptId equals receipt.ID
            join order in db.PurchaseOrders.AsNoTracking() on receipt.PurchaseOrderId equals order.ID
            join item in db.Items.AsNoTracking() on line.ItemId equals item.ID
            where receipt.CompanyID == actor.CompanyId && receipt.Status == "Posted" && order.CompanyID == actor.CompanyId && order.ProjectId == projectId
            orderby receipt.ReceiptDate descending, line.LineNo
            select new { line.ID, ReceiptNo = receipt.ReceiptNo, ReceiptId = receipt.ID, item.Name, item.NameEn, line.Qty })
            .Take(300).ToListAsync(ct);
        var receiptChoices = receiptRows.Select(x => new DsrMaterialChoice(x.ID,
            (x.ReceiptNo ?? $"GRN #{x.ReceiptId}") + " · " + DisplayName.Of(x.Name, x.NameEn), x.Qty)).ToList();
        var issueRows = await (from line in db.ProjectMaterialIssueLines.AsNoTracking()
            join issue in db.ProjectMaterialIssues.AsNoTracking() on line.IssueId equals issue.ID
            join item in db.Items.AsNoTracking() on line.ItemId equals item.ID
            where issue.CompanyID == actor.CompanyId && issue.ProjectId == projectId && issue.Status == "Posted"
            orderby issue.IssueDate descending, issue.IssueNo descending
            select new { line.ID, issue.IssueNo, item.Name, item.NameEn, line.Qty })
            .Take(300).ToListAsync(ct);
        var issueChoices = issueRows.Select(x => new DsrMaterialChoice(x.ID,
            $"Issue #{x.IssueNo} · {DisplayName.Of(x.Name, x.NameEn)}", x.Qty)).ToList();
        var itemIds = materials.Select(x => x.ItemId).Distinct().ToArray();
        var itemRows = itemIds.Length == 0 ? new List<DsrItemLabel>() : await db.Items.AsNoTracking()
            .Where(x => x.CompanyID == actor.CompanyId && itemIds.Contains(x.ID))
            .Select(x => new DsrItemLabel(x.ID, x.Name, x.NameEn)).ToListAsync(ct);
        var itemLabels = itemRows.ToDictionary(x => x.Id, x => DisplayName.Of(x.Name, x.NameEn));
        var libraryFiles = await db.LibraryItems.AsNoTracking().Where(x => x.CompanyID == actor.CompanyId &&
                x.DeletedAt == null && !x.IsFolder && x.StoredPath != null)
            .OrderBy(x => x.Name).Take(500)
            .Select(x => new { x.Id, x.Name, x.NameEn }).ToListAsync(ct);
        var libraryChoices = libraryFiles.Select(x => new DsrChoice(x.Id, DisplayName.Of(x.Name, x.NameEn))).ToList();
        var attachments = new List<DsrAttachmentView>();
        if (reportIds.Length > 0)
        {
            var attachmentRows = await (
            from a in db.Set<ConstructionDsrAttachment>().AsNoTracking()
            join f in db.LibraryItems.AsNoTracking() on new { a.LibraryItemId, a.CompanyID } equals new { LibraryItemId = f.Id, f.CompanyID }
            where a.CompanyID == actor.CompanyId && reportIds.Contains(a.ReportId) && f.DeletedAt == null && !f.IsFolder
            orderby a.AddedAt descending
            select new { a.ID, a.ReportId, a.LibraryItemId, f.Name, f.NameEn, a.Kind, a.Caption, a.WbsNodeId, a.TakenAt, a.Latitude, a.Longitude, a.AddedAt }
            ).ToListAsync(ct);
            attachments = attachmentRows.Select(x => new DsrAttachmentView(x.ID, x.ReportId, x.LibraryItemId,
                DisplayName.Of(x.Name, x.NameEn), x.Kind, x.Caption, x.WbsNodeId, x.TakenAt,
                x.Latitude, x.Longitude, x.AddedAt)).ToList();
        }
        var visitors = reportIds.Length == 0 ? new List<ConstructionDsrVisitorLine>() : await db.Set<ConstructionDsrVisitorLine>()
            .AsNoTracking().Where(x => x.CompanyID == actor.CompanyId && reportIds.Contains(x.ReportId))
            .OrderBy(x => x.TimeIn).ThenBy(x => x.ID).ToListAsync(ct);
        var instructions = reportIds.Length == 0 ? new List<ConstructionDsrInstructionLine>() : await db.Set<ConstructionDsrInstructionLine>()
            .AsNoTracking().Where(x => x.CompanyID == actor.CompanyId && reportIds.Contains(x.ReportId))
            .OrderBy(x => x.ID).ToListAsync(ct);
        var canCreate = await access.CanAsync(actor, ProjectsActions.SiteReportCreate,
            PermissionTarget.ForProject(projectId, actor.CompanyId), ct);
        var canReview = await access.CanAsync(actor, ProjectsActions.SiteReportReview,
            PermissionTarget.ForProject(projectId, actor.CompanyId), ct);
        var canApprove = await access.CanAsync(actor, ProjectsActions.SiteReportApprove,
            PermissionTarget.ForProject(projectId, actor.CompanyId), ct);
        return new(project, sites, reports, canCreate, canReview, canApprove, actor.EmployeeId!.Value,
            employees, subs, assets, wbs, manpower, equipment, boq, quantities, receiptChoices, issueChoices, materials, events,
            itemLabels, libraryChoices, attachments, visitors, instructions);
    }

    public async Task<ConstructionDailySiteReportResult> AddVisitorAsync(int projectId, int reportId,
        string visitorName, string? organisation, string purpose, DateTime? timeIn, DateTime? timeOut,
        CancellationToken ct = default)
    {
        var actor = await ActorAsync(ProjectsActions.SiteReportCreate, projectId, ct);
        if (actor == null) return new(false, "AccessDenied");
        visitorName = Clean(visitorName) ?? "";
        purpose = Clean(purpose) ?? "";
        organisation = Clean(organisation);
        if (visitorName.Length is < 1 or > 150 || organisation?.Length > 150 || purpose.Length is < 1 or > 500 ||
            (timeIn.HasValue && timeOut.HasValue && timeOut < timeIn)) return new(false, "InvalidVisitor");
        var report = await db.Set<ConstructionDailySiteReport>().SingleOrDefaultAsync(x => x.ID == reportId &&
            x.CompanyID == actor.CompanyId && x.ProjectId == projectId, ct);
        if (report == null) return new(false, "NotFound");
        if (report.Status != "Draft" || report.CreatedBy != actor.EmployeeId) return new(false, "DraftOwnerRequired");
        var line = new ConstructionDsrVisitorLine
        {
            CompanyID = actor.CompanyId, ProjectId = projectId, SiteId = report.SiteId, ReportId = reportId,
            VisitorName = visitorName, Organisation = organisation, Purpose = purpose, TimeIn = timeIn, TimeOut = timeOut
        };
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        db.Add(line);
        try
        {
            await db.SaveChangesAsync(ct);
            audit.Record(new[] { ConstructionAuditDraft.Text(actor.CompanyId, projectId, "DsrVisitorLine", line.ID,
                reportId, "VisitorName", null, visitorName, "Recorded", "Site visitor recorded", actor.EmployeeId,
                "Construction.DailySiteReport", Guid.NewGuid()) });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(true, "Saved", reportId);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(false, "InvalidVisitor");
        }
    }

    public async Task<ConstructionDailySiteReportResult> AddInstructionAsync(int projectId, int reportId,
        string source, string? reference, string description, CancellationToken ct = default)
    {
        var actor = await ActorAsync(ProjectsActions.SiteReportCreate, projectId, ct);
        if (actor == null) return new(false, "AccessDenied");
        source = Clean(source) ?? "";
        reference = Clean(reference);
        description = Clean(description) ?? "";
        if (source is not ("Consultant" or "Client" or "Internal") || reference?.Length > 100 ||
            description.Length is < 1 or > 2000) return new(false, "InvalidInstruction");
        var report = await db.Set<ConstructionDailySiteReport>().SingleOrDefaultAsync(x => x.ID == reportId &&
            x.CompanyID == actor.CompanyId && x.ProjectId == projectId, ct);
        if (report == null) return new(false, "NotFound");
        if (report.Status != "Draft" || report.CreatedBy != actor.EmployeeId) return new(false, "DraftOwnerRequired");
        var line = new ConstructionDsrInstructionLine
        {
            CompanyID = actor.CompanyId, ProjectId = projectId, SiteId = report.SiteId, ReportId = reportId,
            Source = source, InstructionReference = reference, Description = description
        };
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        db.Add(line);
        try
        {
            await db.SaveChangesAsync(ct);
            audit.Record(new[] { ConstructionAuditDraft.Text(actor.CompanyId, projectId, "DsrInstructionLine", line.ID,
                reportId, "Description", null, description, "Recorded", "Site instruction recorded", actor.EmployeeId,
                "Construction.DailySiteReport", Guid.NewGuid()) });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(true, "Saved", reportId);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(false, "InvalidInstruction");
        }
    }

    public async Task<ConstructionDailySiteReportResult> AddAttachmentAsync(int projectId, int reportId,
        int libraryItemId, string kind, string? caption, int? wbsNodeId, DateTime? takenAt,
        decimal? latitude, decimal? longitude, CancellationToken ct = default)
    {
        var actor = await ActorAsync(ProjectsActions.SiteReportCreate, projectId, ct);
        if (actor == null) return new(false, "AccessDenied");
        if (libraryItemId <= 0 || caption?.Length > 250 || kind is not ("Photo" or "Document") ||
            ((latitude.HasValue || longitude.HasValue) && (!latitude.HasValue || !longitude.HasValue ||
                latitude is < -90 or > 90 || longitude is < -180 or > 180)) ||
            (kind == "Document" && (wbsNodeId.HasValue || takenAt.HasValue || latitude.HasValue || longitude.HasValue)))
            return new(false, "InvalidAttachment");
        var report = await db.Set<ConstructionDailySiteReport>().SingleOrDefaultAsync(x => x.ID == reportId &&
            x.CompanyID == actor.CompanyId && x.ProjectId == projectId, ct);
        if (report == null) return new(false, "NotFound");
        if (report.Status != "Draft" || report.CreatedBy != actor.EmployeeId) return new(false, "DraftOwnerRequired");
        if (wbsNodeId.HasValue && !await db.Set<WbsNode>().AsNoTracking().AnyAsync(x => x.ID == wbsNodeId.Value &&
            x.CompanyID == actor.CompanyId && x.ProjectId == projectId && x.Status == "Active", ct))
            return new(false, "WbsNodeNotAvailable");
        var file = await db.LibraryItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == libraryItemId &&
            x.CompanyID == actor.CompanyId && x.DeletedAt == null && !x.IsFolder && x.StoredPath != null, ct);
        if (file == null) return new(false, "AttachmentNotAvailable");
        var exists = await db.Set<ConstructionDsrAttachment>().AnyAsync(x => x.CompanyID == actor.CompanyId &&
            x.ReportId == reportId && x.LibraryItemId == libraryItemId, ct);
        if (exists) return new(false, "AttachmentAlreadyAdded");
        var attachment = new ConstructionDsrAttachment
        {
            CompanyID = actor.CompanyId, ProjectId = projectId, SiteId = report.SiteId, ReportId = reportId,
            LibraryItemId = libraryItemId, Kind = kind, Caption = Clean(caption), WbsNodeId = wbsNodeId,
            TakenAt = takenAt, Latitude = latitude, Longitude = longitude,
            AddedAt = DateTime.UtcNow, AddedBy = actor.EmployeeId!.Value
        };
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        db.Add(attachment);
        try
        {
            await db.SaveChangesAsync(ct);
            audit.Record(new[] { ConstructionAuditDraft.Text(actor.CompanyId, projectId, "DsrAttachment", attachment.ID,
                reportId, "LibraryItemId", null, libraryItemId.ToString(), "Attached", "File attached to daily site report",
                actor.EmployeeId, "Construction.DailySiteReport", Guid.NewGuid()) });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(true, "Saved", reportId);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return new(false, "AttachmentAlreadyAdded");
        }
    }

    public async Task<ConstructionDailySiteReportResult> AddEventAsync(int projectId, int reportId, string eventType,
        string description, string severity, string? responsibleParty, int? wbsNodeId, DateTime? startedAt,
        DateTime? endedAt, DateTime? neededByDate, bool reportable, CancellationToken ct = default)
    {
        var actor = await ActorAsync(ProjectsActions.SiteReportCreate, projectId, ct);
        if (actor == null) return new(false, "AccessDenied");
        description = description?.Trim() ?? ""; responsibleParty = Clean(responsibleParty);
        if (reportId <= 0 || eventType is not ("Delay" or "Blocker" or "Incident") ||
            severity is not ("Low" or "Medium" or "High" or "Critical") || description.Length is < 1 or > 2000 ||
            responsibleParty?.Length > 100 || (startedAt.HasValue && endedAt.HasValue && endedAt < startedAt))
            return new(false, "InvalidEvent");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var report = await db.Set<ConstructionDailySiteReport>().SingleOrDefaultAsync(x => x.ID == reportId &&
            x.CompanyID == actor.CompanyId && x.ProjectId == projectId, ct);
        if (report == null) return new(false, "NotFound");
        if (report.Status != "Draft" || report.CreatedBy != actor.EmployeeId) return new(false, "DraftOwnerRequired");
        if (wbsNodeId.HasValue && !await db.Set<WbsNode>().AnyAsync(x => x.ID == wbsNodeId && x.CompanyID == actor.CompanyId &&
            x.ProjectId == projectId && x.Status == "Active", ct)) return new(false, "WbsNodeNotAvailable");
        var line = new ConstructionDsrEventLine { CompanyID = actor.CompanyId, ProjectId = projectId, SiteId = report.SiteId,
            ReportId = reportId, EventType = eventType, Description = description, Severity = severity,
            ResponsibleParty = responsibleParty, WbsNodeId = wbsNodeId, StartedAt = startedAt, EndedAt = endedAt,
            NeededByDate = neededByDate?.Date, Reportable = reportable };
        db.Add(line);
        try
        {
            await db.SaveChangesAsync(ct);
            audit.Record(new[] { ConstructionAuditDraft.Text(actor.CompanyId, projectId, "DsrEventLine", line.ID, reportId,
                "EventType", null, eventType + ":" + severity, "Created", "Site event evidence recorded; not a claim", actor.EmployeeId,
                "Construction.DailySiteReport", Guid.NewGuid()) });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(true, "Saved", reportId);
        }
        catch (DbUpdateException) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(false, "InvalidEvent"); }
    }

    public async Task<ConstructionDailySiteReportResult> AddMaterialAsync(int projectId, int reportId, string direction,
        int sourceLineId, decimal quantity, int? wbsNodeId, string? deliveryNoteNo, string? remarks,
        CancellationToken ct = default)
    {
        var actor = await ActorAsync(ProjectsActions.SiteReportCreate, projectId, ct);
        if (actor == null) return new(false, "AccessDenied");
        deliveryNoteNo = Clean(deliveryNoteNo); remarks = Clean(remarks);
        if (reportId <= 0 || sourceLineId <= 0 || quantity <= 0 || quantity > 1_000_000_000m ||
            direction is not ("Received" or "Consumed") || deliveryNoteNo?.Length > 100 || remarks?.Length > 1000)
            return new(false, "InvalidMaterialLine");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var report = await db.Set<ConstructionDailySiteReport>().SingleOrDefaultAsync(x => x.ID == reportId &&
            x.CompanyID == actor.CompanyId && x.ProjectId == projectId, ct);
        if (report == null) return new(false, "NotFound");
        if (report.Status != "Draft" || report.CreatedBy != actor.EmployeeId) return new(false, "DraftOwnerRequired");
        if (wbsNodeId.HasValue && !await db.Set<WbsNode>().AnyAsync(x => x.ID == wbsNodeId && x.CompanyID == actor.CompanyId &&
            x.ProjectId == projectId && x.Status == "Active", ct)) return new(false, "WbsNodeNotAvailable");

        int itemId; decimal sourceQuantity; int? receiptLineId = null; int? issueLineId = null;
        if (direction == "Received")
        {
            var source = await (from line in db.GoodsReceiptLines.AsNoTracking()
                join receipt in db.GoodsReceipts.AsNoTracking() on line.GoodsReceiptId equals receipt.ID
                join order in db.PurchaseOrders.AsNoTracking() on receipt.PurchaseOrderId equals order.ID
                where line.ID == sourceLineId && receipt.CompanyID == actor.CompanyId && receipt.Status == "Posted" &&
                    order.CompanyID == actor.CompanyId && order.ProjectId == projectId
                select new { line.ItemId, line.Qty }).SingleOrDefaultAsync(ct);
            if (source == null) return new(false, "MaterialSourceNotAvailable");
            itemId = source.ItemId; sourceQuantity = source.Qty; receiptLineId = sourceLineId;
        }
        else
        {
            var source = await (from line in db.ProjectMaterialIssueLines.AsNoTracking()
                join issue in db.ProjectMaterialIssues.AsNoTracking() on line.IssueId equals issue.ID
                where line.ID == sourceLineId && issue.CompanyID == actor.CompanyId && issue.ProjectId == projectId && issue.Status == "Posted"
                select new { line.ItemId, line.Qty }).SingleOrDefaultAsync(ct);
            if (source == null) return new(false, "MaterialSourceNotAvailable");
            itemId = source.ItemId; sourceQuantity = source.Qty; issueLineId = sourceLineId;
        }
        var recorded = await db.Set<ConstructionDsrMaterialLine>().Where(x => x.CompanyID == actor.CompanyId &&
            (receiptLineId.HasValue ? x.GoodsReceiptLineId == receiptLineId : x.ProjectMaterialIssueLineId == issueLineId))
            .SumAsync(x => (decimal?)x.Quantity, ct) ?? 0m;
        if (recorded + quantity > sourceQuantity) return new(false, "MaterialQuantityExceedsSource");
        var material = new ConstructionDsrMaterialLine { CompanyID = actor.CompanyId, ProjectId = projectId,
            SiteId = report.SiteId, ReportId = reportId, Direction = direction, GoodsReceiptLineId = receiptLineId,
            ProjectMaterialIssueLineId = issueLineId, ItemId = itemId, Quantity = decimal.Round(quantity, 3),
            WbsNodeId = wbsNodeId, DeliveryNoteNo = deliveryNoteNo, Remarks = remarks };
        db.Add(material);
        try
        {
            await db.SaveChangesAsync(ct);
            audit.Record(new[] { ConstructionAuditDraft.Text(actor.CompanyId, projectId, "DsrMaterialLine", material.ID, reportId,
                "Quantity", null, material.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture), "Created",
                direction == "Received" ? "Material receipt witnessed; no stock movement created" : "Material consumption witnessed; no stock movement created",
                actor.EmployeeId, "Construction.DailySiteReport", Guid.NewGuid()) });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(true, "Saved", reportId);
        }
        catch (DbUpdateException) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(false, "InvalidMaterialLine"); }
    }

    public async Task<ConstructionDailySiteReportResult> AddQuantityAsync(int projectId, int reportId, int wbsNodeId,
        int boqItemId, decimal quantity, string? location, string? remarks, CancellationToken ct = default)
    {
        var actor = await ActorAsync(ProjectsActions.SiteReportCreate, projectId, ct);
        if (actor == null) return new(false, "AccessDenied");
        location = Clean(location); remarks = Clean(remarks);
        if (reportId <= 0 || wbsNodeId <= 0 || boqItemId <= 0 || quantity <= 0 || quantity > 1_000_000_000m ||
            location?.Length > 200 || remarks?.Length > 1000) return new(false, "InvalidQuantityLine");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var report = await db.Set<ConstructionDailySiteReport>().SingleOrDefaultAsync(x => x.ID == reportId &&
            x.CompanyID == actor.CompanyId && x.ProjectId == projectId, ct);
        if (report == null) return new(false, "NotFound");
        if (report.Status != "Draft" || report.CreatedBy != actor.EmployeeId) return new(false, "DraftOwnerRequired");
        if (!await db.Set<WbsNode>().AnyAsync(x => x.ID == wbsNodeId && x.CompanyID == actor.CompanyId &&
            x.ProjectId == projectId && x.Status == "Active", ct)) return new(false, "WbsNodeNotAvailable");
        var boq = await db.BoqItems.AsNoTracking().SingleOrDefaultAsync(x => x.ID == boqItemId &&
            x.CompanyID == actor.CompanyId && x.ProjectId == projectId && x.Status == "Active", ct);
        if (boq == null) return new(false, "BoqItemNotAvailable");
        var line = new ConstructionDsrQuantityLine { CompanyID = actor.CompanyId, ProjectId = projectId,
            SiteId = report.SiteId, ReportId = reportId, WbsNodeId = wbsNodeId, BoqItemId = boqItemId,
            QuantityExecuted = decimal.Round(quantity, 3), Unit = string.IsNullOrWhiteSpace(boq.Unit) ? "—" : boq.Unit.Trim(),
            Location = location, Remarks = remarks };
        db.Add(line);
        try
        {
            await db.SaveChangesAsync(ct);
            audit.Record(new[] { ConstructionAuditDraft.Text(actor.CompanyId, projectId, "DsrQuantityLine", line.ID, reportId,
                "QuantityExecuted", null, line.QuantityExecuted.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Created", "Daily executed quantity recorded; not certified", actor.EmployeeId,
                "Construction.DailySiteReport", Guid.NewGuid()) });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(true, "Saved", reportId);
        }
        catch (DbUpdateException) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(false, "InvalidQuantityLine"); }
    }

    public async Task<ConstructionDailySiteReportResult> AddManpowerAsync(int projectId, int reportId, string source,
        string trade, int? employeeId, int? subcontractId, int headcount, int? wbsNodeId,
        decimal regularHours, decimal overtimeHours, CancellationToken ct = default)
    {
        var actor = await ActorAsync(ProjectsActions.SiteReportCreate, projectId, ct);
        if (actor == null) return new(false, "AccessDenied");
        trade = trade?.Trim() ?? "";
        if (reportId <= 0 || trade.Length is < 1 or > 100 || headcount is < 1 or > 10000 ||
            regularHours is < 0 or > 24 || overtimeHours is < 0 or > 24 ||
            (source == "OwnEmployee" ? employeeId is not > 0 || subcontractId.HasValue || headcount != 1
                : source != "Subcontractor" || employeeId.HasValue || subcontractId is not > 0))
            return new(false, "InvalidManpowerLine");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var report = await db.Set<ConstructionDailySiteReport>().SingleOrDefaultAsync(x => x.ID == reportId &&
            x.CompanyID == actor.CompanyId && x.ProjectId == projectId, ct);
        if (report == null) return new(false, "NotFound");
        if (report.Status != "Draft" || report.CreatedBy != actor.EmployeeId) return new(false, "DraftOwnerRequired");
        if (employeeId.HasValue && !await db.Employee.AnyAsync(x => x.ID == employeeId && x.EmpCompanyID == actor.CompanyId, ct))
            return new(false, "EmployeeNotAvailable");
        if (subcontractId.HasValue && !await db.Subcontracts.AnyAsync(x => x.ID == subcontractId && x.CompanyID == actor.CompanyId &&
            x.ProjectId == projectId && x.Status == "Active", ct)) return new(false, "SubcontractNotAvailable");
        if (wbsNodeId.HasValue && !await db.Set<WbsNode>().AnyAsync(x => x.ID == wbsNodeId && x.CompanyID == actor.CompanyId &&
            x.ProjectId == projectId && x.Status == "Active", ct)) return new(false, "WbsNodeNotAvailable");
        var line = new ConstructionDsrManpowerLine { CompanyID = actor.CompanyId, ProjectId = projectId,
            SiteId = report.SiteId, ReportId = reportId, Source = source, Trade = trade, EmployeeId = employeeId,
            SubcontractId = subcontractId, Headcount = headcount, WbsNodeId = wbsNodeId,
            RegularHours = regularHours, OvertimeHours = overtimeHours };
        db.Add(line);
        try
        {
            await db.SaveChangesAsync(ct);
            audit.Record(new[] { ConstructionAuditDraft.Text(actor.CompanyId, projectId, "DsrManpowerLine", line.ID, reportId,
                null, null, $"{source}:{trade}:{headcount}", "Created", "Daily report manpower recorded", actor.EmployeeId,
                "Construction.DailySiteReport", Guid.NewGuid()) });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(true, "Saved", reportId);
        }
        catch (DbUpdateException) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(false, "InvalidManpowerLine"); }
    }

    public async Task<ConstructionDailySiteReportResult> AddEquipmentAsync(int projectId, int reportId, int? fixedAssetId,
        int? subcontractId, string? equipmentDescription, int? operatorEmployeeId, int? wbsNodeId,
        decimal workingHours, decimal idleHours, decimal downtimeHours, string? downtimeReason,
        decimal? meterStart, decimal? meterEnd, decimal? fuelQuantity, CancellationToken ct = default)
    {
        var actor = await ActorAsync(ProjectsActions.SiteReportCreate, projectId, ct);
        if (actor == null) return new(false, "AccessDenied");
        equipmentDescription = Clean(equipmentDescription); downtimeReason = Clean(downtimeReason);
        if (reportId <= 0 || (fixedAssetId.HasValue == subcontractId.HasValue) ||
            (subcontractId.HasValue && (equipmentDescription?.Length is < 1 or > 200)) ||
            workingHours is < 0 or > 24 || idleHours is < 0 or > 24 || downtimeHours is < 0 or > 24 ||
            workingHours + idleHours + downtimeHours > 24 || downtimeReason?.Length > 1000 ||
            (meterStart.HasValue && meterStart < 0) || (meterEnd.HasValue && meterEnd < 0) ||
            (meterStart.HasValue && meterEnd.HasValue && meterEnd < meterStart) || fuelQuantity is < 0)
            return new(false, "InvalidEquipmentLine");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var report = await db.Set<ConstructionDailySiteReport>().SingleOrDefaultAsync(x => x.ID == reportId &&
            x.CompanyID == actor.CompanyId && x.ProjectId == projectId, ct);
        if (report == null) return new(false, "NotFound");
        if (report.Status != "Draft" || report.CreatedBy != actor.EmployeeId) return new(false, "DraftOwnerRequired");
        if (fixedAssetId.HasValue && !await db.FixedAssets.AnyAsync(x => x.ID == fixedAssetId && x.CompanyID == actor.CompanyId && x.Status == "Active", ct))
            return new(false, "AssetNotAvailable");
        if (subcontractId.HasValue && !await db.Subcontracts.AnyAsync(x => x.ID == subcontractId && x.CompanyID == actor.CompanyId &&
            x.ProjectId == projectId && x.Status == "Active", ct)) return new(false, "SubcontractNotAvailable");
        if (operatorEmployeeId.HasValue && !await db.Employee.AnyAsync(x => x.ID == operatorEmployeeId && x.EmpCompanyID == actor.CompanyId, ct))
            return new(false, "EmployeeNotAvailable");
        if (wbsNodeId.HasValue && !await db.Set<WbsNode>().AnyAsync(x => x.ID == wbsNodeId && x.CompanyID == actor.CompanyId &&
            x.ProjectId == projectId && x.Status == "Active", ct)) return new(false, "WbsNodeNotAvailable");
        var line = new ConstructionDsrEquipmentLine { CompanyID = actor.CompanyId, ProjectId = projectId, SiteId = report.SiteId,
            ReportId = reportId, FixedAssetId = fixedAssetId, SubcontractId = subcontractId, EquipmentDescription = equipmentDescription,
            OperatorEmployeeId = operatorEmployeeId, WbsNodeId = wbsNodeId, WorkingHours = workingHours, IdleHours = idleHours,
            DowntimeHours = downtimeHours, DowntimeReason = downtimeReason, MeterStart = meterStart, MeterEnd = meterEnd, FuelQuantity = fuelQuantity };
        db.Add(line);
        try
        {
            await db.SaveChangesAsync(ct);
            audit.Record(new[] { ConstructionAuditDraft.Text(actor.CompanyId, projectId, "DsrEquipmentLine", line.ID, reportId,
                null, null, fixedAssetId?.ToString() ?? subcontractId?.ToString(), "Created", "Daily report equipment recorded", actor.EmployeeId,
                "Construction.DailySiteReport", Guid.NewGuid()) });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new(true, "Saved", reportId);
        }
        catch (DbUpdateException) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(false, "InvalidEquipmentLine"); }
    }

    public async Task<ConstructionDailySiteReportResult> CreateDraftAsync(int projectId, int siteId,
        DateTime reportDate, string? weather, decimal? temperatureC, bool rainfallStopped,
        TimeSpan? workStart, TimeSpan? workEnd, decimal? totalHours, int shifts,
        string? nextDayPlan, string? remarks, CancellationToken ct = default)
    {
        var actor = await ActorAsync(ProjectsActions.SiteReportCreate, projectId, ct);
        if (actor == null) return new(false, "AccessDenied");
        if (siteId <= 0 || reportDate == default || reportDate.TimeOfDay != TimeSpan.Zero || shifts is < 1 or > 4 ||
            (temperatureC is < -80 or > 80) || (totalHours is < 0 or > 24) ||
            (workStart.HasValue && workEnd.HasValue && workEnd < workStart) ||
            (nextDayPlan?.Length > 2000) || (remarks?.Length > 4000) || (weather?.Length > 80))
            return new(false, "InvalidReport");

        var site = await db.Set<ConstructionSite>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.ID == siteId && x.CompanyID == actor.CompanyId && x.ProjectId == projectId && x.IsActive, ct);
        if (site == null) return new(false, "SiteNotAvailable");
        var duplicate = await db.Set<ConstructionDailySiteReport>().AsNoTracking().AnyAsync(x =>
            x.CompanyID == actor.CompanyId && x.ProjectId == projectId && x.SiteId == siteId &&
            x.ReportDate == reportDate, ct);
        if (duplicate) return new(false, "ReportAlreadyExists");

        var report = new ConstructionDailySiteReport
        {
            CompanyID = actor.CompanyId, ProjectId = projectId, SiteId = siteId, ReportDate = reportDate,
            WeatherCondition = Clean(weather), TemperatureC = temperatureC, RainfallStopped = rainfallStopped,
            WorkStartTime = workStart, WorkEndTime = workEnd, TotalWorkingHours = totalHours, ShiftCount = shifts,
            Status = "Draft", NextDayPlan = Clean(nextDayPlan), GeneralRemarks = Clean(remarks),
            CreatedBy = actor.EmployeeId!.Value, CreatedAt = DateTime.UtcNow
        };
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        db.Add(report);
        try
        {
            await db.SaveChangesAsync(ct);
            audit.Record(new[] { ConstructionAuditDraft.Text(actor.CompanyId, projectId, Entity, report.ID, null,
                "Status", null, "Draft", "Created", "Daily site report created", actor.EmployeeId,
                "Construction.DailySiteReport", Guid.NewGuid()) });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(true, "Saved", report.ID, report.RowVersion);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            return new(false, "ReportAlreadyExists");
        }
    }

    public async Task<ConstructionDailySiteReportResult> CreateAmendmentAsync(int projectId, int reportId,
        string reason, CancellationToken ct = default)
    {
        var actor = await ActorAsync(ProjectsActions.SiteReportCreate, projectId, ct);
        if (actor == null) return new(false, "AccessDenied");
        reason = reason?.Trim() ?? "";
        if (reportId <= 0 || reason.Length is 0 or > 1000) return new(false, "InvalidAmendment");

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var parent = await db.Set<ConstructionDailySiteReport>().SingleOrDefaultAsync(x =>
            x.ID == reportId && x.CompanyID == actor.CompanyId && x.ProjectId == projectId, ct);
        if (parent == null) return new(false, "NotFound");
        if (parent.Status != "Approved") return new(false, "AmendmentRequiresApprovedReport", parent.ID);

        var laterRevisionExists = await db.Set<ConstructionDailySiteReport>().AsNoTracking().AnyAsync(x =>
            x.CompanyID == actor.CompanyId && x.ProjectId == projectId && x.SiteId == parent.SiteId &&
            x.ReportDate == parent.ReportDate && x.RevisionNo > parent.RevisionNo, ct);
        if (laterRevisionExists) return new(false, "AmendmentAlreadyExists", parent.ID);

        var amendment = new ConstructionDailySiteReport
        {
            CompanyID = parent.CompanyID, ProjectId = parent.ProjectId, SiteId = parent.SiteId,
            ReportDate = parent.ReportDate, RevisionNo = parent.RevisionNo + 1,
            AmendsReportId = parent.ID, AmendmentReason = reason,
            WeatherCondition = parent.WeatherCondition, TemperatureC = parent.TemperatureC,
            RainfallStopped = parent.RainfallStopped, WorkStartTime = parent.WorkStartTime,
            WorkEndTime = parent.WorkEndTime, TotalWorkingHours = parent.TotalWorkingHours,
            ShiftCount = parent.ShiftCount, Status = "Draft", NextDayPlan = parent.NextDayPlan,
            GeneralRemarks = parent.GeneralRemarks, CreatedBy = actor.EmployeeId!.Value,
            CreatedAt = DateTime.UtcNow
        };
        db.Add(amendment);
        try
        {
            await db.SaveChangesAsync(ct);
            await CopyLinesAsync<ConstructionDsrManpowerLine>(parent.ID, amendment.ID, actor.CompanyId, ct);
            await CopyLinesAsync<ConstructionDsrEquipmentLine>(parent.ID, amendment.ID, actor.CompanyId, ct);
            await CopyLinesAsync<ConstructionDsrQuantityLine>(parent.ID, amendment.ID, actor.CompanyId, ct);
            await CopyLinesAsync<ConstructionDsrMaterialLine>(parent.ID, amendment.ID, actor.CompanyId, ct);
            await CopyLinesAsync<ConstructionDsrEventLine>(parent.ID, amendment.ID, actor.CompanyId, ct);
            await CopyLinesAsync<ConstructionDsrAttachment>(parent.ID, amendment.ID, actor.CompanyId, ct);
            await CopyLinesAsync<ConstructionDsrVisitorLine>(parent.ID, amendment.ID, actor.CompanyId, ct);
            await CopyLinesAsync<ConstructionDsrInstructionLine>(parent.ID, amendment.ID, actor.CompanyId, ct);
            audit.Record(new[] { ConstructionAuditDraft.Text(actor.CompanyId, projectId, Entity, amendment.ID, null,
                "Revision", parent.RevisionNo.ToString(), amendment.RevisionNo.ToString(), "AmendmentCreated",
                reason, actor.EmployeeId, "Construction.DailySiteReport", Guid.NewGuid()) });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(true, "Saved", amendment.ID, amendment.RowVersion);
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            return new(false, "AmendmentAlreadyExists", parent.ID);
        }
    }

    private async Task CopyLinesAsync<T>(int sourceReportId, int targetReportId, int companyId,
        CancellationToken ct) where T : class, new()
    {
        var sourceLines = await db.Set<T>().AsNoTracking().Where(x =>
            EF.Property<int>(x, "CompanyID") == companyId &&
            EF.Property<int>(x, "ReportId") == sourceReportId).ToListAsync(ct);
        var properties = typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(x => x.CanRead && x.CanWrite && x.Name is not ("ID" or "RowVersion"));
        var reportIdProperty = typeof(T).GetProperty("ReportId")
            ?? throw new InvalidOperationException($"{typeof(T).Name} does not have a report owner.");
        foreach (var source in sourceLines)
        {
            var copy = new T();
            foreach (var property in properties) property.SetValue(copy, property.GetValue(source));
            reportIdProperty.SetValue(copy, targetReportId);
            db.Add(copy);
        }
    }

    public async Task<ConstructionDailySiteReportResult> RemoveAmendmentLineAsync(int projectId, int reportId,
        string lineType, int lineId, string reason, CancellationToken ct = default)
    {
        var actor = await ActorAsync(ProjectsActions.SiteReportCreate, projectId, ct);
        if (actor == null) return new(false, "AccessDenied");
        reason = reason?.Trim() ?? "";
        if (lineId <= 0 || reason.Length is 0 or > 1000) return new(false, "InvalidAmendment");
        var report = await db.Set<ConstructionDailySiteReport>().SingleOrDefaultAsync(x =>
            x.ID == reportId && x.CompanyID == actor.CompanyId && x.ProjectId == projectId, ct);
        if (report == null) return new(false, "NotFound");
        if (report.Status != "Draft" || report.AmendsReportId == null || report.CreatedBy != actor.EmployeeId)
            return new(false, "AmendmentDraftOnly", report.ID);

        var removed = lineType switch
        {
            "Manpower" => await RemoveLineAsync<ConstructionDsrManpowerLine>(lineId, report, actor.CompanyId, ct),
            "Equipment" => await RemoveLineAsync<ConstructionDsrEquipmentLine>(lineId, report, actor.CompanyId, ct),
            "Quantity" => await RemoveLineAsync<ConstructionDsrQuantityLine>(lineId, report, actor.CompanyId, ct),
            "Material" => await RemoveLineAsync<ConstructionDsrMaterialLine>(lineId, report, actor.CompanyId, ct),
            "Event" => await RemoveLineAsync<ConstructionDsrEventLine>(lineId, report, actor.CompanyId, ct),
            "Attachment" => await RemoveLineAsync<ConstructionDsrAttachment>(lineId, report, actor.CompanyId, ct),
            "Visitor" => await RemoveLineAsync<ConstructionDsrVisitorLine>(lineId, report, actor.CompanyId, ct),
            "Instruction" => await RemoveLineAsync<ConstructionDsrInstructionLine>(lineId, report, actor.CompanyId, ct),
            _ => false
        };
        if (!removed) return new(false, "AmendmentLineNotFound", report.ID);

        audit.Record(new[] { ConstructionAuditDraft.Text(actor.CompanyId, projectId, Entity, report.ID, null,
            lineType, lineId.ToString(), null, "AmendmentLineRemoved", reason, actor.EmployeeId,
            "Construction.DailySiteReport", Guid.NewGuid()) });
        await db.SaveChangesAsync(ct);
        return new(true, "Saved", report.ID, report.RowVersion);
    }

    private async Task<bool> RemoveLineAsync<T>(int lineId, ConstructionDailySiteReport report, int companyId,
        CancellationToken ct) where T : class
    {
        var line = await db.Set<T>().SingleOrDefaultAsync(x => EF.Property<int>(x, "ID") == lineId &&
            EF.Property<int>(x, "ReportId") == report.ID && EF.Property<int>(x, "CompanyID") == companyId, ct);
        if (line == null) return false;
        db.Remove(line);
        return true;
    }

    public async Task<ConstructionDailySiteReportResult> UpdateAmendmentHeaderAsync(int projectId, int reportId,
        byte[] expectedVersion, string? weather, decimal? temperatureC, bool rainfallStopped,
        TimeSpan? workStart, TimeSpan? workEnd, decimal? totalHours, int shifts,
        string? nextDayPlan, string? remarks, string reason, CancellationToken ct = default)
    {
        var actor = await ActorAsync(ProjectsActions.SiteReportCreate, projectId, ct);
        if (actor == null) return new(false, "AccessDenied");
        reason = reason?.Trim() ?? "";
        if (reportId <= 0 || expectedVersion is not { Length: > 0 } || reason.Length is 0 or > 1000 ||
            shifts is < 1 or > 4 || temperatureC is < -80 or > 80 || totalHours is < 0 or > 24 ||
            (workStart.HasValue && workEnd.HasValue && workEnd < workStart) ||
            weather?.Length > 80 || nextDayPlan?.Length > 2000 || remarks?.Length > 4000)
            return new(false, "InvalidReport");

        var report = await db.Set<ConstructionDailySiteReport>().SingleOrDefaultAsync(x =>
            x.ID == reportId && x.CompanyID == actor.CompanyId && x.ProjectId == projectId, ct);
        if (report == null) return new(false, "NotFound");
        if (report.Status != "Draft" || report.AmendsReportId == null || report.CreatedBy != actor.EmployeeId)
            return new(false, "AmendmentDraftOnly", report.ID);
        if (!report.RowVersion.SequenceEqual(expectedVersion))
            return new(false, "ConcurrencyConflict", report.ID, report.RowVersion);

        var previous = new Dictionary<string, string?>
        {
            ["WeatherCondition"] = report.WeatherCondition,
            ["TemperatureC"] = report.TemperatureC?.ToString(),
            ["RainfallStopped"] = report.RainfallStopped.ToString(),
            ["WorkStartTime"] = report.WorkStartTime?.ToString(),
            ["WorkEndTime"] = report.WorkEndTime?.ToString(),
            ["TotalWorkingHours"] = report.TotalWorkingHours?.ToString(),
            ["ShiftCount"] = report.ShiftCount.ToString(),
            ["NextDayPlan"] = report.NextDayPlan,
            ["GeneralRemarks"] = report.GeneralRemarks
        };
        report.WeatherCondition = Clean(weather); report.TemperatureC = temperatureC;
        report.RainfallStopped = rainfallStopped; report.WorkStartTime = workStart;
        report.WorkEndTime = workEnd; report.TotalWorkingHours = totalHours;
        report.ShiftCount = shifts; report.NextDayPlan = Clean(nextDayPlan);
        report.GeneralRemarks = Clean(remarks);
        db.Entry(report).Property(x => x.RowVersion).OriginalValue = expectedVersion;
        var current = new Dictionary<string, string?>
        {
            ["WeatherCondition"] = report.WeatherCondition,
            ["TemperatureC"] = report.TemperatureC?.ToString(),
            ["RainfallStopped"] = report.RainfallStopped.ToString(),
            ["WorkStartTime"] = report.WorkStartTime?.ToString(),
            ["WorkEndTime"] = report.WorkEndTime?.ToString(),
            ["TotalWorkingHours"] = report.TotalWorkingHours?.ToString(),
            ["ShiftCount"] = report.ShiftCount.ToString(),
            ["NextDayPlan"] = report.NextDayPlan,
            ["GeneralRemarks"] = report.GeneralRemarks
        };
        audit.Record(previous.Where(x => x.Value != current[x.Key]).Select(x => ConstructionAuditDraft.Text(
            actor.CompanyId, projectId, Entity, report.ID, null, x.Key, x.Value, current[x.Key],
            "AmendmentHeaderUpdated", reason, actor.EmployeeId, "Construction.DailySiteReport", Guid.NewGuid())));
        try
        {
            await db.SaveChangesAsync(ct);
            return new(true, "Saved", report.ID, report.RowVersion);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return new(false, "ConcurrencyConflict");
        }
    }

    public async Task<ConstructionDailySiteReportResult> TransitionAsync(int projectId, int reportId,
        string transition, byte[] expectedVersion, string reason, CancellationToken ct = default)
    {
        var action = transition switch
        {
            "Submit" => ProjectsActions.SiteReportCreate,
            "Review" => ProjectsActions.SiteReportReview,
            "Approve" => ProjectsActions.SiteReportApprove,
            _ => ""
        };
        var actor = action.Length == 0 ? null : await ActorAsync(action, projectId, ct);
        if (actor == null) return new(false, action.Length == 0 ? "InvalidTransition" : "AccessDenied");
        if (reportId <= 0 || expectedVersion is not { Length: > 0 } || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000)
            return new(false, "InvalidTransition");

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var report = await db.Set<ConstructionDailySiteReport>().SingleOrDefaultAsync(x =>
            x.ID == reportId && x.CompanyID == actor.CompanyId && x.ProjectId == projectId, ct);
        if (report == null) return new(false, "NotFound");
        if (!report.RowVersion.SequenceEqual(expectedVersion)) return new(false, "ConcurrencyConflict", report.ID, report.RowVersion);
        if (transition is "Review" or "Approve" && report.CreatedBy == actor.EmployeeId)
            return new(false, "SeparateReviewerRequired", report.ID, report.RowVersion);
        if (transition == "Approve" && report.ReviewedBy == actor.EmployeeId)
            return new(false, "SeparateApproverRequired", report.ID, report.RowVersion);

        var previous = report.Status;
        switch (transition, previous)
        {
            case ("Submit", "Draft"):
                if (report.CreatedBy != actor.EmployeeId)
                    return new(false, "OnlyCreatorCanSubmit", report.ID, report.RowVersion);
                report.Status = "Submitted"; report.SubmittedBy = actor.EmployeeId; report.SubmittedAt = DateTime.UtcNow; break;
            case ("Review", "Submitted"):
                report.Status = "Reviewed"; report.ReviewedBy = actor.EmployeeId; report.ReviewedAt = DateTime.UtcNow; break;
            case ("Approve", "Reviewed"):
                report.Status = "Approved"; report.ApprovedBy = actor.EmployeeId; report.ApprovedAt = DateTime.UtcNow; break;
            default: return new(false, previous == "Approved" ? "ApprovedReportIsImmutable" : "InvalidTransition", report.ID, report.RowVersion);
        }

        db.Entry(report).Property(x => x.RowVersion).OriginalValue = expectedVersion;
        audit.Record(new[] { ConstructionAuditDraft.Text(actor.CompanyId, projectId, Entity, report.ID, null,
            "Status", previous, report.Status, transition, reason.Trim(), actor.EmployeeId,
            "Construction.DailySiteReport", Guid.NewGuid()) });
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(true, "Saved", report.ID, report.RowVersion);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return new(false, "ConcurrencyConflict");
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
