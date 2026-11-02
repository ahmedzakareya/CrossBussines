# R1 CI attribution contract

The Integration Gate is a pull-request merge check for `integration`. A source branch must use `tab/tab-N-*` (or `tab-tab-N-*`) and N must exist in the generated ownership registry. There is no TAB-0 fallback and no automatic shared-file approval.

Manual runs require a registered tab and an exact full base commit SHA. A requested owner conflicting with an attributed branch is refused. The base must resolve to a commit in the checkout. Inputs are read through environment variables, rather than interpolated into PowerShell source.

Aggregate pushes are not assigned a fictitious single owner. The workflow therefore runs on pull requests and explicit manual dispatch. This is safe as a merge gate only when the remote `integration` branch requires the Integration Gate check and prevents bypassing required reviews/checks. Local workflow edits do not prove those remote settings exist. Until branch protection and a successful remote run are verified, R1 CI delivery remains open.

The same `check-file-ownership.ps1` and twelve-check integration gate remain authoritative. Cross-tab files are still refused; shared files still require deliberate owner approval. Multi-workstream changes must be split or receive a separately reviewed ownership decision, rather than acquire broad ownership through a CI workaround.

Run `python governance/tests/ci_tab_attribution_tests.py` to verify the real resolver's eight success/refusal cases without database or Git writes.
