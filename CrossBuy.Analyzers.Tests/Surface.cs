namespace CrossBuy.Analyzers.Tests;

/// <summary>
/// The CrossBuy authorization surface, declared as C# source for the pattern tests.
///
/// WHY A STUB RATHER THAN THE REAL TYPES. These tests assert the analyzer's RULES: that an access service call is
/// credited, that a lane guard is not, that a helper chain resolves. Compiling the real 900-file application for
/// each of thirty cases would make them slow and would couple a rule test to unrelated application changes.
///
/// The stub declares the same NAMES and the same SHAPES the analyzer matches on — that is the whole contract, and
/// the reconciliation test then proves those names are the real ones by running the same engine over the real
/// sources and reproducing 388/157/88/143. A stub alone would prove the analyzer agrees with itself; the
/// reconciliation is what ties it to the application.
///
/// MVC types are NOT stubbed. [HttpPost], [AllowAnonymous], ControllerBase and Controller come from the real
/// ASP.NET Core reference assemblies, so a verb attribute cannot pass a test by matching a shape MVC lacks.
/// </summary>
internal static class Surface
{
    /// <summary>
    /// The using block every test controller starts with. It is a separate constant, and the harness — not the
    /// test — decides where it goes, because C# requires every using to precede every declaration: composing
    /// "surface + usings + controller" is a compile error, and a compile error in a semantic test proves nothing
    /// while looking like a finding.
    /// </summary>
    internal const string Preamble = """
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using CrossBuy.BL;
using CrossBuy.Models;

""";

    /// <summary>
    /// Declarations only — no using directives. It is appended AFTER the controller under test, and a using may
    /// not follow a declaration. <see cref="Preamble"/> supplies the usings once, at the top of the file.
    /// </summary>
    internal const string Source = """
namespace CrossBuy.Models
{
    // --- credited: module permission attributes ---
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
    public sealed class AccPermAttribute : Attribute { public AccPermAttribute(string action) { } }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
    public sealed class InvPermAttribute : Attribute { public InvPermAttribute(string action) { } }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ApiPermAttribute : Attribute { public ApiPermAttribute(string module, string action) { } }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
    public sealed class PlatformOpsAttribute : Attribute { public PlatformOpsAttribute(bool elevated = false) { } }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TaskPermAttribute : Attribute { public TaskPermAttribute(string action) { } }

    // --- NEVER credited ---
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
    public sealed class SessionValidationAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
    public sealed class PosLaneActivityGuardAttribute : Attribute { public PosLaneActivityGuardAttribute(string lane) { } }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
    public sealed class DevOnlyAttribute : Attribute { }
}

namespace CrossBuy.BL
{
    public sealed class BusinessContext { public int CompanyId; }
    public sealed class PermissionTarget { public int? BranchId; }

    // --- the authorities ---
    public interface IAccountingAccessService
    {
        Task<bool> CanAsync(string action);
    }

    public interface ITasksAccessService
    {
        Task<bool> CanAsync(BusinessContext ctx, string action, PermissionTarget? target = null,
            CancellationToken ct = default);
    }

    public interface ICommunicationAccessService
    {
        Task<bool> IsConversationParticipantAsync(BusinessContext ctx, int conversationId,
            CancellationToken ct = default);
    }

    public interface IPosAccessService
    {
        bool CanSell(IEnumerable<string> roles);
        // The lane routing predicate. Reachable through an authority type and STILL not authorization.
        (bool allowed, bool noActivity) IsActivityAllowedForLane(string? preset, string lane);
    }

    public interface IAccountingApiAuthorization
    {
        Task<bool> AuthorizeAsync(string action, CancellationToken ct = default);
    }

    // --- NOT an authority: an ordinary application service ---
    public interface IThingService
    {
        Task SaveAsync(int id);
        // Named like a guard, declared nowhere in the surface: this is what CBA003 exists to surface.
        Task<bool> AuthorizeThingAsync(int id);
    }

    // --- R1: the Reporting authorization seam and its neighbours ---
    public sealed class ReportAccessDecision { public bool Allowed; }

    // AUTHORITY. Declared in AuthorizationSurface.AuthorityTypes.
    public interface IReportAuthorizationService
    {
        Task<ReportAccessDecision> AuthorizeReportAsync(string code, int level, BusinessContext context);
        Task<ReportAccessDecision> AuthorizeTemplateAsync(int templateId, BusinessContext context);
    }

    // The concrete type is declared too, because a controller may hold either.
    public class ReportAuthorizationService : IReportAuthorizationService
    {
        public Task<ReportAccessDecision> AuthorizeReportAsync(string code, int level, BusinessContext context)
            => Task.FromResult(new ReportAccessDecision());
        public Task<ReportAccessDecision> AuthorizeTemplateAsync(int templateId, BusinessContext context)
            => Task.FromResult(new ReportAccessDecision());
    }

    // NOT an authority: identical method shape, undeclared name. If the analyzer credited this, it would
    // be crediting by shape rather than by declaration - the false credit CORRECTION-004 records.
    public interface IFakeReportAuthorizationService
    {
        Task<ReportAccessDecision> AuthorizeReportAsync(string code, int level, BusinessContext context);
    }

    // NOT an authority: a Reporting service that CONSUMES the seam rather than being it.
    public interface IReportTemplateService
    {
        Task<bool> DeleteAsync(int templateId, BusinessContext context);
        Task<bool> SaveAsync(int templateId, BusinessContext context);
    }

    // --- Central Document Platform: the access spine, the service on it, and their neighbours ---
    public sealed class DocumentAccessDecision { public bool Allowed; public string ReasonCode = ""; }
    public readonly record struct DocumentOwnerRef(string EntityType, int EntityId);
    public enum DocumentAction { View, Download, Upload, Submit, Replace, Delete, Manage }
    public sealed class DocumentResult { public bool Ok; public string ReasonCode = ""; }
    public sealed class DocumentSubmissionRequest { public string EntityType = ""; public int EntityId; }

    // AUTHORITY. The spine that TAKES the decision.
    public interface IDocumentAccessResolver
    {
        Task<DocumentAccessDecision> AuthorizeAsync(BusinessContext? context, DocumentOwnerRef owner,
            DocumentAction action, int documentCompanyId, string? confidentiality = null,
            CancellationToken ct = default);
    }

    public sealed class DocumentAccessResolver : IDocumentAccessResolver
    {
        public Task<DocumentAccessDecision> AuthorizeAsync(BusinessContext? context, DocumentOwnerRef owner,
            DocumentAction action, int documentCompanyId, string? confidentiality = null,
            CancellationToken ct = default) => Task.FromResult(new DocumentAccessDecision());
    }

    // AUTHORITY. Every member calls the resolver before it touches a row - which is why an endpoint that
    // reaches this service has demonstrably taken an authorization decision.
    public interface IPlatformDocumentService
    {
        Task<DocumentResult> SubmitAsync(DocumentSubmissionRequest request, System.IO.Stream content, CancellationToken ct = default);
        Task<DocumentResult> VerifyAsync(long documentId, string? note, CancellationToken ct = default);
        Task<DocumentResult> RejectAsync(long documentId, string note, CancellationToken ct = default);
    }

    public sealed class PlatformDocumentService : IPlatformDocumentService
    {
        public Task<DocumentResult> SubmitAsync(DocumentSubmissionRequest request, System.IO.Stream content, CancellationToken ct = default)
            => Task.FromResult(new DocumentResult());
        public Task<DocumentResult> VerifyAsync(long documentId, string? note, CancellationToken ct = default)
            => Task.FromResult(new DocumentResult());
        public Task<DocumentResult> RejectAsync(long documentId, string note, CancellationToken ct = default)
            => Task.FromResult(new DocumentResult());
    }

    // NOT an authority: it moves bytes. Storing a file is not deciding who may.
    public interface IDocumentStorage
    {
        Task<string> StoreAsync(System.IO.Stream content, string? suggestedName = null, CancellationToken ct = default);
        Task<bool> DeleteAsync(string key, CancellationToken ct = default);
    }

    // NOT an authority: identical shape to the spine, undeclared name.
    public interface IFakeDocumentAccessResolver
    {
        Task<DocumentAccessDecision> AuthorizeAsync(BusinessContext? context, DocumentOwnerRef owner,
            DocumentAction action, int documentCompanyId, string? confidentiality = null,
            CancellationToken ct = default);
    }
}
""";
}
