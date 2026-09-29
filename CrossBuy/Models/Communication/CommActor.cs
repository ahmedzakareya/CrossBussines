using CrossBuy.Models.Platform;

namespace CrossBuy.Models.Communication
{
    // =============================================================================================
    // WHO DID IT — the Communication Platform's actor, as ONE value that cannot be malformed.
    //
    // WHY THIS EXISTS AT ALL. Every actor column in this platform was `int EmployeeId NOT NULL`, and
    // the services wrote `context.EmployeeId ?? 0` into several of them. That produced a real latent
    // defect: an unresolved identity became employee 0, and CommAccessPolicy's ownership test
    // (`comment.AuthorEmployeeId == (context.EmployeeId ?? 0)`) then answered TRUE for any row whose
    // author was also 0. It was unreachable only because the module gate refused external identities
    // upstream — a guard in a different file, which is not a boundary, it is a coincidence.
    //
    // TWO KINDS, AND DELIBERATELY NOT A FRAMEWORK. Employee and PortalUser are the only actor kinds
    // this product has. A generic ActorType/ActorId pair would put both ids in ONE column
    // distinguished only by a discriminator, and the first predicate that forgets the discriminator
    // silently matches the wrong person — employee 7 and portal user 7 are different people. Keeping
    // two columns means a forgotten predicate returns NOTHING rather than someone else.
    //
    // IT IS SHAPED LIKE Models.Context.Admin.NotificationRecipient ON PURPOSE, and is deliberately
    // NOT that type. The convention is shared so a reader who has learned one has learned both; the
    // TYPE is not, because Communication must not take a dependency on the Notification platform to
    // say who wrote a comment. Two small structs are cheaper than that coupling.
    //
    // THE ONE DIFFERENCE FROM NotificationRecipient, and it is a schema difference rather than a
    // disagreement: that type keeps a non-nullable CLR `int` with 0 as the absence marker, because
    // three call sites outside TAB-1's reach need a non-nullable int. Communication has NO consumer
    // of these columns outside BL/Communication and Models/Context/Communication — measured, not
    // assumed — so the columns here are honestly `int?` and 0 is not a magic value anywhere.
    // =============================================================================================
    public readonly struct CommActor : IEquatable<CommActor>
    {
        public enum ActorKind
        {
            Employee = 1,
            PortalUser = 2,
        }

        private CommActor(ActorKind kind, int id) { Kind = kind; Id = id; }

        public ActorKind Kind { get; }

        /// The id WITHIN its kind. Meaningless without Kind, which is why the two are never apart and
        /// why there is no implicit conversion to int.
        public int Id { get; }

        public bool IsEmployee => Kind == ActorKind.Employee;
        public bool IsPortalUser => Kind == ActorKind.PortalUser;

        /// The employee id, or NULL when this actor is external. Named so a caller that wants an
        /// employee id is forced to handle the case where there is not one.
        public int? EmployeeId => IsEmployee ? Id : null;

        /// The PortalUsers.Id, or NULL when this actor is internal.
        public int? PortalUserId => IsPortalUser ? Id : null;

        // ---------------------------------------------------------------------------------------------
        // Construction. Two factories, each setting exactly one field. There is no public constructor,
        // so "both" and "neither" are not states this type can be in.
        // ---------------------------------------------------------------------------------------------

        public static CommActor Employee(int employeeId)
        {
            if (employeeId <= 0)
                throw new ArgumentOutOfRangeException(nameof(employeeId),
                    "An employee actor needs a real employee id. Zero is not an id — it is the absence " +
                    "marker this type exists to abolish.");
            return new CommActor(ActorKind.Employee, employeeId);
        }

        public static CommActor PortalUser(int portalUserId)
        {
            if (portalUserId <= 0)
                throw new ArgumentOutOfRangeException(nameof(portalUserId),
                    "A portal actor needs a real PortalUsers.Id, resolved server-side from the " +
                    "authenticated principal — never taken from the request.");
            return new CommActor(ActorKind.PortalUser, portalUserId);
        }

        // ---------------------------------------------------------------------------------------------
        // THE EXACTLY-ONE RULE, READING.
        //
        // A row carrying both identities or neither is a data fault, and it is REFUSED rather than
        // resolved to whichever happens to be set. Guessing would silently pick a person.
        // ---------------------------------------------------------------------------------------------
        public static CommActor FromColumns(int? employeeId, int? portalUserId)
        {
            var actor = TryFromColumns(employeeId, portalUserId);
            if (actor == null)
                throw new InvalidOperationException(
                    employeeId is > 0 && portalUserId is > 0
                        ? $"Communication row carries BOTH an employee ({employeeId}) and a portal actor " +
                          $"({portalUserId}). Exactly one is allowed."
                        : "Communication row carries NO actor. Exactly one is required.");
            return actor.Value;
        }

        /// The tolerant read, for paths that page over rows and must not throw on one bad row — a list
        /// screen showing 49 comments and an "unknown author" is better than a screen showing nothing.
        /// It returns null for both-set and neither-set alike; callers decide what to render.
        public static CommActor? TryFromColumns(int? employeeId, int? portalUserId)
        {
            bool hasEmployee = employeeId is > 0;
            bool hasPortal = portalUserId is > 0;
            if (hasEmployee == hasPortal) return null;   // both, or neither
            return hasEmployee ? Employee(employeeId!.Value) : PortalUser(portalUserId!.Value);
        }

        // ---------------------------------------------------------------------------------------------
        // THE ONLY SANCTIONED WAY TO NAME THE ACTOR OF A WRITE.
        //
        // Every mutating path in this platform resolves the author/uploader/reader from the
        // BusinessContext and never from a parameter the caller could choose. That is what makes
        // "portal user X cannot write as portal user Y" structural rather than a check somebody
        // remembers: there is no argument to pass.
        //
        // An ExternalPortal context carries PortalUserId and a NULL EmployeeId by construction
        // (BusinessContext.ForExternalPortal states both), so the two branches cannot overlap.
        // ---------------------------------------------------------------------------------------------
        public static CommActor? TryFromContext(BusinessContext? context)
        {
            if (context == null) return null;

            // Portal FIRST, and the order is load-bearing: if a context ever carried both, treating it
            // as an employee would hand an external caller staff identity. The portal reading is the
            // safe one, and IsExternalPortal is set from the SOURCE rather than from the ids.
            if (context.IsExternalPortal)
                return context.PortalUserId is > 0 ? PortalUser(context.PortalUserId.Value) : null;

            return context.EmployeeId is > 0 ? Employee(context.EmployeeId.Value) : null;
        }

        /// Same, but refusing rather than returning null — for write paths, where an unresolved actor
        /// must stop the operation instead of producing a row nobody can be held to.
        public static CommActor FromContext(BusinessContext? context)
            => TryFromContext(context)
               ?? throw new CommActorUnresolvedException(
                   "This operation records WHO performed it, and the identity could not be resolved " +
                   "from the business context. An unresolved identity is not employee 0 and is not a " +
                   "portal user — it is a refusal.");

        // ---------------------------------------------------------------------------------------------
        // Equality is over (Kind, Id), so employee 7 and portal user 7 are NOT equal. The whole point.
        // ---------------------------------------------------------------------------------------------
        public bool Equals(CommActor other) => Kind == other.Kind && Id == other.Id;
        public override bool Equals(object? obj) => obj is CommActor other && Equals(other);
        public override int GetHashCode() => HashCode.Combine((int)Kind, Id);
        public static bool operator ==(CommActor left, CommActor right) => left.Equals(right);
        public static bool operator !=(CommActor left, CommActor right) => !left.Equals(right);

        /// Stable, greppable, and safe to log: it names the kind, so a bare number never appears in a
        /// log line where a reader could take it for an employee id.
        public override string ToString() => $"{Kind}:{Id}";
    }

    /// Thrown when a write cannot say who performed it. Separate from CommValidationException because
    /// it is not the caller's input that is wrong — it is the identity behind the request.
    public sealed class CommActorUnresolvedException : InvalidOperationException
    {
        public CommActorUnresolvedException(string message) : base(message) { }
    }
}
