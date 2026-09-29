using Microsoft.AspNetCore.Authorization;

namespace CrossBuy.Models.Platform
{
    // =============================================================================================
    // THE TWO SECURITY DECLARATIONS AN EXTERNAL AUTHENTICATION ENDPOINT NEEDS.
    //
    // WHY NEITHER COULD BE EXPRESSED BEFORE, measured rather than assumed:
    //
    //   * [Authorize] is listed in AuthorizationSurface.AuthenticationOnlyAttributes — the analyzer
    //     deliberately treats it as proof of AUTHENTICATION and never of permission, which is right
    //     for internal endpoints where the employee's module right is the real question. So
    //     [Authorize(Policy = "CrossBuy.Portal")] on a portal Logout resolves to AuthorizationKind
    //     .None and lands in the debt column, even though it is exactly the correct control.
    //
    //   * An anonymous-by-design endpoint had ONE sanctioned mechanism: an entry in
    //     engineering/authorization-baseline.json. But that file states its own invariant — "This
    //     baseline may only SHRINK. CI rejects any commit that adds an entry." A legitimate new
    //     login could therefore only be declared by breaking the rule that makes the baseline worth
    //     having.
    //
    // Both are declarations, not exemptions. Neither can be applied without saying so in source
    // where a reviewer reads it, and the analyzer still refuses an undeclared anonymous mutator.
    // =============================================================================================

    /// <summary>
    /// The authority for an EXTERNAL portal endpoint: the caller must hold a valid
    /// <see cref="PortalAuthenticationDefaults.AuthenticationScheme"/> cookie.
    ///
    /// THIS IS REAL ENFORCEMENT AND NOT AN ANALYZER HINT. It derives from AuthorizeAttribute and
    /// binds the portal policy, so applying it actually refuses the request — a marker attribute
    /// that only satisfied a static check would be the worst of both worlds: green analysis over an
    /// open endpoint.
    ///
    /// IT IS NOT A MODULE PERMISSION, and the distinction is the whole reason it exists separately.
    /// A portal customer is an external identity, not a member of staff with a right in a module;
    /// there is no Tasks, Inventory or Accounting action to map them onto, and inventing one would
    /// put customers into the employee permission vocabulary. For this audience, holding the portal
    /// identity IS the authority — and what may then be SEEN is decided per request by
    /// PortalContextAccessor from the PortalUser row.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class PortalAuthorizeAttribute : AuthorizeAttribute
    {
        public PortalAuthorizeAttribute()
        {
            Policy = PortalAuthenticationDefaults.PolicyName;
        }
    }

    /// <summary>
    /// A mutating endpoint that is unauthenticated BY DESIGN — the small set of actions that cannot
    /// require an identity because they are how an identity is obtained.
    ///
    /// THE REASON IS MANDATORY AND THAT IS THE POINT. This attribute buys silence from CBA001 and
    /// CBA005, so the price is a sentence a reviewer can disagree with. A declaration nobody has to
    /// justify is an exemption wearing a better name.
    ///
    /// WHAT IT DOES NOT DO: it does not make the endpoint protected, and it does not pretend the
    /// endpoint is safe. It moves it out of the DEBT column into its own counted category, because
    /// a login that must be anonymous is not a gap somebody forgot to close — and counting it as
    /// one means the debt figure can never legitimately reach zero.
    ///
    /// APPLYING IT IS NOT ENOUGH ON ITS OWN. The endpoint still owes anti-forgery, and it still
    /// owes whatever validation stands in for authorization — for a login, the credential check
    /// itself. The analyzer cannot see that this was done; a reviewer reading the reason can.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class AnonymousByDesignAttribute : Attribute
    {
        public AnonymousByDesignAttribute(string reason)
        {
            // An empty reason is the same as no declaration, so it is refused at construction
            // rather than accepted as a blank cheque.
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException(
                    "An anonymous-by-design endpoint must state WHY it cannot require an identity.",
                    nameof(reason));

            Reason = reason;
        }

        public string Reason { get; }
    }
}
