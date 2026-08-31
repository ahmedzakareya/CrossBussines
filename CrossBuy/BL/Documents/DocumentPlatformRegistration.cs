using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CrossBuy.BL.Documents
{
    // =============================================================================================
    // Central Document Platform — its ONE registration extension.
    //
    // WHY IT IS NOT PART OF AddCommunicationPlatform, which is where it started.
    //
    // Putting it there compiled and passed its own tests, and CommunicationDiWiringTests immediately
    // failed 32 ways — correctly. That suite builds a container from AddCommunicationPlatform alone
    // and asserts every Communication service resolves; the document platform needs TAB-1's document
    // spine (IDocumentAccessResolver, IDocumentStorage) and the context accessor, none of which the
    // Communication platform registers or should have to. The failure was not a stale baseline, it
    // was the test saying the Communication platform had just grown a dependency it does not own.
    //
    // So the document platform gets its own entry point, which is also exactly what SHF-01 asks for:
    // "ONE registration extension method per platform". Program.cs gains one line and no knowledge.
    //
    // IT REGISTERS ONE SERVICE AND STARTS NOTHING. No hosted service, no timer, no background sweep.
    // An expiry worker, when it exists, belongs behind the canonical worker governance and is a
    // separate decision from making documents storable.
    // =============================================================================================
    public static class DocumentPlatformRegistration
    {
        /// Requires the platform document spine (IDocumentAccessResolver, IDocumentStorage) and the
        /// business context accessor to be registered already — they are, in Program.cs, by the tab
        /// that owns them. This method deliberately does NOT register them itself: a platform that
        /// quietly re-registers another platform's contracts is how two implementations of one
        /// interface end up in a container with the last one silently winning.
        public static IServiceCollection AddDocumentPlatform(IServiceCollection services)
        {
            services.AddScoped<IPlatformDocumentService, PlatformDocumentService>();

            // THE CLOCK, registered with TryAdd rather than Add.
            //
            // Validity asks "is the expiry date before today", so the platform needs a clock it can be
            // tested against. The container COULD supply nothing and let the constructor's default take
            // over, but that leaves a production behaviour resting on how the DI container treats an
            // optional parameter - a detail no reader of this platform should have to know.
            //
            // TryAdd, because the clock is the application's to own, not the document platform's. If
            // any tab ever registers a real one - a test clock, an offset clock for a deployment in
            // another timezone - theirs is already there and this line does nothing. It supplies the
            // obvious default; it does not claim the seam.
            services.TryAddSingleton(TimeProvider.System);
            return services;
        }
    }
}
