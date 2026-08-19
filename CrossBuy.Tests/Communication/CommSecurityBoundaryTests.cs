using System.Reflection;
using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests.Communication
{
    // =============================================================================================
    // Communication Platform — THE SECURITY BOUNDARY, PROVED IN ONE PLACE.
    //
    // The integration gate asks for nine properties to be mechanically proven. Several are already defended
    // by the behavioural suites (CommAccessPolicyTests in particular). This file exists anyway, and it is not
    // duplication: it is the ONE artifact a reviewer can read end to end to satisfy themselves that the
    // boundary holds, with each property named exactly as the gate names it.
    //
    // Two kinds of proof appear here, deliberately:
    //
    //   BEHAVIOURAL — drive the real services and assert the refusal. These prove what the code DOES.
    //   STRUCTURAL  — reflect over the assembly and the DI graph. These prove what the code CANNOT do, which
    //                 is the stronger claim and the only way to assert a NEGATIVE ("registers no permission
    //                 provider", "takes no Session dependency"). A behavioural test cannot prove absence.
    //
    // THE INVARIANT ALL NINE SERVE:
    //
    //     A communication permission can never widen access to a business record.
    //     Communication is a CONSUMER of authorization, never a source of it.
    // =============================================================================================
    public class CommSecurityBoundaryTests : IDisposable
    {
        private readonly CommunicationTestHost _host = new();
        public void Dispose() => _host.Dispose();

        private static StubPermissionProvider DeniesEverything() => new();
        private static StubPermissionProvider ViewOnly() => new(PlatformActions.View);

        // =========================================================================================
        // PROPERTY 1 — a communication permission cannot widen business-record access
        // =========================================================================================

        // The strongest form of the invariant: give the caller EVERY communication-side advantage that exists
        // — they created the thread, they are a participant, they authored the comment, they hold a thread
        // grant — then remove entity View. Everything must still close.
        [Fact]
        public async Task Every_communication_side_advantage_combined_cannot_open_a_record_the_caller_may_not_view()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            // Arrange with View granted: create the thread, comment on it, become a participant.
            var thread = await _host.Threads(permissions: ViewOnly())
                .GetOrCreateAsync(context, new CommThreadRequest { Entity = entity });

            await _host.Comments(permissions: ViewOnly())
                .AddAsync(context, _host.CommentOn(entity, "authored by me"));

            // Now the SAME caller, after their entity permission is revoked.
            var access = await _host.Access(permissions: DeniesEverything())
                .ResolveThreadAccessAsync(context, thread);

            Assert.False(access.CanRead);
            Assert.False(access.CanComment);
            Assert.False(access.CanModerate);
        }

        // The ordering, stated as a test: entity View is asked FIRST. A caller with no communication standing
        // at all but with View still gets read access — which proves the entity permission is the gate and the
        // thread state is only the second filter, not a substitute for it.
        [Fact]
        public async Task Entity_view_is_the_gate_and_thread_state_is_only_the_second_filter()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            var thread = await _host.Threads(permissions: ViewOnly())
                .GetOrCreateAsync(context, new CommThreadRequest { Entity = entity });

            // A different employee, no participation, no grant, no authorship — but they can open the invoice.
            var stranger = CommunicationTestHost.Context(CommunicationTestHost.Colleague);
            var access = await _host.Access(permissions: ViewOnly())
                .ResolveThreadAccessAsync(stranger, thread);

            Assert.True(access.CanRead);
        }

        // =========================================================================================
        // PROPERTY 2 — denied entity access prevents comments, mentions, attachments AND timeline
        //
        // All four capabilities asserted in one test on purpose: the gate names four, and a per-capability
        // test file could pass three and quietly leave the fourth open.
        // =========================================================================================

        [Fact]
        public async Task A_caller_denied_the_entity_gets_no_comment_no_mention_no_attachment_and_an_empty_timeline()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            // Seed real content while permitted, so the refusals below are about ACCESS, not about emptiness.
            var seeded = await _host.Comments(permissions: ViewOnly()).AddAsync(
                context,
                _host.CommentOn(entity, "visible to the permitted",
                    mentions: new[]
                    {
                        new CommMentionRequest
                        {
                            TargetKind = CommMentionTargetKind.Employee,
                            TargetId = CommunicationTestHost.Colleague,
                        },
                    }));
            Assert.NotNull(seeded);

            var denied = DeniesEverything();

            // 1. COMMENTS — writing is refused.
            await Assert.ThrowsAnyAsync<Exception>(() =>
                _host.Comments(permissions: denied).AddAsync(
                    context, _host.CommentOn(entity, "should never be written")));

            // 2. TIMELINE — reading returns nothing rather than leaking the seeded comment.
            var timeline = await _host.Timeline(permissions: denied).GetAsync(
                context, new CommTimelineQuery { Entity = entity });
            Assert.Empty(timeline.Items);

            // 3. MENTIONS — the seeded mention is not reachable through the timeline either.
            Assert.DoesNotContain(timeline.Items, i =>
                i.Source.Contains("Mention", StringComparison.OrdinalIgnoreCase));

            // 4. ATTACHMENTS — a comment carrying one is refused at the same gate.
            await Assert.ThrowsAnyAsync<Exception>(() =>
                _host.Comments(permissions: denied).AddAsync(
                    context,
                    _host.CommentOn(entity, "with a file",
                        attachments: new[]
                        {
                            new CommAttachmentRequest
                            {
                                FileName = "secret.pdf",
                                ContentType = "application/pdf",
                                SizeBytes = 10,
                                StorageKey = "comm/secret.pdf",
                            },
                        })));
        }

        // =========================================================================================
        // PROPERTY 3 — Communication registers NO module permission provider
        //
        // STRUCTURAL. This is the property that keeps Communication a consumer of authorization rather than a
        // second place where authorization is decided. It cannot be proven behaviourally — only by inspecting
        // what the registration actually puts in the container.
        // =========================================================================================

        [Fact]
        public void The_registration_contributes_no_authorization_service_of_any_kind()
        {
            var services = new ServiceCollection();
            services.AddCommunicationPlatform(new ConfigurationBuilder().Build());

            // The authorization contracts Communication must never supply. If a future change registers one of
            // these, this test fails and the reviewer is forced to justify it.
            var forbidden = new[]
            {
                typeof(IPlatformPermissionProvider),
                typeof(IModuleAccessService),
                typeof(IModulePermissionAdapter),
                typeof(IPlatformRoleDirectory),
                typeof(IBootstrapAccessPolicyReader),
                typeof(ICompanyIsolationBypass),
                typeof(IPlatformGrantWriter),
            };

            foreach (var contract in forbidden)
                Assert.DoesNotContain(services, d => d.ServiceType == contract);
        }

        // The other half of the same claim: it does not merely avoid registering the interfaces, it declares a
        // DEPENDENCY on the provider. Communication asks; it never answers.
        [Fact]
        public void Access_policy_consumes_the_platform_permission_provider_rather_than_implementing_it()
        {
            var policy = typeof(CommAccessPolicy);

            Assert.DoesNotContain(typeof(IPlatformPermissionProvider), policy.GetInterfaces());

            var takesProvider = policy.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Any(p => p.ParameterType == typeof(IPlatformPermissionProvider));

            Assert.True(takesProvider,
                "CommAccessPolicy must take IPlatformPermissionProvider as a dependency — that dependency is " +
                "what makes Communication a consumer of authorization.");
        }

        // =========================================================================================
        // PROPERTY 4 — share / follower / watch state does not grant entity access
        // =========================================================================================

        // Participation is the "follower/watch" state in this platform. Making someone a participant is a
        // communication act; it must not become an authorization act.
        [Fact]
        public async Task Being_made_a_participant_does_not_grant_access_to_the_record()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            var thread = await _host.Threads(permissions: ViewOnly())
                .GetOrCreateAsync(context, new CommThreadRequest { Entity = entity });

            // The outsider follows the thread while they still hold entity View — the most generous possible
            // arrangement of "watch state".
            var outsiderWhileVisible = CommunicationTestHost.Context(CommunicationTestHost.Outsider);
            await _host.Participation(permissions: ViewOnly()).FollowAsync(
                outsiderWhileVisible, thread.Id, CommParticipantRole.Watcher);

            // The outsider is now a follower — and still cannot open the invoice.
            var outsider = CommunicationTestHost.Context(CommunicationTestHost.Outsider);
            var access = await _host.Access(permissions: DeniesEverything())
                .ResolveThreadAccessAsync(outsider, thread);

            Assert.False(access.CanRead);
            Assert.False(access.CanComment);
        }

        // A mention is the other way a third party gets "attached" to a conversation. Being mentioned is not a
        // grant either — otherwise anyone could hand out access simply by typing a name.
        [Fact]
        public async Task Being_mentioned_does_not_grant_access_to_the_record()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            await _host.Comments(permissions: ViewOnly()).AddAsync(
                context,
                _host.CommentOn(entity, "hey @outsider",
                    mentions: new[]
                    {
                        new CommMentionRequest
                        {
                            TargetKind = CommMentionTargetKind.Employee,
                            TargetId = CommunicationTestHost.Outsider,
                        },
                    }));

            var outsider = CommunicationTestHost.Context(CommunicationTestHost.Outsider);
            var timeline = await _host.Timeline(permissions: DeniesEverything())
                .GetAsync(outsider, new CommTimelineQuery { Entity = entity });

            Assert.Empty(timeline.Items);
        }

        // =========================================================================================
        // PROPERTY 5 — an unknown entity code fails closed
        // =========================================================================================

        [Fact]
        public async Task An_unregistered_entity_code_is_refused_by_every_capability()
        {
            var unknown = new CommEntityRef("NotARealEntity", 1);
            var surface = _host.Surface();

            foreach (var capability in CommCapabilities.All)
            {
                var decision = await surface.EvaluateAsync(unknown, capability);
                Assert.False(decision.Allowed, $"'{capability}' must be refused for an unregistered code.");

                Assert.False(await surface.SupportsAsync(unknown, capability));

                await Assert.ThrowsAsync<CommEntityNotSupportedException>(() =>
                    surface.RequireAsync(unknown, capability));
            }
        }

        // Fail-closed must survive the malformed shapes too — a null ref, an empty code and a non-positive id
        // are all "unknown", not "skip the check".
        [Theory]
        [InlineData("", 1)]
        [InlineData("   ", 1)]
        [InlineData("SalesInvoice", 0)]
        [InlineData("SalesInvoice", -5)]
        public async Task A_malformed_entity_reference_fails_closed(string code, int id)
        {
            var surface = _host.Surface();
            var decision = await surface.EvaluateAsync(new CommEntityRef(code, id), CommCapabilities.Comments);
            Assert.False(decision.Allowed);
        }

        [Fact]
        public async Task A_null_entity_reference_fails_closed()
        {
            var surface = _host.Surface();
            Assert.False((await surface.EvaluateAsync(null, CommCapabilities.Comments)).Allowed);
            Assert.False(await surface.SupportsAsync(null, CommCapabilities.Comments));
            await Assert.ThrowsAsync<CommEntityNotSupportedException>(() =>
                surface.RequireAsync(null, CommCapabilities.Comments));
        }

        // An unknown CAPABILITY is refused as firmly as an unknown entity. Otherwise a typo in a future call
        // site ("Attachment" for "Attachments") would silently evaluate to "allowed".
        [Fact]
        public async Task An_unknown_capability_fails_closed()
        {
            var surface = _host.Surface();
            var decision = await surface.EvaluateAsync(CommunicationTestHost.Invoice(), "Attachment");
            Assert.False(decision.Allowed);
        }

        // =========================================================================================
        // PROPERTY 6 — a disabled entity surface fails closed
        // =========================================================================================

        // The deny list is the operational lever: pull one entity out of the surface without a code change.
        // It must beat every other signal, including a registry flag that says the capability is supported.
        [Fact]
        public async Task The_deny_list_beats_the_registry_flag_and_the_allow_list()
        {
            var options = CommunicationTestHost.DefaultOptions();

            // SalesInvoice carries SupportsComments in the registry AND is on the allow list — the most
            // "enabled" an entity can be. Denying it must still win outright.
            options.BlockedEntityCodes = new List<string> { EntityRegistry.SalesInvoice };

            using var host = new CommunicationTestHost(options);
            var surface = host.Surface();

            foreach (var capability in CommCapabilities.All)
                Assert.False((await surface.EvaluateAsync(CommunicationTestHost.Invoice(), capability)).Allowed,
                    $"a denied entity must refuse '{capability}'.");
        }

        // An entity that is simply not enabled is refused too — the allow list is opt-in, not advisory.
        [Fact]
        public async Task An_entity_absent_from_the_enabled_list_fails_closed()
        {
            var options = CommunicationTestHost.DefaultOptions();
            options.EnabledEntityCodes = new List<string>();   // nothing onboarded

            using var host = new CommunicationTestHost(options);

            // PosOrder has no SupportsComments registry flag, so with the allow list empty there is nothing
            // left to permit it.
            Assert.False((await host.Surface()
                .EvaluateAsync(CommunicationTestHost.PosOrder(), CommCapabilities.Comments)).Allowed);
        }

        // Writing through the service, not just probing the surface — the refusal must reach the caller.
        [Fact]
        public async Task A_denied_entity_refuses_a_comment_at_the_service_boundary()
        {
            var options = CommunicationTestHost.DefaultOptions();
            options.BlockedEntityCodes = new List<string> { EntityRegistry.SalesInvoice };

            using var host = new CommunicationTestHost(options);

            await Assert.ThrowsAsync<CommEntityNotSupportedException>(() =>
                host.Comments(permissions: new StubPermissionProvider(PlatformActions.View)).AddAsync(
                    CommunicationTestHost.Context(),
                    host.CommentOn(CommunicationTestHost.Invoice(), "should be refused")));
        }

        // =========================================================================================
        // PROPERTY 7 — a company mismatch denies
        // =========================================================================================

        [Fact]
        public async Task A_caller_from_another_company_cannot_read_this_companys_thread()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            var thread = await _host.Threads(permissions: ViewOnly())
                .GetOrCreateAsync(context, new CommThreadRequest { Entity = entity });

            // Same employee id space, different company — and even with View granted by the stub.
            var otherCompany = CommunicationTestHost.Context(
                CommunicationTestHost.OtherCompanyEmployee, CommunicationTestHost.OtherCompanyId);

            var access = await _host.Access(permissions: ViewOnly())
                .ResolveThreadAccessAsync(otherCompany, thread);

            Assert.False(access.CanRead);
            Assert.False(access.CanComment);
            Assert.False(access.CanModerate);
        }

        [Fact]
        public async Task A_timeline_read_from_another_company_returns_nothing()
        {
            var context = CommunicationTestHost.Context();
            var entity = CommunicationTestHost.Invoice();

            await _host.Comments(permissions: ViewOnly())
                .AddAsync(context, _host.CommentOn(entity, "company 1 only"));

            var otherCompany = CommunicationTestHost.Context(
                CommunicationTestHost.OtherCompanyEmployee, CommunicationTestHost.OtherCompanyId);

            var timeline = await _host.Timeline(permissions: ViewOnly())
                .GetAsync(otherCompany, new CommTimelineQuery { Entity = entity });

            Assert.Empty(timeline.Items);
        }

        // =========================================================================================
        // PROPERTY 8 — there is no CompanyID fallback
        //
        // The platform-wide rule this inherits: "An unresolved company scope reads no company-scoped data and
        // writes none. Fail closed; never default to a company." Stage 1 deleted a company-1 fallback that had
        // silently served one tenant's data to another.
        // =========================================================================================

        [Fact]
        public async Task An_unresolved_company_reads_nothing_and_writes_nothing()
        {
            var entity = CommunicationTestHost.Invoice();

            await _host.Comments(permissions: ViewOnly())
                .AddAsync(CommunicationTestHost.Context(), _host.CommentOn(entity, "real content"));

            // CompanyId 0 = nobody resolved. It must not become company 1.
            var unresolved = new BusinessContext
            {
                CompanyId = 0,
                EmployeeId = CommunicationTestHost.Author,
                UserId = "user-unresolved",
                Roles = Array.Empty<string>(),
            };

            var timeline = await _host.Timeline(permissions: ViewOnly())
                .GetAsync(unresolved, new CommTimelineQuery { Entity = entity });
            Assert.Empty(timeline.Items);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                _host.Comments(permissions: ViewOnly()).AddAsync(
                    unresolved, _host.CommentOn(entity, "must not be written")));
        }

        // STRUCTURAL: no source file in the platform may contain a literal company default. This is what stops
        // the fallback being reintroduced by a well-meaning "?? 1" during a refactor.
        [Fact]
        public void No_service_contains_a_company_id_fallback_literal()
        {
            var offenders = CommunicationSourceFiles()
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                    File.ReadAllText(f.Path),
                    @"CompanyI[dD]\s*(\?\?|\|\|\s*.{0,12}=)\s*1\b"))
                .Select(f => f.Name)
                .ToList();

            Assert.True(offenders.Count == 0,
                "A company-id fallback literal was found in: " + string.Join(", ", offenders));
        }

        // =========================================================================================
        // PROPERTY 9 — no Session dependency in core services
        //
        // STRUCTURAL, and it has to be: "this service never reads Session" is a negative. The platform rule is
        // that no service may read HttpContext, Session or claims directly — identity arrives as a resolved
        // BusinessContext, which is what makes every path testable and a scheduled/background caller possible.
        // =========================================================================================

        [Fact]
        public void No_communication_service_takes_an_http_or_session_dependency()
        {
            var forbiddenTypeNames = new[]
            {
                "IHttpContextAccessor", "HttpContext", "ISession", "HttpRequest", "HttpResponse",
                "ClaimsPrincipal", "IPrincipal",
            };

            var offenders = new List<string>();

            foreach (var type in CommunicationTypes())
                foreach (var parameter in type.GetConstructors().SelectMany(c => c.GetParameters()))
                    if (forbiddenTypeNames.Contains(parameter.ParameterType.Name, StringComparer.Ordinal))
                        offenders.Add($"{type.Name}({parameter.ParameterType.Name})");

            Assert.True(offenders.Count == 0,
                "Communication services must receive a resolved BusinessContext, never web state. Offenders: " +
                string.Join(", ", offenders));
        }

        // The source-level counterpart: a service could reach Session statically rather than by injection.
        [Fact]
        public void No_communication_source_file_references_session_or_http_context()
        {
            var offenders = CommunicationSourceFiles()
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                    File.ReadAllText(f.Path),
                    @"\bHttpContext\b|\.Session\b|GetSession|SetSession|System\.Web"))
                .Select(f => f.Name)
                .ToList();

            Assert.True(offenders.Count == 0,
                "Session/HttpContext reference found in: " + string.Join(", ", offenders));
        }

        // Positive form of the same rule: every public service method that acts on behalf of somebody takes a
        // BusinessContext. That is the seam Session would otherwise have occupied.
        [Fact]
        public void Service_entry_points_take_a_resolved_business_context()
        {
            var serviceInterfaces = typeof(ICommCommentService).Assembly.GetTypes()
                .Where(t => t.IsInterface
                            && t.Namespace == "CrossBuy.BL.Communication"
                            && t.Name.StartsWith("IComm", StringComparison.Ordinal))
                .ToList();

            Assert.NotEmpty(serviceInterfaces);

            // Not every interface is a caller-facing service — the surface, the body policy, the catalog and
            // the infrastructure seams legitimately take none. Asserting on the ones that DO act for a person.
            var mustCarryContext = new[]
            {
                typeof(ICommCommentService), typeof(ICommThreadService),
                typeof(ICommParticipationService), typeof(ICommReactionService),
                typeof(ICommTimelineAggregator), typeof(ICommReadStatusService),
            };

            foreach (var contract in mustCarryContext)
            {
                var methods = contract.GetMethods();
                Assert.All(methods, m => Assert.Contains(m.GetParameters(),
                    p => p.ParameterType == typeof(BusinessContext)));
            }
        }

        // =========================================================================================
        // Helpers — the source sweep the two structural file tests share.
        // =========================================================================================

        private static IEnumerable<Type> CommunicationTypes() =>
            typeof(ICommCommentService).Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && t.Namespace == "CrossBuy.BL.Communication");

        // Locates BL/Communication on disk from the test assembly, walking up to the repository root. The same
        // approach CommunicationSchemaParityTests uses to find the SQL script, and it fails loudly rather than
        // silently asserting over an empty set — a source sweep that found no files would "pass" every time.
        private static IReadOnlyList<(string Name, string Path)> CommunicationSourceFiles()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            for (; directory != null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "CrossBuy", "BL", "Communication");
                if (!Directory.Exists(candidate)) continue;

                var files = Directory.GetFiles(candidate, "*.cs")
                    .Select(p => (Name: Path.GetFileName(p), Path: p))
                    .ToList();

                Assert.NotEmpty(files);
                return files;
            }

            Assert.Fail("CrossBuy/BL/Communication could not be located from the test assembly directory. " +
                        "The structural source sweeps cannot run, and an empty sweep would pass vacuously.");
            return Array.Empty<(string, string)>();
        }
    }
}
