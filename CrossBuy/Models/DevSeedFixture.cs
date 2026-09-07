namespace CrossBuy.Models
{
    // The development SEED FIXTURE — the company the dev/test seeding endpoints operate on by default.
    //
    // WHY THIS TYPE EXISTS. DevSeedController carried the literal `int companyId = 1` on 69 action
    // parameters. Every one of them was a legitimate development fixture choice, but written as a bare
    // literal it is indistinguishable from the defect the CORRECTION-005 work exists to remove: a
    // request-parameter default that silently confers TENANT AUTHORITY. A reader — and a guard — cannot
    // tell "this is the dev seed company" from "this endpoint quietly defaults to someone's tenant".
    //
    // Naming it separates those two ideas. The behaviour is unchanged; what changes is that the intent is
    // now stated once, greppable, and impossible to confuse with a production default.
    //
    // THIS IS NOT A GLOBAL DEFAULT COMPANY. It is scoped to the [DevOnly] seeding surface, which returns
    // 404 outside Development. No production code path may reference it — asserted by test. Business
    // code resolves its company from IRequestCompanyResolver / BusinessContext, and there is no default
    // company there, by design.
    public static class DevSeedFixture
    {
        // Company 1 is the seeded development tenant this repository's fixtures have always used. It is
        // stated here rather than repeated as a literal 69 times.
        public const int DefaultCompanyId = 1;
    }
}
