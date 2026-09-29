using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
    // =============================================================================================
    // PROJECTS-2 — the milestone and deliverable service.
    //
    // WHAT IT REFUSES TO BECOME. Every field a task carries — assignee, hours, escalation, hold,
    // approval — is absent by design, and so is every operation that would need one. This service
    // answers "what did we commit to, and is it done"; ITaskService answers "who is doing it".
    //
    // AUTHORITY IS ASKED, NEVER RESTATED. There is no MilestonePermission and there is no membership
    // query here: ProjectsAccessService already decides, and its answer for a CHILD of a project is
    // the answer for the project. Read admits Observer, Member and Manager; Edit admits Member and
    // Manager and refuses an Observer by name and by rule. A milestone does not widen anything.
    // =============================================================================================

    /// One deliverable, flattened for a caller that will render it.
    public sealed record ProjectDeliverableRow(
        int Id, int? MilestoneId, string Name, string? NameEn, string? Description, string? DescriptionEn,
        DateTime? DueDate, string Status, int SortOrder, DateTime? CompletedAt, bool IsOverdue);

    /// One milestone and the deliverables grouped under it.
    public sealed record ProjectMilestoneRow(
        int Id, string Name, string? NameEn, string? Description, string? DescriptionEn,
        DateTime TargetDate, string Status, int SortOrder, DateTime? CompletedAt, bool IsOverdue,
        IReadOnlyList<ProjectDeliverableRow> Deliverables);

    /// Everything one project's milestone screen needs, from a fixed number of queries.
    public sealed record ProjectMilestoneBoard
    {
        public required IReadOnlyList<ProjectMilestoneRow> Milestones { get; init; }

        /// Deliverables with no milestone. A first-class shape, not leftovers — see ProjectDeliverable.
        public required IReadOnlyList<ProjectDeliverableRow> LooseDeliverables { get; init; }

        /// Whether THIS caller may mutate, so the view asks once instead of guessing per button.
        public required bool CanEdit { get; init; }

        /// A closed project is readable and frozen. Carried so the screen can say why, rather than
        /// showing controls that the service will then refuse.
        public required bool IsProjectClosed { get; init; }

        public static ProjectMilestoneBoard Empty { get; } = new()
        {
            Milestones = Array.Empty<ProjectMilestoneRow>(),
            LooseDeliverables = Array.Empty<ProjectDeliverableRow>(),
            CanEdit = false,
            IsProjectClosed = false,
        };
    }

    public interface IProjectMilestoneService
    {
        Task<ProjectMilestoneBoard> BoardAsync(int projectId, CancellationToken ct = default);

        Task<(bool ok, string? error, int id)> SaveMilestoneAsync(
            int projectId, int id, string? name, string? nameEn, string? description, string? descriptionEn,
            DateTime targetDate, int sortOrder, CancellationToken ct = default);

        Task<(bool ok, string? error)> SetMilestoneStatusAsync(
            int projectId, int id, string toStatus, CancellationToken ct = default);

        Task<(bool ok, string? error)> DeleteMilestoneAsync(int projectId, int id, CancellationToken ct = default);

        Task<(bool ok, string? error, int id)> SaveDeliverableAsync(
            int projectId, int id, int? milestoneId, string? name, string? nameEn, string? description,
            string? descriptionEn, DateTime? dueDate, int sortOrder, CancellationToken ct = default);

        Task<(bool ok, string? error)> SetDeliverableStatusAsync(
            int projectId, int id, string toStatus, CancellationToken ct = default);

        Task<(bool ok, string? error)> DeleteDeliverableAsync(int projectId, int id, CancellationToken ct = default);
    }

    public sealed class ProjectMilestoneService : IProjectMilestoneService
    {
        // ONE refusal for every reason, following ProjectMembershipService and the ProjectGate: an
        // unresolved identity, a missing right, a foreign project and a project that does not exist
        // must be indistinguishable, or the response becomes a probe for other companies' project ids.
        public const string Refused = "You do not have permission to perform this action";
        public const string NameRequired = "A name is required";
        public const string UnknownStatus = "Unknown status";
        public const string IllegalTransition = "That status change is not allowed";
        public const string ProjectClosed = "The project is closed";
        public const string MilestoneNotInProject = "That milestone does not belong to this project";
        public const string HasOpenDeliverables = "Complete or cancel the deliverables under this milestone first";
        public const string HasDeliverables = "Remove the deliverables under this milestone first";
        public const string OnlyOpenMayBeDeleted = "Only a planned milestone may be deleted";
        public const string OnlyOpenDeliverableMayBeDeleted = "Only a pending deliverable may be deleted";

        private readonly CrossDbContext _db;
        private readonly IBusinessContextAccessor _contexts;
        private readonly IProjectsAccessService _access;

        // PROJECTS-3. The EXISTING kernel recorder - no second event service, no outbox of its own,
        // and nothing written to the BusinessEvents DbSet directly. It stamps company, branch and actor
        // from the resolved BusinessContext, so tenancy on the event row is the platform's answer.
        private readonly CrossBuy.BL.Platform.IBusinessEventService _events;

        public ProjectMilestoneService(
            CrossDbContext db, IBusinessContextAccessor contexts, IProjectsAccessService access,
            CrossBuy.BL.Platform.IBusinessEventService events)
        { _db = db; _contexts = contexts; _access = access; _events = events; }

        // ONE SHAPE FOR ALL THREE MILESTONE FACTS.
        //
        // THE ENTITY CODE IS ProjectMilestone AND NOT Project. Collapsing a milestone under the project
        // would give two different records one identity; the registry now carries ProjectMilestone
        // precisely so this does not have to happen, and BusinessEventTypes.Validate compares the event
        // prefix to the recorded code with an ordinal equality, so a wrong prefix is a runtime refusal.
        //
        // CALLED BEFORE CommitAsync, ALWAYS. RecordAsync throws when there is no ambient transaction,
        // and there is deliberately no catch around it anywhere below: a swallowed event failure would
        // leave a mutation standing with no record of it.
        // targetDate is passed IN rather than read here, and always from the row this call is about,
        // captured while that row still exists. On the delete path the milestone is removed a few lines
        // later, so reading it afterwards would be reading nothing.
        //
        // EMITTING A FACT IS NOT INTEGRATION. This file names no Task type, holds no Task reference and
        // knows no consumer; a rule that later reads this date is a separate component. That is the
        // whole point of an outbox.
        private Task RecordAsync(
            BusinessContext ctx, int projectId, int milestoneId, string action,
            string status, string? previousStatus, DateTime targetDate, CancellationToken ct) =>
            _events.RecordAsync(new CrossBuy.Models.Platform.BusinessEventRecord
            {
                EntityCode = CrossBuy.BL.Platform.EntityRegistry.ProjectMilestone,
                EntityId = milestoneId,
                EventType = $"{CrossBuy.BL.Platform.EntityRegistry.ProjectMilestone}.{action}",
                PayloadVersion = ProjectMilestoneEventPayload.Version,
                Visibility = CrossBuy.Models.Platform.BusinessEventVisibility.Internal,

                // DEDUP ONLY ON Created. A milestone is created once, so a deterministic key makes a
                // retried create return the original event and write nothing. StatusChanged is a
                // REPEATABLE fact - Planned to InProgress to Completed, and a reopen back again - and a
                // key like projectmilestone:47:StatusChanged would swallow every move after the first,
                // which is the cycle-blind dedup defect progress billing already suffered once.
                //
                // DELETED TAKES NO KEY, and the precedent was measured rather than assumed - it exists
                // and it goes the other way. CommCommentService emits CommentDeleted with a
                // deterministic comment:{id}:deleted. It is the ONLY deletion event in the repository,
                // and it does not require one of anybody else.
                //
                // NULL IS CHOSEN HERE BECAUSE THE KEY WOULD BUY NOTHING AND COULD COST SOMETHING. A
                // milestone delete cannot be retried after it succeeds - the row is gone and the path
                // refuses - so there is no duplicate to suppress. Meanwhile IDENTITY ids are unique
                // only within the life of a table: after a restore or a reseed a reused id would make a
                // stale key swallow a genuine second deletion, which is the cycle-blind failure this
                // module already reasons about for StatusChanged.
                DedupKey = action == ProjectMilestoneEvents.Created
                    ? $"projectmilestone:{milestoneId}:{ProjectMilestoneEvents.Created}"
                    : null,

                Payload = new ProjectMilestoneEventPayload
                {
                    CompanyId = ctx.CompanyId,
                    ProjectId = projectId,
                    MilestoneId = milestoneId,
                    Status = status,
                    PreviousStatus = previousStatus,
                    // The gate refused any context without a real employee, so this is a fact.
                    ActorEmployeeId = ctx.EmployeeId,

                    // Always populated by this producer: TargetDate is required on the entity, so no
                    // event from here legitimately carries null. Null on a stored event means it
                    // predates this field.
                    TargetDate = targetDate,
                },
            }, ct);

        // =========================================================================================
        // THE GATE. Lifted verbatim in shape from ProjectCloseoutService so reading and acting cannot
        // drift apart: the project's company comes from the PROJECT ROW and is compared with the
        // caller's, so a project in another company is refused exactly as a non-existent one is.
        //
        // THE REQUEST NEVER SUPPLIES A COMPANY. It is read from the resolved BusinessContext and
        // nothing else; there is no companyId parameter on any method here for that reason.
        // =========================================================================================
        private async Task<BusinessContext?> GateAsync(int projectId, string action, CancellationToken ct)
        {
            if (projectId <= 0) return null;

            var ctx = await _contexts.TryGetCurrentAsync(ct);
            if (ctx is not { CompanyId: > 0 } || ctx.EmployeeId is not > 0) return null;

            bool exists = await _db.Projects.AsNoTracking()
                .AnyAsync(p => p.ID == projectId && p.CompanyID == ctx.CompanyId, ct);
            if (!exists) return null;

            return await _access.CanAsync(ctx, action, PermissionTarget.ForProject(projectId, ctx.CompanyId), ct)
                ? ctx : null;
        }

        /// A closed project is frozen for this module. Read stays open — history is the point of a
        /// closed project — but nothing may be added, renamed, completed, reopened or deleted.
        private Task<bool> IsClosedAsync(BusinessContext ctx, int projectId, CancellationToken ct) =>
            _db.Set<ProjectCloseout>().AsNoTracking()
                .AnyAsync(c => c.CompanyID == ctx.CompanyId && c.ProjectId == projectId && c.IsCurrent, ct);

        // =========================================================================================
        // READ
        // =========================================================================================
        public async Task<ProjectMilestoneBoard> BoardAsync(int projectId, CancellationToken ct = default)
        {
            var ctx = await GateAsync(projectId, ProjectsActions.Read, ct);
            if (ctx == null) return ProjectMilestoneBoard.Empty;

            // THREE QUERIES, FIXED — never one per milestone. The deliverables of the whole project
            // come back in ONE read and are grouped in memory; a per-milestone query would be an N+1
            // that grows with the very list this screen exists to show.
            var milestones = await _db.ProjectMilestones.AsNoTracking()
                .Where(m => m.CompanyID == ctx.CompanyId && m.ProjectId == projectId)
                .OrderBy(m => m.SortOrder).ThenBy(m => m.TargetDate).ThenBy(m => m.ID)
                .ToListAsync(ct);

            var deliverables = await _db.ProjectDeliverables.AsNoTracking()
                .Where(d => d.CompanyID == ctx.CompanyId && d.ProjectId == projectId)
                .OrderBy(d => d.SortOrder).ThenBy(d => d.ID)
                .ToListAsync(ct);

            bool closed = await IsClosedAsync(ctx, projectId, ct);

            // Asked ONCE for the whole board rather than per row, and asked of the access service
            // rather than inferred from anything this service knows.
            bool canEdit = !closed && await _access.CanAsync(
                ctx, ProjectsActions.Edit, PermissionTarget.ForProject(projectId, ctx.CompanyId), ct);

            var byMilestone = deliverables.Where(d => d.MilestoneId.HasValue)
                .GroupBy(d => d.MilestoneId!.Value)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<ProjectDeliverableRow>)g.Select(Row).ToList());

            return new ProjectMilestoneBoard
            {
                Milestones = milestones.Select(m => new ProjectMilestoneRow(
                    m.ID, m.Name, m.NameEn, m.Description, m.DescriptionEn, m.TargetDate, m.Status,
                    m.SortOrder, m.CompletedAt, m.IsOverdue,
                    byMilestone.TryGetValue(m.ID, out var kids) ? kids : Array.Empty<ProjectDeliverableRow>()
                )).ToList(),
                LooseDeliverables = deliverables.Where(d => !d.MilestoneId.HasValue).Select(Row).ToList(),
                CanEdit = canEdit,
                IsProjectClosed = closed,
            };
        }

        private static ProjectDeliverableRow Row(ProjectDeliverable d) => new(
            d.ID, d.MilestoneId, d.Name, d.NameEn, d.Description, d.DescriptionEn,
            d.DueDate, d.Status, d.SortOrder, d.CompletedAt, d.IsOverdue);

        // =========================================================================================
        // MILESTONE WRITES
        // =========================================================================================
        public async Task<(bool ok, string? error, int id)> SaveMilestoneAsync(
            int projectId, int id, string? name, string? nameEn, string? description, string? descriptionEn,
            DateTime targetDate, int sortOrder, CancellationToken ct = default)
        {
            var ctx = await GateAsync(projectId, ProjectsActions.Edit, ct);
            if (ctx == null) return (false, Refused, 0);
            if (await IsClosedAsync(ctx, projectId, ct)) return (false, ProjectClosed, 0);

            name = name?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return (false, NameRequired, 0);

            var now = DateTime.UtcNow;

            if (id <= 0)
            {
                var row = new ProjectMilestone
                {
                    CompanyID = ctx.CompanyId,
                    ProjectId = projectId,
                    Name = name,
                    NameEn = nameEn?.Trim(),
                    Description = description?.Trim(),
                    DescriptionEn = descriptionEn?.Trim(),
                    TargetDate = targetDate.Date,
                    Status = ProjectMilestoneStatuses.Planned,
                    SortOrder = sortOrder,
                    CreatedBy = ctx.EmployeeId,
                    CreatedAt = now,
                };
                // A TRANSACTION IS INTRODUCED HERE: this path had none, and RecordAsync refuses to
                // record without an ambient one. BeginOrJoin, not Begin, so a caller that already
                // opened one is joined rather than deadlocked.
                await using var tx = await ScopedTx.BeginOrJoinAsync(_db);

                _db.ProjectMilestones.Add(row);
                // SAVED FIRST so the inserted row has its identity: row.ID is 0 until EF writes it, and
                // an event carrying EntityId 0 is refused by the kernel's own contract check.
                await _db.SaveChangesAsync(ct);

                // CREATE EMITS Created AND NOTHING ELSE. The row is born Planned, but being assigned a
                // starting status is not a transition - emitting StatusChanged here would teach every
                // consumer that a creation is also a move.
                await RecordAsync(ctx, projectId, row.ID, ProjectMilestoneEvents.Created,
                    row.Status, previousStatus: null, row.TargetDate, ct);

                await tx.CommitAsync();
                return (true, null, row.ID);
            }

            // BOTH IDS ARE IN THE PREDICATE. Loading by id alone and checking afterwards is the shape
            // that leaks: the row would be in memory, and any later edit to this method could use it.
            var existing = await _db.ProjectMilestones
                .FirstOrDefaultAsync(m => m.ID == id && m.ProjectId == projectId && m.CompanyID == ctx.CompanyId, ct);
            if (existing == null) return (false, Refused, 0);

            // SAVE DOES NOT MOVE STATUS, and that is the whole reason status has its own method. A
            // completed milestone reverting because a form posted a stale field is exactly the silent
            // revert this separation prevents — CompletedAt would survive while Status changed.
            existing.Name = name;
            existing.NameEn = nameEn?.Trim();
            existing.Description = description?.Trim();
            existing.DescriptionEn = descriptionEn?.Trim();
            existing.TargetDate = targetDate.Date;
            existing.SortOrder = sortOrder;
            existing.UpdatedBy = ctx.EmployeeId;
            existing.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            return (true, null, existing.ID);
        }

        public async Task<(bool ok, string? error)> SetMilestoneStatusAsync(
            int projectId, int id, string toStatus, CancellationToken ct = default)
        {
            var ctx = await GateAsync(projectId, ProjectsActions.Edit, ct);
            if (ctx == null) return (false, Refused);
            if (await IsClosedAsync(ctx, projectId, ct)) return (false, ProjectClosed);
            if (!ProjectMilestoneStatuses.IsKnown(toStatus)) return (false, UnknownStatus);

            var row = await _db.ProjectMilestones
                .FirstOrDefaultAsync(m => m.ID == id && m.ProjectId == projectId && m.CompanyID == ctx.CompanyId, ct);
            if (row == null) return (false, Refused);

            if (!ProjectMilestoneStatuses.CanMove(row.Status, toStatus)) return (false, IllegalTransition);

            // A COMMITMENT IS NOT MET WHILE ITS PARTS ARE OUTSTANDING. Cancelling remains available for
            // the parts that will not happen, so this blocks nothing a user cannot express.
            if (toStatus == ProjectMilestoneStatuses.Completed)
            {
                bool openKids = await _db.ProjectDeliverables.AsNoTracking()
                    .AnyAsync(d => d.CompanyID == ctx.CompanyId && d.MilestoneId == id
                                && (d.Status == ProjectDeliverableStatuses.Pending
                                 || d.Status == ProjectDeliverableStatuses.InProgress), ct);
                if (openKids) return (false, HasOpenDeliverables);
            }

            // CAPTURED BEFORE THE ASSIGNMENT OVERWRITES IT. Read afterwards it would always equal the
            // new value and the event could never carry a real transition.
            var previousStatus = row.Status;

            await using var tx = await ScopedTx.BeginOrJoinAsync(_db);

            row.Status = toStatus;
            row.CompletedAt = toStatus == ProjectMilestoneStatuses.Completed ? DateTime.UtcNow : null;
            row.CompletedBy = toStatus == ProjectMilestoneStatuses.Completed ? ctx.EmployeeId : null;
            row.UpdatedBy = ctx.EmployeeId;
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            // ONLY REACHED WHEN THE STATUS ACTUALLY MOVED: CanMove above already refused a transition
            // to the state the row is in, so there is no same-status write to filter out here.
            await RecordAsync(ctx, projectId, row.ID, ProjectMilestoneEvents.StatusChanged,
                row.Status, previousStatus, row.TargetDate, ct);

            await tx.CommitAsync();
            return (true, null);
        }

        public async Task<(bool ok, string? error)> DeleteMilestoneAsync(
            int projectId, int id, CancellationToken ct = default)
        {
            var ctx = await GateAsync(projectId, ProjectsActions.Edit, ct);
            if (ctx == null) return (false, Refused);
            if (await IsClosedAsync(ctx, projectId, ct)) return (false, ProjectClosed);

            var row = await _db.ProjectMilestones
                .FirstOrDefaultAsync(m => m.ID == id && m.ProjectId == projectId && m.CompanyID == ctx.CompanyId, ct);
            if (row == null) return (false, Refused);

            // STATE TRANSITION IS PREFERRED OVER DESTRUCTION once a commitment has had a life. A
            // milestone that was started, met or cancelled is history; Cancel expresses "this will not
            // happen" without erasing that it was ever promised.
            if (row.Status != ProjectMilestoneStatuses.Planned) return (false, OnlyOpenMayBeDeleted);

            bool anyKids = await _db.ProjectDeliverables.AsNoTracking()
                .AnyAsync(d => d.CompanyID == ctx.CompanyId && d.MilestoneId == id, ct);
            if (anyKids) return (false, HasDeliverables);

            // THE FACTS ARE CAPTURED WHILE THE ROW STILL EXISTS. After Remove and SaveChanges there is
            // nothing left to read, and the event must describe the row that was actually deleted.
            // Nothing here depends on EntityRegistry.ResolveAsync: the kernel validates the registered
            // CODE without resolving the row, which is what lets a deletion event be recorded at all.
            //
            // THE STATUS IS THE REAL PRE-DELETE ONE - Planned, because the guard above admits no other.
            // It is NOT recorded as Cancelled: deletion is not cancellation, and a cancelled milestone
            // keeps its row and its status.
            var deletedStatus = row.Status;

            // CAPTURED BESIDE THE STATUS AND FOR THE SAME REASON. Remove + SaveChanges below leave
            // nothing to read, so the deleted row's own date is taken here and emitted from this
            // local - the deleted state is never queried back afterwards to reconstruct it.
            var deletedTargetDate = row.TargetDate;

            // A TRANSACTION IS INTRODUCED HERE. This path had none at all - it saved and returned - so
            // without one the event could not be recorded, and with the event outside it a deletion
            // could commit while its record did not.
            await using var tx = await ScopedTx.BeginOrJoinAsync(_db);

            await RecordAsync(ctx, projectId, id, ProjectMilestoneEvents.Deleted,
                deletedStatus, previousStatus: null, deletedTargetDate, ct);

            _db.ProjectMilestones.Remove(row);
            await _db.SaveChangesAsync(ct);

            await tx.CommitAsync();
            return (true, null);
        }

        // =========================================================================================
        // DELIVERABLE WRITES
        // =========================================================================================
        public async Task<(bool ok, string? error, int id)> SaveDeliverableAsync(
            int projectId, int id, int? milestoneId, string? name, string? nameEn, string? description,
            string? descriptionEn, DateTime? dueDate, int sortOrder, CancellationToken ct = default)
        {
            var ctx = await GateAsync(projectId, ProjectsActions.Edit, ct);
            if (ctx == null) return (false, Refused, 0);
            if (await IsClosedAsync(ctx, projectId, ct)) return (false, ProjectClosed, 0);

            name = name?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return (false, NameRequired, 0);

            // THE REQUEST'S MilestoneId IS NEVER TRUSTED. It is proved to belong to THIS project and
            // THIS company before it is written, so a posted id cannot file a deliverable under another
            // project's milestone — including one in another tenant.
            if (milestoneId is > 0)
            {
                bool sameProject = await _db.ProjectMilestones.AsNoTracking()
                    .AnyAsync(m => m.ID == milestoneId.Value && m.ProjectId == projectId
                                && m.CompanyID == ctx.CompanyId, ct);
                if (!sameProject) return (false, MilestoneNotInProject, 0);
            }
            else
            {
                milestoneId = null;   // 0 and negative mean "no milestone", never milestone zero
            }

            var now = DateTime.UtcNow;

            if (id <= 0)
            {
                var row = new ProjectDeliverable
                {
                    CompanyID = ctx.CompanyId,
                    ProjectId = projectId,
                    MilestoneId = milestoneId,
                    Name = name,
                    NameEn = nameEn?.Trim(),
                    Description = description?.Trim(),
                    DescriptionEn = descriptionEn?.Trim(),
                    DueDate = dueDate?.Date,
                    Status = ProjectDeliverableStatuses.Pending,
                    SortOrder = sortOrder,
                    CreatedBy = ctx.EmployeeId,
                    CreatedAt = now,
                };
                _db.ProjectDeliverables.Add(row);
                await _db.SaveChangesAsync(ct);
                return (true, null, row.ID);
            }

            var existing = await _db.ProjectDeliverables
                .FirstOrDefaultAsync(d => d.ID == id && d.ProjectId == projectId && d.CompanyID == ctx.CompanyId, ct);
            if (existing == null) return (false, Refused, 0);

            existing.MilestoneId = milestoneId;
            existing.Name = name;
            existing.NameEn = nameEn?.Trim();
            existing.Description = description?.Trim();
            existing.DescriptionEn = descriptionEn?.Trim();
            existing.DueDate = dueDate?.Date;
            existing.SortOrder = sortOrder;
            existing.UpdatedBy = ctx.EmployeeId;
            existing.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
            return (true, null, existing.ID);
        }

        public async Task<(bool ok, string? error)> SetDeliverableStatusAsync(
            int projectId, int id, string toStatus, CancellationToken ct = default)
        {
            var ctx = await GateAsync(projectId, ProjectsActions.Edit, ct);
            if (ctx == null) return (false, Refused);
            if (await IsClosedAsync(ctx, projectId, ct)) return (false, ProjectClosed);
            if (!ProjectDeliverableStatuses.IsKnown(toStatus)) return (false, UnknownStatus);

            var row = await _db.ProjectDeliverables
                .FirstOrDefaultAsync(d => d.ID == id && d.ProjectId == projectId && d.CompanyID == ctx.CompanyId, ct);
            if (row == null) return (false, Refused);

            if (!ProjectDeliverableStatuses.CanMove(row.Status, toStatus)) return (false, IllegalTransition);

            row.Status = toStatus;
            row.CompletedAt = toStatus == ProjectDeliverableStatuses.Delivered ? DateTime.UtcNow : null;
            row.CompletedBy = toStatus == ProjectDeliverableStatuses.Delivered ? ctx.EmployeeId : null;
            row.UpdatedBy = ctx.EmployeeId;
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return (true, null);
        }

        public async Task<(bool ok, string? error)> DeleteDeliverableAsync(
            int projectId, int id, CancellationToken ct = default)
        {
            var ctx = await GateAsync(projectId, ProjectsActions.Edit, ct);
            if (ctx == null) return (false, Refused);
            if (await IsClosedAsync(ctx, projectId, ct)) return (false, ProjectClosed);

            var row = await _db.ProjectDeliverables
                .FirstOrDefaultAsync(d => d.ID == id && d.ProjectId == projectId && d.CompanyID == ctx.CompanyId, ct);
            if (row == null) return (false, Refused);

            if (row.Status != ProjectDeliverableStatuses.Pending) return (false, OnlyOpenDeliverableMayBeDeleted);

            _db.ProjectDeliverables.Remove(row);
            await _db.SaveChangesAsync(ct);
            return (true, null);
        }
    }
}
