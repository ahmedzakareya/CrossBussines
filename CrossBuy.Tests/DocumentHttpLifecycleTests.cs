using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Documents;
using CrossBuy.BL.Platform;
using CrossBuy.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // THE HTTP EDGE of the document lifecycle — the layer TAB-3's service tests could not reach.
    //
    // PlatformDocumentTests proves the SERVICE returns the right refusal code. This file proves the
    // CONTROLLER turns each code into the right thing on the wire, which is a separate claim and the
    // one a caller actually experiences.
    //
    // THE DISTINCTION THAT MATTERS MOST, and the reason this file exists at all:
    //
    //   store_failed / decision_failed  ->  503, because the storage or the database let us down.
    //                                       The write was rolled back and any blob compensated.
    //   file_type_refused, missing date ->  400, because the caller can fix it and it reveals nothing.
    //   anything authorization-shaped   ->  a bare 404 with code "not_found", because "absent",
    //                                       "another company's" and "not permitted" must be one answer.
    //
    // Returning 400 for an infrastructure failure is the bug being guarded against: a client told its
    // request was malformed will "fix" a perfectly good submission instead of retrying it, and the
    // person whose passport never arrived will be asked to send a different passport.
    //
    // NOTHING HERE TOUCHES A DATABASE. The service is doubled so each refusal code can be produced on
    // demand — the point under test is the mapping, and a real service can only produce one code at a
    // time by luck.
    // =============================================================================================
    public class DocumentHttpLifecycleTests
    {
        /// A service that returns exactly what a test asks it to, and records what it was given — so a
        /// test can also assert what the controller did NOT pass down.
        private sealed class ScriptedDocumentService : IPlatformDocumentService
        {
            public DocumentResult Next = DocumentResult.Success(7, 9);
            public DocumentSubmissionRequest? LastSubmission;
            public readonly List<string> Calls = new();

            public Task<DocumentResult> SubmitAsync(DocumentSubmissionRequest request, Stream content, CancellationToken ct = default)
            { Calls.Add("Submit"); LastSubmission = request; return Task.FromResult(Next); }

            public Task<DocumentResult> VerifyAsync(long documentId, string? note, CancellationToken ct = default)
            { Calls.Add("Verify"); return Task.FromResult(Next); }

            public Task<DocumentResult> RejectAsync(long documentId, string note, CancellationToken ct = default)
            { Calls.Add("Reject"); return Task.FromResult(Next); }

            public Task<DocumentResult> UploadAsync(DocumentUploadRequest request, Stream content, CancellationToken ct = default)
                => Task.FromResult(Next);
            public Task<DocumentResult> ReplaceAsync(long documentId, Stream content, string fileName,
                string contentType, string? reason, CancellationToken ct = default) => Task.FromResult(Next);
            public Task<DocumentContent?> OpenCurrentAsync(long documentId, CancellationToken ct = default)
                => Task.FromResult<DocumentContent?>(null);
            public Task<DocumentContent?> OpenVersionAsync(long documentId, int versionNo, CancellationToken ct = default)
                => Task.FromResult<DocumentContent?>(null);
            public Task<IReadOnlyList<DocumentListItem>> ListForEntityAsync(string entityType, int entityId, CancellationToken ct = default)
                => Task.FromResult<IReadOnlyList<DocumentListItem>>(Array.Empty<DocumentListItem>());
            public Task<IReadOnlyList<CrossBuy.Models.Context.Documents.PlatformDocumentVersion>> HistoryAsync(long documentId, CancellationToken ct = default)
                => Task.FromResult<IReadOnlyList<CrossBuy.Models.Context.Documents.PlatformDocumentVersion>>(
                    Array.Empty<CrossBuy.Models.Context.Documents.PlatformDocumentVersion>());
            public Task<DocumentValidity> FindValidDocumentAsync(string entityType, int entityId, long documentTypeId, CancellationToken ct = default)
                => Task.FromResult(DocumentValidity.No(DocumentValidityReasons.NotFound));
            public Task<bool> HasValidDocumentAsync(string entityType, int entityId, long documentTypeId, CancellationToken ct = default)
                => Task.FromResult(false);
        }

        private static (DocumentsController Controller, ScriptedDocumentService Service) Build()
        {
            var service = new ScriptedDocumentService();
            return (new DocumentsController(service), service);
        }

        private static IFormFile File(string name = "passport.pdf", string content = "bytes",
            string contentType = "application/pdf")
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name)
            {
                Headers = new HeaderDictionary(),
                ContentType = contentType,
            };
        }

        private static int StatusOf(IActionResult result) => result switch
        {
            ObjectResult o => o.StatusCode ?? 200,
            StatusCodeResult s => s.StatusCode,
            JsonResult => 200,
            _ => -1,
        };

        private static string BodyOf(IActionResult result) => result switch
        {
            ObjectResult o => JsonSerializer.Serialize(o.Value),
            JsonResult j => JsonSerializer.Serialize(j.Value),
            _ => string.Empty,
        };

        // -----------------------------------------------------------------------------------------
        // THE INFRASTRUCTURE / CALLER DISTINCTION
        // -----------------------------------------------------------------------------------------

        [Theory]
        [InlineData("store_failed")]
        [InlineData("decision_failed")]
        public async Task An_infrastructure_failure_answers_503_and_not_400(string code)
        {
            var (controller, service) = Build();
            service.Next = DocumentResult.Refused(code);

            var result = await controller.Submit("Employee", 5, 1, File(), null, null, null, null);

            Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusOf(result));

            // The code travels, because a retry-able failure the caller can see is better than a bare
            // 503 they have to guess about. It carries no key, no path and no document identity.
            Assert.Contains(code, BodyOf(result), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("file_type_refused")]
        [InlineData("expiry_date_required")]
        [InlineData("issue_date_required")]
        [InlineData("file_too_large")]
        [InlineData("self_service_not_allowed")]
        [InlineData("unknown_document_type")]
        [InlineData("type_entity_mismatch")]
        [InlineData("decision_note_required")]
        [InlineData("not_pending")]
        public async Task A_policy_refusal_the_caller_can_act_on_answers_400_with_its_real_code(string code)
        {
            var (controller, service) = Build();
            service.Next = DocumentResult.Refused(code);

            var result = await controller.Submit("Employee", 5, 1, File(), null, null, null, null);

            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
            Assert.Contains(code, BodyOf(result), StringComparison.Ordinal);
        }

        // -----------------------------------------------------------------------------------------
        // ONE REFUSAL SHAPE FOR EVERYTHING AUTHORIZATION-SHAPED
        // -----------------------------------------------------------------------------------------

        public static TheoryData<string> AuthorizationShaped()
        {
            var data = new TheoryData<string>();
            foreach (var code in new[]
                     {
                         DocumentAccessReasons.CompanyUnresolved,
                         DocumentAccessReasons.NoEmployeeIdentity,
                         DocumentAccessReasons.CompanyMismatch,
                         DocumentAccessReasons.UnknownEntityType,
                         DocumentAccessReasons.NoOwnerResolver,
                         DocumentAccessReasons.OwnerNotFound,
                         DocumentAccessReasons.RelationMismatch,
                         DocumentAccessReasons.ModuleDenied,
                         DocumentAccessReasons.NoModuleAuthority,
                         DocumentAccessReasons.ConfidentialityDenied,
                     }) data.Add(code);
            return data;
        }

        [Theory]
        [MemberData(nameof(AuthorizationShaped))]
        public async Task Every_authorization_shaped_refusal_collapses_to_one_indistinguishable_404(string code)
        {
            // THE PROBING DEFENCE. If "another company's document" answered differently from "no such
            // document", the write endpoints would be an oracle for what exists and whose it is — and a
            // caller could enumerate a company's employees by watching which ids answer 403.
            var (controller, service) = Build();
            service.Next = DocumentResult.Refused(code);

            var result = await controller.Verify(4242, "ok");

            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));

            var body = BodyOf(result);
            Assert.Contains("not_found", body, StringComparison.Ordinal);

            // The specific reason must NOT travel: it is precisely the thing that would distinguish the
            // cases from one another.
            Assert.DoesNotContain(code, body, StringComparison.Ordinal);
        }

        // -----------------------------------------------------------------------------------------
        // WHAT NEVER CROSSES THE WIRE
        // -----------------------------------------------------------------------------------------

        [Fact]
        public async Task No_response_on_any_branch_carries_a_StorageKey_a_path_or_a_file_name()
        {
            var (controller, service) = Build();

            var branches = new List<IActionResult>();
            foreach (var next in new[]
                     {
                         DocumentResult.Success(7, 9),
                         DocumentResult.Refused("store_failed"),
                         DocumentResult.Refused("file_type_refused"),
                         DocumentResult.Refused(DocumentAccessReasons.ModuleDenied),
                     })
            {
                service.Next = next;
                branches.Add(await controller.Submit("Employee", 5, 1, File(), "P123456", null, null, null));
                branches.Add(await controller.Verify(7, "ok"));
                branches.Add(await controller.Reject(7, "unreadable"));
            }

            foreach (var body in branches.Select(BodyOf))
            {
                Assert.DoesNotContain("App_Data", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("StorageKey", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("passport.pdf", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(".pdf", body, StringComparison.OrdinalIgnoreCase);

                // The document NUMBER is the passport or civil-id number itself. It goes in and never
                // comes back out.
                Assert.DoesNotContain("P123456", body, StringComparison.Ordinal);

                // A 32-character hex run is what a StorageKey looks like. Asserting the SHAPE catches a
                // leak through a field nobody thought to name.
                Assert.DoesNotMatch("[0-9a-fA-F]{32}", body);
            }
        }

        // -----------------------------------------------------------------------------------------
        // THE SUBMISSION CONTRACT: WHAT THE CALLER MAY NOT CHOOSE
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void The_submission_endpoint_accepts_no_company_confidentiality_status_or_verifier()
        {
            // Read off the SIGNATURE rather than the body: a parameter that does not exist cannot be
            // supplied, cannot be defaulted wrongly and cannot be forgotten in a later refactor. The
            // employee id IS a parameter, and is safe because Submit maps to employee-request, whose
            // target must be the caller — naming somebody else's id fails the authorization ask.
            var parameters = typeof(DocumentsController).GetMethod("Submit")!
                .GetParameters().Select(p => p.Name!).ToList();

            foreach (var forbidden in new[]
                     { "companyId", "company", "confidentiality", "status", "isActive",
                       "verifiedBy", "decidedBy", "waive", "waiver", "approved", "storageKey" })
                Assert.DoesNotContain(forbidden, parameters, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task The_browsers_content_type_is_carried_but_the_file_NAME_is_stripped_of_any_path()
        {
            var (controller, service) = Build();

            await controller.Submit("Employee", 5, 1,
                File(name: @"..\..\web.config", contentType: "text/plain"), null, null, null, null);

            // Path.GetFileName on the way in, so the extension the service checks and the name it stores
            // are the same string. A submission that arrived as "..\..\web.config" is judged as
            // "web.config" — and refused by the type's allow-list, not by this controller.
            Assert.Equal("web.config", service.LastSubmission!.FileName);

            // Content-Type is PASSED THROUGH, not trusted: acceptability is decided from the extension
            // against the type's allow-list, one layer down, so one place owns the policy.
            Assert.Equal("text/plain", service.LastSubmission.ContentType);
        }

        [Fact]
        public async Task A_submission_with_no_file_never_reaches_the_service()
        {
            var (controller, service) = Build();

            var result = await controller.Submit("Employee", 5, 1, null, null, null, null, null);

            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
            Assert.Empty(service.Calls);
        }

        [Fact]
        public async Task An_empty_file_is_refused_rather_than_stored_as_a_zero_byte_document()
        {
            var (controller, service) = Build();

            var result = await controller.Submit("Employee", 5, 1, File(content: ""), null, null, null, null);

            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
            Assert.Empty(service.Calls);
        }

        // -----------------------------------------------------------------------------------------
        // THE ATTRIBUTES, which are part of the contract and not decoration
        // -----------------------------------------------------------------------------------------

        [Theory]
        [InlineData("Submit")]
        [InlineData("Verify")]
        [InlineData("Reject")]
        public void Every_mutating_document_endpoint_validates_its_antiforgery_token(string action)
        {
            var method = typeof(DocumentsController).GetMethod(action)!;
            Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
            Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
        }

        [Fact]
        public void The_upload_size_cap_is_declared_on_the_endpoint_so_it_applies_before_the_body_is_read()
        {
            // A cap enforced after the read has already spent the memory it exists to protect.
            var limit = typeof(DocumentsController).GetMethod("Submit")!
                .GetCustomAttribute<RequestSizeLimitAttribute>();

            Assert.NotNull(limit);
        }

        [Fact]
        public void The_read_endpoints_are_GET_and_the_write_endpoints_are_POST()
        {
            // DeclaredOnly, because Controller itself publishes a Content(string) helper and a plain
            // GetMethod("Content") matches both — an ambiguity that has nothing to do with routing.
            foreach (var read in new[] { "Content", "VersionContent", "ForEntity", "History" })
                Assert.NotNull(Declared(read).GetCustomAttribute<HttpGetAttribute>());

            foreach (var write in new[] { "Submit", "Verify", "Reject" })
                Assert.Null(Declared(write).GetCustomAttribute<HttpGetAttribute>());
        }

        private static MethodInfo Declared(string name) =>
            typeof(DocumentsController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Single(m => m.Name == name);

        [Fact]
        public void The_controller_requires_a_session_and_never_allows_anonymous()
        {
            Assert.NotNull(typeof(DocumentsController)
                .GetCustomAttributes(inherit: true)
                .FirstOrDefault(a => a.GetType().Name == "SessionValidationAttribute"));

            Assert.DoesNotContain(typeof(DocumentsController).GetMethods(),
                m => m.GetCustomAttributes(inherit: true).Any(a => a.GetType().Name == "AllowAnonymousAttribute"));
        }

        [Fact]
        public async Task A_successful_write_returns_the_identifiers_the_caller_already_owns_and_nothing_else()
        {
            var (controller, service) = Build();
            service.Next = DocumentResult.Success(7, 9);

            var body = BodyOf(await controller.Submit("Employee", 5, 1, File(), null, null, null, null));

            Assert.Contains("\"ok\":true", body.Replace(" ", ""), StringComparison.Ordinal);
            Assert.Contains("7", body, StringComparison.Ordinal);
            Assert.Contains("9", body, StringComparison.Ordinal);
        }
    }
}
