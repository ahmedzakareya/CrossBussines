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
    // IT NOW STARTS ONE WORKER, and the earlier note here said it started none. That note was written
    // when there was nothing periodic to run and it promised that an expiry worker, "when it exists,
    // belongs behind the canonical worker governance". It exists, and it does: DocumentExpiryHostedService
    // uses IWorkerCompanyScope, WorkerCompanyRunner and WorkerScope.ForCompany like every other worker
    // in the product, suppresses itself on a certification runtime, and waits for IWorkerGate.
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

            // ---- expiry and renewal (batch 4) --------------------------------------------------
            //
            // The projection is scoped, because it runs inside a per-company worker scope and reads the
            // context-bound DbContext that scope resolves.
            services.AddScoped<IDocumentExpiryProjection, DocumentExpiryProjection>();

            // THE WORKER REGISTERS HERE, and that is not a convenience - it is what keeps Program.cs
            // out of this batch entirely. Program.cs is SHF-01 shared and already calls
            // AddDocumentPlatform exactly once; a platform that needed a second line there for every
            // capability it grew would make every batch a shared-file negotiation. The header above
            // said this platform "registers one service and starts nothing", and that is no longer
            // true, so it says so plainly instead of quietly drifting.
            //
            // It is a hosted service and therefore starts on boot. It suppresses itself on a
            // certification runtime and waits for the worker gate before its first pass, so a host that
            // should not be writing does not write.
            services.AddHostedService<DocumentExpiryHostedService>();
            return services;
        }
    }
}
