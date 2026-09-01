using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrossBuy.Tests
{
    /// <summary>
    /// CrossBusiness Platform — Stage 2A Batch B, B6: the two constructor dependencies the converted access
    /// services now take.
    ///
    /// AccountingAccessService and InventoryAccessService gained IBootstrapAccessPolicyReader and a logger when
    /// Mechanism A was replaced. Ten existing test call sites construct them by hand, and each needed the same two
    /// arguments — so they come from here rather than being written out ten times. A future third dependency is
    /// then one edit, not ten.
    ///
    /// The REAL reader is used, not a stub: these tests assert behaviour preservation, and a stubbed policy reader
    /// would let a conversion defect pass. It reads from the same DbContext the test already owns, so a test that
    /// configures no policy genuinely gets "no policy configured" — which is the deny path B6 introduces.
    /// </summary>
    internal static class B6TestWiring
    {
        internal static IBootstrapAccessPolicyReader Policies(CrossDbContext db) =>
            new BootstrapAccessPolicyReader(db, NullLogger<BootstrapAccessPolicyReader>.Instance);

        internal static ILogger<T> Log<T>() => NullLogger<T>.Instance;
    }
}