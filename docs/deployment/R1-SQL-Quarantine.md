# R1 — historical root SQL quarantine

The original R1 exit criterion permits `script.sql` to be resolved or formally excluded. The chosen treatment is permanent historical quarantine. No content is deleted or executed, and the file is never a deployment slice.

The exact root path and LF-normalized SHA-256 are recorded in `governance/registry/sql-quarantine.json`. Authoring validation and deployment preflight share one policy validator. Admission requires the exact hash, a single manifest identity, classification `not-deployable` and `excludedFromDeploy: true`. Any changed content, missing decision, alias/wildcard, duplicate row, or attempt to admit a feature slice fails closed.

This distinguishes a known excluded historical artifact from an unsafe script being admitted for execution. A valid quarantine does not block unrelated reviewed scripts. Explicit requests to execute the quarantined file are refused before any database connection. Unreviewed scripts and any other non-deployable script remain blocking.

This is a local R1 governance decision under the coordinator's implementation scope. It does not authorize production deployment, update the visual baseline or change remote Git/CI settings.
