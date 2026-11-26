using CrossBuy.BL.Construction;
using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers;

[SessionValidation, Route("Project/DailySiteReports")]
public sealed class ConstructionDailySiteReportsController(IConstructionDailySiteReportService reports,
    IBusinessContextAccessor contexts, IFileManagerService files, IWebHostEnvironment environment,
    ILogger<ConstructionDailySiteReportsController> logger) : Controller
{
    [HttpGet, ProjectPerm(ProjectsActions.SiteReportRead)]
    public async Task<IActionResult> Index(int projectId)
    {
        var page = await reports.ReadAsync(projectId, HttpContext.RequestAborted);
        return page == null ? Forbid() : View("~/Views/Project/DailySiteReports.cshtml", page);
    }

    [HttpPost("Create"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.SiteReportCreate)]
    public async Task<IActionResult> Create(int projectId, int siteId, DateTime reportDate,
        string? weather, decimal? temperatureC, bool rainfallStopped, TimeSpan? workStart,
        TimeSpan? workEnd, decimal? totalHours, int shifts, string? nextDayPlan, string? remarks)
    {
        var result = ModelState.IsValid
            ? await reports.CreateDraftAsync(projectId, siteId, reportDate, weather, temperatureC,
                rainfallStopped, workStart, workEnd, totalHours, shifts, nextDayPlan, remarks, HttpContext.RequestAborted)
            : new ConstructionDailySiteReportResult(false, "InvalidReport");
        if (result.Code == "AccessDenied") return Forbid();
        TempData[result.Ok ? "DsrMessage" : "DsrError"] = result.Ok ? "Saved" : result.Code;
        return RedirectToAction(nameof(Index), new { projectId,
            culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            ui_culture = System.Globalization.CultureInfo.CurrentUICulture.Name });
    }

    [HttpPost("{reportId:int}/Amend"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.SiteReportCreate)]
    public async Task<IActionResult> Amend(int projectId, int reportId, string? reason)
    {
        var result = ModelState.IsValid
            ? await reports.CreateAmendmentAsync(projectId, reportId, reason ?? "", HttpContext.RequestAborted)
            : new ConstructionDailySiteReportResult(false, "InvalidAmendment");
        if (result.Code == "AccessDenied") return Forbid();
        TempData[result.Ok ? "DsrMessage" : "DsrError"] = result.Ok ? "Saved" : result.Code;
        return RedirectToAction(nameof(Index), new { projectId,
            culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            ui_culture = System.Globalization.CultureInfo.CurrentUICulture.Name });
    }

    [HttpPost("{reportId:int}/Amend/Header"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.SiteReportCreate)]
    public async Task<IActionResult> UpdateAmendmentHeader(int projectId, int reportId, string? expectedVersion,
        string? weather, decimal? temperatureC, bool rainfallStopped, TimeSpan? workStart, TimeSpan? workEnd,
        decimal? totalHours, int shifts, string? nextDayPlan, string? remarks, string? reason)
    {
        byte[] version;
        try { version = Convert.FromBase64String(expectedVersion ?? ""); }
        catch (FormatException) { version = Array.Empty<byte>(); }
        var result = ModelState.IsValid
            ? await reports.UpdateAmendmentHeaderAsync(projectId, reportId, version, weather, temperatureC,
                rainfallStopped, workStart, workEnd, totalHours, shifts, nextDayPlan, remarks, reason ?? "",
                HttpContext.RequestAborted)
            : new ConstructionDailySiteReportResult(false, "InvalidReport");
        if (result.Code == "AccessDenied") return Forbid();
        TempData[result.Ok ? "DsrMessage" : "DsrError"] = result.Ok ? "Saved" : result.Code;
        return RedirectToAction(nameof(Index), new { projectId,
            culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            ui_culture = System.Globalization.CultureInfo.CurrentUICulture.Name });
    }

    [HttpPost("{reportId:int}/Amend/RemoveLine"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.SiteReportCreate)]
    public async Task<IActionResult> RemoveAmendmentLine(int projectId, int reportId, string? target, string? reason)
    {
        var parts = (target ?? "").Split(':', 2);
        var lineType = parts.Length == 2 ? parts[0] : "";
        var lineId = parts.Length == 2 && int.TryParse(parts[1], out var parsedId) ? parsedId : 0;
        var result = ModelState.IsValid
            ? await reports.RemoveAmendmentLineAsync(projectId, reportId, lineType, lineId,
                reason ?? "", HttpContext.RequestAborted)
            : new ConstructionDailySiteReportResult(false, "InvalidAmendment");
        if (result.Code == "AccessDenied") return Forbid();
        TempData[result.Ok ? "DsrMessage" : "DsrError"] = result.Ok ? "Saved" : result.Code;
        return RedirectToAction(nameof(Index), new { projectId,
            culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            ui_culture = System.Globalization.CultureInfo.CurrentUICulture.Name });
    }

    [HttpPost("{reportId:int}/Transition"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.SiteReportRead)]
    public async Task<IActionResult> Transition(int projectId, int reportId, string transition,
        string? expectedVersion, string? reason)
    {
        byte[] version;
        try { version = Convert.FromBase64String(expectedVersion ?? ""); }
        catch (FormatException) { version = Array.Empty<byte>(); }
        var result = ModelState.IsValid
            ? await reports.TransitionAsync(projectId, reportId, transition, version, reason ?? "", HttpContext.RequestAborted)
            : new ConstructionDailySiteReportResult(false, "InvalidTransition");
        if (result.Code == "AccessDenied") return Forbid();
        TempData[result.Ok ? "DsrMessage" : "DsrError"] = result.Ok ? "Saved" : result.Code;
        return RedirectToAction(nameof(Index), new { projectId,
            culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            ui_culture = System.Globalization.CultureInfo.CurrentUICulture.Name });
    }

    [HttpPost("{reportId:int}/Manpower"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.SiteReportCreate)]
    public async Task<IActionResult> AddManpower(int projectId, int reportId, string source, string trade,
        int? employeeId, int? subcontractId, int headcount, int? wbsNodeId, decimal regularHours, decimal overtimeHours)
    {
        var result = await reports.AddManpowerAsync(projectId, reportId, source, trade, employeeId,
            subcontractId, headcount, wbsNodeId, regularHours, overtimeHours, HttpContext.RequestAborted);
        if (result.Code == "AccessDenied") return Forbid();
        TempData[result.Ok ? "DsrMessage" : "DsrError"] = result.Ok ? "Saved" : result.Code;
        return RedirectToAction(nameof(Index), new { projectId, culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            ui_culture = System.Globalization.CultureInfo.CurrentUICulture.Name });
    }

    [HttpPost("{reportId:int}/Equipment"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.SiteReportCreate)]
    public async Task<IActionResult> AddEquipment(int projectId, int reportId, int? fixedAssetId, int? subcontractId,
        string? equipmentDescription, int? operatorEmployeeId, int? wbsNodeId, decimal workingHours, decimal idleHours,
        decimal downtimeHours, string? downtimeReason, decimal? meterStart, decimal? meterEnd, decimal? fuelQuantity)
    {
        var result = await reports.AddEquipmentAsync(projectId, reportId, fixedAssetId, subcontractId,
            equipmentDescription, operatorEmployeeId, wbsNodeId, workingHours, idleHours, downtimeHours,
            downtimeReason, meterStart, meterEnd, fuelQuantity, HttpContext.RequestAborted);
        if (result.Code == "AccessDenied") return Forbid();
        TempData[result.Ok ? "DsrMessage" : "DsrError"] = result.Ok ? "Saved" : result.Code;
        return RedirectToAction(nameof(Index), new { projectId, culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            ui_culture = System.Globalization.CultureInfo.CurrentUICulture.Name });
    }

    [HttpPost("{reportId:int}/Quantity"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.SiteReportCreate)]
    public async Task<IActionResult> AddQuantity(int projectId, int reportId, int wbsNodeId, int boqItemId,
        decimal quantity, string? location, string? remarks)
    {
        var result = await reports.AddQuantityAsync(projectId, reportId, wbsNodeId, boqItemId,
            quantity, location, remarks, HttpContext.RequestAborted);
        if (result.Code == "AccessDenied") return Forbid();
        TempData[result.Ok ? "DsrMessage" : "DsrError"] = result.Ok ? "Saved" : result.Code;
        return RedirectToAction(nameof(Index), new { projectId, culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            ui_culture = System.Globalization.CultureInfo.CurrentUICulture.Name });
    }

    [HttpPost("{reportId:int}/Material"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.SiteReportCreate)]
    public async Task<IActionResult> AddMaterial(int projectId, int reportId, string direction, int sourceLineId,
        decimal quantity, int? wbsNodeId, string? deliveryNoteNo, string? remarks)
    {
        var result = await reports.AddMaterialAsync(projectId, reportId, direction, sourceLineId,
            quantity, wbsNodeId, deliveryNoteNo, remarks, HttpContext.RequestAborted);
        if (result.Code == "AccessDenied") return Forbid();
        TempData[result.Ok ? "DsrMessage" : "DsrError"] = result.Ok ? "Saved" : result.Code;
        return RedirectToAction(nameof(Index), new { projectId, culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            ui_culture = System.Globalization.CultureInfo.CurrentUICulture.Name });
    }

    [HttpPost("{reportId:int}/Event"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.SiteReportCreate)]
    public async Task<IActionResult> AddEvent(int projectId, int reportId, string eventType, string description,
        string severity, string? responsibleParty, int? wbsNodeId, DateTime? startedAt, DateTime? endedAt,
        DateTime? neededByDate, bool reportable)
    {
        var result = await reports.AddEventAsync(projectId, reportId, eventType, description, severity,
            responsibleParty, wbsNodeId, startedAt, endedAt, neededByDate, reportable, HttpContext.RequestAborted);
        if (result.Code == "AccessDenied") return Forbid();
        TempData[result.Ok ? "DsrMessage" : "DsrError"] = result.Ok ? "Saved" : result.Code;
        return RedirectToAction(nameof(Index), new { projectId, culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            ui_culture = System.Globalization.CultureInfo.CurrentUICulture.Name });
    }

    [HttpPost("{reportId:int}/Attachment"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.SiteReportCreate)]
    public async Task<IActionResult> AddAttachment(int projectId, int reportId, int libraryItemId, string kind,
        string? caption, int? wbsNodeId, DateTime? takenAt, decimal? latitude, decimal? longitude)
    {
        var result = await reports.AddAttachmentAsync(projectId, reportId, libraryItemId, kind, caption,
            wbsNodeId, takenAt, latitude, longitude, HttpContext.RequestAborted);
        if (result.Code == "AccessDenied") return Forbid();
        TempData[result.Ok ? "DsrMessage" : "DsrError"] = result.Ok ? "Saved" : result.Code;
        return RedirectToAction(nameof(Index), new { projectId, culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            ui_culture = System.Globalization.CultureInfo.CurrentUICulture.Name });
    }

    [HttpPost("{reportId:int}/Visitor"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.SiteReportCreate)]
    public async Task<IActionResult> AddVisitor(int projectId, int reportId, string visitorName,
        string? organisation, string purpose, DateTime? timeIn, DateTime? timeOut)
    {
        var result = await reports.AddVisitorAsync(projectId, reportId, visitorName, organisation, purpose,
            timeIn, timeOut, HttpContext.RequestAborted);
        if (result.Code == "AccessDenied") return Forbid();
        TempData[result.Ok ? "DsrMessage" : "DsrError"] = result.Ok ? "Saved" : result.Code;
        return RedirectToAction(nameof(Index), new { projectId, culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            ui_culture = System.Globalization.CultureInfo.CurrentUICulture.Name });
    }

    [HttpPost("{reportId:int}/Instruction"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.SiteReportCreate)]
    public async Task<IActionResult> AddInstruction(int projectId, int reportId, string source,
        string? reference, string description)
    {
        var result = await reports.AddInstructionAsync(projectId, reportId, source, reference, description,
            HttpContext.RequestAborted);
        if (result.Code == "AccessDenied") return Forbid();
        TempData[result.Ok ? "DsrMessage" : "DsrError"] = result.Ok ? "Saved" : result.Code;
        return RedirectToAction(nameof(Index), new { projectId, culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            ui_culture = System.Globalization.CultureInfo.CurrentUICulture.Name });
    }

    [HttpPost("{reportId:int}/UploadAttachment"), ValidateAntiForgeryToken, ProjectPerm(ProjectsActions.SiteReportCreate),
     RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> UploadAttachment(int projectId, int reportId, IFormFile? file, string? caption,
        int? wbsNodeId, DateTime? takenAt, decimal? latitude, decimal? longitude)
    {
        var actor = await contexts.TryGetCurrentAsync(HttpContext.RequestAborted);
        if (actor is not { IsAuthenticated: true, IsExternalPortal: false } || actor.CompanyId <= 0 || actor.EmployeeId is not > 0)
            return Forbid();
        if (file == null || file.Length is <= 0 or > 20 * 1024 * 1024 || caption?.Length > 250)
            return RedirectWithDsrError(projectId, "InvalidAttachment");

        var extension = Path.GetExtension(Path.GetFileName(file.FileName)).ToLowerInvariant();
        var contentType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            _ => null
        };
        if (contentType == null) return RedirectWithDsrError(projectId, "UnsupportedAttachmentType");
        var signature = new byte[12];
        int signatureLength;
        await using (var source = file.OpenReadStream())
            signatureLength = await source.ReadAsync(signature.AsMemory(), HttpContext.RequestAborted);
        if (!HasExpectedSignature(extension, signature, signatureLength))
            return RedirectWithDsrError(projectId, "UnsupportedAttachmentType");

        var originalName = Path.GetFileName(file.FileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(originalName) || originalName.Length > 400)
            return RedirectWithDsrError(projectId, "InvalidAttachment");
        var storedName = Guid.NewGuid().ToString("N") + extension;
        var webRoot = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
        var directory = Path.Combine(webRoot, "uploads", "library", actor.CompanyId.ToString());
        var physicalPath = Path.Combine(directory, storedName);
        var webPath = $"/uploads/library/{actor.CompanyId}/{storedName}";
        var libraryItemId = 0;
        try
        {
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(physicalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await file.CopyToAsync(stream, HttpContext.RequestAborted);
            libraryItemId = await files.AddFileAsync(actor.CompanyId, actor.EmployeeId.Value, null,
                originalName, null, webPath, contentType, file.Length);
            var kind = contentType.StartsWith("image/", StringComparison.Ordinal) ? "Photo" : "Document";
            var result = await reports.AddAttachmentAsync(projectId, reportId, libraryItemId, kind, caption,
                wbsNodeId, takenAt, latitude, longitude, HttpContext.RequestAborted);
            if (!result.Ok)
            {
                await files.DeleteAsync(actor.CompanyId, actor.EmployeeId.Value, new[] { libraryItemId });
                System.IO.File.Delete(physicalPath);
                TempData["DsrError"] = result.Code;
            }
            else TempData["DsrMessage"] = "Saved";
        }
        catch (OperationCanceledException)
        {
            await CleanupUploadedFileAsync(actor.CompanyId, actor.EmployeeId.Value, libraryItemId, physicalPath);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Daily site report attachment upload failed for project {ProjectId}, report {ReportId}", projectId, reportId);
            await CleanupUploadedFileAsync(actor.CompanyId, actor.EmployeeId.Value, libraryItemId, physicalPath);
            TempData["DsrError"] = "AttachmentUploadFailed";
        }
        return RedirectToAction(nameof(Index), new { projectId, culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            ui_culture = System.Globalization.CultureInfo.CurrentUICulture.Name });
    }

    private IActionResult RedirectWithDsrError(int projectId, string code)
    {
        TempData["DsrError"] = code;
        return RedirectToAction(nameof(Index), new { projectId, culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            ui_culture = System.Globalization.CultureInfo.CurrentUICulture.Name });
    }

    private static bool HasExpectedSignature(string extension, byte[] bytes, int length) => extension switch
    {
        ".jpg" or ".jpeg" => length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
        ".png" => length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        ".webp" => length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        ".pdf" => length >= 5 && bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8),
        _ => false
    };

    private async Task CleanupUploadedFileAsync(int companyId, int employeeId, int libraryItemId, string physicalPath)
    {
        if (libraryItemId > 0)
        {
            try { await files.DeleteAsync(companyId, employeeId, new[] { libraryItemId }); }
            catch (Exception cleanupError)
            {
                logger.LogError(cleanupError, "Failed to clean up File Manager item {LibraryItemId}; preserving its file", libraryItemId);
                return;
            }
        }
        try { if (System.IO.File.Exists(physicalPath)) System.IO.File.Delete(physicalPath); }
        catch (Exception cleanupError) { logger.LogError(cleanupError, "Failed to remove rejected daily site report upload"); }
    }
}
