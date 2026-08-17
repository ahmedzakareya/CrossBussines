namespace CrossBuy.Models.Platform
{
    // Stage 1 Batch A — what a permission check is ABOUT, beyond the action.
    //
    // Deliberately NOT an ABAC framework. Every field here exists because a real access service already
    // consults exactly that value today; nothing speculative is included:
    //
    //   WarehouseId       InventoryAccessService.CanUseWarehouseAsync(warehouseId) — the only row-level
    //                     gate in the inventory module (keeper scope → Warehouse.BranchHierarchicalId).
    //   OwnerEmployeeId   CrmAccessService.VisibleOwnerIdsAsync() — SalesRep sees own records, SalesManager
    //                     sees the org subtree. The one real record-owner rule in the codebase.
    //   BranchId          PosAccessService resolves rights per Employee.BranchID / BranchUserRoles.
    //   EntityType/Id     carried through so an adapter can look the record up when it needs to.
    //
    // Explicitly NOT included, and why: no Visibility field. Visibility is a property of a business EVENT,
    // not of a permission target, and it is already expressed by choosing between PlatformActions.View /
    // ViewConfidential / ViewRestricted. Adding it here would create two ways to say the same thing.
    public sealed class PermissionTarget
    {
        // Canonical IEntityRegistry code, when the check is about a specific entity type.
        public string? EntityType { get; init; }

        public int? EntityId { get; init; }

        // Inventory: the warehouse the operation touches. Consulted by CanUseWarehouseAsync.
        public int? WarehouseId { get; init; }

        // The branch the operation touches. Consulted by the POS path.
        public int? BranchId { get; init; }

        // CRM: the employee who owns the record. Consulted against VisibleOwnerIdsAsync.
        public int? OwnerEmployeeId { get; init; }

        // ---- Stage 1 Batch C additions. Same rule as above: each exists because a real access service
        // consults exactly that value, and nothing speculative is added. ----

        // The company the operation claims to touch. Checked FIRST and refused on mismatch, so a company id
        // arriving on a target can never widen a decision — it can only fail to match the caller's.
        //
        // It is on the TARGET rather than read from the context because that is the whole point: the context
        // says who you are, the target says what you are claiming, and the gate compares them.
        public int? CompanyId { get; init; }

        // HR: the employee the operation is ABOUT, as distinct from OwnerEmployeeId (who owns a record).
        // HrAccessService compares it to the caller (self-access) and to the company-intersected hierarchy
        // walk (direct reports). A posted employee id is never sufficient on its own.
        public int? SubjectEmployeeId { get; init; }

        // Projects: the project the operation touches. Resolved against ProjectMembers — active membership,
        // in the same company.
        public int? ProjectId { get; init; }

        // Tasks: the task. Resolved against AssigneeEmployeeId / CreatedByEmployeeId / the hierarchy.
        public int? TaskId { get; init; }

        // Communication: the conversation. Resolved against ConversationMember.
        public int? ConversationId { get; init; }

        public static PermissionTarget ForEntity(string entityType, int entityId)
            => new() { EntityType = entityType, EntityId = entityId };

        public static PermissionTarget ForSubjectEmployee(int employeeId, int? companyId = null)
            => new() { SubjectEmployeeId = employeeId, CompanyId = companyId };

        public static PermissionTarget ForProject(int projectId, int? companyId = null)
            => new() { ProjectId = projectId, CompanyId = companyId };

        public static PermissionTarget ForTask(int taskId, int? companyId = null)
            => new() { TaskId = taskId, CompanyId = companyId };

        public static PermissionTarget ForConversation(int conversationId, int? companyId = null)
            => new() { ConversationId = conversationId, CompanyId = companyId };

        public static PermissionTarget ForWarehouse(int warehouseId)
            => new() { WarehouseId = warehouseId };

        public static PermissionTarget ForOwner(int ownerEmployeeId)
            => new() { OwnerEmployeeId = ownerEmployeeId };
    }
}