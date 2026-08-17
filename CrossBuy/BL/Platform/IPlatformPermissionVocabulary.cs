namespace CrossBuy.BL.Platform
{
    // Platform Kernel — how a module publishes its permission vocabulary TO the kernel.
    //
    // WHY THIS EXISTS, measured rather than assumed. PlatformGrantWriter used to declare
    //
    //     [EntityRegistry.ScopeHr]    = <the HR module's own role list>,
    //     [EntityRegistry.ScopeTasks] = <the Tasks module's own role list>,   ... etc
    //
    // which meant kernel source named module constants directly. The cost was not theoretical: a
    // kernel-only candidate built against HEAD failed on those unresolved module constants, so the
    // foundation could not be committed to Git
    // without dragging five module access services — owned by other workstreams — in with it.
    //
    // The dependency is now inverted. Modules push their vocabulary in; the kernel never reaches out.
    //
    // WHAT THIS DELIBERATELY IS NOT. It is not a second source of permission truth. A vocabulary only
    // DECLARES which role and action names a scope publishes, so the grant writer can reject an unknown
    // one. It does not decide whether a given employee holds a role — that stays with the module access
    // services, reached through IModulePermissionAdapter, exactly as before. Nothing here can grant.
    public interface IPlatformPermissionVocabulary
    {
        /// Matches EntityDefinition.PermissionScope and IModuleAccessService.Scope.
        string Scope { get; }

        /// The module's published role names. Projected from the module's own constants — never re-listed,
        /// because a second copy is a copy that goes stale, and the stale one rejects roles the module
        /// genuinely honours.
        IReadOnlyList<string> Roles { get; }

        /// The module's administrative action, or null when the module is not administrable through the
        /// platform grant writer. Named by the module rather than guessed by the kernel: a heuristic
        /// ("the action containing 'manage'") picks `attendance-manage` for HR, which is not its
        /// administrative right.
        string? ManageAction { get; }
    }

    // The kernel's read side. Everything is a lookup against explicitly registered vocabularies; there is
    // no inference, no assembly scanning and no configuration source, because each of those can invent a
    // permission that nobody wrote down.
    public interface IPlatformPermissionVocabularyRegistry
    {
        /// Scopes that declare BOTH roles and a manage action — the only ones the grant writer may administer.
        IReadOnlyCollection<string> AdministrableScopes { get; }

        /// Unknown scope and unknown role both answer false. Ordinal comparison: a role is an identifier,
        /// not prose, and case-insensitive matching would make "Manager" and "manager" the same grant.
        bool IsKnownRole(string scope, string role);

        /// For error messages only.
        string KnownRoles(string scope);

        /// Null when the scope is unknown or declares no administrative action.
        string? ManageActionFor(string scope);
    }

    // Built once from the registered vocabularies. Every failure mode is a STARTUP failure, not a runtime
    // surprise: a duplicate scope or a duplicate role inside a scope means two modules disagree about who
    // owns a name, and discovering that on the first grant attempt — in whichever environment happens to
    // exercise it first — is strictly worse than refusing to boot.
    public sealed class PlatformPermissionVocabularyRegistry : IPlatformPermissionVocabularyRegistry
    {
        private readonly Dictionary<string, IPlatformPermissionVocabulary> _byScope;

        public PlatformPermissionVocabularyRegistry(IEnumerable<IPlatformPermissionVocabulary> vocabularies)
        {
            ArgumentNullException.ThrowIfNull(vocabularies);

            _byScope = new Dictionary<string, IPlatformPermissionVocabulary>(StringComparer.Ordinal);

            foreach (var v in vocabularies)
            {
                if (v is null) throw new InvalidOperationException(
                    "A null IPlatformPermissionVocabulary was registered; the permission registry refuses to start.");

                if (string.IsNullOrWhiteSpace(v.Scope)) throw new InvalidOperationException(
                    $"{v.GetType().Name} declares no Scope. A vocabulary with no scope can never be matched.");

                if (_byScope.ContainsKey(v.Scope)) throw new InvalidOperationException(
                    $"Two permission vocabularies claim scope '{v.Scope}' ({_byScope[v.Scope].GetType().Name} " +
                    $"and {v.GetType().Name}). Ownership of a scope must be unambiguous.");

                if (v.Roles is null) throw new InvalidOperationException(
                    $"{v.GetType().Name} declares a null role list for scope '{v.Scope}'.");

                // A duplicate role inside one module means the module's own published list is inconsistent.
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var role in v.Roles)
                {
                    if (string.IsNullOrWhiteSpace(role)) throw new InvalidOperationException(
                        $"Scope '{v.Scope}' publishes a blank role name.");

                    if (!seen.Add(role)) throw new InvalidOperationException(
                        $"Scope '{v.Scope}' publishes the role '{role}' twice.");
                }

                _byScope[v.Scope] = v;
            }
        }

        // Administrable == has a manage action AND at least one role. A scope with roles but no
        // administrative action cannot be granted through the platform, and saying so here keeps the
        // grant writer from having to ask two questions.
        public IReadOnlyCollection<string> AdministrableScopes =>
            _byScope.Where(kv => kv.Value.ManageAction is not null && kv.Value.Roles.Count > 0)
                    .Select(kv => kv.Key).ToArray();

        public bool IsKnownRole(string scope, string role)
        {
            if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(role)) return false;
            return _byScope.TryGetValue(scope, out var v)
                   && v.Roles.Contains(role, StringComparer.Ordinal);
        }

        public string KnownRoles(string scope) =>
            !string.IsNullOrWhiteSpace(scope) && _byScope.TryGetValue(scope, out var v) && v.Roles.Count > 0
                ? string.Join(", ", v.Roles)
                : "(none registered)";

        public string? ManageActionFor(string scope) =>
            !string.IsNullOrWhiteSpace(scope) && _byScope.TryGetValue(scope, out var v) ? v.ManageAction : null;
    }
}
