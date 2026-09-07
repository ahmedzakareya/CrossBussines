using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers.Api
{
    // =============================================================================================
    // Stage 2A Batch A — the minimal production administration API for platform role grants.
    //
    // NOT the Security Console. Five endpoints, thin, no business logic: every decision belongs to
    // IPlatformGrantWriter, and this controller only translates its typed outcome into HTTP.
    //
    // WHY THIS CONTROLLER HOLDS NO AUTHORIZATION LOGIC OF ITS OWN
    //
    // A grant administration check is not a single permission — it is an authority tier, a company
    // match, a scope match and a privilege ceiling resolved through four different access services.
    // Re-expressing any part of that here would create a second answer to the same question, and the
    // two would diverge the first time either changed. The writer is the authority; this is transport.
    //
    // [Authorize] alone would be authentication, not authorization — the analyzer's CBA002 is right
    // about that. The authorization is IN THE BODY, in the writer, and it is exhaustive: every action
    // below refuses an unauthorized caller before touching a row.
    // =============================================================================================
    [ApiController]
    [Route("api/platform/grants")]
    [Authorize]
    [Produces("application/json")]
    public sealed class PlatformGrantsApiController : ControllerBase
    {
        private readonly IPlatformGrantWriter _grants;
        private readonly IBusinessContextAccessor _contexts;

        public PlatformGrantsApiController(IPlatformGrantWriter grants, IBusinessContextAccessor contexts)
        { _grants = grants; _contexts = contexts; }

        // ---------------------------------------------------------------------------------------------
        // GET direct grants
        // ---------------------------------------------------------------------------------------------
        [HttpGet]
        public async Task<IActionResult> List(
            [FromQuery] int companyId,
            [FromQuery] string? scope = null,
            [FromQuery] int? principalId = null,
            [FromQuery] string? role = null,
            [FromQuery] int? scopeBranchId = null,
            [FromQuery] bool includeInactive = false,
            CancellationToken cancellationToken = default)
        {
            var actor = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (actor == null) return Unauthenticated();

            // companyId is REQUIRED and is validated inside the writer against the resolved context. It is not
            // defaulted from the context: a silent default is how a caller ends up reading a company they did not
            // ask about, and it removes the writer's chance to refuse a mismatch.
            if (companyId <= 0) return Problem400("A companyId is required.");

            var grants = await _grants.ListDirectGrantsAsync(actor, new DirectGrantQuery
            {
                CompanyId = companyId,
                Scope = scope,
                PrincipalId = principalId,
                Role = role,
                ScopeBranchId = scopeBranchId,
                IncludeInactive = includeInactive,
            }, cancellationToken);

            // An unauthorized caller receives an EMPTY list, not a 403, and that is deliberate for this one
            // endpoint: a 403 on a company id confirms the company exists and that grants are administered in it.
            // The writer logs the denial, so the refusal is auditable without being disclosed.
            return Ok(new { success = true, count = grants.Count, grants });
        }

        // ---------------------------------------------------------------------------------------------
        // GET one grant
        // ---------------------------------------------------------------------------------------------
        [HttpGet("{id:int}")]
        public async Task<IActionResult> Get(int id, [FromQuery] int companyId, CancellationToken cancellationToken = default)
        {
            var actor = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (actor == null) return Unauthenticated();
            if (companyId <= 0) return Problem400("A companyId is required.");

            return Translate(await _grants.GetGrantAsync(actor, id, companyId, cancellationToken));
        }

        // ---------------------------------------------------------------------------------------------
        // POST create
        // ---------------------------------------------------------------------------------------------
        [HttpPost]
        public async Task<IActionResult> Create(
            [FromBody] CreatePlatformGrantCommand command, CancellationToken cancellationToken = default)
        {
            var actor = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (actor == null) return Unauthenticated();
            if (command == null) return Problem400("A request body is required.");

            var result = await _grants.CreateGrantAsync(actor, command, cancellationToken);

            // 201 for a real creation; 200 for an idempotent replay, because nothing was created this time and a
            // caller counting 201s must not count a retry twice.
            if (result.Outcome == PlatformGrantOutcome.Success)
                return Created($"/api/platform/grants/{result.Grant!.Id}?companyId={result.Grant.CompanyId}",
                    new { success = true, message = result.Message, grant = result.Grant });

            return Translate(result);
        }

        // ---------------------------------------------------------------------------------------------
        // POST revoke
        // ---------------------------------------------------------------------------------------------
        [HttpPost("{id:int}/revoke")]
        public async Task<IActionResult> Revoke(
            int id, [FromBody] RevokeGrantRequest body, CancellationToken cancellationToken = default)
        {
            var actor = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (actor == null) return Unauthenticated();
            if (body == null) return Problem400("A request body is required.");

            return Translate(await _grants.RevokeGrantAsync(actor, new RevokePlatformGrantCommand
            {
                GrantId = id,
                CompanyId = body.CompanyId,
                Reason = body.Reason ?? "",
            }, cancellationToken));
        }

        // ---------------------------------------------------------------------------------------------
        // POST update validity
        // ---------------------------------------------------------------------------------------------
        [HttpPost("{id:int}/validity")]
        public async Task<IActionResult> UpdateValidity(
            int id, [FromBody] UpdateValidityRequest body, CancellationToken cancellationToken = default)
        {
            var actor = await _contexts.TryGetCurrentAsync(cancellationToken);
            if (actor == null) return Unauthenticated();
            if (body == null) return Problem400("A request body is required.");

            return Translate(await _grants.UpdateValidityAsync(actor, new UpdateGrantValidityCommand
            {
                GrantId = id,
                CompanyId = body.CompanyId,
                ValidFrom = body.ValidFrom,
                ValidTo = body.ValidTo,
                Reason = body.Reason ?? "",
            }, cancellationToken));
        }

        // ---------------------------------------------------------------------------------------------
        // request bodies — separate from the commands so a caller cannot post a GrantId that contradicts the route
        // ---------------------------------------------------------------------------------------------
        public sealed class RevokeGrantRequest
        {
            public int CompanyId { get; set; }
            public string? Reason { get; set; }
        }

        public sealed class UpdateValidityRequest
        {
            public int CompanyId { get; set; }
            public DateTime? ValidFrom { get; set; }
            public DateTime? ValidTo { get; set; }
            public string? Reason { get; set; }
        }

        // ---------------------------------------------------------------------------------------------
        // outcome -> HTTP. One mapping, so no endpoint invents its own status code.
        // ---------------------------------------------------------------------------------------------
        private IActionResult Translate(PlatformGrantResult result) => result.Outcome switch
        {
            PlatformGrantOutcome.Success =>
                Ok(new { success = true, message = result.Message, grant = result.Grant }),

            PlatformGrantOutcome.IdempotentReplay =>
                Ok(new { success = true, replayed = true, message = result.Message, grant = result.Grant }),

            PlatformGrantOutcome.AlreadyRevoked =>
                Ok(new { success = true, alreadyRevoked = true, message = result.Message, grant = result.Grant }),

            PlatformGrantOutcome.ValidationFailed =>
                BadRequest(new { success = false, message = result.Message, errors = result.Errors }),

            // 403 carries NO detail about what would have been allowed. Which tier was missing, which company was
            // resolved and whether the target exists are all facts an unauthorized caller must not learn.
            PlatformGrantOutcome.Forbidden =>
                StatusCode(StatusCodes.Status403Forbidden,
                    new { success = false, message = "You do not have permission to perform this action" }),

            // A foreign-company grant is 404, identical to one that never existed.
            PlatformGrantOutcome.NotFound =>
                NotFound(new { success = false, message = result.Message }),

            PlatformGrantOutcome.Duplicate =>
                Conflict(new { success = false, message = result.Message, grant = result.Grant }),

            PlatformGrantOutcome.Conflict =>
                Conflict(new { success = false, message = result.Message }),

            PlatformGrantOutcome.Expired =>
                Conflict(new { success = false, message = result.Message }),

            _ => StatusCode(StatusCodes.Status500InternalServerError,
                    new { success = false, message = "Unhandled grant outcome." }),
        };

        // Unauthenticated is a DIFFERENT answer from unauthorized: 401 tells a client to sign in, 403 tells it not
        // to retry. Never an HTML redirect — a fetch() caller reads a 302-to-login as success with an unparseable
        // body, which is the defect ApiPermAttribute was created for.
        private IActionResult Unauthenticated() =>
            StatusCode(StatusCodes.Status401Unauthorized,
                new { success = false, message = "Authentication is required." });

        private IActionResult Problem400(string message) =>
            BadRequest(new { success = false, message });
    }
}