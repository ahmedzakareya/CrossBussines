# ADR-030 addendum — R2 entity privacy ceiling

Implements D-05's per-entity authoring ceiling. This is a narrowing control, not a permission grant.

`CommunicationPlatform:EntityPrivacyPolicies` declares a canonical entity code, maximum visibility and, for Public, an external-policy reference. Missing declarations default to Internal. Invalid declarations are refused. Employee and JournalEntry cannot be made Public by configuration. Invoices default to Internal; declaring Public requires an explicit external-policy reference in trusted deployment configuration. A reference is a policy declaration, not evidence of customer authority.

Quotation preserves the already implemented Wave 3B-C policy `CommExternalAccessPolicy.Quotation`. That authority still independently requires resolved external identity, matching company/customer, live participant and the supported customer anchor. Other entity codes are not added to the external authority's allow-list. No caller-controlled input declares a policy.

Thread creation checks the entity ceiling before creating a row. Existing threads' authorable tiers are the intersection of entity policy, thread tier and caller visibility. Creating or editing a comment therefore cannot widen visibility using an old Public thread. Invalid policy yields no authorable tiers and no compose capability; existing authorized staff reads remain available.

Portal conversation lists filter permitted entity codes before querying. Every direct portal read/write rechecks the current policy before consulting external record authority. Withdrawing Quotation's Public policy closes list, direct conversation and comments, reply, read acknowledgement and attachment paths on existing threads. It does not delete or rewrite historical content.

The synthetic internal Communication test host explicitly declares its invoice Public-note policy to test lower-level visibility mechanics. Production defaults do not inherit that fixture policy. Refusal tests independently prove the default denies Public invoices and writes no thread, protected entities cannot be opened, malformed declarations are denied, and policy withdrawal closes both authoring and portal access.

No new endpoint, permission, UI markup, schema or background worker is introduced. Retention (D-06), per-entity audit declarations (D-07), legacy comment migration and notification worker activation remain separate R2 acceptance work. No retention duration or owner approval is invented by this privacy contract.
