using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // THE REPORTING GRAPH'S FOURTH EXTERNAL DEPENDENCY.
    //
    // Two tests build the real container from AddCrossBusinessReporting with ValidateOnBuild, and both
    // say in their own words that the registration "has always depended on three things from outside
    // itself" - a DbContext, the company-scope holder, and the BusinessContext accessor.
    //
    // That stopped being true when ReportOrgImageProvider arrived: it takes IWebHostEnvironment, to
    // find the company and branch marks on disk. Nothing registered one, so the graph no longer built
    // and TEN DI tests failed at construction - which is exactly what ValidateOnBuild is for. The
    // failure was invisible only because the test project would not compile at all.
    //
    // A STUB, NOT A REAL ENVIRONMENT, for the same reason the BusinessContext accessor beside it is a
    // stub: these tests assert that the graph RESOLVES, not that an image is found. A test that needed
    // a real web root would be a different test, and it would belong somewhere it could own the files.
    // NullFileProvider is what "no files here" looks like without a temp directory to clean up.
    // =============================================================================================
    internal sealed class ReportingTestHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "CrossBuy.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
