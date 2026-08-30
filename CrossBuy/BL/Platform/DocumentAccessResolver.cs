using CrossBuy.Models.Platform;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // =============================================================================================
    // The orchestrator. Every refusal below is a step the old path did not take at all.
    //
    // ORDER IS THE DESIGN, and the interesting one is step 5.
    //
    // A document row will carry its own CompanyID. Checking it against the caller's company looks like
    // tenant isolation and is not sufficient on its own: a row can name company A while its
    // EntityType/EntityId points at company B's record. Then a company-A caller passes the company
    // check and is handed a company-B employee's file — the isolation test passes and the isolation is
    // gone. So the OWNING RECORD's company is resolved independently and must agree. A relation that
    // does not agree is refused as malformed rather than resolved in either direction.
    //
    // WHAT IT REFUSES TO GUESS. No resolver registered for a family means DENY, not "probably fine":
    // onboarding a family is a deliberate act, and a document platform that fails open on the families
    // nobody has classified yet would be worse than the hole it replaces. Same for an unknown
    // confidentiality string.
    //
    // The decision carries a reason CODE, never a message about the record: a refusal must not tell a
    // caller whether the document exists, whose it is, or which check rejected them in business terms.
    // =============================================================================================
    public sealed class DocumentAccessResolver : IDocumentAccessResolver
    {
        private readonly IReadOnlyDictionary<string, IDocumentOwnerResolver> _owners;
        private readonly IReadOnlyDictionary<string, IModuleAccessService> _modules;
        private readonly IEntityRegistry _registry;
        private readonly ILogger<DocumentAccessResolver> _log;

        public DocumentAccessResolver(
            IEnumerable<IDocumentOwnerResolver> owners,
            IEnumerable<IModuleAccessService> modules,
            IEntityRegistry registry,
            ILogger<DocumentAccessResolver> log)
        {
            // Last registration wins for a duplicate, deterministically, rather than throwing at
            // startup: a duplicate is a wiring mistake to fix, not a reason to refuse to boot.
            var ownerMap = new Dictionary<string, IDocumentOwnerResolver>(StringComparer.Ordinal);
            foreach (var owner in owners) ownerMap[owner.EntityType] = owner;
            _owners = ownerMap;

            var moduleMap = new Dictionary<string, IModuleAccessService>(StringComparer.Ordinal);
            foreach (var module in modules) moduleMap[module.Scope] = module;
            _modules = moduleMap;

            _registry = registry;
            _log = log;
        }

        public async Task<DocumentAccessDecision> AuthorizeAsync(
            BusinessContext? context,
            DocumentOwnerRef owner,
            DocumentAction action,
            int documentCompanyId,
            string? confidentiality = null,
            CancellationToken cancellationToken = default)
        {
            // ---- 1. a resolved tenant. There is no default company, here or anywhere. ----
            if (context == null || context.CompanyId <= 0)
                return Refuse(DocumentAccessReasons.CompanyUnresolved, owner, action);

            // ---- 2. an identity. A worker or a system context holds no employee, so it holds no
            // record-level relationship and cannot be judged by a module's record rules. ----
            if (context.EmployeeId is not > 0)
                return Refuse(DocumentAccessReasons.NoEmployeeIdentity, owner, action);

            // ---- 3. the document's own company. Necessary, and on its own not sufficient. ----
            if (documentCompanyId <= 0 || documentCompanyId != context.CompanyId)
                return Refuse(DocumentAccessReasons.CompanyMismatch, owner, action);

            // ---- 4. a family this deployment actually knows ----
            if (!_registry.TryGetDefinition(owner.EntityType, out var definition) || definition == null)
                return Refuse(DocumentAccessReasons.UnknownEntityType, owner, action);

            if (!_owners.TryGetValue(owner.EntityType, out var ownerResolver))
                return Refuse(DocumentAccessReasons.NoOwnerResolver, owner, action);

            // ---- 5. THE RELATION ITSELF. See the header: this is the check that makes the company
            // column meaningful rather than decorative. ----
            var owningCompanyId = await ownerResolver.OwningCompanyIdAsync(owner.EntityId, cancellationToken);
            if (owningCompanyId is not > 0)
                return Refuse(DocumentAccessReasons.OwnerNotFound, owner, action);

            if (owningCompanyId.Value != documentCompanyId)
            {
                // Logged as a warning with ids only: a document whose relation crosses a tenant boundary
                // is either a defect or an attempt, and both are worth seeing. No file name, no path.
                _log.LogWarning(
                    "Document relation refused: {EntityType}/{EntityId} belongs to company {OwningCompany} " +
                    "but the document claims company {DocumentCompany}.",
                    owner.EntityType, owner.EntityId, owningCompanyId.Value, documentCompanyId);
                return DocumentAccessDecision.Deny(DocumentAccessReasons.RelationMismatch);
            }

            // ---- 6. the OWNING MODULE decides the business question. The centre supplies no rule of
            // its own here - it does not know what an employee document means, and must not. ----
            if (!_modules.TryGetValue(definition.PermissionScope, out var module))
                return Refuse(DocumentAccessReasons.NoModuleAuthority, owner, action);

            var target = ownerResolver.TargetFor(owner.EntityId, owningCompanyId.Value);

            if (!await module.CanAsync(context, ownerResolver.ModuleActionFor(action), target, cancellationToken))
                return Refuse(DocumentAccessReasons.ModuleDenied, owner, action);

            // ---- 7. confidentiality, applied AFTER the record rule and never instead of it ----
            if (!string.IsNullOrWhiteSpace(confidentiality))
            {
                if (!DocumentConfidentiality.IsKnown(confidentiality))
                    return Refuse(DocumentAccessReasons.UnknownConfidentiality, owner, action);

                // Above Internal the caller needs the module's manage-tier authority over the same
                // record - which is a second CanAsync, not a role name this file knows.
                if (DocumentConfidentiality.RequiresElevatedAuthority(confidentiality)
                    && !await module.CanAsync(
                        context, ownerResolver.ModuleActionFor(DocumentAction.Manage), target, cancellationToken))
                    return Refuse(DocumentAccessReasons.ConfidentialityDenied, owner, action);
            }

            return DocumentAccessDecision.Allow();
        }

        private DocumentAccessDecision Refuse(string reason, DocumentOwnerRef owner, DocumentAction action)
        {
            _log.LogDebug(
                "Document access refused ({Reason}) for {EntityType}/{EntityId}, action {Action}.",
                reason, owner.EntityType, owner.EntityId, action);
            return DocumentAccessDecision.Deny(reason);
        }
    }
}
