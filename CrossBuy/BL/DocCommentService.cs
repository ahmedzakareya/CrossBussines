using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Comm;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
    public class DocCommentDto
    {
        public int Id { get; set; }
        public string Body { get; set; } = "";
        public int AuthorId { get; set; }
        public string AuthorName { get; set; } = "";
        public string? AuthorAvatar { get; set; }
        public DateTime? CreatedAt { get; set; }
        public bool Mine { get; set; }
    }

    public interface IDocCommentService
    {
        Task<DocCommentDto> AddAsync(int companyId, int meId, string entityType, int entityId, string body, IEnumerable<int>? mentionedIds);
        Task<List<DocCommentDto>> ListAsync(int companyId, int meId, string entityType, int entityId);
        Task<bool> DeleteAsync(int meId, int id);
    }

    // Thrown when a comment is written against an entity type the registry does not know. Distinct from a
    // generic argument error so the controller can answer 400 rather than 500.
    public sealed class DocCommentEntityTypeException : InvalidOperationException
    {
        public DocCommentEntityTypeException(string? entityType)
            : base($"Entity type '{entityType ?? "(null)"}' is not a registered business object code. " +
                   "Document comments must be attached to a canonical IEntityRegistry code.")
        { EntityType = entityType; }
        public string? EntityType { get; }
    }

    public class DocCommentService : IDocCommentService
    {
        private readonly CrossDbContext _db;
        private readonly INotificationService _notify;
        private readonly CrossBuy.BL.Platform.IEntityRegistry _registry;   // Stage 0: validates new comment targets
        public DocCommentService(CrossDbContext db, INotificationService notify, CrossBuy.BL.Platform.IEntityRegistry registry)
        { _db = db; _notify = notify; _registry = registry; }

        public async Task<DocCommentDto> AddAsync(int companyId, int meId, string entityType, int entityId, string body, IEnumerable<int>? mentionedIds)
        {
            // Stage 0 (Slice-003): validate the target on WRITE only.
            //
            // Before this, EntityType was unconstrained free text — the one surviving instance of the problem
            // ADR-002 exists to prevent. Validation is deliberately write-only: ListAsync still reads whatever
            // is stored, so historical rows keep loading even if a type was later renamed or removed from the
            // registry. No data is migrated in this stage.
            //
            // No alias mapping was needed: the three types in use ("SalesInvoice", "PurchaseInvoice",
            // "Quotation") are already exactly the canonical PascalCase codes, and "Quotation" was registered in
            // this same slice specifically so its existing rows validate.
            if (!_registry.TryGetDefinition(entityType, out var definition))
                throw new DocCommentEntityTypeException(entityType);
            if (!definition!.SupportsComments)
                throw new DocCommentEntityTypeException(
                    $"{definition.Code} (registered, but SupportsComments is false)");
            if (entityId <= 0)
                throw new ArgumentOutOfRangeException(nameof(entityId), "A comment needs a positive entity id.");

            var c = new DocComment
            {
                CompanyID = companyId, EntityType = entityType, EntityId = entityId,
                Body = (body ?? "").Trim(), CreatedBy = meId, CreatedAt = DateTime.UtcNow,
            };
            _db.DocComments.Add(c);
            await _db.SaveChangesAsync();

            var me = await _db.Employee.AsNoTracking().Where(e => e.ID == meId)
                .Select(e => new { e.FullName, e.FullNameEn, e.ProfileImage }).FirstOrDefaultAsync();

            // notify mentioned employees (best-effort)
            try
            {
                var ids = (mentionedIds ?? Enumerable.Empty<int>()).Where(x => x != meId).Distinct().ToList();
                var url = $"/{entityType}/{entityId}";
                var nameAr = me?.FullName ?? "";
                var nameEn = string.IsNullOrWhiteSpace(me?.FullNameEn) ? nameAr : me!.FullNameEn!;
                foreach (var mid in ids)
                    await _notify.NotifyAsync(mid, $"{nameAr} أشار إليك في تعليق", $"{nameEn} mentioned you in a comment",
                        c.Body, c.Body, NotificationTypes.DocComment, entityId, url: url,
                        companyId: companyId, actorEmployeeId: meId);
            }
            catch { }

            return new DocCommentDto
            {
                Id = c.Id, Body = c.Body, AuthorId = meId,
                AuthorName = me?.FullName ?? me?.FullNameEn ?? "-", AuthorAvatar = me?.ProfileImage,
                CreatedAt = c.CreatedAt, Mine = true,
            };
        }

        public async Task<List<DocCommentDto>> ListAsync(int companyId, int meId, string entityType, int entityId)
        {
            var rows = await _db.DocComments.AsNoTracking()
                .Where(c => c.CompanyID == companyId && c.EntityType == entityType && c.EntityId == entityId && c.DeletedAt == null)
                .OrderBy(c => c.Id).ToListAsync();
            var authorIds = rows.Where(r => r.CreatedBy.HasValue).Select(r => r.CreatedBy!.Value).Distinct().ToList();
            var authors = await _db.Employee.AsNoTracking().Where(e => authorIds.Contains(e.ID))
                .Select(e => new { e.ID, e.FullName, e.FullNameEn, e.ProfileImage }).ToListAsync();
            return rows.Select(c =>
            {
                var a = authors.FirstOrDefault(x => x.ID == c.CreatedBy);
                return new DocCommentDto
                {
                    Id = c.Id, Body = c.Body, AuthorId = c.CreatedBy ?? 0,
                    AuthorName = a?.FullName ?? a?.FullNameEn ?? "-", AuthorAvatar = a?.ProfileImage,
                    CreatedAt = c.CreatedAt, Mine = c.CreatedBy == meId,
                };
            }).ToList();
        }

        public async Task<bool> DeleteAsync(int meId, int id)
        {
            var c = await _db.DocComments.FirstOrDefaultAsync(x => x.Id == id && x.CreatedBy == meId && x.DeletedAt == null);
            if (c == null) return false;
            c.DeletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return true;
        }
    }
}
