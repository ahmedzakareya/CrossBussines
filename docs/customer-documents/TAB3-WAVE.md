# TAB-3 — Accounting.Customer documents and safe publication

Approved base: `c0f97af481d38457fa25c5e9ded529a80aab41a3`. Isolated detached worktree; no inferred branch, merge, push, deployment, production database connection, or SQL script execution.

The user approved this chat as shared-file coordinator. Registry, DI, accounting authorization and the existing role/workspace surfaces are changed only in this worktree. TAB-5's only overlapping path is Program.cs; its registration is near AddProjectsReporting, whereas this wave's registration is beside the document owner resolvers. Integration must preserve both additions. No shared checkout is edited.

## Policy and implementation

- Customer means the Accounting customer master. Its resolver reads the real owning company and the central document authorization chain checks tenant, identity, owner relationship, module authority and confidentiality.
- `CustomerDocumentUploader` explicitly grants `customer-document-upload`; Upload, Replace, Verify and Delete use it. `CustomerDocumentPublisher` separately grants `customer-document-publish` for Publish and Revoke. Neither grant inherits from ChiefAccountant, Accountant, Auditor, bootstrap, or the other document grant. Existing AccountingRoles allows administrators to assign and remove the two grants; nothing assigns a grant in this wave.
- Confidentiality elevation continues to require Accounting manage. Customer self-service submission remains unsupported. Accounting read retains its existing company-wide semantics.
- Portal list and download both require Customer, matching document-owner customer, Internal confidentiality, Active status, and a current version belonging to the same document and company. These guards precede the writer in the commit sequence. Existing non-Customer publication rows become externally unreadable.
- Publishing requires an active, same-company or global type explicitly applicable to Customer and an owned current version. The customer is derived from the document, not supplied by a form. Revoke ends the live grant without deleting documents, versions or historical grants, even after eligibility changes.
- Serializable transactions cover document eligibility, publication state and the mandatory business event. Dedup keys include publication identity, allowing revoke/re-publish to create distinct audit facts. Repeated sequential publish/revoke requests are idempotent; the existing filtered unique index protects one live grant.
- Two antiforgery-protected POSTs and controls in the existing Documents Workspace; zero new screens. Role administration and its threshold operation now resolve the actor's company instead of using company 1. Foreign employees cannot receive roles through AssignAccRole.

## Validation and limits

Acceptance includes grant independence, tenant/employee isolation, forged publications, confidential/HR refusal, version ownership, type and lifecycle refusal, publish/revoke history, audit rollback verified from a fresh context, existing document/portal behavior, DI scope validation and the existing lifecycle UI contract.

Final acceptance: 90 passed, 0 failed, 0 skipped, with normal build analyzers enabled. Family mutation: 2 of 9 tests failed; confidentiality mutation: 4 of 9 failed; constant Accounting target mutation: 20 of 20 passed (known survivor). All mutant source edits were restored before final acceptance. No SQL Server tests ran.

Mutation proofs remove the external family and confidentiality predicates individually; the negative tests must fail. Replacing the permission target with a constant is expected to survive because Accounting does not inspect its target. This is an expressiveness limit: grants are company-wide, not customer-specific. Tenant and owner checks are separate and remain enforced.

The old foundation test assumed every unregistered document family had SupportsFiles=false, but the approved base already exposes SalesInvoice legacy attachments. It now asserts the actual security contract: absent owner resolver means refusal. Customer alone is newly enabled by this wave.

SQL Server locking/deadlock behavior and live schema reachability are unverified under the no-SQL restriction. In-process relational tests use isolated SQLite, never the CrossBuy database. No server runtime was started. Concurrent serializable conflicts may require caller retry; they do not bypass eligibility or the unique grant constraint.
