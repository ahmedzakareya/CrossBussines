# One-time R1 integration bootstrap

The repository owner explicitly approved publication of the fixed source snapshot `6abc412fdaa9f35a0b082204ee6baf8db1d98ef0` and protection of the initially created integration branch at `480c3d49f7b862b0662af161db824360e80ff5df`. This is an integration-owner import of already implemented work from several tabs, not a TAB-1 feature edit of their modules.

Normal tab ownership checks and all ownership/shared-file assignments remain unchanged. For this initial import only, CI may validate the approved immutable snapshot when the authenticated GitHub actor is the repository owner, the PR uses this exact source branch in the same repository, and its base is the original integration commit. The approved source must be an ancestor of HEAD. Every subsequent application edit, additional module file, ownership reassignment or changed shared rule is refused.

Follow-up changes are confined to the five explicitly named CI/bootstrap files and content-identical normalization of existing generator outputs. This is not a blanket coordinator ownership grant. It cannot be used after integration advances, by another actor, from a fork, on another branch or for a different snapshot. Legacy unclaimed paths remain historical ownership debt; they are not silently assigned to TAB-1.

The normal twelve-check integration gate still runs. Builds, both test suites, authorization debt, suppressions, SQL governance, DI wiring and residue are not skipped or made optional. This bootstrap does not declare the historical UI register complete and does not authorize database activation or deployment.
