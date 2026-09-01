using Xunit;

namespace CrossBuy.Tests
{
    // Guard test: proves the real CrossDbContext model materialises on the test provider. If this fails,
    // every other test in this project is meaningless, so it is asserted explicitly rather than assumed.
    public class SmokeTests
    {
        [Fact]
        public void CrossDbContext_creates_the_platform_tables()
        {
            using var host = new PlatformTestHost();
            Assert.Empty(host.Db.BusinessEvents);
            Assert.Empty(host.Db.BusinessEventDispatches);
        }
    }
}