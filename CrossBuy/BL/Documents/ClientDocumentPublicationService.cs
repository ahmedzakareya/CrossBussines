using System.Data;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Context.Portal;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Documents;

public interface IClientDocumentPublicationService
{
    Task<DocumentResult> PublishAsync(long documentId, CancellationToken ct = default);
    Task<DocumentResult> RevokeAsync(long documentId, CancellationToken ct = default);
}

// Company and customer come from the resolved context and document owner, never form fields.
// The transaction covers eligibility, the grant and its audit event. No bytes are copied.
public sealed class ClientDocumentPublicationService(
    CrossDbContext db, IBusinessContextAccessor contexts, IDocumentAccessResolver access,
    AccountingAccessService accounting, IBusinessEventService events, TimeProvider clock)
    : IClientDocumentPublicationService
{
    public Task<DocumentResult> PublishAsync(long documentId, CancellationToken ct = default)
        => ChangeAsync(documentId, false, ct);
    public Task<DocumentResult> RevokeAsync(long documentId, CancellationToken ct = default)
        => ChangeAsync(documentId, true, ct);

    private async Task<DocumentResult> ChangeAsync(long id, bool revoke, CancellationToken ct)
    {
        var context = await contexts.TryGetCurrentAsync(ct);
        if (context == null || context.CompanyId <= 0 || context.EmployeeId is not > 0 || context.IsExternalPortal)
            return DocumentResult.Refused("not_found");
        if (!await accounting.CanAsync(context, AccountingActions.CustomerDocumentPublish, cancellationToken: ct))
            return DocumentResult.Refused("not_found");

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var doc = await db.Set<PlatformDocument>().AsNoTracking().FirstOrDefaultAsync(d =>
            d.Id == id && d.CompanyID == context.CompanyId && d.EntityType == EntityRegistry.Customer, ct);
        if (doc == null || !(await access.AuthorizeAsync(context,
                new(doc.EntityType, doc.EntityId), DocumentAction.View, doc.CompanyID,
                DocumentConfidentiality.Internal, ct)).Allowed)
            return DocumentResult.Refused("not_found");

        PlatformDocumentType? type = null;
        if (!revoke)
        {
            if (doc.Status != "Active" || doc.Confidentiality != DocumentConfidentiality.Internal
                || doc.CurrentVersionId == null || doc.DocumentTypeId == null)
                return DocumentResult.Refused("not_found");
            type = await db.Set<PlatformDocumentType>().AsNoTracking().FirstOrDefaultAsync(t =>
                t.Id == doc.DocumentTypeId && (t.CompanyID == null || t.CompanyID == context.CompanyId) && t.IsActive, ct);
            if (type == null || !type.AppliesToEntityTypes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Contains(EntityRegistry.Customer, StringComparer.Ordinal))
                return DocumentResult.Refused("not_found");
            if (!await db.Set<PlatformDocumentVersion>().AnyAsync(v => v.Id == doc.CurrentVersionId
                    && v.DocumentId == doc.Id && v.CompanyID == context.CompanyId, ct))
                return DocumentResult.Refused("not_found");
        }

        var publication = await db.Set<ClientDocumentPublication>().FirstOrDefaultAsync(p =>
            p.CompanyID == context.CompanyId && p.CustomerId == doc.EntityId
            && p.PlatformDocumentId == doc.Id && p.RevokedAt == null, ct);
        if (revoke && publication == null || !revoke && publication != null)
            return DocumentResult.Success(doc.Id, doc.CurrentVersionId ?? 0);

        var now = clock.GetUtcNow().UtcDateTime;
        if (revoke)
        {
            publication!.RevokedAt = now;
            publication.RevokedByEmployeeId = context.EmployeeId;
        }
        else
        {
            publication = new ClientDocumentPublication
            {
                CompanyID = context.CompanyId, CustomerId = doc.EntityId, PlatformDocumentId = doc.Id,
                PublishedByEmployeeId = context.EmployeeId.Value, PublishedAt = now
            };
            db.Set<ClientDocumentPublication>().Add(publication);
        }
        await db.SaveChangesAsync(ct);
        var action = revoke ? DocumentEvents.Revoked : DocumentEvents.Published;
        await DocumentEvents.RaiseAsync(events, doc, type, action,
            $"platformdoc:{doc.Id}:publication:{publication!.Id}:{action}", ct);
        await tx.CommitAsync(ct);
        return DocumentResult.Success(doc.Id, doc.CurrentVersionId ?? 0);
    }
}
